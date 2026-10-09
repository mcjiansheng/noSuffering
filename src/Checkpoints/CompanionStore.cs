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
        if(_lastNative is not null && RunManager.Instance.IsInProgress && Bridge.Key(Bridge.Thaw(_lastNative))==Bridge.RunKey)Write(Bridge.Thaw(_lastNative));
    }
    public static void Reset() {_lastNative=null;Error=null;LoadedRevision=0;CheckpointService.Reset();}
    [HarmonyPatch(typeof(RunManager),nameof(RunManager.SetUpNewSingleplayer))]
    private static class NewSolo {static void Prefix()=>Reset();}
    [HarmonyPatch(typeof(RunManager),nameof(RunManager.SetUpNewMultiplayer))]
    private static class NewMulti {static void Prefix()=>Reset();}
    private static void Write(SerializableRun save) {
        var digest=NativeDigest(save);var run=Bridge.Key(save);
        var record=new CompanionCommit(1,ModEntry.Version,GameAssembly,Fingerprint,run,digest,HostCoordinator.WorldRevision,CheckpointService.Save());
        var dir=Path.Combine(Root,run);Directory.CreateDirectory(dir);
        var file=Path.Combine(dir,digest+".json");
        File.WriteAllText(file+".tmp",JsonSerializer.Serialize(record));
        File.Move(file+".tmp",file,true);
        // Content-addressed commits pair independently with native primary/backup
        // saves. A failed native write never points at unrelated Mod state.
    }
    private static async Task Saved(Task nativeWrite,SerializableRun save) {
        await nativeWrite;
        _lastNative=Bridge.Freeze(save);
        Write(save);
    }
    public static void Read(SerializableRun save) {
        if(Bridge.IsRestoring)return;
        var run=Bridge.Key(save);var digest=NativeDigest(save);
        var file=Path.Combine(Root,run,digest+".json");
        Error=null;LoadedRevision=0;CheckpointService.Reset();_lastNative=Bridge.Freeze(save);
        if(!File.Exists(file)) {
            if(Directory.Exists(Path.Combine(Root,run)))Error="存档与模组记录未配对，请保留原存档并重新进入";
            return;
        }
        var data=JsonSerializer.Deserialize<CompanionCommit>(File.ReadAllText(file)) ?? throw new InvalidOperationException("伴随存档为空");
        // 0.1.2 fixes only button routing; its snapshot format and gameplay state
        // are identical to 0.1.1. Accept that exact predecessor while requiring
        // the original game, every other gameplay mod, run and native commit.
        bool versionCompatible=data.ModVersion==ModEntry.Version || (ModEntry.Version=="0.1.2" && data.ModVersion=="0.1.1");
        bool modsCompatible=versionCompatible && data.Mods==FingerprintFor(data.ModVersion);
        if(data.Format!=1 || !versionCompatible || data.GameAssembly!=GameAssembly || !modsCompatible || data.RunId!=run || data.NativeDigest!=digest) {
            Log.Warn($"[NoSuffering] Companion mismatch: format={data.Format==1}, version={versionCompatible}, game={data.GameAssembly==GameAssembly}, mods={modsCompatible}, run={data.RunId==run}, native={data.NativeDigest==digest}");
            Error="存档版本或模组组合不符，请保留原存档";return;
        }
        CheckpointService.Restore(data.Timeline);LoadedRevision=data.Revision;
        if(data.ModVersion!=ModEntry.Version)Log.Info("[NoSuffering] Loaded compatible 0.1.1 companion; format, game, other mods and native commit matched.");
    }
    public static async Task SaveNativeCurrent() {
        var manager=RunManager.Instance;
        if(manager.NetService.Type==NetGameType.Client)return;
        SerializableRun save;
        if(MegaCrit.Sts2.Core.Combat.CombatManager.Instance.IsInProgress && CombatService.Baseline is not null)
            save=Bridge.Thaw(CombatService.Baseline);
        else save=Bridge.Capture(Bridge.State.CurrentRoom);
        var native=(RunSaveManager)AccessTools.Field(typeof(SaveManager),"_runSaveManager").GetValue(SaveManager.Instance)!;
        await native.SaveRun(save,manager.NetService.Type==NetGameType.Host);
    }
    [HarmonyPatch(typeof(RunSaveManager),nameof(RunSaveManager.SaveRun),[typeof(SerializableRun),typeof(bool)])]
    private static class PairSave {
        static void Postfix(SerializableRun save,ref Task __result) {
            var frozen=Bridge.Thaw(Bridge.Freeze(save));
            __result=Saved(__result,frozen);
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
