# NoSuffering
- Requirements and acceptance: docs/DESIGN.md. Keep implementation version and test records synchronized.
- Mac edits; xht-rog supplies actual game assemblies, final build, deployment and gameplay verification. Reuse SSH alias xht; never commit credentials or proprietary game assemblies/source.
- Seven operations per DESIGN v1.9. Ancient reward and shop rerolls are personal and sender-authenticated; rollback, ancient replacement, combat restart and act-three Boss HP increases require the actual host, without voting.
- Only the two ancient rerolls have independent Free/Hp costs: replacement pays host HP, reward reroll pays actor HP. All other operations are free.
- Normal restart preserves the active attempt; refreshed draw order persists. Native immutable snapshots and native multiplayer load paths are required.
- Prioritize latest public-beta; verify stable separately. Never claim untested game versions or mod combinations compatible.
- Continue A/B/C without routine approval requests. Record build/load/singleplayer/multiplayer separately; unexecuted tests remain unexecuted.
- DSH disabled. Bounded Codex subagents are allowed; avoid needless abstractions and excessive testing.
