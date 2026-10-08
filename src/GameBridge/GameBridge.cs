using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using NoSuffering.Multiplayer;

namespace NoSuffering.GameBridge;

public static class GameBridge
{
    public static bool IsRestoring { get; private set; }
    private static bool _preserveTransport;
    private static bool _loadingMap;
    private static bool _loadingCombat;
    public static RunState State => RunManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("没有活动运行状态");
    public static string RunKey => Key(RunManager.Instance.ToSave(null));
    public static string Key(SerializableRun save) => Hash(string.Join("/", save.StartTime, save.SerializableRng.Seed,
        string.Join(",", save.Players.Select(p => p.NetId).Order())));
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static ulong StableSeed(params string[] fields)
    {
        using var data = new MemoryStream();
        foreach (var field in fields) {var bytes=Encoding.UTF8.GetBytes(field);data.Write(BitConverter.GetBytes(bytes.Length));data.Write(bytes);}
        return BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(data.ToArray()));
    }
    public static string Freeze(SerializableRun save) => SaveManager.ToJson(save);
    public static SerializableRun Thaw(string json)
    {
        var result=SaveManager.FromJson<SerializableRun>(json);
        if (!result.Success || result.SaveData is null) throw new InvalidOperationException("运行快照无法读取："+result.ErrorMessage);
        return result.SaveData;
    }
    public static SerializableRun Capture(AbstractRoom? room)
    {
        // Native room serialization rejects an active event-nested combat. The
        // completed event effects are already in the players; store its combat
        // initializer rather than replaying the event choice on loading.
        var save=RunManager.Instance.ToSave(room is CombatRoom { ParentEventId: not null, IsPreFinished: false } ? null : room);
        if(room is CombatRoom { ParentEventId: not null, IsPreFinished: false } combat)
            save.PreFinishedRoom=new SerializableRoom {
                RoomType=combat.RoomType, EncounterId=combat.Encounter.Id,
                EncounterState=combat.Encounter.SaveCustomState(), GoldProportion=combat.GoldProportion,
                ParentEventId=combat.ParentEventId, ShouldResumeParentEvent=combat.ShouldResumeParentEventAfterCombat,
                ExtraRewards=combat.ExtraRewards.ToDictionary(p=>p.Key.NetId,p=>p.Value.Select(r=>r.ToSerializable()).ToList())
            };
        return Thaw(Freeze(save)); // Break every mutable history/model reference.
    }
    public static async Task BoundaryAsync(CancellationToken cancellation=default)
    {
        var manager=RunManager.Instance;
        // Let submitted effects finish, including native player-choice continuations.
        // Do not pause a task that still needs its selection screen to finish.
        var deadline=Time.GetTicksMsec()+20000;
        while(manager.ActionExecutor.CurrentlyRunningAction is not null ||
              CombatManager.Instance.IsStarting || CombatManager.Instance.IsEnemyTurnStarted) {
            cancellation.ThrowIfCancellationRequested();
            if(Time.GetTicksMsec()>deadline) throw new InvalidOperationException("请先完成当前动作或选择");
            await Frame();
        }
        cancellation.ThrowIfCancellationRequested();
        manager.ActionExecutor.Pause();
        CombatManager.Instance.Pause();
    }
    public static async Task Frame() => await ((SceneTree)Engine.GetMainLoop()).ToSignal(Engine.GetMainLoop(),SceneTree.SignalName.ProcessFrame);
    public static void Unlock() { if(RunManager.Instance.IsInProgress) {RunManager.Instance.ActionExecutor.Unpause();CombatManager.Instance.Unpause();} }

    public static async Task LoadAsync(SerializableRun snapshot)
    {
        var manager=RunManager.Instance;
        var net=manager.NetService;
        var localId=net.NetId;
        if(!snapshot.Players.Any(p=>p.NetId==localId)) throw new InvalidOperationException("快照玩家阵容不符");
        var state=RunState.FromSerializable(snapshot);
        var game=NGame.Instance ?? throw new InvalidOperationException("游戏场景不可用");
        IsRestoring=true;
        _loadingMap=snapshot.PreFinishedRoom?.RoomType==RoomType.Map;
        _loadingCombat=snapshot.PreFinishedRoom is {IsPreFinished:false,EncounterId:not null};
        try {
            UI.Overlay.Close();
            game.GetViewport().GuiReleaseFocus();
            NTargetManager.Instance?.CancelTargeting();
            NHoverTipSet.Clear();
            await game.Transition.RoomFadeOut();
            // Keep transport alive only for this synchronous native teardown.
            // Native cleanup owns cancellation, UI and synchronizer lifetimes.
            _preserveTransport=net.Type is NetGameType.Host or NetGameType.Client;
            try {
                manager.EventSynchronizer.Dispose();
                manager.OneOffSynchronizer.Dispose();
                manager.CleanUp();
            } finally {_preserveTransport=false;}
            if(net.Type==NetGameType.Singleplayer) {
                await manager.SetUpSavedSingleplayer(state,snapshot);
                game.ReactionContainer.InitializeNetworking(manager.NetService);
                await game.LoadRun(state,snapshot.PreFinishedRoom);
            } else {
                var lobby=new LoadRunLobby(net,new ReloadListener(),snapshot);
                // Every participant is already connected and authenticated; preserve
                // native per-player IDs rather than creating host-only save players.
                foreach(var player in snapshot.Players)
                    lobby.Players.Add(new LoadRunLobbyPlayer {id=player.NetId,isReady=true,isModded=true});
                game.RemoteCursorContainer.Initialize(lobby.InputSynchronizer,snapshot.Players.Select(p=>p.NetId));
                game.ReactionContainer.InitializeNetworking(net);
                await manager.SetUpSavedMultiplayer(state,lobby);
                await game.LoadRun(state,snapshot.PreFinishedRoom);
                lobby.CleanUp(false);
            }
            await game.Transition.FadeIn();
        } finally {IsRestoring=false;_loadingMap=false;_loadingCombat=false;}
    }
    private sealed class ReloadListener : ILoadRunLobbyListener {
        public void PlayerConnected(LoadRunLobbyPlayer player){}
        public void RemotePlayerDisconnected(ulong id) => HostCoordinator.Fail("恢复时有玩家断线");
        public Task<bool> ShouldAllowRunToBegin()=>Task.FromResult(true);
        public void BeginRun(){}
        public void PlayerReadyChanged(ulong id){}
        public void LocalPlayerDisconnected(NetErrorInfo info)=>HostCoordinator.Fail(info.GetReason().ToString());
    }
    [HarmonyPatch(typeof(NGame),nameof(NGame.LoadRun))]
    private static class NativeContinue {
        static void Prefix(SerializableRoom? preFinishedRoom,out bool __state) {
            __state=!IsRestoring;
            if(!__state)return;
            _loadingMap=preFinishedRoom?.RoomType==RoomType.Map;
            _loadingCombat=preFinishedRoom is {IsPreFinished:false,EncounterId:not null} && NoSuffering.Combat.CombatService.Save() is not null;
        }
        static void Postfix(bool __state,ref Task __result) {
            if(__state)__result=Finish(__result);
        }
        static async Task Finish(Task load) {
            try {await load;} finally {_loadingMap=false;_loadingCombat=false;}
        }
    }
    [HarmonyPatch(typeof(NetHostGameService),nameof(NetHostGameService.Disconnect))]
    private static class PreserveHost {static bool Prefix()=>!_preserveTransport;}
    [HarmonyPatch(typeof(NetClientGameService),nameof(NetClientGameService.Disconnect))]
    private static class PreserveClient {static bool Prefix()=>!_preserveTransport;}
    [HarmonyPatch(typeof(AbstractRoom),nameof(AbstractRoom.FromSerializable))]
    private static class MapSnapshot {
        static bool Prefix(SerializableRoom? serializableRoom,ref AbstractRoom? __result) {
            if(serializableRoom?.RoomType!=RoomType.Map)return true;
            __result=new MapRoom();return false;
        }
    }
    [HarmonyPatch(typeof(RunManager),nameof(RunManager.LoadIntoLatestMapCoord))]
    private static class LoadDecision {
        static bool Prefix(AbstractRoom? preFinishedRoom,ref Task __result) {
            if(_loadingCombat && preFinishedRoom is CombatRoom {ParentEventId:not null} combat) {
                __result=NoSuffering.Combat.ParentEventState.RestoreCombatAsync(combat,NoSuffering.Combat.CombatService.Save()!);return false;
            }
            if(!_loadingMap)return true;
            __result=RunManager.Instance.EnterRoom(preFinishedRoom ?? new MapRoom());return false;
        }
    }
    [HarmonyPatch(typeof(RunState),nameof(RunState.AppendToMapPointHistory))]
    private static class PreserveHistory {static bool Prefix()=>!_loadingCombat;}
    [HarmonyPatch(typeof(MegaCrit.Sts2.Core.Hooks.Hook),nameof(MegaCrit.Sts2.Core.Hooks.Hook.BeforeRoomEntered))]
    private static class OpeningRoomEffects {
        static bool Prefix(AbstractRoom room,ref Task __result) {
            if(!_loadingCombat || room is not CombatRoom)return true;
            // B already includes this entry hook; repeating it mutates permanent
            // cards such as Dowsing. Combat opening hooks still run normally.
            __result=Task.CompletedTask;return false;
        }
    }
    [HarmonyPatch(typeof(RunState),nameof(RunState.GetAndIncrementNextRoomId))]
    private static class FreshRoomIdentity {
        static void Postfix(ref int __result) {
            // Native room IDs reset on restore. Reserve a new range after each
            // committed host operation so stale location-addressed messages expire.
            __result=checked(__result+checked((int)HostCoordinator.WorldRevision)*4096);
        }
    }
}
