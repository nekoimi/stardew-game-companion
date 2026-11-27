using System.Diagnostics;
using StardewAI.Companion.Mod.Domain;
using StardewAI.Companion.Mod.Navigation;
using StardewAI.Companion.Mod.Transport;

namespace StardewAI.Companion.Mod.Execution;

public sealed partial class CompanionMechanicsCoordinator
{
    private static readonly TimeSpan RouteObservationBudget = TimeSpan.FromSeconds(20);

    // One ephemeral observation, separate from the actor's active action and receipt.
    private sealed record RouteObservation(SkillExecutePayload Request, EnvelopeDto Envelope,
        NavigationSearch<Dictionary<string, object>> Search, long StartedAt);
    private RouteObservation? _routeObservation;

    private bool BeginRouteObservation(SkillExecutePayload request, EnvelopeDto envelope, out SkillResultPayload? rejection)
    {
        rejection = null;
        if (_cancelledTaskIds.Contains(request.CommandId) || _cancelledTaskIds.Contains(request.TaskId))
        {
            rejection = BuildRejection(request, "CANCELLED", "Route observation was cancelled before execution.", false);
            return false;
        }
        if (_routeObservation is not null)
        {
            rejection = BuildRejection(request, "OBSERVATION_BUSY", "A route observation is already running; await its result.", true);
            return false;
        }
        var tile = request.Parameters.Tile;
        if (tile is null && request.Parameters.NavigationTarget != "shop") throw new ArgumentException("Route destination tile is required.");
        _routeObservation = new(request, envelope,
            _navigationMachine.BeginPreviewRoute(request.Parameters.LocationId, tile is null ? _actor.Tile : new TileCoordinate(tile.X, tile.Y),
                request.Parameters.NavigationTarget ?? "exact", request.Parameters.ShopId), Stopwatch.GetTimestamp());
        return true;
    }

    private void UpdateRouteObservation()
    {
        lock (_taskLock)
        {
            if (_routeObservation is not { } query) return;
            if (Stopwatch.GetTimestamp() - query.StartedAt >= RouteObservationBudget.TotalSeconds * Stopwatch.Frequency)
            {
                FinishRouteObservation("failed", code: "ROUTE_OBSERVATION_TIMEOUT", message: "Route observation ran out of time. Try a nearer destination or inspect the current map.");
                return;
            }
            if (_actor is PlayerActor player && !player.BindingValid)
            {
                FinishRouteObservation("failed", code: "ACTOR_BINDING_LOST", message: "Native player binding changed.");
                return;
            }
            try
            {
                if (query.Search.Advance()) FinishRouteObservation("succeeded", query.Search.Result);
            }
            catch (Exception ex)
            {
                FinishRouteObservation("failed", code: "SPATIAL_OBSERVATION_UNAVAILABLE", message: ex.Message);
            }
        }
    }

    internal void CancelRouteObservation(string reason)
    {
        lock (_taskLock)
            FinishRouteObservation("cancelled", code: "CANCELLED", message: reason);
    }

    private void ClearRouteObservation()
    {
        _routeObservation?.Search.Dispose();
        _routeObservation = null;
    }

    private void FinishRouteObservation(string status, Dictionary<string, object>? route = null,
        string? code = null, string? message = null)
    {
        if (_routeObservation is not { } query) return;
        ClearRouteObservation();
        var result = new SkillResultPayload(query.Request.CommandId, query.Request.TaskId, status, 0, 0, 0,
            _observer.WorldRevision, new(), new SkillResultResources(0, 0, 0), SkillId: "inspect-route",
            Details: route is null ? null : new Dictionary<string, object> { ["route"] = route },
            Error: code is null ? null : new SkillResultError(code, message ?? code, null, false));
        _lastSkillResultPayload = result;
        if (_transportServer is not null)
            _ = _transportServer.SendSkillResultAsync(result, query.Envelope.MessageId, query.Envelope.IdempotencyKey);
    }
}
