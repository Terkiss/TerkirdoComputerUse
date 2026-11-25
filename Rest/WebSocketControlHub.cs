using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using TerkirdoComputer.Core;
using TerkirdoComputer.Mcp;
using TerkirdoComputer.Windows;

namespace TerkirdoComputer.Rest;

public readonly record struct TkrfHeader(
    uint Magic,
    byte Version,
    byte Format,
    ushort Flags,
    uint FrameSeq,
    ushort Width,
    ushort Height,
    ulong TimestampQpcUs,
    int OriginX,
    int OriginY);

/// <summary>
/// Pillar 2 (v2.0.0 — WS /ws/control):
/// Real-time full-duplex single-agent WebSocket control &amp; binary frame streaming hub.
/// Enforces CSWSH Origin validation, query-token ban, atomic post-auth Session Takeover (Close 4001 + ReleaseAll),
/// 256 KiB inbound frame cap (Close 1009), single-writer SendAsync serialization, and O(1) FramePool conflation.
/// </summary>
public sealed class WebSocketControlHub
{
    public const uint MagicTkrf = 0x46524B54; // 'TKRF' in little-endian ASCII
    public const byte ProtocolVersion = 1;
    public const int TkrfHeaderBytes = 32;
    public const int MaxInboundMessageBytes = 256 * 1024; // 256 KiB
    public const int CloseCodeSessionSuperseded = 4001;

    public const byte FormatBgra32 = 0;
    public const byte FormatJpeg = 1;
    public const byte FormatPng = 2;

    public const ushort FlagClientOnly = 0x0001;
    public const ushort FlagRegionRoi = 0x0002;
    public const ushort FlagOnlyOnChange = 0x0004;

    private static readonly JsonSerializerOptions WsJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly AutomationService _automationService;
    private readonly ToolHandlers _toolHandlers;
    private readonly bool _allowRemote;
    private readonly SemaphoreSlim _wsSessionGate = new(1, 1);
    private WsControlSession? _activeWsSession;

    public WebSocketControlHub(AutomationService automationService, ToolHandlers toolHandlers, bool allowRemote)
    {
        _automationService = automationService;
        _toolHandlers = toolHandlers;
        _allowRemote = allowRemote;
    }

    public static void WriteTkrfHeader(
        Span<byte> dest,
        byte format,
        ushort flags,
        uint frameSeq,
        ushort width,
        ushort height,
        ulong timestampQpcUs,
        int originX,
        int originY)
    {
        if (dest.Length < TkrfHeaderBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(dest), "TKRF header requires at least 32 bytes.");
        }

        BinaryPrimitives.WriteUInt32LittleEndian(dest[0..4], MagicTkrf);
        dest[4] = ProtocolVersion;
        dest[5] = format;
        BinaryPrimitives.WriteUInt16LittleEndian(dest[6..8], flags);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[8..12], frameSeq);
        BinaryPrimitives.WriteUInt16LittleEndian(dest[12..14], width);
        BinaryPrimitives.WriteUInt16LittleEndian(dest[14..16], height);
        BinaryPrimitives.WriteUInt64LittleEndian(dest[16..24], timestampQpcUs);
        BinaryPrimitives.WriteInt32LittleEndian(dest[24..28], originX);
        BinaryPrimitives.WriteInt32LittleEndian(dest[28..32], originY);
    }

    public static bool TryParseTkrfHeader(ReadOnlySpan<byte> packet, out TkrfHeader header, out ReadOnlySpan<byte> payload)
    {
        header = default;
        payload = default;
        if (packet.Length < TkrfHeaderBytes)
        {
            return false;
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(packet[0..4]);
        if (magic != MagicTkrf)
        {
            return false;
        }

        header = new TkrfHeader(
            Magic: magic,
            Version: packet[4],
            Format: packet[5],
            Flags: BinaryPrimitives.ReadUInt16LittleEndian(packet[6..8]),
            FrameSeq: BinaryPrimitives.ReadUInt32LittleEndian(packet[8..12]),
            Width: BinaryPrimitives.ReadUInt16LittleEndian(packet[12..14]),
            Height: BinaryPrimitives.ReadUInt16LittleEndian(packet[14..16]),
            TimestampQpcUs: BinaryPrimitives.ReadUInt64LittleEndian(packet[16..24]),
            OriginX: BinaryPrimitives.ReadInt32LittleEndian(packet[24..28]),
            OriginY: BinaryPrimitives.ReadInt32LittleEndian(packet[28..32]));

        payload = packet[TkrfHeaderBytes..];
        return true;
    }

    /// <summary>
    /// Validates CSWSH Origin header and query-token hygiene BEFORE WebSocket upgrade or auth.
    /// Returns true if allowed; writes HTTP 400/403 ProblemDetails and returns false if rejected.
    /// </summary>
    public static async Task<bool> ValidatePreUpgradeSecurityAsync(HttpContext context, bool allowRemote)
    {
        if (context.Request.Query.ContainsKey("token") || context.Request.Query.ContainsKey("access_token"))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(ProblemDetails.QueryTokenForbidden()).ConfigureAwait(false);
            return false;
        }

        if (context.Request.Headers.TryGetValue("Origin", out var originValues))
        {
            string rawOrigin = originValues.ToString().Trim();
            if (!string.IsNullOrEmpty(rawOrigin))
            {
                if (!IsPermittedOrigin(rawOrigin, context.Request.Host.Host, allowRemote))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(ProblemDetails.ForbiddenOrigin(rawOrigin)).ConfigureAwait(false);
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsPermittedOrigin(string rawOrigin, string requestHost, bool allowRemote)
    {
        if (string.Equals(rawOrigin, "null", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Uri.TryCreate(rawOrigin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        bool isLoopbackOrigin =
            string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Host, "[::1]", StringComparison.OrdinalIgnoreCase);

        if (isLoopbackOrigin)
        {
            return true;
        }

        if (allowRemote && string.Equals(uri.Host, requestHost, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public async Task HandleWebSocketAsync(HttpContext context)
    {
        if (!await ValidatePreUpgradeSecurityAsync(context, _allowRemote).ConfigureAwait(false))
        {
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(
                ProblemDetails.InvalidArguments("Expected WebSocket upgrade request on /ws/control.")).ConfigureAwait(false);
            return;
        }

        using WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        using var currentSession = new WsControlSession(webSocket, _automationService, _toolHandlers, context.RequestAborted);

        await _wsSessionGate.WaitAsync(context.RequestAborted).ConfigureAwait(false);
        try
        {
            var prior = _activeWsSession;
            if (prior != null)
            {
                await prior.StopWorkLoopsAsync(500).ConfigureAwait(false);
                _ = ActiveInputTracker.ReleaseAll();
                await prior.SendSupersededAndCloseAsync().ConfigureAwait(false);
                prior.ScheduleTransportAbort(1500);
            }

            _activeWsSession = currentSession;
            currentSession.StartBackgroundLoops();
            currentSession.EnqueueJson(new
            {
                @event = "session_ready",
                version = "2.0.0",
                sessionId = currentSession.SessionId
            });
        }
        finally
        {
            _wsSessionGate.Release();
        }

        try
        {
            await currentSession.RunReceiveLoopAsync().ConfigureAwait(false);
        }
        finally
        {
            await currentSession.ShutdownAsync().ConfigureAwait(false);

            await _wsSessionGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(_activeWsSession, currentSession))
                {
                    _activeWsSession = null;
                    _ = ActiveInputTracker.ReleaseAll();
                }
            }
            finally
            {
                _wsSessionGate.Release();
            }
        }
    }

    private sealed record QueuedInputCommand(string? Id, string Op, Dictionary<string, object?> Args);

    private sealed class WsControlSession : IDisposable
    {
        public string SessionId { get; } = Guid.NewGuid().ToString("N")[..12];

        private readonly WebSocket _socket;
        private readonly AutomationService _automation;
        private readonly ToolHandlers _handlers;
        private readonly CancellationTokenSource _workCts;
        private readonly CancellationTokenSource _transportAbortCts;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly Channel<string> _textOutbox = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        private readonly Channel<QueuedInputCommand> _inputInbox = Channel.CreateBounded<QueuedInputCommand>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        private readonly LatestFrameBox _frameBox = new();
        private readonly SemaphoreSlim _outboundWake = new(0, int.MaxValue);

        private Task? _writerTask;
        private Task? _inputWorkerTask;

        private readonly object _streamSync = new();
        private CancellationTokenSource? _streamCts;
        private Task? _streamTask;
        private uint _frameSeq;

        private readonly object _waitSync = new();
        private CancellationTokenSource? _activeWaitCts;

        private int _superseded;
        private int _disposed;

        public WsControlSession(
            WebSocket socket,
            AutomationService automation,
            ToolHandlers handlers,
            CancellationToken requestAborted)
        {
            _socket = socket;
            _automation = automation;
            _handlers = handlers;
            _workCts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
            _transportAbortCts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        }

        public void StartBackgroundLoops()
        {
            _writerTask = Task.Run(OutboundWriterLoopAsync);
            _inputWorkerTask = Task.Run(InputWorkerLoopAsync);
        }

        public void CancelWork()
        {
            Interlocked.Exchange(ref _superseded, 1);
            try { _workCts.Cancel(); } catch { }
            CancelActiveWait();
            StopFrameStream();
            _inputInbox.Writer.TryComplete();
            SignalOutbound();
        }

        public async Task StopWorkLoopsAsync(int maxWaitMs)
        {
            CancelWork();

            var tasks = new List<Task>(3);
            if (_inputWorkerTask != null) tasks.Add(_inputWorkerTask);
            if (_writerTask != null) tasks.Add(_writerTask);
            lock (_streamSync)
            {
                if (_streamTask != null) tasks.Add(_streamTask);
            }

            if (tasks.Count > 0)
            {
                await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(maxWaitMs)).ConfigureAwait(false);
            }
        }

        public void ScheduleTransportAbort(int delayMs)
        {
            try { _transportAbortCts.CancelAfter(delayMs); } catch { }
        }

        public void AbortTransport()
        {
            try { _transportAbortCts.Cancel(); } catch { }
        }

        public async Task SendSupersededAndCloseAsync()
        {
            using var closeTimeout = new CancellationTokenSource(400);
            bool lockAcquired = false;
            try
            {
                await _sendLock.WaitAsync(closeTimeout.Token).ConfigureAwait(false);
                lockAcquired = true;

                if (_socket.State == WebSocketState.Open)
                {
                    byte[] notice = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                    {
                        @event = "session_superseded",
                        code = CloseCodeSessionSuperseded,
                        reason = "Superseded by newer single-agent control session"
                    }, WsJsonOptions));

                    await _socket.SendAsync(
                        notice,
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        closeTimeout.Token).ConfigureAwait(false);
                }

                if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await _socket.CloseOutputAsync(
                        (WebSocketCloseStatus)CloseCodeSessionSuperseded,
                        "SessionSuperseded",
                        closeTimeout.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                // Best-effort close on displaced socket
            }
            finally
            {
                if (lockAcquired)
                {
                    _sendLock.Release();
                }
            }
        }

        public void EnqueueJson(object payload)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            string json = JsonSerializer.Serialize(payload, WsJsonOptions);
            if (_textOutbox.Writer.TryWrite(json))
            {
                SignalOutbound();
            }
        }

        private void SignalOutbound()
        {
            try
            {
                _outboundWake.Release();
            }
            catch
            {
                // Disposed or saturated
            }
        }

        public async Task RunReceiveLoopAsync()
        {
            byte[] chunkBuf = ArrayPool<byte>.Shared.Rent(16 * 1024);
            byte[] msgBuf = ArrayPool<byte>.Shared.Rent(64 * 1024);

            try
            {
                while (!_transportAbortCts.IsCancellationRequested &&
                       (_socket.State == WebSocketState.Open || _socket.State == WebSocketState.CloseSent))
                {
                    int totalBytes = 0;
                    WebSocketReceiveResult result;

                    do
                    {
                        result = await _socket.ReceiveAsync(chunkBuf, _transportAbortCts.Token).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            if (_socket.State == WebSocketState.CloseReceived)
                            {
                                await SafeCloseAsync(WebSocketCloseStatus.NormalClosure, "Client closed").ConfigureAwait(false);
                            }
                            return;
                        }

                        if (totalBytes + result.Count > MaxInboundMessageBytes)
                        {
                            await SafeCloseAsync(
                                WebSocketCloseStatus.MessageTooBig,
                                "Inbound message exceeds 256 KiB limit.").ConfigureAwait(false);
                            return;
                        }

                        if (totalBytes + result.Count > msgBuf.Length)
                        {
                            int nextSize = Math.Min(MaxInboundMessageBytes, Math.Max(msgBuf.Length * 2, totalBytes + result.Count));
                            byte[] grown = ArrayPool<byte>.Shared.Rent(nextSize);
                            Buffer.BlockCopy(msgBuf, 0, grown, 0, totalBytes);
                            ArrayPool<byte>.Shared.Return(msgBuf);
                            msgBuf = grown;
                        }

                        if (result.Count > 0)
                        {
                            Buffer.BlockCopy(chunkBuf, 0, msgBuf, totalBytes, result.Count);
                            totalBytes += result.Count;
                        }
                    }
                    while (!result.EndOfMessage);

                    if (_workCts.IsCancellationRequested)
                    {
                        continue;
                    }

                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        EnqueueJson(new { ok = false, error = "Inbound binary frames are not supported; send UTF-8 JSON text commands." });
                        continue;
                    }

                    HandleInboundTextMessage(msgBuf.AsSpan(0, totalBytes));
                }
            }
            catch (OperationCanceledException)
            {
                // Session cancelled or superseded
            }
            catch (WebSocketException)
            {
                // Peer disconnected abruptly
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunkBuf);
                ArrayPool<byte>.Shared.Return(msgBuf);
            }
        }

        private void HandleInboundTextMessage(ReadOnlySpan<byte> utf8Bytes)
        {
            string? reqId = null;
            string? op = null;

            try
            {
                var docOpts = new JsonDocumentOptions { MaxDepth = 32 };
                using var doc = JsonDocument.Parse(utf8Bytes.ToArray(), docOpts);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    EnqueueJson(new { ok = false, error = "Command payload must be a JSON object." });
                    return;
                }

                if (root.TryGetProperty("id", out var idProp) && idProp.ValueKind != JsonValueKind.Null)
                {
                    reqId = idProp.ToString();
                }

                if (root.TryGetProperty("op", out var opProp) && opProp.ValueKind == JsonValueKind.String)
                {
                    op = opProp.GetString();
                }
                else if (root.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String)
                {
                    op = typeProp.GetString();
                }

                if (string.IsNullOrWhiteSpace(op))
                {
                    EnqueueJson(new { id = reqId, ok = false, error = "Missing required 'op' field." });
                    return;
                }

                var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                if (root.TryGetProperty("args", out var argsProp) && argsProp.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in argsProp.EnumerateObject())
                    {
                        args[p.Name] = ToolHandlers.JsonElementToObject(p.Value);
                    }
                }
                else
                {
                    foreach (var p in root.EnumerateObject())
                    {
                        if (!string.Equals(p.Name, "id", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(p.Name, "op", StringComparison.OrdinalIgnoreCase))
                        {
                            args[p.Name] = ToolHandlers.JsonElementToObject(p.Value);
                        }
                    }
                }

                string normOp = op.Trim().ToLowerInvariant();
                switch (normOp)
                {
                    case "ping":
                        EnqueueJson(new
                        {
                            id = reqId,
                            ok = true,
                            op = "ping",
                            result = new
                            {
                                pong = true,
                                timestampQpcUs = (ulong)((double)Stopwatch.GetTimestamp() * 1_000_000.0 / Stopwatch.Frequency),
                                droppedFrames = _frameBox.DroppedFrames
                            }
                        });
                        break;

                    case "release_inputs" or "release-inputs":
                        var released = ActiveInputTracker.ReleaseAll();
                        EnqueueJson(new { id = reqId, ok = true, op = "release_inputs", result = released });
                        break;

                    case "probe" or "probe_screen" or "probe-screen":
                        _ = Task.Run(() =>
                        {
                            var probeRes = _handlers.CallTool("probe_screen", args);
                            SendToolCallAck(reqId, "probe_screen", probeRes);
                        });
                        break;

                    case "manage_shared_frame" or "shared_frame" or "shared-frame":
                        _ = Task.Run(() =>
                        {
                            var shmRes = _handlers.ManageSharedFrame(args);
                            SendToolCallAck(reqId, "manage_shared_frame", shmRes);
                        });
                        break;

                    case "subscribe_frames":
                        StartFrameStream(reqId, args);
                        break;

                    case "unsubscribe_frames":
                        StopFrameStream();
                        EnqueueJson(new { id = reqId, ok = true, op = "unsubscribe_frames", result = new { subscribed = false, droppedFrames = _frameBox.DroppedFrames } });
                        break;

                    case "cancel_wait":
                        CancelActiveWait();
                        EnqueueJson(new { id = reqId, ok = true, op = "cancel_wait" });
                        break;

                    case "wait_for" or "wait-for":
                        _ = Task.Run(() => ExecuteWaitOnSocketAsync(reqId, args));
                        break;

                    case "get_app_state" or "query" or "list_apps" or "capture_screen":
                        _ = Task.Run(() =>
                        {
                            var readRes = _handlers.CallTool(normOp, args);
                            SendToolCallAck(reqId, normOp, readRes);
                        });
                        break;

                    default:
                        if (!_inputInbox.Writer.TryWrite(new QueuedInputCommand(reqId, normOp, args)))
                        {
                            EnqueueJson(new { id = reqId, ok = false, op = normOp, error = "Session input command queue is full (64)." });
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                EnqueueJson(new { id = reqId, ok = false, op, error = $"Invalid JSON message: {ex.Message}" });
            }
        }

        private async Task ExecuteWaitOnSocketAsync(string? reqId, Dictionary<string, object?> args)
        {
            using var linkedWaitCts = CancellationTokenSource.CreateLinkedTokenSource(_workCts.Token);
            lock (_waitSync)
            {
                _activeWaitCts?.Cancel();
                _activeWaitCts = linkedWaitCts;
            }

            try
            {
                var waitResult = await _handlers.WaitForAsync(args, linkedWaitCts.Token).ConfigureAwait(false);
                EnqueueJson(new { id = reqId, ok = waitResult.Ok, op = "wait_for", result = waitResult, error = waitResult.ReactionError });
            }
            catch (OperationCanceledException)
            {
                if (!_workCts.IsCancellationRequested)
                {
                    EnqueueJson(new { id = reqId, ok = false, op = "wait_for", error = "wait_for cancelled." });
                }
            }
            catch (Exception ex)
            {
                EnqueueJson(new { id = reqId, ok = false, op = "wait_for", error = ex.Message });
            }
            finally
            {
                lock (_waitSync)
                {
                    if (ReferenceEquals(_activeWaitCts, linkedWaitCts))
                    {
                        _activeWaitCts = null;
                    }
                }
            }
        }

        private void CancelActiveWait()
        {
            lock (_waitSync)
            {
                try { _activeWaitCts?.Cancel(); } catch { }
                _activeWaitCts = null;
            }
        }

        private async Task InputWorkerLoopAsync()
        {
            try
            {
                await foreach (var cmd in _inputInbox.Reader.ReadAllAsync(_workCts.Token).ConfigureAwait(false))
                {
                    try
                    {
                        if (!cmd.Args.ContainsKey("skip_snapshot") && !cmd.Args.ContainsKey("skipSnapshot"))
                        {
                            cmd.Args["skip_snapshot"] = true;
                        }

                        var res = await ActionLock.ExecuteAsync(
                            () => _handlers.ExecuteActionLocked(cmd.Op, cmd.Args),
                            timeoutMs: 15_000,
                            cancellationToken: _workCts.Token).ConfigureAwait(false);

                        SendToolCallAck(cmd.Id, cmd.Op, res);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        EnqueueJson(new { id = cmd.Id, ok = false, op = cmd.Op, error = ex.Message });
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Clean shutdown
            }
        }

        private void SendToolCallAck(string? reqId, string op, ToolCallResult res)
        {
            string? text = res.Content.FirstOrDefault()?.Text;
            if (res.IsError)
            {
                EnqueueJson(new { id = reqId, ok = false, op, error = text ?? "Action failed" });
                return;
            }

            object? parsedPayload = text;
            if (!string.IsNullOrEmpty(text) && (text.StartsWith('{') || text.StartsWith('[')))
            {
                try
                {
                    parsedPayload = JsonSerializer.Deserialize<JsonElement>(text);
                }
                catch
                {
                    parsedPayload = text;
                }
            }

            EnqueueJson(new { id = reqId, ok = true, op, result = parsedPayload });
        }

        private void StartFrameStream(string? reqId, Dictionary<string, object?> args)
        {
            var cfg = OcuConfig.GetEffectiveValues();
            string defaultFormat = cfg.TryGetValue("stream.format", out var sf) && sf is string sFmt ? sFmt : "jpg";
            int defaultMaxFps = cfg.TryGetValue("stream.maxFps", out var smf) && smf is int iFps ? iFps : 30;

            string? app = args.TryGetValue("app", out var rawApp) ? rawApp?.ToString()?.Trim() : null;
            int? pid = null;
            if (args.TryGetValue("pid", out var rawPid) && rawPid != null)
            {
                if (!int.TryParse(rawPid.ToString(), out int parsedPid) || parsedPid <= 0)
                {
                    EnqueueJson(new { id = reqId, ok = false, op = "subscribe_frames", error = "Invalid pid: must be a positive integer." });
                    return;
                }
                pid = parsedPid;
            }

            string? display = args.TryGetValue("display", out var rawDisp) ? rawDisp?.ToString()?.Trim() : null;
            string rawFmt = (args.TryGetValue("format", out var fObj) ? fObj?.ToString() : null) ?? defaultFormat;
            if (!OcuConfig.TryNormalizeStreamFormat(rawFmt, out string normFmt))
            {
                EnqueueJson(new { id = reqId, ok = false, op = "subscribe_frames", error = $"Invalid stream format '{rawFmt}'. Supported: bgra32, jpg, png." });
                return;
            }

            int maxFps = defaultMaxFps;
            if ((args.TryGetValue("max_fps", out var fpsObj) || args.TryGetValue("maxFps", out fpsObj)) && fpsObj != null)
            {
                if (!int.TryParse(fpsObj.ToString(), out maxFps) || maxFps < 1 || maxFps > 120)
                {
                    EnqueueJson(new { id = reqId, ok = false, op = "subscribe_frames", error = "max_fps must be between 1 and 120." });
                    return;
                }
            }

            bool clientOnly = true;
            if ((args.TryGetValue("client_only", out var coObj) || args.TryGetValue("clientOnly", out coObj)) && coObj != null &&
                bool.TryParse(coObj.ToString(), out bool parsedCo))
            {
                clientOnly = parsedCo;
            }

            bool onlyOnChange = true;
            if ((args.TryGetValue("only_on_change", out var oocObj) || args.TryGetValue("onlyOnChange", out oocObj)) && oocObj != null &&
                bool.TryParse(oocObj.ToString(), out bool parsedOoc))
            {
                onlyOnChange = parsedOoc;
            }

            double? regX = TryReadDouble(args, "region_x") ?? TryReadDouble(args, "regionX");
            double? regY = TryReadDouble(args, "region_y") ?? TryReadDouble(args, "regionY");
            double? regW = TryReadDouble(args, "region_width") ?? TryReadDouble(args, "regionWidth");
            double? regH = TryReadDouble(args, "region_height") ?? TryReadDouble(args, "regionHeight");

            try
            {
                _ = ScreenCapture.ValidateOptionalRegion(regX, regY, regW, regH, "region_");
                if (!string.IsNullOrWhiteSpace(app) || pid.HasValue)
                {
                    _ = _automation.ResolveApp(app, pid);
                }
            }
            catch (Exception ex)
            {
                EnqueueJson(new { id = reqId, ok = false, op = "subscribe_frames", error = ex.Message });
                return;
            }

            StopFrameStream();

            EnqueueJson(new
            {
                id = reqId,
                ok = true,
                op = "subscribe_frames",
                result = new
                {
                    subscribed = true,
                    format = normFmt,
                    maxFps,
                    clientOnly,
                    onlyOnChange
                }
            });

            lock (_streamSync)
            {
                _streamCts = CancellationTokenSource.CreateLinkedTokenSource(_workCts.Token);
                var linkedToken = _streamCts.Token;
                _streamTask = Task.Run(() => FrameProducerLoopAsync(
                    app,
                    pid,
                    display,
                    normFmt,
                    maxFps,
                    clientOnly,
                    onlyOnChange,
                    regX,
                    regY,
                    regW,
                    regH,
                    linkedToken));
            }
        }

        private void StopFrameStream()
        {
            CancellationTokenSource? cts;
            Task? prevTask;
            lock (_streamSync)
            {
                cts = _streamCts;
                prevTask = _streamTask;
                _streamCts = null;
                _streamTask = null;
            }

            try { cts?.Cancel(); } catch { }
            try { prevTask?.Wait(250); } catch { }
            _frameBox.TryTake()?.Dispose();
            if (prevTask == null || prevTask.IsCompleted)
            {
                try { cts?.Dispose(); } catch { }
            }
        }

        private async Task FrameProducerLoopAsync(
            string? app,
            int? pid,
            string? display,
            string normFormat,
            int maxFps,
            bool clientOnly,
            bool onlyOnChange,
            double? regionX,
            double? regionY,
            double? regionWidth,
            double? regionHeight,
            CancellationToken token)
        {
            double intervalMs = 1000.0 / maxFps;
            double ticksPerMs = Stopwatch.Frequency / 1000.0;
            long epochTicks = Stopwatch.GetTimestamp();
            long tickIndex = 0;
            ulong lastHash = 0;
            int lastW = 0;
            int lastH = 0;
            bool hasPrev = false;

            byte fmtCode = normFormat switch
            {
                "bgra32" => FormatBgra32,
                "png" => FormatPng,
                _ => FormatJpeg
            };

            bool hasRegion = regionWidth.HasValue && regionHeight.HasValue;
            ushort flags = (ushort)((clientOnly ? FlagClientOnly : 0) |
                                    (hasRegion ? FlagRegionRoi : 0) |
                                    (onlyOnChange ? FlagOnlyOnChange : 0));

            try
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        ProduceSingleFrame(
                            app,
                            pid,
                            display,
                            normFormat,
                            fmtCode,
                            flags,
                            clientOnly,
                            onlyOnChange,
                            regionX,
                            regionY,
                            regionWidth,
                            regionHeight,
                            ref lastHash,
                            ref lastW,
                            ref lastH,
                            ref hasPrev);
                    }
                    catch
                    {
                        // Transient capture miss
                    }

                    tickIndex++;
                    long targetTicks = epochTicks + (long)Math.Round(tickIndex * intervalMs * ticksPerMs);
                    long remTicks = targetTicks - Stopwatch.GetTimestamp();
                    if (remTicks > 0)
                    {
                        double remMs = remTicks / ticksPerMs;
                        if (remMs >= 3.0)
                        {
                            await Task.Delay(Math.Max(1, (int)(remMs - 1.0)), token).ConfigureAwait(false);
                        }
                        else
                        {
                            PrecisionTimer.WaitUntilTimestamp(targetTicks);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal unsubscribe / disconnect
            }
        }

        private void ProduceSingleFrame(
            string? app,
            int? pid,
            string? display,
            string normFormat,
            byte fmtCode,
            ushort flags,
            bool clientOnly,
            bool onlyOnChange,
            double? regionX,
            double? regionY,
            double? regionWidth,
            double? regionHeight,
            ref ulong lastHash,
            ref int lastW,
            ref int lastH,
            ref bool hasPrev)
        {
            Bitmap? rawBmp = null;
            int originX = (int)Math.Round(regionX ?? 0.0);
            int originY = (int)Math.Round(regionY ?? 0.0);
            bool hasTargetApp = !string.IsNullOrWhiteSpace(app) || pid.HasValue;

            if (hasTargetApp)
            {
                var proc = _automation.ResolveApp(app, pid);
                IntPtr hwnd = WindowManager.GetTargetHwnd(proc);
                if (hwnd == IntPtr.Zero) hwnd = proc.MainWindowHandle;
                var winBounds = _automation.GetWindowBounds(proc, null, hwnd);
                rawBmp = ScreenCapture.CaptureWindowRawBitmap(
                    winBounds,
                    hwnd,
                    regionX,
                    regionY,
                    regionWidth,
                    regionHeight,
                    clientOnly: clientOnly,
                    fastNonBlocking: true);
            }
            else
            {
                rawBmp = ScreenCapture.CaptureScreenRegionBitmap(
                    string.IsNullOrWhiteSpace(display) ? "1" : display!,
                    regionX,
                    regionY,
                    regionWidth,
                    regionHeight,
                    fastNonBlocking: true);
            }

            if (rawBmp == null)
            {
                return;
            }

            using (rawBmp)
            {
                ulong qpcUs = (ulong)((double)Stopwatch.GetTimestamp() * 1_000_000.0 / Stopwatch.Frequency);
                int w = rawBmp.Width;
                int h = rawBmp.Height;
                if (w <= 0 || h <= 0 || w > ushort.MaxValue || h > ushort.MaxValue)
                {
                    return;
                }

                if (fmtCode == FormatBgra32)
                {
                    int payloadBytes = w * h * 4;
                    int totalPacketBytes = TkrfHeaderBytes + payloadBytes;
                    byte[] rented = FramePool.Rent(totalPacketBytes);

                    var (hash, _) = ScreenCapture.CopyBitmapBgra32AndHash(
                        rawBmp,
                        rented.AsSpan(TkrfHeaderBytes, payloadBytes),
                        out _,
                        out _,
                        out _);

                    if (onlyOnChange && hasPrev && hash == lastHash && w == lastW && h == lastH)
                    {
                        FramePool.Return(rented);
                        return;
                    }

                    lastHash = hash;
                    lastW = w;
                    lastH = h;
                    hasPrev = true;

                    uint seq = Interlocked.Increment(ref _frameSeq);
                    WriteTkrfHeader(
                        rented.AsSpan(0, TkrfHeaderBytes),
                        fmtCode,
                        flags,
                        seq,
                        (ushort)w,
                        (ushort)h,
                        qpcUs,
                        originX,
                        originY);

                    _frameBox.Publish(new PooledFrame(rented, totalPacketBytes, seq, w, h, fmtCode, flags, qpcUs, originX, originY, hash));
                    SignalOutbound();
                    return;
                }

                // JPEG or PNG encoded binary frame
                int rawBgraBytes = w * h * 4;
                byte[] scratch = FramePool.Rent(rawBgraBytes);
                ulong contentHash;
                try
                {
                    (contentHash, _) = ScreenCapture.CopyBitmapBgra32AndHash(
                        rawBmp,
                        scratch.AsSpan(0, rawBgraBytes),
                        out _,
                        out _,
                        out _);
                }
                finally
                {
                    FramePool.Return(scratch);
                }

                if (onlyOnChange && hasPrev && contentHash == lastHash && w == lastW && h == lastH)
                {
                    return;
                }

                var imgCfg = ScreenCapture.ApplyOverrides(OcuConfig.LoadImageCaptureConfig(), normFormat, null);
                byte[] encoded = ScreenCapture.EncodeBitmapToBytes(rawBmp, imgCfg, out int finalW, out int finalH, out _);

                lastHash = contentHash;
                lastW = w;
                lastH = h;
                hasPrev = true;

                int packetLen = TkrfHeaderBytes + encoded.Length;
                byte[] packetBuf = FramePool.Rent(packetLen);
                uint encSeq = Interlocked.Increment(ref _frameSeq);
                WriteTkrfHeader(
                    packetBuf.AsSpan(0, TkrfHeaderBytes),
                    fmtCode,
                    flags,
                    encSeq,
                    (ushort)finalW,
                    (ushort)finalH,
                    qpcUs,
                    originX,
                    originY);
                Buffer.BlockCopy(encoded, 0, packetBuf, TkrfHeaderBytes, encoded.Length);

                _frameBox.Publish(new PooledFrame(packetBuf, packetLen, encSeq, finalW, finalH, fmtCode, flags, qpcUs, originX, originY, contentHash));
                SignalOutbound();
            }
        }

        private async Task OutboundWriterLoopAsync()
        {
            try
            {
                while (!_workCts.IsCancellationRequested && !_transportAbortCts.IsCancellationRequested && _socket.State == WebSocketState.Open)
                {
                    bool sentAny = false;

                    // 1. Priority-drain all pending JSON control/ACK text messages first
                    while (!_workCts.IsCancellationRequested && _textOutbox.Reader.TryRead(out string? jsonMsg))
                    {
                        sentAny = true;
                        byte[] utf8 = Encoding.UTF8.GetBytes(jsonMsg);
                        await _sendLock.WaitAsync(_transportAbortCts.Token).ConfigureAwait(false);
                        try
                        {
                            if (!_workCts.IsCancellationRequested && _socket.State == WebSocketState.Open)
                            {
                                await _socket.SendAsync(
                                    utf8,
                                    WebSocketMessageType.Text,
                                    endOfMessage: true,
                                    _transportAbortCts.Token).ConfigureAwait(false);
                            }
                        }
                        finally
                        {
                            _sendLock.Release();
                        }
                    }

                    // 2. Send latest conflated binary video frame (at most 1 per cycle so control ACKs never starve)
                    if (!_workCts.IsCancellationRequested)
                    {
                        using PooledFrame? frame = _frameBox.TryTake();
                        if (frame?.Buffer != null)
                        {
                            sentAny = true;
                            await _sendLock.WaitAsync(_transportAbortCts.Token).ConfigureAwait(false);
                            try
                            {
                                if (!_workCts.IsCancellationRequested && _socket.State == WebSocketState.Open)
                                {
                                    await _socket.SendAsync(
                                        new ArraySegment<byte>(frame.Buffer, 0, frame.Length),
                                        WebSocketMessageType.Binary,
                                        endOfMessage: true,
                                        _transportAbortCts.Token).ConfigureAwait(false);
                                }
                            }
                            finally
                            {
                                _sendLock.Release();
                            }
                        }
                    }

                    if (_workCts.IsCancellationRequested)
                    {
                        break;
                    }

                    if (!sentAny)
                    {
                        await _outboundWake.WaitAsync(_workCts.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Clean exit
            }
            catch (WebSocketException)
            {
                // Socket disconnected
            }
        }

        private async Task SafeCloseAsync(WebSocketCloseStatus status, string description)
        {
            using var closeCts = new CancellationTokenSource(500);
            try
            {
                await _sendLock.WaitAsync(closeCts.Token).ConfigureAwait(false);
                try
                {
                    if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    {
                        await _socket.CloseOutputAsync(status, description, closeCts.Token).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _sendLock.Release();
                }
            }
            catch
            {
                // Ignore if socket already closed
            }
        }

        public async Task ShutdownAsync()
        {
            await StopWorkLoopsAsync(300).ConfigureAwait(false);
            AbortTransport();
        }

        private static double? TryReadDouble(Dictionary<string, object?> args, string key)
        {
            if (!args.TryGetValue(key, out var val) || val == null) return null;
            if (val is double d) return d;
            if (val is float f) return f;
            if (val is int i) return i;
            if (val is long l) return l;
            return double.TryParse(val.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double p) ? p : null;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            CancelWork();
            AbortTransport();
            _frameBox.Dispose();
            try { _outboundWake.Dispose(); } catch { }
            try { _sendLock.Dispose(); } catch { }
            try { _workCts.Dispose(); } catch { }
            try { _transportAbortCts.Dispose(); } catch { }
        }
    }
}
