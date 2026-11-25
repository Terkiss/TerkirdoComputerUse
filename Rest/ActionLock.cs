namespace TerkirdoComputer.Rest;

public static class ActionLock
{
    private static readonly SemaphoreSlim _semaphore = new(1, 1);
    private static readonly AsyncLocal<bool> _heldInCurrentFlow = new();

    public static bool IsHeldByCurrentFlow => _heldInCurrentFlow.Value;

    private static void EnsureNotReentrant()
    {
        if (_heldInCurrentFlow.Value)
        {
            throw new InvalidOperationException(
                "Reentrant ActionLock acquisition detected. Invoke internal *Locked action handlers when already holding ActionLock.");
        }
    }

    public static async Task<T> ExecuteAsync<T>(Func<T> action, int timeoutMs = 30000, CancellationToken cancellationToken = default)
    {
        EnsureNotReentrant();
        bool acquired = await _semaphore.WaitAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
        if (!acquired)
        {
            throw new TimeoutException($"Could not acquire desktop action lock within {timeoutMs}ms. Another action is currently executing.");
        }

        _heldInCurrentFlow.Value = true;
        try
        {
            return action();
        }
        finally
        {
            _heldInCurrentFlow.Value = false;
            _semaphore.Release();
        }
    }

    public static async Task<T> ExecuteAsync<T>(Func<Task<T>> actionAsync, int timeoutMs = 30000, CancellationToken cancellationToken = default)
    {
        EnsureNotReentrant();
        bool acquired = await _semaphore.WaitAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
        if (!acquired)
        {
            throw new TimeoutException($"Could not acquire desktop action lock within {timeoutMs}ms. Another action is currently executing.");
        }

        _heldInCurrentFlow.Value = true;
        try
        {
            return await actionAsync().ConfigureAwait(false);
        }
        finally
        {
            _heldInCurrentFlow.Value = false;
            _semaphore.Release();
        }
    }

    public static T Execute<T>(Func<T> action, int timeoutMs = 30000, CancellationToken cancellationToken = default)
    {
        EnsureNotReentrant();
        bool acquired = _semaphore.Wait(timeoutMs, cancellationToken);
        if (!acquired)
        {
            throw new TimeoutException($"Could not acquire desktop action lock within {timeoutMs}ms. Another action is currently executing.");
        }

        _heldInCurrentFlow.Value = true;
        try
        {
            return action();
        }
        finally
        {
            _heldInCurrentFlow.Value = false;
            _semaphore.Release();
        }
    }

    public static void Execute(Action action, int timeoutMs = 30000, CancellationToken cancellationToken = default)
    {
        Execute(() =>
        {
            action();
            return true;
        }, timeoutMs, cancellationToken);
    }
}
