using System.Globalization;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Rooms;
using NoSuffering.Config;
using Bridge = NoSuffering.GameBridge.GameBridge;

namespace NoSuffering.Combat;

public sealed record BossEnemyHealth(uint CombatId, string ModelId, string? Slot, int AddedHp);
public sealed record BossHealthRecord(string RunId, string ContextId, List<BossEnemyHealth> Enemies);

// Native combat saves recreate creatures rather than serializing their live HP.
// Store only this operation's additive contribution, leaving native/mod HP intact.
public static class BossHealthService
{
    private const int NativeMaxHp = 999999999;
    private static BossHealthRecord? _record;
    private sealed class AppliedHealth { public int Amount; }
    private static readonly ConditionalWeakTable<Creature, AppliedHealth> Applied = new();

    public static BossHealthRecord? CaptureState() => _record is null ? null : Copy(_record);

    public static void RestoreState(BossHealthRecord? record)
    {
        if (record is not null && (string.IsNullOrEmpty(record.RunId) ||
            string.IsNullOrEmpty(record.ContextId) || record.Enemies.Any(e =>
                string.IsNullOrEmpty(e.ModelId) || e.AddedHp <= 0 || e.AddedHp > NativeMaxHp) ||
            record.Enemies.Select(e => e.CombatId).Distinct().Count() != record.Enemies.Count))
            throw new InvalidOperationException("Invalid boss health adjustment record.");
        _record = record is null ? null : Copy(record);
    }

    public static string? GetUnavailableReason(HostRules rules)
    {
        if (!rules.EnableBossHealthIncrease) return "此功能已关闭";
        if (Bridge.State.CurrentActIndex != 2) return "仅第三幕可用";
        if (Bridge.State.CurrentRoom is not CombatRoom { RoomType: RoomType.Boss } room ||
            !CombatManager.Instance.IsInProgress) return "仅在Boss战斗中可用";
        if (!room.Enemies.Any(enemy => enemy.IsAlive)) return "当前没有存活的敌人";
        return null;
    }

    public static Task ExecuteAsync(int percent)
    {
        if (percent is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(percent));
        if (Bridge.State.CurrentActIndex != 2 ||
            Bridge.State.CurrentRoom is not CombatRoom { RoomType: RoomType.Boss } room ||
            !CombatManager.Instance.IsInProgress)
            throw new InvalidOperationException("仅第三幕Boss战斗可增加生命");
        var state = room.CombatState;
        var record = Matches(state) ? _record! : new BossHealthRecord(Bridge.RunKey, Context(state), new());
        // Validate the entire operation before changing any creature. At the native
        // HP ceiling there is no exact percentage increase, so report it explicitly.
        var changes = state.Enemies.Where(enemy => enemy.IsAlive).Select(enemy =>
        {
            if (enemy.CombatId is not uint id || enemy.Monster is null)
                throw new InvalidOperationException("Enemy lacks its native combat identity.");
            var amount = CalculateIncrease(enemy.MaxHp, percent);
            var prior = record.Enemies.SingleOrDefault(entry => entry.CombatId == id);
            if (prior is not null && !IdentityMatches(prior, enemy))
                throw new InvalidOperationException("Boss health creature identity differs from its saved adjustment.");
            var total = checked((prior?.AddedHp ?? 0) + amount);
            if (total > NativeMaxHp)
                throw new InvalidOperationException("Boss health adjustment exceeds the native HP limit.");
            return (Enemy: enemy, Amount: amount, Entry: new BossEnemyHealth(id,
                enemy.Monster.Id.ToString(), enemy.SlotName, total));
        }).ToList();
        if (changes.Count == 0) throw new InvalidOperationException("当前没有存活的敌人");
        var entries = new List<BossEnemyHealth>(record.Enemies);
        foreach (var change in changes)
        {
            AddHealth(change.Enemy, change.Amount);
            Applied.GetValue(change.Enemy, static _ => new AppliedHealth()).Amount = change.Entry.AddedHp;
            entries.RemoveAll(entry => entry.CombatId == change.Entry.CombatId);
            entries.Add(change.Entry);
        }
        _record = record with { Enemies = entries.OrderBy(entry => entry.CombatId).ToList() };
        return Task.CompletedTask;
    }

    internal static int CalculateIncrease(int maxHp, int percent)
    {
        if (maxHp <= 0 || percent is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(percent));
        var amount = ((long)maxHp * percent + 99) / 100;
        if (amount > NativeMaxHp - maxHp)
            throw new InvalidOperationException("敌人生命已接近上限，请降低增加比例");
        return checked((int)amount);
    }

    internal static void RestoreInitialEnemies(CombatState state)
    {
        if (!Matches(state)) return;
        foreach (var enemy in state.Enemies) RestoreEnemy(state, enemy);
    }

    internal static void RestoreEnemy(CombatState state, Creature enemy)
    {
        if (!Matches(state) || enemy.Side != CombatSide.Enemy || !enemy.IsAlive) return;
        var entry = _record!.Enemies.SingleOrDefault(saved => saved.CombatId == enemy.CombatId);
        // A different summon sequence can reuse an ID. Never apply its adjustment
        // to an unrelated creature or a later form with a different native model.
        if (entry is null || !IdentityMatches(entry, enemy)) return;
        var applied = Applied.GetValue(enemy, static _ => new AppliedHealth());
        var remaining = entry.AddedHp - applied.Amount;
        if (remaining <= 0) return;
        if ((long)enemy.MaxHp + remaining > NativeMaxHp)
            throw new InvalidOperationException("Restored boss health exceeds the native HP limit.");
        AddHealth(enemy, remaining);
        applied.Amount = entry.AddedHp;
    }

    private static void AddHealth(Creature enemy, int amount)
    {
        var current = enemy.CurrentHp;
        // These native setters raise MaxHpChanged/CurrentHpChanged notifications.
        // No healing/damage hooks or revival are introduced by this operation.
        enemy.SetMaxHpInternal(enemy.MaxHp + amount);
        enemy.SetCurrentHpInternal(current + amount);
    }

    private static bool IdentityMatches(BossEnemyHealth entry, Creature enemy) =>
        entry.ModelId == enemy.Monster?.Id.ToString() && entry.Slot == enemy.SlotName;

    private static bool Matches(CombatState state) => _record is not null &&
        state.RunState.CurrentActIndex == 2 && state.Encounter?.RoomType == RoomType.Boss &&
        _record.RunId == Bridge.RunKey && _record.ContextId == Context(state);

    private static string Context(CombatState state)
    {
        var run = state.RunState;
        var coord = run.CurrentMapCoord;
        return string.Join("/", run.CurrentActIndex.ToString(CultureInfo.InvariantCulture),
            run.TotalFloor.ToString(CultureInfo.InvariantCulture),
            coord?.col.ToString(CultureInfo.InvariantCulture) ?? "none",
            coord?.row.ToString(CultureInfo.InvariantCulture) ?? "none", state.Encounter!.Id.ToString());
    }

    private static BossHealthRecord Copy(BossHealthRecord record) =>
        record with { Enemies = new List<BossEnemyHealth>(record.Enemies) };
}

[HarmonyPatch(typeof(Player), nameof(Player.PopulateCombatState))]
internal static class BossHealthInitialEnemiesPatch
{
    // SetUpCombat has already run native multiplayer scaling, and the initial
    // shuffle and opening turn have not started. Repeated players are idempotent.
    private static void Prefix(CombatState state) => BossHealthService.RestoreInitialEnemies(state);
}

[HarmonyPatch(typeof(CombatState), nameof(CombatState.CreateCreature))]
internal static class BossHealthRecreatedSummonPatch
{
    private static void Postfix(CombatState __instance, Creature __result)
    {
        if (CombatManager.Instance.IsInProgress)
            BossHealthService.RestoreEnemy(__instance, __result);
    }
}
