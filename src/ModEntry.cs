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
    public const string Version="0.1.4";
    public static void Initialize() {
        if (OS.GetCmdlineUserArgs().Contains("--ns-lab-probe"))
            Log.Info($"[NoSuffering] LAB user_data={OS.GetUserDataDir()}");
        new Harmony("mcjiansheng.NoSuffering").PatchAll(typeof(ModEntry).Assembly);
        ConfigStore.Initialize();ConfigStore.Changed+=Multiplayer.HostCoordinator.RulesChanged;
        var tree=(SceneTree)Engine.GetMainLoop();
        Diagnostics.GameplayProbe.Initialize(tree);
        Diagnostics.MultiplayerProbe.Initialize(tree);
        void Attach(){tree.ProcessFrame-=Attach;UI.Overlay.Attach();}
        tree.ProcessFrame+=Attach;
        Log.Info($"[NoSuffering] {Version} initialized; game={typeof(MegaCrit.Sts2.Core.Runs.RunManager).Assembly.ManifestModule.ModuleVersionId}");
    }
}
