using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KSPChatBridge
{
    /// <summary>Pure grid maths + JSON for terrain_kerbin.json (tested without KSP).</summary>
    internal static class TerrainGrid
    {
        internal sealed class Site { internal string Name; internal double Lat, Lon; internal int N; internal double DLat, DLon; internal int[] H; }
        internal const double HalfM = 35000, StepM = 400, Radius = 600000;
        internal static readonly string[] Names = { "KSC", "Island" };
        internal static readonly double[,] Centers = { { -0.0494, -74.6074 }, { -1.5155, -71.9093 } };
        internal static Site Make(string name, double lat, double lon, double halfM = HalfM, double stepM = StepM, double radius = Radius)
        {
            int n = (int)Math.Round(2 * halfM / stepM) + 1;
            double dLat = stepM / radius * 180 / Math.PI, dLon = dLat / Math.Cos(lat * Math.PI / 180);
            return new Site { Name = name, N = n, DLat = dLat, DLon = dLon, Lat = lat - dLat * (n - 1) / 2, Lon = lon - dLon * (n - 1) / 2, H = new int[n * n] };
        }
        /// <summary>Row r (south->north), column c (west->east).</summary>
        internal static void Point(Site s, int i, out double lat, out double lon) { lat = s.Lat + (i / s.N) * s.DLat; lon = s.Lon + (i % s.N) * s.DLon; }
        internal static string Json(List<Site> sites, string body)
        {
            var ci = CultureInfo.InvariantCulture; var sb = new StringBuilder();
            sb.Append("{\"body\":\"").Append(body).Append("\",\"ref\":\"msl, ocean 0\",\"sites\":[");
            for (int k = 0; k < sites.Count; k++)
            {
                var s = sites[k]; if (k > 0) sb.Append(',');
                sb.AppendFormat(ci, "{{\"name\":\"{0}\",\"lat0\":{1:F6},\"lon0\":{2:F6},\"dlat\":{3:F7},\"dlon\":{4:F7},\"n\":{5},\"h\":[", s.Name, s.Lat, s.Lon, s.DLat, s.DLon, s.N);
                for (int i = 0; i < s.H.Length; i++) { if (i > 0) sb.Append(','); sb.Append(s.H[i].ToString(ci)); }
                sb.Append("]}");
            }
            return sb.Append("]}").ToString();
        }
    }

}
