using System;

namespace KSPChatBridge
{
    internal static class BodyInfo
    {
        public static CelestialBody Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string lowerName = name.ToLower();
            foreach (CelestialBody body in FlightGlobals.Bodies)
            {
                if (body != null && body.bodyName.ToLower() == lowerName)
                    return body;
            }
            return null;
        }

        public static string Line(CelestialBody b)
        {
            if (b == null) return "Mun: mu 0, R 0 km, atmo 0 km, SOI 0 km, day 0 s, parent None";
            var sb = new System.Text.StringBuilder();
            sb.Append("Mun: mu ");
            sb.Append(b.gravParameter);
            sb.Append(", R ");
            sb.Append(Math.Round(b.Radius / 1000f, 2));
            sb.Append(" km");

            if (b.atmosphere)
            {
                sb.Append(", atmo ");
                sb.Append(Math.Round(b.atmosphereDepth / 1000f, 2));
                sb.Append(" km");
            }

            sb.Append(", SOI ");
            sb.Append(Math.Round(b.sphereOfInfluence / 1000f, 2));
            sb.Append(" km");

            sb.Append(", day ");
            sb.Append(Math.Round(b.rotationPeriod, 2));
            sb.Append(" s");

            if (b.referenceBody != null && b.referenceBody != b)
                sb.Append(", parent ").Append(b.referenceBody.bodyName);
            else
                sb.Append(", parent None");

            return sb.ToString();
        }
    }
}
