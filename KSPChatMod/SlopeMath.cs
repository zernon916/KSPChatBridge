using System;

namespace KSPChatBridge
{
    /// <summary>Pure landing-site slope maths (L3): a least-squares plane over a terrain height grid gives the local
    /// slope, and when the site is too steep the flattest nearby cell gives a sideways nudge. Grid rows run south to
    /// north and columns west to east (the TerrainGrid convention); spacing and heights are metres, heights may be
    /// negative (below sea level) or NaN ("no data"). Doubles only - no KSP or Unity types - so it is testable offline.</summary>
    internal static class SlopeMath
    {
        /// <summary>Steepest slope a lander's legs are planned for (deg).</summary>
        internal const double DefaultMaxSlopeDeg = 8;
        /// <summary>Samples per side of the square grid (odd: the centre cell is the landing spot itself).</summary>
        internal const int DefaultGrid = 5;
        /// <summary>Metres between neighbouring samples.</summary>
        internal const double DefaultSpacingM = 10;
        /// <summary>A nudge has to buy at least this much slope reduction, else the craft stays where it is.</summary>
        internal const double NudgeGainDeg = .5;

        internal static bool Finite(double d) { return !double.IsNaN(d) && !double.IsInfinity(d); }

        /// <summary>True for a finite latitude/longitude pair inside the usual ranges.</summary>
        internal static bool SaneLatLon(double lat, double lon)
        {
            return Finite(lat) && Finite(lon) && lat >= -90 && lat <= 90 && lon >= -180 && lon < 180;
        }

        /// <summary>Slope (deg) of the least-squares plane over the whole grid; NaN when no plane can be fitted.</summary>
        internal static double PlaneSlopeDeg(double[,] heights, double spacingM)
        {
            double eastGrad, northGrad;
            if (heights == null || !Fit(heights, spacingM, 0, heights.GetLength(0), 0, heights.GetLength(1), out eastGrad, out northGrad)) return double.NaN;
            return GradientDeg(eastGrad, northGrad);
        }

        /// <summary>Slope (deg) of the plane fitted over the 3x3 cells around one cell. The window clamps at the grid
        /// edge, so corner and edge cells are candidates too. NaN when that window cannot be fitted.</summary>
        internal static double CellSlopeDeg(double[,] heights, double spacingM, int row, int col)
        {
            if (heights == null) return double.NaN;
            double eastGrad, northGrad;
            if (!Fit(heights, spacingM, row - 1, row + 2, col - 1, col + 2, out eastGrad, out northGrad)) return double.NaN;
            return GradientDeg(eastGrad, northGrad);
        }

        /// <summary>Site slope plus, when it exceeds maxSlopeDeg, the horizontal offset (east/north metres) of the
        /// flattest nearby cell. Returns true only when a nudge is advised; slopeDeg is always the whole-grid slope
        /// (NaN when the terrain is unreadable) and both offsets stay 0 when no nudge is advised.</summary>
        internal static bool Nudge(double[,] heights, double spacingM, double maxSlopeDeg, out double slopeDeg, out double eastM, out double northM)
        {
            slopeDeg = PlaneSlopeDeg(heights, spacingM);
            eastM = northM = 0;
            if (!Finite(slopeDeg) || !(spacingM > 0) || slopeDeg <= maxSlopeDeg) return false;   // flat enough, or unreadable
            int rows = heights.GetLength(0), cols = heights.GetLength(1);
            int centreRow = rows / 2, centreCol = cols / 2;
            double best = CellSlopeDeg(heights, spacingM, centreRow, centreCol);   // start from staying put
            int bestRow = centreRow, bestCol = centreCol; double bestDist = 0;
            if (!Finite(best)) best = double.PositiveInfinity;   // unreadable centre: any readable cell may win
            for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                double local = CellSlopeDeg(heights, spacingM, r, c);
                if (!Finite(local)) continue;
                double dist = Math.Abs(r - centreRow) + Math.Abs(c - centreCol);   // cells, for ties only
                // A clearly flatter cell wins; equally flat cells, the nearer one (less manoeuvring, centre keeps ties).
                if (local < best - 1e-9 || (local <= best + 1e-9 && dist < bestDist)) { best = local; bestRow = r; bestCol = c; bestDist = dist; }
            }
            if (bestRow == centreRow && bestCol == centreCol) return false;   // nowhere flatter than here
            if (!(best <= slopeDeg - NudgeGainDeg)) return false;             // not worth moving for
            eastM = (bestCol - centreCol) * spacingM;
            northM = (bestRow - centreRow) * spacingM;
            return Finite(eastM) && Finite(northM);
        }

        /// <summary>Same with the default slope limit.</summary>
        internal static bool Nudge(double[,] heights, double spacingM, out double slopeDeg, out double eastM, out double northM)
        {
            return Nudge(heights, spacingM, DefaultMaxSlopeDeg, out slopeDeg, out eastM, out northM);
        }

        /// <summary>Move a lat/lon by an east/north offset in metres on a sphere (small-offset equirectangular: exact
        /// enough for a landing nudge). Longitude wraps to [-180,180); both outputs are NaN when the input is bad.</summary>
        internal static void OffsetLatLon(double lat, double lon, double eastM, double northM, double radius, out double newLat, out double newLon)
        {
            newLat = double.NaN; newLon = double.NaN;
            if (!(radius > 0) || !Finite(lat) || !Finite(lon) || !Finite(eastM) || !Finite(northM)) return;
            double dLat = northM / radius * 180 / Math.PI;
            double cosLat = Math.Cos(lat * Math.PI / 180);
            double dLon = Math.Abs(cosLat) > 1e-6 ? eastM / (radius * cosLat) * 180 / Math.PI : 0;   // at a pole: east is meaningless
            newLat = Math.Max(-90, Math.Min(90, lat + dLat));
            double wrapped = lon + dLon;
            while (wrapped >= 180) wrapped -= 360;
            while (wrapped < -180) wrapped += 360;
            newLon = wrapped;
        }

        /// <summary>Least-squares plane z = a + b*x + c*y over rows [row0,row1) and columns [col0,col1), with x east
        /// and y north in metres. NaN samples are skipped; at least 4 finite samples spanning both axes are needed.
        /// b and c come back as the rise per metre east / north.</summary>
        static bool Fit(double[,] heights, double spacingM, int row0, int row1, int col0, int col1, out double eastGrad, out double northGrad)
        {
            eastGrad = northGrad = double.NaN;
            if (heights == null || !(spacingM > 0)) return false;
            int rows = heights.GetLength(0), cols = heights.GetLength(1);
            int r0 = Math.Max(0, row0), c0 = Math.Max(0, col0), r1 = Math.Min(rows, row1), c1 = Math.Min(cols, col1);
            if (r1 - r0 < 2 || c1 - c0 < 2) return false;
            int count = 0; double sumX = 0, sumY = 0, sumZ = 0;
            for (int r = r0; r < r1; r++)
            for (int c = c0; c < c1; c++)
            {
                double z = heights[r, c];
                if (!Finite(z)) continue;
                count++; sumX += c * spacingM; sumY += r * spacingM; sumZ += z;
            }
            if (count < 4) return false;
            double meanX = sumX / count, meanY = sumY / count, meanZ = sumZ / count;
            double sxx = 0, syy = 0, sxy = 0, sxz = 0, syz = 0;
            for (int r = r0; r < r1; r++)
            for (int c = c0; c < c1; c++)
            {
                double z = heights[r, c];
                if (!Finite(z)) continue;
                double dx = c * spacingM - meanX, dy = r * spacingM - meanY, dz = z - meanZ;
                sxx += dx * dx; syy += dy * dy; sxy += dx * dy; sxz += dx * dz; syz += dy * dz;
            }
            double det = sxx * syy - sxy * sxy;
            if (!(Math.Abs(det) > 1e-9)) return false;   // every sample on one line: no unique plane
            eastGrad = (syy * sxz - sxy * syz) / det;
            northGrad = (sxx * syz - sxy * sxz) / det;
            return Finite(eastGrad) && Finite(northGrad);
        }

        /// <summary>Slope in degrees from the east/north gradients (rise per metre).</summary>
        static double GradientDeg(double eastGrad, double northGrad)
        {
            return Math.Atan(Math.Sqrt(eastGrad * eastGrad + northGrad * northGrad)) * 180 / Math.PI;
        }
    }
}

