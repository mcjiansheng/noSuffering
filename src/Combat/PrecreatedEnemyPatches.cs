using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace NoSuffering.Combat;

[HarmonyPatch(typeof(CombatState), nameof(CombatState.CreateCreature))]
internal static class PrecreatedEnemyScopePatch
{
    [ThreadStatic] internal static PrecreatedCombatEnemy? HpInput;

    private static void Prefix(CombatState __instance, MonsterModel monster, CombatSide side, string? slot,
        out (PrecreatedCombatEnemy? Previous, PrecreatedCombatEnemy? Current) __state)
    {
        var input = CombatService.PrecreatedEnemyInput(__instance, monster, side, slot);
        __state = (HpInput, input);
        HpInput = input;
    }

    private static void Postfix(Creature __result,
        (PrecreatedCombatEnemy? Previous, PrecreatedCombatEnemy? Current) __state)
    {
        if (__state.Current is not { } saved) return;
        // Native multiplayer scaling and combat identity/RNG creation ran normally.
        if (__result.CombatId != saved.CombatId || __result.MaxHp != saved.MaxHp)
            throw new InvalidOperationException("Precreated enemy HP scaling or combat identity differs from its baseline.");
        __result.SetCurrentHpInternal(saved.CurrentHp);
    }

    private static Exception? Finalizer(Exception? __exception,
        (PrecreatedCombatEnemy? Previous, PrecreatedCombatEnemy? Current) __state)
    {
        HpInput = __state.Previous;
        return __exception;
    }
}

[HarmonyPatch(typeof(Creature), nameof(Creature.SetUniqueMonsterHpValue))]
internal static class PrecreatedEnemyHpPatch
{
    private static bool Prefix(Creature __instance)
    {
        if (PrecreatedEnemyScopePatch.HpInput is not { } saved) return true;
        var monster = __instance.Monster;
        if (monster is null || monster.Id.ToString() != saved.ModelId || __instance.SlotName != saved.Slot ||
            saved.RawHp < monster.MinInitialHp || saved.RawHp > monster.MaxInitialHp)
            throw new InvalidOperationException("Saved precreated enemy HP is incompatible with its native monster.");
        // Equivalent output to this native method, with no second consumption of Niche RNG.
        __instance.SetMaxHpInternal(saved.RawHp);
        __instance.SetCurrentHpInternal(saved.RawHp);
        AccessTools.Property(typeof(Creature), nameof(Creature.MonsterMaxHpBeforeModification))
            .SetValue(__instance, saved.RawHp);
        return false;
    }
}
