using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using TerkirdoComputer.Core;

namespace TerkirdoComputer.Windows;

/// <summary>
/// Sub-millisecond UIA-less perception sensor for 3D/2D games (probe_screen / POST /api/vision/probe).
/// Captures only a single tight bounding rectangle across all requested points and regions into a Format32bppArgb
/// Bitmap (zero PNG/JPEG encoding, zero Base64 allocation) and computes RGB point matches, 64-bit FNV-1a region hashes,
/// color match ratios, and matching-pixel center-of-mass (centroidX, centroidY) in a single unsafe LockBits pass.
/// </summary>
public static class ScreenProbe
{
    public const int MaxPoints = 256;
    public const int MaxRegions = 32;

    public static (byte R, byte G, byte B, string CanonicalHex) ParseHexColor(string rawHex, string paramName)
    {
        if (string.IsNullOrWhiteSpace(rawHex))
        {
            throw new ArgumentException($"{paramName} cannot be empty.");
        }

        string s = rawHex.Trim();
        if (s.StartsWith('#'))
        {
            s = s[1..];
        }

        if (s.Length != 6 ||
            !byte.TryParse(s.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) ||
            !byte.TryParse(s.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) ||
            !byte.TryParse(s.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
        {
            throw new ArgumentException($"Invalid hex color '{rawHex}' for {paramName}. Expected '#RRGGBB' or 'RRGGBB'.");
        }

        return (r, g, b, $"#{r:X2}{g:X2}{b:X2}");
    }

    public static VisionProbeResult Probe(
        AutomationService automationService,
        string? app,
        int? pid,
        string? display,
        string? coordinateSpace,
        IReadOnlyList<PointProbeSpec> points,
        IReadOnlyList<RegionProbeSpec> regions,
        bool fastNonBlocking = false)
    {
        ValidateSpecs(points, regions, out var parsedExpectedColors, out var parsedTargetColors);

        var sw = Stopwatch.StartNew();
        NativeMethods.EnsurePerMonitorDpiAwareness();

        bool hasTargetApp = !string.IsNullOrWhiteSpace(app) || pid.HasValue;
        string normSpace;

        // Compute tight bounding box in caller's requested coordinate space
        ComputeBoundingBox(points, regions, out int minX, out int minY, out int bboxW, out int bboxH);

        Bitmap? capturedBitmap;
        if (hasTargetApp)
        {
            var process = automationService.ResolveApp(app, pid);
            IntPtr hwnd = WindowManager.GetTargetHwnd(process);
            if (hwnd == IntPtr.Zero)
            {
                hwnd = process.MainWindowHandle;
            }

            var currentSnapshot = automationService.GetCurrentSnapshot(app, pid);
            normSpace = WindowManager.NormalizeCoordinateSpace(
                coordinateSpace,
                hasTargetApp: true,
                snapshotClientOnly: currentSnapshot?.ClientOnly == true);

            var windowBounds = automationService.GetWindowBounds(process, null, hwnd);

            if (normSpace == "client")
            {
                var clientBounds = WindowManager.GetClientScreenBounds(hwnd)
                    ?? throw new InvalidOperationException("Target window client area is unavailable (window may be minimized or closed).");

                capturedBitmap = ScreenCapture.CaptureWindowRawBitmap(
                    windowBounds,
                    hwnd,
                    minX,
                    minY,
                    bboxW,
                    bboxH,
                    clientOnly: true,
                    fastNonBlocking: fastNonBlocking);

                capturedBitmap ??= ScreenCapture.CaptureAbsoluteScreenRectBitmap(
                    (int)Math.Round(clientBounds.X) + minX,
                    (int)Math.Round(clientBounds.Y) + minY,
                    bboxW,
                    bboxH,
                    fastNonBlocking);
            }
            else if (normSpace == "window")
            {
                capturedBitmap = ScreenCapture.CaptureWindowRawBitmap(
                    windowBounds,
                    hwnd,
                    minX,
                    minY,
                    bboxW,
                    bboxH,
                    clientOnly: false,
                    fastNonBlocking: fastNonBlocking);

                if (capturedBitmap == null && windowBounds != null)
                {
                    capturedBitmap = ScreenCapture.CaptureAbsoluteScreenRectBitmap(
                        (int)Math.Round(windowBounds.X) + minX,
                        (int)Math.Round(windowBounds.Y) + minY,
                        bboxW,
                        bboxH,
                        fastNonBlocking);
                }
            }
            else
            {
                capturedBitmap = ScreenCapture.CaptureAbsoluteScreenRectBitmap(minX, minY, bboxW, bboxH, fastNonBlocking);
            }
        }
        else
        {
            normSpace = WindowManager.NormalizeCoordinateSpace(coordinateSpace, hasTargetApp: false);
            if (normSpace != "screen")
            {
                throw new ArgumentException($"coordinate_space '{normSpace}' requires a target application ('app' or 'pid').");
            }

            string targetDisplay = string.IsNullOrWhiteSpace(display) ? "1" : display.Trim();
            if (string.Equals(targetDisplay, "all", StringComparison.OrdinalIgnoreCase))
            {
                capturedBitmap = ScreenCapture.CaptureAbsoluteScreenRectBitmap(minX, minY, bboxW, bboxH, fastNonBlocking);
            }
            else
            {
                capturedBitmap = ScreenCapture.CaptureScreenRegionBitmap(targetDisplay, minX, minY, bboxW, bboxH, fastNonBlocking);
            }
        }

        if (capturedBitmap == null)
        {
            throw new InvalidOperationException("Unable to capture target surface for probe_screen (window may be minimized or unavailable).");
        }

        using (capturedBitmap)
        {
            return EvaluateLockedBitmap(
                capturedBitmap,
                minX,
                minY,
                normSpace,
                points,
                regions,
                parsedExpectedColors,
                parsedTargetColors,
                sw);
        }
    }

    public static VisionProbeResult ProbeBitmap(
        Bitmap bitmap,
        int bboxOriginX,
        int bboxOriginY,
        string coordinateSpace,
        IReadOnlyList<PointProbeSpec> points,
        IReadOnlyList<RegionProbeSpec> regions)
    {
        ValidateSpecs(points, regions, out var parsedExpectedColors, out var parsedTargetColors);
        var sw = Stopwatch.StartNew();
        return EvaluateLockedBitmap(
            bitmap,
            bboxOriginX,
            bboxOriginY,
            coordinateSpace,
            points,
            regions,
            parsedExpectedColors,
            parsedTargetColors,
            sw);
    }

    private static void ValidateSpecs(
        IReadOnlyList<PointProbeSpec> points,
        IReadOnlyList<RegionProbeSpec> regions,
        out (bool HasColor, byte R, byte G, byte B, string Hex)[] expectedColors,
        out (bool HasColor, byte R, byte G, byte B, string Hex)[] targetColors)
    {
        int pCount = points?.Count ?? 0;
        int rCount = regions?.Count ?? 0;

        if (pCount + rCount < 1)
        {
            throw new ArgumentException("probe_screen requires at least one item in 'points' or 'regions'.");
        }

        if (pCount > MaxPoints)
        {
            throw new ArgumentException($"points count ({pCount}) exceeds maximum of {MaxPoints}.");
        }

        if (rCount > MaxRegions)
        {
            throw new ArgumentException($"regions count ({rCount}) exceeds maximum of {MaxRegions}.");
        }

        expectedColors = new (bool, byte, byte, byte, string)[pCount];
        for (int i = 0; i < pCount; i++)
        {
            var pt = points![i];
            if (!double.IsFinite(pt.X) || !double.IsFinite(pt.Y))
            {
                throw new ArgumentException($"Point[{i}] x and y must be finite numbers.");
            }
            if (pt.Tolerance < 0 || pt.Tolerance > 255)
            {
                throw new ArgumentException($"Point[{i}] tolerance must be between 0 and 255.");
            }
            if (!string.IsNullOrWhiteSpace(pt.ExpectedHex))
            {
                var (er, eg, eb, hex) = ParseHexColor(pt.ExpectedHex, $"points[{i}].expected_hex");
                expectedColors[i] = (true, er, eg, eb, hex);
            }
        }

        targetColors = new (bool, byte, byte, byte, string)[rCount];
        for (int i = 0; i < rCount; i++)
        {
            var reg = regions![i];
            if (!double.IsFinite(reg.X) || !double.IsFinite(reg.Y) ||
                !double.IsFinite(reg.Width) || !double.IsFinite(reg.Height))
            {
                throw new ArgumentException($"Region[{i}] x, y, width, and height must be finite numbers.");
            }
            if (reg.Width <= 0 || reg.Width > 16384 || reg.Height <= 0 || reg.Height > 16384)
            {
                throw new ArgumentException($"Region[{i}] width and height must be between 1 and 16384.");
            }
            if (reg.Tolerance < 0 || reg.Tolerance > 255)
            {
                throw new ArgumentException($"Region[{i}] tolerance must be between 0 and 255.");
            }
            if (!string.IsNullOrWhiteSpace(reg.TargetHex))
            {
                var (tr, tg, tb, hex) = ParseHexColor(reg.TargetHex, $"regions[{i}].target_hex");
                targetColors[i] = (true, tr, tg, tb, hex);
            }
        }
    }

    private static void ComputeBoundingBox(
        IReadOnlyList<PointProbeSpec> points,
        IReadOnlyList<RegionProbeSpec> regions,
        out int minX,
        out int minY,
        out int width,
        out int height)
    {
        int left = int.MaxValue;
        int top = int.MaxValue;
        int right = int.MinValue;
        int bottom = int.MinValue;

        for (int i = 0; i < points.Count; i++)
        {
            int px = (int)Math.Round(points[i].X);
            int py = (int)Math.Round(points[i].Y);
            if (px < left) left = px;
            if (py < top) top = py;
            if (px + 1 > right) right = px + 1;
            if (py + 1 > bottom) bottom = py + 1;
        }

        for (int i = 0; i < regions.Count; i++)
        {
            int rx = (int)Math.Round(regions[i].X);
            int ry = (int)Math.Round(regions[i].Y);
            int rw = Math.Max(1, (int)Math.Round(regions[i].Width));
            int rh = Math.Max(1, (int)Math.Round(regions[i].Height));
            if (rx < left) left = rx;
            if (ry < top) top = ry;
            if (rx + rw > right) right = rx + rw;
            if (ry + rh > bottom) bottom = ry + rh;
        }

        minX = left;
        minY = top;
        width = Math.Clamp(right - left, 1, 16384);
        height = Math.Clamp(bottom - top, 1, 16384);
    }

    private static unsafe VisionProbeResult EvaluateLockedBitmap(
        Bitmap bitmap,
        int bboxOriginX,
        int bboxOriginY,
        string normSpace,
        IReadOnlyList<PointProbeSpec> points,
        IReadOnlyList<RegionProbeSpec> regions,
        (bool HasColor, byte R, byte G, byte B, string Hex)[] expectedColors,
        (bool HasColor, byte R, byte G, byte B, string Hex)[] targetColors,
        Stopwatch sw)
    {
        int bmpW = bitmap.Width;
        int bmpH = bitmap.Height;
        if (bmpW <= 0 || bmpH <= 0)
        {
            throw new InvalidOperationException("Captured probe bitmap has invalid dimensions.");
        }

        var pointResults = new List<PointProbeResult>(points.Count);
        var regionResults = new List<RegionProbeResult>(regions.Count);

        var lockRect = new Rectangle(0, 0, bmpW, bmpH);
        var data = bitmap.LockBits(lockRect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte* scan0 = (byte*)data.Scan0;
            int stride = data.Stride;

            for (int i = 0; i < points.Count; i++)
            {
                var pt = points[i];
                int px = Math.Clamp((int)Math.Round(pt.X) - bboxOriginX, 0, bmpW - 1);
                int py = Math.Clamp((int)Math.Round(pt.Y) - bboxOriginY, 0, bmpH - 1);
                byte* row = scan0 + (long)py * stride;
                int off = px * 4;
                int b = row[off];
                int g = row[off + 1];
                int r = row[off + 2];

                var pr = new PointProbeResult
                {
                    Label = string.IsNullOrWhiteSpace(pt.Label) ? null : pt.Label,
                    X = pt.X,
                    Y = pt.Y,
                    R = r,
                    G = g,
                    B = b,
                    Hex = $"#{r:X2}{g:X2}{b:X2}"
                };

                var exp = expectedColors[i];
                if (exp.HasColor)
                {
                    int dr = Math.Abs(r - exp.R);
                    int dg = Math.Abs(g - exp.G);
                    int db = Math.Abs(b - exp.B);
                    int maxDiff = Math.Max(dr, Math.Max(dg, db));
                    double euclid = Math.Round(Math.Sqrt((dr * dr) + (dg * dg) + (db * db)), 2);

                    pr.ExpectedHex = exp.Hex;
                    pr.MaxChannelDiff = maxDiff;
                    pr.Distance = euclid;
                    bool withinTol = maxDiff <= pt.Tolerance;
                    pr.Matched = pt.Negate ? !withinTol : withinTol;
                }

                pointResults.Add(pr);
            }

            const ulong fnvOffsetBasis = 14695981039346656037UL;
            const ulong fnvPrime = 1099511628211UL;

            for (int i = 0; i < regions.Count; i++)
            {
                var reg = regions[i];
                int rx0 = Math.Clamp((int)Math.Round(reg.X) - bboxOriginX, 0, bmpW - 1);
                int ry0 = Math.Clamp((int)Math.Round(reg.Y) - bboxOriginY, 0, bmpH - 1);
                int rw = Math.Clamp((int)Math.Round(reg.Width), 1, bmpW - rx0);
                int rh = Math.Clamp((int)Math.Round(reg.Height), 1, bmpH - ry0);

                ulong hash = fnvOffsetBasis;
                long sumR = 0;
                long sumG = 0;
                long sumB = 0;
                int nonBlack = 0;

                var tgt = targetColors[i];
                bool hasTarget = tgt.HasColor;
                int tr = tgt.R;
                int tg = tgt.G;
                int tb = tgt.B;
                int tol = reg.Tolerance;

                int matchCount = 0;
                double matchSumX = 0.0;
                double matchSumY = 0.0;
                int coordStartX = bboxOriginX + rx0;
                int coordStartY = bboxOriginY + ry0;

                for (int y = 0; y < rh; y++)
                {
                    byte* row = scan0 + (long)(ry0 + y) * stride;
                    int pyCoord = coordStartY + y;

                    for (int x = 0; x < rw; x++)
                    {
                        int off = (rx0 + x) * 4;
                        byte b = row[off];
                        byte g = row[off + 1];
                        byte r = row[off + 2];

                        hash = (hash ^ r) * fnvPrime;
                        hash = (hash ^ g) * fnvPrime;
                        hash = (hash ^ b) * fnvPrime;

                        sumR += r;
                        sumG += g;
                        sumB += b;

                        if (r != 0 || g != 0 || b != 0)
                        {
                            nonBlack++;
                        }

                        if (hasTarget)
                        {
                            int dr = r >= tr ? r - tr : tr - r;
                            int dg = g >= tg ? g - tg : tg - g;
                            int db = b >= tb ? b - tb : tb - b;
                            int maxDiff = dr > dg ? (dr > db ? dr : db) : (dg > db ? dg : db);
                            if (maxDiff <= tol)
                            {
                                matchCount++;
                                matchSumX += coordStartX + x;
                                matchSumY += pyCoord;
                            }
                        }
                    }
                }

                int totalPixels = Math.Max(1, rw * rh);
                int avgR = (int)Math.Round((double)sumR / totalPixels);
                int avgG = (int)Math.Round((double)sumG / totalPixels);
                int avgB = (int)Math.Round((double)sumB / totalPixels);

                var rr = new RegionProbeResult
                {
                    Label = string.IsNullOrWhiteSpace(reg.Label) ? null : reg.Label,
                    X = reg.X,
                    Y = reg.Y,
                    Width = rw,
                    Height = rh,
                    Fnv1aHash = $"0x{hash:x16}",
                    AvgHex = $"#{avgR:X2}{avgG:X2}{avgB:X2}",
                    AvgR = avgR,
                    AvgG = avgG,
                    AvgB = avgB,
                    NonBlackRatio = Math.Round((double)nonBlack / totalPixels, 4)
                };

                if (hasTarget)
                {
                    rr.TargetHex = tgt.Hex;
                    rr.MatchCount = matchCount;
                    rr.MatchRatio = Math.Round((double)matchCount / totalPixels, 4);
                    if (matchCount > 0)
                    {
                        rr.CentroidX = Math.Round(matchSumX / matchCount, 2);
                        rr.CentroidY = Math.Round(matchSumY / matchCount, 2);
                    }
                }

                regionResults.Add(rr);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        sw.Stop();
        return new VisionProbeResult
        {
            Ok = true,
            CoordinateSpace = normSpace,
            ElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 3),
            Points = pointResults,
            Regions = regionResults
        };
    }

    public static ulong ComputeFnv1aHashBgra32(ReadOnlySpan<byte> bgraBuffer, int width, int height, int stride)
    {
        ulong hash = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        int rowBytes = width * 4;

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * stride;
            for (int x = 0; x < rowBytes; x += 4)
            {
                int idx = rowStart + x;
                hash = (hash ^ bgraBuffer[idx + 2]) * prime; // R
                hash = (hash ^ bgraBuffer[idx + 1]) * prime; // G
                hash = (hash ^ bgraBuffer[idx]) * prime;     // B
            }
        }

        return hash;
    }
}
