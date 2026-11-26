namespace MangaCli;

/// <summary>
/// Single-instance coordination shared with the GUI. The mutex is created with
/// <c>initiallyOwned: false</c> purely for its <c>createdNew</c> flag: that is the same signal the
/// GUI uses, and because ownership is never taken there is no mutex thread-affinity issue —
/// the handle is just kept open for the duration of the command.
/// </summary>
public sealed class ProcessLock : IDisposable
{
    private readonly Mutex _mutex;

    private ProcessLock(Mutex mutex)
    {
        _mutex = mutex;
    }

    public static bool TryAcquire(string name, out ProcessLock? handle)
    {
        var mutex = new Mutex(initiallyOwned: false, name, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            handle = null;
            return false;
        }

        handle = new ProcessLock(mutex);
        return true;
    }

    /// <summary>True when some process (GUI or another MangaCli) currently holds the named mutex.</summary>
    public static bool IsHeld(string name)
    {
        try
        {
            using var existing = Mutex.OpenExisting(name);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // The mutex exists but this process may not open it — it is held by someone else.
            return true;
        }
    }

    public void Dispose() => _mutex.Dispose();
}
