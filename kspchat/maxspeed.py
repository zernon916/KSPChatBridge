"""Best (conservative) estimate of a plane's achievable max level speed (Luke 2026-10-08).

A requested speed above it is clamped and reported ("Best estimate max is ~326 m/s here; you asked 400. Flying 326.").
This is the PHYSICAL limit and separate from the 220 m/s EAS cap (speedcap.py: that one needs override authority).

Method (kRPC, stock aero; all forces in N, q in Pa): measure the drag area now, CdA = |Flight.drag| / dynamic_pressure,
and assume drag scales with rho * V^2 at the target altitude (no transonic drag-rise model, so a SAFETY factor).
Available thrust at the target altitude: air-breathing engines = their current available thrust (at this speed) scaled
by the density ratio rho_target / rho_now (KSP jets lose thrust ~ with density); rocket engines = available_thrust_at
(target pressure). Level flight max: thrust = drag -> V = SAFETY * sqrt(2 T / (rho_t * CdA)). For jets the density
cancels, so the estimate barely depends on altitude (only through the measured thrust/drag). Near level flight at the
current altitude it is never below the speed we're already holding. No estimate (None) on the ground, below 40 m/s or
in very thin air (q < 300 Pa), or if kRPC can't tell - then nothing is clamped.
"""
import logging
import math
import time

log = logging.getLogger("kspchat")
SAFETY = 0.9
MIN_SPD = 40.0
MIN_Q = 300.0
RHO0 = 1.225
H_RHO = 5600.0
PROP_DT = 0.5          # s between the two speed samples of the measured-acceleration thrust estimate (propellers)


def prop_thrust(drag_n, mass_kg, spd0, spd1, dt, vs, g, throttle):
    """Propeller thrust kRPC can't read, from the measured acceleration along the flight path: T = D + m (dV/dt +
    g sin(gamma)), scaled up to full throttle (at most x2 - prop thrust isn't linear in throttle)."""
    if dt <= 0 or spd0 <= 0:
        return 0.0
    t_now = drag_n + mass_kg * ((spd1 - spd0) / dt + g * max(-1.0, min(1.0, vs / spd0)))
    return max(0.0, t_now) / max(float(throttle or 0.0), 0.5)


def vmax(drag_n, q_pa, rho_now, rho_t, jet_thrust_now, rocket_thrust_t):
    """Level-flight max speed (m/s) from the measured drag and the thrust at the target altitude; None if unknown."""
    if not (drag_n > 0 and q_pa > 0 and rho_now > 0 and rho_t > 0):
        return None
    cda = drag_n / q_pa
    thrust = max(jet_thrust_now, 0.0) * (rho_t / rho_now) + max(rocket_thrust_t, 0.0)
    if thrust <= 0:
        return 0.0
    return SAFETY * math.sqrt(2.0 * thrust / (rho_t * cda))


def _density(body, alt):
    try:
        return float(body.density_at(alt))
    except Exception:  # noqa: BLE001
        return RHO0 * math.exp(-max(alt, 0.0) / H_RHO)


def _pressure_atm(body, alt):
    try:
        return float(body.pressure_at(alt)) / 101325.0
    except Exception:  # noqa: BLE001
        return math.exp(-max(alt, 0.0) / H_RHO)


def estimate(v, alt_t=None):
    """(max speed m/s or None, info) for vessel v at altitude alt_t (m MSL; None = here)."""
    try:
        body = v.orbit.body
        f = v.flight(body.reference_frame)
        alt, spd, q, vs = float(f.mean_altitude), float(f.speed), float(f.dynamic_pressure), float(f.vertical_speed)
        if spd < MIN_SPD or q < MIN_Q:
            return None, {"why": "no airflow to measure drag (on the ground, slow or very thin air)"}
        drag = math.sqrt(sum(float(c) ** 2 for c in f.drag))
        h = alt if alt_t is None else float(alt_t)
        try:
            rho_now = float(f.atmosphere_density)
        except Exception:  # noqa: BLE001
            rho_now = _density(body, alt)
        rho_t = _density(body, h)
        p_t = _pressure_atm(body, h)
        from . import propulsion
        jets = rockets = prop = 0.0
        for e in v.parts.engines:
            if propulsion.engine_kind(e.propellant_names) in ("jet", "electric"):  # air-dependent: density-scaled
                jets += float(e.available_thrust)
            else:
                try:
                    rockets += float(e.available_thrust_at(p_t))
                except Exception:  # noqa: BLE001
                    rockets += float(e.available_thrust)
        if jets + rockets <= 0 and propulsion.classify(v)["props"]:  # rotor props: thrust from measured accel
            time.sleep(PROP_DT)
            spd1 = float(f.speed)
            prop = prop_thrust(drag, float(v.mass), spd, spd1, PROP_DT, vs, float(body.surface_gravity),
                               float(v.control.throttle))
            jets += prop
        vm = vmax(drag, q, rho_now, rho_t, jets, rockets)
        if vm is None:
            return None, {"why": "no drag reading"}
        if abs(vs) < 5.0 and abs(h - alt) < 500.0:
            vm = max(vm, spd)  # we're already holding this speed level here
        info = {"drag_n": round(drag), "q_pa": round(q), "cda_m2": round(drag / q, 3), "jet_n": round(jets),
                "rocket_n": round(rockets), **({"prop_n": round(prop)} if prop else {}), "rho_ratio": round(rho_t / rho_now, 3), "alt": round(h), "spd": round(spd)}
        log.info("max speed estimate %.0f m/s at %.0f m (%s)", vm, h, info)
        return vm, info
    except Exception as e:  # noqa: BLE001
        return None, {"why": f"{e.__class__.__name__}: {e}"}


def clamp(v, speed, alt_t=None):
    """(speed to fly, note or ''): a request above the estimate is clamped to it (rounded down)."""
    est, _ = estimate(v, alt_t)
    if est is None or speed <= est:
        return float(speed), ""
    fly = float(math.floor(est))
    where = "here" if alt_t is None else f"at {alt_t:.0f} m"
    return fly, f"Best estimate max is ~{fly:.0f} m/s {where}; you asked {speed:.0f}. Flying {fly:.0f}."