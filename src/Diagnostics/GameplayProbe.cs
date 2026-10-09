using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;
using NoSuffering.Ancients;
using NoSuffering.Checkpoints;
using NoSuffering.Combat;
using NoSuffering.Multiplayer;
using Bridge = NoSuffering.GameBridge.GameBridge;

namespace NoSuffering.Diagnostics;

// Explicit lab-only native-engine smoke probe. It never runs in an ordinary game.
// Native setup APIs jump to act two to give F02 more than Neow as a candidate;
// this is not an end-to-end playthrough or a multiplayer acceptance test.
public static class GameplayProbe
{
    private static readonly Dictionary<string, string> Results = new();
    private static bool _attached;

    public static void Initialize(SceneTree tree)
    {
        var args = OS.GetCmdlineUserArgs();
        bool continueProbe = args.Contains("--ns-gameplay-continue-probe");
        bool gameplayProbe = args.Contains("--ns-gameplay-probe");
        bool neowProbe = args.Contains("--ns-neow-probe");
        bool rollbackProbe = args.Contains("--ns-rollback-probe");
        bool expansionProbe = args.Contains("--ns-expansion-probe");
        if (_attached || (!gameplayProbe && !continueProbe && !neowProbe && !rollbackProbe && !expansionProbe)) return;
        _attached = true;
        var platform = OS.GetName() == "Windows" ? "windows" : OS.GetName() == "macOS" ? "macos" : "";
        var userDir = OS.GetUserDataDir().Replace('\\', '/').TrimEnd('/');
        var labRoot = OS.GetDataDir().Replace('\\', '/').TrimEnd('/') + "/NoSufferingLab/";
        if (new[] { gameplayProbe, continueProbe, neowProbe, rollbackProbe, expansionProbe }.Count(flag => flag) != 1 || !args.Contains("--ns-lab-probe") || platform.Length == 0 ||
            !string.Equals(CommandLineHelper.GetValue("force-steam"), "off", StringComparison.OrdinalIgnoreCase) ||
            (userDir != labRoot + platform + "-public-beta" && userDir != labRoot + platform + "-public"))
        {
            Log.Error("[NoSuffering] GAMEPLAY_PROBE refused: requires --force-steam=off and an actual NoSufferingLab user directory.");
            return;
        }
        void Start() { tree.ProcessFrame -= Start; _ = expansionProbe ? ExpansionProbe.Run(tree) : rollbackProbe ? RollbackProbe.Run(tree) : neowProbe ? RunNeow(tree) : continueProbe ? RunContinue(tree) : Run(tree); }
        tree.ProcessFrame += Start;
    }

    private static async Task Run(SceneTree tree)
    {
        string[] checks = ["native_run_start", "F02_ancient", "F03_options", "map_checkpoint", "F01_rollback",
            "native_combat_start", "F04_restart", "F05_order_refresh", "F04_retains_refresh", "native_save_continue_retains_attempt"];
        foreach (var check in checks) Results[check] = "UNEXECUTED";
        var stage = checks[0];
        try
        {
            await AwaitStartup();
            // The separate settings probe intentionally persists this toggle.
            // Enable the operation required by this isolated gameplay fixture.
            NoSuffering.Config.ConfigStore.ChangeRules(r => r with { EnableCombatReroll = true });
            var game = NGame.Instance!;
            if (RunManager.Instance.IsInProgress) throw new InvalidOperationException("Lab already has an active run.");
            // The game's NSceneBootstrapper uses this same native setup and scene ownership.
            var run = RunState.CreateForNewRun([Player.CreateForNewRun(ModelDb.Character<Ironclad>(), UnlockState.all, 1UL)],
                ActModel.GetDefaultList().Select(a => a.ToMutable()).ToList(), [], GameMode.Standard, 0, "NOSUFFERINGLAB");
            var manager = RunManager.Instance;
            manager.SetUpNewSingleplayer(run, true);
            await Timed(PreloadManager.LoadRunAssets(run.Players.Select(p => p.Character)), "run assets");
            await Timed(manager.FinalizeStartingRelics(), "starting relics");
            manager.Launch();
            game.RootSceneContainer.SetCurrentScene(NRun.Create(run));
            game.ReactionContainer.InitializeNetworking(manager.NetService);
            await Timed(manager.EnterAct(1, false), "native act-two setup");
            HostCoordinator.OnRunReady();
            Require(manager.IsInProgress && NRun.Instance != null, "Native run/scene not running.");
            Pass(stage, "native RunState, NRun, act-two lab jump, all unlocks; singleplayer");

            stage = "map_checkpoint";
            await Bridge.Frame();
            CheckpointService.CaptureDecision(true);
            var checkpoint = CheckpointService.History.LastOrDefault() ?? throw new InvalidOperationException("Native map decision did not produce a checkpoint.");
            Require(Bridge.Thaw(checkpoint.Snapshot).PreFinishedRoom?.RoomType == RoomType.Map, "Checkpoint is not a native map snapshot.");
            Pass(stage, "captured act-two map decision");
            await Timed(manager.EnterMapCoord(Bridge.State.Map.StartingMapPoint.coord), "native ancient entry");
            Require(AncientService.Available, AncientService.UnavailableReason ?? "Ancient unavailable.");

            stage = "F02_ancient";
            var ancientBefore = ((EventRoom)Bridge.State.CurrentRoom!).CanonicalEvent!.Id;
            await Operation(CoreOperation.AncientReroll);
            Require(((EventRoom)Bridge.State.CurrentRoom!).CanonicalEvent!.Id != ancientBefore, "Ancient did not change.");
            Pass(stage, "native event replaced through host coordinator");

            stage = "F03_options";
            var optionsBefore = AncientService.CaptureState()?.OptionsRerolls ?? 0;
            var ancient = ((EventRoom)Bridge.State.CurrentRoom!).CanonicalEvent!.Id;
            await Operation(CoreOperation.AncientOptionsReroll);
            Require(((EventRoom)Bridge.State.CurrentRoom!).CanonicalEvent!.Id == ancient &&
                AncientService.CaptureState()?.OptionsRerolls == optionsBefore + 1, "Options reroll did not commit for the same ancient.");
            Require(manager.EventSynchronizer.Events.All(e => e.CurrentOptions.Count > 0 && !e.IsFinished), "Native rerolled rewards unavailable.");
            Pass(stage, "native same-ancient rewards generated and available");

            stage = "F01_rollback";
            await Operation(CoreOperation.MapRollback, checkpoint.Id);
            Require(Bridge.State.CurrentRoom is MapRoom && Bridge.State.MapLocation.ToString() == checkpoint.Position,
                "Rollback did not restore the map decision boundary.");
            Require(game.Transition.MouseFilter == Control.MouseFilterEnum.Ignore && !game.Transition.InTransition,
                "Rollback left a transparent transition blocking GUI input.");
            Pass(stage, "native map snapshot loaded through host coordinator");

            stage = "native_combat_start";
            // Choose a real generated monster coordinate. The probe does not play the ancient reward.
            var monster = Bridge.State.Map.GetAllMapPoints().Where(p => p.PointType == MapPointType.Monster)
                .OrderBy(p => p.coord.row).ThenBy(p => p.coord.col).First();
            await Timed(manager.EnterMapCoord(monster.coord), "native monster entry");
            await Wait(() => CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsStarting &&
                CombatService.CurrentOrderDigests.Count == Bridge.State.Players.Count, "opening draw");
            var baseline = CombatService.Baseline;
            var zero = Digests();
            Require(baseline != null && CombatService.Attempt == 0, "Native opening baseline not captured.");
            Pass(stage, "native generated monster room and opening draw; route selected by lab");

            stage = "F04_restart";
            await Operation(CoreOperation.CombatRestart);
            Require(CombatService.Attempt == 0 && Digests() == zero && CombatService.Baseline == baseline,
                "Normal restart changed attempt zero or its opening order/baseline.");
            Pass(stage, "attempt zero and actual opening order retained");

            stage = "F05_order_refresh";
            await Operation(CoreOperation.CombatReroll);
            var refreshed = Digests();
            Require(CombatService.Attempt == 1 && refreshed != zero && CombatService.Baseline == baseline,
                "Refreshed attempt/order or immutable baseline assertion failed.");
            Pass(stage, "attempt one changed actual opening order and retained baseline");

            stage = "F04_retains_refresh";
            await Operation(CoreOperation.CombatRestart);
            Require(CombatService.Attempt == 1 && Digests() == refreshed, "Normal restart lost refreshed attempt/order.");
            Pass(stage, "attempt one and refreshed opening order retained");

            stage = "native_save_continue_retains_attempt";
            await Timed(CompanionStore.SaveNativeCurrent(), "native save");
            var saved = SaveManager.Instance.LoadRunSave(); // Native disk read also reads the paired companion.
            Require(saved.Success && saved.SaveData != null, "Native saved run could not be read: " + saved.ErrorMessage);
            Require(CompanionStore.Error == null && CombatService.Attempt == 1, "Companion attempt not paired with native save.");
            var state = RunState.FromSerializable(saved.SaveData!);
            manager.EventSynchronizer.Dispose();
            manager.OneOffSynchronizer.Dispose();
            manager.CleanUp();
            await Timed(manager.SetUpSavedSingleplayer(state, saved.SaveData!), "native saved setup");
            game.ReactionContainer.InitializeNetworking(manager.NetService);
            await Timed(game.LoadRun(state, saved.SaveData!.PreFinishedRoom), "native continue");
            await Wait(() => !CombatManager.Instance.IsStarting && CombatService.CurrentOrderDigests.Count == state.Players.Count, "continued opening draw");
            Require(CombatService.Attempt == 1 && Digests() == refreshed, "Native continue lost refreshed attempt/order.");
            Pass(stage, "actual disk save/read and native singleplayer continue retained attempt/order");
        }
        catch (Exception error)
        {
            Results[stage] = "FAIL: " + error.Message;
            Log.Error("[NoSuffering] GAMEPLAY_PROBE " + stage + " " + error);
        }
        finally
        {
            bool success = Results.Values.All(v => v.StartsWith("PASS", StringComparison.Ordinal));
            var reportData = new Dictionary<string, object> {
                ["scope"] = "native-engine singleplayer smoke; act-two lab jump; no multiplayer/gameplay acceptance",
                ["process_id"] = System.Environment.ProcessId, ["results"] = Results
            };
            if (success) { reportData["expected_attempt"] = CombatService.Attempt; reportData["expected_order"] = Digests(); }
            var report = JsonSerializer.Serialize(reportData);
            Log.Info("[NoSuffering] GAMEPLAY_PROBE_RESULTS " + report);
            File.WriteAllText(Path.Combine(OS.GetUserDataDir(), "nosuffering-gameplay-probe.json"), report);
            tree.Quit(success ? 0 : 1);
        }
    }

    private static async Task RunContinue(SceneTree tree)
    {
        const string check = "fresh_process_continue";
        Results[check] = "UNEXECUTED";
        bool settledContinue=false;
        try
        {
            await AwaitStartup();
            var game = NGame.Instance!;
            var manager = RunManager.Instance;
            // Native menu startup may already read the saved run and its companion.
            // The different process ID below establishes fresh-process ownership.
            Require(!manager.IsInProgress, "Continue probe requires a fresh process with no active run.");
            var saved = SaveManager.Instance.LoadRunSave();
            if(saved.Success && saved.SaveData is {PreFinishedRoom.IsPreFinished:true} data &&
               CheckpointService.CompletedCombatAt(new MapLocation(data.VisitedMapCoords.LastOrDefault(),data.CurrentActIndex))) {
                settledContinue=true;await RollbackProbe.RunSettledContinue(tree);return;
            }
            using var prior = JsonDocument.Parse(File.ReadAllText(Path.Combine(OS.GetUserDataDir(), "nosuffering-gameplay-probe.json")));
            var expected = prior.RootElement;
            Require(expected.GetProperty("process_id").GetInt32() != System.Environment.ProcessId, "Continue probe must run in a different process from the gameplay probe.");
            var previousChecks = expected.GetProperty("results").EnumerateObject().ToList();
            Require(previousChecks.Count == 10 && previousChecks.All(p => p.Value.GetString()?.StartsWith("PASS", StringComparison.Ordinal) == true),
                "Prior native gameplay probe must have passed every check.");
            var attempt = expected.GetProperty("expected_attempt").GetInt32();
            var order = expected.GetProperty("expected_order").GetString();
            Require(attempt > 0 && !string.IsNullOrEmpty(order), "Prior probe has no refreshed attempt/order expectation.");
            Require(saved.Success && saved.SaveData != null, "Native saved run could not be read: " + saved.ErrorMessage);
            Require(CompanionStore.Error == null && CombatService.Attempt == attempt && Digests() == order,
                "Fresh companion read did not restore the saved attempt/order.");
            var state = RunState.FromSerializable(saved.SaveData!);
            await Timed(manager.SetUpSavedSingleplayer(state, saved.SaveData!), "fresh native saved setup");
            game.ReactionContainer.InitializeNetworking(manager.NetService);
            await Timed(game.LoadRun(state, saved.SaveData!.PreFinishedRoom), "fresh native continue");
            await Wait(() => CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsStarting &&
                manager.ActionQueueSynchronizer.CombatState == ActionSynchronizerCombatState.PlayPhase,
                "fresh continued opening draw");
            Require(NRun.Instance != null && CombatService.Attempt == attempt && Digests() == order,
                "Fresh native continue lost the refreshed attempt/order.");
            Pass(check, "different process, native disk/companion read and actual opening draw retained refreshed attempt/order; native startup may already read the companion");
        }
        catch (Exception error)
        {
            Results[check] = "FAIL: " + error.Message;
            Log.Error("[NoSuffering] GAMEPLAY_PROBE " + check + " " + error);
        }
        finally
        {
            if(!settledContinue) {
            var report = JsonSerializer.Serialize(new {
                scope = "native-engine fresh-process singleplayer continue smoke; no multiplayer/gameplay acceptance",
                process_id = System.Environment.ProcessId, results = Results
            });
            Log.Info("[NoSuffering] CONTINUE_PROBE_RESULTS " + report);
            File.WriteAllText(Path.Combine(OS.GetUserDataDir(), "nosuffering-continue-probe.json"), report);
            tree.Quit(Results[check].StartsWith("PASS", StringComparison.Ordinal) ? 0 : 1);
            }
        }
    }

    private static async Task RunNeow(SceneTree tree)
    {
        const string check = "neow_reroll_claim_proceed";
        Results[check] = "UNEXECUTED";
        var state = new Dictionary<string, object>();
        try
        {
            await AwaitStartup();
            var game = NGame.Instance!;
            var manager = RunManager.Instance;
            Require(!manager.IsInProgress, "Probe requires no active run.");
            var existing = SaveManager.Instance.LoadRunSave();
            if (existing.Success && existing.SaveData != null)
            {
                Require(CompanionStore.Error == null, "Existing save pairing failed: " + CompanionStore.Error);
                state["existing_ancient_record_loaded"] = AncientService.CaptureState() != null;
            }
            var run = RunState.CreateForNewRun([Player.CreateForNewRun(ModelDb.Character<Ironclad>(), UnlockState.all, 1UL)],
                ActModel.GetDefaultList().Select(a => a.ToMutable()).ToList(), [], GameMode.Standard, 0, "NOSUFFERINGNEOW");
            manager.SetUpNewSingleplayer(run, true);
            await Timed(PreloadManager.LoadRunAssets(run.Players.Select(p => p.Character)), "run assets");
            await Timed(manager.FinalizeStartingRelics(), "starting relics");
            manager.Launch();
            game.RootSceneContainer.SetCurrentScene(NRun.Create(run));
            game.ReactionContainer.InitializeNetworking(manager.NetService);
            await Timed(manager.EnterAct(0, false), "native act-one setup");
            HostCoordinator.OnRunReady();
            await Timed(manager.EnterMapCoord(run.Map.StartingMapPoint.coord), "native Neow entry");
            Require(((EventRoom)run.CurrentRoom!).CanonicalEvent is Neow, "Act-one ancient is not Neow.");
            await Wait(() => NEventRoom.Instance?.Layout?.OptionButtons.Any() == true, "Neow buttons");
            var oldOption = manager.EventSynchronizer.GetLocalEvent().CurrentOptions[0];
            NEventOptionButton? reward = null;
            for (int rerolls = 1; reward == null; rerolls++)
            {
                await Operation(CoreOperation.AncientOptionsReroll);
                await Wait(() => NEventRoom.Instance?.Layout?.OptionButtons.Any() == true, "rerolled Neow buttons");
                state["rerolls"] = rerolls;
                state["options"] = manager.EventSynchronizer.GetLocalEvent().CurrentOptions.Select(o => o.Relic?.Id.Entry ?? o.TextKey).ToArray();
                // Pick a native reward without an additional card-selection screen.
                reward = NEventRoom.Instance!.Layout!.OptionButtons.FirstOrDefault(b => b.Option.Relic is { } relic &&
                    (relic.Id.Entry is "NEOWS_TALISMAN" or "GOLDEN_PEARL" or "NUTRITIOUS_OYSTER" or "CURSED_PEARL" or "SILKEN_TRESS" or "LEAFY_POULTICE" ||
                     relic.GetType().GetMethod(nameof(RelicModel.AfterObtained))!.DeclaringType == typeof(RelicModel)) && !b.Option.IsLocked);
                Require(reward != null || rerolls < 4, "No prompt-free Neow reward in the diagnostic fixture.");
            }
            // A click queued for the discarded reward must not claim a new reward.
            NEventRoom.Instance!.OptionButtonClicked(oldOption, 0);
            Require(!manager.EventSynchronizer.GetLocalEvent().CurrentOptions.Any(o => o.WasChosen), "Stale reward click was accepted.");
            state["stale_reward_rejected"] = true;
            var local = manager.EventSynchronizer.GetLocalEvent();
            state["reward"] = reward.Option.Relic!.Id.Entry;
            reward.Call(NEventOptionButton.MethodName.OnRelease);
            await Wait(() => local.IsFinished && NEventRoom.Instance!.Layout!.OptionButtons.Any(b => b.Option.IsProceed), "claimed Neow reward");
            await Timed(manager.EventSynchronizer.AwaitPendingOptionTasks(), "native reward completion");
            Require(((EventRoom)run.CurrentRoom!).IsPreFinished, "Claimed ancient room not marked finished.");
            var proceed = NEventRoom.Instance!.Layout!.OptionButtons.Single(b => b.Option.IsProceed);
            state["event_finished"] = local.IsFinished;
            state["proceed_in_model_options"] = local.CurrentOptions.Contains(proceed.Option);
            state["map_open_before_click"] = NMapScreen.Instance!.IsOpen;
            Require(!NMapScreen.Instance.IsOpen, "Fixture map was already open before the Proceed click.");
            proceed.Call(NEventOptionButton.MethodName.OnRelease);
            await Bridge.Frame();
            state["map_open_after_click"] = NMapScreen.Instance!.IsOpen;
            state["travel_enabled_after_click"] = NMapScreen.Instance.IsTravelEnabled;
            Require(NMapScreen.Instance.IsOpen && NMapScreen.Instance.IsTravelEnabled, "Native Proceed click did not open an actionable map.");
            Pass(check, "act-one Neow reroll, stale reward rejected, actual native reward/Proceed button handlers opened travel map");
        }
        catch (Exception error)
        {
            Results[check] = "FAIL: " + error.Message;
            Log.Error("[NoSuffering] NEOW_PROBE " + error);
        }
        finally
        {
            var report = JsonSerializer.Serialize(new { scope = "native-engine singleplayer Neow reward and button-handler regression; no physical pointer or multiplayer test", results = Results, state });
            Log.Info("[NoSuffering] NEOW_PROBE_RESULTS " + report);
            File.WriteAllText(Path.Combine(OS.GetUserDataDir(), "nosuffering-neow-probe.json"), report);
            tree.Quit(Results[check].StartsWith("PASS", StringComparison.Ordinal) ? 0 : 1);
        }
    }

    private static async Task AwaitStartup()
    {
#if STS2_STABLE
        // v0.107.1 exposes the ready native menu instead of GameStartupComplete.
        await Wait(() => NGame.Instance?.MainMenu?.IsNodeReady() == true, "native main menu", 60000);
        await Timed(PreloadManager.LoadCommonAndMainMenuAssets(), "deferred startup assets");
#else
        await Wait(() => NGame.Instance?.GameStartupComplete.IsCompleted == true, "game startup", 60000);
        await NGame.Instance!.GameStartupComplete;
#endif
    }

    private static string Digests() => string.Join("/", CombatService.CurrentOrderDigests.OrderBy(p => p.Key).Select(p => p.Key + ":" + p.Value));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Pass(string check, string detail) { Results[check] = "PASS: " + detail; Log.Info("[NoSuffering] GAMEPLAY_PROBE " + check + " " + Results[check]); }
    private static async Task Operation(CoreOperation kind, long checkpoint = 0)
    {
        var revision = HostCoordinator.WorldRevision;
        HostCoordinator.Submit(kind, checkpoint);
        await Wait(() => !HostCoordinator.Busy, kind.ToString(), 60000);
        Require(HostCoordinator.WorldRevision == revision + 1 && HostCoordinator.Status == "操作完成", kind + " failed: " + HostCoordinator.Status);
    }
    private static async Task Timed(Task task, string detail)
    {
        await Wait(() => task.IsCompleted, detail, 60000);
        await task;
    }
    private static async Task Wait(Func<bool> done, string detail, ulong timeout = 30000)
    {
        var deadline = Time.GetTicksMsec() + timeout;
        while (!done()) { if (Time.GetTicksMsec() >= deadline) throw new TimeoutException("Timed out: " + detail); await Bridge.Frame(); }
    }
}
