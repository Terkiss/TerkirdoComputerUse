using System.Diagnostics;
using TerkirdoComputer.Core;

namespace TerkirdoComputer.Windows;

public static class ProcessManager
{
    private static string UnwrapOptionalQuotes(string value)
    {
        string s = value.Trim();
        if (s.Length >= 2 &&
            ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
        {
            s = s[1..^1].Trim();
        }
        return s;
    }

    public static ProcessLaunchResult LaunchApp(string path, string? args = null, string? workingDir = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path is required to launch an application.");
        }

        string trimmedPath = UnwrapOptionalQuotes(path);
        if (string.IsNullOrWhiteSpace(trimmedPath))
        {
            throw new ArgumentException("Path is required to launch an application.");
        }

        string resolvedFileName = trimmedPath;

        // Verify and resolve full path if path contains directory separators
        if (trimmedPath.Contains(Path.DirectorySeparatorChar) || trimmedPath.Contains(Path.AltDirectorySeparatorChar))
        {
            resolvedFileName = Path.GetFullPath(trimmedPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = resolvedFileName,
            Arguments = args ?? string.Empty,
            UseShellExecute = true
        };

        if (!string.IsNullOrWhiteSpace(workingDir))
        {
            string cleanWorkDir = UnwrapOptionalQuotes(workingDir);
            if (!string.IsNullOrWhiteSpace(cleanWorkDir))
            {
                startInfo.WorkingDirectory = Path.GetFullPath(cleanWorkDir);
            }
        }

        string baseName = Path.GetFileNameWithoutExtension(trimmedPath);
        HashSet<int> preExistingPids = [];
        if (!string.IsNullOrWhiteSpace(baseName))
        {
            try
            {
                foreach (var existing in Process.GetProcessesByName(baseName))
                {
                    preExistingPids.Add(existing.Id);
                }
            }
            catch { }
        }

        Process? proc;
        try
        {
            proc = Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to launch application '{path}': {ex.Message}", ex);
        }

        if (proc == null)
        {
            throw new InvalidOperationException($"Failed to start process for path '{path}'.");
        }

        DateTime startedAt;
        try
        {
            startedAt = proc.StartTime.ToUniversalTime();
        }
        catch
        {
            startedAt = DateTime.UtcNow;
        }

        int finalPid = proc.Id;
        string finalProcName = proc.ProcessName;

        // On Windows 11, app execution aliases (like notepad.exe) run a launcher shim
        // that exits quickly while spawning the actual packaged app process.
        // Poll up to 1000ms (40 x 25ms), breaking immediately once the real window is capturable.
        static bool IsReadyTopLevelWindow(IntPtr h) =>
            h != IntPtr.Zero &&
            NativeMethods.IsWindowVisible(h) &&
            !NativeMethods.IsIconic(h) &&
            NativeMethods.GetWindowRect(h, out var r) &&
            (r.Right - r.Left) >= 64 &&
            (r.Bottom - r.Top) >= 64;

        Process currentProc = proc;
        var deadRelayPids = new HashSet<int>();
        var launchSw = Stopwatch.StartNew();
        const int maxTotalWaitMs = 2500;
        const int shimSpawnWaitMs = 1800;
        const int singleInstanceFallbackMs = 500;

        while (launchSw.ElapsedMilliseconds < maxTotalWaitMs)
        {
            currentProc.Refresh();
            if (!currentProc.HasExited && IsReadyTopLevelWindow(WindowManager.GetTargetHwnd(currentProc)))
            {
                break;
            }

            if (currentProc.HasExited)
            {
                deadRelayPids.Add(currentProc.Id);
            }

            // On Windows 11, C:\Windows\System32\notepad.exe (and other MSIX execution-alias shims) can either
            // exit quickly OR stay alive windowless while its spawned packaged child process already owns the
            // visible top-level window. Check newly spawned same-name processes whenever currentProc has no window.
            if (!string.IsNullOrWhiteSpace(baseName))
            {
                var candidates = Process.GetProcessesByName(baseName);
                var freshReady = candidates
                    .Where(p => !p.HasExited &&
                                p.Id != currentProc.Id &&
                                !preExistingPids.Contains(p.Id) &&
                                !deadRelayPids.Contains(p.Id) &&
                                IsReadyTopLevelWindow(WindowManager.GetTargetHwnd(p)))
                    .OrderByDescending(p =>
                    {
                        try { return p.StartTime; } catch { return DateTime.MinValue; }
                    })
                    .FirstOrDefault();

                if (freshReady != null)
                {
                    currentProc = freshReady;
                    finalPid = freshReady.Id;
                    finalProcName = freshReady.ProcessName;
                    break;
                }

                if (currentProc.HasExited)
                {
                    var freshStarting = candidates
                        .Where(p => !p.HasExited &&
                                    !preExistingPids.Contains(p.Id) &&
                                    !deadRelayPids.Contains(p.Id))
                        .OrderByDescending(p =>
                        {
                            try { return p.StartTime; } catch { return DateTime.MinValue; }
                        })
                        .FirstOrDefault();

                    Process? selected = freshStarting;
                    if (selected == null && launchSw.ElapsedMilliseconds >= singleInstanceFallbackMs)
                    {
                        // Single-instance packaged app fallback (relay process exited after handing off to live instance)
                        selected = candidates
                            .Where(p => !p.HasExited &&
                                        !deadRelayPids.Contains(p.Id) &&
                                        IsReadyTopLevelWindow(WindowManager.GetTargetHwnd(p)))
                            .OrderByDescending(p =>
                            {
                                try { return p.StartTime; } catch { return DateTime.MinValue; }
                            })
                            .FirstOrDefault();
                    }

                    if (selected != null)
                    {
                        currentProc = selected;
                        finalPid = selected.Id;
                        finalProcName = selected.ProcessName;
                        if (IsReadyTopLevelWindow(WindowManager.GetTargetHwnd(currentProc)))
                        {
                            break;
                        }
                        PrecisionTimer.WaitMs(25);
                        continue;
                    }

                    if (launchSw.ElapsedMilliseconds < shimSpawnWaitMs)
                    {
                        PrecisionTimer.WaitMs(25);
                        continue;
                    }

                    break;
                }
            }
            else if (currentProc.HasExited)
            {
                break;
            }

            PrecisionTimer.WaitMs(25);
        }

        return new ProcessLaunchResult
        {
            PID = finalPid,
            ProcessName = finalProcName,
            StartedAt = startedAt.ToString("yyyy-MM-ddTHH:mm:ssZ")
        };
    }

    public static ProcessCloseResult CloseApp(string? app, int? pid = null, bool force = false, int timeoutMs = 3000)
    {
        if (pid.HasValue && pid.Value <= 0)
        {
            throw new ArgumentException("Invalid pid: must be a positive integer.");
        }

        if (string.IsNullOrWhiteSpace(app) && !pid.HasValue)
        {
            throw new ArgumentException("Either 'app' or 'pid' must be specified for close_app.");
        }

        int? strictPid = pid.HasValue
            ? pid.Value
            : (WindowManager.TryParseStrictPidPrefix(app, out int prefixPid) ? prefixPid : null);

        Process? proc = null;
        if (strictPid.HasValue)
        {
            try
            {
                proc = Process.GetProcessById(strictPid.Value);
            }
            catch { }

            if (proc == null || proc.HasExited)
            {
                throw new ArgumentException($"appNotFound(\"pid:{strictPid.Value}\")");
            }
        }

        if (proc == null && !string.IsNullOrWhiteSpace(app))
        {
            string query = app.Trim();
            string procQuery = query.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? query[..^4] : query;

            // Search by process name first
            var candidates = Process.GetProcessesByName(procQuery);
            if (candidates.Length > 0)
            {
                proc = candidates.FirstOrDefault(p => WindowManager.GetTargetHwnd(p) != IntPtr.Zero) ?? candidates[0];
            }
            else
            {
                // Search all processes
                var all = Process.GetProcesses();
                proc = all.FirstOrDefault(p =>
                    string.Equals(p.ProcessName, procQuery, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p.MainWindowTitle, query, StringComparison.OrdinalIgnoreCase) ||
                    p.MainWindowTitle.Contains(query, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (proc == null || proc.HasExited)
        {
            throw new ArgumentException($"appNotFound(\"{app ?? pid?.ToString()}\")");
        }

        int targetPid = proc.Id;
        string mode;

        if (force)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(1500);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
            {
                throw;
            }
            catch (AggregateException ex) when (ex.InnerExceptions.OfType<System.ComponentModel.Win32Exception>().Any(w => w.NativeErrorCode == 5))
            {
                throw ex.InnerExceptions.OfType<System.ComponentModel.Win32Exception>().First(w => w.NativeErrorCode == 5);
            }
            catch { }
            mode = "killed";
        }
        else
        {
            // Give newly started processes a moment to settle into their message loop
            Thread.Sleep(350);

            try
            {
                proc.WaitForInputIdle(1000);
            }
            catch { }

            bool closedGracefully = false;
            try
            {
                int waitTimeout = timeoutMs > 0 ? timeoutMs : 3000;
                var sw = System.Diagnostics.Stopwatch.StartNew();

                while (sw.ElapsedMilliseconds < waitTimeout && !proc.HasExited)
                {
                    var closeTargets = new HashSet<IntPtr>();
                    IntPtr targetWnd = WindowManager.GetTargetHwnd(proc);
                    if (targetWnd != IntPtr.Zero && NativeMethods.IsWindow(targetWnd))
                    {
                        closeTargets.Add(targetWnd);
                    }

                    if (proc.MainWindowHandle != IntPtr.Zero && NativeMethods.IsWindow(proc.MainWindowHandle))
                    {
                        closeTargets.Add(proc.MainWindowHandle);
                    }

                    // Enumerate all visible/iconic non-helper top-level windows for multi-window apps (e.g. Win11 multi-window Notepad)
                    NativeMethods.EnumWindows((hWnd, _) =>
                    {
                        try
                        {
                            NativeMethods.GetWindowThreadProcessId(hWnd, out uint ownerPid);
                            if (ownerPid != (uint)targetPid)
                            {
                                return true;
                            }

                            if (!NativeMethods.IsWindowVisible(hWnd) && !NativeMethods.IsIconic(hWnd))
                            {
                                return true;
                            }

                            var sbCls = new System.Text.StringBuilder(256);
                            NativeMethods.GetClassName(hWnd, sbCls, 256);
                            if (WindowManager.IsSystemHelperWindowClass(sbCls.ToString()))
                            {
                                return true;
                            }

                            long exStyle = NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_EXSTYLE).ToInt64();
                            if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0)
                            {
                                return true;
                            }

                            closeTargets.Add(hWnd);
                        }
                        catch
                        {
                        }
                        return true;
                    }, IntPtr.Zero);

                    foreach (IntPtr wnd in closeTargets)
                    {
                        NativeMethods.PostMessage(wnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    }

                    proc.CloseMainWindow();

                    if (proc.WaitForExit(250))
                    {
                        closedGracefully = true;
                        break;
                    }

                    proc.Refresh();
                    if (proc.HasExited)
                    {
                        closedGracefully = true;
                        break;
                    }
                }
            }
            catch
            {
                closedGracefully = false;
            }

            if (closedGracefully || proc.HasExited)
            {
                mode = "graceful";
            }
            else
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(1500);
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
                {
                    throw;
                }
                catch (AggregateException ex) when (ex.InnerExceptions.OfType<System.ComponentModel.Win32Exception>().Any(w => w.NativeErrorCode == 5))
                {
                    throw ex.InnerExceptions.OfType<System.ComponentModel.Win32Exception>().First(w => w.NativeErrorCode == 5);
                }
                catch { }
                mode = "killed";
            }
        }

        WindowManager.ClearProcessHwndAffinity(targetPid);
        return new ProcessCloseResult
        {
            PID = targetPid,
            Terminated = true,
            Mode = mode
        };
    }
}
