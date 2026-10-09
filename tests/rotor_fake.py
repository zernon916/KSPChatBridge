"""Typed rotor API backed by mutable fixture state, independent of production readers."""
class RotorApi:
    def __init__(self, fields): self.fields = fields
    @property
    def current_rpm(self): return float(self.fields.get("Current RPM", 0))
    @property
    def target_rpm(self): return float(self.fields.get("RPM Limit", 460))
    @property
    def torque_limit(self): return float(self.fields.get("Torque Limit(%)", 100))
    @property
    def brake_percentage(self): return float(self.fields.get("Brake", 0))
    @property
    def motor_engaged(self): return self.fields.get("Motor", "Engaged") == "Engaged"
