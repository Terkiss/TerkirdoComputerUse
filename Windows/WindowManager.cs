using System.Collections.Concurrent;
using System.Diagnostics;
using TerkirdoComputer.Core;

namespace TerkirdoComputer.Windows;

public static class WindowManager
{
    private static readonly ConcurrentDictionary<int, (IntPtr Hwnd, long TimestampTicks)> TargetHwndCache = new();
    private static readonly ConcurrentDictionary<int, IntPtr> LastPrimaryHwndCache = new();
    private static readonly long TargetHwndCacheTtlTicks = (long)(0.150 * Stopwatch.Frequency);

    public static bool IsSystemHelperWindowClass(string cls)
    {
        if (string.IsNullOrEmpty(cls))
        {
            return false;
        }

        return cls.Contains("Overlay", StringComparison.OrdinalIgnoreCase) ||
               cls.Contains("IME", StringComparison.OrdinalIgnoreCase) ||
               cls.Contains("UAC Input Indicator", StringComparison.OrdinalIgnoreCase) ||
               cls.Contains("UAC_InputIndicator", StringComparison.OrdinalIgnoreCase) ||
               cls.Contains("CiceroUIWndFrame", StringComparison.OrdinalIgnoreCase) ||
               cls.Contains("CicLoaderWndClass", StringComparison.OrdinalIgnoreCase) ||
               cls.Contains("GDI+ Hook Window", StringComparison.OrdinalIgnoreCase) ||
               cls.Contains("Touch Tooltip", StringComparison.OrdinalIgnoreCase) ||
               cls.Contains("PopupWindowSiteBridge", StringComparison.OrdinalIgnoreCase) ||
               cls.Contains("Xaml_WindowedPopupClass", StringComparison.OrdinalIgnoreCase) ||
               cls.Contains("DesktopChildSiteBridge", StringComparison.OrdinalIgnoreCase);
    }
    public static Frame? GetClientScreenBounds(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd) || NativeMethods.IsIconic(hwnd))
        {
            return null;
        }

        if (!NativeMethods.GetClientRect(hwnd, out var rc))
        {
            return null;
        }

        int width = rc.Right - rc.Left;
        int height = rc.Bottom - rc.Top;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var origin = new NativeMethods.POINT { X = rc.Left, Y = rc.Top };
        if (!NativeMethods.ClientToScreen(hwnd, ref origin))
        {
            return null;
        }

        return new Frame(origin.X, origin.Y, width, height);
    }

    public static string NormalizeCoordinateSpace(
        string? coordinateSpace,
        bool hasTargetApp,
        bool snapshotClientOnly = false)
    {
        if (string.IsNullOrWhiteSpace(coordinateSpace))
        {
            if (!hasTargetApp)
            {
                return "screen";
            }

            return snapshotClientOnly ? "client" : "window";
        }

        string norm = coordinateSpace.Trim().ToLowerInvariant();
        return norm switch
        {
            "window" or "client" or "screen" => norm,
            _ => throw new ArgumentException(
                $"Invalid coordinate_space '{coordinateSpace}'. Valid options are: window, client, screen.")
        };
    }

    public static (double OriginX, double OriginY) ResolveCoordinateOrigin(
        string normalizedCoordinateSpace,
        IntPtr hwnd,
        Frame? windowBounds)
    {
        switch (normalizedCoordinateSpace)
        {
            case "screen":
                return (0.0, 0.0);

            case "client":
            {
                if (hwnd == IntPtr.Zero)
                {
                    throw new ArgumentException("coordinate_space 'client' requires a target application ('app' or 'pid').");
                }

                var clientBounds = GetClientScreenBounds(hwnd);
                if (clientBounds == null)
                {
                    throw new InvalidOperationException(
                        "Target window client area is unavailable (window may be minimized or closed). Restore the window before using coordinate_space 'client'.");
                }

                return (clientBounds.X, clientBounds.Y);
            }

            case "window":
            {
                if (windowBounds != null)
                {
                    return (windowBounds.X, windowBounds.Y);
                }

                if (hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(hwnd, out var rect))
                {
                    return (rect.Left, rect.Top);
                }

                if (hwnd == IntPtr.Zero)
                {
                    throw new ArgumentException("coordinate_space 'window' requires a target application ('app' or 'pid').");
                }

                return (0.0, 0.0);
            }

            default:
                throw new ArgumentException(
                    $"Invalid coordinate_space '{normalizedCoordinateSpace}'. Valid options are: window, client, screen.");
        }
    }

    public static bool TryParseStrictPidPrefix(string? query, out int strictPid)
    {
        strictPid = 0;
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        string trimmed = query.Trim();
        if (!trimmed.StartsWith("pid:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string suffix = trimmed[4..].Trim();
        if (!int.TryParse(suffix, out strictPid) || strictPid <= 0)
        {
            throw new ArgumentException($"Invalid pid format '{query}': pid:<id> requires a positive integer.");
        }

        return true;
    }

    public static WindowManagementResult ManageWindow(
        string? app,
        int? pid,
        string action,
        double? x = null,
        double? y = null,
        double? width = null,
        double? height = null)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            throw new ArgumentException("Action is required for manage_window.");
        }

        string normAction = action.Trim().ToLowerInvariant();
        var validActions = new[] { "focus", "minimize", "maximize", "restore", "move", "resize" };
        if (!validActions.Contains(normAction))
        {
            throw new ArgumentException(
                $"Unsupported window action '{action}'. Valid actions are: focus, minimize, maximize, restore, move, resize.");
        }

        if (normAction == "move")
        {
            if (!x.HasValue || !y.HasValue)
            {
                throw new ArgumentException("Action 'move' requires both 'x' and 'y' coordinates.");
            }
            if (!double.IsFinite(x.Value) || !double.IsFinite(y.Value))
            {
                throw new ArgumentException("Action 'move' coordinates 'x' and 'y' must be finite numbers.");
            }
        }
        else if (normAction == "resize")
        {
            if (!width.HasValue || !height.HasValue)
            {
                throw new ArgumentException("Action 'resize' requires both 'width' and 'height'.");
            }
            if (!double.IsFinite(width.Value) || !double.IsFinite(height.Value))
            {
                throw new ArgumentException("Action 'resize' dimensions 'width' and 'height' must be finite numbers.");
            }
            if (x.HasValue && !double.IsFinite(x.Value))
            {
                throw new ArgumentException("Action 'resize' coordinate 'x' must be a finite number.");
            }
            if (y.HasValue && !double.IsFinite(y.Value))
            {
                throw new ArgumentException("Action 'resize' coordinate 'y' must be a finite number.");
            }
        }

        if (pid.HasValue && pid.Value <= 0)
        {
            throw new ArgumentException("Invalid pid: must be a positive integer.");
        }

        if (string.IsNullOrWhiteSpace(app) && !pid.HasValue)
        {
            throw new ArgumentException("Either 'app' or 'pid' must be specified for manage_window.");
        }

        int? effectiveStrictPid = pid.HasValue
            ? pid.Value
            : (TryParseStrictPidPrefix(app, out int prefixPid) ? prefixPid : null);

        Process? proc = null;
        if (effectiveStrictPid.HasValue)
        {
            try
            {
                var p = Process.GetProcessById(effectiveStrictPid.Value);
                if (!p.HasExited)
                {
                    proc = p;
                }
            }
            catch { }

            if (proc == null)
            {
                throw new ArgumentException($"appNotFound(\"pid:{effectiveStrictPid.Value}\")");
            }
        }

        // Never fall back to sibling same-name processes when strict PID (pid parameter or "pid:<id>") was supplied
        if (!effectiveStrictPid.HasValue && proc != null && GetTargetHwnd(proc) == IntPtr.Zero)
        {
            try
            {
                var byName = Process.GetProcessesByName(proc.ProcessName);
                var withWindow = byName.FirstOrDefault(p => GetTargetHwnd(p) != IntPtr.Zero);
                if (withWindow != null)
                {
                    proc = withWindow;
                }
            }
            catch { }
        }

        if (!effectiveStrictPid.HasValue && (proc == null || GetTargetHwnd(proc) == IntPtr.Zero) && !string.IsNullOrWhiteSpace(app))
        {
            string query = app.Trim();
            string procQuery = query.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? query[..^4] : query;

            // 1. Fast path: Try exact process name match first (avoids scanning all OS processes)
            try
            {
                var byName = Process.GetProcessesByName(procQuery);
                if (byName.Length > 0)
                {
                    proc = byName.FirstOrDefault(p => GetTargetHwnd(p) != IntPtr.Zero);
                }
            }
            catch { }

            // 2. Fallback: Search all top-level window processes by process name or window title
            if (proc == null)
            {
                var processesWithWindow = Process.GetProcesses()
                    .Where(p => GetTargetHwnd(p) != IntPtr.Zero)
                    .ToList();

                var match = processesWithWindow.FirstOrDefault(p =>
                {
                    if (string.Equals(p.ProcessName, procQuery, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals($"{p.ProcessName}.exe", query, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(p.MainWindowTitle, query, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrEmpty(p.MainWindowTitle) && p.MainWindowTitle.Contains(query, StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }

                    IntPtr targetHwnd = GetTargetHwnd(p);
                    if (targetHwnd != IntPtr.Zero)
                    {
                        var sb = new System.Text.StringBuilder(256);
                        NativeMethods.GetWindowText(targetHwnd, sb, 256);
                        string targetTitle = sb.ToString();
                        if (!string.IsNullOrEmpty(targetTitle) &&
                            (string.Equals(targetTitle, query, StringComparison.OrdinalIgnoreCase) ||
                             targetTitle.Contains(query, StringComparison.OrdinalIgnoreCase)))
                        {
                            return true;
                        }
                    }
                    return false;
                });

                if (match != null)
                {
                    proc = match;
                }
            }
        }

        if (proc == null)
        {
            throw new ArgumentException($"appNotFound(\"{app ?? pid?.ToString()}\")");
        }

        IntPtr hwnd = GetTargetHwnd(proc);
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            throw new InvalidOperationException($"Target process '{proc.ProcessName}' (pid {proc.Id}) does not have an active window handle.");
        }

        // Security Guard: Prevent HWND reuse race condition (ABA Problem)
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint windowOwnerPid);
        if (windowOwnerPid != (uint)proc.Id)
        {
            throw new InvalidOperationException(
                $"Security verification failed: HWND {hwnd} is owned by PID {windowOwnerPid}, but expected PID {proc.Id}.");
        }

        switch (normAction)
        {
            case "focus":
                BringWindowToForeground(hwnd);
                break;

            case "minimize":
                InvalidateTargetHwndCache(proc.Id);
                NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_MINIMIZE);
                for (int i = 0; i < 25 && !NativeMethods.IsIconic(hwnd); i++)
                {
                    PrecisionTimer.WaitMs(10);
                }
                break;

            case "maximize":
                InvalidateTargetHwndCache(proc.Id);
                NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_MAXIMIZE);
                for (int i = 0; i < 25 && !NativeMethods.IsZoomed(hwnd); i++)
                {
                    PrecisionTimer.WaitMs(10);
                }
                break;

            case "restore":
                InvalidateTargetHwndCache(proc.Id);
                NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_RESTORE);
                for (int i = 0; i < 25 && (NativeMethods.IsIconic(hwnd) || NativeMethods.IsZoomed(hwnd)); i++)
                {
                    PrecisionTimer.WaitMs(10);
                }
                break;

            case "move":
                if (NativeMethods.IsZoomed(hwnd) || NativeMethods.IsIconic(hwnd))
                {
                    InvalidateTargetHwndCache(proc.Id);
                    NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                    for (int i = 0; i < 20 && (NativeMethods.IsIconic(hwnd) || NativeMethods.IsZoomed(hwnd)); i++)
                    {
                        PrecisionTimer.WaitMs(10);
                    }
                    PrecisionTimer.WaitMs(35);
                }

                if (NativeMethods.GetWindowRect(hwnd, out var curRectForMove))
                {
                    int curW = Math.Clamp(curRectForMove.Right - curRectForMove.Left, 100, 16000);
                    int curH = Math.Clamp(curRectForMove.Bottom - curRectForMove.Top, 100, 16000);
                    // Clamp within [-16000, 16000] so (X + Width) never overflows signed 16-bit WM_MOVE (32767)
                    // and Y never enters Windows' <= -32000 iconic sentinel coordinate region.
                    int targetX = Math.Clamp((int)Math.Round(x!.Value), -16000, 16000);
                    int targetY = Math.Clamp((int)Math.Round(y!.Value), -16000, 16000);
                    NativeMethods.SetWindowPos(
                        hwnd,
                        IntPtr.Zero,
                        targetX,
                        targetY,
                        curW,
                        curH,
                        NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
                }
                break;

            case "resize":
                if (NativeMethods.IsZoomed(hwnd) || NativeMethods.IsIconic(hwnd))
                {
                    InvalidateTargetHwndCache(proc.Id);
                    NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                    for (int i = 0; i < 20 && (NativeMethods.IsIconic(hwnd) || NativeMethods.IsZoomed(hwnd)); i++)
                    {
                        PrecisionTimer.WaitMs(10);
                    }
                    PrecisionTimer.WaitMs(35);
                }

                if (NativeMethods.GetWindowRect(hwnd, out var curRectForResize))
                {
                    int targetX = x.HasValue
                        ? Math.Clamp((int)Math.Round(x.Value), -16000, 16000)
                        : Math.Clamp(curRectForResize.Left, -16000, 16000);
                    int targetY = y.HasValue
                        ? Math.Clamp((int)Math.Round(y.Value), -16000, 16000)
                        : Math.Clamp(curRectForResize.Top, -16000, 16000);
                    int targetW = Math.Clamp((int)Math.Round(width!.Value), 100, 16000);
                    int targetH = Math.Clamp((int)Math.Round(height!.Value), 100, 16000);
                    uint swpFlags = NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED;
                    if (!x.HasValue && !y.HasValue &&
                        curRectForResize.Left == targetX && curRectForResize.Top == targetY)
                    {
                        swpFlags |= NativeMethods.SWP_NOMOVE;
                    }
                    NativeMethods.SetWindowPos(
                        hwnd,
                        IntPtr.Zero,
                        targetX,
                        targetY,
                        targetW,
                        targetH,
                        swpFlags);
                }
                break;
        }

        PrecisionTimer.WaitMs(15);

        if (normAction == "resize" && width.HasValue && height.HasValue)
        {
            int targetW = Math.Clamp((int)Math.Round(width.Value), 100, 16000);
            int targetH = Math.Clamp((int)Math.Round(height.Value), 100, 16000);
            int minExpectedW = Math.Min(targetW, 100);
            int minExpectedH = Math.Min(targetH, 100);

            for (int i = 0; i < 15; i++)
            {
                if (NativeMethods.GetWindowRect(hwnd, out var chkRect) &&
                    (chkRect.Right - chkRect.Left) >= minExpectedW &&
                    (chkRect.Bottom - chkRect.Top) >= minExpectedH &&
                    !NativeMethods.IsIconic(hwnd))
                {
                    break;
                }

                if (NativeMethods.IsIconic(hwnd) || NativeMethods.IsZoomed(hwnd))
                {
                    InvalidateTargetHwndCache(proc.Id);
                    NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                    PrecisionTimer.WaitMs(15);
                    NativeMethods.GetWindowRect(hwnd, out chkRect);
                }

                int retryX = x.HasValue
                    ? Math.Clamp((int)Math.Round(x.Value), -16000, 16000)
                    : Math.Clamp(chkRect.Left, -16000, 16000);
                int retryY = y.HasValue
                    ? Math.Clamp((int)Math.Round(y.Value), -16000, 16000)
                    : Math.Clamp(chkRect.Top, -16000, 16000);
                NativeMethods.SetWindowPos(
                    hwnd,
                    IntPtr.Zero,
                    retryX,
                    retryY,
                    targetW,
                    targetH,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
                PrecisionTimer.WaitMs(12);
            }
        }

        Frame bounds;
        if (NativeMethods.GetWindowRect(hwnd, out var finalRect))
        {
            bounds = new Frame(
                finalRect.Left,
                finalRect.Top,
                finalRect.Right - finalRect.Left,
                finalRect.Bottom - finalRect.Top);
        }
        else
        {
            bounds = new Frame(x ?? 0, y ?? 0, width ?? 0, height ?? 0);
        }

        return new WindowManagementResult
        {
            App = proc.ProcessName,
            PID = proc.Id,
            Action = normAction,
            Bounds = bounds
        };
    }

    public static void BringWindowToForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return;
        }

        bool isIconic = NativeMethods.IsIconic(hwnd);
        bool isVisible = NativeMethods.IsWindowVisible(hwnd);
        if (!isIconic && isVisible && NativeMethods.GetForegroundWindow() == hwnd)
        {
            return;
        }

        bool targetResponsive = false;
        for (int waitAttempt = 0; waitAttempt < 6; waitAttempt++)
        {
            if (NativeMethods.IsWindowResponsive(hwnd, 25))
            {
                targetResponsive = true;
                break;
            }
        }

        if (!targetResponsive)
        {
            if (isIconic)
            {
                NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_RESTORE);
            }
            else if (!isVisible)
            {
                NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_SHOW);
            }
            return;
        }

        IntPtr fgWnd = NativeMethods.GetForegroundWindow();
        uint curThreadId = NativeMethods.GetCurrentThreadId();
        uint fgThreadId = (fgWnd != IntPtr.Zero && fgWnd != hwnd && NativeMethods.IsWindowResponsive(fgWnd, 25))
            ? NativeMethods.GetWindowThreadProcessId(fgWnd, out _)
            : 0;
        uint targetThreadId = NativeMethods.GetWindowThreadProcessId(hwnd, out _);

        bool attachedToFg = false;
        bool attachedToTarget = false;

        try
        {
            if (fgThreadId != 0 && fgThreadId != curThreadId)
            {
                attachedToFg = NativeMethods.AttachThreadInput(curThreadId, fgThreadId, true);
            }

            if (targetThreadId != 0 && targetThreadId != curThreadId && targetThreadId != fgThreadId)
            {
                attachedToTarget = NativeMethods.AttachThreadInput(curThreadId, targetThreadId, true);
            }

            if (isIconic)
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            }
            else if (!isVisible)
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);
            }

            NativeMethods.SetForegroundWindow(hwnd);
            NativeMethods.BringWindowToTop(hwnd);

            if (NativeMethods.GetForegroundWindow() != hwnd)
            {
                // Satisfy Windows ForegroundLockTimeout check via a side-effect-free unassigned key-up (0xFF)
                var unlockInput = new NativeMethods.INPUT
                {
                    type = NativeMethods.INPUT_KEYBOARD,
                    u = new NativeMethods.InputUnion
                    {
                        ki = new NativeMethods.KEYBDINPUT
                        {
                            wVk = 0xFF,
                            wScan = 0,
                            dwFlags = NativeMethods.KEYEVENTF_KEYUP,
                            time = 0,
                            dwExtraInfo = UIntPtr.Zero
                        }
                    }
                };
                NativeMethods.SendInput(1, [unlockInput], System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>());
                NativeMethods.SetForegroundWindow(hwnd);
                NativeMethods.BringWindowToTop(hwnd);

                if (NativeMethods.GetForegroundWindow() != hwnd)
                {
                    NativeMethods.SwitchToThisWindow(hwnd, true);
                }

                for (int i = 0; i < 8 && NativeMethods.GetForegroundWindow() != hwnd; i++)
                {
                    PrecisionTimer.WaitMs(5.0);
                }
            }
        }
        finally
        {
            if (attachedToFg)
            {
                NativeMethods.AttachThreadInput(curThreadId, fgThreadId, false);
            }
            if (attachedToTarget)
            {
                NativeMethods.AttachThreadInput(curThreadId, targetThreadId, false);
            }
        }
    }

    private sealed class WindowCandidate
    {
        public IntPtr Hwnd { get; set; }
        public string ClassName { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public bool HasTitle => !string.IsNullOrWhiteSpace(Title);
        public bool IsVisible { get; set; }
        public bool IsIconic { get; set; }
        public int Area { get; set; }
    }

    public static IntPtr GetTargetHwnd(Process proc)
    {
        int targetPid = proc.Id;
        long nowTicks = Stopwatch.GetTimestamp();

        if (TargetHwndCache.TryGetValue(targetPid, out var cached) &&
            (nowTicks - cached.TimestampTicks) <= TargetHwndCacheTtlTicks &&
            cached.Hwnd != IntPtr.Zero &&
            NativeMethods.IsWindow(cached.Hwnd))
        {
            NativeMethods.GetWindowThreadProcessId(cached.Hwnd, out uint ownerPid);
            if (ownerPid == (uint)targetPid &&
                (NativeMethods.IsWindowVisible(cached.Hwnd) || NativeMethods.IsIconic(cached.Hwnd)))
            {
                return cached.Hwnd;
            }
        }

        var candidates = new List<WindowCandidate>();

        NativeMethods.EnumWindows((hWnd, lParam) =>
        {
            try
            {
                NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid != (uint)proc.Id)
                {
                    return true;
                }

                var sbCls = new System.Text.StringBuilder(256);
                NativeMethods.GetClassName(hWnd, sbCls, 256);
                string cls = sbCls.ToString();

                // Skip IME/UAC/Cicero/GDI+/XAML-Popup system helper windows
                if (IsSystemHelperWindowClass(cls))
                {
                    return true;
                }

                // Filter out tool windows
                try
                {
                    long exStyle = NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_EXSTYLE).ToInt64();
                    if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0)
                    {
                        return true;
                    }
                }
                catch { }

                bool isVisible = NativeMethods.IsWindowVisible(hWnd);
                bool isIconic = NativeMethods.IsIconic(hWnd);

                // Ignore hidden, non-iconic background helper windows (minimized windows have isIconic == true and are preserved)
                if (!isVisible && !isIconic)
                {
                    return true;
                }

                var sbTitle = new System.Text.StringBuilder(512);
                NativeMethods.GetWindowText(hWnd, sbTitle, 512);
                string title = sbTitle.ToString().Trim();

                int area = 0;
                int w = 0;
                int h = 0;
                if (NativeMethods.GetWindowRect(hWnd, out var r))
                {
                    w = Math.Max(0, r.Right - r.Left);
                    h = Math.Max(0, r.Bottom - r.Top);
                    area = w * h;
                }

                // Skip degenerate 0px/1px non-iconic startup/helper windows so callers never lock onto uninitialized frames
                if (!isIconic && (w <= 1 || h <= 1))
                {
                    return true;
                }

                candidates.Add(new WindowCandidate
                {
                    Hwnd = hWnd,
                    ClassName = cls,
                    Title = title,
                    IsVisible = isVisible,
                    IsIconic = isIconic,
                    Area = area
                });
            }
            catch
            {
            }
            return true;
        }, IntPtr.Zero);

        if (candidates.Count == 0)
        {
            try
            {
                proc.Refresh();
                return proc.MainWindowHandle;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        IntPtr stickyHwnd = IntPtr.Zero;
        if (LastPrimaryHwndCache.TryGetValue(targetPid, out IntPtr prevPrimary))
        {
            if (prevPrimary != IntPtr.Zero && NativeMethods.IsWindow(prevPrimary))
            {
                NativeMethods.GetWindowThreadProcessId(prevPrimary, out uint spid);
                if (spid == (uint)targetPid &&
                    (NativeMethods.IsWindowVisible(prevPrimary) || NativeMethods.IsIconic(prevPrimary)))
                {
                    stickyHwnd = prevPrimary;
                }
                else
                {
                    LastPrimaryHwndCache.TryRemove(targetPid, out _);
                }
            }
            else
            {
                LastPrimaryHwndCache.TryRemove(targetPid, out _);
            }
        }

        IntPtr mainHwnd = IntPtr.Zero;
        try { proc.Refresh(); mainHwnd = proc.MainWindowHandle; } catch { }

        // Filter out empty window titles (GetWindowText) when a main application window is present
        bool hasMainAppWindow = candidates.Any(c =>
            c.HasTitle ||
            (stickyHwnd != IntPtr.Zero && c.Hwnd == stickyHwnd) ||
            (mainHwnd != IntPtr.Zero && c.Hwnd == mainHwnd));

        var pool = hasMainAppWindow
            ? candidates.Where(c =>
                c.HasTitle ||
                (stickyHwnd != IntPtr.Zero && c.Hwnd == stickyHwnd) ||
                (mainHwnd != IntPtr.Zero && c.Hwnd == mainHwnd)).ToList()
            : candidates;

        if (pool.Count == 0)
        {
            pool = candidates;
        }

        // Selection priority:
        // 0. Sticky primary HWND affinity (prevents multi-window processes like Win11 Notepad from jumping to Window #2 on minimize)
        // 1. Matches proc.MainWindowHandle (if valid)
        // 2. Visible iconic (minimized) main window prioritized over child/tool overlay windows (e.g. 50x50 UAC Input Indicator)
        // 3. Visible windows with largest area
        // 4. Non-visible iconic window
        // 5. Non-visible window with largest area
        var best = pool
            .OrderByDescending(c =>
            {
                long score = 0;
                if (stickyHwnd != IntPtr.Zero && c.Hwnd == stickyHwnd && (c.IsVisible || c.IsIconic)) score += 200_000_000;
                if (mainHwnd != IntPtr.Zero && c.Hwnd == mainHwnd && (c.IsVisible || c.IsIconic)) score += 100_000_000;
                if (c.HasTitle) score += 10_000_000;
                if (c.IsVisible) score += 5_000_000;

                if (c.IsIconic)
                {
                    // Minimized main window prioritized over child/tool overlay windows (e.g. 50x50 UAC Input Indicator)
                    score += 2_000_000;
                }
                else
                {
                    // Filter out / penalize tiny helper windows (e.g. 50x50 UAC Input Indicator = 2500 area)
                    if (c.Area < 10000)
                    {
                        score -= 1_000_000;
                    }
                    score += Math.Min(c.Area, 1_000_000);
                }

                return score;
            })
            .FirstOrDefault();

        if (best != null && best.Hwnd != IntPtr.Zero)
        {
            if (best.IsIconic || (best.IsVisible && best.HasTitle && best.Area >= 10000))
            {
                RememberTargetHwnd(targetPid, best.Hwnd);
            }
            return best.Hwnd;
        }

        try
        {
            proc.Refresh();
            IntPtr fallback = proc.MainWindowHandle;
            if (fallback != IntPtr.Zero)
            {
                RememberTargetHwnd(targetPid, fallback);
            }
            return fallback;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    public static void InvalidateTargetHwndCache(int pid)
    {
        TargetHwndCache.TryRemove(pid, out _);
    }

    public static void ClearProcessHwndAffinity(int pid)
    {
        TargetHwndCache.TryRemove(pid, out _);
        LastPrimaryHwndCache.TryRemove(pid, out _);
    }

    private static void RememberTargetHwnd(int pid, IntPtr hwnd)
    {
        if (TargetHwndCache.Count > 256)
        {
            TargetHwndCache.Clear();
        }
        if (LastPrimaryHwndCache.Count > 512)
        {
            LastPrimaryHwndCache.Clear();
        }
        TargetHwndCache[pid] = (hwnd, Stopwatch.GetTimestamp());
        LastPrimaryHwndCache[pid] = hwnd;
    }
}
