using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using TerkirdoComputer.Core;

namespace TerkirdoComputer.Windows;

public static class ScreenCapture
{
    private static readonly ImageCodecInfo? CachedJpegEncoder =
        ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

    /// <summary>
    /// Checks if DXGI Desktop Duplication / GPU display capture is supported and active in the current session.
    /// </summary>
    public static bool IsDxgiSupported() => DxgiCapture.IsSupported();

    /// <summary>
    /// Validates optional ROI region coordinates. If any of the 4 parameters is provided, all 4 must be finite
    /// and width/height must be strictly positive.
    /// </summary>
    public static bool ValidateOptionalRegion(
        double? x,
        double? y,
        double? width,
        double? height,
        string prefix = "")
    {
        bool anyProvided = x.HasValue || y.HasValue || width.HasValue || height.HasValue;
        if (!anyProvided)
        {
            return false;
        }

        if (!x.HasValue || !y.HasValue || !width.HasValue || !height.HasValue)
        {
            throw new ArgumentException(
                $"Region capture requires all four parameters: {prefix}x, {prefix}y, {prefix}width, and {prefix}height.");
        }

        if (!double.IsFinite(x.Value) || !double.IsFinite(y.Value) ||
            !double.IsFinite(width.Value) || !double.IsFinite(height.Value))
        {
            throw new ArgumentException($"Region parameters ({prefix}x, {prefix}y, {prefix}width, {prefix}height) must be finite numbers.");
        }

        if (width.Value <= 0 || height.Value <= 0)
        {
            throw new ArgumentException($"Region dimensions ({prefix}width, {prefix}height) must be > 0.");
        }

        return true;
    }

    public static ImageCaptureConfig ApplyOverrides(
        ImageCaptureConfig baseCfg,
        string? formatOverride = null,
        int? maxDimensionOverride = null)
    {
        string fmt = baseCfg.Format;
        if (!string.IsNullOrWhiteSpace(formatOverride))
        {
            string normFmt = formatOverride.Trim().ToLowerInvariant();
            fmt = normFmt switch
            {
                "png" => "png",
                "jpg" or "jpeg" => "jpg",
                _ => throw new ArgumentException($"Invalid format '{formatOverride}'. Supported formats are: png, jpg.")
            };
        }

        int? maxDim = baseCfg.MaxDimension;
        if (maxDimensionOverride.HasValue)
        {
            if (maxDimensionOverride.Value < ImageCaptureConfig.MinimumDimension)
            {
                throw new ArgumentException($"max_dimension must be >= {ImageCaptureConfig.MinimumDimension}.");
            }
            maxDim = maxDimensionOverride.Value;
        }

        return new ImageCaptureConfig
        {
            Format = fmt,
            JpegQuality = baseCfg.JpegQuality,
            MaxDimension = maxDim,
            ScaleDownAfterMaxSize = baseCfg.ScaleDownAfterMaxSize,
            DiscardBelowPixelCount = baseCfg.DiscardBelowPixelCount,
            CaptureTimeout = baseCfg.CaptureTimeout
        };
    }

    /// <summary>
    /// Enumerates all connected display monitors using EnumDisplayMonitors.
    /// Guarantees that the primary monitor is the first element in the returned list.
    /// </summary>
    public static List<DisplayDescriptor> GetDisplays()
    {
        var rawList = new List<(bool isPrimary, DisplayDescriptor desc)>();
        int enumCount = 0;

        NativeMethods.MonitorEnumProc proc = (IntPtr hMonitor, IntPtr hdcMonitor, ref NativeMethods.RECT lprcMonitor, IntPtr dwData) =>
        {
            var mi = new NativeMethods.MONITORINFOEX();
            mi.cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>();
            if (NativeMethods.GetMonitorInfo(hMonitor, ref mi))
            {
                enumCount++;
                bool isPrimary = (mi.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0;
                string name = string.IsNullOrWhiteSpace(mi.szDevice) ? $"DISPLAY{enumCount}" : mi.szDevice;
                int width = mi.rcMonitor.Right - mi.rcMonitor.Left;
                int height = mi.rcMonitor.Bottom - mi.rcMonitor.Top;

                var descriptor = new DisplayDescriptor
                {
                    Index = enumCount,
                    Name = name,
                    IsPrimary = isPrimary,
                    Bounds = new Frame(mi.rcMonitor.Left, mi.rcMonitor.Top, width, height)
                };

                rawList.Add((isPrimary, descriptor));
            }
            return true;
        };

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);

        if (rawList.Count == 0)
        {
            int w = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
            int h = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
            return
            [
                new DisplayDescriptor
                {
                    Index = 1,
                    Name = "DISPLAY1",
                    IsPrimary = true,
                    Bounds = new Frame(0, 0, w > 0 ? w : 1920, h > 0 ? h : 1080)
                }
            ];
        }

        // Primary display MUST be first in the returned list
        var ordered = rawList
            .OrderByDescending(x => x.isPrimary)
            .Select(x => x.desc)
            .ToList();

        for (int i = 0; i < ordered.Count; i++)
        {
            ordered[i].Index = i + 1;
        }

        return ordered;
    }

    private static (Bitmap Bitmap, string NormDisplay, List<DisplayDescriptor> Displays, Frame? RegionFrame) CaptureScreenBitmapCore(
        string display,
        double? regionX,
        double? regionY,
        double? regionWidth,
        double? regionHeight,
        bool fastNonBlocking = false)
    {
        bool hasRegion = ValidateOptionalRegion(regionX, regionY, regionWidth, regionHeight);
        var displays = GetDisplays();
        string norm = string.IsNullOrWhiteSpace(display) ? "all" : display.Trim().ToLowerInvariant();

        int baseX;
        int baseY;
        int baseWidth;
        int baseHeight;

        if (norm == "all")
        {
            baseX = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
            baseY = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
            baseWidth = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
            baseHeight = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

            if (baseWidth <= 0 || baseHeight <= 0)
            {
                baseX = 0;
                baseY = 0;
                baseWidth = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
                baseHeight = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
            }
        }
        else
        {
            DisplayDescriptor? target = null;
            if (int.TryParse(norm, out int displayIdx))
            {
                target = displays.FirstOrDefault(d => d.Index == displayIdx);
            }
            target ??= displays.FirstOrDefault(d => string.Equals(d.Name, norm, StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                throw new ArgumentException($"displayNotFound(\"{display}\")");
            }

            baseX = (int)Math.Round(target.Bounds.X);
            baseY = (int)Math.Round(target.Bounds.Y);
            baseWidth = (int)Math.Round(target.Bounds.Width);
            baseHeight = (int)Math.Round(target.Bounds.Height);
        }

        if (baseWidth <= 0) baseWidth = 1920;
        if (baseHeight <= 0) baseHeight = 1080;

        int captureX;
        int captureY;
        int captureWidth;
        int captureHeight;
        Frame? regionFrame = null;

        if (hasRegion)
        {
            int relX = (int)Math.Round(regionX!.Value);
            int relY = (int)Math.Round(regionY!.Value);
            captureWidth = Math.Clamp((int)Math.Round(regionWidth!.Value), 1, 16384);
            captureHeight = Math.Clamp((int)Math.Round(regionHeight!.Value), 1, 16384);
            captureX = baseX + relX;
            captureY = baseY + relY;
            regionFrame = new Frame(relX, relY, captureWidth, captureHeight);
        }
        else
        {
            captureX = baseX;
            captureY = baseY;
            captureWidth = baseWidth;
            captureHeight = baseHeight;
        }

        uint? dxgiTimeout = fastNonBlocking ? 0u : null;
        Bitmap? capturedBitmap = null;
        if (!DxgiCapture.TryCaptureRegion(captureX, captureY, captureWidth, captureHeight, out capturedBitmap, dxgiTimeout) ||
            capturedBitmap == null)
        {
            capturedBitmap?.Dispose();
            capturedBitmap = new Bitmap(captureWidth, captureHeight, PixelFormat.Format32bppArgb);
            try
            {
                using var graphics = Graphics.FromImage(capturedBitmap);
                graphics.CopyFromScreen(captureX, captureY, 0, 0, new Size(captureWidth, captureHeight), CopyPixelOperation.SourceCopy);
            }
            catch
            {
                using var graphics = Graphics.FromImage(capturedBitmap);
                graphics.Clear(Color.Black);
            }
        }

        return (capturedBitmap, norm, displays, regionFrame);
    }

    /// <summary>
    /// Captures the full virtual desktop, a specific display, or an exact ROI sub-rectangle (allocating only the ROI size).
    /// Applies OcuConfig image format (png/jpg) and maxDimension scaling with optional per-call overrides.
    /// </summary>
    public static ScreenSnapshot CaptureScreen(
        string display = "all",
        double? regionX = null,
        double? regionY = null,
        double? regionWidth = null,
        double? regionHeight = null,
        string? formatOverride = null,
        int? maxDimensionOverride = null)
    {
        var cfg = ApplyOverrides(OcuConfig.LoadImageCaptureConfig(), formatOverride, maxDimensionOverride);
        var (capturedBitmap, norm, displays, regionFrame) = CaptureScreenBitmapCore(
            display,
            regionX,
            regionY,
            regionWidth,
            regionHeight);

        using (capturedBitmap)
        {
            string base64 = EncodeBitmapToBase64(capturedBitmap, cfg, out int finalWidth, out int finalHeight, out string mimeType);
            return new ScreenSnapshot
            {
                Display = norm,
                MimeType = mimeType,
                Region = regionFrame,
                Frame = new ScreenFrame
                {
                    Width = finalWidth,
                    Height = finalHeight,
                    Base64 = base64
                },
                Displays = displays
            };
        }
    }

    /// <summary>
    /// Caller-agnostic zero-Base64 binary fast path for GET /api/screens/screenshot?raw=true.
    /// Encodes directly to byte[] without Base64 string allocation or decoding overhead for 30-60 FPS OpenCV/HttpClient loops.
    /// </summary>
    public static byte[] CaptureScreenRawBytes(
        string display,
        out int finalWidth,
        out int finalHeight,
        out string mimeType,
        double? regionX = null,
        double? regionY = null,
        double? regionWidth = null,
        double? regionHeight = null,
        string? formatOverride = null,
        int? maxDimensionOverride = null)
    {
        var cfg = ApplyOverrides(OcuConfig.LoadImageCaptureConfig(), formatOverride, maxDimensionOverride);
        var (capturedBitmap, _, _, _) = CaptureScreenBitmapCore(
            display,
            regionX,
            regionY,
            regionWidth,
            regionHeight);

        using (capturedBitmap)
        {
            return EncodeBitmapToBytes(capturedBitmap, cfg, out finalWidth, out finalHeight, out mimeType);
        }
    }

    /// <summary>
    /// Alias method for multi-monitor display capture.
    /// </summary>
    public static ScreenSnapshot CaptureDisplay(string displayIndexOrAll) =>
        CaptureScreen(displayIndexOrAll);

    /// <summary>
    /// Captures an unencoded Format32bppArgb Bitmap of a display or display-relative ROI for ScreenProbe.
    /// </summary>
    public static Bitmap CaptureScreenRegionBitmap(
        string display,
        double? regionX = null,
        double? regionY = null,
        double? regionWidth = null,
        double? regionHeight = null,
        bool fastNonBlocking = false)
    {
        var (capturedBitmap, _, _, _) = CaptureScreenBitmapCore(display, regionX, regionY, regionWidth, regionHeight, fastNonBlocking);
        return capturedBitmap;
    }

    /// <summary>
    /// Captures an unencoded Format32bppArgb Bitmap at absolute virtual-screen coordinates for ScreenProbe.
    /// </summary>
    public static Bitmap CaptureAbsoluteScreenRectBitmap(int screenX, int screenY, int width, int height, bool fastNonBlocking = false)
    {
        int w = Math.Clamp(width, 1, 16384);
        int h = Math.Clamp(height, 1, 16384);
        uint? dxgiTimeout = fastNonBlocking ? 0u : null;

        if (DxgiCapture.TryCaptureRegion(screenX, screenY, w, h, out Bitmap? dxgiBmp, dxgiTimeout) && dxgiBmp != null)
        {
            return dxgiBmp;
        }

        dxgiBmp?.Dispose();
        var gdiBmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        try
        {
            using var g = Graphics.FromImage(gdiBmp);
            g.CopyFromScreen(screenX, screenY, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
        }
        catch
        {
            using var g = Graphics.FromImage(gdiBmp);
            g.Clear(Color.Black);
        }

        return gdiBmp;
    }

    /// <summary>
    /// Captures the specified window bounds and returns a Base64-encoded image string.
    /// </summary>
    public static string? CaptureWindowPngBase64(Frame? bounds, IntPtr hwnd = default) =>
        CaptureWindowImageBase64(bounds, hwnd, out _);

    public static string? CaptureWindowImageBase64(
        Frame? bounds,
        IntPtr hwnd,
        out string mimeType,
        double? regionX = null,
        double? regionY = null,
        double? regionWidth = null,
        double? regionHeight = null,
        string? formatOverride = null,
        int? maxDimensionOverride = null,
        bool clientOnly = false)
    {
        byte[]? bytes = CaptureWindowImageBytes(
            bounds,
            hwnd,
            out mimeType,
            regionX,
            regionY,
            regionWidth,
            regionHeight,
            formatOverride,
            maxDimensionOverride,
            clientOnly);

        return bytes != null ? Convert.ToBase64String(bytes) : null;
    }

    /// <summary>
    /// Walks upward in the Win32 top-level Z-order (GW_HWNDPREV) from hwnd's root window to verify that
    /// zero visible, non-iconic, non-cloaked higher-Z windows (including WS_EX_TOPMOST overlays) intersect
    /// [roiLeft, roiTop, roiRight, roiBottom], and that the ROI lies completely within virtual screen bounds.
    /// </summary>
    public static bool IsRectUnoccludedInZOrder(IntPtr hwnd, int roiLeft, int roiTop, int roiRight, int roiBottom)
    {
        if (roiRight <= roiLeft || roiBottom <= roiTop)
        {
            return false;
        }

        int vX = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        int vY = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        int vW = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        int vH = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        if (vW <= 0 || vH <= 0)
        {
            vX = 0;
            vY = 0;
            vW = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
            vH = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
        }

        if (vW <= 0 || vH <= 0 ||
            roiLeft < vX ||
            roiTop < vY ||
            roiRight > (vX + vW) ||
            roiBottom > (vY + vH))
        {
            return false;
        }

        if (hwnd == IntPtr.Zero)
        {
            return true;
        }

        IntPtr rootHwnd = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        if (rootHwnd == IntPtr.Zero)
        {
            rootHwnd = hwnd;
        }

        IntPtr above = NativeMethods.GetWindow(rootHwnd, NativeMethods.GW_HWNDPREV);
        int steps = 0;
        while (above != IntPtr.Zero)
        {
            if (++steps > 512)
            {
                return false;
            }

            if (NativeMethods.IsWindowVisible(above) &&
                !NativeMethods.IsIconic(above) &&
                !NativeMethods.IsCloaked(above) &&
                NativeMethods.GetWindowRect(above, out var r) &&
                r.Right > r.Left &&
                r.Bottom > r.Top &&
                r.Left < roiRight &&
                r.Right > roiLeft &&
                r.Top < roiBottom &&
                r.Bottom > roiTop)
            {
                return false;
            }

            above = NativeMethods.GetWindow(above, NativeMethods.GW_HWNDPREV);
        }

        return true;
    }

    /// <summary>
    /// Captures full window, client-only canvas, or window/client-relative ROI directly into an unencoded Format32bppArgb Bitmap.
    /// </summary>
    public static Bitmap? CaptureWindowRawBitmap(
        Frame? bounds,
        IntPtr hwnd,
        double? regionX = null,
        double? regionY = null,
        double? regionWidth = null,
        double? regionHeight = null,
        bool clientOnly = false,
        int discardBelowPixelCount = 0,
        bool fastNonBlocking = false)
    {
        bool hasRegion = ValidateOptionalRegion(regionX, regionY, regionWidth, regionHeight, "region_");

        if (hwnd != IntPtr.Zero && (!NativeMethods.IsWindow(hwnd) || NativeMethods.IsIconic(hwnd)))
        {
            return null;
        }

        try
        {
            int fullWinX;
            int fullWinY;
            int fullWinWidth;
            int fullWinHeight;
            int baseScreenX;
            int baseScreenY;
            int baseWidth;
            int baseHeight;
            int pwOffsetX = 0;
            int pwOffsetY = 0;

            if (clientOnly)
            {
                if (hwnd == IntPtr.Zero || !NativeMethods.IsCapturableWindow(hwnd, out var fullWinBounds) || fullWinBounds == null)
                {
                    return null;
                }

                var clientBounds = WindowManager.GetClientScreenBounds(hwnd);
                if (clientBounds == null || clientBounds.Width < 1 || clientBounds.Height < 1)
                {
                    return null;
                }

                fullWinX = (int)Math.Round(fullWinBounds.X);
                fullWinY = (int)Math.Round(fullWinBounds.Y);
                fullWinWidth = (int)Math.Round(fullWinBounds.Width);
                fullWinHeight = (int)Math.Round(fullWinBounds.Height);

                baseScreenX = (int)Math.Round(clientBounds.X);
                baseScreenY = (int)Math.Round(clientBounds.Y);
                baseWidth = (int)Math.Round(clientBounds.Width);
                baseHeight = (int)Math.Round(clientBounds.Height);

                pwOffsetX = Math.Max(0, baseScreenX - fullWinX);
                pwOffsetY = Math.Max(0, baseScreenY - fullWinY);
            }
            else
            {
                if (bounds == null || bounds.Width <= 0 || bounds.Height <= 0)
                {
                    return null;
                }

                fullWinX = (int)Math.Round(bounds.X);
                fullWinY = (int)Math.Round(bounds.Y);
                fullWinWidth = (int)Math.Round(bounds.Width);
                fullWinHeight = (int)Math.Round(bounds.Height);

                baseScreenX = fullWinX;
                baseScreenY = fullWinY;
                baseWidth = fullWinWidth;
                baseHeight = fullWinHeight;
            }

            if (fullWinWidth <= 0 || fullWinHeight <= 0 || baseWidth <= 0 || baseHeight <= 0)
            {
                return null;
            }

            if (!hasRegion && discardBelowPixelCount > 0 && (fullWinWidth * fullWinHeight) < discardBelowPixelCount)
            {
                return null;
            }

            if (hasRegion || clientOnly)
            {
                int relX = hasRegion ? (int)Math.Round(regionX!.Value) : 0;
                int relY = hasRegion ? (int)Math.Round(regionY!.Value) : 0;
                int roiW = hasRegion ? Math.Clamp((int)Math.Round(regionWidth!.Value), 1, 16384) : baseWidth;
                int roiH = hasRegion ? Math.Clamp((int)Math.Round(regionHeight!.Value), 1, 16384) : baseHeight;
                int roiScreenX = baseScreenX + relX;
                int roiScreenY = baseScreenY + relY;

                bool isDirectlyVisible = IsRectUnoccludedInZOrder(
                    hwnd,
                    roiScreenX,
                    roiScreenY,
                    roiScreenX + roiW,
                    roiScreenY + roiH);
                uint? dxgiTimeout = fastNonBlocking ? 0u : null;

                // Fast path for foreground or unoccluded game windows: sub-millisecond DXGI / BitBlt slice without DWM PrintWindow RPC flood
                if ((hasRegion || fastNonBlocking) &&
                    isDirectlyVisible &&
                    (hwnd == IntPtr.Zero || !NativeMethods.IsCloaked(hwnd)))
                {
                    if (DxgiCapture.TryCaptureRegion(roiScreenX, roiScreenY, roiW, roiH, out Bitmap? fgDxgiRoi, dxgiTimeout) &&
                        fgDxgiRoi != null)
                    {
                        return fgDxgiRoi;
                    }

                    if (fastNonBlocking)
                    {
                        var fastGdiRoi = new Bitmap(roiW, roiH, PixelFormat.Format32bppArgb);
                        try
                        {
                            using var fgG = Graphics.FromImage(fastGdiRoi);
                            fgG.CopyFromScreen(roiScreenX, roiScreenY, 0, 0, new Size(roiW, roiH), CopyPixelOperation.SourceCopy);
                            return fastGdiRoi;
                        }
                        catch
                        {
                            fastGdiRoi.Dispose();
                        }
                    }
                }

                // Stage 1: PrintWindow always renders from fullWindowRect origin (including title bar & DWM border),
                // so crop by (pwOffsetX + relX, pwOffsetY + relY) for pixel-exact client/ROI output even when occluded.
                int pwCropX = pwOffsetX + relX;
                int pwCropY = pwOffsetY + relY;
                if (hwnd != IntPtr.Zero && NativeMethods.IsWindow(hwnd) &&
                    pwCropX >= 0 && pwCropY >= 0 && pwCropX < fullWinWidth && pwCropY < fullWinHeight)
                {
                    int clampedW = Math.Min(roiW, fullWinWidth - pwCropX);
                    int clampedH = Math.Min(roiH, fullWinHeight - pwCropY);
                    if (clampedW > 0 && clampedH > 0)
                    {
                        using var fullWindowBmp = new Bitmap(fullWinWidth, fullWinHeight, PixelFormat.Format32bppArgb);
                        bool pwOk = false;
                        try
                        {
                            using var g = Graphics.FromImage(fullWindowBmp);
                            IntPtr hdc = g.GetHdc();
                            try
                            {
                                pwOk = NativeMethods.PrintWindow(hwnd, hdc, NativeMethods.PW_RENDERFULLCONTENT);
                            }
                            finally
                            {
                                g.ReleaseHdc(hdc);
                            }
                        }
                        catch
                        {
                            pwOk = false;
                        }

                        if (pwOk && HasNonBlackContent(fullWindowBmp))
                        {
                            return fullWindowBmp.Clone(
                                new Rectangle(pwCropX, pwCropY, clampedW, clampedH),
                                PixelFormat.Format32bppArgb);
                        }
                    }
                }

                // Stage 2: DXGI Desktop Duplication slice at exact client/ROI screen coordinates
                if ((hwnd == IntPtr.Zero || !NativeMethods.IsCloaked(hwnd)) &&
                    DxgiCapture.TryCaptureRegion(roiScreenX, roiScreenY, roiW, roiH, out Bitmap? bgDxgiRoi, dxgiTimeout) &&
                    bgDxgiRoi != null)
                {
                    return bgDxgiRoi;
                }

                // Stage 3: GDI CopyFromScreen fallback
                var gdiRoi = new Bitmap(roiW, roiH, PixelFormat.Format32bppArgb);
                try
                {
                    using var g = Graphics.FromImage(gdiRoi);
                    g.CopyFromScreen(roiScreenX, roiScreenY, 0, 0, new Size(roiW, roiH), CopyPixelOperation.SourceCopy);
                    return gdiRoi;
                }
                catch
                {
                    gdiRoi.Dispose();
                    return null;
                }
            }

            Bitmap workingBitmap = new(fullWinWidth, fullWinHeight, PixelFormat.Format32bppArgb);
            bool captured = false;

            try
            {
                if (hwnd != IntPtr.Zero && NativeMethods.IsWindow(hwnd))
                {
                    try
                    {
                        bool pwOk;
                        using (var graphics = Graphics.FromImage(workingBitmap))
                        {
                            IntPtr hdc = graphics.GetHdc();
                            try
                            {
                                pwOk = NativeMethods.PrintWindow(hwnd, hdc, NativeMethods.PW_RENDERFULLCONTENT);
                            }
                            finally
                            {
                                graphics.ReleaseHdc(hdc);
                            }
                        }

                        captured = pwOk && HasNonBlackContent(workingBitmap);
                    }
                    catch
                    {
                        captured = false;
                    }
                }

                if (!captured &&
                    (hwnd == IntPtr.Zero || (!NativeMethods.IsCloaked(hwnd) && NativeMethods.IntersectsVirtualScreen(bounds!))))
                {
                    uint? fullDxgiTimeout = fastNonBlocking ? 0u : null;
                    if (DxgiCapture.TryCaptureRegion(fullWinX, fullWinY, fullWinWidth, fullWinHeight, out Bitmap? dxgiBitmap, fullDxgiTimeout) && dxgiBitmap != null)
                    {
                        workingBitmap.Dispose();
                        workingBitmap = dxgiBitmap;
                        captured = true;
                    }
                }

                if (!captured)
                {
                    try
                    {
                        using var graphics = Graphics.FromImage(workingBitmap);
                        graphics.CopyFromScreen(fullWinX, fullWinY, 0, 0, new Size(fullWinWidth, fullWinHeight), CopyPixelOperation.SourceCopy);
                        captured = true;
                    }
                    catch
                    {
                        captured = false;
                    }
                }

                if (!captured)
                {
                    workingBitmap.Dispose();
                    return null;
                }

                return workingBitmap;
            }
            catch
            {
                workingBitmap.Dispose();
                return null;
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Captures full window, client-only canvas, or window/client-relative ROI directly into encoded PNG/JPEG bytes without Base64 roundtrip.
    /// </summary>
    public static byte[]? CaptureWindowImageBytes(
        Frame? bounds,
        IntPtr hwnd,
        out string mimeType,
        double? regionX = null,
        double? regionY = null,
        double? regionWidth = null,
        double? regionHeight = null,
        string? formatOverride = null,
        int? maxDimensionOverride = null,
        bool clientOnly = false)
    {
        mimeType = "image/png";
        _ = ValidateOptionalRegion(regionX, regionY, regionWidth, regionHeight, "region_");
        var cfg = ApplyOverrides(OcuConfig.LoadImageCaptureConfig(), formatOverride, maxDimensionOverride);
        mimeType = cfg.MimeType;

        using Bitmap? rawBitmap = CaptureWindowRawBitmap(
            bounds,
            hwnd,
            regionX,
            regionY,
            regionWidth,
            regionHeight,
            clientOnly,
            cfg.DiscardBelowPixelCount);

        if (rawBitmap == null)
        {
            return null;
        }

        return EncodeBitmapToBytes(rawBitmap, cfg, out _, out _, out mimeType);
    }

    /// <summary>
    /// Fast grid-sampling check (up to 32x32 points) verifying whether a bitmap contains at least one non-black pixel.
    /// </summary>
    public static unsafe bool HasNonBlackContent(Bitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var rect = new Rectangle(0, 0, width, height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int cols = Math.Min(32, width);
            int rows = Math.Min(32, height);
            byte* scan0 = (byte*)data.Scan0;
            int stride = data.Stride;

            for (int ry = 0; ry < rows; ry++)
            {
                int py = rows == 1 ? 0 : (int)((long)ry * (height - 1) / (rows - 1));
                byte* rowPtr = scan0 + (long)py * stride;
                for (int rx = 0; rx < cols; rx++)
                {
                    int px = cols == 1 ? 0 : (int)((long)rx * (width - 1) / (cols - 1));
                    int off = px * 4;
                    if (rowPtr[off] != 0 || rowPtr[off + 1] != 0 || rowPtr[off + 2] != 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>
    /// Allocation-lean Option A byte-array encoder: applies MaxDimension scaling and encodes directly to PNG/JPEG byte[]
    /// without Base64 conversion.
    /// </summary>
    public static byte[] EncodeBitmapToBytes(
        Bitmap source,
        ImageCaptureConfig cfg,
        out int finalWidth,
        out int finalHeight,
        out string mimeType)
    {
        int width = source.Width;
        int height = source.Height;
        finalWidth = width;
        finalHeight = height;

        Bitmap outputBitmap = source;
        Bitmap? resizedBitmap = null;

        try
        {
            if (cfg.MaxDimension.HasValue && cfg.MaxDimension.Value >= ImageCaptureConfig.MinimumDimension)
            {
                int maxDim = cfg.MaxDimension.Value;
                int longEdge = Math.Max(width, height);
                if (longEdge > maxDim)
                {
                    double scale = (double)maxDim / longEdge;
                    int scaledW = Math.Max(1, (int)Math.Round(width * scale));
                    int scaledH = Math.Max(1, (int)Math.Round(height * scale));
                    resizedBitmap = new Bitmap(scaledW, scaledH, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(resizedBitmap))
                    {
                        g.CompositingMode = CompositingMode.SourceCopy;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.SmoothingMode = SmoothingMode.HighQuality;
                        g.DrawImage(source, 0, 0, scaledW, scaledH);
                    }
                    outputBitmap = resizedBitmap;
                    finalWidth = scaledW;
                    finalHeight = scaledH;
                }
            }

            using var memoryStream = new MemoryStream(Math.Max(4096, finalWidth * finalHeight / 2));
            if (string.Equals(cfg.Format, "jpg", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cfg.Format, "jpeg", StringComparison.OrdinalIgnoreCase))
            {
                mimeType = "image/jpeg";
                if (CachedJpegEncoder != null)
                {
                    using var encoderParams = new EncoderParameters(1);
                    long qualityPercent = Math.Clamp((long)Math.Round(cfg.JpegQuality * 100.0), 1L, 100L);
                    encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, qualityPercent);
                    outputBitmap.Save(memoryStream, CachedJpegEncoder, encoderParams);
                }
                else
                {
                    outputBitmap.Save(memoryStream, ImageFormat.Jpeg);
                }
            }
            else
            {
                mimeType = "image/png";
                outputBitmap.Save(memoryStream, ImageFormat.Png);
            }

            return memoryStream.ToArray();
        }
        finally
        {
            resizedBitmap?.Dispose();
        }
    }

    /// <summary>
    /// Shared Option A Base64 encoding pipeline built atop EncodeBitmapToBytes.
    /// </summary>
    public static string EncodeBitmapToBase64(
        Bitmap source,
        ImageCaptureConfig cfg,
        out int finalWidth,
        out int finalHeight,
        out string mimeType)
    {
        byte[] bytes = EncodeBitmapToBytes(source, cfg, out finalWidth, out finalHeight, out mimeType);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Single-pass unsafe BGRA32 scanline copy and 64-bit FNV-1a hash calculation into a caller-supplied Span
    /// (used by both SharedFrameStreamService "TKSM" shared memory and WebSocketControlHub "TKRF" binary frames).
    /// </summary>
    public static unsafe (ulong Fnv1aHash, int ByteCount) CopyBitmapBgra32AndHash(
        Bitmap source,
        Span<byte> destination,
        out int width,
        out int height,
        out int packedStride)
    {
        width = source.Width;
        height = source.Height;
        packedStride = width * 4;
        long totalBytesLong = (long)packedStride * height;
        if (width <= 0 || height <= 0 || totalBytesLong > destination.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(destination),
                $"Destination buffer ({destination.Length} B) is smaller than frame byte count ({totalBytesLong} B).");
        }

        int totalBytes = (int)totalBytesLong;
        var rect = new Rectangle(0, 0, width, height);
        var data = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte* srcScan0 = (byte*)data.Scan0;
            int srcStride = data.Stride;
            ulong hash = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;

            fixed (byte* dstBase = destination)
            {
                for (int y = 0; y < height; y++)
                {
                    byte* srcRow = srcScan0 + ((long)y * srcStride);
                    byte* dstRow = dstBase + ((long)y * packedStride);
                    Buffer.MemoryCopy(srcRow, dstRow, packedStride, packedStride);

                    // Sample every pixel's RGB channels for deterministic FNV-1a content change detection
                    for (int x = 0; x < packedStride; x += 4)
                    {
                        hash = (hash ^ srcRow[x + 2]) * prime; // R
                        hash = (hash ^ srcRow[x + 1]) * prime; // G
                        hash = (hash ^ srcRow[x]) * prime;     // B
                    }
                }
            }

            return (hash, totalBytes);
        }
        finally
        {
            source.UnlockBits(data);
        }
    }
}
