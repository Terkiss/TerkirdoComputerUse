using System.Globalization;
using System.Text.Json;
using TerkirdoComputer.Core;

namespace TerkirdoComputer.Windows;

public enum HumanizeProfile
{
    None = 0,
    Subtle = 1,
    Natural = 2,
    Fast = 3
}

/// <summary>
/// Biomechanical input kinematics engine implementing Flash &amp; Hogan (1985) 5th-order Minimum-Jerk
/// time-warping (10t^3 - 15t^4 + 6t^5), monitor-clamped convex-hull Cubic Bezier spatial arcs with
/// exact 0.0px terminal target accuracy, telescoping zero-drift relative 3D camera quantization, and
/// truncated [-2.5s, +2.5s] Box-Muller Gaussian dwell/interval jitter.
/// </summary>
public static class HumanizedPhysics
{
    private static readonly object SeedLock = new();
    private static int? _cachedConfigSeed;
    private static Random? _seededRandom;

    public static string ToCanonicalName(HumanizeProfile profile) => profile switch
    {
        HumanizeProfile.Subtle => "subtle",
        HumanizeProfile.Natural => "natural",
        HumanizeProfile.Fast => "fast",
        _ => "none"
    };

    /// <summary>
    /// Resolves effective HumanizeProfile from per-call argument (bool or string) and OcuConfig fallback.
    /// Note: follow_trajectory (isTrajectoryTool: true) defaults strictly to None unless humanize is explicitly supplied per-call.
    /// </summary>
    public static HumanizeProfile ResolveProfile(object? rawArg, bool isTrajectoryTool = false)
    {
        if (rawArg is null)
        {
            if (isTrajectoryTool)
            {
                return HumanizeProfile.None;
            }

            var cfg = OcuConfig.GetEffectiveValues();
            if (cfg.TryGetValue("input.humanize", out var cfgVal) && cfgVal is string cfgStr)
            {
                return ParseExplicitProfileString(cfgStr);
            }
            return HumanizeProfile.None;
        }

        if (rawArg is HumanizeProfile hp)
        {
            return hp;
        }

        if (rawArg is bool b)
        {
            return b ? HumanizeProfile.Natural : HumanizeProfile.None;
        }

        if (rawArg is JsonElement je)
        {
            return je.ValueKind switch
            {
                JsonValueKind.True => HumanizeProfile.Natural,
                JsonValueKind.False => HumanizeProfile.None,
                JsonValueKind.Null or JsonValueKind.Undefined => isTrajectoryTool ? HumanizeProfile.None : ResolveProfile(null, false),
                JsonValueKind.String => ParseExplicitProfileString(je.GetString()),
                _ => throw new ArgumentException($"Invalid humanize value '{je}'. Expected boolean or one of: none, subtle, natural, fast.")
            };
        }

        string str = Convert.ToString(rawArg, CultureInfo.InvariantCulture) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(str))
        {
            return isTrajectoryTool ? HumanizeProfile.None : ResolveProfile(null, false);
        }

        return ParseExplicitProfileString(str);
    }

    public static HumanizeProfile ParseExplicitProfileString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return HumanizeProfile.None;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "none" or "false" or "0" or "off" => HumanizeProfile.None,
            "subtle" => HumanizeProfile.Subtle,
            "natural" or "true" or "1" or "on" => HumanizeProfile.Natural,
            "fast" => HumanizeProfile.Fast,
            _ => throw new ArgumentException($"Invalid humanize profile '{raw}'. Valid options are: none, subtle, natural, fast (or true/false).")
        };
    }

    /// <summary>
    /// Evaluates Flash &amp; Hogan (1985) normalized 5th-order Minimum-Jerk progression u(tau) = 10*tau^3 - 15*tau^4 + 6*tau^5.
    /// Exact boundary conditions: u(0) = 0, u(1) = 1, u'(0) = u'(1) = 0, u''(0) = u''(1) = 0, u'(0.5) = 1.875.
    /// </summary>
    public static double EvaluateMinimumJerk(double tau)
    {
        if (tau <= 0.0) return 0.0;
        if (tau >= 1.0) return 1.0;
        double t3 = tau * tau * tau;
        return t3 * (10.0 + tau * (-15.0 + 6.0 * tau));
    }

    /// <summary>
    /// Evaluates 1D Cubic Bezier B(u) = (1-u)^3*p0 + 3*(1-u)^2*u*p1 + 3*(1-u)*u^2*p2 + u^3*p3.
    /// At u == 1.0, returns p3 identically without floating-point accumulation error.
    /// </summary>
    public static double EvaluateCubicBezier(double u, double p0, double p1, double p2, double p3)
    {
        if (u <= 0.0) return p0;
        if (u >= 1.0) return p3;
        double inv = 1.0 - u;
        double inv2 = inv * inv;
        double u2 = u * u;
        return (inv2 * inv * p0) + (3.0 * inv2 * u * p1) + (3.0 * inv * u2 * p2) + (u2 * u * p3);
    }

    /// <summary>
    /// Computes Fitts's Law movement duration (ms) when explicit duration_ms is 0 or omitted.
    /// </summary>
    public static double ComputeAutoDurationMs(HumanizeProfile profile, double distancePx, Random? rng = null)
    {
        if (profile == HumanizeProfile.None || distancePx <= 1.0)
        {
            return 0.0;
        }

        (double baseMs, double slopeMs, double minMs, double maxMs) = profile switch
        {
            HumanizeProfile.Fast => (24.0, 14.0, 36.0, 125.0),
            HumanizeProfile.Subtle => (34.0, 19.0, 50.0, 180.0),
            _ => (48.0, 28.0, 75.0, 300.0) // Natural
        };

        double fitts = baseMs + slopeMs * Math.Log2(1.0 + (distancePx / 12.0));
        double mean = Math.Clamp(fitts, minMs, maxMs);
        double std = mean * 0.06;
        return SampleTruncatedGaussian(mean, std, minMs, maxMs, rng);
    }

    /// <summary>
    /// Resolves the containing monitor rectangle when both start and end points reside on the same display,
    /// allowing Bezier control points P1/P2 to be clamped inside the monitor's convex hull so arcs never bleed
    /// onto adjacent monitors.
    /// </summary>
    public static (int Left, int Top, int Right, int Bottom)? ResolveContainingMonitorBounds(
        int startX,
        int startY,
        int endX,
        int endY)
    {
        try
        {
            var displays = ScreenCapture.GetDisplays();
            foreach (var d in displays)
            {
                int left = (int)Math.Round(d.Bounds.X);
                int top = (int)Math.Round(d.Bounds.Y);
                int right = left + (int)Math.Round(d.Bounds.Width);
                int bottom = top + (int)Math.Round(d.Bounds.Height);

                if (startX >= left && startX < right && startY >= top && startY < bottom &&
                    endX >= left && endX < right && endY >= top && endY < bottom)
                {
                    return (left, top, right, bottom);
                }
            }
        }
        catch
        {
            // Fall back to unclamped if display enumeration is unavailable
        }

        return null;
    }

    /// <summary>
    /// Generates absolute screen-space waypoints (X, Y, TimeMs) from (startX, startY) to (targetX, targetY).
    /// Guarantees final waypoint == (targetX, targetY) with 0.0px error and clamps Bezier control points P1, P2
    /// inside the containing monitor bounds so the convex hull never crosses monitor edges.
    /// </summary>
    public static (int X, int Y, double TimeMs)[] GenerateAbsoluteSteps(
        int startX,
        int startY,
        int targetX,
        int targetY,
        HumanizeProfile profile,
        int? requestedDurationMs = null,
        int? requestedSteps = null,
        (int Left, int Top, int Right, int Bottom)? monitorBoundsOverride = null,
        Random? rng = null)
    {
        double dx = targetX - startX;
        double dy = targetY - startY;
        double dist = Math.Sqrt((dx * dx) + (dy * dy));

        if (profile == HumanizeProfile.None)
        {
            int linearSteps = Math.Clamp(requestedSteps ?? 1, 1, 240);
            double totalMs = Math.Max(0.0, requestedDurationMs ?? 0);
            var linear = new (int X, int Y, double TimeMs)[linearSteps];
            for (int i = 1; i <= linearSteps; i++)
            {
                int lx = i == linearSteps ? targetX : (int)Math.Round(startX + (dx * i / linearSteps));
                int ly = i == linearSteps ? targetY : (int)Math.Round(startY + (dy * i / linearSteps));
                double lt = totalMs * i / linearSteps;
                linear[i - 1] = (lx, ly, lt);
            }
            return linear;
        }

        Random r = rng ?? GetRandom();
        double durationMs = requestedDurationMs is > 0
            ? requestedDurationMs.Value
            : ComputeAutoDurationMs(profile, dist, r);

        if (dist < 1.0)
        {
            return [(targetX, targetY, durationMs)];
        }

        int stepCount = requestedSteps is > 1
            ? Math.Clamp(requestedSteps.Value, 2, 240)
            : Math.Clamp((int)Math.Ceiling(Math.Max(durationMs, 24.0) / 8.3333), 6, 180);

        var (p1x, p1y, p2x, p2y) = BuildBezierControlPoints(
            startX,
            startY,
            targetX,
            targetY,
            dist,
            profile,
            monitorBoundsOverride ?? ResolveContainingMonitorBounds(startX, startY, targetX, targetY),
            r);

        var waypoints = new (int X, int Y, double TimeMs)[stepCount];
        for (int i = 1; i <= stepCount; i++)
        {
            if (i == stepCount)
            {
                waypoints[i - 1] = (targetX, targetY, durationMs);
                continue;
            }

            double tau = (double)i / stepCount;
            double u = EvaluateMinimumJerk(tau);
            int bx = (int)Math.Round(EvaluateCubicBezier(u, startX, p1x, p2x, targetX));
            int by = (int)Math.Round(EvaluateCubicBezier(u, startY, p1y, p2y, targetY));
            waypoints[i - 1] = (bx, by, durationMs * tau);
        }

        return waypoints;
    }

    /// <summary>
    /// Generates humanized relative 3D camera rotation steps (MOUSEEVENTF_MOVE) using telescoping
    /// cumulative quantization so sum(Dx) == round(deltaX) and sum(Dy) == round(deltaY) with 0 drift.
    /// </summary>
    public static (int Dx, int Dy)[] GenerateRelativeSteps(
        double deltaX,
        double deltaY,
        HumanizeProfile profile,
        int? requestedSteps = null,
        Random? rng = null)
    {
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY))
        {
            throw new ArgumentException("delta_x and delta_y must be finite numbers.");
        }

        int steps = Math.Clamp(requestedSteps ?? (profile == HumanizeProfile.None ? 1 : 16), 1, 240);
        if (profile == HumanizeProfile.None || steps == 1)
        {
            return InputInjector.ComputeRelativeSteps(deltaX, deltaY, steps);
        }

        int totalDx = (int)Math.Round(deltaX);
        int totalDy = (int)Math.Round(deltaY);
        double dist = Math.Sqrt(((double)totalDx * totalDx) + ((double)totalDy * totalDy));
        if (dist < 1.0)
        {
            return new (int Dx, int Dy)[steps];
        }

        Random r = rng ?? GetRandom();
        var (p1x, p1y, p2x, p2y) = BuildBezierControlPoints(
            0.0,
            0.0,
            totalDx,
            totalDy,
            dist,
            profile,
            monitorBounds: null,
            r);

        var deltas = new (int Dx, int Dy)[steps];
        int emittedX = 0;
        int emittedY = 0;

        for (int i = 1; i <= steps; i++)
        {
            if (i == steps)
            {
                deltas[i - 1] = (totalDx - emittedX, totalDy - emittedY);
                emittedX = totalDx;
                emittedY = totalDy;
                continue;
            }

            double tau = (double)i / steps;
            double u = EvaluateMinimumJerk(tau);
            int cumX = (int)Math.Round(EvaluateCubicBezier(u, 0.0, p1x, p2x, totalDx));
            int cumY = (int)Math.Round(EvaluateCubicBezier(u, 0.0, p1y, p2y, totalDy));
            deltas[i - 1] = (cumX - emittedX, cumY - emittedY);
            emittedX = cumX;
            emittedY = cumY;
        }

        return deltas;
    }

    /// <summary>
    /// Samples a truncated Box-Muller Gaussian variable clamped strictly within [mean - 2.5*stdDev, mean + 2.5*stdDev]
    /// and intersected with [minFloor, maxCeiling].
    /// </summary>
    public static double SampleTruncatedGaussian(
        double mean,
        double stdDev,
        double minFloor,
        double maxCeiling,
        Random? rng = null)
    {
        if (maxCeiling < minFloor)
        {
            maxCeiling = minFloor;
        }

        if (stdDev <= 0.0)
        {
            return Math.Clamp(mean, minFloor, maxCeiling);
        }

        Random r = rng ?? GetRandom();
        double u1;
        double u2;
        lock (r)
        {
            u1 = Math.Clamp(1.0 - r.NextDouble(), 1e-12, 1.0);
            u2 = r.NextDouble();
        }

        double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        double clampedZ = Math.Clamp(z, -2.5, 2.5);
        double sample = mean + (clampedZ * stdDev);
        return Math.Clamp(sample, minFloor, maxCeiling);
    }

    public static double SampleHoldDurationMs(
        HumanizeProfile profile,
        int? explicitHoldMs,
        bool isHardwareMode,
        Random? rng = null)
    {
        double floor = isHardwareMode ? 16.0 : 2.0;
        if (profile == HumanizeProfile.None)
        {
            if (explicitHoldMs.HasValue)
            {
                return explicitHoldMs.Value;
            }
            return isHardwareMode ? 18.0 : 0.0;
        }

        double defaultMean = profile switch
        {
            HumanizeProfile.Fast => isHardwareMode ? 22.0 : 8.0,
            HumanizeProfile.Subtle => isHardwareMode ? 30.0 : 12.0,
            _ => isHardwareMode ? 46.0 : 18.0 // Natural
        };

        double mean = explicitHoldMs.HasValue && explicitHoldMs.Value > 0
            ? Math.Max(floor, explicitHoldMs.Value)
            : defaultMean;

        double sigmaRatio = profile switch
        {
            HumanizeProfile.Fast => 0.10,
            HumanizeProfile.Subtle => 0.12,
            _ => 0.18
        };

        double maxCap = Math.Max(mean * 1.6, floor + 20.0);
        return SampleTruncatedGaussian(mean, mean * sigmaRatio, floor, Math.Min(60_000.0, maxCap), rng);
    }

    public static double SampleInterKeyDelayMs(
        HumanizeProfile profile,
        double baseDelayMs = 18.0,
        Random? rng = null)
    {
        if (profile == HumanizeProfile.None)
        {
            return baseDelayMs;
        }

        (double mean, double sigma, double minFloor, double maxCap) = profile switch
        {
            HumanizeProfile.Fast => (Math.Max(6.0, baseDelayMs * 0.55), 2.5, 4.0, 35.0),
            HumanizeProfile.Subtle => (Math.Max(10.0, baseDelayMs * 0.85), 4.0, 6.0, 55.0),
            _ => (Math.Max(18.0, baseDelayMs * 1.35), 7.5, 10.0, 95.0)
        };

        return SampleTruncatedGaussian(mean, sigma, minFloor, maxCap, rng);
    }

    private static (double P1X, double P1Y, double P2X, double P2Y) BuildBezierControlPoints(
        double startX,
        double startY,
        double endX,
        double endY,
        double dist,
        HumanizeProfile profile,
        (int Left, int Top, int Right, int Bottom)? monitorBounds,
        Random rng)
    {
        double dx = endX - startX;
        double dy = endY - startY;

        double alpha1;
        double alpha2;
        double sideSign;
        double ampScale;
        double eta;

        lock (rng)
        {
            alpha1 = 0.25 + (rng.NextDouble() * 0.13); // [0.25, 0.38]
            alpha2 = 0.65 + (rng.NextDouble() * 0.15); // [0.65, 0.80]
            sideSign = rng.Next(0, 2) == 0 ? -1.0 : 1.0;
            ampScale = 0.55 + (rng.NextDouble() * 0.45); // [0.55, 1.00] ensures visible natural curvature > 0px
            eta = 0.45 + (rng.NextDouble() * 0.50);      // [0.45, 0.95]
        }

        (double kappa, double maxArcPx) = profile switch
        {
            HumanizeProfile.Subtle => (0.022, 6.0),
            HumanizeProfile.Fast => (0.032, 10.0),
            _ => (0.055, 22.0) // Natural
        };

        double h = dist < 4.0 ? 0.0 : sideSign * Math.Min(kappa * dist, maxArcPx) * ampScale;
        double nx = -dy / dist;
        double ny = dx / dist;

        double p1x = startX + (alpha1 * dx) + (h * nx);
        double p1y = startY + (alpha1 * dy) + (h * ny);
        double p2x = startX + (alpha2 * dx) + (eta * h * nx);
        double p2y = startY + (alpha2 * dy) + (eta * h * ny);

        if (monitorBounds.HasValue)
        {
            var m = monitorBounds.Value;
            double minX = Math.Min(startX, Math.Min(endX, m.Left + 1));
            double maxX = Math.Max(startX, Math.Max(endX, m.Right - 2));
            // Strictly enforce containing monitor inner bounds [Left, Right - 1] x [Top, Bottom - 1]
            double clampLeft = Math.Max(m.Left, Math.Min(m.Left + 1, minX));
            double clampRight = Math.Min(m.Right - 1, Math.Max(m.Right - 2, maxX));
            double minY = Math.Min(startY, Math.Min(endY, m.Top + 1));
            double maxY = Math.Max(startY, Math.Max(endY, m.Bottom - 2));
            double clampTop = Math.Max(m.Top, Math.Min(m.Top + 1, minY));
            double clampBottom = Math.Min(m.Bottom - 1, Math.Max(m.Bottom - 2, maxY));

            // If adding lateral arc towards the near monitor edge got clamped flat, flip arc inward into the monitor!
            if (p1x < clampLeft || p1x > clampRight || p1y < clampTop || p1y > clampBottom ||
                p2x < clampLeft || p2x > clampRight || p2y < clampTop || p2y > clampBottom)
            {
                double flippedH = -h;
                double fp1x = startX + (alpha1 * dx) + (flippedH * nx);
                double fp1y = startY + (alpha1 * dy) + (flippedH * ny);
                double fp2x = startX + (alpha2 * dx) + (eta * flippedH * nx);
                double fp2y = startY + (alpha2 * dy) + (eta * flippedH * ny);

                if (fp1x >= clampLeft && fp1x <= clampRight && fp1y >= clampTop && fp1y <= clampBottom &&
                    fp2x >= clampLeft && fp2x <= clampRight && fp2y >= clampTop && fp2y <= clampBottom)
                {
                    p1x = fp1x;
                    p1y = fp1y;
                    p2x = fp2x;
                    p2y = fp2y;
                }
            }

            p1x = Math.Clamp(p1x, clampLeft, clampRight);
            p1y = Math.Clamp(p1y, clampTop, clampBottom);
            p2x = Math.Clamp(p2x, clampLeft, clampRight);
            p2y = Math.Clamp(p2y, clampTop, clampBottom);
        }

        return (p1x, p1y, p2x, p2y);
    }

    private static Random GetRandom()
    {
        var cfg = OcuConfig.GetEffectiveValues();
        if (cfg.TryGetValue("input.humanizeSeed", out var seedObj) && seedObj is int seed)
        {
            lock (SeedLock)
            {
                if (_seededRandom == null || _cachedConfigSeed != seed)
                {
                    _cachedConfigSeed = seed;
                    _seededRandom = new Random(seed);
                }
                return _seededRandom;
            }
        }

        return Random.Shared;
    }
}
