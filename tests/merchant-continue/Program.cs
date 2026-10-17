using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Rooms;
using NoSuffering.Shops;
using Bridge = NoSuffering.GameBridge.GameBridge;

const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
int checks = 0;
void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
var harmony = new Harmony("NoSuffering.tests.merchant-continue");
// Isolate the load decision from Godot; production stock restore and the
// Harmony prefixes themselves run against each branch's real assemblies.
harmony.Patch(typeof(ShopService).GetProperty("Context", Static)!.GetMethod!,
    prefix: new HarmonyMethod(typeof(ContextFixture), nameof(ContextFixture.Prefix)));
var decision = typeof(Bridge).GetNestedType("LoadDecision", BindingFlags.NonPublic)!.GetMethod("Prefix", Static)!;
var before = typeof(Bridge).GetNestedType("OpeningRoomEffects", BindingFlags.NonPublic)!.GetMethod("Prefix", Static)!;
void Field(string name, object? value) => typeof(Bridge).GetField(name, Static)!.SetValue(null, value);
AbstractRoom? Decide(AbstractRoom? room, bool continuing, bool restoring = false)
{
    Field("_continuingNative", continuing);
    Field("<IsRestoring>k__BackingField", restoring);
    object?[] args = [room, null];
    Require((bool)decision.Invoke(null, args)!, "Merchant decision replaced the native load task.");
    Require(args[1] is null, "Merchant decision bypassed native map loading.");
    return (AbstractRoom?)args[0];
}
var stock = new ShopRecord("paired-location", new() { [1] = new(123, true, [], false) });
ShopService.RestoreState(stock);
Require(Decide(null, false) is null, "Ordinary room entry consumed paired stock.");
var restored = Decide(null, true);
Require(restored is MerchantRoom, "Native Continue did not supply the saved merchant.");
object?[] hookArgs = [restored, null];
Require(!(bool)before.Invoke(null, hookArgs)! && ReferenceEquals(hookArgs[1], Task.CompletedTask),
    "Restored merchant repeated BeforeRoomEntered effects.");
hookArgs = [new MerchantRoom(), null];
Require((bool)before.Invoke(null, hookArgs)!, "Another merchant lost its entry hook.");
Require(Decide(null, false, true) is MerchantRoom, "Committed snapshot load did not restore its merchant.");
var existing = new MapRoom();
Require(ReferenceEquals(Decide(existing, true), existing), "A serialized room was replaced by stale shop metadata.");
((HashSet<ulong>)typeof(ShopService).GetField("PendingRestore", Static)!.GetValue(null)!).Clear();
Require(Decide(null, true) is null, "Already-replayed stock selected another merchant.");
ShopService.RestoreState(stock with { Context = "another-location" });
Require(Decide(null, true) is null, "Stock from another coordinate selected a merchant.");
ShopService.RestoreState(null);
Require(Decide(null, true) is null, "Pre-entry rollback without saved stock selected a merchant.");
var finish = typeof(Bridge).GetNestedType("NativeContinue", BindingFlags.NonPublic)!.GetMethod("Finish", Static)!;
foreach (bool failed in new[] { false, true })
{
    Field("_continuingNative", true);
    Field("_loadingMerchant", restored);
    var failure = new InvalidOperationException("fixture load failure");
    var finishing = (Task)finish.Invoke(null, [failed ? Task.FromException(failure) : Task.CompletedTask])!;
    try { await finishing; Require(!failed, "Native load failure was discarded."); }
    catch (InvalidOperationException error) { Require(ReferenceEquals(error, failure), "Native load failure changed."); }
    Require(!(bool)typeof(Bridge).GetField("_continuingNative", Static)!.GetValue(null)! &&
        typeof(Bridge).GetField("_loadingMerchant", Static)!.GetValue(null) is null,
        "Native load completion retained merchant restore scope.");
}
Field("_continuingNative", false);
Field("<IsRestoring>k__BackingField", false);
Field("_loadingMerchant", null);
harmony.UnpatchAll(harmony.Id);
Console.WriteLine($"PASS: {checks} merchant continuation checks.");

static class ContextFixture
{
    public static bool Prefix(ref string __result) { __result = "paired-location"; return false; }
}
