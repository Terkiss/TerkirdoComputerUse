using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerkirdoComputer.Core;

/// <summary>
/// RFC 7807 Problem Details for HTTP / JSON-RPC APIs.
/// </summary>
public sealed class ProblemDetails
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "about:blank";

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public int Status { get; set; } = 500;

    [JsonPropertyName("detail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; set; }

    [JsonPropertyName("instance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Instance { get; set; }

    [JsonExtensionData]
    public Dictionary<string, object?>? Extensions { get; set; }

    public string ToJson() =>
        JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

    public ToolCallResult ToToolCallResult() =>
        ToolCallResult.TextResult(Detail ?? Title, isError: true);

    public ToolCallResult ToProblemDetailsResult() =>
        ToolCallResult.TextResult(ToJson(), isError: true);

    public static ProblemDetails AppNotFound(string app, string? instance = null) =>
        new()
        {
            Type = "urn:terkirdo:error:app-not-found",
            Title = "Application Not Found",
            Status = 404,
            Detail = $"appNotFound(\"{app}\")",
            Instance = instance
        };

    public static ProblemDetails DisplayNotFound(string display, string? instance = null) =>
        new()
        {
            Type = "urn:terkirdo:error:display-not-found",
            Title = "Display Not Found",
            Status = 404,
            Detail = $"displayNotFound(\"{display}\")",
            Instance = instance
        };

    public static ProblemDetails ElementNotFound(string elementIndex, string? instance = null) =>
        new()
        {
            Type = "urn:terkirdo:error:element-not-found",
            Title = "Element Not Found",
            Status = 404,
            Detail = $"unknown element_index \"{elementIndex}\"",
            Instance = instance
        };

    public static ProblemDetails UnsupportedAction(string action, string elementIndex, string? instance = null) =>
        new()
        {
            Type = "urn:terkirdo:error:unsupported-action",
            Title = "Unsupported Action",
            Status = 400,
            Detail = $"{action} is not a valid secondary action for {elementIndex}",
            Instance = instance
        };

    public static ProblemDetails InvalidArguments(string detail, string? instance = null) =>
        new()
        {
            Type = "urn:terkirdo:error:invalid-arguments",
            Title = "Invalid Arguments",
            Status = 400,
            Detail = detail,
            Instance = instance
        };

    public static ProblemDetails PrivilegeViolation(string detail, string? instance = null) =>
        new()
        {
            Type = "urn:terkirdo:error:uipi-privilege-violation",
            Title = "UIPI Privilege Violation",
            Status = 403,
            Detail = detail,
            Instance = instance
        };

    public static ProblemDetails Timeout(string detail, string? instance = null) =>
        new()
        {
            Type = "urn:terkirdo:error:uia-timeout",
            Title = "Timeout",
            Status = 408,
            Detail = detail,
            Instance = instance
        };

    public static ProblemDetails ClipboardLocked(string detail = "Clipboard is locked by another process", string? instance = null) =>
        new()
        {
            Type = "urn:terkirdo:error:clipboard-locked",
            Title = "Clipboard Locked",
            Status = 423,
            Detail = detail,
            Instance = instance
        };

    public static ProblemDetails InternalError(string detail, string? instance = null) =>
        new()
        {
            Type = "urn:terkirdo:error:internal-error",
            Title = "Internal Error",
            Status = 500,
            Detail = detail,
            Instance = instance
        };

    public static ProblemDetails QueryTokenForbidden(string detail = "Passing authentication tokens in URL query parameters is forbidden. Use Authorization: Bearer <token> or X-Terkirdo-Token header.", string? instance = null) =>
        new()
        {
            Type = "urn:terkirdo:error:query-token-forbidden",
            Title = "Query Token Forbidden",
            Status = 400,
            Detail = detail,
            Instance = instance
        };

    public static ProblemDetails ForbiddenOrigin(string origin, string? instance = null) =>
        new()
        {
            Type = "urn:terkirdo:error:forbidden-origin",
            Title = "Forbidden Origin",
            Status = 403,
            Detail = $"Origin '{origin}' is not permitted for WebSocket control handshake.",
            Instance = instance
        };

    public static ProblemDetails ShmLocalOnly(string detail = "Shared-memory frame streams are restricted to local loopback callers.", string? instance = null) =>
        new()
        {
            Type = "urn:terkirdo:error:shm-local-only",
            Title = "Shared Memory Local Only",
            Status = 403,
            Detail = detail,
            Instance = instance
        };

    public static ProblemDetails TooManyWaiters(string detail = "Maximum concurrent wait_for operations (4) exceeded.", string? instance = null) =>
        new()
        {
            Type = "urn:terkirdo:error:too-many-waiters",
            Title = "Too Many Concurrent Waiters",
            Status = 429,
            Detail = detail,
            Instance = instance
        };
}
