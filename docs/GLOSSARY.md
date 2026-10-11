# AICS glossary

Plain-language definitions for the terms used across the AICS docs (`AICS_MENU.md`, `AUTOPILOT_OVERHAUL.md`, `TEST_FLIGHT_CHECKLIST.md`).

- **LEARN** - the in-mod test flight (the LEARN button / `learn` order) that flies a craft through takeoff, climb, turns, stall, decel, sink and landing, measuring its real performance and saving it as a profile. Run it once per craft before relying on the autopilot.
- **Profile** - the per-craft performance data LEARN records (the `plane_profiles["<craft>"]` block in `native_settings.json`: stall, turns, samples, sink table and so on). The autopilot flies from these numbers; with no profile it falls back to conservative defaults and says so.
- **Vs (stall speed)** - the slowest speed the craft can fly before the wing stalls. AICS stays at or above 1.3 x Vs outside the flare and adds power near the stall margin.
- **TWR (thrust-to-weight ratio)** - engine thrust divided by the craft's weight. AICS uses it to pick the takeoff start throttle and to estimate a safe ceiling (`8000 m x ln(TWR / 0.1)`).
- **Lead-turn** - rolling in early so the craft rolls out level on the target heading; the roll-in lead comes from the learned roll rate and true speed.
- **Flare** - the nose-up round-out just before touchdown that trades sink rate for a soft landing (target under 2 m/s vertical speed).
- **Entry chart** - the approach chart picked by bearing to the runway; it sets the join point and the stabilized glide path the autoland flies.
- **ThrottleGate** - the single throttle output path. Every mode/step hands its throttle *target* to the gate, which moves the actual throttle at a finite, logged rate (normal / fast / emergency) so power changes stay smooth.
- **Suicide burn** - a rocket landing burn started at the last moment (retrograde) so the craft reaches ~0 speed just at the surface; AICS adds a start margin and keeps throttle headroom.
