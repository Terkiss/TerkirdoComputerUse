using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerkirdoComputer.Core;
using TerkirdoComputer.Rest;
using TerkirdoComputer.Windows;

namespace TerkirdoComputer.Mcp;

public sealed class ToolHandlers
{
    private readonly AutomationService _automationService;

    public ToolHandlers(AutomationService automationService)
    {
        _automationService = automationService;
    }

    public static List<ToolDefinition> GetToolDefinitions()
    {
        var defaultAnnot = new Dictionary<string, object?> { ["destructiveHint"] = false, ["openWorldHint"] = false };
        var readOnlyAnnot = new Dictionary<string, object?>
        {
            ["destructiveHint"] = false,
            ["idempotentHint"] = true,
            ["openWorldHint"] = false,
            ["readOnlyHint"] = true
        };

        const string appDesc = "App name, window title, numeric PID, or strict 'pid:<id>' (e.g. 'pid:12345')";
        const string pidDesc = "Optional explicit strict PID of the target process (never falls back to sibling processes).";

        return
        [
            new ToolDefinition
            {
                Name = "click",
                Description = "Click, hold down, or release a mouse button on an element by index or pixel coordinates. Supports coordinate_space ('window', 'client', 'screen'), game-grade hold_ms, and stateful down/up. This tool is part of plugin `Computer Use`.",
                Annotations = defaultAnnot,
                InputSchema = ObjectSchema(new()
                {
                    ["app"] = StringProperty(appDesc),
                    ["pid"] = PositiveIntegerProperty(pidDesc),
                    ["element_index"] = StringProperty("Element index to click"),
                    ["x"] = NumberProperty("X coordinate in pixel coordinates"),
                    ["y"] = NumberProperty("Y coordinate in pixel coordinates"),
                    ["coordinate_space"] = EnumStringProperty("Coordinate reference frame: 'window' (default unless client_only snapshot is active), 'client' (pure game canvas excluding title bar and DWM borders), or 'screen' (absolute virtual desktop).", ["window", "client", "screen"]),
                    ["click_count"] = IntegerProperty("Number of clicks. Defaults to 1"),
                    ["mouse_button"] = EnumStringProperty("Mouse button to click. Defaults to left.", ["left", "right", "middle"]),
                    ["button_state"] = EnumStringProperty("Mouse button state transition: click (default), down (hold with auto-release safety TTL), or up (release).", ["click", "down", "up"]),
                    ["hold_ms"] = BoundedIntegerProperty("Hold duration in ms for click, or max auto-release safety TTL in ms when button_state is 'down' (0..60000).", 0, 60000),
                    ["click_method"] = EnumStringProperty(
                        "Click implementation: auto (default), accessibility, app_post, stealth, sky_click, global, or hardware. Accessibility requires element_index. Windows supports app_post (100% background HWND messages with activation spoofing), stealth (~40ms micro-swap & focus/cursor restore for games), and global/hardware for DirectInput/DirectX games.",
                        ["auto", "accessibility", "app_post", "stealth", "sky_click", "global", "hardware"]),
                    ["humanize"] = HumanizeProperty("Optional biomechanical humanization profile: 'none' (default), 'subtle', 'natural', 'fast', or boolean true/false."),
                    ["skip_snapshot"] = BooleanProperty("If true, skips post-action UIA tree rebuild and screenshot capture for fast batching. Also allows coordinate-only clicks without a prior get_app_state call. Defaults to false.")
                }, ["app"])
            },
            new ToolDefinition
            {
                Name = "move_mouse",
                Description = "Move the mouse cursor to window/client/screen coordinates (x, y) for hover detection, or inject pure RawInput relative motion (delta_x, delta_y) via SendInput(MOUSEEVENTF_MOVE) for 3D FPS/TPS/RPG camera rotation. Defaults to skip_snapshot=true. This tool is part of plugin `Computer Use`.",
                Annotations = defaultAnnot,
                InputSchema = ObjectSchema(new()
                {
                    ["app"] = StringProperty("Optional app name, window title, numeric PID, or 'pid:<id>' (required for window/client x/y or app_post; optional for 3D camera delta_x/delta_y)."),
                    ["pid"] = PositiveIntegerProperty(pidDesc),
                    ["x"] = NumberProperty("Target X coordinate (window/client-relative when app/pid is specified, otherwise screen coordinate)."),
                    ["y"] = NumberProperty("Target Y coordinate (window/client-relative when app/pid is specified, otherwise screen coordinate)."),
                    ["coordinate_space"] = EnumStringProperty("Coordinate reference frame for absolute x/y: 'window', 'client', or 'screen'.", ["window", "client", "screen"]),
                    ["delta_x"] = NumberProperty("Relative horizontal motion in pixels (SendInput MOUSEEVENTF_MOVE without MOUSEEVENTF_ABSOLUTE for 3D camera rotation)."),
                    ["delta_y"] = NumberProperty("Relative vertical motion in pixels (SendInput MOUSEEVENTF_MOVE without MOUSEEVENTF_ABSOLUTE for 3D camera rotation)."),
                    ["steps"] = BoundedIntegerProperty("Number of smooth error-accumulating interpolation steps (1..240). Defaults to 1.", 1, 240),
                    ["duration_ms"] = BoundedIntegerProperty("Total interpolation duration in milliseconds (0..5000). Defaults to 0.", 0, 5000),
                    ["method"] = EnumStringProperty("Movement delivery method: hardware (default for delta_x/delta_y), global, auto, stealth, or app_post.", ["hardware", "global", "auto", "stealth", "app_post"]),
                    ["humanize"] = HumanizeProperty("Optional biomechanical humanization profile (Minimum-Jerk + monitor-clamped Cubic Bezier, 0px final error): 'none', 'subtle', 'natural', 'fast', or boolean."),
                    ["skip_snapshot"] = BooleanProperty("Skips post-action UIA tree rebuild and screenshot capture. Defaults to true for sub-millisecond movement.")
                }, [])
            },
            new ToolDefinition
            {
                Name = "follow_trajectory",
                Description = "Play a continuous 60Hz-120Hz cursor trajectory (1..2400 timed waypoints up to 60s) scheduled against a monotonic QPC epoch with zero cumulative drift and optional hold_button auto-release safety. Designed for real-time tracking, rhythm/QTE paths, and anti-macro moving targets. This tool is part of plugin `Computer Use`.",
                Annotations = defaultAnnot,
                InputSchema = ObjectSchema(new()
                {
                    ["app"] = StringProperty("Optional target app name, window title, numeric PID, or 'pid:<id>' (required for 'client'/'window' coordinate_space or 'app_post'/'stealth' method)."),
                    ["pid"] = PositiveIntegerProperty(pidDesc),
                    ["waypoints"] = new Dictionary<string, object?>
                    {
                        ["type"] = "array",
                        ["minItems"] = 1,
                        ["maxItems"] = 2400,
                        ["description"] = "Ordered list of waypoints. Each item may be {x, y, t_ms?, button_state?} or [x, y] / [x, y, t_ms].",
                        ["items"] = new Dictionary<string, object?> { ["additionalProperties"] = true }
                    },
                    ["interval_ms"] = NumberProperty("Default cadence in milliseconds when waypoints omit t_ms (0..5000). Defaults to 16.6667 (60Hz)."),
                    ["coordinate_space"] = EnumStringProperty("Coordinate reference frame: 'window', 'client', or 'screen'.", ["window", "client", "screen"]),
                    ["method"] = EnumStringProperty("Delivery method: hardware (default), global, stealth, app_post, or auto.", ["hardware", "global", "stealth", "app_post", "auto"]),
                    ["mouse_button"] = EnumStringProperty("Mouse button when hold_button or per-waypoint button_state is used. Defaults to left.", ["left", "right", "middle"]),
                    ["hold_button"] = BooleanProperty("If true, holds mouse_button down at waypoint 0 and guarantees release at completion or error. Defaults to false."),
                    ["humanize"] = HumanizeProperty("Explicit per-call humanize profile ('none' default; does not inherit global config unless specified here)."),
                    ["skip_snapshot"] = BooleanProperty("Skips post-trajectory UIA/screenshot rebuild. Defaults to true.")
                }, ["waypoints"])
            },
            new ToolDefinition
            {
                Name = "probe_screen",
                Description = "Sub-millisecond UIA-less game perception sensor. Captures a single tight bounding box without PNG/JPEG/Base64 encoding and computes RGB point matches, 64-bit FNV-1a region hashes, color match ratios, and matching-pixel center-of-mass (centroidX, centroidY) in the requested coordinate_space. This tool is part of plugin `Computer Use`.",
                Annotations = readOnlyAnnot,
                InputSchema = ObjectSchema(new()
                {
                    ["app"] = StringProperty("Optional target app name, window title, numeric PID, or 'pid:<id>'. Omit to probe a display directly."),
                    ["pid"] = PositiveIntegerProperty(pidDesc),
                    ["display"] = StringProperty("Display index ('1', '2', ...) or 'all' when app/pid is omitted. Defaults to '1'."),
                    ["coordinate_space"] = EnumStringProperty("Coordinate reference frame: 'client', 'window', or 'screen'.", ["client", "window", "screen"]),
                    ["points"] = new Dictionary<string, object?>
                    {
                        ["type"] = "array",
                        ["maxItems"] = 256,
                        ["description"] = "Up to 256 pixel sample points: {x, y, label?, expected_hex?, tolerance? (0..255, default 16), negate?}.",
                        ["items"] = new Dictionary<string, object?> { ["type"] = "object", ["additionalProperties"] = true }
                    },
                    ["regions"] = new Dictionary<string, object?>
                    {
                        ["type"] = "array",
                        ["maxItems"] = 32,
                        ["description"] = "Up to 32 rectangular regions: {x, y, width, height, label?, target_hex?, tolerance? (0..255, default 24)} returning fnv1aHash, matchRatio, and centroidX/centroidY.",
                        ["items"] = new Dictionary<string, object?> { ["type"] = "object", ["additionalProperties"] = true }
                    }
                }, [])
            },
            new ToolDefinition
            {
                Name = "wait_for",
                Description = "Synchronous 120Hz low-level pixel/hash/centroid signal reflex engine. Polls point colors, 64-bit FNV-1a region hash changes ('initial' or '0x...'), or target-color match thresholds outside ActionLock and optionally fires an immediate (<2ms) in-process on_match reaction (supporting @centroid / @centroid:<label> coordinate binding). Returns matched:false, timedOut:true without error on normal timeout. This tool is part of plugin `Computer Use`.",
                Annotations = defaultAnnot,
                InputSchema = ObjectSchema(new()
                {
                    ["app"] = StringProperty("Optional target app name, window title, numeric PID, or 'pid:<id>'."),
                    ["pid"] = PositiveIntegerProperty(pidDesc),
                    ["display"] = StringProperty("Display index ('1', '2', ...) or 'all' when app/pid is omitted. Defaults to '1'."),
                    ["coordinate_space"] = EnumStringProperty("Coordinate reference frame: 'client', 'window', or 'screen'.", ["client", "window", "screen"]),
                    ["points"] = new Dictionary<string, object?>
                    {
                        ["type"] = "array",
                        ["maxItems"] = 256,
                        ["description"] = "Pixel conditions: {x, y, expected_hex, tolerance? (0..255), negate?, label?}.",
                        ["items"] = new Dictionary<string, object?> { ["type"] = "object", ["additionalProperties"] = true }
                    },
                    ["regions"] = new Dictionary<string, object?>
                    {
                        ["type"] = "array",
                        ["maxItems"] = 32,
                        ["description"] = "Region conditions: {x, y, width, height, label?, target_hex?, tolerance?, min_match_count?, min_match_ratio?, changed_from_hash? ('initial' or '0x...')}.",
                        ["items"] = new Dictionary<string, object?> { ["type"] = "object", ["additionalProperties"] = true }
                    },
                    ["poll_hz"] = BoundedIntegerProperty("Polling frequency in Hz (1..240). Defaults to 120.", 1, 240),
                    ["timeout_ms"] = BoundedIntegerProperty("Maximum wait duration in milliseconds (10..60000). Defaults to 5000.", 10, 60000),
                    ["match_mode"] = EnumStringProperty("Logical aggregation across predicates: 'all' (default) or 'any'.", ["all", "any"]),
                    ["consecutive_hits"] = BoundedIntegerProperty("Consecutive matching polls required before triggering (1..10). Defaults to 1.", 1, 10),
                    ["on_match"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["description"] = "Optional immediate (<2ms) reaction on match: {type: 'click'|'move_mouse'|'press_key'|'drag'|'scroll'|'batch_actions'|'release_inputs', target?: '@centroid'|'@centroid:<label>', use_matched_centroid?: bool, offset_x?: number, offset_y?: number, ...}.",
                        ["additionalProperties"] = true
                    }
                }, [])
            },
            new ToolDefinition
            {
                Name = "manage_shared_frame",
                Description = "Start, stop, or inspect the Windows Local\\ zero-copy MemoryMappedFile BGRA32 frame publisher ('TKSM' 64-byte Per-Slot + Global Seqlock Double-Buffer) for <0.05ms Python mmap/numpy and C# real-time vision loops. This tool is part of plugin `Computer Use`.",
                Annotations = defaultAnnot,
                InputSchema = ObjectSchema(new()
                {
                    ["action"] = EnumStringProperty("Stream lifecycle command: 'start', 'stop', or 'status'.", ["start", "stop", "status"]),
                    ["app"] = StringProperty("Optional target app name, window title, numeric PID, or 'pid:<id>'."),
                    ["pid"] = PositiveIntegerProperty(pidDesc),
                    ["display"] = StringProperty("Display index ('1', '2', ...) when app/pid is omitted. Defaults to '1'."),
                    ["client_only"] = BooleanProperty("Capture pure game client canvas excluding titlebar/DWM borders when targeting an app. Defaults to true."),
                    ["region_x"] = NumberProperty("Optional ROI X offset."),
                    ["region_y"] = NumberProperty("Optional ROI Y offset."),
                    ["region_width"] = NumberProperty("Optional ROI width (> 0)."),
                    ["region_height"] = NumberProperty("Optional ROI height (> 0)."),
                    ["max_fps"] = BoundedIntegerProperty("Maximum publish rate in FPS (1..240). Defaults to 60.", 1, 240),
                    ["only_on_change"] = BooleanProperty("Skip publishing duplicate static frames via 64-bit FNV-1a comparison. Defaults to true."),
                    ["owner_pid"] = PositiveIntegerProperty("Optional consumer PID; stream auto-stops within 250ms if owner_pid exits.")
                }, ["action"])
            },
            new ToolDefinition
            {
                Name = "drag",
                Description = "Drag from one point to another using pixel coordinates, supporting coordinate_space ('window', 'client', 'screen') and background app_post or smooth physical hardware/stealth SendInput dragging for game inventories and maps. This tool is part of plugin `Computer Use`.",
                Annotations = defaultAnnot,
                InputSchema = ObjectSchema(new()
                {
                    ["app"] = StringProperty(appDesc),
                    ["pid"] = PositiveIntegerProperty(pidDesc),
                    ["from_x"] = NumberProperty("Start X coordinate"),
                    ["from_y"] = NumberProperty("Start Y coordinate"),
                    ["to_x"] = NumberProperty("End X coordinate"),
                    ["to_y"] = NumberProperty("End Y coordinate"),
                    ["coordinate_space"] = EnumStringProperty("Coordinate reference frame: 'window', 'client', or 'screen'.", ["window", "client", "screen"]),
                    ["duration_ms"] = BoundedIntegerProperty("Total drag duration in milliseconds (0..10000). Defaults to 80.", 0, 10000),
                    ["drag_method"] = EnumStringProperty("Drag delivery method: auto (default), app_post, stealth, global, or hardware.", ["auto", "app_post", "stealth", "global", "hardware"]),
                    ["humanize"] = HumanizeProperty("Optional biomechanical humanization profile: 'none', 'subtle', 'natural', 'fast', or boolean."),
                    ["skip_snapshot"] = BooleanProperty("If true, skips post-action UIA tree rebuild and screenshot capture for fast batching. Defaults to false.")
                }, ["app", "from_x", "from_y", "to_x", "to_y"])
            },
            new ToolDefinition
            {
                Name = "query",
                Description = "Search an app's accessibility hierarchy for matching elements without capturing a screenshot or rendering the full accessibility tree. Returns matching element indexes that can be used with click, set_value, select_text, scroll, and perform_secondary_action. This tool is part of plugin `Computer Use`.",
                Annotations = readOnlyAnnot,
                InputSchema = ObjectSchema(new()
                {
                    ["app"] = StringProperty(appDesc),
                    ["pid"] = PositiveIntegerProperty(pidDesc),
                    ["text"] = StringProperty("Match against element title, label, value, or identifier. Case-insensitive substring by default."),
                    ["role"] = StringProperty("Match against accessibility role or control type (for example Button, TextField, Edit, CheckBox). Case-insensitive substring by default."),
                    ["exact"] = BooleanProperty("Require exact case-insensitive matches for text and role instead of substring matches. Defaults to false."),
                    ["limit"] = BoundedIntegerProperty("Maximum matching elements to return. Defaults to 20, maximum 100.", 1, 100),
                    ["max_nodes"] = BoundedIntegerProperty("Maximum accessibility nodes to inspect before stopping. Defaults to 500, maximum 5000.", 1, 5000),
                    ["window_id"] = PositiveIntegerProperty("Optional window handle/ID to scope the search to a specific window.")
                }, ["app"])
            },
            new ToolDefinition
            {
                Name = "get_app_state",
                Description = "Get the state of an already running app's key window and return a screenshot (full window, client_only game canvas, or optional ROI sub-region), window/client bounds, and accessibility tree. This must be called once per assistant turn before interacting with the app. This tool is part of plugin `Computer Use`.",
                Annotations = readOnlyAnnot,
                InputSchema = ObjectSchema(new()
                {
                    ["app"] = StringProperty(appDesc),
                    ["pid"] = PositiveIntegerProperty(pidDesc),
                    ["text_limit"] = TextLimitProperty("Maximum text characters to return. Use \"max\" for full text. Defaults to 500."),
                    ["max_tree_nodes"] = PositiveIntegerProperty("Maximum accessibility tree nodes to render. Defaults to 1200."),
                    ["max_tree_depth"] = PositiveIntegerProperty("Maximum accessibility tree depth to render. Defaults to 64."),
                    ["include_ax"] = BooleanProperty("Include the accessibility tree, visible text, focused element, and element index cache. Defaults to true."),
                    ["include_screenshot"] = BooleanProperty("If false, skips window screenshot capture and returns only the accessibility tree and element coordinates. Defaults to true."),
                    ["client_only"] = BooleanProperty("If true, captures only the pure client canvas area (excluding title bar and DWM borders) and sets default coordinateSpace to 'client'. Defaults to false."),
                    ["window_placement"] = EnumStringProperty(
                        "Window placement policy for this observation: keep (default), agent_display, or restore.",
                        ["keep", "agent_display", "restore"]),
                    ["region_x"] = NumberProperty("Optional window/client-relative ROI X coordinate in pixels (requires region_y, region_width, region_height)."),
                    ["region_y"] = NumberProperty("Optional window/client-relative ROI Y coordinate in pixels (requires region_x, region_width, region_height)."),
                    ["region_width"] = NumberProperty("Optional window/client-relative ROI width in pixels (> 0)."),
                    ["region_height"] = NumberProperty("Optional window/client-relative ROI height in pixels (> 0).")
                }, ["app"])
            },
            new ToolDefinition
            {
                Name = "list_apps",
                Description = "List the apps on this computer. Returns the set of apps that are currently running, as well as any that have been used in the last 14 days, including details on usage frequency. This tool is part of plugin `Computer Use`.",
                Annotations = readOnlyAnnot,
                InputSchema = ObjectSchema(new(), [])
            },
            new ToolDefinition
            {
                Name = "perform_secondary_action",
                Description = "Invoke a secondary accessibility action exposed by an element. This tool is part of plugin `Computer Use`.",
                Annotations = defaultAnnot,
                InputSchema = ObjectSchema(new()
                {
                    ["app"] = StringProperty(appDesc),
                    ["pid"] = PositiveIntegerProperty(pidDesc),
                    ["element_index"] = StringProperty("Element identifier"),
                    ["action"] = StringProperty("Secondary accessibility action name"),
                    ["skip_snapshot"] = BooleanProperty("If true, skips post-action UIA tree rebuild and screenshot capture. Defaults to false.")
                }, ["app", "element_index", "action"])
            },
            new ToolDefinition
            {
                Name = "press_key",
                Description = "Press, hold down, or release a key or key-combination on the keyboard, including modifier, navigation, and game movement keys.\n  - Supports xdotool's `key` syntax (\"a\", \"Return\", \"Tab\", \"super+c\", \"Up\", \"KP_0\").\n  - Supports stateful key_state ('tap', 'down', 'up'), hold_ms, and repeat. This tool is part of plugin `Computer Use`.",
                Annotations = defaultAnnot,
                InputSchema = ObjectSchema(new()
                {
                    ["app"] = StringProperty(appDesc),
                    ["pid"] = PositiveIntegerProperty(pidDesc),
                    ["key"] = StringProperty("Key or key-combination to press"),
                    ["key_method"] = EnumStringProperty(
                        "Keyboard delivery implementation: auto (default), app_post, stealth, sky_key, global, or hardware.",
                        ["auto", "app_post", "stealth", "sky_key", "global", "hardware"]),
                    ["key_state"] = EnumStringProperty("Key state transition: tap (default), down (hold with auto-release safety TTL), or up (release).", ["tap", "down", "up"]),
                    ["action"] = EnumStringProperty("Alias for key_state: tap, down, or up.", ["tap", "down", "up"]),
                    ["hold_ms"] = BoundedIntegerProperty("Hold duration in ms for 'tap', or max auto-release safety TTL in ms when key_state is 'down' (0..60000, default 30000 for 'down').", 0, 60000),
                    ["repeat"] = BoundedIntegerProperty("Number of key taps when key_state is 'tap' (1..100). Defaults to 1.", 1, 100),
                    ["repeat_interval_ms"] = BoundedIntegerProperty("Interval in milliseconds between repeated taps (0..5000). Defaults to 15.", 0, 5000),
                    ["humanize"] = HumanizeProperty("Optional biomechanical dwell/interval jitter profile: 'none', 'subtle', 'natural', 'fast', or boolean."),
                    ["skip_snapshot"] = BooleanProperty("If true, skips post-action UIA tree rebuild and screenshot capture for fast batching. Also allows pressing keys without a prior get_app_state call. Defaults to false.")
                }, ["app", "key"])
            },
            new ToolDefinition
            {
                Name = "batch_actions",
                Description = "Execute a sequence of up to 200 input/wait steps (press_key, click, move_mouse, follow_trajectory, type_text, scroll, drag, wait, release_inputs) inside a single in-process ActionLock with zero IPC/HTTP roundtrip overhead and automatic panic-release safety on error. This tool is part of plugin `Computer Use`.",
                Annotations = defaultAnnot,
                InputSchema = ObjectSchema(new()
                {
                    ["app"] = StringProperty("Optional default target app or 'pid:<id>' for steps that omit 'app'."),
                    ["pid"] = PositiveIntegerProperty(pidDesc),
                    ["steps"] = new Dictionary<string, object?>
                    {
                        ["type"] = "array",
                        ["minItems"] = 1,
                        ["maxItems"] = 200,
                        ["description"] = "Ordered list of step objects. Each step specifies 'type' (or 'action'): press_key, click, move_mouse, follow_trajectory, type_text, scroll, drag, wait (with 'ms' 0..30000), or release_inputs.",
                        ["items"] = new Dictionary<string, object?>
                        {
                            ["type"] = "object",
                            ["additionalProperties"] = true
                        }
                    },
                    ["humanize"] = HumanizeProperty("Optional default humanize profile propagated to steps that omit 'humanize'."),
                    ["stop_on_error"] = BooleanProperty("Stop executing remaining steps immediately if a step fails. Defaults to true."),
                    ["release_on_error"] = BooleanProperty("Automatically release all held keys and mouse buttons if a step fails. Defaults to true."),
                    ["skip_snapshot"] = BooleanProperty("Skip final UIA/screenshot rebuild after sequence completes. Defaults to true for game combos.")
                }, ["steps"])
            },
            new ToolDefinition
            {
                Name = "release_inputs",
                Description = "Immediately release all held keyboard keys, held mouse buttons, and stuck hardware modifiers tracked by ActiveInputTracker. Safe emergency panic-release tool. This tool is part of plugin `Computer Use`.",
                Annotations = defaultAnnot,
                InputSchema = ObjectSchema(new(), [])
            },
            new ToolDefinition
            {
                Name = "scroll",
                Description = "Scroll an element by index or at window/client/screen coordinates (x, y) in a direction by a number of pages, supporting UIA, background app_post, or hardware wheel SendInput for 3D camera zoom and game inventories. This tool is part of plugin `Computer Use`.",
                Annotations = defaultAnnot,
                InputSchema = ObjectSchema(new()
                {
                    ["app"] = StringProperty(appDesc),
                    ["pid"] = PositiveIntegerProperty(pidDesc),
                    ["direction"] = StringProperty("Scroll direction: up, down, left, or right"),
                    ["element_index"] = StringProperty("Optional element identifier (required unless x and y coordinates are provided)"),
                    ["x"] = NumberProperty("Optional X coordinate for coordinate-based wheel scrolling"),
                    ["y"] = NumberProperty("Optional Y coordinate for coordinate-based wheel scrolling"),
                    ["coordinate_space"] = EnumStringProperty("Coordinate reference frame for x/y: 'window', 'client', or 'screen'.", ["window", "client", "screen"]),
                    ["pages"] = NumberProperty("Number of pages to scroll. Fractional values are supported. Defaults to 1"),
                    ["scroll_method"] = EnumStringProperty("Scroll delivery method: auto (default), app_post, stealth, global, or hardware (SendInput MOUSEEVENTF_WHEEL/HWHEEL).", ["auto", "app_post", "stealth", "global", "hardware"]),
                    ["skip_snapshot"] = BooleanProperty("If true, skips post-action UIA tree rebuild and screenshot capture. Defaults to false.")
                }, ["app", "direction"])
            },
            new ToolDefinition
            {
                Name = "set_value",
                Description = "Set the value of a settable accessibility element. This tool is part of plugin `Computer Use`.",
                Annotations = defaultAnnot,
                InputSchema = ObjectSchema(new()
                {
                    ["app"] = StringProperty(appDesc),
                    ["pid"] = PositiveIntegerProperty(pidDesc),
                    ["element_index"] = StringProperty("Element identifier"),
                    ["value"] = StringProperty("Value to assign"),
                    ["skip_snapshot"] = BooleanProperty("If true, skips post-action UIA tree rebuild and screenshot capture. Defaults to false.")
                }, ["app", "element_index", "value"])
            },
            new ToolDefinition
            {
                Name = "select_text",
                Description = "Select text or place the insertion cursor in a text accessibility element by matching text with optional prefix/suffix disambiguation. This tool is part of plugin `Computer Use`.",
                Annotations = defaultAnnot,
                InputSchema = ObjectSchema(new()
                {
                    ["app"] = StringProperty(appDesc),
                    ["pid"] = PositiveIntegerProperty(pidDesc),
                    ["element_index"] = StringProperty("Text element identifier"),
                    ["text"] = StringProperty("Exact text to locate inside the target element"),
                    ["prefix"] = StringProperty("Optional exact text immediately preceding the target text to disambiguate repeated matches"),
                    ["suffix"] = StringProperty("Optional exact text immediately following the target text to disambiguate repeated matches"),
                    ["selection"] = EnumStringProperty(
                        "Selection behavior: text selects the matched text (default), cursor_before places the cursor before it, and cursor_after places the cursor after it.",
                        ["text", "cursor_before", "cursor_after"]),
                    ["skip_snapshot"] = BooleanProperty("If true, skips post-action UIA tree rebuild and screenshot capture. Defaults to false.")
                }, ["app", "element_index", "text"])
            },
            new ToolDefinition
            {
                Name = "type_text",
                Description = "Type literal text using ultra-fast batched keyboard input. This tool is part of plugin `Computer Use`.",
                Annotations = defaultAnnot,
                InputSchema = ObjectSchema(new()
                {
                    ["app"] = StringProperty(appDesc),
                    ["pid"] = PositiveIntegerProperty(pidDesc),
                    ["text"] = StringProperty("Literal text to type"),
                    ["key_method"] = EnumStringProperty(
                        "Keyboard delivery implementation: auto (default), app_post, stealth, sky_key, global, or hardware.",
                        ["auto", "app_post", "stealth", "sky_key", "global", "hardware"]),
                    ["humanize"] = HumanizeProperty("Optional biomechanical inter-key timing jitter profile: 'none', 'subtle', 'natural', 'fast', or boolean."),
                    ["skip_snapshot"] = BooleanProperty("If true, skips post-action UIA tree rebuild and screenshot capture for fast batching. Also allows typing text without a prior get_app_state call. Defaults to false.")
                }, ["app", "text"])
            },
            new ToolDefinition
            {
                Name = "get_clipboard",
                Description = "Get the current text or image content from the Windows clipboard. This tool is part of plugin `Computer Use`.",
                Annotations = readOnlyAnnot,
                InputSchema = ObjectSchema(new()
                {
                    ["format"] = EnumStringProperty("Clipboard format to retrieve: text, image, or all. Defaults to text.", ["text", "image", "all"])
                }, [])
            },
            new ToolDefinition
            {
                Name = "set_clipboard",
                Description = "Set text or image content to the Windows clipboard, with optional automatic paste (Ctrl+V) into the active window. This tool is part of plugin `Computer Use`.",
                Annotations = defaultAnnot,
                InputSchema = ObjectSchema(new()
                {
                    ["text"] = StringProperty("Text to write to the clipboard"),
                    ["image_base64"] = StringProperty("Base64 encoded image to write to the clipboard"),
                    ["paste"] = BooleanProperty("If true, automatically inject Ctrl+V keystroke into active window after setting clipboard")
                }, [])
            },
            new ToolDefinition
            {
                Name = "manage_window",
                Description = "Manage a window's state, focus, position, or size. This tool is part of plugin `Computer Use`.",
                Annotations = defaultAnnot,
                InputSchema = ObjectSchema(new()
                {
                    ["app"] = StringProperty("Process name or window title to target"),
                    ["pid"] = IntegerProperty("Explicit PID of the target process"),
                    ["action"] = EnumStringProperty("Window action to perform", ["focus", "minimize", "maximize", "restore", "move", "resize"]),
                    ["x"] = NumberProperty("Target X coordinate for move/resize"),
                    ["y"] = NumberProperty("Target Y coordinate for move/resize"),
                    ["width"] = NumberProperty("Target width for resize"),
                    ["height"] = NumberProperty("Target height for resize")
                }, ["action"])
            },
            new ToolDefinition
            {
                Name = "capture_screen",
                Description = "Capture the full multi-monitor virtual desktop, a specific display by index, or an ultra-fast ROI sub-rectangle (x, y, width, height) for game HUDs/HP bars/minimaps. This tool is part of plugin `Computer Use`.",
                Annotations = readOnlyAnnot,
                InputSchema = ObjectSchema(new()
                {
                    ["display"] = StringProperty("Display identifier: 'all' for full virtual desktop, or '1', '2', etc. for specific display index. Defaults to 'all'."),
                    ["x"] = NumberProperty("Optional ROI X offset relative to target display (requires y, width, height)."),
                    ["y"] = NumberProperty("Optional ROI Y offset relative to target display (requires x, width, height)."),
                    ["width"] = NumberProperty("Optional ROI width in pixels (> 0, requires x, y, height)."),
                    ["height"] = NumberProperty("Optional ROI height in pixels (> 0, requires x, y, width)."),
                    ["format"] = EnumStringProperty("Optional image format override: png or jpg.", ["png", "jpg", "jpeg"]),
                    ["max_dimension"] = BoundedIntegerProperty("Optional maximum long-edge dimension override (>= 64).", 64, 16384)
                }, [])
            },
            new ToolDefinition
            {
                Name = "launch_app",
                Description = "Launch an application safely without invoking a command shell. This tool is part of plugin `Computer Use`.",
                Annotations = defaultAnnot,
                InputSchema = ObjectSchema(new()
                {
                    ["path"] = StringProperty("Executable file path or command to launch"),
                    ["args"] = StringProperty("Command-line arguments"),
                    ["working_dir"] = StringProperty("Working directory for the process")
                }, ["path"])
            },
            new ToolDefinition
            {
                Name = "close_app",
                Description = "Close an application gracefully with WM_CLOSE, with fallback to forceful termination after a timeout. This tool is part of plugin `Computer Use`.",
                Annotations = defaultAnnot,
                InputSchema = ObjectSchema(new()
                {
                    ["app"] = StringProperty("Process name or window title to target"),
                    ["pid"] = IntegerProperty("Explicit PID of the target process"),
                    ["force"] = BooleanProperty("If true, immediately kills the process tree. Defaults to false (graceful WM_CLOSE)."),
                    ["timeout_ms"] = IntegerProperty("Graceful close timeout in milliseconds before force kill. Defaults to 3000.")
                }, [])
            }
        ];
    }

    public ToolCallResult CallTool(string name, JsonElement args)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (args.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in args.EnumerateObject())
            {
                dict[prop.Name] = JsonElementToObject(prop.Value);
            }
        }
        return CallTool(name, dict);
    }

    public ToolCallResult CallTool(string name, Dictionary<string, object?> args)
    {
        try
        {
            switch (name)
            {
                case "list_apps":
                    return ListApps();
                case "get_app_state":
                    return GetAppState(args);
                case "query":
                    return Query(args);
                case "click":
                    return ActionLock.Execute(() => ClickLocked(args));
                case "move_mouse":
                    return ActionLock.Execute(() => MoveMouseLocked(args));
                case "follow_trajectory":
                    return ActionLock.Execute(() => FollowTrajectoryLocked(args));
                case "probe_screen":
                    return ProbeScreen(args);
                case "wait_for":
                    return WaitFor(args);
                case "manage_shared_frame":
                    return ManageSharedFrame(args);
                case "perform_secondary_action":
                    return ActionLock.Execute(() => PerformSecondaryActionLocked(args));
                case "scroll":
                    return ActionLock.Execute(() => ScrollLocked(args));
                case "drag":
                    return ActionLock.Execute(() => DragLocked(args));
                case "type_text":
                    return ActionLock.Execute(() => TypeTextLocked(args));
                case "press_key":
                    return ActionLock.Execute(() => PressKeyLocked(args));
                case "batch_actions":
                    return BatchActions(args);
                case "release_inputs":
                    return ReleaseInputs();
                case "set_value":
                    return ActionLock.Execute(() => SetValueLocked(args));
                case "select_text":
                    return ActionLock.Execute(() => SelectTextLocked(args));
                case "get_clipboard":
                    return GetClipboard(args);
                case "set_clipboard":
                    return SetClipboard(args);
                case "manage_window":
                    return ManageWindow(args);
                case "capture_screen":
                    return CaptureScreen(args);
                case "launch_app":
                    return LaunchApp(args);
                case "close_app":
                    return CloseApp(args);
                default:
                    return ToolCallResult.TextResult($"unsupportedTool(\"{name}\")", isError: true);
            }
        }
        catch (Exception ex)
        {
            return ToolCallResult.TextResult(ex.Message, isError: true);
        }
    }

    /// <summary>
    /// Dispatches an action when the caller ALREADY holds ActionLock (used by ExecuteBatchLocked,
    /// VisionWaitService on_match reactions, and WebSocketControlHub InputWorkerTask so ActionLock is never re-entered).
    /// </summary>
    internal ToolCallResult ExecuteActionLocked(string name, Dictionary<string, object?> args)
    {
        try
        {
            return name.Trim().ToLowerInvariant() switch
            {
                "click" => ClickLocked(args),
                "move_mouse" or "move-mouse" => MoveMouseLocked(args),
                "follow_trajectory" or "trajectory" => FollowTrajectoryLocked(args),
                "perform_secondary_action" or "secondary" => PerformSecondaryActionLocked(args),
                "scroll" => ScrollLocked(args),
                "drag" => DragLocked(args),
                "type_text" or "type" => TypeTextLocked(args),
                "press_key" or "press-key" => PressKeyLocked(args),
                "batch_actions" or "batch" => ExecuteBatchLocked(args),
                "release_inputs" or "release-inputs" => ReleaseInputs(),
                "set_value" or "set-value" => SetValueLocked(args),
                "select_text" or "select-text" => SelectTextLocked(args),
                "set_clipboard" => SetClipboardLocked(args),
                "manage_window" => ManageWindowLocked(args),
                _ => ToolCallResult.TextResult($"unsupportedLockedAction(\"{name}\")", isError: true)
            };
        }
        catch (Exception ex)
        {
            return ToolCallResult.TextResult(ex.Message, isError: true);
        }
    }

    private static (int? Pid, string? Error) GetOptionalStrictPid(Dictionary<string, object?> args)
    {
        if (!args.TryGetValue("pid", out var val) || val == null)
        {
            return (null, null);
        }

        if (val is int i)
        {
            return i > 0 ? (i, null) : (null, "Invalid pid: must be a positive integer.");
        }
        if (val is long l)
        {
            return (l > 0 && l <= int.MaxValue) ? ((int)l, null) : (null, "Invalid pid: must be a positive integer.");
        }
        if (int.TryParse(val.ToString()?.Trim(), out int parsed) && parsed > 0)
        {
            return (parsed, null);
        }

        return (null, "Invalid pid: must be a positive integer.");
    }

    private static bool TryResolveAppOrPid(
        Dictionary<string, object?> args,
        out string? app,
        out int? pid,
        out string targetLabel,
        out string? error)
    {
        app = GetOptionalString(args, "app");
        (pid, error) = GetOptionalStrictPid(args);
        if (error != null)
        {
            targetLabel = "";
            return false;
        }

        if (string.IsNullOrWhiteSpace(app) && !pid.HasValue)
        {
            targetLabel = "";
            error = "Missing required argument: app";
            return false;
        }

        targetLabel = !string.IsNullOrWhiteSpace(app) ? app! : $"pid:{pid!.Value}";
        return true;
    }

    private static string? GetOptionalCoordinateSpace(Dictionary<string, object?> args) =>
        GetOptionalString(args, "coordinate_space") ?? GetOptionalString(args, "coordinateSpace");

    private ToolCallResult ListApps()
    {
        string result = _automationService.ListApps();
        return ToolCallResult.TextResult(result, isError: false);
    }

    private ToolCallResult GetAppState(Dictionary<string, object?> args)
    {
        if (!TryResolveAppOrPid(args, out string? app, out int? pid, out _, out string? targetErr))
        {
            return ToolCallResult.TextResult(targetErr!, isError: true);
        }

        var (textLimit, tlErr) = GetOptionalTextLimit(args, "text_limit");
        if (tlErr != null) return ToolCallResult.TextResult(tlErr, isError: true);

        var (maxNodes, mnErr) = GetOptionalPositiveInt(args, "max_tree_nodes");
        if (mnErr != null) return ToolCallResult.TextResult(mnErr, isError: true);

        var (maxDepth, mdErr) = GetOptionalPositiveInt(args, "max_tree_depth");
        if (mdErr != null) return ToolCallResult.TextResult(mdErr, isError: true);

        bool includeAx = GetOptionalBool(args, "include_ax") ?? GetOptionalBool(args, "includeAx") ?? true;
        bool includeScreenshot = GetOptionalBool(args, "include_screenshot") ?? GetOptionalBool(args, "includeScreenshot") ?? true;
        bool clientOnly = GetOptionalBool(args, "client_only") ?? GetOptionalBool(args, "clientOnly") ?? false;
        string? windowPlacement = GetOptionalString(args, "window_placement") ?? GetOptionalString(args, "windowPlacement");

        double? regionX = GetOptionalDouble(args, "region_x") ?? GetOptionalDouble(args, "regionX");
        double? regionY = GetOptionalDouble(args, "region_y") ?? GetOptionalDouble(args, "regionY");
        double? regionWidth = GetOptionalDouble(args, "region_width") ?? GetOptionalDouble(args, "regionWidth");
        double? regionHeight = GetOptionalDouble(args, "region_height") ?? GetOptionalDouble(args, "regionHeight");

        if (!includeAx && !includeScreenshot)
        {
            return ToolCallResult.TextResult("At least one of include_ax or include_screenshot must be true", isError: true);
        }

        var snapshot = _automationService.BuildSnapshot(
            app,
            textLimit,
            maxNodes ?? 1200,
            maxDepth ?? 64,
            includeScreenshot,
            includeAx,
            windowPlacement,
            regionX,
            regionY,
            regionWidth,
            regionHeight,
            clientOnly,
            pid);

        return snapshot.ToToolCallResult();
    }

    private ToolCallResult Query(Dictionary<string, object?> args)
    {
        if (!TryResolveAppOrPid(args, out string? app, out int? pid, out _, out string? targetErr))
        {
            return ToolCallResult.TextResult(targetErr!, isError: true);
        }

        string? text = GetOptionalString(args, "text");
        string? role = GetOptionalString(args, "role");
        bool exact = GetOptionalBool(args, "exact") ?? false;

        var (limit, limitErr) = GetOptionalPositiveInt(args, "limit");
        if (limitErr != null) return ToolCallResult.TextResult(limitErr, isError: true);
        int effectiveLimit = limit ?? 20;
        if (effectiveLimit < 1 || effectiveLimit > 100)
        {
            return ToolCallResult.TextResult("limit must be between 1 and 100", isError: true);
        }

        var (maxNodes, mnErr) = GetOptionalPositiveInt(args, "max_nodes");
        if (mnErr != null) return ToolCallResult.TextResult(mnErr, isError: true);
        int effectiveMaxNodes = maxNodes ?? 500;
        if (effectiveMaxNodes < 1 || effectiveMaxNodes > 5000)
        {
            return ToolCallResult.TextResult("max_nodes must be between 1 and 5000", isError: true);
        }

        var (windowId, widErr) = GetOptionalPositiveInt(args, "window_id");
        if (widErr != null) return ToolCallResult.TextResult(widErr, isError: true);

        var result = _automationService.Query(
            app,
            text,
            role,
            exact,
            effectiveLimit,
            effectiveMaxNodes,
            windowId,
            pid);

        return result.ToToolCallResult();
    }

    private static bool IsSkipSnapshot(Dictionary<string, object?> args, bool defaultValue = false) =>
        GetOptionalBool(args, "skip_snapshot") ?? GetOptionalBool(args, "skipSnapshot") ?? defaultValue;

    private ToolCallResult FinalizeAction(string? app, int? pid, string targetLabel, bool skipSnapshot, string actionName, bool preserveClientOnly = false)
    {
        if (skipSnapshot)
        {
            return ToolCallResult.TextResult(
                JsonSerializer.Serialize(new { ok = true, app = targetLabel, pid, action = actionName, snapshotSkipped = true }, JsonOptions),
                isError: false);
        }

        PrecisionTimer.WaitMs(12);
        var refreshed = _automationService.BuildSnapshot(app, clientOnly: preserveClientOnly, pid: pid);
        return refreshed.ToToolCallResult();
    }

    private static IntPtr GetProcessHwnd(Process process)
    {
        IntPtr target = WindowManager.GetTargetHwnd(process);
        return target != IntPtr.Zero ? target : process.MainWindowHandle;
    }

    internal ToolCallResult ClickLocked(Dictionary<string, object?> args)
    {
        if (!TryResolveAppOrPid(args, out string? app, out int? pid, out string targetLabel, out string? targetErr))
        {
            return ToolCallResult.TextResult(targetErr!, isError: true);
        }

        HumanizeProfile humanize = HumanizedPhysics.ResolveProfile(args.TryGetValue("humanize", out var rawHum) ? rawHum : null);

        string? elementIndexStr = GetOptionalElementIndex(args);
        double? x = GetOptionalDouble(args, "x");
        double? y = GetOptionalDouble(args, "y");
        string? coordinateSpace = GetOptionalCoordinateSpace(args);
        string buttonState = GetOptionalString(args, "button_state") ?? GetOptionalString(args, "buttonState") ?? "click";
        string normButtonState = InputInjector.NormalizeButtonState(buttonState);
        int? holdMs = GetOptionalInt(args, "hold_ms") ?? GetOptionalInt(args, "holdMs");

        if (normButtonState != "up" && string.IsNullOrEmpty(elementIndexStr) && (!x.HasValue || !y.HasValue))
        {
            return ToolCallResult.TextResult("click requires either element_index or x/y", isError: true);
        }

        string clickMethod = GetOptionalString(args, "click_method") ?? GetOptionalString(args, "clickMethod") ?? "auto";
        clickMethod = clickMethod.Trim().ToLowerInvariant();

        if (clickMethod == "accessibility" && string.IsNullOrEmpty(elementIndexStr))
        {
            return ToolCallResult.TextResult("click_method 'accessibility' requires element_index", isError: true);
        }

        if (clickMethod == "sky_click")
        {
            return ToolCallResult.TextResult("click_method 'sky_click' is not supported on Windows", isError: true);
        }

        bool skipSnapshot = IsSkipSnapshot(args);
        var currentSnapshot = _automationService.GetCurrentSnapshot(app, pid);
        string normSpace = WindowManager.NormalizeCoordinateSpace(
            coordinateSpace,
            hasTargetApp: true,
            snapshotClientOnly: currentSnapshot?.ClientOnly == true);

        Process? resolvedStrictProcess = null;
        if (pid.HasValue || WindowManager.TryParseStrictPidPrefix(app, out _))
        {
            resolvedStrictProcess = _automationService.ResolveApp(app, pid);
        }

        ElementRecord? elementRecord = null;
        FlaUI.Core.AutomationElements.AutomationElement? uiaElement = null;

        if (!string.IsNullOrEmpty(elementIndexStr))
        {
            if (!int.TryParse(elementIndexStr, out int idx))
            {
                return ToolCallResult.TextResult($"unknown element_index \"{elementIndexStr}\"", isError: true);
            }
            elementRecord = _automationService.GetElementRecordFromSnapshot(app, idx, pid);
            if (elementRecord == null)
            {
                if (currentSnapshot == null)
                {
                    return ToolCallResult.TextResult($"No app state is available for {targetLabel}. Run get_app_state before action tools.", isError: true);
                }
                return ToolCallResult.TextResult($"unknown element_index \"{elementIndexStr}\"", isError: true);
            }
            uiaElement = _automationService.GetElementFromSnapshot(app, idx, pid);
        }
        else if (currentSnapshot == null && !skipSnapshot)
        {
            return ToolCallResult.TextResult($"No app state is available for {targetLabel}. Run get_app_state before action tools.", isError: true);
        }

        int clickCount = (int)(GetOptionalDouble(args, "click_count") ?? GetOptionalDouble(args, "clickCount") ?? 1.0);
        string mouseButton = GetOptionalString(args, "mouse_button") ?? GetOptionalString(args, "mouseButton") ?? "left";

        var process = resolvedStrictProcess ?? _automationService.ResolveApp(app, pid);
        IntPtr hwnd = GetProcessHwnd(process);

        Frame? windowBounds = currentSnapshot?.WindowBounds;
        if (windowBounds == null && NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            windowBounds = new Frame
            {
                X = rect.Left,
                Y = rect.Top,
                Width = Math.Max(0, rect.Right - rect.Left),
                Height = Math.Max(0, rect.Bottom - rect.Top)
            };
        }

        double? originX = null;
        double? originY = null;
        if (elementRecord == null && x.HasValue && y.HasValue)
        {
            var resolvedOrigin = WindowManager.ResolveCoordinateOrigin(normSpace, hwnd, windowBounds);
            originX = resolvedOrigin.OriginX;
            originY = resolvedOrigin.OriginY;
        }

        InputInjector.Click(
            hwnd,
            windowBounds,
            elementRecord,
            x,
            y,
            clickCount,
            mouseButton,
            clickMethod,
            uiaElement,
            normButtonState,
            holdMs,
            originX,
            originY,
            humanize);

        return FinalizeAction(app, pid, targetLabel, skipSnapshot, "click", currentSnapshot?.ClientOnly == true);
    }

    internal ToolCallResult MoveMouseLocked(Dictionary<string, object?> args)
    {
        string? app = GetOptionalString(args, "app");
        var (pid, pidErr) = GetOptionalStrictPid(args);
        if (pidErr != null)
        {
            return ToolCallResult.TextResult(pidErr, isError: true);
        }

        HumanizeProfile humanize = HumanizedPhysics.ResolveProfile(args.TryGetValue("humanize", out var rawHum) ? rawHum : null);

        double? x = GetOptionalDouble(args, "x");
        double? y = GetOptionalDouble(args, "y");
        string? coordinateSpace = GetOptionalCoordinateSpace(args);
        double? deltaX = GetOptionalDouble(args, "delta_x") ?? GetOptionalDouble(args, "deltaX");
        double? deltaY = GetOptionalDouble(args, "delta_y") ?? GetOptionalDouble(args, "deltaY");
        int steps = GetOptionalInt(args, "steps") ?? 1;
        int durationMs = GetOptionalInt(args, "duration_ms") ?? GetOptionalInt(args, "durationMs") ?? 0;
        string? method = GetOptionalString(args, "method");
        bool skipSnapshot = IsSkipSnapshot(args, defaultValue: true);

        // Validate parameters before ResolveApp so invalid arguments always fail with 400 Bad Request
        if (steps < 1 || steps > 240)
        {
            return ToolCallResult.TextResult("steps must be between 1 and 240.", isError: true);
        }
        if (durationMs < 0 || durationMs > 5000)
        {
            return ToolCallResult.TextResult("duration_ms must be between 0 and 5000.", isError: true);
        }
        bool hasRelative = deltaX.HasValue || deltaY.HasValue;
        bool hasAbsolute = x.HasValue || y.HasValue;
        if (!hasRelative && !hasAbsolute)
        {
            return ToolCallResult.TextResult("move_mouse requires either (x and y) coordinates or (delta_x / delta_y) relative motion.", isError: true);
        }
        if (hasAbsolute && (!x.HasValue || !y.HasValue))
        {
            return ToolCallResult.TextResult("move_mouse absolute movement requires both x and y coordinates.", isError: true);
        }
        if (!string.IsNullOrWhiteSpace(method))
        {
            string m = method.Trim().ToLowerInvariant();
            if (m is not ("hardware" or "global" or "auto" or "stealth" or "app_post"))
            {
                return ToolCallResult.TextResult($"Invalid move_mouse method '{method}'. Valid options are: hardware, global, auto, stealth, app_post.", isError: true);
            }
            if (hasRelative && m == "app_post")
            {
                return ToolCallResult.TextResult("Relative delta_x/delta_y 3D camera movement requires hardware, global, auto, or stealth method (not app_post).", isError: true);
            }
        }
        if (!string.IsNullOrWhiteSpace(coordinateSpace))
        {
            _ = WindowManager.NormalizeCoordinateSpace(coordinateSpace, hasTargetApp: true);
        }

        bool hasTargetApp = !string.IsNullOrWhiteSpace(app) || pid.HasValue;
        IntPtr hwnd = IntPtr.Zero;
        Frame? windowBounds = null;
        AppSnapshot? currentSnapshot = null;

        if (hasTargetApp)
        {
            var process = _automationService.ResolveApp(app, pid);
            hwnd = GetProcessHwnd(process);
            currentSnapshot = _automationService.GetCurrentSnapshot(app, pid);
            windowBounds = currentSnapshot?.WindowBounds;
            if (windowBounds == null && NativeMethods.GetWindowRect(hwnd, out var rect))
            {
                windowBounds = new Frame(
                    rect.Left,
                    rect.Top,
                    Math.Max(0, rect.Right - rect.Left),
                    Math.Max(0, rect.Bottom - rect.Top));
            }
        }
        else if (string.Equals(method?.Trim(), "app_post", StringComparison.OrdinalIgnoreCase))
        {
            return ToolCallResult.TextResult("move_mouse with method 'app_post' requires 'app' or 'pid'", isError: true);
        }

        string normSpace = WindowManager.NormalizeCoordinateSpace(
            coordinateSpace,
            hasTargetApp,
            currentSnapshot?.ClientOnly == true);

        double? originX = null;
        double? originY = null;
        if (x.HasValue && y.HasValue)
        {
            var resolvedOrigin = WindowManager.ResolveCoordinateOrigin(normSpace, hwnd, windowBounds);
            originX = resolvedOrigin.OriginX;
            originY = resolvedOrigin.OriginY;
        }
        else if (!string.IsNullOrWhiteSpace(coordinateSpace) && !hasTargetApp && normSpace != "screen")
        {
            return ToolCallResult.TextResult($"coordinate_space '{normSpace}' requires a target application ('app' or 'pid').", isError: true);
        }

        InputInjector.MoveMouse(hwnd, windowBounds, x, y, deltaX, deltaY, steps, durationMs, method, originX, originY, humanize);

        string? targetLabel = !string.IsNullOrWhiteSpace(app) ? app : (pid.HasValue ? $"pid:{pid.Value}" : null);
        if (!skipSnapshot && hasTargetApp)
        {
            return FinalizeAction(app, pid, targetLabel!, false, "move_mouse", currentSnapshot?.ClientOnly == true);
        }

        return ToolCallResult.TextResult(
            JsonSerializer.Serialize(new
            {
                ok = true,
                app = targetLabel,
                pid,
                action = "move_mouse",
                coordinateSpace = normSpace,
                steps,
                humanize = HumanizedPhysics.ToCanonicalName(humanize),
                snapshotSkipped = true
            }, JsonOptions),
            isError: false);
    }

    internal ToolCallResult PerformSecondaryActionLocked(Dictionary<string, object?> args)
    {
        if (!TryResolveAppOrPid(args, out string? app, out int? pid, out string targetLabel, out string? targetErr))
        {
            return ToolCallResult.TextResult(targetErr!, isError: true);
        }

        string? elementIndexStr = GetRequiredString(args, "element_index");
        if (string.IsNullOrEmpty(elementIndexStr)) return ToolCallResult.TextResult("Missing required argument: element_index", isError: true);

        string? action = GetRequiredString(args, "action");
        if (string.IsNullOrEmpty(action)) return ToolCallResult.TextResult("Missing required argument: action", isError: true);

        _ = _automationService.ResolveApp(app, pid);

        bool skipSnapshot = IsSkipSnapshot(args);
        var currentSnapshot = _automationService.GetCurrentSnapshot(app, pid);

        if (!int.TryParse(elementIndexStr, out int idx))
        {
            if (currentSnapshot == null)
            {
                return ToolCallResult.TextResult($"No app state is available for {targetLabel}. Run get_app_state before action tools.", isError: true);
            }
            return ToolCallResult.TextResult($"unknown element_index \"{elementIndexStr}\"", isError: true);
        }

        var uiaElement = _automationService.GetElementFromSnapshot(app, idx, pid);
        if (uiaElement == null)
        {
            if (currentSnapshot == null)
            {
                return ToolCallResult.TextResult($"No app state is available for {targetLabel}. Run get_app_state before action tools.", isError: true);
            }
            return ToolCallResult.TextResult($"unknown element_index \"{elementIndexStr}\"", isError: true);
        }

        InputInjector.PerformSecondaryAction(uiaElement, action, idx);
        return FinalizeAction(app, pid, targetLabel, skipSnapshot, "perform_secondary_action", currentSnapshot?.ClientOnly == true);
    }

    internal ToolCallResult ScrollLocked(Dictionary<string, object?> args)
    {
        if (!TryResolveAppOrPid(args, out string? app, out int? pid, out string targetLabel, out string? targetErr))
        {
            return ToolCallResult.TextResult(targetErr!, isError: true);
        }

        string? direction = GetRequiredString(args, "direction");
        if (string.IsNullOrEmpty(direction)) return ToolCallResult.TextResult("Missing required argument: direction", isError: true);

        string normDir = direction.Trim().ToLowerInvariant();
        if (normDir is not ("up" or "down" or "left" or "right"))
        {
            return ToolCallResult.TextResult($"Invalid scroll direction: {direction}", isError: true);
        }

        double pages = GetOptionalDouble(args, "pages") ?? 1.0;
        if (pages <= 0 || !double.IsFinite(pages))
        {
            return ToolCallResult.TextResult("pages must be > 0", isError: true);
        }

        string scrollMethod = GetOptionalString(args, "scroll_method") ?? GetOptionalString(args, "scrollMethod") ?? "auto";
        scrollMethod = scrollMethod.Trim().ToLowerInvariant();
        if (scrollMethod is not ("auto" or "app_post" or "stealth" or "global" or "hardware"))
        {
            return ToolCallResult.TextResult($"Invalid scroll_method '{scrollMethod}'. Valid options are: auto, app_post, stealth, global, hardware.", isError: true);
        }

        string? elementIndexStr = GetOptionalElementIndex(args);
        double? x = GetOptionalDouble(args, "x");
        double? y = GetOptionalDouble(args, "y");
        string? coordinateSpace = GetOptionalCoordinateSpace(args);

        if (string.IsNullOrEmpty(elementIndexStr) && (!x.HasValue || !y.HasValue))
        {
            return ToolCallResult.TextResult("scroll requires either element_index or x/y coordinates", isError: true);
        }

        bool skipSnapshot = IsSkipSnapshot(args);
        var currentSnapshot = _automationService.GetCurrentSnapshot(app, pid);
        string normSpace = WindowManager.NormalizeCoordinateSpace(
            coordinateSpace,
            hasTargetApp: true,
            snapshotClientOnly: currentSnapshot?.ClientOnly == true);

        var process = _automationService.ResolveApp(app, pid);

        ElementRecord? record = null;
        FlaUI.Core.AutomationElements.AutomationElement? uiaElement = null;

        if (!string.IsNullOrEmpty(elementIndexStr))
        {
            if (!int.TryParse(elementIndexStr, out int idx))
            {
                if (currentSnapshot == null)
                {
                    return ToolCallResult.TextResult($"No app state is available for {targetLabel}. Run get_app_state before action tools.", isError: true);
                }
                return ToolCallResult.TextResult($"unknown element_index \"{elementIndexStr}\"", isError: true);
            }

            record = _automationService.GetElementRecordFromSnapshot(app, idx, pid);
            if (record == null)
            {
                if (currentSnapshot == null)
                {
                    return ToolCallResult.TextResult($"No app state is available for {targetLabel}. Run get_app_state before action tools.", isError: true);
                }
                return ToolCallResult.TextResult($"unknown element_index \"{elementIndexStr}\"", isError: true);
            }

            uiaElement = _automationService.GetElementFromSnapshot(app, idx, pid);
        }
        else if (currentSnapshot == null && !skipSnapshot)
        {
            return ToolCallResult.TextResult($"No app state is available for {targetLabel}. Run get_app_state before action tools.", isError: true);
        }

        bool handled = false;
        if (scrollMethod == "auto" && uiaElement != null)
        {
            handled = InputInjector.InvokeScroll(uiaElement, normDir, pages);
        }

        if (!handled)
        {
            IntPtr hwnd = GetProcessHwnd(process);
            Frame? windowBounds = currentSnapshot?.WindowBounds;
            if (windowBounds == null && NativeMethods.GetWindowRect(hwnd, out var rect))
            {
                windowBounds = new Frame(rect.Left, rect.Top, Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top));
            }

            NativeMethods.POINT? screenPoint = null;
            if (record?.Frame != null && windowBounds != null)
            {
                screenPoint = InputInjector.GetScreenPoint(record.Frame, windowBounds);
            }
            else if (x.HasValue && y.HasValue)
            {
                var (originX, originY) = WindowManager.ResolveCoordinateOrigin(normSpace, hwnd, windowBounds);
                screenPoint = new NativeMethods.POINT
                {
                    X = (int)Math.Round(originX + x.Value),
                    Y = (int)Math.Round(originY + y.Value)
                };
            }

            if (screenPoint != null)
            {
                if (scrollMethod is "hardware" or "global")
                {
                    InputInjector.SendHardwareScroll(hwnd, screenPoint.Value.X, screenPoint.Value.Y, normDir, pages, stealth: false);
                }
                else if (scrollMethod == "stealth")
                {
                    InputInjector.SendHardwareScroll(hwnd, screenPoint.Value.X, screenPoint.Value.Y, normDir, pages, stealth: true);
                }
                else
                {
                    InputInjector.SendScroll(hwnd, screenPoint.Value.X, screenPoint.Value.Y, normDir, pages);
                }
            }
        }

        return FinalizeAction(app, pid, targetLabel, skipSnapshot, "scroll", currentSnapshot?.ClientOnly == true);
    }

    internal ToolCallResult DragLocked(Dictionary<string, object?> args)
    {
        if (!TryResolveAppOrPid(args, out string? app, out int? pid, out string targetLabel, out string? targetErr))
        {
            return ToolCallResult.TextResult(targetErr!, isError: true);
        }

        HumanizeProfile humanize = HumanizedPhysics.ResolveProfile(args.TryGetValue("humanize", out var rawHum) ? rawHum : null);

        double? fromX = GetOptionalDouble(args, "from_x") ?? GetOptionalDouble(args, "fromX");
        double? fromY = GetOptionalDouble(args, "from_y") ?? GetOptionalDouble(args, "fromY");
        double? toX = GetOptionalDouble(args, "to_x") ?? GetOptionalDouble(args, "toX");
        double? toY = GetOptionalDouble(args, "to_y") ?? GetOptionalDouble(args, "toY");
        string? coordinateSpace = GetOptionalCoordinateSpace(args);

        if (!fromX.HasValue) return ToolCallResult.TextResult("Missing required argument: from_x", isError: true);
        if (!fromY.HasValue) return ToolCallResult.TextResult("Missing required argument: from_y", isError: true);
        if (!toX.HasValue) return ToolCallResult.TextResult("Missing required argument: to_x", isError: true);
        if (!toY.HasValue) return ToolCallResult.TextResult("Missing required argument: to_y", isError: true);

        int durationMs = GetOptionalInt(args, "duration_ms") ?? GetOptionalInt(args, "durationMs") ?? 80;
        if (durationMs < 0 || durationMs > 10_000)
        {
            return ToolCallResult.TextResult("duration_ms must be between 0 and 10000", isError: true);
        }

        string dragMethod = GetOptionalString(args, "drag_method") ?? GetOptionalString(args, "dragMethod") ?? "auto";
        dragMethod = dragMethod.Trim().ToLowerInvariant();
        if (dragMethod is not ("auto" or "app_post" or "stealth" or "global" or "hardware"))
        {
            return ToolCallResult.TextResult($"Invalid drag_method '{dragMethod}'. Valid options are: auto, app_post, stealth, global, hardware.", isError: true);
        }

        bool skipSnapshot = IsSkipSnapshot(args);
        var currentSnapshot = _automationService.GetCurrentSnapshot(app, pid);
        string normSpace = WindowManager.NormalizeCoordinateSpace(
            coordinateSpace,
            hasTargetApp: true,
            snapshotClientOnly: currentSnapshot?.ClientOnly == true);

        if (currentSnapshot == null && !skipSnapshot)
        {
            return ToolCallResult.TextResult($"No app state is available for {targetLabel}. Run get_app_state before action tools.", isError: true);
        }

        var process = _automationService.ResolveApp(app, pid);
        IntPtr hwnd = GetProcessHwnd(process);
        Frame? windowBounds = currentSnapshot?.WindowBounds;
        if (windowBounds == null && NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            windowBounds = new Frame(rect.Left, rect.Top, Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top));
        }

        var (originX, originY) = WindowManager.ResolveCoordinateOrigin(normSpace, hwnd, windowBounds);
        int screenFromX = (int)Math.Round(originX + fromX.Value);
        int screenFromY = (int)Math.Round(originY + fromY.Value);
        int screenToX = (int)Math.Round(originX + toX.Value);
        int screenToY = (int)Math.Round(originY + toY.Value);

        if (dragMethod is "hardware" or "global")
        {
            InputInjector.SendHardwareDrag(hwnd, screenFromX, screenFromY, screenToX, screenToY, durationMs, stealth: false, humanize: humanize);
        }
        else if (dragMethod == "stealth")
        {
            InputInjector.SendHardwareDrag(hwnd, screenFromX, screenFromY, screenToX, screenToY, durationMs, stealth: true, humanize: humanize);
        }
        else
        {
            InputInjector.SendDrag(hwnd, screenFromX, screenFromY, screenToX, screenToY, durationMs, humanize: humanize);
        }

        return FinalizeAction(app, pid, targetLabel, skipSnapshot, "drag", currentSnapshot?.ClientOnly == true);
    }

    internal ToolCallResult TypeTextLocked(Dictionary<string, object?> args)
    {
        if (!TryResolveAppOrPid(args, out string? app, out int? pid, out string targetLabel, out string? targetErr))
        {
            return ToolCallResult.TextResult(targetErr!, isError: true);
        }

        string? text = GetOptionalString(args, "text", trim: false);
        if (string.IsNullOrEmpty(text)) return ToolCallResult.TextResult("Missing required argument: text", isError: true);

        string? keyMethod = GetOptionalString(args, "key_method") ?? GetOptionalString(args, "keyMethod");
        InputInjector.ValidateKeyMethod(keyMethod);
        string normMethod = string.IsNullOrWhiteSpace(keyMethod) ? "auto" : keyMethod.Trim().ToLowerInvariant();
        HumanizeProfile humanize = HumanizedPhysics.ResolveProfile(args.TryGetValue("humanize", out var rawHum) ? rawHum : null);

        bool skipSnapshot = IsSkipSnapshot(args);
        var currentSnapshot = _automationService.GetCurrentSnapshot(app, pid);
        if (currentSnapshot == null && !skipSnapshot)
        {
            return ToolCallResult.TextResult($"No app state is available for {targetLabel}. Run get_app_state before action tools.", isError: true);
        }

        var process = _automationService.ResolveApp(app, pid);
        IntPtr hwnd = GetProcessHwnd(process);

        if (normMethod is "global" or "hardware")
        {
            InputInjector.SendText(hwnd, text, humanize);
        }
        else if (normMethod == "stealth")
        {
            InputInjector.SendTextStealth(hwnd, text, humanize);
        }
        else if (normMethod == "app_post")
        {
            InputInjector.SendTextBackground(hwnd, text, humanize);
        }
        else
        {
            if (humanize != HumanizeProfile.None || !_automationService.InvokeTypeText(process, text))
            {
                InputInjector.SendTextBackground(hwnd, text, humanize);
            }
        }

        return FinalizeAction(app, pid, targetLabel, skipSnapshot, "type_text", currentSnapshot?.ClientOnly == true);
    }

    internal ToolCallResult PressKeyLocked(Dictionary<string, object?> args)
    {
        if (!TryResolveAppOrPid(args, out string? app, out int? pid, out string targetLabel, out string? targetErr))
        {
            return ToolCallResult.TextResult(targetErr!, isError: true);
        }

        string? key = GetRequiredString(args, "key");
        if (string.IsNullOrEmpty(key)) return ToolCallResult.TextResult("Missing required argument: key", isError: true);

        string? keyMethod = GetOptionalString(args, "key_method") ?? GetOptionalString(args, "keyMethod");
        InputInjector.ValidateKeyMethod(keyMethod);
        HumanizeProfile humanize = HumanizedPhysics.ResolveProfile(args.TryGetValue("humanize", out var rawHum) ? rawHum : null);

        string? keyState = GetOptionalString(args, "key_state")
                           ?? GetOptionalString(args, "keyState")
                           ?? GetOptionalString(args, "action");
        int? holdMs = GetOptionalInt(args, "hold_ms") ?? GetOptionalInt(args, "holdMs");
        int repeat = GetOptionalInt(args, "repeat") ?? 1;
        int repeatIntervalMs = GetOptionalInt(args, "repeat_interval_ms") ?? GetOptionalInt(args, "repeatIntervalMs") ?? 15;

        bool skipSnapshot = IsSkipSnapshot(args);
        var currentSnapshot = _automationService.GetCurrentSnapshot(app, pid);
        if (currentSnapshot == null && !skipSnapshot)
        {
            return ToolCallResult.TextResult($"No app state is available for {targetLabel}. Run get_app_state before action tools.", isError: true);
        }

        var process = _automationService.ResolveApp(app, pid);
        IntPtr hwnd = GetProcessHwnd(process);

        InputInjector.ExecutePressKey(hwnd, key, keyMethod, keyState, holdMs, repeat, repeatIntervalMs, humanize);
        return FinalizeAction(app, pid, targetLabel, skipSnapshot, "press_key", currentSnapshot?.ClientOnly == true);
    }

    private ToolCallResult BatchActions(Dictionary<string, object?> args)
    {
        return ActionLock.Execute(() => ExecuteBatchLocked(args));
    }

    internal ToolCallResult ExecuteBatchLocked(Dictionary<string, object?> args)
    {
        try
        {
            string? defaultApp = GetOptionalString(args, "app");
            var (defaultPid, pidErr) = GetOptionalStrictPid(args);
            if (pidErr != null)
            {
                return ToolCallResult.TextResult(pidErr, isError: true);
            }

            object? batchHumanize = args.TryGetValue("humanize", out var rawBatchHum) ? rawBatchHum : null;
            if (batchHumanize != null)
            {
                _ = HumanizedPhysics.ResolveProfile(batchHumanize);
            }

            bool stopOnError = GetOptionalBool(args, "stop_on_error") ?? GetOptionalBool(args, "stopOnError") ?? true;
            bool releaseOnError = GetOptionalBool(args, "release_on_error") ?? GetOptionalBool(args, "releaseOnError") ?? true;
            bool skipSnapshot = IsSkipSnapshot(args, defaultValue: true);

            if (!TryExtractStepDictionaries(args, out var stepList, out string? parseError))
            {
                return ToolCallResult.TextResult(parseError ?? "Invalid steps array", isError: true);
            }

            if (stepList.Count < 1 || stepList.Count > 200)
            {
                return ToolCallResult.TextResult("batch_actions requires between 1 and 200 steps", isError: true);
            }

            var sw = Stopwatch.StartNew();
            int executedSteps = 0;
            bool releasedOnError = false;
            string? firstError = null;
            string? lastTargetApp = defaultApp;
            int? lastTargetPid = defaultPid;

            for (int i = 0; i < stepList.Count; i++)
            {
                var rawStep = stepList[i];
                var stepArgs = new Dictionary<string, object?>(rawStep, StringComparer.OrdinalIgnoreCase);

                string? stepType = GetOptionalString(stepArgs, "type") ?? GetOptionalString(stepArgs, "action");
                if (string.IsNullOrWhiteSpace(stepType))
                {
                    firstError ??= $"Step {i}: missing required 'type' (or 'action') field.";
                    if (releaseOnError)
                    {
                        _ = ActiveInputTracker.ReleaseAll();
                        releasedOnError = true;
                    }
                    if (stopOnError) break;
                    continue;
                }

                string normType = stepType.Trim().ToLowerInvariant();
                if (normType == "trajectory")
                {
                    normType = "follow_trajectory";
                }

                try
                {
                    if (normType == "wait")
                    {
                        double? waitMs = GetOptionalDouble(stepArgs, "ms")
                                         ?? GetOptionalDouble(stepArgs, "duration_ms")
                                         ?? GetOptionalDouble(stepArgs, "milliseconds");
                        if (!waitMs.HasValue || !double.IsFinite(waitMs.Value) || waitMs.Value < 0 || waitMs.Value > 30_000)
                        {
                            throw new ArgumentException("wait step requires 'ms' between 0 and 30000.");
                        }
                        PrecisionTimer.WaitMs(waitMs.Value);
                        executedSteps++;
                        continue;
                    }

                    if (normType == "release_inputs")
                    {
                        _ = ActiveInputTracker.ReleaseAll();
                        executedSteps++;
                        continue;
                    }

                    if (normType is "wait_for" or "wait-for")
                    {
                        throw new ArgumentException("Nested 'wait_for' inside batch_actions is forbidden to prevent ActionLock starvation.");
                    }

                    if (normType is not ("press_key" or "click" or "move_mouse" or "follow_trajectory" or "type_text" or "scroll" or "drag"))
                    {
                        throw new ArgumentException(
                            $"Unsupported batch step type '{stepType}'. Supported types: press_key, click, move_mouse, follow_trajectory, type_text, scroll, drag, wait, release_inputs.");
                    }

                    // If 'action' was used to specify the step type (e.g. action="press_key"), remove 'action' before PressKey reads it as key_state alias
                    if (normType == "press_key" &&
                        stepArgs.TryGetValue("action", out var actVal) &&
                        string.Equals(actVal?.ToString()?.Trim(), "press_key", StringComparison.OrdinalIgnoreCase))
                    {
                        stepArgs.Remove("action");
                    }

                    if (batchHumanize != null &&
                        normType != "follow_trajectory" &&
                        !stepArgs.ContainsKey("humanize"))
                    {
                        stepArgs["humanize"] = batchHumanize;
                    }

                    bool stepHasApp = stepArgs.ContainsKey("app") && !string.IsNullOrWhiteSpace(stepArgs["app"]?.ToString());
                    bool stepHasPid = stepArgs.ContainsKey("pid") && stepArgs["pid"] != null;

                    if (!stepHasApp && !stepHasPid)
                    {
                        if (!string.IsNullOrWhiteSpace(defaultApp))
                        {
                            stepArgs["app"] = defaultApp;
                        }
                        if (defaultPid.HasValue)
                        {
                            stepArgs["pid"] = defaultPid.Value;
                        }
                    }
                    else
                    {
                        if (stepHasApp)
                        {
                            lastTargetApp = stepArgs["app"]?.ToString()?.Trim();
                        }
                        if (stepHasPid && int.TryParse(stepArgs["pid"]?.ToString(), out int sp) && sp > 0)
                        {
                            lastTargetPid = sp;
                        }
                    }

                    // Force intermediate steps to skip snapshot rebuild
                    stepArgs["skip_snapshot"] = true;

                    ToolCallResult stepRes = normType switch
                    {
                        "press_key" => PressKeyLocked(stepArgs),
                        "click" => ClickLocked(stepArgs),
                        "move_mouse" => MoveMouseLocked(stepArgs),
                        "follow_trajectory" => FollowTrajectoryLocked(stepArgs),
                        "type_text" => TypeTextLocked(stepArgs),
                        "scroll" => ScrollLocked(stepArgs),
                        "drag" => DragLocked(stepArgs),
                        _ => ToolCallResult.TextResult($"Unsupported step '{normType}'", isError: true)
                    };

                    if (stepRes.IsError)
                    {
                        string errDetail = stepRes.Content.FirstOrDefault()?.Text ?? "Unknown step error";
                        throw new InvalidOperationException(errDetail);
                    }

                    executedSteps++;
                }
                catch (Exception ex)
                {
                    firstError ??= $"Step {i} ({normType}): {ex.Message}";
                    if (releaseOnError)
                    {
                        _ = ActiveInputTracker.ReleaseAll();
                        releasedOnError = true;
                    }
                    if (stopOnError)
                    {
                        break;
                    }
                }
            }

            sw.Stop();
            var batchSummary = new BatchExecutionResult
            {
                Ok = firstError == null,
                ExecutedSteps = executedSteps,
                TotalSteps = stepList.Count,
                ElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 3),
                ReleasedOnError = releasedOnError,
                Error = firstError
            };

            string summaryJson = JsonSerializer.Serialize(batchSummary, JsonOptions);
            if (!batchSummary.Ok || skipSnapshot || (string.IsNullOrWhiteSpace(lastTargetApp) && !lastTargetPid.HasValue))
            {
                return ToolCallResult.TextResult(summaryJson, isError: !batchSummary.Ok);
            }

            PrecisionTimer.WaitMs(12);
            var prevSnap = _automationService.GetCurrentSnapshot(lastTargetApp, lastTargetPid);
            var refreshed = _automationService.BuildSnapshot(lastTargetApp, clientOnly: prevSnap?.ClientOnly == true, pid: lastTargetPid);
            var snapResult = refreshed.ToToolCallResult();
            snapResult.Content.Insert(0, new ContentItem { Type = "text", Text = summaryJson });
            return snapResult;
        }
        catch (Exception ex)
        {
            return ToolCallResult.TextResult(ex.Message, isError: true);
        }
    }

    internal ToolCallResult FollowTrajectoryLocked(Dictionary<string, object?> args)
    {
        string? app = GetOptionalString(args, "app");
        var (pid, pidErr) = GetOptionalStrictPid(args);
        if (pidErr != null)
        {
            return ToolCallResult.TextResult(pidErr, isError: true);
        }

        // follow_trajectory defaults strictly to None unless humanize is explicitly provided per-call
        HumanizeProfile humanize = HumanizedPhysics.ResolveProfile(
            args.TryGetValue("humanize", out var rawHum) ? rawHum : null,
            isTrajectoryTool: true);

        double intervalMs = GetOptionalDouble(args, "interval_ms") ?? GetOptionalDouble(args, "intervalMs") ?? (1000.0 / 60.0);
        if (!double.IsFinite(intervalMs) || intervalMs < 0.0 || intervalMs > 5000.0)
        {
            return ToolCallResult.TextResult("interval_ms must be between 0 and 5000", isError: true);
        }

        if (!TryExtractWaypoints(args, intervalMs, out var waypoints, out string? wpError))
        {
            return ToolCallResult.TextResult(wpError ?? "Invalid waypoints array", isError: true);
        }

        string? coordinateSpace = GetOptionalCoordinateSpace(args);
        string? method = GetOptionalString(args, "method");
        string? mouseButton = GetOptionalString(args, "mouse_button") ?? GetOptionalString(args, "mouseButton");
        bool holdButton = GetOptionalBool(args, "hold_button") ?? GetOptionalBool(args, "holdButton") ?? false;
        bool skipSnapshot = IsSkipSnapshot(args, defaultValue: true);

        bool hasTargetApp = !string.IsNullOrWhiteSpace(app) || pid.HasValue;
        _ = WindowManager.NormalizeCoordinateSpace(coordinateSpace, hasTargetApp);
        _ = ActiveInputTracker.NormalizeButtonName(mouseButton ?? "left");
        if (!string.IsNullOrWhiteSpace(method))
        {
            string m = method.Trim().ToLowerInvariant();
            if (m is not ("hardware" or "global" or "stealth" or "app_post" or "auto"))
            {
                return ToolCallResult.TextResult($"Invalid trajectory method '{method}'. Valid options are: hardware, global, stealth, app_post, auto.", isError: true);
            }
        }
        double prevWpTime = 0.0;
        for (int i = 0; i < waypoints.Count; i++)
        {
            var wp = waypoints[i];
            if (!double.IsFinite(wp.X) || !double.IsFinite(wp.Y) || !double.IsFinite(wp.TimeMs) || wp.TimeMs < 0.0 || wp.TimeMs > 60_000.0)
            {
                return ToolCallResult.TextResult($"Waypoint[{i}] coordinates must be finite and t_ms must be between 0 and 60000.", isError: true);
            }
            if (i > 0 && wp.TimeMs < prevWpTime)
            {
                return ToolCallResult.TextResult($"Waypoint[{i}] t_ms ({wp.TimeMs}) must be monotonically non-decreasing (>= {prevWpTime}).", isError: true);
            }
            prevWpTime = wp.TimeMs;
        }

        IntPtr hwnd = IntPtr.Zero;
        Frame? windowBounds = null;
        AppSnapshot? currentSnapshot = null;

        if (hasTargetApp)
        {
            var process = _automationService.ResolveApp(app, pid);
            hwnd = GetProcessHwnd(process);
            currentSnapshot = _automationService.GetCurrentSnapshot(app, pid);
            windowBounds = currentSnapshot?.WindowBounds;
            if (windowBounds == null && NativeMethods.GetWindowRect(hwnd, out var rect))
            {
                windowBounds = new Frame(
                    rect.Left,
                    rect.Top,
                    Math.Max(0, rect.Right - rect.Left),
                    Math.Max(0, rect.Bottom - rect.Top));
            }
        }

        var execResult = InputInjector.ExecuteTrajectory(
            hwnd,
            windowBounds,
            waypoints,
            coordinateSpace,
            currentSnapshot?.ClientOnly == true,
            method,
            mouseButton,
            holdButton,
            humanize);

        string summaryJson = JsonSerializer.Serialize(execResult, JsonOptions);
        if (skipSnapshot || !hasTargetApp)
        {
            return ToolCallResult.TextResult(summaryJson, isError: false);
        }

        PrecisionTimer.WaitMs(12);
        var refreshed = _automationService.BuildSnapshot(app, clientOnly: currentSnapshot?.ClientOnly == true, pid: pid);
        var snapResult = refreshed.ToToolCallResult();
        snapResult.Content.Insert(0, new ContentItem { Type = "text", Text = summaryJson });
        return snapResult;
    }

    private ToolCallResult WaitFor(Dictionary<string, object?> args)
    {
        var result = WaitForAsync(args, CancellationToken.None).GetAwaiter().GetResult();
        string json = JsonSerializer.Serialize(result, JsonOptions);
        return ToolCallResult.TextResult(json, isError: !result.Ok);
    }

    internal async Task<VisionWaitResult> WaitForAsync(Dictionary<string, object?> args, CancellationToken cancellationToken = default)
    {
        string? app = GetOptionalString(args, "app");
        var (pid, pidErr) = GetOptionalStrictPid(args);
        if (pidErr != null)
        {
            throw new ArgumentException(pidErr);
        }

        string? display = GetOptionalString(args, "display");
        string? coordinateSpace = GetOptionalCoordinateSpace(args);

        if (!TryExtractProbeSpecs(args, out var points, out var regions, out string? specErr, toolNameForError: "wait_for"))
        {
            throw new ArgumentException(specErr ?? "Invalid wait_for points/regions");
        }

        int? pollHz = GetOptionalInt(args, "poll_hz") ?? GetOptionalInt(args, "pollHz");
        int? timeoutMs = GetOptionalInt(args, "timeout_ms") ?? GetOptionalInt(args, "timeoutMs");
        string? matchMode = GetOptionalString(args, "match_mode") ?? GetOptionalString(args, "matchMode");
        int? consecutiveHits = GetOptionalInt(args, "consecutive_hits") ?? GetOptionalInt(args, "consecutiveHits");

        if (!TryExtractWaitReactionSpec(args, out var onMatch, out string? reactErr))
        {
            throw new ArgumentException(reactErr ?? "Invalid on_match reaction specification");
        }

        return await VisionWaitService.WaitAsync(
            _automationService,
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
            ExecuteActionLocked,
            cancellationToken).ConfigureAwait(false);
    }

    internal ToolCallResult ManageSharedFrame(Dictionary<string, object?> args)
    {
        string? action = GetRequiredString(args, "action");
        if (string.IsNullOrEmpty(action))
        {
            return ToolCallResult.TextResult("Missing required argument: action (expected 'start', 'stop', or 'status')", isError: true);
        }

        string normAction = action.Trim().ToLowerInvariant();
        if (normAction == "status")
        {
            var status = SharedFrameStreamService.GetStatus();
            return ToolCallResult.TextResult(JsonSerializer.Serialize(status, JsonOptions), isError: false);
        }

        if (normAction == "stop")
        {
            var stopped = SharedFrameStreamService.StopSync();
            return ToolCallResult.TextResult(JsonSerializer.Serialize(stopped, JsonOptions), isError: false);
        }

        if (normAction != "start")
        {
            return ToolCallResult.TextResult($"Unsupported manage_shared_frame action '{action}'. Valid options: start, stop, status.", isError: true);
        }

        string? app = GetOptionalString(args, "app");
        var (pid, pidErr) = GetOptionalStrictPid(args);
        if (pidErr != null)
        {
            return ToolCallResult.TextResult(pidErr, isError: true);
        }

        string? display = GetOptionalString(args, "display");
        bool clientOnly = GetOptionalBool(args, "client_only") ?? GetOptionalBool(args, "clientOnly") ?? true;
        double? regionX = GetOptionalDouble(args, "region_x") ?? GetOptionalDouble(args, "regionX");
        double? regionY = GetOptionalDouble(args, "region_y") ?? GetOptionalDouble(args, "regionY");
        double? regionW = GetOptionalDouble(args, "region_width") ?? GetOptionalDouble(args, "regionWidth");
        double? regionH = GetOptionalDouble(args, "region_height") ?? GetOptionalDouble(args, "regionHeight");
        int maxFps = GetOptionalInt(args, "max_fps") ?? GetOptionalInt(args, "maxFps") ?? 60;
        bool onlyOnChange = GetOptionalBool(args, "only_on_change") ?? GetOptionalBool(args, "onlyOnChange") ?? true;

        int? ownerPid = null;
        if (args.TryGetValue("owner_pid", out var rawOwner) && rawOwner != null)
        {
            if (!int.TryParse(rawOwner.ToString(), out int op) || op <= 0)
            {
                return ToolCallResult.TextResult("owner_pid must be a positive integer.", isError: true);
            }
            ownerPid = op;
        }
        else if (args.TryGetValue("ownerPid", out var rawOwnerCamel) && rawOwnerCamel != null)
        {
            if (!int.TryParse(rawOwnerCamel.ToString(), out int op) || op <= 0)
            {
                return ToolCallResult.TextResult("owner_pid must be a positive integer.", isError: true);
            }
            ownerPid = op;
        }

        var started = SharedFrameStreamService.StartSync(
            _automationService,
            app,
            pid,
            display,
            clientOnly,
            regionX,
            regionY,
            regionW,
            regionH,
            maxFps,
            onlyOnChange,
            ownerPid);

        return ToolCallResult.TextResult(JsonSerializer.Serialize(started, JsonOptions), isError: false);
    }

    private ToolCallResult ProbeScreen(Dictionary<string, object?> args)
    {
        string? app = GetOptionalString(args, "app");
        var (pid, pidErr) = GetOptionalStrictPid(args);
        if (pidErr != null)
        {
            return ToolCallResult.TextResult(pidErr, isError: true);
        }

        string? display = GetOptionalString(args, "display");
        string? coordinateSpace = GetOptionalCoordinateSpace(args);

        if (!TryExtractProbeSpecs(args, out var points, out var regions, out string? specErr))
        {
            return ToolCallResult.TextResult(specErr ?? "Invalid probe_screen points/regions", isError: true);
        }

        var result = ScreenProbe.Probe(
            _automationService,
            app,
            pid,
            display,
            coordinateSpace,
            points,
            regions);

        string json = JsonSerializer.Serialize(result, JsonOptions);
        return ToolCallResult.TextResult(json, isError: false);
    }

    private static bool TryExtractWaypoints(
        Dictionary<string, object?> args,
        double intervalMs,
        out List<TrajectoryWaypoint> waypoints,
        out string? error)
    {
        waypoints = [];
        error = null;

        if (!args.TryGetValue("waypoints", out var raw) || raw == null)
        {
            error = "Missing required argument: waypoints";
            return false;
        }

        if (raw is string jsonStr)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonStr);
                raw = JsonElementToObject(doc.RootElement);
            }
            catch (Exception ex)
            {
                error = $"Invalid JSON in waypoints: {ex.Message}";
                return false;
            }
        }
        else if (raw is JsonElement je)
        {
            raw = JsonElementToObject(je);
        }

        if (raw is not IEnumerable<object?> enumerable)
        {
            error = "waypoints must be an array of waypoint objects or [x, y, t_ms?] arrays";
            return false;
        }

        double prevTime = 0.0;
        int index = 0;
        foreach (var item in enumerable)
        {
            object? currentItem = item is JsonElement elem ? JsonElementToObject(elem) : item;

            if (currentItem is Dictionary<string, object?> dict)
            {
                var wpDict = new Dictionary<string, object?>(dict, StringComparer.OrdinalIgnoreCase);
                double? x = GetOptionalDouble(wpDict, "x");
                double? y = GetOptionalDouble(wpDict, "y");
                if (!x.HasValue || !y.HasValue)
                {
                    error = $"Waypoint[{index}] requires numeric 'x' and 'y' fields.";
                    return false;
                }

                double? explicitTime = GetOptionalDouble(wpDict, "t_ms")
                                       ?? GetOptionalDouble(wpDict, "tMs")
                                       ?? GetOptionalDouble(wpDict, "time_ms")
                                       ?? GetOptionalDouble(wpDict, "timeMs");

                double resolvedTime = explicitTime ?? (index == 0 ? 0.0 : prevTime + intervalMs);
                string btnState = GetOptionalString(wpDict, "button_state")
                                  ?? GetOptionalString(wpDict, "buttonState")
                                  ?? "none";

                waypoints.Add(new TrajectoryWaypoint
                {
                    X = x.Value,
                    Y = y.Value,
                    TimeMs = resolvedTime,
                    ButtonState = btnState
                });
                prevTime = resolvedTime;
            }
            else if (currentItem is IList<object?> arr && arr.Count >= 2)
            {
                if (!TryConvertToDouble(arr[0], out double x) || !TryConvertToDouble(arr[1], out double y))
                {
                    error = $"Waypoint[{index}] array coordinates [x, y] must be valid numbers.";
                    return false;
                }

                double resolvedTime;
                if (arr.Count >= 3 && arr[2] != null)
                {
                    if (!TryConvertToDouble(arr[2], out resolvedTime))
                    {
                        error = $"Waypoint[{index}] array t_ms must be a valid number.";
                        return false;
                    }
                }
                else
                {
                    resolvedTime = index == 0 ? 0.0 : prevTime + intervalMs;
                }

                waypoints.Add(new TrajectoryWaypoint
                {
                    X = x,
                    Y = y,
                    TimeMs = resolvedTime,
                    ButtonState = "none"
                });
                prevTime = resolvedTime;
            }
            else
            {
                error = $"Waypoint[{index}] must be an object {{x, y, t_ms?}} or numeric array [x, y, t_ms?].";
                return false;
            }

            index++;
            if (index > 2400)
            {
                error = "waypoints count exceeds maximum of 2400.";
                return false;
            }
        }

        if (waypoints.Count == 0)
        {
            error = "waypoints must contain between 1 and 2400 items.";
            return false;
        }

        return true;
    }

    internal static bool TryExtractProbeSpecs(
        Dictionary<string, object?> args,
        out List<PointProbeSpec> points,
        out List<RegionProbeSpec> regions,
        out string? error,
        string toolNameForError = "probe_screen")
    {
        points = [];
        regions = [];
        error = null;

        if (args.TryGetValue("points", out var rawPoints) && rawPoints != null)
        {
            if (!TryNormalizeObjectList(rawPoints, "points", out var pointDicts, out error))
            {
                return false;
            }

            for (int i = 0; i < pointDicts.Count; i++)
            {
                var d = pointDicts[i];
                double? x = GetOptionalDouble(d, "x");
                double? y = GetOptionalDouble(d, "y");
                if (!x.HasValue || !y.HasValue)
                {
                    error = $"points[{i}] requires numeric 'x' and 'y'.";
                    return false;
                }

                int tolerance = GetOptionalInt(d, "tolerance") ?? 16;
                string? expectedHex = GetOptionalString(d, "expected_hex") ?? GetOptionalString(d, "expectedHex");
                string? label = GetOptionalString(d, "label");
                bool negate = GetOptionalBool(d, "negate") ?? false;

                points.Add(new PointProbeSpec
                {
                    Label = label,
                    X = x.Value,
                    Y = y.Value,
                    ExpectedHex = expectedHex,
                    Tolerance = tolerance,
                    Negate = negate
                });
            }
        }

        if (args.TryGetValue("regions", out var rawRegions) && rawRegions != null)
        {
            if (!TryNormalizeObjectList(rawRegions, "regions", out var regDicts, out error))
            {
                return false;
            }

            for (int i = 0; i < regDicts.Count; i++)
            {
                var d = regDicts[i];
                double? x = GetOptionalDouble(d, "x");
                double? y = GetOptionalDouble(d, "y");
                double? w = GetOptionalDouble(d, "width");
                double? h = GetOptionalDouble(d, "height");
                if (!x.HasValue || !y.HasValue || !w.HasValue || !h.HasValue)
                {
                    error = $"regions[{i}] requires numeric 'x', 'y', 'width', and 'height'.";
                    return false;
                }

                int tolerance = GetOptionalInt(d, "tolerance") ?? 24;
                string? targetHex = GetOptionalString(d, "target_hex") ?? GetOptionalString(d, "targetHex");
                string? label = GetOptionalString(d, "label");
                int? minMatchCount = GetOptionalInt(d, "min_match_count") ?? GetOptionalInt(d, "minMatchCount");
                double? minMatchRatio = GetOptionalDouble(d, "min_match_ratio") ?? GetOptionalDouble(d, "minMatchRatio");
                string? changedFromHash = GetOptionalString(d, "changed_from_hash") ?? GetOptionalString(d, "changedFromHash");

                regions.Add(new RegionProbeSpec
                {
                    Label = label,
                    X = x.Value,
                    Y = y.Value,
                    Width = w.Value,
                    Height = h.Value,
                    TargetHex = targetHex,
                    Tolerance = tolerance,
                    MinMatchCount = minMatchCount,
                    MinMatchRatio = minMatchRatio,
                    ChangedFromHash = changedFromHash
                });
            }
        }

        if (points.Count + regions.Count < 1)
        {
            error = $"{toolNameForError} requires at least one item in 'points' or 'regions'.";
            return false;
        }

        return true;
    }

    internal static bool TryExtractWaitReactionSpec(
        Dictionary<string, object?> args,
        out VisionWaitReactionSpec? spec,
        out string? error)
    {
        spec = null;
        error = null;

        object? raw = null;
        if (!args.TryGetValue("on_match", out raw) || raw == null)
        {
            args.TryGetValue("onMatch", out raw);
        }

        if (raw == null)
        {
            return true;
        }

        if (raw is string jsonStr)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonStr);
                raw = JsonElementToObject(doc.RootElement);
            }
            catch (Exception ex)
            {
                error = $"Invalid JSON in on_match: {ex.Message}";
                return false;
            }
        }
        else if (raw is JsonElement je)
        {
            raw = JsonElementToObject(je);
        }

        if (raw is not Dictionary<string, object?> rawDict)
        {
            error = "on_match must be an object.";
            return false;
        }

        var d = new Dictionary<string, object?>(rawDict, StringComparer.OrdinalIgnoreCase);
        string? rawType = GetOptionalString(d, "type") ?? GetOptionalString(d, "action");
        if (string.IsNullOrWhiteSpace(rawType) && d.ContainsKey("steps"))
        {
            rawType = "batch_actions";
        }

        if (string.IsNullOrWhiteSpace(rawType))
        {
            error = "on_match requires 'type' (or 'action', e.g. 'click', 'press_key', 'move_mouse', 'batch_actions').";
            return false;
        }

        string normType = rawType.Trim().ToLowerInvariant() switch
        {
            "move-mouse" => "move_mouse",
            "press-key" => "press_key",
            "batch" or "batch-actions" => "batch_actions",
            "release-inputs" => "release_inputs",
            var t => t
        };

        bool useCentroid = GetOptionalBool(d, "use_matched_centroid") ?? GetOptionalBool(d, "useMatchedCentroid") ?? false;
        string? centroidLabel = null;
        string? target = GetOptionalString(d, "target");
        if (!string.IsNullOrWhiteSpace(target))
        {
            string t = target.Trim();
            if (string.Equals(t, "@centroid", StringComparison.OrdinalIgnoreCase))
            {
                useCentroid = true;
            }
            else if (t.StartsWith("@centroid:", StringComparison.OrdinalIgnoreCase))
            {
                string labelPart = t["@centroid:".Length..].Trim();
                if (string.IsNullOrWhiteSpace(labelPart))
                {
                    error = "on_match target '@centroid:<label>' requires a non-empty region label.";
                    return false;
                }
                useCentroid = true;
                centroidLabel = labelPart;
            }
            else
            {
                error = $"Invalid on_match target '{target}'. Expected '@centroid' or '@centroid:<label>'.";
                return false;
            }
        }

        double offsetX = GetOptionalDouble(d, "offset_x") ?? GetOptionalDouble(d, "offsetX") ?? 0.0;
        double offsetY = GetOptionalDouble(d, "offset_y") ?? GetOptionalDouble(d, "offsetY") ?? 0.0;

        spec = new VisionWaitReactionSpec
        {
            ActionType = normType,
            Args = d,
            UseMatchedCentroid = useCentroid,
            CentroidLabel = centroidLabel,
            OffsetX = offsetX,
            OffsetY = offsetY
        };
        return true;
    }

    private static bool TryNormalizeObjectList(
        object raw,
        string fieldName,
        out List<Dictionary<string, object?>> list,
        out string? error)
    {
        list = [];
        error = null;

        if (raw is string jsonStr)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonStr);
                raw = JsonElementToObject(doc.RootElement) ?? list;
            }
            catch (Exception ex)
            {
                error = $"Invalid JSON in {fieldName}: {ex.Message}";
                return false;
            }
        }
        else if (raw is JsonElement je)
        {
            raw = JsonElementToObject(je) ?? list;
        }

        if (raw is not IEnumerable<object?> enumerable)
        {
            error = $"{fieldName} must be an array of objects";
            return false;
        }

        foreach (var item in enumerable)
        {
            object? cur = item is JsonElement elem ? JsonElementToObject(elem) : item;
            if (cur is Dictionary<string, object?> dict)
            {
                list.Add(new Dictionary<string, object?>(dict, StringComparer.OrdinalIgnoreCase));
            }
            else
            {
                error = $"Each element in {fieldName} must be an object";
                return false;
            }
        }

        return true;
    }

    private static bool TryConvertToDouble(object? val, out double result)
    {
        result = 0;
        if (val == null) return false;
        if (val is double d) { result = d; return true; }
        if (val is float f) { result = f; return true; }
        if (val is int i) { result = i; return true; }
        if (val is long l) { result = l; return true; }
        return double.TryParse(val.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out result);
    }

    private static bool TryExtractStepDictionaries(
        Dictionary<string, object?> args,
        out List<Dictionary<string, object?>> steps,
        out string? error)
    {
        steps = [];
        error = null;

        if (!args.TryGetValue("steps", out var rawSteps) || rawSteps == null)
        {
            error = "Missing required argument: steps";
            return false;
        }

        if (rawSteps is string jsonStr)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonStr);
                rawSteps = JsonElementToObject(doc.RootElement);
            }
            catch (Exception ex)
            {
                error = $"Invalid JSON in steps: {ex.Message}";
                return false;
            }
        }
        else if (rawSteps is JsonElement je)
        {
            rawSteps = JsonElementToObject(je);
        }

        if (rawSteps is not IEnumerable<object?> enumerable)
        {
            error = "steps must be an array of step objects";
            return false;
        }

        foreach (var item in enumerable)
        {
            if (item is Dictionary<string, object?> dict)
            {
                steps.Add(dict);
            }
            else if (item is JsonElement elem && elem.ValueKind == JsonValueKind.Object)
            {
                var converted = JsonElementToObject(elem) as Dictionary<string, object?>;
                if (converted != null) steps.Add(converted);
            }
            else
            {
                error = "Each item in steps must be an object";
                return false;
            }
        }

        return true;
    }

    private static ToolCallResult ReleaseInputs()
    {
        var released = ActiveInputTracker.ReleaseAll();
        string json = JsonSerializer.Serialize(released, JsonOptions);
        return ToolCallResult.TextResult(json, isError: false);
    }

    internal ToolCallResult SetValueLocked(Dictionary<string, object?> args)
    {
        if (!TryResolveAppOrPid(args, out string? app, out int? pid, out string targetLabel, out string? targetErr))
        {
            return ToolCallResult.TextResult(targetErr!, isError: true);
        }

        string? elementIndexStr = GetRequiredString(args, "element_index");
        if (string.IsNullOrEmpty(elementIndexStr)) return ToolCallResult.TextResult("Missing required argument: element_index", isError: true);

        string? value = GetOptionalString(args, "value", trim: false);
        if (value == null) return ToolCallResult.TextResult("Missing required argument: value", isError: true);

        _ = _automationService.ResolveApp(app, pid);

        bool skipSnapshot = IsSkipSnapshot(args);
        var currentSnapshot = _automationService.GetCurrentSnapshot(app, pid);

        if (!int.TryParse(elementIndexStr, out int idx))
        {
            if (currentSnapshot == null)
            {
                return ToolCallResult.TextResult($"No app state is available for {targetLabel}. Run get_app_state before action tools.", isError: true);
            }
            return ToolCallResult.TextResult($"unknown element_index \"{elementIndexStr}\"", isError: true);
        }

        var uiaElement = _automationService.GetElementFromSnapshot(app, idx, pid);
        if (uiaElement == null && currentSnapshot == null)
        {
            return ToolCallResult.TextResult($"No app state is available for {targetLabel}. Run get_app_state before action tools.", isError: true);
        }

        InputInjector.SetValue(uiaElement, value, idx);

        return FinalizeAction(app, pid, targetLabel, skipSnapshot, "set_value", currentSnapshot?.ClientOnly == true);
    }

    internal ToolCallResult SelectTextLocked(Dictionary<string, object?> args)
    {
        if (!TryResolveAppOrPid(args, out string? app, out int? pid, out string targetLabel, out string? targetErr))
        {
            return ToolCallResult.TextResult(targetErr!, isError: true);
        }

        string? elementIndexStr = GetRequiredString(args, "element_index");
        if (string.IsNullOrEmpty(elementIndexStr)) return ToolCallResult.TextResult("Missing required argument: element_index", isError: true);

        string? text = GetOptionalString(args, "text", trim: false);
        if (string.IsNullOrEmpty(text)) return ToolCallResult.TextResult("select_text requires a non-empty text string", isError: true);

        string? prefix = GetOptionalString(args, "prefix", trim: false);
        string? suffix = GetOptionalString(args, "suffix", trim: false);
        string? selectionMode = GetOptionalString(args, "selection");

        var process = _automationService.ResolveApp(app, pid);

        bool skipSnapshot = IsSkipSnapshot(args);
        var currentSnapshot = _automationService.GetCurrentSnapshot(app, pid);

        if (!int.TryParse(elementIndexStr, out int idx))
        {
            if (currentSnapshot == null)
            {
                return ToolCallResult.TextResult($"No app state is available for {targetLabel}. Run get_app_state before action tools.", isError: true);
            }
            return ToolCallResult.TextResult($"unknown element_index \"{elementIndexStr}\"", isError: true);
        }

        var record = _automationService.GetElementRecordFromSnapshot(app, idx, pid);
        var uiaElement = _automationService.GetElementFromSnapshot(app, idx, pid);
        if (record == null && uiaElement == null)
        {
            if (currentSnapshot == null)
            {
                return ToolCallResult.TextResult($"No app state is available for {targetLabel}. Run get_app_state before action tools.", isError: true);
            }
            return ToolCallResult.TextResult($"unknown element_index \"{elementIndexStr}\"", isError: true);
        }

        IntPtr hwnd = GetProcessHwnd(process);

        InputInjector.SelectText(uiaElement, hwnd, record, text, prefix, suffix, selectionMode, idx);
        return FinalizeAction(app, pid, targetLabel, skipSnapshot, "select_text", currentSnapshot?.ClientOnly == true);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private ToolCallResult GetClipboard(Dictionary<string, object?> args)
    {
        string format = GetOptionalString(args, "format") ?? "text";
        var data = ClipboardService.GetClipboard(format);
        string json = JsonSerializer.Serialize(data, JsonOptions);
        return ToolCallResult.TextResult(json, isError: false);
    }

    private ToolCallResult SetClipboard(Dictionary<string, object?> args)
    {
        return ActionLock.Execute(() => SetClipboardLocked(args));
    }

    internal ToolCallResult SetClipboardLocked(Dictionary<string, object?> args)
    {
        string? text = GetOptionalString(args, "text", trim: false);
        string? imageBase64 = GetOptionalString(args, "image_base64") ?? GetOptionalString(args, "imageBase64");
        bool paste = GetOptionalBool(args, "paste") ?? false;

        var result = ClipboardService.SetClipboard(text, imageBase64, paste);
        string json = JsonSerializer.Serialize(result, JsonOptions);
        return ToolCallResult.TextResult(json, isError: false);
    }

    private ToolCallResult ManageWindow(Dictionary<string, object?> args)
    {
        return ActionLock.Execute(() => ManageWindowLocked(args));
    }

    internal ToolCallResult ManageWindowLocked(Dictionary<string, object?> args)
    {
        string? app = GetOptionalString(args, "app");
        string? action = GetRequiredString(args, "action");
        if (string.IsNullOrEmpty(action))
        {
            return ToolCallResult.TextResult("Missing required argument: action", isError: true);
        }

        var (pid, pidErr) = GetOptionalStrictPid(args);
        if (pidErr != null)
        {
            return ToolCallResult.TextResult(pidErr, isError: true);
        }

        double? x = GetOptionalDouble(args, "x");
        double? y = GetOptionalDouble(args, "y");
        double? width = GetOptionalDouble(args, "width");
        double? height = GetOptionalDouble(args, "height");

        var result = WindowManager.ManageWindow(app, pid, action, x, y, width, height);
        string json = JsonSerializer.Serialize(result, JsonOptions);
        return ToolCallResult.TextResult(json, isError: false);
    }

    private ToolCallResult CaptureScreen(Dictionary<string, object?> args)
    {
        string display = GetOptionalString(args, "display") ?? "all";
        double? regionX = GetOptionalDouble(args, "x");
        double? regionY = GetOptionalDouble(args, "y");
        double? regionWidth = GetOptionalDouble(args, "width");
        double? regionHeight = GetOptionalDouble(args, "height");
        string? format = GetOptionalString(args, "format");
        int? maxDimension = GetOptionalInt(args, "max_dimension") ?? GetOptionalInt(args, "maxDimension");

        var snapshot = ScreenCapture.CaptureScreen(
            display,
            regionX,
            regionY,
            regionWidth,
            regionHeight,
            format,
            maxDimension);

        string json = JsonSerializer.Serialize(snapshot, JsonOptions);

        var content = new List<ContentItem>
        {
            new() { Type = "text", Text = json }
        };

        if (!string.IsNullOrEmpty(snapshot.Frame?.Base64))
        {
            content.Add(new ContentItem
            {
                Type = "image",
                Data = snapshot.Frame.Base64,
                MimeType = string.IsNullOrWhiteSpace(snapshot.MimeType) ? "image/png" : snapshot.MimeType
            });
        }

        return new ToolCallResult
        {
            Content = content,
            IsError = false
        };
    }

    private ToolCallResult LaunchApp(Dictionary<string, object?> args)
    {
        string? path = GetRequiredString(args, "path");
        if (string.IsNullOrEmpty(path))
        {
            return ToolCallResult.TextResult("Missing required argument: path", isError: true);
        }

        string? launchArgs = GetOptionalString(args, "args");
        string? workingDir = GetOptionalString(args, "working_dir") ?? GetOptionalString(args, "workingDir");

        var result = ProcessManager.LaunchApp(path, launchArgs, workingDir);
        string json = JsonSerializer.Serialize(result, JsonOptions);
        return ToolCallResult.TextResult(json, isError: false);
    }

    private ToolCallResult CloseApp(Dictionary<string, object?> args)
    {
        string? app = GetOptionalString(args, "app");
        var (pid, pidErr) = GetOptionalStrictPid(args);
        if (pidErr != null)
        {
            return ToolCallResult.TextResult(pidErr, isError: true);
        }
        bool force = GetOptionalBool(args, "force") ?? false;
        int timeoutMs = GetOptionalInt(args, "timeout_ms") ?? GetOptionalInt(args, "timeoutMs") ?? 3000;

        var result = ProcessManager.CloseApp(app, pid, force, timeoutMs);
        string json = JsonSerializer.Serialize(result, JsonOptions);
        return ToolCallResult.TextResult(json, isError: false);
    }

    private static Dictionary<string, object?> ObjectSchema(Dictionary<string, object?> properties, List<string> required)
    {
        var schema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false
        };
        if (required.Count > 0)
        {
            schema["required"] = required;
        }
        return schema;
    }

    private static Dictionary<string, object?> StringProperty(string description) =>
        new() { ["type"] = "string", ["description"] = description };

    private static Dictionary<string, object?> NumberProperty(string description) =>
        new() { ["type"] = "number", ["description"] = description };

    private static Dictionary<string, object?> IntegerProperty(string description) =>
        new() { ["type"] = "integer", ["description"] = description };

    private static Dictionary<string, object?> PositiveIntegerProperty(string description) =>
        new() { ["type"] = "integer", ["minimum"] = 1, ["description"] = description };

    private static Dictionary<string, object?> BoundedIntegerProperty(string description, int minimum, int maximum) =>
        new() { ["type"] = "integer", ["minimum"] = minimum, ["maximum"] = maximum, ["description"] = description };

    private static Dictionary<string, object?> EnumStringProperty(string description, List<string> values) =>
        new() { ["type"] = "string", ["enum"] = values, ["description"] = description };

    private static Dictionary<string, object?> BooleanProperty(string description) =>
        new() { ["type"] = "boolean", ["description"] = description };

    private static Dictionary<string, object?> HumanizeProperty(string description) =>
        new()
        {
            ["anyOf"] = new List<object?>
            {
                new Dictionary<string, object?> { ["type"] = "boolean" },
                new Dictionary<string, object?> { ["type"] = "string", ["enum"] = new List<string> { "none", "subtle", "natural", "fast" } }
            },
            ["description"] = description
        };

    private static Dictionary<string, object?> TextLimitProperty(string description) =>
        new()
        {
            ["anyOf"] = new List<object?>
            {
                new Dictionary<string, object?> { ["type"] = "integer", ["minimum"] = 1 },
                new Dictionary<string, object?> { ["type"] = "string", ["enum"] = new List<string> { "max" } }
            },
            ["description"] = description
        };

    private static string? GetRequiredString(Dictionary<string, object?> args, string key)
    {
        if (args.TryGetValue(key, out var val) && val != null)
        {
            return val.ToString()?.Trim();
        }
        return null;
    }

    private static string? GetOptionalString(Dictionary<string, object?> args, string key, bool trim = true)
    {
        if (args.TryGetValue(key, out var val) && val != null)
        {
            string s = val is JsonElement je && je.ValueKind == JsonValueKind.String ? je.GetString() ?? "" : val.ToString() ?? "";
            return trim ? s.Trim() : s;
        }
        return null;
    }

    private static string? GetOptionalElementIndex(Dictionary<string, object?> args)
    {
        if (args.TryGetValue("element_index", out var val) && val != null)
        {
            return val.ToString()?.Trim();
        }
        if (args.TryGetValue("elementIndex", out var camelVal) && camelVal != null)
        {
            return camelVal.ToString()?.Trim();
        }
        return null;
    }

    private static double? GetOptionalDouble(Dictionary<string, object?> args, string key)
    {
        if (args.TryGetValue(key, out var val) && val != null)
        {
            if (val is double d) return d;
            if (val is float f) return f;
            if (val is int i) return i;
            if (val is long l) return l;
            if (double.TryParse(val.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed)) return parsed;
        }
        return null;
    }

    private static (int? Value, string? Error) GetOptionalPositiveInt(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var val) || val == null)
        {
            return (null, null);
        }

        if (int.TryParse(val.ToString(), out int i) && i > 0)
        {
            return (i, null);
        }
        return (null, $"{key} must be a positive integer");
    }

    private static bool? GetOptionalBool(Dictionary<string, object?> args, string key)
    {
        if (args.TryGetValue(key, out var val) && val != null)
        {
            if (val is bool b) return b;
            if (bool.TryParse(val.ToString(), out bool parsed)) return parsed;
        }
        return null;
    }

    private static int? GetOptionalInt(Dictionary<string, object?> args, string key)
    {
        if (args.TryGetValue(key, out var val) && val != null)
        {
            if (val is int i) return i;
            if (val is long l) return (int)l;
            if (val is double d) return (int)d;
            if (int.TryParse(val.ToString(), out int parsed)) return parsed;
        }
        return null;
    }

    private static (int? Value, string? Error) GetOptionalTextLimit(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var val) || val == null)
        {
            return (null, null);
        }

        string str = val.ToString()?.Trim() ?? "";
        if (string.Equals(str, "max", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        if (int.TryParse(str, out int limit) && limit > 0)
        {
            return (limit, null);
        }

        return (null, $"{key} must be a positive integer or max");
    }

    internal static object? JsonElementToObject(JsonElement elem) =>
        elem.ValueKind switch
        {
            JsonValueKind.String => elem.GetString(),
            JsonValueKind.Number => elem.TryGetInt64(out long l) ? (object)l : elem.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => elem.EnumerateArray().Select(JsonElementToObject).ToList(),
            JsonValueKind.Object => elem.EnumerateObject().ToDictionary(p => p.Name, p => JsonElementToObject(p.Value), StringComparer.OrdinalIgnoreCase),
            _ => elem.ToString()
        };
}
