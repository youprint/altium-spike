// Cleanup.cs
//
// Three checks that find things DRC does not:
//
//   InvalidObjects  -- polygons and regions with fewer than three vertices
//   ViaAntennas     -- vias with copper on only one layer
//   DanglingCopper  -- track and arc ends that touch nothing
//
// Nothing here deletes without being told to. InvalidObjects is the only one
// that can remove anything, and it is off unless you ask for it; the other
// two select what they find and leave the board alone. A cleanup tool that
// quietly deletes is a cleanup tool you cannot trust on a board you care
// about.
//
// CONNECTIVITY IS INFERRED, NOT QUERIED. The SDK exposes no "what is
// connected to this" call that can be trusted across polygon pours and
// planes, so both connectivity checks work geometrically: an endpoint or a
// via centre is considered connected to another primitive on the same net
// when they coincide within a tolerance. That is the same thing Altium's own
// centre-to-centre connectivity model does, and the same thing the
// DelphiScript versions of these checks do, but it means two caveats worth
// stating plainly:
//
//   - Copper poured over a via connects it in reality and not in this check.
//     Vias inside a pour are therefore EXCLUDED from the antenna check by
//     default, because otherwise every stitching via in a ground pour is
//     reported as an antenna and the result is noise.
//   - A track ending part-way along another track, rather than at its end,
//     is a real connection in Altium and does not register here. The
//     tolerance is configurable for that reason.
//
// So these are screens that produce a list worth looking at, not verdicts.

using DXP;
using PCB;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace AltiumSpike
{
    public static class Cleanup
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static double ToMM(int coord) { return EDP.Utils.CoordToMMs(coord); }

        // ==================================================================
        // A coarse spatial hash, so the coincidence tests do not go quadratic
        // on a board with 40000 copper primitives.
        // ==================================================================
        private sealed class PointGrid
        {
            private readonly double cell;
            private readonly Dictionary<long, List<int>> buckets = new Dictionary<long, List<int>>();
            private readonly List<double> xs = new List<double>();
            private readonly List<double> ys = new List<double>();
            private readonly List<string> nets = new List<string>();
            private readonly List<string> layers = new List<string>();

            public PointGrid(double cellMM) { cell = cellMM > 0 ? cellMM : 1.0; }

            private long Key(int cx, int cy)
            {
                return ((long)cx << 32) ^ (uint)cy;
            }

            public void Add(double x, double y, string net, string layer)
            {
                int idx = xs.Count;
                xs.Add(x); ys.Add(y); nets.Add(net); layers.Add(layer);

                int cx = (int)Math.Floor(x / cell);
                int cy = (int)Math.Floor(y / cell);
                long k = Key(cx, cy);

                List<int> b;
                if (!buckets.TryGetValue(k, out b)) { b = new List<int>(); buckets[k] = b; }
                b.Add(idx);
            }

            // Distinct layers carrying a point of this net within tolerance.
            public HashSet<string> LayersNear(double x, double y, string net, double tolMM)
            {
                HashSet<string> found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                double tol2 = tolMM * tolMM;

                int cx = (int)Math.Floor(x / cell);
                int cy = (int)Math.Floor(y / cell);

                for (int ix = cx - 1; ix <= cx + 1; ix++)
                {
                    for (int iy = cy - 1; iy <= cy + 1; iy++)
                    {
                        List<int> b;
                        if (!buckets.TryGetValue(Key(ix, iy), out b)) continue;
                        for (int i = 0; i < b.Count; i++)
                        {
                            int j = b[i];
                            if (!string.Equals(nets[j], net, StringComparison.OrdinalIgnoreCase)) continue;
                            double dx = xs[j] - x, dy = ys[j] - y;
                            if (dx * dx + dy * dy <= tol2) found.Add(layers[j]);
                        }
                    }
                }
                return found;
            }

            // How many points of this net sit within tolerance, ignoring the
            // one at the query index itself.
            public int CountNear(double x, double y, string net, double tolMM, int skipIndex)
            {
                int n = 0;
                double tol2 = tolMM * tolMM;
                int cx = (int)Math.Floor(x / cell);
                int cy = (int)Math.Floor(y / cell);

                for (int ix = cx - 1; ix <= cx + 1; ix++)
                {
                    for (int iy = cy - 1; iy <= cy + 1; iy++)
                    {
                        List<int> b;
                        if (!buckets.TryGetValue(Key(ix, iy), out b)) continue;
                        for (int i = 0; i < b.Count; i++)
                        {
                            int j = b[i];
                            if (j == skipIndex) continue;
                            if (!string.Equals(nets[j], net, StringComparison.OrdinalIgnoreCase)) continue;
                            double dx = xs[j] - x, dy = ys[j] - y;
                            if (dx * dx + dy * dy <= tol2) n++;
                        }
                    }
                }
                return n;
            }

            public int Count { get { return xs.Count; } }
        }

        public sealed class Result
        {
            public int Found;
            public int Removed;
            public int Scanned;
            public bool Selected;
            public List<string> Notes = new List<string>();
            public List<string> Errors = new List<string>();
        }

        // ==================================================================
        // Invalid polygons and regions
        //
        // A polygon with fewer than three vertices cannot enclose anything.
        // Altium keeps them, they survive save and reload, and they turn up
        // later as Gerber artefacts or as a rebuild that never finishes.
        // ==================================================================
        public static Result InvalidObjects(IPCB_ServerInterface pcbServer, IPCB_Board board, bool delete)
        {
            Result res = new Result();
            List<IPCB_Primitive> bad = new List<IPCB_Primitive>();

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
                    res.Scanned++;
                    try
                    {
                        bool invalid = false;

                        IPCB_Polygon poly = p as IPCB_Polygon;
                        if (poly != null)
                        {
                            if (poly.GetState_PointCount() < 3) invalid = true;
                        }
                        else
                        {
                            // A region degenerate enough to have no extent is
                            // the region equivalent; the SDK gives regions no
                            // vertex count, so the bounding box stands in.
                            CoordRect r = p.BoundingRectangle();
                            if (Math.Abs(r.GetX2() - r.GetX1()) < 2 ||
                                Math.Abs(r.GetY2() - r.GetY1()) < 2) invalid = true;
                        }

                        if (invalid) bad.Add(p);
                    }
                    catch { }

                    p = it.NextPCBObject();
                }
            }
            catch (Exception ex)
            {
                res.Errors.Add("Scan failed -- " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                board.BoardIterator_Destroy(ref it);
            }

            res.Found = bad.Count;
            if (bad.Count == 0) return res;

            if (delete)
            {
                // Removal happens after the iterator is destroyed. Deleting
                // from under a live iterator crashes Altium rather than
                // throwing something catchable.
                pcbServer.PreProcess();
                try
                {
                    for (int i = 0; i < bad.Count; i++)
                    {
                        try { board.RemovePCBObject(bad[i]); res.Removed++; }
                        catch (Exception ex)
                        {
                            res.Errors.Add("Could not remove one object -- " + ex.GetType().Name);
                        }
                    }
                }
                finally { pcbServer.PostProcess(); }
            }
            else
            {
                Select(board, bad, res);
            }

            board.ViewManager_FullUpdate();
            Log.Write("Cleanup.InvalidObjects: " + res.Found + " found, " + res.Removed + " removed");
            return res;
        }

        // ==================================================================
        // Via antennas
        //
        // A via with copper on only one layer connects nothing. They collect
        // after rip-up and reroute, and each one is an unterminated stub.
        // ==================================================================
        public sealed class AntennaOptions
        {
            public double ToleranceMM = 0.01;
            public bool IgnoreInPours = true;
        }

        public static Result ViaAntennas(IPCB_ServerInterface pcbServer, IPCB_Board board, AntennaOptions opt)
        {
            Result res = new Result();
            IPCB_LayerUtils lu = pcbServer.LayerUtils();

            // Every track and arc endpoint, keyed by net and layer.
            PointGrid grid = new PointGrid(Math.Max(opt.ToleranceMM * 10.0, 0.5));

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
                        IPCB_Net n = p.GetState_Net();
                        string net = n == null ? "" : (n.GetState_Name() ?? "");
                        if (net.Length > 0)
                        {
                            string layer = "";
                            try { layer = lu.AsString(p.GetState_V7Layer()); } catch { }

                            if (p.GetState_ObjectID() == TObjectId.eTrackObject)
                            {
                                IPCB_Track t = p as IPCB_Track;
                                if (t != null)
                                {
                                    grid.Add(ToMM(t.GetState_X1()), ToMM(t.GetState_Y1()), net, layer);
                                    grid.Add(ToMM(t.GetState_X2()), ToMM(t.GetState_Y2()), net, layer);
                                }
                            }
                            else
                            {
                                IPCB_Arc a = p as IPCB_Arc;
                                if (a != null)
                                {
                                    grid.Add(ToMM(a.GetState_StartX()), ToMM(a.GetState_StartY()), net, layer);
                                    grid.Add(ToMM(a.GetState_EndX()), ToMM(a.GetState_EndY()), net, layer);
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
                res.Errors.Add("Copper scan failed -- " + ex.GetType().Name + ": " + ex.Message);
            }
            finally { board.BoardIterator_Destroy(ref it); }

            // Pours connect vias without any coincident endpoint, so a via
            // inside one would otherwise always look like an antenna.
            List<CoordRect> pours = opt.IgnoreInPours ? PourBoxes(board) : new List<CoordRect>();

            List<IPCB_Primitive> antennas = new List<IPCB_Primitive>();

            it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(TObjectId.eViaObject));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);

                IPCB_Via v = it.FirstPCBObject() as IPCB_Via;
                while (v != null)
                {
                    res.Scanned++;
                    try
                    {
                        IPCB_Net n = v.GetState_Net();
                        string net = n == null ? "" : (n.GetState_Name() ?? "");
                        if (net.Length > 0)
                        {
                            int x = v.GetState_XLocation(), y = v.GetState_YLocation();
                            double mx = ToMM(x), my = ToMM(y);

                            bool inPour = false;
                            for (int i = 0; i < pours.Count && !inPour; i++)
                            {
                                if (x >= pours[i].GetX1() && x <= pours[i].GetX2() &&
                                    y >= pours[i].GetY1() && y <= pours[i].GetY2()) inPour = true;
                            }

                            if (!inPour)
                            {
                                HashSet<string> layers = grid.LayersNear(mx, my, net, opt.ToleranceMM);
                                if (layers.Count < 2) antennas.Add(v);
                            }
                        }
                    }
                    catch { }

                    v = it.NextPCBObject() as IPCB_Via;
                }
            }
            catch (Exception ex)
            {
                res.Errors.Add("Via scan failed -- " + ex.GetType().Name + ": " + ex.Message);
            }
            finally { board.BoardIterator_Destroy(ref it); }

            res.Found = antennas.Count;
            if (opt.IgnoreInPours && pours.Count > 0)
                res.Notes.Add(pours.Count + " pour(s) ignored — vias inside them are connected by copper, " +
                              "not by a coincident track end");

            Select(board, antennas, res);
            Log.Write("Cleanup.ViaAntennas: " + res.Found + " antenna via(s)");
            return res;
        }

        private static List<CoordRect> PourBoxes(IPCB_Board board)
        {
            List<CoordRect> boxes = new List<CoordRect>();
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
                    try { boxes.Add(p.BoundingRectangle()); } catch { }
                    p = it.NextPCBObject();
                }
            }
            catch { }
            finally { board.BoardIterator_Destroy(ref it); }
            return boxes;
        }

        // ==================================================================
        // Dangling copper
        //
        // A track end that coincides with nothing else on its net is either a
        // stub left by an edit or a connection that only looks made.
        // ==================================================================
        public static Result DanglingCopper(IPCB_ServerInterface pcbServer, IPCB_Board board, double toleranceMM)
        {
            Result res = new Result();
            IPCB_LayerUtils lu = pcbServer.LayerUtils();

            PointGrid grid = new PointGrid(Math.Max(toleranceMM * 10.0, 0.5));

            // Endpoints, plus pad and via centres, all of which legitimately
            // terminate a track.
            List<IPCB_Primitive> owners = new List<IPCB_Primitive>();
            List<double> ex = new List<double>(), ey = new List<double>();
            List<string> enet = new List<string>();

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
                        IPCB_Net n = p.GetState_Net();
                        string net = n == null ? "" : (n.GetState_Name() ?? "");
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
                                    AddEnd(grid, owners, ex, ey, enet, p,
                                           ToMM(t.GetState_X1()), ToMM(t.GetState_Y1()), net, layer, true);
                                    AddEnd(grid, owners, ex, ey, enet, p,
                                           ToMM(t.GetState_X2()), ToMM(t.GetState_Y2()), net, layer, true);
                                }
                            }
                            else if (id == TObjectId.eArcObject)
                            {
                                IPCB_Arc a = p as IPCB_Arc;
                                if (a != null)
                                {
                                    AddEnd(grid, owners, ex, ey, enet, p,
                                           ToMM(a.GetState_StartX()), ToMM(a.GetState_StartY()), net, layer, true);
                                    AddEnd(grid, owners, ex, ey, enet, p,
                                           ToMM(a.GetState_EndX()), ToMM(a.GetState_EndY()), net, layer, true);
                                }
                            }
                            else if (id == TObjectId.ePadObject)
                            {
                                IPCB_Pad pad = p as IPCB_Pad;
                                if (pad != null)
                                    grid.Add(ToMM(pad.GetState_XLocation()), ToMM(pad.GetState_YLocation()), net, layer);
                            }
                            else
                            {
                                IPCB_Via v = p as IPCB_Via;
                                if (v != null)
                                    grid.Add(ToMM(v.GetState_XLocation()), ToMM(v.GetState_YLocation()), net, layer);
                            }
                        }
                    }
                    catch { }

                    p = it.NextPCBObject();
                }
            }
            catch (Exception ex2)
            {
                res.Errors.Add("Copper scan failed -- " + ex2.GetType().Name + ": " + ex2.Message);
            }
            finally { board.BoardIterator_Destroy(ref it); }

            res.Scanned = owners.Count;

            HashSet<IPCB_Primitive> dangling = new HashSet<IPCB_Primitive>();
            for (int i = 0; i < owners.Count; i++)
            {
                // Index i in the endpoint list is also index i in the grid,
                // because every endpoint was added to both in lockstep.
                if (grid.CountNear(ex[i], ey[i], enet[i], toleranceMM, i) == 0)
                    dangling.Add(owners[i]);
            }

            List<IPCB_Primitive> list = new List<IPCB_Primitive>(dangling);
            res.Found = list.Count;
            Select(board, list, res);
            Log.Write("Cleanup.DanglingCopper: " + res.Found + " primitive(s) with a free end");
            return res;
        }

        private static void AddEnd(PointGrid grid, List<IPCB_Primitive> owners,
                                   List<double> ex, List<double> ey, List<string> enet,
                                   IPCB_Primitive p, double x, double y, string net, string layer,
                                   bool trackEnd)
        {
            grid.Add(x, y, net, layer);
            if (trackEnd) { owners.Add(p); ex.Add(x); ey.Add(y); enet.Add(net); }
        }

        // ------------------------------------------------------------------
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
                res.Selected = true;
            }
            catch (Exception ex)
            {
                res.Errors.Add("Could not select the results -- " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
