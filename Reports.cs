// Reports.cs
//
// Three read-only reports. Nothing here touches the board.
//
//   BoardCensus  -- what the board is made of, by object type and by layer
//   DrillTable   -- hole sizes with counts, plated and unplated separated
//   Unrouted     -- pin pairs that still have unrouted length
//
// THE DRILL TABLE IS THE USEFUL ONE. Altium will draw a drill table on the
// fabrication drawing, but getting the numbers out as data -- to check a
// quote, to compare two revisions, to confirm nobody slipped a 0.15 mm hole
// into a board quoted for 0.2 mm minimum -- means reading them off a drawing.
// Here they are a CSV, with plated and non-plated counted separately because
// a fab house prices them separately.
//
// UNROUTED USES PIN PAIRS, not net-level connectivity. A net is "routed" in
// the loosest sense as soon as any copper is on it, so net-level state says
// nothing useful. A pin pair knows its own unrouted length, so a fly-by net
// with one missing hop reports that one hop rather than looking finished.

using DXP;
using PCB;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AltiumSpike
{
    public static class Reports
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static readonly double CoordPerMM = 1000000.0 / EDP.Utils.CoordToMMs(1000000);
        private static double ToMM(int coord) { return EDP.Utils.CoordToMMs(coord); }

        private static double MM64(long coord)
        {
            if (coord <= int.MaxValue && coord >= int.MinValue)
                return EDP.Utils.CoordToMMs((int)coord);
            return coord / CoordPerMM;
        }

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

        private static void Save(string folder, string name, List<string> lines, Result res)
        {
            try
            {
                string path = Path.Combine(folder, name);
                File.WriteAllLines(path, lines, new UTF8Encoding(false));
                res.Files.Add(path);
                Log.Write("wrote " + path + " (" + (lines.Count - 1) + " data row(s))");
            }
            catch (Exception ex)
            {
                res.Errors.Add("Could not write " + name + " -- " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public sealed class Result
        {
            public List<string> Files = new List<string>();
            public List<string> Headline = new List<string>();
            public List<string> Errors = new List<string>();

            public string Summarise()
            {
                string s = "";
                foreach (string h in Headline) s += h + "\n";
                foreach (string f in Files) s += "wrote " + f + "\n";
                foreach (string e in Errors) s += e + "\n";
                return s;
            }
        }

        // ==================================================================
        // Board census
        // ==================================================================
        public static Result BoardCensus(IPCB_ServerInterface pcbServer, IPCB_Board board, string folder)
        {
            Result res = new Result();
            IPCB_LayerUtils lu = pcbServer.LayerUtils();

            Dictionary<string, int> byType = new Dictionary<string, int>(StringComparer.Ordinal);
            Dictionary<string, int> copperByLayer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int nets = 0, components = 0, classes = 0, rules = 0;

            TObjectId[] wanted = {
                TObjectId.eComponentObject, TObjectId.ePadObject, TObjectId.eViaObject,
                TObjectId.eTrackObject, TObjectId.eArcObject, TObjectId.eTextObject,
                TObjectId.eFillObject, TObjectId.ePolyObject, TObjectId.eRegionObject,
                TObjectId.eDimensionObject, TObjectId.eCoordinateObject,
                TObjectId.eNetObject, TObjectId.eClassObject, TObjectId.eRuleObject,
            };

            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(wanted));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);

                IPCB_Primitive p = it.FirstPCBObject();
                while (p != null)
                {
                    try
                    {
                        TObjectId id = p.GetState_ObjectID();
                        string name = id.ToString();
                        int n;
                        byType.TryGetValue(name, out n);
                        byType[name] = n + 1;

                        if (id == TObjectId.eNetObject) nets++;
                        else if (id == TObjectId.eComponentObject) components++;
                        else if (id == TObjectId.eClassObject) classes++;
                        else if (id == TObjectId.eRuleObject) rules++;
                        else if (id == TObjectId.eTrackObject || id == TObjectId.eArcObject ||
                                 id == TObjectId.ePolyObject || id == TObjectId.eRegionObject ||
                                 id == TObjectId.eFillObject)
                        {
                            string layer = "";
                            try { layer = lu.AsString(p.GetState_V7Layer()); } catch { }
                            if (layer.Length > 0)
                            {
                                int c;
                                copperByLayer.TryGetValue(layer, out c);
                                copperByLayer[layer] = c + 1;
                            }
                        }
                    }
                    catch { }

                    p = it.NextPCBObject();
                }
            }
            catch (Exception ex)
            {
                res.Errors.Add("Census scan failed -- " + ex.GetType().Name + ": " + ex.Message);
            }
            finally { board.BoardIterator_Destroy(ref it); }

            List<string> rows = new List<string>();
            rows.Add("Category,Item,Count");

            List<string> types = new List<string>(byType.Keys);
            types.Sort(StringComparer.Ordinal);
            for (int i = 0; i < types.Count; i++)
                rows.Add("ObjectType," + Csv(types[i]) + "," + byType[types[i]].ToString(Inv));

            List<string> layers = new List<string>(copperByLayer.Keys);
            layers.Sort(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < layers.Count; i++)
                rows.Add("PrimitivesPerLayer," + Csv(layers[i]) + "," + copperByLayer[layers[i]].ToString(Inv));

            Save(folder, "board_census.csv", rows, res);

            res.Headline.Add(components + " component(s), " + nets + " net(s), " +
                             rules + " rule(s), " + classes + " class(es)");

            int pads, vias, tracks;
            byType.TryGetValue(TObjectId.ePadObject.ToString(), out pads);
            byType.TryGetValue(TObjectId.eViaObject.ToString(), out vias);
            byType.TryGetValue(TObjectId.eTrackObject.ToString(), out tracks);
            res.Headline.Add(pads + " pad(s), " + vias + " via(s), " + tracks + " track(s)");

            return res;
        }

        // ==================================================================
        // Drill table
        // ==================================================================
        public static Result DrillTable(IPCB_Board board, string folder)
        {
            Result res = new Result();

            // Hole sizes are binned on their exact internal value, so two
            // holes that differ by a rounding artefact do not become two
            // separate drill sizes in the table.
            Dictionary<int, int[]> bins = new Dictionary<int, int[]>();  // [platedVia, platedPad, unplated]

            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(new TObjectId[] {
                    TObjectId.ePadObject, TObjectId.eViaObject }));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);

                IPCB_Primitive p = it.FirstPCBObject();
                while (p != null)
                {
                    try
                    {
                        int hole = 0;
                        int slot = -1;

                        if (p.GetState_ObjectID() == TObjectId.eViaObject)
                        {
                            IPCB_Via v = p as IPCB_Via;
                            if (v != null) { hole = v.GetState_HoleSize(); slot = 0; }
                        }
                        else
                        {
                            IPCB_Pad pad = p as IPCB_Pad;
                            if (pad != null)
                            {
                                hole = pad.GetState_HoleSize();
                                // A surface-mount pad has no hole and must not
                                // appear in a drill table.
                                if (hole > 0) slot = pad.GetState_Plated() ? 1 : 2;
                            }
                        }

                        if (hole > 0 && slot >= 0)
                        {
                            int[] b;
                            if (!bins.TryGetValue(hole, out b)) { b = new int[3]; bins[hole] = b; }
                            b[slot]++;
                        }
                    }
                    catch { }

                    p = it.NextPCBObject();
                }
            }
            catch (Exception ex)
            {
                res.Errors.Add("Drill scan failed -- " + ex.GetType().Name + ": " + ex.Message);
            }
            finally { board.BoardIterator_Destroy(ref it); }

            List<int> sizes = new List<int>(bins.Keys);
            sizes.Sort();

            List<string> rows = new List<string>();
            rows.Add("HoleMM,HoleMil,Vias,PlatedPads,NonPlatedPads,Total");

            int total = 0, smallestPlated = int.MaxValue;
            for (int i = 0; i < sizes.Count; i++)
            {
                int[] b = bins[sizes[i]];
                int t = b[0] + b[1] + b[2];
                total += t;
                if (b[0] + b[1] > 0 && sizes[i] < smallestPlated) smallestPlated = sizes[i];

                double mm = ToMM(sizes[i]);
                rows.Add(string.Join(",",
                    F3(mm), (mm / 0.0254).ToString("0.0", Inv),
                    b[0].ToString(Inv), b[1].ToString(Inv), b[2].ToString(Inv), t.ToString(Inv)));
            }

            Save(folder, "drill_table.csv", rows, res);

            res.Headline.Add(sizes.Count + " distinct hole size(s), " + total + " hole(s) in total");
            if (smallestPlated < int.MaxValue)
                res.Headline.Add("smallest plated hole " + F3(ToMM(smallestPlated)) + " mm (" +
                                 (ToMM(smallestPlated) / 0.0254).ToString("0.0", Inv) + " mil)");

            return res;
        }

        // ==================================================================
        // Unrouted connections
        // ==================================================================
        public static Result Unrouted(IPCB_Board board, string folder)
        {
            Result res = new Result();

            IPCB_PinPairsManager mgr = null;
            try { mgr = board.GetState_PinPairsManager(); }
            catch (Exception ex)
            {
                res.Errors.Add("Pin pairs unavailable -- " + ex.GetType().Name + ": " + ex.Message);
            }

            List<string> rows = new List<string>();
            rows.Add("Net,PinPair,FromPin,ToPin,UnroutedMM,RoutedMM");

            if (mgr == null)
            {
                res.Headline.Add("This board reports no pin pairs, so nothing can be checked.");
                Save(folder, "unrouted.csv", rows, res);
                return res;
            }

            int count = 0;
            try { count = mgr.GetState_PinPairsCount(); } catch { }

            int unroutedPairs = 0;
            double worst = 0.0;
            string worstNet = "";

            for (int i = 0; i < count; i++)
            {
                try
                {
                    IPCB_PinPair pp = mgr.GetState_PinPairs(i);
                    if (pp == null) continue;

                    double un = MM64(pp.GetState_UnroutedLength());
                    if (un <= 1e-6) continue;

                    string net = "", from = "", to = "";
                    try
                    {
                        IPCB_Primitive a = pp.GetPrimitives(0);
                        if (a != null)
                        {
                            from = pp.GetPinPairItemDisplayName(a);
                            IPCB_Net n = a.GetState_Net();
                            if (n != null) net = n.GetState_Name() ?? "";
                        }
                    }
                    catch { }
                    try
                    {
                        IPCB_Primitive b = pp.GetPrimitives(1);
                        if (b != null) to = pp.GetPinPairItemDisplayName(b);
                    }
                    catch { }

                    string label = "";
                    try { label = pp.GetState_Name(); } catch { }

                    rows.Add(string.Join(",",
                        Csv(net), Csv(label), Csv(from), Csv(to),
                        F3(un), F3(MM64(pp.GetState_RoutedLength()))));

                    unroutedPairs++;
                    if (un > worst) { worst = un; worstNet = net; }
                }
                catch { }
            }

            Save(folder, "unrouted.csv", rows, res);

            if (unroutedPairs == 0)
                res.Headline.Add("Every pin pair is routed (" + count + " checked).");
            else
            {
                res.Headline.Add(unroutedPairs + " unrouted pin pair(s) of " + count);
                res.Headline.Add("longest gap " + F3(worst) + " mm on " + worstNet);
            }

            return res;
        }
    }
}
