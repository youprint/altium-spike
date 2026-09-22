// Variants.cs
//
// Reports the design variants and what each one populates.
//
// THE WAY IN IS IPCB_BoardEx, NOT IPCB_Board. There is no variant accessor on
// the plain board interface -- IPCB_DesignVariants exists in the SDK but
// nothing on IPCB_Board hands you one, which is a dead end people hit and
// conclude that variants are unreachable from the PCB side. They are not:
//
//     IPCB_BoardEx.GetState_FullComponents()
//         .GetComponentsForAllVariants()   -> IPCB_FullComponentList
//             [i] -> IPCB_FullComponent
//                      .GetDesignVariant() -> IPCB_DesignVariant (.GetName())
//                      .GetDesignator() / .GetComment() / .GetKind()
//
// so the list is components CROSSED WITH variants, and the variant set falls
// out of it. That is the whole report.
//
// COMPONENT KIND MATTERS FOR A BOM. TComponentKind separates
// eComponentKind_Standard from Standard_NoBOM, Mechanical, Graphical and the
// net-tie kinds. A part marked NoBOM or Graphical is on the board and must
// not be ordered; counting it as populated is how a build ends up short or
// over on parts. The kind is reported per row and summarised per variant.

using DXP;
using PCB;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AltiumSpike
{
    public static class Variants
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static string Csv(string s)
        {
            if (s == null) return "";
            if (s.IndexOf(',') < 0 && s.IndexOf('"') < 0 && s.IndexOf('\n') < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        // Which kinds a purchaser actually orders.
        private static bool CountsForBom(EDP.TComponentKind k)
        {
            return k == EDP.TComponentKind.eComponentKind_Standard
                || k == EDP.TComponentKind.eComponentKind_NetTie_BOM;
        }

        public sealed class Result
        {
            public int VariantCount;
            public int Rows;
            public string CsvPath = "";
            public List<string> Names = new List<string>();
            public List<string> Summary = new List<string>();
            public List<string> Notes = new List<string>();
            public List<string> Errors = new List<string>();
        }

        private sealed class Tally
        {
            public int Total;
            public int Bom;
            public int NoBom;
            public int Mechanical;
            public int Graphical;
        }

        public static Result Report(IPCB_Board board, string folder)
        {
            Result res = new Result();

            IPCB_BoardEx ex = board as IPCB_BoardEx;
            if (ex == null)
            {
                res.Errors.Add("This board does not implement IPCB_BoardEx, so the variant model " +
                               "cannot be reached.");
                return res;
            }

            IPCB_FullComponents full = null;
            try { full = ex.GetState_FullComponents(); }
            catch (Exception e)
            {
                res.Errors.Add("Could not reach the component model -- " + e.GetType().Name + ": " + e.Message);
            }
            if (full == null) { return res; }

            IPCB_FullComponentList list = null;
            try { list = full.GetComponentsForAllVariants(); }
            catch (Exception e)
            {
                res.Errors.Add("Could not read components across variants -- " + e.GetType().Name + ": " + e.Message);
            }

            if (list == null)
            {
                res.Notes.Add("No variant component list on this board. A project with no variants defined " +
                              "reports nothing here, which is normal.");
                return res;
            }

            int count = 0;
            try { count = list.GetCount(); } catch { }

            Dictionary<string, Tally> perVariant =
                new Dictionary<string, Tally>(StringComparer.OrdinalIgnoreCase);

            List<string> rows = new List<string>();
            rows.Add("Variant,Designator,Comment,Description,Kind,InBOM,Library");

            for (int i = 0; i < count; i++)
            {
                try
                {
                    IPCB_FullComponent c = list.GetItem(i);
                    if (c == null) continue;

                    string variant = "(no variant)";
                    try
                    {
                        IPCB_DesignVariant v = c.GetDesignVariant();
                        if (v != null)
                        {
                            string n = v.GetName();
                            if (!string.IsNullOrEmpty(n)) variant = n;
                        }
                    }
                    catch { }

                    string designator = "", comment = "", description = "", library = "";
                    try { designator = c.GetDesignator() ?? ""; } catch { }
                    try { comment = c.GetComment() ?? ""; } catch { }
                    try { description = c.GetDescription() ?? ""; } catch { }
                    try { library = c.GetLibraryName() ?? ""; } catch { }

                    EDP.TComponentKind kind = EDP.TComponentKind.eComponentKind_Standard;
                    try { kind = c.GetKind(); } catch { }

                    bool bom = CountsForBom(kind);

                    Tally t;
                    if (!perVariant.TryGetValue(variant, out t)) { t = new Tally(); perVariant[variant] = t; }
                    t.Total++;
                    if (bom) t.Bom++; else t.NoBom++;
                    if (kind == EDP.TComponentKind.eComponentKind_Mechanical) t.Mechanical++;
                    if (kind == EDP.TComponentKind.eComponentKind_Graphical) t.Graphical++;

                    rows.Add(string.Join(",",
                        Csv(variant), Csv(designator), Csv(comment), Csv(description),
                        kind.ToString(), bom ? "Y" : "N", Csv(library)));
                    res.Rows++;
                }
                catch { }
            }

            List<string> names = new List<string>(perVariant.Keys);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            res.Names = names;
            res.VariantCount = names.Count;

            for (int i = 0; i < names.Count; i++)
            {
                Tally t = perVariant[names[i]];
                string line = names[i] + ": " + t.Bom + " part(s) to order of " + t.Total + " placed";
                if (t.Mechanical > 0 || t.Graphical > 0)
                    line += " (" + t.Mechanical + " mechanical, " + t.Graphical + " graphical)";
                res.Summary.Add(line);
            }

            if (!string.IsNullOrEmpty(folder))
            {
                try
                {
                    string path = Path.Combine(folder, "variant_components.csv");
                    File.WriteAllLines(path, rows, new UTF8Encoding(false));
                    res.CsvPath = path;
                    Log.Write("wrote " + path + " (" + res.Rows + " row(s), " + names.Count + " variant(s))");
                }
                catch (Exception e)
                {
                    res.Errors.Add("Could not write the CSV -- " + e.GetType().Name + ": " + e.Message);
                }
            }

            res.Notes.Add("InBOM counts only Standard and NetTie_BOM kinds. A part marked NoBOM, Mechanical " +
                          "or Graphical is on the board and must not be ordered.");
            return res;
        }
    }
}
