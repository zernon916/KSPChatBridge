using System;

namespace KSPChatBridge
{
    /// <summary>SI units, right-handed coordinates. Circular parking orbit about the departure body.</summary>
    internal static class EjectionMath
    {
        internal static bool Finite(double x) { return !double.IsNaN(x) && !double.IsInfinity(x); }

        /// <summary>
        /// X is parent velocity projected into the parking plane, Y is normal cross X,
        /// Z is the parking angular momentum. Returns position and impulse in that frame.
        /// Angle is signed burn-position longitude from X, in [-180,180].
        /// False means invalid input or no periapsis in the parking plane can supply this asymptote.
        /// </summary>
        internal static bool Solve(double mu, double bodyRadius, double parkingRadius,
            double vx, double vy, double vz, out double dv, out double angleDeg,
            out double[] burnPoint, out double[] impulse)
        {
            dv = angleDeg = double.NaN;
            burnPoint = new[] { double.NaN, double.NaN, double.NaN };
            impulse = new[] { double.NaN, double.NaN, double.NaN };
            if (!Finite(mu) || !Finite(bodyRadius) || !Finite(parkingRadius) ||
                !Finite(vx) || !Finite(vy) || !Finite(vz) || mu <= 0 || bodyRadius <= 0 || parkingRadius <= bodyRadius) return false;
            double speed = Math.Sqrt(vx*vx + vy*vy + vz*vz);
            if (!Finite(speed) || speed <= 0) return false;
            double sx = vx/speed, sy = vy/speed, sz = vz/speed;
            double planar = Math.Sqrt(sx*sx + sy*sy);
            double turn = RocketMath.EjectionAngleDeg(mu, parkingRadius, speed)*Math.PI/180;
            double c = -Math.Cos(turn); // 1 / hyperbolic eccentricity
            if (planar < c || planar < 1e-12) return false;
            double a = -c/planar, b = -Math.Sqrt(Math.Max(0, 1-a*a));
            double rx = a*sx/planar - b*sy/planar, ry = a*sy/planar + b*sx/planar;
            double circular = Math.Sqrt(mu/parkingRadius);
            double periSpeed = circular + RocketMath.EjectionDv(mu, parkingRadius, speed);
            double sinTurn = Math.Sin(turn);
            if (sinTurn <= 1e-12) return false;
            impulse = new[] { periSpeed*(sx+c*rx)/sinTurn + circular*ry,
                periSpeed*(sy+c*ry)/sinTurn - circular*rx, periSpeed*sz/sinTurn };
            burnPoint = new[] { parkingRadius*rx, parkingRadius*ry, 0.0 };
            dv = Math.Sqrt(impulse[0]*impulse[0]+impulse[1]*impulse[1]+impulse[2]*impulse[2]);
            angleDeg = Math.Atan2(ry, rx)*180/Math.PI;
            return Finite(dv);
        }
    }
}
