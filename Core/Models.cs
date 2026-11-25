using System.Text.Json.Serialization;

namespace TerkirdoComputer.Core;

public sealed class AppDescriptor
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("bundleIdentifier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BundleIdentifier { get; set; }

    [JsonPropertyName("pid")]
    public int PID { get; set; }
}

public sealed class Frame
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("height")]
    public double Height { get; set; }

    public Frame() { }

    public Frame(double x, double y, double width, double height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public string RenderedLocalFrame() =>
        $"{{x: {Math.Round(X)}, y: {Math.Round(Y)}, width: {Math.Round(Width)}, height: {Math.Round(Height)}}}";
}

public sealed class PointOffset
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    public PointOffset() { }

    public PointOffset(double x, double y)
    {
        X = x;
        Y = y;
    }
}

public sealed class ElementRecord
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("runtimeId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int[]? RuntimeId { get; set; }

    [JsonPropertyName("automationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AutomationId { get; set; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("controlType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ControlType { get; set; }

    [JsonPropertyName("localizedControlType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LocalizedControlType { get; set; }

    [JsonPropertyName("className")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClassName { get; set; }

    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Value { get; set; }

    [JsonPropertyName("nativeWindowHandle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? NativeWindowHandle { get; set; }

    [JsonPropertyName("frame")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Frame? Frame { get; set; }

    [JsonPropertyName("actions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Actions { get; set; }
}

public sealed class AppSnapshot
{
    [JsonPropertyName("app")]
    public AppDescriptor App { get; set; } = new();

    [JsonPropertyName("windowTitle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WindowTitle { get; set; }

    [JsonPropertyName("windowHandle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? WindowHandle { get; set; }

    [JsonPropertyName("windowBounds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Frame? WindowBounds { get; set; }

    [JsonPropertyName("clientBounds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Frame? ClientBounds { get; set; }

    [JsonPropertyName("clientOffset")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PointOffset? ClientOffset { get; set; }

    [JsonPropertyName("clientOnly")]
    public bool ClientOnly { get; set; }

    [JsonPropertyName("coordinateSpace")]
    public string CoordinateSpace { get; set; } = "window";

    [JsonPropertyName("isMinimized")]
    public bool IsMinimized { get; set; }

    [JsonPropertyName("isMaximized")]
    public bool IsMaximized { get; set; }

    [JsonPropertyName("isForeground")]
    public bool IsForeground { get; set; }

    [JsonPropertyName("region")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Frame? Region { get; set; }

    [JsonPropertyName("screenshotPngBase64")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScreenshotPngBase64 { get; set; }

    [JsonPropertyName("treeLines")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? TreeLines { get; set; }

    [JsonPropertyName("focusedSummary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FocusedSummary { get; set; }

    [JsonPropertyName("selectedText")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectedText { get; set; }

    [JsonPropertyName("elements")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ElementRecord>? Elements { get; set; }

    [JsonPropertyName("screenshotOmitted")]
    public bool ScreenshotOmitted { get; set; }

    [JsonPropertyName("screenshotUnavailable")]
    public bool ScreenshotUnavailable { get; set; }

    [JsonPropertyName("accessibilityOmitted")]
    public bool AccessibilityOmitted { get; set; }

    [JsonPropertyName("screenshotMimeType")]
    public string ScreenshotMimeType { get; set; } = "image/png";

    public string RenderedText()
    {
        var appRef = string.IsNullOrEmpty(App.BundleIdentifier) ? App.Name : App.BundleIdentifier;
        var title = string.IsNullOrWhiteSpace(WindowTitle) ? App.Name : WindowTitle;

        var lines = new List<string>
        {
            $"App={appRef} (pid {App.PID})",
            $"Window: \"{title}\", App: {App.Name}."
        };

        if (ScreenshotOmitted)
        {
            lines.Add("Screenshot omitted by include_screenshot=false");
        }
        else if (ScreenshotUnavailable)
        {
            lines.Add("Screenshot unavailable. Restore a visible, non-minimized, on-screen window before using screenshot-coordinate actions; element_index actions may still work.");
        }
        else if (ClientOnly)
        {
            lines.Add("Screenshot coordinate space: client (title bar and window borders excluded).");
        }

        if (AccessibilityOmitted)
        {
            lines.Add("Accessibility omitted; previous element indexes invalidated. Read AX before element-targeted actions.");
        }

        if (TreeLines != null && TreeLines.Count > 0)
        {
            lines.AddRange(TreeLines);
        }

        if (!string.IsNullOrWhiteSpace(SelectedText))
        {
            lines.Add("");
            lines.Add($"Selected text: [{SelectedText}]");
        }
        else if (!string.IsNullOrWhiteSpace(FocusedSummary))
        {
            lines.Add("");
            lines.Add($"The focused UI element is {FocusedSummary}.");
        }

        return string.Join("\n", lines);
    }

    public ToolCallResult ToToolCallResult()
    {
        var content = new List<ContentItem>
        {
            new() { Type = "text", Text = RenderedText() }
        };

        if (!string.IsNullOrEmpty(ScreenshotPngBase64))
        {
            content.Add(new ContentItem
            {
                Type = "image",
                Data = ScreenshotPngBase64,
                MimeType = string.IsNullOrWhiteSpace(ScreenshotMimeType) ? "image/png" : ScreenshotMimeType
            });
        }

        return new ToolCallResult
        {
            Content = content,
            IsError = false
        };
    }
}

public sealed class QueryMatchRecord
{
    [JsonPropertyName("element_index")]
    public int ElementIndex { get; set; }

    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    [JsonPropertyName("label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; set; }

    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Value { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("focused")]
    public bool Focused { get; set; }

    [JsonPropertyName("selected")]
    public bool Selected { get; set; }

    [JsonPropertyName("bounds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Frame? Bounds { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; } = "0";

    [JsonPropertyName("window_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? WindowId { get; set; }

    [JsonPropertyName("actions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Actions { get; set; }
}

public sealed class QueryResult
{
    [JsonPropertyName("app")]
    public string App { get; set; } = string.Empty;

    [JsonPropertyName("pid")]
    public int PID { get; set; }

    [JsonPropertyName("bundle_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BundleId { get; set; }

    [JsonPropertyName("window_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? WindowId { get; set; }

    [JsonPropertyName("matches")]
    public List<QueryMatchRecord> Matches { get; set; } = [];

    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }

    [JsonPropertyName("stop_reason")]
    public string StopReason { get; set; } = "complete";

    [JsonPropertyName("visited_nodes")]
    public int VisitedNodes { get; set; }

    [JsonPropertyName("max_nodes")]
    public int MaxNodes { get; set; }

    [JsonPropertyName("limit")]
    public int Limit { get; set; }

    public string RenderedText()
    {
        var lines = new List<string>
        {
            $"Query: {App} (pid {PID})",
            $"Bundle ID: {BundleId ?? "unknown"}",
            $"Window ID: {(WindowId.HasValue ? WindowId.Value.ToString() : "default")}",
            $"Matches: {Matches.Count} (visited {VisitedNodes}/{MaxNodes} nodes, limit {Limit}, stop_reason={StopReason}, truncated={Truncated.ToString().ToLowerInvariant()})"
        };

        if (Matches.Count == 0)
        {
            lines.Add("(no matching accessibility elements)");
            return string.Join("\n", lines);
        }

        lines.Add("");
        foreach (var match in Matches)
        {
            var parts = new List<string> { $"[{match.ElementIndex}] {match.Role}" };
            if (!string.IsNullOrEmpty(match.Title))
            {
                parts.Add($"title=\"{match.Title}\"");
            }
            if (!string.IsNullOrEmpty(match.Label) && match.Label != match.Title)
            {
                parts.Add($"label=\"{match.Label}\"");
            }
            if (!string.IsNullOrEmpty(match.Value) && match.Value != match.Title && match.Value != match.Label)
            {
                parts.Add($"value=\"{match.Value}\"");
            }
            if (match.Bounds is not null)
            {
                parts.Add($"bounds=({(int)Math.Round(match.Bounds.X)}, {(int)Math.Round(match.Bounds.Y)}, {(int)Math.Round(match.Bounds.Width)}, {(int)Math.Round(match.Bounds.Height)})");
            }

            var flags = new List<string>();
            if (!match.Enabled) flags.Add("disabled");
            if (match.Focused) flags.Add("focused");
            if (match.Selected) flags.Add("selected");
            if (flags.Count > 0)
            {
                parts.Add($"flags={string.Join(",", flags)}");
            }

            if (match.Actions is { Count: > 0 })
            {
                parts.Add($"actions={string.Join(",", match.Actions)}");
            }

            parts.Add($"path={match.Path}");
            lines.Add(string.Join(" ", parts));
        }

        return string.Join("\n", lines);
    }

    public ToolCallResult ToToolCallResult()
    {
        return new ToolCallResult
        {
            Content =
            [
                new ContentItem { Type = "text", Text = RenderedText() }
            ],
            IsError = false
        };
    }
}

public sealed class ContentItem
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Data { get; set; }

    [JsonPropertyName("mimeType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; set; }
}

public sealed class ToolCallResult
{
    [JsonPropertyName("content")]
    public List<ContentItem> Content { get; set; } = [];

    [JsonPropertyName("isError")]
    public bool IsError { get; set; }

    public static ToolCallResult TextResult(string text, bool isError = false) =>
        new()
        {
            Content = [new ContentItem { Type = "text", Text = text }],
            IsError = isError
        };
}

public sealed class ToolDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("annotations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object?>? Annotations { get; set; }

    [JsonPropertyName("inputSchema")]
    public Dictionary<string, object?> InputSchema { get; set; } = [];
}

public sealed class ClipboardData
{
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    [JsonPropertyName("has_image")]
    public bool HasImage { get; set; }

    [JsonPropertyName("image_base64")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageBase64 { get; set; }
}

public sealed class SetClipboardResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("bytes_written")]
    public int BytesWritten { get; set; }

    [JsonPropertyName("pasted")]
    public bool Pasted { get; set; }
}

public sealed class WindowManagementResult
{
    [JsonPropertyName("app")]
    public string App { get; set; } = string.Empty;

    [JsonPropertyName("pid")]
    public int PID { get; set; }

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("bounds")]
    public Frame Bounds { get; set; } = new();
}

public sealed class DisplayDescriptor
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("is_primary")]
    public bool IsPrimary { get; set; }

    [JsonPropertyName("bounds")]
    public Frame Bounds { get; set; } = new();
}

public sealed class ScreenFrame
{
    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("base64")]
    public string Base64 { get; set; } = string.Empty;
}

public sealed class ScreenSnapshot
{
    [JsonPropertyName("display")]
    public string Display { get; set; } = "all";

    [JsonPropertyName("mime_type")]
    public string MimeType { get; set; } = "image/png";

    [JsonPropertyName("frame")]
    public ScreenFrame Frame { get; set; } = new();

    [JsonPropertyName("region")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Frame? Region { get; set; }

    [JsonPropertyName("displays")]
    public List<DisplayDescriptor> Displays { get; set; } = [];
}

public sealed class ReleaseInputsResult
{
    [JsonPropertyName("released_keys")]
    public List<string> ReleasedKeys { get; set; } = [];

    [JsonPropertyName("released_mouse_buttons")]
    public List<string> ReleasedMouseButtons { get; set; } = [];

    [JsonPropertyName("count")]
    public int Count { get; set; }
}

public sealed class BatchExecutionResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("executed_steps")]
    public int ExecutedSteps { get; set; }

    [JsonPropertyName("total_steps")]
    public int TotalSteps { get; set; }

    [JsonPropertyName("elapsed_ms")]
    public double ElapsedMs { get; set; }

    [JsonPropertyName("released_on_error")]
    public bool ReleasedOnError { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }
}

public sealed class ProcessLaunchResult
{
    [JsonPropertyName("pid")]
    public int PID { get; set; }

    [JsonPropertyName("process_name")]
    public string ProcessName { get; set; } = string.Empty;

    [JsonPropertyName("started_at")]
    public string StartedAt { get; set; } = string.Empty;
}

public sealed class ProcessCloseResult
{
    [JsonPropertyName("pid")]
    public int PID { get; set; }

    [JsonPropertyName("terminated")]
    public bool Terminated { get; set; }

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "graceful";
}

public sealed class TrajectoryWaypoint
{
    public double X { get; set; }
    public double Y { get; set; }
    public double TimeMs { get; set; }
    public string ButtonState { get; set; } = "none";
}

public sealed class TrajectoryExecutionResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; } = true;

    [JsonPropertyName("waypointsExecuted")]
    public int WaypointsExecuted { get; set; }

    [JsonPropertyName("plannedDurationMs")]
    public double PlannedDurationMs { get; set; }

    [JsonPropertyName("actualDurationMs")]
    public double ActualDurationMs { get; set; }

    [JsonPropertyName("maxDriftMs")]
    public double MaxDriftMs { get; set; }

    [JsonPropertyName("coordinateSpace")]
    public string CoordinateSpace { get; set; } = "window";

    [JsonPropertyName("method")]
    public string Method { get; set; } = "hardware";

    [JsonPropertyName("buttonHeld")]
    public bool ButtonHeld { get; set; }
}

public sealed class PointProbeSpec
{
    public string? Label { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public string? ExpectedHex { get; set; }
    public int Tolerance { get; set; } = 16;
    public bool Negate { get; set; }
}

public sealed class RegionProbeSpec
{
    public string? Label { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string? TargetHex { get; set; }
    public int Tolerance { get; set; } = 24;
    public int? MinMatchCount { get; set; }
    public double? MinMatchRatio { get; set; }
    public string? ChangedFromHash { get; set; }
}

public sealed class PointProbeResult
{
    [JsonPropertyName("label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; set; }

    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("r")]
    public int R { get; set; }

    [JsonPropertyName("g")]
    public int G { get; set; }

    [JsonPropertyName("b")]
    public int B { get; set; }

    [JsonPropertyName("hex")]
    public string Hex { get; set; } = "#000000";

    [JsonPropertyName("expectedHex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExpectedHex { get; set; }

    [JsonPropertyName("maxChannelDiff")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxChannelDiff { get; set; }

    [JsonPropertyName("distance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Distance { get; set; }

    [JsonPropertyName("matched")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Matched { get; set; }
}

public sealed class RegionProbeResult
{
    [JsonPropertyName("label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; set; }

    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("fnv1aHash")]
    public string Fnv1aHash { get; set; } = "0x0000000000000000";

    [JsonPropertyName("avgHex")]
    public string AvgHex { get; set; } = "#000000";

    [JsonPropertyName("avgR")]
    public int AvgR { get; set; }

    [JsonPropertyName("avgG")]
    public int AvgG { get; set; }

    [JsonPropertyName("avgB")]
    public int AvgB { get; set; }

    [JsonPropertyName("nonBlackRatio")]
    public double NonBlackRatio { get; set; }

    [JsonPropertyName("targetHex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetHex { get; set; }

    [JsonPropertyName("matchCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MatchCount { get; set; }

    [JsonPropertyName("matchRatio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MatchRatio { get; set; }

    [JsonPropertyName("centroidX")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? CentroidX { get; set; }

    [JsonPropertyName("centroidY")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? CentroidY { get; set; }
}

public sealed class VisionProbeResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; } = true;

    [JsonPropertyName("coordinateSpace")]
    public string CoordinateSpace { get; set; } = "screen";

    [JsonPropertyName("elapsedMs")]
    public double ElapsedMs { get; set; }

    [JsonPropertyName("points")]
    public List<PointProbeResult> Points { get; set; } = [];

    [JsonPropertyName("regions")]
    public List<RegionProbeResult> Regions { get; set; } = [];
}

public sealed class VisionWaitResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; } = true;

    [JsonPropertyName("matched")]
    public bool Matched { get; set; }

    [JsonPropertyName("timedOut")]
    public bool TimedOut { get; set; }

    [JsonPropertyName("elapsedMs")]
    public double ElapsedMs { get; set; }

    [JsonPropertyName("pollsExecuted")]
    public int PollsExecuted { get; set; }

    [JsonPropertyName("coordinateSpace")]
    public string CoordinateSpace { get; set; } = "screen";

    [JsonPropertyName("matchedCentroidX")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MatchedCentroidX { get; set; }

    [JsonPropertyName("matchedCentroidY")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MatchedCentroidY { get; set; }

    [JsonPropertyName("reactionExecuted")]
    public bool ReactionExecuted { get; set; }

    [JsonPropertyName("reactionLatencyMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ReactionLatencyMs { get; set; }

    [JsonPropertyName("reactionError")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReactionError { get; set; }

    [JsonPropertyName("probe")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VisionProbeResult? Probe { get; set; }
}

public sealed class SharedFrameStatusResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; } = true;

    [JsonPropertyName("active")]
    public bool Active { get; set; }

    [JsonPropertyName("mapName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MapName { get; set; }

    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("headerBytes")]
    public int HeaderBytes { get; set; } = 64;

    [JsonPropertyName("maxSlotBytes")]
    public uint MaxSlotBytes { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("stride")]
    public int Stride { get; set; }

    [JsonPropertyName("pixelFormat")]
    public string PixelFormat { get; set; } = "bgra32";

    [JsonPropertyName("maxFps")]
    public int MaxFps { get; set; }

    [JsonPropertyName("framesPublished")]
    public ulong FramesPublished { get; set; }

    [JsonPropertyName("coordinateSpace")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CoordinateSpace { get; set; }

    [JsonPropertyName("ownerPid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OwnerPid { get; set; }
}
