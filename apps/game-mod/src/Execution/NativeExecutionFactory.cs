using StardewAI.Companion.Mod.Adapters;
using StardewAI.Companion.Mod.Avatar;
using StardewAI.Companion.Mod.Domain;
using StardewAI.Companion.Mod.Navigation;
using StardewAI.Companion.Mod.Observation;
using StardewModdingAPI;

namespace StardewAI.Companion.Mod.Execution;

internal static class NativeExecutionFactory
{
    public static CompanionMechanicsCoordinator Create(
        IModHelper helper, IMonitor monitor, IFarmerActor actor, IWorldObserver observer,
        SameMapNavigator navigator, string dataDirectory, CompanionAvatar? avatar = null)
    {
        var coordinator = new CompanionMechanicsCoordinator(actor, observer, navigator,
            new NormalWateringCanAdapter(observer, monitor), avatar, monitor.Log,
            harvestAdapter: new NormalHarvestAdapter(observer, monitor),
            chestAdapter: new NormalChestAdapter(observer, monitor),
            hoeAdapter: new NormalHoeAdapter(observer, monitor),
            plantAdapter: new NormalPlantAdapter(observer, monitor),
            shippingAdapter: new NormalShippingAdapter(observer, monitor),
            mapGraph: avatar is null ? null : new WorldMapGraph(null, monitor.Log),
            purchaseAdapter: new NormalPurchaseAdapter(observer, monitor),
            nativeActionAdapter: new NormalNativeActionAdapter(observer, monitor));
        coordinator.ConfigureExtensions(Path.Combine(dataDirectory, "abilities", "packages"),
            helper.ModRegistry, helper.DirectoryPath);
        return coordinator;
    }
}
