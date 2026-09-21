// JlcExport.cs
//
// JLCPCB / LCSC assembly export: BOM + CPL (pick-and-place).
//
// FORMAT, from JLCPCB's own documentation (not guessed):
//
//   bom_jlcpcb.csv   Comment,Designator,Footprint,LCSC Part #
//       One row per distinct part. Designators are comma-separated inside
//       a quoted field, which is why the whole file is quoted properly
//       rather than naively concatenated.
//
//   cpl_jlcpcb.csv   Designator,Mid X,Mid Y,Layer,Rotation
//       Units: millimetres. Rotation: degrees, positive counter-clockwise.
//       Layer: exactly "Top" or "Bottom".
//       Mid X / Mid Y are the component CENTROID, which is NOT the same as
//       the anchor Altium reports as Component.x/.y -- hence CenterOffset
//       in footprint_sizes.csv. We compute the true centre from the
//       bounding rectangle, the same way BoardExport derives CenterOffset.
//
//   bom_missing_lcsc.csv   Designator,Comment,Footprint,Description
//       Every component with no LCSC part number, as a worklist.
//
// WHERE THE LCSC NUMBER COMES FROM: Altium has no standard parameter for
// it, so different libraries use different names. We check a list of
// common spellings in order, and the log records EVERY parameter name
// seen on the board, so if none matches you can see what yours are called
// and add it to LcscParameterNames.

using DXP;
using PCB;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AltiumSpike
{
    public static class JlcExport
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // Checked in order; first non-empty match wins.
        //
        // "Supplier Part" comes FIRST because that is what actually holds the
        // LCSC number on this board. Established by reading the installed
        // EasyEDA-Loader.dll, which is what imported these components: it
        // writes exactly four Altium parameters --
        //
        //     Supplier          (e.g. "LCSC")
        //     Supplier Part     <- the LCSC part number
        //     Manufacturer
        //     Manufacturer Part
        //
        // There is no "LCSC Part #" parameter anywhere, which is the name
        // every JLCPCB tutorial assumes. The rest of this list is kept for
        // components added by other means.
        private static readonly string[] LcscParameterNames = new string[]
        {
            "Supplier Part",
            "LCSC Part #", "LCSC Part Number", "LCSC", "LCSC#", "LCSC Part",
            "JLCPCB Part #", "JLCPCB Part Number", "JLC Part #",
            "Supplier Part Number 1", "Supplier Part Number",
        };

        // Sanity-check the supplier when the value came from "Supplier Part":
        // that field is generic, so a non-LCSC supplier must not be passed off
        // as an LCSC number.
        private const string SupplierParameterName = "Supplier";
        private const string ManufacturerPartParameterName = "Manufacturer Part";

        private static string F3(double v)
        {
            double r = Math.Round(v, 3, MidpointRounding.AwayFromZero);
            if (r == 0) r = 0;
            return r.ToString("0.000", Inv);
        }

        private static string F1(double v)
        {
            double r = Math.Round(v, 1, MidpointRounding.AwayFromZero);
            if (r == 0) r = 0;
            return r.ToString("0.0", Inv);
        }

        private static string Csv(string s)
        {
            if (s == null) return "";
            if (s.IndexOf(',') < 0 && s.IndexOf('"') < 0 && s.IndexOf('\n') < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        private static double MM(int coord) { return EDP.Utils.CoordToMMs(coord); }

        private class Part
        {
            public string Comment = "";
            public string Footprint = "";
            public string Lcsc = "";
            public string Description = "";
            public List<string> Designators = new List<string>();
        }

        private class Placement
        {
            public string Designator = "";
            public double MidX, MidY, Rotation;
            public string Layer = "Top";
        }

        public class JlcResult
        {
            public int Parts;
            public int Placements;
            public List<string> MissingLcsc = new List<string>();
            public List<string> ParameterNamesSeen = new List<string>();
        }

        public static JlcResult Export(IPCB_ServerInterface pcbServer, IPCB_Board board, string folder)
        {
            JlcResult res = new JlcResult();

            Dictionary<string, Part> parts = new Dictionary<string, Part>(StringComparer.OrdinalIgnoreCase);
            List<Placement> placements = new List<Placement>();
            HashSet<string> paramNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string[]> missing = new List<string[]>();

            IPCB_BoardEx boardEx = board as IPCB_BoardEx;
            if (boardEx == null)
                throw new Exception("This board does not expose IPCB_BoardEx, so component parameters are unreachable.");

            IPCB_FullComponentList list = boardEx.GetState_FullComponents().GetComponentsForCurrentVariant();
            int count = list.GetCount();
            Log.Write("JLC export: " + count + " full component(s)");

            for (int i = 0; i < count; i++)
            {
                IPCB_FullComponent fc = list.GetItem(i);
                if (fc == null) continue;

                string designator = fc.GetDesignator() ?? "";
                if (designator.Length == 0) continue;

                string comment = fc.GetComment() ?? "";
                string description = fc.GetDescription() ?? "";

                // --- parameters: find the LCSC number, and record the names ---
                string lcsc = "";
                string mpnByDesignator = "";
                try
                {
                    IPCB_ParameterList plist = fc.GetParameters();
                    if (plist != null)
                    {
                        int pc = plist.GetCount();
                        Dictionary<string, string> byName =
                            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        for (int p = 0; p < pc; p++)
                        {
                            IPCB_Parameter par = plist.GetByIndex(p);
                            if (par == null) continue;
                            string pn = par.GetName() ?? "";
                            if (pn.Length == 0) continue;
                            paramNames.Add(pn);
                            byName[pn] = par.GetValue() ?? "";
                        }

                        string supplier, mpn;
                        byName.TryGetValue(SupplierParameterName, out supplier);
                        byName.TryGetValue(ManufacturerPartParameterName, out mpn);
                        if (mpn != null) mpnByDesignator = mpn.Trim();

                        foreach (string candidate in LcscParameterNames)
                        {
                            string v;
                            if (!byName.TryGetValue(candidate, out v) || string.IsNullOrWhiteSpace(v)) continue;

                            if (string.Equals(candidate, "Supplier Part", StringComparison.OrdinalIgnoreCase) &&
                                !string.IsNullOrWhiteSpace(supplier) &&
                                supplier.IndexOf("LCSC", StringComparison.OrdinalIgnoreCase) < 0)
                            {
                                // Supplier Part is populated but the supplier is
                                // not LCSC -- do not pass it off as an LCSC code.
                                Log.Write("JLC export: " + designator + " has Supplier='" + supplier +
                                          "' (not LCSC), ignoring its Supplier Part value");
                                continue;
                            }

                            lcsc = v.Trim();
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("JLC export: parameters unreadable for " + designator + " -- " + ex.Message);
                }

                IPCB_Component fp = fc.GetFootprint();
                string footprint = "";
                if (fp != null)
                {
                    try { footprint = fp.GetState_Pattern() ?? ""; } catch { }
                }

                // A component with no LCSC number cannot be quoted OR placed by
                // JLCPCB, so it is kept out of BOTH deliverable files and appears
                // only in bom_missing_lcsc.csv, which exists purely as a checklist.
                // On this board those are the mounting holes, solder pads and the
                // through-hole connector -- real parts, but not JLCPCB's to fit.
                if (lcsc.Length == 0)
                {
                    missing.Add(new string[] { designator, comment, footprint, mpnByDesignator, description });
                    continue;
                }

                // --- BOM grouping: identical Comment + Footprint + LCSC ---
                string key = comment + "\u0001" + footprint + "\u0001" + lcsc;
                Part part;
                if (!parts.TryGetValue(key, out part))
                {
                    part = new Part();
                    part.Comment = comment;
                    part.Footprint = footprint;
                    part.Lcsc = lcsc;
                    part.Description = description;
                    parts[key] = part;
                }
                part.Designators.Add(designator);

                // --- CPL row ---
                if (fp != null)
                {
                    try
                    {
                        CoordRect r = fp.BoundingRectangleNoNameComment();
                        double midX = (MM(r.GetX1()) + MM(r.GetX2())) / 2.0;
                        double midY = (MM(r.GetY1()) + MM(r.GetY2())) / 2.0;

                        Placement pl = new Placement();
                        pl.Designator = designator;
                        pl.MidX = midX;
                        pl.MidY = midY;
                        pl.Rotation = fp.GetState_Rotation();
                        pl.Layer = (fp.GetState_Layer() == TV6_Layer.eV6_BottomLayer) ? "Bottom" : "Top";
                        placements.Add(pl);
                    }
                    catch (Exception ex)
                    {
                        Log.Write("JLC export: no placement for " + designator + " -- " + ex.Message);
                    }
                }
            }

            // ----------------------------- BOM -----------------------------
            List<string> bom = new List<string>();
            bom.Add("Comment,Designator,Footprint,LCSC Part #");
            List<Part> ordered = new List<Part>(parts.Values);
            ordered.Sort(delegate (Part a, Part b)
            {
                int c = string.Compare(a.Comment, b.Comment, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : string.Compare(a.Footprint, b.Footprint, StringComparison.OrdinalIgnoreCase);
            });
            foreach (Part p in ordered)
            {
                p.Designators.Sort(StringComparer.OrdinalIgnoreCase);
                bom.Add(string.Join(",", Csv(p.Comment), Csv(string.Join(",", p.Designators)),
                                         Csv(p.Footprint), Csv(p.Lcsc)));
            }
            File.WriteAllLines(Path.Combine(folder, "bom_jlcpcb.csv"), bom, new UTF8Encoding(false));
            Log.Write("wrote " + Path.Combine(folder, "bom_jlcpcb.csv") + " (" + (bom.Count - 1) + " part row(s))");
            res.Parts = bom.Count - 1;

            // ----------------------------- CPL -----------------------------
            List<string> cpl = new List<string>();
            cpl.Add("Designator,Mid X,Mid Y,Layer,Rotation");
            placements.Sort(delegate (Placement a, Placement b)
            {
                return string.Compare(a.Designator, b.Designator, StringComparison.OrdinalIgnoreCase);
            });
            foreach (Placement p in placements)
            {
                cpl.Add(string.Join(",", Csv(p.Designator), F3(p.MidX), F3(p.MidY), p.Layer, F1(p.Rotation)));
            }
            File.WriteAllLines(Path.Combine(folder, "cpl_jlcpcb.csv"), cpl, new UTF8Encoding(false));
            Log.Write("wrote " + Path.Combine(folder, "cpl_jlcpcb.csv") + " (" + (cpl.Count - 1) + " placement(s))");
            res.Placements = cpl.Count - 1;

            // ------------------------ missing LCSC -------------------------
            List<string> miss = new List<string>();
            miss.Add("Designator,Comment,Footprint,Manufacturer Part,Description");
            missing.Sort(delegate (string[] a, string[] b)
            {
                return string.Compare(a[0], b[0], StringComparison.OrdinalIgnoreCase);
            });
            foreach (string[] m in missing)
            {
                miss.Add(string.Join(",", Csv(m[0]), Csv(m[1]), Csv(m[2]), Csv(m[3]), Csv(m[4])));
                res.MissingLcsc.Add(m[0]);
            }
            File.WriteAllLines(Path.Combine(folder, "bom_missing_lcsc.csv"), miss, new UTF8Encoding(false));
            Log.Write("wrote " + Path.Combine(folder, "bom_missing_lcsc.csv") + " (" + (miss.Count - 1) +
                      " row(s) -- these are EXCLUDED from the BOM and CPL)");

            // Record every parameter name seen, so an unmatched LCSC field
            // can be identified without guessing.
            List<string> names = new List<string>(paramNames);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            res.ParameterNamesSeen = names;
            Log.Write("JLC export: parameter names present on this board: " +
                      (names.Count == 0 ? "(none)" : string.Join(" | ", names)));

            return res;
        }
    }
}
