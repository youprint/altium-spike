// NetLengths.cs
//
// Exports routed length and propagation delay for every net, and for every
// pin pair within those nets, to two CSVs:
//
//     net_lengths.csv
//       Net,Class,PinCount,ViaCount,RoutedLength,SignalLength,
//       DelayTotal,SignalDelay,InDiffPair
//     pin_pair_lengths.csv
//       Net,Class,PinPair,FromPin,ToPin,RoutedLength,UnroutedLength,
//       TotalLength,DelayTotal,NodeCount
//
// Read-only: nothing here mutates the board.
//
// WHY BOTH FILES. A net-level total is the wrong number for skew matching
// the moment a net has more than two nodes. A DDR4 address line routed as a
// fly-by to four DRAMs is ONE net with ONE RoutedLength, but four different
// pin-to-pin distances, and it is those four you match against the clock.
// pin_pair_lengths.csv gives you the per-segment numbers; net_lengths.csv
// stays useful for simple point-to-point nets and for a quick total.
//
// WHY NOT COMPUTE LENGTH BY SUMMING TRACKS. The obvious implementation --
// iterate tracks and arcs, sum their lengths per net -- is wrong in ways
// that matter at speed. It misses the via barrel contribution, it double
// counts overlapping segments after a loop removal, and it has no idea
// about xSignals that span series terminators. Altium already computes all
// of this, and the SDK exposes the answer:
//
//   IPCB_Net.GetState_RoutedLength()    Int32 coord   -- classic routed length
//   IPCB_Net2.GetState_RoutedLength64() Int64 coord   -- same, wide
//   IPCB_Net2.GetState_SignalLength()   Int64 coord   -- xSignal-aware
//   IPCB_Net2.GetState_DelayTotal()     Double
//   IPCB_Net2.GetState_SignalDelay()    Double
//   IPCB_PinPair.GetState_RoutedLength()   Int64 coord
//   IPCB_PinPair.GetState_UnroutedLength() Int64 coord
//   IPCB_PinPair.GetState_Length()         Int64 coord
//   IPCB_PinPair.GetState_DelayTotal()     Double
//
// So these numbers agree with the PCB panel by construction rather than by
// my arithmetic happening to match Altium's.
//
// DELAY UNITS. The SDK returns a bare Double with no unit in the signature
// and no documentation. Rather than guess and mislabel a column, the writer
// records the raw value and the header names it DelayTotal_Raw until it has
// been checked against Altium's own panel on a real board. See DelayNote().

using DXP;
using PCB;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AltiumSpike
{
    public static class NetLengths
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // Altium's own coord-per-mm ratio, derived from its own converter so
        // this cannot drift from CoordToMMs if the internal unit ever changes.
        private static readonly double CoordPerMM = 1000000.0 / EDP.Utils.CoordToMMs(1000000);

        private static double MM(int coord) { return EDP.Utils.CoordToMMs(coord); }

        // Lengths come back as Int64. CoordToMMs only takes Int32, and at
        // 1e-4 mil per unit an Int32 tops out around 54 metres -- comfortably
        // past any real net, but a corrupt board can hand back nonsense, so
        // fall back to the ratio rather than overflowing a cast.
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

        private static string F4(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "";
            double r = Math.Round(v, 4, MidpointRounding.AwayFromZero);
            if (r == 0) r = 0;
            return r.ToString("0.0000", Inv);
        }

        private static string Csv(string s)
        {
            if (s == null) return "";
            if (s.IndexOf(',') < 0 && s.IndexOf('"') < 0 && s.IndexOf('\n') < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        private static void Save(string folder, string fileName, List<string> lines)
        {
            string path = Path.Combine(folder, fileName);
            File.WriteAllLines(path, lines, new UTF8Encoding(false));
            Log.Write("wrote " + path + " (" + (lines.Count - 1) + " data row(s))");
        }

        public sealed class Result
        {
            public int NetRows;
            public int PinPairRows;
            public int NetsWithoutNet2;
            public bool PinPairsUnavailable;
            public List<string> Notes = new List<string>();
        }

        // ------------------------------------------------------------------
        // Net class lookup
        //
        // IPCB_ObjectClass exposes IsMember(String) but no member enumeration
        // on the base interface, so the mapping is built the other way round:
        // walk the classes once, and for each net ask each net-kind class
        // whether it claims that net. Boards have a handful of classes, so
        // this stays cheap.
        //
        // "All Nets" is skipped -- every net is in it, so reporting it tells
        // you nothing and would mask the class you actually care about.
        // ------------------------------------------------------------------
        private static List<IPCB_ObjectClass> NetClasses(IPCB_Board board)
        {
            List<IPCB_ObjectClass> classes = new List<IPCB_ObjectClass>();
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
                        if (cls.GetState_MemberKind() == TClassMemberKind.eClassMemberKind_Net)
                        {
                            string nm = cls.GetState_Name();
                            if (!string.Equals(nm, "All Nets", StringComparison.OrdinalIgnoreCase))
                                classes.Add(cls);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Write("NetLengths: skipped a class -- " + ex.GetType().Name + ": " + ex.Message);
                    }
                    cls = it.NextPCBObject() as IPCB_ObjectClass;
                }
            }
            finally
            {
                board.BoardIterator_Destroy(ref it);
            }
            Log.Write("NetLengths: " + classes.Count + " net class(es) found");
            return classes;
        }

        // A net can legitimately sit in several classes. Joining with ';'
        // keeps one row per net rather than fanning out and inflating counts.
        private static string ClassesOf(List<IPCB_ObjectClass> classes, string netName)
        {
            List<string> hits = null;
            for (int i = 0; i < classes.Count; i++)
            {
                try
                {
                    if (classes[i].IsMember(netName))
                    {
                        if (hits == null) hits = new List<string>();
                        hits.Add(classes[i].GetState_Name());
                    }
                }
                catch { /* a class that refuses the query is simply not reported */ }
            }
            return hits == null ? "" : string.Join(";", hits);
        }

        // Delays are cached. Without a reset a board edited since the last
        // calculation can hand back stale numbers, which is exactly the sort
        // of silent wrongness that makes a length report worse than useless.
        private static void ResetDelays(IPCB_Board board)
        {
            try
            {
                IPCB_BoardEx ex = board as IPCB_BoardEx;
                if (ex != null) { ex.ResetDelaysCalculator(); Log.Write("NetLengths: delay calculator reset"); }
                else Log.Write("NetLengths: board is not IPCB_BoardEx -- delays not reset");
            }
            catch (Exception e)
            {
                Log.Write("NetLengths: ResetDelaysCalculator failed -- " + e.GetType().Name + ": " + e.Message);
            }
        }

        // ------------------------------------------------------------------
        // net_lengths.csv
        // ------------------------------------------------------------------
        public static Result Export(IPCB_Board board, string folder)
        {
            Result res = new Result();
            ResetDelays(board);
            List<IPCB_ObjectClass> classes = NetClasses(board);

            List<string> lines = new List<string>();
            lines.Add("Net,Class,PinCount,ViaCount,RoutedLength,SignalLength,DelayTotal_Raw,SignalDelay_Raw,InDiffPair");

            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(TObjectId.eNetObject));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);

                IPCB_Net net = it.FirstPCBObject() as IPCB_Net;
                while (net != null)
                {
                    string name = "?";
                    try
                    {
                        name = net.GetState_Name();

                        int pinCount = net.GetState_PinCount();
                        int viaCount = net.GetState_ViaCount();
                        bool inDiff = net.GetState_InDifferentialPair();

                        // IPCB_Net2 carries the wide lengths and the delays.
                        // An older board format may not implement it, in
                        // which case fall back to the Int32 routed length and
                        // leave the signal/delay columns empty rather than
                        // writing a zero that reads like a real measurement.
                        double routedMM;
                        string signalMM = "";
                        string delayTotal = "";
                        string signalDelay = "";

                        IPCB_Net2 n2 = net as IPCB_Net2;
                        if (n2 != null)
                        {
                            routedMM = MM64(n2.GetState_RoutedLength64());
                            signalMM = F3(MM64(n2.GetState_SignalLength()));
                            delayTotal = F4(n2.GetState_DelayTotal());
                            signalDelay = F4(n2.GetState_SignalDelay());
                        }
                        else
                        {
                            routedMM = MM(net.GetState_RoutedLength());
                            res.NetsWithoutNet2++;
                        }

                        lines.Add(string.Join(",",
                            Csv(name),
                            Csv(ClassesOf(classes, name)),
                            pinCount.ToString(Inv),
                            viaCount.ToString(Inv),
                            F3(routedMM),
                            signalMM,
                            delayTotal,
                            signalDelay,
                            inDiff ? "True" : "False"));
                        res.NetRows++;
                    }
                    catch (Exception ex)
                    {
                        Log.Write("net_lengths: skipped net " + name + " -- " + ex.GetType().Name + ": " + ex.Message);
                    }

                    net = it.NextPCBObject() as IPCB_Net;
                }
            }
            finally
            {
                board.BoardIterator_Destroy(ref it);
            }

            Save(folder, "net_lengths.csv", lines);

            if (res.NetsWithoutNet2 > 0)
                res.Notes.Add(res.NetsWithoutNet2 + " net(s) had no IPCB_Net2 -- signal length and delay left blank");

            ExportPinPairs(board, folder, classes, res);
            return res;
        }

        // ------------------------------------------------------------------
        // pin_pair_lengths.csv
        //
        // Pin pairs come from the board's PinPairsManager rather than from
        // the nets. A pin pair knows its two endpoint primitives, so the net
        // is read off the first endpoint instead of being tracked separately.
        // ------------------------------------------------------------------
        private static void ExportPinPairs(IPCB_Board board, string folder,
                                           List<IPCB_ObjectClass> classes, Result res)
        {
            List<string> lines = new List<string>();
            lines.Add("Net,Class,PinPair,FromPin,ToPin,RoutedLength,UnroutedLength,TotalLength,DelayTotal_Raw,NodeCount");

            IPCB_PinPairsManager mgr = null;
            try
            {
                mgr = board.GetState_PinPairsManager();
            }
            catch (Exception ex)
            {
                Log.Write("pin_pair_lengths: PinPairsManager unavailable -- " + ex.GetType().Name + ": " + ex.Message);
            }

            if (mgr == null)
            {
                res.PinPairsUnavailable = true;
                res.Notes.Add("pin pairs unavailable on this board -- pin_pair_lengths.csv written empty");
                Save(folder, "pin_pair_lengths.csv", lines);
                return;
            }

            int count = 0;
            try { count = mgr.GetState_PinPairsCount(); }
            catch (Exception ex)
            {
                Log.Write("pin_pair_lengths: PinPairsCount failed -- " + ex.GetType().Name + ": " + ex.Message);
            }

            Log.Write("pin_pair_lengths: manager reports " + count + " pin pair(s)");
            if (count == 0)
            {
                // Not an error. A board with no xSignals or length rules
                // often has an empty pin pair set until something asks
                // Altium to build one.
                res.Notes.Add("board reports 0 pin pairs -- nothing to write");
            }

            for (int i = 0; i < count; i++)
            {
                string label = "#" + i;
                try
                {
                    IPCB_PinPair pp = mgr.GetState_PinPairs(i);
                    if (pp == null) continue;

                    try { label = pp.GetState_Name(); } catch { }

                    IPCB_Primitive a = null, b = null;
                    try { a = pp.GetPrimitives(0); } catch { }
                    try { b = pp.GetPrimitives(1); } catch { }

                    string fromPin = "", toPin = "", netName = "";
                    if (a != null)
                    {
                        try { fromPin = pp.GetPinPairItemDisplayName(a); } catch { }
                        try
                        {
                            IPCB_Net n = a.GetState_Net();
                            if (n != null) netName = n.GetState_Name();
                        }
                        catch { }
                    }
                    if (b != null)
                    {
                        try { toPin = pp.GetPinPairItemDisplayName(b); } catch { }
                    }

                    double routedMM = MM64(pp.GetState_RoutedLength());
                    double unroutedMM = MM64(pp.GetState_UnroutedLength());
                    double totalMM = MM64(pp.GetState_Length());
                    string delay = F4(pp.GetState_DelayTotal());

                    int nodes = 0;
                    try { nodes = pp.GetState_NodeCount(); } catch { }

                    lines.Add(string.Join(",",
                        Csv(netName),
                        Csv(netName.Length > 0 ? ClassesOf(classes, netName) : ""),
                        Csv(label),
                        Csv(fromPin),
                        Csv(toPin),
                        F3(routedMM),
                        F3(unroutedMM),
                        F3(totalMM),
                        delay,
                        nodes.ToString(Inv)));
                    res.PinPairRows++;
                }
                catch (Exception ex)
                {
                    Log.Write("pin_pair_lengths: skipped pair " + label + " -- " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            Save(folder, "pin_pair_lengths.csv", lines);
        }
    }
}
