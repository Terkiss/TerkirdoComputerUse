using System.Runtime.InteropServices;

namespace TerkirdoComputer.Windows;

public static class NativeMethods
{
    public const uint WM_ACTIVATE = 0x0006;
    public const uint WM_SETFOCUS = 0x0007;
    public const uint WM_SETTEXT = 0x000C;
    public const uint WM_GETTEXT = 0x000D;
    public const uint WM_GETTEXTLENGTH = 0x000E;
    public const uint WM_ACTIVATEAPP = 0x001C;
    public const uint WM_MOUSEACTIVATE = 0x0021;
    public const uint WM_NCACTIVATE = 0x0086;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;
    public const uint WM_CHAR = 0x0102;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONDOWN = 0x0204;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_MBUTTONDOWN = 0x0207;
    public const uint WM_MBUTTONUP = 0x0208;
    public const uint WM_MOUSEWHEEL = 0x020A;
    public const uint WM_MOUSEHWHEEL = 0x020E;
    public const uint EM_SETSEL = 0x00B1;
    public const uint EM_REPLACESEL = 0x00C2;
    public const uint BM_CLICK = 0x00F5;

    public const int WA_INACTIVE = 0;
    public const int WA_ACTIVE = 1;
    public const int WA_CLICKACTIVE = 2;
    public const int MA_ACTIVATE = 1;

    public const uint INPUT_MOUSE = 0;
    public const uint INPUT_KEYBOARD = 1;

    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    public const uint MOUSEEVENTF_WHEEL = 0x0800;
    public const uint MOUSEEVENTF_HWHEEL = 0x1000;
    public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;
    public const uint KEYEVENTF_SCANCODE = 0x0008;
    public const uint MAPVK_VK_TO_VSC = 0;

    public const uint WM_CLOSE = 0x0010;
    public const uint PW_RENDERFULLCONTENT = 0x00000002;

    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;
    public const int SM_CMONITORS = 80;

    // Clipboard formats
    public const uint CF_TEXT = 1;
    public const uint CF_BITMAP = 2;
    public const uint CF_DIB = 8;
    public const uint CF_UNICODETEXT = 13;
    public const uint CF_DIBV5 = 17;

    // Global memory flags
    public const uint GMEM_MOVEABLE = 0x0002;
    public const uint GMEM_ZEROINIT = 0x0040;
    public const uint GHND = GMEM_MOVEABLE | GMEM_ZEROINIT;

    // ShowWindow commands
    public const int SW_HIDE = 0;
    public const int SW_SHOWNORMAL = 1;
    public const int SW_SHOWMINIMIZED = 2;
    public const int SW_MAXIMIZE = 3;
    public const int SW_SHOWNOACTIVATE = 4;
    public const int SW_SHOW = 5;
    public const int SW_MINIMIZE = 6;
    public const int SW_SHOWMINNOACTIVE = 7;
    public const int SW_SHOWNA = 8;
    public const int SW_RESTORE = 9;

    // SetWindowPos flags
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;

    // Monitor flags
    public const uint MONITORINFOF_PRIMARY = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;

        [FieldOffset(0)]
        public KEYBDINPUT ki;

        [FieldOffset(0)]
        public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, string lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, System.Text.StringBuilder? lParam);

    public const uint WM_NULL = 0x0000;
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint Msg,
        IntPtr wParam,
        IntPtr lParam,
        uint fuFlags,
        uint uTimeout,
        out IntPtr lpdwResult);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint Msg,
        IntPtr wParam,
        string lParam,
        uint fuFlags,
        uint uTimeout,
        out IntPtr lpdwResult);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern void SwitchToThisWindow(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool fAltTab);

    public static bool IsWindowResponsive(IntPtr hWnd, uint timeoutMs = 25)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd))
        {
            return false;
        }

        return SendMessageTimeout(hWnd, WM_NULL, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, timeoutMs, out _) != IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, [MarshalAs(UnmanagedType.LPArray), In] INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    public static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern short VkKeyScan(char ch);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    // Token elevation checking for UIPI
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass, out int TokenInformation, int TokenInformationLength, out int ReturnLength);

    public static bool IsProcessElevated(IntPtr processHandle)
    {
        const uint TOKEN_QUERY = 0x0008;
        const int TokenElevation = 20;

        if (!OpenProcessToken(processHandle, TOKEN_QUERY, out var tokenHandle))
        {
            return false;
        }

        try
        {
            if (GetTokenInformation(tokenHandle, TokenElevation, out var elevation, sizeof(int), out _))
            {
                return elevation != 0;
            }
            return false;
        }
        finally
        {
            CloseHandle(tokenHandle);
        }
    }

    public static IntPtr ConvertToLParam(int x, int y)
    {
        var packed = ((y & 0xffff) << 16) | (x & 0xffff);
        return new IntPtr(packed);
    }

    public static IntPtr ConvertToWheelWParam(int delta)
    {
        var packed = (delta & 0xffff) << 16;
        return new IntPtr(packed);
    }

    // --- Win32 Window Management APIs ---
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    public const int GWL_EXSTYLE = -20;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
    {
        return IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsZoomed(IntPtr hWnd);

    // --- Win32 Clipboard APIs ---
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsClipboardFormatAvailable(uint format);

    // --- Kernel32 Global Memory APIs for Clipboard ---
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalFree(IntPtr hMem);

    // --- GDI32 APIs ---
    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr hObject);

    // --- WinMM & Kernel32 High-Resolution Timer + Power Throttling APIs ---
    [DllImport("winmm.dll")]
    internal static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    internal static extern uint timeEndPeriod(uint uPeriod);

    internal const int ProcessPowerThrottling = 4;
    internal const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    internal const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;
    internal const uint PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 0x4;

    internal const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    internal const uint TIMER_ALL_ACCESS = 0x1F0003;

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetProcessInformation(
        IntPtr hProcess,
        int ProcessInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE ProcessInformation,
        uint ProcessInformationSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateWaitableTimerExW(
        IntPtr lpTimerAttributes,
        string? lpTimerName,
        uint dwFlags,
        uint dwDesiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWaitableTimer(
        IntPtr hTimer,
        ref long pDueTime,
        int lPeriod,
        IntPtr pfnCompletionRoutine,
        IntPtr lpArgToCompletionRoutine,
        [MarshalAs(UnmanagedType.Bool)] bool fResume);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    // --- Multi-Monitor Display APIs ---
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    // --- DWM, DPI, & Hit-Testing APIs (Reference v1.1.4 Parity) ---
    public const int DWMWA_CLOAKED = 14;
    public const uint CWP_SKIPINVISIBLE = 0x0001;
    public const uint GA_ROOT = 2;
    public const uint GW_HWNDPREV = 3;

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    public const int DPI_AWARENESS_PER_MONITOR_AWARE = 2;
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    public const int MinimumScreenshotDimension = 64;
    public const int MinimumScreenshotArea = 20000;

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern IntPtr ChildWindowFromPointEx(IntPtr hWndParent, POINT pt, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowEnabled(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT Point);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    public static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    public static extern int GetAwarenessFromDpiAwarenessContext(IntPtr value);

    public static bool EnsurePerMonitorDpiAwareness()
    {
        try
        {
            SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch { }

        try
        {
            SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch { }

        try
        {
            IntPtr ctx = GetThreadDpiAwarenessContext();
            return GetAwarenessFromDpiAwarenessContext(ctx) == DPI_AWARENESS_PER_MONITOR_AWARE;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsCloaked(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;
        try
        {
            if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0)
            {
                return cloaked != 0;
            }
        }
        catch { }
        return false;
    }

    public static bool IsPhysicalMouseButtonDown()
    {
        return (GetAsyncKeyState(1) & 0x8000) != 0 ||
               (GetAsyncKeyState(2) & 0x8000) != 0 ||
               (GetAsyncKeyState(4) & 0x8000) != 0;
    }

    public static bool IsUsableScreenshotFrame(Core.Frame? frame)
    {
        if (frame == null) return false;
        if (!double.IsFinite(frame.X) || !double.IsFinite(frame.Y) ||
            !double.IsFinite(frame.Width) || !double.IsFinite(frame.Height))
        {
            return false;
        }
        if (frame.Width < MinimumScreenshotDimension || frame.Height < MinimumScreenshotDimension)
        {
            return false;
        }
        if (frame.Width * frame.Height < MinimumScreenshotArea)
        {
            return false;
        }
        return true;
    }

    public static bool IntersectsVirtualScreen(Core.Frame frame)
    {
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (vw <= 0 || vh <= 0)
        {
            vw = GetSystemMetrics(SM_CXSCREEN);
            vh = GetSystemMetrics(SM_CYSCREEN);
        }
        if (vw <= 0 || vh <= 0)
        {
            return true;
        }

        double left = Math.Max(frame.X, vx);
        double top = Math.Max(frame.Y, vy);
        double right = Math.Min(frame.X + frame.Width, vx + vw);
        double bottom = Math.Min(frame.Y + frame.Height, vy + vh);
        double width = right - left;
        double height = bottom - top;
        return width >= MinimumScreenshotDimension &&
               height >= MinimumScreenshotDimension &&
               (width * height) >= MinimumScreenshotArea;
    }

    public static bool IsCapturableWindow(IntPtr hWnd, out Core.Frame? bounds)
    {
        bounds = null;
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return false;
        if (!IsWindowVisible(hWnd) || IsIconic(hWnd) || IsCloaked(hWnd)) return false;
        if (!GetWindowRect(hWnd, out var rect)) return false;

        var frame = new Core.Frame(
            rect.Left,
            rect.Top,
            Math.Max(0, rect.Right - rect.Left),
            Math.Max(0, rect.Bottom - rect.Top));

        if (!IsUsableScreenshotFrame(frame) || !IntersectsVirtualScreen(frame))
        {
            return false;
        }

        bounds = frame;
        return true;
    }

    public readonly record struct ClickTargetInfo(
        bool Ok,
        IntPtr TargetHwnd,
        int ClientX,
        int ClientY,
        RECT TargetRect);

    public static ClickTargetInfo ResolveClickTarget(IntPtr root, int screenX, int screenY)
    {
        if (root == IntPtr.Zero || !IsWindow(root))
        {
            return default;
        }

        GetWindowThreadProcessId(root, out uint rootPid);
        if (rootPid == 0)
        {
            return default;
        }

        IntPtr current = root;
        for (int depth = 0; depth < 64; depth++)
        {
            var point = new POINT { X = screenX, Y = screenY };
            if (!ScreenToClient(current, ref point))
            {
                return default;
            }

            IntPtr child = ChildWindowFromPointEx(current, point, CWP_SKIPINVISIBLE);
            if (child == IntPtr.Zero || child == current)
            {
                break;
            }

            GetWindowThreadProcessId(child, out uint childPid);
            if (childPid != rootPid || !IsChild(root, child))
            {
                break;
            }

            current = child;
        }

        GetWindowThreadProcessId(current, out uint targetPid);
        if (targetPid != rootPid)
        {
            return default;
        }

        if (current != root && !IsChild(root, current))
        {
            return default;
        }

        if (!IsWindowVisible(current) || !IsWindowEnabled(current))
        {
            return default;
        }

        if (!GetWindowRect(current, out var targetRect))
        {
            return default;
        }

        var targetPoint = new POINT { X = screenX, Y = screenY };
        if (!ScreenToClient(current, ref targetPoint))
        {
            return default;
        }

        if (!GetClientRect(current, out var clientRect))
        {
            return default;
        }

        int clientWidth = clientRect.Right - clientRect.Left;
        int clientHeight = clientRect.Bottom - clientRect.Top;
        if (clientWidth <= 0 || clientHeight <= 0)
        {
            return default;
        }

        if (targetPoint.X < 0 || targetPoint.Y < 0 || targetPoint.X >= clientWidth || targetPoint.Y >= clientHeight)
        {
            return default;
        }

        return new ClickTargetInfo(true, current, targetPoint.X, targetPoint.Y, targetRect);
    }

    // --- Inactive / Background & Game-Compatible Control Helpers ---
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    /// <summary>
    /// Spoofs window and child control activation via non-intrusive PostMessage calls
    /// (WM_NCACTIVATE, WM_ACTIVATEAPP, WM_ACTIVATE, WM_SETFOCUS) without changing the OS foreground window.
    /// Allows background games (YU-RIS, Kirikiri, Unity windowed, SDL, Win32) and apps to accept input while inactive.
    /// </summary>
    public static void SpoofWindowActivation(IntPtr rootHwnd, IntPtr targetHwnd)
    {
        if (rootHwnd != IntPtr.Zero && IsWindow(rootHwnd))
        {
            PostMessage(rootHwnd, WM_NCACTIVATE, new IntPtr(1), IntPtr.Zero);
            PostMessage(rootHwnd, WM_ACTIVATEAPP, new IntPtr(1), IntPtr.Zero);
            PostMessage(rootHwnd, WM_ACTIVATE, new IntPtr(WA_ACTIVE), IntPtr.Zero);
            PostMessage(rootHwnd, WM_SETFOCUS, IntPtr.Zero, IntPtr.Zero);
        }

        if (targetHwnd != IntPtr.Zero && targetHwnd != rootHwnd && IsWindow(targetHwnd))
        {
            PostMessage(targetHwnd, WM_ACTIVATE, new IntPtr(WA_ACTIVE), IntPtr.Zero);
            PostMessage(targetHwnd, WM_SETFOCUS, rootHwnd, IntPtr.Zero);
        }
    }

    /// <summary>
    /// Packs the Win32 WM_KEYDOWN lParam with repeatCount=1 (bits 0..15), hardware scanCode (bits 16..23),
    /// and extended key flag (bit 24) so game engines inspecting hardware scan codes in WM_KEYDOWN accept background keys.
    /// </summary>
    public static IntPtr BuildKeyDownLParam(int vk, bool isExtended = false)
    {
        uint scanCode = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC) & 0xFFu;
        uint lParam = 1u | (scanCode << 16);
        if (isExtended)
        {
            lParam |= 1u << 24;
        }
        return new IntPtr(unchecked((int)lParam));
    }

    /// <summary>
    /// Packs the Win32 WM_KEYUP lParam with repeatCount=1 (bits 0..15), hardware scanCode (bits 16..23),
    /// extended key flag (bit 24), previous key state=1 (bit 30), and transition state=1 (bit 31).
    /// </summary>
    public static IntPtr BuildKeyUpLParam(int vk, bool isExtended = false)
    {
        uint scanCode = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC) & 0xFFu;
        uint lParam = 1u | (scanCode << 16) | (1u << 30) | (1u << 31);
        if (isExtended)
        {
            lParam |= 1u << 24;
        }
        return new IntPtr(unchecked((int)lParam));
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public uint cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetFocus(IntPtr hWnd);

    public static bool IsEditControlClassName(string? className)
    {
        if (string.IsNullOrEmpty(className))
        {
            return false;
        }

        return className.Contains("RichEditD2DPT", StringComparison.OrdinalIgnoreCase) ||
               className.Contains("Edit", StringComparison.OrdinalIgnoreCase) ||
               className.Contains("RICHEDIT", StringComparison.OrdinalIgnoreCase) ||
               className.Contains("Scintilla", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the best child HWND inside a top-level window to receive background keyboard or text messages
    /// (e.g., RichEditD2DPT in Windows 11 Notepad, Edit in classic Notepad, Chrome_RenderWidgetHostHWND, or game canvas).
    /// Prioritizes the GUI thread's focused edit control, then the visible enabled edit control with the largest client area.
    /// </summary>
    public static IntPtr FindInputTargetHwnd(IntPtr rootHwnd)
    {
        if (rootHwnd == IntPtr.Zero || !IsWindow(rootHwnd))
        {
            return IntPtr.Zero;
        }

        uint rootTid = GetWindowThreadProcessId(rootHwnd, out uint rootPid);
        if (rootTid != 0)
        {
            var gti = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
            if (GetGUIThreadInfo(rootTid, ref gti) &&
                gti.hwndFocus != IntPtr.Zero &&
                gti.hwndFocus != rootHwnd &&
                IsChild(rootHwnd, gti.hwndFocus) &&
                IsWindowVisible(gti.hwndFocus) &&
                IsWindowEnabled(gti.hwndFocus))
            {
                GetWindowThreadProcessId(gti.hwndFocus, out uint focPid);
                if (rootPid == 0 || focPid == rootPid)
                {
                    var focSb = new System.Text.StringBuilder(128);
                    GetClassName(gti.hwndFocus, focSb, focSb.Capacity);
                    if (IsEditControlClassName(focSb.ToString()))
                    {
                        return gti.hwndFocus;
                    }
                }
            }
        }

        IntPtr bestEditChild = IntPtr.Zero;
        int bestEditArea = -1;
        IntPtr bestCanvasChild = IntPtr.Zero;
        int bestCanvasArea = 0;

        EnumChildWindows(rootHwnd, (child, _) =>
        {
            if (child == IntPtr.Zero || !IsWindowVisible(child) || !IsWindowEnabled(child))
            {
                return true;
            }

            uint childTid = GetWindowThreadProcessId(child, out uint childPid);
            if (rootPid != 0 && childPid != rootPid)
            {
                return true;
            }

            var sb = new System.Text.StringBuilder(128);
            GetClassName(child, sb, sb.Capacity);
            string className = sb.ToString();

            if (IsEditControlClassName(className))
            {
                if (childTid != 0 && childTid != rootTid)
                {
                    var childGti = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
                    if (GetGUIThreadInfo(childTid, ref childGti) && childGti.hwndFocus == child)
                    {
                        bestEditChild = child;
                        return false;
                    }
                }

                int editArea = 0;
                if (GetClientRect(child, out var editRc))
                {
                    editArea = Math.Max(0, editRc.Right - editRc.Left) * Math.Max(0, editRc.Bottom - editRc.Top);
                }

                if (editArea > bestEditArea)
                {
                    bestEditArea = editArea;
                    bestEditChild = child;
                }

                return true;
            }

            if (GetClientRect(child, out var rc))
            {
                int area = Math.Max(0, rc.Right - rc.Left) * Math.Max(0, rc.Bottom - rc.Top);
                if (className.Contains("Chrome_RenderWidgetHostHWND", StringComparison.OrdinalIgnoreCase) ||
                    className.Contains("Unity", StringComparison.OrdinalIgnoreCase) ||
                    className.Contains("SDL", StringComparison.OrdinalIgnoreCase) ||
                    area > bestCanvasArea)
                {
                    bestCanvasArea = area;
                    bestCanvasChild = child;
                }
            }

            return true;
        }, IntPtr.Zero);

        if (bestEditChild != IntPtr.Zero)
        {
            return bestEditChild;
        }

        if (bestCanvasChild != IntPtr.Zero && GetClientRect(rootHwnd, out var rootRc))
        {
            int rootArea = Math.Max(1, (rootRc.Right - rootRc.Left) * (rootRc.Bottom - rootRc.Top));
            if (bestCanvasArea >= rootArea / 3)
            {
                return bestCanvasChild;
            }
        }

        return rootHwnd;
    }
}
