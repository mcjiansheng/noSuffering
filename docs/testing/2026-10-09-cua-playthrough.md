# macOS public-beta native UI playthrough record

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

## Remaining validation

The full run, shop purchase-lock/save-continue, route rollback to a visited node, Act 2 ancient reward/replacement, Act 3 boss-HP action, and run completion remain untested. The candidate is being replaced before those tests.

## Final candidate `578af2e7` continuation

- Launched the final candidate and selected the main-menu Continue Game item. The resumed in-combat opening hand exactly matched the saved, refreshed hand: Defend, Survivor, Strike, Neutralize, Strike, Defend, Defend. This is visual evidence that the Save and Quit / whole-process relaunch preserved the refreshed draw order.
- Card resolution is currently blocked in native Computer Use: clicking, double-clicking, holding, and dragging the visible Strike card to the enemy all enlarged/hovered the card but left energy at 3 and enemy HP at 55/55. The NoSuffering panel and native menus remain clickable. Root agent is inspecting the live input path; no successful card-play outcome is claimed.

No source files changed by this validation session.
