using HarmonyLib;
using StardewValley;
using StardewAI.Companion.Mod.Domain;
namespace StardewAI.Companion.Mod.PlayerMode;
// Arbitrate native movement intentions before the game's hardware-input cleanup.
// The native Farmer Update/MovePosition collision and warp pipeline remains untouched.
public static class NativePlayerIntentHook {
    private static PlayerActor? _actor;
    private static PlayerControlAuthority? _authority;
    private static bool _installed;
    public static void Bind(PlayerActor actor, PlayerControlAuthority authority) {
        _actor=actor; _authority=authority;
        if (_installed) return;
        var harmony=new Harmony("StardewAI.NativePlayerIntent");
        harmony.Patch(AccessTools.Method(typeof(Farmer),nameof(Farmer.setMoving)),prefix:new HarmonyMethod(typeof(NativePlayerIntentHook),nameof(Moving)));
        harmony.Patch(AccessTools.Method(typeof(Character),nameof(Character.Halt)),prefix:new HarmonyMethod(typeof(NativePlayerIntentHook),nameof(Halt)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(Farmer),nameof(Farmer.Halt)),prefix:new HarmonyMethod(typeof(NativePlayerIntentHook),nameof(Halt)));
        _installed=true;
    }
    private static bool Owns(Character character) => _actor?.BindingValid == true && ReferenceEquals(character,_actor.GameFarmer) && _authority?.AgentOwns == true;
    private static bool Moving(Farmer __instance, byte __0) =>
        !Owns(__instance) || __0 is 16 or 48 || ((__0 & 64) != 0 && _actor!.HeldDirections == 0);
    private static bool Halt(Character __instance) => !Owns(__instance) || _actor!.HeldDirections == 0;
}
