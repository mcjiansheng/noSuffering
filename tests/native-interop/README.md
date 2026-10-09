# Native interoperability fixture

Test-only addon; not part of NoSuffering.csproj or release packages. Build against the final deployed Windows public-beta game/mod assemblies:

```
dotnet build tests/native-interop/NativeInterop.csproj -c Release -p:Game=C:/Users/48811/source/NoSuffering/.tools/game-lab/windows-public-beta/game
```

Copy its DLL and manifest into that isolated lab's `mods/NativeInterop` only. Execute through a waiting process wrapper with the lab game working directory:

```
SlayTheSpire2.exe --headless --max-fps 30 --log-file C:/Users/48811/source/NoSuffering/artifacts/native-interop/engine.log --force-steam=off -- --ns-lab-probe --ns-native-interop
```

The fixture requires the exact Windows NoSufferingLab/windows-public-beta custom user directory and Steamoff flags. It creates a seeded singleplayer run, enters native rooms directly, and invokes actual native action and third-party handlers. It does not demonstrate pointer input, full playthrough, multiplayer, Steam networking, or other versions. Its JSON report is `native-interop.json` in the isolated profile. Restore the lab profile backup and remove this addon after the test.

AncientSL 1.0 has no reroll action: its opening-ancient Harmony patch suppresses the post-claim native save so an earlier save can be reloaded. The fixture tests that native load, then NoS F03, claim, and Proceed. UndoAndRestart handlers are called through reflection solely because that mod's classes are internal. Card play uses native queued PlayCardAction; undo/restart callbacks perform their actual restoration. The potion check uses native PotionModel.EnqueueManualUse targeting the actor.

Two new opening snapshots may legitimately exist after a native combat rebuild. Undo rejection is therefore not a valid assertion of stale-history cleanup; the fixture checks that the timeline generation changes and navigation cannot resurrect the previous attempt/order.

## Deterministic Busy regression

The fixture additionally holds `HostCoordinator.Busy` through reflection and calls the real native potion entry points. This is an artificial hold of the production flag, not a physical-input or operation-timing test. With `--ns-busy-baseline`, it expects the original defect: `EnqueueManualUse` invokes `BeforeUse` and sets `IsQueued` even though the later queue gate rejects the action; the actual popup discard callback disables its holder before the same rejection. Without that switch it expects no such local changes, then releases the flag and retries the exact same potion/callback successfully. The hold is always cleared in `finally`.
