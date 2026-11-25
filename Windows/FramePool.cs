using System.Buffers;

namespace TerkirdoComputer.Windows;

/// <summary>
/// Dedicated high-capacity byte-array pool (up to 36 MiB per buffer, covering 4K 3840x2160 BGRA32 + headers)
/// to avoid .NET ArrayPool&lt;byte&gt;.Shared's 1 MiB bucket ceiling and eliminate Large Object Heap (LOH) churn
/// at 60-120 FPS.
/// </summary>
public static class FramePool
{
    public const int MaxPooledFrameBytes = 36 * 1024 * 1024; // 36 MiB (> 3840 * 2160 * 4 = 33,177,600 B)

    private static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Create(
        maxArrayLength: MaxPooledFrameBytes,
        maxArraysPerBucket: 4);

    public static byte[] Rent(int minimumLength)
    {
        if (minimumLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumLength), "minimumLength must be > 0.");
        }

        return minimumLength <= MaxPooledFrameBytes
            ? Pool.Rent(minimumLength)
            : new byte[minimumLength];
    }

    public static void Return(byte[]? buffer)
    {
        if (buffer != null && buffer.Length <= MaxPooledFrameBytes)
        {
            Pool.Return(buffer, clearArray: false);
        }
    }
}

/// <summary>
/// Reference-guarded pooled binary frame payload with atomic single-return semantics.
/// </summary>
public sealed class PooledFrame : IDisposable
{
    private byte[]? _buffer;

    public PooledFrame(
        byte[] buffer,
        int length,
        uint frameSeq,
        int width,
        int height,
        byte format,
        ushort flags,
        ulong timestampQpcUs,
        int originX,
        int originY,
        ulong fnv1aHash)
    {
        _buffer = buffer;
        Length = length;
        FrameSeq = frameSeq;
        Width = width;
        Height = height;
        Format = format;
        Flags = flags;
        TimestampQpcUs = timestampQpcUs;
        OriginX = originX;
        OriginY = originY;
        Fnv1aHash = fnv1aHash;
    }

    public byte[]? Buffer => Volatile.Read(ref _buffer);
    public int Length { get; }
    public uint FrameSeq { get; }
    public int Width { get; }
    public int Height { get; }
    public byte Format { get; }
    public ushort Flags { get; }
    public ulong TimestampQpcUs { get; }
    public int OriginX { get; }
    public int OriginY { get; }
    public ulong Fnv1aHash { get; }

    public void Dispose()
    {
        byte[]? buf = Interlocked.Exchange(ref _buffer, null);
        if (buf != null)
        {
            FramePool.Return(buf);
        }
    }
}

/// <summary>
/// Bounded 1-slot conflating mailbox (DropOldest semantics with deterministic pool return).
/// Bounds memory strictly to O(1) under slow WebSocket consumers while waking the writer loop immediately.
/// </summary>
public sealed class LatestFrameBox : IDisposable
{
    private PooledFrame? _pending;
    private long _droppedFrames;
    private int _disposed;
    private readonly SemaphoreSlim _signal = new(0, 1);

    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

    public void Publish(PooledFrame frame)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            frame.Dispose();
            return;
        }

        PooledFrame? overwritten = Interlocked.Exchange(ref _pending, frame);
        if (overwritten != null)
        {
            overwritten.Dispose();
            Interlocked.Increment(ref _droppedFrames);
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            Interlocked.Exchange(ref _pending, null)?.Dispose();
            return;
        }

        try
        {
            if (_signal.CurrentCount == 0)
            {
                _signal.Release();
            }
        }
        catch (SemaphoreFullException)
        {
            // Signal already set
        }
        catch (ObjectDisposedException)
        {
            Interlocked.Exchange(ref _pending, null)?.Dispose();
        }
    }

    public async Task<PooledFrame?> WaitAndTakeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && Volatile.Read(ref _disposed) == 0)
        {
            PooledFrame? immediate = Interlocked.Exchange(ref _pending, null);
            if (immediate != null)
            {
                return immediate;
            }

            try
            {
                await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
        }

        return null;
    }

    public PooledFrame? TryTake() => Interlocked.Exchange(ref _pending, null);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _pending, null)?.Dispose();
        try { _signal.Dispose(); } catch { }
    }
}
