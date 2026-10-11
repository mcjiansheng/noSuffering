using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;
using NoSuffering.Multiplayer;
using Bridge = NoSuffering.GameBridge.GameBridge;

namespace NoSuffering.ReviewTests;

[ModInitializer(nameof(Initialize))]
public static partial class ReviewProbe
{
    private static SceneTree Tree = null!;
    private static bool ExpectFixed;
    private static string Case = "", Role = "", Output = "";
    private static bool Enabled;
    private static readonly Dictionary<string, object> Results = new();
    private static string? Arg(string key) => OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith(key + "="))?[(key.Length + 1)..];
    public static void Initialize()
    {
        if (!OS.GetCmdlineUserArgs().Contains("--ns-review-probe")) return;
        Tree = (SceneTree)Engine.GetMainLoop();
        Case = Arg("--ns-review-case") ?? "";
        Role = Arg("--ns-review-role") ?? "solo";
        Output = Arg("--ns-review-output") ?? "";
        ExpectFixed = OS.GetCmdlineUserArgs().Contains("--ns-review-fixed");
        try
        {
            Require(CommandLineHelper.GetValue("force-steam") == "off", "Review tests require offline startup.");
            Require(Path.IsPathFullyQualified(Output) && Path.GetFullPath(Output) == Output, "Review output path is invalid.");
            using var session = JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, ".review-session.json")));
            var peer = session.RootElement.GetProperty("peers").GetProperty(Role);
            Require(Path.GetFullPath(OS.GetUserDataDir()) == peer.GetProperty("user_data").GetString(), "Review user directory mismatch.");
            Require(Path.GetFullPath(OS.GetExecutablePath()) == peer.GetProperty("executable").GetString(), "Review executable mismatch.");
            Require(OS.GetUserDataDir().Replace('\\', '/').Contains("/NoSufferingLab/review/"), "Review requires dedicated profile.");
            Enabled = true;
            new Harmony("NoSuffering.ReviewRegression").PatchAll(typeof(ReviewProbe).Assembly);
        }
        catch (Exception e) { GD.PrintErr("REVIEW_GATE_FAILED " + e); Tree.Quit(1); return; }
        void Start() { Tree.ProcessFrame -= Start; _ = Run(); }
        Tree.ProcessFrame += Start;
    }
    private static async Task Run()
    {
        bool passed = false;
        try
        {
#if STS2_STABLE
            await Wait(() => NGame.Instance?.MainMenu?.IsNodeReady() == true, "native startup", 60000);
            await Timed(PreloadManager.LoadCommonAndMainMenuAssets(), "native startup assets");
#else
            await Wait(() => NGame.Instance?.GameStartupComplete.IsCompleted == true, "native startup", 60000);
            await NGame.Instance!.GameStartupComplete;
#endif
            await Frames(3);
            GD.Print($"REVIEW_START case={Case} role={Role} fixed={ExpectFixed} process={System.Environment.ProcessId} user={OS.GetUserDataDir()}");
            switch (Case)
            {
                case "storage": await RunStorage(); break;
                case "config-ui": await RunConfigUi(); break;
                case "network": await RunNetwork(); break;
                default: throw new InvalidOperationException("Unknown review case: " + Case);
            }
            passed = true;
        }
        catch (Exception e) { Results["error"] = e.ToString(); GD.PrintErr("REVIEW_FAILED " + e); }
        var report = JsonSerializer.Serialize(new { version = typeof(NoSuffering.ModEntry).GetField(nameof(NoSuffering.ModEntry.Version))!.GetRawConstantValue(), process = System.Environment.ProcessId,
            role = Role, testCase = Case, expectFixed = ExpectFixed, passed, results = Results });
        File.WriteAllText(Path.Combine(Output, "report-" + Role + ".json"), report);
        GD.Print("REVIEW_RESULTS " + report);
        Tree.Quit(passed ? 0 : 1);
    }
    private static async Task<RunState> StartSolo(string seed)
    {
        var manager = RunManager.Instance;
        Require(!manager.IsInProgress, "Previous run must be cleaned up before starting a fixture.");
        var run = RunState.CreateForNewRun([Player.CreateForNewRun(ModelDb.Character<Ironclad>(), UnlockState.all, 1UL)],
            ActModel.GetDefaultList().Select(a => a.ToMutable()).ToList(), [], GameMode.Standard, 0, seed);
        manager.SetUpNewSingleplayer(run, true);
        await Timed(PreloadManager.LoadRunAssets(run.Players.Select(p => p.Character)), "native run assets");
        await Timed(manager.FinalizeStartingRelics(), "native starting relics");
        manager.Launch();
        NGame.Instance!.RootSceneContainer.SetCurrentScene(NRun.Create(run));
        NGame.Instance.ReactionContainer.InitializeNetworking(manager.NetService);
        foreach (var ftue in new[] { "map_select_ftue", "combat_rules_ftue", "combat_reward_ftue", "merchant_ftue" })
            SaveManager.Instance.MarkFtueAsComplete(ftue);
        await Timed(manager.EnterAct(0, false), "native first act");
        HostCoordinator.OnRunReady();
        return run;
    }
    private static void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
    private static void Expect(string name, bool fixedCondition, object facts)
    {
        Results[name] = new { status = fixedCondition ? "FIXED" : "REPRODUCED", facts };
        GD.Print("REVIEW_CHECK " + name + " " + JsonSerializer.Serialize(Results[name]));
        Require(fixedCondition == ExpectFixed, name + (ExpectFixed ? " remains broken" : " did not reproduce"));
    }
    private static async Task Frames(int count = 2) { for (int i = 0; i < count; i++) await Bridge.Frame(); }
    private static async Task Wait(Func<bool> condition, string detail, int timeoutMs = 30000)
    {
        ulong deadline = Time.GetTicksMsec() + (ulong)timeoutMs;
        while (!condition()) { Require(Time.GetTicksMsec() < deadline, "Timed out: " + detail); await Bridge.Frame(); }
    }
    private static async Task Timed(Task task, string detail) { await Wait(() => task.IsCompleted, detail, 60000); await task; }
    [HarmonyPatch(typeof(ENetConnection), nameof(ENetConnection.CreateHostBound))]
    private static class LoopbackOnly
    {
        static void Prefix([HarmonyArgument(0)] ref string address) { if (Enabled) { address = "127.0.0.1"; GD.Print("REVIEW_LOOPBACK_BOUND"); } }
    }
}
