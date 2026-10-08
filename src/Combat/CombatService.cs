using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Saves;
using NoSuffering.Checkpoints;
using Bridge = NoSuffering.GameBridge.GameBridge;

namespace NoSuffering.Combat;

public sealed record CombatEnemy(string ModelId, string? Slot);
public sealed record PrecreatedCombatEnemy(string ModelId, string? Slot, int RawHp, int MaxHp,
    int CurrentHp, uint CombatId);

// JSON companion data contains only immutable snapshot text and value records.
public sealed record CombatRecord
{
    public int FormatVersion { get; init; } = 1;
    public required string RunId { get; init; }
    public required string ContextId { get; init; }
    public required string Baseline { get; init; }
    public int Attempt { get; init; }
    public Dictionary<ulong, ulong> AttemptSeeds { get; init; } = new();
    public Dictionary<ulong, string> OrderDigests { get; init; } = new();
    public List<CombatEnemy> Enemies { get; init; } = new();
    public ParentEventRecord? ParentEvent { get; init; }
    public List<PrecreatedCombatEnemy> PrecreatedEnemies { get; init; } = new();
    public string? UnavailableReason { get; init; }
    public string? RerollUnavailableReason { get; init; }
}

public static class CombatService
{
    private static CombatRecord? _record;
    private static CombatState? _precreatedRestoreState;

    public static string? Baseline => _record?.Baseline;
    public static string? ContextId => _record?.ContextId;
    public static int Attempt => _record?.Attempt ?? 0;
    public static string? UnavailableReason => _record?.UnavailableReason;
    public static string? RerollUnavailableReason => _record?.RerollUnavailableReason;
    public static IReadOnlyDictionary<ulong, ulong> CurrentAttemptSeeds =>
        new Dictionary<ulong, ulong>(_record?.AttemptSeeds ?? new Dictionary<ulong, ulong>());
    public static IReadOnlyDictionary<ulong, string> CurrentOrderDigests =>
        new Dictionary<ulong, string>(_record?.OrderDigests ?? new Dictionary<ulong, string>());

    public static CombatRecord? Save() => _record is null ? null : Copy(_record);

    public static void Restore(CombatRecord? record)
    {
        if (record is not null)
        {
            if (record.FormatVersion != 1 || record.Attempt < 0 ||
                string.IsNullOrEmpty(record.RunId) || string.IsNullOrEmpty(record.ContextId) ||
                string.IsNullOrEmpty(record.Baseline))
                throw new InvalidOperationException("Invalid combat restart record.");
            _ = Bridge.Thaw(record.Baseline);
            foreach (var (player, seed) in record.AttemptSeeds)
                if (seed != Seed(record.RunId, record.ContextId, record.Attempt, player))
                    throw new InvalidOperationException("Combat attempt seed does not match its context.");
        }
        _record = record is null ? null : Copy(record);
        _precreatedRestoreState = null;
    }

    // The coordinator stages this before native loading and restores the old record on failure.
    // Merely preparing the native snapshot must not commit a successful attempt.
    public static SerializableRun PrepareRestart(bool reroll)
    {
        RequireCurrent();
        if (reroll && _record!.RerollUnavailableReason is { } reason)
            throw new InvalidOperationException(reason);
        if (reroll && Attempt == int.MaxValue)
            throw new InvalidOperationException("Combat attempt counter is exhausted.");
        return Bridge.Thaw(_record!.Baseline);
    }

    public static void SetAttempt(int attempt)
    {
        if (attempt < 0) throw new ArgumentOutOfRangeException(nameof(attempt));
        var record = _record ?? throw new InvalidOperationException("No combat baseline is available.");
        _record = record with
        {
            Attempt = attempt,
            AttemptSeeds = record.AttemptSeeds.Keys.ToDictionary(player => player,
                player => Seed(record.RunId, record.ContextId, attempt, player)),
            OrderDigests = attempt == record.Attempt
                ? new Dictionary<ulong, string>(record.OrderDigests) : new()
        };
    }

    internal static void BeforeCombatStart(CombatRoom room)
    {
        var state = room.CombatState;
        _precreatedRestoreState = null;
        // StartCombat performs this same call before any enemy HP, opening effects or draws.
        // Generating here lets Capture store the chosen encounter and pre-initialization run RNG.
        if (!room.Encounter.HaveMonstersBeenGenerated)
            room.Encounter.GenerateMonstersWithSlots(state.RunState);
        var context = Context(state);
        var runId = Bridge.RunKey;
        var enemies = room.Encounter.MonstersWithSlots
            .Select(enemy => new CombatEnemy(enemy.Item1.Id.ToString(), enemy.Item2)).ToList();
        if (_record is not null && _record.RunId == runId && _record.ContextId == context)
        {
            if (!_record.Enemies.SequenceEqual(enemies))
                throw new InvalidOperationException("Restored encounter differs from the combat baseline.");
            ValidatePlayers(state);
            if (_record.PrecreatedEnemies.Count > 0 && room.ShouldCreateCombat)
            {
                if (state.Enemies.Count != 0)
                    throw new InvalidOperationException("Precreated enemy restoration requires a fresh combat state.");
                _precreatedRestoreState = state;
            }
            return;
        }
        if (Bridge.IsRestoring)
            throw new InvalidOperationException("Loaded combat does not match the synchronized combat baseline.");
        var snapshot = Bridge.Capture(room);
        var unavailable = ParentEventState.UnavailableReason(room);
        if (!room.ShouldCreateCombat && room.ParentEventId is null)
            unavailable = "该战斗入口尚不支持重开";
        _record = new CombatRecord
        {
            RunId = runId,
            ContextId = context,
            Baseline = Bridge.Freeze(snapshot),
            Attempt = 0,
            AttemptSeeds = state.Players.ToDictionary(player => player.NetId,
                player => Seed(runId, context, 0, player.NetId)),
            Enemies = enemies,
            ParentEvent = ParentEventState.Capture(room),
            PrecreatedEnemies = room.ShouldCreateCombat || unavailable is not null ? new() : CapturePrecreatedEnemies(room),
            UnavailableReason = unavailable
        };
    }

    private static List<PrecreatedCombatEnemy> CapturePrecreatedEnemies(CombatRoom room)
    {
        // Combat-layout events initialized these enemies before their combat option.
        // Baseline Niche RNG already includes their HP draws; replay must not draw again.
        if (room.ParentEventId is null)
            throw new InvalidOperationException("Precreated combat enemies require a supported parent event.");
        return room.Encounter.MonstersWithSlots.Select(item =>
        {
            var creature = room.CombatState.Enemies.Single(enemy => ReferenceEquals(enemy.Monster, item.Item1));
            if (creature.MonsterMaxHpBeforeModification is not int rawHp || creature.CombatId is not uint id)
                throw new InvalidOperationException("Precreated enemy lacks its native initial HP or combat identity.");
            return new PrecreatedCombatEnemy(item.Item1.Id.ToString(), item.Item2,
                rawHp, creature.MaxHp, creature.CurrentHp, id);
        }).ToList();
    }

    internal static PrecreatedCombatEnemy? PrecreatedEnemyInput(CombatState state, MonsterModel monster,
        CombatSide side, string? slot)
    {
        if (!ReferenceEquals(state, _precreatedRestoreState) || side != CombatSide.Enemy) return null;
        var saved = _record!.PrecreatedEnemies;
        var index = state.Enemies.Count;
        if (index >= saved.Count || saved[index].ModelId != monster.Id.ToString() || saved[index].Slot != slot)
            throw new InvalidOperationException("Precreated enemy initialization order differs from its baseline.");
        return saved[index];
    }

    internal static void CompletePrecreatedEnemyRestore(CombatState state)
    {
        if (!ReferenceEquals(state, _precreatedRestoreState)) return;
        if (state.Enemies.Count != _record!.PrecreatedEnemies.Count)
            throw new InvalidOperationException("Precreated enemy restoration did not recreate the complete encounter.");
        _precreatedRestoreState = null; // All later summons use the untouched native HP/RNG path.
    }

    internal static void ReplaceInitialOrder(List<CardModel> cards, Player player, CombatState state)
    {
        if (!InitialDrawOrderPatch.IsInitializing(player, state) ||
            !ReferenceEquals(cards, player.PlayerCombatState?.DrawPile.Cards)) return;
        if (_record is null || _record.RunId != Bridge.RunKey || _record.ContextId != Context(state))
            throw new InvalidOperationException("Initial shuffle has no matching combat baseline.");
        if (Attempt == 0) return; // Attempt zero keeps the exact native output and consumption.
        if (!_record.AttemptSeeds.TryGetValue(player.NetId, out var seed))
            throw new InvalidOperationException("Initial shuffle player is missing from the combat attempt.");

        // DeckVersion maps every newly cloned runtime card to its logical deck card.
        // Deck order is serialized natively, including duplicates and permanent properties.
        var deckIndices = player.Deck.Cards.Select((card, index) => (card, index))
            .ToDictionary(item => item.card, item => item.index);
        if (cards.Any(card => card.DeckVersion is null || !deckIndices.ContainsKey(card.DeckVersion)))
            throw new InvalidOperationException("An initial draw-pile card has no canonical deck position.");
        cards.Sort((left, right) => deckIndices[left.DeckVersion!].CompareTo(deckIndices[right.DeckVersion!]));
        for (var i = cards.Count - 1; i > 0; i--)
        {
            var j = NextIndex(ref seed, i + 1);
            (cards[i], cards[j]) = (cards[j], cards[i]);
        }
        // Native ModifyShuffleOrder and first-turn Innate/bottom rules execute after this.
    }

    internal static void RecordInitialOrder(IReadOnlyList<CardModel> cards, Player player)
    {
        var record = _record ?? throw new InvalidOperationException("No combat baseline for initial order digest.");
        var deckIndices = player.Deck.Cards.Select((card, index) => (card, index))
            .ToDictionary(item => item.card, item => item.index);
        if (cards.Any(card => card.DeckVersion is null || !deckIndices.ContainsKey(card.DeckVersion)))
        {
            // Unknown initial-card injection must never prevent a normal native battle.
            // No BaseLib runtime/source was supplied to establish a stable extra-card identity.
            _record = record with { RerollUnavailableReason = "当前额外起手卡暂不支持刷新牌序" };
            CompanionStore.PairWithLastNativeSave();
            return;
        }
        var canonicalOrder = string.Join(",", cards.Select(card =>
            card.DeckVersion is not null && deckIndices.TryGetValue(card.DeckVersion, out var index)
                ? index.ToString(CultureInfo.InvariantCulture)
                : throw new InvalidOperationException("Initial card has no canonical deck position.")));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalOrder)));
        if (record.OrderDigests.TryGetValue(player.NetId, out var expected) && expected != digest)
            throw new InvalidOperationException("Restored initial draw order differs from the active attempt.");
        var digests = new Dictionary<ulong, string>(record.OrderDigests) { [player.NetId] = digest };
        _record = record with { OrderDigests = digests };
        CompanionStore.PairWithLastNativeSave();
    }

    private static void RequireCurrent()
    {
        if (_record is null || _record.RunId != Bridge.RunKey || !CombatManager.Instance.IsInProgress)
            throw new InvalidOperationException("No active combat restart baseline is available.");
        if (_record.UnavailableReason is { } reason) throw new InvalidOperationException(reason);
    }

    private static void ValidatePlayers(CombatState state)
    {
        if (!_record!.AttemptSeeds.Keys.Order().SequenceEqual(state.Players.Select(player => player.NetId).Order()))
            throw new InvalidOperationException("Combat baseline player roster does not match the loaded run.");
    }

    private static string Context(CombatState state)
    {
        var run = state.RunState;
        var coord = run.CurrentMapCoord;
        return string.Join("/", run.CurrentActIndex.ToString(CultureInfo.InvariantCulture),
            run.TotalFloor.ToString(CultureInfo.InvariantCulture),
            coord?.col.ToString(CultureInfo.InvariantCulture) ?? "none",
            coord?.row.ToString(CultureInfo.InvariantCulture) ?? "none", state.Encounter!.Id.ToString());
    }

    private static CombatRecord Copy(CombatRecord record) => record with
    {
        AttemptSeeds = new Dictionary<ulong, ulong>(record.AttemptSeeds),
        OrderDigests = new Dictionary<ulong, string>(record.OrderDigests),
        Enemies = new List<CombatEnemy>(record.Enemies),
        PrecreatedEnemies = new List<PrecreatedCombatEnemy>(record.PrecreatedEnemies),
        ParentEvent = record.ParentEvent is null ? null : record.ParentEvent with
        {
            Players = record.ParentEvent.Players.Select(player => player with { }).ToList()
        }
    };

    private static ulong Seed(string run, string context, int attempt, ulong player)
    {
        // Length-prefixed UTF-8 avoids delimiter ambiguities; integers use invariant encoding.
        var fields = new[] { "NoSuffering/combat/v1", run, context,
            attempt.ToString(CultureInfo.InvariantCulture), player.ToString(CultureInfo.InvariantCulture) };
        using var buffer = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (var field in fields)
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            buffer.Write(length);
            buffer.Write(bytes);
        }
        return BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(buffer.ToArray()));
    }

    private static int NextIndex(ref ulong state, int bound)
    {
        var range = (ulong)bound;
        var threshold = unchecked(0UL - range) % range;
        ulong value;
        do
        {
            state = unchecked(state + 0x9e3779b97f4a7c15UL);
            value = state;
            value = unchecked((value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL);
            value = unchecked((value ^ (value >> 27)) * 0x94d049bb133111ebUL);
            value ^= value >> 31;
        } while (value < threshold);
        return (int)(value % range);
    }
}
