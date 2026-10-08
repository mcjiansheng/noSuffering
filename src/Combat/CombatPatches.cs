using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Rooms;

namespace NoSuffering.Combat;

[HarmonyPatch(typeof(CombatRoom), "StartCombat")]
internal static class CombatBaselinePatch
{
    private static void Prefix(CombatRoom __instance) => CombatService.BeforeCombatStart(__instance);
}

[HarmonyPatch(typeof(CardPile), nameof(CardPile.RandomizeOrderInternal))]
internal static class InitialDrawOrderPatch
{
    [ThreadStatic] internal static Player? InitializingPlayer;
    [ThreadStatic] internal static CombatState? InitializingState;
    [ThreadStatic] internal static CombatState? SettingUpState;

    internal static bool IsInitializing(Player player, CombatState state) =>
        ReferenceEquals(InitializingPlayer, player) && ReferenceEquals(InitializingState, state);

    private static void Postfix(CardPile __instance, Player player, CombatState state)
    {
        if (IsInitializing(player, state) && ReferenceEquals(__instance, player.PlayerCombatState?.DrawPile))
            CombatService.RecordInitialOrder(__instance.Cards, player);
    }

    // The beta implementation calls UnstableShuffle, then TestRngInjector, then
    // Hook.ModifyShuffleOrder. Insert after native RNG consumption but before rules.
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var matches = 0;
        foreach (var instruction in instructions)
        {
            yield return instruction;
            if (instruction.operand is not System.Reflection.MethodInfo method || method.Name != "UnstableShuffle")
                continue;
            matches++;
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(CardPile), "_cards"));
            yield return new CodeInstruction(OpCodes.Ldarg_1);
            yield return new CodeInstruction(OpCodes.Ldarg_3);
            yield return new CodeInstruction(OpCodes.Call,
                AccessTools.Method(typeof(CombatService), nameof(CombatService.ReplaceInitialOrder)));
        }
        if (matches != 1)
            throw new InvalidOperationException("Unsupported initial shuffle implementation: expected one native shuffle call.");
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.PopulateCombatState))]
internal static class InitialDrawOrderScopePatch
{
    private static void Prefix(Player __instance, CombatState state,
        out (Player? Player, CombatState? State) __state)
    {
        __state = (InitialDrawOrderPatch.InitializingPlayer, InitialDrawOrderPatch.InitializingState);
        if (ReferenceEquals(InitialDrawOrderPatch.SettingUpState, state))
        {
            InitialDrawOrderPatch.InitializingPlayer = __instance;
            InitialDrawOrderPatch.InitializingState = state;
        }
    }

    private static Exception? Finalizer(Exception? __exception,
        (Player? Player, CombatState? State) __state)
    {
        InitialDrawOrderPatch.InitializingPlayer = __state.Player;
        InitialDrawOrderPatch.InitializingState = __state.State;
        return __exception;
    }
}

[HarmonyPatch(typeof(CombatManager), nameof(CombatManager.SetUpCombat))]
internal static class InitialDrawOrderSetupScopePatch
{
    private static void Prefix(CombatState state, out CombatState? __state)
    {
        CombatService.CompletePrecreatedEnemyRestore(state);
        __state = InitialDrawOrderPatch.SettingUpState;
        InitialDrawOrderPatch.SettingUpState = state;
    }

    private static Exception? Finalizer(Exception? __exception, CombatState? __state)
    {
        InitialDrawOrderPatch.SettingUpState = __state;
        return __exception;
    }
}
