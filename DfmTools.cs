// DfmTools.cs
//
//   SilkOverPads    -- silkscreen printed across exposed copper
//   PasteGrid       -- window-pane the paste on oversized pads
//   NetClassReport  -- nets per class, and the nets in no class at all
//   MechLayerNames  -- which mechanical layers are used and what for
//
// SILK OVER PADS is the DFM check that costs real money. Silkscreen ink on a
// solder pad either gets printed and contaminates the joint, or the fab house
// clips it and calls you about it, or -- worst -- they clip it silently and
// your reference designators come back with holes in them. Altium has a
// solder-mask-to-silk rule but it is off on most boards and it checks mask,
// not pads. This walks the overlay layers and reports every silk primitive
// whose bounding box crosses a pad on the same side.
//
// Bounding boxes, not exact geometry: a rotated designator overlapping the
// corner of a pad is reported even when the ink misses by a hair. That
// direction of error is the right one for a screen -- it gives you a list to
// look at, and a false positive costs a glance while a false negative costs a
// board.
//
// PASTE GRID. A large thermal pad given one big paste aperture floats the
// part on molten solder and tombstones it, or traps voids under it. The fix
// every assembly house asks for is window-paning: replace the one aperture
// with a grid of smaller ones covering 50-80% of the area. This sets the
// pad's own paste expansion hard negative to remove its aperture, then draws
// the grid as fills on the paste layer.
//
// That pad keeps its copper and its mask -- only the paste changes.

using DXP;
using PCB;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AltiumSpike
{
    public static class DfmTools
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static double ToMM(int c) { return EDP.Utils.CoordToMMs(c); }
        private static int ToCoord(double mm) { return EDP.Utils.MMsToCoord(mm); }

        private static string F3(double v)
        {
            double r = Math.Round(v, 3, MidpointRounding.AwayFromZero);
            if (r == 0) r = 0;
            return r.ToString("0.000", Inv);
        }

        private static string Csv(string s)
        {
            if (s == null) return "";
            if (s.IndexOf(',') < 0 && s.IndexOf('"') < 0 && s.IndexOf('\n') < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        public sealed class Result
        {
            public int Scanned, Found, Changed;
            public string CsvPath = "";
            public List<string> Notes = new List<string>();
            public List<string> Errors = new List<string>();
        }

        // ==================================================================
        // Silkscreen over pads
        // ==================================================================
        public static Result SilkOverPads(IPCB_ServerInterface pcbServer, IPCB_Board board,
                                          string folder, bool select)
        {
            Result res = new Result();
            IPCB_LayerUtils lu = pcbServer.LayerUtils();

            // Pads first, split by side. A through-hole pad is exposed on
            // both, so it counts against both overlays.
            List<CoordRect> topPads = new List<CoordRect>(), botPads = new List<CoordRect>();
            List<string> topNames = new List<string>(), botNames = new List<string>();

            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(TObjectId.ePadObject));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);

                IPCB_Pad p = it.FirstPCBObject() as IPCB_Pad;
                while (p != null)
                {
                    try
                    {
                        CoordRect r = p.BoundingRectangle();
                        string nm = "";
                        try { nm = p.GetState_Name() ?? ""; } catch { }

                        bool through = false;
                        try { through = p.GetState_HoleSize() > 0; } catch { }

                        TV6_Layer l = TV6_Layer.eV6_TopLayer;
                        try { l = p.GetState_Layer(); } catch { }

                        if (through || l == TV6_Layer.eV6_TopLayer) { topPads.Add(r); topNames.Add(nm); }
                        if (through || l == TV6_Layer.eV6_BottomLayer) { botPads.Add(r); botNames.Add(nm); }
                    }
                    catch { }
                    p = it.NextPCBObject() as IPCB_Pad;
                }
            }
            catch (Exception ex)
            {
                res.Errors.Add("Pad scan failed -- " + ex.GetType().Name + ": " + ex.Message);
            }
            finally { board.BoardIterator_Destroy(ref it); }

            List<string> rows = new List<string>();
            rows.Add("Layer,SilkKind,Text,PadsCrossed,X,Y");

            List<IPCB_Primitive> hits = new List<IPCB_Primitive>();

            it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(new TObjectId[] {
                    TObjectId.eTrackObject, TObjectId.eArcObject, TObjectId.eTextObject }));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);

                IPCB_Primitive s = it.FirstPCBObject();
                while (s != null)
                {
                    try
                    {
                        TV6_Layer l = s.GetState_Layer();
                        bool top = l == TV6_Layer.eV6_TopOverlay;
                        bool bot = l == TV6_Layer.eV6_BottomOverlay;

                        if (top || bot)
                        {
                            res.Scanned++;
                            CoordRect r = s.BoundingRectangle();
                            List<CoordRect> pads = top ? topPads : botPads;
                            List<string> names = top ? topNames : botNames;

                            int crossed = 0;
                            List<string> which = new List<string>();
                            for (int i = 0; i < pads.Count; i++)
                            {
                                if (Overlaps(r, pads[i]))
                                {
                                    crossed++;
                                    if (which.Count < 4 && names[i].Length > 0) which.Add(names[i]);
                                }
                            }

                            if (crossed > 0)
                            {
                                string kind = s.GetState_ObjectID().ToString();
                                string text = "";
                                IPCB_Text t = s as IPCB_Text;
                                if (t != null) { try { text = t.GetState_Text() ?? ""; } catch { } }

                                rows.Add(string.Join(",",
                                    Csv(lu.AsString(s.GetState_V7Layer())),
                                    Csv(kind), Csv(text), crossed.ToString(Inv),
                                    F3(ToMM(r.GetX1())), F3(ToMM(r.GetY1()))));

                                hits.Add(s);
                                res.Found++;
                            }
                        }
                    }
                    catch { }
                    s = it.NextPCBObject();
                }
            }
            catch (Exception ex)
            {
                res.Errors.Add("Silkscreen scan failed -- " + ex.GetType().Name + ": " + ex.Message);
            }
            finally { board.BoardIterator_Destroy(ref it); }

            if (select && hits.Count > 0) Select(board, hits, res);

            if (!string.IsNullOrEmpty(folder))
            {
                try
                {
                    string path = Path.Combine(folder, "silk_over_pads.csv");
                    File.WriteAllLines(path, rows, new UTF8Encoding(false));
                    res.CsvPath = path;
                    Log.Write("wrote " + path + " (" + res.Found + " row(s))");
                }
                catch (Exception ex)
                {
                    res.Errors.Add("Could not write the CSV -- " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            res.Notes.Add(res.Found == 0
                ? "No silkscreen crosses a pad (" + res.Scanned + " silk primitives checked)."
                : res.Found + " silk primitive(s) of " + res.Scanned + " cross a pad — selected on the board.");
            res.Notes.Add("Bounding boxes are used, so a near miss on a rotated designator is reported. " +
                          "That is the safe direction to err.");
            return res;
        }

        private static bool Overlaps(CoordRect a, CoordRect b)
        {
            return !(a.GetX2() < b.GetX1() || a.GetX1() > b.GetX2() ||
                     a.GetY2() < b.GetY1() || a.GetY1() > b.GetY2());
        }

        // ==================================================================
        // Paste grid
        // ==================================================================
        public sealed class PasteOptions
        {
            public double MinPadMM = 3.0;        // only pads at least this big in both axes
            public double CoveragePercent = 60;  // paste area as a share of pad area
            public int Divisions = 3;            // grid is Divisions x Divisions
            public bool OnlySelection = false;
        }

        public static Result PasteGrid(IPCB_ServerInterface pcbServer, IPCB_Board board, PasteOptions opt)
        {
            Result res = new Result();

            if (opt.MinPadMM <= 0) { res.Errors.Add("Minimum pad size must be greater than 0."); return res; }
            if (opt.Divisions < 2 || opt.Divisions > 10)
            { res.Errors.Add("Divisions must be between 2 and 10."); return res; }
            if (opt.CoveragePercent <= 0 || opt.CoveragePercent >= 100)
            { res.Errors.Add("Coverage must be between 0 and 100 percent."); return res; }

            IPCB_LayerUtils lu = pcbServer.LayerUtils();
            IV7_Layer topPaste = lu.FromString("Top Paste");
            IV7_Layer botPaste = lu.FromString("Bottom Paste");

            List<IPCB_Pad> targets = new List<IPCB_Pad>();

            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(TObjectId.ePadObject));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);

                IPCB_Pad p = it.FirstPCBObject() as IPCB_Pad;
                while (p != null)
                {
                    try
                    {
                        res.Scanned++;
                        if (opt.OnlySelection && !p.GetState_Selected()) { p = it.NextPCBObject() as IPCB_Pad; continue; }

                        // Surface mount only: a through-hole pad has no paste
                        // aperture worth window-paning.
                        if (p.GetState_HoleSize() > 0) { p = it.NextPCBObject() as IPCB_Pad; continue; }

                        CoordRect r = p.BoundingRectangle();
                        double w = ToMM(r.GetX2() - r.GetX1());
                        double h = ToMM(r.GetY2() - r.GetY1());

                        if (w >= opt.MinPadMM && h >= opt.MinPadMM) targets.Add(p);
                    }
                    catch { }
                    p = it.NextPCBObject() as IPCB_Pad;
                }
            }
            catch (Exception ex)
            {
                res.Errors.Add("Pad scan failed -- " + ex.GetType().Name + ": " + ex.Message);
            }
            finally { board.BoardIterator_Destroy(ref it); }

            res.Found = targets.Count;
            if (targets.Count == 0)
            {
                res.Notes.Add("No surface-mount pad is at least " + F3(opt.MinPadMM) +
                              " mm in both axes (" + res.Scanned + " pads scanned).");
                return res;
            }

            // Each aperture is a square whose total area over the grid gives
            // the requested coverage.
            double frac = Math.Sqrt(opt.CoveragePercent / 100.0) / opt.Divisions;

            pcbServer.PreProcess();
            try
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    try
                    {
                        IPCB_Pad pad = targets[i];
                        CoordRect r = pad.BoundingRectangle();
                        double x1 = ToMM(r.GetX1()), y1 = ToMM(r.GetY1());
                        double w = ToMM(r.GetX2() - r.GetX1());
                        double h = ToMM(r.GetY2() - r.GetY1());

                        TV6_Layer side = TV6_Layer.eV6_TopLayer;
                        try { side = pad.GetState_Layer(); } catch { }
                        IV7_Layer paste = side == TV6_Layer.eV6_BottomLayer ? botPaste : topPaste;

                        // Remove the pad's own aperture. A negative expansion
                        // of half the pad closes it; flagged manual or the
                        // rule puts it straight back.
                        V7_PadCache c = pad.GetState_Cache();
                        c.PasteMaskExpansion = ToCoord(-(Math.Max(w, h) / 2.0 + 0.05));
                        c.PasteMaskExpansionValid = TCacheState.eCacheManual;
                        pad.SetState_Cache(c);

                        double aw = w * frac, ah = h * frac;
                        double stepX = w / opt.Divisions, stepY = h / opt.Divisions;

                        for (int gx = 0; gx < opt.Divisions; gx++)
                        {
                            for (int gy = 0; gy < opt.Divisions; gy++)
                            {
                                double cx = x1 + stepX * (gx + 0.5);
                                double cy = y1 + stepY * (gy + 0.5);

                                IPCB_Fill f = pcbServer.PCBObjectFactory(
                                    TObjectId.eFillObject, TDimensionKind.eNoDimension,
                                    TObjectCreationMode.eCreate_Default) as IPCB_Fill;

                                // Fill location is the LOWER-LEFT corner, and
                                // Length is the X extent, Width the Y extent.
                                f.SetState_LocationX(ToCoord(cx - aw / 2.0));
                                f.SetState_LocationY(ToCoord(cy - ah / 2.0));
                                f.SetState_Length(ToCoord(aw));
                                f.SetState_Width(ToCoord(ah));
                                f.SetState_V7Layer(paste);

                                board.AddPCBObject(f);
                            }
                        }

                        res.Changed++;
                    }
                    catch (Exception ex)
                    {
                        res.Errors.Add("Pad " + i + ": " + ex.GetType().Name + " -- " + ex.Message);
                    }
                }
            }
            finally { pcbServer.PostProcess(); }

            board.ViewManager_FullUpdate();
            res.Notes.Add(res.Changed + " pad(s) window-paned into a " + opt.Divisions + "x" +
                          opt.Divisions + " grid at " + opt.CoveragePercent.ToString("0", Inv) + "% coverage.");
            res.Notes.Add("Copper and solder mask are unchanged — only the paste aperture.");
            Log.Write("DfmTools.PasteGrid: " + res.Changed + " pads");
            return res;
        }

        // ==================================================================
        // Net class report
        // ==================================================================
        public static Result NetClassReport(IPCB_Board board, string folder)
        {
            Result res = new Result();

            List<IPCB_ObjectClass> classes = new List<IPCB_ObjectClass>();
            List<string> classNames = new List<string>();

            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(TObjectId.eClassObject));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);

                IPCB_ObjectClass c = it.FirstPCBObject() as IPCB_ObjectClass;
                while (c != null)
                {
                    try
                    {
                        if (c.GetState_MemberKind() == TClassMemberKind.eClassMemberKind_Net)
                        {
                            classes.Add(c);
                            classNames.Add(c.GetState_Name() ?? "");
                        }
                    }
                    catch { }
                    c = it.NextPCBObject() as IPCB_ObjectClass;
                }
            }
            catch (Exception ex)
            {
                res.Errors.Add("Class scan failed -- " + ex.GetType().Name + ": " + ex.Message);
            }
            finally { board.BoardIterator_Destroy(ref it); }

            List<string> rows = new List<string>();
            rows.Add("Net,Classes,PinCount,InAnyClass");

            Dictionary<string, int> perClass = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int unclassified = 0;

            it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(TObjectId.eNetObject));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);

                IPCB_Net n = it.FirstPCBObject() as IPCB_Net;
                while (n != null)
                {
                    try
                    {
                        string name = n.GetState_Name() ?? "";
                        if (name.Length > 0)
                        {
                            res.Scanned++;
                            List<string> mine = new List<string>();
                            for (int i = 0; i < classes.Count; i++)
                            {
                                // "All Nets" contains everything and tells you
                                // nothing, so it does not count as membership.
                                if (string.Equals(classNames[i], "All Nets", StringComparison.OrdinalIgnoreCase))
                                    continue;
                                try { if (classes[i].IsMember(name)) mine.Add(classNames[i]); } catch { }
                            }

                            foreach (string cn in mine)
                            {
                                int v; perClass.TryGetValue(cn, out v); perClass[cn] = v + 1;
                            }
                            if (mine.Count == 0) unclassified++;

                            rows.Add(string.Join(",",
                                Csv(name), Csv(string.Join(";", mine.ToArray())),
                                n.GetState_PinCount().ToString(Inv),
                                mine.Count > 0 ? "Y" : "N"));
                        }
                    }
                    catch { }
                    n = it.NextPCBObject() as IPCB_Net;
                }
            }
            catch (Exception ex)
            {
                res.Errors.Add("Net scan failed -- " + ex.GetType().Name + ": " + ex.Message);
            }
            finally { board.BoardIterator_Destroy(ref it); }

            List<string> keys = new List<string>(perClass.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < keys.Count; i++)
                rows.Add(string.Join(",", "CLASS TOTAL", Csv(keys[i]), perClass[keys[i]].ToString(Inv), ""));

            if (!string.IsNullOrEmpty(folder))
            {
                try
                {
                    string path = Path.Combine(folder, "net_classes.csv");
                    File.WriteAllLines(path, rows, new UTF8Encoding(false));
                    res.CsvPath = path;
                    Log.Write("wrote " + path);
                }
                catch (Exception ex)
                {
                    res.Errors.Add("Could not write the CSV -- " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            res.Found = keys.Count;
            res.Notes.Add(res.Scanned + " nets across " + keys.Count + " class(es).");
            res.Notes.Add(unclassified + " net(s) belong to no class — every rule scoped by class misses them.");
            return res;
        }

        // ==================================================================
        // Mechanical layer usage
        // ==================================================================
        public static Result MechLayerNames(IPCB_ServerInterface pcbServer, IPCB_Board board, string folder)
        {
            Result res = new Result();
            IPCB_LayerUtils lu = pcbServer.LayerUtils();

            Dictionary<string, int> used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(new TObjectId[] {
                    TObjectId.eTrackObject, TObjectId.eArcObject, TObjectId.eTextObject,
                    TObjectId.eFillObject, TObjectId.eRegionObject, TObjectId.eDimensionObject }));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);

                IPCB_Primitive p = it.FirstPCBObject();
                while (p != null)
                {
                    try
                    {
                        IV7_Layer l = p.GetState_V7Layer();
                        if (lu.IsMechanicalLayer(l))
                        {
                            string nm = lu.AsString(l);
                            int v; used.TryGetValue(nm, out v); used[nm] = v + 1;
                            res.Scanned++;
                        }
                    }
                    catch { }
                    p = it.NextPCBObject();
                }
            }
            catch (Exception ex)
            {
                res.Errors.Add("Scan failed -- " + ex.GetType().Name + ": " + ex.Message);
            }
            finally { board.BoardIterator_Destroy(ref it); }

            List<string> rows = new List<string>();
            rows.Add("MechanicalLayer,Primitives");

            List<string> keys = new List<string>(used.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < keys.Count; i++)
                rows.Add(Csv(keys[i]) + "," + used[keys[i]].ToString(Inv));

            if (!string.IsNullOrEmpty(folder))
            {
                try
                {
                    string path = Path.Combine(folder, "mechanical_layers.csv");
                    File.WriteAllLines(path, rows, new UTF8Encoding(false));
                    res.CsvPath = path;
                    Log.Write("wrote " + path);
                }
                catch (Exception ex)
                {
                    res.Errors.Add("Could not write the CSV -- " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            res.Found = keys.Count;
            res.Notes.Add(keys.Count + " mechanical layer(s) carry content, " + res.Scanned +
                          " primitive(s) in total.");
            if (keys.Count > 0)
                res.Notes.Add("In use: " + string.Join(", ", keys.ToArray()));
            res.Notes.Add("Layers with nothing on them are omitted — a fab package listing every empty " +
                          "mechanical layer is how the wrong one ends up in the Gerber set.");
            return res;
        }

        private static void Select(IPCB_Board board, List<IPCB_Primitive> items, Result res)
        {
            try
            {
                board.SelectedObjects_BeginUpdate();
                board.SelectedObjects_Clear();
                for (int i = 0; i < items.Count; i++) board.SelectedObjects_Add(items[i]);
                board.SelectedObjects_EndUpdate();
                board.ViewManager_FullUpdate();
            }
            catch (Exception ex)
            {
                res.Errors.Add("Could not select the results -- " + ex.GetType().Name);
            }
        }
    }
}
