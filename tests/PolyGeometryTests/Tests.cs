// Tests.cs -- checks for the placement geometry.
//
// Compiles the PLUGIN'S OWN PolyGeometry.cs (see polytest.csproj), not a copy,
// so these assertions cannot drift away from what ships.
//
// The question these answer is the one the off-board check turns on: is this
// corner inside the outline or not. A crossing-count routine that is subtly
// wrong does not crash or look odd -- it quietly says every component is off
// the board, or none is, and the check becomes noise either way. So the cases
// below are the ones that break a naive implementation: points level with a
// vertex, points exactly on an edge, concave shapes, holes reached by a slot,
// and winding order reversed.

using AltiumSpike;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace PolyTest
{
    static class T
    {
        static int passed, failed;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static void Ok(bool cond, string what)
        {
            if (cond) { passed++; Console.WriteLine("  PASS  " + what); }
            else { failed++; Console.WriteLine("  FAIL  " + what); }
        }

        static void Near(double actual, double expected, double tol, string what)
        {
            if (Math.Abs(actual - expected) <= tol) { passed++; Console.WriteLine("  PASS  " + what); }
            else
            {
                failed++;
                Console.WriteLine("  FAIL  " + what +
                    "  (got " + actual.ToString("0.#####", Inv) +
                    ", expected " + expected.ToString("0.#####", Inv) +
                    " +/- " + tol.ToString("0.#####", Inv) + ")");
            }
        }

        static void Eq(string actual, string expected, string what)
        {
            if (actual == expected) { passed++; Console.WriteLine("  PASS  " + what); }
            else
            {
                failed++;
                Console.WriteLine("  FAIL  " + what + "  (got \"" + actual + "\", expected \"" + expected + "\")");
            }
        }

        static void Section(string s) { Console.WriteLine(); Console.WriteLine("== " + s); }

        // ---- shapes -------------------------------------------------------

        // A plain 60 x 33 rectangle, the shape of the test board.
        static void Rect(out List<double> vx, out List<double> vy)
        {
            vx = new List<double> { 0, 60, 60, 0 };
            vy = new List<double> { 0, 0, 33, 33 };
        }

        // An L: 60 wide, 40 tall, with the top-right 30 x 20 removed.
        static void Ell(out List<double> vx, out List<double> vy)
        {
            vx = new List<double> { 0, 60, 60, 30, 30, 0 };
            vy = new List<double> { 0, 0, 20, 20, 40, 40 };
        }

        static int Main()
        {
            Console.WriteLine("PolyGeometry checks");

            List<double> vx, vy;

            // ---------------------------------------------------------------
            Section("Rectangle: the obvious cases");
            Rect(out vx, out vy);

            Ok(PolyGeometry.PointInPolygon(vx, vy, 30, 16), "centre is inside");
            Ok(!PolyGeometry.PointInPolygon(vx, vy, -1, 16), "left of the board is outside");
            Ok(!PolyGeometry.PointInPolygon(vx, vy, 61, 16), "right of the board is outside");
            Ok(!PolyGeometry.PointInPolygon(vx, vy, 30, -1), "below the board is outside");
            Ok(!PolyGeometry.PointInPolygon(vx, vy, 30, 34), "above the board is outside");
            Ok(PolyGeometry.PointInPolygon(vx, vy, 0.001, 0.001), "just inside the corner is inside");
            Ok(!PolyGeometry.PointInPolygon(vx, vy, -0.001, -0.001), "just outside the corner is outside");

            // ---------------------------------------------------------------
            Section("Rectangle: level with a vertex");
            // The failure mode of a >= on both sides of the vertical test: a
            // point level with a vertex gets counted twice and flips.
            Ok(PolyGeometry.PointInPolygon(vx, vy, 30, 0.0001), "level with the bottom edge, inside");
            Ok(!PolyGeometry.PointInPolygon(vx, vy, -5, 0.0001), "level with the bottom edge, left of it");
            Ok(!PolyGeometry.PointInPolygon(vx, vy, 70, 16), "ray from outside to the right crosses nothing");

            // ---------------------------------------------------------------
            Section("Rectangle: winding order must not matter");
            List<double> rx = new List<double> { 0, 0, 60, 60 };
            List<double> ry = new List<double> { 0, 33, 33, 0 };
            Ok(PolyGeometry.PointInPolygon(rx, ry, 30, 16), "clockwise winding, centre still inside");
            Ok(!PolyGeometry.PointInPolygon(rx, ry, 70, 16), "clockwise winding, outside still outside");

            // ---------------------------------------------------------------
            Section("L-shape: the notch is outside");
            Ell(out vx, out vy);

            Ok(PolyGeometry.PointInPolygon(vx, vy, 10, 30), "left arm, above the step");
            Ok(PolyGeometry.PointInPolygon(vx, vy, 45, 10), "right arm, below the step");
            Ok(!PolyGeometry.PointInPolygon(vx, vy, 45, 30), "the removed corner is OUTSIDE");
            Ok(PolyGeometry.PointInPolygon(vx, vy, 29, 39), "top-left corner of the L is inside");
            Ok(!PolyGeometry.PointInPolygon(vx, vy, 31, 39), "two mm right of it is outside");

            // ---------------------------------------------------------------
            Section("Degenerate input is refused, not guessed");
            Ok(!PolyGeometry.PointInPolygon(new List<double> { 0, 1 }, new List<double> { 0, 1 }, 0.5, 0.5),
               "a two-point outline is not a shape");
            Ok(!PolyGeometry.PointInPolygon(null, null, 0, 0), "null outline is outside, not a crash");
            Near(PolyGeometry.DistanceToEdge(null, null, 0, 0), 0, 1e-9, "null outline gives distance 0");

            // ---------------------------------------------------------------
            Section("Distance to the nearest edge");
            Rect(out vx, out vy);

            Near(PolyGeometry.DistanceToEdge(vx, vy, 30, 16.5), 16.5, 1e-9, "centre is half the height from an edge");
            Near(PolyGeometry.DistanceToEdge(vx, vy, 30, 0), 0, 1e-9, "a point on the bottom edge is 0 away");
            Near(PolyGeometry.DistanceToEdge(vx, vy, -2, 16), 2, 1e-9, "2 mm left of the left edge");
            Near(PolyGeometry.DistanceToEdge(vx, vy, 63, 37), 5, 1e-9, "3,4 past the top-right corner is 5 away");
            Near(PolyGeometry.DistanceToEdge(vx, vy, 30, 34), 1, 1e-9, "1 mm above the top edge");

            // This is the pair the off-board check relies on: a corner sitting
            // exactly on the outline must not be reported, whichever way the
            // crossing count falls, because the distance is 0 and 0 <= margin.
            Section("On-edge points are settled by the margin, not by the count");
            double margin = 0.05;
            double d = PolyGeometry.DistanceToEdge(vx, vy, 0, 16);
            Ok(d <= margin, "a corner exactly on the edge is within the margin (" +
                            d.ToString("0.#####", Inv) + " <= " + margin.ToString("0.###", Inv) + ")");
            d = PolyGeometry.DistanceToEdge(vx, vy, -0.2, 16);
            Ok(d > margin, "0.2 mm over the edge is beyond the margin (" + d.ToString("0.###", Inv) + ")");

            // ---------------------------------------------------------------
            Section("Point to segment");
            Near(PolyGeometry.PointSegmentDistance(5, 5, 0, 0, 10, 0), 5, 1e-9, "perpendicular to the middle");
            Near(PolyGeometry.PointSegmentDistance(-5, 0, 0, 0, 10, 0), 5, 1e-9, "past the start clamps to the start");
            Near(PolyGeometry.PointSegmentDistance(15, 0, 0, 0, 10, 0), 5, 1e-9, "past the end clamps to the end");
            Near(PolyGeometry.PointSegmentDistance(3, 4, 0, 0, 0, 0), 5, 1e-9, "zero-length segment is a point");
            Near(PolyGeometry.PointSegmentDistance(0, 0, 0, 0, 10, 10), 0, 1e-9, "on the segment is 0");

            // ---------------------------------------------------------------
            Section("Rectangle overlap");
            double ox, oy;

            Ok(PolyGeometry.RectOverlap(0, 0, 10, 10, 5, 5, 15, 15, 0, out ox, out oy),
               "overlapping squares collide");
            Near(ox, 5, 1e-9, "overlap in X is 5");
            Near(oy, 5, 1e-9, "overlap in Y is 5");

            Ok(!PolyGeometry.RectOverlap(0, 0, 10, 10, 11, 0, 20, 10, 0, out ox, out oy),
               "1 mm apart with no clearance does not collide");
            Near(ox, -1, 1e-9, "the gap is reported as a negative overlap");

            Ok(PolyGeometry.RectOverlap(0, 0, 10, 10, 11, 0, 20, 10, 2, out ox, out oy),
               "the same pair collides once 2 mm of clearance is demanded");
            Near(ox, -1, 1e-9, "and the raw gap is still reported, not the grown one");

            Ok(!PolyGeometry.RectOverlap(0, 0, 10, 10, 20, 20, 30, 30, 0, out ox, out oy),
               "diagonal neighbours do not collide");
            Ok(!PolyGeometry.RectOverlap(0, 0, 10, 10, 10, 10, 20, 20, 0, out ox, out oy),
               "touching at one corner is not an overlap");

            // ---------------------------------------------------------------
            Section("Designator prefix");
            Eq(PolyGeometry.DesignatorPrefix("R12"), "R", "R12 -> R");
            Eq(PolyGeometry.DesignatorPrefix("TP3"), "TP", "TP3 -> TP");
            Eq(PolyGeometry.DesignatorPrefix("U1"), "U", "U1 -> U");
            Eq(PolyGeometry.DesignatorPrefix("HOLE1"), "HOLE", "HOLE1 -> HOLE");
            Eq(PolyGeometry.DesignatorPrefix("R"), "", "a designator with no number has no prefix");
            Eq(PolyGeometry.DesignatorPrefix("123"), "", "a designator with no letters has no prefix");
            Eq(PolyGeometry.DesignatorPrefix(""), "", "empty is empty");
            Eq(PolyGeometry.DesignatorPrefix(null), "", "null is empty, not a crash");
            Eq(PolyGeometry.DesignatorPrefix("C1A"), "C", "trailing letters stay out of the prefix");

            // ---------------------------------------------------------------
            Section("Row clustering");
            // The case fixed banding gets wrong: two parts 20 um apart that
            // straddle a fixed boundary. Rows must come from the spacing.
            {
                double[] ys = new double[] { 20.000, 20.020 };
                int[] rows = PolyGeometry.RowIndices(ys, 5, true);
                Ok(rows[0] == rows[1], "20 um apart is the same row, whatever the absolute height");
            }
            {
                // Three rows of three, 10 mm apart, each row jittered by 20 um.
                double[] ys = new double[] { 30.00, 30.02, 29.98, 20.00, 20.02, 19.98, 10.00, 10.02, 9.98 };
                int[] rows = PolyGeometry.RowIndices(ys, 5, true);
                Ok(rows[0] == rows[1] && rows[1] == rows[2], "the top three are one row");
                Ok(rows[3] == rows[4] && rows[4] == rows[5], "the middle three are one row");
                Ok(rows[6] == rows[7] && rows[7] == rows[8], "the bottom three are one row");
                Ok(rows[0] < rows[3] && rows[3] < rows[6], "top first: rows number downward");

                int[] up = PolyGeometry.RowIndices(ys, 5, false);
                Ok(up[0] > up[3] && up[3] > up[6], "bottom first: the order reverses");
            }
            {
                double[] ys = new double[] { 0, 6, 12 };
                int[] rows = PolyGeometry.RowIndices(ys, 5, true);
                Ok(rows[0] != rows[1] && rows[1] != rows[2], "gaps wider than the band are separate rows");
            }
            {
                double[] ys = new double[] { 0, 3, 6, 9 };
                int[] rows = PolyGeometry.RowIndices(ys, 5, true);
                Ok(rows[0] == rows[3], "known cost: a chain of sub-band gaps joins into one row");
            }
            {
                double[] ys = new double[] { 7, 1, 4 };
                int[] rows = PolyGeometry.RowIndices(ys, 0, true);
                Ok(rows[0] == 0 && rows[1] == 0 && rows[2] == 0, "a zero band puts everything in one row");
                Ok(PolyGeometry.RowIndices(null, 5, true).Length == 0, "null input is empty, not a crash");
                Ok(PolyGeometry.RowIndices(new double[0], 5, true).Length == 0, "empty input is empty");
                Ok(PolyGeometry.RowIndices(new double[] { 42 }, 5, true)[0] == 0, "one part is row 0");
            }

            // ---------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("passed " + passed + ", failed " + failed);
            return failed == 0 ? 0 : 1;
        }
    }
}
