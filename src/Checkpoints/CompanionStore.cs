using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using NoSuffering.Combat;
using NoSuffering.Multiplayer;
using Bridge=NoSuffering.GameBridge.GameBridge;

namespace NoSuffering.Checkpoints;

public sealed record CompanionCommit(int Format,string ModVersion,string GameAssembly,string Mods,string RunId,string NativeDigest,long Revision,TimelineData Timeline);
public static class CompanionStore
{
    private static string? _lastNative;
    private static TimelineData? _lastNativeTimeline;
    public static long LoadedRevision {get;private set;}
    public static string? Error {get;private set;}
    private static string Root=>ProjectSettings.GlobalizePath("user://NoSuffering/runs");
    private static string Fingerprint=>FingerprintFor(ModEntry.Version);
    private static string FingerprintFor(string ownVersion)=>Bridge.Hash(string.Join("\n",ModManager.Mods.Where(m=>(m.state==ModLoadState.Loaded && m.manifest?.affectsGameplay!=false) || m.manifest?.id=="NoSuffering")
        .Select(m=>m.manifest?.id=="NoSuffering" ? "NoSuffering@"+SemanticVersion.FromString(ownVersion) : m.manifest?.id+"@"+m.version).Order()));
    private static string GameAssembly=>typeof(RunManager).Assembly.ManifestModule.ModuleVersionId.ToString();
    private static string NativeDigest(SerializableRun save) {
        var copy=Bridge.Thaw(Bridge.Freeze(save));
        copy.NumReloads=0;copy.SaveTime=0;copy.RunTime=0;
        return Bridge.Hash(Bridge.Freeze(copy));
    }
    public static void PairWithLastNativeSave() {
        if(_lastNative is not null && RunManager.Instance.IsInProgress && Bridge.Key(Bridge.Thaw(_lastNative))==Bridge.RunKey) {
            var timeline=CheckpointService.Save();
            // History may advance after a victory save, but that old native save
            // still has unclaimed resources. Only retain its request-bound marker.
            timeline=timeline with {SettledCombat=timeline.SettledCombat==_lastNativeTimeline?.SettledCombat?timeline.SettledCombat:null};
            Write(Bridge.Thaw(_lastNative),timeline);
        }
    }
    public static void Reset() {_lastNative=null;_lastNativeTimeline=null;Error=null;LoadedRevision=0;CheckpointService.Reset();}
    [HarmonyPatch(typeof(RunManager),nameof(RunManager.SetUpNewSingleplayer))]
    private static class NewSolo {static void Prefix()=>Reset();}
    [HarmonyPatch(typeof(RunManager),nameof(RunManager.SetUpNewMultiplayer))]
    private static class NewMulti {static void Prefix()=>Reset();}
    private static void Write(SerializableRun save,TimelineData timeline) {
        var digest=NativeDigest(save);var run=Bridge.Key(save);
        // Native reload-count writes precede InitializeShared/Launch. Preserve
        // the loaded revision until the live coordinator has been initialized.
        var revision=RunManager.Instance.ShouldSave?HostCoordinator.WorldRevision:LoadedRevision;
        var record=new CompanionCommit(2,ModEntry.Version,GameAssembly,Fingerprint,run,digest,revision,timeline);
        var dir=Path.Combine(Root,run);Directory.CreateDirectory(dir);
        var file=Path.Combine(dir,digest+".json");
        File.WriteAllText(file+".tmp",JsonSerializer.Serialize(record));
        File.Move(file+".tmp",file,true);
        // Content-addressed commits pair independently with native primary/backup
        // saves. A failed native write never points at unrelated Mod state.
    }
    private static async Task Saved(Task nativeWrite,SerializableRun save,TimelineData timeline) {
        await nativeWrite;
        _lastNative=Bridge.Freeze(save);
        _lastNativeTimeline=timeline;
        Write(save,timeline);
    }
    public static void Read(SerializableRun save) {
        if(Bridge.IsRestoring)return;
        var run=Bridge.Key(save);var digest=NativeDigest(save);
        var file=Path.Combine(Root,run,digest+".json");
        Error=null;LoadedRevision=0;CheckpointService.Reset();_lastNative=Bridge.Freeze(save);_lastNativeTimeline=null;
        if(!File.Exists(file)) {
            if(Directory.Exists(Path.Combine(Root,run)))Error="存档与模组记录未配对，请保留原存档并重新进入";
            return;
        }
        var data=JsonSerializer.Deserialize<CompanionCommit>(File.ReadAllText(file)) ?? throw new InvalidOperationException("伴随存档为空");
        // Format 2 adds node entry/settled-combat semantics. Read known format 1
        // predecessors without inventing entry snapshots absent from their data.
        bool legacy=data.Format==1 && data.ModVersion is "0.1.1" or "0.1.2";
        bool versionCompatible=data.Format==2 && data.ModVersion==ModEntry.Version || legacy;
        bool modsCompatible=versionCompatible && data.Mods==FingerprintFor(data.ModVersion);
        if(!versionCompatible || data.GameAssembly!=GameAssembly || !modsCompatible || data.RunId!=run || data.NativeDigest!=digest) {
            Log.Warn($"[NoSuffering] Companion mismatch: format={data.Format}, version={versionCompatible}, game={data.GameAssembly==GameAssembly}, mods={modsCompatible}, run={data.RunId==run}, native={data.NativeDigest==digest}");
            Error="存档版本或模组组合不符，请保留原存档";return;
        }
        CheckpointService.Restore(data.Timeline);_lastNativeTimeline=data.Timeline;LoadedRevision=data.Revision;
        if(legacy){CheckpointService.ImportLegacyHistory();Log.Info($"[NoSuffering] Loaded compatible {data.ModVersion} companion; preserved known snapshots without inventing node-entry state.");}
    }
    public static async Task SaveNativeCurrent() {
        var manager=RunManager.Instance;
        if(manager.NetService.Type==NetGameType.Client)return;
        SerializableRun save;
        if(MegaCrit.Sts2.Core.Combat.CombatManager.Instance.IsInProgress && CombatService.Baseline is not null)
            save=Bridge.Thaw(CombatService.Baseline);
        else save=Bridge.Capture(Bridge.State.CurrentRoom);
        var native=(RunSaveManager)AccessTools.Field(typeof(SaveManager),"_runSaveManager").GetValue(SaveManager.Instance)!;
        // Participate in the native write slot even when saving a frozen combat
        // baseline rather than the active, unserializable combat room.
        var saves=SaveManager.Instance;
        if(saves.CurrentRunSaveTask is { } pending)await pending;
        var task=native.SaveRun(save,manager.NetService.Type==NetGameType.Host);
        var slot=AccessTools.Property(typeof(SaveManager),nameof(SaveManager.CurrentRunSaveTask));
        slot.SetValue(saves,task);
        try{await task;}finally{if(ReferenceEquals(saves.CurrentRunSaveTask,task))slot.SetValue(saves,null);}
    }
    [HarmonyPatch(typeof(RunSaveManager),nameof(RunSaveManager.SaveRun),[typeof(SerializableRun),typeof(bool)])]
    private static class PairSave {
        static void Postfix(SerializableRun save,ref Task __result) {
            var frozen=Bridge.Thaw(Bridge.Freeze(save));
            var timeline=CheckpointService.Save();
            if(save.PreFinishedRoom is not {IsPreFinished:true,EncounterId:not null} ||
               timeline.SettledCombat!=new CombatSettlement(save.VisitedMapCoords.LastOrDefault(),save.CurrentActIndex))
                timeline=timeline with {SettledCombat=null};
            __result=Saved(__result,frozen,timeline);
        }
    }
    [HarmonyPatch(typeof(RunSaveManager),nameof(RunSaveManager.LoadRunSave))]
    private static class ReadSolo {static void Postfix(ReadSaveResult<SerializableRun> __result){if(__result.Success && __result.SaveData is not null)Read(__result.SaveData);}}
    [HarmonyPatch(typeof(RunSaveManager),nameof(RunSaveManager.LoadMultiplayerRunSave))]
    private static class ReadMulti {static void Postfix(ReadSaveResult<SerializableRun> __result){if(__result.Success && __result.SaveData is not null)Read(__result.SaveData);}}
    [HarmonyPatch(typeof(CombatRoom),"StartCombat")]
    private static class BaselinePair {
        [HarmonyPriority(Priority.Last)]
        static void Prefix()=>PairWithLastNativeSave();
    }
}
