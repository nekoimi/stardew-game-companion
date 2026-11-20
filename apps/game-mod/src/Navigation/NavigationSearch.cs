using System.Diagnostics;

namespace StardewAI.Companion.Mod.Navigation;

/// <summary>A disposable, cooperative search. Native world reads stay on the caller's
/// game thread; yielding returns control to SMAPI between small search expansions.</summary>
public abstract class NavigationSearch : IDisposable
{
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private readonly Stack<NavigationSearch> _stack = new();
    private IEnumerator<NavigationSearch?>? _steps;
    private bool _disposed;
    public bool IsComplete { get; private set; }

    protected void Initialize(IEnumerable<NavigationSearch?> steps) => _steps = steps.GetEnumerator();

    public bool Advance(int maxSteps = 256, double milliseconds = 3)
    {
        if (_ownerThread != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Navigation searches must advance on their owning game thread.");
        if (_disposed) throw new ObjectDisposedException(GetType().Name);
        if (IsComplete) return true;
        var timer = Stopwatch.StartNew();
        if (_stack.Count == 0) _stack.Push(this);
        for (int step = 0; step < maxSteps; step++)
        {
            var current = _stack.Peek();
            if (current._steps!.MoveNext())
            {
                if (current._steps.Current is { IsComplete: false } child) _stack.Push(child);
            }
            else
            {
                current._steps.Dispose();
                current._steps = null;
                current.IsComplete = true;
                _stack.Pop();
                if (_stack.Count == 0) return true;
            }
            if (timer.Elapsed.TotalMilliseconds >= milliseconds) break;
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // The root can also be in its own stack. Dispose iterators directly.
        foreach (var search in _stack)
        {
            search._steps?.Dispose();
            search._steps = null;
            search._disposed = true;
        }
        _stack.Clear();
        _steps?.Dispose();
        _steps = null;
    }
}

public sealed class NavigationSearch<T> : NavigationSearch
{
    public T Result { get; set; } = default!;

    public NavigationSearch(Func<NavigationSearch<T>, IEnumerable<NavigationSearch?>> search) => Initialize(search(this));

    /// <summary>Compatibility for synchronous callers. Tick-driven consumers use Advance.</summary>
    public T Complete()
    {
        while (!Advance(int.MaxValue, double.PositiveInfinity)) { }
        return Result;
    }
}
