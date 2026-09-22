// Layers.cs
//
//   MoveToLayer   -- move selected copper to another signal layer, adding
//                    vias wherever that breaks a connection
//   ExportStack   -- the layer stack as a CSV
//   Visibility    -- show or hide layers in bulk
//
// MOVE TO LAYER IS THE ONE THAT NEEDS CARE. Moving a track to another layer
// is one line; keeping the board connected afterwards is the actual job. An
// endpoint that was touching copper on the old layer is now floating above
// it, and without a via there the net is silently broken -- silently because
// the track still looks connected on screen and the ratsnest does not
// necessarily come back.
//
// So every endpoint of every moved track is checked against copper that
// STAYED on the original layer, and a via is placed wherever one is needed.
// Endpoints that land on a pad or an existing via are left alone, since those
// already span layers.
//
// Moving to an internal plane is refused. A plane is negative artwork; a
// track dropped onto one is a void, not a conductor, and it will not do what
// it looks like it does.

using DXP;
using PCB;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AltiumSpike
{
    public static class Layers
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static double ToMM(int coord) { return EDP.Utils.CoordToMMs(coord); }
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
            public int Moved, ViasPlaced, Skipped, Considered;
            public string CsvPath = "";
            public List<string> Notes = new List<string>();
            public List<string> Errors = new List<string>();
        }

        public static List<string> SignalLayerNames(IPCB_ServerInterface pcbServer, IPCB_Board board)
        {
            List<string> names = new List<string>();
            try
            {
                IPCB_LayerUtils lu = pcbServer.LayerUtils();
                IPCB_LayerStack stack = board.GetState_LayerStack();
                if (stack == null) return names;

                IPCB_LayerObject lo = stack.First(TLayerClassID.eLayerClass_Signal);
                int guard = 0;
                while (lo != null && guard++ < 256)
                {
                    try { names.Add(lu.AsString(lo.V7_LayerID())); } catch { }
                    lo = stack.Next(TLayerClassID.eLayerClass_Signal, lo);
                }
            }
            catch { }
            return names;
        }

        // ==================================================================
        // Move selected copper to another layer
        // ==================================================================
        public sealed class MoveOptions
        {
            public string TargetLayer = "";
            public bool PlaceVias = true;
            public double ViaDiameterMM = 0.6;
            public double ViaHoleMM = 0.3;
            public double ToleranceMM = 0.01;
        }

        public static Result MoveToLayer(IPCB_ServerInterface pcbServer, IPCB_Board board, MoveOptions opt)
        {
            Result res = new Result();
            IPCB_LayerUtils lu = pcbServer.LayerUtils();

            if (string.IsNullOrEmpty(opt.TargetLayer))
            { res.Errors.Add("Choose a target layer."); return res; }

            IV7_Layer target = lu.FromString(opt.TargetLayer);
            if (target == null)
            { res.Errors.Add("Layer \"" + opt.TargetLayer + "\" does not exist on this board."); return res; }

            try
            {
                // A plane is negative artwork; a track on it is a void.
                if (lu.IsInternalPlaneLayer(target))
                {
                    res.Errors.Add("\"" + opt.TargetLayer + "\" is an internal plane. A track placed on a " +
                                   "plane is a void in the copper, not a conductor.");
                    return res;
                }
                if (!lu.IsSignalLayer(target))
                {
                    res.Errors.Add("\"" + opt.TargetLayer + "\" is not a signal layer.");
                    return res;
                }
            }
            catch { /* if the classification will not answer, carry on */ }

            // The selected copper, snapshotted before anything changes.
            List<IPCB_Primitive> moving = new List<IPCB_Primitive>();
            int selCount;
            try { selCount = board.GetState_SelectecObjectCount(); }
            catch (Exception ex)
            {
                res.Errors.Add("Could not read the selection -- " + ex.GetType().Name + ": " + ex.Message);
                return res;
            }

            for (int i = 0; i < selCount; i++)
            {
                try
                {
                    IPCB_Primitive p = board.GetState_SelectecObject(i);
                    if (p == null) continue;
                    TObjectId id = p.GetState_ObjectID();
                    if (id == TObjectId.eTrackObject || id == TObjectId.eArcObject) moving.Add(p);
                }
                catch { }
            }

            res.Considered = moving.Count;
            if (moving.Count == 0)
            { res.Errors.Add("Select the tracks or arcs to move first."); return res; }

            // Endpoints of the moving copper, with the layer each came from.
            List<double> px = new List<double>(), py = new List<double>();
            List<string> pnet = new List<string>();
            List<string> pfrom = new List<string>();

            for (int i = 0; i < moving.Count; i++)
            {
                try
                {
                    IPCB_Primitive p = moving[i];
                    string net = "";
                    IPCB_Net n = p.GetState_Net();
                    if (n != null) net = n.GetState_Name() ?? "";
                    string from = lu.AsString(p.GetState_V7Layer());

                    if (p.GetState_ObjectID() == TObjectId.eTrackObject)
                    {
                        IPCB_Track t = p as IPCB_Track;
                        if (t == null) continue;
                        px.Add(ToMM(t.GetState_X1())); py.Add(ToMM(t.GetState_Y1())); pnet.Add(net); pfrom.Add(from);
                        px.Add(ToMM(t.GetState_X2())); py.Add(ToMM(t.GetState_Y2())); pnet.Add(net); pfrom.Add(from);
                    }
                    else
                    {
                        IPCB_Arc a = p as IPCB_Arc;
                        if (a == null) continue;
                        px.Add(ToMM(a.GetState_StartX())); py.Add(ToMM(a.GetState_StartY())); pnet.Add(net); pfrom.Add(from);
                        px.Add(ToMM(a.GetState_EndX())); py.Add(ToMM(a.GetState_EndY())); pnet.Add(net); pfrom.Add(from);
                    }
                }
                catch { }
            }

            // Everything that is NOT moving, so we can tell which endpoints
            // will be stranded and which land on something that already spans
            // layers.
            List<double> sx = new List<double>(), sy = new List<double>();
            List<string> snet = new List<string>(), slayer = new List<string>();
            List<bool> sSpans = new List<bool>();

            HashSet<IPCB_Primitive> movingSet = new HashSet<IPCB_Primitive>(moving);

            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(new TObjectId[] {
                    TObjectId.eTrackObject, TObjectId.eArcObject,
                    TObjectId.ePadObject, TObjectId.eViaObject }));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);

                IPCB_Primitive p = it.FirstPCBObject();
                while (p != null)
                {
                    try
                    {
                        if (!movingSet.Contains(p))
                        {
                            string net = "";
                            IPCB_Net n = p.GetState_Net();
                            if (n != null) net = n.GetState_Name() ?? "";

                            if (net.Length > 0)
                            {
                                string layer = "";
                                try { layer = lu.AsString(p.GetState_V7Layer()); } catch { }

                                TObjectId id = p.GetState_ObjectID();
                                if (id == TObjectId.eTrackObject)
                                {
                                    IPCB_Track t = p as IPCB_Track;
                                    if (t != null)
                                    {
                                        Add(sx, sy, snet, slayer, sSpans, ToMM(t.GetState_X1()), ToMM(t.GetState_Y1()), net, layer, false);
                                        Add(sx, sy, snet, slayer, sSpans, ToMM(t.GetState_X2()), ToMM(t.GetState_Y2()), net, layer, false);
                                    }
                                }
                                else if (id == TObjectId.eArcObject)
                                {
                                    IPCB_Arc a = p as IPCB_Arc;
                                    if (a != null)
                                    {
                                        Add(sx, sy, snet, slayer, sSpans, ToMM(a.GetState_StartX()), ToMM(a.GetState_StartY()), net, layer, false);
                                        Add(sx, sy, snet, slayer, sSpans, ToMM(a.GetState_EndX()), ToMM(a.GetState_EndY()), net, layer, false);
                                    }
                                }
                                else if (id == TObjectId.ePadObject)
                                {
                                    IPCB_Pad pad = p as IPCB_Pad;
                                    // A through-hole pad already spans layers,
                                    // so a track moving to another layer is
                                    // still connected to it.
                                    if (pad != null)
                                        Add(sx, sy, snet, slayer, sSpans, ToMM(pad.GetState_XLocation()),
                                            ToMM(pad.GetState_YLocation()), net, layer, pad.GetState_HoleSize() > 0);
                                }
                                else
                                {
                                    IPCB_Via v = p as IPCB_Via;
                                    if (v != null)
                                        Add(sx, sy, snet, slayer, sSpans, ToMM(v.GetState_XLocation()),
                                            ToMM(v.GetState_YLocation()), net, layer, true);
                                }
                            }
                        }
                    }
                    catch { }

                    p = it.NextPCBObject();
                }
            }
            catch (Exception ex)
            {
                res.Errors.Add("Board scan failed -- " + ex.GetType().Name + ": " + ex.Message);
            }
            finally { board.BoardIterator_Destroy(ref it); }

            // Which endpoints need a via: something of the same net stays on
            // the old layer at that point, and nothing there already spans.
            List<int> needVia = new List<int>();
            double tol2 = opt.ToleranceMM * opt.ToleranceMM;

            for (int i = 0; i < px.Count; i++)
            {
                bool stranded = false, spanned = false;
                for (int j = 0; j < sx.Count; j++)
                {
                    double dx = sx[j] - px[i], dy = sy[j] - py[i];
                    if (dx * dx + dy * dy > tol2) continue;
                    if (!string.Equals(snet[j], pnet[i], StringComparison.OrdinalIgnoreCase)) continue;

                    if (sSpans[j]) { spanned = true; break; }
                    if (string.Equals(slayer[j], pfrom[i], StringComparison.OrdinalIgnoreCase)) stranded = true;
                }
                if (stranded && !spanned) needVia.Add(i);
            }

            pcbServer.PreProcess();
            try
            {
                for (int i = 0; i < moving.Count; i++)
                {
                    try { moving[i].SetState_V7Layer(target); res.Moved++; }
                    catch (Exception ex)
                    {
                        res.Errors.Add("Item " + i + ": " + ex.GetType().Name + " -- " + ex.Message);
                        res.Skipped++;
                    }
                }

                if (opt.PlaceVias)
                {
                    // Several moved ends can meet at one point; one via each
                    // would stack them.
                    List<double> placedX = new List<double>(), placedY = new List<double>();

                    for (int k = 0; k < needVia.Count; k++)
                    {
                        int i = needVia[k];
                        bool already = false;
                        for (int j = 0; j < placedX.Count; j++)
                        {
                            double dx = placedX[j] - px[i], dy = placedY[j] - py[i];
                            if (dx * dx + dy * dy <= tol2) { already = true; break; }
                        }
                        if (already) continue;

                        try
                        {
                            IPCB_Via via = pcbServer.PCBObjectFactory(
                                TObjectId.eViaObject, TDimensionKind.eNoDimension,
                                TObjectCreationMode.eCreate_Default) as IPCB_Via;

                            via.SetState_XLocation(ToCoord(px[i]));
                            via.SetState_YLocation(ToCoord(py[i]));
                            via.SetState_HoleSize(ToCoord(opt.ViaHoleMM));
                            via.SetState_Size(ToCoord(opt.ViaDiameterMM));
                            via.SetState_LowLayer(lu.FromString("Top Layer"));
                            via.SetState_HighLayer(lu.FromString("Bottom Layer"));

                            IPCB_Net net = FindNet(board, pnet[i]);
                            if (net != null) via.SetState_Net(net);

                            board.AddPCBObject(via);
                            placedX.Add(px[i]); placedY.Add(py[i]);
                            res.ViasPlaced++;
                        }
                        catch (Exception ex)
                        {
                            res.Errors.Add("Via at " + F3(px[i]) + "," + F3(py[i]) + ": " + ex.GetType().Name);
                        }
                    }
                }
                else if (needVia.Count > 0)
                {
                    res.Notes.Add(needVia.Count + " endpoint(s) now need a via and none was placed — " +
                                  "those connections are broken until you add them.");
                }
            }
            finally { pcbServer.PostProcess(); }

            board.ViewManager_FullUpdate();
            Log.Write("Layers.MoveToLayer: " + res.Moved + " moved to " + opt.TargetLayer +
                      ", " + res.ViasPlaced + " via(s) placed");
            return res;
        }

        private static void Add(List<double> xs, List<double> ys, List<string> nets,
                                List<string> layers, List<bool> spans,
                                double x, double y, string net, string layer, bool spanning)
        {
            xs.Add(x); ys.Add(y); nets.Add(net); layers.Add(layer); spans.Add(spanning);
        }

        private static IPCB_Net FindNet(IPCB_Board board, string netName)
        {
            string target = (netName ?? "").Trim().ToUpperInvariant();
            if (target.Length == 0) return null;

            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(TObjectId.eNetObject));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);

                IPCB_Net n = it.FirstPCBObject() as IPCB_Net;
                while (n != null)
                {
                    if ((n.GetState_Name() ?? "").ToUpperInvariant() == target) return n;
                    n = it.NextPCBObject() as IPCB_Net;
                }
            }
            catch { }
            finally { board.BoardIterator_Destroy(ref it); }
            return null;
        }

        // ==================================================================
        // Export the stack
        // ==================================================================
        public static Result ExportStack(IPCB_ServerInterface pcbServer, IPCB_Board board, string folder)
        {
            Result res = new Result();
            IPCB_LayerUtils lu = pcbServer.LayerUtils();

            List<string> rows = new List<string>();
            rows.Add("Index,Layer,Kind,Material,ThicknessMM,ThicknessMil,DielectricConstant,LossTangent");

            try
            {
                IPCB_LayerStack stack = board.GetState_LayerStack();
                if (stack == null) { res.Errors.Add("This board has no layer stack."); return res; }

                IPCB_LayerObject lo = stack.First(TLayerClassID.eLayerClass_Physical);
                int index = 0, guard = 0;
                double total = 0.0;

                while (lo != null && guard++ < 512)
                {
                    string name = "";
                    try { name = lo.GetState_LayerName(); } catch { }

                    string kind = "Layer", material = "", er = "", lt = "";
                    double thickness = 0.0;

                    IPCB_ElectricalLayer el = lo as IPCB_ElectricalLayer;
                    IPCB_DielectricLayer dl = lo as IPCB_DielectricLayer;

                    if (el != null)
                    {
                        kind = "Copper";
                        material = "Copper";
                        try { thickness = ToMM(el.GetState_CopperThickness()); } catch { }
                    }
                    else if (dl != null)
                    {
                        try
                        {
                            TDielectricType dt = dl.GetState_DielectricType();
                            kind = dt == TDielectricType.eCore ? "Core"
                                 : dt == TDielectricType.ePrePreg ? "Prepreg"
                                 : dt == TDielectricType.eSurfaceMaterial ? "Surface"
                                 : dt == TDielectricType.eFilm ? "Film" : "Dielectric";
                        }
                        catch { kind = "Dielectric"; }

                        try { material = dl.GetState_DielectricMaterial(); } catch { }
                        try { thickness = ToMM(dl.GetState_DielectricHeight()); } catch { }
                        try
                        {
                            double v = dl.GetState_DielectricConstant();
                            if (v > 0) er = v.ToString("0.000", Inv);
                        }
                        catch { }
                        try
                        {
                            double v = dl.GetState_DielectricLossTangent();
                            if (v > 0) lt = v.ToString("0.0000", Inv);
                        }
                        catch { }
                    }

                    total += thickness;

                    rows.Add(string.Join(",",
                        index.ToString(Inv), Csv(name), kind, Csv(material),
                        F3(thickness), (thickness / 0.0254).ToString("0.00", Inv),
                        er, lt));

                    index++;
                    lo = stack.Next(TLayerClassID.eLayerClass_Physical, lo);
                }

                rows.Add(string.Join(",", "", "TOTAL", "", "", F3(total),
                                     (total / 0.0254).ToString("0.00", Inv), "", ""));

                res.Considered = index;
                res.Notes.Add(index + " physical layer(s), finished thickness " + F3(total) + " mm");
            }
            catch (Exception ex)
            {
                res.Errors.Add("Stack read failed -- " + ex.GetType().Name + ": " + ex.Message);
            }

            try
            {
                string path = Path.Combine(folder, "layer_stack.csv");
                File.WriteAllLines(path, rows, new UTF8Encoding(false));
                res.CsvPath = path;
                Log.Write("wrote " + path + " (" + (rows.Count - 1) + " row(s))");
            }
            catch (Exception ex)
            {
                res.Errors.Add("Could not write the CSV -- " + ex.GetType().Name + ": " + ex.Message);
            }

            return res;
        }

        // ==================================================================
        // Visibility
        // ==================================================================
        public static Result Visibility(IPCB_ServerInterface pcbServer, IPCB_Board board,
                                        bool show, bool signalOnly)
        {
            Result res = new Result();
            IPCB_LayerUtils lu = pcbServer.LayerUtils();

            try
            {
                IPCB_LayerStack stack = board.GetState_LayerStack();
                if (stack == null) { res.Errors.Add("This board has no layer stack."); return res; }

                TLayerClassID which = signalOnly ? TLayerClassID.eLayerClass_Signal
                                                 : TLayerClassID.eLayerClass_Electrical;

                IPCB_LayerObject lo = stack.First(which);
                int guard = 0;
                while (lo != null && guard++ < 256)
                {
                    try
                    {
                        board.SetState_LayerIsDisplayed(lo.V7_LayerID(), show);
                        res.Considered++;
                        res.Moved++;   // reused as "layers changed"
                    }
                    catch { res.Skipped++; }
                    lo = stack.Next(which, lo);
                }
            }
            catch (Exception ex)
            {
                res.Errors.Add("Visibility change failed -- " + ex.GetType().Name + ": " + ex.Message);
            }

            board.ViewManager_FullUpdate();
            Log.Write("Layers.Visibility: " + res.Considered + " layer(s) set to " + (show ? "shown" : "hidden"));
            return res;
        }
    }
}
