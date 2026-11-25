using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using TerkirdoComputer.Core;

namespace TerkirdoComputer.Windows;

public sealed class AutomationService : IDisposable
{
    private const int DefaultTextLimit = 500;
    private const int DefaultMaxTreeNodes = 1200;
    private const int DefaultMaxTreeDepth = 64;

    private readonly UIA3Automation _automation;
    private readonly CacheRequest _treeCacheRequest;
    private readonly object _cacheSync = new();
    private readonly Dictionary<string, AppSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<int, AutomationElement>> _elementCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _pidCache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record TargetedElementEntry(
        int ElementIndex,
        int ProcessId,
        IntPtr WindowHwnd,
        ElementRecord Record,
        AutomationElement Element,
        DateTime ExpiresAtUtc);

    private readonly Dictionary<int, TargetedElementEntry> _targetedElements = new();
    private int _nextTargetedElementIndex = 1_000_001;
    private static readonly TimeSpan TargetedElementTtl = TimeSpan.FromSeconds(120);
    private const int MaxTargetedElements = 5000;

    public AutomationService()
    {
        _automation = new UIA3Automation();
        _treeCacheRequest = BuildTreeCacheRequest();
    }

    private CacheRequest BuildTreeCacheRequest()
    {
        var cr = new CacheRequest
        {
            TreeScope = TreeScope.Element | TreeScope.Children,
            AutomationElementMode = AutomationElementMode.Full
        };

        var props = _automation.PropertyLibrary.Element;
        cr.Add(props.RuntimeId);
        cr.Add(props.BoundingRectangle);
        cr.Add(props.AutomationId);
        cr.Add(props.Name);
        cr.Add(props.ControlType);
        cr.Add(props.LocalizedControlType);
        cr.Add(props.ClassName);
        cr.Add(props.NativeWindowHandle);
        cr.Add(props.IsOffscreen);
        cr.Add(props.IsEnabled);
        cr.Add(props.HasKeyboardFocus);

        var valProps = _automation.PropertyLibrary.Value;
        cr.Add(valProps.Value);
        cr.Add(valProps.IsReadOnly);

        var ecProps = _automation.PropertyLibrary.ExpandCollapse;
        cr.Add(ecProps.ExpandCollapseState);

        var selProps = _automation.PropertyLibrary.SelectionItem;
        cr.Add(selProps.IsSelected);

        var pats = _automation.PatternLibrary;
        cr.Add(pats.InvokePattern);
        cr.Add(pats.ValuePattern);
        cr.Add(pats.TogglePattern);
        cr.Add(pats.SelectionItemPattern);
        cr.Add(pats.ExpandCollapsePattern);
        cr.Add(pats.ScrollPattern);
        cr.Add(pats.ScrollItemPattern);

        return cr;
    }

    private static bool TryGetCachedOrLiveProperty<T>(IAutomationProperty<T> property, out T value)
    {
        try
        {
            if (property.TryGetValue(out var cachedVal) && cachedVal is not null)
            {
                value = cachedVal;
                return true;
            }
            value = default!;
            return false;
        }
        catch
        {
            if (CacheRequest.IsCachingActive)
            {
                try
                {
                    using (CacheRequest.ForceNoCache())
                    {
                        if (property.TryGetValue(out var liveVal) && liveVal is not null)
                        {
                            value = liveVal;
                            return true;
                        }
                    }
                }
                catch
                {
                }
            }

            value = default!;
            return false;
        }
    }

    private static bool IsPatternSupportedCachedOrLive<TPattern>(IAutomationPattern<TPattern> pattern, out TPattern? instance)
        where TPattern : class, FlaUI.Core.Patterns.Infrastructure.IPattern
    {
        try
        {
            return pattern.TryGetPattern(out instance) && instance != null;
        }
        catch
        {
            if (CacheRequest.IsCachingActive)
            {
                try
                {
                    using (CacheRequest.ForceNoCache())
                    {
                        return pattern.TryGetPattern(out instance) && instance != null;
                    }
                }
                catch
                {
                }
            }

            instance = null;
            return false;
        }
    }

    public void Dispose()
    {
        _automation.Dispose();
    }

    public string ListApps()
    {
        var processes = Process.GetProcesses()
            .Where(p => p.MainWindowHandle != IntPtr.Zero)
            .OrderBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Id);

        var lines = new List<string>();
        foreach (var proc in processes)
        {
            string title = proc.MainWindowTitle;
            if (string.IsNullOrWhiteSpace(title))
            {
                title = "untitled";
            }
            lines.Add($"{proc.ProcessName} -- {proc.ProcessName} [running, pid={proc.Id}, window={title}]");
        }

        if (lines.Count == 0)
        {
            return "No running top-level apps are visible to this Windows runtime.";
        }

        return string.Join("\n", lines);
    }

    public Process ResolveApp(string? query, int? pid = null)
    {
        int? strictPid = null;
        if (pid.HasValue)
        {
            if (pid.Value <= 0)
            {
                throw new ArgumentException("Invalid pid: must be a positive integer.");
            }
            strictPid = pid.Value;
        }
        else if (WindowManager.TryParseStrictPidPrefix(query, out int parsedPrefixPid))
        {
            strictPid = parsedPrefixPid;
        }

        // Strict PID targeting ("pid:<id>" or explicit pid parameter): never fall back to sibling processes
        if (strictPid.HasValue)
        {
            try
            {
                var strictProc = Process.GetProcessById(strictPid.Value);
                if (!strictProc.HasExited &&
                    (WindowManager.GetTargetHwnd(strictProc) != IntPtr.Zero || strictProc.MainWindowHandle != IntPtr.Zero))
                {
                    return strictProc;
                }
            }
            catch
            {
                // Fall through to strict appNotFound
            }

            throw new InvalidOperationException($"appNotFound(\"pid:{strictPid.Value}\")");
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("Missing required argument: app");
        }

        string normalized = query.Trim();
        string processQuery = normalized;
        if (processQuery.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            processQuery = processQuery[..^4];
        }

        // 1. Fast path: Direct numeric PID lookup without enumerating all processes
        if (int.TryParse(normalized, out int pidValue))
        {
            if (pidValue <= 0)
            {
                throw new ArgumentException("Invalid pid: must be a positive integer.");
            }

            try
            {
                var byPid = Process.GetProcessById(pidValue);
                if (!byPid.HasExited)
                {
                    if (WindowManager.GetTargetHwnd(byPid) != IntPtr.Zero || byPid.MainWindowHandle != IntPtr.Zero)
                    {
                        return byPid;
                    }
                }
                else
                {
                    var sameName = Process.GetProcessesByName(byPid.ProcessName)
                        .FirstOrDefault(p => WindowManager.GetTargetHwnd(p) != IntPtr.Zero || p.MainWindowHandle != IntPtr.Zero);
                    if (sameName != null)
                    {
                        return sameName;
                    }
                }
            }
            catch { }
        }

        // 2. Fast path: Check previously resolved PID cache
        bool hasCachedPid;
        int cachedPid;
        lock (_cacheSync)
        {
            hasCachedPid = _pidCache.TryGetValue(normalized, out cachedPid);
        }

        if (hasCachedPid)
        {
            try
            {
                var cachedProc = Process.GetProcessById(cachedPid);
                if (!cachedProc.HasExited && (WindowManager.GetTargetHwnd(cachedProc) != IntPtr.Zero || cachedProc.MainWindowHandle != IntPtr.Zero))
                {
                    if (string.Equals(cachedProc.ProcessName, processQuery, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals($"{cachedProc.ProcessName}.exe", normalized, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(cachedProc.MainWindowTitle, normalized, StringComparison.OrdinalIgnoreCase) ||
                        cachedProc.MainWindowTitle.Contains(normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        return cachedProc;
                    }
                }
            }
            catch { }
        }

        // 3. Fast path: Try Process.GetProcessesByName before enumerating all OS processes
        try
        {
            var byName = Process.GetProcessesByName(processQuery);
            var matchByName = byName.FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero)
                ?? byName.FirstOrDefault(p => WindowManager.GetTargetHwnd(p) != IntPtr.Zero);
            if (matchByName != null)
            {
                lock (_cacheSync)
                {
                    _pidCache[normalized] = matchByName.Id;
                }
                return matchByName;
            }
        }
        catch { }

        // 4. Fallback: Full scan across all processes with a top-level window (e.g., window title query)
        var processes = Process.GetProcesses()
            .Where(p => p.MainWindowHandle != IntPtr.Zero)
            .ToList();

        var match = processes.FirstOrDefault(p =>
            string.Equals(p.ProcessName, processQuery, StringComparison.OrdinalIgnoreCase) ||
            string.Equals($"{p.ProcessName}.exe", normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.MainWindowTitle, normalized, StringComparison.OrdinalIgnoreCase) ||
            p.MainWindowTitle.Contains(normalized, StringComparison.OrdinalIgnoreCase));

        if (match != null)
        {
            lock (_cacheSync)
            {
                _pidCache[normalized] = match.Id;
            }
            return match;
        }

        if (TestEnvFlagEnabled("OPEN_COMPUTER_USE_WINDOWS_ALLOW_APP_LAUNCH"))
        {
            try
            {
                var started = Process.Start(new ProcessStartInfo(normalized) { UseShellExecute = true });
                if (started != null)
                {
                    for (int i = 0; i < 20; i++)
                    {
                        Thread.Sleep(250);
                        started.Refresh();
                        if (started.MainWindowHandle != IntPtr.Zero)
                        {
                            lock (_cacheSync)
                            {
                                _pidCache[normalized] = started.Id;
                            }
                            return started;
                        }
                    }
                }
            }
            catch
            {
                // Fall through to exception
            }
        }

        throw new InvalidOperationException($"appNotFound(\"{query}\")");
    }

    public AutomationElement GetMainElement(Process process, IntPtr hwnd = default)
    {
        if (hwnd == IntPtr.Zero)
        {
            hwnd = WindowManager.GetTargetHwnd(process);
            if (hwnd == IntPtr.Zero) hwnd = process.MainWindowHandle;
        }

        if (hwnd != IntPtr.Zero)
        {
            try
            {
                var el = _automation.FromHandle(hwnd);
                if (el != null) return el;
            }
            catch
            {
                // Fallback to desktop child search
            }
        }

        try
        {
            var condition = _automation.ConditionFactory.ByProcessId(process.Id);
            var children = _automation.GetDesktop().FindAllChildren(condition);
            if (children.Length > 0)
            {
                return children[0];
            }
        }
        catch
        {
            // Ignore
        }

        throw new InvalidOperationException(
            $"No top-level UI Automation window is available for {process.ProcessName}. Run the Windows runtime in the signed-in desktop session.");
    }

    public Frame? GetWindowBounds(Process process, AutomationElement? element = null, IntPtr hwnd = default)
    {
        if (hwnd == IntPtr.Zero)
        {
            hwnd = WindowManager.GetTargetHwnd(process);
            if (hwnd == IntPtr.Zero) hwnd = process.MainWindowHandle;
        }

        if (hwnd != IntPtr.Zero)
        {
            if (NativeMethods.GetWindowRect(hwnd, out var rect))
            {
                return new Frame(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            }
        }

        if (element != null)
        {
            try
            {
                if (TryGetCachedOrLiveProperty(element.Properties.BoundingRectangle, out var bounds) &&
                    bounds.Width > 0 && bounds.Height > 0)
                {
                    return new Frame(bounds.X, bounds.Y, bounds.Width, bounds.Height);
                }
            }
            catch
            {
                // Ignore
            }
        }

        return null;
    }

    public AppSnapshot BuildSnapshot(
        string? query,
        int? textLimit = DefaultTextLimit,
        int maxTreeNodes = DefaultMaxTreeNodes,
        int maxTreeDepth = DefaultMaxTreeDepth,
        bool includeScreenshot = true,
        bool includeAx = true,
        string? windowPlacement = null,
        double? regionX = null,
        double? regionY = null,
        double? regionWidth = null,
        double? regionHeight = null,
        bool clientOnly = false,
        int? pid = null)
    {
        if (!string.IsNullOrWhiteSpace(windowPlacement) &&
            !string.Equals(windowPlacement.Trim(), "keep", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"window_placement '{windowPlacement}' is not supported on Windows");
        }

        if (!includeAx && !includeScreenshot)
        {
            throw new ArgumentException("At least one of include_ax or include_screenshot must be true");
        }

        bool hasRegion = ScreenCapture.ValidateOptionalRegion(regionX, regionY, regionWidth, regionHeight, "region_");

        NativeMethods.EnsurePerMonitorDpiAwareness();

        var process = ResolveApp(query, pid);
        IntPtr hwnd = WindowManager.GetTargetHwnd(process);
        if (hwnd == IntPtr.Zero) hwnd = process.MainWindowHandle;

        AutomationElement mainElement;
        using (_treeCacheRequest.Activate())
        {
            mainElement = GetMainElement(process, hwnd);
        }

        var bounds = GetWindowBounds(process, mainElement, hwnd);
        var clientBounds = hwnd != IntPtr.Zero ? WindowManager.GetClientScreenBounds(hwnd) : null;
        PointOffset? clientOffset = (bounds != null && clientBounds != null)
            ? new PointOffset(clientBounds.X - bounds.X, clientBounds.Y - bounds.Y)
            : null;
        bool isMinimized = hwnd != IntPtr.Zero && NativeMethods.IsIconic(hwnd);
        bool isMaximized = hwnd != IntPtr.Zero && NativeMethods.IsZoomed(hwnd);
        bool isForeground = hwnd != IntPtr.Zero && NativeMethods.GetForegroundWindow() == hwnd;

        Frame? regionFrame = hasRegion
            ? new Frame(
                Math.Round(regionX!.Value),
                Math.Round(regionY!.Value),
                Math.Max(1, Math.Round(regionWidth!.Value)),
                Math.Max(1, Math.Round(regionHeight!.Value)))
            : null;

        // Capture screenshot on a background thread concurrently with UIA tree traversal
        string screenshotMimeType = "image/png";
        Task<string?>? screenshotTask = includeScreenshot
            ? Task.Run(() => ScreenCapture.CaptureWindowImageBase64(
                bounds,
                hwnd,
                out screenshotMimeType,
                regionX,
                regionY,
                regionWidth,
                regionHeight,
                clientOnly: clientOnly))
            : null;

        var elementCache = new Dictionary<int, AutomationElement>();
        List<ElementRecord> records = [];
        List<string> lines = [];
        string? focusedSummary = null;
        string? selectedText = null;

        if (includeAx)
        {
            int effectiveMaxNodes = maxTreeNodes > 0 ? maxTreeNodes : DefaultMaxTreeNodes;
            int effectiveMaxDepth = maxTreeDepth > 0 ? maxTreeDepth : DefaultMaxTreeDepth;

            using (_treeCacheRequest.Activate())
            {
                (records, lines) = RenderTree(mainElement, bounds, textLimit, effectiveMaxNodes, effectiveMaxDepth, elementCache, hwnd);
            }

            AutomationElement? focusedElement = null;
            try
            {
                var f = _automation.FocusedElement();
                if (f != null && f.Properties.ProcessId.ValueOrDefault == process.Id)
                {
                    focusedElement = f;
                }
            }
            catch { }

            focusedSummary = GetFocusedSummary(focusedElement, textLimit);
            selectedText = GetSelectedText(focusedElement, textLimit);
        }
        else
        {
            InvalidateElementCache(query, process);
        }

        string? screenshot = screenshotTask != null ? screenshotTask.GetAwaiter().GetResult() : null;

        var snapshot = new AppSnapshot
        {
            App = new AppDescriptor
            {
                Name = process.ProcessName,
                BundleIdentifier = process.ProcessName,
                PID = process.Id
            },
            WindowTitle = LimitText(process.MainWindowTitle, textLimit),
            WindowHandle = hwnd != IntPtr.Zero ? hwnd.ToInt64() : null,
            WindowBounds = bounds,
            ClientBounds = clientBounds,
            ClientOffset = clientOffset,
            ClientOnly = clientOnly,
            CoordinateSpace = clientOnly ? "client" : "window",
            IsMinimized = isMinimized,
            IsMaximized = isMaximized,
            IsForeground = isForeground,
            Region = regionFrame,
            ScreenshotPngBase64 = screenshot,
            ScreenshotMimeType = screenshotMimeType,
            ScreenshotOmitted = !includeScreenshot,
            ScreenshotUnavailable = includeScreenshot && string.IsNullOrEmpty(screenshot),
            AccessibilityOmitted = !includeAx,
            TreeLines = lines,
            FocusedSummary = focusedSummary,
            SelectedText = selectedText,
            Elements = records
        };

        RememberSnapshot(query, snapshot, elementCache);
        return snapshot;
    }

    private static int? ResolveStrictPid(string? app, int? pid)
    {
        if (pid.HasValue)
        {
            if (pid.Value <= 0)
            {
                throw new ArgumentException("Invalid pid: must be a positive integer.");
            }
            return pid.Value;
        }

        if (WindowManager.TryParseStrictPidPrefix(app, out int parsedPrefixPid))
        {
            return parsedPrefixPid;
        }

        return null;
    }

    private static List<string> BuildLookupKeys(string? app, int? pid = null, int? resolvedStrictPid = null)
    {
        int? strictPid = resolvedStrictPid ?? ResolveStrictPid(app, pid);
        if (strictPid.HasValue)
        {
            return [$"pid:{strictPid.Value}", strictPid.Value.ToString()];
        }

        var keys = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(app))
        {
            string trimmed = app.Trim();
            keys.Add(trimmed);
            if (int.TryParse(trimmed, out int numericPid) && numericPid > 0)
            {
                keys.Add($"pid:{numericPid}");
                keys.Add(numericPid.ToString());
            }
        }

        return keys;
    }

    private void InvalidateElementCache(string? query, Process process)
    {
        int pid = process.Id;
        string procName = process.ProcessName;
        var keys = new[]
        {
            query,
            procName,
            pid.ToString(),
            $"pid:{pid}"
        };

        lock (_cacheSync)
        {
            foreach (var k in keys)
            {
                if (!string.IsNullOrWhiteSpace(k))
                {
                    _elementCache.Remove(k.Trim());
                }
            }

            var targetedKeys = _targetedElements
                .Where(kv => kv.Value.ProcessId == pid)
                .Select(kv => kv.Key)
                .ToList();
            foreach (int id in targetedKeys)
            {
                _targetedElements.Remove(id);
            }
        }
    }

    public AppSnapshot? GetCurrentSnapshot(string? app, int? pid = null)
    {
        int? strictPid = ResolveStrictPid(app, pid);
        var keys = BuildLookupKeys(app, pid, strictPid);
        lock (_cacheSync)
        {
            foreach (string key in keys)
            {
                if (_snapshots.TryGetValue(key, out var snapshot))
                {
                    if (strictPid.HasValue && snapshot.App.PID != strictPid.Value)
                    {
                        continue;
                    }
                    return snapshot;
                }
            }
            return null;
        }
    }

    public ElementRecord? GetElementRecordFromSnapshot(string? app, int elementIndex, int? pid = null)
    {
        int? strictPid = ResolveStrictPid(app, pid);
        var keys = BuildLookupKeys(app, pid, strictPid);
        AppSnapshot? snapshot = null;
        lock (_cacheSync)
        {
            PruneExpiredTargetedElementsLocked();
            if (_targetedElements.TryGetValue(elementIndex, out var targeted))
            {
                if (strictPid.HasValue && targeted.ProcessId != strictPid.Value)
                {
                    return null;
                }
                return targeted.Record;
            }

            foreach (string key in keys)
            {
                if (_snapshots.TryGetValue(key, out var candidate))
                {
                    if (strictPid.HasValue && candidate.App.PID != strictPid.Value)
                    {
                        continue;
                    }
                    snapshot = candidate;
                    break;
                }
            }
        }

        return snapshot?.Elements?.FirstOrDefault(e => e.Index == elementIndex);
    }

    public AutomationElement? GetElementFromSnapshot(string? app, int elementIndex, int? pid = null)
    {
        int? strictPid = ResolveStrictPid(app, pid);
        var keys = BuildLookupKeys(app, pid, strictPid);
        AppSnapshot? snapshot = null;
        lock (_cacheSync)
        {
            PruneExpiredTargetedElementsLocked();
            if (_targetedElements.TryGetValue(elementIndex, out var targeted))
            {
                if (strictPid.HasValue && targeted.ProcessId != strictPid.Value)
                {
                    return null;
                }
                return targeted.Element;
            }

            foreach (string key in keys)
            {
                if (_elementCache.TryGetValue(key, out var cache) && cache.TryGetValue(elementIndex, out var element))
                {
                    if (strictPid.HasValue &&
                        _snapshots.TryGetValue(key, out var ownerSnap) &&
                        ownerSnap.App.PID != strictPid.Value)
                    {
                        continue;
                    }
                    return element;
                }
            }

            foreach (string key in keys)
            {
                if (_snapshots.TryGetValue(key, out var candidate))
                {
                    if (strictPid.HasValue && candidate.App.PID != strictPid.Value)
                    {
                        continue;
                    }
                    snapshot = candidate;
                    break;
                }
            }
        }

        // Search in current snapshot elements outside _cacheSync so no lock is held during UIA COM calls
        if (snapshot != null)
        {
            var record = snapshot.Elements?.FirstOrDefault(e => e.Index == elementIndex);
            if (record != null)
            {
                try
                {
                    var process = ResolveApp(app, pid);
                    return FindElement(process, record);
                }
                catch
                {
                    return null;
                }
            }
        }

        return null;
    }

    private void PruneExpiredTargetedElementsLocked()
    {
        var now = DateTime.UtcNow;
        var expired = _targetedElements
            .Where(kv => kv.Value.ExpiresAtUtc <= now)
            .Select(kv => kv.Key)
            .ToList();
        foreach (int k in expired)
        {
            _targetedElements.Remove(k);
        }
    }

    private int RegisterTargetedElement(int pid, IntPtr windowHwnd, ElementRecord record, AutomationElement element)
    {
        lock (_cacheSync)
        {
            PruneExpiredTargetedElementsLocked();
            if (_targetedElements.Count >= MaxTargetedElements)
            {
                var oldest = _targetedElements.OrderBy(kv => kv.Value.ExpiresAtUtc).First().Key;
                _targetedElements.Remove(oldest);
            }

            int index = _nextTargetedElementIndex++;
            record.Index = index;
            _targetedElements[index] = new TargetedElementEntry(
                index,
                pid,
                windowHwnd,
                record,
                element,
                DateTime.UtcNow.Add(TargetedElementTtl));
            return index;
        }
    }

    public QueryResult Query(
        string? app,
        string? text = null,
        string? role = null,
        bool exact = false,
        int limit = 20,
        int maxNodes = 500,
        int? windowId = null,
        int? pid = null)
    {
        if (string.IsNullOrWhiteSpace(app) && !pid.HasValue)
        {
            throw new ArgumentException("app (or pid) is required");
        }

        string? normText = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        string? normRole = string.IsNullOrWhiteSpace(role) ? null : role.Trim();
        if (normText == null && normRole == null)
        {
            throw new ArgumentException("query requires at least one of 'text' or 'role'");
        }

        if (limit < 1 || limit > 100)
        {
            throw new ArgumentException("limit must be between 1 and 100");
        }

        if (maxNodes < 1 || maxNodes > 5000)
        {
            throw new ArgumentException("max_nodes must be between 1 and 5000");
        }

        if (windowId.HasValue && windowId.Value <= 0)
        {
            throw new ArgumentException("window_id must be a positive integer");
        }

        NativeMethods.EnsurePerMonitorDpiAwareness();

        var process = ResolveApp(app, pid);
        IntPtr hwnd;
        AutomationElement rootElement;

        using (_treeCacheRequest.Activate())
        {
            if (windowId.HasValue)
            {
                hwnd = new IntPtr(windowId.Value);
                if (!NativeMethods.IsWindow(hwnd))
                {
                    throw new ArgumentException($"Window {windowId.Value} was not found for application '{process.ProcessName}'");
                }
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint ownerPid);
                if (ownerPid != (uint)process.Id)
                {
                    throw new ArgumentException($"Window {windowId.Value} was not found for application '{process.ProcessName}'");
                }
                rootElement = _automation.FromHandle(hwnd)
                    ?? throw new InvalidOperationException($"Window {windowId.Value} does not expose a UI Automation root.");
            }
            else
            {
                hwnd = WindowManager.GetTargetHwnd(process);
                if (hwnd == IntPtr.Zero) hwnd = process.MainWindowHandle;
                rootElement = GetMainElement(process, hwnd);
            }

            var windowBounds = GetWindowBounds(process, rootElement, hwnd);
            int? resolvedWindowId = hwnd != IntPtr.Zero ? (int)hwnd.ToInt64() : windowId;

            var matches = new List<QueryMatchRecord>();
            var queue = new Queue<(AutomationElement Element, string Path, int Depth)>();
            var visitedIds = new HashSet<string>(StringComparer.Ordinal);
            queue.Enqueue((rootElement, "0", 0));

            int visitedNodes = 0;
            bool truncated = false;
            string stopReason = "complete";
            var sw = Stopwatch.StartNew();
            var timeout = TimeSpan.FromSeconds(2.0);

            while (queue.Count > 0)
            {
                if (sw.Elapsed >= timeout)
                {
                    truncated = true;
                    stopReason = "timeout";
                    break;
                }

                if (visitedNodes >= maxNodes)
                {
                    truncated = true;
                    stopReason = "max_nodes";
                    break;
                }

                var (current, path, depth) = queue.Dequeue();

                int[]? runtimeId = null;
                string runtimeKey;
                if (TryGetCachedOrLiveProperty(current.Properties.RuntimeId, out var rid) && rid is { Length: > 0 })
                {
                    runtimeId = rid;
                    runtimeKey = string.Join('.', runtimeId);
                }
                else
                {
                    runtimeKey = $"q_{path}_{visitedNodes}";
                }

                if (!visitedIds.Add(runtimeKey))
                {
                    continue;
                }

                visitedNodes++;
                var record = GetElementRecord(current, visitedNodes - 1, runtimeId, windowBounds, DefaultTextLimit);

                if (MatchesQueryCriteria(record, normText, normRole, exact))
                {
                    int assignedIndex = RegisterTargetedElement(process.Id, hwnd, record, current);
                    bool isEnabled = !TryGetCachedOrLiveProperty(current.Properties.IsEnabled, out bool en) || en;
                    bool isFocused = TryGetCachedOrLiveProperty(current.Properties.HasKeyboardFocus, out bool foc) && foc;
                    bool isSelected = false;
                    if (IsPatternSupportedCachedOrLive(current.Patterns.SelectionItem, out var selItem) && selItem != null)
                    {
                        if (TryGetCachedOrLiveProperty(selItem.IsSelected, out bool selVal))
                        {
                            isSelected = selVal;
                        }
                    }

                    string roleName = !string.IsNullOrWhiteSpace(record.ControlType)
                        ? record.ControlType.Replace("ControlType.", "", StringComparison.OrdinalIgnoreCase)
                        : (record.LocalizedControlType ?? "Unknown");

                    matches.Add(new QueryMatchRecord
                    {
                        ElementIndex = assignedIndex,
                        Role = roleName,
                        Title = record.Name,
                        Label = record.AutomationId,
                        Value = record.Value,
                        Enabled = isEnabled,
                        Focused = isFocused,
                        Selected = isSelected,
                        Bounds = record.Frame,
                        Path = path,
                        WindowId = resolvedWindowId,
                        Actions = record.Actions
                    });

                    if (matches.Count >= limit)
                    {
                        bool hasMoreChildren = false;
                        if (depth < DefaultMaxTreeDepth)
                        {
                            try
                            {
                                var ch = current.FindAllChildren();
                                hasMoreChildren = ch is { Length: > 0 };
                            }
                            catch { }
                        }
                        if (queue.Count > 0 || hasMoreChildren)
                        {
                            truncated = true;
                            stopReason = "limit";
                        }
                        break;
                    }
                }

                if (depth < DefaultMaxTreeDepth)
                {
                    try
                    {
                        var children = GetChildElementsWithWin32Fallback(current, depth == 0 ? hwnd : IntPtr.Zero);
                        for (int i = 0; i < children.Length; i++)
                        {
                            queue.Enqueue((children[i], $"{path}.{i}", depth + 1));
                        }
                    }
                    catch { }
                }
            }

            return new QueryResult
            {
                App = process.ProcessName,
                PID = process.Id,
                BundleId = process.ProcessName,
                WindowId = resolvedWindowId,
                Matches = matches,
                Truncated = truncated,
                StopReason = stopReason,
                VisitedNodes = visitedNodes,
                MaxNodes = maxNodes,
                Limit = limit
            };
        }
    }

    private static bool MatchesQueryCriteria(ElementRecord record, string? normText, string? normRole, bool exact)
    {
        if (normRole != null)
        {
            string cleanRoleQuery = normRole;
            if (cleanRoleQuery.StartsWith("ControlType.", StringComparison.OrdinalIgnoreCase))
            {
                cleanRoleQuery = cleanRoleQuery["ControlType.".Length..];
            }
            else if (cleanRoleQuery.StartsWith("AX", StringComparison.OrdinalIgnoreCase) && cleanRoleQuery.Length > 2)
            {
                cleanRoleQuery = cleanRoleQuery[2..];
            }

            string rawControl = record.ControlType ?? string.Empty;
            string shortControl = rawControl.StartsWith("ControlType.", StringComparison.OrdinalIgnoreCase)
                ? rawControl["ControlType.".Length..]
                : rawControl;
            string locControl = record.LocalizedControlType ?? string.Empty;

            bool roleMatch = exact
                ? string.Equals(rawControl, normRole, StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(shortControl, cleanRoleQuery, StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(locControl, normRole, StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(locControl, cleanRoleQuery, StringComparison.OrdinalIgnoreCase)
                : rawControl.Contains(normRole, StringComparison.OrdinalIgnoreCase) ||
                  shortControl.Contains(cleanRoleQuery, StringComparison.OrdinalIgnoreCase) ||
                  locControl.Contains(normRole, StringComparison.OrdinalIgnoreCase) ||
                  locControl.Contains(cleanRoleQuery, StringComparison.OrdinalIgnoreCase);

            if (!roleMatch)
            {
                return false;
            }
        }

        if (normText != null)
        {
            string?[] candidates = [record.Name, record.AutomationId, record.Value, record.ClassName];
            bool textMatch = candidates.Any(c =>
            {
                if (string.IsNullOrWhiteSpace(c)) return false;
                return exact
                    ? string.Equals(c.Trim(), normText, StringComparison.OrdinalIgnoreCase)
                    : c.Contains(normText, StringComparison.OrdinalIgnoreCase);
            });

            if (!textMatch)
            {
                return false;
            }
        }

        return true;
    }

    public AutomationElement? FindElement(Process process, ElementRecord record)
    {
        var root = GetMainElement(process);
        var all = GetAllElements(root);

        if (record.RuntimeId != null && record.RuntimeId.Length > 0)
        {
            foreach (var el in all)
            {
                try
                {
                    var id = el.Properties.RuntimeId.ValueOrDefault;
                    if (id != null && id.SequenceEqual(record.RuntimeId))
                    {
                        return el;
                    }
                }
                catch { }
            }
        }

        foreach (var el in all)
        {
            try
            {
                bool sameAutoId = !string.IsNullOrWhiteSpace(record.AutomationId) &&
                                  string.Equals(el.AutomationId, record.AutomationId, StringComparison.Ordinal);
                bool sameName = !string.IsNullOrWhiteSpace(record.Name) &&
                                string.Equals(el.Name, record.Name, StringComparison.Ordinal);
                bool sameType = string.Equals($"ControlType.{el.ControlType}", record.ControlType, StringComparison.Ordinal);

                if ((sameAutoId || sameName) && sameType)
                {
                    return el;
                }
            }
            catch { }
        }

        return null;
    }

    public bool InvokeTypeText(Process process, string text)
    {
        var (entryElement, targetHwnd) = FindTextEntryElementAndHandle(process);
        if (targetHwnd != IntPtr.Zero && InputInjector.SendTextToEditHandle(targetHwnd, text, entryElement))
        {
            return true;
        }

        if (entryElement != null)
        {
            var valPattern = entryElement.Patterns.Value.PatternOrDefault;
            if (valPattern != null && !valPattern.IsReadOnly.ValueOrDefault)
            {
                if (!TestEnvFlagEnabled("OPEN_COMPUTER_USE_WINDOWS_ALLOW_UIA_TEXT_FALLBACK"))
                {
                    throw new InvalidOperationException(
                        "UIA ValuePattern text fallback is disabled by default because it may bring the target app to the foreground; set OPEN_COMPUTER_USE_WINDOWS_ALLOW_UIA_TEXT_FALLBACK=1 to enable it.");
                }

                string current = valPattern.Value.ValueOrDefault ?? "";
                valPattern.SetValue(current + text);
                return true;
            }
        }

        return false;
    }

    private (AutomationElement? Element, IntPtr Hwnd) FindTextEntryElementAndHandle(Process process)
    {
        AutomationElement? candidate = null;
        bool candidateFromFocused = false;
        try
        {
            var focused = _automation.FocusedElement();
            if (focused != null && focused.Properties.ProcessId.ValueOrDefault == process.Id)
            {
                var val = focused.Patterns.Value.PatternOrDefault;
                if (val != null && !val.IsReadOnly.ValueOrDefault)
                {
                    candidate = focused;
                    candidateFromFocused = true;
                }
                else
                {
                    var focHwnd = focused.Properties.NativeWindowHandle.ValueOrDefault;
                    if (focHwnd != IntPtr.Zero && focHwnd != process.MainWindowHandle)
                    {
                        var focSb = new System.Text.StringBuilder(128);
                        NativeMethods.GetClassName(focHwnd, focSb, focSb.Capacity);
                        if (NativeMethods.IsEditControlClassName(focSb.ToString()))
                        {
                            return (focused, focHwnd);
                        }
                    }
                }
            }
        }
        catch { }

        bool allowUiaFallback = TestEnvFlagEnabled("OPEN_COMPUTER_USE_WINDOWS_ALLOW_UIA_TEXT_FALLBACK");

        // Fast path: Check already cached snapshot records instead of expensive FindAllDescendants()
        if (candidate == null)
        {
            string pidKey = process.Id.ToString();
            string procName = process.ProcessName;
            IntPtr rootForFilter = WindowManager.GetTargetHwnd(process);
            long mainHwndVal = rootForFilter != IntPtr.Zero ? rootForFilter.ToInt64() : process.MainWindowHandle.ToInt64();

            AutomationElement? cachedEl = null;
            lock (_cacheSync)
            {
                Dictionary<int, AutomationElement>? elemMap = null;
                if (_snapshots.TryGetValue(pidKey, out var snap) && snap.App.PID == process.Id)
                {
                    _elementCache.TryGetValue(pidKey, out elemMap);
                }
                else if (_snapshots.TryGetValue(procName, out var nameSnap) && nameSnap.App.PID == process.Id)
                {
                    snap = nameSnap;
                    _elementCache.TryGetValue(procName, out elemMap);
                }
                else
                {
                    snap = null;
                }

                if (snap?.Elements != null && elemMap != null)
                {
                    static double GetRecordArea(ElementRecord r) =>
                        (r.Frame?.Width ?? 0.0) * (r.Frame?.Height ?? 0.0);

                    var editRecord = snap.Elements
                        .Where(r =>
                            (r.NativeWindowHandle.HasValue && r.NativeWindowHandle.Value != 0 && r.NativeWindowHandle.Value != mainHwndVal &&
                             (r.ControlType != null && (r.ControlType.Contains("Edit", StringComparison.OrdinalIgnoreCase) ||
                                                        r.ControlType.Contains("Document", StringComparison.OrdinalIgnoreCase)))) ||
                            (allowUiaFallback && r.Actions != null && r.Actions.Contains("SetValue") &&
                             r.ControlType != null && (r.ControlType.Contains("Edit", StringComparison.OrdinalIgnoreCase) ||
                                                       r.ControlType.Contains("Document", StringComparison.OrdinalIgnoreCase))))
                        .OrderByDescending(GetRecordArea)
                        .FirstOrDefault();

                    editRecord ??= snap.Elements
                        .Where(r =>
                            (r.NativeWindowHandle.HasValue && r.NativeWindowHandle.Value != 0 && r.NativeWindowHandle.Value != mainHwndVal &&
                             r.Actions != null && r.Actions.Contains("SetValue")) ||
                            (allowUiaFallback && r.Actions != null && r.Actions.Contains("SetValue")))
                        .OrderByDescending(GetRecordArea)
                        .FirstOrDefault();

                    if (editRecord != null)
                    {
                        elemMap.TryGetValue(editRecord.Index, out cachedEl);
                    }
                }
            }

            if (cachedEl != null)
            {
                try
                {
                    var val = cachedEl.Patterns.Value.PatternOrDefault;
                    if (val != null && !val.IsReadOnly.ValueOrDefault)
                    {
                        candidate = cachedEl;
                    }
                }
                catch { }
            }
        }

        if (candidateFromFocused && candidate != null)
        {
            try
            {
                var handle = candidate.Properties.NativeWindowHandle.ValueOrDefault;
                if (handle != IntPtr.Zero && handle != process.MainWindowHandle)
                {
                    return (candidate, handle);
                }
            }
            catch { }
        }

        IntPtr rootHwnd = WindowManager.GetTargetHwnd(process);
        if (rootHwnd == IntPtr.Zero) rootHwnd = process.MainWindowHandle;
        if (rootHwnd != IntPtr.Zero)
        {
            IntPtr childHwnd = NativeMethods.FindInputTargetHwnd(rootHwnd);
            if (childHwnd != IntPtr.Zero && childHwnd != rootHwnd)
            {
                var sb = new System.Text.StringBuilder(128);
                NativeMethods.GetClassName(childHwnd, sb, sb.Capacity);
                if (NativeMethods.IsEditControlClassName(sb.ToString()))
                {
                    return (candidate, childHwnd);
                }
            }
        }

        if (candidate != null)
        {
            try
            {
                var handle = candidate.Properties.NativeWindowHandle.ValueOrDefault;
                if (handle != IntPtr.Zero && handle != process.MainWindowHandle)
                {
                    return (candidate, handle);
                }
            }
            catch { }
        }

        return (candidate, IntPtr.Zero);
    }

    private AutomationElement[] GetChildElementsWithWin32Fallback(AutomationElement parent, IntPtr fallbackRootHwnd)
    {
        try
        {
            var children = parent.FindAllChildren();
            if (children is { Length: > 0 })
            {
                return children;
            }
        }
        catch { }

        if (fallbackRootHwnd == IntPtr.Zero || !NativeMethods.IsWindow(fallbackRootHwnd))
        {
            return [];
        }

        var list = new List<AutomationElement>();
        var seenHwnds = new HashSet<IntPtr> { fallbackRootHwnd };

        NativeMethods.EnumChildWindows(fallbackRootHwnd, (childHwnd, _) =>
        {
            if (childHwnd == IntPtr.Zero || !seenHwnds.Add(childHwnd))
            {
                return true;
            }
            if (!NativeMethods.IsWindowVisible(childHwnd))
            {
                return true;
            }
            try
            {
                var el = _automation.FromHandle(childHwnd);
                if (el != null)
                {
                    list.Add(el);
                }
            }
            catch { }
            return true;
        }, IntPtr.Zero);

        return list.ToArray();
    }

    private void RememberSnapshot(string? query, AppSnapshot snapshot, Dictionary<int, AutomationElement> elementCache)
    {
        var keys = new[]
        {
            query,
            snapshot.App.Name,
            snapshot.App.BundleIdentifier,
            snapshot.App.PID.ToString(),
            $"pid:{snapshot.App.PID}"
        };

        lock (_cacheSync)
        {
            foreach (var key in keys)
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    string norm = key.Trim();
                    _snapshots[norm] = snapshot;
                    _elementCache[norm] = elementCache;
                    _pidCache[norm] = snapshot.App.PID;
                }
            }
        }
    }

    private sealed record PrioritizedTreeNode(
        AutomationElement Element,
        int Depth,
        int[] Path,
        bool InCollection,
        ElementRecord DraftRecord);

    private static bool IsInteractiveControlType(string? controlType)
    {
        if (string.IsNullOrWhiteSpace(controlType)) return false;
        return controlType is
            "ControlType.Button" or
            "ControlType.CheckBox" or
            "ControlType.RadioButton" or
            "ControlType.Edit" or
            "ControlType.ComboBox" or
            "ControlType.Slider" or
            "ControlType.Spinner" or
            "ControlType.Hyperlink";
    }

    private static bool IsCollectionControlType(string? controlType)
    {
        if (string.IsNullOrWhiteSpace(controlType)) return false;
        return controlType is
            "ControlType.List" or
            "ControlType.DataGrid" or
            "ControlType.Tree" or
            "ControlType.Table";
    }

    private static int GetTreeQueueRank(AutomationElement element, ElementRecord record, int depth, bool inCollection)
    {
        bool offscreen = TryGetCachedOrLiveProperty(element.Properties.IsOffscreen, out bool isOff) && isOff;

        bool visible = !offscreen && (depth == 0 || record.Frame != null);
        if (!visible)
        {
            return 4;
        }

        bool interactive = IsInteractiveControlType(record.ControlType);
        if (interactive && !inCollection) return 0;
        if (!inCollection) return 1;
        if (interactive) return 2;
        return 3;
    }

    private static int CompareIntPaths(int[] left, int[] right)
    {
        int limit = Math.Min(left.Length, right.Length);
        for (int i = 0; i < limit; i++)
        {
            int cmp = left[i].CompareTo(right[i]);
            if (cmp != 0) return cmp;
        }
        return left.Length.CompareTo(right.Length);
    }

    private (List<ElementRecord> Records, List<string> Lines) RenderTree(
        AutomationElement root,
        Frame? windowBounds,
        int? textLimit,
        int maxTreeNodes,
        int maxTreeDepth,
        Dictionary<int, AutomationElement> elementCache,
        IntPtr rootHwnd = default)
    {
        var queues = new Queue<PrioritizedTreeNode>[5];
        for (int i = 0; i < queues.Length; i++)
        {
            queues[i] = new Queue<PrioritizedTreeNode>();
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var selected = new List<PrioritizedTreeNode>();
        bool truncated = false;
        int anonCounter = 0;

        int[]? rootRuntimeId = TryGetCachedOrLiveProperty(root.Properties.RuntimeId, out var rRid) ? rRid : null;
        var rootRecord = GetElementRecord(root, 0, rootRuntimeId, windowBounds, textLimit);
        queues[0].Enqueue(new PrioritizedTreeNode(
            root,
            0,
            [0],
            IsCollectionControlType(rootRecord.ControlType),
            rootRecord));

        bool HasPending() => queues.Any(q => q.Count > 0);

        while (selected.Count < maxTreeNodes && HasPending())
        {
            PrioritizedTreeNode? item = null;
            for (int r = 0; r < queues.Length; r++)
            {
                if (queues[r].Count > 0)
                {
                    item = queues[r].Dequeue();
                    break;
                }
            }

            if (item == null) break;

            string runtimeIdKey = item.DraftRecord.RuntimeId is { Length: > 0 }
                ? string.Join('.', item.DraftRecord.RuntimeId)
                : $"anon_{anonCounter++}_{item.Depth}_{string.Join('.', item.Path)}";

            if (!visited.Add(runtimeIdKey))
            {
                continue;
            }

            selected.Add(item);

            if (item.Depth >= maxTreeDepth)
            {
                truncated = true;
                continue;
            }

            try
            {
                var children = GetChildElementsWithWin32Fallback(item.Element, item.Depth == 0 ? rootHwnd : IntPtr.Zero);
                for (int childIndex = 0; childIndex < children.Length; childIndex++)
                {
                    var child = children[childIndex];
                    int[]? childRuntimeId = TryGetCachedOrLiveProperty(child.Properties.RuntimeId, out var cRid) ? cRid : null;
                    var childRecord = GetElementRecord(child, 0, childRuntimeId, windowBounds, textLimit);
                    bool childInCollection = item.InCollection || IsCollectionControlType(childRecord.ControlType);
                    int rank = GetTreeQueueRank(child, childRecord, item.Depth + 1, childInCollection);

                    int[] childPath = new int[item.Path.Length + 1];
                    Array.Copy(item.Path, childPath, item.Path.Length);
                    childPath[^1] = childIndex;

                    queues[rank].Enqueue(new PrioritizedTreeNode(
                        child,
                        item.Depth + 1,
                        childPath,
                        childInCollection,
                        childRecord));
                }
            }
            catch
            {
                // Ignore COM errors on detachment
            }
        }

        if (HasPending())
        {
            truncated = true;
        }

        selected.Sort((a, b) => CompareIntPaths(a.Path, b.Path));

        var records = new List<ElementRecord>(selected.Count);
        var lines = new List<string>(selected.Count + 1);

        for (int index = 0; index < selected.Count; index++)
        {
            var node = selected[index];
            var record = node.DraftRecord;
            record.Index = index;
            elementCache[index] = node.Element;
            records.Add(record);

            string role = !string.IsNullOrWhiteSpace(record.LocalizedControlType)
                ? record.LocalizedControlType
                : record.ControlType ?? "";

            string title = GetElementTitle(record);

            string actionsSegment = "";
            if (record.Actions != null && record.Actions.Count > 0)
            {
                actionsSegment = " Secondary Actions: " + string.Join(", ", record.Actions);
            }

            string valueSegment = "";
            if (!string.IsNullOrWhiteSpace(record.Value) && record.Value != title)
            {
                string safeValue = record.Value.Replace("\r", "\\r").Replace("\n", "\\n");
                valueSegment = $" Value: {safeValue}";
            }

            string frameSegment = "";
            if (record.Frame != null)
            {
                frameSegment = $" Frame: {{x: {Math.Round(record.Frame.X)}, y: {Math.Round(record.Frame.Y)}, width: {Math.Round(record.Frame.Width)}, height: {Math.Round(record.Frame.Height)}}}";
            }

            string indent = new('\t', node.Depth + 1);
            lines.Add($"{indent}{index} {role} {title}{valueSegment}{actionsSegment}{frameSegment}");
        }

        if (truncated)
        {
            lines.Add("[Accessibility tree truncated: prioritized visible controls; some nodes were omitted.]");
        }

        return (records, lines);
    }

    private ElementRecord GetElementRecord(AutomationElement element, int index, int[]? runtimeId, Frame? windowBounds, int? textLimit)
    {
        var frame = GetElementFrame(element, windowBounds);

        string? autoId = TryGetCachedOrLiveProperty(element.Properties.AutomationId, out string? rawAutoId) ? rawAutoId : null;
        string? name = TryGetCachedOrLiveProperty(element.Properties.Name, out string? rawName) ? LimitText(rawName, textLimit) : null;

        ControlType? rawType = null;
        string? controlType = null;
        if (TryGetCachedOrLiveProperty(element.Properties.ControlType, out ControlType ct))
        {
            rawType = ct;
            controlType = $"ControlType.{ct}";
        }

        string? localizedType = TryGetCachedOrLiveProperty(element.Properties.LocalizedControlType, out string? rawLoc) ? rawLoc : null;
        string? className = TryGetCachedOrLiveProperty(element.Properties.ClassName, out string? rawCls) ? rawCls : null;

        long? nativeWindowHandle = null;
        if (TryGetCachedOrLiveProperty(element.Properties.NativeWindowHandle, out IntPtr h) && h != IntPtr.Zero)
        {
            nativeWindowHandle = h.ToInt64();
        }

        var (value, actions) = GetElementValueAndPatterns(element, rawType, textLimit);

        return new ElementRecord
        {
            Index = index,
            RuntimeId = runtimeId,
            AutomationId = autoId,
            Name = name,
            ControlType = controlType,
            LocalizedControlType = localizedType,
            ClassName = className,
            Value = value,
            NativeWindowHandle = nativeWindowHandle,
            Frame = frame,
            Actions = actions
        };
    }

    private Frame? GetElementFrame(AutomationElement element, Frame? windowBounds)
    {
        if (!TryGetCachedOrLiveProperty(element.Properties.BoundingRectangle, out var rect))
        {
            return null;
        }

        if (rect.Width <= 0 || rect.Height <= 0) return null;

        if (windowBounds != null)
        {
            return new Frame(rect.X - windowBounds.X, rect.Y - windowBounds.Y, rect.Width, rect.Height);
        }
        return new Frame(rect.X, rect.Y, rect.Width, rect.Height);
    }

    private (string? Value, List<string> Actions) GetElementValueAndPatterns(AutomationElement element, ControlType? rawType, int? textLimit)
    {
        string? value = null;
        var names = new List<string>(4);

        // Static decorative types rarely support interactive patterns; skip unnecessary checks
        bool isStaticDecorative = rawType is ControlType.Separator or ControlType.Image or ControlType.ToolTip or ControlType.Thumb;

        try
        {
            if (!isStaticDecorative)
            {
                if (IsPatternSupportedCachedOrLive(element.Patterns.Invoke, out _))
                {
                    names.Add("Invoke");
                }

                bool canHaveValue = rawType is not (ControlType.Pane or ControlType.TitleBar or ControlType.MenuBar or ControlType.ToolBar);
                if (canHaveValue && IsPatternSupportedCachedOrLive(element.Patterns.Value, out var valPattern) && valPattern != null)
                {
                    names.Add("SetValue");
                    if (TryGetCachedOrLiveProperty(valPattern.Value, out string? rawVal))
                    {
                        value = LimitText(rawVal, textLimit);
                    }
                }

                bool canToggleOrSelect = rawType is not (ControlType.Text or ControlType.Pane or ControlType.Document or ControlType.Edit or ControlType.Window or ControlType.TitleBar or ControlType.ToolBar or ControlType.StatusBar);
                if (canToggleOrSelect)
                {
                    if (IsPatternSupportedCachedOrLive(element.Patterns.Toggle, out _)) names.Add("Toggle");
                    if (IsPatternSupportedCachedOrLive(element.Patterns.SelectionItem, out _)) names.Add("Select");
                    if (IsPatternSupportedCachedOrLive(element.Patterns.ExpandCollapse, out var ec) && ec != null)
                    {
                        if (TryGetCachedOrLiveProperty(ec.ExpandCollapseState, out var state))
                        {
                            if (state == ExpandCollapseState.Collapsed) names.Add("Expand");
                            else if (state == ExpandCollapseState.Expanded) names.Add("Collapse");
                            else
                            {
                                names.Add("Expand");
                                names.Add("Collapse");
                            }
                        }
                        else
                        {
                            names.Add("Expand");
                            names.Add("Collapse");
                        }
                    }
                }

                if (rawType is ControlType.Pane or ControlType.Document or ControlType.List or ControlType.DataGrid or ControlType.Tree or ControlType.Window or ControlType.Group or ControlType.Custom)
                {
                    if (IsPatternSupportedCachedOrLive(element.Patterns.Scroll, out _)) names.Add("Scroll");
                }
                else if (rawType is ControlType.ListItem or ControlType.DataItem or ControlType.TreeItem or ControlType.TabItem)
                {
                    if (IsPatternSupportedCachedOrLive(element.Patterns.ScrollItem, out _)) names.Add("ScrollIntoView");
                }
            }
        }
        catch { }

        return (value, names);
    }

    private string GetElementTitle(ElementRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.Name)) return record.Name;
        if (!string.IsNullOrWhiteSpace(record.AutomationId)) return $"ID: {record.AutomationId}";
        return "";
    }

    private string? GetFocusedSummary(AutomationElement? focused, int? textLimit)
    {
        if (focused == null) return null;
        try
        {
            string role = focused.Properties.LocalizedControlType.ValueOrDefault ?? focused.ControlType.ToString();
            string? name = LimitText(focused.Properties.Name.ValueOrDefault, textLimit);
            if (string.IsNullOrWhiteSpace(name))
            {
                return role;
            }
            return $"{role} {name}";
        }
        catch { }
        return null;
    }

    private string? GetSelectedText(AutomationElement? focused, int? textLimit)
    {
        if (focused == null) return null;
        try
        {
            if (!focused.Patterns.Text.IsSupported) return null;
            var textPattern = focused.Patterns.Text.PatternOrDefault;
            if (textPattern != null)
            {
                var selection = textPattern.GetSelection();
                if (selection != null && selection.Length > 0)
                {
                    int maxLength = textLimit.HasValue ? textLimit.Value + 1 : -1;
                    return LimitText(selection[0].GetText(maxLength), textLimit);
                }
            }
        }
        catch { }
        return null;
    }

    private List<AutomationElement> GetAllElements(AutomationElement root)
    {
        var items = new List<AutomationElement> { root };
        try
        {
            var descendants = root.FindAllDescendants();
            if (descendants != null)
            {
                items.AddRange(descendants);
            }
        }
        catch { }
        return items;
    }

    private static string? LimitText(string? text, int? limit)
    {
        if (text == null) return null;
        if (!limit.HasValue) return text;
        if (text.Length > limit.Value)
        {
            return text[..limit.Value] + "...";
        }
        return text;
    }

    private static bool TestEnvFlagEnabled(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "1" or "true" or "yes" or "on";
    }
}
