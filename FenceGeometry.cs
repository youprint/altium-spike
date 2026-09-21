// FenceGeometry.cs
//
// The pure geometry behind the via fence: given trace segments, produce the
// list of points where fence vias belong. Deliberately free of any Altium
// type, so it compiles standalone and can be exercised by a test harness
// without a PCB editor in the loop. ViaFence.cs does the board work and
// nothing else.
//
// Splitting it this way is not tidiness for its own sake. The arc stepping
// and the junction filter are where a fence goes quietly wrong -- vias in
// roughly the right place, spacing subtly off -- and that is invisible on
// screen but not in a test that checks the distances.

using System;
using System.Collections.Generic;

namespace AltiumSpike
{
    public sealed class FenceOptions
    {
        public double PitchMM = 1.0;
        public double OffsetMM = 0.5;
        public double ViaDiameterMM = 0.6;
        public double HoleSizeMM = 0.3;
        public string NetName = "GND";
        public bool LeftSide = true;
        public bool RightSide = true;

        // Returns null when the options are usable, otherwise the reason.
        public string Validate()
        {
            if (PitchMM <= 0.0) return "Pitch must be greater than 0.";
            if (OffsetMM < 0.0) return "Offset cannot be negative.";
            if (ViaDiameterMM <= 0.0) return "Via diameter must be greater than 0.";
            if (HoleSizeMM <= 0.0) return "Hole size must be greater than 0.";
            if (HoleSizeMM >= ViaDiameterMM)
                return "Hole size must be smaller than the via diameter.";
            if (!LeftSide && !RightSide) return "Select at least one side to fence.";
            return null;
        }
    }

    public struct FenceCandidate
    {
        public double X;
        public double Y;
        public bool LeftSide;

        public FenceCandidate(double x, double y, bool left) { X = x; Y = y; LeftSide = left; }
    }

    public static class FenceGeometry
    {
        // A mistyped pitch is the dangerous input: "0.01" instead of "1.0" on
        // a 100 mm run asks for 20000 vias, and the junction filter is a
        // linear scan, so the cost is quadratic. Without a cap Altium appears
        // to hang with no way back.
        public const int MaxVias = 5000;

        public sealed class Builder
        {
            private readonly FenceOptions opt;
            private readonly double minSep;
            private readonly List<FenceCandidate> points = new List<FenceCandidate>();

            // Junction de-duplication is PER SIDE. Selecting a polyline gives
            // segments that share endpoints, and each independently wants a
            // via at its start. But with a tight offset the two mirrored vias
            // can sit closer to each other than half the pitch, so a single
            // shared list would silently delete one wall of the fence.
            private readonly List<FenceCandidate> acceptedLeft = new List<FenceCandidate>();
            private readonly List<FenceCandidate> acceptedRight = new List<FenceCandidate>();

            public int SkippedDuplicate;
            public int SkippedInnerArc;
            public bool HitCap;

            public Builder(FenceOptions options)
            {
                opt = options;
                minSep = options.PitchMM * 0.5;
            }

            public IList<FenceCandidate> Points { get { return points; } }

            public void AddTrack(double x1, double y1, double x2, double y2)
            {
                double vx = x2 - x1, vy = y2 - y1;
                double len = Math.Sqrt(vx * vx + vy * vy);

                // A zero-length track is legal in Altium and would divide by zero.
                if (len < 1e-9) return;

                double ux = vx / len, uy = vy / len;   // unit direction
                double nx = -uy, ny = ux;              // unit normal, left of travel

                for (double s = 0.0; s < len - 1e-9; s += opt.PitchMM)
                {
                    if (HitCap) return;
                    double px = x1 + ux * s, py = y1 + uy * s;

                    if (opt.LeftSide)
                        Offer(new FenceCandidate(px + nx * opt.OffsetMM, py + ny * opt.OffsetMM, true));
                    if (opt.RightSide)
                        Offer(new FenceCandidate(px - nx * opt.OffsetMM, py - ny * opt.OffsetMM, false));
                }
            }

            // Angles in degrees, counterclockwise, as Altium stores them.
            public void AddArc(double cx, double cy, double radius, double startDeg, double endDeg)
            {
                if (radius < 1e-9) return;

                // An end angle at or below the start means the arc crosses 0.
                double sweep = endDeg - startDeg;
                while (sweep <= 0.0) sweep += 360.0;

                double sweepRad = sweep * Math.PI / 180.0;
                if (radius * sweepRad < 1e-9) return;

                double theta0 = startDeg * Math.PI / 180.0;

                // EACH SIDE IS STEPPED AT ITS OWN RADIUS.
                //
                // Stepping the centreline at dTheta = pitch/radius and
                // dropping a via on each side at that angle is the obvious
                // implementation and it is wrong. The vias sit at radius +/-
                // offset, so their real spacing comes out as
                // pitch * (radius +/- offset) / radius. On a 5 mm bend with a
                // 0.5 mm offset the outer wall is 10% too open; on a 1 mm bend
                // it is 50% too open -- and the gap between adjacent vias is
                // the entire point of a shield.
                //
                // Stepping each side at its own radius makes "pitch" mean
                // via-to-via spacing everywhere, exactly as it already does on
                // a straight track. The two walls then land at different
                // angles, which is correct rather than untidy.
                if (opt.LeftSide)
                    StepArc(cx, cy, radius + opt.OffsetMM, theta0, sweepRad, true);

                if (opt.RightSide)
                {
                    double ri = radius - opt.OffsetMM;
                    // Past this point the inner wall folds through the centre.
                    // Skip it rather than mirroring it onto the far side of
                    // the arc, which would look like a fence and shield nothing.
                    if (ri <= 1e-9) SkippedInnerArc++;
                    else StepArc(cx, cy, ri, theta0, sweepRad, false);
                }
            }

            private void StepArc(double cx, double cy, double sideRadius,
                                 double theta0, double sweepRad, bool left)
            {
                double dTheta = opt.PitchMM / sideRadius;   // radians per pitch AT THIS RADIUS
                if (dTheta <= 0.0) return;

                for (double th = 0.0; th < sweepRad - 1e-12; th += dTheta)
                {
                    if (HitCap) return;
                    double ang = theta0 + th;
                    Offer(new FenceCandidate(cx + Math.Cos(ang) * sideRadius,
                                             cy + Math.Sin(ang) * sideRadius, left));
                }
            }

            private void Offer(FenceCandidate c)
            {
                if (points.Count >= MaxVias) { HitCap = true; return; }

                List<FenceCandidate> accepted = c.LeftSide ? acceptedLeft : acceptedRight;
                double min2 = minSep * minSep;
                for (int i = 0; i < accepted.Count; i++)
                {
                    double dx = accepted[i].X - c.X;
                    double dy = accepted[i].Y - c.Y;
                    if (dx * dx + dy * dy < min2) { SkippedDuplicate++; return; }
                }

                accepted.Add(c);
                points.Add(c);
            }
        }
    }
}
