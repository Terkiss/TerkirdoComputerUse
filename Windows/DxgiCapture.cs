using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TerkirdoComputer.Windows;

/// <summary>
/// Zero-dependency pure C# COM VTable DirectX 11 + DXGI 1.2 Multi-Adapter Desktop Duplication engine.
/// Supports multi-GPU topologies (e.g., dual discrete GPUs + virtual display adapters), per-output session caching,
/// static desktop frame reuse, DWM alpha normalization, automatic session recovery, and per-monitor GDI fallback.
/// </summary>
public static unsafe class DxgiCapture
{
    private const int S_OK = 0;
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);
    private const int DXGI_ERROR_INVALID_CALL = unchecked((int)0x887A0001);
    private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
    private const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
    private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);

    private const int D3D_DRIVER_TYPE_UNKNOWN = 0;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const uint D3D11_SDK_VERSION = 7;

    private const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    private const int DXGI_MODE_ROTATION_IDENTITY = 1;

    private const uint D3D11_USAGE_STAGING = 3;
    private const uint D3D11_CPU_ACCESS_READ = 0x20000;
    private const uint D3D11_MAP_READ = 1;

    private const uint InitialFrameTimeoutMs = 150;
    private const uint SubsequentFrameTimeoutMs = 35;

    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IID_IDXGIOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, OutputDuplicationSession> Sessions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTime> UnsupportedCooldownUntilUtc = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan UnsupportedRetryCooldown = TimeSpan.FromSeconds(60);
    private static DateTime _accessDeniedCooldownUntilUtc = DateTime.MinValue;
    private static string _lastTopologySignature = string.Empty;

    static DxgiCapture()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DisposeAllSessions();
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr ppFactory);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter,
        int DriverType,
        IntPtr Software,
        uint Flags,
        uint* pFeatureLevels,
        uint FeatureLevels,
        uint SDKVersion,
        out IntPtr ppDevice,
        out uint pFeatureLevel,
        out IntPtr ppImmediateContext);

    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DXGI_ADAPTER_DESC1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public LUID AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DXGI_OUTPUT_DESC
    {
        public fixed char DeviceName[32];
        public NativeMethods.RECT DesktopCoordinates;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DXGI_SAMPLE_DESC
    {
        public uint Count;
        public uint Quality;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DXGI_MODE_DESC
    {
        public uint Width;
        public uint Height;
        public uint RefreshRateNumerator;
        public uint RefreshRateDenominator;
        public uint Format;
        public int ScanlineOrdering;
        public int Scaling;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DXGI_OUTDUPL_DESC
    {
        public DXGI_MODE_DESC ModeDesc;
        public int Rotation;
        public int DesktopImageInSystemMemory;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DXGI_OUTDUPL_POINTER_POSITION
    {
        public NativeMethods.POINT Position;
        public int Visible;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DXGI_OUTDUPL_FRAME_INFO
    {
        public long LastPresentTime;
        public long LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectsCoalesced;
        public int ProtectedContentMaskedOut;
        public DXGI_OUTDUPL_POINTER_POSITION PointerPosition;
        public uint TotalMetadataBufferSize;
        public uint PointerShapeBufferSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_TEXTURE2D_DESC
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public DXGI_SAMPLE_DESC SampleDesc;
        public uint Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_MAPPED_SUBRESOURCE
    {
        public IntPtr pData;
        public uint RowPitch;
        public uint DepthPitch;
    }

    private readonly record struct MonitorTarget(
        string DeviceName,
        IntPtr MonitorHandle,
        NativeMethods.RECT Bounds);

    private sealed class OutputDuplicationSession : IDisposable
    {
        public required string Key { get; init; }
        public required IntPtr MonitorHandle { get; init; }
        public required NativeMethods.RECT Bounds { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public IntPtr Device;
        public IntPtr Context;
        public IntPtr Duplication;
        public IntPtr StagingTexture;
        public byte[]? LastBgraFrame;

        public bool Matches(MonitorTarget target) =>
            MonitorHandle == target.MonitorHandle &&
            Bounds.Left == target.Bounds.Left &&
            Bounds.Top == target.Bounds.Top &&
            Bounds.Right == target.Bounds.Right &&
            Bounds.Bottom == target.Bounds.Bottom;

        public void Dispose()
        {
            ComRelease(ref Duplication);
            ComRelease(ref StagingTexture);
            ComRelease(ref Context);
            ComRelease(ref Device);
            LastBgraFrame = null;
        }
    }

    public static bool IsDisabledByEnvironment()
    {
        string? raw = Environment.GetEnvironmentVariable("OPEN_COMPUTER_USE_DISABLE_DXGI");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return raw.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";
    }

    /// <summary>
    /// Checks whether DXGI Desktop Duplication is enabled and supported on at least one attached desktop output.
    /// </summary>
    public static bool IsSupported()
    {
        if (IsDisabledByEnvironment())
        {
            return false;
        }

        try
        {
            var monitors = EnumerateDesktopMonitors();
            if (monitors.Count == 0)
            {
                return false;
            }

            lock (SyncRoot)
            {
                RefreshTopologyStateLocked(monitors);
                if (DateTime.UtcNow < _accessDeniedCooldownUntilUtc)
                {
                    return false;
                }

                foreach (var monitor in monitors)
                {
                    var session = GetOrCreateSessionLocked(monitor, ignoreCooldown: true);
                    if (session != null)
                    {
                        return true;
                    }
                    if (DateTime.UtcNow < _accessDeniedCooldownUntilUtc)
                    {
                        return false;
                    }
                }
            }
        }
        catch
        {
            // Fall through to false
        }

        return false;
    }

    /// <summary>
    /// Captures an arbitrary virtual-screen rectangle (single monitor, multi-monitor virtual desktop, or window bounds)
    /// using DXGI Desktop Duplication for all supported GPU outputs and per-subrect GDI fallback for virtual adapters.
    /// Returns true when at least one intersecting monitor sub-region was captured via DXGI.
    /// </summary>
    public static bool TryCaptureRegion(
        int x,
        int y,
        int width,
        int height,
        out Bitmap? bitmap,
        uint? subsequentTimeoutOverrideMs = null)
    {
        bitmap = null;
        if (width <= 0 || height <= 0 || IsDisabledByEnvironment())
        {
            return false;
        }

        var monitors = EnumerateDesktopMonitors();
        if (monitors.Count == 0)
        {
            return false;
        }

        lock (SyncRoot)
        {
            RefreshTopologyStateLocked(monitors);
            if (DateTime.UtcNow < _accessDeniedCooldownUntilUtc)
            {
                return false;
            }
        }

        int reqLeft = x;
        int reqTop = y;
        int reqRight = x + width;
        int reqBottom = y + height;

        var intersecting = new List<(MonitorTarget Monitor, NativeMethods.RECT Intersection)>();
        foreach (var mon in monitors)
        {
            int il = Math.Max(reqLeft, mon.Bounds.Left);
            int it = Math.Max(reqTop, mon.Bounds.Top);
            int ir = Math.Min(reqRight, mon.Bounds.Right);
            int ib = Math.Min(reqBottom, mon.Bounds.Bottom);
            if (ir > il && ib > it)
            {
                intersecting.Add((mon, new NativeMethods.RECT
                {
                    Left = il,
                    Top = it,
                    Right = ir,
                    Bottom = ib
                }));
            }
        }

        if (intersecting.Count == 0)
        {
            return false;
        }

        var resultBitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        bool coversFullTarget = intersecting.Count == 1 &&
                                (intersecting[0].Intersection.Right - intersecting[0].Intersection.Left) == width &&
                                (intersecting[0].Intersection.Bottom - intersecting[0].Intersection.Top) == height;
        if (!coversFullTarget)
        {
            using var clearGraphics = Graphics.FromImage(resultBitmap);
            clearGraphics.Clear(Color.Black);
        }

        bool anyDxgiCaptured = false;
        var gdiFallbackRects = new List<NativeMethods.RECT>();

        try
        {
            lock (SyncRoot)
            {
                foreach (var (monitor, inter) in intersecting)
                {
                    if (TryCaptureMonitorFrameLocked(
                            monitor,
                            out byte[]? bgraFrame,
                            out int monWidth,
                            out int monHeight,
                            subsequentTimeoutOverrideMs) &&
                        bgraFrame != null)
                    {
                        BlitBgraSubregion(
                            bgraFrame,
                            monWidth,
                            monHeight,
                            srcX: inter.Left - monitor.Bounds.Left,
                            srcY: inter.Top - monitor.Bounds.Top,
                            resultBitmap,
                            dstX: inter.Left - reqLeft,
                            dstY: inter.Top - reqTop,
                            copyWidth: inter.Right - inter.Left,
                            copyHeight: inter.Bottom - inter.Top);
                        anyDxgiCaptured = true;
                    }
                    else
                    {
                        gdiFallbackRects.Add(inter);
                    }
                }
            }

            if (!anyDxgiCaptured)
            {
                resultBitmap.Dispose();
                return false;
            }

            // Fill any virtual display adapter (e.g. Parsec) or cold-timeout sub-rectangles via GDI CopyFromScreen
            if (gdiFallbackRects.Count > 0)
            {
                using var graphics = Graphics.FromImage(resultBitmap);
                foreach (var inter in gdiFallbackRects)
                {
                    int copyW = inter.Right - inter.Left;
                    int copyH = inter.Bottom - inter.Top;
                    int dstX = inter.Left - reqLeft;
                    int dstY = inter.Top - reqTop;
                    graphics.CopyFromScreen(inter.Left, inter.Top, dstX, dstY, new Size(copyW, copyH), CopyPixelOperation.SourceCopy);
                }
            }

            bitmap = resultBitmap;
            return true;
        }
        catch
        {
            resultBitmap.Dispose();
            bitmap = null;
            return false;
        }
    }

    public static void DisposeAllSessions()
    {
        lock (SyncRoot)
        {
            foreach (var session in Sessions.Values)
            {
                session.Dispose();
            }
            Sessions.Clear();
            UnsupportedCooldownUntilUtc.Clear();
            _accessDeniedCooldownUntilUtc = DateTime.MinValue;
            _lastTopologySignature = string.Empty;
        }
    }

    private static void RefreshTopologyStateLocked(List<MonitorTarget> monitors)
    {
        var sb = new System.Text.StringBuilder(monitors.Count * 48);
        sb.Append(monitors.Count);
        foreach (var mon in monitors)
        {
            sb.Append('|')
              .Append(mon.DeviceName)
              .Append(':')
              .Append(mon.Bounds.Left).Append(',')
              .Append(mon.Bounds.Top).Append(',')
              .Append(mon.Bounds.Right).Append(',')
              .Append(mon.Bounds.Bottom);
        }

        string signature = sb.ToString();
        if (!string.Equals(_lastTopologySignature, signature, StringComparison.Ordinal))
        {
            _lastTopologySignature = signature;
            UnsupportedCooldownUntilUtc.Clear();
            _accessDeniedCooldownUntilUtc = DateTime.MinValue;
        }
    }

    private static bool TryCaptureMonitorFrameLocked(
        MonitorTarget monitor,
        out byte[]? bgraFrame,
        out int monWidth,
        out int monHeight,
        uint? subsequentTimeoutOverrideMs = null)
    {
        bgraFrame = null;
        monWidth = monitor.Bounds.Right - monitor.Bounds.Left;
        monHeight = monitor.Bounds.Bottom - monitor.Bounds.Top;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            var session = GetOrCreateSessionLocked(monitor, ignoreCooldown: attempt > 0);
            if (session == null)
            {
                return false;
            }

            var status = AcquireOrReuseFrameLocked(session, subsequentTimeoutOverrideMs);
            if (status == FrameCaptureStatus.Success && session.LastBgraFrame != null)
            {
                bgraFrame = session.LastBgraFrame;
                monWidth = session.Width;
                monHeight = session.Height;
                return true;
            }

            if (status == FrameCaptureStatus.SessionLost)
            {
                RemoveAndDisposeSessionLocked(monitor.DeviceName);
                continue;
            }

            // ColdWaitTimeout or UnsupportedFormat: keep session alive for next frame if valid, fall back to GDI now
            return false;
        }

        return false;
    }

    private enum FrameCaptureStatus
    {
        Success,
        ColdWaitTimeout,
        SessionLost,
        Failed
    }

    private static FrameCaptureStatus AcquireOrReuseFrameLocked(
        OutputDuplicationSession session,
        uint? subsequentTimeoutOverrideMs = null)
    {
        bool isCold = session.LastBgraFrame == null;
        uint timeoutMs = isCold
            ? InitialFrameTimeoutMs
            : (subsequentTimeoutOverrideMs ?? SubsequentFrameTimeoutMs);

        // Allow up to 2 iterations in case the first returned frame is a cursor-only metadata update (LastPresentTime == 0)
        for (int pass = 0; pass < 2; pass++)
        {
            uint effectiveTimeout = pass == 0 ? timeoutMs : (subsequentTimeoutOverrideMs ?? 15u);
            int hr = DuplicationAcquireNextFrame(session.Duplication, effectiveTimeout, out var frameInfo, out IntPtr desktopResource);

            if (hr == DXGI_ERROR_WAIT_TIMEOUT)
            {
                return session.LastBgraFrame != null
                    ? FrameCaptureStatus.Success
                    : FrameCaptureStatus.ColdWaitTimeout;
            }

            if (hr == DXGI_ERROR_ACCESS_LOST || hr == DXGI_ERROR_INVALID_CALL)
            {
                ComRelease(ref desktopResource);
                return FrameCaptureStatus.SessionLost;
            }

            if (hr < 0)
            {
                ComRelease(ref desktopResource);
                return FrameCaptureStatus.Failed;
            }

            // hr == S_OK: frame lock is held; MUST call ReleaseFrame before returning or acquiring again
            bool copied = false;
            bool cursorOnly = frameInfo.LastPresentTime == 0;
            bool mismatchOrInvalid = false;

            try
            {
                if (desktopResource != IntPtr.Zero && (!cursorOnly || isCold))
                {
                    int qiHr = ComQueryInterface(desktopResource, in IID_ID3D11Texture2D, out IntPtr gpuTexture);
                    if (qiHr >= 0 && gpuTexture != IntPtr.Zero)
                    {
                        try
                        {
                            Texture2DGetDesc(gpuTexture, out var texDesc);
                            if (texDesc.Width != (uint)session.Width || texDesc.Height != (uint)session.Height)
                            {
                                mismatchOrInvalid = true;
                            }
                            else if (texDesc.Format == DXGI_FORMAT_B8G8R8A8_UNORM)
                            {
                                ContextCopyResource(session.Context, session.StagingTexture, gpuTexture);
                                copied = true;
                            }
                            else
                            {
                                return FrameCaptureStatus.Failed;
                            }
                        }
                        finally
                        {
                            ComRelease(ref gpuTexture);
                        }
                    }
                }
            }
            finally
            {
                ComRelease(ref desktopResource);
                _ = DuplicationReleaseFrame(session.Duplication);
            }

            if (mismatchOrInvalid)
            {
                return FrameCaptureStatus.SessionLost;
            }

            if (copied)
            {
                if (!ReadStagingTextureIntoBufferLocked(session, requireNonZeroWhenColdCursorOnly: isCold && cursorOnly))
                {
                    if (isCold && cursorOnly && pass == 0)
                    {
                        continue;
                    }
                    return session.LastBgraFrame != null ? FrameCaptureStatus.Success : FrameCaptureStatus.ColdWaitTimeout;
                }
                return FrameCaptureStatus.Success;
            }

            if (cursorOnly && pass == 0)
            {
                continue;
            }

            return session.LastBgraFrame != null
                ? FrameCaptureStatus.Success
                : FrameCaptureStatus.ColdWaitTimeout;
        }

        return session.LastBgraFrame != null
            ? FrameCaptureStatus.Success
            : FrameCaptureStatus.ColdWaitTimeout;
    }

    private static bool ReadStagingTextureIntoBufferLocked(OutputDuplicationSession session, bool requireNonZeroWhenColdCursorOnly)
    {
        int mapHr = ContextMap(session.Context, session.StagingTexture, 0, D3D11_MAP_READ, 0, out var mapped);
        if (mapHr < 0 || mapped.pData == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            int width = session.Width;
            int height = session.Height;
            int dstStride = width * 4;
            if (mapped.RowPitch < (uint)dstStride)
            {
                return false;
            }
            int requiredLength = dstStride * height;

            if (requireNonZeroWhenColdCursorOnly)
            {
                bool hasAnyColor = false;
                byte* probeBase = (byte*)mapped.pData;
                int stepY = Math.Max(1, height / 16);
                int stepX = Math.Max(1, width / 16);
                for (int py = 0; py < height && !hasAnyColor; py += stepY)
                {
                    byte* row = probeBase + (nuint)py * mapped.RowPitch;
                    for (int px = 0; px < width; px += stepX)
                    {
                        int off = px * 4;
                        if (row[off] != 0 || row[off + 1] != 0 || row[off + 2] != 0)
                        {
                            hasAnyColor = true;
                            break;
                        }
                    }
                }

                if (!hasAnyColor)
                {
                    return false;
                }
            }

            byte[] buffer = session.LastBgraFrame is { Length: var len } && len == requiredLength
                ? session.LastBgraFrame
                : new byte[requiredLength];

            byte* srcBase = (byte*)mapped.pData;
            fixed (byte* dstBase = buffer)
            {
                int pairCount = width / 2;
                bool hasTrailingPixel = (width & 1) != 0;

                for (int row = 0; row < height; row++)
                {
                    byte* srcRow = srcBase + (nuint)row * mapped.RowPitch;
                    byte* dstRow = dstBase + row * dstStride;
                    Buffer.MemoryCopy(srcRow, dstRow, dstStride, dstStride);

                    // Force alpha byte [x*4 + 3] = 0xFF (2 pixels per 64-bit word on little-endian x64)
                    ulong* pairPtr = (ulong*)dstRow;
                    for (int p = 0; p < pairCount; p++)
                    {
                        pairPtr[p] |= 0xFF000000_FF000000UL;
                    }

                    if (hasTrailingPixel)
                    {
                        ((uint*)dstRow)[width - 1] |= 0xFF000000u;
                    }
                }
            }

            session.LastBgraFrame = buffer;
            return true;
        }
        finally
        {
            ContextUnmap(session.Context, session.StagingTexture, 0);
        }
    }

    private static void BlitBgraSubregion(
        byte[] srcBgra,
        int srcTotalWidth,
        int srcTotalHeight,
        int srcX,
        int srcY,
        Bitmap dstBitmap,
        int dstX,
        int dstY,
        int copyWidth,
        int copyHeight)
    {
        if (copyWidth <= 0 || copyHeight <= 0)
        {
            return;
        }

        if (srcX < 0 || srcY < 0 || srcX + copyWidth > srcTotalWidth || srcY + copyHeight > srcTotalHeight)
        {
            return;
        }

        long srcStrideLong = (long)srcTotalWidth * 4L;
        long rowBytesLong = (long)copyWidth * 4L;
        long lastRowEndOffset = ((long)(srcY + copyHeight - 1) * srcStrideLong) + ((long)srcX * 4L) + rowBytesLong;
        if (srcStrideLong <= 0 || rowBytesLong <= 0 || lastRowEndOffset > srcBgra.LongLength)
        {
            return;
        }

        var lockRect = new Rectangle(dstX, dstY, copyWidth, copyHeight);
        var bmpData = dstBitmap.LockBits(lockRect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            fixed (byte* srcBase = srcBgra)
            {
                byte* dstBase = (byte*)bmpData.Scan0;
                for (int row = 0; row < copyHeight; row++)
                {
                    byte* srcRow = srcBase + ((long)(srcY + row) * srcStrideLong) + ((long)srcX * 4L);
                    byte* dstRow = dstBase + ((long)row * bmpData.Stride);
                    Buffer.MemoryCopy(srcRow, dstRow, rowBytesLong, rowBytesLong);
                }
            }
        }
        finally
        {
            dstBitmap.UnlockBits(bmpData);
        }
    }

    private static OutputDuplicationSession? GetOrCreateSessionLocked(MonitorTarget monitor, bool ignoreCooldown)
    {
        if (Sessions.TryGetValue(monitor.DeviceName, out var existing))
        {
            if (existing.Matches(monitor))
            {
                return existing;
            }
            RemoveAndDisposeSessionLocked(monitor.DeviceName);
        }

        if (DateTime.UtcNow < _accessDeniedCooldownUntilUtc)
        {
            return null;
        }

        if (!ignoreCooldown &&
            UnsupportedCooldownUntilUtc.TryGetValue(monitor.DeviceName, out var cooldownUntil) &&
            DateTime.UtcNow < cooldownUntil)
        {
            return null;
        }

        var created = CreateSessionForMonitor(monitor, out bool accessDenied);
        if (created != null)
        {
            UnsupportedCooldownUntilUtc.Remove(monitor.DeviceName);
            Sessions[monitor.DeviceName] = created;
            return created;
        }

        DateTime expiry = DateTime.UtcNow.Add(UnsupportedRetryCooldown);
        UnsupportedCooldownUntilUtc[monitor.DeviceName] = expiry;
        if (accessDenied)
        {
            _accessDeniedCooldownUntilUtc = expiry;
        }
        return null;
    }

    private static void RemoveAndDisposeSessionLocked(string key)
    {
        if (Sessions.Remove(key, out var existing))
        {
            existing.Dispose();
        }
    }

    private static OutputDuplicationSession? CreateSessionForMonitor(MonitorTarget target, out bool accessDenied)
    {
        accessDenied = false;
        int hrFactory = CreateDXGIFactory1(in IID_IDXGIFactory1, out IntPtr factory);
        if (hrFactory < 0 || factory == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                int hrAdapter = FactoryEnumAdapters1(factory, adapterIndex, out IntPtr adapter);
                if (hrAdapter == DXGI_ERROR_NOT_FOUND || hrAdapter < 0 || adapter == IntPtr.Zero)
                {
                    break;
                }

                try
                {
                    if (AdapterGetDesc1(adapter, out var adapterDesc) >= 0 && (adapterDesc.Flags & 2u) != 0)
                    {
                        // Skip DXGI_ADAPTER_FLAG_SOFTWARE (Microsoft Basic Render Driver WARP adapter)
                        continue;
                    }

                    for (uint outputIndex = 0; ; outputIndex++)
                    {
                        int hrOutput = AdapterEnumOutputs(adapter, outputIndex, out IntPtr output);
                        if (hrOutput == DXGI_ERROR_NOT_FOUND || hrOutput < 0 || output == IntPtr.Zero)
                        {
                            break;
                        }

                        try
                        {
                            if (OutputGetDesc(output, out var outDesc) < 0)
                            {
                                continue;
                            }

                            if (outDesc.AttachedToDesktop == 0 || outDesc.Rotation != DXGI_MODE_ROTATION_IDENTITY)
                            {
                                continue;
                            }

                            string outputName = ReadFixedChar32(ref outDesc);
                            bool sameMonitor =
                                (target.MonitorHandle != IntPtr.Zero && outDesc.Monitor == target.MonitorHandle) ||
                                string.Equals(outputName, target.DeviceName, StringComparison.OrdinalIgnoreCase) ||
                                (outDesc.DesktopCoordinates.Left == target.Bounds.Left &&
                                 outDesc.DesktopCoordinates.Top == target.Bounds.Top &&
                                 outDesc.DesktopCoordinates.Right == target.Bounds.Right &&
                                 outDesc.DesktopCoordinates.Bottom == target.Bounds.Bottom);

                            if (!sameMonitor)
                            {
                                continue;
                            }

                            int outWidth = outDesc.DesktopCoordinates.Right - outDesc.DesktopCoordinates.Left;
                            int outHeight = outDesc.DesktopCoordinates.Bottom - outDesc.DesktopCoordinates.Top;
                            if (outWidth <= 0 || outHeight <= 0)
                            {
                                continue;
                            }

                            var session = TryCreateDuplicationOnAdapterOutput(
                                adapter,
                                output,
                                target.DeviceName,
                                outDesc.Monitor,
                                outDesc.DesktopCoordinates,
                                outWidth,
                                outHeight,
                                out bool outputAccessDenied);

                            if (session != null)
                            {
                                return session;
                            }

                            if (outputAccessDenied)
                            {
                                accessDenied = true;
                                return null;
                            }
                        }
                        finally
                        {
                            ComRelease(ref output);
                        }
                    }
                }
                finally
                {
                    ComRelease(ref adapter);
                }
            }
        }
        finally
        {
            ComRelease(ref factory);
        }

        return null;
    }

    private static OutputDuplicationSession? TryCreateDuplicationOnAdapterOutput(
        IntPtr adapter,
        IntPtr output,
        string key,
        IntPtr monitorHandle,
        NativeMethods.RECT bounds,
        int width,
        int height,
        out bool accessDenied)
    {
        accessDenied = false;
        IntPtr device = IntPtr.Zero;
        IntPtr context = IntPtr.Zero;
        IntPtr output1 = IntPtr.Zero;
        IntPtr duplication = IntPtr.Zero;
        IntPtr stagingTexture = IntPtr.Zero;

        try
        {
            // When pAdapter is non-null, DriverType MUST be D3D_DRIVER_TYPE_UNKNOWN (0).
            uint* featureLevels = stackalloc uint[3] { 0xB000u, 0xA100u, 0xA000u };
            int hrDevice = D3D11CreateDevice(
                adapter,
                D3D_DRIVER_TYPE_UNKNOWN,
                IntPtr.Zero,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                featureLevels,
                3u,
                D3D11_SDK_VERSION,
                out device,
                out _,
                out context);

            if (hrDevice < 0 || device == IntPtr.Zero || context == IntPtr.Zero)
            {
                return null;
            }

            int hrQi = ComQueryInterface(output, in IID_IDXGIOutput1, out output1);
            if (hrQi < 0 || output1 == IntPtr.Zero)
            {
                return null;
            }

            int hrDup = Output1DuplicateOutput(output1, device, out duplication);
            if (hrDup < 0 || duplication == IntPtr.Zero)
            {
                if (hrDup == E_ACCESSDENIED)
                {
                    accessDenied = true;
                }
                return null;
            }

            DuplicationGetDesc(duplication, out var duplDesc);
            if (duplDesc.ModeDesc.Format != 0 && duplDesc.ModeDesc.Format != DXGI_FORMAT_B8G8R8A8_UNORM)
            {
                return null;
            }

            var stagingDesc = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                Usage = D3D11_USAGE_STAGING,
                BindFlags = 0,
                CPUAccessFlags = D3D11_CPU_ACCESS_READ,
                MiscFlags = 0
            };

            int hrTex = DeviceCreateTexture2D(device, in stagingDesc, IntPtr.Zero, out stagingTexture);
            if (hrTex < 0 || stagingTexture == IntPtr.Zero)
            {
                return null;
            }

            var session = new OutputDuplicationSession
            {
                Key = key,
                MonitorHandle = monitorHandle,
                Bounds = bounds,
                Width = width,
                Height = height,
                Device = device,
                Context = context,
                Duplication = duplication,
                StagingTexture = stagingTexture
            };

            // Ownership transferred to session
            device = IntPtr.Zero;
            context = IntPtr.Zero;
            duplication = IntPtr.Zero;
            stagingTexture = IntPtr.Zero;
            return session;
        }
        finally
        {
            ComRelease(ref output1);
            ComRelease(ref duplication);
            ComRelease(ref stagingTexture);
            ComRelease(ref context);
            ComRelease(ref device);
        }
    }

    private static List<MonitorTarget> EnumerateDesktopMonitors()
    {
        var list = new List<MonitorTarget>();
        int idx = 0;

        NativeMethods.MonitorEnumProc proc = (IntPtr hMonitor, IntPtr hdcMonitor, ref NativeMethods.RECT lprcMonitor, IntPtr dwData) =>
        {
            var mi = new NativeMethods.MONITORINFOEX
            {
                cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>()
            };
            if (NativeMethods.GetMonitorInfo(hMonitor, ref mi))
            {
                idx++;
                int w = mi.rcMonitor.Right - mi.rcMonitor.Left;
                int h = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
                if (w > 0 && h > 0)
                {
                    string name = string.IsNullOrWhiteSpace(mi.szDevice) ? $"DISPLAY{idx}" : mi.szDevice.Trim();
                    list.Add(new MonitorTarget(name, hMonitor, mi.rcMonitor));
                }
            }
            return true;
        };

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        return list;
    }

    private static string ReadFixedChar32(ref DXGI_OUTPUT_DESC desc)
    {
        fixed (char* ptr = desc.DeviceName)
        {
            int len = 0;
            while (len < 32 && ptr[len] != '\0')
            {
                len++;
            }
            return new string(ptr, 0, len);
        }
    }

    // =========================================================================
    // Exact x64 COM VTable Unmanaged Function-Pointer Wrappers
    // =========================================================================

    private static int ComQueryInterface(IntPtr thisPtr, in Guid riid, out IntPtr ppvObject)
    {
        ppvObject = IntPtr.Zero;
        if (thisPtr == IntPtr.Zero) return -1;

        void** vtbl = *(void***)thisPtr;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtbl[0];
        fixed (Guid* pRiid = &riid)
        fixed (IntPtr* ppv = &ppvObject)
        {
            return fn(thisPtr, pRiid, ppv);
        }
    }

    private static void ComRelease(ref IntPtr comPtr)
    {
        IntPtr ptr = comPtr;
        if (ptr == IntPtr.Zero)
        {
            return;
        }

        comPtr = IntPtr.Zero;
        void** vtbl = *(void***)ptr;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtbl[2];
        _ = fn(ptr);
    }

    // IDXGIFactory1::EnumAdapters1 -> VTable[12]
    private static int FactoryEnumAdapters1(IntPtr factory, uint adapterIndex, out IntPtr ppAdapter)
    {
        ppAdapter = IntPtr.Zero;
        void** vtbl = *(void***)factory;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)vtbl[12];
        fixed (IntPtr* pOut = &ppAdapter)
        {
            return fn(factory, adapterIndex, pOut);
        }
    }

    // IDXGIAdapter::EnumOutputs -> VTable[7]
    private static int AdapterEnumOutputs(IntPtr adapter, uint outputIndex, out IntPtr ppOutput)
    {
        ppOutput = IntPtr.Zero;
        void** vtbl = *(void***)adapter;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)vtbl[7];
        fixed (IntPtr* pOut = &ppOutput)
        {
            return fn(adapter, outputIndex, pOut);
        }
    }

    // IDXGIAdapter1::GetDesc1 -> VTable[10]
    private static int AdapterGetDesc1(IntPtr adapter, out DXGI_ADAPTER_DESC1 desc)
    {
        desc = default;
        void** vtbl = *(void***)adapter;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, DXGI_ADAPTER_DESC1*, int>)vtbl[10];
        fixed (DXGI_ADAPTER_DESC1* pDesc = &desc)
        {
            return fn(adapter, pDesc);
        }
    }

    // IDXGIOutput::GetDesc -> VTable[7]
    private static int OutputGetDesc(IntPtr output, out DXGI_OUTPUT_DESC desc)
    {
        desc = default;
        void** vtbl = *(void***)output;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, DXGI_OUTPUT_DESC*, int>)vtbl[7];
        fixed (DXGI_OUTPUT_DESC* pDesc = &desc)
        {
            return fn(output, pDesc);
        }
    }

    // IDXGIOutput1::DuplicateOutput -> VTable[22]
    private static int Output1DuplicateOutput(IntPtr output1, IntPtr device, out IntPtr ppOutputDuplication)
    {
        ppOutputDuplication = IntPtr.Zero;
        void** vtbl = *(void***)output1;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)vtbl[22];
        fixed (IntPtr* pOut = &ppOutputDuplication)
        {
            return fn(output1, device, pOut);
        }
    }

    // IDXGIOutputDuplication::GetDesc -> VTable[7] (void return)
    private static void DuplicationGetDesc(IntPtr duplication, out DXGI_OUTDUPL_DESC desc)
    {
        desc = default;
        void** vtbl = *(void***)duplication;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, DXGI_OUTDUPL_DESC*, void>)vtbl[7];
        fixed (DXGI_OUTDUPL_DESC* pDesc = &desc)
        {
            fn(duplication, pDesc);
        }
    }

    // IDXGIOutputDuplication::AcquireNextFrame -> VTable[8]
    private static int DuplicationAcquireNextFrame(
        IntPtr duplication,
        uint timeoutMs,
        out DXGI_OUTDUPL_FRAME_INFO frameInfo,
        out IntPtr ppDesktopResource)
    {
        frameInfo = default;
        ppDesktopResource = IntPtr.Zero;
        void** vtbl = *(void***)duplication;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, DXGI_OUTDUPL_FRAME_INFO*, IntPtr*, int>)vtbl[8];
        fixed (DXGI_OUTDUPL_FRAME_INFO* pInfo = &frameInfo)
        fixed (IntPtr* pRes = &ppDesktopResource)
        {
            return fn(duplication, timeoutMs, pInfo, pRes);
        }
    }

    // IDXGIOutputDuplication::ReleaseFrame -> VTable[14]
    private static int DuplicationReleaseFrame(IntPtr duplication)
    {
        void** vtbl = *(void***)duplication;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)vtbl[14];
        return fn(duplication);
    }

    // ID3D11Device::CreateTexture2D -> VTable[5]
    private static int DeviceCreateTexture2D(
        IntPtr device,
        in D3D11_TEXTURE2D_DESC desc,
        IntPtr initialData,
        out IntPtr ppTexture2D)
    {
        ppTexture2D = IntPtr.Zero;
        void** vtbl = *(void***)device;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, IntPtr, IntPtr*, int>)vtbl[5];
        fixed (D3D11_TEXTURE2D_DESC* pDesc = &desc)
        fixed (IntPtr* pOut = &ppTexture2D)
        {
            return fn(device, pDesc, initialData, pOut);
        }
    }

    // ID3D11Texture2D::GetDesc -> VTable[10] (void return)
    private static void Texture2DGetDesc(IntPtr texture2D, out D3D11_TEXTURE2D_DESC desc)
    {
        desc = default;
        void** vtbl = *(void***)texture2D;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, void>)vtbl[10];
        fixed (D3D11_TEXTURE2D_DESC* pDesc = &desc)
        {
            fn(texture2D, pDesc);
        }
    }

    // ID3D11DeviceContext::Map -> VTable[14]
    private static int ContextMap(
        IntPtr context,
        IntPtr resource,
        uint subresource,
        uint mapType,
        uint mapFlags,
        out D3D11_MAPPED_SUBRESOURCE mapped)
    {
        mapped = default;
        void** vtbl = *(void***)context;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, D3D11_MAPPED_SUBRESOURCE*, int>)vtbl[14];
        fixed (D3D11_MAPPED_SUBRESOURCE* pMapped = &mapped)
        {
            return fn(context, resource, subresource, mapType, mapFlags, pMapped);
        }
    }

    // ID3D11DeviceContext::Unmap -> VTable[15] (void return)
    private static void ContextUnmap(IntPtr context, IntPtr resource, uint subresource)
    {
        void** vtbl = *(void***)context;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)vtbl[15];
        fn(context, resource, subresource);
    }

    // ID3D11DeviceContext::CopyResource -> VTable[47] (void return)
    private static void ContextCopyResource(IntPtr context, IntPtr dstResource, IntPtr srcResource)
    {
        void** vtbl = *(void***)context;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)vtbl[47];
        fn(context, dstResource, srcResource);
    }
}
