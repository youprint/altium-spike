// SchSelfTest.cs
//
// The schematic half of the self-test: exercises SchPlacement against the
// focused .SchDoc and writes spike_selftest_sch.md beside spike_selftest.md.
//
// It places the same library symbol twice in a scratch area far outside any
// standard sheet -- once at 0 degrees, once at 90 -- and asks questions each
// answered by something other than the code under test:
//
//   - Did exactly two components appear?            (sheet census, before/after)
//   - Did they land where asked, facing the right way, with the designator set?
//   - Do the pin tips point AWAY from the body?     (the body's own bounding box)
//     A tip computed at the wrong end still gets a tidy stub and label and
//     connects to nothing, so this is the check that matters most.
//   - Does each pin's tip move with the component?  (the 0-degree copy rotated
//     by hand must give the 90-degree copy)
//   - Do the stub and label read back where they were put?
//
// Then everything it added is removed and the census must match the start.
// Nothing is saved.

using DXP;
using SCH;
using System;
using System.Collections.Generic;
using System.IO;

namespace AltiumSpike
{
    public static partial class SelfTest
    {
        // Past the corner of an A0 sheet (46.8 x 33.1 in), so nothing placed
        // here can land on the drawing.
        private const double SchScratchXMil = 50000.0;
        private const double SchScratchYMil = 40000.0;
        private const string SchTestNet = "SPIKE_SELFTEST";

        public static Report RunSchematic(IClient client, string folder, string libraryPath)
        {
            Report rep = new Report();
            rep.ModifyingRun = true;
            Runner r = new Runner();
            r.Rep = rep;

            Log.Write("=========== Schematic self-test starting ===========");

            ISch_ServerInterface server = null;
            ISch_Document doc = null;
            string docName = "";
            HashSet<string> names = null;
            string symbol = null;

            r.Section = "Schematic";
            r.Run("Schematic open", "a schematic sheet has focus", delegate
            {
                string why;
                if (!SchPlacement.TryGetSheet(client, out server, out doc, out why)) return "FAIL: " + why;
                try { docName = doc.GetState_DocumentName() ?? ""; } catch { }
                return "PASS: " + Path.GetFileName(docName) + " -- " +
                       SchPlacement.Count(doc, TObjectId.eSchComponent) + " components, " +
                       SchPlacement.Count(doc, TObjectId.eWire) + " wires, " +
                       SchPlacement.Count(doc, TObjectId.eNetLabel) + " net labels";
            });

            if (doc != null)
            {
                r.Run("Symbol library", "the library lists its symbols without being opened", delegate
                {
                    string why;
                    names = SchPlacement.ReadLibraryNames(server, libraryPath, out why);
                    if (names == null) return "FAIL: " + why;
                    if (names.Count == 0) return "FAIL: " + Path.GetFileName(libraryPath) + " lists no symbols";
                    List<string> sorted = new List<string>(names);
                    sorted.Sort(StringComparer.Ordinal);
                    symbol = sorted[0];
                    return "PASS: " + names.Count + " symbols in " + Path.GetFileName(libraryPath) +
                           "; testing with " + symbol;
                });
            }

            if (doc != null && symbol != null)
                SchematicScratchChecks(r, client, server, doc, libraryPath, symbol);

            string subject = "Sheet `" + docName + "`, library `" + libraryPath + "`";
            rep.Path = Write(rep, "# AltiumSpike schematic self-test", subject,
                             "Places a library symbol twice in a scratch area outside any standard sheet, " +
                             "connects one pin of each, verifies, then removes everything it added. Nothing " +
                             "is saved -- the sheet is modified in memory only.",
                             folder, "spike_selftest_sch.md");
            Log.Write("=========== Schematic self-test done: " + rep.Headline() + " ===========");
            return rep;
        }

        private static void SchematicScratchChecks(Runner r, IClient client, ISch_ServerInterface server,
                                                   ISch_Document doc, string libraryPath, string symbol)
        {
            int compsBefore = SchPlacement.Count(doc, TObjectId.eSchComponent);
            int wiresBefore = SchPlacement.Count(doc, TObjectId.eWire);
            int labelsBefore = SchPlacement.Count(doc, TObjectId.eNetLabel);

            SchPlacementPlan.Part p0 = new SchPlacementPlan.Part();
            p0.Designator = "SPIKE_T0";
            p0.LibRef = symbol;
            p0.XMil = SchScratchXMil;
            p0.YMil = SchScratchYMil;
            p0.Rotation = 0;

            SchPlacementPlan.Part p90 = new SchPlacementPlan.Part();
            p90.Designator = "SPIKE_T90";
            p90.LibRef = symbol;
            p90.XMil = SchScratchXMil + 3000.0;
            p90.YMil = SchScratchYMil;
            p90.Rotation = 90;

            ISch_Component c0 = null, c90 = null;
            List<ISch_BasicContainer> prims = new List<ISch_BasicContainer>();
            SchPlacement.Context ctx = new SchPlacement.Context(client, server, doc);

            r.Section = "Schematic (scratch area)";

            r.Run("Place from library", "two new components, one per placement call", delegate
            {
                IIntegratedLibraryManager ilm = EDP.Utils.LoadIntegratedLibraryManager();
                if (ilm == null) return "FAIL: Altium's integrated library manager is unavailable";

                HashSet<string> ids = SchPlacement.ComponentIds(doc);
                string e0, e90;
                c0 = SchPlacement.PlaceOne(doc, ilm, p0, libraryPath, ids, out e0);
                c90 = SchPlacement.PlaceOne(doc, ilm, p90, libraryPath, ids, out e90);
                int after = SchPlacement.Count(doc, TObjectId.eSchComponent);

                if (c0 == null || c90 == null)
                    return "FAIL: " + (e0 ?? "") + (e0 != null && e90 != null ? "; " : "") + (e90 ?? "");
                if (after != compsBefore + 2)
                    return "FAIL: components " + compsBefore + " -> " + after + ", expected +2";
                return "PASS: " + symbol + " placed twice; components " + compsBefore + " -> " + after;
            });

            if (c0 != null && c90 != null)
            {
                r.Run("Placement call honours location and rotation", "the parameter string is obeyed as given", delegate
                {
                    // Informational: Finish corrects both if the call ignored them.
                    string a = Where(c0, p0), b = Where(c90, p90);
                    if (a == null && b == null)
                        return "PASS: both landed at the requested point and angle from the call alone";
                    return "INFO: " + (a ?? "") + (a != null && b != null ? "; " : "") + (b ?? "") +
                           " -- Finish corrects this, see the next check";
                });

                r.Run("Designator, location, rotation", "each reads back exactly as requested", delegate
                {
                    string n0, n90;
                    ctx.Begin();
                    try
                    {
                        SchPlacement.Finish(ctx, c0, p0, out n0);
                        SchPlacement.Finish(ctx, c90, p90, out n90);
                    }
                    finally { ctx.End(); }

                    string a = Where(c0, p0), b = Where(c90, p90);
                    if (a != null || b != null)
                        return "FAIL: after correction " + (a ?? "") + " " + (b ?? "");
                    string d0 = Designator(c0), d90 = Designator(c90);
                    if (d0 != p0.Designator || d90 != p90.Designator)
                        return "FAIL: designators read \"" + d0 + "\" and \"" + d90 + "\"";
                    return "PASS: " + d0 + " at " + p0.XMil + "," + p0.YMil + " mil / 0 deg, " +
                           d90 + " at " + p90.XMil + "," + p90.YMil + " mil / 90 deg";
                });

                List<SchPlacement.PinGeom> pins0 = Visible(SchPlacement.Pins(c0));
                List<SchPlacement.PinGeom> pins90 = Visible(SchPlacement.Pins(c90));

                r.Run("Pin tips point away from the body", "every tip is farther from the body centre than its base", delegate
                {
                    if (pins0.Count == 0) return "SKIP: " + symbol + " has no visible pins";

                    CoordRect br = c0.BoundingRectangle();
                    double cx = (br.Left + (double)br.Right) / 2.0, cy = (br.Bottom + (double)br.Top) / 2.0;
                    int outward = 0;
                    List<string> bad = new List<string>();
                    foreach (SchPlacement.PinGeom g in pins0)
                    {
                        double dBase = Sq(g.X - cx) + Sq(g.Y - cy);
                        double dTip = Sq(g.TipX - cx) + Sq(g.TipY - cy);
                        if (dTip > dBase) outward++;
                        else if (bad.Count < 3)
                            bad.Add("pin " + g.Info.Designator + " base " + SchPlacement.Mil(g.X) + "," + SchPlacement.Mil(g.Y) +
                                    " " + g.OrientDeg + " deg len " + SchPlacement.Mil(g.Length) + " -> tip " +
                                    SchPlacement.Mil(g.TipX) + "," + SchPlacement.Mil(g.TipY));
                    }
                    if (outward == pins0.Count)
                        return "PASS: " + outward + " of " + pins0.Count + " pin tips point outward (body centre " +
                               SchPlacement.Mil((long)cx) + "," + SchPlacement.Mil((long)cy) + " mil)";
                    return "FAIL: " + (pins0.Count - outward) + " of " + pins0.Count + " tips point INTO the body -- " +
                           "stubs there would connect nothing. " + string.Join("; ", bad.ToArray());
                });

                r.Run("Pin tips follow the rotation", "the 0-degree tips rotated by hand match the 90-degree copy", delegate
                {
                    if (pins0.Count == 0) return "SKIP: no visible pins";
                    Dictionary<string, SchPlacement.PinGeom> by90 = new Dictionary<string, SchPlacement.PinGeom>(StringComparer.OrdinalIgnoreCase);
                    foreach (SchPlacement.PinGeom g in pins90) by90[g.Info.Designator] = g;

                    Point l0 = c0.GetState_Location(), l90 = c90.GetState_Location();
                    int ccw = 0, cw = 0, compared = 0;
                    string first = null;
                    foreach (SchPlacement.PinGeom g in pins0)
                    {
                        SchPlacement.PinGeom h;
                        if (!by90.TryGetValue(g.Info.Designator, out h)) continue;
                        compared++;
                        long ox = g.TipX - l0.X, oy = g.TipY - l0.Y;
                        long hx = h.TipX - l90.X, hy = h.TipY - l90.Y;
                        if (hx == -oy && hy == ox) ccw++;
                        else if (hx == oy && hy == -ox) cw++;
                        else if (first == null)
                            first = "pin " + g.Info.Designator + " offset " + SchPlacement.Mil(ox) + "," + SchPlacement.Mil(oy) +
                                    " at 0 deg but " + SchPlacement.Mil(hx) + "," + SchPlacement.Mil(hy) + " at 90";
                    }
                    if (compared == 0) return "FAIL: no pin designator in common between the two copies";
                    if (ccw == compared) return "PASS: all " + compared + " tips rotate with the part (counter-clockwise)";
                    if (cw == compared)
                        return "INFO: all " + compared + " tips rotate with the part, but CLOCKWISE -- the CSV Rotation " +
                               "column follows Altium's convention, which is then clockwise";
                    return "FAIL: " + (compared - ccw - cw) + " of " + compared + " tips do not follow the rotation -- " + first;
                });

                r.Run("Stub and net label", "each wire starts on its pin tip and each label sits on its wire", delegate
                {
                    if (pins0.Count == 0 || pins90.Count == 0) return "SKIP: no visible pins to connect";
                    long stub = EDP.Utils.MilsToCoord(300.0), inset = EDP.Utils.MilsToCoord(50.0);
                    string e0 = null, e90 = null;
                    ctx.Begin();
                    try
                    {
                        SchPlacement.AddStubAndLabel(ctx, pins0[0], SchTestNet, stub, inset, prims, out e0);
                        SchPlacement.AddStubAndLabel(ctx, pins90[0], SchTestNet, stub, inset, prims, out e90);
                    }
                    finally { ctx.End(); }

                    int wires = SchPlacement.Count(doc, TObjectId.eWire);
                    int labels = SchPlacement.Count(doc, TObjectId.eNetLabel);
                    if (e0 != null || e90 != null) return "FAIL: " + (e0 ?? "") + " " + (e90 ?? "");
                    if (wires != wiresBefore + 2 || labels != labelsBefore + 2)
                        return "FAIL: wires " + wiresBefore + " -> " + wires + ", labels " + labelsBefore + " -> " + labels +
                               ", expected +2 each";
                    return "PASS: " + SchTestNet + " on " + p0.Designator + "." + pins0[0].Info.Designator + " (" +
                           pins0[0].OrientDeg + " deg) and " + p90.Designator + "." + pins90[0].Info.Designator + " (" +
                           pins90[0].OrientDeg + " deg); wires " + wiresBefore + " -> " + wires + ", labels " +
                           labelsBefore + " -> " + labels + ", vertices and anchors read back";
                });
            }

            r.Run("Remove the scratch objects", "the sheet census matches the start", delegate
            {
                ctx.Begin();
                try
                {
                    foreach (ISch_BasicContainer o in prims) ctx.Remove(o);
                    if (c0 != null) ctx.Remove(c0);
                    if (c90 != null) ctx.Remove(c90);
                }
                finally { ctx.End(); }

                int comps = SchPlacement.Count(doc, TObjectId.eSchComponent);
                int wires = SchPlacement.Count(doc, TObjectId.eWire);
                int labels = SchPlacement.Count(doc, TObjectId.eNetLabel);
                if (comps != compsBefore || wires != wiresBefore || labels != labelsBefore)
                    return "FAIL: components " + compsBefore + " -> " + comps + ", wires " + wiresBefore + " -> " + wires +
                           ", labels " + labelsBefore + " -> " + labels + " -- delete what is left at " +
                           SchScratchXMil + "," + SchScratchYMil + " mil by hand, and do not save";
                return "PASS: " + (prims.Count + (c0 != null ? 1 : 0) + (c90 != null ? 1 : 0)) +
                       " scratch object(s) removed; census back to " + comps + " components, " + wires +
                       " wires, " + labels + " labels";
            });
        }

        // Null when the component is where and how it was asked to be;
        // otherwise what is different.
        private static string Where(ISch_Component c, SchPlacementPlan.Part p)
        {
            Point at = c.GetState_Location();
            int x = EDP.Utils.MilsToCoord(p.XMil), y = EDP.Utils.MilsToCoord(p.YMil);
            int deg = SchPlacement.Degrees(c.GetState_Orientation());
            List<string> diff = new List<string>();
            if (at.X != x || at.Y != y)
                diff.Add(p.Designator + " at " + SchPlacement.Mil(at.X) + "," + SchPlacement.Mil(at.Y) + " not " +
                         SchPlacement.Mil(x) + "," + SchPlacement.Mil(y));
            if (deg != p.Rotation) diff.Add(p.Designator + " at " + deg + " deg not " + p.Rotation);
            return diff.Count == 0 ? null : string.Join(", ", diff.ToArray());
        }

        private static string Designator(ISch_Component c)
        {
            ISch_Designator d = c.GetState_SchDesignator();
            return d == null ? "" : (d.GetState_Text() ?? "");
        }

        private static List<SchPlacement.PinGeom> Visible(List<SchPlacement.PinGeom> pins)
        {
            List<SchPlacement.PinGeom> v = new List<SchPlacement.PinGeom>();
            foreach (SchPlacement.PinGeom g in pins)
                if (g.Info.OnPlacedPart && !g.Info.Hidden) v.Add(g);
            return v;
        }

        private static double Sq(double v) { return v * v; }
    }
}
