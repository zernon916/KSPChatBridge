"""kRPC version compatibility helpers (Luke 2026-10-08: the live server is kRPC 0.6.0).

* AutoPilot: 0.6 dropped engage()/disengage() - on/off is the `engaged` property. autopilot(ap, on) works on both.
* Module.fields: deprecated in 0.6 and THROWS on Breaking Ground rotors ("An item with the same key has already been
  added. Key: Motor" - servoMotorIsEngaged and motorState are both shown as 'Motor'). propulsion.fields() reads
  fields_by_id instead (see there).
"""


def autopilot(ap, on):
    """kRPC AutoPilot on/off on any kRPC version. Returns True when the state was set."""
    on = bool(on)
    meth = getattr(ap, "engage" if on else "disengage", None)
    if callable(meth):
        meth()
        return True
    ap.engaged = on
    return True
