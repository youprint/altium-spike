// FilletGeometry.cs
//
// The maths for rounding the corner between two straight segments that meet
// at a point, and for distributing and scaling a set of points.
//
// Free of any Altium type so it compiles standalone and is unit-tested.
// Geometry.cs does the board work and nothing else.
//
// THE FILLET. Two segments leave a shared corner P along unit directions u1
// and u2, with the interior half-angle a between the bisector and each
// segment. For a fillet of radius R:
//
//     tangent distance   t = R / tan(a)      along each segment from P
//     centre distance    d = R / sin(a)      along the bisector from P
//
// so the arc touches each segment at P + u*t and is centred at P + bisector*d.
// Both segments are then shortened to their tangent point and the arc is
// dropped in between.
//
// The two cases that ruin a fillet routine, both handled below:
//
//   COLLINEAR OR NEARLY SO. As the angle between the segments approaches 180
//   degrees, tan(a) goes to infinity is wrong -- a goes to 90 degrees, tan(a)
//   goes to infinity, t goes to zero, and the fillet is a no-op. As the angle
//   approaches 0 (a doubled-back segment), tan(a) goes to zero and t explodes
//   past any real geometry. Both are rejected rather than producing a
//   degenerate arc.
//
//   TANGENT LONGER THAN THE SEGMENT. A large radius on a short segment wants
//   to cut back further than the segment exists, which would invert it. The
//   caller is told the largest radius that fits so it can clamp rather than
//   silently skipping the corner.

using System;

namespace AltiumSpike
{
    public struct Vec
    {
        public double X, Y;
        public Vec(double x, double y) { X = x; Y = y; }

        public double Length { get { return Math.Sqrt(X * X + Y * Y); } }

        public Vec Unit()
        {
            double l = Length;
            if (l < 1e-12) return new Vec(0, 0);
            return new Vec(X / l, Y / l);
        }

        public static Vec operator -(Vec a, Vec b) { return new Vec(a.X - b.X, a.Y - b.Y); }
        public static Vec operator +(Vec a, Vec b) { return new Vec(a.X + b.X, a.Y + b.Y); }
        public static Vec operator *(Vec a, double s) { return new Vec(a.X * s, a.Y * s); }

        public static double Dot(Vec a, Vec b) { return a.X * b.X + a.Y * b.Y; }
        public static double Cross(Vec a, Vec b) { return a.X * b.Y - a.Y * b.X; }
        public static double Dist(Vec a, Vec b) { return (a - b).Length; }
    }

    public sealed class Fillet
    {
        public bool Ok;
        public string Why = "";

        public Vec Tangent1;      // where the arc meets the first segment
        public Vec Tangent2;      // where it meets the second
        public Vec Centre;
        public double Radius;
        public double StartAngleDeg;   // counterclockwise, as Altium stores arcs
        public double EndAngleDeg;
        public double MaxRadius;       // the largest radius that would fit here
    }

    public static class FilletGeometry
    {
        // Angles closer to straight or to doubled-back than this are refused.
        public const double MinAngleDeg = 1.0;
        public const double MaxAngleDeg = 179.0;

        /// corner is the shared point; end1 and end2 are the FAR ends of the
        /// two segments. Returns a Fillet describing the arc, or Ok=false with
        /// a reason.
        public static Fillet Corner(Vec corner, Vec end1, Vec end2, double radius)
        {
            Fillet f = new Fillet();
            f.Radius = radius;

            if (radius <= 0.0) { f.Why = "Radius must be greater than 0."; return f; }

            Vec v1 = end1 - corner;
            Vec v2 = end2 - corner;
            double len1 = v1.Length, len2 = v2.Length;

            if (len1 < 1e-9 || len2 < 1e-9) { f.Why = "One segment has no length."; return f; }

            Vec u1 = v1.Unit();
            Vec u2 = v2.Unit();

            double cos = Math.Max(-1.0, Math.Min(1.0, Vec.Dot(u1, u2)));
            double angleDeg = Math.Acos(cos) * 180.0 / Math.PI;

            if (angleDeg < MinAngleDeg)
            { f.Why = "The segments double back on each other."; return f; }
            if (angleDeg > MaxAngleDeg)
            { f.Why = "The segments are collinear, so there is no corner."; return f; }

            double aRad = (angleDeg / 2.0) * Math.PI / 180.0;
            double tanA = Math.Tan(aRad);
            double sinA = Math.Sin(aRad);
            if (tanA < 1e-12 || sinA < 1e-12)
            { f.Why = "Degenerate corner."; return f; }

            double t = radius / tanA;

            // The largest radius that fits is set by the shorter segment.
            f.MaxRadius = Math.Min(len1, len2) * tanA;

            if (t > len1 - 1e-9 || t > len2 - 1e-9)
            {
                f.Why = "Radius too large for these segments; the largest that fits is " +
                        f.MaxRadius.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ".";
                return f;
            }

            f.Tangent1 = corner + u1 * t;
            f.Tangent2 = corner + u2 * t;

            Vec bisector = (u1 + u2).Unit();
            if (bisector.Length < 1e-12) { f.Why = "Degenerate corner."; return f; }

            f.Centre = corner + bisector * (radius / sinA);

            double a1 = Math.Atan2(f.Tangent1.Y - f.Centre.Y, f.Tangent1.X - f.Centre.X) * 180.0 / Math.PI;
            double a2 = Math.Atan2(f.Tangent2.Y - f.Centre.Y, f.Tangent2.X - f.Centre.X) * 180.0 / Math.PI;
            if (a1 < 0) a1 += 360.0;
            if (a2 < 0) a2 += 360.0;

            // Altium sweeps counterclockwise from start to end. Take whichever
            // ordering gives the MINOR arc -- the major one would sweep the
            // long way round the circle and cross the segments.
            double ccw = a2 - a1;
            while (ccw < 0) ccw += 360.0;

            if (ccw <= 180.0) { f.StartAngleDeg = a1; f.EndAngleDeg = a2; }
            else { f.StartAngleDeg = a2; f.EndAngleDeg = a1; }

            f.Ok = true;
            return f;
        }

        /// Evenly spaces values between the first and last, keeping both ends
        /// fixed. Returns the new positions in the same order as the input.
        ///
        /// Sorting is the caller's job: "distribute" on an unsorted set would
        /// otherwise shuffle objects past each other, which is never what
        /// anyone means by it.
        public static double[] Distribute(double[] sortedPositions)
        {
            if (sortedPositions == null || sortedPositions.Length < 3) return sortedPositions;

            int n = sortedPositions.Length;
            double first = sortedPositions[0];
            double last = sortedPositions[n - 1];
            double step = (last - first) / (n - 1);

            double[] outp = new double[n];
            for (int i = 0; i < n; i++) outp[i] = first + step * i;
            return outp;
        }

        /// Scales a point about an anchor.
        public static Vec Scale(Vec p, Vec anchor, double factor)
        {
            return new Vec(anchor.X + (p.X - anchor.X) * factor,
                           anchor.Y + (p.Y - anchor.Y) * factor);
        }
    }
}
