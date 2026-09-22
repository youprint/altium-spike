// Tests.cs -- fillet, distribute and scale geometry.
//
// Compiles the plugin's own FilletGeometry.cs, not a copy.
//
// A fillet that is slightly wrong looks right. The arc lands near the corner,
// the tracks meet it near enough, and the error shows up as a hairline break
// in copper that DRC may or may not catch. So these assertions check the
// things the eye cannot: that the tangent points are exactly the radius from
// the centre, that the arc taken is the minor one, and that a radius which
// does not fit is refused rather than inverting a track.

using AltiumSpike;
using System;
using System.Globalization;

namespace FilletTest
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
                Console.WriteLine("  FAIL  " + what + "  (got " + actual.ToString("0.######", Inv) +
                                  ", expected " + expected.ToString("0.######", Inv) + ")");
            }
        }

        static Vec V(double x, double y) { return new Vec(x, y); }

        static void Main()
        {
            Console.WriteLine("fillet geometry");
            Console.WriteLine();

            // ---------------------------------------------------------------
            Console.WriteLine("1. a right-angled corner");
            {
                // Corner at the origin, one leg along +X, one along +Y.
                Fillet f = FilletGeometry.Corner(V(0, 0), V(10, 0), V(0, 10), 1.0);
                Ok(f.Ok, "a 90 degree corner fillets");

                // half-angle 45 deg: t = R/tan(45) = R
                Near(f.Tangent1.X, 1.0, 1e-9, "tangent on the X leg is R from the corner");
                Near(f.Tangent1.Y, 0.0, 1e-9, "and still on that leg");
                Near(f.Tangent2.X, 0.0, 1e-9, "tangent on the Y leg is on that leg");
                Near(f.Tangent2.Y, 1.0, 1e-9, "and R from the corner");

                // centre distance = R/sin(45) = R*sqrt2, along the bisector
                Near(f.Centre.X, 1.0, 1e-9, "centre sits at (R, R)");
                Near(f.Centre.Y, 1.0, 1e-9, "centre sits at (R, R)");
                Near(Vec.Dist(f.Centre, V(0, 0)), Math.Sqrt(2.0), 1e-9, "centre is R/sin(a) from the corner");
            }

            // ---------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("2. tangent points are EXACTLY the radius from the centre");
            {
                // The property that makes the arc actually meet the tracks.
                double[] angles = { 30, 45, 60, 90, 120, 150 };
                double[] radii = { 0.2, 1.0, 3.0 };
                bool all = true;

                foreach (double deg in angles)
                {
                    foreach (double r in radii)
                    {
                        double rad = deg * Math.PI / 180.0;
                        Vec end2 = V(Math.Cos(rad) * 20.0, Math.Sin(rad) * 20.0);
                        Fillet f = FilletGeometry.Corner(V(0, 0), V(20, 0), end2, r);
                        if (!f.Ok) { all = false; continue; }

                        if (Math.Abs(Vec.Dist(f.Tangent1, f.Centre) - r) > 1e-9) all = false;
                        if (Math.Abs(Vec.Dist(f.Tangent2, f.Centre) - r) > 1e-9) all = false;
                    }
                }
                Ok(all, "every angle and radius puts both tangents exactly R from the centre");
            }

            // ---------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("3. the arc taken is the MINOR one");
            {
                // Sweeping the long way round would cross the tracks.
                double[] angles = { 20, 45, 90, 135, 170 };
                bool all = true;
                foreach (double deg in angles)
                {
                    double rad = deg * Math.PI / 180.0;
                    Vec end2 = V(Math.Cos(rad) * 20.0, Math.Sin(rad) * 20.0);
                    Fillet f = FilletGeometry.Corner(V(0, 0), V(20, 0), end2, 0.5);
                    if (!f.Ok) { all = false; continue; }

                    double sweep = f.EndAngleDeg - f.StartAngleDeg;
                    while (sweep < 0) sweep += 360.0;
                    if (sweep > 180.0 + 1e-9) all = false;
                }
                Ok(all, "counterclockwise sweep from start to end is never more than 180 degrees");
            }

            // ---------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("4. degenerate corners are refused, not fudged");
            {
                Fillet straight = FilletGeometry.Corner(V(0, 0), V(10, 0), V(-10, 0), 1.0);
                Ok(!straight.Ok, "collinear segments have no corner");
                Ok(straight.Why.Length > 0, "and the refusal says why");

                Fillet doubled = FilletGeometry.Corner(V(0, 0), V(10, 0), V(10, 0), 1.0);
                Ok(!doubled.Ok, "segments doubling back are refused");

                Fillet zero = FilletGeometry.Corner(V(0, 0), V(0, 0), V(0, 10), 1.0);
                Ok(!zero.Ok, "a zero-length segment is refused");

                Fillet negative = FilletGeometry.Corner(V(0, 0), V(10, 0), V(0, 10), -1.0);
                Ok(!negative.Ok, "a negative radius is refused");
            }

            // ---------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("5. a radius that will not fit is refused, and the limit is usable");
            {
                // 90 degree corner on 1mm legs: t = R, so R must be under 1.
                Fillet tooBig = FilletGeometry.Corner(V(0, 0), V(1, 0), V(0, 1), 5.0);
                Ok(!tooBig.Ok, "an oversized radius is refused rather than inverting the tracks");
                Ok(tooBig.MaxRadius > 0, "the largest radius that fits is reported");
                Near(tooBig.MaxRadius, 1.0, 1e-9, "on 1mm legs at 90 degrees that limit is 1.0");

                // The reported limit must actually work, slightly backed off.
                Fillet atLimit = FilletGeometry.Corner(V(0, 0), V(1, 0), V(0, 1), tooBig.MaxRadius * 0.98);
                Ok(atLimit.Ok, "98% of the reported limit does fit");
            }

            // ---------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("6. tangents stay ON their segments");
            {
                // A tangent past the far end would invert the track.
                bool all = true;
                for (double deg = 10; deg <= 170; deg += 10)
                {
                    double rad = deg * Math.PI / 180.0;
                    Vec e1 = V(6, 0);
                    Vec e2 = V(Math.Cos(rad) * 6.0, Math.Sin(rad) * 6.0);
                    Fillet f = FilletGeometry.Corner(V(0, 0), e1, e2, 0.4);
                    if (!f.Ok) continue;

                    double d1 = Vec.Dist(f.Tangent1, V(0, 0));
                    double d2 = Vec.Dist(f.Tangent2, V(0, 0));
                    if (d1 > 6.0 + 1e-9 || d2 > 6.0 + 1e-9) all = false;
                    if (d1 < 0 || d2 < 0) all = false;
                }
                Ok(all, "no tangent point falls beyond the end of its segment");
            }

            // ---------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("7. distribute");
            {
                double[] outp = FilletGeometry.Distribute(new double[] { 0, 1, 2, 10 });
                Near(outp[0], 0.0, 1e-12, "first stays put");
                Near(outp[3], 10.0, 1e-12, "last stays put");
                Near(outp[1], 10.0 / 3.0, 1e-12, "the middle two space evenly");
                Near(outp[2], 20.0 / 3.0, 1e-12, "the middle two space evenly");

                double[] two = FilletGeometry.Distribute(new double[] { 3, 7 });
                Ok(two.Length == 2 && two[0] == 3 && two[1] == 7, "fewer than three is left alone");

                double[] equal = FilletGeometry.Distribute(new double[] { 5, 5, 5 });
                Ok(equal[0] == 5 && equal[1] == 5 && equal[2] == 5, "coincident points survive without NaN");
            }

            // ---------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("8. scale about an anchor");
            {
                Vec a = V(10, 10);
                Near(FilletGeometry.Scale(a, a, 3.0).X, 10.0, 1e-12, "the anchor never moves");
                Near(FilletGeometry.Scale(a, a, 3.0).Y, 10.0, 1e-12, "the anchor never moves");

                Vec p = FilletGeometry.Scale(V(12, 10), a, 2.0);
                Near(p.X, 14.0, 1e-12, "a point 2 away goes to 4 away at 2x");
                Near(p.Y, 10.0, 1e-12, "and does not drift off-axis");

                Vec back = FilletGeometry.Scale(FilletGeometry.Scale(V(3, -7), a, 2.5), a, 1.0 / 2.5);
                Near(back.X, 3.0, 1e-9, "scaling by f then 1/f returns the point");
                Near(back.Y, -7.0, 1e-9, "scaling by f then 1/f returns the point");
            }

            Console.WriteLine();
            Console.WriteLine("passed " + passed + ", failed " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }
    }
}
