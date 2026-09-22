// Tests.cs -- IPC-2221 current capacity checks.
//
// Compiles the plugin's own Ipc2221.cs, not a copy.
//
// The dangerous failure here is a plausible-looking number. A current rating
// that is wrong by 2x still looks like a current rating, and the board that
// results runs hot rather than failing an obvious check. So these assertions
// pin the constants, the internal/external split, and the round trip between
// the formula and its inverse.

using AltiumSpike;
using System;
using System.Globalization;

namespace IpcTest
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
                    "  (got " + actual.ToString("0.######", Inv) +
                    ", expected " + expected.ToString("0.######", Inv) + ")");
            }
        }

        const double OneOz = 0.03479;      // mm, 1 oz copper by definition

        static void Main()
        {
            Console.WriteLine("IPC-2221 current capacity");
            Console.WriteLine();

            // ---------------------------------------------------------------
            Console.WriteLine("1. constants match the standard");
            {
                Near(Ipc2221.KExternal, 0.048, 1e-12, "k = 0.048 external");
                Near(Ipc2221.KInternal, 0.024, 1e-12, "k = 0.024 internal");
                Near(Ipc2221.TempExponent, 0.44, 1e-12, "temperature exponent 0.44");
                Near(Ipc2221.AreaExponent, 0.725, 1e-12, "area exponent 0.725");
                Near(Ipc2221.ThicknessMMForOunces(1.0), 0.03479, 1e-9, "1 oz copper is 34.79 um");
                Near(Ipc2221.ThicknessMMForOunces(2.0), 0.06958, 1e-9, "2 oz is twice that");
            }

            // ---------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("2. a worked example against the published formula");
            {
                // 10 mil (0.254 mm) track, 1 oz copper, 10 C rise, outer layer.
                //   A  = 10 mil x 1.3697 mil = 13.697 sq mil
                //   I  = 0.048 * 10^0.44 * 13.697^0.725 = 0.8817 A
                double w = Ipc2221.MilsToMM(10.0);
                double i = Ipc2221.CurrentAmps(w, OneOz, 10.0, true);
                Near(i, 0.8817, 0.005, "10 mil, 1 oz, +10 C, external = 0.88 A");

                Near(Ipc2221.AreaSquareMils(w, OneOz), 13.697, 0.01, "cross-section is 13.7 sq mil");
            }

            // ---------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("3. internal carries exactly half of external");
            {
                // This is the 2x error that makes a buried track look fine.
                double w = Ipc2221.MilsToMM(20.0);
                double ext = Ipc2221.CurrentAmps(w, OneOz, 10.0, true);
                double intl = Ipc2221.CurrentAmps(w, OneOz, 10.0, false);

                Near(ext / intl, 2.0, 1e-9, "external / internal = 2.0 exactly");
                Ok(intl < ext, "internal is the smaller number");
            }

            // ---------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("4. the inverse really is the inverse");
            {
                double[] widths = { 0.1, 0.25, 0.5, 1.0, 2.5, 5.0 };
                bool allBack = true;
                foreach (double w in widths)
                {
                    foreach (bool ext in new bool[] { true, false })
                    {
                        double i = Ipc2221.CurrentAmps(w, OneOz, 10.0, ext);
                        double back = Ipc2221.RequiredWidthMM(i, OneOz, 10.0, ext);
                        if (Math.Abs(back - w) > w * 1e-6) allBack = false;
                    }
                }
                Ok(allBack, "width -> current -> width round trips for every case");

                // 2 oz copper needs half the width of 1 oz for the same current,
                // because capacity depends on area, not width alone.
                double w1 = Ipc2221.RequiredWidthMM(2.0, OneOz, 10.0, true);
                double w2 = Ipc2221.RequiredWidthMM(2.0, OneOz * 2.0, 10.0, true);
                Near(w1 / w2, 2.0, 1e-9, "doubling copper weight halves the width needed");
            }

            // ---------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("5. it scales the way the formula says");
            {
                double w = 0.5;
                double at10 = Ipc2221.CurrentAmps(w, OneOz, 10.0, true);
                double at20 = Ipc2221.CurrentAmps(w, OneOz, 20.0, true);
                Near(at20 / at10, Math.Pow(2.0, 0.44), 1e-9, "doubling the rise scales by 2^0.44");

                double narrow = Ipc2221.CurrentAmps(0.5, OneOz, 10.0, true);
                double wide = Ipc2221.CurrentAmps(1.0, OneOz, 10.0, true);
                Near(wide / narrow, Math.Pow(2.0, 0.725), 1e-9, "doubling width scales by 2^0.725");
                Ok(wide > narrow, "wider carries more");
            }

            // ---------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("6. degenerate input returns zero, never NaN");
            {
                Ok(Ipc2221.CurrentAmps(0.0, OneOz, 10.0, true) == 0.0, "zero width");
                Ok(Ipc2221.CurrentAmps(0.5, 0.0, 10.0, true) == 0.0, "zero thickness");
                Ok(Ipc2221.CurrentAmps(0.5, OneOz, 0.0, true) == 0.0, "zero temperature rise");
                Ok(Ipc2221.CurrentAmps(-1.0, OneOz, 10.0, true) == 0.0, "negative width");
                Ok(Ipc2221.RequiredWidthMM(0.0, OneOz, 10.0, true) == 0.0, "zero current wanted");
                Ok(Ipc2221.RequiredWidthMM(1.0, 0.0, 10.0, true) == 0.0, "zero thickness available");

                // A NaN leaking into a report is worse than a zero: it
                // formats as "NaN" in a CSV a fab house reads.
                bool anyNaN =
                    double.IsNaN(Ipc2221.CurrentAmps(0.0, 0.0, 0.0, true)) ||
                    double.IsNaN(Ipc2221.RequiredWidthMM(0.0, 0.0, 0.0, false));
                Ok(!anyNaN, "no NaN escapes from all-zero input");
            }

            // ---------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("7. unit conversion");
            {
                Near(Ipc2221.MMToMils(0.0254), 1.0, 1e-12, "0.0254 mm is 1 mil");
                Near(Ipc2221.MilsToMM(1.0), 0.0254, 1e-12, "1 mil is 0.0254 mm");
                Near(Ipc2221.MilsToMM(Ipc2221.MMToMils(3.7)), 3.7, 1e-12, "conversion round trips");
            }

            Console.WriteLine();
            Console.WriteLine("passed " + passed + ", failed " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }
    }
}
