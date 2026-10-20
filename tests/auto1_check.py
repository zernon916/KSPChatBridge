"""AUTO1 check 2: independent re-derivation of the auto landing numbers (no C# code shared).
Inputs: mass + summed deflectionLiftCoeff from the stock .craft files, stall in use and touchdown
speed / AoA from Luke's flight logs (Oct 2026). Run: python tests/auto1_check.py"""
import math
G, RHO = 9.81, 1.225
PLANES = [  # name, mass t, lift coeff, stall in use, touchdown m/s, touchdown AoA deg, Luke landing, Luke approach
    ("Aeris 3A", 5.555, 7.44, 36, None, None, 41.5, 70),
    ("Aeris 4A", 19.96, 9.02, 59, 124, 4.598, 115, 160),
    ("Albatross 3", 39.88, 48.78, 48, 90, 0.521, 55, 80),
    ("Thunderbird", 14.23, 8.51, 47, 120, 4.598, 115, 130),
    ("Stratolauncher", 36.9, 38.0, 64, 71, 6.418, 74, 147),
    ("Stearwing A300", 48.945, 62.05, 85, 78, 2.952, 125, 160),
]
# what the C# replay test printed (AutoLanding.Compute)
CSHARP = {"Aeris 3A": (64.5, 90.3), "Aeris 4A": (118.9, 166.5), "Albatross 3": (67.5, 94.5),
          "Thunderbird": (115.1, 161.1), "Stratolauncher": (80.4, 112.6), "Stearwing A300": (97.8, 136.9)}
ok = True
for n, m, lc, stall, v, aoa, ll, la in PLANES:
    area = 3.52 * lc
    vs = math.sqrt(2 * m * 1000 * G / (RHO * area * 1.6))
    v5 = None
    if v and aoa and aoa >= 1.5:  # lift slope from touchdown: W = q S cl' aoa, then solve q S cl' 5 = W
        clp = m * 1000 * G / (0.5 * RHO * v * v * area * aoa)
        v5 = math.sqrt(2 * m * 1000 * G / (RHO * area * clp * 5))
    land = max(1.15 * stall, v5 if v5 else 1.4 * vs)
    app = max(1.4 * land, land + 10)
    cl, ca = CSHARP[n]
    good = abs(land - cl) < 0.06 and abs(app - ca) < 0.06
    ok &= good
    print(f"{'OK ' if good else 'BAD'} {n:15s} landing {land:6.1f} (Luke {ll:5}) approach {app:6.1f} (Luke {la})")
print("AUTO1 CHECK 2 PASSED" if ok else "AUTO1 CHECK 2 FAILED")
raise SystemExit(0 if ok else 1)
