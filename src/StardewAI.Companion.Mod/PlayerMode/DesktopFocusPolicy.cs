using System.Reflection.Emit;
using HarmonyLib;
using StardewValley;

namespace StardewAI.Companion.Mod.PlayerMode;

/// <summary>Apply the desktop focus policy at the native read, without writing it to game settings.</summary>
internal static class DesktopFocusPolicy
{
    private static Func<bool>? _shouldRunInBackground;

    public static void Install(Func<bool> shouldRunInBackground)
    {
        _shouldRunInBackground = shouldRunInBackground;
        new Harmony("StardewAI.Companion.DesktopFocus").Patch(
            AccessTools.Method(typeof(Game1), "_update"),
            transpiler: new HarmonyMethod(typeof(DesktopFocusPolicy), nameof(UseEffectiveFocusOption)));
    }

    private static IEnumerable<CodeInstruction> UseEffectiveFocusOption(IEnumerable<CodeInstruction> instructions)
    {
        var option = AccessTools.Field(typeof(Options), nameof(Options.pauseWhenOutOfFocus));
        var effective = AccessTools.Method(typeof(DesktopFocusPolicy), nameof(ShouldPauseWhenUnfocused));
        foreach (var instruction in instructions)
        {
            if (instruction.LoadsField(option))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = effective;
            }
            yield return instruction;
        }
    }

    private static bool ShouldPauseWhenUnfocused(Options options) =>
        options.pauseWhenOutOfFocus && _shouldRunInBackground?.Invoke() != true;
}
