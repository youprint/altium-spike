// Testpoints.cs
//
//   Coverage   -- which nets have a testpoint and which do not
//   Assign     -- mark suitable pads and vias on a net class as testpoints
//   PadCentres -- pads with no copper landing on their centre
//
// TESTPOINT FLAGS ARE FOUR, NOT ONE. IPCB_Primitive carries
// IsTestPoint_Top / _Bottom for the FABRICATION testpoint (what the bed-of-
// nails probes) and IsAssyTestPoint_Top / _Bottom for the ASSEMBLY testpoint
// (what a flying probe or an in-circuit fixture uses at assembly). They are
// independent, a board can have one without the other, and a coverage report
// that conflates them tells you a net is covered when the test house cannot
// reach it. All four are reported separately.
//
// PAD CENTRES is the odd one out and belongs here because it is a test
// concern. Altium's connectivity is centre-to-centre: a track that touches
// the edge of a pad is drawn as connected and behaves as connected, but a
// probe landing on the pad centre may be on copper that is only joined
// through the pad's own plating. The check is the same one the
// IsPadCenterConnected script does.

using DXP;
using PCB;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AltiumSpike
{
    public static class Testpoints
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

        public sealed class Result
        {
            public int Nets, Covered, Uncovered, Changed, Scanned, Found;
            public string CsvPath = "";
            public List<string> Notes = new List<string>();
            public List<string> Errors = new List<string>();
        }

        private sealed class NetCover
        {
            public bool FabTop, FabBottom, AssyTop, AssyBottom;
            public int Candidates;
        }

        // ==================================================================
        // Coverage
        // ==================================================================
        public static Result Coverage(IPCB_Board board, string folder)
        {
            Result res = new Result();

            Dictionary<string, NetCover> cover =
                new Dictionary<string, NetCover>(StringComparer.OrdinalIgnoreCase);

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
                        IPCB_Net n = p.GetState_Net();
                        string net = n == null ? "" : (n.GetState_Name() ?? "");
                        if (net.Length > 0)
                        {
                            NetCover c;
                            if (!cover.TryGetValue(net, out c)) { c = new NetCover(); cover[net] = c; }
                            c.Candidates++;

                            if (p.GetState_IsTestPoint_Top()) c.FabTop = true;
                            if (p.GetState_IsTestPoint_Bottom()) c.FabBottom = true;
                            if (p.GetState_IsAssyTestPoint_Top()) c.AssyTop = true;
                            if (p.GetState_IsAssyTestPoint_Bottom()) c.AssyBottom = true;
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
            rows.Add("Net,Candidates,FabTop,FabBottom,AssyTop,AssyBottom,AnyFab,AnyAssy");

            List<string> names = new List<string>(cover.Keys);
            names.Sort(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < names.Count; i++)
            {
                NetCover c = cover[names[i]];
                bool anyFab = c.FabTop || c.FabBottom;
                bool anyAssy = c.AssyTop || c.AssyBottom;

                if (anyFab) res.Covered++; else res.Uncovered++;

                rows.Add(string.Join(",",
                    Csv(names[i]), c.Candidates.ToString(Inv),
                    c.FabTop ? "Y" : "", c.FabBottom ? "Y" : "",
                    c.AssyTop ? "Y" : "", c.AssyBottom ? "Y" : "",
                    anyFab ? "Y" : "N", anyAssy ? "Y" : "N"));
            }

            res.Nets = names.Count;

            if (!string.IsNullOrEmpty(folder))
            {
                try
                {
                    string path = Path.Combine(folder, "testpoint_coverage.csv");
                    File.WriteAllLines(path, rows, new UTF8Encoding(false));
                    res.CsvPath = path;
                    Log.Write("wrote " + path + " (" + names.Count + " net(s))");
                }
                catch (Exception ex)
                {
                    res.Errors.Add("Could not write the CSV -- " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            res.Notes.Add("Fabrication and assembly testpoints are separate flags; a net can have one " +
                          "without the other.");
            return res;
        }

        // ==================================================================
        // Assign
        //
        // Marks vias, and optionally through-hole pads, on the chosen nets as
        // fabrication testpoints. Surface-mount pads are never marked: a bed
        // of nails probing an SMD pad damages the joint it is meant to test.
        // ==================================================================
        public sealed class AssignOptions
        {
            public string NetClass = "";      // empty = every net
            public bool IncludePads = false;  // through-hole pads as well as vias
            public bool Assembly = false;     // set the assembly flags instead
            public bool OnlySelection = false;
            public bool OnlyUncovered = true; // skip nets that already have one
        }

        public static Result Assign(IPCB_ServerInterface pcbServer, IPCB_Board board, AssignOptions opt)
        {
            Result res = new Result();

            HashSet<string> allowed = null;
            if (opt.NetClass.Trim().Length > 0)
            {
                allowed = NetsInClass(board, opt.NetClass.Trim());
                if (allowed == null)
                {
                    res.Errors.Add("No net class called \"" + opt.NetClass.Trim() + "\" on this board.");
                    return res;
                }
                if (allowed.Count == 0)
                {
                    res.Errors.Add("Net class \"" + opt.NetClass.Trim() + "\" has no members.");
                    return res;
                }
            }

            // Nets that already carry a testpoint are collected during the
            // same pass that gathers candidates, so OnlyUncovered can skip
            // them without a second scan.
            List<IPCB_Primitive> targets = new List<IPCB_Primitive>();
            Dictionary<string, bool> netHas = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

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
                        IPCB_Net n = p.GetState_Net();
                        string net = n == null ? "" : (n.GetState_Name() ?? "");
                        if (net.Length == 0) { p = it.NextPCBObject(); continue; }
                        if (allowed != null && !allowed.Contains(net)) { p = it.NextPCBObject(); continue; }
                        if (opt.OnlySelection && !p.GetState_Selected()) { p = it.NextPCBObject(); continue; }

                        bool already = opt.Assembly
                            ? (p.GetState_IsAssyTestPoint_Top() || p.GetState_IsAssyTestPoint_Bottom())
                            : (p.GetState_IsTestPoint_Top() || p.GetState_IsTestPoint_Bottom());
                        if (already) netHas[net] = true;

                        bool usable = false;
                        if (p.GetState_ObjectID() == TObjectId.eViaObject) usable = true;
                        else if (opt.IncludePads)
                        {
                            IPCB_Pad pad = p as IPCB_Pad;
                            // Through-hole only. Probing an SMD pad damages
                            // the joint the test exists to verify.
                            if (pad != null && pad.GetState_HoleSize() > 0) usable = true;
                        }

                        if (usable && !already) targets.Add(p);
                        res.Scanned++;
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

            pcbServer.PreProcess();
            try
            {
                HashSet<string> done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < targets.Count; i++)
                {
                    try
                    {
                        IPCB_Primitive p = targets[i];
                        IPCB_Net n = p.GetState_Net();
                        string net = n == null ? "" : (n.GetState_Name() ?? "");

                        if (opt.OnlyUncovered)
                        {
                            // One testpoint per net is what a test house
                            // needs; marking every via on GND helps nobody.
                            if (netHas.ContainsKey(net) || done.Contains(net)) continue;
                        }

                        if (opt.Assembly)
                        {
                            p.SetState_IsAssyTestPoint_Top(true);
                            p.SetState_IsAssyTestPoint_Bottom(true);
                        }
                        else
                        {
                            p.SetState_IsTestPoint_Top(true);
                            p.SetState_IsTestPoint_Bottom(true);
                        }

                        done.Add(net);
                        res.Changed++;
                    }
                    catch (Exception ex)
                    {
                        res.Errors.Add("Item " + i + ": " + ex.GetType().Name + " -- " + ex.Message);
                    }
                }
            }
            finally { pcbServer.PostProcess(); }

            board.ViewManager_FullUpdate();
            Log.Write("Testpoints.Assign: " + res.Changed + " marked");
            return res;
        }

        private static HashSet<string> NetsInClass(IPCB_Board board, string className)
        {
            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(TObjectId.eClassObject));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);

                IPCB_ObjectClass cls = it.FirstPCBObject() as IPCB_ObjectClass;
                while (cls != null)
                {
                    try
                    {
                        if (cls.GetState_MemberKind() == TClassMemberKind.eClassMemberKind_Net &&
                            string.Equals(cls.GetState_Name(), className, StringComparison.OrdinalIgnoreCase))
                        {
                            // IPCB_ObjectClass answers IsMember(name) but will
                            // not enumerate, so the membership set is built by
                            // asking it about every net on the board.
                            HashSet<string> members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (string net in AllNets(board))
                                if (cls.IsMember(net)) members.Add(net);
                            return members;
                        }
                    }
                    catch { }
                    cls = it.NextPCBObject() as IPCB_ObjectClass;
                }
            }
            catch { }
            finally { board.BoardIterator_Destroy(ref it); }
            return null;
        }

        private static List<string> AllNets(IPCB_Board board)
        {
            List<string> nets = new List<string>();
            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(TObjectId.eNetObject));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);

                IPCB_Net n = it.FirstPCBObject() as IPCB_Net;
                while (n != null)
                {
                    try { string s = n.GetState_Name(); if (!string.IsNullOrEmpty(s)) nets.Add(s); }
                    catch { }
                    n = it.NextPCBObject() as IPCB_Net;
                }
            }
            catch { }
            finally { board.BoardIterator_Destroy(ref it); }
            return nets;
        }

        // ==================================================================
        // Pad centres
        // ==================================================================
        public static Result PadCentres(IPCB_ServerInterface pcbServer, IPCB_Board board, double toleranceMM)
        {
            Result res = new Result();
            IPCB_LayerUtils lu = pcbServer.LayerUtils();

            // Track and arc endpoints, keyed loosely by position.
            Dictionary<long, List<int>> buckets = new Dictionary<long, List<int>>();
            List<double> px = new List<double>(), py = new List<double>();
            List<string> pnet = new List<string>();
            double cell = Math.Max(toleranceMM * 10.0, 0.5);

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
                            if (p.GetState_ObjectID() == TObjectId.eTrackObject)
                            {
                                IPCB_Track t = p as IPCB_Track;
                                if (t != null)
                                {
                                    AddPt(buckets, px, py, pnet, cell, ToMM(t.GetState_X1()), ToMM(t.GetState_Y1()), net);
                                    AddPt(buckets, px, py, pnet, cell, ToMM(t.GetState_X2()), ToMM(t.GetState_Y2()), net);
                                }
                            }
                            else
                            {
                                IPCB_Arc a = p as IPCB_Arc;
                                if (a != null)
                                {
                                    AddPt(buckets, px, py, pnet, cell, ToMM(a.GetState_StartX()), ToMM(a.GetState_StartY()), net);
                                    AddPt(buckets, px, py, pnet, cell, ToMM(a.GetState_EndX()), ToMM(a.GetState_EndY()), net);
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

            List<IPCB_Primitive> lonely = new List<IPCB_Primitive>();

            it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(TObjectId.ePadObject));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);

                IPCB_Pad pad = it.FirstPCBObject() as IPCB_Pad;
                while (pad != null)
                {
                    try
                    {
                        IPCB_Net n = pad.GetState_Net();
                        string net = n == null ? "" : (n.GetState_Name() ?? "");
                        if (net.Length > 0)
                        {
                            res.Scanned++;
                            double x = ToMM(pad.GetState_XLocation());
                            double y = ToMM(pad.GetState_YLocation());
                            if (!Near(buckets, px, py, pnet, cell, x, y, net, toleranceMM))
                                lonely.Add(pad);
                        }
                    }
                    catch { }

                    pad = it.NextPCBObject() as IPCB_Pad;
                }
            }
            catch (Exception ex)
            {
                res.Errors.Add("Pad scan failed -- " + ex.GetType().Name + ": " + ex.Message);
            }
            finally { board.BoardIterator_Destroy(ref it); }

            res.Found = lonely.Count;

            if (lonely.Count > 0)
            {
                try
                {
                    board.SelectedObjects_BeginUpdate();
                    board.SelectedObjects_Clear();
                    for (int i = 0; i < lonely.Count; i++) board.SelectedObjects_Add(lonely[i]);
                    board.SelectedObjects_EndUpdate();
                    board.ViewManager_FullUpdate();
                }
                catch (Exception ex)
                {
                    res.Errors.Add("Could not select the pads -- " + ex.GetType().Name);
                }
            }

            res.Notes.Add("A pad fed only by a polygon pour has no track end at its centre and will appear " +
                          "here; that is expected, not a fault.");
            Log.Write("Testpoints.PadCentres: " + res.Found + " pad(s) with nothing at the centre");
            return res;
        }

        private static long Key(double x, double y, double cell)
        {
            int cx = (int)Math.Floor(x / cell);
            int cy = (int)Math.Floor(y / cell);
            return ((long)cx << 32) ^ (uint)cy;
        }

        private static void AddPt(Dictionary<long, List<int>> buckets, List<double> px, List<double> py,
                                  List<string> pnet, double cell, double x, double y, string net)
        {
            int idx = px.Count;
            px.Add(x); py.Add(y); pnet.Add(net);

            long k = Key(x, y, cell);
            List<int> b;
            if (!buckets.TryGetValue(k, out b)) { b = new List<int>(); buckets[k] = b; }
            b.Add(idx);
        }

        private static bool Near(Dictionary<long, List<int>> buckets, List<double> px, List<double> py,
                                 List<string> pnet, double cell, double x, double y, string net, double tolMM)
        {
            double tol2 = tolMM * tolMM;
            int cx = (int)Math.Floor(x / cell);
            int cy = (int)Math.Floor(y / cell);

            for (int ix = cx - 1; ix <= cx + 1; ix++)
            {
                for (int iy = cy - 1; iy <= cy + 1; iy++)
                {
                    List<int> b;
                    if (!buckets.TryGetValue(((long)ix << 32) ^ (uint)iy, out b)) continue;
                    for (int i = 0; i < b.Count; i++)
                    {
                        int j = b[i];
                        if (!string.Equals(pnet[j], net, StringComparison.OrdinalIgnoreCase)) continue;
                        double dx = px[j] - x, dy = py[j] - y;
                        if (dx * dx + dy * dy <= tol2) return true;
                    }
                }
            }
            return false;
        }
    }
}
