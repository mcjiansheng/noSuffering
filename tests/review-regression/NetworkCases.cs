using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;
using NoSuffering.Ancients;
using NoSuffering.Checkpoints;
using NoSuffering.Config;
using NoSuffering.Multiplayer;
using Bridge = NoSuffering.GameBridge.GameBridge;

namespace NoSuffering.ReviewTests;

public static partial class ReviewProbe
{
    private static bool NetworkHost => Role == "host";
    private static bool NetworkAwaitingHostRestart;
    [HarmonyPatch(typeof(RunManager), nameof(RunManager.LocalPlayerDisconnected))]
    private static class RestartMenuScheduling
    {
        // Hold only native automatic menu navigation while this fixture restarts
        // its host. Network disconnect events and coordinator cleanup still run.
        static bool Prefix() => !(Case == "network" && Role == "client" && NetworkAwaitingHostRestart);
    }
    private static string NetworkRoot => Output;
    private static ulong NetworkMe => RunManager.Instance.NetService.NetId;
    private static string NetworkFile(string name) => Path.Combine(NetworkRoot, name);
    private static async Task NetworkBarrier(string name)
    {
        File.WriteAllText(NetworkFile(name + "-" + Role), "ready");
        await Wait(() => File.Exists(NetworkFile(name + "-host")) && File.Exists(NetworkFile(name + "-client")), name, 90000);
    }
    private static T? NetworkFind<T>(Node node) where T : Node
    {
        if (node is T found) return found;
        foreach (var child in node.GetChildren()) if (NetworkFind<T>(child) is { } result) return result;
        return null;
    }
    private static EventModel NetworkEvent(ulong id) => RunManager.Instance.EventSynchronizer.GetEventForPlayer(Bridge.State.Players.Single(p => p.NetId == id));
    private static object NetworkField(string name) => AccessTools.Field(typeof(HostCoordinator), name).GetValue(null)!;
    private static async Task NetworkOperation(ulong actor, string label)
    {
        await NetworkBarrier(label + "-before");
        long revision = HostCoordinator.WorldRevision;
        if (NetworkMe == actor) HostCoordinator.Submit(CoreOperation.AncientOptionsReroll);
        await Wait(() => !HostCoordinator.Busy && HostCoordinator.WorldRevision == revision + 1, label, 70000);
        Require(HostCoordinator.Status == "操作完成", label + ": " + HostCoordinator.Status);
        await NetworkBarrier(label + "-after");
    }
    private static bool NetworkSimple(EventModel model) => model.CurrentOptions.Any(o => o.Relic?.Id.Entry is "NEOWS_TALISMAN" or "GOLDEN_PEARL" or "NUTRITIOUS_OYSTER" or "CURSED_PEARL" or "SILKEN_TRESS" or "LEAFY_POULTICE");
    private static async Task NetworkLoadedLobby(string label)
    {
        await Wait(() => NetworkFind<NMultiplayerLoadGameScreen>(Tree.Root) != null, label + " screen", 90000);
        var screen = NetworkFind<NMultiplayerLoadGameScreen>(Tree.Root)!;
        var lobby = (LoadRunLobby)AccessTools.Field(typeof(NMultiplayerLoadGameScreen), "_runLobby").GetValue(screen)!;
#if STS2_STABLE
        await Wait(() => lobby.ConnectedPlayerIds.Count == 2, label + " peers", 90000);
#else
        await Wait(() => lobby.PlayerCount == 2, label + " peers", 90000);
#endif
        await NetworkBarrier(label); lobby.SetReady(true);
        await Wait(() => RunManager.Instance.IsInProgress && NRun.Instance != null && Bridge.State.CurrentRoom != null, label + " loaded run", 90000);
    }
    private static async Task NetworkResumeCheck()
    {
        await NetworkLoadedLobby("network-resume-lobby");
        await NetworkBarrier("network-resume-run");
        if (NetworkHost) ConfigStore.ChangeRules(r => r with { OptionsHpCost = 5 });
        await Wait(() => ConfigStore.Rules.OptionsHpCost == 5, "real host rule override after restart");
        await NetworkBarrier("network-resume-rules");
        var previous = JsonSerializer.Deserialize<NetworkLifetime>(File.ReadAllText(NetworkFile("network-lifetime-client.json")))!;
        Require(Bridge.RunKey == previous.Run && (string)NetworkField("_epoch") != previous.Epoch, "native reconnect did not retain RunKey and change host epoch");
        long revision = HostCoordinator.WorldRevision;
        var appliedAtState = (long)NetworkField("_appliedOperation");
        await NetworkBarrier("network-resume-operation");
        if (NetworkHost) HostCoordinator.Submit(CoreOperation.ShopReroll);
        await Frames(15);
        await Wait(() => !HostCoordinator.Busy, "first operation after host restart settles", 70000);
        await NetworkBarrier("network-resume-observed");
        Expect("same_process_reconnect_new_epoch", HostCoordinator.WorldRevision == revision + 1,
            new { previous, run = Bridge.RunKey, epoch = NetworkField("_epoch"), appliedAtState, activeOperation = NetworkField("_activeOperation"), revisionBefore = revision, revisionAfter = HostCoordinator.WorldRevision, HostCoordinator.Status });
        await NetworkBarrier("network-final-cleanup");
        if (!NetworkHost) CaptureRulesBeforeNetworkCleanup();
        RunManager.Instance.CleanUp();
        if (!NetworkHost) await VerifyLocalRulesAfterNetwork();
    }
    private sealed record NetworkLifetime(string Run, string Epoch, long Applied);
    private static async Task RunNetwork()
    {
        if (OS.GetCmdlineUserArgs().Contains("--ns-review-network-stage=resume")) { await NetworkResumeCheck(); return; }
        CaptureLocalRulesBeforeNetwork();
        await Wait(() => NetworkFind<NCharacterSelectScreen>(Tree.Root)?.Lobby?.Players.Count == 2, "native ENet lobby", 90000);
        var screen = NetworkFind<NCharacterSelectScreen>(Tree.Root)!;
        screen.Lobby.SetLocalCharacter(ModelDb.Character<Ironclad>());
        for (int i = 0; i < screen.Lobby.Players.Count; i++)
        {
            var player = screen.Lobby.Players[i]; player.unlockState = UnlockState.all.ToSerializable(); player.unlockState.NumberOfRuns = 100; screen.Lobby.Players[i] = player;
        }
        if (NetworkHost) NGame.Instance!.DebugSeedOverride = "REVIEWNETWORK";
        await NetworkBarrier("network-lobby"); screen.Lobby.SetReady(true);
        await Wait(() => RunManager.Instance.IsInProgress && NRun.Instance != null && Bridge.State.Map != null && Bridge.State.CurrentRoom != null, "native network run", 90000);
        Require(NetworkMe == (NetworkHost ? 1UL : 2UL), "unexpected native ENet identity");
        if (NetworkHost) ConfigStore.ChangeRules(r => r with { EnableAncientOptionsReroll = true, OptionsCostMode = RefreshCostMode.Free, OptionsHpCost = 5, EnableShopReroll = true });
        await NetworkBarrier("network-run");
        if (!AncientService.IsAncientRoom) await Timed(RunManager.Instance.EnterMapCoord(Bridge.State.Map.StartingMapPoint.coord), "native ancient entry");
        await Wait(() => AncientService.IsAncientRoom && RunManager.Instance.EventSynchronizer.Events.Count == 2 && NEventRoom.Instance?.Layout?.OptionButtons.Any() == true, "ancient UI");
        await NetworkBarrier("network-ancient");
        for (int i = 0; !NetworkSimple(NetworkEvent(1)); i++)
        {
            Require(i < 12, "prompt-free host reward unavailable");
            await NetworkOperation(1, "network-simple-" + i);
        }
        await Wait(() => NEventRoom.Instance?.Layout?.OptionButtons.Any(b => NetworkEvent(NetworkMe).CurrentOptions.Contains(b.Option)) == true, "settled native ancient UI");
        await NetworkBarrier("network-settled-ui");
        var gate = NetworkFile("network-release-native-reward");
        foreach (var option in NetworkEvent(1).CurrentOptions) option.BeforeChosen += async _ => await Wait(() => File.Exists(gate), "native reward fixture gate", 70000);
        await NetworkBarrier("network-gated");
        if (NetworkHost)
        {
            var button = NEventRoom.Instance!.Layout!.OptionButtons.First(b => b.Option.Relic?.Id.Entry is "NEOWS_TALISMAN" or "GOLDEN_PEARL" or "NUTRITIOUS_OYSTER" or "CURSED_PEARL" or "SILKEN_TRESS" or "LEAFY_POULTICE");
            button.Call(NEventOptionButton.MethodName.OnRelease);
        }
        await Wait(() => NetworkEvent(1).CurrentOptions.Any(o => o.WasChosen), "native pending host reward");
        await NetworkBarrier("network-pending");
        if (!NetworkHost) HostCoordinator.Submit(CoreOperation.AncientOptionsReroll);
        await Wait(() => HostCoordinator.Busy, "native Prepare");
        if (!NetworkHost)
        {
            Require(!(bool)AccessTools.Field(typeof(AncientService), "_busy").GetValue(null)!, "fixture missed preparation-only boundary");
            var count = NEventRoom.Instance!.Layout!.OptionButtons.Count();
            var button = NEventRoom.Instance.Layout.OptionButtons.First();
            button.Call(NEventOptionButton.MethodName.OnRelease);
            await Frames(2);
            File.WriteAllText(NetworkFile("network-busy-click.json"), JsonSerializer.Serialize(new { countBefore = count, countAfter = NEventRoom.Instance.Layout.OptionButtons.Count(), selected = NetworkEvent(2).CurrentOptions.Any(o => o.WasChosen) }));
        }
        await Wait(() => !HostCoordinator.Busy, "native preparation timeout abort", 70000);
        await NetworkBarrier("network-aborted");
        if (!NetworkHost)
        {
            var count = NEventRoom.Instance!.Layout!.OptionButtons.Count();
            Expect("ancient_click_during_prepare_keeps_buttons", count > 0 && !NetworkEvent(2).CurrentOptions.Any(o => o.WasChosen), new { countAfterAbort = count, click = File.ReadAllText(NetworkFile("network-busy-click.json")), HostCoordinator.Status });
        }
        await NetworkBarrier("network-button-proof");
        // Restore only the test's initial UI after the first independent case.
        // These options retain native BeforeChosen subscriptions from room entry.
        if (!NetworkHost && !NEventRoom.Instance!.Layout!.OptionButtons.Any())
            NEventRoom.Instance.Layout.AddOptions(NetworkEvent(2).CurrentOptions);
        await NetworkBarrier("network-reject-fixture");
        void PauseBeforePrepare()
        {
            if (!HostCoordinator.Busy) return;
            HostCoordinator.Changed -= PauseBeforePrepare;
            File.WriteAllText(NetworkFile("network-reject-host-busy"), "host busy before broadcasting Prepare");
            var deadline = System.Environment.TickCount64 + 10000;
            while (!File.Exists(NetworkFile("network-reject-client-sent")))
            {
                Require(System.Environment.TickCount64 < deadline, "client failed to send during held host Prepare");
                System.Threading.Thread.Sleep(10);
            }
        }
        if (NetworkHost)
        {
            HostCoordinator.Changed += PauseBeforePrepare;
            HostCoordinator.Submit(CoreOperation.AncientOptionsReroll);
        }
        else
        {
            await Wait(() => File.Exists(NetworkFile("network-reject-host-busy")), "host held before native Prepare");
            Require(!HostCoordinator.Busy, "client received Prepare before rejection fixture click");
            NEventRoom.Instance!.Layout!.OptionButtons.First().Call(NEventOptionButton.MethodName.OnRelease);
            Require((bool)NetworkField("_choicePending"), "native choice request was not sent");
            await Frames(2); // Flush the real native ENet request while host is held.
            File.WriteAllText(NetworkFile("network-reject-client-sent"), "actual client native option clicked and sent");
            await Wait(() => !(bool)NetworkField("_choicePending"), "actual host RequestRejected response");
            var count = NEventRoom.Instance.Layout.OptionButtons.Count();
            Expect("ancient_rejected_choice_restores_buttons", count > 0 && !NetworkEvent(2).CurrentOptions.Any(o => o.WasChosen),
                new { countAfterRejected = count, HostCoordinator.Status, HostCoordinator.Busy });
        }
        await NetworkBarrier("network-rejected-proof");
        await Wait(() => !HostCoordinator.Busy, "rejected choice preparation abort", 70000);
        await NetworkBarrier("network-rejected-aborted");
        if (NetworkHost) File.WriteAllText(gate, "release");
        await Wait(() => NetworkEvent(1).IsFinished, "native host reward finishes");
        await NetworkBarrier("network-reward-settled");
        var shop = Bridge.State.Map.GetAllMapPoints().Where(p => p.PointType == MapPointType.Shop).OrderBy(p => p.coord.row).ThenBy(p => p.coord.col).First();
        await Timed(RunManager.Instance.EnterMapCoord(shop.coord), "native shop fixture");
        await NetworkBarrier("network-shop");
        long revision = HostCoordinator.WorldRevision;
        if (NetworkHost) HostCoordinator.Submit(CoreOperation.ShopReroll);
        await Wait(() => !HostCoordinator.Busy && HostCoordinator.WorldRevision == revision + 1, "pre-restart applied operation", 70000);
        await NetworkBarrier("network-applied");
        File.WriteAllText(NetworkFile("network-lifetime-" + Role + ".json"), JsonSerializer.Serialize(new NetworkLifetime(Bridge.RunKey, (string)NetworkField("_epoch"), (long)NetworkField("_appliedOperation"))));
        if (!NetworkHost) NetworkAwaitingHostRestart = true;
        await NetworkBarrier("network-lifetime");
        if (NetworkHost)
        {
            await Timed(CompanionStore.SaveNativeCurrent(), "native save before host process restart");
            File.WriteAllText(NetworkFile("network-restart-request"), "restart host only with fastmp=load and stage=resume");
            return;
        }
        await Wait(() => File.Exists(NetworkFile("network-host-restarted")), "runner host process restart", 120000);
        await Timed(NGame.Instance!.ReturnToMainMenu(), "client same-process return to native menu");
        NetworkAwaitingHostRestart = false;
        await NetworkResumeCheck();
    }
}
