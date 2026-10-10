# FLIGHTPLAN.md — Flight plan queues for the AICS Flight Plan editor

Paste any plan into **AICS > Flight Plan** (or ask the chat AI: "fly the island hop plan"), edit the numbers,
then **Fly**. One step per line. Plans below the built-in four (`circle`, `cruise`, `circuit`, `orbit`).

**Step grammar the runner understands today** (planes/rockets fully; see `KSPChatMod/NativePlan.cs`):
`takeoff` · `climb 3000 m agl vs 50` · `descend 1000 m agl vs 30` · `cruise hdg 090 alt 3000 m agl speed 180 for 3 min`
(`for` takes s/min/km) · `circle 3 laps right bank 15 [around field]` · `fly to Island Airfield alt 500 m speed 150`
(flies there and lands) · `land` / `land Runway 27` / `land Island Airfield` / `land Runway 09 tg 2` (touch-and-goes)
· `taxi to Runway 09 start` · rockets (MechJeb): `ascent 80 km inc 0` · `circularize` · `transfer Mun` · `warp to soi`
· `course correction 30 km` · `deorbit 30 km` · `land ksc` / `land here` / `chutes` · any: `wait 30 s` · `science`
· `override bank 30 *` (asks yes/no) · `override off`.
Steps marked **[manual]** are flown by hand or via chat tools (heli/rover/station verbs land in P5-2…4).

---

## Fixed wing

### 1. Island Hop
KSC to Island Airfield and stop. Good first cross-water flight.
```
takeoff
climb 1200 m agl vs 45
cruise hdg 270 alt 1200 m agl speed 170 for 2 min
descend 400 m agl vs 20
fly to Island Airfield alt 300 m speed 140
```

### 2. Touch-and-Go Marathon
Circuit practice: 3 touch-and-goes, then full stop.
```
takeoff
climb 600 m agl vs 30
land Runway 09 tg 3
```

### 3. High-Altitude Survey
Climb to thin air, cruise a grid leg, come back down.
```
takeoff
climb 8000 m msl vs 45
cruise hdg 045 alt 8000 m msl speed 220 for 5 min
cruise hdg 225 speed 220 for 5 min
descend 2000 m agl vs 25
land
```

### 4. Valley Run
Low, slow, scenic. Watch the terrain floor do its job.
```
takeoff
climb 500 m agl vs 25
cruise hdg 000 alt 500 m agl speed 130 for 4 min
circle 1 lap right bank 20
cruise hdg 180 speed 130 for 4 min
land
```

### 5. Coastal Patrol
Out along the coast, figure-eight over the shore, home.
```
takeoff
climb 1500 m agl vs 40
cruise hdg 270 alt 1500 m agl speed 160 for 6 km
circle 2 laps right bank 15
cruise hdg 090 speed 160 for 6 km
land
```

### 6. Night Qualifier
Same as a normal circuit but timed for after sunset. Lights on.
```
takeoff
climb 800 m agl vs 30
cruise speed 150 for 2 min
circle 2 laps right bank 12
descend 300 m agl vs 15
land Runway 09
```

### 7. Engine-Out Return
Winds around and lands long. Practice the glide-back pattern.
```
takeoff
climb 1000 m agl vs 35
cruise hdg 090 alt 1000 m agl speed 140 for 1 min
circle 1 lap left bank 10
descend 250 m agl vs 12
land
```

### 8. Airdrop Spotter
Overfly the drop zone at altitude, circle while cargo lands, recover.
```
takeoff
climb 2000 m agl vs 40
cruise hdg 180 alt 2000 m agl speed 160 for 3 min
circle 3 laps right bank 12
descend 400 m agl vs 20
land
```

### 9. Ferry Flight — Island to KSC
The island hop in reverse with a fuel-conscious cruise.
```
taxi to Runway 09 start
takeoff
climb 2500 m agl vs 35
cruise hdg 090 alt 2500 m agl speed 180 for 5 min
descend 600 m agl vs 20
land
```

### 10. Speed Run
Straight-line dash: climb out, shove the throttle, turn around.
```
takeoff
climb 3000 m agl vs 60
override speed 320 *
cruise hdg 270 alt 3000 m agl speed 320 for 3 km
circle 1 lap right bank 25
cruise hdg 090 speed 320 for 3 km
override off
land
```

---

## Helicopters

### 11. Hover Tour
Climb straight up, orbit the field at hover speed, set down.
```
takeoff
climb 300 m agl vs 8
circle 2 laps right bank 8 around field
descend 30 m agl vs 2
land
```
(Use `heli_control hover/fly/land` from chat for finer collective control.)

### 12. Rooftop Rescue
Short hop to a pad, hover, extract, return. Fly legs [manual] or with `heli_control`.
```
takeoff
climb 150 m agl vs 6
cruise hdg 090 alt 150 m agl speed 40 for 30 s
wait 20 s
cruise hdg 270 speed 40 for 30 s
descend 20 m agl vs 1.5
land
```

### 13. Sling-Load Delivery
Lift, crawl out at low altitude, drop, come home. Keep it under 60 m AGL.
```
takeoff
climb 60 m agl vs 3
cruise hdg 180 alt 60 m agl speed 30 for 90 s
wait 15 s
cruise hdg 000 speed 30 for 90 s
land
```

### 14. Mountain Recon
Ridge-line patrol: slow legs with wide circles at the waypoints.
```
takeoff
climb 600 m agl vs 5
cruise hdg 045 alt 600 m agl speed 35 for 2 min
circle 1 lap right bank 10
cruise hdg 225 speed 35 for 2 min
descend 40 m agl vs 1.5
land
```

---

## Rockets (MechJeb)

### 15. Basic Orbit + Science
The starter rocket flight: up, circular, run the experiments, come home.
```
ascent 80 km inc 0
circularize
science
deorbit 30 km
chutes
```

### 16. Mun Flags and Back
Ascent, transfer, land at the Mun, plant flags (EVA [manual]), return.
```
ascent 80 km inc 0
circularize
transfer Mun
warp to soi
deorbit 15 km
land here
science
deorbit 25 km
chutes
```
(The `deorbit/land here` legs after `transfer` run at the Mun; the last `deorbit 25 km / chutes` runs at Kerbin
after `warp to soi` back — verify the body before each burn.)

### 17. Minmus Mint Run
Low-gravity landing practice with a science haul.
```
ascent 80 km inc 5
circularize
transfer Minmus
warp to soi
deorbit 8 km
land here
science
deorbit 20 km
chutes
```

### 18. Polar Science Pass
Inclined orbit for mapping contracts and biome scans.
```
ascent 90 km inc 90
circularize
science
wait 60 s
deorbit 30 km
chutes
```

### 19. Suborbital Hop
Contract "reach space" runs. Cheap and quick.
```
ascent 75 km inc 85
wait 10 s
deorbit 20 km
chutes
```

### 20. Rescue Rendezvous
Launch to a stranded kerbal's orbit and hold near them. Docking [manual] or `dock_with` from chat.
```
ascent 100 km inc 2
circularize
course correction 10 km
wait 30 s
```

---

## Space stations

### 21. Station Crew Rotation
Launch-to-station run. Approach + dock with `dock_with target` from chat or [manual].
```
ascent 100 km inc 2
circularize
course correction 5 km
wait 20 s
```

### 22. Station Reboost
Raise a decaying station's orbit without leaving the neighborhood.
```
ascent 95 km inc 2
circularize
course correction 2 km
wait 30 s
deorbit 35 km
chutes
```
(For the station itself: `change periapsis 100 km`-style burns from chat's orbital tools.)

### 23. Station Science Swap
Dock, run the lab, swap experiments, come down with the results.
```
ascent 100 km inc 2
circularize
course correction 3 km
science
wait 60 s
deorbit 30 km
chutes
```

### 24. Station Resupply Freighter
Cargo up, empty cans down. Bring RCS fuel and snacks.
```
ascent 100 km inc 2
circularize
course correction 5 km
wait 45 s
deorbit 30 km
chutes
```

---

## Satellites

### 25. Comms Constellation — One Plane
Deploy a relay at 80 km, circularize, leave it running. Repeat launches with `inc` offsets for full coverage.
```
ascent 80 km inc 0
circularize
wait 30 s
science
```

### 26. Polar Mapping Bird
High-inclination mapping satellite with a science package.
```
ascent 120 km inc 88
circularize
science
wait 90 s
```

### 27. KEO Comms Relay
High, slow relay with a long view of the KSC side of Kerbin.
```
ascent 2863 km inc 0
circularize
wait 60 s
```
(`ascent` tops out where MechJeb puts the ap; use `change apoapsis 2863 km` + `circularize` from chat for exact KEO.)

### 28. Deorbit a Dead Bird
Deorbit a spent stage/satellite over the ocean. No chutes — controlled reentry.
```
deorbit 0 km
```
(Set periapsis just below the surface, over water. Do NOT `chutes`.)

---

## Rovers (drive legs run via `taxi_to`)

### 29. KSC Perimeter Drive
Wheeled shakedown: taxi the crawler-way points at low speed.
```
taxi to Runway 09 start speed 8
taxi to KSC Pad speed 8
taxi to Runway 27 start speed 8
```

### 30. Mun Base Resupply Trundle
Drive between surface bases. Legs are long — warp between them.
```
taxi to Island 27 start speed 6
wait 30 s
taxi to KSC Pad speed 6
```
(Swap in your saved spots on other bodies with `save_landing_spot` first.)

### 31. Hill Climb Test
Rover drivetrain torture test: short legs at speed, pause, repeat.
```
taxi to Runway 27 start speed 12
wait 20 s
taxi to KSC Pad speed 12
wait 20 s
taxi to Runway 09 start speed 12
```

---

## Kerbals themselves (EVA)

### 32. EVA Inspection Walk
Jetpack hop around the hull, check the panels, get back inside.
```
wait 30 s
```
(Fly the EVA [manual] — `eject_kerbal` from chat starts it; RCS jetpack, then board.)

### 33. Parachute Drop Test
Fly the test article up, toss the kerbal out, chute to the surface.
```
takeoff
climb 2000 m agl vs 25
wait 10 s
land
```
(Eject with `eject_kerbal` at altitude, stage chutes [manual] or `deploy_parachutes` from chat, recover on the ground.)

### 34. Surface Sample Circuit
On EVA: hop biome to biome, collect samples, return to the pod.
```
wait 60 s
```
(EVA hops between biomes [manual]; `run_science` / `reset_experiments` from chat handles the data.)

---

## Notes
- **`override bank 30 *`** raises a limit for the rest of the plan — ALWAYS end it with `override off`. The ` *`
  means Luke gets a yes/no prompt when it runs. Never write `***` or `authorise all`.
- Known runways: `Runway 09`, `Runway 27`, `Island Airfield` (plus any saved spot via `save_landing_spot`).
- Rocket legs need **MechJeb**. Plane and taxi legs run without it.
- The chat AI can draft a plan for you: "plan a survey flight" fills the editor; you edit and press Fly.
