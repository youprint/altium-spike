// ViaTools.cs
//
// Three via utilities that sit beside the via fence:
//
//   ReturnViaCheck   -- finds signal vias with no return via nearby
//   SetTenting       -- tents or opens the solder mask over vias
//   BarrelRelief     -- opens mask from the HOLE edge on large vias
//
// RETURN VIA CHECK is the one that earns its place. When a high-speed signal
// changes layer, its return current has to change reference plane with it,
// and it can only do that through a nearby stitching via between the two
// planes. Without one the return current takes a long detour, and that loop
// area is radiated emission and crosstalk. It is invisible in DRC, invisible
// on screen, and it is one of the most common EMC findings on an otherwise
// clean board.
//
// The check is deliberately simple: for every via carrying a signal net,
// find the nearest via on the reference net, and flag it if that distance
// exceeds a threshold. It does NOT attempt to work out which reference
// planes the signal actually transitions between, or whether the return via
// connects the right pair of planes. That analysis needs the full stackup
// and the plane assignment of both layers, and getting it subtly wrong would
// produce confident wrong answers. What this gives you is the list worth
// looking at, which is what you want from a screen.
//
// SELECTION AS OUTPUT. Offenders are selected on the board as well as
// reported, because a list of coordinates is useless and a highlighted set
// of vias can be stepped through with the keyboard.

using DXP;
using PCB;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AltiumSpike
{
    public static class ViaTools
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

        // ==================================================================
        // Return via check
        // ==================================================================
        public sealed class ReturnViaOptions
        {
            public string ReferenceNet = "GND";
            public double MaxDistanceMM = 2.0;
            public bool SelectOffenders = true;
            public bool OnlySelection = false;   // check only the selected vias
            public string WriteCsvTo = null;     // null = no file
        }

        public sealed class ReturnViaResult
        {
            public int SignalViasChecked;
            public int ReferenceVias;
            public int Offenders;
            public double WorstDistanceMM;
            public string WorstNet = "";
            public string CsvPath = "";
            public List<string> Errors = new List<string>();

            public string Summarise(ReturnViaOptions opt)
            {
                if (ReferenceVias == 0)
                    return "No vias found on net \"" + opt.ReferenceNet + "\". " +
                           "Nothing can be checked against.\n" +
                           "Check the reference net name.";

                string s = "Checked " + SignalViasChecked + " signal via(s) against " +
                           ReferenceVias + " via(s) on " + opt.ReferenceNet + ".\n";

                if (Offenders == 0)
                {
                    s += "Every signal via has a return via within " +
                         opt.MaxDistanceMM.ToString("0.###", Inv) + " mm.\n";
                }
                else
                {
                    s += Offenders + " via(s) have NO return via within " +
                         opt.MaxDistanceMM.ToString("0.###", Inv) + " mm.\n";
                    s += "Worst: " + F3(WorstDistanceMM) + " mm on net " + WorstNet + ".\n";
                    if (opt.SelectOffenders) s += "They are selected on the board.\n";
                }
                if (CsvPath.Length > 0) s += "Wrote " + CsvPath + "\n";
                foreach (string e in Errors) s += e + "\n";
                return s;
            }
        }

        private struct ViaPt
        {
            public double X, Y;
            public string Net;
            public IPCB_Via Via;
        }

        public static ReturnViaResult ReturnViaCheck(IPCB_ServerInterface pcbServer, IPCB_Board board,
                                                     ReturnViaOptions opt)
        {
            ReturnViaResult res = new ReturnViaResult();

            if (opt.MaxDistanceMM <= 0.0)
            { res.Errors.Add("Maximum distance must be greater than 0."); return res; }

            string refNet = (opt.ReferenceNet ?? "").Trim().ToUpperInvariant();
            if (refNet.Length == 0)
            { res.Errors.Add("Enter the reference net (usually GND)."); return res; }

            List<ViaPt> reference = new List<ViaPt>();
            List<ViaPt> signal = new List<ViaPt>();

            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(TObjectId.eViaObject));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);

                IPCB_Via v = it.FirstPCBObject() as IPCB_Via;
                while (v != null)
                {
                    try
                    {
                        string net = "";
                        IPCB_Net n = v.GetState_Net();
                        if (n != null) net = n.GetState_Name() ?? "";

                        ViaPt p = new ViaPt();
                        p.X = ToMM(v.GetState_XLocation());
                        p.Y = ToMM(v.GetState_YLocation());
                        p.Net = net;
                        p.Via = v;

                        if (net.ToUpperInvariant() == refNet) reference.Add(p);
                        else if (net.Length > 0)
                        {
                            // A via with no net is a mechanical or stitching
                            // via and has no return path to check.
                            if (!opt.OnlySelection || v.GetState_Selected()) signal.Add(p);
                        }
                    }
                    catch { /* one unreadable via must not lose the scan */ }

                    v = it.NextPCBObject() as IPCB_Via;
                }
            }
            catch (Exception ex)
            {
                res.Errors.Add("Via scan failed -- " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                board.BoardIterator_Destroy(ref it);
            }

            res.ReferenceVias = reference.Count;
            res.SignalViasChecked = signal.Count;
            Log.Write("ReturnViaCheck: " + signal.Count + " signal via(s), " +
                      reference.Count + " on " + opt.ReferenceNet);

            if (reference.Count == 0 || signal.Count == 0) return res;

            // Linear nearest-neighbour. A dense board runs a few hundred
            // signal vias against a couple of thousand ground vias, which is
            // well under a million distance tests -- far cheaper than the
            // spatial index it would take to avoid them.
            double limit2 = opt.MaxDistanceMM * opt.MaxDistanceMM;
            List<IPCB_Via> bad = new List<IPCB_Via>();
            List<string> rows = new List<string>();
            rows.Add("Net,X,Y,NearestReturnViaMM,Pass");

            for (int i = 0; i < signal.Count; i++)
            {
                double best2 = double.MaxValue;
                for (int j = 0; j < reference.Count; j++)
                {
                    double dx = reference[j].X - signal[i].X;
                    double dy = reference[j].Y - signal[i].Y;
                    double d2 = dx * dx + dy * dy;
                    if (d2 < best2) best2 = d2;
                }

                double best = Math.Sqrt(best2);
                bool pass = best2 <= limit2;

                rows.Add(string.Join(",",
                    Csv(signal[i].Net), F3(signal[i].X), F3(signal[i].Y),
                    F3(best), pass ? "PASS" : "FAIL"));

                if (!pass)
                {
                    bad.Add(signal[i].Via);
                    if (best > res.WorstDistanceMM)
                    {
                        res.WorstDistanceMM = best;
                        res.WorstNet = signal[i].Net;
                    }
                }
            }

            res.Offenders = bad.Count;

            if (opt.SelectOffenders)
            {
                try
                {
                    board.SelectedObjects_BeginUpdate();
                    board.SelectedObjects_Clear();
                    for (int i = 0; i < bad.Count; i++) board.SelectedObjects_Add(bad[i]);
                    board.SelectedObjects_EndUpdate();
                    board.ViewManager_FullUpdate();
                }
                catch (Exception ex)
                {
                    res.Errors.Add("Could not select the offenders -- " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            if (!string.IsNullOrEmpty(opt.WriteCsvTo))
            {
                try
                {
                    string path = Path.Combine(opt.WriteCsvTo, "return_via_check.csv");
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
        // Tenting / mask expansion
        // ==================================================================
        public sealed class TentOptions
        {
            public bool Tent = true;             // true = cover, false = open
            public double OpenExpansionMM = 0.05;// used when opening
            public bool OnlySelection = false;
            public double MinHoleMM = 0.0;       // 0 = no lower bound
            public double MaxHoleMM = 0.0;       // 0 = no upper bound
        }

        public sealed class TentResult
        {
            public int Considered, Changed;
            public List<string> Errors = new List<string>();

            public string Summarise(TentOptions opt)
            {
                string s = (opt.Tent ? "Tented " : "Opened mask over ") + Changed +
                           " via(s) of " + Considered + " considered.\n";
                if (Changed > 0) s += "Solder mask expansion was set manually, overriding the rule.\n";
                foreach (string e in Errors) s += e + "\n";
                return s;
            }
        }

        public static TentResult SetTenting(IPCB_ServerInterface pcbServer, IPCB_Board board, TentOptions opt)
        {
            TentResult res = new TentResult();
            List<IPCB_Via> targets = CollectVias(board, opt.OnlySelection, opt.MinHoleMM, opt.MaxHoleMM, res.Errors);
            res.Considered = targets.Count;
            if (targets.Count == 0) return res;

            pcbServer.PreProcess();
            try
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    try
                    {
                        IPCB_Via v = targets[i];
                        V7_PadCache c = v.GetState_Cache();

                        if (opt.Tent)
                        {
                            // Tenting is not a flag in the SDK -- it is a mask
                            // opening small enough to vanish. An expansion of
                            // minus the pad radius closes the aperture exactly;
                            // a little more guarantees it across fab tolerance.
                            double radius = ToMM(v.GetState_Size()) / 2.0;
                            c.SolderMaskExpansion = ToCoord(-(radius + 0.05));
                        }
                        else
                        {
                            c.SolderMaskExpansion = ToCoord(opt.OpenExpansionMM);
                        }

                        // Without the manual flag the value is recomputed from
                        // the solder mask expansion RULE on the next rebuild
                        // and the change silently disappears.
                        c.SolderMaskExpansionValid = TCacheState.eCacheManual;

                        v.SetState_Cache(c);
                        res.Changed++;
                    }
                    catch (Exception ex)
                    {
                        res.Errors.Add("Via " + i + ": " + ex.GetType().Name + " -- " + ex.Message);
                    }
                }
            }
            finally
            {
                pcbServer.PostProcess();
            }

            board.ViewManager_FullUpdate();
            Log.Write("ViaTools.SetTenting: " + res.Changed + " via(s) changed (tent=" + opt.Tent + ")");
            return res;
        }

        // ==================================================================
        // Barrel relief
        //
        // A large plated hole under solder mask is a mask-cracking risk: the
        // mask bridges the barrel with nothing under it. Opening the mask a
        // fixed distance FROM THE HOLE EDGE rather than from the pad edge
        // gives a consistent annular opening whatever the pad size.
        // ==================================================================
        public sealed class BarrelReliefOptions
        {
            public double MinHoleMM = 0.5;       // only vias at least this big
            public double ReliefMM = 0.05;       // opening measured from the hole edge
            public bool OnlySelection = false;
        }

        public sealed class BarrelResult
        {
            public int Considered, Changed;
            public List<string> Errors = new List<string>();

            public string Summarise(BarrelReliefOptions opt)
            {
                string s = "Relieved " + Changed + " via(s) of " + Considered +
                           " with a hole of at least " + opt.MinHoleMM.ToString("0.###", Inv) + " mm.\n";
                if (Changed > 0)
                    s += "Mask opens " + opt.ReliefMM.ToString("0.###", Inv) + " mm from the hole edge.\n";
                foreach (string e in Errors) s += e + "\n";
                return s;
            }
        }

        public static BarrelResult BarrelRelief(IPCB_ServerInterface pcbServer, IPCB_Board board,
                                                BarrelReliefOptions opt)
        {
            BarrelResult res = new BarrelResult();

            if (opt.MinHoleMM <= 0.0)
            { res.Errors.Add("Minimum hole size must be greater than 0."); return res; }

            List<IPCB_Via> targets = CollectVias(board, opt.OnlySelection, opt.MinHoleMM, 0.0, res.Errors);
            res.Considered = targets.Count;
            if (targets.Count == 0) return res;

            pcbServer.PreProcess();
            try
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    try
                    {
                        IPCB_Via v = targets[i];

                        // Switches the expansion's datum from the pad edge to
                        // the hole edge; without it the value below would be
                        // measured from the wrong place.
                        v.SetState_SolderMaskExpansionFromHoleEdge(true);

                        V7_PadCache c = v.GetState_Cache();
                        c.SolderMaskExpansion = ToCoord(opt.ReliefMM);
                        c.SolderMaskExpansionValid = TCacheState.eCacheManual;
                        v.SetState_Cache(c);

                        res.Changed++;
                    }
                    catch (Exception ex)
                    {
                        res.Errors.Add("Via " + i + ": " + ex.GetType().Name + " -- " + ex.Message);
                    }
                }
            }
            finally
            {
                pcbServer.PostProcess();
            }

            board.ViewManager_FullUpdate();
            Log.Write("ViaTools.BarrelRelief: " + res.Changed + " via(s) changed");
            return res;
        }

        // ------------------------------------------------------------------
        private static List<IPCB_Via> CollectVias(IPCB_Board board, bool onlySelection,
                                                  double minHoleMM, double maxHoleMM,
                                                  List<string> errors)
        {
            List<IPCB_Via> vias = new List<IPCB_Via>();

            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(TObjectId.eViaObject));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);

                IPCB_Via v = it.FirstPCBObject() as IPCB_Via;
                while (v != null)
                {
                    try
                    {
                        bool take = true;
                        if (onlySelection && !v.GetState_Selected()) take = false;

                        if (take && (minHoleMM > 0.0 || maxHoleMM > 0.0))
                        {
                            double hole = ToMM(v.GetState_HoleSize());
                            if (minHoleMM > 0.0 && hole < minHoleMM) take = false;
                            if (maxHoleMM > 0.0 && hole > maxHoleMM) take = false;
                        }

                        if (take) vias.Add(v);
                    }
                    catch { }

                    v = it.NextPCBObject() as IPCB_Via;
                }
            }
            catch (Exception ex)
            {
                errors.Add("Via scan failed -- " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                board.BoardIterator_Destroy(ref it);
            }

            return vias;
        }
    }
}
