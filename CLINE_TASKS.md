# Cline tasks (safe, small, self-contained)

General rules for every task:
- Do NOT edit autopilot core: `KSPChatMod/LearnFlight.cs`, `NativeLearn.cs`, `PerfCurves.cs`, `NativeFlightController.cs`, `PilotPolicy.cs`, `FlightPolicy.cs`, `HelicopterPolicy.cs`, `NativeVerticalLanding.cs`, `NativeOrbital.cs`, `ThrottleGate` code.
- Do not touch `GameData/` or any `PluginData` JSON.
- One task = one commit. Only commit the files that task lists.

## 1. README optional-mods tidy
- File: `README.md` (section "Optional mods (work if installed)").
- Change: put the bullets in alphabetical order and give each one a single short line of what it adds. Keep every mod.
- Accept: same mods listed; each bullet is one line; no other sections changed.

## 2. Glossary page
- New file: `docs/GLOSSARY.md`.
- Change: define LEARN, profile, Vs (stall speed), TWR, lead-turn, flare, entry chart, ThrottleGate, suicide burn, using 1-2 lines each. Link it from `docs/AICS_MENU.md` (one line at the end).
- Accept: at least 9 terms; the link works; no code files changed.

## 3. Test-flight checklist page
- New file: `docs/TEST_FLIGHT_CHECKLIST.md`.
- Change: printable checkbox list built from the "5 plane test flights" table in `docs/AUTOPILOT_OVERHAUL.md` (one block per craft; boxes for pass/fail and a notes line).
- Accept: 5 blocks; every "Must confirm" item from the table appears; the overhaul doc itself is unchanged.

## 4. Unit tests for profile JSON parsing (tests only)
- File: new test file in the existing test project (match the folder and naming of the current tests; find them with `git ls-files | findstr /i test`).
- Change: add a fixture with a copy of the A300 `plane_profiles` block and test that `MiniJson` parses `stall`, `turns[0]`, and `samples` correctly.
- Accept: the new tests pass; no production `.cs` file changed.

## 5. Menu doc spell/consistency pass
- File: `docs/AICS_MENU.md`.
- Change: fix typos and make button names match the in-game labels exactly (check them in `KSPChatMod/AicsMenu.cs`, read-only).
- Accept: diff is wording only; no change to headings structure; `AicsMenu.cs` unchanged.

## 6. Release notes entry
- File: `docs/RELEASE_NOTES_NEXT.md`.
- Change: add a "Docs" bullet listing the new docs (AUTOPILOT_OVERHAUL, GLOSSARY, TEST_FLIGHT_CHECKLIST, KWT as optional reference).
- Accept: one new section/bullets only; existing notes untouched.
