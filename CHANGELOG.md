# Changelog

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