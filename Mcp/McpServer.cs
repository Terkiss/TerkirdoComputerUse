using System.Text.Json;
using System.Text.Json.Serialization;
using TerkirdoComputer.Core;
using TerkirdoComputer.Windows;

namespace TerkirdoComputer.Mcp;

public sealed class McpServer
{
    public const string ServerVersion = "2.0.0";
    public const string ServerName = "open-computer-use";
    public const string ProtocolVersion = "2025-03-26";

    public const string ServerInstructions =
        "Computer Use tools let you interact with Windows apps and games by performing high-speed I/O, UI, and humanized hardware input actions.\n\n" +
        "Begin by calling `get_app_state` every turn you want to use Computer Use to get the latest state before acting, use `query` for targeted accessibility lookups, use `probe_screen` / `wait_for` for sub-millisecond UIA-less game pixel/hash/centroid sensing and reflex reactions, or use `manage_shared_frame` for zero-copy Local\\ shared-memory frame streaming. " +
        "The available tools are list_apps, get_app_state, query, click, move_mouse, follow_trajectory, probe_screen, wait_for, manage_shared_frame, perform_secondary_action, scroll, drag, type_text, press_key, batch_actions, release_inputs, set_value, select_text, get_clipboard, set_clipboard, manage_window, capture_screen, launch_app, and close_app.\n\n" +
        "Prefer element-targeted interactions over coordinate clicks when an index for the targeted element is available. " +
        "Windows actions use UI Automation patterns first and fall back to window messages when an app does not expose the needed pattern. " +
        "The Windows runtime does not auto-launch apps, perform SetFocus, or use UIA text fallback by default, so background-capable actions do not intentionally steal the user's foreground focus.";

    private readonly AutomationService _automationService;
    private readonly ToolHandlers _toolHandlers;
    private readonly JsonSerializerOptions _jsonOptions;

    public McpServer(AutomationService automationService)
    {
        _automationService = automationService;
        _toolHandlers = new ToolHandlers(_automationService);
        _jsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };
    }

    public void Run(TextReader input, TextWriter output)
    {
        Console.Error.WriteLine($"[TerkirdoComputer MCP] Starting stdio server (v{ServerVersion})...");

        string? line;
        while ((line = input.ReadLine()) != null)
        {
            line = line.TrimStart('\uFEFF').Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonRpcResponse? response;
            try
            {
                var request = JsonSerializer.Deserialize<JsonRpcRequest>(line, _jsonOptions);
                if (request == null)
                {
                    response = JsonRpcResponse.ErrorResponse(null, -32700, "Invalid JSON-RPC payload");
                }
                else
                {
                    response = HandleRequest(request);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[TerkirdoComputer MCP] JSON-RPC parse error: {ex.Message}");
                response = JsonRpcResponse.ErrorResponse(null, -32700, "Invalid JSON-RPC payload");
            }

            if (response != null)
            {
                string json = JsonSerializer.Serialize(response, _jsonOptions);
                output.WriteLine(json);
                output.Flush();
            }
        }

        Console.Error.WriteLine("[TerkirdoComputer MCP] Stdio stream closed, exiting.");
    }

    public JsonRpcResponse? HandleRequest(JsonRpcRequest request)
    {
        switch (request.Method)
        {
            case "initialize":
                return JsonRpcResponse.Success(request.Id, new Dictionary<string, object?>
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["serverInfo"] = new Dictionary<string, object?>
                    {
                        ["name"] = ServerName,
                        ["version"] = ServerVersion
                    },
                    ["capabilities"] = new Dictionary<string, object?>
                    {
                        ["tools"] = new Dictionary<string, object?> { ["listChanged"] = false }
                    },
                    ["instructions"] = ServerInstructions
                });

            case "notifications/initialized":
            case "notifications/turn-ended":
                // MCP notifications must not receive a response
                return null;

            case "ping":
                return JsonRpcResponse.Success(request.Id, new Dictionary<string, object?>());

            case "tools/list":
                return JsonRpcResponse.Success(request.Id, new Dictionary<string, object?>
                {
                    ["tools"] = ToolHandlers.GetToolDefinitions()
                });

            case "tools/call":
                if (request.Params.HasValue && request.Params.Value.ValueKind == JsonValueKind.Object)
                {
                    string toolName = "";
                    var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

                    if (request.Params.Value.TryGetProperty("name", out var nameProp))
                    {
                        toolName = nameProp.GetString() ?? "";
                    }

                    if (request.Params.Value.TryGetProperty("arguments", out var argsProp) &&
                        argsProp.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in argsProp.EnumerateObject())
                        {
                            args[prop.Name] = JsonElementToObject(prop.Value);
                        }
                    }

                    var result = _toolHandlers.CallTool(toolName, args);
                    return JsonRpcResponse.Success(request.Id, result);
                }
                return JsonRpcResponse.ErrorResponse(request.Id, -32602, "Invalid params for tools/call");

            default:
                if (string.IsNullOrEmpty(request.Method))
                {
                    return null;
                }
                return JsonRpcResponse.ErrorResponse(request.Id, -32601, $"Method not found: {request.Method}");
        }
    }

    private static object? JsonElementToObject(JsonElement elem) =>
        elem.ValueKind switch
        {
            JsonValueKind.String => elem.GetString(),
            JsonValueKind.Number => elem.TryGetInt64(out long l) ? (object)l : elem.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => elem.EnumerateArray().Select(JsonElementToObject).ToList(),
            JsonValueKind.Object => elem.EnumerateObject().ToDictionary(p => p.Name, p => JsonElementToObject(p.Value)),
            _ => elem.ToString()
        };
}
