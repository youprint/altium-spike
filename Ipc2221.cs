// Ipc2221.cs
//
// Conductor current capacity from IPC-2221, and its inverse.
//
//     I = k * dT^0.44 * A^0.725
//
//     I   amps
//     dT  temperature rise above ambient, degrees C
//     A   conductor cross-section, SQUARE MILS
//     k   0.048 for external layers, 0.024 for internal
//
// The k split is the part people get wrong. An internal conductor is buried
// in laminate and can only shed heat by conduction through it, so it carries
// roughly HALF the current of an identical track on an outer layer, which is
// cooled by the surface. Applying the external constant to an inner-layer
// track overstates its capacity by about 2x, and the failure mode is a hot
// board rather than an obvious one.
//
// The formula is empirical, fitted to the IPC-2221 charts, and it assumes
// still air, a single isolated conductor, and a conductor long enough that
// the ends are not heatsinking it. A short fat track between two big copper
// pours does better than this says; a long track in a hot enclosure beside
// other hot tracks does worse. It is a design check, not a guarantee.
//
// Deliberately free of any Altium type so it compiles standalone and is
// checked by a test harness against values computed straight from the
// published formula.

using System;

namespace AltiumSpike
{
    public static class Ipc2221
    {
        public const double KExternal = 0.048;
        public const double KInternal = 0.024;
        public const double TempExponent = 0.44;
        public const double AreaExponent = 0.725;

        public const double MMPerMil = 0.0254;

        public static double MMToMils(double mm) { return mm / MMPerMil; }
        public static double MilsToMM(double mils) { return mils * MMPerMil; }

        public static double K(bool externalLayer) { return externalLayer ? KExternal : KInternal; }

        /// Cross-section in square mils, from millimetre width and thickness.
        public static double AreaSquareMils(double widthMM, double thicknessMM)
        {
            if (widthMM <= 0.0 || thicknessMM <= 0.0) return 0.0;
            return MMToMils(widthMM) * MMToMils(thicknessMM);
        }

        /// Current a conductor can carry for a given temperature rise.
        /// Returns 0 for degenerate geometry rather than NaN, so a board with
        /// a zero-width track does not poison a whole report.
        public static double CurrentAmps(double widthMM, double thicknessMM,
                                         double tempRiseC, bool externalLayer)
        {
            if (tempRiseC <= 0.0) return 0.0;
            double area = AreaSquareMils(widthMM, thicknessMM);
            if (area <= 0.0) return 0.0;

            return K(externalLayer)
                 * Math.Pow(tempRiseC, TempExponent)
                 * Math.Pow(area, AreaExponent);
        }

        /// The inverse: the width needed to carry a current at a given rise.
        /// Useful for "this net needs 3 A, how wide?" rather than only
        /// "how much will this track take?".
        public static double RequiredWidthMM(double amps, double thicknessMM,
                                             double tempRiseC, bool externalLayer)
        {
            if (amps <= 0.0 || thicknessMM <= 0.0 || tempRiseC <= 0.0) return 0.0;

            double areaMils2 = Math.Pow(
                amps / (K(externalLayer) * Math.Pow(tempRiseC, TempExponent)),
                1.0 / AreaExponent);

            double thicknessMils = MMToMils(thicknessMM);
            if (thicknessMils <= 0.0) return 0.0;

            return MilsToMM(areaMils2 / thicknessMils);
        }

        /// Copper thickness for a weight in ounces. 1 oz is 34.79 um by
        /// definition -- the "1.4 mil" figure everyone quotes is this rounded.
        public static double ThicknessMMForOunces(double ounces)
        {
            return ounces * 0.03479;
        }
    }
}
