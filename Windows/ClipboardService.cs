using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using TerkirdoComputer.Core;

namespace TerkirdoComputer.Windows;

public class ClipboardLockedException : Exception
{
    public ClipboardLockedException(string message) : base(message) { }
}

public static class ClipboardService
{
    private sealed class StaWorkItem
    {
        public required Action Action { get; init; }
        public ManualResetEventSlim Done { get; } = new(false);
        public Exception? Exception { get; set; }
    }

    private static readonly BlockingCollection<StaWorkItem> StaQueue = new();
    private static readonly Thread StaWorkerThread;

    static ClipboardService()
    {
        StaWorkerThread = new Thread(() =>
        {
            foreach (var item in StaQueue.GetConsumingEnumerable())
            {
                try
                {
                    item.Action();
                }
                catch (Exception ex)
                {
                    item.Exception = ex;
                }
                finally
                {
                    item.Done.Set();
                }
            }
        })
        {
            IsBackground = true,
            Name = "TerkirdoClipboardStaWorker"
        };
        StaWorkerThread.SetApartmentState(ApartmentState.STA);
        StaWorkerThread.Start();
    }

    public static ClipboardData GetClipboard(string format = "text")
    {
        string norm = string.IsNullOrWhiteSpace(format) ? "text" : format.Trim().ToLowerInvariant();
        string? textResult = null;
        bool hasImage = false;
        string? imageBase64 = null;

        RunInSta(() =>
        {
            ExecuteWithRetry(() =>
            {
                if (!NativeMethods.OpenClipboard(IntPtr.Zero))
                {
                    throw new ClipboardLockedException("Clipboard is locked by another process.");
                }

                try
                {
                    bool wantText = norm is "text" or "all";
                    bool wantImage = norm is "image" or "all";

                    if (wantText && NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_UNICODETEXT))
                    {
                        IntPtr hMem = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);
                        if (hMem != IntPtr.Zero)
                        {
                            IntPtr pMem = NativeMethods.GlobalLock(hMem);
                            if (pMem != IntPtr.Zero)
                            {
                                try
                                {
                                    textResult = Marshal.PtrToStringUni(pMem);
                                }
                                finally
                                {
                                    NativeMethods.GlobalUnlock(hMem);
                                }
                            }
                        }
                    }

                    if (wantImage && NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_BITMAP))
                    {
                        IntPtr hBitmap = NativeMethods.GetClipboardData(NativeMethods.CF_BITMAP);
                        if (hBitmap != IntPtr.Zero)
                        {
                            try
                            {
                                using var img = Image.FromHbitmap(hBitmap);
                                using var ms = new MemoryStream();
                                img.Save(ms, ImageFormat.Png);
                                imageBase64 = Convert.ToBase64String(ms.ToArray());
                                hasImage = true;
                            }
                            catch
                            {
                                // Bitmap conversion failed
                            }
                        }
                    }
                }
                finally
                {
                    NativeMethods.CloseClipboard();
                }
            });
        });

        return new ClipboardData
        {
            Text = textResult,
            HasImage = hasImage,
            ImageBase64 = imageBase64
        };
    }

    public static SetClipboardResult SetClipboard(string? text, string? imageBase64 = null, bool paste = false)
    {
        int bytesWritten = 0;

        RunInSta(() =>
        {
            ExecuteWithRetry(() =>
            {
                if (!NativeMethods.OpenClipboard(IntPtr.Zero))
                {
                    throw new ClipboardLockedException("Clipboard is locked by another process.");
                }

                try
                {
                    NativeMethods.EmptyClipboard();

                    if (text != null)
                    {
                        int byteLength = (text.Length + 1) * sizeof(char);
                        IntPtr hMem = NativeMethods.GlobalAlloc(NativeMethods.GHND, (UIntPtr)byteLength);
                        if (hMem == IntPtr.Zero)
                        {
                            throw new InvalidOperationException("Failed to allocate global memory for clipboard text.");
                        }

                        IntPtr pMem = NativeMethods.GlobalLock(hMem);
                        if (pMem == IntPtr.Zero)
                        {
                            NativeMethods.GlobalFree(hMem);
                            throw new InvalidOperationException("Failed to lock global memory for clipboard text.");
                        }

                        try
                        {
                            if (text.Length > 0)
                            {
                                Marshal.Copy(text.ToCharArray(), 0, pMem, text.Length);
                            }
                            Marshal.WriteInt16(pMem, text.Length * sizeof(char), 0);
                        }
                        finally
                        {
                            NativeMethods.GlobalUnlock(hMem);
                        }

                        if (NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, hMem) == IntPtr.Zero)
                        {
                            NativeMethods.GlobalFree(hMem);
                            throw new InvalidOperationException("SetClipboardData failed for text.");
                        }

                        bytesWritten += byteLength;
                    }

                    if (!string.IsNullOrEmpty(imageBase64))
                    {
                        byte[] imageBytes = Convert.FromBase64String(imageBase64);
                        using var ms = new MemoryStream(imageBytes);
                        using var bitmap = new Bitmap(ms);
                        IntPtr hBitmap = bitmap.GetHbitmap();
                        if (NativeMethods.SetClipboardData(NativeMethods.CF_BITMAP, hBitmap) == IntPtr.Zero)
                        {
                            NativeMethods.DeleteObject(hBitmap);
                            throw new InvalidOperationException("SetClipboardData failed for bitmap.");
                        }

                        bytesWritten += imageBytes.Length;
                    }
                }
                finally
                {
                    NativeMethods.CloseClipboard();
                }
            });
        });

        bool pasted = false;
        if (paste)
        {
            Thread.Sleep(10);
            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (fg != IntPtr.Zero)
            {
                SendPasteKeystroke();
                pasted = true;
            }
        }

        return new SetClipboardResult
        {
            Success = true,
            BytesWritten = bytesWritten,
            Pasted = pasted
        };
    }

    private static void SendPasteKeystroke()
    {
        const ushort VK_CONTROL = 0x11;
        const ushort VK_V = 0x56;

        var inputs = new NativeMethods.INPUT[]
        {
            new()
            {
                type = NativeMethods.INPUT_KEYBOARD,
                u = new() { ki = new() { wVk = VK_CONTROL, dwFlags = 0 } }
            },
            new()
            {
                type = NativeMethods.INPUT_KEYBOARD,
                u = new() { ki = new() { wVk = VK_V, dwFlags = 0 } }
            },
            new()
            {
                type = NativeMethods.INPUT_KEYBOARD,
                u = new() { ki = new() { wVk = VK_V, dwFlags = NativeMethods.KEYEVENTF_KEYUP } }
            },
            new()
            {
                type = NativeMethods.INPUT_KEYBOARD,
                u = new() { ki = new() { wVk = VK_CONTROL, dwFlags = NativeMethods.KEYEVENTF_KEYUP } }
            }
        };

        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static void ExecuteWithRetry(Action action)
    {
        const int maxAttempts = 5;
        int delayMs = 20;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (ClipboardLockedException)
            {
                if (attempt == maxAttempts)
                {
                    throw;
                }
                Thread.Sleep(delayMs);
                delayMs *= 2;
            }
        }
    }

    private static void RunInSta(Action action)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            action();
            return;
        }

        var item = new StaWorkItem { Action = action };
        StaQueue.Add(item);
        item.Done.Wait();
        item.Done.Dispose();

        if (item.Exception != null)
        {
            ExceptionDispatchInfo.Capture(item.Exception).Throw();
        }
    }
}
