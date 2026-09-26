// CopperCurrent.cs
//
// Two copper reports:
//
//   CurrentCapacity  -- IPC-2221 current rating for every routed net, driven
//                       by its NARROWEST track and the real copper weight of
//                       the layer that track is on
//   CopperAreas      -- polygon and region area per layer and per net
//
// WHY THE NARROWEST TRACK. A net's current capacity is set by its worst
// point, not its average. A power net that is 2 mm wide for 40 mm and 0.2 mm
// wide through one escape from a BGA is a 0.2 mm net as far as heating is
// concerned, and that escape is exactly the bit nobody looks at. Reporting
// the minimum, with the layer and coordinates of where it occurs, points at
// the place that will actually get hot.
//
// WHY THE LAYER MATTERS. IPC-2221 uses k=0.048 on an external layer and
// k=0.024 on an internal one, because a buried conductor can only lose heat
// by conduction through laminate. Using the external constant everywhere
// overstates an inner-layer track by roughly 2x. So each track is rated
// against its own layer's copper weight AND its own inside/outside status,
// both read from the real stackup rather than assumed.
//
// The rating is a design check, not a guarantee -- see the note in
// Ipc2221.cs about still air and isolated conductors.

using DXP;
using PCB;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AltiumSpike
{
    public static class CopperCurrent
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static double ToMM(int coord) { return EDP.Utils.CoordToMMs(coord); }

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

        // ==================================================================
        // Layer copper weights, read once from the stackup
        //
        // Outer/inner status comes from position in the ELECTRICAL stack:
        // the first and last electrical layers are the outside ones. This is
        // more reliable than matching on the names "Top Layer"/"Bottom Layer",
        // which a renamed stack will not have.
        // ==================================================================
        private sealed class LayerInfo
        {
            public double ThicknessMM;
            public bool External;
        }

        private static Dictionary<string, LayerInfo> ReadLayers(IPCB_ServerInterface pcbServer,
                                                                IPCB_Board board, List<string> errors)
        {
            Dictionary<string, LayerInfo> map =
                new Dictionary<string, LayerInfo>(StringComparer.OrdinalIgnoreCase);

            try
            {
                IPCB_LayerStack stack = board.GetState_LayerStack();
                if (stack == null) { errors.Add("This board has no layer stack."); return map; }

                IPCB_LayerUtils lu = pcbServer.LayerUtils();

                List<string> order = new List<string>();
                IPCB_LayerObject lo = stack.First(TLayerClassID.eLayerClass_Electrical);
                int guard = 0;
                while (lo != null && guard++ < 256)
                {
                    // Keyed on the SAME spelling the primitives report, via
                    // LayerUtils, rather than on the layer object's own name.
                    // A renamed stack makes those two differ, and the lookup
                    // would then silently miss every track.
                    string name = "?";
                    try { name = lu.AsString(lo.V7_LayerID()); } catch { }

                    LayerInfo li = new LayerInfo();
                    IPCB_ElectricalLayer el = lo as IPCB_ElectricalLayer;
                    if (el != null)
                    {
                        try { li.ThicknessMM = ToMM(el.GetState_CopperThickness()); } catch { }
                    }

                    if (!map.ContainsKey(name)) { map[name] = li; order.Add(name); }
                    lo = stack.Next(TLayerClassID.eLayerClass_Electrical, lo);
                }

                // First and last electrical layers are the outer ones.
                if (order.Count > 0)
                {
                    map[order[0]].External = true;
                    map[order[order.Count - 1]].External = true;
                }

                Log.Write("CopperCurrent: read " + order.Count + " electrical layer(s) from the stack");
            }
            catch (Exception ex)
            {
                errors.Add("Could not read the layer stack -- " + ex.GetType().Name + ": " + ex.Message);
            }

            return map;
        }

        // ==================================================================
        // Current capacity per net
        // ==================================================================
        public sealed class CurrentOptions
        {
            public double TempRiseC = 10.0;
            public double TargetAmps = 0.0;        // 0 = report only, no pass/fail
            public double DefaultThicknessMM = 0.0348;  // 1 oz, when the stack will not say
            public bool OnlySelection = false;
            public bool SelectFailures = true;
            public string OutputFolder = null;
        }

        private sealed class NetWorst
        {
            public string Net;
            public double WidthMM = double.MaxValue;
            public string Layer = "";
            public double X, Y;
            public double ThicknessMM;
            public bool External;
            public int Segments;
        }

        public sealed class CurrentResult
        {
            public int TracksScanned;
            public int NetsReported;
            public int Failures;
            public int SkippedNoNet;        // copper carrying no net at all
            public int SkippedOffCopper;    // tracks on overlay / mechanical layers
            public string CsvPath = "";
            public string WorstNet = "";
            public double WorstAmps;
            public bool UsedDefaultThickness;
            public List<string> Errors = new List<string>();

            public string Summarise(CurrentOptions opt)
            {
                string s = "Rated " + NetsReported + " net(s) from " + TracksScanned +
                           " copper segment(s) at +" + opt.TempRiseC.ToString("0.#", Inv) + " C rise.\n";
                if (WorstNet.Length > 0)
                    s += "Lowest capacity: " + WorstNet + " at " + F3(WorstAmps) + " A.\n";
                if (opt.TargetAmps > 0.0)
                {
                    s += Failures + " net(s) below the " + opt.TargetAmps.ToString("0.###", Inv) + " A target";
                    if (Failures > 0 && opt.SelectFailures) s += " (their narrowest tracks are selected)";
                    s += ".\n";
                }
                if (SkippedNoNet > 0)
                    s += SkippedNoNet + " primitive(s) ON A COPPER LAYER carry no net and could not be rated -- " +
                         "current capacity is reported per net, and copper with no net belongs to none. " +
                         "Run the unnetted copper report in Cleanup to see what they are.\n";
                if (SkippedOffCopper > 0)
                    s += SkippedOffCopper + " track(s) and arc(s) are on non-copper layers (silkscreen, " +
                         "mechanical) and were ignored -- they are drawings, not conductors.\n";
                if (TracksScanned == 0 && SkippedOffCopper > 0 && SkippedNoNet == 0)
                    s += "NOTHING ON THIS BOARD'S COPPER LAYERS WAS FOUND TO RATE. If that is a surprise, " +
                         "the board is not routed.\n";
                if (UsedDefaultThickness)
                    s += "Some layers reported no copper thickness; " +
                         opt.DefaultThicknessMM.ToString("0.####", Inv) + " mm was assumed for those.\n";
                if (CsvPath.Length > 0) s += "Wrote " + CsvPath + "\n";
                foreach (string e in Errors) s += e + "\n";
                return s;
            }
        }

        public static CurrentResult CurrentCapacity(IPCB_ServerInterface pcbServer, IPCB_Board board,
                                                    CurrentOptions opt)
        {
            CurrentResult res = new CurrentResult();

            if (opt.TempRiseC <= 0.0)
            { res.Errors.Add("Temperature rise must be greater than 0."); return res; }

            Dictionary<string, LayerInfo> layers = ReadLayers(pcbServer, board, res.Errors);
            IPCB_LayerUtils lu = pcbServer.LayerUtils();
            Dictionary<string, NetWorst> worst =
                new Dictionary<string, NetWorst>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, IPCB_Primitive> worstPrim =
                new Dictionary<string, IPCB_Primitive>(StringComparer.OrdinalIgnoreCase);

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
                        if (opt.OnlySelection && !p.GetState_Selected()) { p = it.NextPCBObject(); continue; }

                        // LAYER FIRST, THEN NET. Counting unnetted primitives
                        // before checking the layer counted every silkscreen
                        // line on the board as "copper with no net" -- 414 of
                        // them on a board whose copper layers hold exactly one
                        // primitive. That number then went into the self-test
                        // report as a finding about the copper, and it was a
                        // finding about the silkscreen.
                        string layerName = "";
                        try { layerName = lu.AsString(p.GetState_V7Layer()); } catch { }

                        if (!layers.ContainsKey(layerName)) { res.SkippedOffCopper++; p = it.NextPCBObject(); continue; }

                        IPCB_Net net = p.GetState_Net();
                        if (net == null) { res.SkippedNoNet++; p = it.NextPCBObject(); continue; }

                        string netName = net.GetState_Name() ?? "";
                        if (netName.Length == 0) { res.SkippedNoNet++; p = it.NextPCBObject(); continue; }

                        double width = 0.0;
                        double x = 0.0, y = 0.0;

                        TObjectId id = p.GetState_ObjectID();
                        if (id == TObjectId.eTrackObject)
                        {
                            IPCB_Track t = p as IPCB_Track;
                            if (t == null) { p = it.NextPCBObject(); continue; }
                            width = ToMM(t.GetState_Width());
                            x = ToMM(t.GetState_X1());
                            y = ToMM(t.GetState_Y1());
                        }
                        else
                        {
                            IPCB_Arc a = p as IPCB_Arc;
                            if (a == null) { p = it.NextPCBObject(); continue; }
                            width = ToMM(a.GetState_LineWidth());
                            x = ToMM(a.GetState_StartX());
                            y = ToMM(a.GetState_StartY());
                        }

                        if (width <= 1e-9) { p = it.NextPCBObject(); continue; }

                        res.TracksScanned++;

                        NetWorst nw;
                        if (!worst.TryGetValue(netName, out nw))
                        {
                            nw = new NetWorst();
                            nw.Net = netName;
                            worst[netName] = nw;
                        }
                        nw.Segments++;

                        if (width < nw.WidthMM)
                        {
                            LayerInfo li = layers[layerName];
                            nw.WidthMM = width;
                            nw.Layer = layerName;
                            nw.X = x;
                            nw.Y = y;
                            nw.External = li.External;
                            nw.ThicknessMM = li.ThicknessMM > 1e-9 ? li.ThicknessMM : opt.DefaultThicknessMM;
                            if (li.ThicknessMM <= 1e-9) res.UsedDefaultThickness = true;
                            worstPrim[netName] = p;
                        }
                    }
                    catch { /* one unreadable primitive must not lose the scan */ }

                    p = it.NextPCBObject();
                }
            }
            catch (Exception ex)
            {
                res.Errors.Add("Copper scan failed -- " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                board.BoardIterator_Destroy(ref it);
            }

            List<string> rows = new List<string>();
            rows.Add("Net,Segments,MinWidthMM,Layer,LayerType,CopperMM,CapacityA,RequiredWidthMM,X,Y,Pass");

            List<IPCB_Primitive> failures = new List<IPCB_Primitive>();
            double lowest = double.MaxValue;

            List<string> names = new List<string>(worst.Keys);
            names.Sort(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < names.Count; i++)
            {
                NetWorst nw = worst[names[i]];
                if (nw.WidthMM >= double.MaxValue) continue;

                double amps = Ipc2221.CurrentAmps(nw.WidthMM, nw.ThicknessMM, opt.TempRiseC, nw.External);

                string required = "";
                string pass = "";
                if (opt.TargetAmps > 0.0)
                {
                    double need = Ipc2221.RequiredWidthMM(opt.TargetAmps, nw.ThicknessMM,
                                                          opt.TempRiseC, nw.External);
                    required = F3(need);
                    bool ok = amps >= opt.TargetAmps;
                    pass = ok ? "PASS" : "FAIL";
                    if (!ok)
                    {
                        res.Failures++;
                        IPCB_Primitive prim;
                        if (worstPrim.TryGetValue(nw.Net, out prim)) failures.Add(prim);
                    }
                }

                if (amps < lowest) { lowest = amps; res.WorstNet = nw.Net; res.WorstAmps = amps; }

                rows.Add(string.Join(",",
                    Csv(nw.Net),
                    nw.Segments.ToString(Inv),
                    F3(nw.WidthMM),
                    Csv(nw.Layer),
                    nw.External ? "External" : "Internal",
                    F3(nw.ThicknessMM),
                    F3(amps),
                    required,
                    F3(nw.X), F3(nw.Y),
                    pass));
                res.NetsReported++;
            }

            if (opt.SelectFailures && failures.Count > 0)
            {
                try
                {
                    board.SelectedObjects_BeginUpdate();
                    board.SelectedObjects_Clear();
                    for (int i = 0; i < failures.Count; i++) board.SelectedObjects_Add(failures[i]);
                    board.SelectedObjects_EndUpdate();
                    board.ViewManager_FullUpdate();
                }
                catch (Exception ex)
                {
                    res.Errors.Add("Could not select the failing tracks -- " + ex.GetType().Name);
                }
            }

            if (!string.IsNullOrEmpty(opt.OutputFolder))
            {
                try
                {
                    string path = Path.Combine(opt.OutputFolder, "net_current_capacity.csv");
                    File.WriteAllLines(path, rows, new UTF8Encoding(false));
                    res.CsvPath = path;
                    Log.Write("wrote " + path + " (" + (rows.Count - 1) + " row(s))");
                }
                catch (Exception ex)
                {
                    res.Errors.Add("Could not write the CSV -- " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            return res;
        }

        // ==================================================================
        // Polygon and region areas
        //
        // IPCB_Polygon exposes GetState_AreaSize() directly, so the pour's
        // real filled area -- after thermal reliefs and island removal -- is
        // read rather than approximated from the outline.
        // ==================================================================
        public sealed class AreaResult
        {
            public int Polygons, Regions;
            public double TotalAreaMM2;
            public string CsvPath = "";
            public List<string> Errors = new List<string>();

            public string Summarise()
            {
                string s = Polygons + " polygon(s) and " + Regions + " region(s), " +
                           TotalAreaMM2.ToString("0.00", Inv) + " mm2 of copper pour in total.\n";
                if (CsvPath.Length > 0) s += "Wrote " + CsvPath + "\n";
                foreach (string e in Errors) s += e + "\n";
                return s;
            }
        }

        public static AreaResult CopperAreas(IPCB_ServerInterface pcbServer, IPCB_Board board,
                                             string outputFolder)
        {
            AreaResult res = new AreaResult();
            IPCB_LayerUtils lu = pcbServer.LayerUtils();

            List<string> rows = new List<string>();
            rows.Add("Kind,Net,Layer,AreaMM2,AreaIN2");

            Dictionary<string, double> perLayer =
                new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(new TObjectId[] {
                    TObjectId.ePolyObject, TObjectId.eRegionObject }));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);

                IPCB_Primitive p = it.FirstPCBObject();
                while (p != null)
                {
                    try
                    {
                        string net = "";
                        try
                        {
                            IPCB_Net n = p.GetState_Net();
                            if (n != null) net = n.GetState_Name() ?? "";
                        }
                        catch { }

                        string layer = "";
                        try { layer = lu.AsString(p.GetState_V7Layer()); } catch { }

                        double areaMM2 = 0.0;
                        string kind;

                        IPCB_Polygon poly = p as IPCB_Polygon;
                        if (poly != null)
                        {
                            kind = "Polygon";

                            // MEASURED FROM THE OUTLINE, not read from
                            // GetState_AreaSize(). That accessor came back as
                            // exactly 0 on a poured polygon on a real board,
                            // and a copper report saying 0.00 mm2 for a pour
                            // that visibly covers half the board is worse than
                            // no report. The outline is there, so measure it.
                            List<double> px = new List<double>();
                            List<double> py = new List<double>();
                            int declared = -1;
                            string why = "";
                            try
                            {
                                // GetState_Segments, the same wrapper the board
                                // outline is read with. Internal_GetState_Segments
                                // returns an IPolySegment that came back empty on
                                // a real polygon.
                                declared = poly.GetState_PointCount();
                                for (int i = 0; i < declared; i++)
                                {
                                    PolySegment seg = poly.GetState_Segments(i);
                                    px.Add(ToMM(seg.GetVx()));
                                    py.Add(ToMM(seg.GetVy()));
                                }
                            }
                            catch (Exception pex) { why = pex.GetType().Name + ": " + pex.Message; }

                            // Two guesses at this have already been wrong, so
                            // the reason is recorded rather than inferred. The
                            // record paid off: on SpikeTest (2026-09-26 12:05)
                            // GetState_PointCount itself threw "Interface not
                            // supported" on the iterated polygon, while the
                            // same call works on the board outline.
                            if (px.Count == 0)
                                res.Errors.Add("Polygon outline walk got " + px.Count + " vertices from a " +
                                               "declared PointCount of " + declared +
                                               (why.Length > 0 ? " (" + why + ")" : " (no exception)"));

                            areaMM2 = PolyGeometry.PolygonArea(px, py);

                            if (areaMM2 <= 0)
                            {
                                // No outline: measure the poured copper itself
                                // -- the regions the pour is made of, each
                                // from its contour less its holes. That is
                                // the real filled area, not the boundary.
                                string pieces;
                                areaMM2 = PouredArea(poly, out pieces);
                                if (areaMM2 > 0) kind = "Polygon (poured copper)";
                                res.Errors.Add("Polygon " + PolyName(poly) + " on " + layer + ": poured copper " +
                                               areaMM2.ToString("0.00", Inv) + " mm2 from " + pieces);
                            }

                            if (areaMM2 <= 0)
                            {
                                // Fall back to the cached value rather than
                                // reporting nothing, and say which was used.
                                double mmPerCoord = EDP.Utils.CoordToMMs(1000000) / 1000000.0;
                                areaMM2 = poly.GetState_AreaSize() * mmPerCoord * mmPerCoord;
                                if (areaMM2 > 0) kind = "Polygon (cached area)";
                                else kind = "Polygon (no measurable outline)";
                            }
                            else
                            {
                                // The outline is the boundary; copper removed
                                // inside it for clearances and islands is not
                                // subtracted. Said plainly in the CSV rather
                                // than implied.
                                kind = "Polygon (outline)";
                            }
                            res.Polygons++;
                        }
                        else
                        {
                            kind = "Region";
                            CoordRect r = p.BoundingRectangle();
                            // IPCB_Region exposes no area member, so a region
                            // is reported by its BOUNDING BOX and labelled as
                            // such in the CSV rather than being passed off as
                            // a true filled area.
                            areaMM2 = ToMM(r.GetX2() - r.GetX1()) * ToMM(r.GetY2() - r.GetY1());
                            res.Regions++;
                            kind = "Region (bbox)";
                        }

                        res.TotalAreaMM2 += areaMM2;
                        if (layer.Length > 0)
                        {
                            double acc;
                            perLayer.TryGetValue(layer, out acc);
                            perLayer[layer] = acc + areaMM2;
                        }

                        rows.Add(string.Join(",",
                            kind, Csv(net), Csv(layer),
                            areaMM2.ToString("0.00", Inv),
                            (areaMM2 / 645.16).ToString("0.0000", Inv)));
                    }
                    catch { }

                    p = it.NextPCBObject();
                }
            }
            catch (Exception ex)
            {
                res.Errors.Add("Area scan failed -- " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                board.BoardIterator_Destroy(ref it);
            }

            List<string> layerNames = new List<string>(perLayer.Keys);
            layerNames.Sort(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < layerNames.Count; i++)
            {
                rows.Add(string.Join(",",
                    "LAYER TOTAL", "", Csv(layerNames[i]),
                    perLayer[layerNames[i]].ToString("0.00", Inv),
                    (perLayer[layerNames[i]] / 645.16).ToString("0.0000", Inv)));
            }

            if (!string.IsNullOrEmpty(outputFolder))
            {
                try
                {
                    string path = Path.Combine(outputFolder, "copper_areas.csv");
                    File.WriteAllLines(path, rows, new UTF8Encoding(false));
                    res.CsvPath = path;
                    Log.Write("wrote " + path + " (" + (rows.Count - 1) + " row(s))");
                }
                catch (Exception ex)
                {
                    res.Errors.Add("Could not write the CSV -- " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            return res;
        }

        // The copper a pour actually holds: the sum of its child regions,
        // each measured from its main contour less its holes. `pieces` says
        // what the group held, so a hatched pour (tracks, no regions) or an
        // unpoured one (nothing) reads as that, not as a mystery zero.
        private static double PouredArea(IPCB_Polygon poly, out string pieces)
        {
            double area = 0.0;
            int regions = 0, others = 0, points = 0;
            string why = "";
            IPCB_GroupIterator gi = null;
            try
            {
                gi = poly.GroupIterator_Create();
                gi.AddFilter_ObjectSet(new TObjectSet(new TObjectId[] { TObjectId.eRegionObject, TObjectId.eTrackObject }));
                gi.AddFilter_AllLayers();
                IPCB_Primitive c = gi.FirstPCBObject();
                while (c != null)
                {
                    IPCB_Region rg = c as IPCB_Region;
                    if (rg == null) others++;
                    else
                    {
                        regions++;
                        IPCB_Contour main = rg.GetMainContour();
                        points += main.GetState_Count();
                        double a = ContourArea(main);
                        int holes = rg.GetHoleCount();
                        for (int h = 0; h < holes; h++) a -= ContourArea(rg.GetHole(h));
                        if (a > 0) area += a;
                    }
                    c = gi.NextPCBObject();
                }
            }
            catch (Exception ex) { why = "; walk stopped: " + ex.GetType().Name + ": " + ex.Message; }
            finally
            {
                if (gi != null) try { poly.GroupIterator_Destroy(ref gi); } catch { }
            }

            pieces = regions + " region(s) (" + points + " contour points) and " + others +
                     " other piece(s)" + why;
            return area;
        }

        private static double ContourArea(IPCB_Contour k)
        {
            if (k == null) return 0.0;
            int n = k.GetState_Count();
            List<double> xs = new List<double>(n), ys = new List<double>(n);
            for (int i = 0; i < n; i++)
            {
                xs.Add(ToMM(k.GetState_PointX(i)));
                ys.Add(ToMM(k.GetState_PointY(i)));
            }
            return PolyGeometry.PolygonArea(xs, ys);
        }

        private static string PolyName(IPCB_Polygon poly)
        {
            string name = "";
            try { name = poly.GetState_Name() ?? ""; } catch { }
            return name.Length > 0 ? "'" + name + "'" : "(unnamed)";
        }
    }
}
