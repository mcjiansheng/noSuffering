using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using NoSuffering.Checkpoints;
using Bridge = NoSuffering.GameBridge.GameBridge;

namespace NoSuffering.ReviewTests;

public static partial class ReviewProbe
{
    private static async Task RunStorage()
    {
        await StartSolo("REVIEWSTORAGE");
        // Capture an actual decision in the running native run, then persist it
        // through the ordinary native SaveManager / patched RunSaveManager path.
        CheckpointService.CaptureDecision(true);
        Require(CheckpointService.History.Count > 0, "storage fixture has no history");
        await Timed(SaveManager.Instance.SaveRun(new MapRoom(), false), "storage initial native save");
        var initialLoad = SaveManager.Instance.LoadRunSave();
        Require(initialLoad.Success && initialLoad.SaveData is not null, "initial native disk load failed");
        var digestInput = Bridge.Thaw(Bridge.Freeze(initialLoad.SaveData!));
        digestInput.NumReloads = 0;
        digestInput.SaveTime = 0;
        digestInput.RunTime = 0;
        var root = ProjectSettings.GlobalizePath("user://NoSuffering/runs");
        var runDir = Path.Combine(root, Bridge.RunKey);
        var file = Path.Combine(runDir, Bridge.Hash(Bridge.Freeze(digestInput)) + ".json");
        var originalBytes = File.ReadAllBytes(file);
        var original = JsonSerializer.Deserialize<CompanionCommit>(originalBytes)!;
        Require(original.Timeline.Checkpoints.Count > 0, "initial disk companion has no history");

        foreach (var mismatch in new[] { "game", "mods" })
        {
            var rejected = mismatch == "game"
                ? original with { GameAssembly = "review-incompatible-game" }
                : original with { Mods = "review-incompatible-mods" };
            var rejectedBytes = JsonSerializer.SerializeToUtf8Bytes(rejected);
            File.WriteAllBytes(file, rejectedBytes);
            var manager = RunManager.Instance;
            manager.CleanUp();
            var loaded = SaveManager.Instance.LoadRunSave();
            Require(loaded.Success && loaded.SaveData is not null, "native disk load failed");
            Require(CompanionStore.Error is not null, "incompatible companion was accepted");
            Require(CheckpointService.History.Count == 0, "rejected history was installed");
            var save = loaded.SaveData!;
            var reloadsBefore = save.NumReloads;
            await Timed(manager.SetUpSavedSingleplayer(RunState.FromSerializable(save), save), "native incompatible saved-run setup");
            Require(save.NumReloads == reloadsBefore + 1, "native saved setup did not increment reload count");
            var afterBytes = File.ReadAllBytes(file);
            var after = JsonSerializer.Deserialize<CompanionCommit>(afterBytes)!;
            var bytesPreserved = rejectedBytes.SequenceEqual(afterBytes);
            var errorPreserved = CompanionStore.Error is not null;

            // Restore only compatibility fields in whatever is actually left on
            // disk. This distinguishes retained history from an empty replacement.
            var compatibleAfter = after with { GameAssembly = original.GameAssembly, Mods = original.Mods };
            // Whitespace is a harmless write witness: the ordinary companion
            // writer emits canonical JSON without this trailing newline.
            File.WriteAllText(file, JsonSerializer.Serialize(compatibleAfter) + "\n");
            var recovery = SaveManager.Instance.LoadRunSave();
            Require(recovery.Success && recovery.SaveData is not null, "native recovery disk load failed");
            Require(recovery.SaveData!.NumReloads == save.NumReloads, "native reload count was not persisted to disk");
            Require(CompanionStore.Error is null, "compatible recovery companion rejected");
            var historyRecovered = CheckpointService.History.Count == original.Timeline.Checkpoints.Count;
            var recoveryBytes = File.ReadAllBytes(file);
            CompanionStore.PairWithLastNativeSave();
            Require(!recoveryBytes.SequenceEqual(File.ReadAllBytes(file)), "compatible read did not resume companion writes");
            Expect("storage_incompatible_" + mismatch + "_preserves_history", bytesPreserved && historyRecovered && errorPreserved,
                new { mismatch, bytesPreserved, historyRecovered, errorPreserved, reloadsBefore, reloadsAfter = save.NumReloads,
                    reloadsPersisted = recovery.SaveData.NumReloads,
                    originalHistory = original.Timeline.Checkpoints.Count, historyAfterSetup = after.Timeline.Checkpoints.Count,
                    recoveredHistory = CheckpointService.History.Count, sameDigest = after.NativeDigest == original.NativeDigest });
            // The next mismatch starts from the same original companion. Native
            // reload-count changes deliberately retain the same content digest.
            File.WriteAllBytes(file, originalBytes);
        }

        // A genuinely new run owns a fresh timeline and must release isolation
        // even if the preceding loaded run was still incompatible.
        File.WriteAllText(file, JsonSerializer.Serialize(original with { GameAssembly = "review-incompatible-game" }));
        Require(SaveManager.Instance.LoadRunSave().Success && CompanionStore.Error is not null, "new-run isolation fixture failed");
        RunManager.Instance.CleanUp();
        await StartSolo("REVIEWSTORAGENEW");
        Require(CompanionStore.Error is null, "new run retained companion error");
        CheckpointService.CaptureDecision(true);
        await Timed(SaveManager.Instance.SaveRun(new MapRoom(), false), "storage new-run native save");
        var newDir = Path.Combine(root, Bridge.RunKey);
        Require(newDir != runDir && Directory.GetFiles(newDir, "*.json").Any(p =>
            JsonSerializer.Deserialize<CompanionCommit>(File.ReadAllText(p))!.Timeline.Checkpoints.Count > 0),
            "new run did not resume companion writes");
    }
}
