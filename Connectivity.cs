// Connectivity.cs
//
//   UnnettedCopper -- copper that belongs to no net at all
//   MeasureNets    -- routed length per net, measured from the copper itself
//
// WHY THIS EXISTS. A self-test run against a real imported board reported 313
// tracks, 39 nets, and then: zero nets rated for current, zero endpoints
// scanned for dangling copper, zero routed length on every net, zero pin
// pairs. Four separate functions producing nothing, and all four for the same
// reason -- every track on that board carried NO NET. The pads had nets. The
// copper did not.
//
// That is a serious condition and Altium is quiet about it. Unnetted copper
// looks completely normal on screen. It is not checked by any clearance or
// width rule that is scoped by net or class, it contributes nothing to routed
// length, it does not connect anything as far as the connectivity model is
// concerned, and it will not be reported as unrouted either -- because the
// nets it should belong to have no copper to be unrouted from. It comes from
// imports, from copy-paste between documents, and from drawing tracks with no
// net attached.
//
// So the first function names it: how much copper has no net, on which layers,
// and where. Its CSV is what you take to Design > Netlist > Update Free
// Primitives From Component Pads, which is the fix on Altium's side.
//
// MEASURED LENGTH, NOT REPORTED LENGTH. The second function exists because
// IPCB_Net's own RoutedLength came back as exactly 0.000 for all 39 nets on
// that same board while SignalLength carried real millimetres for some of
// them. Rather than trust either, this sums the geometry: track lengths by
// Pythagoras, arc lengths by radius and sweep. It needs no connectivity
// analysis and it can be checked by hand against a ruler on screen.
//
// The two numbers answer different questions and the report carries both. A
// net whose measured length is far from its reported length is telling you
// its copper and its connectivity model disagree -- which is exactly the
// condition above.

using DXP;
using PCB;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AltiumSpike
{
    public static class Connectivity
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static double ToMM(int c) { return EDP.Utils.CoordToMMs(c); }

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
            public int Tracks, Arcs, Vias, Fills, Regions, Polygons, Pads;
            public double UnnettedLengthMM;
            public string CsvPath = "";
            public List<string> Notes = new List<string>();
            public List<string> Errors = new List<string>();
        }

        // ==================================================================
        // Copper with no net
        // ==================================================================
        public static Result UnnettedCopper(IPCB_ServerInterface pcbServer, IPCB_Board board,
                                            string folder, bool select)
        {
            Result res = new Result();
            IPCB_LayerUtils lu = pcbServer.LayerUtils();

            List<string> rows = new List<string>();
            rows.Add("Kind,Layer,X mm,Y mm,Length mm,Width mm");

            Dictionary<string, int> perLayer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<IPCB_Primitive> orphans = new List<IPCB_Primitive>();

            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(new TObjectId[] {
                    TObjectId.eTrackObject, TObjectId.eArcObject, TObjectId.eViaObject,
                    TObjectId.eFillObject, TObjectId.eRegionObject, TObjectId.ePolyObject,
                    TObjectId.ePadObject }));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);

                IPCB_Primitive p = it.FirstPCBObject();
                while (p != null)
                {
                    try
                    {
                        string layer = "";
                        try { layer = lu.AsString(p.GetState_V7Layer()); } catch { }

                        // Only copper layers count. An unnetted track on the
                        // overlay is a drawing and entirely normal; reporting
                        // those would bury the finding in silkscreen.
                        if (!IsCopperLayer(layer)) { p = it.NextPCBObject(); continue; }

                        res.Scanned++;

                        string net = "";
                        try
                        {
                            IPCB_Net n = p.GetState_Net();
                            if (n != null) net = n.GetState_Name() ?? "";
                        }
                        catch { }

                        if (net.Length > 0) { p = it.NextPCBObject(); continue; }

                        TObjectId id = p.GetState_ObjectID();
                        string kind = "";
                        double x = 0, y = 0, len = 0, w = 0;

                        if (id == TObjectId.eTrackObject)
                        {
                            IPCB_Track t = p as IPCB_Track;
                            if (t == null) { p = it.NextPCBObject(); continue; }
                            kind = "Track";
                            double x1 = ToMM(t.GetState_X1()), y1 = ToMM(t.GetState_Y1());
                            double x2 = ToMM(t.GetState_X2()), y2 = ToMM(t.GetState_Y2());
                            x = x1; y = y1;
                            len = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
                            w = ToMM(t.GetState_Width());
                            res.Tracks++;
                        }
                        else if (id == TObjectId.eArcObject)
                        {
                            IPCB_Arc a = p as IPCB_Arc;
                            if (a == null) { p = it.NextPCBObject(); continue; }
                            kind = "Arc";
                            x = ToMM(a.GetState_StartX()); y = ToMM(a.GetState_StartY());
                            len = PolyGeometry.ArcLength(ToMM(a.GetState_Radius()),
                                                         a.GetState_StartAngle(), a.GetState_EndAngle());
                            w = ToMM(a.GetState_LineWidth());
                            res.Arcs++;
                        }
                        else if (id == TObjectId.eViaObject)
                        {
                            IPCB_Via v = p as IPCB_Via;
                            if (v == null) { p = it.NextPCBObject(); continue; }
                            kind = "Via";
                            x = ToMM(v.GetState_XLocation()); y = ToMM(v.GetState_YLocation());
                            w = ToMM(v.GetState_Size());
                            res.Vias++;
                        }
                        else if (id == TObjectId.ePadObject)
                        {
                            IPCB_Pad pad = p as IPCB_Pad;
                            if (pad == null) { p = it.NextPCBObject(); continue; }
                            kind = "Pad";
                            x = ToMM(pad.GetState_XLocation()); y = ToMM(pad.GetState_YLocation());
                            res.Pads++;
                        }
                        else if (id == TObjectId.eFillObject)
                        {
                            IPCB_Fill f = p as IPCB_Fill;
                            if (f == null) { p = it.NextPCBObject(); continue; }
                            kind = "Fill";
                            x = ToMM(f.GetState_LocationX()); y = ToMM(f.GetState_LocationY());
                            res.Fills++;
                        }
                        else if (id == TObjectId.ePolyObject)
                        {
                            kind = "Polygon";
                            CoordRect r = p.BoundingRectangle();
                            x = ToMM(r.GetX1()); y = ToMM(r.GetY1());
                            res.Polygons++;
                        }
                        else
                        {
                            kind = "Region";
                            CoordRect r = p.BoundingRectangle();
                            x = ToMM(r.GetX1()); y = ToMM(r.GetY1());
                            res.Regions++;
                        }

                        res.UnnettedLengthMM += len;
                        res.Found++;
                        orphans.Add(p);

                        int c;
                        perLayer.TryGetValue(layer, out c);
                        perLayer[layer] = c + 1;

                        rows.Add(string.Join(",", kind, Csv(layer), F3(x), F3(y),
                                             len > 0 ? F3(len) : "", w > 0 ? F3(w) : ""));
                    }
                    catch { }
                    p = it.NextPCBObject();
                }
            }
            catch (Exception ex)
            {
                res.Errors.Add("Copper scan failed -- " + ex.GetType().Name + ": " + ex.Message);
            }
            finally { board.BoardIterator_Destroy(ref it); }

            if (res.Found == 0)
                res.Notes.Add("Every copper primitive on this board belongs to a net.");
            else
            {
                res.Notes.Add(res.Found + " of " + res.Scanned + " copper primitive(s) carry NO NET.");

                List<string> parts = new List<string>();
                if (res.Tracks > 0) parts.Add(res.Tracks + " track(s)");
                if (res.Arcs > 0) parts.Add(res.Arcs + " arc(s)");
                if (res.Vias > 0) parts.Add(res.Vias + " via(s)");
                if (res.Pads > 0) parts.Add(res.Pads + " pad(s)");
                if (res.Fills > 0) parts.Add(res.Fills + " fill(s)");
                if (res.Polygons > 0) parts.Add(res.Polygons + " polygon(s)");
                if (res.Regions > 0) parts.Add(res.Regions + " region(s)");
                res.Notes.Add(string.Join(", ", parts.ToArray()) + ".");

                if (res.UnnettedLengthMM > 0)
                    res.Notes.Add(F3(res.UnnettedLengthMM) + " mm of copper length is unaccounted for.");

                List<string> ls = new List<string>(perLayer.Keys);
                ls.Sort(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < ls.Count; i++)
                    res.Notes.Add("  " + ls[i] + ": " + perLayer[ls[i]]);

                res.Notes.Add("This copper is invisible to every rule scoped by net or class, contributes " +
                              "nothing to routed length, and connects nothing as far as Altium is concerned.");
                res.Notes.Add("Fix it with Design > Netlist > Update Free Primitives From Component Pads.");
            }

            if (select && orphans.Count > 0) Select(board, orphans, res);
            WriteCsv(folder, "unnetted_copper.csv", rows, res);
            return res;
        }

        // Copper layers by the only test that holds on a renamed stack: ask
        // the board's own layer stack which layers are electrical.
        private static HashSet<string> copperNames;

        public static void ResetLayerCache() { copperNames = null; }

        private static bool IsCopperLayer(string layer)
        {
            if (copperNames == null) return DefaultCopperGuess(layer);
            return copperNames.Contains(layer);
        }

        private static bool DefaultCopperGuess(string layer)
        {
            if (string.IsNullOrEmpty(layer)) return false;
            string l = layer.ToLowerInvariant();
            if (l.Contains("overlay") || l.Contains("paste") || l.Contains("solder") ||
                l.Contains("mechanical") || l.Contains("keep") || l.Contains("drill") ||
                l.Contains("outline")) return false;
            return l.Contains("layer") || l.Contains("plane") || l.Contains("mid") || l.Contains("internal");
        }

        public static void CacheCopperLayers(IPCB_ServerInterface pcbServer, IPCB_Board board)
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                IPCB_LayerStack stack = board.GetState_LayerStack();
                if (stack == null) return;
                IPCB_LayerUtils lu = pcbServer.LayerUtils();

                IPCB_LayerObject lo = stack.First(TLayerClassID.eLayerClass_Electrical);
                int guard = 0;
                while (lo != null && guard++ < 256)
                {
                    try { set.Add(lu.AsString(lo.V7_LayerID())); } catch { }
                    lo = stack.Next(TLayerClassID.eLayerClass_Electrical, lo);
                }
            }
            catch { }

            if (set.Count > 0) copperNames = set;
        }

        // ==================================================================
        // Measured routed length per net
        // ==================================================================
        public static Result MeasureNets(IPCB_ServerInterface pcbServer, IPCB_Board board, string folder)
        {
            Result res = new Result();
            IPCB_LayerUtils lu = pcbServer.LayerUtils();

            Dictionary<string, double> lengthOf =
                new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> segsOf =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(new TObjectId[] {
                    TObjectId.eTrackObject, TObjectId.eArcObject }));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);

                IPCB_Primitive p = it.FirstPCBObject();
                while (p != null)
                {
                    try
                    {
                        string layer = "";
                        try { layer = lu.AsString(p.GetState_V7Layer()); } catch { }
                        if (!IsCopperLayer(layer)) { p = it.NextPCBObject(); continue; }

                        string net = "";
                        try
                        {
                            IPCB_Net n = p.GetState_Net();
                            if (n != null) net = n.GetState_Name() ?? "";
                        }
                        catch { }
                        if (net.Length == 0) { p = it.NextPCBObject(); continue; }

                        double len = 0;
                        if (p.GetState_ObjectID() == TObjectId.eTrackObject)
                        {
                            IPCB_Track t = p as IPCB_Track;
                            if (t == null) { p = it.NextPCBObject(); continue; }
                            double dx = ToMM(t.GetState_X2()) - ToMM(t.GetState_X1());
                            double dy = ToMM(t.GetState_Y2()) - ToMM(t.GetState_Y1());
                            len = Math.Sqrt(dx * dx + dy * dy);
                        }
                        else
                        {
                            IPCB_Arc a = p as IPCB_Arc;
                            if (a == null) { p = it.NextPCBObject(); continue; }
                            len = PolyGeometry.ArcLength(ToMM(a.GetState_Radius()),
                                                         a.GetState_StartAngle(), a.GetState_EndAngle());
                        }

                        double acc;
                        lengthOf.TryGetValue(net, out acc);
                        lengthOf[net] = acc + len;

                        int c;
                        segsOf.TryGetValue(net, out c);
                        segsOf[net] = c + 1;

                        res.Scanned++;
                    }
                    catch { }
                    p = it.NextPCBObject();
                }
            }
            catch (Exception ex)
            {
                res.Errors.Add("Copper scan failed -- " + ex.GetType().Name + ": " + ex.Message);
            }
            finally { board.BoardIterator_Destroy(ref it); }

            // Every net on the board, so a net with no copper shows as 0 rather
            // than being missing.
            List<string> rows = new List<string>();
            rows.Add("Net,Segments,MeasuredLengthMM,ReportedRoutedLengthMM,Agreement");

            int disagree = 0, noCopper = 0;

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
                            double measured;
                            lengthOf.TryGetValue(name, out measured);
                            int segs;
                            segsOf.TryGetValue(name, out segs);

                            double reported = 0;
                            try { reported = ToMM(n.GetState_RoutedLength()); } catch { }

                            string verdict;
                            if (segs == 0)
                            {
                                verdict = "NO COPPER ON THIS NET";
                                noCopper++;
                            }
                            else if (reported <= 0.0005)
                            {
                                verdict = "MEASURED ONLY - Altium reports zero";
                                disagree++;
                            }
                            else if (Math.Abs(measured - reported) > Math.Max(0.05, reported * 0.02))
                            {
                                verdict = "DISAGREE";
                                disagree++;
                            }
                            else verdict = "agree";

                            rows.Add(string.Join(",", Csv(name), segs.ToString(Inv),
                                                 F3(measured), F3(reported), verdict));
                            res.Found++;
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

            res.Notes.Add(res.Found + " net(s), " + res.Scanned + " netted copper segment(s) measured.");
            if (noCopper > 0)
                res.Notes.Add(noCopper + " net(s) have no copper at all on them.");
            if (disagree > 0)
                res.Notes.Add(disagree + " net(s) where the measured length and Altium's reported routed " +
                              "length disagree. Measured is the geometry; reported comes from the " +
                              "connectivity model, which is stale or empty when copper carries no net.");
            if (disagree == 0 && noCopper == 0)
                res.Notes.Add("Measured and reported lengths agree on every net.");

            WriteCsv(folder, "net_lengths_measured.csv", rows, res);
            return res;
        }

        // ==================================================================
        private static void WriteCsv(string folder, string name, List<string> rows, Result res)
        {
            if (string.IsNullOrEmpty(folder)) return;
            try
            {
                string path = Path.Combine(folder, name);
                File.WriteAllLines(path, rows, new UTF8Encoding(false));
                res.CsvPath = path;
                Log.Write("wrote " + path + " (" + (rows.Count - 1) + " row(s))");
            }
            catch (Exception ex)
            {
                res.Errors.Add("Could not write " + name + " -- " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Select(IPCB_Board board, List<IPCB_Primitive> items, Result res)
        {
            if (items.Count == 0) return;
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
