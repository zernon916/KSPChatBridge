using System;
using KSPChatBridge;

class Program
{
    class Servo { private float transformRateOfMotion = 380; }
    class Rotor : Servo { public float currentRPM = 0; }
    static void Check(bool value, string name) { if (!value) throw new Exception(name); }
    static void Main()
    {
        var p = new RestartPolicy();
        Check(p.CanStart(0, true, false, false, false), "initial launch");
        Check(!p.CanStart(0, false, false, false, false), "autostart off");
        Check(!p.CanStart(0, true, true, false, false), "quit");
        Check(!p.CanStart(0, true, false, true, false), "already starting");
        Check(!p.CanStart(0, true, false, false, true), "alive but unhealthy");
        p.Failed(0);
        Check(!p.CanStart(1, true, false, false, false), "backoff");
        Check(p.CanStart(2, true, false, false, false), "retry");
        p.Failed(2);
        Check(!p.CanStart(5, true, false, false, false), "increased backoff");
        for (int i = 0; i < 20; i++) p.Failed(10);
        Check(p.CanStart(70, true, false, false, false), "bounded backoff");
        p.Stable(); p.Failed(100);
        Check(p.CanStart(102, true, false, false, false), "stable reset");
        Console.WriteLine("Restart policy: 10 behavior checks passed.");
        Check(RotorMeasurements.Rpm(new Rotor()) == 380, "physics RPM beats stale UI field");
        Check(float.IsNaN(RotorMeasurements.Rpm(new object())), "missing measurement is unknown");
        Console.WriteLine("Rotor measurement: 2 behavior checks passed.");
    }
}
