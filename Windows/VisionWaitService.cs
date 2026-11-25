using System.Diagnostics;
using System.Globalization;
using TerkirdoComputer.Core;
using TerkirdoComputer.Rest;

namespace TerkirdoComputer.Windows;

public sealed class TooManyWaitersException(string message) : InvalidOperationException(message);

public sealed class VisionWaitReactionSpec
{
    public required string ActionType { get; init; }
    public required Dictionary<string, object?> Args { get; init; }
    public bool UseMatchedCentroid { get; init; }
    public string? CentroidLabel { get; init; }
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }
}

/// <summary>
/// Pillar 1 (v2.0.0 — Tool #23 wait_for / POST /api/vision/wait):
/// Synchronous high-frequency (up to 240Hz, default 120Hz) low-level pixel/hash/centroid signal reflex engine.
/// Polls strictly OUTSIDE ActionLock (bounded to 4 concurrent waiters) and acquires ActionLock ONLY for the
/// sub-2ms in-process on_match hardware/message reaction via *Locked handlers.
/// </summary>
public static class VisionWaitService
{
    public const int MaxConcurrentWaiters = 4;
    private static readonly SemaphoreSlim WaitConcurrencyGate = new(MaxConcurrentWaiters, MaxConcurrentWaiters);

    public static VisionWaitResult WaitSync(
        AutomationService automationService,
        string? app,
        int? pid,
        string? display,
        string? coordinateSpace,
        IReadOnlyList<PointProbeSpec> points,
        IReadOnlyList<RegionProbeSpec> regions,
        int? pollHz,
        int? timeoutMs,
        string? matchMode,
        int? consecutiveHits,
        VisionWaitReactionSpec? onMatch,
        Func<string, Dictionary<string, object?>, ToolCallResult>? lockedReactionDispatcher = null,
        CancellationToken cancellationToken = default) =>
        WaitAsync(
            automationService,
            app,
            pid,
            display,
            coordinateSpace,
            points,
            regions,
            pollHz,
            timeoutMs,
            matchMode,
            consecutiveHits,
            onMatch,
            lockedReactionDispatcher,
            cancellationToken).GetAwaiter().GetResult();

    public static async Task<VisionWaitResult> WaitAsync(
        AutomationService automationService,
        string? app,
        int? pid,
        string? display,
        string? coordinateSpace,
        IReadOnlyList<PointProbeSpec> points,
        IReadOnlyList<RegionProbeSpec> regions,
        int? pollHz,
        int? timeoutMs,
        string? matchMode,
        int? consecutiveHits,
        VisionWaitReactionSpec? onMatch,
        Func<string, Dictionary<string, object?>, ToolCallResult>? lockedReactionDispatcher = null,
        CancellationToken cancellationToken = default)
    {
        var cfg = OcuConfig.GetEffectiveValues();
        int defaultPollHz = cfg.TryGetValue("vision.waitPollHz", out var phz) && phz is int iPhz ? iPhz : 120;
        int defaultTimeoutMs = cfg.TryGetValue("vision.waitTimeoutMs", out var wto) && wto is int iWto ? iWto : 5000;

        int effectivePollHz = pollHz ?? defaultPollHz;
        if (effectivePollHz < 1 || effectivePollHz > 240)
        {
            throw new ArgumentException("poll_hz must be between 1 and 240.");
        }

        int effectiveTimeoutMs = timeoutMs ?? defaultTimeoutMs;
        if (effectiveTimeoutMs < 10 || effectiveTimeoutMs > 60_000)
        {
            throw new ArgumentException("timeout_ms must be between 10 and 60000.");
        }

        string normMatchMode = string.IsNullOrWhiteSpace(matchMode) ? "all" : matchMode.Trim().ToLowerInvariant();
        if (normMatchMode is not ("all" or "any"))
        {
            throw new ArgumentException($"Invalid match_mode '{matchMode}'. Valid options are: all, any.");
        }

        int effectiveConsecutive = consecutiveHits ?? 1;
        if (effectiveConsecutive < 1 || effectiveConsecutive > 10)
        {
            throw new ArgumentException("consecutive_hits must be between 1 and 10.");
        }

        ValidateWaitPredicates(points, regions, out string?[] normalizedChangedHashes, out bool[] isInitialHashBaseline);

        if (onMatch != null)
        {
            ValidateReactionSpec(onMatch);
        }

        // Validate target app/strict PID up front so missing PID returns 404 immediately before polling
        bool hasTargetApp = !string.IsNullOrWhiteSpace(app) || pid.HasValue;
        if (hasTargetApp)
        {
            _ = automationService.ResolveApp(app, pid);
            _ = WindowManager.NormalizeCoordinateSpace(coordinateSpace, hasTargetApp: true);
        }
        else
        {
            string normSpace = WindowManager.NormalizeCoordinateSpace(coordinateSpace, hasTargetApp: false);
            if (normSpace != "screen")
            {
                throw new ArgumentException($"coordinate_space '{normSpace}' requires a target application ('app' or 'pid').");
            }
        }

        bool gateAcquired = await WaitConcurrencyGate.WaitAsync(0, cancellationToken).ConfigureAwait(false);
        if (!gateAcquired)
        {
            throw new TooManyWaitersException($"Maximum concurrent wait_for operations ({MaxConcurrentWaiters}) exceeded.");
        }

        try
        {
            double intervalMs = 1000.0 / effectivePollHz;
            double ticksPerMs = Stopwatch.Frequency / 1000.0;
            long startTicks = Stopwatch.GetTimestamp();
            long deadlineTicks = startTicks + (long)Math.Round(effectiveTimeoutMs * ticksPerMs);

            string?[] baselineHashes = new string?[regions.Count];
            for (int i = 0; i < regions.Count; i++)
            {
                baselineHashes[i] = normalizedChangedHashes[i];
            }

            int pollsExecuted = 0;
            int streak = 0;
            bool matched = false;
            VisionProbeResult? lastProbe = null;
            bool[] regionMatchedFlags = regions.Count > 0 ? new bool[regions.Count] : [];

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                lastProbe = ScreenProbe.Probe(
                    automationService,
                    app,
                    pid,
                    display,
                    coordinateSpace,
                    points,
                    regions,
                    fastNonBlocking: true);
                pollsExecuted++;

                bool pollMatched = EvaluatePollMatch(
                    lastProbe,
                    points,
                    regions,
                    isInitialHashBaseline,
                    baselineHashes,
                    pollsExecuted == 1,
                    normMatchMode,
                    regionMatchedFlags);

                streak = pollMatched ? streak + 1 : 0;
                if (streak >= effectiveConsecutive)
                {
                    matched = true;
                    break;
                }

                long nowTicks = Stopwatch.GetTimestamp();
                if (nowTicks >= deadlineTicks)
                {
                    break;
                }

                long nextTick = startTicks + (long)Math.Round(pollsExecuted * intervalMs * ticksPerMs);
                if (nextTick > deadlineTicks)
                {
                    nextTick = deadlineTicks;
                }

                await WaitUntilTimestampCancellableAsync(nextTick, cancellationToken).ConfigureAwait(false);
            }

            double totalElapsedMs = Math.Round((Stopwatch.GetTimestamp() - startTicks) / ticksPerMs, 3);
            string resolvedSpace = lastProbe?.CoordinateSpace ?? "screen";

            if (!matched)
            {
                return new VisionWaitResult
                {
                    Ok = true,
                    Matched = false,
                    TimedOut = true,
                    ElapsedMs = totalElapsedMs,
                    PollsExecuted = pollsExecuted,
                    CoordinateSpace = resolvedSpace,
                    ReactionExecuted = false,
                    Probe = lastProbe
                };
            }

            var (centroidX, centroidY) = ResolveMatchedCentroid(lastProbe!, regions, regionMatchedFlags, onMatch?.CentroidLabel);

            bool reactionExecuted = false;
            double? reactionLatencyMs = null;
            string? reactionError = null;

            if (onMatch != null && lockedReactionDispatcher != null)
            {
                var reactionArgs = PrepareReactionArgs(
                    onMatch,
                    app,
                    pid,
                    resolvedSpace,
                    centroidX,
                    centroidY,
                    out string? prepError);

                if (prepError != null)
                {
                    reactionError = prepError;
                }
                else
                {
                    long reactStartTicks = Stopwatch.GetTimestamp();
                    var reactRes = await ActionLock.ExecuteAsync(
                        () => lockedReactionDispatcher(onMatch.ActionType, reactionArgs),
                        timeoutMs: 10_000,
                        cancellationToken: cancellationToken).ConfigureAwait(false);

                    reactionLatencyMs = Math.Round((Stopwatch.GetTimestamp() - reactStartTicks) / ticksPerMs, 3);
                    if (reactRes.IsError)
                    {
                        reactionError = reactRes.Content.FirstOrDefault()?.Text ?? "on_match reaction failed.";
                    }
                    else
                    {
                        reactionExecuted = true;
                    }
                }
            }

            return new VisionWaitResult
            {
                Ok = reactionError == null,
                Matched = true,
                TimedOut = false,
                ElapsedMs = totalElapsedMs,
                PollsExecuted = pollsExecuted,
                CoordinateSpace = resolvedSpace,
                MatchedCentroidX = centroidX,
                MatchedCentroidY = centroidY,
                ReactionExecuted = reactionExecuted,
                ReactionLatencyMs = reactionLatencyMs,
                ReactionError = reactionError,
                Probe = lastProbe
            };
        }
        finally
        {
            WaitConcurrencyGate.Release();
        }
    }

    private static void ValidateWaitPredicates(
        IReadOnlyList<PointProbeSpec> points,
        IReadOnlyList<RegionProbeSpec> regions,
        out string?[] normalizedChangedHashes,
        out bool[] isInitialHashBaseline)
    {
        int pCount = points?.Count ?? 0;
        int rCount = regions?.Count ?? 0;

        if (pCount + rCount < 1)
        {
            throw new ArgumentException("wait_for requires at least one predicate in 'points' or 'regions'.");
        }

        if (pCount > ScreenProbe.MaxPoints)
        {
            throw new ArgumentException($"points count ({pCount}) exceeds maximum of {ScreenProbe.MaxPoints}.");
        }

        if (rCount > ScreenProbe.MaxRegions)
        {
            throw new ArgumentException($"regions count ({rCount}) exceeds maximum of {ScreenProbe.MaxRegions}.");
        }

        for (int i = 0; i < pCount; i++)
        {
            var pt = points![i];
            if (string.IsNullOrWhiteSpace(pt.ExpectedHex))
            {
                throw new ArgumentException($"wait_for points[{i}] requires 'expected_hex' to evaluate a match condition.");
            }
            _ = ScreenProbe.ParseHexColor(pt.ExpectedHex, $"points[{i}].expected_hex");
        }

        normalizedChangedHashes = new string?[rCount];
        isInitialHashBaseline = new bool[rCount];

        for (int i = 0; i < rCount; i++)
        {
            var reg = regions![i];
            bool hasTargetColor = !string.IsNullOrWhiteSpace(reg.TargetHex);
            if (hasTargetColor)
            {
                _ = ScreenProbe.ParseHexColor(reg.TargetHex!, $"regions[{i}].target_hex");
            }

            if (reg.MinMatchCount.HasValue && reg.MinMatchCount.Value < 0)
            {
                throw new ArgumentException($"regions[{i}].min_match_count must be >= 0.");
            }

            if (reg.MinMatchRatio.HasValue &&
                (!double.IsFinite(reg.MinMatchRatio.Value) || reg.MinMatchRatio.Value < 0.0 || reg.MinMatchRatio.Value > 1.0))
            {
                throw new ArgumentException($"regions[{i}].min_match_ratio must be between 0.0 and 1.0.");
            }

            if ((reg.MinMatchCount.HasValue || reg.MinMatchRatio.HasValue) && !hasTargetColor)
            {
                throw new ArgumentException($"regions[{i}] specifies min_match_count/min_match_ratio and therefore requires 'target_hex'.");
            }

            bool hasHashChange = !string.IsNullOrWhiteSpace(reg.ChangedFromHash);
            if (hasHashChange)
            {
                string rawHash = reg.ChangedFromHash!.Trim();
                if (string.Equals(rawHash, "initial", StringComparison.OrdinalIgnoreCase))
                {
                    isInitialHashBaseline[i] = true;
                }
                else
                {
                    string hexPart = rawHash.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? rawHash[2..] : rawHash;
                    if (hexPart.Length is < 1 or > 16 ||
                        !ulong.TryParse(hexPart, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong parsedHash))
                    {
                        throw new ArgumentException($"Invalid changed_from_hash '{reg.ChangedFromHash}' on regions[{i}]. Expected 'initial' or 64-bit hex '0x...'.");
                    }
                    normalizedChangedHashes[i] = $"0x{parsedHash:x16}";
                }
            }

            if (!hasTargetColor && !hasHashChange)
            {
                throw new ArgumentException(
                    $"wait_for regions[{i}] requires at least one condition ('target_hex' with optional min_match_count/min_match_ratio, or 'changed_from_hash').");
            }
        }
    }

    private static void ValidateReactionSpec(VisionWaitReactionSpec spec)
    {
        if (string.Equals(spec.ActionType, "wait_for", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Nested 'wait_for' inside on_match is forbidden.");
        }

        if (spec.ActionType is not ("click" or "move_mouse" or "press_key" or "drag" or "scroll" or "batch_actions" or "release_inputs"))
        {
            throw new ArgumentException(
                $"Unsupported on_match action '{spec.ActionType}'. Valid options: click, move_mouse, press_key, drag, scroll, batch_actions, release_inputs.");
        }

        if (!double.IsFinite(spec.OffsetX) || !double.IsFinite(spec.OffsetY))
        {
            throw new ArgumentException("on_match offset_x and offset_y must be finite numbers.");
        }

        if (spec.Args.TryGetValue("humanize", out var rawHum) && rawHum != null)
        {
            _ = HumanizedPhysics.ResolveProfile(rawHum);
        }
    }

    private static bool EvaluatePollMatch(
        VisionProbeResult probe,
        IReadOnlyList<PointProbeSpec> points,
        IReadOnlyList<RegionProbeSpec> regions,
        bool[] isInitialHashBaseline,
        string?[] baselineHashes,
        bool isFirstPoll,
        string normMatchMode,
        bool[] regionMatchedFlags)
    {
        bool anyTrue = false;
        bool allTrue = true;

        for (int i = 0; i < points.Count; i++)
        {
            bool ptMatch = i < probe.Points.Count && probe.Points[i].Matched == true;
            if (ptMatch) anyTrue = true;
            else allTrue = false;
        }

        for (int i = 0; i < regions.Count; i++)
        {
            var spec = regions[i];
            var res = probe.Regions[i];
            bool regMatch = true;

            if (isInitialHashBaseline[i])
            {
                if (isFirstPoll)
                {
                    baselineHashes[i] = res.Fnv1aHash;
                    regMatch = false;
                }
                else
                {
                    regMatch &= !string.Equals(res.Fnv1aHash, baselineHashes[i], StringComparison.OrdinalIgnoreCase);
                }
            }
            else if (baselineHashes[i] != null)
            {
                regMatch &= !string.Equals(res.Fnv1aHash, baselineHashes[i], StringComparison.OrdinalIgnoreCase);
            }

            if (!string.IsNullOrWhiteSpace(spec.TargetHex))
            {
                int count = res.MatchCount ?? 0;
                double ratio = res.MatchRatio ?? 0.0;

                if (spec.MinMatchCount.HasValue)
                {
                    regMatch &= count >= spec.MinMatchCount.Value;
                }
                if (spec.MinMatchRatio.HasValue)
                {
                    regMatch &= ratio >= spec.MinMatchRatio.Value;
                }
                if (!spec.MinMatchCount.HasValue && !spec.MinMatchRatio.HasValue)
                {
                    regMatch &= count >= 1;
                }
            }

            regionMatchedFlags[i] = regMatch;
            if (regMatch) anyTrue = true;
            else allTrue = false;
        }

        return normMatchMode == "any" ? anyTrue : allTrue;
    }

    private static (double? CentroidX, double? CentroidY) ResolveMatchedCentroid(
        VisionProbeResult probe,
        IReadOnlyList<RegionProbeSpec> regions,
        bool[] regionMatchedFlags,
        string? preferredLabel)
    {
        if (!string.IsNullOrWhiteSpace(preferredLabel))
        {
            for (int i = 0; i < probe.Regions.Count; i++)
            {
                if (string.Equals(probe.Regions[i].Label, preferredLabel, StringComparison.OrdinalIgnoreCase))
                {
                    var r = probe.Regions[i];
                    if (r.CentroidX.HasValue && r.CentroidY.HasValue)
                    {
                        return (r.CentroidX.Value, r.CentroidY.Value);
                    }
                    return (Math.Round(r.X + (r.Width / 2.0), 2), Math.Round(r.Y + (r.Height / 2.0), 2));
                }
            }
        }

        for (int i = 0; i < probe.Regions.Count; i++)
        {
            if (i < regionMatchedFlags.Length && regionMatchedFlags[i])
            {
                var r = probe.Regions[i];
                if (r.CentroidX.HasValue && r.CentroidY.HasValue)
                {
                    return (r.CentroidX.Value, r.CentroidY.Value);
                }
            }
        }

        for (int i = 0; i < probe.Regions.Count; i++)
        {
            if (i < regionMatchedFlags.Length && regionMatchedFlags[i])
            {
                var r = probe.Regions[i];
                return (Math.Round(r.X + (r.Width / 2.0), 2), Math.Round(r.Y + (r.Height / 2.0), 2));
            }
        }

        return (null, null);
    }

    private static Dictionary<string, object?> PrepareReactionArgs(
        VisionWaitReactionSpec spec,
        string? defaultApp,
        int? defaultPid,
        string resolvedCoordinateSpace,
        double? centroidX,
        double? centroidY,
        out string? error)
    {
        error = null;
        var args = new Dictionary<string, object?>(spec.Args, StringComparer.OrdinalIgnoreCase);
        args.Remove("type");
        args.Remove("use_matched_centroid");
        args.Remove("useMatchedCentroid");
        args.Remove("target");
        args.Remove("offset_x");
        args.Remove("offsetX");
        args.Remove("offset_y");
        args.Remove("offsetY");

        if (spec.ActionType == "press_key" &&
            args.TryGetValue("action", out var actVal) &&
            string.Equals(actVal?.ToString()?.Trim(), "press_key", StringComparison.OrdinalIgnoreCase))
        {
            args.Remove("action");
        }
        else if (spec.ActionType != "press_key")
        {
            args.Remove("action");
        }

        bool hasApp = args.TryGetValue("app", out var aVal) && !string.IsNullOrWhiteSpace(aVal?.ToString());
        bool hasPid = args.TryGetValue("pid", out var pVal) && pVal != null;
        if (!hasApp && !hasPid)
        {
            if (!string.IsNullOrWhiteSpace(defaultApp))
            {
                args["app"] = defaultApp;
            }
            if (defaultPid.HasValue)
            {
                args["pid"] = defaultPid.Value;
            }
        }

        if (!args.ContainsKey("coordinate_space") && !args.ContainsKey("coordinateSpace"))
        {
            args["coordinate_space"] = resolvedCoordinateSpace;
        }

        if (!args.ContainsKey("skip_snapshot") && !args.ContainsKey("skipSnapshot"))
        {
            args["skip_snapshot"] = true;
        }

        if (spec.UseMatchedCentroid)
        {
            if (!centroidX.HasValue || !centroidY.HasValue)
            {
                error = "on_match requested @centroid / use_matched_centroid, but no matched region centroid was available.";
                return args;
            }

            double finalX = Math.Round(centroidX.Value + spec.OffsetX, 2);
            double finalY = Math.Round(centroidY.Value + spec.OffsetY, 2);

            if (spec.ActionType == "drag")
            {
                args["to_x"] = finalX;
                args["to_y"] = finalY;
            }
            else
            {
                args["x"] = finalX;
                args["y"] = finalY;
            }
        }

        return args;
    }

    private static async Task WaitUntilTimestampCancellableAsync(long targetTicks, CancellationToken cancellationToken)
    {
        PrecisionTimer.EnsureInitialized();
        long spinThresholdTicks = (long)(Stopwatch.Frequency * 0.0020); // 2.0ms
        long asyncYieldThresholdTicks = (long)(Stopwatch.Frequency * 0.020); // 20.0ms

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long remaining = targetTicks - Stopwatch.GetTimestamp();
            if (remaining <= 0)
            {
                return;
            }

            if (remaining > asyncYieldThresholdTicks)
            {
                double remMs = remaining * 1000.0 / Stopwatch.Frequency;
                await Task.Delay(Math.Max(1, (int)(remMs - 16.0)), cancellationToken).ConfigureAwait(false);
            }
            else if (remaining > spinThresholdTicks)
            {
                PrecisionTimer.SleepOneMillisecondHighRes();
            }
            else
            {
                Thread.SpinWait(20);
            }
        }
    }
}
