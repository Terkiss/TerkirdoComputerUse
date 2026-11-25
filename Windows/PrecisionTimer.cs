using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TerkirdoComputer.Windows;

/// <summary>
/// High-resolution hybrid timer combining Windows 11 EcoQoS/timer-throttling opt-out,
/// kernel CREATE_WAITABLE_TIMER_HIGH_RESOLUTION waitable timers, WinMM 1ms scheduler period,
/// and QPC (Stopwatch.GetTimestamp) spin-tail waiting.
/// Eliminates the 15.625ms-17.2ms Windows 11 background process timer quantization penalty.
/// </summary>
public static class PrecisionTimer
{
    private const uint TIMERR_NOERROR = 0;
    private const double SpinTailThresholdMs = 2.0;

    private static int _initialized;
    private static int _periodEnabled;

    [ThreadStatic]
    private static SafeWaitHandle? t_highResTimer;

    [ThreadStatic]
    private static bool t_highResTimerUnavailable;

    public static bool IsHighResolutionPeriodActive => Volatile.Read(ref _periodEnabled) == 1;

    internal static void EnsureInitialized()
    {
        if (Volatile.Read(ref _initialized) != 0)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _initialized, 1, 0) == 0)
        {
            // Opt out of Windows 11 background timer-resolution throttling (PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION)
            try
            {
                var state = new NativeMethods.PROCESS_POWER_THROTTLING_STATE
                {
                    Version = NativeMethods.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                    ControlMask = NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED |
                                  NativeMethods.PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
                    StateMask = 0
                };
                _ = NativeMethods.SetProcessInformation(
                    NativeMethods.GetCurrentProcess(),
                    NativeMethods.ProcessPowerThrottling,
                    ref state,
                    (uint)Marshal.SizeOf<NativeMethods.PROCESS_POWER_THROTTLING_STATE>());
            }
            catch
            {
                // Ignore on older OS builds where ProcessPowerThrottling is unsupported
            }

            try
            {
                if (NativeMethods.timeBeginPeriod(1) == TIMERR_NOERROR)
                {
                    Volatile.Write(ref _periodEnabled, 1);
                    AppDomain.CurrentDomain.ProcessExit += static (_, _) => ReleasePeriod();
                }
            }
            catch
            {
                // Ignore if winmm.dll is unavailable in restricted environments; high-res waitable timer + QPC spin still function.
            }
        }
    }

    internal static void ReleasePeriod()
    {
        if (Interlocked.Exchange(ref _periodEnabled, 0) == 1)
        {
            try
            {
                _ = NativeMethods.timeEndPeriod(1);
            }
            catch
            {
                // Ignore during process shutdown
            }
        }
    }

    internal static void SleepOneMillisecondHighRes()
    {
        if (!t_highResTimerUnavailable)
        {
            var handle = t_highResTimer;
            if (handle == null || handle.IsInvalid || handle.IsClosed)
            {
                try
                {
                    IntPtr raw = NativeMethods.CreateWaitableTimerExW(
                        IntPtr.Zero,
                        null,
                        NativeMethods.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
                        NativeMethods.TIMER_ALL_ACCESS);
                    if (raw != IntPtr.Zero)
                    {
                        handle = new SafeWaitHandle(raw, ownsHandle: true);
                        t_highResTimer = handle;
                    }
                    else
                    {
                        t_highResTimerUnavailable = true;
                    }
                }
                catch
                {
                    t_highResTimerUnavailable = true;
                }
            }

            if (handle is { IsInvalid: false, IsClosed: false })
            {
                // -10,000 * 100ns = 1.0ms relative wait on kernel high-resolution waitable timer
                long dueTime100ns = -10_000L;
                if (NativeMethods.SetWaitableTimer(handle.DangerousGetHandle(), ref dueTime100ns, 0, IntPtr.Zero, IntPtr.Zero, false))
                {
                    _ = NativeMethods.WaitForSingleObject(handle.DangerousGetHandle(), 5);
                    return;
                }
            }
        }

        Thread.Sleep(1);
    }

    /// <summary>
    /// Waits with sub-millisecond precision using a 1ms high-resolution waitable timer for coarse intervals (> 2.0ms)
    /// and lightweight CPU spin-wait for the final sub-2.0ms tail.
    /// </summary>
    public static void WaitMs(double milliseconds)
    {
        if (milliseconds <= 0.0 || !double.IsFinite(milliseconds))
        {
            return;
        }

        EnsureInitialized();

        double ticksPerMs = Stopwatch.Frequency / 1000.0;
        long targetTicks = Stopwatch.GetTimestamp() + (long)Math.Round(milliseconds * ticksPerMs);
        WaitUntilTimestamp(targetTicks);
    }

    /// <summary>
    /// Waits until an absolute monotonic QPC timestamp (Stopwatch.GetTimestamp) is reached.
    /// Used by follow_trajectory to achieve 0.0ms cumulative drift across long 60Hz-120Hz sequences.
    /// </summary>
    public static void WaitUntilTimestamp(long targetTicks)
    {
        if (Stopwatch.GetTimestamp() >= targetTicks)
        {
            return;
        }

        EnsureInitialized();
        long sleepThresholdTicks = (long)(SpinTailThresholdMs * (Stopwatch.Frequency / 1000.0));

        while (true)
        {
            long remainingTicks = targetTicks - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
            {
                break;
            }

            if (remainingTicks > sleepThresholdTicks)
            {
                SleepOneMillisecondHighRes();
            }
            else
            {
                Thread.SpinWait(20);
            }
        }
    }
}
