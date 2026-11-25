using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using TerkirdoComputer.Core;

[assembly: InternalsVisibleTo("TerkirdoComputer.Tests")]

namespace TerkirdoComputer.Windows;

public static class InputInjector
{
    private static bool TestEnvFlagEnabled(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "1" or "true" or "yes" or "on";
    }

    public static NativeMethods.POINT? GetScreenPoint(Frame? localFrame, Frame? windowBounds)
    {
        if (localFrame == null || windowBounds == null) return null;
        return new NativeMethods.POINT
        {
            X = (int)Math.Round(windowBounds.X + localFrame.X + (localFrame.Width / 2.0)),
            Y = (int)Math.Round(windowBounds.Y + localFrame.Y + (localFrame.Height / 2.0))
        };
    }

    internal static (int NormX, int NormY, uint MoveFlags) NormalizeScreenPointForSendInput(int screenX, int screenY)
    {
        int screenWidth = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        int screenHeight = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
        if (screenWidth <= 0) screenWidth = 1920;
        if (screenHeight <= 0) screenHeight = 1080;

        int vX = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        int vY = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        int vW = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        int vH = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

        uint moveFlags = NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE;
        int normX;
        int normY;
        if (vW > 0 && vH > 0)
        {
            moveFlags |= NativeMethods.MOUSEEVENTF_VIRTUALDESK;
            normX = (int)Math.Round((screenX - vX) * 65535.0 / Math.Max(1, vW - 1));
            normY = (int)Math.Round((screenY - vY) * 65535.0 / Math.Max(1, vH - 1));
        }
        else
        {
            normX = (int)Math.Round(screenX * 65535.0 / Math.Max(1, screenWidth - 1));
            normY = (int)Math.Round(screenY * 65535.0 / Math.Max(1, screenHeight - 1));
        }

        normX = Math.Clamp(normX, 0, 65535);
        normY = Math.Clamp(normY, 0, 65535);
        return (normX, normY, moveFlags);
    }

    /// <summary>
    /// Splits relative mouse motion (deltaX, deltaY) into exact error-accumulating integer steps
    /// so sum(stepDx) == round(deltaX) and sum(stepDy) == round(deltaY) with zero rounding drift.
    /// </summary>
    public static (int Dx, int Dy)[] ComputeRelativeSteps(double deltaX, double deltaY, int steps)
    {
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY))
        {
            throw new ArgumentException("delta_x and delta_y must be finite numbers.");
        }

        if (steps < 1 || steps > 240)
        {
            throw new ArgumentException("steps must be between 1 and 240.");
        }

        int totalDx = (int)Math.Round(deltaX);
        int totalDy = (int)Math.Round(deltaY);
        var result = new (int Dx, int Dy)[steps];

        int emittedX = 0;
        int emittedY = 0;
        for (int i = 1; i <= steps; i++)
        {
            int targetX = (int)Math.Round((double)totalDx * i / steps);
            int targetY = (int)Math.Round((double)totalDy * i / steps);
            result[i - 1] = (targetX - emittedX, targetY - emittedY);
            emittedX = targetX;
            emittedY = targetY;
        }

        return result;
    }

    public static bool TryPreferredClick(AutomationElement element)
    {
        try
        {
            var invoke = element.Patterns.Invoke.PatternOrDefault;
            if (invoke != null)
            {
                invoke.Invoke();
                return true;
            }

            var selection = element.Patterns.SelectionItem.PatternOrDefault;
            if (selection != null)
            {
                selection.Select();
                return true;
            }

            var toggle = element.Patterns.Toggle.PatternOrDefault;
            if (toggle != null)
            {
                toggle.Toggle();
                return true;
            }
        }
        catch
        {
            // Ignore and fall back
        }
        return false;
    }

    private static (IntPtr TargetHwnd, IntPtr ClientLParam) ResolveBackgroundPointTarget(IntPtr hwnd, int screenX, int screenY)
    {
        var resolved = NativeMethods.ResolveClickTarget(hwnd, screenX, screenY);
        if (resolved.Ok)
        {
            return (resolved.TargetHwnd, NativeMethods.ConvertToLParam(resolved.ClientX, resolved.ClientY));
        }

        var point = new NativeMethods.POINT { X = screenX, Y = screenY };
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.ScreenToClient(hwnd, ref point);
        }
        return (hwnd, NativeMethods.ConvertToLParam(point.X, point.Y));
    }

    private static (uint DownMsg, uint UpMsg, IntPtr DownWParam) GetBackgroundButtonMessages(string button)
    {
        if (string.Equals(button, "right", StringComparison.OrdinalIgnoreCase))
        {
            return (NativeMethods.WM_RBUTTONDOWN, NativeMethods.WM_RBUTTONUP, new IntPtr(0x0002));
        }

        if (string.Equals(button, "middle", StringComparison.OrdinalIgnoreCase))
        {
            return (NativeMethods.WM_MBUTTONDOWN, NativeMethods.WM_MBUTTONUP, new IntPtr(0x0010));
        }

        return (NativeMethods.WM_LBUTTONDOWN, NativeMethods.WM_LBUTTONUP, new IntPtr(0x0001));
    }

    private static (uint DownFlag, uint UpFlag) GetHardwareButtonFlags(string button)
    {
        if (string.Equals(button, "right", StringComparison.OrdinalIgnoreCase))
        {
            return (NativeMethods.MOUSEEVENTF_RIGHTDOWN, NativeMethods.MOUSEEVENTF_RIGHTUP);
        }

        if (string.Equals(button, "middle", StringComparison.OrdinalIgnoreCase))
        {
            return (NativeMethods.MOUSEEVENTF_MIDDLEDOWN, NativeMethods.MOUSEEVENTF_MIDDLEUP);
        }

        return (NativeMethods.MOUSEEVENTF_LEFTDOWN, NativeMethods.MOUSEEVENTF_LEFTUP);
    }

    public static void SendMouseClick(
        IntPtr hwnd,
        int screenX,
        int screenY,
        string button,
        int count,
        string buttonState = "click",
        int? holdMs = null)
    {
        string normButton = ActiveInputTracker.NormalizeButtonName(button);
        string normState = NormalizeButtonState(buttonState);

        var (downMsg, upMsg, downWParam) = GetBackgroundButtonMessages(normButton);

        if (normState == "up")
        {
            var (fallbackTarget, fallbackLParam) = ResolveBackgroundPointTarget(hwnd, screenX, screenY);
            var tracked = ActiveInputTracker.UnregisterMouseButtonAndResolveTarget(false, hwnd, fallbackTarget, normButton);
            IntPtr upTarget = tracked.TargetHwnd != IntPtr.Zero ? tracked.TargetHwnd : fallbackTarget;
            IntPtr upLParam = tracked.Found ? tracked.ClientLParam : fallbackLParam;
            if (upTarget != IntPtr.Zero)
            {
                NativeMethods.SpoofWindowActivation(hwnd, upTarget);
                NativeMethods.PostMessage(upTarget, upMsg, IntPtr.Zero, upLParam);
            }
            return;
        }

        var (targetHwnd, lParam) = ResolveBackgroundPointTarget(hwnd, screenX, screenY);
        NativeMethods.SpoofWindowActivation(hwnd, targetHwnd);

        if (normState == "down")
        {
            NativeMethods.PostMessage(targetHwnd, NativeMethods.WM_MOUSEMOVE, IntPtr.Zero, lParam);
            NativeMethods.PostMessage(targetHwnd, downMsg, downWParam, lParam);
            ActiveInputTracker.RegisterBackgroundMouseButton(hwnd, targetHwnd, normButton, screenX, screenY, lParam, holdMs);
            return;
        }

        bool isLeft = normButton == "left";
        if (isLeft && targetHwnd != IntPtr.Zero && (!holdMs.HasValue || holdMs.Value <= 0))
        {
            var sbCls = new System.Text.StringBuilder(128);
            NativeMethods.GetClassName(targetHwnd, sbCls, sbCls.Capacity);
            string cls = sbCls.ToString();
            if (cls.Contains("Button", StringComparison.OrdinalIgnoreCase))
            {
                int btnRepeat = Math.Max(1, count);
                for (int i = 0; i < btnRepeat; i++)
                {
                    NativeMethods.PostMessage(targetHwnd, NativeMethods.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                    if (i < btnRepeat - 1) PrecisionTimer.WaitMs(8);
                }
                return;
            }
        }

        int repeat = Math.Max(1, count);
        for (int i = 0; i < repeat; i++)
        {
            NativeMethods.PostMessage(targetHwnd, NativeMethods.WM_MOUSEMOVE, IntPtr.Zero, lParam);
            NativeMethods.PostMessage(targetHwnd, downMsg, downWParam, lParam);
            if (holdMs is > 0)
            {
                PrecisionTimer.WaitMs(holdMs.Value);
            }
            NativeMethods.PostMessage(targetHwnd, upMsg, IntPtr.Zero, lParam);
            if (i < repeat - 1)
            {
                PrecisionTimer.WaitMs(8);
            }
        }
    }

    private static void WaitIfPhysicalMouseDown(int maxWaitMs = 250)
    {
        int waited = 0;
        while (waited < maxWaitMs && NativeMethods.IsPhysicalMouseButtonDown())
        {
            PrecisionTimer.WaitMs(10);
            waited += 10;
        }
    }

    private static void FocusWindowIfNeeded(IntPtr hwnd, double settleMs = 8.0)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        IntPtr initialFg = NativeMethods.GetForegroundWindow();
        if (initialFg == hwnd)
        {
            return;
        }

        // Avoid blocking 2000ms inside win32kfull!xxxSetForegroundWindow2 when either the target window
        // or the current foreground window is not actively pumping its Win32 message queue within 12ms.
        if (!NativeMethods.IsWindowResponsive(hwnd, 12) ||
            (initialFg != IntPtr.Zero && !NativeMethods.IsWindowResponsive(initialFg, 12)))
        {
            return;
        }

        try
        {
            if (NativeMethods.IsIconic(hwnd))
            {
                NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_RESTORE);
            }

            if (!NativeMethods.SetForegroundWindow(hwnd) && NativeMethods.GetForegroundWindow() != hwnd)
            {
                IntPtr fgWnd = NativeMethods.GetForegroundWindow();
                uint curThreadId = NativeMethods.GetCurrentThreadId();
                uint fgThreadId = (fgWnd != IntPtr.Zero && fgWnd != hwnd && NativeMethods.IsWindowResponsive(fgWnd, 15))
                    ? NativeMethods.GetWindowThreadProcessId(fgWnd, out _)
                    : 0;
                bool attached = false;
                try
                {
                    if (fgThreadId != 0 && fgThreadId != curThreadId)
                    {
                        attached = NativeMethods.AttachThreadInput(curThreadId, fgThreadId, true);
                    }

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
                    NativeMethods.SendInput(1, [unlockInput], Marshal.SizeOf<NativeMethods.INPUT>());
                    NativeMethods.SetForegroundWindow(hwnd);
                    NativeMethods.BringWindowToTop(hwnd);

                    if (NativeMethods.GetForegroundWindow() != hwnd)
                    {
                        NativeMethods.SwitchToThisWindow(hwnd, true);
                    }
                }
                finally
                {
                    if (attached)
                    {
                        NativeMethods.AttachThreadInput(curThreadId, fgThreadId, false);
                    }
                }
            }
        }
        catch
        {
            try { NativeMethods.SetForegroundWindow(hwnd); } catch { }
        }

        if (settleMs > 0)
        {
            PrecisionTimer.WaitMs(settleMs);
        }
    }

    private static void RestoreWindowIfNeeded(IntPtr savedFg, IntPtr targetHwnd)
    {
        if (savedFg != IntPtr.Zero && savedFg != targetHwnd && NativeMethods.IsWindow(savedFg))
        {
            try
            {
                WindowManager.BringWindowToForeground(savedFg);
            }
            catch
            {
                try { NativeMethods.SetForegroundWindow(savedFg); } catch { }
            }
        }
    }

    public static void SendStealthClick(
        int screenX,
        int screenY,
        string button,
        int count,
        IntPtr hwnd = default,
        string buttonState = "click",
        int? holdMs = null)
    {
        WaitIfPhysicalMouseDown();

        bool hasSavedCursor = NativeMethods.GetCursorPos(out var savedCursor);
        IntPtr savedFg = NativeMethods.GetForegroundWindow();

        try
        {
            SendGlobalClick(screenX, screenY, button, count, hwnd, dwellMs: 12, buttonState: buttonState, holdMs: holdMs);
        }
        finally
        {
            if (hasSavedCursor)
            {
                try { NativeMethods.SetCursorPos(savedCursor.X, savedCursor.Y); } catch { }
            }

            RestoreWindowIfNeeded(savedFg, hwnd);
        }
    }

    public static void SendGlobalClick(
        int screenX,
        int screenY,
        string button,
        int count,
        IntPtr hwnd = default,
        int dwellMs = 16,
        string buttonState = "click",
        int? holdMs = null)
    {
        string normButton = ActiveInputTracker.NormalizeButtonName(button);
        string normState = NormalizeButtonState(buttonState);
        var (downFlag, upFlag) = GetHardwareButtonFlags(normButton);
        int inputSize = Marshal.SizeOf<NativeMethods.INPUT>();

        FocusWindowIfNeeded(hwnd, Math.Min(8, Math.Max(0, dwellMs)));

        if (normState == "up")
        {
            var tracked = ActiveInputTracker.UnregisterMouseButtonAndResolveTarget(true, hwnd, IntPtr.Zero, normButton);
            int upX = screenX;
            int upY = screenY;
            if (upX == int.MinValue && tracked.Found)
            {
                upX = tracked.ScreenX;
                upY = tracked.ScreenY;
            }
            else if (upX == int.MinValue && NativeMethods.GetCursorPos(out var curPos))
            {
                upX = curPos.X;
                upY = curPos.Y;
            }

            NativeMethods.SetCursorPos(upX, upY);
            var (upNormX, upNormY, upMoveFlags) = NormalizeScreenPointForSendInput(upX, upY);
            uint upButtonFlags = upMoveFlags & ~NativeMethods.MOUSEEVENTF_MOVE;
            var upOnlyInput = new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_MOUSE,
                u = new NativeMethods.InputUnion
                {
                    mi = new NativeMethods.MOUSEINPUT
                    {
                        dx = upNormX,
                        dy = upNormY,
                        dwFlags = upFlag | upButtonFlags
                    }
                }
            };
            NativeMethods.SendInput(1, [upOnlyInput], inputSize);
            return;
        }

        NativeMethods.SetCursorPos(screenX, screenY);
        var (normX, normY, moveFlags) = NormalizeScreenPointForSendInput(screenX, screenY);

        var moveInput = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            u = new NativeMethods.InputUnion
            {
                mi = new NativeMethods.MOUSEINPUT
                {
                    dx = normX,
                    dy = normY,
                    dwFlags = moveFlags
                }
            }
        };

        NativeMethods.SendInput(1, [moveInput], inputSize);
        PrecisionTimer.WaitMs(Math.Min(8, Math.Max(0, dwellMs)));

        // Preserve MOUSEEVENTF_ABSOLUTE and MOUSEEVENTF_VIRTUALDESK so normX/normY are never treated as relative offsets
        uint buttonFlags = moveFlags & ~NativeMethods.MOUSEEVENTF_MOVE;

        if (normState == "down")
        {
            var downOnlyInput = new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_MOUSE,
                u = new NativeMethods.InputUnion
                {
                    mi = new NativeMethods.MOUSEINPUT
                    {
                        dx = normX,
                        dy = normY,
                        dwFlags = downFlag | buttonFlags
                    }
                }
            };
            NativeMethods.SendInput(1, [downOnlyInput], inputSize);
            ActiveInputTracker.RegisterHardwareMouseButton(normButton, screenX, screenY, holdMs);
            return;
        }

        double effectiveDwellMs = holdMs ?? dwellMs;
        int repeat = Math.Max(1, count);
        for (int i = 0; i < repeat; i++)
        {
            var downInput = new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_MOUSE,
                u = new NativeMethods.InputUnion
                {
                    mi = new NativeMethods.MOUSEINPUT
                    {
                        dx = normX,
                        dy = normY,
                        dwFlags = downFlag | buttonFlags
                    }
                }
            };

            var upInput = new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_MOUSE,
                u = new NativeMethods.InputUnion
                {
                    mi = new NativeMethods.MOUSEINPUT
                    {
                        dx = normX,
                        dy = normY,
                        dwFlags = upFlag | buttonFlags
                    }
                }
            };

            NativeMethods.SendInput(1, [downInput], inputSize);
            if (effectiveDwellMs > 0)
            {
                PrecisionTimer.WaitMs(effectiveDwellMs);
            }
            NativeMethods.SendInput(1, [upInput], inputSize);

            if (i < repeat - 1 && effectiveDwellMs > 0)
            {
                PrecisionTimer.WaitMs(Math.Min(12.0, effectiveDwellMs));
            }
        }
    }

    public static string NormalizeButtonState(string? buttonState)
    {
        if (string.IsNullOrWhiteSpace(buttonState))
        {
            return "click";
        }

        string norm = buttonState.Trim().ToLowerInvariant();
        return norm switch
        {
            "click" or "down" or "up" => norm,
            _ => throw new ArgumentException($"Invalid button_state '{buttonState}'. Valid options are: click, down, up.")
        };
    }

    private static void GlideHardwareCursorHumanized(int targetScreenX, int targetScreenY, HumanizeProfile humanize, int? durationOverrideMs = null, int? stepsOverride = null)
    {
        if (humanize == HumanizeProfile.None)
        {
            return;
        }

        int startX = targetScreenX;
        int startY = targetScreenY;
        if (NativeMethods.GetCursorPos(out var curPos))
        {
            startX = curPos.X;
            startY = curPos.Y;
        }

        var waypoints = HumanizedPhysics.GenerateAbsoluteSteps(
            startX,
            startY,
            targetScreenX,
            targetScreenY,
            humanize,
            durationOverrideMs,
            stepsOverride);

        int inputSize = Marshal.SizeOf<NativeMethods.INPUT>();
        long epochTicks = Stopwatch.GetTimestamp();
        double ticksPerMs = Stopwatch.Frequency / 1000.0;

        for (int i = 0; i < waypoints.Length; i++)
        {
            var (wx, wy, tMs) = waypoints[i];
            if (tMs > 0.0)
            {
                PrecisionTimer.WaitUntilTimestamp(epochTicks + (long)Math.Round(tMs * ticksPerMs));
            }

            var (nx, ny, mf) = NormalizeScreenPointForSendInput(wx, wy);
            var moveInput = new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_MOUSE,
                u = new NativeMethods.InputUnion
                {
                    mi = new NativeMethods.MOUSEINPUT
                    {
                        dx = nx,
                        dy = ny,
                        dwFlags = mf
                    }
                }
            };
            NativeMethods.SendInput(1, [moveInput], inputSize);
        }

        NativeMethods.SetCursorPos(targetScreenX, targetScreenY);
    }

    public static void Click(
        IntPtr hwnd,
        Frame? windowBounds,
        ElementRecord? elementRecord,
        double? x,
        double? y,
        int clickCount,
        string mouseButton,
        string clickMethod,
        AutomationElement? automationElement,
        string buttonState = "click",
        int? holdMs = null,
        double? coordOriginX = null,
        double? coordOriginY = null,
        HumanizeProfile humanize = HumanizeProfile.None)
    {
        string method = string.IsNullOrWhiteSpace(clickMethod) ? "auto" : clickMethod.Trim().ToLowerInvariant();
        string normState = NormalizeButtonState(buttonState);
        string normButton = ActiveInputTracker.NormalizeButtonName(mouseButton);

        if (holdMs.HasValue && (holdMs.Value < 0 || holdMs.Value > 60_000))
        {
            throw new ArgumentException("hold_ms must be between 0 and 60000.");
        }

        if (method == "accessibility")
        {
            if (normState != "click")
            {
                throw new InvalidOperationException($"click_method 'accessibility' does not support button_state '{normState}'");
            }
            if (automationElement == null)
            {
                throw new InvalidOperationException("click_method 'accessibility' requires element_index");
            }
            if (normButton is "right" or "middle")
            {
                throw new InvalidOperationException($"click_method 'accessibility' does not support mouse_button '{mouseButton}'");
            }
            if (!TryPreferredClick(automationElement))
            {
                throw new InvalidOperationException("click_method 'accessibility' could not click the requested element");
            }
            return;
        }

        if (method == "sky_click")
        {
            throw new NotSupportedException("click_method 'sky_click' is not supported on Windows");
        }

        NativeMethods.POINT point;
        if (elementRecord?.Frame != null && windowBounds != null)
        {
            var p = GetScreenPoint(elementRecord.Frame, windowBounds);
            point = p ?? new NativeMethods.POINT { X = (int)Math.Round(windowBounds.X + (x ?? 0)), Y = (int)Math.Round(windowBounds.Y + (y ?? 0)) };
        }
        else if (coordOriginX.HasValue && coordOriginY.HasValue && x.HasValue && y.HasValue)
        {
            point = new NativeMethods.POINT
            {
                X = (int)Math.Round(coordOriginX.Value + x.Value),
                Y = (int)Math.Round(coordOriginY.Value + y.Value)
            };
        }
        else if (windowBounds != null && x.HasValue && y.HasValue)
        {
            point = new NativeMethods.POINT
            {
                X = (int)Math.Round(windowBounds.X + x.Value),
                Y = (int)Math.Round(windowBounds.Y + y.Value)
            };
        }
        else if (x.HasValue && y.HasValue)
        {
            point = new NativeMethods.POINT
            {
                X = (int)Math.Round(x.Value),
                Y = (int)Math.Round(y.Value)
            };
        }
        else if (normState == "up")
        {
            if (NativeMethods.GetCursorPos(out var cur))
            {
                point = cur;
            }
            else
            {
                point = new NativeMethods.POINT { X = 0, Y = 0 };
            }
        }
        else
        {
            throw new InvalidOperationException("click requires either element_index or x/y coordinates");
        }

        int? effectiveHoldMs = holdMs;
        if (humanize != HumanizeProfile.None && normState == "click")
        {
            bool isHw = method is "global" or "hardware" or "stealth" ||
                        (method == "auto" && (hwnd == IntPtr.Zero || TestEnvFlagEnabled("OPEN_COMPUTER_USE_ALLOW_GLOBAL_POINTER_FALLBACKS")));
            effectiveHoldMs = (int)Math.Round(HumanizedPhysics.SampleHoldDurationMs(humanize, holdMs, isHw));
        }

        if (method is "global" or "hardware")
        {
            if (humanize != HumanizeProfile.None && normState != "up")
            {
                FocusWindowIfNeeded(hwnd, 6.0);
                GlideHardwareCursorHumanized(point.X, point.Y, humanize);
            }
            SendGlobalClick(point.X, point.Y, normButton, clickCount, hwnd, buttonState: normState, holdMs: effectiveHoldMs);
            return;
        }

        if (method == "stealth")
        {
            SendStealthClick(point.X, point.Y, normButton, clickCount, hwnd, buttonState: normState, holdMs: effectiveHoldMs);
            return;
        }

        if (method == "app_post")
        {
            SendMouseClick(hwnd, point.X, point.Y, normButton, clickCount, normState, effectiveHoldMs);
            return;
        }

        if (method == "auto")
        {
            bool handled = false;
            if (normState == "click" &&
                (!holdMs.HasValue || holdMs.Value <= 0) &&
                humanize == HumanizeProfile.None &&
                automationElement != null &&
                normButton == "left")
            {
                handled = TryPreferredClick(automationElement);
            }

            if (!handled)
            {
                if (hwnd != IntPtr.Zero && !TestEnvFlagEnabled("OPEN_COMPUTER_USE_ALLOW_GLOBAL_POINTER_FALLBACKS"))
                {
                    SendMouseClick(hwnd, point.X, point.Y, normButton, clickCount, normState, effectiveHoldMs);
                }
                else
                {
                    if (humanize != HumanizeProfile.None && normState != "up")
                    {
                        FocusWindowIfNeeded(hwnd, 6.0);
                        GlideHardwareCursorHumanized(point.X, point.Y, humanize);
                    }
                    SendGlobalClick(point.X, point.Y, normButton, clickCount, hwnd, buttonState: normState, holdMs: effectiveHoldMs);
                }
            }
            return;
        }

        throw new InvalidOperationException($"Invalid click_method '{clickMethod}'");
    }

    public static void MoveMouse(
        IntPtr hwnd,
        Frame? windowBounds,
        double? x,
        double? y,
        double? deltaX,
        double? deltaY,
        int steps = 1,
        int durationMs = 0,
        string? method = null,
        double? coordOriginX = null,
        double? coordOriginY = null,
        HumanizeProfile humanize = HumanizeProfile.None)
    {
        if (steps < 1 || steps > 240)
        {
            throw new ArgumentException("steps must be between 1 and 240.");
        }

        if (durationMs < 0 || durationMs > 5000)
        {
            throw new ArgumentException("duration_ms must be between 0 and 5000.");
        }

        bool hasRelative = deltaX.HasValue || deltaY.HasValue;
        bool hasAbsolute = x.HasValue || y.HasValue;

        if (!hasRelative && !hasAbsolute)
        {
            throw new ArgumentException("move_mouse requires either (x and y) coordinates or (delta_x / delta_y) relative motion.");
        }

        if (hasAbsolute && (!x.HasValue || !y.HasValue))
        {
            throw new ArgumentException("move_mouse absolute movement requires both x and y coordinates.");
        }

        string defaultMethod = hasRelative ? "hardware" : (hwnd != IntPtr.Zero ? "auto" : "hardware");
        string normMethod = string.IsNullOrWhiteSpace(method) ? defaultMethod : method.Trim().ToLowerInvariant();
        if (normMethod is not ("hardware" or "global" or "auto" or "stealth" or "app_post"))
        {
            throw new ArgumentException($"Invalid move_mouse method '{method}'. Valid options are: hardware, global, auto, stealth, app_post.");
        }

        int inputSize = Marshal.SizeOf<NativeMethods.INPUT>();

        if (hasRelative)
        {
            if (normMethod == "app_post")
            {
                throw new ArgumentException("Relative delta_x/delta_y 3D camera movement requires hardware, global, auto, or stealth method (not app_post).");
            }

            double dxVal = deltaX ?? 0.0;
            double dyVal = deltaY ?? 0.0;
            var stepDeltas = humanize == HumanizeProfile.None
                ? ComputeRelativeSteps(dxVal, dyVal, steps)
                : HumanizedPhysics.GenerateRelativeSteps(dxVal, dyVal, humanize, steps > 1 ? steps : null);

            double effectiveRelDurationMs = durationMs > 0
                ? durationMs
                : (humanize != HumanizeProfile.None
                    ? HumanizedPhysics.ComputeAutoDurationMs(humanize, Math.Sqrt((dxVal * dxVal) + (dyVal * dyVal)))
                    : 0.0);

            IntPtr savedFg = IntPtr.Zero;
            if (normMethod == "stealth")
            {
                WaitIfPhysicalMouseDown();
                savedFg = NativeMethods.GetForegroundWindow();
            }

            try
            {
                if (hwnd != IntPtr.Zero)
                {
                    FocusWindowIfNeeded(hwnd, 6.0);
                }

                double stepDelayMs = effectiveRelDurationMs > 0 ? effectiveRelDurationMs / stepDeltas.Length : 0.0;
                long relEpoch = Stopwatch.GetTimestamp();
                double ticksPerMs = Stopwatch.Frequency / 1000.0;

                for (int i = 0; i < stepDeltas.Length; i++)
                {
                    if (stepDelayMs > 0 && i > 0)
                    {
                        PrecisionTimer.WaitUntilTimestamp(relEpoch + (long)Math.Round(stepDelayMs * i * ticksPerMs));
                    }

                    var (stepDx, stepDy) = stepDeltas[i];
                    if (stepDx != 0 || stepDy != 0 || stepDeltas.Length == 1)
                    {
                        var relInput = new NativeMethods.INPUT
                        {
                            type = NativeMethods.INPUT_MOUSE,
                            u = new NativeMethods.InputUnion
                            {
                                mi = new NativeMethods.MOUSEINPUT
                                {
                                    dx = stepDx,
                                    dy = stepDy,
                                    mouseData = 0,
                                    // Pure MOUSEEVENTF_MOVE without MOUSEEVENTF_ABSOLUTE for RawInput 3D camera rotation
                                    dwFlags = NativeMethods.MOUSEEVENTF_MOVE,
                                    time = 0,
                                    dwExtraInfo = UIntPtr.Zero
                                }
                            }
                        };
                        NativeMethods.SendInput(1, [relInput], inputSize);
                    }

                    if (stepDelayMs > 0 && stepDeltas.Length == 1)
                    {
                        PrecisionTimer.WaitMs(stepDelayMs);
                    }
                }
            }
            finally
            {
                if (normMethod == "stealth")
                {
                    RestoreWindowIfNeeded(savedFg, hwnd);
                }
            }

            return;
        }

        if (!double.IsFinite(x!.Value) || !double.IsFinite(y!.Value))
        {
            throw new ArgumentException("x and y coordinates must be finite numbers.");
        }

        double baseOriginX = coordOriginX ?? windowBounds?.X ?? 0.0;
        double baseOriginY = coordOriginY ?? windowBounds?.Y ?? 0.0;
        int targetScreenX = (int)Math.Round(baseOriginX + x.Value);
        int targetScreenY = (int)Math.Round(baseOriginY + y.Value);

        bool useBackgroundPost = normMethod == "app_post" ||
                                 (normMethod == "auto" && hwnd != IntPtr.Zero && !TestEnvFlagEnabled("OPEN_COMPUTER_USE_ALLOW_GLOBAL_POINTER_FALLBACKS"));

        if (useBackgroundPost)
        {
            if (hwnd == IntPtr.Zero)
            {
                throw new ArgumentException("move_mouse with method 'app_post' requires a valid target app.");
            }

            var (targetHwnd, endLParam) = ResolveBackgroundPointTarget(hwnd, targetScreenX, targetScreenY);
            NativeMethods.SpoofWindowActivation(hwnd, targetHwnd);

            if (steps <= 1 && humanize == HumanizeProfile.None)
            {
                NativeMethods.PostMessage(targetHwnd, NativeMethods.WM_MOUSEMOVE, IntPtr.Zero, endLParam);
                if (durationMs > 0) PrecisionTimer.WaitMs(durationMs);
                return;
            }

            int endClientX = unchecked((short)(endLParam.ToInt64() & 0xFFFF));
            int endClientY = unchecked((short)((endLParam.ToInt64() >> 16) & 0xFFFF));
            int startClientX = endClientX / 2;
            int startClientY = endClientY / 2;

            if (humanize != HumanizeProfile.None)
            {
                var bgWaypoints = HumanizedPhysics.GenerateAbsoluteSteps(
                    startClientX,
                    startClientY,
                    endClientX,
                    endClientY,
                    humanize,
                    durationMs > 0 ? durationMs : null,
                    steps > 1 ? steps : null,
                    monitorBoundsOverride: null);
                long bgEpoch = Stopwatch.GetTimestamp();
                double bgTicksPerMs = Stopwatch.Frequency / 1000.0;
                for (int i = 0; i < bgWaypoints.Length; i++)
                {
                    var (cx, cy, tMs) = bgWaypoints[i];
                    if (tMs > 0.0)
                    {
                        PrecisionTimer.WaitUntilTimestamp(bgEpoch + (long)Math.Round(tMs * bgTicksPerMs));
                    }
                    NativeMethods.PostMessage(targetHwnd, NativeMethods.WM_MOUSEMOVE, IntPtr.Zero, NativeMethods.ConvertToLParam(cx, cy));
                }
                return;
            }

            double stepDelayMs = durationMs > 0 ? (double)durationMs / steps : 0.0;

            for (int i = 1; i <= steps; i++)
            {
                int cx = (int)Math.Round(startClientX + ((endClientX - startClientX) * i / (double)steps));
                int cy = (int)Math.Round(startClientY + ((endClientY - startClientY) * i / (double)steps));
                NativeMethods.PostMessage(targetHwnd, NativeMethods.WM_MOUSEMOVE, IntPtr.Zero, NativeMethods.ConvertToLParam(cx, cy));
                if (stepDelayMs > 0 && i < steps)
                {
                    PrecisionTimer.WaitMs(stepDelayMs);
                }
            }
            return;
        }

        // Hardware / global / stealth absolute cursor positioning
        bool hasSavedCursor = false;
        NativeMethods.POINT savedCursor = default;
        IntPtr savedForeground = IntPtr.Zero;

        if (normMethod == "stealth")
        {
            WaitIfPhysicalMouseDown();
            hasSavedCursor = NativeMethods.GetCursorPos(out savedCursor);
            savedForeground = NativeMethods.GetForegroundWindow();
        }

        try
        {
            if (hwnd != IntPtr.Zero)
            {
                FocusWindowIfNeeded(hwnd, 6.0);
            }

            if (humanize != HumanizeProfile.None)
            {
                GlideHardwareCursorHumanized(
                    targetScreenX,
                    targetScreenY,
                    humanize,
                    durationMs > 0 ? durationMs : null,
                    steps > 1 ? steps : null);
                return;
            }

            int startX = targetScreenX;
            int startY = targetScreenY;
            if (steps > 1 && NativeMethods.GetCursorPos(out var curPos))
            {
                startX = curPos.X;
                startY = curPos.Y;
            }

            double stepDelayMs = durationMs > 0 ? (double)durationMs / steps : 0.0;
            for (int i = 1; i <= steps; i++)
            {
                int curX = steps == 1 ? targetScreenX : (int)Math.Round(startX + ((targetScreenX - startX) * i / (double)steps));
                int curY = steps == 1 ? targetScreenY : (int)Math.Round(startY + ((targetScreenY - startY) * i / (double)steps));

                NativeMethods.SetCursorPos(curX, curY);
                var (normX, normY, moveFlags) = NormalizeScreenPointForSendInput(curX, curY);
                var moveInput = new NativeMethods.INPUT
                {
                    type = NativeMethods.INPUT_MOUSE,
                    u = new NativeMethods.InputUnion
                    {
                        mi = new NativeMethods.MOUSEINPUT
                        {
                            dx = normX,
                            dy = normY,
                            dwFlags = moveFlags
                        }
                    }
                };
                NativeMethods.SendInput(1, [moveInput], inputSize);

                if (stepDelayMs > 0 && (i < steps || steps == 1))
                {
                    PrecisionTimer.WaitMs(stepDelayMs);
                }
            }
        }
        finally
        {
            if (normMethod == "stealth")
            {
                if (hasSavedCursor)
                {
                    try { NativeMethods.SetCursorPos(savedCursor.X, savedCursor.Y); } catch { }
                }
                RestoreWindowIfNeeded(savedForeground, hwnd);
            }
        }
    }

    public static TrajectoryExecutionResult ExecuteTrajectory(
        IntPtr hwnd,
        Frame? windowBounds,
        IReadOnlyList<TrajectoryWaypoint> waypoints,
        string? coordinateSpace = null,
        bool snapshotClientOnly = false,
        string? method = null,
        string? mouseButton = null,
        bool holdButton = false,
        HumanizeProfile humanize = HumanizeProfile.None)
    {
        if (waypoints == null || waypoints.Count < 1 || waypoints.Count > 2400)
        {
            throw new ArgumentException("waypoints must contain between 1 and 2400 items.");
        }

        double prevTimeMs = 0.0;
        for (int i = 0; i < waypoints.Count; i++)
        {
            var wp = waypoints[i];
            if (!double.IsFinite(wp.X) || !double.IsFinite(wp.Y))
            {
                throw new ArgumentException($"Waypoint[{i}] x and y must be finite numbers.");
            }
            if (!double.IsFinite(wp.TimeMs) || wp.TimeMs < 0.0 || wp.TimeMs > 60_000.0)
            {
                throw new ArgumentException($"Waypoint[{i}] t_ms ({wp.TimeMs}) must be between 0 and 60000.");
            }
            if (i > 0 && wp.TimeMs < prevTimeMs)
            {
                throw new ArgumentException($"Waypoint[{i}] t_ms ({wp.TimeMs}) must be monotonically non-decreasing (>= {prevTimeMs}).");
            }
            string bs = string.IsNullOrWhiteSpace(wp.ButtonState) ? "none" : wp.ButtonState.Trim().ToLowerInvariant();
            if (bs is not ("none" or "down" or "up" or "click"))
            {
                throw new ArgumentException($"Waypoint[{i}] invalid button_state '{wp.ButtonState}'. Valid options: none, down, up, click.");
            }
            wp.ButtonState = bs;
            prevTimeMs = wp.TimeMs;
        }

        string normButton = ActiveInputTracker.NormalizeButtonName(mouseButton ?? "left");
        string normMethod = string.IsNullOrWhiteSpace(method) ? "hardware" : method.Trim().ToLowerInvariant();
        if (normMethod is not ("hardware" or "global" or "stealth" or "app_post" or "auto"))
        {
            throw new ArgumentException($"Invalid trajectory method '{method}'. Valid options are: hardware, global, stealth, app_post, auto.");
        }

        bool hasTargetApp = hwnd != IntPtr.Zero;
        string normSpace = WindowManager.NormalizeCoordinateSpace(coordinateSpace, hasTargetApp, snapshotClientOnly);
        var (originX, originY) = WindowManager.ResolveCoordinateOrigin(normSpace, hwnd, windowBounds);

        bool useBackgroundPost = normMethod == "app_post" ||
                                 (normMethod == "auto" && hwnd != IntPtr.Zero && !TestEnvFlagEnabled("OPEN_COMPUTER_USE_ALLOW_GLOBAL_POINTER_FALLBACKS"));

        if ((useBackgroundPost || normMethod == "stealth") && hwnd == IntPtr.Zero)
        {
            throw new ArgumentException($"follow_trajectory with method '{normMethod}' requires a target application ('app' or 'pid').");
        }

        int count = waypoints.Count;
        var screenXs = new int[count];
        var screenYs = new int[count];
        for (int i = 0; i < count; i++)
        {
            screenXs[i] = (int)Math.Round(originX + waypoints[i].X);
            screenYs[i] = (int)Math.Round(originY + waypoints[i].Y);
        }

        // If humanize was explicitly requested on follow_trajectory, apply subtle spatial micro-jitter to intermediate
        // waypoints while keeping waypoint[0] and waypoint[^1] 100% exact (0.0px endpoint error).
        if (humanize != HumanizeProfile.None && count >= 3)
        {
            double jitterStd = humanize switch
            {
                HumanizeProfile.Subtle => 0.8,
                HumanizeProfile.Fast => 1.2,
                _ => 2.0
            };
            var monitorRect = HumanizedPhysics.ResolveContainingMonitorBounds(screenXs[0], screenYs[0], screenXs[^1], screenYs[^1]);
            for (int i = 1; i < count - 1; i++)
            {
                if (waypoints[i].ButtonState is "none")
                {
                    int jx = screenXs[i] + (int)Math.Round(HumanizedPhysics.SampleTruncatedGaussian(0.0, jitterStd, -5.0, 5.0));
                    int jy = screenYs[i] + (int)Math.Round(HumanizedPhysics.SampleTruncatedGaussian(0.0, jitterStd, -5.0, 5.0));
                    if (monitorRect.HasValue)
                    {
                        var mr = monitorRect.Value;
                        jx = Math.Clamp(jx, mr.Left, mr.Right - 1);
                        jy = Math.Clamp(jy, mr.Top, mr.Bottom - 1);
                    }
                    screenXs[i] = jx;
                    screenYs[i] = jy;
                }
            }
        }

        double plannedDurationMs = waypoints[^1].TimeMs;
        int holdSafetyTtlMs = (int)Math.Clamp(Math.Ceiling(plannedDurationMs) + 5000.0, 1000.0, 65000.0);
        int inputSize = Marshal.SizeOf<NativeMethods.INPUT>();
        var (downFlag, upFlag) = GetHardwareButtonFlags(normButton);
        var (downMsg, upMsg, downWParam) = GetBackgroundButtonMessages(normButton);

        bool hasSavedCursor = false;
        NativeMethods.POINT savedCursor = default;
        IntPtr savedForeground = IntPtr.Zero;

        if (normMethod == "stealth")
        {
            WaitIfPhysicalMouseDown();
            hasSavedCursor = NativeMethods.GetCursorPos(out savedCursor);
            savedForeground = NativeMethods.GetForegroundWindow();
        }

        if (!useBackgroundPost && hwnd != IntPtr.Zero)
        {
            FocusWindowIfNeeded(hwnd, 6.0);
        }
        else if (useBackgroundPost)
        {
            var (initTarget, _) = ResolveBackgroundPointTarget(hwnd, screenXs[0], screenYs[0]);
            NativeMethods.SpoofWindowActivation(hwnd, initTarget);
        }

        bool buttonCurrentlyDown = false;
        bool holdManagedByTrajectory = false;
        int lastScreenX = screenXs[0];
        int lastScreenY = screenYs[0];
        IntPtr lastBgTarget = IntPtr.Zero;
        IntPtr lastBgLParam = IntPtr.Zero;
        int executed = 0;
        double maxDriftMs = 0.0;

        long epochTicks = Stopwatch.GetTimestamp();
        double ticksPerMs = Stopwatch.Frequency / 1000.0;

        try
        {
            for (int i = 0; i < count; i++)
            {
                var wp = waypoints[i];
                int sx = screenXs[i];
                int sy = screenYs[i];

                long targetTicks = epochTicks + (long)Math.Round(wp.TimeMs * ticksPerMs);
                PrecisionTimer.WaitUntilTimestamp(targetTicks);

                double actualElapsedMs = (Stopwatch.GetTimestamp() - epochTicks) / ticksPerMs;
                double absDrift = Math.Abs(actualElapsedMs - wp.TimeMs);
                if (absDrift > maxDriftMs)
                {
                    maxDriftMs = absDrift;
                }

                if (useBackgroundPost)
                {
                    var (ptTarget, ptLParam) = ResolveBackgroundPointTarget(hwnd, sx, sy);
                    lastBgTarget = ptTarget;
                    lastBgLParam = ptLParam;

                    IntPtr moveWParam = buttonCurrentlyDown ? downWParam : IntPtr.Zero;
                    NativeMethods.PostMessage(ptTarget, NativeMethods.WM_MOUSEMOVE, moveWParam, ptLParam);

                    if (i == 0 && holdButton)
                    {
                        NativeMethods.PostMessage(ptTarget, downMsg, downWParam, ptLParam);
                        ActiveInputTracker.RegisterBackgroundMouseButton(hwnd, ptTarget, normButton, sx, sy, ptLParam, holdSafetyTtlMs);
                        buttonCurrentlyDown = true;
                        holdManagedByTrajectory = true;
                    }

                    if (wp.ButtonState == "down" && !buttonCurrentlyDown)
                    {
                        NativeMethods.PostMessage(ptTarget, downMsg, downWParam, ptLParam);
                        ActiveInputTracker.RegisterBackgroundMouseButton(hwnd, ptTarget, normButton, sx, sy, ptLParam, holdSafetyTtlMs);
                        buttonCurrentlyDown = true;
                    }
                    else if (wp.ButtonState == "up" && buttonCurrentlyDown)
                    {
                        _ = ActiveInputTracker.UnregisterMouseButtonAndResolveTarget(false, hwnd, ptTarget, normButton);
                        NativeMethods.PostMessage(ptTarget, upMsg, IntPtr.Zero, ptLParam);
                        buttonCurrentlyDown = false;
                        holdManagedByTrajectory = false;
                    }
                    else if (wp.ButtonState == "click")
                    {
                        NativeMethods.PostMessage(ptTarget, downMsg, downWParam, ptLParam);
                        NativeMethods.PostMessage(ptTarget, upMsg, IntPtr.Zero, ptLParam);
                        buttonCurrentlyDown = false;
                    }
                }
                else
                {
                    NativeMethods.SetCursorPos(sx, sy);
                    var (normX, normY, moveFlags) = NormalizeScreenPointForSendInput(sx, sy);
                    uint absFlagsOnly = moveFlags & ~NativeMethods.MOUSEEVENTF_MOVE;

                    var moveInput = new NativeMethods.INPUT
                    {
                        type = NativeMethods.INPUT_MOUSE,
                        u = new NativeMethods.InputUnion
                        {
                            mi = new NativeMethods.MOUSEINPUT
                            {
                                dx = normX,
                                dy = normY,
                                dwFlags = moveFlags
                            }
                        }
                    };
                    NativeMethods.SendInput(1, [moveInput], inputSize);

                    if (i == 0 && holdButton)
                    {
                        var downInput = new NativeMethods.INPUT
                        {
                            type = NativeMethods.INPUT_MOUSE,
                            u = new NativeMethods.InputUnion
                            {
                                mi = new NativeMethods.MOUSEINPUT
                                {
                                    dx = normX,
                                    dy = normY,
                                    dwFlags = downFlag | absFlagsOnly
                                }
                            }
                        };
                        NativeMethods.SendInput(1, [downInput], inputSize);
                        ActiveInputTracker.RegisterHardwareMouseButton(normButton, sx, sy, holdSafetyTtlMs);
                        buttonCurrentlyDown = true;
                        holdManagedByTrajectory = true;
                    }

                    if (wp.ButtonState == "down" && !buttonCurrentlyDown)
                    {
                        var downInput = new NativeMethods.INPUT
                        {
                            type = NativeMethods.INPUT_MOUSE,
                            u = new NativeMethods.InputUnion
                            {
                                mi = new NativeMethods.MOUSEINPUT
                                {
                                    dx = normX,
                                    dy = normY,
                                    dwFlags = downFlag | absFlagsOnly
                                }
                            }
                        };
                        NativeMethods.SendInput(1, [downInput], inputSize);
                        ActiveInputTracker.RegisterHardwareMouseButton(normButton, sx, sy, holdSafetyTtlMs);
                        buttonCurrentlyDown = true;
                    }
                    else if (wp.ButtonState == "up" && buttonCurrentlyDown)
                    {
                        _ = ActiveInputTracker.UnregisterMouseButtonAndResolveTarget(true, hwnd, IntPtr.Zero, normButton);
                        var upInput = new NativeMethods.INPUT
                        {
                            type = NativeMethods.INPUT_MOUSE,
                            u = new NativeMethods.InputUnion
                            {
                                mi = new NativeMethods.MOUSEINPUT
                                {
                                    dx = normX,
                                    dy = normY,
                                    dwFlags = upFlag | absFlagsOnly
                                }
                            }
                        };
                        NativeMethods.SendInput(1, [upInput], inputSize);
                        buttonCurrentlyDown = false;
                        holdManagedByTrajectory = false;
                    }
                    else if (wp.ButtonState == "click")
                    {
                        var clickInputs = new[]
                        {
                            new NativeMethods.INPUT
                            {
                                type = NativeMethods.INPUT_MOUSE,
                                u = new NativeMethods.InputUnion
                                {
                                    mi = new NativeMethods.MOUSEINPUT { dx = normX, dy = normY, dwFlags = downFlag | absFlagsOnly }
                                }
                            },
                            new NativeMethods.INPUT
                            {
                                type = NativeMethods.INPUT_MOUSE,
                                u = new NativeMethods.InputUnion
                                {
                                    mi = new NativeMethods.MOUSEINPUT { dx = normX, dy = normY, dwFlags = upFlag | absFlagsOnly }
                                }
                            }
                        };
                        NativeMethods.SendInput(2, clickInputs, inputSize);
                        buttonCurrentlyDown = false;
                    }
                }

                lastScreenX = sx;
                lastScreenY = sy;
                executed++;
            }
        }
        finally
        {
            if (holdManagedByTrajectory)
            {
                if (useBackgroundPost)
                {
                    var tracked = ActiveInputTracker.UnregisterMouseButtonAndResolveTarget(false, hwnd, lastBgTarget, normButton);
                    IntPtr relTarget = tracked.TargetHwnd != IntPtr.Zero ? tracked.TargetHwnd : lastBgTarget;
                    IntPtr relLParam = tracked.Found ? tracked.ClientLParam : lastBgLParam;
                    if (relTarget != IntPtr.Zero)
                    {
                        try { NativeMethods.PostMessage(relTarget, upMsg, IntPtr.Zero, relLParam); } catch { }
                    }
                }
                else
                {
                    _ = ActiveInputTracker.UnregisterMouseButtonAndResolveTarget(true, hwnd, IntPtr.Zero, normButton);
                    var (upNormX, upNormY, upMoveFlags) = NormalizeScreenPointForSendInput(lastScreenX, lastScreenY);
                    uint upAbsFlags = upMoveFlags & ~NativeMethods.MOUSEEVENTF_MOVE;
                    var upInput = new NativeMethods.INPUT
                    {
                        type = NativeMethods.INPUT_MOUSE,
                        u = new NativeMethods.InputUnion
                        {
                            mi = new NativeMethods.MOUSEINPUT
                            {
                                dx = upNormX,
                                dy = upNormY,
                                dwFlags = upFlag | upAbsFlags
                            }
                        }
                    };
                    try { NativeMethods.SendInput(1, [upInput], inputSize); } catch { }
                }
            }

            if (normMethod == "stealth")
            {
                if (hasSavedCursor)
                {
                    try { NativeMethods.SetCursorPos(savedCursor.X, savedCursor.Y); } catch { }
                }
                RestoreWindowIfNeeded(savedForeground, hwnd);
            }
        }

        double totalActualMs = (Stopwatch.GetTimestamp() - epochTicks) / ticksPerMs;
        return new TrajectoryExecutionResult
        {
            Ok = true,
            WaypointsExecuted = executed,
            PlannedDurationMs = Math.Round(plannedDurationMs, 3),
            ActualDurationMs = Math.Round(totalActualMs, 3),
            MaxDriftMs = Math.Round(maxDriftMs, 3),
            CoordinateSpace = normSpace,
            Method = useBackgroundPost ? "app_post" : normMethod,
            ButtonHeld = holdButton
        };
    }

    public static void PerformSecondaryAction(AutomationElement element, string action, int elementIndex)
    {
        string normalized = action.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "invoke":
                var invoke = element.Patterns.Invoke.PatternOrDefault;
                if (invoke != null) { invoke.Invoke(); return; }
                break;
            case "toggle":
                var toggle = element.Patterns.Toggle.PatternOrDefault;
                if (toggle != null) { toggle.Toggle(); return; }
                break;
            case "select":
                var select = element.Patterns.SelectionItem.PatternOrDefault;
                if (select != null) { select.Select(); return; }
                break;
            case "expand":
                var expand = element.Patterns.ExpandCollapse.PatternOrDefault;
                if (expand != null) { expand.Expand(); return; }
                break;
            case "collapse":
                var collapse = element.Patterns.ExpandCollapse.PatternOrDefault;
                if (collapse != null) { collapse.Collapse(); return; }
                break;
            case "scrollintoview":
                var scrollItem = element.Patterns.ScrollItem.PatternOrDefault;
                if (scrollItem != null) { scrollItem.ScrollIntoView(); return; }
                break;
            case "setfocus":
                if (!TestEnvFlagEnabled("OPEN_COMPUTER_USE_WINDOWS_ALLOW_FOCUS_ACTIONS"))
                {
                    throw new InvalidOperationException("SetFocus is disabled by default to avoid stealing user focus; set OPEN_COMPUTER_USE_WINDOWS_ALLOW_FOCUS_ACTIONS=1 to enable it.");
                }
                element.Focus();
                return;
        }

        throw new InvalidOperationException($"{action} is not a valid secondary action for {elementIndex}");
    }

    public static bool InvokeScroll(AutomationElement? element, string direction, double pages)
    {
        if (element == null) return false;
        try
        {
            var scroll = element.Patterns.Scroll.PatternOrDefault;
            if (scroll == null) return false;

            var horizontal = ScrollAmount.NoAmount;
            var vertical = ScrollAmount.NoAmount;

            switch (direction.ToLowerInvariant())
            {
                case "up":
                    vertical = ScrollAmount.LargeDecrement;
                    break;
                case "down":
                    vertical = ScrollAmount.LargeIncrement;
                    break;
                case "left":
                    horizontal = ScrollAmount.LargeDecrement;
                    break;
                case "right":
                    horizontal = ScrollAmount.LargeIncrement;
                    break;
            }

            int repeat = Math.Max(1, (int)Math.Ceiling(pages));
            for (int i = 0; i < repeat; i++)
            {
                scroll.Scroll(horizontal, vertical);
                if (i < repeat - 1)
                {
                    PrecisionTimer.WaitMs(10);
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void SendScroll(IntPtr hwnd, int screenX, int screenY, string direction, double pages)
    {
        // WM_MOUSEWHEEL and WM_MOUSEHWHEEL expect screen coordinates in lParam
        var lParam = NativeMethods.ConvertToLParam(screenX, screenY);

        int delta = (int)Math.Round(120 * pages);
        uint message = NativeMethods.WM_MOUSEWHEEL;

        if (direction.Equals("down", StringComparison.OrdinalIgnoreCase) ||
            direction.Equals("right", StringComparison.OrdinalIgnoreCase))
        {
            delta = -delta;
        }

        if (direction.Equals("left", StringComparison.OrdinalIgnoreCase) ||
            direction.Equals("right", StringComparison.OrdinalIgnoreCase))
        {
            message = NativeMethods.WM_MOUSEHWHEEL;
        }

        IntPtr targetHwnd = hwnd;
        var resolved = NativeMethods.ResolveClickTarget(hwnd, screenX, screenY);
        if (resolved.Ok)
        {
            targetHwnd = resolved.TargetHwnd;
        }

        NativeMethods.SpoofWindowActivation(hwnd, targetHwnd);
        NativeMethods.PostMessage(targetHwnd, message, NativeMethods.ConvertToWheelWParam(delta), lParam);
    }

    public static void SendHardwareScroll(IntPtr hwnd, int screenX, int screenY, string direction, double pages, bool stealth = false)
    {
        bool hasSavedCursor = false;
        NativeMethods.POINT savedCursor = default;
        IntPtr savedFg = IntPtr.Zero;

        if (stealth)
        {
            WaitIfPhysicalMouseDown();
            hasSavedCursor = NativeMethods.GetCursorPos(out savedCursor);
            savedFg = NativeMethods.GetForegroundWindow();
        }

        try
        {
            FocusWindowIfNeeded(hwnd, 6.0);

            NativeMethods.SetCursorPos(screenX, screenY);
            var (normX, normY, moveFlags) = NormalizeScreenPointForSendInput(screenX, screenY);
            int inputSize = Marshal.SizeOf<NativeMethods.INPUT>();

            int wheelDelta = (int)Math.Round(120 * pages);
            bool isHorizontal = direction.Equals("left", StringComparison.OrdinalIgnoreCase) ||
                                direction.Equals("right", StringComparison.OrdinalIgnoreCase);

            if (isHorizontal)
            {
                // MOUSEEVENTF_HWHEEL: positive tilts right, negative tilts left
                if (direction.Equals("left", StringComparison.OrdinalIgnoreCase))
                {
                    wheelDelta = -wheelDelta;
                }
            }
            else
            {
                // MOUSEEVENTF_WHEEL: positive scrolls up/forward, negative scrolls down/backward
                if (direction.Equals("down", StringComparison.OrdinalIgnoreCase))
                {
                    wheelDelta = -wheelDelta;
                }
            }

            uint wheelFlag = isHorizontal ? NativeMethods.MOUSEEVENTF_HWHEEL : NativeMethods.MOUSEEVENTF_WHEEL;
            var moveInput = new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_MOUSE,
                u = new NativeMethods.InputUnion
                {
                    mi = new NativeMethods.MOUSEINPUT
                    {
                        dx = normX,
                        dy = normY,
                        dwFlags = moveFlags
                    }
                }
            };

            var wheelInput = new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_MOUSE,
                u = new NativeMethods.InputUnion
                {
                    mi = new NativeMethods.MOUSEINPUT
                    {
                        dx = normX,
                        dy = normY,
                        mouseData = unchecked((uint)wheelDelta),
                        dwFlags = wheelFlag | (moveFlags & ~NativeMethods.MOUSEEVENTF_MOVE)
                    }
                }
            };

            NativeMethods.SendInput(2, [moveInput, wheelInput], inputSize);
        }
        finally
        {
            if (stealth)
            {
                if (hasSavedCursor)
                {
                    try { NativeMethods.SetCursorPos(savedCursor.X, savedCursor.Y); } catch { }
                }
                RestoreWindowIfNeeded(savedFg, hwnd);
            }
        }
    }

    public static void SendDrag(IntPtr hwnd, int fromX, int fromY, int toX, int toY, int durationMs = 80, HumanizeProfile humanize = HumanizeProfile.None)
    {
        var start = new NativeMethods.POINT { X = fromX, Y = fromY };
        NativeMethods.ScreenToClient(hwnd, ref start);
        var end = new NativeMethods.POINT { X = toX, Y = toY };
        NativeMethods.ScreenToClient(hwnd, ref end);

        IntPtr targetHwnd = hwnd;
        var resolved = NativeMethods.ResolveClickTarget(hwnd, fromX, fromY);
        if (resolved.Ok && resolved.TargetHwnd == hwnd)
        {
            targetHwnd = resolved.TargetHwnd;
        }

        NativeMethods.SpoofWindowActivation(hwnd, targetHwnd);

        var startParam = NativeMethods.ConvertToLParam(start.X, start.Y);
        NativeMethods.PostMessage(targetHwnd, NativeMethods.WM_MOUSEMOVE, IntPtr.Zero, startParam);
        NativeMethods.PostMessage(targetHwnd, NativeMethods.WM_LBUTTONDOWN, new IntPtr(1), startParam);

        if (humanize != HumanizeProfile.None)
        {
            var bgArc = HumanizedPhysics.GenerateAbsoluteSteps(
                start.X,
                start.Y,
                end.X,
                end.Y,
                humanize,
                durationMs > 0 ? durationMs : null,
                monitorBoundsOverride: null);
            long epoch = Stopwatch.GetTimestamp();
            double ticksPerMs = Stopwatch.Frequency / 1000.0;
            for (int i = 0; i < bgArc.Length; i++)
            {
                var (cx, cy, tMs) = bgArc[i];
                if (tMs > 0.0)
                {
                    PrecisionTimer.WaitUntilTimestamp(epoch + (long)Math.Round(tMs * ticksPerMs));
                }
                NativeMethods.PostMessage(targetHwnd, NativeMethods.WM_MOUSEMOVE, new IntPtr(1), NativeMethods.ConvertToLParam(cx, cy));
            }
            NativeMethods.PostMessage(targetHwnd, NativeMethods.WM_LBUTTONUP, IntPtr.Zero, NativeMethods.ConvertToLParam(end.X, end.Y));
            return;
        }

        int steps = durationMs <= 0 ? 2 : Math.Clamp(durationMs / 8, 4, 120);
        double stepDelayMs = durationMs > 0 ? (double)durationMs / steps : 0.0;

        for (int i = 1; i <= steps; i++)
        {
            int x = (int)Math.Round(start.X + ((end.X - start.X) * i / (double)steps));
            int y = (int)Math.Round(start.Y + ((end.Y - start.Y) * i / (double)steps));
            NativeMethods.PostMessage(targetHwnd, NativeMethods.WM_MOUSEMOVE, new IntPtr(1), NativeMethods.ConvertToLParam(x, y));
            if (stepDelayMs > 0)
            {
                PrecisionTimer.WaitMs(stepDelayMs);
            }
        }

        NativeMethods.PostMessage(targetHwnd, NativeMethods.WM_LBUTTONUP, IntPtr.Zero, NativeMethods.ConvertToLParam(end.X, end.Y));
    }

    public static void SendHardwareDrag(IntPtr hwnd, int fromX, int fromY, int toX, int toY, int durationMs = 80, bool stealth = false, HumanizeProfile humanize = HumanizeProfile.None)
    {
        bool hasSavedCursor = false;
        NativeMethods.POINT savedCursor = default;
        IntPtr savedFg = IntPtr.Zero;

        if (stealth)
        {
            WaitIfPhysicalMouseDown();
            hasSavedCursor = NativeMethods.GetCursorPos(out savedCursor);
            savedFg = NativeMethods.GetForegroundWindow();
        }

        try
        {
            FocusWindowIfNeeded(hwnd, 8.0);
            int inputSize = Marshal.SizeOf<NativeMethods.INPUT>();

            NativeMethods.SetCursorPos(fromX, fromY);
            var (startNormX, startNormY, startMoveFlags) = NormalizeScreenPointForSendInput(fromX, fromY);
            uint buttonBaseFlags = startMoveFlags & ~NativeMethods.MOUSEEVENTF_MOVE;

            var startMove = new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_MOUSE,
                u = new NativeMethods.InputUnion
                {
                    mi = new NativeMethods.MOUSEINPUT { dx = startNormX, dy = startNormY, dwFlags = startMoveFlags }
                }
            };
            var startDown = new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_MOUSE,
                u = new NativeMethods.InputUnion
                {
                    mi = new NativeMethods.MOUSEINPUT { dx = startNormX, dy = startNormY, dwFlags = NativeMethods.MOUSEEVENTF_LEFTDOWN | buttonBaseFlags }
                }
            };

            NativeMethods.SendInput(1, [startMove], inputSize);
            PrecisionTimer.WaitMs(6.0);
            NativeMethods.SendInput(1, [startDown], inputSize);
            PrecisionTimer.WaitMs(8.0);

            if (humanize != HumanizeProfile.None)
            {
                var hwArc = HumanizedPhysics.GenerateAbsoluteSteps(
                    fromX,
                    fromY,
                    toX,
                    toY,
                    humanize,
                    durationMs > 0 ? durationMs : null);
                long epoch = Stopwatch.GetTimestamp();
                double ticksPerMs = Stopwatch.Frequency / 1000.0;
                for (int i = 0; i < hwArc.Length; i++)
                {
                    var (wx, wy, tMs) = hwArc[i];
                    if (tMs > 0.0)
                    {
                        PrecisionTimer.WaitUntilTimestamp(epoch + (long)Math.Round(tMs * ticksPerMs));
                    }
                    var (nx, ny, mf) = NormalizeScreenPointForSendInput(wx, wy);
                    var stepMove = new NativeMethods.INPUT
                    {
                        type = NativeMethods.INPUT_MOUSE,
                        u = new NativeMethods.InputUnion
                        {
                            mi = new NativeMethods.MOUSEINPUT { dx = nx, dy = ny, dwFlags = mf }
                        }
                    };
                    NativeMethods.SendInput(1, [stepMove], inputSize);
                }
                NativeMethods.SetCursorPos(toX, toY);
            }
            else
            {
                int steps = durationMs <= 0 ? 4 : Math.Clamp(durationMs / 6, 4, 120);
                double stepDelayMs = durationMs > 0 ? (double)durationMs / steps : 0.0;

                for (int i = 1; i <= steps; i++)
                {
                    int curX = (int)Math.Round(fromX + ((toX - fromX) * i / (double)steps));
                    int curY = (int)Math.Round(fromY + ((toY - fromY) * i / (double)steps));
                    NativeMethods.SetCursorPos(curX, curY);
                    var (nx, ny, mf) = NormalizeScreenPointForSendInput(curX, curY);
                    var stepMove = new NativeMethods.INPUT
                    {
                        type = NativeMethods.INPUT_MOUSE,
                        u = new NativeMethods.InputUnion
                        {
                            mi = new NativeMethods.MOUSEINPUT { dx = nx, dy = ny, dwFlags = mf }
                        }
                    };
                    NativeMethods.SendInput(1, [stepMove], inputSize);
                    if (stepDelayMs > 0)
                    {
                        PrecisionTimer.WaitMs(stepDelayMs);
                    }
                }
            }

            PrecisionTimer.WaitMs(6.0);
            var (endNormX, endNormY, endMoveFlags) = NormalizeScreenPointForSendInput(toX, toY);
            var endUp = new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_MOUSE,
                u = new NativeMethods.InputUnion
                {
                    mi = new NativeMethods.MOUSEINPUT
                    {
                        dx = endNormX,
                        dy = endNormY,
                        dwFlags = NativeMethods.MOUSEEVENTF_LEFTUP | (endMoveFlags & ~NativeMethods.MOUSEEVENTF_MOVE)
                    }
                }
            };
            NativeMethods.SendInput(1, [endUp], inputSize);
        }
        finally
        {
            if (stealth)
            {
                if (hasSavedCursor)
                {
                    try { NativeMethods.SetCursorPos(savedCursor.X, savedCursor.Y); } catch { }
                }
                RestoreWindowIfNeeded(savedFg, hwnd);
            }
        }
    }

    private static NativeMethods.INPUT CreateKeyboardInput(ushort vk, ushort scan, uint flags)
    {
        return new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            u = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = vk,
                    wScan = scan,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            }
        };
    }

    private static NativeMethods.INPUT BuildScanCodeKeyInput(int vk, bool keyUp)
    {
        uint scan = GetScanCode(vk);
        uint flags = NativeMethods.KEYEVENTF_SCANCODE;
        if (IsExtendedKey(vk)) flags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;
        if (keyUp) flags |= NativeMethods.KEYEVENTF_KEYUP;

        ushort sendVk = scan == 0 ? (ushort)vk : (ushort)0;
        if (scan == 0) flags &= ~NativeMethods.KEYEVENTF_SCANCODE;

        return CreateKeyboardInput(sendVk, (ushort)scan, flags);
    }

    private static bool TrySendKeyboardInputsAll(
        NativeMethods.INPUT[] inputs,
        int inputSize,
        bool allowRetry,
        Func<uint, NativeMethods.INPUT[], int, uint>? sendInputOverride,
        out int totalSent)
    {
        int offset = 0;
        int zeroSendRetries = 0;
        while (offset < inputs.Length)
        {
            var slice = offset == 0 ? inputs : inputs[offset..];
            uint sent = sendInputOverride != null
                ? sendInputOverride((uint)slice.Length, slice, inputSize)
                : NativeMethods.SendInput((uint)slice.Length, slice, inputSize);
            if (sent > 0)
            {
                offset += (int)sent;
                zeroSendRetries = 0;
                if (offset >= inputs.Length)
                {
                    totalSent = offset;
                    return true;
                }
                if (!allowRetry)
                {
                    totalSent = offset;
                    return false;
                }
                PrecisionTimer.WaitMs(1.5);
            }
            else
            {
                if (!allowRetry)
                {
                    totalSent = offset;
                    return false;
                }
                zeroSendRetries++;
                if (zeroSendRetries >= 12)
                {
                    totalSent = offset;
                    return false;
                }
                PrecisionTimer.WaitMs(1.5);
            }
        }

        totalSent = offset;
        return true;
    }

    internal static void EnsureChildEditFocusOnColdTransition(IntPtr rootHwnd)
    {
        IntPtr editChild = NativeMethods.FindInputTargetHwnd(rootHwnd);
        if (editChild == IntPtr.Zero || editChild == rootHwnd)
        {
            return;
        }

        var sb = new System.Text.StringBuilder(128);
        NativeMethods.GetClassName(editChild, sb, sb.Capacity);
        if (!NativeMethods.IsEditControlClassName(sb.ToString()))
        {
            return;
        }

        if (!NativeMethods.IsWindowResponsive(editChild, 15))
        {
            return;
        }

        uint editTid = NativeMethods.GetWindowThreadProcessId(editChild, out _);
        if (editTid == 0)
        {
            return;
        }

        for (int poll = 0; poll < 10; poll++)
        {
            var gti = new NativeMethods.GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.GUITHREADINFO>() };
            if (NativeMethods.GetGUIThreadInfo(editTid, ref gti) &&
                gti.hwndFocus != IntPtr.Zero &&
                gti.hwndFocus != rootHwnd)
            {
                return;
            }
            PrecisionTimer.WaitMs(3.0);
        }

        if (!NativeMethods.IsWindowResponsive(editChild, 15))
        {
            return;
        }

        // Execute rare AttachThreadInput + SetFocus fallback on an ephemeral non-ThreadPool thread with a bounded
        // 100ms join so .NET ThreadPool workers never acquire a pumpless Win32 THREADINFO queue or stall ActionLock.
        var focusThread = new Thread(() =>
        {
            uint helperTid = NativeMethods.GetCurrentThreadId();
            bool attached = false;
            try
            {
                if (helperTid != editTid)
                {
                    attached = NativeMethods.AttachThreadInput(helperTid, editTid, true);
                }

                if (attached || helperTid == editTid)
                {
                    NativeMethods.SetFocus(editChild);
                }
            }
            catch
            {
            }
            finally
            {
                if (attached)
                {
                    NativeMethods.AttachThreadInput(helperTid, editTid, false);
                }
            }
        })
        {
            IsBackground = true,
            Name = "TerkirdoColdEditFocusHelper"
        };

        focusThread.Start();
        if (focusThread.Join(100))
        {
            PrecisionTimer.WaitMs(4.0);
        }
    }

    /// <summary>
    /// Dispatches literal text via a unified KEYEVENTF_UNICODE (VK_PACKET) single-character stream
    /// with sub-millisecond QPC pacing (0.65ms), 2.0ms control-key (\n, \t) drain barriers,
    /// UTF-16 surrogate pair atomicity, partial SendInput slice retry, and cold focus settle protection.
    /// </summary>
    public static void SendText(IntPtr hwnd, string text, HumanizeProfile humanize = HumanizeProfile.None)
    {
        SendTextCore(hwnd, text, humanize, sendInputOverride: null, forceForegroundStream: false);
    }

    internal static void SendTextCore(
        IntPtr hwnd,
        string text,
        HumanizeProfile humanize,
        Func<uint, NativeMethods.INPUT[], int, uint>? sendInputOverride,
        bool forceForegroundStream)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (!forceForegroundStream)
        {
            bool wasAlreadyForeground = hwnd == IntPtr.Zero || NativeMethods.GetForegroundWindow() == hwnd;
            FocusWindowIfNeeded(hwnd, wasAlreadyForeground ? 0.0 : 24.0);

            IntPtr fg = NativeMethods.GetForegroundWindow();
            bool isForeground = hwnd == IntPtr.Zero || fg == hwnd;
            if (!isForeground && hwnd != IntPtr.Zero)
            {
                SendTextBackground(hwnd, text, humanize);
                return;
            }

            if (!wasAlreadyForeground && hwnd != IntPtr.Zero)
            {
                EnsureChildEditFocusOnColdTransition(hwnd);
            }
        }

        int inputSize = Marshal.SizeOf<NativeMethods.INPUT>();
        string normalizedText = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);

        uint returnScan = GetScanCode(0x0D);
        uint tabScan = GetScanCode(0x09);
        bool allowRetry = hwnd != IntPtr.Zero;

        int index = 0;
        while (index < normalizedText.Length)
        {
            int charStart = index;
            char c = normalizedText[index++];

            if (c is '\n' or '\t')
            {
                if (charStart > 0)
                {
                    PrecisionTimer.WaitMs(2.0);
                }

                NativeMethods.INPUT[] controlInputs;
                if (c == '\n')
                {
                    controlInputs = returnScan != 0
                        ? [
                            CreateKeyboardInput(0, (ushort)returnScan, NativeMethods.KEYEVENTF_SCANCODE),
                            CreateKeyboardInput(0, (ushort)returnScan, NativeMethods.KEYEVENTF_SCANCODE | NativeMethods.KEYEVENTF_KEYUP)
                        ]
                        : [
                            CreateKeyboardInput(0x0D, 0, 0),
                            CreateKeyboardInput(0x0D, 0, NativeMethods.KEYEVENTF_KEYUP)
                        ];
                }
                else
                {
                    controlInputs = tabScan != 0
                        ? [
                            CreateKeyboardInput(0, (ushort)tabScan, NativeMethods.KEYEVENTF_SCANCODE),
                            CreateKeyboardInput(0, (ushort)tabScan, NativeMethods.KEYEVENTF_SCANCODE | NativeMethods.KEYEVENTF_KEYUP)
                        ]
                        : [
                            CreateKeyboardInput(0x09, 0, 0),
                            CreateKeyboardInput(0x09, 0, NativeMethods.KEYEVENTF_KEYUP)
                        ];
                }

                bool controlSentOk = TrySendKeyboardInputsAll(controlInputs, inputSize, allowRetry, sendInputOverride, out int controlSentCount);
                if (!controlSentOk && hwnd != IntPtr.Zero)
                {
                    int fallbackStart = controlSentCount >= 1 ? index : charStart;
                    if (fallbackStart < normalizedText.Length)
                    {
                        SendTextBackground(hwnd, normalizedText[fallbackStart..], humanize);
                    }
                    return;
                }

                if (index < normalizedText.Length)
                {
                    double postControlDelayMs = humanize == HumanizeProfile.None
                        ? 2.0
                        : HumanizedPhysics.SampleInterKeyDelayMs(humanize, 18.0);
                    PrecisionTimer.WaitMs(postControlDelayMs);
                }
                continue;
            }

            if (char.IsHighSurrogate(c) && index < normalizedText.Length && char.IsLowSurrogate(normalizedText[index]))
            {
                char low = normalizedText[index++];
                NativeMethods.INPUT[] surrogateInputs =
                [
                    CreateKeyboardInput(0, c, NativeMethods.KEYEVENTF_UNICODE),
                    CreateKeyboardInput(0, low, NativeMethods.KEYEVENTF_UNICODE),
                    CreateKeyboardInput(0, c, NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP),
                    CreateKeyboardInput(0, low, NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP)
                ];

                bool surrogateSentOk = TrySendKeyboardInputsAll(surrogateInputs, inputSize, allowRetry, sendInputOverride, out int surrogateSentCount);
                if (!surrogateSentOk && hwnd != IntPtr.Zero)
                {
                    int fallbackStart = surrogateSentCount >= 2 ? index : charStart;
                    if (fallbackStart < normalizedText.Length)
                    {
                        SendTextBackground(hwnd, normalizedText[fallbackStart..], humanize);
                    }
                    return;
                }

                if (index < normalizedText.Length && normalizedText[index] is not ('\n' or '\t'))
                {
                    double delayMs = humanize == HumanizeProfile.None
                        ? 0.65
                        : HumanizedPhysics.SampleInterKeyDelayMs(humanize, 18.0);
                    PrecisionTimer.WaitMs(delayMs);
                }
                continue;
            }

            NativeMethods.INPUT[] bmpInputs =
            [
                CreateKeyboardInput(0, c, NativeMethods.KEYEVENTF_UNICODE),
                CreateKeyboardInput(0, c, NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP)
            ];

            bool bmpSentOk = TrySendKeyboardInputsAll(bmpInputs, inputSize, allowRetry, sendInputOverride, out int bmpSentCount);
            if (!bmpSentOk && hwnd != IntPtr.Zero)
            {
                int fallbackStart = bmpSentCount >= 1 ? index : charStart;
                if (fallbackStart < normalizedText.Length)
                {
                    SendTextBackground(hwnd, normalizedText[fallbackStart..], humanize);
                }
                return;
            }

            if (index < normalizedText.Length && normalizedText[index] is not ('\n' or '\t'))
            {
                double delayMs = humanize == HumanizeProfile.None
                    ? 0.65
                    : HumanizedPhysics.SampleInterKeyDelayMs(humanize, 18.0);
                PrecisionTimer.WaitMs(delayMs);
            }
        }
    }

    public static void SendTextBackground(IntPtr hwnd, string text, HumanizeProfile humanize = HumanizeProfile.None)
    {
        if (hwnd == IntPtr.Zero || string.IsNullOrEmpty(text))
        {
            return;
        }

        if (!NativeMethods.IsWindow(hwnd))
        {
            throw new InvalidOperationException($"Target window handle 0x{hwnd.ToInt64():X} is no longer valid.");
        }

        IntPtr targetHwnd = NativeMethods.FindInputTargetHwnd(hwnd);
        if (targetHwnd == IntPtr.Zero || !NativeMethods.IsWindow(targetHwnd))
        {
            throw new InvalidOperationException("Target input window handle is invalid.");
        }

        NativeMethods.SpoofWindowActivation(hwnd, targetHwnd);
        PrecisionTimer.WaitMs(2.0);

        var targetClsSb = new System.Text.StringBuilder(128);
        NativeMethods.GetClassName(targetHwnd, targetClsSb, targetClsSb.Capacity);
        bool isRichEditTarget = targetClsSb.ToString().Contains("RichEdit", StringComparison.OrdinalIgnoreCase);

        string normalizedLf = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);

        for (int idx = 0; idx < normalizedLf.Length; idx++)
        {
            char c = normalizedLf[idx];
            char sendChar = c == '\n' ? '\r' : c;

            if (c == '\n' && isRichEditTarget)
            {
                IntPtr downLParam = NativeMethods.BuildKeyDownLParam(0x0D);
                IntPtr upLParam = NativeMethods.BuildKeyUpLParam(0x0D);

                if (!PostMessageWithRetry(targetHwnd, NativeMethods.WM_KEYDOWN, new IntPtr(0x0D), downLParam) ||
                    !PostMessageWithRetry(targetHwnd, NativeMethods.WM_KEYUP, new IntPtr(0x0D), upLParam))
                {
                    throw new InvalidOperationException("Target window message queue rejected background character input (PostMessage VK_RETURN failed).");
                }
            }
            else
            {
                short vks = NativeMethods.VkKeyScan(sendChar);
                IntPtr lParam = vks != -1
                    ? NativeMethods.BuildKeyDownLParam(vks & 0xFF)
                    : new IntPtr(1);

                if (!PostMessageWithRetry(targetHwnd, NativeMethods.WM_CHAR, new IntPtr((int)sendChar), lParam))
                {
                    throw new InvalidOperationException("Target window message queue rejected background character input (PostMessage WM_CHAR failed).");
                }
            }

            if (idx < normalizedLf.Length - 1)
            {
                if (humanize != HumanizeProfile.None)
                {
                    PrecisionTimer.WaitMs(HumanizedPhysics.SampleInterKeyDelayMs(humanize, 14.0));
                }
                else if (sendChar == '\r')
                {
                    PrecisionTimer.WaitMs(1.5);
                }
                else if (((idx + 1) & 7) == 0)
                {
                    PrecisionTimer.WaitMs(1.0);
                }
            }
        }
    }

    private static bool PostMessageWithRetry(IntPtr targetHwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            if (NativeMethods.PostMessage(targetHwnd, msg, wParam, lParam))
            {
                return true;
            }
            PrecisionTimer.WaitMs(2.0);
        }
        return false;
    }

    public static void SendTextStealth(IntPtr hwnd, string text, HumanizeProfile humanize = HumanizeProfile.None)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        WaitIfPhysicalMouseDown();
        IntPtr savedFg = NativeMethods.GetForegroundWindow();

        try
        {
            SendText(hwnd, text, humanize);
            if (hwnd != IntPtr.Zero && savedFg != IntPtr.Zero && savedFg != hwnd)
            {
                PrecisionTimer.WaitMs(12.0);
            }
        }
        finally
        {
            RestoreWindowIfNeeded(savedFg, hwnd);
        }
    }

    public static bool SendTextToEditHandle(IntPtr hwnd, string text, AutomationElement? element)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd) || !NativeMethods.IsWindowResponsive(hwnd, 50))
        {
            return false;
        }

        string crlfText = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Replace("\n", "\r\n", StringComparison.Ordinal);

        try
        {
            IntPtr replaceOk = NativeMethods.SendMessageTimeout(
                hwnd,
                NativeMethods.EM_REPLACESEL,
                new IntPtr(1),
                crlfText,
                NativeMethods.SMTO_ABORTIFHUNG,
                500,
                out _);
            return replaceOk != IntPtr.Zero;
        }
        catch
        {
            // Try WM_SETTEXT fallback only if SendMessageTimeout threw
        }

        try
        {
            string current = "";
            if (element != null)
            {
                var valPattern = element.Patterns.Value.PatternOrDefault;
                if (valPattern != null) current = valPattern.Value.Value ?? "";
            }
            IntPtr setTextOk = NativeMethods.SendMessageTimeout(
                hwnd,
                NativeMethods.WM_SETTEXT,
                IntPtr.Zero,
                current + crlfText,
                NativeMethods.SMTO_ABORTIFHUNG,
                500,
                out _);
            return setTextOk != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
    }

    public static int GetVirtualKey(string key)
    {
        string normalized = key.ToLowerInvariant().Trim();
        var map = new Dictionary<string, int>
        {
            // Modifiers
            ["ctrl"] = 0x11, ["control"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12, ["menu"] = 0x12,
            ["super"] = 0x5B, ["win"] = 0x5B, ["cmd"] = 0x5B, ["lwin"] = 0x5B, ["rwin"] = 0x5C, ["apps"] = 0x5D,

            // Control / Editing
            ["return"] = 0x0D, ["enter"] = 0x0D, ["tab"] = 0x09, ["escape"] = 0x1B, ["esc"] = 0x1B,
            ["backspace"] = 0x08, ["back_space"] = 0x08, ["back"] = 0x08,
            ["delete"] = 0x2E, ["del"] = 0x2E, ["insert"] = 0x2D, ["ins"] = 0x2D,
            ["space"] = 0x20, ["spacebar"] = 0x20,

            // Navigation
            ["left"] = 0x25, ["arrowleft"] = 0x25, ["arrow_left"] = 0x25,
            ["up"] = 0x26, ["arrowup"] = 0x26, ["arrow_up"] = 0x26,
            ["right"] = 0x27, ["arrowright"] = 0x27, ["arrow_right"] = 0x27,
            ["down"] = 0x28, ["arrowdown"] = 0x28, ["arrow_down"] = 0x28,
            ["home"] = 0x24, ["end"] = 0x23,
            ["page_up"] = 0x21, ["pageup"] = 0x21, ["pgup"] = 0x21, ["prior"] = 0x21,
            ["page_down"] = 0x22, ["pagedown"] = 0x22, ["pgdn"] = 0x22, ["next"] = 0x22,

            // Locks & System
            ["capslock"] = 0x14, ["caps_lock"] = 0x14, ["caps"] = 0x14,
            ["numlock"] = 0x90, ["num_lock"] = 0x90,
            ["scrolllock"] = 0x91, ["scroll_lock"] = 0x91, ["scroll"] = 0x91,
            ["printscreen"] = 0x2C, ["print_screen"] = 0x2C, ["prtscn"] = 0x2C, ["snapshot"] = 0x2C,
            ["pause"] = 0x13, ["break"] = 0x13,

            // OEM & Punctuation
            ["plus"] = 0xBB, ["+"] = 0xBB,
            ["minus"] = 0xBD, ["-"] = 0xBD,
            ["comma"] = 0xBC, [","] = 0xBC,
            ["period"] = 0xBE, ["."] = 0xBE,
            ["slash"] = 0xBF, ["/"] = 0xBF,
            ["backslash"] = 0xDC, ["\\"] = 0xDC,
            ["semicolon"] = 0xBA, [";"] = 0xBA,
            ["quote"] = 0xDE, ["'"] = 0xDE,
            ["backtick"] = 0xC0, ["`"] = 0xC0, ["tilde"] = 0xC0,
            ["openbracket"] = 0xDB, ["["] = 0xDB,
            ["closebracket"] = 0xDD, ["]"] = 0xDD,
            ["equal"] = 0xBB, ["="] = 0xBB,

            // Numpad
            ["kp_add"] = 0x6B, ["kp_subtract"] = 0x6D, ["kp_multiply"] = 0x6A,
            ["kp_divide"] = 0x6F, ["kp_decimal"] = 0x6E, ["kp_enter"] = 0x0D
        };

        if (map.TryGetValue(normalized, out int mapped))
        {
            return mapped;
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(normalized, @"^f([1-9]|1[0-9]|2[0-4])$"))
        {
            int fNum = int.Parse(normalized[1..]);
            return 0x70 + fNum - 1;
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(normalized, @"^kp_([0-9])$"))
        {
            int kpNum = int.Parse(normalized[3..]);
            return 0x60 + kpNum;
        }

        if (normalized.Length == 1)
        {
            char c = char.ToUpperInvariant(normalized[0]);
            if ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z'))
            {
                return (int)c;
            }
        }

        throw new ArgumentException($"Unsupported key: {key}");
    }

    public static bool IsExtendedKey(int vk)
    {
        return vk switch
        {
            0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E => true,
            0x5B or 0x5C or 0x5D => true,
            0x6F => true,
            0x90 => true,
            0xA3 or 0xA5 => true,
            0x2C => true,
            _ => false
        };
    }

    public static uint GetScanCode(int vk)
    {
        return NativeMethods.MapVirtualKey((uint)vk, NativeMethods.MAPVK_VK_TO_VSC);
    }

    public static (List<int> Modifiers, int MainVk) ParseKeyChord(string key)
    {
        string trimmed = key.Trim();
        string mainKey;
        var modifierNames = new List<string>();

        if (trimmed == "+" || trimmed == "++")
        {
            mainKey = "+";
        }
        else if (trimmed.EndsWith("++", StringComparison.Ordinal))
        {
            mainKey = "+";
            var rawMods = trimmed[..^2].Split('+', StringSplitOptions.RemoveEmptyEntries);
            modifierNames.AddRange(rawMods);
        }
        else if (trimmed.EndsWith("+", StringComparison.Ordinal) && trimmed.Length > 1)
        {
            mainKey = "+";
            var rawMods = trimmed[..^1].Split('+', StringSplitOptions.RemoveEmptyEntries);
            modifierNames.AddRange(rawMods);
        }
        else
        {
            var parts = trimmed.Split('+');
            mainKey = parts[^1];
            for (int i = 0; i < parts.Length - 1; i++)
            {
                modifierNames.Add(parts[i]);
            }
        }

        var modifiers = new List<int>();
        foreach (var modName in modifierNames)
        {
            switch (modName.ToLowerInvariant().Trim())
            {
                case "ctrl":
                case "control":
                    modifiers.Add(0x11);
                    break;
                case "shift":
                    modifiers.Add(0x10);
                    break;
                case "alt":
                case "menu":
                    modifiers.Add(0x12);
                    break;
                case "super":
                case "win":
                case "cmd":
                case "lwin":
                    modifiers.Add(0x5B);
                    break;
                case "rwin":
                    modifiers.Add(0x5C);
                    break;
            }
        }

        int mainVk = GetVirtualKey(mainKey);
        return (modifiers, mainVk);
    }

    public static string NormalizeKeyState(string? keyState)
    {
        if (string.IsNullOrWhiteSpace(keyState))
        {
            return "tap";
        }

        string norm = keyState.Trim().ToLowerInvariant();
        return norm switch
        {
            "tap" or "down" or "up" => norm,
            _ => throw new ArgumentException($"Invalid key_state '{keyState}'. Valid options are: tap, down, up.")
        };
    }

    public static void SendKeyBackground(IntPtr hwnd, string key, string keyState = "tap", int? holdMs = null)
    {
        var (modifiers, mainVk) = ParseKeyChord(key);
        string normState = NormalizeKeyState(keyState);
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        IntPtr targetHwnd = NativeMethods.FindInputTargetHwnd(hwnd);
        NativeMethods.SpoofWindowActivation(hwnd, targetHwnd);

        if (normState == "down")
        {
            foreach (int modVk in modifiers)
            {
                NativeMethods.PostMessage(
                    targetHwnd,
                    NativeMethods.WM_KEYDOWN,
                    new IntPtr(modVk),
                    NativeMethods.BuildKeyDownLParam(modVk, IsExtendedKey(modVk)));
                ActiveInputTracker.RegisterBackgroundKey(hwnd, targetHwnd, modVk, holdMs);
            }

            NativeMethods.PostMessage(
                targetHwnd,
                NativeMethods.WM_KEYDOWN,
                new IntPtr(mainVk),
                NativeMethods.BuildKeyDownLParam(mainVk, IsExtendedKey(mainVk)));
            ActiveInputTracker.RegisterBackgroundKey(hwnd, targetHwnd, mainVk, holdMs, key);
            return;
        }

        if (normState == "up")
        {
            IntPtr resolvedMainTarget = ActiveInputTracker.UnregisterKeyAndResolveTarget(false, hwnd, targetHwnd, mainVk);
            if (resolvedMainTarget != IntPtr.Zero)
            {
                NativeMethods.PostMessage(
                    resolvedMainTarget,
                    NativeMethods.WM_KEYUP,
                    new IntPtr(mainVk),
                    NativeMethods.BuildKeyUpLParam(mainVk, IsExtendedKey(mainVk)));
            }

            for (int i = modifiers.Count - 1; i >= 0; i--)
            {
                int modVk = modifiers[i];
                IntPtr resolvedModTarget = ActiveInputTracker.UnregisterKeyAndResolveTarget(false, hwnd, targetHwnd, modVk);
                if (resolvedModTarget != IntPtr.Zero)
                {
                    NativeMethods.PostMessage(
                        resolvedModTarget,
                        NativeMethods.WM_KEYUP,
                        new IntPtr(modVk),
                        NativeMethods.BuildKeyUpLParam(modVk, IsExtendedKey(modVk)));
                }
            }
            return;
        }

        // Default "tap": PostMessage is FIFO — zero sleep unless caller explicitly requested hold_ms > 0
        foreach (int modVk in modifiers)
        {
            NativeMethods.PostMessage(
                targetHwnd,
                NativeMethods.WM_KEYDOWN,
                new IntPtr(modVk),
                NativeMethods.BuildKeyDownLParam(modVk, IsExtendedKey(modVk)));
        }

        NativeMethods.PostMessage(
            targetHwnd,
            NativeMethods.WM_KEYDOWN,
            new IntPtr(mainVk),
            NativeMethods.BuildKeyDownLParam(mainVk, IsExtendedKey(mainVk)));

        if (holdMs is > 0)
        {
            PrecisionTimer.WaitMs(holdMs.Value);
        }

        NativeMethods.PostMessage(
            targetHwnd,
            NativeMethods.WM_KEYUP,
            new IntPtr(mainVk),
            NativeMethods.BuildKeyUpLParam(mainVk, IsExtendedKey(mainVk)));

        for (int i = modifiers.Count - 1; i >= 0; i--)
        {
            int modVk = modifiers[i];
            NativeMethods.PostMessage(
                targetHwnd,
                NativeMethods.WM_KEYUP,
                new IntPtr(modVk),
                NativeMethods.BuildKeyUpLParam(modVk, IsExtendedKey(modVk)));
        }
    }

    public static void SendKeyStealth(IntPtr hwnd, string key, string keyState = "tap", int? holdMs = null)
    {
        _ = ParseKeyChord(key);
        _ = NormalizeKeyState(keyState);
        WaitIfPhysicalMouseDown();
        IntPtr savedFg = NativeMethods.GetForegroundWindow();

        try
        {
            SendKey(hwnd, key, keyState, holdMs, isHardwareMode: true);
        }
        finally
        {
            RestoreWindowIfNeeded(savedFg, hwnd);
        }
    }

    public static void SendKey(
        IntPtr hwnd,
        string key,
        string keyState = "tap",
        int? holdMs = null,
        bool isHardwareMode = true)
    {
        var (modifiers, mainVk) = ParseKeyChord(key);
        string normState = NormalizeKeyState(keyState);
        int inputSize = Marshal.SizeOf<NativeMethods.INPUT>();

        FocusWindowIfNeeded(hwnd, 8.0);

        IntPtr fg = NativeMethods.GetForegroundWindow();
        bool isTargetForeground = hwnd == IntPtr.Zero || fg == hwnd;
        IntPtr targetHwnd = (!isTargetForeground && hwnd != IntPtr.Zero) ? NativeMethods.FindInputTargetHwnd(hwnd) : IntPtr.Zero;
        IntPtr ResolveFallbackTarget() => targetHwnd != IntPtr.Zero ? targetHwnd : (targetHwnd = NativeMethods.FindInputTargetHwnd(hwnd));

        if (normState == "down")
        {
            var downBatch = new NativeMethods.INPUT[modifiers.Count + 1];
            for (int i = 0; i < modifiers.Count; i++)
            {
                downBatch[i] = BuildScanCodeKeyInput(modifiers[i], keyUp: false);
            }
            downBatch[modifiers.Count] = BuildScanCodeKeyInput(mainVk, keyUp: false);

            uint downSent = NativeMethods.SendInput((uint)downBatch.Length, downBatch, inputSize);
            if (downSent > 0)
            {
                foreach (int modVk in modifiers)
                {
                    ActiveInputTracker.RegisterHardwareKey(modVk, holdMs);
                }
                ActiveInputTracker.RegisterHardwareKey(mainVk, holdMs, key);
            }

            if (hwnd != IntPtr.Zero && (!isTargetForeground || downSent == 0))
            {
                IntPtr fbTarget = ResolveFallbackTarget();
                NativeMethods.SpoofWindowActivation(hwnd, fbTarget);
                foreach (int modVk in modifiers)
                {
                    NativeMethods.PostMessage(
                        fbTarget,
                        NativeMethods.WM_KEYDOWN,
                        new IntPtr(modVk),
                        NativeMethods.BuildKeyDownLParam(modVk, IsExtendedKey(modVk)));
                    ActiveInputTracker.RegisterBackgroundKey(hwnd, fbTarget, modVk, holdMs);
                }
                NativeMethods.PostMessage(
                    fbTarget,
                    NativeMethods.WM_KEYDOWN,
                    new IntPtr(mainVk),
                    NativeMethods.BuildKeyDownLParam(mainVk, IsExtendedKey(mainVk)));
                ActiveInputTracker.RegisterBackgroundKey(hwnd, fbTarget, mainVk, holdMs, key);
            }
            return;
        }

        if (normState == "up")
        {
            var upBatch = new NativeMethods.INPUT[1 + modifiers.Count];
            upBatch[0] = BuildScanCodeKeyInput(mainVk, keyUp: true);
            ActiveInputTracker.UnregisterKeyAndResolveTarget(true, hwnd, IntPtr.Zero, mainVk);

            for (int i = 0; i < modifiers.Count; i++)
            {
                int modVk = modifiers[modifiers.Count - 1 - i];
                upBatch[i + 1] = BuildScanCodeKeyInput(modVk, keyUp: true);
                ActiveInputTracker.UnregisterKeyAndResolveTarget(true, hwnd, IntPtr.Zero, modVk);
            }

            uint upSent = NativeMethods.SendInput((uint)upBatch.Length, upBatch, inputSize);
            if (hwnd != IntPtr.Zero && (!isTargetForeground || upSent == 0))
            {
                IntPtr fbTarget = ResolveFallbackTarget();
                IntPtr resolvedMain = ActiveInputTracker.UnregisterKeyAndResolveTarget(false, hwnd, fbTarget, mainVk);
                if (resolvedMain != IntPtr.Zero)
                {
                    NativeMethods.PostMessage(
                        resolvedMain,
                        NativeMethods.WM_KEYUP,
                        new IntPtr(mainVk),
                        NativeMethods.BuildKeyUpLParam(mainVk, IsExtendedKey(mainVk)));
                }

                for (int i = modifiers.Count - 1; i >= 0; i--)
                {
                    int modVk = modifiers[i];
                    IntPtr resolvedMod = ActiveInputTracker.UnregisterKeyAndResolveTarget(false, hwnd, fbTarget, modVk);
                    if (resolvedMod != IntPtr.Zero)
                    {
                        NativeMethods.PostMessage(
                            resolvedMod,
                            NativeMethods.WM_KEYUP,
                            new IntPtr(modVk),
                            NativeMethods.BuildKeyUpLParam(modVk, IsExtendedKey(modVk)));
                    }
                }
            }
            return;
        }

        // Single-array chord KeyDown: [modifiersDown..., mainKeyDown] in 1 SendInput call (zero sleep between modifiers)
        var chordDown = new NativeMethods.INPUT[modifiers.Count + 1];
        for (int i = 0; i < modifiers.Count; i++)
        {
            chordDown[i] = BuildScanCodeKeyInput(modifiers[i], keyUp: false);
        }
        chordDown[modifiers.Count] = BuildScanCodeKeyInput(mainVk, keyUp: false);

        uint chordDownSent = NativeMethods.SendInput((uint)chordDown.Length, chordDown, inputSize);

        if (hwnd != IntPtr.Zero && (!isTargetForeground || chordDownSent == 0))
        {
            IntPtr fbTarget = ResolveFallbackTarget();
            NativeMethods.SpoofWindowActivation(hwnd, fbTarget);
            foreach (int modifier in modifiers)
            {
                NativeMethods.PostMessage(
                    fbTarget,
                    NativeMethods.WM_KEYDOWN,
                    new IntPtr(modifier),
                    NativeMethods.BuildKeyDownLParam(modifier, IsExtendedKey(modifier)));
            }
            NativeMethods.PostMessage(
                fbTarget,
                NativeMethods.WM_KEYDOWN,
                new IntPtr(mainVk),
                NativeMethods.BuildKeyDownLParam(mainVk, IsExtendedKey(mainVk)));
        }

        double effectiveHoldMs = holdMs ?? (isHardwareMode ? 18.0 : 4.0);
        if (effectiveHoldMs > 0)
        {
            PrecisionTimer.WaitMs(effectiveHoldMs);
        }

        // Single-array chord KeyUp: [mainKeyUp, reverseModifiersUp...] in 1 SendInput call
        var chordUp = new NativeMethods.INPUT[1 + modifiers.Count];
        chordUp[0] = BuildScanCodeKeyInput(mainVk, keyUp: true);
        for (int i = 0; i < modifiers.Count; i++)
        {
            int modVk = modifiers[modifiers.Count - 1 - i];
            chordUp[i + 1] = BuildScanCodeKeyInput(modVk, keyUp: true);
        }

        uint chordUpSent = NativeMethods.SendInput((uint)chordUp.Length, chordUp, inputSize);

        if (hwnd != IntPtr.Zero && (!isTargetForeground || chordUpSent == 0))
        {
            IntPtr fbTarget = ResolveFallbackTarget();
            NativeMethods.PostMessage(
                fbTarget,
                NativeMethods.WM_KEYUP,
                new IntPtr(mainVk),
                NativeMethods.BuildKeyUpLParam(mainVk, IsExtendedKey(mainVk)));

            for (int i = modifiers.Count - 1; i >= 0; i--)
            {
                int modVk = modifiers[i];
                NativeMethods.PostMessage(
                    fbTarget,
                    NativeMethods.WM_KEYUP,
                    new IntPtr(modVk),
                    NativeMethods.BuildKeyUpLParam(modVk, IsExtendedKey(modVk)));
            }
        }
    }

    public static void ExecutePressKey(
        IntPtr hwnd,
        string key,
        string? keyMethod,
        string? keyState = null,
        int? holdMs = null,
        int repeat = 1,
        int repeatIntervalMs = 15,
        HumanizeProfile humanize = HumanizeProfile.None)
    {
        ValidateKeyMethod(keyMethod);
        string normMethod = string.IsNullOrWhiteSpace(keyMethod) ? "auto" : keyMethod.Trim().ToLowerInvariant();
        string normState = NormalizeKeyState(keyState);

        if (holdMs.HasValue && (holdMs.Value < 0 || holdMs.Value > 60_000))
        {
            throw new ArgumentException("hold_ms must be between 0 and 60000.");
        }

        if (repeat < 1 || repeat > 100)
        {
            throw new ArgumentException("repeat must be between 1 and 100.");
        }

        if (repeatIntervalMs < 0 || repeatIntervalMs > 5000)
        {
            throw new ArgumentException("repeat_interval_ms must be between 0 and 5000.");
        }

        if (repeat > 1 && normState != "tap")
        {
            throw new ArgumentException("repeat > 1 is only supported when key_state is 'tap'.");
        }

        bool isHw = normMethod is "global" or "hardware" or "stealth";
        for (int i = 0; i < repeat; i++)
        {
            int? iterHoldMs = holdMs;
            if (humanize != HumanizeProfile.None && normState == "tap")
            {
                iterHoldMs = (int)Math.Round(HumanizedPhysics.SampleHoldDurationMs(humanize, holdMs, isHw));
            }

            if (normMethod is "global" or "hardware")
            {
                SendKey(hwnd, key, normState, iterHoldMs, isHardwareMode: true);
            }
            else if (normMethod == "stealth")
            {
                SendKeyStealth(hwnd, key, normState, iterHoldMs);
            }
            else
            {
                SendKeyBackground(hwnd, key, normState, iterHoldMs);
            }

            if (i < repeat - 1 && repeatIntervalMs > 0)
            {
                double waitInterval = humanize == HumanizeProfile.None
                    ? repeatIntervalMs
                    : HumanizedPhysics.SampleInterKeyDelayMs(humanize, repeatIntervalMs);
                PrecisionTimer.WaitMs(waitInterval);
            }
        }
    }

    public static void ValidateKeyMethod(string? keyMethod)
    {
        if (string.IsNullOrWhiteSpace(keyMethod))
        {
            return;
        }

        string norm = keyMethod.Trim().ToLowerInvariant();
        if (norm == "sky_key")
        {
            throw new NotSupportedException("key_method 'sky_key' is not supported on Windows");
        }

        if (norm is not ("auto" or "app_post" or "stealth" or "global" or "hardware"))
        {
            throw new ArgumentException($"Invalid key_method '{keyMethod}'. Valid options are: auto, app_post, stealth, global, hardware.");
        }
    }

    public static void SetValue(AutomationElement? element, string value, int elementIndex)
    {
        if (element == null)
        {
            throw new InvalidOperationException($"unknown element_index '{elementIndex}'");
        }

        var valPattern = element.Patterns.Value.PatternOrDefault;
        if (valPattern == null)
        {
            throw new InvalidOperationException("Cannot set a value for an element that is not settable");
        }

        try
        {
            if (valPattern.IsReadOnly.IsSupported && valPattern.IsReadOnly.ValueOrDefault)
            {
                throw new InvalidOperationException("Cannot set a value on a read-only element");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch
        {
            // Ignore property access failure and proceed to SetValue
        }

        valPattern.SetValue(value);

        var deadline = DateTime.UtcNow.AddMilliseconds(600);
        do
        {
            string? actual = TryReadElementValue(element);
            if (actual != null && string.Equals(actual, value, StringComparison.Ordinal))
            {
                return;
            }
            PrecisionTimer.WaitMs(20);
        } while (DateTime.UtcNow < deadline);

        string? finalActual = TryReadElementValue(element);
        if (finalActual != null && string.Equals(finalActual, value, StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            "set_value could not verify the requested value. Read the current value before retrying; the write may have changed the control.");
    }

    private static string? TryReadElementValue(AutomationElement element)
    {
        try
        {
            var valPattern = element.Patterns.Value.PatternOrDefault;
            if (valPattern != null && valPattern.Value.IsSupported)
            {
                return valPattern.Value.ValueOrDefault ?? string.Empty;
            }
        }
        catch { }

        try
        {
            var textPattern = element.Patterns.Text.PatternOrDefault;
            if (textPattern?.DocumentRange != null)
            {
                return textPattern.DocumentRange.GetText(-1);
            }
        }
        catch { }

        try
        {
            var rangePattern = element.Patterns.RangeValue.PatternOrDefault;
            if (rangePattern != null && rangePattern.Value.IsSupported)
            {
                return Convert.ToString(rangePattern.Value.ValueOrDefault, System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        catch { }

        return null;
    }

    public static void SelectText(
        AutomationElement? element,
        IntPtr windowHwnd,
        ElementRecord? record,
        string text,
        string? prefix,
        string? suffix,
        string? selectionMode,
        int elementIndex)
    {
        if (element == null && record == null)
        {
            throw new InvalidOperationException($"unknown element_index '{elementIndex}'");
        }

        var mode = TextSelectionModeExtensions.Parse(selectionMode);

        string? source = null;
        if (element != null)
        {
            source = TryReadElementValue(element);
            if (source == null)
            {
                try { source = element.Name; } catch { }
            }
        }
        source ??= record?.Value ?? record?.Name ?? string.Empty;

        var resolved = TextSelectionResolver.Resolve(source, text, prefix, suffix, mode, elementIndex);

        bool applied = false;

        // 1. Try UIA TextPattern range selection
        if (element != null)
        {
            try
            {
                var textPattern = element.Patterns.Text.PatternOrDefault;
                if (textPattern?.DocumentRange != null)
                {
                    var docRange = textPattern.DocumentRange;
                    var selRange = docRange.Clone();
                    selRange.MoveEndpointByRange(TextPatternRangeEndpoint.End, docRange, TextPatternRangeEndpoint.Start);
                    if (resolved.Location > 0)
                    {
                        selRange.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, resolved.Location);
                        selRange.MoveEndpointByRange(TextPatternRangeEndpoint.End, selRange, TextPatternRangeEndpoint.Start);
                    }
                    if (resolved.Length > 0)
                    {
                        selRange.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, resolved.Length);
                    }
                    selRange.Select();
                    applied = true;
                }
            }
            catch
            {
                // Fallback to Win32 EM_SETSEL
            }
        }

        // 2. Try Win32 EM_SETSEL on element's native window handle or resolved child control
        IntPtr editHwnd = IntPtr.Zero;
        if (record?.NativeWindowHandle is > 0)
        {
            editHwnd = new IntPtr(record.NativeWindowHandle.Value);
        }
        else if (element != null)
        {
            try
            {
                if (element.Properties.NativeWindowHandle.IsSupported)
                {
                    editHwnd = element.Properties.NativeWindowHandle.ValueOrDefault;
                }
            }
            catch { }
        }

        if (editHwnd == IntPtr.Zero && record?.Frame != null && windowHwnd != IntPtr.Zero)
        {
            int cx = (int)Math.Round(record.Frame.X + (record.Frame.Width / 2.0));
            int cy = (int)Math.Round(record.Frame.Y + (record.Frame.Height / 2.0));
            if (NativeMethods.GetWindowRect(windowHwnd, out var wr))
            {
                var hit = NativeMethods.ResolveClickTarget(windowHwnd, wr.Left + cx, wr.Top + cy);
                if (hit.Ok)
                {
                    editHwnd = hit.TargetHwnd;
                }
            }
        }

        if (editHwnd != IntPtr.Zero && NativeMethods.IsWindow(editHwnd))
        {
            try
            {
                NativeMethods.SendMessage(
                    editHwnd,
                    NativeMethods.EM_SETSEL,
                    new IntPtr(resolved.Location),
                    new IntPtr(resolved.Location + resolved.Length));
                applied = true;
            }
            catch { }
        }

        if (!applied)
        {
            throw new InvalidOperationException($"Element [{elementIndex}] does not support text selection.");
        }
    }
}
