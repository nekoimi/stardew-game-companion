using StardewAI.Companion.Mod.Domain;

namespace StardewAI.Companion.Mod.Navigation;

/// <summary>One cancellable path calculation for a tick-driven action. Never queues
/// work or moves the actor; native world reads happen only in Advance on the game thread.</summary>
internal sealed class ActionPathPlanning : IDisposable
{
    private NavigationSearch<PathResult>? _search;
    private Func<NavigationSearch<PathResult>>? _begin;
    private Action<PathResult>? _accept;
    private IFarmerActor? _actor;
    private AuthoritativePose? _origin;

    public bool IsPending => _begin is not null;

    public void Start(IFarmerActor actor, Func<NavigationSearch<PathResult>> begin, Action<PathResult> accept)
    {
        Dispose();
        _actor = actor;
        _begin = begin;
        _accept = accept;
    }

    public void Advance()
    {
        if (_begin is null) return;
        var origin = new AuthoritativePose(_actor!.LocationName, _actor.Tile, _actor.Facing);
        if (_search is null || origin != _origin)
        {
            _search?.Dispose();
            _origin = origin;
            _search = _begin();
        }
        if (!_search.Advance()) return;
        var result = _search.Result;
        var accept = _accept!;
        Dispose();
        accept(result);
    }

    public void Dispose()
    {
        _search?.Dispose();
        _search = null;
        _begin = null;
        _accept = null;
        _actor = null;
        _origin = null;
    }
}
