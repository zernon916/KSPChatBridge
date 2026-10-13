using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using Expansions.Serenity;

namespace KSPChatBridge
{
    // L3: is the predicted touchdown spot flat enough for the legs, and if not, which nearby cell is flatter?
    // Read-only: samples terrain and reports, never touches flight controls (wiring that up is a later task).
    public partial class NativeFlightController
    {
        const double SlopeSpacingM = SlopeMath.DefaultSpacingM;    // metres between terrain samples
        const double SlopeMaxDeg = SlopeMath.DefaultMaxSlopeDeg;   // steepest slope accepted without a nudge
        const double SlopeMaxNudgeM = 40;                          // never nudge further than the sampled grid reaches
        const double SlopeCoastLimitS = 1800;                      // ballistic prediction horizon (long final descents)
        const double SlopeFallM = 1000;                            // above this, trust the orbit's impact point
        static readonly CultureInfo SlopeInv = CultureInfo.InvariantCulture;

        /// <summary>Judge the terrain at the predicted touchdown spot. Returns true when the site is flat enough
        /// (slopeDeg &lt;= 8 deg, nudge zero). Returns false when the ground is too steep - nudge then points
        /// horizontally (world space, metres) at the flattest nearby cell - or when the terrain could not be
        /// sampled at all (nudge zero); msg always says which.</summary>
        internal bool CheckLandingSlope(Vessel v, out double slopeDeg, out Vector3d nudge, out string msg)
        {
            slopeDeg = double.NaN;
            nudge = Vector3d.zero;
            msg = "No vessel to check a landing slope for.";
            if (v == null) return false;
            CelestialBody body = v.mainBody;
            if (body == null || !(body.Radius > 0)) { msg = "The vessel has no body to sample terrain on."; return false; }
            if (body.pqsController == null)
            {
                msg = (string.IsNullOrEmpty(body.bodyName) ? "This body" : body.bodyName) + " has no terrain to sample.";
                return false;
            }
            double lat, lon; string spot;
            if (!SlopeSpot(v, body, out lat, out lon, out spot)) { msg = "No landing spot to check: " + spot + "."; return false; }
            double dLat, dLon;
            if (!SlopeStep(body.Radius, lat, SlopeSpacingM, out dLat, out dLon))
            {
                msg = "Cannot lay a terrain grid at " + SlopeWhere(lat, lon) + ".";
                return false;
            }
            var heights = new double[SlopeMath.DefaultGrid, SlopeMath.DefaultGrid];
            int centre = SlopeMath.DefaultGrid / 2, usable = 0;
            for (int r = 0; r < SlopeMath.DefaultGrid; r++)
            for (int c = 0; c < SlopeMath.DefaultGrid; c++)
            {
                double h;
                heights[r, c] = SlopeTerrain(body, lat + (r - centre) * dLat, lon + (c - centre) * dLon, out h) ? h : double.NaN;
                if (!double.IsNaN(heights[r, c])) usable++;
            }
            if (usable < 4) { msg = "Terrain around " + spot + " returned no usable heights."; return false; }
            double eastM, northM;
            bool nudged = SlopeMath.Nudge(heights, SlopeSpacingM, SlopeMaxDeg, out slopeDeg, out eastM, out northM);
            if (!SlopeMath.Finite(slopeDeg)) { msg = "Terrain around " + spot + " is too broken to fit a slope."; return false; }
            string slopeText = SlopeDegText(slopeDeg), where = SlopeWhere(lat, lon);
            if (!nudged)
            {
                bool flat = slopeDeg <= SlopeMaxDeg;
                msg = flat
                    ? "Landing spot " + where + " (" + spot + ") slopes " + slopeText + " - flat enough to land."
                    : "Landing spot " + where + " (" + spot + ") slopes " + slopeText + " - too steep, and no flatter ground within "
                        + SlopeSpacingM.ToString("0", SlopeInv) + " m of it.";
                return flat;
            }
            double reach = Math.Sqrt(eastM * eastM + northM * northM);
            if (reach > SlopeMaxNudgeM) { double scale = SlopeMaxNudgeM / reach; eastM *= scale; northM *= scale; }
            Vector3d up, north, east;
            if (!SlopeFrame(v, body, out up, out north, out east))
            {
                msg = "Landing spot " + where + " slopes " + slopeText + " - too steep, but there is no local east/north to nudge along.";
                return false;
            }
            nudge = east * eastM + north * northM;
            msg = "Landing spot " + where + " (" + spot + ") slopes " + slopeText + " - too steep; nudge " + SlopeOffsetText(eastM, northM)
                + " to flatter ground.";
            return false;
        }

        /// <summary>Where to check: the orbit's impact point while the trajectory still cuts the surface, else a
        /// ballistic coast from the live descent rate and horizontal speed, else the ground under the craft.</summary>
        static bool SlopeSpot(Vessel v, CelestialBody body, out double lat, out double lon, out string spot)
        {
            lat = v.latitude; lon = v.longitude; spot = "the ground under the craft";
            if (!SlopeMath.SaneLatLon(lat, lon)) { lat = lon = double.NaN; spot = "the vessel reports no position"; return false; }
            if (v.LandedOrSplashed) return true;
            Orbit o = v.orbit;
            // Periapsis under the ground: the craft lands at (or before) its lowest point, so sample that.
            if (o != null && o.referenceBody == body && o.PeA < 0 && SlopeMath.Finite(o.timeToPe) && o.timeToPe > 1 && v.altitude > SlopeFallM)
            {
                try
                {
                    Vector3d world = body.position + o.getRelativePositionAtUT(Planetarium.GetUniversalTime() + o.timeToPe);
                    double impactLat = body.GetLatitude(world), impactLon = SlopeWrapLon(body.GetLongitude(world));
                    if (SlopeMath.SaneLatLon(impactLat, impactLon)) { lat = impactLat; lon = impactLon; spot = "the predicted impact point"; return true; }
                }
                catch (Exception) { }   // fall through to the ballistic coast
            }
            // Final descent: coast sideways for as long as it takes to lose the height above the ground.
            double ground;
            if (!SlopeTerrain(body, lat, lon, out ground)) { spot = "no terrain under the craft"; return false; }
            double high = v.altitude - ground, sink = -v.verticalSpeed;
            if (!(high > 0) || !(sink > .5)) return true;   // on the ground, climbing or level: check under the craft
            Vector3d up, north, east;
            if (!SlopeFrame(v, body, out up, out north, out east)) return true;
            double seconds = Math.Min(high / sink, SlopeCoastLimitS);
            double coastLat, coastLon;
            SlopeMath.OffsetLatLon(lat, lon, Vector3d.Dot(v.srf_velocity, east) * seconds, Vector3d.Dot(v.srf_velocity, north) * seconds,
                body.Radius, out coastLat, out coastLon);
            if (SlopeMath.SaneLatLon(coastLat, coastLon)) { lat = coastLat; lon = coastLon; spot = "the predicted touchdown point"; }
            return true;
        }

        /// <summary>Local up / north / east unit vectors (world space) at the vessel. False without a body frame or
        /// over a pole, where there is no east/west to nudge along.</summary>
        static bool SlopeFrame(Vessel v, CelestialBody body, out Vector3d up, out Vector3d north, out Vector3d east)
        {
            up = north = east = Vector3d.zero;
            if (v == null || body == null || body.transform == null) return false;
            up = v.upAxis;
            if (up.sqrMagnitude < 1e-9) up = body.transform.up;   // degenerate: fall back to the body's axis
            if (up.sqrMagnitude < 1e-9) return false;
            up = up.normalized;
            north = Vector3d.Exclude(up, body.transform.up);      // body axis minus its part along up = geographic north
            if (north.sqrMagnitude < 1e-6) return false;          // over a pole
            north = north.normalized;
            east = Vector3d.Cross(up, north).normalized;
            return east.sqrMagnitude > 1e-6;
        }

        /// <summary>Terrain height (m MSL, may be negative under water) at a lat/lon; false when the game cannot
        /// answer - no PQS, an out-of-range coordinate, or a PQS that throws / returns nothing usable.</summary>
        static bool SlopeTerrain(CelestialBody body, double lat, double lon, out double height)
        {
            height = double.NaN;
            if (body == null || body.pqsController == null || !SlopeMath.SaneLatLon(lat, lon)) return false;
            try { height = body.TerrainAltitude(lat, lon, true); }   // allowNegative: real ground below sea level too
            catch (Exception) { return false; }
            return SlopeMath.Finite(height);
        }

        /// <summary>Degrees of latitude / longitude per metre at this latitude (longitude shrinks toward the poles).</summary>
        static bool SlopeStep(double radius, double lat, double metres, out double dLat, out double dLon)
        {
            dLat = dLon = double.NaN;
            if (!(radius > 0) || !(metres > 0) || !SlopeMath.Finite(lat) || Math.Abs(lat) > 90) return false;
            dLat = metres / radius * 180 / Math.PI;
            double cosLat = Math.Cos(lat * Math.PI / 180);
            if (!(Math.Abs(cosLat) > 1e-6)) return false;   // at a pole the grid degenerates to a line
            dLon = metres / (radius * cosLat) * 180 / Math.PI;
            return SlopeMath.Finite(dLat) && SlopeMath.Finite(dLon) && dLat > 0 && dLon > 0;
        }

        static double SlopeWrapLon(double lon)
        {
            if (!SlopeMath.Finite(lon)) return double.NaN;
            double wrapped = lon;
            while (wrapped >= 180) wrapped -= 360;
            while (wrapped < -180) wrapped += 360;
            return wrapped;
        }

        static string SlopeDegText(double deg) { return SlopeMath.Finite(deg) ? deg.ToString("0.0", SlopeInv) + " deg" : "an unknown amount"; }

        static string SlopeWhere(double lat, double lon)
        {
            if (!SlopeMath.SaneLatLon(lat, lon)) return "an unknown spot";
            return Math.Abs(lat).ToString("0.000", SlopeInv) + (lat < 0 ? " S, " : " N, ")
                + Math.Abs(lon).ToString("0.000", SlopeInv) + (lon < 0 ? " W" : " E");
        }

        static string SlopeOffsetText(double eastM, double northM)
        {
            return Math.Abs(northM).ToString("0", SlopeInv) + " m " + (northM >= 0 ? "north" : "south") + ", "
                + Math.Abs(eastM).ToString("0", SlopeInv) + " m " + (eastM >= 0 ? "east" : "west");
        }
    }
}

