using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using TerkirdoComputer.Core;
using TerkirdoComputer.Mcp;
using TerkirdoComputer.Windows;

namespace TerkirdoComputer.Rest;

public static class RestEndpoints
{
    public static void MapEndpoints(WebApplication app, AutomationService automationService, TokenManager tokenManager, int port, bool allowRemote = false)
    {
        var toolHandlers = new ToolHandlers(automationService);
        var wsHub = new WebSocketControlHub(automationService, toolHandlers, allowRemote);

        app.Lifetime.ApplicationStopping.Register(() =>
        {
            try { _ = SharedFrameStreamService.StopSync(); } catch { }
        });

        // 1. Host Validation & DNS Rebinding Protection Middleware (active only in loopback mode)
        app.Use(async (context, next) =>
        {
            if (!allowRemote)
            {
                string host = context.Request.Host.Host;
                if (!string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(host, "[::1]", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        type = "urn:terkirdo:error:forbidden-host",
                        title = "Forbidden Host",
                        status = 403,
                        detail = $"Host '{host}' is not permitted in loopback mode. Launch daemon with --allow-remote to accept external requests."
                    });
                    return;
                }
            }

            await next();
        });

        // 1.5. WebSocket Pre-Upgrade CSWSH Origin & Query-Token Hygiene Gate (runs BEFORE Auth & AcceptWebSocketAsync)
        app.Use(async (context, next) =>
        {
            string path = context.Request.Path.Value ?? string.Empty;
            if (string.Equals(path, "/ws/control", StringComparison.OrdinalIgnoreCase))
            {
                if (!await WebSocketControlHub.ValidatePreUpgradeSecurityAsync(context, allowRemote))
                {
                    return;
                }
            }

            await next();
        });

        // 2. Authentication Middleware
        app.Use(async (context, next) =>
        {
            string path = context.Request.Path.Value ?? string.Empty;

            // Public paths: health, swagger, openapi, discover
            if (string.Equals(path, "/health", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, "/discover", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, "/api/discover", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, "/openapi.json", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            string? authHeader = context.Request.Headers.Authorization;
            string? customHeader = context.Request.Headers["X-Terkirdo-Token"];

            if (!tokenManager.Validate(authHeader, customHeader))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers["WWW-Authenticate"] = "Bearer";
                await context.Response.WriteAsJsonAsync(new
                {
                    type = "urn:terkirdo:error:unauthorized",
                    title = "Unauthorized",
                    status = 401,
                    detail = "Missing or invalid Bearer authentication token. Provide Authorization: Bearer <token> or X-Terkirdo-Token header."
                });
                return;
            }

            await next();
        });

        // 3. Attach Active Discovery Response Headers
        app.Use(async (context, next) =>
        {
            string requestHost = context.Request.Host.Host;
            string resolvedUrl = (string.Equals(requestHost, "localhost", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(requestHost, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(requestHost, "[::1]", StringComparison.OrdinalIgnoreCase))
                ? $"http://127.0.0.1:{port}"
                : $"http://{requestHost}:{port}";

            context.Response.Headers["X-Terkirdo-Active-Port"] = port.ToString();
            context.Response.Headers["X-Terkirdo-Active-Url"] = resolvedUrl;
            await next();
        });

        // 4. Enable RFC 6455 WebSocket Transport
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(15)
        });

        // --- Public Endpoints ---
        app.MapGet("/health", (HttpContext context) =>
        {
            string requestHost = context.Request.Host.Host;
            string resolvedUrl = (string.Equals(requestHost, "localhost", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(requestHost, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(requestHost, "[::1]", StringComparison.OrdinalIgnoreCase))
                ? $"http://127.0.0.1:{port}"
                : $"http://{requestHost}:{port}";

            return Results.Ok(new
            {
                status = "healthy",
                version = "2.0.0",
                runtime = "TerkirdoComputer .NET 10 Pure Real-Time I/O Dual-Mode Daemon",
                activePort = port,
                activeUrl = resolvedUrl,
                discoveryPort = 5005,
                allowRemote = allowRemote,
                timestamp = DateTime.UtcNow
            });
        });

        app.MapGet("/discover", (HttpContext context) =>
        {
            string requestHost = context.Request.Host.Host;
            string resolvedUrl = (string.Equals(requestHost, "localhost", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(requestHost, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(requestHost, "[::1]", StringComparison.OrdinalIgnoreCase))
                ? $"http://127.0.0.1:{port}"
                : $"http://{requestHost}:{port}";

            return Results.Ok(new
            {
                service = "TerkirdoComputer",
                status = "ready",
                activePort = port,
                activeUrl = resolvedUrl,
                discoveryPort = 5005,
                allowRemote = allowRemote,
                handoff = new
                {
                    description = "Port 5005 is the well-known discovery and handoff gateway. Route all desktop CUA operations to activeUrl.",
                    target = resolvedUrl,
                    tokenPath = tokenManager.TokenFilePath
                },
                endpoints = new
                {
                    health = "/health",
                    swagger = "/swagger",
                    openapi = "/openapi.json",
                    apps = "/api/apps",
                    actions = "/api/actions",
                    trajectory = "/api/actions/trajectory",
                    visionProbe = "/api/vision/probe",
                    visionWait = "/api/vision/wait",
                    sharedFrame = "/api/vision/shared-frame",
                    wsControl = "/ws/control"
                },
                timestamp = DateTime.UtcNow
            });
        });

        // --- Full-Duplex Real-Time WebSocket Control & Binary Frame Stream (Pillar 2) ---
        app.Map("/ws/control", (HttpContext context) => wsHub.HandleWebSocketAsync(context));

        app.MapGet("/api/discover", () => Results.Redirect("/discover"));

        app.MapGet("/openapi.json", () => Results.Content(SwaggerDocHelper.GetOpenApiJson(port), "application/json"));
        app.MapGet("/swagger", () => Results.Content(SwaggerDocHelper.GetSwaggerHtml(), "text/html"));
        app.MapGet("/swagger/index.html", () => Results.Content(SwaggerDocHelper.GetSwaggerHtml(), "text/html"));

        // --- Diagnostics ---
        app.MapGet("/api/doctor", () =>
        {
            int width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
            int height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
            bool dxgi = ScreenCapture.IsDxgiSupported();
            bool elevated = NativeMethods.IsProcessElevated(System.Diagnostics.Process.GetCurrentProcess().Handle);

            return Results.Ok(new
            {
                elevation = elevated ? "Elevated (Administrator)" : "Standard User",
                resolution = $"{width}x{height}",
                dxgiAvailable = dxgi,
                framework = ".NET 10.0 (Native C#)",
                uipiDefense = true
            });
        });

        // --- Application Exploration ---
        app.MapGet("/api/apps", () =>
        {
            var result = toolHandlers.CallTool("list_apps", []);
            if (result.IsError)
            {
                return Results.BadRequest(result);
            }
            string raw = result.Content.Count > 0 ? result.Content[0].Text ?? "" : "";
            var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return Results.Ok(new { apps = lines, raw });
        });

        app.MapGet("/api/apps/{query}/state", (
            string query,
            string? text_limit,
            string? textLimit,
            int? max_tree_nodes,
            int? maxTreeNodes,
            int? max_tree_depth,
            int? maxTreeDepth,
            bool? include_ax,
            bool? includeAx,
            bool? include_screenshot,
            bool? includeScreenshot,
            bool? client_only,
            bool? clientOnly,
            string? window_placement,
            string? windowPlacement,
            double? region_x,
            double? regionX,
            double? region_y,
            double? regionY,
            double? region_width,
            double? regionWidth,
            double? region_height,
            double? regionHeight) =>
        {
            var args = new Dictionary<string, object?>
            {
                ["app"] = query
            };
            string? tl = text_limit ?? textLimit;
            int? mn = max_tree_nodes ?? maxTreeNodes;
            int? md = max_tree_depth ?? maxTreeDepth;
            bool? incAx = include_ax ?? includeAx;
            bool? incScreen = include_screenshot ?? includeScreenshot;
            bool? co = client_only ?? clientOnly;
            string? winPlace = window_placement ?? windowPlacement;
            double? rx = region_x ?? regionX;
            double? ry = region_y ?? regionY;
            double? rw = region_width ?? regionWidth;
            double? rh = region_height ?? regionHeight;

            if (!string.IsNullOrEmpty(tl)) args["text_limit"] = tl;
            if (mn.HasValue) args["max_tree_nodes"] = mn.Value;
            if (md.HasValue) args["max_tree_depth"] = md.Value;
            if (incAx.HasValue) args["include_ax"] = incAx.Value;
            if (incScreen.HasValue) args["include_screenshot"] = incScreen.Value;
            if (co.HasValue) args["client_only"] = co.Value;
            if (!string.IsNullOrEmpty(winPlace)) args["window_placement"] = winPlace;
            if (rx.HasValue) args["region_x"] = rx.Value;
            if (ry.HasValue) args["region_y"] = ry.Value;
            if (rw.HasValue) args["region_width"] = rw.Value;
            if (rh.HasValue) args["region_height"] = rh.Value;

            var result = toolHandlers.CallTool("get_app_state", args);
            if (result.IsError)
            {
                string? errText = result.Content.Count > 0 ? result.Content[0].Text : null;
                if (errText != null && (errText.Contains("include_ax") ||
                                        errText.Contains("window_placement") ||
                                        errText.Contains("Region ") ||
                                        errText.Contains("Invalid pid", StringComparison.OrdinalIgnoreCase) ||
                                        errText.Contains("must be a positive integer")))
                {
                    return Results.BadRequest(ProblemDetails.InvalidArguments(errText));
                }
                return Results.NotFound(ProblemDetails.AppNotFound(query, errText));
            }

            var snapshot = automationService.GetCurrentSnapshot(query);
            return Results.Ok(new { result, snapshot });
        });

        // Direct Browser / High-FPS Vision Binary Window Screenshot Endpoint (supports client_only, optional ROI & zero-Base64 path)
        app.MapGet("/api/apps/{query}/screenshot", (
            string query,
            bool? client_only,
            bool? clientOnly,
            double? region_x,
            double? regionX,
            double? region_y,
            double? regionY,
            double? region_width,
            double? regionWidth,
            double? region_height,
            double? regionHeight,
            string? format,
            int? max_dimension,
            int? maxDimension) =>
        {
            bool? explicitClientOnly = client_only ?? clientOnly;
            bool co = explicitClientOnly == true;
            double? rx = region_x ?? regionX;
            double? ry = region_y ?? regionY;
            double? rw = region_width ?? regionWidth;
            double? rh = region_height ?? regionHeight;
            int? maxDim = max_dimension ?? maxDimension;

            var existingSnapshot = automationService.GetCurrentSnapshot(query);
            bool clientModeDiffersFromCache = explicitClientOnly.HasValue && existingSnapshot != null && existingSnapshot.ClientOnly != co;

            bool hasOverridesOrRoi = co || clientModeDiffersFromCache ||
                                     rx.HasValue || ry.HasValue || rw.HasValue || rh.HasValue ||
                                     !string.IsNullOrWhiteSpace(format) || maxDim.HasValue;

            if (hasOverridesOrRoi)
            {
                try
                {
                    _ = ScreenCapture.ValidateOptionalRegion(rx, ry, rw, rh, "region_");
                    _ = ScreenCapture.ApplyOverrides(OcuConfig.LoadImageCaptureConfig(), format, maxDim);

                    var proc = automationService.ResolveApp(query);
                    IntPtr hwnd = WindowManager.GetTargetHwnd(proc);
                    if (hwnd == IntPtr.Zero) hwnd = proc.MainWindowHandle;
                    var bounds = automationService.GetWindowBounds(proc, null, hwnd);

                    byte[]? directBytes = ScreenCapture.CaptureWindowImageBytes(
                        bounds,
                        hwnd,
                        out string directMime,
                        rx,
                        ry,
                        rw,
                        rh,
                        format,
                        maxDim,
                        clientOnly: co);

                    if (directBytes == null || directBytes.Length == 0)
                    {
                        return Results.NotFound(new { error = "Screenshot not available for this application." });
                    }

                    return Results.File(directBytes, string.IsNullOrWhiteSpace(directMime) ? "image/png" : directMime);
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(ProblemDetails.InvalidArguments(ex.Message));
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("appNotFound", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.NotFound(ProblemDetails.AppNotFound(query, ex.Message));
                }
            }

            if (existingSnapshot == null || string.IsNullOrEmpty(existingSnapshot.ScreenshotPngBase64))
            {
                try
                {
                    var proc = automationService.ResolveApp(query);
                    IntPtr hwnd = WindowManager.GetTargetHwnd(proc);
                    if (hwnd == IntPtr.Zero) hwnd = proc.MainWindowHandle;
                    var bounds = automationService.GetWindowBounds(proc, null, hwnd);

                    byte[]? freshBytes = ScreenCapture.CaptureWindowImageBytes(bounds, hwnd, out string freshMime, clientOnly: co);
                    if (freshBytes != null && freshBytes.Length > 0)
                    {
                        return Results.File(freshBytes, string.IsNullOrWhiteSpace(freshMime) ? "image/png" : freshMime);
                    }
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(ProblemDetails.InvalidArguments(ex.Message));
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("appNotFound", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.NotFound(new { error = $"App '{query}' not found." });
                }

                return Results.NotFound(new { error = "Screenshot not available for this application." });
            }

            byte[] imgBytes = Convert.FromBase64String(existingSnapshot.ScreenshotPngBase64);
            return Results.File(imgBytes, string.IsNullOrWhiteSpace(existingSnapshot.ScreenshotMimeType) ? "image/png" : existingSnapshot.ScreenshotMimeType);
        });

        static IResult MapActionResult(ToolCallResult result, string? app, int? pid = null)
        {
            if (!result.IsError)
            {
                return Results.Ok(result);
            }

            string errText = result.Content.FirstOrDefault()?.Text ?? "";
            if (errText.Contains("appNotFound", StringComparison.OrdinalIgnoreCase))
            {
                string target = !string.IsNullOrWhiteSpace(app) ? app! : (pid.HasValue ? $"pid:{pid.Value}" : "unknown");
                return Results.NotFound(ProblemDetails.AppNotFound(target, errText));
            }
            if (errText.Contains("displayNotFound", StringComparison.OrdinalIgnoreCase))
            {
                return Results.NotFound(ProblemDetails.DisplayNotFound("unknown", errText));
            }

            return Results.BadRequest(result);
        }

        static void ApplyOptionalHumanize(Dictionary<string, object?> args, JsonElement? humanize)
        {
            if (humanize.HasValue &&
                humanize.Value.ValueKind != JsonValueKind.Null &&
                humanize.Value.ValueKind != JsonValueKind.Undefined)
            {
                args["humanize"] = humanize.Value;
            }
        }

        static bool IsLoopbackCaller(HttpContext context) =>
            context.Connection.RemoteIpAddress == null || IPAddress.IsLoopback(context.Connection.RemoteIpAddress);

        // --- Desktop & Game Actions (Protected by ActionLock) ---
        app.MapPost("/api/actions/click", async (ClickRequest req) =>
        {
            return await ActionLock.ExecuteAsync(() =>
            {
                var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(req.App)) args["app"] = req.App;
                if (req.Pid.HasValue) args["pid"] = req.Pid.Value;

                string? elementIndex = req.ElementIndex ?? req.ElementIndexSnake;
                string? coordSpace = req.CoordinateSpace ?? req.CoordinateSpaceSnake;
                int? clickCount = req.ClickCount ?? req.ClickCountSnake;
                string? mouseButton = req.MouseButton ?? req.MouseButtonSnake;
                string? buttonState = req.ButtonState ?? req.ButtonStateSnake;
                int? holdMs = req.HoldMs ?? req.HoldMsSnake;
                string? clickMethod = req.ClickMethod ?? req.ClickMethodSnake;
                bool? skipSnapshot = req.SkipSnapshot ?? req.SkipSnapshotSnake;

                if (!string.IsNullOrEmpty(elementIndex)) args["element_index"] = elementIndex;
                if (req.X.HasValue) args["x"] = req.X.Value;
                if (req.Y.HasValue) args["y"] = req.Y.Value;
                if (!string.IsNullOrEmpty(coordSpace)) args["coordinate_space"] = coordSpace;
                if (clickCount.HasValue) args["click_count"] = clickCount.Value;
                if (!string.IsNullOrEmpty(mouseButton)) args["mouse_button"] = mouseButton;
                if (!string.IsNullOrEmpty(buttonState)) args["button_state"] = buttonState;
                if (holdMs.HasValue) args["hold_ms"] = holdMs.Value;
                if (!string.IsNullOrEmpty(clickMethod)) args["click_method"] = clickMethod;
                if (skipSnapshot.HasValue) args["skip_snapshot"] = skipSnapshot.Value;
                ApplyOptionalHumanize(args, req.Humanize);

                var result = toolHandlers.ExecuteActionLocked("click", args);
                return MapActionResult(result, req.App, req.Pid);
            });
        });

        app.MapPost("/api/actions/move-mouse", async (MoveMouseRequest req) =>
        {
            return await ActionLock.ExecuteAsync(() =>
            {
                var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(req.App)) args["app"] = req.App;
                if (req.Pid.HasValue) args["pid"] = req.Pid.Value;
                if (req.X.HasValue) args["x"] = req.X.Value;
                if (req.Y.HasValue) args["y"] = req.Y.Value;

                string? coordSpace = req.CoordinateSpace ?? req.CoordinateSpaceSnake;
                if (!string.IsNullOrEmpty(coordSpace)) args["coordinate_space"] = coordSpace;

                double? dx = req.DeltaX ?? req.DeltaXSnake;
                double? dy = req.DeltaY ?? req.DeltaYSnake;
                if (dx.HasValue) args["delta_x"] = dx.Value;
                if (dy.HasValue) args["delta_y"] = dy.Value;

                if (req.Steps.HasValue) args["steps"] = req.Steps.Value;

                int? durationMs = req.DurationMs ?? req.DurationMsSnake;
                if (durationMs.HasValue) args["duration_ms"] = durationMs.Value;

                if (!string.IsNullOrEmpty(req.Method)) args["method"] = req.Method;

                bool? skipSnapshot = req.SkipSnapshot ?? req.SkipSnapshotSnake;
                if (skipSnapshot.HasValue) args["skip_snapshot"] = skipSnapshot.Value;
                ApplyOptionalHumanize(args, req.Humanize);

                var result = toolHandlers.ExecuteActionLocked("move_mouse", args);
                return MapActionResult(result, req.App, req.Pid);
            });
        });

        app.MapPost("/api/actions/trajectory", async (TrajectoryRequest req) =>
        {
            return await ActionLock.ExecuteAsync(() =>
            {
                var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(req.App)) args["app"] = req.App;
                if (req.Pid.HasValue) args["pid"] = req.Pid.Value;
                if (req.Waypoints.HasValue) args["waypoints"] = req.Waypoints.Value;

                double? intervalMs = req.IntervalMs ?? req.IntervalMsSnake;
                if (intervalMs.HasValue) args["interval_ms"] = intervalMs.Value;

                string? coordSpace = req.CoordinateSpace ?? req.CoordinateSpaceSnake;
                if (!string.IsNullOrEmpty(coordSpace)) args["coordinate_space"] = coordSpace;

                if (!string.IsNullOrEmpty(req.Method)) args["method"] = req.Method;

                string? mouseButton = req.MouseButton ?? req.MouseButtonSnake;
                if (!string.IsNullOrEmpty(mouseButton)) args["mouse_button"] = mouseButton;

                bool? holdButton = req.HoldButton ?? req.HoldButtonSnake;
                if (holdButton.HasValue) args["hold_button"] = holdButton.Value;

                bool? skipSnapshot = req.SkipSnapshot ?? req.SkipSnapshotSnake;
                if (skipSnapshot.HasValue) args["skip_snapshot"] = skipSnapshot.Value;
                ApplyOptionalHumanize(args, req.Humanize);

                var result = toolHandlers.ExecuteActionLocked("follow_trajectory", args);
                if (result.IsError)
                {
                    return MapActionResult(result, req.App, req.Pid);
                }

                string? summaryJson = result.Content.FirstOrDefault()?.Text;
                if (!string.IsNullOrEmpty(summaryJson))
                {
                    var summary = JsonSerializer.Deserialize<TrajectoryExecutionResult>(summaryJson);
                    if (summary != null)
                    {
                        return Results.Ok(new
                        {
                            ok = summary.Ok,
                            waypointsExecuted = summary.WaypointsExecuted,
                            plannedDurationMs = summary.PlannedDurationMs,
                            actualDurationMs = summary.ActualDurationMs,
                            maxDriftMs = summary.MaxDriftMs,
                            coordinateSpace = summary.CoordinateSpace,
                            method = summary.Method,
                            buttonHeld = summary.ButtonHeld,
                            content = result.Content,
                            isError = false
                        });
                    }
                }

                return Results.Ok(result);
            });
        });

        app.MapPost("/api/vision/probe", (VisionProbeRequest req) =>
        {
            var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(req.App)) args["app"] = req.App;
            if (req.Pid.HasValue) args["pid"] = req.Pid.Value;
            if (!string.IsNullOrEmpty(req.Display)) args["display"] = req.Display;

            string? coordSpace = req.CoordinateSpace ?? req.CoordinateSpaceSnake;
            if (!string.IsNullOrEmpty(coordSpace)) args["coordinate_space"] = coordSpace;

            if (req.Points.HasValue) args["points"] = req.Points.Value;
            if (req.Regions.HasValue) args["regions"] = req.Regions.Value;

            var result = toolHandlers.CallTool("probe_screen", args);
            if (result.IsError)
            {
                string errText = result.Content.FirstOrDefault()?.Text ?? "Invalid probe request";
                if (errText.Contains("appNotFound", StringComparison.OrdinalIgnoreCase))
                {
                    string target = !string.IsNullOrWhiteSpace(req.App) ? req.App! : (req.Pid.HasValue ? $"pid:{req.Pid.Value}" : "unknown");
                    return Results.NotFound(ProblemDetails.AppNotFound(target, errText));
                }
                if (errText.Contains("displayNotFound", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.NotFound(ProblemDetails.DisplayNotFound(req.Display ?? "1", errText));
                }
                return Results.BadRequest(ProblemDetails.InvalidArguments(errText));
            }

            string? probeJson = result.Content.FirstOrDefault()?.Text;
            if (!string.IsNullOrEmpty(probeJson))
            {
                var parsed = JsonSerializer.Deserialize<VisionProbeResult>(probeJson);
                if (parsed != null)
                {
                    return Results.Ok(parsed);
                }
            }

            return Results.Ok(result);
        });

        // --- Pillar 1: 120Hz Low-Level Signal Reflex & In-Process <2ms Reaction (ActionLock-Free Polling) ---
        app.MapPost("/api/vision/wait", async (HttpContext httpContext, VisionWaitRequest req) =>
        {
            var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(req.App)) args["app"] = req.App;
            if (req.Pid.HasValue) args["pid"] = req.Pid.Value;
            if (!string.IsNullOrEmpty(req.Display)) args["display"] = req.Display;

            string? coordSpace = req.CoordinateSpace ?? req.CoordinateSpaceSnake;
            if (!string.IsNullOrEmpty(coordSpace)) args["coordinate_space"] = coordSpace;

            if (req.Points.HasValue) args["points"] = req.Points.Value;
            if (req.Regions.HasValue) args["regions"] = req.Regions.Value;

            int? pollHz = req.PollHz ?? req.PollHzSnake;
            if (pollHz.HasValue) args["poll_hz"] = pollHz.Value;

            int? timeoutMs = req.TimeoutMs ?? req.TimeoutMsSnake;
            if (timeoutMs.HasValue) args["timeout_ms"] = timeoutMs.Value;

            string? matchMode = req.MatchMode ?? req.MatchModeSnake;
            if (!string.IsNullOrEmpty(matchMode)) args["match_mode"] = matchMode;

            int? consecutiveHits = req.ConsecutiveHits ?? req.ConsecutiveHitsSnake;
            if (consecutiveHits.HasValue) args["consecutive_hits"] = consecutiveHits.Value;

            JsonElement? onMatch = req.OnMatch ?? req.OnMatchSnake;
            if (onMatch.HasValue &&
                onMatch.Value.ValueKind != JsonValueKind.Null &&
                onMatch.Value.ValueKind != JsonValueKind.Undefined)
            {
                args["on_match"] = onMatch.Value;
            }

            try
            {
                var waitResult = await toolHandlers.WaitForAsync(args, httpContext.RequestAborted);
                return Results.Ok(waitResult);
            }
            catch (TooManyWaitersException ex)
            {
                return Results.Json(ProblemDetails.TooManyWaiters(ex.Message), statusCode: StatusCodes.Status429TooManyRequests);
            }
            catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
            {
                return Results.StatusCode(499);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("appNotFound", StringComparison.OrdinalIgnoreCase))
            {
                string target = !string.IsNullOrWhiteSpace(req.App) ? req.App! : (req.Pid.HasValue ? $"pid:{req.Pid.Value}" : "unknown");
                return Results.NotFound(ProblemDetails.AppNotFound(target, ex.Message));
            }
            catch (ArgumentException ex)
            {
                if (ex.Message.Contains("appNotFound", StringComparison.OrdinalIgnoreCase))
                {
                    string target = !string.IsNullOrWhiteSpace(req.App) ? req.App! : (req.Pid.HasValue ? $"pid:{req.Pid.Value}" : "unknown");
                    return Results.NotFound(ProblemDetails.AppNotFound(target, ex.Message));
                }
                if (ex.Message.Contains("displayNotFound", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.NotFound(ProblemDetails.DisplayNotFound(req.Display ?? "1", ex.Message));
                }
                return Results.BadRequest(ProblemDetails.InvalidArguments(ex.Message));
            }
        });

        // --- Pillar 3: Zero-Copy Shared Memory Frame Ring ("TKSM" Seqlock + Double-Buffer, Loopback Only) ---
        app.MapGet("/api/vision/shared-frame", (HttpContext httpContext) =>
        {
            if (!IsLoopbackCaller(httpContext))
            {
                return Results.Json(ProblemDetails.ShmLocalOnly(), statusCode: StatusCodes.Status403Forbidden);
            }

            return Results.Ok(SharedFrameStreamService.GetStatus());
        });

        app.MapPost("/api/vision/shared-frame/start", async (HttpContext httpContext) =>
        {
            if (!IsLoopbackCaller(httpContext))
            {
                return Results.Json(ProblemDetails.ShmLocalOnly(), statusCode: StatusCodes.Status403Forbidden);
            }

            SharedFrameStartRequest req;
            try
            {
                if (httpContext.Request.ContentLength is > 0 || httpContext.Request.HasJsonContentType())
                {
                    req = await httpContext.Request.ReadFromJsonAsync<SharedFrameStartRequest>(cancellationToken: httpContext.RequestAborted)
                          ?? new SharedFrameStartRequest();
                }
                else
                {
                    req = new SharedFrameStartRequest();
                }
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(ProblemDetails.InvalidArguments($"Invalid JSON payload: {ex.Message}"));
            }

            bool clientOnly = req.ClientOnly ?? req.ClientOnlySnake ?? true;
            double? regionX = req.RegionX ?? req.RegionXSnake;
            double? regionY = req.RegionY ?? req.RegionYSnake;
            double? regionW = req.RegionWidth ?? req.RegionWidthSnake;
            double? regionH = req.RegionHeight ?? req.RegionHeightSnake;
            int maxFps = req.MaxFps ?? req.MaxFpsSnake ?? 60;
            bool onlyOnChange = req.OnlyOnChange ?? req.OnlyOnChangeSnake ?? true;
            int? ownerPid = req.OwnerPid ?? req.OwnerPidSnake;

            try
            {
                var status = await SharedFrameStreamService.StartAsync(
                    automationService,
                    req.App,
                    req.Pid,
                    req.Display,
                    clientOnly,
                    regionX,
                    regionY,
                    regionW,
                    regionH,
                    maxFps,
                    onlyOnChange,
                    ownerPid,
                    cancellationToken: httpContext.RequestAborted);

                return Results.Ok(status);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("appNotFound", StringComparison.OrdinalIgnoreCase))
            {
                string target = !string.IsNullOrWhiteSpace(req.App) ? req.App! : (req.Pid.HasValue ? $"pid:{req.Pid.Value}" : "unknown");
                return Results.NotFound(ProblemDetails.AppNotFound(target, ex.Message));
            }
            catch (ArgumentException ex)
            {
                if (ex.Message.Contains("appNotFound", StringComparison.OrdinalIgnoreCase))
                {
                    string target = !string.IsNullOrWhiteSpace(req.App) ? req.App! : (req.Pid.HasValue ? $"pid:{req.Pid.Value}" : "unknown");
                    return Results.NotFound(ProblemDetails.AppNotFound(target, ex.Message));
                }
                if (ex.Message.Contains("displayNotFound", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.NotFound(ProblemDetails.DisplayNotFound(req.Display ?? "1", ex.Message));
                }
                return Results.BadRequest(ProblemDetails.InvalidArguments(ex.Message));
            }
        });

        app.MapPost("/api/vision/shared-frame/stop", async (HttpContext httpContext) =>
        {
            if (!IsLoopbackCaller(httpContext))
            {
                return Results.Json(ProblemDetails.ShmLocalOnly(), statusCode: StatusCodes.Status403Forbidden);
            }

            var stopped = await SharedFrameStreamService.StopAsync(httpContext.RequestAborted);
            return Results.Ok(stopped);
        });

        app.MapPost("/api/actions/secondary", async (SecondaryRequest req) =>
        {
            return await ActionLock.ExecuteAsync(() =>
            {
                var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["element_index"] = req.ElementIndex ?? req.ElementIndexSnake,
                    ["action"] = req.Action
                };
                if (!string.IsNullOrEmpty(req.App)) args["app"] = req.App;
                if (req.Pid.HasValue) args["pid"] = req.Pid.Value;
                bool? skipSnapshot = req.SkipSnapshot ?? req.SkipSnapshotSnake;
                if (skipSnapshot.HasValue) args["skip_snapshot"] = skipSnapshot.Value;

                var result = toolHandlers.ExecuteActionLocked("perform_secondary_action", args);
                return MapActionResult(result, req.App, req.Pid);
            });
        });

        app.MapPost("/api/actions/scroll", async (ScrollRequest req) =>
        {
            return await ActionLock.ExecuteAsync(() =>
            {
                var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["direction"] = req.Direction
                };
                if (!string.IsNullOrEmpty(req.App)) args["app"] = req.App;
                if (req.Pid.HasValue) args["pid"] = req.Pid.Value;
                string? elementIndex = req.ElementIndex ?? req.ElementIndexSnake;
                if (!string.IsNullOrEmpty(elementIndex)) args["element_index"] = elementIndex;
                if (req.X.HasValue) args["x"] = req.X.Value;
                if (req.Y.HasValue) args["y"] = req.Y.Value;
                string? coordSpace = req.CoordinateSpace ?? req.CoordinateSpaceSnake;
                if (!string.IsNullOrEmpty(coordSpace)) args["coordinate_space"] = coordSpace;
                if (req.Pages.HasValue) args["pages"] = req.Pages.Value;
                string? scrollMethod = req.ScrollMethod ?? req.ScrollMethodSnake;
                if (!string.IsNullOrEmpty(scrollMethod)) args["scroll_method"] = scrollMethod;
                bool? skipSnapshot = req.SkipSnapshot ?? req.SkipSnapshotSnake;
                if (skipSnapshot.HasValue) args["skip_snapshot"] = skipSnapshot.Value;

                var result = toolHandlers.ExecuteActionLocked("scroll", args);
                return MapActionResult(result, req.App, req.Pid);
            });
        });

        app.MapPost("/api/actions/drag", async (DragRequest req) =>
        {
            return await ActionLock.ExecuteAsync(() =>
            {
                var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["from_x"] = req.FromX ?? req.FromXSnake,
                    ["from_y"] = req.FromY ?? req.FromYSnake,
                    ["to_x"] = req.ToX ?? req.ToXSnake,
                    ["to_y"] = req.ToY ?? req.ToYSnake
                };
                if (!string.IsNullOrEmpty(req.App)) args["app"] = req.App;
                if (req.Pid.HasValue) args["pid"] = req.Pid.Value;
                string? coordSpace = req.CoordinateSpace ?? req.CoordinateSpaceSnake;
                if (!string.IsNullOrEmpty(coordSpace)) args["coordinate_space"] = coordSpace;
                int? durationMs = req.DurationMs ?? req.DurationMsSnake;
                if (durationMs.HasValue) args["duration_ms"] = durationMs.Value;
                string? dragMethod = req.DragMethod ?? req.DragMethodSnake;
                if (!string.IsNullOrEmpty(dragMethod)) args["drag_method"] = dragMethod;
                bool? skipSnapshot = req.SkipSnapshot ?? req.SkipSnapshotSnake;
                if (skipSnapshot.HasValue) args["skip_snapshot"] = skipSnapshot.Value;
                ApplyOptionalHumanize(args, req.Humanize);

                var result = toolHandlers.ExecuteActionLocked("drag", args);
                return MapActionResult(result, req.App, req.Pid);
            });
        });

        app.MapPost("/api/actions/type", async (TypeRequest req) =>
        {
            return await ActionLock.ExecuteAsync(() =>
            {
                var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["text"] = req.Text
                };
                if (!string.IsNullOrEmpty(req.App)) args["app"] = req.App;
                if (req.Pid.HasValue) args["pid"] = req.Pid.Value;
                string? keyMethod = req.KeyMethod ?? req.KeyMethodSnake;
                if (!string.IsNullOrEmpty(keyMethod)) args["key_method"] = keyMethod;
                bool? skipSnapshot = req.SkipSnapshot ?? req.SkipSnapshotSnake;
                if (skipSnapshot.HasValue) args["skip_snapshot"] = skipSnapshot.Value;
                ApplyOptionalHumanize(args, req.Humanize);

                var result = toolHandlers.ExecuteActionLocked("type_text", args);
                return MapActionResult(result, req.App, req.Pid);
            });
        });

        app.MapPost("/api/actions/press-key", async (PressKeyRequest req) =>
        {
            return await ActionLock.ExecuteAsync(() =>
            {
                var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["key"] = req.Key
                };
                if (!string.IsNullOrEmpty(req.App)) args["app"] = req.App;
                if (req.Pid.HasValue) args["pid"] = req.Pid.Value;
                string? keyMethod = req.KeyMethod ?? req.KeyMethodSnake;
                if (!string.IsNullOrEmpty(keyMethod)) args["key_method"] = keyMethod;
                string? keyState = req.KeyState ?? req.KeyStateSnake ?? req.Action;
                if (!string.IsNullOrEmpty(keyState)) args["key_state"] = keyState;
                int? holdMs = req.HoldMs ?? req.HoldMsSnake;
                if (holdMs.HasValue) args["hold_ms"] = holdMs.Value;
                if (req.Repeat.HasValue) args["repeat"] = req.Repeat.Value;
                int? repeatIntervalMs = req.RepeatIntervalMs ?? req.RepeatIntervalMsSnake;
                if (repeatIntervalMs.HasValue) args["repeat_interval_ms"] = repeatIntervalMs.Value;
                bool? skipSnapshot = req.SkipSnapshot ?? req.SkipSnapshotSnake;
                if (skipSnapshot.HasValue) args["skip_snapshot"] = skipSnapshot.Value;
                ApplyOptionalHumanize(args, req.Humanize);

                var result = toolHandlers.ExecuteActionLocked("press_key", args);
                return MapActionResult(result, req.App, req.Pid);
            });
        });

        app.MapPost("/api/actions/batch", async (BatchActionsRequest req) =>
        {
            return await ActionLock.ExecuteAsync(() =>
            {
                var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(req.App)) args["app"] = req.App;
                if (req.Pid.HasValue) args["pid"] = req.Pid.Value;
                if (req.Steps.HasValue) args["steps"] = req.Steps.Value;

                bool? stopOnError = req.StopOnError ?? req.StopOnErrorSnake;
                if (stopOnError.HasValue) args["stop_on_error"] = stopOnError.Value;

                bool? releaseOnError = req.ReleaseOnError ?? req.ReleaseOnErrorSnake;
                if (releaseOnError.HasValue) args["release_on_error"] = releaseOnError.Value;

                bool? skipSnapshot = req.SkipSnapshot ?? req.SkipSnapshotSnake;
                if (skipSnapshot.HasValue) args["skip_snapshot"] = skipSnapshot.Value;
                ApplyOptionalHumanize(args, req.Humanize);

                var result = toolHandlers.ExecuteBatchLocked(args);
                return result.IsError ? Results.BadRequest(result) : Results.Ok(result);
            });
        });

        app.MapPost("/api/actions/release-inputs", () =>
        {
            var released = ActiveInputTracker.ReleaseAll();
            return Results.Ok(released);
        });

        app.MapPost("/api/actions/set-value", async (SetValueRequest req) =>
        {
            return await ActionLock.ExecuteAsync(() =>
            {
                var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["element_index"] = req.ElementIndex ?? req.ElementIndexSnake,
                    ["value"] = req.Value
                };
                if (!string.IsNullOrEmpty(req.App)) args["app"] = req.App;
                if (req.Pid.HasValue) args["pid"] = req.Pid.Value;
                bool? skipSnapshot = req.SkipSnapshot ?? req.SkipSnapshotSnake;
                if (skipSnapshot.HasValue) args["skip_snapshot"] = skipSnapshot.Value;

                var result = toolHandlers.ExecuteActionLocked("set_value", args);
                return MapActionResult(result, req.App, req.Pid);
            });
        });

        app.MapPost("/api/actions/query", (QueryRequest req) =>
        {
            var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(req.App)) args["app"] = req.App;
            if (req.Pid.HasValue) args["pid"] = req.Pid.Value;
            if (!string.IsNullOrEmpty(req.Text)) args["text"] = req.Text;
            if (!string.IsNullOrEmpty(req.Role)) args["role"] = req.Role;
            if (req.Exact.HasValue) args["exact"] = req.Exact.Value;
            if (req.Limit.HasValue) args["limit"] = req.Limit.Value;
            int? maxNodes = req.MaxNodes ?? req.MaxNodesSnake;
            if (maxNodes.HasValue) args["max_nodes"] = maxNodes.Value;
            int? windowId = req.WindowId ?? req.WindowIdSnake;
            if (windowId.HasValue) args["window_id"] = windowId.Value;

            var result = toolHandlers.CallTool("query", args);
            return MapActionResult(result, req.App, req.Pid);
        });

        app.MapPost("/api/actions/select-text", async (SelectTextRequest req) =>
        {
            return await ActionLock.ExecuteAsync(() =>
            {
                var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["element_index"] = req.ElementIndex ?? req.ElementIndexSnake,
                    ["text"] = req.Text
                };
                if (!string.IsNullOrEmpty(req.App)) args["app"] = req.App;
                if (req.Pid.HasValue) args["pid"] = req.Pid.Value;
                if (req.Prefix != null) args["prefix"] = req.Prefix;
                if (req.Suffix != null) args["suffix"] = req.Suffix;
                if (!string.IsNullOrEmpty(req.Selection)) args["selection"] = req.Selection;
                bool? skipSnapshot = req.SkipSnapshot ?? req.SkipSnapshotSnake;
                if (skipSnapshot.HasValue) args["skip_snapshot"] = skipSnapshot.Value;

                var result = toolHandlers.ExecuteActionLocked("select_text", args);
                return MapActionResult(result, req.App, req.Pid);
            });
        });

        // --- OS Extensions: Clipboard ---
        app.MapGet("/api/clipboard", (string? format) =>
        {
            try
            {
                var data = ClipboardService.GetClipboard(format ?? "text");
                return Results.Ok(data);
            }
            catch (ClipboardLockedException ex)
            {
                return Results.Json(ProblemDetails.ClipboardLocked(ex.Message), statusCode: StatusCodes.Status423Locked);
            }
            catch (Exception ex)
            {
                return Results.Json(ProblemDetails.InternalError(ex.Message), statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapPost("/api/clipboard", async (ClipboardSetRequest req) =>
        {
            try
            {
                return await ActionLock.ExecuteAsync(() =>
                {
                    string? imgBase64 = req.ImageBase64 ?? req.ImageBase64Snake;
                    var result = ClipboardService.SetClipboard(req.Text, imgBase64, req.Paste ?? false);
                    return Results.Ok(result);
                });
            }
            catch (ClipboardLockedException ex)
            {
                return Results.Json(ProblemDetails.ClipboardLocked(ex.Message), statusCode: StatusCodes.Status423Locked);
            }
            catch (TimeoutException ex)
            {
                return Results.Json(ProblemDetails.Timeout(ex.Message), statusCode: StatusCodes.Status408RequestTimeout);
            }
            catch (Exception ex)
            {
                return Results.Json(ProblemDetails.InternalError(ex.Message), statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        // --- OS Extensions: Window Management ---
        app.MapPost("/api/windows/{app}/manage", async (string app, ManageWindowRequest req) =>
        {
            try
            {
                return await ActionLock.ExecuteAsync(() =>
                {
                    var result = WindowManager.ManageWindow(app, req.Pid, req.Action, req.X, req.Y, req.Width, req.Height);
                    return Results.Ok(result);
                });
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
            {
                return Results.Json(ProblemDetails.PrivilegeViolation(ex.Message), statusCode: StatusCodes.Status403Forbidden);
            }
            catch (InvalidOperationException ex) when (ex.InnerException is System.ComponentModel.Win32Exception winEx && winEx.NativeErrorCode == 5)
            {
                return Results.Json(ProblemDetails.PrivilegeViolation(winEx.Message), statusCode: StatusCodes.Status403Forbidden);
            }
            catch (ArgumentException ex)
            {
                if (ex.Message.Contains("appNotFound", StringComparison.OrdinalIgnoreCase))
                {
                    string target = req.Pid is > 0 ? $"pid:{req.Pid.Value}" : app;
                    return Results.NotFound(ProblemDetails.AppNotFound(target, ex.Message));
                }
                return Results.BadRequest(ProblemDetails.InvalidArguments(ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ProblemDetails.InvalidArguments(ex.Message));
            }
            catch (TimeoutException ex)
            {
                return Results.Json(ProblemDetails.Timeout(ex.Message), statusCode: StatusCodes.Status408RequestTimeout);
            }
            catch (Exception ex)
            {
                return Results.Json(ProblemDetails.InternalError(ex.Message), statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        // --- OS Extensions: Multi-Monitor Displays & ROI Screenshots ---
        app.MapGet("/api/screens", () =>
        {
            try
            {
                var displays = ScreenCapture.GetDisplays();
                return Results.Ok(new { displays });
            }
            catch (Exception ex)
            {
                return Results.Json(ProblemDetails.InternalError(ex.Message), statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapGet("/api/screens/screenshot", (
            string? display,
            bool? raw,
            double? x,
            double? y,
            double? width,
            double? height,
            string? format,
            int? max_dimension,
            int? maxDimension) =>
        {
            int? maxDim = max_dimension ?? maxDimension;
            try
            {
                if (raw == true)
                {
                    // Zero-Base64 direct byte[] path for 30-60 FPS Python (OpenCV/numpy) & C# HttpClient loops
                    byte[] rawBytes = ScreenCapture.CaptureScreenRawBytes(
                        display ?? "all",
                        out _,
                        out _,
                        out string mimeType,
                        x,
                        y,
                        width,
                        height,
                        format,
                        maxDim);

                    return Results.File(rawBytes, string.IsNullOrWhiteSpace(mimeType) ? "image/png" : mimeType);
                }

                var snapshot = ScreenCapture.CaptureScreen(
                    display ?? "all",
                    x,
                    y,
                    width,
                    height,
                    format,
                    maxDim);

                return Results.Ok(snapshot);
            }
            catch (ArgumentException ex)
            {
                if (ex.Message.Contains("displayNotFound", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.NotFound(ProblemDetails.DisplayNotFound(display ?? "unknown", ex.Message));
                }
                return Results.BadRequest(ProblemDetails.InvalidArguments(ex.Message));
            }
            catch (Exception ex)
            {
                return Results.Json(ProblemDetails.InternalError(ex.Message), statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        // --- OS Extensions: Process Lifecycle Management ---
        app.MapPost("/api/processes/launch", (ProcessLaunchRequest req) =>
        {
            try
            {
                string? workingDir = req.WorkingDir ?? req.WorkingDirSnake;
                var result = ProcessManager.LaunchApp(req.Path, req.Args, workingDir);
                return Results.Ok(result);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
            {
                return Results.Json(ProblemDetails.PrivilegeViolation(ex.Message), statusCode: StatusCodes.Status403Forbidden);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ProblemDetails.InvalidArguments(ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                if (ex.InnerException is System.ComponentModel.Win32Exception winEx && winEx.NativeErrorCode == 5)
                {
                    return Results.Json(ProblemDetails.PrivilegeViolation(winEx.Message), statusCode: StatusCodes.Status403Forbidden);
                }
                return Results.BadRequest(ProblemDetails.InvalidArguments(ex.Message));
            }
            catch (Exception ex)
            {
                return Results.Json(ProblemDetails.InternalError(ex.Message), statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapPost("/api/processes/close", (ProcessCloseRequest req) =>
        {
            try
            {
                int timeoutMs = req.TimeoutMs ?? req.TimeoutMsSnake ?? 3000;
                var result = ProcessManager.CloseApp(req.App, req.Pid, req.Force ?? false, timeoutMs);
                return Results.Ok(result);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
            {
                return Results.Json(ProblemDetails.PrivilegeViolation(ex.Message), statusCode: StatusCodes.Status403Forbidden);
            }
            catch (InvalidOperationException ex) when (ex.InnerException is System.ComponentModel.Win32Exception winEx && winEx.NativeErrorCode == 5)
            {
                return Results.Json(ProblemDetails.PrivilegeViolation(winEx.Message), statusCode: StatusCodes.Status403Forbidden);
            }
            catch (ArgumentException ex)
            {
                if (ex.Message.Contains("appNotFound", StringComparison.OrdinalIgnoreCase))
                {
                    string target = req.Pid is > 0 ? $"pid:{req.Pid.Value}" : (req.App ?? "unknown");
                    return Results.NotFound(ProblemDetails.AppNotFound(target, ex.Message));
                }
                return Results.BadRequest(ProblemDetails.InvalidArguments(ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ProblemDetails.InvalidArguments(ex.Message));
            }
            catch (Exception ex)
            {
                return Results.Json(ProblemDetails.InternalError(ex.Message), statusCode: StatusCodes.Status500InternalServerError);
            }
        });
    }
}

/// <summary>
/// Request payload for /api/actions/click.
/// ClickMethod options: "auto" (default), "accessibility", "app_post", "stealth", "global", "hardware".
/// </summary>
public sealed record ClickRequest(
    string? App = null,
    int? Pid = null,
    string? ElementIndex = null,
    double? X = null,
    double? Y = null,
    string? CoordinateSpace = null,
    int? ClickCount = null,
    string? MouseButton = null,
    string? ClickMethod = null,
    bool? SkipSnapshot = null,
    string? ButtonState = null,
    int? HoldMs = null,
    JsonElement? Humanize = null)
{
    [JsonPropertyName("element_index")] public string? ElementIndexSnake { get; init; }
    [JsonPropertyName("coordinate_space")] public string? CoordinateSpaceSnake { get; init; }
    [JsonPropertyName("click_count")] public int? ClickCountSnake { get; init; }
    [JsonPropertyName("mouse_button")] public string? MouseButtonSnake { get; init; }
    [JsonPropertyName("button_state")] public string? ButtonStateSnake { get; init; }
    [JsonPropertyName("hold_ms")] public int? HoldMsSnake { get; init; }
    [JsonPropertyName("click_method")] public string? ClickMethodSnake { get; init; }
    [JsonPropertyName("skip_snapshot")] public bool? SkipSnapshotSnake { get; init; }
}

public sealed record MoveMouseRequest(
    string? App = null,
    int? Pid = null,
    double? X = null,
    double? Y = null,
    string? CoordinateSpace = null,
    double? DeltaX = null,
    double? DeltaY = null,
    int? Steps = null,
    int? DurationMs = null,
    string? Method = null,
    bool? SkipSnapshot = null,
    JsonElement? Humanize = null)
{
    [JsonPropertyName("coordinate_space")] public string? CoordinateSpaceSnake { get; init; }
    [JsonPropertyName("delta_x")] public double? DeltaXSnake { get; init; }
    [JsonPropertyName("delta_y")] public double? DeltaYSnake { get; init; }
    [JsonPropertyName("duration_ms")] public int? DurationMsSnake { get; init; }
    [JsonPropertyName("skip_snapshot")] public bool? SkipSnapshotSnake { get; init; }
}

public sealed record TrajectoryRequest(
    string? App = null,
    int? Pid = null,
    JsonElement? Waypoints = null,
    double? IntervalMs = null,
    string? CoordinateSpace = null,
    string? Method = null,
    string? MouseButton = null,
    bool? HoldButton = null,
    bool? SkipSnapshot = null,
    JsonElement? Humanize = null)
{
    [JsonPropertyName("interval_ms")] public double? IntervalMsSnake { get; init; }
    [JsonPropertyName("coordinate_space")] public string? CoordinateSpaceSnake { get; init; }
    [JsonPropertyName("mouse_button")] public string? MouseButtonSnake { get; init; }
    [JsonPropertyName("hold_button")] public bool? HoldButtonSnake { get; init; }
    [JsonPropertyName("skip_snapshot")] public bool? SkipSnapshotSnake { get; init; }
}

public sealed record VisionProbeRequest(
    string? App = null,
    int? Pid = null,
    string? Display = null,
    string? CoordinateSpace = null,
    JsonElement? Points = null,
    JsonElement? Regions = null)
{
    [JsonPropertyName("coordinate_space")] public string? CoordinateSpaceSnake { get; init; }
}

public sealed record VisionWaitRequest(
    string? App = null,
    int? Pid = null,
    string? Display = null,
    string? CoordinateSpace = null,
    JsonElement? Points = null,
    JsonElement? Regions = null,
    int? PollHz = null,
    int? TimeoutMs = null,
    string? MatchMode = null,
    int? ConsecutiveHits = null,
    JsonElement? OnMatch = null)
{
    [JsonPropertyName("coordinate_space")] public string? CoordinateSpaceSnake { get; init; }
    [JsonPropertyName("poll_hz")] public int? PollHzSnake { get; init; }
    [JsonPropertyName("timeout_ms")] public int? TimeoutMsSnake { get; init; }
    [JsonPropertyName("match_mode")] public string? MatchModeSnake { get; init; }
    [JsonPropertyName("consecutive_hits")] public int? ConsecutiveHitsSnake { get; init; }
    [JsonPropertyName("on_match")] public JsonElement? OnMatchSnake { get; init; }
}

public sealed record SharedFrameStartRequest(
    string? App = null,
    int? Pid = null,
    string? Display = null,
    bool? ClientOnly = null,
    double? RegionX = null,
    double? RegionY = null,
    double? RegionWidth = null,
    double? RegionHeight = null,
    int? MaxFps = null,
    bool? OnlyOnChange = null,
    int? OwnerPid = null)
{
    [JsonPropertyName("client_only")] public bool? ClientOnlySnake { get; init; }
    [JsonPropertyName("region_x")] public double? RegionXSnake { get; init; }
    [JsonPropertyName("region_y")] public double? RegionYSnake { get; init; }
    [JsonPropertyName("region_width")] public double? RegionWidthSnake { get; init; }
    [JsonPropertyName("region_height")] public double? RegionHeightSnake { get; init; }
    [JsonPropertyName("max_fps")] public int? MaxFpsSnake { get; init; }
    [JsonPropertyName("only_on_change")] public bool? OnlyOnChangeSnake { get; init; }
    [JsonPropertyName("owner_pid")] public int? OwnerPidSnake { get; init; }
}

public sealed record SecondaryRequest(
    string? App = null,
    int? Pid = null,
    string? ElementIndex = null,
    string Action = "",
    bool? SkipSnapshot = null)
{
    [JsonPropertyName("element_index")] public string? ElementIndexSnake { get; init; }
    [JsonPropertyName("skip_snapshot")] public bool? SkipSnapshotSnake { get; init; }
}

public sealed record ScrollRequest(
    string? App = null,
    int? Pid = null,
    string Direction = "",
    string? ElementIndex = null,
    double? Pages = null,
    bool? SkipSnapshot = null,
    double? X = null,
    double? Y = null,
    string? CoordinateSpace = null,
    string? ScrollMethod = null)
{
    [JsonPropertyName("element_index")] public string? ElementIndexSnake { get; init; }
    [JsonPropertyName("coordinate_space")] public string? CoordinateSpaceSnake { get; init; }
    [JsonPropertyName("scroll_method")] public string? ScrollMethodSnake { get; init; }
    [JsonPropertyName("skip_snapshot")] public bool? SkipSnapshotSnake { get; init; }
}

public sealed record DragRequest(
    string? App = null,
    int? Pid = null,
    double? FromX = null,
    double? FromY = null,
    double? ToX = null,
    double? ToY = null,
    string? CoordinateSpace = null,
    bool? SkipSnapshot = null,
    int? DurationMs = null,
    string? DragMethod = null,
    JsonElement? Humanize = null)
{
    [JsonPropertyName("from_x")] public double? FromXSnake { get; init; }
    [JsonPropertyName("from_y")] public double? FromYSnake { get; init; }
    [JsonPropertyName("to_x")] public double? ToXSnake { get; init; }
    [JsonPropertyName("to_y")] public double? ToYSnake { get; init; }
    [JsonPropertyName("coordinate_space")] public string? CoordinateSpaceSnake { get; init; }
    [JsonPropertyName("duration_ms")] public int? DurationMsSnake { get; init; }
    [JsonPropertyName("drag_method")] public string? DragMethodSnake { get; init; }
    [JsonPropertyName("skip_snapshot")] public bool? SkipSnapshotSnake { get; init; }
}

public sealed record TypeRequest(
    string? App = null,
    int? Pid = null,
    string Text = "",
    string? KeyMethod = null,
    bool? SkipSnapshot = null,
    JsonElement? Humanize = null)
{
    [JsonPropertyName("key_method")] public string? KeyMethodSnake { get; init; }
    [JsonPropertyName("skip_snapshot")] public bool? SkipSnapshotSnake { get; init; }
}

public sealed record PressKeyRequest(
    string? App = null,
    int? Pid = null,
    string Key = "",
    string? KeyMethod = null,
    bool? SkipSnapshot = null,
    string? KeyState = null,
    string? Action = null,
    int? HoldMs = null,
    int? Repeat = null,
    int? RepeatIntervalMs = null,
    JsonElement? Humanize = null)
{
    [JsonPropertyName("key_method")] public string? KeyMethodSnake { get; init; }
    [JsonPropertyName("key_state")] public string? KeyStateSnake { get; init; }
    [JsonPropertyName("hold_ms")] public int? HoldMsSnake { get; init; }
    [JsonPropertyName("repeat_interval_ms")] public int? RepeatIntervalMsSnake { get; init; }
    [JsonPropertyName("skip_snapshot")] public bool? SkipSnapshotSnake { get; init; }
}

public sealed record BatchActionsRequest(
    string? App = null,
    int? Pid = null,
    JsonElement? Steps = null,
    bool? StopOnError = null,
    bool? ReleaseOnError = null,
    bool? SkipSnapshot = null,
    JsonElement? Humanize = null)
{
    [JsonPropertyName("stop_on_error")] public bool? StopOnErrorSnake { get; init; }
    [JsonPropertyName("release_on_error")] public bool? ReleaseOnErrorSnake { get; init; }
    [JsonPropertyName("skip_snapshot")] public bool? SkipSnapshotSnake { get; init; }
}

public sealed record SetValueRequest(
    string? App = null,
    int? Pid = null,
    string? ElementIndex = null,
    string Value = "",
    bool? SkipSnapshot = null)
{
    [JsonPropertyName("element_index")] public string? ElementIndexSnake { get; init; }
    [JsonPropertyName("skip_snapshot")] public bool? SkipSnapshotSnake { get; init; }
}

public sealed record QueryRequest(
    string? App = null,
    int? Pid = null,
    string? Text = null,
    string? Role = null,
    bool? Exact = null,
    int? Limit = null,
    int? MaxNodes = null,
    int? WindowId = null)
{
    [JsonPropertyName("max_nodes")] public int? MaxNodesSnake { get; init; }
    [JsonPropertyName("window_id")] public int? WindowIdSnake { get; init; }
}

public sealed record SelectTextRequest(
    string? App = null,
    int? Pid = null,
    string? ElementIndex = null,
    string Text = "",
    string? Prefix = null,
    string? Suffix = null,
    string? Selection = null,
    bool? SkipSnapshot = null)
{
    [JsonPropertyName("element_index")] public string? ElementIndexSnake { get; init; }
    [JsonPropertyName("skip_snapshot")] public bool? SkipSnapshotSnake { get; init; }
}

public sealed record ClipboardSetRequest(string? Text = null, string? ImageBase64 = null, bool? Paste = null)
{
    [JsonPropertyName("image_base64")] public string? ImageBase64Snake { get; init; }
}

public sealed record ManageWindowRequest(int? Pid, string Action, double? X = null, double? Y = null, double? Width = null, double? Height = null);

public sealed record ProcessLaunchRequest(string Path, string? Args = null, string? WorkingDir = null)
{
    [JsonPropertyName("working_dir")] public string? WorkingDirSnake { get; init; }
}

public sealed record ProcessCloseRequest(string? App = null, int? Pid = null, bool? Force = null, int? TimeoutMs = null)
{
    [JsonPropertyName("timeout_ms")] public int? TimeoutMsSnake { get; init; }
}
