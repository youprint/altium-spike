// Tests.cs -- geometry checks for the via fence.
//
// Compiles the PLUGIN'S OWN FenceGeometry.cs (see fencetest.csproj), not a
// copy of it, so these assertions cannot drift away from what ships.
//
// What is worth testing here is spacing, not existence. A fence with vias in
// roughly the right place but the wrong gap looks perfect on screen and does
// not shield; that is the failure these checks are aimed at.

using AltiumSpike;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace FenceTest
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
            bool ok = Math.Abs(actual - expected) <= tol;
            if (ok) { passed++; Console.WriteLine("  PASS  " + what); }
            else
            {
                failed++;
                Console.WriteLine("  FAIL  " + what +
                    "  (got " + actual.ToString("0.#####", Inv) +
                    ", expected " + expected.ToString("0.#####", Inv) +
                    " +/- " + tol.ToString("0.#####", Inv) + ")");
            }
        }

        static FenceOptions Opt(double pitch, double offset, bool left, bool right)
        {
            FenceOptions o = new FenceOptions();
            o.PitchMM = pitch; o.OffsetMM = offset;
            o.LeftSide = left; o.RightSide = right;
            return o;
        }

        static List<FenceCandidate> Side(IList<FenceCandidate> pts, bool left)
        {
            List<FenceCandidate> o = new List<FenceCandidate>();
            foreach (FenceCandidate c in pts) if (c.LeftSide == left) o.Add(c);
            return o;
        }

        static double Dist(FenceCandidate a, FenceCandidate b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // Smallest gap between any two vias on the same wall. This is the
        // number that decides whether a fence is a fence.
        static double MinGap(List<FenceCandidate> s)
        {
            double m = double.MaxValue;
            for (int i = 0; i < s.Count; i++)
                for (int j = i + 1; j < s.Count; j++)
                    m = Math.Min(m, Dist(s[i], s[j]));
            return s.Count < 2 ? double.NaN : m;
        }

        static void Main()
        {
            Console.WriteLine("via fence geometry");
            Console.WriteLine();

            // ---------------------------------------------------------------
            Console.WriteLine("1. straight track, both walls");
            {
                FenceGeometry.Builder b = new FenceGeometry.Builder(Opt(2.0, 0.5, true, true));
                b.AddTrack(0, 0, 10, 0);
                List<FenceCandidate> L = Side(b.Points, true), R = Side(b.Points, false);

                Ok(L.Count == 5, "5 vias on the left wall (s = 0,2,4,6,8)");
                Ok(R.Count == 5, "5 vias on the right wall");

                bool yL = true, yR = true;
                foreach (FenceCandidate c in L) if (Math.Abs(c.Y - 0.5) > 1e-9) yL = false;
                foreach (FenceCandidate c in R) if (Math.Abs(c.Y + 0.5) > 1e-9) yR = false;
                Ok(yL, "left wall sits at +offset");
                Ok(yR, "right wall sits at -offset");

                Near(L[1].X - L[0].X, 2.0, 1e-9, "spacing along the wall equals the pitch");
                Near(L[4].X, 8.0, 1e-9, "last via before the end of the segment");
                Ok(b.SkippedDuplicate == 0, "nothing merged on a single segment");
                Ok(!b.HitCap, "cap not reached");
            }

            // ---------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("2. diagonal track -- offset is perpendicular distance");
            {
                FenceGeometry.Builder b = new FenceGeometry.Builder(Opt(1.0, 0.4, true, true));
                b.AddTrack(0, 0, 7, 7);

                // distance from the point to the infinite line y = x
                double worst = 0;
                foreach (FenceCandidate c in b.Points)
                    worst = Math.Max(worst, Math.Abs(Math.Abs(c.X - c.Y) / Math.Sqrt(2.0) - 0.4));
                Near(worst, 0.0, 1e-9, "every via is exactly the offset from the centreline");

                List<FenceCandidate> L = Side(b.Points, true);
                Near(Dist(L[0], L[1]), 1.0, 1e-9, "spacing along a diagonal still equals the pitch");
            }

            // ---------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("3. arc -- the spacing bug this refactor fixes");
            {
                // Quarter circle, radius 5, centred on the origin.
                // Stepping the CENTRELINE at pitch/radius would put the outer
                // wall at 1.0 * 5.5/5 = 1.1 mm spacing: 10% too open.
                FenceGeometry.Builder b = new FenceGeometry.Builder(Opt(1.0, 0.5, true, true));
                b.AddArc(0, 0, 5.0, 0.0, 90.0);

                List<FenceCandidate> L = Side(b.Points, true), R = Side(b.Points, false);
                Ok(L.Count > 0 && R.Count > 0, "both walls produced vias");

                double rOut = Math.Sqrt(L[0].X * L[0].X + L[0].Y * L[0].Y);
                double rIn = Math.Sqrt(R[0].X * R[0].X + R[0].Y * R[0].Y);
                Near(rOut, 5.5, 1e-9, "outer wall at radius + offset");
                Near(rIn, 4.5, 1e-9, "inner wall at radius - offset");

                // Chord is marginally under the arc step; 1% covers it.
                Near(Dist(L[0], L[1]), 1.0, 0.01, "OUTER wall spacing equals the pitch, not 1.1");
                Near(Dist(R[0], R[1]), 1.0, 0.01, "INNER wall spacing equals the pitch, not 0.9");

                // The tight-bend case, where the centreline bug is worst:
                // radius 1, offset 0.5 would give 1.5x spacing outside.
                FenceGeometry.Builder t = new FenceGeometry.Builder(Opt(0.5, 0.5, true, false));
                t.AddArc(0, 0, 1.0, 0.0, 90.0);
                List<FenceCandidate> TL = Side(t.Points, true);
                Near(Dist(TL[0], TL[1]), 0.5, 0.01, "tight bend: outer spacing still equals the pitch");
            }

            // ---------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("4. arc -- inner wall folds through the centre");
            {
                FenceGeometry.Builder b = new FenceGeometry.Builder(Opt(0.5, 1.0, true, true));
                b.AddArc(0, 0, 0.8, 0.0, 90.0);   // offset 1.0 > radius 0.8

                Ok(b.SkippedInnerArc == 1, "inner wall reported as skipped");
                Ok(Side(b.Points, false).Count == 0, "no inner vias emitted");
                Ok(Side(b.Points, true).Count > 0, "outer wall still placed");
            }

            // ---------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("5. polyline junction -- no doubled via where segments meet");
            {
                // Segment length is deliberately NOT a multiple of the pitch,
                // which is the case that actually crowds a junction: the
                // first run ends at x=4 and the second starts at x=4.5, half
                // a pitch apart.
                FenceGeometry.Builder b = new FenceGeometry.Builder(Opt(2.0, 0.5, true, true));
                b.AddTrack(0, 0, 4.5, 0);     // wants s = 0,2,4
                b.AddTrack(4.5, 0, 9, 0);     // wants x = 4.5,6.5,8.5

                List<FenceCandidate> L = Side(b.Points, true);
                Ok(b.SkippedDuplicate > 0, "crowded junction candidates were merged");
                Ok(MinGap(L) >= 1.0 - 1e-9, "no two vias on a wall closer than half the pitch");

                // An exact multiple leaves the junction exactly half a pitch
                // apart, which is tighter than nominal but not a duplicate --
                // and a tighter fence is the safe direction to err in.
                FenceGeometry.Builder e = new FenceGeometry.Builder(Opt(2.0, 0.5, true, false));
                e.AddTrack(0, 0, 5, 0);
                e.AddTrack(5, 0, 10, 0);
                Ok(e.SkippedDuplicate == 0, "exact-multiple junction keeps both vias");
                Near(MinGap(Side(e.Points, true)), 1.0, 1e-9, "and they sit half a pitch apart");
            }

            // ---------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("5b. overlapping selection --true duplicates are dropped");
            {
                // Selecting overlapping routing (or the same run twice) must
                // not stack two vias in one hole.
                FenceGeometry.Builder b = new FenceGeometry.Builder(Opt(2.0, 0.5, true, true));
                b.AddTrack(0, 0, 10, 0);
                b.AddTrack(0, 0, 6, 0);      // every candidate coincides exactly

                List<FenceCandidate> L = Side(b.Points, true);
                Ok(b.SkippedDuplicate == 6, "all 6 coincident candidates dropped (3 per wall)");
                Ok(L.Count == 5, "left wall still has exactly the 5 it should");
                Ok(MinGap(L) >= 1.0 - 1e-9, "no stacked vias");
            }

            // ---------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("6. tight offset -- the two walls must NOT cancel each other");
            {
                // offset 0.2 puts the mirrored pair 0.4 apart, inside the
                // 0.5 merge radius. A single shared accepted-list would delete
                // one whole wall here.
                FenceGeometry.Builder b = new FenceGeometry.Builder(Opt(1.0, 0.2, true, true));
                b.AddTrack(0, 0, 5, 0);

                List<FenceCandidate> L = Side(b.Points, true), R = Side(b.Points, false);
                Ok(L.Count == 5 && R.Count == 5, "both walls survive a sub-merge-radius offset");
                Near(Dist(L[0], R[0]), 0.4, 1e-9, "mirrored pair really is inside the merge radius");
            }

            // ---------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("7. mistyped pitch is capped, not fatal");
            {
                FenceGeometry.Builder b = new FenceGeometry.Builder(Opt(0.001, 0.5, true, true));
                b.AddTrack(0, 0, 100, 0);   // would be 200000 vias uncapped

                Ok(b.HitCap, "cap reported");
                Ok(b.Points.Count <= FenceGeometry.MaxVias,
                   "no more than " + FenceGeometry.MaxVias + " vias generated");
            }

            // ---------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("8. option validation");
            {
                FenceOptions o = Opt(1.0, 0.5, true, true);
                Ok(o.Validate() == null, "sane options accepted");

                o = Opt(0.0, 0.5, true, true);
                Ok(o.Validate() != null, "zero pitch rejected");

                o = Opt(1.0, 0.5, false, false);
                Ok(o.Validate() != null, "no sides selected rejected");

                o = Opt(1.0, 0.5, true, true); o.HoleSizeMM = 0.6; o.ViaDiameterMM = 0.6;
                Ok(o.Validate() != null, "hole >= diameter rejected");
            }

            // ---------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("9. degenerate input does not throw");
            {
                FenceGeometry.Builder b = new FenceGeometry.Builder(Opt(1.0, 0.5, true, true));
                b.AddTrack(3, 3, 3, 3);        // zero length
                b.AddArc(0, 0, 0.0, 0, 90);    // zero radius
                b.AddArc(0, 0, 5.0, 90, 90);   // zero sweep -> full turn
                Ok(true, "zero-length track, zero radius and zero sweep all survived");
            }

            Console.WriteLine();
            Console.WriteLine("passed " + passed + ", failed " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }
    }
}
