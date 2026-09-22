// PolyGeometry.cs
//
// Pure geometry for the placement checks: point-in-polygon, distance to the
// nearest polygon edge, rectangle overlap, and the designator prefix split.
//
// NO ALTIUM TYPES IN THIS FILE. That is deliberate -- it is compiled directly
// by a test harness outside Altium, so the arithmetic that decides whether a
// component is off the board can be checked against known answers rather than
// eyeballed on a screen.
//
// PointInPolygon is the standard crossing count. Two details matter and both
// are easy to get wrong:
//
//   * The half-open vertical test (vy[i] > y) != (vy[j] > y) counts a vertex
//     lying exactly on the ray once, not twice. Written with >= on one side it
//     double-counts and every point level with a vertex flips to the wrong
//     answer.
//   * A point exactly on an edge is not defined either way by crossing count.
//     That is why the caller pairs this with DistanceToEdge and a margin: a
//     corner within the margin of the outline is treated as on the board,
//     whichever side the crossing count lands on.

using System;
using System.Collections.Generic;

namespace AltiumSpike
{
    public static class PolyGeometry
    {
        // Crossing count along a ray heading in +X.
        public static bool PointInPolygon(IList<double> vx, IList<double> vy, double x, double y)
        {
            if (vx == null || vy == null) return false;
            int n = Math.Min(vx.Count, vy.Count);
            if (n < 3) return false;

            bool inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                if ((vy[i] > y) != (vy[j] > y))
                {
                    double t = (y - vy[i]) / (vy[j] - vy[i]);
                    if (x < vx[i] + t * (vx[j] - vx[i])) inside = !inside;
                }
            }
            return inside;
        }

        public static double DistanceToEdge(IList<double> vx, IList<double> vy, double x, double y)
        {
            if (vx == null || vy == null) return 0;
            int n = Math.Min(vx.Count, vy.Count);
            if (n < 2) return 0;

            double best = double.MaxValue;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double d = PointSegmentDistance(x, y, vx[j], vy[j], vx[i], vy[i]);
                if (d < best) best = d;
            }
            return best == double.MaxValue ? 0 : best;
        }

        public static double PointSegmentDistance(double px, double py, double ax, double ay,
                                                  double bx, double by)
        {
            double dx = bx - ax, dy = by - ay;
            double len2 = dx * dx + dy * dy;
            if (len2 < 1e-12) return Math.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));

            double t = ((px - ax) * dx + (py - ay) * dy) / len2;
            if (t < 0) t = 0; else if (t > 1) t = 1;

            double qx = ax + t * dx, qy = ay + t * dy;
            return Math.Sqrt((px - qx) * (px - qx) + (py - qy) * (py - qy));
        }

        // Overlap of two axis-aligned rectangles, grown by clearance on each
        // side. Positive in both axes means they collide. Returns the raw
        // (ungrown) overlap so the caller can tell a real overlap from a
        // near miss.
        public static bool RectOverlap(double ax1, double ay1, double ax2, double ay2,
                                       double bx1, double by1, double bx2, double by2,
                                       double clearance,
                                       out double overlapX, out double overlapY)
        {
            overlapX = Math.Min(ax2, bx2) - Math.Max(ax1, bx1);
            overlapY = Math.Min(ay2, by2) - Math.Max(ay1, by1);
            return (overlapX + clearance) > 0 && (overlapY + clearance) > 0;
        }

        // The letters at the front of a designator: "R12" -> "R", "TP3" -> "TP".
        // A designator with no digits, or one starting with a digit, has no
        // prefix to renumber within and comes back empty.
        public static string DesignatorPrefix(string designator)
        {
            if (string.IsNullOrEmpty(designator)) return "";
            int i = 0;
            while (i < designator.Length && !char.IsDigit(designator[i])) i++;
            if (i == 0 || i == designator.Length) return "";
            return designator.Substring(0, i);
        }

        // Row number for each Y, so a row of 0402s whose origins differ by
        // 20 um is numbered as one row rather than scattered by a raw Y sort.
        //
        // NOT floor(y / band). That was the first attempt and it is wrong: the
        // band boundaries sit at fixed absolute heights, so two parts 20 um
        // apart that happen to straddle one land in different rows -- which is
        // exactly the case this is meant to fix, failing silently on whichever
        // rows the board happens to land on.
        //
        // Instead the parts are sorted and cut wherever the gap to the previous
        // one exceeds the band. Rows then come from the spacing that is there,
        // not from where the origin happens to be. The known cost is chaining:
        // a run of parts each half a band apart joins into one long row. That
        // is the right failure -- a diagonal scatter has no rows to find, and
        // numbering it in one sweep is at least predictable.
        //
        // Returns one row index per input, in the input's own order.
        public static int[] RowIndices(IList<double> ys, double band, bool topFirst)
        {
            if (ys == null) return new int[0];
            int n = ys.Count;
            int[] rows = new int[n];
            if (n == 0) return rows;
            if (band <= 0) return rows;               // every part in one row

            int[] order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;

            // Ascending in "reading order": top first means the largest Y first.
            Array.Sort(order, delegate (int a, int b)
            {
                double ka = topFirst ? -ys[a] : ys[a];
                double kb = topFirst ? -ys[b] : ys[b];
                int c = ka.CompareTo(kb);
                return c != 0 ? c : a.CompareTo(b);
            });

            int row = 0;
            double prev = topFirst ? -ys[order[0]] : ys[order[0]];
            rows[order[0]] = 0;

            for (int i = 1; i < n; i++)
            {
                double k = topFirst ? -ys[order[i]] : ys[order[i]];
                if (k - prev > band) row++;
                rows[order[i]] = row;
                prev = k;
            }
            return rows;
        }
    }
}
