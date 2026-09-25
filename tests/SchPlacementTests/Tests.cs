// Tests.cs -- checks for the schematic placement plan.
//
// Compiles the PLUGIN'S OWN SchPlacementPlan.cs (see the .csproj), not a copy.
//
// Every rule here fails quietly when it is wrong. A pin tip computed at the
// body end instead of the electrical end still draws a tidy stub and label --
// connected to nothing. A pin listed in two nets shorts them without a word.
// A symbol name built one underscore differently from YouEDA's makes every
// LCSC lookup miss. So the cases below are the ones that break a naive
// version: every pin direction, the label anchor landing on the wire, a
// header with a byte-order mark, mm columns, rotation wrap-around, and the
// YouEDA sanitising rule on the names that actually occur.

using AltiumSpike;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace SchPlacementTest
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

        static void Eq<TV>(TV actual, TV expected, string what)
        {
            if (EqualityComparer<TV>.Default.Equals(actual, expected)) { passed++; Console.WriteLine("  PASS  " + what); }
            else
            {
                failed++;
                Console.WriteLine("  FAIL  " + what + "  (got \"" + actual + "\", expected \"" + expected + "\")");
            }
        }

        static void Near(double actual, double expected, double tol, string what)
        {
            if (Math.Abs(actual - expected) <= tol) { passed++; Console.WriteLine("  PASS  " + what); }
            else
            {
                failed++;
                Console.WriteLine("  FAIL  " + what + "  (got " + actual.ToString("0.#####", Inv) +
                                  ", expected " + expected.ToString("0.#####", Inv) + ")");
            }
        }

        static bool Has(List<string> list, string fragment)
        {
            foreach (string s in list)
                if (s.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        static void Section(string s) { Console.WriteLine(); Console.WriteLine("== " + s); }

        // "a,b,c" rows -> split rows. The harness does not need quoted fields;
        // the plugin splits with CsvIo.Split.
        static List<List<string>> Rows(params string[] lines)
        {
            List<List<string>> rows = new List<List<string>>();
            foreach (string l in lines) rows.Add(new List<string>(l.Split(',')));
            return rows;
        }

        static SchPlacementPlan.Part Find(SchPlacementPlan.Plan p, string des)
        {
            foreach (SchPlacementPlan.Part x in p.Parts) if (x.Designator == des) return x;
            return null;
        }

        static int Main()
        {
            Console.WriteLine("SchPlacementPlan checks");

            // ---------------------------------------------------------------
            Section("Components CSV: the happy path");
            {
                SchPlacementPlan.Plan p = SchPlacementPlan.Parse(
                    Rows("﻿Designator,LCSC,X,Y,Rotation,Mirror",
                         "R1,c12530,1000,2000,90,no",
                         "",
                         "# a comment row",
                         "U1,C7519,3000,2000,-90,yes"),
                    Rows("Net,Designator,Pin", "N1,R1,1", "N1,U1,3"));
                Ok(p.Ok, "clean file parses with no errors (" + string.Join(" | ", p.Errors) + ")");
                Eq(p.Parts.Count, 2, "blank and comment rows skipped, two parts");
                Eq(Find(p, "R1").Lcsc, "C12530", "LCSC normalised to upper case");
                Eq(Find(p, "R1").Rotation, 90, "rotation 90 kept");
                Eq(Find(p, "U1").Rotation, 270, "rotation -90 wraps to 270");
                Ok(Find(p, "U1").Mirror && !Find(p, "R1").Mirror, "mirror yes/no read");
                Eq(Find(p, "U1").Line, 5, "line numbers are file lines, counting the header");
                Ok(Find(p, "R1").HasPosition, "X and Y together give a position");
            }

            Section("Components CSV: units and header spellings");
            {
                SchPlacementPlan.Plan p = SchPlacementPlan.Parse(
                    Rows("Ref,Lib Ref,X (mm),Y (mm)", "C1,CAP_0402,25.4,-12.7"),
                    null);
                Ok(p.Ok, "Ref / Lib Ref / X (mm) accepted");
                Near(Find(p, "C1").XMil, 1000.0, 1e-9, "25.4 mm -> 1000 mil");
                Near(Find(p, "C1").YMil, -500.0, 1e-9, "-12.7 mm -> -500 mil");
                Eq(Find(p, "C1").LibRef, "CAP_0402", "LibRef read");
                Ok(Has(p.Warnings, "no nets"), "no nets file is a warning, not an error");
            }

            Section("Components CSV: every error is collected, nothing half-parsed");
            {
                SchPlacementPlan.Plan p = SchPlacementPlan.Parse(
                    Rows("Designator,LCSC,X,Y,Rotation,Mirror",
                         "R1,C1,0,0,45,",          // bad rotation
                         "R1,C2,0,0,0,",           // duplicate designator
                         ",C3,0,0,0,",             // no designator
                         "R3,,0,0,0,",             // neither LCSC nor LibRef
                         "R4,C4,100,,0,",          // X without Y
                         "R5,BANANA,0,0,0,",       // not an LCSC number
                         "R6,C6,0,0,0,maybe",      // bad mirror
                         "R7,C7,1e3,abc,0,"),      // not a number
                    null);
                Ok(!p.Ok, "file with bad rows is not ok");
                Ok(Has(p.Errors, "rotation \"45\""), "rotation 45 rejected");
                Ok(Has(p.Errors, "already used on line 2"), "duplicate designator names the first line");
                Ok(Has(p.Errors, "line 4: no designator"), "missing designator");
                Ok(Has(p.Errors, "neither an LCSC number nor a LibRef"), "row with no symbol reference");
                Ok(Has(p.Errors, "R4 has X but no Y"), "half a position");
                Ok(Has(p.Errors, "\"BANANA\" is not an LCSC number"), "malformed LCSC");
                Ok(Has(p.Errors, "mirror \"maybe\""), "bad mirror flag");
                Ok(Has(p.Errors, "not a number pair"), "non-numeric position");
            }

            Section("Components CSV: missing columns");
            {
                SchPlacementPlan.Plan a = SchPlacementPlan.Parse(Rows("LCSC,X,Y", "C1,0,0"), null);
                Ok(Has(a.Errors, "no Designator column"), "no designator column");
                SchPlacementPlan.Plan b = SchPlacementPlan.Parse(Rows("Designator,X,Y", "R1,0,0"), null);
                Ok(Has(b.Errors, "needs an LCSC or a LibRef"), "no symbol column");
                SchPlacementPlan.Plan c = SchPlacementPlan.Parse(Rows("Designator,LCSC,X", "R1,C1,0"), null);
                Ok(Has(c.Errors, "an X column but no Y"), "X column without Y column");
                SchPlacementPlan.Plan d = SchPlacementPlan.Parse(Rows("Designator,LCSC"), null);
                Ok(Has(d.Errors, "no component rows"), "header only");
            }

            // ---------------------------------------------------------------
            Section("Nets CSV");
            {
                SchPlacementPlan.Plan p = SchPlacementPlan.Parse(
                    Rows("Designator,LCSC", "R1,C1", "R2,C2", "U1,C3"),
                    Rows("Net,Node",
                         "VCC,R1.1",
                         "VCC,U1.VDD",
                         "VCC,R1.1",               // same pin, same net: warn, once
                         "GND,R1.1",               // same pin, other net: short
                         "SIG,X9.1",               // not in the components CSV
                         "LONE,R2.2",              // single-pin net
                         "BAD,R2",                 // not Designator.Pin
                         ",R2.1"));                // no net name
                Ok(Has(p.Errors, "R1.1 is in GND but line 2 puts it in VCC"), "pin in two nets is an error");
                Ok(Has(p.Warnings, "listed in VCC twice"), "duplicate node is a warning");
                Ok(Has(p.Warnings, "X9 is not in the components CSV"), "unknown designator skipped with warning");
                Ok(Has(p.Warnings, "net LONE has only one pin"), "single-pin net warned");
                Ok(Has(p.Errors, "\"R2\" is not Designator.Pin"), "malformed node");
                Ok(Has(p.Errors, "line 9: no net name"), "empty net name");
                Eq(p.Nodes.Count, 3, "three distinct nodes kept (R1.1, U1.VDD, R2.2)");
                Eq(p.NetCount, 2, "two nets counted (VCC, LONE)");
                Eq(p.Nodes[1].Pin, "VDD", "pin given by name is kept as text");
            }
            {
                SchPlacementPlan.Plan p = SchPlacementPlan.Parse(
                    Rows("Designator,LCSC", "U10,C1"),
                    Rows("Net,Designator,Pin", "N,U10,A1"));
                Ok(p.Ok && p.Nodes.Count == 1 && p.Nodes[0].Designator == "U10" && p.Nodes[0].Pin == "A1",
                   "Designator + Pin columns");
                SchPlacementPlan.Plan q = SchPlacementPlan.Parse(
                    Rows("Designator,LCSC", "U1.A,C1"),
                    Rows("Net,Node", "N,U1.A.3"));
                Ok(q.Ok && q.Nodes.Count == 1 && q.Nodes[0].Designator == "U1.A" && q.Nodes[0].Pin == "3",
                   "node splits on the LAST dot, so a designator may contain one");
                SchPlacementPlan.Plan r = SchPlacementPlan.Parse(Rows("Designator,LCSC", "R1,C1"), Rows("Designator,Pin", "R1,1"));
                Ok(Has(r.Errors, "no Net column"), "nets file without a Net column");
            }

            // ---------------------------------------------------------------
            Section("Automatic layout");
            {
                SchPlacementPlan.Plan p = SchPlacementPlan.Parse(
                    Rows("Designator,LCSC,X,Y", "R10,C1,,", "R2,C1,,", "C1,C1,,", "U1,C1,4040,1000", "R1,C1,,"),
                    null);
                SchPlacementPlan.AutoLayout(p, 1000, 1000, 1500, 2);
                // explicit max X 4040 + 1500 = 5540 -> snapped 5500
                Ok(!Find(p, "U1").AutoPlaced && Find(p, "U1").XMil == 4040, "positioned part left alone");
                Eq(Find(p, "C1").XMil, 5500.0, "grid starts one pitch right of the right-most placed part, snapped");
                Eq(Find(p, "C1").YMil, 1000.0, "first auto part in the first row");
                Eq(Find(p, "R1").XMil, 7000.0, "second column");
                Eq(Find(p, "R2").YMil, 2500.0, "wraps to the next row after two columns");
                Eq(Find(p, "R10").XMil, 7000.0, "R10 after R2 -- natural order, not text order");
                Ok(Find(p, "R10").AutoPlaced, "auto-placed parts are marked");
            }
            Ok(SchPlacementPlan.CompareDesignators("R2", "R10") < 0, "R2 < R10");
            Ok(SchPlacementPlan.CompareDesignators("R10", "RN1") < 0, "R10 < RN1 (prefix first)");
            Ok(SchPlacementPlan.CompareDesignators("U1A", "U1B") < 0, "U1A < U1B");
            Ok(SchPlacementPlan.CompareDesignators("J", "J1") < 0, "a designator with no number sorts first");

            // ---------------------------------------------------------------
            Section("YouEDA symbol names");
            Eq(SchPlacementPlan.SafeLibraryName("CL05A225MQ5NSNC"), "CL05A225MQ5NSNC", "an MPN is unchanged");
            Eq(SchPlacementPlan.SafeLibraryName("SS34 C52023881"), "SS34_C52023881", "space -> underscore");
            Eq(SchPlacementPlan.SafeLibraryName("10uF/25V (X5R)"), "10uF_25V_X5R", "runs of punctuation collapse, ends trimmed");
            Eq(SchPlacementPlan.SafeLibraryName("0.1µF"), "0.1_F", "non-ASCII letter replaced");
            Eq(SchPlacementPlan.SafeLibraryName("A+B-C.D_E"), "A+B-C.D_E", "+ - . _ are kept");
            Eq(SchPlacementPlan.SafeLibraryName("  __  "), "YouEDA_Component", "nothing left -> the placeholder");
            Eq(SchPlacementPlan.SafeLibraryName(null), "YouEDA_Component", "null -> the placeholder");
            Eq(SchPlacementPlan.SafeLibraryName(new string('A', 130)).Length, 120, "truncated to 120");

            Section("LCSC -> symbol name");
            {
                Dictionary<string, string> map = new Dictionary<string, string>();
                int a = SchPlacementPlan.AddClassification(map, Rows(
                    "LCSC Part,Family,Confidence,Evidence,Name,Description,Package",
                    "C107145,Capacitors,High,x,CC0805KRX7R9BB221,,C0805",
                    "C999,Other,Low,x,Some Part/2,,X"));
                int b = SchPlacementPlan.AddClassification(map, Rows(
                    "LCSC Part,Name", "C107145,OLDER_NAME", "C555,NEW_ONE"));
                Eq(a, 2, "two pairs from the newest file");
                Eq(b, 1, "an older file does not override a newer pair");
                Eq(map["C999"], "Some_Part_2", "names are stored as YouEDA writes them");
                Eq(map["C107145"], "CC0805KRX7R9BB221", "first (newest) file wins");

                HashSet<string> lib = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "CC0805KRX7R9BB221", "C12345", "Some_Part_2" };
                string err;

                SchPlacementPlan.Part p1 = new SchPlacementPlan.Part { Designator = "C1", Lcsc = "C107145" };
                Ok(SchPlacementPlan.ResolveLibRef(p1, map, lib, "youeda.SchLib", out err) && p1.LibRef == "CC0805KRX7R9BB221",
                   "LCSC resolved through the classification");

                SchPlacementPlan.Part p2 = new SchPlacementPlan.Part { Designator = "U1", Lcsc = "C12345" };
                Ok(SchPlacementPlan.ResolveLibRef(p2, map, lib, "youeda.SchLib", out err) && p2.LibRef == "C12345",
                   "falls back to an LCSC-named symbol (YouEDA before 1.2.8)");

                SchPlacementPlan.Part p3 = new SchPlacementPlan.Part { Designator = "U2", Lcsc = "C1" };
                Ok(!SchPlacementPlan.ResolveLibRef(p3, map, lib, "youeda.SchLib", out err) && err.Contains("has no symbol name"),
                   "unknown LCSC is an error that says what to do");

                SchPlacementPlan.Part p4 = new SchPlacementPlan.Part { Designator = "U3", Lcsc = "C555" };
                Ok(!SchPlacementPlan.ResolveLibRef(p4, map, lib, "youeda.SchLib", out err) &&
                   err.Contains("NEW_ONE") && err.Contains("3 symbols"),
                   "mapped name missing from the library is an error naming both");

                SchPlacementPlan.Part p5 = new SchPlacementPlan.Part { Designator = "C2", LibRef = "some_part_2", Lcsc = "C1" };
                Ok(SchPlacementPlan.ResolveLibRef(p5, map, lib, "youeda.SchLib", out err) && p5.LibRef == "Some_Part_2",
                   "an explicit LibRef wins over LCSC and takes the library's own spelling");

                SchPlacementPlan.Part p6 = new SchPlacementPlan.Part { Designator = "C3", LibRef = "ANYTHING" };
                Ok(SchPlacementPlan.ResolveLibRef(p6, map, null, "x", out err) && p6.LibRef == "ANYTHING",
                   "unreadable library: the name is taken on trust");
            }

            // ---------------------------------------------------------------
            Section("Pin tip: Location + Length in the direction the pin points");
            {
                long tx, ty;
                SchPlacementPlan.PinTip(1000, 2000, 0, 300, out tx, out ty);
                Ok(tx == 1300 && ty == 2000, "0 -> right");
                SchPlacementPlan.PinTip(1000, 2000, 90, 300, out tx, out ty);
                Ok(tx == 1000 && ty == 2300, "90 -> up");
                SchPlacementPlan.PinTip(1000, 2000, 180, 300, out tx, out ty);
                Ok(tx == 700 && ty == 2000, "180 -> left");
                SchPlacementPlan.PinTip(1000, 2000, 270, 300, out tx, out ty);
                Ok(tx == 1000 && ty == 1700, "270 -> down");
                SchPlacementPlan.PinTip(0, 0, -90, 10, out tx, out ty);
                Ok(tx == 0 && ty == -10, "-90 is 270");
                SchPlacementPlan.PinTip(0, 0, 0, 3000000000L, out tx, out ty);
                Ok(tx == 3000000000L, "no 32-bit overflow on long coordinates");
                bool threw = false;
                try { SchPlacementPlan.PinTip(0, 0, 45, 10, out tx, out ty); } catch (ArgumentException) { threw = true; }
                Ok(threw, "45 degrees is refused, not rounded");
            }

            Section("Stub and label");
            {
                int[] dirs = { 0, 90, 180, 270 };
                foreach (int d in dirs)
                {
                    SchPlacementPlan.Stub s = SchPlacementPlan.StubFor(5000, 6000, d, 300, 50);
                    Ok(s.X1 == 5000 && s.Y1 == 6000, d + ": wire starts exactly on the pin tip");
                    Ok(Math.Abs(s.X2 - s.X1) + Math.Abs(s.Y2 - s.Y1) == 300, d + ": wire is the stub length");
                    Ok(SchPlacementPlan.OnSegment(s.LabelX, s.LabelY, s.X1, s.Y1, s.X2, s.Y2), d + ": label anchor is on the wire");
                    Ok(!(s.LabelX == s.X1 && s.LabelY == s.Y1), d + ": label anchor is not on the pin tip itself");
                }
                SchPlacementPlan.Stub r = SchPlacementPlan.StubFor(0, 0, 0, 300, 50);
                SchPlacementPlan.Stub l = SchPlacementPlan.StubFor(0, 0, 180, 300, 50);
                SchPlacementPlan.Stub u = SchPlacementPlan.StubFor(0, 0, 90, 300, 50);
                SchPlacementPlan.Stub dn = SchPlacementPlan.StubFor(0, 0, 270, 300, 50);
                Ok(r.X2 == 300 && l.X2 == -300 && u.Y2 == 300 && dn.Y2 == -300, "stub points away from the body");
                Ok(r.LabelRotation == 0 && l.LabelRotation == 0 && u.LabelRotation == 90 && dn.LabelRotation == 90,
                   "label vertical on vertical stubs only");
                Ok(!r.LabelRightAligned && l.LabelRightAligned && !u.LabelRightAligned && dn.LabelRightAligned,
                   "text runs outward: right-aligned for left and down stubs");
                SchPlacementPlan.Stub big = SchPlacementPlan.StubFor(0, 0, 0, 100, 400);
                Ok(big.LabelX > 0 && big.LabelX < 100, "an inset longer than the stub is pulled back onto it");
                bool threw = false;
                try { SchPlacementPlan.StubFor(0, 0, 0, 0, 1); } catch (ArgumentException) { threw = true; }
                Ok(threw, "zero-length stub refused");
            }
            Ok(SchPlacementPlan.OnSegment(5, 0, 0, 0, 10, 0) && !SchPlacementPlan.OnSegment(5, 1, 0, 0, 10, 0) &&
               !SchPlacementPlan.OnSegment(11, 0, 0, 0, 10, 0) && SchPlacementPlan.OnSegment(0, 0, 0, 10, 0, 0),
               "OnSegment: inside, off-axis, past the end, reversed ends");

            // ---------------------------------------------------------------
            Section("Pin matching");
            {
                List<SchPlacementPlan.PinInfo> pins = new List<SchPlacementPlan.PinInfo>
                {
                    new SchPlacementPlan.PinInfo { Designator = "1", Name = "VDD" },
                    new SchPlacementPlan.PinInfo { Designator = "2", Name = "GND" },
                    new SchPlacementPlan.PinInfo { Designator = "3", Name = "GND" },
                    new SchPlacementPlan.PinInfo { Designator = "4", Name = "1" },     // a pin NAMED 1
                    new SchPlacementPlan.PinInfo { Designator = "5", Name = "NC", Hidden = true },
                    new SchPlacementPlan.PinInfo { Designator = "6", Name = "B_IN", OnPlacedPart = false },
                    new SchPlacementPlan.PinInfo { Designator = "a1", Name = "X" },
                };
                string why;
                Eq(SchPlacementPlan.MatchPin(pins, "1", out why), 0, "number beats a pin whose NAME is the same text");
                Eq(SchPlacementPlan.MatchPin(pins, "vdd", out why), 0, "name, case-insensitive");
                Eq(SchPlacementPlan.MatchPin(pins, "A1", out why), 6, "number, case-insensitive");
                Ok(SchPlacementPlan.MatchPin(pins, "GND", out why) == -1 && why.Contains("2 pins are named GND"),
                   "ambiguous name refused");
                Ok(SchPlacementPlan.MatchPin(pins, "5", out why) == -1 && why.Contains("hidden"), "hidden pin refused");
                Ok(SchPlacementPlan.MatchPin(pins, "6", out why) == -1 && why.Contains("another part"),
                   "pin on an unplaced part refused, and says so");
                Ok(SchPlacementPlan.MatchPin(pins, "99", out why) == -1 && why.Contains("pins: 1 VDD, 2 GND") &&
                   !why.Contains("B_IN"), "missing pin lists the pins that do exist on the placed part");
            }

            Console.WriteLine();
            Console.WriteLine(passed + " passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }
    }
}
