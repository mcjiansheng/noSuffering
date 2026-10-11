using System.Reflection;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using NoSuffering.Combat;
using NoSuffering.Config;
using NoSuffering.Multiplayer;
using NoSuffering.UI;
using Bridge = NoSuffering.GameBridge.GameBridge;

namespace NoSuffering.ReviewTests;

public static partial class ReviewProbe
{
    private static HostRules? _savedSoloRules;
    private static HostRules? _rulesBeforeNetworkCleanup;

    // Called before the client joins the real native network session.
    public static void CaptureLocalRulesBeforeNetwork()
    {
        Require(!RunManager.Instance.IsInProgress, "local rule fixture must precede network setup");
        ConfigStore.SetLocalRules(new HostRules { OptionsHpCost = 19 });
        _savedSoloRules = ConfigStore.Rules;
    }

    public static void CaptureRulesBeforeNetworkCleanup()
    {
        Require(RunManager.Instance.IsInProgress && RunManager.Instance.NetService.Type == NetGameType.Client,
            "rule restoration fixture requires a live native client session");
        _rulesBeforeNetworkCleanup = ConfigStore.Rules;
        Require(_savedSoloRules != null && _rulesBeforeNetworkCleanup != _savedSoloRules,
            "host rules must differ from saved solo rules before network cleanup");
    }

    // Called after the real client cleanup, not after a simulated mode flag change.
    public static async Task VerifyLocalRulesAfterNetwork()
    {
        Require(_savedSoloRules != null, "solo rules were not captured before network session");
        Require(_rulesBeforeNetworkCleanup != null && _rulesBeforeNetworkCleanup != _savedSoloRules,
            "different host rules were not observed before native cleanup");
        Require(!RunManager.Instance.IsInProgress, "native network run must be cleaned up first");
        var afterCleanup = ConfigStore.Rules;
        var writableAfterCleanup = ConfigStore.RulesWritable;
        await StartSolo("REVIEWLOCALRULES");
        Expect("local_rules_after_network_cleanup", afterCleanup == _savedSoloRules && writableAfterCleanup && ConfigStore.Rules == _savedSoloRules && ConfigStore.RulesWritable,
            new { saved = _savedSoloRules, beforeCleanup = _rulesBeforeNetworkCleanup, afterCleanup, writableAfterCleanup, afterSoloLaunch = ConfigStore.Rules, ConfigStore.RulesWritable });
    }

    public static async Task RunConfigUi()
    {
        await StartSolo("REVIEWCONFIGUI");
        var manager = RunManager.Instance;
        await Timed(manager.EnterMapCoord(Bridge.State.Map.StartingMapPoint.coord), "native ancient entry for panel");
        await Frames(5);
        ConfigStore.UpdateLocal(c => c with { TogglePanelKey = new HotkeyBinding(Key.F6) });
        Overlay.Close();
        var backdrop = (Control)typeof(Overlay).GetField("_backdrop", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        NGame.Instance!.GetViewport().PushInput(new InputEventKey { Keycode = Key.F6, PhysicalKeycode = Key.F6, Pressed = true });
        await Frames(2);
        Require(backdrop.Visible, "native F6 input did not open NoSuffering panel");
        var oldRoom = Bridge.State.CurrentRoom;
        var oldRunNode = NRun.Instance;
        var monster = Bridge.State.Map.GetAllMapPoints().Where(p => p.PointType == MapPointType.Monster)
            .OrderBy(p => p.coord.row).ThenBy(p => p.coord.col).First();
        await Timed(manager.EnterMapCoord(monster.coord), "native room travel with panel open");
        await Wait(() => CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsStarting &&
            manager.ActionQueueSynchronizer.CombatState == ActionSynchronizerCombatState.PlayPhase && CombatService.Baseline != null,
            "native combat opening");
        await Frames(5);
        Require(!ReferenceEquals(oldRoom, Bridge.State.CurrentRoom), "native travel did not replace room");
        Require(ReferenceEquals(oldRunNode, NRun.Instance), "native travel unexpectedly rebuilt NRun");
        Expect("overlay_closes_on_native_room_travel", !backdrop.Visible,
            new { beforeRoom = oldRoom?.GetType().Name, afterRoom = Bridge.State.CurrentRoom?.GetType().Name, backdrop.Visible, sameNRun = true });
        Overlay.Close();

        ConfigStore.ChangeRules(r => r with { EnableCombatRestart = true, EnableShopReroll = true });
        bool edited = false;
        string? editStatus = null;
        long editRevision = -1;
        void EditDuringPreparation()
        {
            if (edited || !HostCoordinator.Busy || !ConfigStore.OperationInProgress || HostCoordinator.Status != "等待当前动作完成") return;
            edited = true;
            editStatus = HostCoordinator.Status;
            editRevision = HostCoordinator.WorldRevision;
            ConfigStore.ChangeRules(r => r with { EnableShopReroll = false });
            Require(!ConfigStore.EditableRules.EnableShopReroll && ConfigStore.Rules.EnableShopReroll,
                "preparation edit was not queued independently from active rules");
        }
        var revision = HostCoordinator.WorldRevision;
        HostCoordinator.Changed += EditDuringPreparation;
        try
        {
            HostCoordinator.Submit(CoreOperation.CombatRestart);
            await Wait(() => !HostCoordinator.Busy, "native combat restart release after preparation edit");
        }
        finally { HostCoordinator.Changed -= EditDuringPreparation; }
        Require(edited, "coordinator preparation callback was not observed");
        Require(HostCoordinator.WorldRevision == revision + 1 && HostCoordinator.Status == "操作完成", "native operation failed: " + HostCoordinator.Status);
        using var savedConfig = JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath("user://NoSuffering/config.json")));
        bool savedShopReroll = savedConfig.RootElement.GetProperty("SinglePlayerRules").GetProperty("EnableShopReroll").GetBoolean();
        Expect("settings_edit_survives_operation_commit", !ConfigStore.Rules.EnableShopReroll && !ConfigStore.EditableRules.EnableShopReroll && !savedShopReroll,
            new { edited, editStatus, editRevision, revisionAfter = HostCoordinator.WorldRevision, active = ConfigStore.Rules.EnableShopReroll, editable = ConfigStore.EditableRules.EnableShopReroll, savedShopReroll });
        revision = HostCoordinator.WorldRevision;
        HostCoordinator.Submit(CoreOperation.CombatRestart);
        await Wait(() => !HostCoordinator.Busy, "subsequent native combat restart");
        Require(HostCoordinator.WorldRevision == revision + 1 && HostCoordinator.Status == "操作完成", "subsequent native operation failed: " + HostCoordinator.Status);
        Expect("settings_edit_survives_subsequent_operation", !ConfigStore.Rules.EnableShopReroll,
            new { revisionAfter = HostCoordinator.WorldRevision, active = ConfigStore.Rules.EnableShopReroll });
    }
}
