using StardewAI.Companion.Mod.Domain;
using StardewAI.Companion.Mod.Navigation;
using StardewAI.Companion.Mod.Transport;

namespace StardewAI.Companion.Mod.Execution;

internal sealed class BedtimeRoutePlanner
{
    private const int FallbackTravelMinutes = 60;
    private readonly IFarmerActor _actor;
    private readonly NavigationStateMachine _navigation;
    private readonly CompanionBedtimeRouteEstimate _estimate = new();
    private NavigationSearch<Dictionary<string, object>>? _search;
    private (int Day, int Bedtime, string Location, TileCoordinate Tile, string Home, TileCoordinate Target, int Clock) _origin;
    private (int Day, int Bedtime, string Location, string Home, TileCoordinate Target, int Clock)? _lastPreview;

    public BedtimeRoutePlanner(IFarmerActor actor, NavigationStateMachine navigation)
    {
        _actor = actor;
        _navigation = navigation;
    }

    public void StopPreview()
    {
        _search?.Dispose();
        _search = null;
    }

    public void Reset()
    {
        StopPreview();
        _estimate.Clear();
        _lastPreview = null;
    }

    public BedtimeScheduleSnapshot Update(int day, int now, int bedtime, string? home, TileCoordinate? target,
        Func<string, TileCoordinate, TileCoordinate?>? findApproach = null)
    {
        int? travel = null;
        string location = _actor.LocationName;
        TileCoordinate tile = _actor.Tile;
        if (home is not null && target is { } destination && CompanionBedtime.ShouldPreviewRoute(now, bedtime))
        {
            if (_estimate.TryGet(day, bedtime, location, tile, home, destination, now, out travel))
                StopPreview();
            else
            {
                if (_search is not null && (_origin.Day != day || _origin.Bedtime != bedtime || _origin.Location != location
                    || _origin.Home != home || _origin.Target != destination
                    || Math.Abs(_origin.Tile.X - tile.X) + Math.Abs(_origin.Tile.Y - tile.Y) > 12))
                    StopPreview();
                if (_search is null && _lastPreview != (day, bedtime, location, home, destination, now))
                {
                    _origin = (day, bedtime, location, tile, home, destination, now);
                    var approach = findApproach is null ? destination : findApproach(home, destination);
                    if (approach is { } standingTile) _search = _navigation.BeginPreviewRoute(home, standingTile);
                    else _lastPreview = (day, bedtime, location, home, destination, now);
                }
                if (_search is not null && _search.Advance(milliseconds: 2))
                {
                    if (_search.Result.TryGetValue("estimatedGameMinutes", out var estimate) && estimate is int minutes) travel = minutes;
                    _estimate.Store(day, bedtime, location, _origin.Tile, home, destination, _origin.Clock, travel);
                    _lastPreview = (day, bedtime, location, home, destination, now);
                    StopPreview();
                }
            }
        }
        else StopPreview();
        int departure = CompanionBedtime.ReturnHomeAt(bedtime, travel ?? (home is null || target is null ? 30 : FallbackTravelMinutes));
        return new(departure, travel, now >= departure);
    }
}
