# macOS public-beta native UI playthrough record

> **Final status: PASS — console-assisted Standard run completed on 2026-10-10 (Asia/Shanghai).** Computer Use operated the native Mac ARM64 beta UI from a new Silent run to the native “胜利……？” ending and score screen: floor 49, 3 Bosses, 4 elites, 780 gold obtained. Seed: `0RNLP90GRNV7`, Ascension 0. Actual F07, F04/F05 and whole-process continuation passed at the Act 3 Boss. The final candidate was used from the Act 1 Boss onward; this was one continued run across the three candidates below, not an unchanged-DLL full run.

| Segment | Actual DLL SHA-256 prefix | Scope |
| --- | --- | --- |
| New run and initial Neow/settings/restarts | `7850b053…` | Early candidate UI checks |
| Act 1 traversal, visited-node rollback and shop/purchase/continuation | `578af2e7…` | Historical candidate; recorded observations retained below |
| Act 1 Boss continuation through Act 2/3 and native conclusion | `02f63de7…` | Final 0.1.4 Mac beta candidate; F02/F03/F04/F05/F06/F07 actual UI and persistence |

Only NoSuffering was loaded in this full UI run. The lab used isolated data and Steam-off mode, with no gameplay fixture argument. The user expressly authorized strong cards through the native console; 12 permanent cards and temporary Hand cards were used as documented below. No kill/god/room-jump command or save edit was used. This does not establish unassisted balance, Steam multiplayer, all-platform full playthroughs or all-mod compatibility. Historical checkpoint phrases such as “pending” describe their time of observation; this final status supersedes them.

## Pre-deployment unlock cleanup

- Environment: macOS public-beta lab launch, isolated `NoSufferingLab/macos-public-beta` data; the game UI showed one loaded module. This was the pre-final-deployment DLL, so this is not feature-validation evidence.
- Completed the pending Chapter 2 timeline entry through its visible **Continue** and relic-unlock confirmation screens.
- Completed the remaining Chapter 6 Silent timeline entry through its visible **Continue** and character-unlock confirmation screens.
- Returned to the main menu with Escape. The timeline showed the new Chapter 6 art and unlocked Silent character.
- Selected the visible main-menu Quit item and confirmed the visible quit dialog. The game window disappeared.

## Candidate `7850b053` early-run validation

- Started a native Standard run as Silent, skipped the native tutorial prompt, selected a visible Neow relic reward, and continued to the Act 1 map. The selected relic icon appeared in the UI before proceeding.
- Opened the NoSuffering panel with `F6`; its nested settings page opened and closed successfully. The run-specific panel visibly offered restart combat, restart-and-refresh-draw-order, and route rollback. Settings were restored with all seven operations enabled after a temporary toggle inspection.
- At Neow, the replacement operation was visibly disabled for the current state and the reward-reroll operation was enabled. `F1`–`F7` are DESIGN feature identifiers, not implemented keyboard shortcuts; the earlier `F3` keypress is therefore not a reroll validation.
- Entered the first normal enemy combat through the native map. The combat-restart panel action returned it to the visible Battle Start state at 70/70 versus the same 55/55 enemy. The restart-and-refresh-draw-order action also returned to Battle Start; the subsequent visible opening hand changed order/content from the previous restart's opening hand.
- Opened the native pause menu and used **Save and Quit**. The main menu visibly showed **Continue Game**, preserving the run. In native Video Settings, disabled the visibly checked Fullscreen mode for the lab profile. Exited through the main-menu Quit confirmation so the root agent can deploy the shop fix.
- A normal card-selection interaction was attempted before the restart test, but I did not obtain a visible damage/state change. Therefore no successful played-card assertion is recorded.

## Historical candidate status (resolved by the final candidate)

At this pre-final-deployment point, the full run, shop purchase-lock/save-continue, route rollback to a visited node, Act 2 ancient reward/replacement, Act 3 boss-HP action, and run completion had not yet been tested. The candidate was replaced before those checks. Later final-candidate observations below supersede the shop, persistence, and rollback portions; the full run and Act 2/3 checks remain in progress.

## Historical candidate `578af2e7` continuation

- Launched the final candidate and selected the main-menu Continue Game item. The resumed in-combat opening hand exactly matched the saved, refreshed hand: Defend, Survivor, Strike, Neutralize, Strike, Defend, Defend. This is visual evidence that the Save and Quit / whole-process relaunch preserved the refreshed draw order.
- Direct CUA drag/click card gestures did not resolve a card (they only enlarged/hovered it); this was recorded before the native keyboard-index interaction was identified. On the same final build, selecting a card with its visible hand index, taking a fresh frame, then clicking its target resolved normal card play. After a native restart from 1/3 energy, 5 block, and an enemy at 49/55, the UI returned to the Battle Start state (3/3 energy, no block, enemy 55/55). I then played Survivor, selected a discard through the visible UI, and played Strike against the enemy: the resulting frame showed 1/3 energy, 8 block, and enemy 49/55. This is successful ordinary combat input plus a visual restart reset.
- Continued the normal first-floor combat without any fixture/debug action. I spent the remaining early turns using target-selected Strikes and blocks; the character remains at 70/70 and the enemy is at 13/55 at the latest observation. The combat, rollback, shop, Act 2/3, and completion checks are still in progress.
- Finished that first ordinary combat normally through the native reward flow. Silent finished at 56/70, collected 17 gold (116 total), and selected the displayed `连绫反弹` card. On the native Act 1 map, F6 → `路线回滚` opened the explicit “choose node to return to” map mode. Selecting the completed enemy node returned cleanly to the normal map and displayed the game-saved indicator; no freeze or error occurred.
- Entered the next native `?` event, chose the lone transformling option, and observed 56/70 become 61/75 while gold remained 116. From its completed-event screen, F6 → route rollback → that visited `?` node returned to the original event choices and restored the pre-event 56/70 value. Re-selecting the option again produced 61/75 and the ordinary map remained interactive.
- Historical interaction miss, resolved immediately below: at the accessible merchant (61/75, 116 gold), F6 exposed `刷新商店` and `路线回滚`. I initially clicked decorative face-down table papers rather than the merchant body, so no purchasable stock opened. This did not establish a shop failure and does not block the later genuine purchase, lock, or persistence results.
- Root identified the native interaction I had missed: click the merchant’s body, not the table papers. Root then observed the actual stock, refreshed it through F6, and confirmed cards/relics/potions/prices visibly changed while HP stayed 61/75 and gold stayed 116. Root purchased the native Predator card from the refreshed stock: gold 116→68, deck count 13→14, and the card disappeared. The F6 `刷新商店` control is now visibly grey/disabled. I resume ownership at this exact post-purchase state to independently verify disabled re-attempt and persistence.
- I clicked the disabled `刷新商店` control myself; the visible stock and the 61/75, 68-gold, 14-card state did not change. Native **Save and Quit** returned to the menu with **Continue Game**. Selecting Continue in the same process restored the merchant scene with the same 61/75, 68 gold, and deck count 14. Reopening the merchant reproduced the post-refresh stock (Hunt 145, discounted Dash 26, Sidestep 73, Infinite Blades 77, plus the same relic/potion price layout) with Predator absent. F6 again showed `刷新商店` grey/disabled. This establishes same-process purchase and lock persistence; whole-process continuation is being checked next.
- After native **Save and Quit**, main-menu Quit, and the parent’s relaunch of the exact beta app/profile, **Continue Game** returned to the merchant at 61/75, 68 gold, and deck count 14. Opening the merchant body reproduced the same post-refresh stock with Predator absent; F6 again rendered `刷新商店` grey/disabled. This completes the actual whole-process purchase/stock/lock Save-and-Continue check.

## Continued normal run

- Continued through ordinary Act 1 rooms using only the native map, rewards, events, and combat UI. The direct card input required a held key-index press and held click on the enemy body; with 150 ms holds, the card armed and resolved normally. This was a CUA input-timing observation, not a mod action.
- Completed a three-slime combat at floor 6, collected 19 gold and an Energy Potion, and added `闪躲翻滚` from the normal reward. Chose the next native `?` event and took the non-damaging 51-gold team option (gold 87→136, HP unchanged).
- Entered and defeated a floor-8 native small-bite-beast combat with normal Silent cards; HP is 47/75 and gold 138 at its reward screen. The ordinary full run remains in progress; Act 2 ancient operations, Act 3 Boss HP, and completion are not yet asserted.
- Took the floor-9 native rest option, restoring Silent from 47/75 to 74/80 before continuing. The run is at the campfire’s normal Advance screen.
- From the normal map after that rest, opened F6 and selected `路线回滚`, then selected the just-visited campfire. The native campfire returned at its pre-entry state (47/75) with both Rest and Smith choices. Selecting Rest again restored 74/80 and the native Advance control returned to the normal map. This verified campfire pre-entry rollback semantics.
- The next available map branch entered a forced elite, `异蛙寄生虫`, which split into four `扭动虫` enemies. It was cleared entirely through ordinary held-index/target card input; Silent fell from 74/80 to 27/80. Native rewards showed gold 154→198, relic `战纹涂料`, and automatic upgrades to `防御+` and `生存者+`; the card reward was skipped. I then opened the next chest natively, collected its relic, and entered the following normal two-louse combat.
- That two-louse combat was also completed through normal card and target UI at 2/80. A Forge-style potion menu was opened and `饮用` was clicked, but its icon remained and no potion-use log was available; the visible green `+` upgrades could instead have come from the Feather relic. Therefore no Forge-potion consumption or effect is asserted. The later Energy Potion menu also showed its stated effect but a click on `饮用` did not visibly consume it or change the 3/3 display; no Energy Potion use is asserted. The two-louse reward supplied 10 gold and `后空翻`; the subsequent event offered only a lethal 6-HP-for-gold option or a curse-and-relic option, so the curse-and-relic option was chosen.
- To avoid continuing from 2/80 into another combat/elite branch, I used F6 route rollback from the completed event and selected the previously visited chest. The visible state restored to that chest's pre-entry snapshot at 27/80, 198 gold, and floor 11. I then entered route rollback again to seek the preceding campfire. Its exact clickable center was later confirmed by the root agent as `[509,1076]` with a 150 ms native click, restoring the original native campfire at 47/75, 154 gold, floor 9. The apparent blue outline was not a rollback-eligibility indicator. This was deliberate test-authorized recovery, not a product error; the root agent is replaying the later Act 1 route from that safe checkpoint.
- Root replay progress from that restored native floor-9 campfire: Rest restored 74/80; the forced floor-10 Phrog elite was finished at 16/80 and 198 gold, yielding War Paint, an Attack Potion, and Dagger Throw. The floor-11 chest supplied Feather and 50 gold (248 total). The floor-12 event cost 6 HP for 50 gold (10/80, 298 gold). Resting at floor 13 restored 39/85. The floor-14 Nibbits combat was won at 12/85, gold 308; Backflip was added (deck 17). The floor-15 Greenbird elite was then won at 11/85 on turn 6. These were native card/map/reward actions; no fixture or debug action was used.
- Root subsequently cleared the normal floor-16 three-Shadow combat without HP loss (still 11/85) on turn 5, gained 13 gold (358 total), and selected Dagger Spray (deck 19). At the floor-17 campfire, Rest brought Silent to 41/90. I entered the Act 1 Boss through the native map and used ordinary held-index card selection plus target clicks. During this actual combat, F6 → `重新开始战斗` restored the visible Battle Start state from an in-progress turn, confirming the native combat-restart operation again on the final candidate. The restarted attempt is currently paused at Silent 27/90 versus the 173-HP Boss at 132/173 after turn 9; the parent has temporarily resumed exclusive GUI control to progress it. The Boss outcome, Act 2 ancient operations, Act 3 Boss-HP change, and conclusion remain pending.
- During this pause the parent reported a newly diagnosed Busy-queue potion/discard issue and is preparing a later candidate. No change was deployed to the running game at the time of this record, and the earlier unverified potion clicks remain neither a confirmation nor a reproduction of that newly diagnosed issue.

No source files changed by this validation session.

## Candidate `02f63de7` resumed Act 1 Boss attempt

- Parent deployed and relaunched the later Mac public-beta candidate (`02f63de79fe3d74563c70993c5c81fb0a2f1f6f52735b599d25fff9b849ba7dd`). Native Continue restored the real Act 1 Boss battle at its visible Battle Start state, 41/90 versus 173/173, confirming the earlier native Save and Quit continuation remained available on the later candidate.
- I used the visible held-index card selection and enemy-body targeting in the restarted battle. During its first turns, the Boss health changed from 173 to 162 and the Slippery multi-hit shield icon disappeared. The live state later reached 22/90 versus 162/173 on turn 4 with a non-attacking buff intent.
- At that turn, an anomalous input sequence occurred. Holding index 1 visibly raised Predator and displayed the native red aiming/corner feedback, but multiple held target clicks on the visibly rendered Boss deselected the card without consuming energy or changing Boss HP. A direct click then removed Predator from the rendered hand while energy remained 3/3 and Boss HP remained 162/173. The native End Turn control visibly hovered/highlighted, but both a held click and its displayed `E` shortcut left the same turn on screen. This is a current candidate observation, reported immediately to the parent; it is not attributed to a specific mod feature or to the earlier unverified potion behavior. Further blind repetitions were paused.

No source files changed by this validation session.

## Final candidate `02f63de7` keyboard-mode continuation

- Native macOS Controls were switched to the game’s visible keyboard-navigation mode. This resolved the prior mouse-targeting limitation without any mod action: arrow navigation entered keyboard mode, a hand index armed the card, and `Space` confirmed its target. On the Act 1 Boss, keyboard-selected Predator changed the visible state from 162/173 and 3 energy to 147/173 and 1 energy; keyboard-selected Strike then changed it to 141/173 and 0 energy. Keyboard `E` ended turns. This result supersedes the earlier mouse-coordinate input limitation; its root cause remains unverified.
- The parent and I then used only visible keyboard navigation through ordinary card, discard, and potion UI. The actual Energy Potion opened with keyboard `W`, the highlighted Drink action was confirmed with keyboard navigation, its belt icon became an empty slot, and energy visibly rose from 3/3 to 5/3. The Attack Potion similarly became an empty slot and opened its native three-card attack choice. The Forge Potion belt icon also became an empty slot after its native Drink path. These establish successful native potion consumption through the keyboard path; they do not infer a separate mod effect beyond the visible state changes.
- One Boss attempt reached 17/90 versus 111/173 but could not cover a visible 28-damage-plus-Wounds turn with the available block. I used F6 → the visible `重新开始战斗` control to restart the active Boss battle, returning to the visible Battle Start state at 41/90 versus 173/173 with the potion belt restored. This was an authorized normal NoS combat restart, not a fixture, save edit, or room jump.
- In the restarted battle, a fresh ordinary sequence used Backflip, Dagger Spray, Neutralize, Dagger Throw, Ricochet, blocks, and the three native potion interactions through keyboard-only UI. The initial Slippery stack visibly began at 8; Dagger Spray reduced it from 8 to 6, Neutralize reduced it to 5, and later Ricochet removed the remaining protection while dealing visible post-shield damage. The Boss reached 128/173; Silent is at 24/90 at the parent handoff. The run is still in progress: Act 2 ancient operations, Act 3 Boss HP increase, and native conclusion remain unexecuted and unclaimed.

No source files changed by this validation session.

## Final candidate `02f63de7` Act 2 continuation with authorized card assistance

- Root documented the user-authorized native-console assistance: two permanent batches, **12 cards total** (`HYPERBEAM` ×4, `ADRENALINE` ×4, `WRAITH_FORM` ×2, `BULLET_TIME` ×2), plus the earlier temporary Act 1 Boss `BULLET_TIME` + `HYPERBEAM` Hand assistance. This is test assistance, not a NoS operation, fixture, room jump, kill command, god mode, or save edit.
- Root completed the Act 2 ancient UI flow through visible controls: free F03 reward-options refresh; F02 replacement cost 5 HP (90→85); independent F03 cost 5 HP (85→80); selected Pael's Blood for one additional draw; native Continue remained interactive.
- Act 2 floor 20 beetle: won in two turns at 80 HP / 477 gold. Floor 21 shop: refreshed stock, bought Red Heart for 179 gold (to 298); refresh visibly disabled afterward. Floor 22 group combat was won at 80 HP / 298 gold.
- I resumed at floor 22 reward: gold selection via keyboard navigation visibly changed gold 298→313; skipped the other reward entries. Floor 23 three-enemy encounter was won at 67/90. Floor 24 sleeping-creature encounter was won after two Hyperbeams and normal card play; reward gold changed 313→323. The route then reached the floor 25 campfire; native Rest changed 44/90→76/95.
- At handoff checkpoint, floor 26 normal single-enemy encounter is active: Silent 76/95, 323 gold; `BULLET_TIME` followed by visible `HYPERBEAM` reduced the enemy from 121 to 97. No additional console cards were added during this continuation.
- **Status:** Act 3 Boss F07 UI verification and final native conclusion remain pending at this checkpoint.

### Continued native Act 2 traversal with authorized temporary Hand assistance

- Floor 26: completed a normal red-enemy combat at 23/95 after adding and actually playing one temporary `FIEND_FIRE`, one temporary `ADRENALINE`, and one temporary `HYPERBEAM` through the native console. These are user-authorized test-assistance cards, not NoSuffering operations, room jumps, kills, god mode, fixtures, or save edits.
- Floor 27: opened a native chest, gained gold and a visible meat-icon relic. Floor 28: native Rest changed HP from 23/95 to 56/100.
- Floor 29 is active at this record checkpoint: 56/100, 372 gold; two enemies were reduced to 14/62 and 27/60 by actual keyboard-mode Bullet Time, Hyperbeam, Predator, Ricochet, and Strike play.
- This continuation therefore has three additional temporary Hand cards beyond the earlier Act 1 temporary pair. The cumulative permanent console assistance remains 12 cards (`HYPERBEAM` x4, `ADRENALINE` x4, `WRAITH_FORM` x2, `BULLET_TIME` x2). Act 3 Boss F07 validation and native conclusion remain pending.

### Continued Act 2 floors 29–31 and diagnostic handoff

- Floor 29: finished the two-enemy normal combat through the native keyboard flow. The visible result screen showed Silent at 52/100; map progression proceeded normally.
- Floor 30: selected the native Rest option, visibly restoring HP from 52/100 to 87/105.
- Floor 31: the next connected node was a forced elite (145 HP). I added one temporary `WRAITH_FORM`, one temporary `BULLET_TIME`, and one temporary `HYPERBEAM` to Hand through the user-authorized native console and actually played them with visible keyboard actions. Its HP visibly fell 145→121→109→85→77→74→50; Silent was 83/105 at the final frame. These three cards are additional temporary test assistance only; no room jump, kill command, god mode, fixture, or save edit was used.
- At the 50/145 elite state, the visible End Turn control remained blue/enabled but did not advance after a held native click at its visible center, keyboard `e`/`E` (150 ms and 1000 ms), `Left`+`e`, `Escape`+`e`, `Space`, or `Return`. The screen had no visible target-selection corners. I stopped further blind input and handed the intact game state to the parent for focused read-only diagnosis/recovery. This is a candidate input/UI observation only; no NoS feature failure is asserted from it.

No source files changed by this validation session.

### Act 2 completion and Act 3 handoff (authorized temporary Hand assistance)

- Correction to the floor-31 input note above: the parent rebound the exact public-beta app and invoked its exposed AX0 `Raise` action. The previously stale CUA screenshot/binding then caught up; Escape/F6 actions had in fact already opened the native pause/NoS panel. This was not established as a NoSuffering logic deadlock. Without source changes or a restart, the preserved elite was completed through native keyboard actions.
- The Act 2 Boss was then completed through the ordinary native combat UI. Its first phase started at 379 HP. The final observed native victory frame followed turn 7 at Silent 66/110. The route collected the visible 100-gold reward (409→509), skipped the optional card, and entered Act 3 through the native map and the visible ancient-reward flow.
- During the Act 2 Boss completion I added temporary `BULLET_TIME` and `HYPERBEAM` cards through the user-authorized native console as needed, then actually selected and played them with the game's keyboard navigation. In particular, the visible boss health progressed 316/379 → 268/379 → 253/379 → 157/379 → 127/379 → 61/379 → 22/379 → 16/379 → defeat. These are test-assistance cards only; no kill command, god mode, room jump, fixture, or save edit was used.
- In Act 3, the ancient reward was claimed through the native keyboard UI; the map then advanced through two normal enemy rooms. The first three-enemy room (floor 36) was cleared on the first turn using native Bullet Time/Hyperbeam plus a native multi-target card. The second ordinary room (floor 37, Sculptor) was handed off intact with Silent 110/110, 16 block, 0/4 energy, and the 162-HP enemy at 42 HP on turn 2.
- Temporary-console additions actually made in this Act 3 segment: floor 36, one `BULLET_TIME` and one `HYPERBEAM`; floor 37, four `HYPERBEAM` cards were successfully added and played after native Bullet Time. Earlier commands attempted while the hand was full are not counted as successful additions. The permanent 12-card assistance described above remains unchanged.
- **Current status at handoff:** Act 3 is in progress. The actual F07 Boss-HP action, F04/F05 persistence checks, native Save-and-Quit/full-process relaunch assertion, and native conclusion remain pending and are not claimed here.

No source files changed by this validation session.


## Final candidate: Act 3 floors 37–48

Root resumed exclusive Computer Use control at the recorded floor-37 handoff. All map, room, reward and combat transitions below used the visible native UI.

| Floor | Native room/action | Observed result |
| --- | --- | --- |
| 37 | Sculptor combat finished | 110/110 HP; gold 509→527; card reward skipped |
| 38 | Merchant, advanced without purchase | HP and 527 gold unchanged; no additional shop assertion |
| 39 | Judgment event, Accept / Guilty / Continue | Heal at full HP; 110/110, 527 gold |
| 40 | Globe Head normal combat | 101/110, gold 527→540; optional rewards skipped |
| 41 | Potion Courier event | Claimed Stable Serum in belt slot 4 |
| 42 | Chest | Gold 540→586; Candles was displayed but skipped, not claimed |
| 43 | Four Scrolls combat | 101/110, gold 586→600; Vulnerable Potion in slot 5 |
| 44 | Symbiote event | Corrupted one permanent Hyperbeam through native card selection; displayed 36 damage / lose 2 HP when played |
| 45 | Soul Nexus elite | Three turns; 74/110, gold 600→641; claimed Nunchaku |
| 46 | Frog Knight normal combat | Three turns; 74/110, gold 641→652 |
| 47 | Relic Trader event, bottom option | Swinging Ball replaced by Frozen Crystal; HP/gold unchanged |
| 48 | Campfire, Rest / Advance | 74/110→112/115; continued to connected Boss node |

Temporary native-console assistance successfully added during this root segment: floor 37 Hyperbeam ×2; floor 40 Hyperbeam ×5 and Bullet Time ×1; floor 43 Hyperbeam ×1; floor 45 Hyperbeam ×8 and Bullet Time ×1; floor 46 Hyperbeam ×5 and Bullet Time ×2. These 25 temporary cards supplement the preceding recorded assistance; they do not alter the permanent 12-card assistance count. Pending native discard decisions were completed with the displayed confirm controls before continuing. No source or gameplay-state file was edited.

## Actual Act 3 Boss UI and persistence checks — final candidate

Entered native `QUEEN_BOSS` at floor 49 with Silent 112/115, 652 gold. Two living enemies began at 199/199 and 400/400.

1. Played the native corrupted Hyperbeam: Silent 112→110 and both enemies took 36 damage, becoming **163/199** and **364/400**.
2. F6 → visible **增加 Boss 生命 25%** executed once. The resulting values were **213/249** and **464/500**. Maxima rose by ceil(199×25%)=50 and 100; both retained exactly 36 damage. No dead enemy or later summon was involved in this UI check.
3. F6 → **重新开始战斗** restored Silent **112/115**, the original opening hand and enemies **249/249**, **500/500**. The increase was retained, not applied twice.
4. F6 → **重开并刷新牌序** restored the same HP/maxima with a new eight-card opening hand: **Bullet Time, Hyperbeam, Strike, Strike, Wraith Form, Defend+, Defend, Dagger Spray**.
5. Used native **Save and Quit**, then main-menu **Quit / Yes**. The first final-candidate process exited 0. Relaunched the same isolated beta app through `scripts/lab.py`, selected native **Continue Game**, and visually confirmed **112/115**, **249/249**, **500/500**, and the exact same eight-card refreshed opening order. No 312/625 repeat increase occurred.
6. Completed the preserved attempt through native card and turn controls. Temporary Hand assistance in this Boss attempt was Hyperbeam ×17, Bullet Time ×2, Wraith Form ×1 (20 cards), visibly added and then played; no kill/god/room jump was used. The minion died on turn 3. Queen HP progressed 500→420→300→235→197→113→14→defeat on turn 7. Silent finished the combat at **105/115**, 652 gold.
7. Native **Advance** entered `THE_ARCHITECT_EVENT_ENCOUNTER` in VisualOnly mode. Continued the native dialogue, reached **“胜利……？”**, observed the displayed **1,457** damage to the Architect, and continued to the score screen: **floor 49, obtained gold 780, elites 4, Bosses 3**. The ending's native scripted player HP became 0/115; this was after the Boss victory, not a combat loss. The unlock/timeline flow also opened normally.

The first mouse click on some native controls only focused/hovered them; native keyboard navigation (Left / Space) or a held 300 ms click resolved the intended action. Rebinding the exact app path and using its exposed Raise action corrected a stale CUA binding. After quitting, querying the old app binding once auto-launched an extra process without the lab arguments and showed the native Steam-not-initialized error. That accidental process was closed through its visible Exit control; inventory selection rebound the proper Steam-off lab process. This was a tool-launch artifact, not a NoS Steam compatibility result. No global settings or Steam connection were changed.

Final-candidate raw logs are local ignored artifacts:

- `artifacts/macos/public-beta/lab-20261009T131806175344Z` — Act 1 Boss through the Act 3 F07/restart/save checks; exit 0.
- `artifacts/macos/public-beta/lab-20261009T170715555945Z` — fresh-process Continue, preserved new attempt, Boss victory and native ending.

Curated native-log evidence, complete log hashes and diagnostic counts: [cua-native-flow.txt](expansion-evidence/cua-native-flow.txt). Native logs retained asset-cache, focus and shutdown resource warnings; no claim of a warning-free runtime is made. The automated run-record fields for gameplay probes remain `not_executed`: this manual Computer Use result is a separate evidence source.

Native read-only history additionally confirmed `win=true`, `was_abandoned=false`, `game_mode=standard`, Ascension 0, and the same seed. After confirming one character unlock, the remaining post-run timeline entries were left for later; the normal macOS Quit shortcut exited the final lab process with code 0. This does not leave an active run: the native run history has already recorded its win.
