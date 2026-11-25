using System.Runtime.InteropServices;
using TerkirdoComputer.Core;

namespace TerkirdoComputer.Windows;

/// <summary>
/// Thread-safe registry tracking stateful keyboard holds (key_state="down") and mouse button holds (button_state="down")
/// across both hardware SendInput and background PostMessage modes.
/// Automatically releases expired holds via a 500ms watchdog timer and guarantees panic-release on ProcessExit or batch error.
/// </summary>
public static class ActiveInputTracker
{
    public const int DefaultMaxHoldMs = 30_000;
    public const int AbsoluteMaxHoldMs = 300_000;
    private const int WatchdogIntervalMs = 500;

    private static readonly int[] WatchedModifierVks = [0x10, 0x11, 0x12, 0x5B, 0x5C];

    private readonly record struct KeyRegistryKey(bool IsHardware, IntPtr TargetHwnd, int Vk);
    private readonly record struct MouseRegistryKey(bool IsHardware, IntPtr TargetHwnd, string Button);

    private sealed record HeldKeyEntry(
        bool IsHardware,
        IntPtr RootHwnd,
        IntPtr TargetHwnd,
        int Vk,
        string KeyLabel,
        DateTime DeadlineUtc);

    private sealed record HeldMouseEntry(
        bool IsHardware,
        IntPtr RootHwnd,
        IntPtr TargetHwnd,
        string Button,
        int ScreenX,
        int ScreenY,
        IntPtr ClientLParam,
        DateTime DeadlineUtc);

    private static readonly object SyncRoot = new();
    private static readonly Dictionary<KeyRegistryKey, HeldKeyEntry> HeldKeys = new();
    private static readonly Dictionary<MouseRegistryKey, HeldMouseEntry> HeldMouseButtons = new();
    private static readonly Timer WatchdogTimer;
    private static bool _timerActive;

    static ActiveInputTracker()
    {
        WatchdogTimer = new Timer(static _ => ReleaseExpiredSafe(), null, Timeout.Infinite, Timeout.Infinite);
        AppDomain.CurrentDomain.ProcessExit += static (_, _) =>
        {
            try
            {
                _ = ReleaseAll();
            }
            catch
            {
                // Ignore during process exit
            }
        };
    }

    public static int ActiveKeyCount
    {
        get
        {
            lock (SyncRoot)
            {
                return HeldKeys.Count;
            }
        }
    }

    public static int ActiveMouseButtonCount
    {
        get
        {
            lock (SyncRoot)
            {
                return HeldMouseButtons.Count;
            }
        }
    }

    public static int ActiveCount
    {
        get
        {
            lock (SyncRoot)
            {
                return HeldKeys.Count + HeldMouseButtons.Count;
            }
        }
    }

    public static int NormalizeMaxHoldMs(int? requestedHoldMs)
    {
        int effective = requestedHoldMs is > 0 ? requestedHoldMs.Value : DefaultMaxHoldMs;
        return Math.Clamp(effective, 1, AbsoluteMaxHoldMs);
    }

    public static void RegisterHardwareKey(int vk, int? maxHoldMs = null, string? label = null)
    {
        int clampedMs = NormalizeMaxHoldMs(maxHoldMs);
        var key = new KeyRegistryKey(true, IntPtr.Zero, vk);
        var entry = new HeldKeyEntry(
            true,
            IntPtr.Zero,
            IntPtr.Zero,
            vk,
            string.IsNullOrWhiteSpace(label) ? FormatVkName(vk) : label.Trim(),
            DateTime.UtcNow.AddMilliseconds(clampedMs));

        lock (SyncRoot)
        {
            HeldKeys[key] = entry;
            EnsureWatchdogRunningLocked();
        }
    }

    public static void RegisterBackgroundKey(IntPtr rootHwnd, IntPtr targetHwnd, int vk, int? maxHoldMs = null, string? label = null)
    {
        if (targetHwnd == IntPtr.Zero)
        {
            return;
        }

        int clampedMs = NormalizeMaxHoldMs(maxHoldMs);
        var key = new KeyRegistryKey(false, targetHwnd, vk);
        var entry = new HeldKeyEntry(
            false,
            rootHwnd,
            targetHwnd,
            vk,
            string.IsNullOrWhiteSpace(label) ? FormatVkName(vk) : label.Trim(),
            DateTime.UtcNow.AddMilliseconds(clampedMs));

        lock (SyncRoot)
        {
            HeldKeys[key] = entry;
            EnsureWatchdogRunningLocked();
        }
    }

    public static IntPtr UnregisterKeyAndResolveTarget(bool isHardware, IntPtr rootHwnd, IntPtr fallbackTargetHwnd, int vk)
    {
        lock (SyncRoot)
        {
            if (isHardware)
            {
                HeldKeys.Remove(new KeyRegistryKey(true, IntPtr.Zero, vk));
                StopWatchdogIfIdleLocked();
                return IntPtr.Zero;
            }

            if (fallbackTargetHwnd != IntPtr.Zero &&
                HeldKeys.Remove(new KeyRegistryKey(false, fallbackTargetHwnd, vk), out var exact))
            {
                StopWatchdogIfIdleLocked();
                return exact.TargetHwnd;
            }

            var matchByRoot = HeldKeys
                .FirstOrDefault(kv => !kv.Key.IsHardware && kv.Value.Vk == vk && kv.Value.RootHwnd == rootHwnd);
            if (matchByRoot.Value != null)
            {
                HeldKeys.Remove(matchByRoot.Key);
                StopWatchdogIfIdleLocked();
                return matchByRoot.Value.TargetHwnd;
            }

            StopWatchdogIfIdleLocked();
            return fallbackTargetHwnd;
        }
    }

    public static void RegisterHardwareMouseButton(string button, int screenX, int screenY, int? maxHoldMs = null)
    {
        string normBtn = NormalizeButtonName(button);
        int clampedMs = NormalizeMaxHoldMs(maxHoldMs);
        var key = new MouseRegistryKey(true, IntPtr.Zero, normBtn);
        var entry = new HeldMouseEntry(
            true,
            IntPtr.Zero,
            IntPtr.Zero,
            normBtn,
            screenX,
            screenY,
            IntPtr.Zero,
            DateTime.UtcNow.AddMilliseconds(clampedMs));

        lock (SyncRoot)
        {
            HeldMouseButtons[key] = entry;
            EnsureWatchdogRunningLocked();
        }
    }

    public static void RegisterBackgroundMouseButton(
        IntPtr rootHwnd,
        IntPtr targetHwnd,
        string button,
        int screenX,
        int screenY,
        IntPtr clientLParam,
        int? maxHoldMs = null)
    {
        if (targetHwnd == IntPtr.Zero)
        {
            return;
        }

        string normBtn = NormalizeButtonName(button);
        int clampedMs = NormalizeMaxHoldMs(maxHoldMs);
        var key = new MouseRegistryKey(false, targetHwnd, normBtn);
        var entry = new HeldMouseEntry(
            false,
            rootHwnd,
            targetHwnd,
            normBtn,
            screenX,
            screenY,
            clientLParam,
            DateTime.UtcNow.AddMilliseconds(clampedMs));

        lock (SyncRoot)
        {
            HeldMouseButtons[key] = entry;
            EnsureWatchdogRunningLocked();
        }
    }

    public static (IntPtr TargetHwnd, IntPtr ClientLParam, int ScreenX, int ScreenY, bool Found) UnregisterMouseButtonAndResolveTarget(
        bool isHardware,
        IntPtr rootHwnd,
        IntPtr fallbackTargetHwnd,
        string button)
    {
        string normBtn = NormalizeButtonName(button);
        lock (SyncRoot)
        {
            if (isHardware)
            {
                if (HeldMouseButtons.Remove(new MouseRegistryKey(true, IntPtr.Zero, normBtn), out var hwEntry))
                {
                    StopWatchdogIfIdleLocked();
                    return (IntPtr.Zero, IntPtr.Zero, hwEntry.ScreenX, hwEntry.ScreenY, true);
                }

                StopWatchdogIfIdleLocked();
                return (IntPtr.Zero, IntPtr.Zero, 0, 0, false);
            }

            if (fallbackTargetHwnd != IntPtr.Zero &&
                HeldMouseButtons.Remove(new MouseRegistryKey(false, fallbackTargetHwnd, normBtn), out var exact))
            {
                StopWatchdogIfIdleLocked();
                return (exact.TargetHwnd, exact.ClientLParam, exact.ScreenX, exact.ScreenY, true);
            }

            var matchByRoot = HeldMouseButtons
                .FirstOrDefault(kv => !kv.Key.IsHardware &&
                                      string.Equals(kv.Value.Button, normBtn, StringComparison.OrdinalIgnoreCase) &&
                                      kv.Value.RootHwnd == rootHwnd);
            if (matchByRoot.Value != null)
            {
                HeldMouseButtons.Remove(matchByRoot.Key);
                StopWatchdogIfIdleLocked();
                return (matchByRoot.Value.TargetHwnd, matchByRoot.Value.ClientLParam, matchByRoot.Value.ScreenX, matchByRoot.Value.ScreenY, true);
            }

            StopWatchdogIfIdleLocked();
            return (fallbackTargetHwnd, IntPtr.Zero, 0, 0, false);
        }
    }

    public static ReleaseInputsResult ReleaseAll()
    {
        List<HeldKeyEntry> keysToRelease;
        List<HeldMouseEntry> buttonsToRelease;

        lock (SyncRoot)
        {
            keysToRelease = [.. HeldKeys.Values];
            buttonsToRelease = [.. HeldMouseButtons.Values];
            HeldKeys.Clear();
            HeldMouseButtons.Clear();
            StopWatchdogIfIdleLocked();
        }

        var releasedKeyNames = new List<string>();
        var releasedButtonNames = new List<string>();
        var releasedHardwareVks = new HashSet<int>();

        DispatchReleases(keysToRelease, buttonsToRelease, releasedKeyNames, releasedButtonNames, releasedHardwareVks);

        // Safety sweep: check physical modifier VKs (Shift, Ctrl, Alt, LWin, RWin) via GetAsyncKeyState
        var stuckModInputs = new List<NativeMethods.INPUT>();
        foreach (int modVk in WatchedModifierVks)
        {
            if (releasedHardwareVks.Contains(modVk))
            {
                continue;
            }

            try
            {
                if ((NativeMethods.GetAsyncKeyState(modVk) & 0x8000) != 0)
                {
                    stuckModInputs.Add(BuildHardwareKeyUpInput(modVk));
                    releasedHardwareVks.Add(modVk);
                    releasedKeyNames.Add(FormatVkName(modVk));
                }
            }
            catch
            {
                // Ignore in headless/non-interactive station
            }
        }

        if (stuckModInputs.Count > 0)
        {
            try
            {
                NativeMethods.SendInput((uint)stuckModInputs.Count, [.. stuckModInputs], Marshal.SizeOf<NativeMethods.INPUT>());
            }
            catch
            {
                // Ignore
            }
        }

        return new ReleaseInputsResult
        {
            ReleasedKeys = releasedKeyNames,
            ReleasedMouseButtons = releasedButtonNames,
            Count = releasedKeyNames.Count + releasedButtonNames.Count
        };
    }

    private static void ReleaseExpiredSafe()
    {
        try
        {
            DateTime now = DateTime.UtcNow;
            List<HeldKeyEntry>? expiredKeys = null;
            List<HeldMouseEntry>? expiredButtons = null;

            lock (SyncRoot)
            {
                foreach (var kv in HeldKeys)
                {
                    if (now >= kv.Value.DeadlineUtc)
                    {
                        expiredKeys ??= [];
                        expiredKeys.Add(kv.Value);
                    }
                }

                if (expiredKeys != null)
                {
                    foreach (var entry in expiredKeys)
                    {
                        HeldKeys.Remove(new KeyRegistryKey(entry.IsHardware, entry.TargetHwnd, entry.Vk));
                    }
                }

                foreach (var kv in HeldMouseButtons)
                {
                    if (now >= kv.Value.DeadlineUtc)
                    {
                        expiredButtons ??= [];
                        expiredButtons.Add(kv.Value);
                    }
                }

                if (expiredButtons != null)
                {
                    foreach (var entry in expiredButtons)
                    {
                        HeldMouseButtons.Remove(new MouseRegistryKey(entry.IsHardware, entry.TargetHwnd, entry.Button));
                    }
                }

                StopWatchdogIfIdleLocked();
            }

            if (expiredKeys != null || expiredButtons != null)
            {
                DispatchReleases(
                    expiredKeys ?? [],
                    expiredButtons ?? [],
                    [],
                    [],
                    []);
            }
        }
        catch
        {
            // Never allow background timer exceptions to surface
        }
    }

    private static void DispatchReleases(
        List<HeldKeyEntry> keysToRelease,
        List<HeldMouseEntry> buttonsToRelease,
        List<string> releasedKeyNames,
        List<string> releasedButtonNames,
        HashSet<int> releasedHardwareVks)
    {
        int inputSize = Marshal.SizeOf<NativeMethods.INPUT>();
        var hwInputs = new List<NativeMethods.INPUT>();

        // Release non-modifiers first, then modifiers last for clean chord teardown
        foreach (var keyEntry in keysToRelease.OrderBy(k => IsModifierVk(k.Vk) ? 1 : 0))
        {
            if (keyEntry.IsHardware)
            {
                hwInputs.Add(BuildHardwareKeyUpInput(keyEntry.Vk));
                releasedHardwareVks.Add(keyEntry.Vk);
                releasedKeyNames.Add(keyEntry.KeyLabel);
            }
            else if (keyEntry.TargetHwnd != IntPtr.Zero && NativeMethods.IsWindow(keyEntry.TargetHwnd))
            {
                try
                {
                    NativeMethods.PostMessage(
                        keyEntry.TargetHwnd,
                        NativeMethods.WM_KEYUP,
                        new IntPtr(keyEntry.Vk),
                        NativeMethods.BuildKeyUpLParam(keyEntry.Vk, InputInjector.IsExtendedKey(keyEntry.Vk)));
                }
                catch
                {
                }
                releasedKeyNames.Add(keyEntry.KeyLabel);
            }
            else
            {
                releasedKeyNames.Add(keyEntry.KeyLabel);
            }
        }

        foreach (var btnEntry in buttonsToRelease)
        {
            if (btnEntry.IsHardware)
            {
                hwInputs.Add(BuildHardwareMouseUpInput(btnEntry.Button, btnEntry.ScreenX, btnEntry.ScreenY));
                releasedButtonNames.Add(btnEntry.Button);
            }
            else if (btnEntry.TargetHwnd != IntPtr.Zero && NativeMethods.IsWindow(btnEntry.TargetHwnd))
            {
                uint upMsg = btnEntry.Button switch
                {
                    "right" => NativeMethods.WM_RBUTTONUP,
                    "middle" => NativeMethods.WM_MBUTTONUP,
                    _ => NativeMethods.WM_LBUTTONUP
                };
                try
                {
                    NativeMethods.PostMessage(btnEntry.TargetHwnd, upMsg, IntPtr.Zero, btnEntry.ClientLParam);
                }
                catch
                {
                }
                releasedButtonNames.Add(btnEntry.Button);
            }
            else
            {
                releasedButtonNames.Add(btnEntry.Button);
            }
        }

        if (hwInputs.Count > 0)
        {
            try
            {
                NativeMethods.SendInput((uint)hwInputs.Count, [.. hwInputs], inputSize);
            }
            catch
            {
            }
        }
    }

    internal static NativeMethods.INPUT BuildHardwareKeyUpInput(int vk)
    {
        uint scan = InputInjector.GetScanCode(vk);
        uint flags = NativeMethods.KEYEVENTF_SCANCODE | NativeMethods.KEYEVENTF_KEYUP;
        if (InputInjector.IsExtendedKey(vk))
        {
            flags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;
        }

        ushort sendVk = scan == 0 ? (ushort)vk : (ushort)0;
        if (scan == 0)
        {
            flags &= ~NativeMethods.KEYEVENTF_SCANCODE;
        }

        return new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            u = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = sendVk,
                    wScan = (ushort)scan,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            }
        };
    }

    internal static NativeMethods.INPUT BuildHardwareMouseUpInput(string button, int screenX, int screenY)
    {
        uint upFlag = button switch
        {
            "right" => NativeMethods.MOUSEEVENTF_RIGHTUP,
            "middle" => NativeMethods.MOUSEEVENTF_MIDDLEUP,
            _ => NativeMethods.MOUSEEVENTF_LEFTUP
        };

        var (normX, normY, absFlags) = InputInjector.NormalizeScreenPointForSendInput(screenX, screenY);
        return new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            u = new NativeMethods.InputUnion
            {
                mi = new NativeMethods.MOUSEINPUT
                {
                    dx = normX,
                    dy = normY,
                    mouseData = 0,
                    dwFlags = upFlag | (absFlags & ~NativeMethods.MOUSEEVENTF_MOVE),
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            }
        };
    }

    private static void EnsureWatchdogRunningLocked()
    {
        if (!_timerActive)
        {
            _timerActive = true;
            WatchdogTimer.Change(WatchdogIntervalMs, WatchdogIntervalMs);
        }
    }

    private static void StopWatchdogIfIdleLocked()
    {
        if (_timerActive && HeldKeys.Count == 0 && HeldMouseButtons.Count == 0)
        {
            _timerActive = false;
            WatchdogTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    private static bool IsModifierVk(int vk) =>
        vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    internal static string NormalizeButtonName(string? button)
    {
        if (string.IsNullOrWhiteSpace(button))
        {
            return "left";
        }

        string norm = button.Trim().ToLowerInvariant();
        return norm switch
        {
            "left" or "right" or "middle" => norm,
            _ => throw new ArgumentException($"Unsupported mouse_button '{button}'. Valid options are: left, right, middle.")
        };
    }

    internal static string FormatVkName(int vk)
    {
        return vk switch
        {
            0x10 => "Shift",
            0x11 => "Ctrl",
            0x12 => "Alt",
            0x5B => "LWin",
            0x5C => "RWin",
            0x0D => "Return",
            0x09 => "Tab",
            0x1B => "Escape",
            0x20 => "Space",
            0x08 => "Backspace",
            0x2E => "Delete",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            >= '0' and <= '9' => ((char)vk).ToString(),
            >= 'A' and <= 'Z' => ((char)vk).ToString(),
            >= 0x70 and <= 0x87 => $"F{vk - 0x70 + 1}",
            _ => $"VK_0x{vk:X2}"
        };
    }
}
