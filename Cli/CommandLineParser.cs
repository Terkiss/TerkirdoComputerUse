using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using TerkirdoComputer.Core;
using TerkirdoComputer.Mcp;
using TerkirdoComputer.Rest;
using TerkirdoComputer.Windows;

namespace TerkirdoComputer.Cli;

public static class CommandLineParser
{
    public const string Version = "2.0.0";

    public static int Run(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0)
        {
            stdout.Write(HelpText(""));
            return 0;
        }

        string command = args[0].ToLowerInvariant();
        try
        {
            switch (command)
            {
                case "-h":
                case "--help":
                case "help":
                    string topic = args.Length > 1 ? args[1] : "";
                    stdout.Write(HelpText(topic));
                    return 0;

                case "-v":
                case "--version":
                case "version":
                    stdout.WriteLine(Version);
                    return 0;

                case "mcp":
                    using (var automation = new AutomationService())
                    {
                        var server = new McpServer(automation);
                        server.Run(stdin, stdout);
                    }
                    return 0;

                case "doctor":
                    return RunDoctor(args[1..], stdout, stderr);

                case "config":
                    return RunConfig(args[1..], stdout, stderr);

                case "list-apps":
                    using (var automation = new AutomationService())
                    {
                        var handlers = new ToolHandlers(automation);
                        var result = handlers.CallTool("list_apps", new Dictionary<string, object?>());
                        if (result.IsError)
                        {
                            stderr.WriteLine(result.Content[0].Text);
                            return 1;
                        }
                        stdout.WriteLine(result.Content[0].Text);
                    }
                    return 0;

                case "snapshot":
                    return RunSnapshot(args[1..], stdout, stderr);

                case "call":
                    return RunCall(args[1..], stdout, stderr);

                case "install-codex-mcp":
                    return Installers.InstallCodexMcp();

                case "install-claude-mcp":
                    return Installers.InstallClaudeMcp();

                case "install-gemini-mcp":
                    return Installers.InstallGeminiMcp();

                case "install-opencode-mcp":
                    return Installers.InstallOpencodeMcp();

                case "install-skill":
                    string? skillPath = args.Length > 1 ? args[1] : null;
                    return Installers.InstallSkill(skillPath);

                case "serve":
                    return RunServe(args[1..], stdout, stderr);

                case "clipboard":
                    return RunClipboard(args[1..], stdout, stderr);

                case "window":
                    return RunWindow(args[1..], stdout, stderr);

                case "screen":
                    return RunScreen(args[1..], stdout, stderr);

                case "follow-trajectory":
                case "follow_trajectory":
                case "trajectory":
                    return RunTrajectory(args[1..], stdout, stderr);

                case "probe-screen":
                case "probe_screen":
                case "probe":
                    return RunProbe(args[1..], stdout, stderr);

                case "wait-for":
                case "wait_for":
                    return RunWaitFor(args[1..], stdout, stderr);

                case "shared-frame":
                case "shared_frame":
                case "manage-shared-frame":
                case "manage_shared_frame":
                    return RunSharedFrame(args[1..], stdout, stderr);

                case "release-inputs":
                case "release_inputs":
                    return RunReleaseInputs(stdout);

                case "launch":
                    return RunLaunch(args[1..], stdout, stderr);

                case "close":
                    return RunClose(args[1..], stdout, stderr);

                default:
                    stderr.WriteLine($"unknown command: {args[0]}\n\n{HelpText("")}");
                    return 1;
            }
        }
        catch (Exception ex)
        {
            stderr.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int RunDoctor(string[] args, TextWriter stdout, TextWriter stderr)
    {
        bool jsonOutput = false;
        foreach (string arg in args)
        {
            if (arg == "--json")
            {
                jsonOutput = true;
            }
            else
            {
                stderr.WriteLine($"unknown doctor option: {arg}");
                return 1;
            }
        }

        bool dpiPerMonitor = NativeMethods.EnsurePerMonitorDpiAwareness();
        bool elevated = false;
        try
        {
            elevated = NativeMethods.IsProcessElevated(Process.GetCurrentProcess().Handle);
        }
        catch { }

        int screenWidth = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        int screenHeight = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
        bool dxgiSupported = ScreenCapture.IsDxgiSupported();

        if (jsonOutput)
        {
            var payload = new
            {
                ok = true,
                platform = "windows",
                version = Version,
                elevated,
                displayWidth = screenWidth,
                displayHeight = screenHeight,
                dpiAwareness = dpiPerMonitor ? "PerMonitorV2" : "System",
                dxgiDesktopDuplication = dxgiSupported,
                gdiScreenCapture = true,
                configPath = OcuConfig.GetConfigFilePath()
            };
            stdout.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        stdout.WriteLine("Windows runtime: UI Automation and Win32 window-message bridge are available when this process runs in the signed-in desktop session.");
        stdout.WriteLine($"  - Process Elevation: {(elevated ? "Elevated (Administrator)" : "Standard User")}");
        stdout.WriteLine($"  - Display Resolution: {screenWidth}x{screenHeight}");
        stdout.WriteLine($"  - DPI Awareness: {(dpiPerMonitor ? "Per-Monitor V2" : "System")}");
        stdout.WriteLine($"  - DXGI Desktop Duplication: {(dxgiSupported ? "Available" : "Fallback to GDI BitBlt")}");
        stdout.WriteLine(dxgiSupported ? "  - GDI Screen Capture: Active (Fallback)" : "  - GDI Screen Capture: Active (DXGI: Disabled/Fallback)");
        stdout.WriteLine($"  - Framework: .NET 10.0 (Native C#)");
        stdout.WriteLine($"  - UIPI Defense: Active (Monitors target process elevation token)");
        return 0;
    }

    private static int RunConfig(string[] args, TextWriter stdout, TextWriter stderr)
    {
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        if (sub == "--json")
        {
            sub = "list";
        }

        switch (sub)
        {
            case "list":
            {
                bool asJson = args.Any(a => a == "--json");
                var values = OcuConfig.GetEffectiveValues();
                if (asJson)
                {
                    stdout.WriteLine(JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
                }
                else
                {
                    stdout.WriteLine($"Config file: {OcuConfig.GetConfigFilePath()}");
                    foreach (var kv in values)
                    {
                        stdout.WriteLine($"{kv.Key} = {OcuConfig.FormatValue(kv.Value)}");
                    }
                }
                return 0;
            }

            case "path":
                stdout.WriteLine(OcuConfig.GetConfigFilePath());
                return 0;

            case "get":
                if (args.Length < 2)
                {
                    stderr.WriteLine("Usage: config get <key>");
                    return 1;
                }
                stdout.WriteLine(OcuConfig.FormatValue(OcuConfig.GetValue(args[1])));
                return 0;

            case "set":
                if (args.Length < 3)
                {
                    stderr.WriteLine("Usage: config set <key> <value>");
                    return 1;
                }
                var updated = OcuConfig.SetValue(args[1], args[2]);
                stdout.WriteLine($"{args[1]} = {OcuConfig.FormatValue(updated)}");
                return 0;

            case "reset":
                string? key = args.Length > 1 ? args[1] : null;
                OcuConfig.Reset(key);
                stdout.WriteLine(key == null ? "Reset all configuration values to defaults." : $"Reset {key} to default.");
                return 0;

            default:
                stderr.WriteLine($"unknown config subcommand: {args[0]}");
                return 1;
        }
    }

    private static int RunSnapshot(string[] args, TextWriter stdout, TextWriter stderr)
    {
        string app = "";
        int? textLimit = null;
        bool textLimitMax = false;
        int? maxTreeNodes = null;
        int? maxTreeDepth = null;
        bool includeAx = true;
        bool includeScreenshot = true;
        bool clientOnly = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--text-limit":
                    i++;
                    if (i >= args.Length)
                    {
                        stderr.WriteLine("--text-limit requires a positive integer or max value");
                        return 1;
                    }
                    if (string.Equals(args[i], "max", StringComparison.OrdinalIgnoreCase))
                    {
                        textLimitMax = true;
                    }
                    else if (int.TryParse(args[i], out int tl) && tl > 0)
                    {
                        textLimit = tl;
                    }
                    else
                    {
                        stderr.WriteLine("--text-limit requires a positive integer or max value");
                        return 1;
                    }
                    break;

                case "--max-tree-nodes":
                    i++;
                    if (i >= args.Length || !int.TryParse(args[i], out int mn) || mn <= 0)
                    {
                        stderr.WriteLine("--max-tree-nodes requires a positive integer value");
                        return 1;
                    }
                    maxTreeNodes = mn;
                    break;

                case "--max-tree-depth":
                    i++;
                    if (i >= args.Length || !int.TryParse(args[i], out int md) || md <= 0)
                    {
                        stderr.WriteLine("--max-tree-depth requires a positive integer value");
                        return 1;
                    }
                    maxTreeDepth = md;
                    break;

                case "--include-ax":
                    i++;
                    if (i >= args.Length || !TryParseCliBool(args[i], out includeAx))
                    {
                        stderr.WriteLine("--include-ax requires true or false");
                        return 1;
                    }
                    break;

                case "--include-screenshot":
                    i++;
                    if (i >= args.Length || !TryParseCliBool(args[i], out includeScreenshot))
                    {
                        stderr.WriteLine("--include-screenshot requires true or false");
                        return 1;
                    }
                    break;

                case "--client-only":
                    if (i + 1 < args.Length && TryParseCliBool(args[i + 1], out bool parsedCo))
                    {
                        i++;
                        clientOnly = parsedCo;
                    }
                    else
                    {
                        clientOnly = true;
                    }
                    break;

                default:
                    if (arg.StartsWith('-'))
                    {
                        stderr.WriteLine($"unknown snapshot option: {arg}");
                        return 1;
                    }
                    if (!string.IsNullOrEmpty(app))
                    {
                        stderr.WriteLine("snapshot accepts exactly one app name, process name, window title, or pid");
                        return 1;
                    }
                    app = arg;
                    break;
            }
        }

        if (string.IsNullOrEmpty(app))
        {
            stderr.WriteLine("snapshot requires an app name, process name, window title, or pid");
            return 1;
        }

        if (!includeAx && !includeScreenshot)
        {
            stderr.WriteLine("At least one of --include-ax or --include-screenshot must be true");
            return 1;
        }

        var toolArgs = new Dictionary<string, object?>
        {
            ["app"] = app,
            ["include_ax"] = includeAx,
            ["include_screenshot"] = includeScreenshot,
            ["client_only"] = clientOnly
        };
        if (textLimitMax) toolArgs["text_limit"] = "max";
        else if (textLimit.HasValue) toolArgs["text_limit"] = textLimit.Value;

        if (maxTreeNodes.HasValue) toolArgs["max_tree_nodes"] = maxTreeNodes.Value;
        if (maxTreeDepth.HasValue) toolArgs["max_tree_depth"] = maxTreeDepth.Value;

        using (var automation = new AutomationService())
        {
            var handlers = new ToolHandlers(automation);
            var result = handlers.CallTool("get_app_state", toolArgs);
            if (result.IsError)
            {
                stderr.WriteLine(result.Content[0].Text);
                return 1;
            }
            stdout.WriteLine(result.Content[0].Text);
        }

        return 0;
    }

    private static bool TryParseCliBool(string raw, out bool value)
    {
        value = true;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        switch (raw.Trim().ToLowerInvariant())
        {
            case "1" or "true" or "yes" or "on":
                value = true;
                return true;
            case "0" or "false" or "no" or "off":
                value = false;
                return true;
            default:
                return false;
        }
    }

    private static int RunCall(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0)
        {
            stderr.WriteLine("call requires a tool name or --calls/--calls-file");
            return 1;
        }

        string toolName = "";
        string argsJson = "";
        string argsFile = "";
        string callsJson = "";
        string callsFile = "";

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--args":
                    i++;
                    if (i >= args.Length) { stderr.WriteLine("--args requires a value"); return 1; }
                    argsJson = args[i];
                    break;
                case "--args-file":
                    i++;
                    if (i >= args.Length) { stderr.WriteLine("--args-file requires a value"); return 1; }
                    argsFile = args[i];
                    break;
                case "--calls":
                    i++;
                    if (i >= args.Length) { stderr.WriteLine("--calls requires a value"); return 1; }
                    callsJson = args[i];
                    break;
                case "--calls-file":
                    i++;
                    if (i >= args.Length) { stderr.WriteLine("--calls-file requires a value"); return 1; }
                    callsFile = args[i];
                    break;
                default:
                    if (arg.StartsWith('-'))
                    {
                        stderr.WriteLine($"unknown call option: {arg}");
                        return 1;
                    }
                    if (!string.IsNullOrEmpty(toolName))
                    {
                        stderr.WriteLine("call accepts at most one tool name");
                        return 1;
                    }
                    toolName = arg;
                    break;
            }
        }

        using var automation = new AutomationService();
        var handlers = new ToolHandlers(automation);
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };

        if (!string.IsNullOrEmpty(callsJson) || !string.IsNullOrEmpty(callsFile))
        {
            if (!string.IsNullOrEmpty(toolName) || !string.IsNullOrEmpty(argsJson) || !string.IsNullOrEmpty(argsFile))
            {
                stderr.WriteLine("call sequence does not accept a tool name, --args, or --args-file");
                return 1;
            }

            string raw = !string.IsNullOrEmpty(callsJson) ? callsJson : File.ReadAllText(callsFile);
            var callArray = JsonNode.Parse(raw)?.AsArray();
            if (callArray == null)
            {
                stderr.WriteLine("Invalid JSON array in calls");
                return 1;
            }

            var outputs = new List<Dictionary<string, object?>>();
            bool hasError = false;

            foreach (var item in callArray)
            {
                string itemTool = item?["tool"]?.ToString() ?? item?["name"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(itemTool))
                {
                    stderr.WriteLine("call sequence item requires a non-empty tool");
                    return 1;
                }

                var itemArgs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                var argsNode = item?["args"] ?? item?["arguments"];
                if (argsNode is JsonObject obj)
                {
                    foreach (var prop in obj)
                    {
                        itemArgs[prop.Key] = JsonNodeToObject(prop.Value);
                    }
                }

                var result = handlers.CallTool(itemTool, itemArgs);
                outputs.Add(new() { ["tool"] = itemTool, ["result"] = result });

                if (result.IsError)
                {
                    hasError = true;
                    break;
                }
            }

            stdout.WriteLine(JsonSerializer.Serialize(outputs, jsonOptions));
            return hasError ? 1 : 0;
        }

        if (string.IsNullOrEmpty(toolName))
        {
            stderr.WriteLine("call requires a tool name or --calls/--calls-file");
            return 1;
        }

        var callArgs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(argsJson) || !string.IsNullOrEmpty(argsFile))
        {
            string raw = !string.IsNullOrEmpty(argsJson) ? argsJson : File.ReadAllText(argsFile);
            var obj = JsonNode.Parse(raw)?.AsObject();
            if (obj != null)
            {
                foreach (var prop in obj)
                {
                    callArgs[prop.Key] = JsonNodeToObject(prop.Value);
                }
            }
        }

        var callResult = handlers.CallTool(toolName, callArgs);
        stdout.WriteLine(JsonSerializer.Serialize(callResult, jsonOptions));
        return callResult.IsError ? 1 : 0;
    }

    private static object? JsonNodeToObject(JsonNode? node)
    {
        if (node == null) return null;
        if (node is JsonObject obj)
        {
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in obj)
            {
                dict[kv.Key] = JsonNodeToObject(kv.Value);
            }
            return dict;
        }
        if (node is JsonArray arr)
        {
            return arr.Select(JsonNodeToObject).ToList();
        }
        if (node is JsonValue val)
        {
            using var doc = JsonDocument.Parse(val.ToJsonString());
            return ToolHandlers.JsonElementToObject(doc.RootElement);
        }
        return node.ToString();
    }

    private static int RunServe(string[] args, TextWriter stdout, TextWriter stderr)
    {
        int port = 5005;
        string? token = null;
        string host = "127.0.0.1";
        bool allowRemote = false;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--port" && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out int p)) port = p;
            }
            else if (args[i] == "--token" && i + 1 < args.Length)
            {
                token = args[++i];
            }
            else if (args[i] == "--host" && i + 1 < args.Length)
            {
                host = args[++i];
            }
            else if (args[i] == "--allow-remote" || args[i] == "-r")
            {
                allowRemote = true;
            }
        }

        using var automation = new AutomationService();
        return Rest.RestServer.RunAsync(automation, port, token, host, allowRemote).GetAwaiter().GetResult();
    }

    private static int RunClipboard(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0)
        {
            stderr.WriteLine("Usage: clipboard <get|set> [options]");
            return 1;
        }

        string sub = args[0].ToLowerInvariant();
        if (sub == "get")
        {
            string format = "text";
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--format" && i + 1 < args.Length)
                {
                    format = args[++i];
                }
            }

            var data = ClipboardService.GetClipboard(format);
            stdout.WriteLine(JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        if (sub == "set")
        {
            if (args.Length < 2)
            {
                stderr.WriteLine("Usage: clipboard set <text> [--paste]");
                return 1;
            }

            bool paste = false;
            var textParts = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--paste")
                {
                    paste = true;
                }
                else
                {
                    textParts.Add(args[i]);
                }
            }

            if (textParts.Count == 0)
            {
                stderr.WriteLine("Usage: clipboard set <text> [--paste]");
                return 1;
            }

            string text = string.Join(" ", textParts);
            var result = ActionLock.Execute(() => ClipboardService.SetClipboard(text, null, paste));
            stdout.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        stderr.WriteLine($"unknown clipboard action: {args[0]}");
        return 1;
    }

    private static int RunWindow(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length < 1)
        {
            stderr.WriteLine("Usage: window <app> <action> [--pid <n>] [--x <n>] [--y <n>] [--width <n>] [--height <n>]");
            return 1;
        }

        string? app = null;
        string? action = null;
        int? pid = null;
        double? x = null;
        double? y = null;
        double? width = null;
        double? height = null;

        var positional = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pid" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int p)) pid = p;
                    break;
                case "--x" when i + 1 < args.Length:
                    if (double.TryParse(args[++i], out double px)) x = px;
                    break;
                case "--y" when i + 1 < args.Length:
                    if (double.TryParse(args[++i], out double py)) y = py;
                    break;
                case "--width" when i + 1 < args.Length:
                    if (double.TryParse(args[++i], out double pw)) width = pw;
                    break;
                case "--height" when i + 1 < args.Length:
                    if (double.TryParse(args[++i], out double ph)) height = ph;
                    break;
                default:
                    if (!args[i].StartsWith('-'))
                    {
                        positional.Add(args[i]);
                    }
                    break;
            }
        }

        if (positional.Count >= 2)
        {
            app = positional[0];
            action = positional[1];
        }
        else if (positional.Count == 1)
        {
            if (pid.HasValue)
            {
                action = positional[0];
            }
            else
            {
                app = positional[0];
            }
        }

        if (string.IsNullOrEmpty(action))
        {
            stderr.WriteLine("Usage: window <app> <action> [--pid <n>] [--x <n>] [--y <n>] [--width <n>] [--height <n>]");
            return 1;
        }

        var result = ActionLock.Execute(() => WindowManager.ManageWindow(app, pid, action, x, y, width, height));
        stdout.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static int RunScreen(string[] args, TextWriter stdout, TextWriter stderr)
    {
        string display = "all";
        double? x = null;
        double? y = null;
        double? width = null;
        double? height = null;
        string? format = null;
        int? maxDimension = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--display" when i + 1 < args.Length:
                    display = args[++i];
                    break;
                case "--x" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double px))
                    {
                        stderr.WriteLine("--x requires a valid number");
                        return 1;
                    }
                    x = px;
                    break;
                case "--y" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double py))
                    {
                        stderr.WriteLine("--y requires a valid number");
                        return 1;
                    }
                    y = py;
                    break;
                case "--width" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double pw))
                    {
                        stderr.WriteLine("--width requires a valid number");
                        return 1;
                    }
                    width = pw;
                    break;
                case "--height" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ph))
                    {
                        stderr.WriteLine("--height requires a valid number");
                        return 1;
                    }
                    height = ph;
                    break;
                case "--format" when i + 1 < args.Length:
                    format = args[++i];
                    break;
                case "--max-dimension" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out int md))
                    {
                        stderr.WriteLine("--max-dimension requires an integer");
                        return 1;
                    }
                    maxDimension = md;
                    break;
                default:
                    stderr.WriteLine($"unknown screen option: {args[i]}");
                    return 1;
            }
        }

        try
        {
            var snapshot = ScreenCapture.CaptureScreen(display, x, y, width, height, format, maxDimension);
            stdout.WriteLine(JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static int RunTrajectory(string[] args, TextWriter stdout, TextWriter stderr)
    {
        string? app = null;
        int? pid = null;
        string? waypointsJson = null;
        string? waypointsFile = null;
        double? intervalMs = null;
        string? coordinateSpace = null;
        string? method = null;
        string? mouseButton = null;
        bool holdButton = false;
        string? humanize = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--app" when i + 1 < args.Length:
                    app = args[++i];
                    break;
                case "--pid" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out int p) || p <= 0)
                    {
                        stderr.WriteLine("--pid requires a positive integer");
                        return 1;
                    }
                    pid = p;
                    break;
                case "--waypoints" when i + 1 < args.Length:
                    waypointsJson = args[++i];
                    break;
                case "--waypoints-file" when i + 1 < args.Length:
                    waypointsFile = args[++i];
                    break;
                case "--interval-ms" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double im))
                    {
                        stderr.WriteLine("--interval-ms requires a valid number");
                        return 1;
                    }
                    intervalMs = im;
                    break;
                case "--coordinate-space" when i + 1 < args.Length:
                    coordinateSpace = args[++i];
                    break;
                case "--method" when i + 1 < args.Length:
                    method = args[++i];
                    break;
                case "--mouse-button" when i + 1 < args.Length:
                    mouseButton = args[++i];
                    break;
                case "--humanize" when i + 1 < args.Length:
                    humanize = args[++i];
                    break;
                case "--hold-button":
                    if (i + 1 < args.Length && TryParseCliBool(args[i + 1], out bool hb))
                    {
                        i++;
                        holdButton = hb;
                    }
                    else
                    {
                        holdButton = true;
                    }
                    break;
                default:
                    stderr.WriteLine($"unknown trajectory option: {args[i]}");
                    return 1;
            }
        }

        string? rawWp = !string.IsNullOrEmpty(waypointsJson)
            ? waypointsJson
            : (!string.IsNullOrEmpty(waypointsFile) ? File.ReadAllText(waypointsFile) : null);

        if (string.IsNullOrWhiteSpace(rawWp))
        {
            stderr.WriteLine("Usage: follow-trajectory --waypoints '<json-array>' [--app <name|pid:N>] [--pid <N>] [--interval-ms <ms>] [--coordinate-space <client|window|screen>] [--method <hardware|app_post|stealth>] [--humanize <none|subtle|natural|fast>] [--hold-button]");
            return 1;
        }

        var toolArgs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["waypoints"] = rawWp,
            ["hold_button"] = holdButton,
            ["skip_snapshot"] = true
        };
        if (!string.IsNullOrEmpty(app)) toolArgs["app"] = app;
        if (pid.HasValue) toolArgs["pid"] = pid.Value;
        if (intervalMs.HasValue) toolArgs["interval_ms"] = intervalMs.Value;
        if (!string.IsNullOrEmpty(coordinateSpace)) toolArgs["coordinate_space"] = coordinateSpace;
        if (!string.IsNullOrEmpty(method)) toolArgs["method"] = method;
        if (!string.IsNullOrEmpty(mouseButton)) toolArgs["mouse_button"] = mouseButton;
        if (!string.IsNullOrEmpty(humanize)) toolArgs["humanize"] = humanize;

        using var automation = new AutomationService();
        var handlers = new ToolHandlers(automation);
        var result = handlers.CallTool("follow_trajectory", toolArgs);
        if (result.IsError)
        {
            stderr.WriteLine(result.Content.FirstOrDefault()?.Text ?? "follow_trajectory failed");
            return 1;
        }

        stdout.WriteLine(result.Content.FirstOrDefault()?.Text ?? "{}");
        return 0;
    }

    private static int RunProbe(string[] args, TextWriter stdout, TextWriter stderr)
    {
        string? app = null;
        int? pid = null;
        string? display = null;
        string? coordinateSpace = null;
        string? pointsJson = null;
        string? regionsJson = null;
        string? argsJson = null;
        string? argsFile = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--app" when i + 1 < args.Length:
                    app = args[++i];
                    break;
                case "--pid" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out int p) || p <= 0)
                    {
                        stderr.WriteLine("--pid requires a positive integer");
                        return 1;
                    }
                    pid = p;
                    break;
                case "--display" when i + 1 < args.Length:
                    display = args[++i];
                    break;
                case "--coordinate-space" when i + 1 < args.Length:
                    coordinateSpace = args[++i];
                    break;
                case "--points" when i + 1 < args.Length:
                    pointsJson = args[++i];
                    break;
                case "--regions" when i + 1 < args.Length:
                    regionsJson = args[++i];
                    break;
                case "--args" when i + 1 < args.Length:
                    argsJson = args[++i];
                    break;
                case "--args-file" when i + 1 < args.Length:
                    argsFile = args[++i];
                    break;
                default:
                    stderr.WriteLine($"unknown probe option: {args[i]}");
                    return 1;
            }
        }

        var toolArgs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(argsJson) || !string.IsNullOrEmpty(argsFile))
        {
            string raw = !string.IsNullOrEmpty(argsJson) ? argsJson : File.ReadAllText(argsFile!);
            var obj = JsonNode.Parse(raw)?.AsObject();
            if (obj != null)
            {
                foreach (var kv in obj)
                {
                    toolArgs[kv.Key] = JsonNodeToObject(kv.Value);
                }
            }
        }

        if (!string.IsNullOrEmpty(app)) toolArgs["app"] = app;
        if (pid.HasValue) toolArgs["pid"] = pid.Value;
        if (!string.IsNullOrEmpty(display)) toolArgs["display"] = display;
        if (!string.IsNullOrEmpty(coordinateSpace)) toolArgs["coordinate_space"] = coordinateSpace;
        if (!string.IsNullOrEmpty(pointsJson)) toolArgs["points"] = pointsJson;
        if (!string.IsNullOrEmpty(regionsJson)) toolArgs["regions"] = regionsJson;

        using var automation = new AutomationService();
        var handlers = new ToolHandlers(automation);
        var result = handlers.CallTool("probe_screen", toolArgs);
        if (result.IsError)
        {
            stderr.WriteLine(result.Content.FirstOrDefault()?.Text ?? "probe_screen failed");
            return 1;
        }

        stdout.WriteLine(result.Content.FirstOrDefault()?.Text ?? "{}");
        return 0;
    }

    private static int RunWaitFor(string[] args, TextWriter stdout, TextWriter stderr)
    {
        string? app = null;
        int? pid = null;
        string? display = null;
        string? coordinateSpace = null;
        string? pointsJson = null;
        string? regionsJson = null;
        int? pollHz = null;
        int? timeoutMs = null;
        string? matchMode = null;
        int? consecutiveHits = null;
        string? onMatchJson = null;
        string? argsJson = null;
        string? argsFile = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--app" when i + 1 < args.Length:
                    app = args[++i];
                    break;
                case "--pid" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out int p) || p <= 0)
                    {
                        stderr.WriteLine("--pid requires a positive integer");
                        return 1;
                    }
                    pid = p;
                    break;
                case "--display" when i + 1 < args.Length:
                    display = args[++i];
                    break;
                case "--coordinate-space" when i + 1 < args.Length:
                    coordinateSpace = args[++i];
                    break;
                case "--points" when i + 1 < args.Length:
                    pointsJson = args[++i];
                    break;
                case "--regions" when i + 1 < args.Length:
                    regionsJson = args[++i];
                    break;
                case "--poll-hz" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out int ph) || ph < 1 || ph > 240)
                    {
                        stderr.WriteLine("--poll-hz requires an integer between 1 and 240");
                        return 1;
                    }
                    pollHz = ph;
                    break;
                case "--timeout-ms" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out int tm) || tm < 10 || tm > 60000)
                    {
                        stderr.WriteLine("--timeout-ms requires an integer between 10 and 60000");
                        return 1;
                    }
                    timeoutMs = tm;
                    break;
                case "--match-mode" when i + 1 < args.Length:
                    matchMode = args[++i];
                    break;
                case "--consecutive-hits" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out int ch) || ch < 1 || ch > 10)
                    {
                        stderr.WriteLine("--consecutive-hits requires an integer between 1 and 10");
                        return 1;
                    }
                    consecutiveHits = ch;
                    break;
                case "--on-match" when i + 1 < args.Length:
                    onMatchJson = args[++i];
                    break;
                case "--args" when i + 1 < args.Length:
                    argsJson = args[++i];
                    break;
                case "--args-file" when i + 1 < args.Length:
                    argsFile = args[++i];
                    break;
                default:
                    stderr.WriteLine($"unknown wait-for option: {args[i]}");
                    return 1;
            }
        }

        var toolArgs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(argsJson) || !string.IsNullOrEmpty(argsFile))
        {
            string raw = !string.IsNullOrEmpty(argsJson) ? argsJson : File.ReadAllText(argsFile!);
            var obj = JsonNode.Parse(raw)?.AsObject();
            if (obj != null)
            {
                foreach (var kv in obj)
                {
                    toolArgs[kv.Key] = JsonNodeToObject(kv.Value);
                }
            }
        }

        if (!string.IsNullOrEmpty(app)) toolArgs["app"] = app;
        if (pid.HasValue) toolArgs["pid"] = pid.Value;
        if (!string.IsNullOrEmpty(display)) toolArgs["display"] = display;
        if (!string.IsNullOrEmpty(coordinateSpace)) toolArgs["coordinate_space"] = coordinateSpace;
        if (!string.IsNullOrEmpty(pointsJson)) toolArgs["points"] = pointsJson;
        if (!string.IsNullOrEmpty(regionsJson)) toolArgs["regions"] = regionsJson;
        if (pollHz.HasValue) toolArgs["poll_hz"] = pollHz.Value;
        if (timeoutMs.HasValue) toolArgs["timeout_ms"] = timeoutMs.Value;
        if (!string.IsNullOrEmpty(matchMode)) toolArgs["match_mode"] = matchMode;
        if (consecutiveHits.HasValue) toolArgs["consecutive_hits"] = consecutiveHits.Value;
        if (!string.IsNullOrEmpty(onMatchJson)) toolArgs["on_match"] = onMatchJson;

        using var automation = new AutomationService();
        var handlers = new ToolHandlers(automation);
        var result = handlers.CallTool("wait_for", toolArgs);
        if (result.IsError)
        {
            stderr.WriteLine(result.Content.FirstOrDefault()?.Text ?? "wait_for failed");
            return 1;
        }

        stdout.WriteLine(result.Content.FirstOrDefault()?.Text ?? "{}");
        return 0;
    }

    private static int RunSharedFrame(string[] args, TextWriter stdout, TextWriter stderr)
    {
        string action = "status";
        string? app = null;
        int? pid = null;
        string? display = null;
        bool? clientOnly = null;
        double? regionX = null;
        double? regionY = null;
        double? regionWidth = null;
        double? regionHeight = null;
        int? maxFps = null;
        bool? onlyOnChange = null;
        int? ownerPid = null;

        int startIdx = 0;
        if (args.Length > 0 && !args[0].StartsWith('-'))
        {
            action = args[0].ToLowerInvariant();
            startIdx = 1;
        }

        for (int i = startIdx; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--action" when i + 1 < args.Length:
                    action = args[++i].ToLowerInvariant();
                    break;
                case "--app" when i + 1 < args.Length:
                    app = args[++i];
                    break;
                case "--pid" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out int p) || p <= 0)
                    {
                        stderr.WriteLine("--pid requires a positive integer");
                        return 1;
                    }
                    pid = p;
                    break;
                case "--display" when i + 1 < args.Length:
                    display = args[++i];
                    break;
                case "--client-only":
                    if (i + 1 < args.Length && TryParseCliBool(args[i + 1], out bool co))
                    {
                        i++;
                        clientOnly = co;
                    }
                    else
                    {
                        clientOnly = true;
                    }
                    break;
                case "--region-x" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double rx))
                    {
                        stderr.WriteLine("--region-x requires a valid number");
                        return 1;
                    }
                    regionX = rx;
                    break;
                case "--region-y" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ry))
                    {
                        stderr.WriteLine("--region-y requires a valid number");
                        return 1;
                    }
                    regionY = ry;
                    break;
                case "--region-width" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double rw))
                    {
                        stderr.WriteLine("--region-width requires a valid number");
                        return 1;
                    }
                    regionWidth = rw;
                    break;
                case "--region-height" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double rh))
                    {
                        stderr.WriteLine("--region-height requires a valid number");
                        return 1;
                    }
                    regionHeight = rh;
                    break;
                case "--max-fps" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out int fps) || fps < 1 || fps > 240)
                    {
                        stderr.WriteLine("--max-fps requires an integer between 1 and 240");
                        return 1;
                    }
                    maxFps = fps;
                    break;
                case "--only-on-change":
                    if (i + 1 < args.Length && TryParseCliBool(args[i + 1], out bool ooc))
                    {
                        i++;
                        onlyOnChange = ooc;
                    }
                    else
                    {
                        onlyOnChange = true;
                    }
                    break;
                case "--owner-pid" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out int op) || op <= 0)
                    {
                        stderr.WriteLine("--owner-pid requires a positive integer");
                        return 1;
                    }
                    ownerPid = op;
                    break;
                default:
                    stderr.WriteLine($"unknown shared-frame option: {args[i]}");
                    return 1;
            }
        }

        var toolArgs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["action"] = action
        };
        if (!string.IsNullOrEmpty(app)) toolArgs["app"] = app;
        if (pid.HasValue) toolArgs["pid"] = pid.Value;
        if (!string.IsNullOrEmpty(display)) toolArgs["display"] = display;
        if (clientOnly.HasValue) toolArgs["client_only"] = clientOnly.Value;
        if (regionX.HasValue) toolArgs["region_x"] = regionX.Value;
        if (regionY.HasValue) toolArgs["region_y"] = regionY.Value;
        if (regionWidth.HasValue) toolArgs["region_width"] = regionWidth.Value;
        if (regionHeight.HasValue) toolArgs["region_height"] = regionHeight.Value;
        if (maxFps.HasValue) toolArgs["max_fps"] = maxFps.Value;
        if (onlyOnChange.HasValue) toolArgs["only_on_change"] = onlyOnChange.Value;
        if (ownerPid.HasValue) toolArgs["owner_pid"] = ownerPid.Value;

        using var automation = new AutomationService();
        var handlers = new ToolHandlers(automation);
        var result = handlers.CallTool("manage_shared_frame", toolArgs);
        if (result.IsError)
        {
            stderr.WriteLine(result.Content.FirstOrDefault()?.Text ?? "manage_shared_frame failed");
            return 1;
        }

        stdout.WriteLine(result.Content.FirstOrDefault()?.Text ?? "{}");
        return 0;
    }

    private static int RunReleaseInputs(TextWriter stdout)
    {
        var released = ActiveInputTracker.ReleaseAll();
        stdout.WriteLine(JsonSerializer.Serialize(released, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static int RunLaunch(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0)
        {
            stderr.WriteLine("Usage: launch <path> [--args <str>] [--working-dir <dir>]");
            return 1;
        }

        string path = args[0];
        string? launchArgs = null;
        string? workingDir = null;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--args" when i + 1 < args.Length:
                    launchArgs = args[++i];
                    break;
                case "--working-dir" when i + 1 < args.Length:
                    workingDir = args[++i];
                    break;
            }
        }

        var result = ProcessManager.LaunchApp(path, launchArgs, workingDir);
        stdout.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static int RunClose(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0)
        {
            stderr.WriteLine("Usage: close <app> [--pid <n>] [--force] [--timeout <ms>]");
            return 1;
        }

        string? app = null;
        int? pid = null;
        bool force = false;
        int timeoutMs = 3000;
        int startIndex = 0;

        if (!args[0].StartsWith('-'))
        {
            app = args[0];
            startIndex = 1;
        }

        for (int i = startIndex; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pid" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int p)) pid = p;
                    break;
                case "--force":
                    force = true;
                    break;
                case "--timeout" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int t)) timeoutMs = t;
                    break;
                default:
                    if (string.IsNullOrEmpty(app) && !args[i].StartsWith('-'))
                    {
                        app = args[i];
                    }
                    break;
            }
        }

        var result = ProcessManager.CloseApp(app, pid, force, timeoutMs);
        stdout.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    public static string HelpText(string command)
    {
        switch (command.ToLowerInvariant().Trim())
        {
            case "mcp":
                return "Usage:\n  open-computer-use.exe mcp\n\nStart the stdio MCP server.\n";
            case "serve":
                return "Usage:\n  open-computer-use.exe serve [--port <number>] [--token <secret>] [--host <ip>] [--allow-remote]\n\nStart the REST API daemon with Swagger UI at /swagger and handoff at /discover.\n  --allow-remote, -r   Allow connections from remote machines (binds to 0.0.0.0).\n  --host <ip>          Specify bind IP address (default: 127.0.0.1).\n";
            case "call":
                return "Usage:\n  open-computer-use.exe call <tool> [--args '<json-object>']\n  open-computer-use.exe call --calls '<json-array>'\n\nThe JSON array form keeps all calls in one process so element_index state can be reused.\n";
            case "snapshot":
                return "Usage:\n  open-computer-use.exe snapshot [--text-limit <positive-int|max>] [--max-tree-nodes <positive-int>] [--max-tree-depth <positive-int>] [--include-ax <true|false>] [--include-screenshot <true|false>] <app>\n\nPrint the current Windows UI Automation snapshot for the target app.\n";
            case "config":
                return "Usage:\n  open-computer-use.exe config [list [--json] | path | get <key> | set <key> <value> | reset [<key>]]\n\nRead or modify persistent Open Computer Use configuration.\n";
            case "install-skill":
                return "Usage:\n  open-computer-use.exe install-skill [target-directory]\n\nDeploy the terkirdo-computer-use SKILL.md to AI agent directories.\n";
            case "clipboard":
                return "Usage:\n  open-computer-use.exe clipboard get [--format <text|image|all>]\n  open-computer-use.exe clipboard set <text> [--paste]\n\nRead or write to the Windows clipboard.\n";
            case "window":
                return "Usage:\n  open-computer-use.exe window <app> <action> [--pid <n>] [--x <n>] [--y <n>] [--width <n>] [--height <n>]\n\nManage application window state and position.\n";
            case "screen":
                return "Usage:\n  open-computer-use.exe screen [--display <id>] [--x <n> --y <n> --width <n> --height <n>] [--format <png|jpg>] [--max-dimension <n>]\n\nCapture screenshot of all displays, a specific display, or an ROI sub-region.\n";
            case "release-inputs":
            case "release_inputs":
                return "Usage:\n  open-computer-use.exe release-inputs\n\nImmediately release all held keyboard keys, held mouse buttons, and stuck modifiers.\n";
            case "follow-trajectory":
            case "trajectory":
                return "Usage:\n  open-computer-use.exe follow-trajectory --waypoints '<json>' [--app <name|pid:N>] [--pid <N>] [--interval-ms <ms>] [--coordinate-space <client|window|screen>] [--method <hardware|app_post|stealth>] [--hold-button] [--humanize <none|subtle|natural|fast>]\n\nPlay a continuous 60Hz-120Hz monotonic QPC cursor trajectory.\n";
            case "probe-screen":
            case "probe":
                return "Usage:\n  open-computer-use.exe probe-screen [--app <name|pid:N>] [--pid <N>] [--display <id>] [--coordinate-space <client|window|screen>] [--points '<json>'] [--regions '<json>']\n\nRun sub-millisecond pixel, FNV-1a region hash, and centroid sensing.\n";
            case "wait-for":
            case "wait_for":
                return "Usage:\n  open-computer-use.exe wait-for [--app <name|pid:N>] [--pid <N>] [--display <id>] [--coordinate-space <client|window|screen>] [--points '<json>'] [--regions '<json>'] [--poll-hz <1..240>] [--timeout-ms <10..60000>] [--match-mode <all|any>] [--consecutive-hits <1..10>] [--on-match '<json>']\n\nPoll low-level pixel, FNV-1a region hash, and centroid signals at up to 240Hz (120Hz default) with optional <2ms in-process reaction.\n";
            case "shared-frame":
            case "shared_frame":
            case "manage-shared-frame":
            case "manage_shared_frame":
                return "Usage:\n  open-computer-use.exe shared-frame <start|stop|status> [--app <name|pid:N>] [--pid <N>] [--display <id>] [--client-only <true|false>] [--max-fps <1..240>] [--only-on-change <true|false>] [--owner-pid <N>]\n\nManage the 64-byte TKSM Seqlock + Double-Buffer zero-copy shared memory frame publisher.\n";
            case "launch":
                return "Usage:\n  open-computer-use.exe launch <path> [--args <str>] [--working-dir <dir>]\n\nLaunch an application safely.\n";
            case "close":
                return "Usage:\n  open-computer-use.exe close <app> [--pid <n>] [--force] [--timeout <ms>]\n\nClose an application gracefully or forcefully.\n";
            default:
                return @"Open Computer Use for Windows

Usage:
  open-computer-use.exe [command] [options]

Commands:
  mcp                  Start the stdio MCP server.
  serve                Start the REST + Full-Duplex WebSocket (/ws/control) daemon (Default: http://127.0.0.1:5005).
  doctor               Print Windows runtime notes.
  list-apps            Print running apps with top-level windows.
  snapshot <app>       Print the current UI Automation snapshot for an app.
  call <tool>          Call one tool, or run a JSON array of tool calls.
  clipboard            Get or set the Windows clipboard (text/image/paste).
  window               Manage application window (focus, minimize, maximize, restore, move, resize).
  screen               Capture multi-monitor display, ROI region, or full virtual desktop.
  follow-trajectory    Play a 60Hz-120Hz monotonic QPC cursor trajectory.
  probe-screen         Sub-millisecond pixel, FNV-1a region hash, and centroid sensor.
  wait-for             120Hz pixel/hash/centroid signal reflex with <2ms on_match reaction.
  shared-frame         Manage zero-copy TKSM Seqlock shared-memory frame ring (start/stop/status).
  release-inputs       Panic-release all held keyboard keys and mouse buttons.
  launch <path>        Launch an application safely.
  close <app>          Close an application gracefully or forcefully.
  help [command]       Show general or command-specific help.
  version              Print the CLI version.
  install-skill        Deploy the terkirdo-computer-use skill to agent directories.
  install-codex-mcp    Register MCP server in ~/.codex/config.toml
  install-claude-mcp   Register MCP server in ~/.claude.json
  install-gemini-mcp   Register MCP server in ./.gemini/settings.json
  install-opencode-mcp Register MCP server in ~/.config/opencode/opencode.json

Notes:
  The Windows runtime uses UI Automation first, then Win32 window messages for
  fallback input. Run it in the signed-in desktop session, not as a service.
";
        }
    }
}
