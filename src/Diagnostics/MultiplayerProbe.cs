using System.Text.Json;
using System.Text.RegularExpressions;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using MegaCrit.Sts2.Core.Unlocks;
using NoSuffering.Ancients;
using NoSuffering.Checkpoints;
using NoSuffering.Combat;
using NoSuffering.Config;
using NoSuffering.Multiplayer;
using NoSuffering.Shops;
using Bridge = NoSuffering.GameBridge.GameBridge;

namespace NoSuffering.Diagnostics;

// Two real native ENet processes. Files coordinate fixture steps only; production
// operations and player choices travel through the actual authenticated transport.
public static class MultiplayerProbe
{
    private static string Role = "", Root = "", SeedStage = "";
    private static bool Enabled, Requested;
    private static readonly Dictionary<string,string> Results = new();
    private static ulong Me => RunManager.Instance.NetService.NetId;
    private static bool Host => Role == "host";
    private static string? Arg(string key) => OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith(key + "="))?[(key.Length + 1)..];
    public static void Initialize(SceneTree tree)
    {
        Requested=OS.GetCmdlineUserArgs().Contains("--ns-mp-probe");
        if (!Requested) return;
        Role = Arg("--ns-mp-role") ?? "";
        Root = Arg("--ns-mp-root") ?? "";
        if(Path.IsPathFullyQualified(Root))Root=Path.GetFullPath(Root);
        SeedStage = Arg("--ns-mp-seed-stage") ?? "";
        string platform = OS.GetName() == "macOS" ? "macos" : OS.GetName() == "Windows" ? "windows" : "";
        string native = Path.GetFullPath(OS.GetExecutablePath()).Replace('\\','/');
        string marker = "/.tools/game-lab/mp/" + platform + "/";
        int markerIndex = native.LastIndexOf(marker, StringComparison.Ordinal);
        string[] parts = markerIndex < 0 ? [] : native[(markerIndex + marker.Length)..].Split('/');
        string project = markerIndex < 0 ? "" : native[..markerIndex];
        string branch = parts.Length >= 4 ? parts[0] : "";
        string stamp = parts.Length >= 4 ? parts[1] : "";
        string expectedRoot = Path.GetFullPath(Path.Combine(project, "artifacts", platform, branch, "mp-" + stamp));
        string expectedUser = Path.GetFullPath(Path.Combine(OS.GetDataDir(), "NoSufferingLab", "mp", platform + "-" + branch, stamp, Role));
        Enabled = OS.GetCmdlineUserArgs().Contains("--ns-lab-probe") &&
            SeedStage is "" or "first" or "resume" && CommandLineHelper.GetValue("force-steam") == "off" && Role is "host" or "client" && platform != "" &&
            branch is "public-beta" or "public" && Regex.IsMatch(stamp, "^[0-9]{8}-[0-9]{6}$") &&
            parts.Length >= 4 && parts[2] == Role && parts[3] == "game" &&
            File.Exists(Path.Combine(project, "NoSuffering.csproj")) &&
            Canonical(Root) && Canonical(expectedUser) &&
            Path.GetFullPath(Root) == Path.GetFullPath(expectedRoot) &&
            Path.GetFullPath(OS.GetUserDataDir()) == Path.GetFullPath(expectedUser) &&
            File.Exists(Path.Combine(Root, ".mp-probe-session.json"));
        if (!Enabled) { Log.Error("[NoSuffering] MP_PROBE refused: requires exact fresh project artifact/app/user-directory hierarchy and offline lab arguments."); tree.Quit(1);return; }
        // Each role may initialize exactly once in this fresh script-owned slot.
        using (File.Open(Path.Combine(Root, "initialized-" + SeedStage + "-" + Role), FileMode.CreateNew, System.IO.FileAccess.Write)) { }
        void Start() { tree.ProcessFrame -= Start; _ = Run(tree); }
        tree.ProcessFrame += Start;
    }
    private static bool Canonical(string path)
    {
        if (!Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path) return false;
        for (DirectoryInfo? directory = new DirectoryInfo(path); directory != null; directory = directory.Parent)
            if (directory.LinkTarget != null) return false;
        return true;
    }
    [HarmonyPatch(typeof(ENetConnection), nameof(ENetConnection.CreateHostBound))]
    private static class LoopbackOnly
    {
        static void Prefix([HarmonyArgument(0)] ref string address)
        {
            if(Requested && !Enabled)throw new InvalidOperationException("Refused native multiplayer probe binding outside the isolated lab hierarchy.");
            if (Enabled) { address = "127.0.0.1"; Log.Info("[NoSuffering] MP_PROBE_BIND 127.0.0.1"); }
        }
    }
    private static async Task Run(SceneTree tree)
    {
        string[] checks = ["native_two_peer_start", "authenticated_ancient_choice", "client_F03_after_host_claim",
            "independent_claim_native_proceed", "client_shop_refresh", "courier_purchase_owner_stock",
            "host_only_boss_hp", "normal_restart", "new_order_restart", "map_rollback", "pending_ancient_choice_boundary", "shop_disk_continue",
            "victory_pending_rewards_rollback"];
        if(SeedStage!="")checks=["fresh_process_seed_"+SeedStage];
        foreach (string check in checks) Results[check] = "UNEXECUTED";
        string stage = checks[0];
        try
        {
            if(SeedStage=="resume") {await ResumeSeed(tree);return;}
            await Wait(() => Find<NCharacterSelectScreen>(tree.Root)?.Lobby?.Players.Count == 2, "native two-player lobby", 90);
            var screen = Find<NCharacterSelectScreen>(tree.Root)!;
            screen.Lobby.SetLocalCharacter(ModelDb.Character<Ironclad>());
            // Native lobby/run/transport remain real. Unlocks are a fixture so a
            // fresh offline account starts at Neow rather than tutorial combat.
            for(int slot=0;slot<screen.Lobby.Players.Count;slot++)
            {
                var player=screen.Lobby.Players[slot];player.unlockState=UnlockState.all.ToSerializable();
                player.unlockState.NumberOfRuns=100; // Native network field is 16 bits.
                screen.Lobby.Players[slot]=player;
            }
            if (Host) NGame.Instance!.DebugSeedOverride="NOSUFFERINGMP";
            await Barrier("lobby");
            screen.Lobby.SetReady(true);
            await Wait(() => RunManager.Instance.IsInProgress && NRun.Instance != null && Bridge.State.Map != null && Bridge.State.CurrentRoom != null, "native run launch", 90);
            Require(Me == (Host ? 1UL : 2UL) && Bridge.State.Players.Count == 2, "Unexpected native ENet player identity.");
            if (Host) ConfigStore.ChangeRules(r => r with { EnableAncientOptionsReroll=true, OptionsCostMode=RefreshCostMode.Hp,
                OptionsHpCost=3,EnableShopReroll=true,EnableBossHealthIncrease=true,EnableCombatRestart=true,EnableCombatReroll=true,EnableMapRollback=true });
            await Barrier("run");
            if (!AncientService.IsAncientRoom) await Timed(RunManager.Instance.EnterMapCoord(Bridge.State.Map.StartingMapPoint.coord), "initial ancient");
            await Wait(() => AncientService.IsAncientRoom && RunManager.Instance.EventSynchronizer.Events.Count==Bridge.State.Players.Count && RunManager.Instance.EventSynchronizer.Events.All(e => e.CurrentOptions.Count > 0), "native ancient models");
            await Barrier("ancient");
            if(SeedStage=="first") {await FirstSeed();return;}
            Pass(stage,"native ENet host 1/client 2, native lobby/run/ancient");

            stage=checks[1];
            if (!Host)
            {
                var data=new AncientChoiceData(1,0,AncientService.ChoiceContext(1));
                Send(Phase.AncientChoiceRequest,JsonSerializer.Serialize(data));
            }
            await Frames(30);
            Require(RunManager.Instance.EventSynchronizer.Events.All(e => !e.IsFinished && e.CurrentOptions.All(o => !o.WasChosen)), "Client selected another player's reward.");
            await Barrier("identity");
            Pass(stage,"actual client forged host actor choice rejected; no reward committed");
            await EnsureSimpleReward(1,"host_reward");
            stage=checks[10];
            // Native BeforeChosen is a multicast Func<Task>: only its last
            // subscriber's Task is awaited. Finish native UI subscription first
            // so this fixture delay remains the actual native awaited task.
            await Wait(()=>NEventRoom.Instance?.Layout?.OptionButtons.Any(b=>Event(Me).CurrentOptions.Contains(b.Option))==true,"settled native reward UI");
            await Barrier("pending_fixture_ui");
            string gate=Path.Combine(Root,"release-native-host-reward");
            foreach(var option in Event(1).CurrentOptions)
                option.BeforeChosen += async _ => await Wait(()=>File.Exists(gate),"delayed native reward fixture");
            if (Host) await ClickReward();
            await Wait(()=>Event(1).CurrentOptions.Any(o=>o.WasChosen)&&AncientService.HasUnfinishedCommittedChoices,"native reward pending");
            int hpBeforePending=Player(2).Creature.CurrentHp;
            async Task ReleaseNativeReward()
            {
                await Wait(()=>HostCoordinator.Busy,"pending refresh preparation");
                await Frames(10);
                Require(Player(2).Creature.CurrentHp==hpBeforePending,"Refresh charged HP before native choice boundary.");
                await Barrier("pending_choice_no_charge");
                if(Host)File.WriteAllText(gate,"release actual native reward continuation");
            }
            var release=ReleaseNativeReward();
            await Operation(CoreOperation.AncientOptionsReroll,2,"pending_choice_F03");
            await release;
            Require(!AncientService.HasUnfinishedCommittedChoices,"Native pending reward remained unsafe after refresh.");
            Pass(checks[10],"actual native reward task drained before refresh charge/save; selection UI continuation remained runnable");
            await Wait(() => Event(1).IsFinished,"host reward finished");
            await Barrier("host_claim");

            stage=checks[2];
            var hostEvent=Event(1);int hostHp=Player(1).Creature.CurrentHp;int clientHp=Player(2).Creature.CurrentHp;
            await Operation(CoreOperation.AncientOptionsReroll,2,"client_F03");
            Require(ReferenceEquals(hostEvent,Event(1)) && Event(1).IsFinished && Player(1).Creature.CurrentHp==hostHp &&
                Player(2).Creature.CurrentHp==clientHp-3,"Personal reroll changed teammate or charged wrong HP.");
            Pass(stage,"client request rerolled only own event and charged own 3 HP after host claimed");
            stage=checks[3];
            await EnsureSimpleReward(2,"client_reward");
            if (!Host) await ClickReward();
            await Wait(() => Event(2).IsFinished && ((EventRoom)Bridge.State.CurrentRoom!).IsPreFinished,"independent native claims");
            await Timed(RunManager.Instance.EventSynchronizer.AwaitPendingOptionTasks(),"native pending rewards");
            await Wait(() => NEventRoom.Instance?.Layout?.OptionButtons.Any(b=>b.Option.IsProceed)==true,"native Proceed");
            NEventRoom.Instance!.Layout!.OptionButtons.Single(b=>b.Option.IsProceed).Call(NEventOptionButton.MethodName.OnRelease);
            await Frames(5);
            Require(NMapScreen.Instance is {IsOpen:true,IsTravelEnabled:true},"Native Proceed did not open map.");
            await Barrier("claims");Pass(checks[3],"both actual native reward handlers and local Proceed completed");

            stage=checks[4];
            var shop=Bridge.State.Map.GetAllMapPoints().Where(p=>p.PointType==MapPointType.Shop).OrderBy(p=>p.coord.row).ThenBy(p=>p.coord.col).First();
            await Timed(RunManager.Instance.EnterMapCoord(shop.coord),"native shop fixture");
            await Barrier("shop");
            var room=(MerchantRoom)Bridge.State.CurrentRoom!;
            var oldHost=room.Inventories.Single(i=>i.Player.NetId==1);
            await Operation(CoreOperation.ShopReroll,2,"client_shop");
            Require(ReferenceEquals(oldHost,room.Inventories.Single(i=>i.Player.NetId==1)),"Client shop refresh replaced host inventory.");
            Pass(stage,"actual client shop request replaced only client stock");

            stage=checks[5];
            await Timed(RelicCmd.Obtain<TheCourier>(Player(2)),"Courier native fixture");
            await Timed(PlayerCmd.SetGold(10000,Player(2)),"gold native fixture");
            await Barrier("courier_fixture");
            if (!Host)
            {
                var inv=room.GetLocalInventory();var offer=inv.CharacterCardEntries.First(e=>e.IsStocked && e.Cost>0);
                bool bought=await offer.OnTryPurchaseWrapper(inv);
                Require(bought && offer.IsStocked,"Native Courier purchase/restock failed.");
            }
            await Barrier("courier_bought");
            await Wait(()=>Player(2).Gold<10000,"native remote gold sync");
            await Operation(CoreOperation.ShopReroll,1,"host_shop_after_client_purchase");
            var stock=ShopService.CaptureState()!.Players[2];
            Require(stock.Purchased,"Owner purchase lock missing after authenticated stock merge.");
            string ownerStock=JsonSerializer.Serialize(stock);
            File.WriteAllText(Path.Combine(Root,$"stock-{Role}.json"),ownerStock);
            await Barrier("owner_stock");
            Require(File.ReadAllText(Path.Combine(Root,"stock-host.json"))==File.ReadAllText(Path.Combine(Root,"stock-client.json")),"Courier owner stock differs between peers.");
            long beforeReject=HostCoordinator.WorldRevision;
            if (!Host) HostCoordinator.Submit(CoreOperation.ShopReroll);
            await Frames(60);await Wait(()=>!HostCoordinator.Busy,"rejected shop request");
            Require(HostCoordinator.WorldRevision==beforeReject,"Purchased client shop request committed.");
            await Barrier("purchase_lock");Pass(stage,"native client Courier purchase, owner stock merge, other player refresh and purchase lock");

            stage=checks[11];
            if(Host)
            {
                await Timed(CompanionStore.SaveNativeCurrent(),"native multiplayer shop disk save");
                var nativeSave=(RunSaveManager)AccessTools.Field(typeof(SaveManager),"_runSaveManager").GetValue(SaveManager.Instance)!;
                var saved=nativeSave.LoadMultiplayerRunSave();
                Require(saved.Success && saved.SaveData!=null && CompanionStore.Error==null,"Native multiplayer shop disk/companion pairing failed.");
                Require(saved.SaveData!.PreFinishedRoom==null,"Merchant native save retained unloadable prefinished room.");
                File.WriteAllText(Path.Combine(Root,"shop-disk-native.json"),Bridge.Freeze(saved.SaveData));
                File.WriteAllText(Path.Combine(Root,"shop-disk-companion.json"),JsonSerializer.Serialize(CheckpointService.Save()));
            }
            await Barrier("shop_disk_saved");
            var diskShop=JsonSerializer.Deserialize<TimelineData>(File.ReadAllText(Path.Combine(Root,"shop-disk-companion.json")))!;
            CheckpointService.Restore(diskShop);
            await Timed(Bridge.LoadAsync(Bridge.Thaw(File.ReadAllText(Path.Combine(Root,"shop-disk-native.json")))),"actual native two-peer shop disk continue");
            Bridge.Unlock();
            await Barrier("shop_disk_loaded");
            Require(Bridge.State.CurrentRoom is MerchantRoom,"Native disk continue did not re-enter shop.");
            Require(JsonSerializer.Serialize(ShopService.CaptureState()!.Players[2])==ownerStock,"Native disk continue lost Courier owner stock or purchase lock.");
            Require(ShopService.GetUnavailableReason(2,ConfigStore.Rules)!=null,"Native disk continue unlocked purchased client shop.");
            Pass(stage,"actual host native disk read and two-peer native load retained Courier stock/purchase lock");

            stage=checks[12];
            var ordinaryMonster=Bridge.State.Map.GetAllMapPoints().Where(p=>p.PointType==MapPointType.Monster)
                .OrderBy(p=>p.coord.row).ThenBy(p=>p.coord.col).First();
            int rewardAct=Bridge.State.CurrentActIndex;
            await Timed(RunManager.Instance.EnterMapCoord(ordinaryMonster.coord),"native ordinary monster reward fixture");
            await Opening();
            await Barrier("ordinary_monster_ready");
            await Timed(CreatureCmd.Kill(CombatManager.Instance.DebugOnlyGetState()!.Enemies.ToList(),true),"peer-symmetric native ordinary victory");
            await Timed(CombatManager.Instance.CheckWinCondition(),"native ordinary victory safe point");
            await Wait(()=>!CombatManager.Instance.IsInProgress&&Descendants<NRewardsScreen>(tree.Root).Any(),"native ordinary victory rewards");
            await Barrier("ordinary_rewards_generated");
            var originalRewards=Descendants<NRewardsScreen>(tree.Root).Single();
            var originalResources=LocalResources();
            var originalRewardCards=RewardCardsFingerprint(originalRewards);
            var originalRngOdds=RewardRngOddsFingerprint();
            var originalGold=Descendants<NRewardButton>(originalRewards).First(b=>b.Reward is GoldReward);
            await ClaimLocalGold(originalGold,"native ordinary gold claimed");
            var oneClaimResources=LocalResources();
            await Barrier("ordinary_gold_claimed");
            var originalProceed=Descendants<NProceedButton>(originalRewards).Single();
            await Wait(()=>originalProceed.IsEnabled,"native ordinary rewards Proceed enabled");
            originalProceed.ForceClick();
            await Wait(()=>NMapScreen.Instance is {IsOpen:true,IsTravelEnabled:true}&&!Descendants<NRewardsScreen>(tree.Root).Any(),"native ordinary rewards settled map");
            await Barrier("ordinary_rewards_settled");
            long rewardCheckpoint=Host?CheckpointService.History.Last(c=>c.ActIndex==rewardAct&&c.Coord==ordinaryMonster.coord&&c.BeforeRewards).Id:0;
            await Operation(CoreOperation.MapRollback,1,"victory_pending_rewards",checkpoint:rewardCheckpoint);
            Require(Bridge.State.CurrentActIndex==rewardAct&&Bridge.State.CurrentMapCoord==ordinaryMonster.coord&&
                Bridge.State.CurrentRoom is CombatRoom {IsPreFinished:true}&&!CombatManager.Instance.IsInProgress,
                "Pending-reward rollback did not restore the completed native combat.");
            await Wait(()=>Descendants<NRewardsScreen>(tree.Root).Any(),"restored native ordinary victory rewards");
            var restoredRewards=Descendants<NRewardsScreen>(tree.Root).Single();
            Require(LocalResources()==originalResources,"Rollback did not restore each peer's pre-claim resources.");
            Require(RewardCardsFingerprint(restoredRewards)==originalRewardCards,"Rollback changed a peer's native card reward options.");
            Require(RewardRngOddsFingerprint()==originalRngOdds,"Rollback changed a peer's reward RNG or rarity odds.");
            var restoredGold=Descendants<NRewardButton>(restoredRewards).First(b=>b.Reward is GoldReward);
            await ClaimLocalGold(restoredGold,"restored native ordinary gold claim");
            var restoredProceed=Descendants<NProceedButton>(restoredRewards).Single();
            await Wait(()=>restoredProceed.IsEnabled,"restored native ordinary rewards Proceed enabled");
            restoredProceed.ForceClick();
            await Wait(()=>NMapScreen.Instance is {IsOpen:true,IsTravelEnabled:true}&&!Descendants<NRewardsScreen>(tree.Root).Any(),"restored native ordinary rewards settled map");
            Require(LocalResources()==oneClaimResources,"Reclaimed native gold/Proceed did not reproduce exactly one original claim.");
            await Barrier("victory_pending_rewards_complete");
            Pass(stage,"both native peers claimed gold and settled; host rolled back to pre-reward combat, restoring local cards/RNG/odds/resources, then each reclaimed gold and proceeded once");

            stage=checks[6];
            await Timed(RunManager.Instance.EnterAct(2,false),"act-three fixture");
            await Barrier("act_three");
            var rollbackShop=Bridge.State.Map.GetAllMapPoints().Where(p=>p.PointType==MapPointType.Shop).OrderBy(p=>p.coord.row).ThenBy(p=>p.coord.col).First();
            await Timed(RunManager.Instance.EnterMapCoord(rollbackShop.coord),"act-three rollback target fixture");
            await Barrier("act_three_rollback_target");
            await Timed(RunManager.Instance.EnterMapCoord(Bridge.State.Map.BossMapPoint.coord),"native boss fixture");
            await Opening();await Barrier("boss");
            var health=Health();long revision=HostCoordinator.WorldRevision;
            if(!Host)HostCoordinator.Submit(CoreOperation.BossHealthIncrease,percent:25);
            await Frames(20);Require(HostCoordinator.WorldRevision==revision && Health().SequenceEqual(health),"Client changed boss HP.");
            await Operation(CoreOperation.BossHealthIncrease,1,"boss_hp",25);
            var boosted=Health();Require(boosted.Length==health.Length && boosted.Where((hp,i)=>hp.Max!=health[i].Max+(health[i].Max+3)/4 || hp.Hp!=health[i].Hp+(health[i].Max+3)/4).Count()==0,"Boss HP increment differs.");
            Pass(stage,"client host-only request ignored; host increased live boss current/max HP on both peers");
            int attemptBefore=CombatService.Attempt;
            string orderBefore=JsonSerializer.Serialize(CombatService.CurrentOrderDigests);
            stage=checks[7];await Operation(CoreOperation.CombatRestart,1,"restart");
            Require(CombatService.Attempt==attemptBefore && JsonSerializer.Serialize(CombatService.CurrentOrderDigests)==orderBefore,"Normal restart changed active attempt or draw-order digests.");
            Require(Health().Select(h=>h.Max).SequenceEqual(boosted.Select(h=>h.Max)),"Normal restart lost boss increment.");Pass(stage,"actual two-peer native combat reload retained boss increment");
            stage=checks[8];await Operation(CoreOperation.CombatReroll,1,"new_order");
            Require(CombatService.Attempt==attemptBefore+1,"New-order restart did not advance attempt exactly once.");
            Require(Health().Select(h=>h.Max).SequenceEqual(boosted.Select(h=>h.Max)),"New-order restart lost boss increment.");Pass(stage,"actual two-peer refreshed-order reload retained boss increment");
            stage=checks[9];await Barrier("before_rollback");
            long target=Host?CheckpointService.History.Last(c=>c.ActIndex==2 && c.IsNodeTarget).Id:0;
            await Operation(CoreOperation.MapRollback,1,"rollback",checkpoint:target);
            Require(!CombatManager.Instance.IsInProgress,"Rollback retained active boss combat.");Pass(stage,"actual native two-peer rollback completed");
            await Barrier("complete");
        }
        catch(Exception e){Results[stage]="FAIL: "+e;Log.Error("[NoSuffering] MP_PROBE "+e);File.WriteAllText(Path.Combine(Root,"failed-"+Role),e.ToString());}
        finally
        {
            string report=JsonSerializer.Serialize(new {scope="two real native localhost ENet processes; direct fixture room/act entries; no Steam, internet or human-pointer multiplayer claim",role=Role,results=Results});
            Log.Info("[NoSuffering] MP_PROBE_RESULTS "+report);File.WriteAllText(Path.Combine(Root,"report-"+Role+".json"),report);
            tree.Quit(Results.Values.All(r=>r.StartsWith("PASS"))?0:1);
        }
    }
    private sealed record SeedProof(ulong Seed,long Revision,int ClientGold,int ClientCards);
    private static async Task FirstSeed()
    {
        if(Host)ConfigStore.ChangeRules(r=>r with {EnableShopReroll=true});
        await Barrier("seed_first_run");
        var shop=Bridge.State.Map.GetAllMapPoints().First(p=>p.PointType==MapPointType.Shop);
        await Timed(RunManager.Instance.EnterMapCoord(shop.coord),"seed initial shop fixture");
        await Operation(CoreOperation.ShopReroll,1,"seed_first_refresh");
        Require(HostCoordinator.WorldRevision==1,"Fresh fixture did not use first committed operation.");
        await Timed(RelicCmd.Obtain<TheCourier>(Player(2)),"fresh-process Courier fixture");
        await Timed(PlayerCmd.SetGold(10000,Player(2)),"fresh-process purchase gold fixture");
        await Barrier("seed_purchase_fixture");
        if(!Host)
        {
            var inv=((MerchantRoom)Bridge.State.CurrentRoom!).GetLocalInventory();
            Require(await inv.CharacterCardEntries.First(e=>e.IsStocked&&e.Cost>0).OnTryPurchaseWrapper(inv),"Fresh-process native client purchase failed.");
        }
        await Barrier("seed_purchase_persisted");
        Require(HostCoordinator.WorldRevision==2 && !HostCoordinator.Busy,"Purchase did not commit its own native save boundary.");
        var proof=new SeedProof(ShopService.CaptureState()!.Players[1].Seed,HostCoordinator.WorldRevision,Player(2).Gold,Player(2).Deck.Cards.Count);
        File.WriteAllText(Path.Combine(Root,"seed-proof-"+Role+".json"),JsonSerializer.Serialize(proof));
        File.WriteAllText(Path.Combine(Root,"seed-stock-"+Role+".json"),JsonSerializer.Serialize(ShopService.CaptureState()));
        // Deliberately no extra save or user operation after purchase: quit must
        // preserve the purchase through its own authenticated persistence boundary.
        await Barrier("seed_first_saved");
        Pass("fresh_process_seed_first","first reroll then native client Courier purchase committed its own disk save before process exit");
    }
    private static async Task ResumeSeed(SceneTree tree)
    {
        var proof=JsonSerializer.Deserialize<SeedProof>(File.ReadAllText(Path.Combine(Root,"seed-proof-"+Role+".json")))!;
        await Wait(()=>Find<NMultiplayerLoadGameScreen>(tree.Root)!=null,"native saved multiplayer lobby",90);
        var screen=Find<NMultiplayerLoadGameScreen>(tree.Root)!;
        var lobby=(LoadRunLobby)AccessTools.Field(typeof(NMultiplayerLoadGameScreen),"_runLobby").GetValue(screen)!;
#if STS2_STABLE
        await Wait(()=>lobby.ConnectedPlayerIds.Count==2,"native saved lobby peers",90);
#else
        await Wait(()=>lobby.PlayerCount==2,"native saved lobby peers",90);
#endif
        await Barrier("seed_resume_lobby");lobby.SetReady(true);
        await Wait(()=>RunManager.Instance.IsInProgress && Bridge.State.CurrentRoom is MerchantRoom,"native fresh-process shop continue",90);
        await Wait(()=>HostCoordinator.WorldRevision==proof.Revision,"persisted revision restored",90);
        await Wait(()=>NMerchantRoom.Instance?.Inventory!=null,"native continued shop UI");
        Require(JsonSerializer.Serialize(ShopService.CaptureState())==File.ReadAllText(Path.Combine(Root,"seed-stock-"+Role+".json")),"Fresh multiplayer join lost saved owner stock.");
        Require(Player(2).Gold==proof.ClientGold && Player(2).Deck.Cards.Count==proof.ClientCards,"Fresh process lost native client purchase gold/card.");
        Require(ShopService.CaptureState()!.Players[2].Purchased,"Fresh process unlocked purchased client shop.");
        Require(ShopService.CaptureState()!.Players[1].Seed==proof.Seed,"Native fresh-process load lost saved shop seed.");
        await Operation(CoreOperation.ShopReroll,1,"seed_resume_refresh");
        Require(ShopService.CaptureState()!.Players[1].Seed!=proof.Seed,"First reroll in new process reused prior seed.");
        Require(HostCoordinator.WorldRevision==proof.Revision+1,"Fresh process did not advance persisted revision.");
        await Barrier("seed_resume_complete");
        Pass("fresh_process_seed_resume","actual exited/restarted native peers loaded saved run and first reroll used a distinct persisted-revision seed");
    }
    public static void RecordDigest(string native, string ancient, string shop, string boss)
    {
        if (!Enabled) return;
        File.WriteAllText(Path.Combine(Root,$"digest-{HostCoordinator.WorldRevision}-{Role}-native.json"),native);
        File.WriteAllText(Path.Combine(Root,$"digest-{HostCoordinator.WorldRevision}-{Role}-companion.json"),
            JsonSerializer.Serialize(new {ancient,shop,boss,order=CombatService.CurrentOrderDigests}));
    }
    private static IEnumerable<T> Descendants<T>(Node parent) where T:Node
    {
        if(parent is T result)yield return result;
        foreach(Node child in parent.GetChildren())foreach(var match in Descendants<T>(child))yield return match;
    }
    private static string RewardCardsFingerprint(NRewardsScreen screen)
    {
        var field=typeof(NRewardsScreen).GetField("_rewardsSet",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)
            ??throw new MissingFieldException("Native reward screen set changed.");
        var set=(RewardsSet)(field.GetValue(screen)??throw new InvalidOperationException("Native reward screen has no RewardsSet."));
        var options=new JsonSerializerOptions {IncludeFields=true};
        return string.Join("|",set.Rewards.Select(reward=>reward is CardReward card
            ?"Card:"+string.Join(",",card.Cards.Select(CardKey))
            :reward.GetType().Name+":"+JsonSerializer.Serialize(reward.ToSerializable(),options)).OrderBy(value=>value,StringComparer.Ordinal));
    }
    private static string CardKey(CardModel card)=>card.Id+":"+card.IsUpgraded;
    private static string RewardRngOddsFingerprint()
    {
        var player=Player(Me);
#if STS2_STABLE
        var rng=new {seed=player.PlayerRng.Seed,counter=player.PlayerRng.Rewards.Counter};
#else
        var rng=player.PlayerRng.ToSerializable().Rngs[PlayerRngType.Rewards];
#endif
        return JsonSerializer.Serialize(new {rewards_rng=rng,odds=player.PlayerOdds.ToSerializable()},new JsonSerializerOptions {IncludeFields=true});
    }
    private static string LocalResources()
    {
        var player=Player(Me);
        return JsonSerializer.Serialize(new {player.Gold,hp=player.Creature.CurrentHp,maxHp=player.Creature.MaxHp,
            deck=player.Deck.Cards.Select(c=>c.Id+":"+c.IsUpgraded).ToArray(),
            relics=player.Relics.Select(r=>r.Id.ToString()).ToArray(),potions=player.Potions.Select(p=>p.Id.ToString()).ToArray()});
    }
    private static async Task ClaimLocalGold(NRewardButton button,string detail)
    {
        int before=Player(Me).Gold;
        button.Call("OnRelease");
        await Wait(()=>Player(Me).Gold>before,detail);
    }
    private static MegaCrit.Sts2.Core.Entities.Players.Player Player(ulong id)=>Bridge.State.Players.Single(p=>p.NetId==id);
    private static EventModel Event(ulong id)=>RunManager.Instance.EventSynchronizer.GetEventForPlayer(Player(id));
    private static bool Simple(EventModel e)=>e.CurrentOptions.Any(o=>o.Relic?.Id.Entry is "NEOWS_TALISMAN" or "GOLDEN_PEARL" or "NUTRITIOUS_OYSTER" or "CURSED_PEARL" or "SILKEN_TRESS" or "LEAFY_POULTICE");
    private static async Task EnsureSimpleReward(ulong actor,string label)
    {
        for(int n=0;n<8;n++)
        {
            await Barrier(label+"_check_"+n);
            if(Simple(Event(actor)))return;
            await Operation(CoreOperation.AncientOptionsReroll,actor,label+"_reroll_"+n);
        }
        throw new InvalidOperationException("No prompt-free Neow reward in fixture.");
    }
    private static async Task ClickReward()
    {
        bool Match(NEventOptionButton button)=>Event(Me).CurrentOptions.Contains(button.Option) &&
            button.Option.Relic?.Id.Entry is "NEOWS_TALISMAN" or "GOLDEN_PEARL" or "NUTRITIOUS_OYSTER" or "CURSED_PEARL" or "SILKEN_TRESS" or "LEAFY_POULTICE";
        await Wait(()=>NEventRoom.Instance?.Layout?.OptionButtons.Any(Match)==true,"native refreshed reward UI");
        var button=NEventRoom.Instance!.Layout!.OptionButtons.First(Match);
        button.Call(NEventOptionButton.MethodName.OnRelease);
    }
    private static (int Hp,int Max)[] Health()=>((CombatRoom)Bridge.State.CurrentRoom!).Enemies.Where(e=>e.IsAlive).Select(e=>(e.CurrentHp,e.MaxHp)).ToArray();
    private static async Task Operation(CoreOperation kind,ulong actor,string label,int percent=0,long checkpoint=0)
    {
        await Barrier(label+"_before");long revision=HostCoordinator.WorldRevision;
        if(Me==actor)HostCoordinator.Submit(kind,checkpoint,percent);
        await Wait(()=>HostCoordinator.WorldRevision==revision+1 && !HostCoordinator.Busy,label,70);
        Require(HostCoordinator.Status=="操作完成",label+" failed after revision advanced: "+HostCoordinator.Status);
        await Barrier(label+"_after");
    }
    private static void Send(Phase phase,string payload)
    {
        var t=typeof(HostCoordinator);
        RunManager.Instance.NetService.SendMessage(new ControlMessage {Phase=phase,Payload=payload,Run=(string)AccessTools.Field(t,"_run").GetValue(null)!,Epoch=(string)AccessTools.Field(t,"_epoch").GetValue(null)!,Revision=HostCoordinator.WorldRevision});
    }
    private static async Task Opening()=>await Wait(()=>CombatManager.Instance.IsInProgress&&!CombatManager.Instance.IsStarting&&RunManager.Instance.ActionQueueSynchronizer.CombatState==ActionSynchronizerCombatState.PlayPhase,"combat opening");
    private static T? Find<T>(Node node)where T:Node {if(node is T match)return match;foreach(Node child in node.GetChildren()){var found=Find<T>(child);if(found!=null)return found;}return null;}
    private static async Task Barrier(string step)
    {
        File.WriteAllText(Path.Combine(Root,step+"-"+Role),"ready");
        await Wait(()=>File.Exists(Path.Combine(Root,step+"-host"))&&File.Exists(Path.Combine(Root,step+"-client")),"peer barrier "+step,90);
    }
    private static async Task Frames(int count){for(int n=0;n<count;n++)await Bridge.Frame();}
    private static async Task Timed(Task task,string label){try{await task.WaitAsync(TimeSpan.FromSeconds(90));}catch(TimeoutException){throw new InvalidOperationException("Timeout: "+label);}}
    private static async Task Wait(Func<bool> test,string label,int seconds=40)
    {
        ulong until=Time.GetTicksMsec()+(ulong)seconds*1000;
        while(!test()){if(File.Exists(Path.Combine(Root,"failed-host"))||File.Exists(Path.Combine(Root,"failed-client")))throw new InvalidOperationException("Other native peer failed: "+label);if(Time.GetTicksMsec()>until)throw new InvalidOperationException("Timeout: "+label+"; status="+HostCoordinator.Status);await Bridge.Frame();}
    }
    private static void Require(bool value,string reason){if(!value)throw new InvalidOperationException(reason);}
    private static void Pass(string check,string detail){Results[check]="PASS: "+detail;Log.Info("[NoSuffering] MP_PROBE "+Role+" "+check+" "+Results[check]);}
}
