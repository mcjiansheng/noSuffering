using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Logging;
using NoSuffering.Config;
using NoSuffering.Checkpoints;

namespace NoSuffering;

[ModInitializer(nameof(Initialize))]
public static class ModEntry
{
    public const string Version="0.1.0";
    public static void Initialize() {
        new Harmony("mcjiansheng.NoSuffering").PatchAll(typeof(ModEntry).Assembly);
        ConfigStore.Initialize();ConfigStore.Changed+=CheckpointService.Trim;ConfigStore.Changed+=Multiplayer.HostCoordinator.RulesChanged;
        var tree=(SceneTree)Engine.GetMainLoop();
        void Attach(){tree.ProcessFrame-=Attach;UI.Overlay.Attach();}
        tree.ProcessFrame+=Attach;
        Log.Info($"[NoSuffering] {Version} initialized; game={typeof(MegaCrit.Sts2.Core.Runs.RunManager).Assembly.ManifestModule.ModuleVersionId}");
    }
}
