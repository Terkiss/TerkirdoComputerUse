using System.Diagnostics;
using System.Drawing;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using TerkirdoComputer.Core;

namespace TerkirdoComputer.Windows;

/// <summary>
/// 64-byte cache-line-aligned Per-Slot + Global Seqlock Double-Buffer header ("TKSM", Python struct "&lt;IHHQQQQIIHHBBHQ").
/// Every 64-bit sequence/timestamp/hash field sits on an exact 8-byte natural alignment boundary.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct TksmHeader
{
    [FieldOffset(0x00)] public uint Magic;          // 0x4D534B54 ('TKSM')
    [FieldOffset(0x04)] public ushort Version;      // 1
    [FieldOffset(0x06)] public ushort HeaderSize;   // 64
    [FieldOffset(0x08)] public ulong GlobalSeq;     // Odd = updating header, Even = committed
    [FieldOffset(0x10)] public ulong Slot0Seq;      // Odd = writing Slot 0, Even = quiescent
    [FieldOffset(0x18)] public ulong Slot1Seq;      // Odd = writing Slot 1, Even = quiescent
    [FieldOffset(0x20)] public ulong TimestampQpcUs;// Capture QPC timestamp in microseconds
    [FieldOffset(0x28)] public uint MaxSlotBytes;   // Pre-allocated bytes per slot
    [FieldOffset(0x2C)] public uint Stride;         // Width * 4
    [FieldOffset(0x30)] public ushort Width;        // Active frame width
    [FieldOffset(0x32)] public ushort Height;       // Active frame height
    [FieldOffset(0x34)] public byte ActiveSlot;     // 0 or 1
    [FieldOffset(0x35)] public byte PixelFormat;    // 0 = BGRA32
    [FieldOffset(0x36)] public ushort Flags;        // 0x01 = Active, 0x02 = ClientOnly, 0x04 = Closed
    [FieldOffset(0x38)] public ulong Fnv1aHash;     // 64-bit FNV-1a content hash
}

/// <summary>
/// Pillar 3 (v2.0.0 — Tool #24 manage_shared_frame):
/// Pagefile-backed Windows Local\ session-isolated shared-memory zero-copy BGRA32 frame publisher.
/// Implements a 64-byte Per-Slot + Global Seqlock Double-Buffer ("TKSM") with safe publisher drain before UnmapViewOfFile.
/// </summary>
public static class SharedFrameStreamService
{
    public const uint MagicTksm = 0x4D534B54; // 'TKSM' in little-endian ASCII
    public const ushort CurrentVersion = 1;
    public const ushort HeaderByteSize = 64;
    public const uint MaxAllowedSlotBytes = 3840u * 2160u * 4u; // 4K ceiling (33,177,600 B per slot)

    public const ushort FlagActive = 0x0001;
    public const ushort FlagClientOnly = 0x0002;
    public const ushort FlagClosed = 0x0004;

    private static readonly SemaphoreSlim LifecycleLock = new(1, 1);
    private static ActiveSharedSession? _activeSession;

    static SharedFrameStreamService()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { StopSync(); } catch { }
        };
    }

    public static SharedFrameStatusResult StartSync(
        AutomationService automationService,
        string? app = null,
        int? pid = null,
        string? display = null,
        bool clientOnly = true,
        double? regionX = null,
        double? regionY = null,
        double? regionWidth = null,
        double? regionHeight = null,
        int maxFps = 60,
        bool onlyOnChange = true,
        int? ownerPid = null,
        int? maxWidthOverride = null,
        int? maxHeightOverride = null) =>
        StartAsync(
            automationService,
            app,
            pid,
            display,
            clientOnly,
            regionX,
            regionY,
            regionWidth,
            regionHeight,
            maxFps,
            onlyOnChange,
            ownerPid,
            maxWidthOverride,
            maxHeightOverride).GetAwaiter().GetResult();

    public static async Task<SharedFrameStatusResult> StartAsync(
        AutomationService automationService,
        string? app = null,
        int? pid = null,
        string? display = null,
        bool clientOnly = true,
        double? regionX = null,
        double? regionY = null,
        double? regionWidth = null,
        double? regionHeight = null,
        int maxFps = 60,
        bool onlyOnChange = true,
        int? ownerPid = null,
        int? maxWidthOverride = null,
        int? maxHeightOverride = null,
        CancellationToken cancellationToken = default)
    {
        if (maxFps < 1 || maxFps > 240)
        {
            throw new ArgumentException("max_fps must be between 1 and 240.");
        }

        if (ownerPid.HasValue && ownerPid.Value <= 0)
        {
            throw new ArgumentException("owner_pid must be a positive integer.");
        }

        _ = ScreenCapture.ValidateOptionalRegion(regionX, regionY, regionWidth, regionHeight, "region_");

        bool hasTargetApp = !string.IsNullOrWhiteSpace(app) || pid.HasValue;
        int initialW = 1280;
        int initialH = 720;
        string coordSpace;

        if (hasTargetApp)
        {
            var proc = automationService.ResolveApp(app, pid);
            IntPtr hwnd = WindowManager.GetTargetHwnd(proc);
            if (hwnd == IntPtr.Zero) hwnd = proc.MainWindowHandle;

            coordSpace = clientOnly ? "client" : "window";
            var winBounds = automationService.GetWindowBounds(proc, null, hwnd);
            var cliBounds = clientOnly && hwnd != IntPtr.Zero ? WindowManager.GetClientScreenBounds(hwnd) : null;
            var refBounds = cliBounds ?? winBounds;
            if (refBounds != null && refBounds.Width > 0 && refBounds.Height > 0)
            {
                initialW = (int)Math.Round(refBounds.Width);
                initialH = (int)Math.Round(refBounds.Height);
            }
        }
        else
        {
            coordSpace = "screen";
            string targetDisp = string.IsNullOrWhiteSpace(display) ? "1" : display.Trim();
            var displays = ScreenCapture.GetDisplays();
            if (!string.Equals(targetDisp, "all", StringComparison.OrdinalIgnoreCase))
            {
                DisplayDescriptor? found = null;
                if (int.TryParse(targetDisp, out int dIdx))
                {
                    found = displays.FirstOrDefault(d => d.Index == dIdx);
                }
                found ??= displays.FirstOrDefault(d => string.Equals(d.Name, targetDisp, StringComparison.OrdinalIgnoreCase));
                if (found == null)
                {
                    throw new ArgumentException($"displayNotFound(\"{display}\")");
                }
                initialW = (int)Math.Round(found.Bounds.Width);
                initialH = (int)Math.Round(found.Bounds.Height);
            }
            else if (displays.Count > 0)
            {
                initialW = (int)Math.Round(displays[0].Bounds.Width);
                initialH = (int)Math.Round(displays[0].Bounds.Height);
            }
        }

        if (regionWidth.HasValue && regionHeight.HasValue)
        {
            initialW = Math.Clamp((int)Math.Round(regionWidth.Value), 1, 3840);
            initialH = Math.Clamp((int)Math.Round(regionHeight.Value), 1, 2160);
        }

        // Pre-size slot capacity to monitor ceiling (or explicit test ceiling), capped at 4K (3840x2160x4)
        int maxMonitorW = 1920;
        int maxMonitorH = 1080;
        try
        {
            foreach (var d in ScreenCapture.GetDisplays())
            {
                maxMonitorW = Math.Max(maxMonitorW, (int)Math.Round(d.Bounds.Width));
                maxMonitorH = Math.Max(maxMonitorH, (int)Math.Round(d.Bounds.Height));
            }
        }
        catch { }

        int capW = maxWidthOverride.HasValue
            ? Math.Clamp(maxWidthOverride.Value, 16, 3840)
            : Math.Clamp(Math.Max(maxMonitorW, initialW), 64, 3840);
        int capH = maxHeightOverride.HasValue
            ? Math.Clamp(maxHeightOverride.Value, 16, 2160)
            : Math.Clamp(Math.Max(maxMonitorH, initialH), 64, 2160);

        uint maxSlotBytes = (uint)Math.Min((long)capW * capH * 4L, MaxAllowedSlotBytes);
        string randomHex = RandomNumberGenerator.GetHexString(32, lowercase: true);
        string mapName = $"Local\\TerkirdoFrame_{randomHex}";

        await LifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_activeSession != null)
            {
                await _activeSession.StopAndDisposeAsync().ConfigureAwait(false);
                _activeSession = null;
            }

            var session = new ActiveSharedSession(
                automationService,
                mapName,
                maxSlotBytes,
                app,
                pid,
                display,
                clientOnly,
                coordSpace,
                regionX,
                regionY,
                regionWidth,
                regionHeight,
                maxFps,
                onlyOnChange,
                ownerPid);

            session.Start();
            // Wait briefly (up to 60ms) for first frame publish so immediate readers find GlobalSeq >= 2
            await session.WaitForFirstFrameAsync(60).ConfigureAwait(false);
            _activeSession = session;
            return session.GetStatus();
        }
        finally
        {
            LifecycleLock.Release();
        }
    }

    public static SharedFrameStatusResult StopSync() =>
        StopAsync().GetAwaiter().GetResult();

    public static async Task<SharedFrameStatusResult> StopAsync(CancellationToken cancellationToken = default)
    {
        await LifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_activeSession == null)
            {
                return new SharedFrameStatusResult { Ok = true, Active = false };
            }

            var stopped = _activeSession;
            _activeSession = null;
            await stopped.StopAndDisposeAsync().ConfigureAwait(false);
            return new SharedFrameStatusResult { Ok = true, Active = false };
        }
        finally
        {
            LifecycleLock.Release();
        }
    }

    public static SharedFrameStatusResult GetStatus()
    {
        var session = Volatile.Read(ref _activeSession);
        if (session == null || !session.IsRunning)
        {
            return new SharedFrameStatusResult { Ok = true, Active = false };
        }
        return session.GetStatus();
    }

    /// <summary>
    /// Safe MemoryMappedViewAccessor wrapper around InitializeTksmHeader + WriteFrameToTksmView for callers/tests without unsafe blocks.
    /// </summary>
    public static unsafe void InitializeTksmAccessor(MemoryMappedViewAccessor accessor, uint maxSlotBytes, bool clientOnly = true)
    {
        byte* ptr = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        try
        {
            *(TksmHeader*)ptr = new TksmHeader
            {
                Magic = MagicTksm,
                Version = CurrentVersion,
                HeaderSize = HeaderByteSize,
                MaxSlotBytes = maxSlotBytes,
                PixelFormat = 0,
                Flags = (ushort)(FlagActive | (clientOnly ? FlagClientOnly : 0))
            };
            Interlocked.MemoryBarrier();
        }
        finally
        {
            accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    public static unsafe void WriteFrameToAccessor(
        MemoryMappedViewAccessor accessor,
        uint maxSlotBytes,
        ReadOnlySpan<byte> packedBgra,
        int width,
        int height,
        bool clientOnly,
        ref byte currentActiveSlot,
        ref ulong globalSeq,
        ref ulong slot0Seq,
        ref ulong slot1Seq)
    {
        byte* ptr = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        try
        {
            WriteFrameToTksmView(
                ptr,
                maxSlotBytes,
                packedBgra,
                width,
                height,
                clientOnly,
                ref currentActiveSlot,
                ref globalSeq,
                ref slot0Seq,
                ref slot1Seq);
        }
        finally
        {
            accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    public static unsafe void WriteGlobalSeqToAccessorForTest(MemoryMappedViewAccessor accessor, ulong globalSeq)
    {
        byte* ptr = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        try
        {
            Volatile.Write(ref ((TksmHeader*)ptr)->GlobalSeq, globalSeq);
            Interlocked.MemoryBarrier();
        }
        finally
        {
            accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    /// <summary>
    /// Writes a synthetic BGRA32 buffer into an open "TKSM" view using the exact Per-Slot + Global Seqlock protocol
    /// (enabling deterministic >5,000-frame multi-reader tear-detection stress tests).
    /// </summary>
    public static unsafe void WriteFrameToTksmView(
        byte* basePtr,
        uint maxSlotBytes,
        ReadOnlySpan<byte> packedBgra,
        int width,
        int height,
        bool clientOnly,
        ref byte currentActiveSlot,
        ref ulong globalSeq,
        ref ulong slot0Seq,
        ref ulong slot1Seq)
    {
        int stride = width * 4;
        long frameBytes = (long)stride * height;
        if (width <= 0 || height <= 0 || frameBytes > maxSlotBytes || packedBgra.Length < frameBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(packedBgra), "Frame byte size exceeds MaxSlotBytes or source span.");
        }

        TksmHeader* hdr = (TksmHeader*)basePtr;
        byte writeSlot = (byte)(1 - currentActiveSlot);
        ref ulong localSlotCounter = ref (writeSlot == 0 ? ref slot0Seq : ref slot1Seq);
        ref ulong headerSlotSeq = ref (writeSlot == 0 ? ref hdr->Slot0Seq : ref hdr->Slot1Seq);

        // 1. Mark target slot odd BEFORE writing pixel payload
        ulong oddSlot = localSlotCounter + 1UL;
        Volatile.Write(ref headerSlotSeq, oddSlot);
        Interlocked.MemoryBarrier();

        byte* slotPtr = basePtr + HeaderByteSize + ((long)writeSlot * maxSlotBytes);
        fixed (byte* srcPtr = packedBgra)
        {
            Buffer.MemoryCopy(srcPtr, slotPtr, maxSlotBytes, frameBytes);
        }

        ulong fnv1a = ScreenProbe.ComputeFnv1aHashBgra32(packedBgra[..(int)frameBytes], width, height, stride);
        ulong qpcUs = (ulong)((double)Stopwatch.GetTimestamp() * 1_000_000.0 / Stopwatch.Frequency);

        // 2. Full memory barrier, then mark target slot even
        Interlocked.MemoryBarrier();
        localSlotCounter = oddSlot + 1UL;
        Volatile.Write(ref headerSlotSeq, localSlotCounter);

        // 3. Mark GlobalSeq odd, update header metadata, then commit GlobalSeq even
        ulong oddGlobal = globalSeq + 1UL;
        Volatile.Write(ref hdr->GlobalSeq, oddGlobal);
        Interlocked.MemoryBarrier();

        hdr->ActiveSlot = writeSlot;
        hdr->Width = (ushort)width;
        hdr->Height = (ushort)height;
        hdr->Stride = (uint)stride;
        hdr->TimestampQpcUs = qpcUs;
        hdr->Fnv1aHash = fnv1a;
        hdr->Flags = (ushort)(FlagActive | (clientOnly ? FlagClientOnly : 0));

        Interlocked.MemoryBarrier();
        globalSeq = oddGlobal + 1UL;
        Volatile.Write(ref hdr->GlobalSeq, globalSeq);
        currentActiveSlot = writeSlot;
    }

    /// <summary>
    /// Zero-tear Seqlock reader over a named Local\ shared-memory mapping with bounded odd-sequence timeout.
    /// </summary>
    public static bool TryReadSharedFrame(
        string mapName,
        Span<byte> destination,
        out TksmHeader header,
        out int bytesRead,
        int timeoutMs = 25)
    {
        header = default;
        bytesRead = 0;

        MemoryMappedFile mmf;
        try
        {
            mmf = MemoryMappedFile.OpenExisting(mapName, MemoryMappedFileRights.Read);
        }
        catch
        {
            return false;
        }

        using (mmf)
        using (var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
        {
            return TryReadFromAccessor(accessor, destination, out header, out bytesRead, timeoutMs);
        }
    }

    public static unsafe bool TryReadFromAccessor(
        MemoryMappedViewAccessor accessor,
        Span<byte> destination,
        out TksmHeader header,
        out int bytesRead,
        int timeoutMs = 25)
    {
        header = default;
        bytesRead = 0;

        byte* basePtr = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
        try
        {
            long capacity = accessor.Capacity;
            if (capacity < HeaderByteSize)
            {
                return false;
            }

            TksmHeader* hdr = (TksmHeader*)basePtr;
            long startTicks = Stopwatch.GetTimestamp();
            long maxTicks = (long)(Math.Max(1, timeoutMs) * (Stopwatch.Frequency / 1000.0));

            while (true)
            {
                uint magic = Volatile.Read(ref hdr->Magic);
                ushort flags = Volatile.Read(ref hdr->Flags);
                if (magic != MagicTksm || (flags & FlagClosed) != 0)
                {
                    return false;
                }

                ulong gSeq1 = Volatile.Read(ref hdr->GlobalSeq);
                if (gSeq1 == 0UL || (gSeq1 & 1UL) != 0UL)
                {
                    if ((Stopwatch.GetTimestamp() - startTicks) >= maxTicks)
                    {
                        return false;
                    }
                    Thread.SpinWait(16);
                    continue;
                }

                Interlocked.MemoryBarrier();
                byte slot = Volatile.Read(ref hdr->ActiveSlot);
                if (slot > 1)
                {
                    return false;
                }

                ulong slotSeq1 = slot == 0
                    ? Volatile.Read(ref hdr->Slot0Seq)
                    : Volatile.Read(ref hdr->Slot1Seq);

                if (slotSeq1 == 0UL || (slotSeq1 & 1UL) != 0UL)
                {
                    if ((Stopwatch.GetTimestamp() - startTicks) >= maxTicks)
                    {
                        return false;
                    }
                    Thread.SpinWait(16);
                    continue;
                }

                TksmHeader snap = *hdr;
                Interlocked.MemoryBarrier();
                ulong gSeqMid = Volatile.Read(ref hdr->GlobalSeq);
                if (gSeqMid != gSeq1)
                {
                    if ((Stopwatch.GetTimestamp() - startTicks) >= maxTicks)
                    {
                        return false;
                    }
                    continue;
                }

                long frameBytes = (long)snap.Width * snap.Height * 4L;
                long slotOffset = HeaderByteSize + ((long)slot * snap.MaxSlotBytes);
                if (frameBytes <= 0 ||
                    frameBytes > snap.MaxSlotBytes ||
                    slotOffset + frameBytes > capacity ||
                    destination.Length < frameBytes)
                {
                    return false;
                }

                fixed (byte* dstPtr = destination)
                {
                    Buffer.MemoryCopy(basePtr + slotOffset, dstPtr, destination.Length, frameBytes);
                }

                Interlocked.MemoryBarrier();
                ulong slotSeq2 = slot == 0
                    ? Volatile.Read(ref hdr->Slot0Seq)
                    : Volatile.Read(ref hdr->Slot1Seq);

                if (slotSeq1 == slotSeq2 && (slotSeq2 & 1UL) == 0UL)
                {
                    header = snap;
                    bytesRead = (int)frameBytes;
                    return true;
                }

                if ((Stopwatch.GetTimestamp() - startTicks) >= maxTicks)
                {
                    return false;
                }
            }
        }
        finally
        {
            accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    private sealed class ActiveSharedSession
    {
        private readonly AutomationService _automation;
        private readonly string _mapName;
        private readonly uint _maxSlotBytes;
        private readonly long _totalBytes;
        private readonly string? _app;
        private readonly int? _pid;
        private readonly string? _display;
        private readonly bool _clientOnly;
        private readonly string _coordSpace;
        private readonly double? _regionX;
        private readonly double? _regionY;
        private readonly double? _regionWidth;
        private readonly double? _regionHeight;
        private readonly int _maxFps;
        private readonly bool _onlyOnChange;
        private readonly int? _ownerPid;

        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _accessor;
        private nint _basePtr;
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource<bool> _firstFrameTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _publisherTask;

        private byte _activeSlot;
        private ulong _globalSeq;
        private ulong _slot0Seq;
        private ulong _slot1Seq;
        private ulong _lastHash;
        private int _lastWidth;
        private int _lastHeight;
        private ulong _framesPublished;
        private int _stopped;

        public ActiveSharedSession(
            AutomationService automation,
            string mapName,
            uint maxSlotBytes,
            string? app,
            int? pid,
            string? display,
            bool clientOnly,
            string coordSpace,
            double? regionX,
            double? regionY,
            double? regionWidth,
            double? regionHeight,
            int maxFps,
            bool onlyOnChange,
            int? ownerPid)
        {
            _automation = automation;
            _mapName = mapName;
            _maxSlotBytes = maxSlotBytes;
            _totalBytes = HeaderByteSize + (2L * maxSlotBytes);
            _app = app;
            _pid = pid;
            _display = display;
            _clientOnly = clientOnly;
            _coordSpace = coordSpace;
            _regionX = regionX;
            _regionY = regionY;
            _regionWidth = regionWidth;
            _regionHeight = regionHeight;
            _maxFps = maxFps;
            _onlyOnChange = onlyOnChange;
            _ownerPid = ownerPid;

            _mmf = MemoryMappedFile.CreateNew(mapName, _totalBytes, MemoryMappedFileAccess.ReadWrite);
            _accessor = _mmf.CreateViewAccessor(0, _totalBytes, MemoryMappedFileAccess.ReadWrite);
            InitializeHeaderUnsafe(maxSlotBytes, clientOnly);
        }

        private unsafe void InitializeHeaderUnsafe(uint maxSlotBytes, bool clientOnly)
        {
            byte* rawPtr = null;
            _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref rawPtr);
            _basePtr = (nint)rawPtr;

            TksmHeader* hdr = (TksmHeader*)rawPtr;
            *hdr = new TksmHeader
            {
                Magic = MagicTksm,
                Version = CurrentVersion,
                HeaderSize = HeaderByteSize,
                MaxSlotBytes = maxSlotBytes,
                PixelFormat = 0,
                Flags = (ushort)(FlagActive | (clientOnly ? FlagClientOnly : 0))
            };
            Interlocked.MemoryBarrier();
        }

        public bool IsRunning => Volatile.Read(ref _stopped) == 0 && (_publisherTask is { IsCompleted: false });

        public void Start()
        {
            _publisherTask = Task.Run(PublisherLoopAsync);
        }

        public async Task WaitForFirstFrameAsync(int timeoutMs)
        {
            await Task.WhenAny(_firstFrameTcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
        }

        public SharedFrameStatusResult GetStatus()
        {
            return new SharedFrameStatusResult
            {
                Ok = true,
                Active = IsRunning,
                MapName = _mapName,
                TotalBytes = _totalBytes,
                HeaderBytes = HeaderByteSize,
                MaxSlotBytes = _maxSlotBytes,
                Width = Volatile.Read(ref _lastWidth),
                Height = Volatile.Read(ref _lastHeight),
                Stride = Volatile.Read(ref _lastWidth) * 4,
                PixelFormat = "bgra32",
                MaxFps = _maxFps,
                FramesPublished = Volatile.Read(ref _framesPublished),
                CoordinateSpace = _coordSpace,
                OwnerPid = _ownerPid
            };
        }

        public async Task StopAndDisposeAsync()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
            {
                return;
            }

            try { _cts.Cancel(); } catch { }

            if (_publisherTask != null)
            {
                try { await _publisherTask.ConfigureAwait(false); } catch { }
            }

            // Publisher loop has permanently exited — safe to mark FlagClosed and release view pointer!
            MarkClosedAndReleasePointerUnsafe();

            try { _accessor.Dispose(); } catch { }
            try { _mmf.Dispose(); } catch { }
            try { _cts.Dispose(); } catch { }
            _firstFrameTcs.TrySetResult(false);
        }

        private unsafe void MarkClosedAndReleasePointerUnsafe()
        {
            if (_basePtr != 0)
            {
                TksmHeader* hdr = (TksmHeader*)_basePtr;
                ulong oddG = _globalSeq + 1UL;
                Volatile.Write(ref hdr->GlobalSeq, oddG);
                Volatile.Write(ref hdr->Flags, (ushort)(hdr->Flags | FlagClosed));
                Interlocked.MemoryBarrier();
                _globalSeq = oddG + 1UL;
                Volatile.Write(ref hdr->GlobalSeq, _globalSeq);

                _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                _basePtr = 0;
            }
        }

        private async Task PublisherLoopAsync()
        {
            double intervalMs = 1000.0 / _maxFps;
            double ticksPerMs = Stopwatch.Frequency / 1000.0;
            long epochTicks = Stopwatch.GetTimestamp();
            long lastOwnerCheckTicks = epochTicks;
            long ownerCheckIntervalTicks = (long)(250.0 * ticksPerMs);
            long tickIndex = 0;

            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    long now = Stopwatch.GetTimestamp();
                    if (_ownerPid.HasValue && (now - lastOwnerCheckTicks) >= ownerCheckIntervalTicks)
                    {
                        lastOwnerCheckTicks = now;
                        if (HasOwnerExited(_ownerPid.Value))
                        {
                            _ = Task.Run(() => StopAsync());
                            break;
                        }
                    }

                    try
                    {
                        CaptureAndPublishOnce();
                    }
                    catch
                    {
                        // Transient capture miss (e.g. target window momentarily minimized); continue loop
                    }

                    tickIndex++;
                    long targetTicks = epochTicks + (long)Math.Round(tickIndex * intervalMs * ticksPerMs);
                    long remTicks = targetTicks - Stopwatch.GetTimestamp();
                    if (remTicks > 0)
                    {
                        double remMs = remTicks / ticksPerMs;
                        if (remMs >= 3.0)
                        {
                            await Task.Delay(Math.Max(1, (int)(remMs - 1.0)), _cts.Token).ConfigureAwait(false);
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
                // Clean stop
            }
        }

        private unsafe void CaptureAndPublishOnce()
        {
            byte* basePtr = (byte*)_basePtr;
            if (basePtr == null)
            {
                return;
            }

            Bitmap? rawBmp = null;
            bool hasTargetApp = !string.IsNullOrWhiteSpace(_app) || _pid.HasValue;

            if (hasTargetApp)
            {
                var proc = _automation.ResolveApp(_app, _pid);
                IntPtr hwnd = WindowManager.GetTargetHwnd(proc);
                if (hwnd == IntPtr.Zero) hwnd = proc.MainWindowHandle;
                var winBounds = _automation.GetWindowBounds(proc, null, hwnd);
                rawBmp = ScreenCapture.CaptureWindowRawBitmap(
                    winBounds,
                    hwnd,
                    _regionX,
                    _regionY,
                    _regionWidth,
                    _regionHeight,
                    clientOnly: _clientOnly,
                    fastNonBlocking: true);
            }
            else
            {
                rawBmp = ScreenCapture.CaptureScreenRegionBitmap(
                    string.IsNullOrWhiteSpace(_display) ? "1" : _display!,
                    _regionX,
                    _regionY,
                    _regionWidth,
                    _regionHeight,
                    fastNonBlocking: true);
            }

            if (rawBmp == null)
            {
                return;
            }

            using (rawBmp)
            {
                long neededBytes = (long)rawBmp.Width * rawBmp.Height * 4L;
                if (neededBytes <= 0)
                {
                    return;
                }

                Bitmap sourceBmp = rawBmp;
                Bitmap? croppedBmp = null;
                try
                {
                    // Guard against dynamic window resize exceeding pre-allocated MaxSlotBytes ceiling
                    if (neededBytes > _maxSlotBytes)
                    {
                        int safeH = Math.Max(1, (int)(_maxSlotBytes / ((long)rawBmp.Width * 4L)));
                        int safeW = rawBmp.Width;
                        if ((long)safeW * 4L > _maxSlotBytes)
                        {
                            safeW = Math.Max(1, (int)(_maxSlotBytes / 4u));
                            safeH = 1;
                        }
                        croppedBmp = rawBmp.Clone(new Rectangle(0, 0, safeW, safeH), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                        sourceBmp = croppedBmp;
                    }

                    byte writeSlot = (byte)(1 - _activeSlot);
                    TksmHeader* hdr = (TksmHeader*)basePtr;
                    ref ulong localSlotCounter = ref (writeSlot == 0 ? ref _slot0Seq : ref _slot1Seq);
                    ref ulong headerSlotSeq = ref (writeSlot == 0 ? ref hdr->Slot0Seq : ref hdr->Slot1Seq);

                    ulong oddSlot = localSlotCounter + 1UL;
                    Volatile.Write(ref headerSlotSeq, oddSlot);
                    Interlocked.MemoryBarrier();

                    var slotSpan = new Span<byte>(basePtr + HeaderByteSize + ((long)writeSlot * _maxSlotBytes), (int)_maxSlotBytes);
                    var (hash, _) = ScreenCapture.CopyBitmapBgra32AndHash(sourceBmp, slotSpan, out int w, out int h, out int stride);

                    Interlocked.MemoryBarrier();
                    localSlotCounter = oddSlot + 1UL;
                    Volatile.Write(ref headerSlotSeq, localSlotCounter);

                    if (_onlyOnChange &&
                        Volatile.Read(ref _framesPublished) > 0 &&
                        hash == _lastHash &&
                        w == _lastWidth &&
                        h == _lastHeight)
                    {
                        return;
                    }

                    ulong qpcUs = (ulong)((double)Stopwatch.GetTimestamp() * 1_000_000.0 / Stopwatch.Frequency);
                    ulong oddGlobal = _globalSeq + 1UL;
                    Volatile.Write(ref hdr->GlobalSeq, oddGlobal);
                    Interlocked.MemoryBarrier();

                    hdr->ActiveSlot = writeSlot;
                    hdr->Width = (ushort)w;
                    hdr->Height = (ushort)h;
                    hdr->Stride = (uint)stride;
                    hdr->TimestampQpcUs = qpcUs;
                    hdr->Fnv1aHash = hash;
                    hdr->Flags = (ushort)(FlagActive | (_clientOnly ? FlagClientOnly : 0));

                    Interlocked.MemoryBarrier();
                    _globalSeq = oddGlobal + 1UL;
                    Volatile.Write(ref hdr->GlobalSeq, _globalSeq);

                    _activeSlot = writeSlot;
                    _lastHash = hash;
                    Volatile.Write(ref _lastWidth, w);
                    Volatile.Write(ref _lastHeight, h);
                    Interlocked.Increment(ref _framesPublished);
                    _firstFrameTcs.TrySetResult(true);
                }
                finally
                {
                    croppedBmp?.Dispose();
                }
            }
        }

        private static bool HasOwnerExited(int ownerPid)
        {
            try
            {
                using var p = Process.GetProcessById(ownerPid);
                return p.HasExited;
            }
            catch
            {
                return true;
            }
        }
    }
}
