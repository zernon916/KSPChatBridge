# Changelog

## 0.4.0 - 2026-10-21

Rover science release. Rover autopilot and science tours are new - watch the first tours and be ready to take over.

- Rover mode: per-craft PLANE/ROVER switch (AUTOPILOT/CRAFT) with a vehicle classifier; rover pages Drive, Science tour and Places plus ROVER settings.
- Science inventory: every science part on the craft (stock, DMagic, StationScience, containers, labs) with its value here, one-shot / held / scientist-reset state.
- Rover science autopilot: drives a science tour around KSC and named places, follows the KSC roads (runway, taxiway, hub spokes, Crawlerway) with roads bent around building footprints, per-surface speed caps learned per rover, slope and roll limits, runway hold-short, obstacle rays that brake in time (ray longer than the braking distance), circling and stuck detection with a back-up escape that checks behind first, tour stopped with a tablet alert after 3 failed escapes.
- Science policies: per-craft transmit policy (ALWAYS / >=60% / >=40% / SAVE) with Science-Full-Transmit and ScienceRelay detection, scientist resets of used one-shots (Goo, Materials Bay), power management (on low charge the rover parks, deploys solar panels, starts EC generators/converters that have inputs, and keeps a fuel reserve to get home), AutomatedScienceSampler paused while the autopilot does the science.
- Named places: pin / rename / drive to places on the tablet PLACES page; Kerbal Konstructs launch sites and facilities are picked up; marked places join the science tour.
- Tablet status bar: CommNet signal bars, relay flag, battery gauge with percentage and signal delay.
- Science tour checklist: sites x experiments with live science gains and road driving distances, tick sites or single experiments (saved per rover), one-shots run at their best site, START TOUR drives the shortest order; experiments predicted 0 are still tried live at each stop; check marks come from the game (science earned or data on board), so a revert or quickload leaves no stale marks.
- Also since 0.3.1: automatic-landing work (energy throttle, flare, rollout brakes and reverse thrust, centreline tracking), takeoff/landing tuning in CRAFT with auto-filled live values, landing test zone.

## 0.3.1 - 2026-10-17

Landing repair and fixes. Automatic landing is still in testing - watch the approach and be ready to take over.

- Landing: bank commands capped at 30 degrees; runway endpoints moved onto the surveyed centerline; Track() now uses ground track (wind/crab aware); final-capture validation, misaligned flares gated, bank onto centerline sharing the ILS glidepath.
- Landing lateral diagnostics in the log; new runway geometry tests.
- LEARN: pause and resume running segments; returns toward KSC beyond 140 km; stall test flown 1000 m below the learned ceiling (altitude labelled).
- UI: brighter card text, tab labels no longer tinted; separate AP row below Mach; Mission tab hotfix.
- Chat: new get_gps tool (vessel latitude/longitude/altitude).
- Tests: one failing test no longer aborts the whole run.

## 0.3.0 - 2026-10-14

Tablet home screen with app library; Comms, Contracts, Science, Dv Map, Xfer, Campaign, Crew and Systems apps; docking; porkchop planner; native rocket autopilot; KSC rover map and science tour; helicopter autopilot.