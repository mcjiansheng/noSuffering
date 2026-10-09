using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;
using NoSuffering.Checkpoints;
using NoSuffering.Config;
using NoSuffering.Multiplayer;
using NoSuffering.UI;
using Bridge = NoSuffering.GameBridge.GameBridge;

namespace NoSuffering.Diagnostics;

// Caller must enforce the same isolated-userdir/lab guards as GameplayProbe.
// Native setup selects generated coordinates; no map/resource/model mocks are used.
public static class RollbackProbe
{
    public static async Task Run(SceneTree tree)
    {
        string[] checks = ["shop_entry_rollback", "transition_native_gui", "standalone_settings", "settled_combat_rollback", "legal_next_node", "past_act_selector_cancel", "settled_boss_cross_act_proceed"];
        var results = checks.ToDictionary(c => c, _ => "UNEXECUTED");
        var state = new Dictionary<string, object>();
        string stage = checks[0];
        void Pass(string detail) { results[stage] = "PASS: " + detail; }
        try
        {
            await AwaitStartup(tree);
            var game = NGame.Instance!;
            var manager = RunManager.Instance;
            Require(!manager.IsInProgress, "Probe requires no active run.");
            var run = RunState.CreateForNewRun([Player.CreateForNewRun(ModelDb.Character<Ironclad>(), UnlockState.all, 1UL)],
                ActModel.GetDefaultList().Select(a => a.ToMutable()).ToList(), [], GameMode.Standard, 0, "NOSUFFERINGROLLBACK");
            manager.SetUpNewSingleplayer(run, true);
            await Timed(PreloadManager.LoadRunAssets(run.Players.Select(p => p.Character)), "run assets");
            await Timed(manager.FinalizeStartingRelics(), "starting relics");
            manager.Launch();
            game.RootSceneContainer.SetCurrentScene(NRun.Create(run));
            game.ReactionContainer.InitializeNetworking(manager.NetService);
            await Timed(manager.EnterAct(1, false), "native act-two fixture");
            HostCoordinator.OnRunReady();
            // Suppress native tutorial modals only in the caller's isolated lab save.
            foreach (var ftue in new[] { "map_select_ftue", "merchant_ftue", "combat_reward_ftue", "combat_rules_ftue" }) SaveManager.Instance.MarkFtueAsComplete(ftue);
            var map = MapFingerprint();
            var shop = Bridge.State.Map.GetAllMapPoints().Where(p => p.PointType == MapPointType.Shop)
                .OrderBy(p => p.coord.row).ThenBy(p => p.coord.col).First();
            var entryResources = Resources();
            await Timed(manager.EnterMapCoord(shop.coord), "native shop entry");
            var shopTarget = MapRollbackSelection.Targets.Single(c => c.Coord == shop.coord && c.ActIndex == Bridge.State.CurrentActIndex);
            state["shop_coord"] = shop.coord.ToString();
            Require(shopTarget.BeforeEntry && shopTarget.LastRoom == "Shop", "Shop has no immutable entry target.");
            await BuyCard();
            Require(Resources() != entryResources, "Actual shop purchase did not change resources.");
            await SelectRollback(shopTarget);
            Require(Bridge.State.CurrentRoom is MerchantRoom && Bridge.State.CurrentMapCoord == shop.coord, "Rollback did not re-enter the shop.");
            Require(Resources() == entryResources, "Shop entry resources were not restored.");
            Require(MapFingerprint() == map, "Rollback changed the generated map.");
            await BuyCard();
            Require(Resources() != entryResources, "Restored shop cannot purchase again.");
            // Closing the native inventory uses its actual button handler.
            var back = Descendants<NBackButton>(NMerchantRoom.Instance!).First(b => b.IsVisibleInTree());
            back.ForceClick();
            await Bridge.Frame();
            Pass("native shop card purchased via Viewport GUI, entry resources/map restored via visited-node handler, purchased again");

            stage = checks[1];
            await IdleTransition();
            Require(game.Transition.MouseFilter == Control.MouseFilterEnum.Ignore && !game.Transition.InTransition,
                "Inactive native transition intercepts input.");
            var deck = NRun.Instance!.GlobalUi.TopBar.Deck;
            Require(NCapstoneContainer.Instance!.CurrentCapstoneScreen is not NDeckViewScreen, "Deck already open.");
            await Click(deck);
            await Wait(() => NCapstoneContainer.Instance!.CurrentCapstoneScreen is NDeckViewScreen, "deck opened by native GUI click");
            state["deck_opened_via_viewport"] = true;
            NCapstoneContainer.Instance.Close();
            await Bridge.Frame();
            Pass("inactive transition ignores input; Viewport mouse click really opened native deck screen");

            stage = checks[2];
            Require(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetType("ModConfig.ModConfigApi") != null), "Standalone settings fixture must not install ModConfig.");
            Require(ConfigStore.EditableLocal.TogglePanelKey == new HotkeyBinding(Key.F6), "Standalone fixture requires default F6.");
            var layer = tree.Root.GetNode<CanvasLayer>("NoSufferingOverlay");
            KeyInput(game, Key.F6);
            await Wait(() => Descendants<Button>(layer).Any(b => b.IsVisibleInTree() && b.Text is "设置" or "Settings"), "F6 panel");
            await Screenshot("nosuffering-actions.png");
            await Click(Descendants<Button>(layer).Single(b => b.IsVisibleInTree() && b.Text is "设置" or "Settings"));
            await Wait(() => Descendants<CheckButton>(layer).Count() == 7, "settings toggles");
            await Screenshot("nosuffering-settings.png");
            var before = ConfigStore.EditableRules.EnableCombatReroll;
            await Click(Descendants<CheckButton>(layer).ElementAt(2));
            await Wait(() => ConfigStore.EditableRules.EnableCombatReroll != before, "GUI setting change");
            using (var persisted = JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath("user://NoSuffering/config.json"))))
                Require(persisted.RootElement.GetProperty("SinglePlayerRules").GetProperty("EnableCombatReroll").GetBoolean() == !before, "GUI setting not persisted.");
            await Click(Descendants<Button>(layer).Single(b => b.IsVisibleInTree() && b.Text == "×"));
            await Click(NRun.Instance!.GlobalUi.TopBar.Deck);
            await Wait(() => NCapstoneContainer.Instance!.CurrentCapstoneScreen is NDeckViewScreen, "native GUI after settings close");
            NCapstoneContainer.Instance.Close();
            state["persisted_combat_reroll"] = !before;
            Pass("without ModConfig: F6, GUI settings/toggle/close, disk persistence, subsequent native deck GUI click");

            stage = checks[3];
            var monster = Bridge.State.Map.GetAllMapPoints().Where(p => p.PointType == MapPointType.Monster && p.Children.Any())
                .OrderBy(p => p.coord.row).ThenBy(p => p.coord.col).First();
            await Timed(manager.EnterMapCoord(monster.coord), "native monster entry");
            await Wait(() => CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsStarting, "combat opening");
            await Timed(CreatureCmd.Kill(CombatManager.Instance.DebugOnlyGetState()!.Enemies.ToList(), true), "native diagnostic kill/victory");
            await Wait(() => !CombatManager.Instance.IsInProgress && Descendants<NRewardsScreen>(tree.Root).Any(), "native rewards");
            var rewards = Descendants<NRewardsScreen>(tree.Root).Single();
            var gold = Descendants<NRewardButton>(rewards).First(b => b.Reward is MegaCrit.Sts2.Core.Rewards.GoldReward);
            var goldBefore = Bridge.State.Players.Single().Gold;
            gold.Call("OnRelease");
            await Wait(() => Bridge.State.Players.Single().Gold > goldBefore, "gold reward claimed");
            // Skip remaining rewards via the actual native terminal Proceed handler.
            var rewardProceed = Descendants<NProceedButton>(rewards).Single();
            await Wait(() => rewardProceed.IsEnabled, "native reward proceed enabled");
            rewardProceed.ForceClick();
            await Wait(() => NMapScreen.Instance is { IsOpen: true, IsTravelEnabled: true } && !Descendants<NRewardsScreen>(tree.Root).Any(), "rewards finished/actionable map");
            var completed = MapRollbackSelection.Targets.Single(c => c.Coord == monster.coord && c.CompletedCombat);
            var settledResources = Resources();
            var second = Bridge.State.Map.GetAllMapPoints().Where(p => p.PointType == MapPointType.Monster && p.coord != monster.coord)
                .OrderBy(p => p.coord.row).ThenBy(p => p.coord.col).First();
            await Timed(manager.EnterMapCoord(second.coord), "second native combat fixture");
            await Wait(() => CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsStarting, "second combat opening");
            await SelectRollback(completed);
            Require(Bridge.State.CurrentRoom is CombatRoom { IsPreFinished: true } && !CombatManager.Instance.IsInProgress, "Completed combat replayed instead of loading finished room.");
            Require(Resources() == settledResources, "Settled gold/deck/resources not preserved.");
            Require(!Descendants<NRewardsScreen>(tree.Root).Any(), "Completed combat produced a second rewards screen.");
            Require(MapFingerprint() == map, "Completed-combat rollback changed map.");
            state["settled_coord"] = monster.coord.ToString();
            state["settled_resources"] = settledResources;
            Pass("actual monster victory via native diagnostic kill, gold claimed/others skipped; rollback from second combat retained finished room/resources without duplicate reward screen");

            stage = checks[4];
            await Wait(() => NMapScreen.Instance is { IsOpen: true, IsTravelEnabled: true, IsTraveling: false }, "restored travel map");
            await IdleTransition();
            var next = Descendants<NMapPoint>(NMapScreen.Instance!).First(p => p.State == MapPointState.Travelable && monster.Children.Any(c => c.coord == p.Point.coord));
            var nextCoord = next.Point.coord;
            // Use the normal native point handler: no debug-travel override or direct EnterMapCoord.
            next.Call("OnRelease");
            // Native entry publishes its coordinate and pushes its room before
            // combat asset loading and initial setup finish. Do not start another
            // fixture while that native travel action is still entering the room.
            await Wait(() => Bridge.State.CurrentMapCoord == nextCoord && Bridge.State.CurrentRoom != null &&
                NMapScreen.Instance is { IsTraveling: false } && manager.ActionExecutor.CurrentlyRunningAction == null &&
                (Bridge.State.CurrentRoom is not CombatRoom || CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsStarting),
                "legal native node travel/action and room initialization complete", 60000);
            state["next_coord"] = nextCoord.ToString();
            Pass("normal native travelable-node handler completed travel/action and room initialization at a generated child after completed-combat rollback");

            // Finish one actual act-two boss, using native reward completion and
            // native act transition. This target must be captured by production
            // hooks; the probe never calls CaptureDecision to manufacture it.
            stage = checks[6];
            if (Bridge.State.CurrentRoom is CombatRoom)
            {
                await Timed(CreatureCmd.Kill(CombatManager.Instance.DebugOnlyGetState()!.Enemies.ToList(), true), "legal child native diagnostic kill/victory");
                await Wait(() => !CombatManager.Instance.IsInProgress && Descendants<NRewardsScreen>(tree.Root).Any(), "legal child native rewards");
                var childRewards = Descendants<NRewardsScreen>(tree.Root).Single();
                var childProceed = Descendants<NProceedButton>(childRewards).Single();
                await Wait(() => childProceed.IsEnabled, "legal child native reward proceed enabled");
                childProceed.ForceClick();
                await Wait(() => NMapScreen.Instance is { IsOpen: true, IsTravelEnabled: true, IsTraveling: false } &&
                    !Descendants<NRewardsScreen>(tree.Root).Any() && manager.ActionExecutor.CurrentlyRunningAction == null &&
                    SaveManager.Instance.CurrentRunSaveTask == null, "legal child rewards finished and native save/action complete", 60000);
                await IdleTransition();
            }
            var bossCoord = Bridge.State.Map.BossMapPoint.coord;
            await Timed(manager.EnterMapCoord(bossCoord), "native act-two boss entry");
            await Wait(() => CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsStarting, "boss opening");
            await Timed(CreatureCmd.Kill(CombatManager.Instance.DebugOnlyGetState()!.Enemies.ToList(), true), "native boss diagnostic kill/victory");
            await Wait(() => !CombatManager.Instance.IsInProgress && Descendants<NRewardsScreen>(tree.Root).Any(), "boss native rewards");
            var bossRewards = Descendants<NRewardsScreen>(tree.Root).Single();
            var bossGold = Descendants<NRewardButton>(bossRewards).First(b => b.Reward is MegaCrit.Sts2.Core.Rewards.GoldReward);
            var beforeBossGold = Bridge.State.Players.Single().Gold;
            bossGold.Call("OnRelease");
            await Wait(() => Bridge.State.Players.Single().Gold > beforeBossGold, "native boss gold claim");
            var bossResources = Resources();
            var bossProceed = Descendants<NProceedButton>(bossRewards).Single();
            await Wait(() => bossProceed.IsEnabled, "native boss reward proceed");
            bossProceed.ForceClick();
            await Wait(() => Bridge.State.CurrentActIndex == 2 && Bridge.State.CurrentRoom is MapRoom && !Descendants<NRewardsScreen>(tree.Root).Any(), "native boss advances to act three", 60000);
            await IdleTransition();
            var bossTarget = MapRollbackSelection.Targets.SingleOrDefault(c => c.ActIndex == 1 && c.Coord == bossCoord && c.CompletedCombat)
                ?? throw new InvalidOperationException("Natural boss reward completion did not capture a completed boss target.");
            Require(Bridge.Thaw(bossTarget.Snapshot).PreFinishedRoom is { IsPreFinished: true, RoomType: RoomType.Boss }, "Boss target does not contain a native finished boss.");
            state["boss_target_captured_naturally"] = true;
            state["boss_coord"] = bossCoord.ToString();
            // A real entry in the later act provides two target groups so the
            // public act controls, rather than an auto-selected single act, run.
            var laterShop = Bridge.State.Map.GetAllMapPoints().Where(p => p.PointType == MapPointType.Shop)
                .OrderBy(p => p.coord.row).ThenBy(p => p.coord.col).First();
            await Timed(manager.EnterMapCoord(laterShop.coord), "native act-three shop target");
            Require(MapRollbackSelection.Targets.Select(c => c.ActIndex).Distinct().Order().SequenceEqual(new[] { 1, 2 }), "Cross-act fixture lacks actual node targets in both acts.");

            stage = checks[5];
            var liveState = Bridge.State;
            var laterMap = MapFingerprint();
            var laterResources = Resources();
            var laterPosition = Bridge.State.MapLocation;
            var historyBeforePreview = string.Join(",", CheckpointService.History.Select(c => c.Id));
            var revisionBeforePreview = HostCoordinator.WorldRevision;
            var mapWasOpen = NMapScreen.Instance!.IsOpen;
            MapRollbackSelection.Begin();
            await Wait(() => MapRollbackSelection.Active && NMapScreen.Instance is { IsOpen: true, IsTraveling: false }, "cross-act selector");
            await ShowSelectorAct(1);
            await Screenshot("nosuffering-rollback.png");
            Require(ReferenceEquals(Bridge.State, liveState) && Bridge.State.CurrentActIndex == 2 && Bridge.State.MapLocation == laterPosition && Resources() == laterResources && MapFingerprint() == laterMap,
                "Past-act preview mutated the live run or resources.");
            Require(Descendants<NMapPoint>(NMapScreen.Instance!).Any(p => p.Point.coord == bossCoord && p.State == MapPointState.Traveled), "Past-act preview does not display the finished boss as selectable.");
            var cancel = Descendants<Button>(NMapScreen.Instance!).Single(b => b.IsVisibleInTree() && b.Text is "取消" or "Cancel");
            await Click(cancel);
            await Wait(() => !MapRollbackSelection.Active, "past-act cancel");
            Require(ReferenceEquals(Bridge.State, liveState) && Bridge.State.CurrentActIndex == 2 && Bridge.State.MapLocation == laterPosition && Resources() == laterResources && MapFingerprint() == laterMap,
                "Past-act cancel did not preserve the live later-act run.");
            Require(NMapScreen.Instance!.IsOpen == mapWasOpen && DisplayedAct() == 2 && DisplayedMapFingerprint() == laterMap &&
                HostCoordinator.WorldRevision == revisionBeforePreview && string.Join(",", CheckpointService.History.Select(c => c.Id)) == historyBeforePreview,
                "Past-act cancel did not restore the displayed later-act map/history without a commit.");
            Pass("actual targets in acts two/three; GUI act switch displays boss without mutating live run; GUI Cancel restores later-act display/open state/history/resources");

            stage = checks[6];
            await SelectRollback(bossTarget);
            Require(Bridge.State.CurrentActIndex == 1 && Bridge.State.CurrentMapCoord == bossCoord && Bridge.State.CurrentRoom is CombatRoom { IsPreFinished: true, RoomType: RoomType.Boss } && !CombatManager.Instance.IsInProgress,
                "Past-act boss selection did not load the finished native boss.");
            Require(Resources() == bossResources && MapFingerprint() == map, "Past-act boss rollback lost settled resources or changed its map.");
            await Wait(() => Descendants<NRewardsScreen>(tree.Root).Any(), "native empty boss rewards/proceed");
            var emptyBossRewards = Descendants<NRewardsScreen>(tree.Root).Single();
            Require(!Descendants<NRewardButton>(emptyBossRewards).Any(), "Completed boss offered a second resource reward.");
            var emptyBossProceed = Descendants<NProceedButton>(emptyBossRewards).Single();
            await Wait(() => emptyBossProceed.IsEnabled, "native empty boss proceed enabled");
            emptyBossProceed.ForceClick();
            await Wait(() => Bridge.State.CurrentActIndex == 2 && Bridge.State.CurrentRoom is MapRoom && !Descendants<NRewardsScreen>(tree.Root).Any(), "restored boss native Proceed advances act", 60000);
            await IdleTransition();
            Require(Resources() == bossResources, "Empty restored boss Proceed granted duplicate resources.");
            Require(MapFingerprint() == laterMap, "Restored boss Proceed regenerated a different later-act map.");
            state["boss_resources_after_empty_proceed"] = Resources();
            // Leave a real paired settled-boss disk save for the existing
            // fresh-process continue mode, after proving empty Proceed works.
            await SelectRollback(bossTarget);
            await Timed(CompanionStore.SaveNativeCurrent(), "native settled-boss save for fresh continue");
            var settledDisk = SaveManager.Instance.LoadRunSave();
            Require(settledDisk.Success && settledDisk.SaveData?.PreFinishedRoom is { IsPreFinished: true, RoomType: RoomType.Boss } && CompanionStore.Error == null &&
                CheckpointService.Save().SettledCombat == new CombatSettlement(bossCoord, 1), "Settled boss native save/companion marker was not persisted.");
            state["saved_settled_boss"] = true;
            Pass("boss target captured naturally; GUI past-act selection committed through visited boss handler; native empty boss reward Proceed advanced act without repeated resource rewards");
        }
        catch (Exception error)
        {
            results[stage] = "FAIL: " + error.Message;
            Log.Error("[NoSuffering] ROLLBACK_PROBE " + stage + " " + error);
        }
        finally
        {
            bool success = results.Values.All(v => v.StartsWith("PASS", StringComparison.Ordinal));
            var report = JsonSerializer.Serialize(new { scope = "isolated native-engine singleplayer regression; generated-coordinate fixture jumps; native diagnostic combat kill; Viewport GUI and native map/reward handlers; physical pointer and multiplayer untested", process_id = System.Environment.ProcessId, results, state });
            Log.Info("[NoSuffering] ROLLBACK_PROBE_RESULTS " + report);
            File.WriteAllText(Path.Combine(OS.GetUserDataDir(), "nosuffering-rollback-probe.json"), report);
            tree.Quit(success ? 0 : 1);
        }
    }

    public static async Task RunSettledContinue(SceneTree tree)
    {
        const string check = "fresh_settled_boss_continue";
        var results = new Dictionary<string, string> { [check] = "UNEXECUTED" };
        var state = new Dictionary<string, object>();
        try
        {
            await AwaitStartup(tree);
            var game = NGame.Instance!;
            var manager = RunManager.Instance;
            Require(!manager.IsInProgress, "Settled continue probe requires no active run.");
            using var previous = JsonDocument.Parse(File.ReadAllText(Path.Combine(OS.GetUserDataDir(), "nosuffering-rollback-probe.json")));
            var prior = previous.RootElement;
            var priorPid = prior.GetProperty("process_id").GetInt32();
            Require(priorPid != System.Environment.ProcessId, "Settled continue must run in a different process.");
            var priorChecks = prior.GetProperty("results").EnumerateObject().ToList();
            Require(priorChecks.Count == 7 && priorChecks.All(p => p.Value.GetString()?.StartsWith("PASS", StringComparison.Ordinal) == true), "Prior rollback probe must pass every check.");
            Require(prior.GetProperty("state").GetProperty("saved_settled_boss").GetBoolean(), "Prior rollback probe did not prepare a settled boss disk save.");
            var expectedResources = prior.GetProperty("state").GetProperty("boss_resources_after_empty_proceed").GetString();
            Require(!string.IsNullOrEmpty(expectedResources), "Prior probe has no settled resource expectation.");
            var saved = SaveManager.Instance.LoadRunSave();
            Require(saved.Success && saved.SaveData != null, "Native settled-boss save could not be read: " + saved.ErrorMessage);
            var nativeSave = saved.SaveData!;
            var location = new MapLocation(nativeSave.VisitedMapCoords.LastOrDefault(), nativeSave.CurrentActIndex);
            Require(CompanionStore.Error == null && CheckpointService.Save().SettledCombat == CombatSettlement.At(location) &&
                nativeSave.CurrentActIndex == 1 && nativeSave.PreFinishedRoom is { IsPreFinished: true, RoomType: RoomType.Boss },
                "Disk native boss and companion settled marker are not paired: " + CompanionStore.Error);
            var restored = RunState.FromSerializable(nativeSave);
            var expectedRevision = CompanionStore.LoadedRevision;
            Require(expectedRevision > 0, "Prepared settled-boss save has no operation revision.");
            await Timed(manager.SetUpSavedSingleplayer(restored, nativeSave), "fresh native settled-boss setup");
            var afterSetup = SaveManager.Instance.LoadRunSave();
            Require(afterSetup.Success && CompanionStore.LoadedRevision == expectedRevision,
                "Native reload-count save reset the paired operation revision.");
            game.ReactionContainer.InitializeNetworking(manager.NetService);
            await Timed(game.LoadRun(restored, nativeSave.PreFinishedRoom), "fresh native settled-boss continue");
            await IdleTransition();
            Require(Bridge.State.CurrentRoom is CombatRoom { IsPreFinished: true, RoomType: RoomType.Boss } && !CombatManager.Instance.IsInProgress,
                "Fresh continue replayed the settled boss.");
            Require(Resources() == expectedResources, "Fresh continue changed settled gold/deck/resources.");
            await Wait(() => Descendants<NRewardsScreen>(tree.Root).Any(), "fresh native empty boss rewards");
            var rewards = Descendants<NRewardsScreen>(tree.Root).Single();
            Require(!Descendants<NRewardButton>(rewards).Any(), "Fresh settled boss offered duplicate resource rewards.");
            var proceed = Descendants<NProceedButton>(rewards).Single();
            await Wait(() => proceed.IsEnabled, "fresh native boss Proceed enabled");
            proceed.ForceClick();
            await Wait(() => Bridge.State.CurrentActIndex == 2 && Bridge.State.CurrentRoom is MapRoom && !Descendants<NRewardsScreen>(tree.Root).Any(), "fresh settled boss Proceed advances to act three", 60000);
            await IdleTransition();
            Require(Resources() == expectedResources, "Fresh continued boss Proceed duplicated resources.");
            state["prior_process_id"] = priorPid;
            state["paired_revision_after_setup"] = CompanionStore.LoadedRevision;
            state["resources_after_proceed"] = Resources();
            state["act_after_proceed"] = Bridge.State.CurrentActIndex;
            results[check] = "PASS: different process, native disk/companion marker, finished boss without repeat rewards, native empty Proceed advances act three with settled resources";
        }
        catch (Exception error)
        {
            results[check] = "FAIL: " + error.Message;
            Log.Error("[NoSuffering] CONTINUE_PROBE " + check + " " + error);
        }
        finally
        {
            var report = JsonSerializer.Serialize(new { scope = "isolated native-engine fresh-process singleplayer settled-boss continue; physical pointer and multiplayer untested", process_id = System.Environment.ProcessId, results, state });
            Log.Info("[NoSuffering] CONTINUE_PROBE_RESULTS " + report);
            File.WriteAllText(Path.Combine(OS.GetUserDataDir(), "nosuffering-continue-probe.json"), report);
            tree.Quit(results[check].StartsWith("PASS", StringComparison.Ordinal) ? 0 : 1);
        }
    }

    private static async Task AwaitStartup(SceneTree tree)
    {
#if STS2_STABLE
        await Wait(() => NGame.Instance?.MainMenu?.IsNodeReady() == true, "startup", 60000);
        await Timed(PreloadManager.LoadCommonAndMainMenuAssets(), "startup assets");
#else
        await Wait(() => NGame.Instance?.GameStartupComplete.IsCompleted == true, "startup", 60000);
        await NGame.Instance!.GameStartupComplete;
#endif
        await Bridge.Frame();
        // The isolated lab profile starts with the real native disclaimer. Its
        // modal catches Viewport GUI input even after the run scene is installed.
        if (NModalContainer.Instance?.OpenModal is NEarlyAccessDisclaimer disclaimer)
            await Timed(disclaimer.CloseScreen(), "native startup disclaimer close");
        var modal = NModalContainer.Instance ?? throw new InvalidOperationException("Native modal container missing after startup.");
        MegaCrit.Sts2.Core.Nodes.Multiplayer.NGenericPopup? confirmingAnnouncement = null;
        bool StartupModalClear()
        {
            if (modal.OpenModal == null) return true;
            if (modal.OpenModal is not MegaCrit.Sts2.Core.Nodes.Multiplayer.NGenericPopup popup)
                throw new InvalidOperationException("Unrecognized startup modal: " + modal.OpenModal.GetType().FullName);
            var vertical = popup.GetNode<NVerticalPopup>("VerticalPopup");
            var title = vertical.GetNode<Control>("Header").Get("text").AsString();
            var body = vertical.GetNode<Control>("Description").Get("text").AsString();
            // UndoAndRestart registers these exact localized strings before it
            // opens its one-time announcement, with only a Yes button.
            var expectedTitle = new MegaCrit.Sts2.Core.Localization.LocString("main_menu_ui", "UNDO_AND_RESTART_FEATURE.title").GetFormattedText();
            var expectedBody = new MegaCrit.Sts2.Core.Localization.LocString("main_menu_ui", "UNDO_AND_RESTART_FEATURE.body").GetFormattedText();
            if (title != expectedTitle || body != expectedBody || vertical.NoButton.Visible)
                throw new InvalidOperationException("Unrecognized startup popup: " + title + " | " + body);
            if (!ReferenceEquals(confirmingAnnouncement, popup) && vertical.YesButton.IsEnabled)
            {
                Log.Info("[NoSuffering] ROLLBACK_PROBE_ACKNOWLEDGE UndoAndRestart welcome announcement: " + title);
                confirmingAnnouncement = popup;
                // Native release resolves WaitForConfirmation(true); the mod
                // owns acknowledgment persistence and clearing its own popup.
                vertical.YesButton.ForceClick();
            }
            return false;
        }
        await Wait(StartupModalClear, "native startup modal clear");
        var backstop = modal.GetNode<ColorRect>("Backstop");
        var tweenField = typeof(NModalContainer).GetField("_backstopTween", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingFieldException("Native modal backstop tween changed.");
        void LogModal(string phase)
        {
            var tween = tweenField.GetValue(modal) as Tween;
            var tweenValid = tween != null && GodotObject.IsInstanceValid(tween) && tween.IsValid();
            var ancestors = new List<object>();
            for (Node? node = modal; node != null; node = node.GetParent())
                ancestors.Add(new { path = node.GetPath().ToString(), process_mode = node.ProcessMode.ToString(), can_process = node.CanProcess() });
            Log.Info("[NoSuffering] ROLLBACK_PROBE_STARTUP_UI " + JsonSerializer.Serialize(new {
                phase, process_id = System.Environment.ProcessId, display_server = DisplayServer.GetName(),
                open_modal = modal.OpenModal?.GetType().FullName ?? "<none>", mouse_filter = modal.MouseFilter.ToString(),
                backstop_visible = backstop.Visible, backstop_alpha = backstop.Color.A,
                children = modal.GetChildren().Select(child => child.Name.ToString()).ToArray(),
                tree_paused = tree.Paused, time_scale = Engine.TimeScale, ancestors,
                tween_valid = tweenValid, tween_running = tweenValid && tween!.IsRunning(),
                tween_elapsed = tweenValid ? tween!.GetTotalElapsedTime() : (double?)null
            }));
        }
        LogModal("before_empty_native_clear");
        Require(modal.OpenModal == null, "Cannot clear a real open modal in the fixture.");
        // A lab profile that already saw the disclaimer skips CloseScreen's
        // native Clear call and can retain an empty modal's input-catching
        // backstop when this probe installs the run scene directly.
        modal.Clear();
        LogModal("immediate_after_empty_native_clear");
        try
        {
            await Wait(() => StartupModalClear() && modal.MouseFilter == Control.MouseFilterEnum.Ignore && !backstop.Visible, "empty native modal backstop cleared");
        }
        catch (TimeoutException)
        {
            LogModal("timeout_empty_native_clear");
            throw;
        }
        LogModal("after_empty_native_clear");
    }

    private static async Task BuyCard()
    {
        var room = NMerchantRoom.Instance ?? throw new InvalidOperationException("No native merchant room.");
        room.OpenInventory();
        await IdleTransition();
        await Wait(() => room.Inventory.IsOpen && room.Inventory.IsVisibleInTree(), "merchant inventory");
        // IsOpen is set before the native one-second opening tween finishes.
        // Wait for that tween rather than clicking a slot while it is moving offscreen.
        var tweenField = typeof(NMerchantInventory).GetField("_inventoryTween", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingFieldException("Native merchant opening tween changed.");
        await Wait(() => tweenField.GetValue(room.Inventory) is Tween tween && !tween.IsRunning(), "merchant opening animation");
        var slot = room.Inventory.GetAllSlots().OfType<NMerchantCard>().Where(s => s.Entry.IsStocked && s.Entry.EnoughGold)
            .OrderBy(s => s.Entry.Cost).FirstOrDefault() ?? throw new InvalidOperationException("No affordable native shop card in fixture.");
        var player = Bridge.State.Players.Single();
        var count = player.Deck.Cards.Count;
        var gold = player.Gold;
        await Screenshot("nosuffering-shop-before-purchase.png");
        await Click(slot.Hitbox);
        await Wait(() => player.Deck.Cards.Count == count + 1 && player.Gold < gold && !slot.Entry.IsStocked, "actual native shop purchase");
    }

    private static async Task SelectRollback(MapCheckpoint checkpoint)
    {
        var revision = HostCoordinator.WorldRevision;
        MapRollbackSelection.Begin();
        await Wait(() => MapRollbackSelection.Active && NMapScreen.Instance is { IsOpen: true, IsTraveling: false }, "rollback selector");
        await IdleTransition();
        if (DisplayedAct() != checkpoint.ActIndex) await ShowSelectorAct(checkpoint.ActIndex);
        var node = Descendants<NMapPoint>(NMapScreen.Instance!).Single(p => p.Point.coord == checkpoint.Coord);
        node.Call("OnRelease");
        await Wait(() => !HostCoordinator.Busy && !MapRollbackSelection.Active, "selector host operation", 60000);
        Require(HostCoordinator.WorldRevision == revision + 1 && HostCoordinator.Status == "操作完成", "Rollback failed: " + HostCoordinator.Status);
    }

    private static async Task ShowSelectorAct(int act)
    {
        var screen = NMapScreen.Instance!;
        var button = Descendants<Button>(screen).Single(b => b.IsVisibleInTree() && (b.Text == $"第 {act + 1} 幕" || b.Text == $"Act {act + 1}"));
        await Click(button);
        await Wait(() => DisplayedAct() == act, "selector displayed act " + (act + 1));
    }
    private static int DisplayedAct()
    {
        var field = typeof(NMapScreen).GetField("_runState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingFieldException("Native map screen state changed.");
        return ((RunState)field.GetValue(NMapScreen.Instance!)!).CurrentActIndex;
    }
    private static string DisplayedMapFingerprint()
    {
        // ActMap.GetAllMapPoints excludes the separately rendered starting/boss
        // nodes; compare the same generated grid on both sides after cancellation.
        var grid = Bridge.State.Map.GetAllMapPoints().Select(p => p.coord).ToHashSet();
        return string.Join("|", Descendants<NMapPoint>(NMapScreen.Instance!).Select(p => p.Point).Where(p => grid.Contains(p.coord))
            .OrderBy(p => p.coord.row).ThenBy(p => p.coord.col)
            .Select(p => p.coord + ":" + p.PointType + ":" + string.Join(",", p.Children.Select(c => c.coord.ToString()).Order())));
    }

    private static string Resources()
    {
        var p = Bridge.State.Players.Single();
        return JsonSerializer.Serialize(new { p.Gold, hp = p.Creature.CurrentHp, maxHp = p.Creature.MaxHp,
            deck = p.Deck.Cards.Select(c => c.Id + ":" + c.IsUpgraded).ToArray(), relics = p.Relics.Select(r => r.Id.ToString()).ToArray(), potions = p.Potions.Select(potion => potion.Id.ToString()).ToArray() });
    }
    private static string MapFingerprint() => string.Join("|", Bridge.State.Map.GetAllMapPoints().OrderBy(p => p.coord.row).ThenBy(p => p.coord.col)
        .Select(p => p.coord + ":" + p.PointType + ":" + string.Join(",", p.Children.Select(c => c.coord.ToString()).Order())));
    private static IEnumerable<T> Descendants<T>(Node parent) where T : Node
    {
        if (parent is T result) yield return result;
        foreach (var child in parent.GetChildren()) foreach (var match in Descendants<T>(child)) yield return match;
    }
    private static async Task Screenshot(string name)
    {
        if (DisplayServer.GetName() == "headless") return;
        await Bridge.Frame();
        await NGame.Instance!.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        // Actual game viewport only; headless runs have no render artifact.
        using var image = NGame.Instance!.GetViewport().GetTexture().GetImage();
        var error = image.SavePng(Path.Combine(OS.GetUserDataDir(), name));
        Require(error == Error.Ok, "Native viewport screenshot failed: " + name + " " + error);
    }
    private static async Task Click(Control control)
    {
        Require(control.IsVisibleInTree(), "GUI target not visible: " + control.GetPath());
        await Bridge.Frame();
        // PushInput(..., true) consumes viewport coordinates. GlobalRect omits
        // a CanvasLayer's canvas transform and can miss native scaled UI.
        var point = control.GetGlobalTransformWithCanvas() * (control.Size * 0.5f);
        var globalRect = control.GetGlobalRect();
        var viewport = control.GetViewport();
        int guiInputs = 0, mousePressed = 0, mouseReleased = 0;
        string hoveredBefore = viewport.GuiGetHoveredControl()?.GetPath().ToString() ?? "<none>";
        void ObserveGui(InputEvent input) { if (input is InputEventMouseButton) guiInputs++; }
        void ObservePress(InputEvent _) => mousePressed++;
        void ObserveRelease(InputEvent _) => mouseReleased++;
        var native = control as NClickableControl;
        control.GuiInput += ObserveGui;
        if (native != null) { native.MousePressed += ObservePress; native.MouseReleased += ObserveRelease; }
        var diagnostic = new Dictionary<string, object> {
            ["target"] = control.GetPath().ToString(), ["global_rect"] = globalRect.ToString(),
            ["global_rect_center"] = globalRect.GetCenter().ToString(), ["canvas_center"] = point.ToString(),
            ["viewport_rect"] = viewport.GetVisibleRect().ToString(), ["viewport_size"] = viewport.GetVisibleRect().Size.ToString(),
            ["hover_before"] = hoveredBefore, ["mouse_filter"] = control.MouseFilter.ToString(),
            ["native_enabled"] = native?.IsEnabled.ToString() ?? "not_native_clickable"
        };
        Log.Info("[NoSuffering] ROLLBACK_PROBE_GUI_BEFORE " + JsonSerializer.Serialize(diagnostic));
        try
        {
            viewport.PushInput(new InputEventMouseMotion { Position = point, GlobalPosition = point }, true);
            await Bridge.Frame();
            diagnostic["hover_after_motion"] = viewport.GuiGetHoveredControl()?.GetPath().ToString() ?? "<none>";
            // Use Ritsu's native per-notification close API, which does not
            // invoke the optional action callback attached to a toast click.
            static Control? HoveredRitsuToast(Node? node)
            {
                for (; node != null; node = node.GetParent())
                    if (node is Control entry && node.GetType().FullName == "STS2RitsuLib.Ui.Toast.RitsuToastEntry") return entry;
                return null;
            }
            var toastDeadline = Time.GetTicksMsec() + 30000;
            while (HoveredRitsuToast(viewport.GuiGetHoveredControl()) is { } toast)
            {
                var toastPath = toast.GetPath().ToString();
                Node? host = toast.GetParent();
                while (host != null && host.GetType().FullName != "STS2RitsuLib.Ui.Toast.RitsuToastHost") host = host.GetParent();
                Require(host != null, "Unrecognized Ritsu toast host: " + toastPath);
                var hostType = host!.GetType();
                const System.Reflection.BindingFlags fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var visible = (System.Collections.IEnumerable)(hostType.GetField("_visible", fields)?.GetValue(host)
                    ?? throw new InvalidOperationException("Ritsu toast visible list missing: " + toastPath));
                static object? Value(object item, string name) => (item.GetType().GetProperty(name)
                    ?? throw new InvalidOperationException("Ritsu toast property missing: " + name)).GetValue(item);
                var item = visible.Cast<object>().Single(item => ReferenceEquals(Value(item, "Entry"), toast));
                var request = Value(item, "Request")!;
                var dismissOnClick = (bool)Value(request, "DismissOnClick")!;
                Log.Info("[NoSuffering] ROLLBACK_PROBE_DISMISS_TOAST " + JsonSerializer.Serialize(new {
                    path = toastPath, title = Value(request, "Title"), body = Value(request, "Body"), kind = Value(request, "Level")!.ToString(),
                    duration = Value(request, "DurationSeconds"), total_seconds = Value(item, "TotalSeconds"), remaining_seconds = Value(item, "RemainingSeconds"),
                    persistent = Value(request, "IsPersistent"), hovering = Value(item, "IsHovering"),
                    host_hovering_count = hostType.GetField("_hoveringCount", fields)!.GetValue(host), dismiss_on_click = dismissOnClick,
                    has_click_callback = Value(request, "OnClick") != null }));
                Require(dismissOnClick, "Ritsu toast does not allow click dismissal: " + toastPath);
                var now = Time.GetTicksMsec();
                Require(now < toastDeadline, "Ritsu toast kept blocking GUI target: " + toastPath);
                var close = hostType.GetMethod("Close", [typeof(Guid), typeof(bool)])
                    ?? throw new InvalidOperationException("Ritsu native Close API missing: " + toastPath);
                Require((bool)close.Invoke(host, [Value(item, "Id"), false])!, "Ritsu native Close rejected notification: " + toastPath);
                // Entry nodes are pooled and can immediately represent a new toast.
                // Await removal of this notification record rather than the node.
                await Wait(() => !visible.Cast<object>().Contains(item), "Ritsu native close: " + toastPath, toastDeadline - now);
                Require(control.IsVisibleInTree(), "GUI target disappeared while waiting for toast: " + control.GetPath());
                point = control.GetGlobalTransformWithCanvas() * (control.Size * 0.5f);
                viewport.PushInput(new InputEventMouseMotion { Position = point, GlobalPosition = point }, true);
                await Bridge.Frame();
                diagnostic["hover_after_toast_wait"] = viewport.GuiGetHoveredControl()?.GetPath().ToString() ?? "<none>";
            }
            viewport.PushInput(new InputEventMouseButton { Position = point, GlobalPosition = point, ButtonIndex = MouseButton.Left, Pressed = true }, true);
            await Bridge.Frame();
            viewport.PushInput(new InputEventMouseButton { Position = point, GlobalPosition = point, ButtonIndex = MouseButton.Left, Pressed = false }, true);
            await Bridge.Frame();
        }
        finally
        {
            // A successful button may remove its own UI subtree immediately.
            if (GodotObject.IsInstanceValid(control))
            {
                control.GuiInput -= ObserveGui;
                if (native != null) { native.MousePressed -= ObservePress; native.MouseReleased -= ObserveRelease; }
            }
            diagnostic["gui_mouse_button_events"] = guiInputs;
            diagnostic["native_mouse_pressed"] = mousePressed;
            diagnostic["native_mouse_released"] = mouseReleased;
            diagnostic["hover_after_click"] = viewport.GuiGetHoveredControl()?.GetPath().ToString() ?? "<none>";
            Log.Info("[NoSuffering] ROLLBACK_PROBE_GUI_AFTER " + JsonSerializer.Serialize(diagnostic));
        }
    }
    private static void KeyInput(NGame game, Key key)
    {
        game.GetViewport().PushInput(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true }, true);
        game.GetViewport().PushInput(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false }, true);
    }
    private static async Task IdleTransition() => await Wait(() => NGame.Instance!.Transition is { InTransition: false, MouseFilter: Control.MouseFilterEnum.Ignore }, "inactive transition");
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Timed(Task task, string detail) { await Wait(() => task.IsCompleted, detail, 60000); await task; }
    private static async Task Wait(Func<bool> done, string detail, ulong timeout = 30000)
    {
        var deadline = Time.GetTicksMsec() + timeout;
        while (!done()) { if (Time.GetTicksMsec() >= deadline) throw new TimeoutException("Timed out: " + detail); await Bridge.Frame(); }
    }
}
