// NetTools.cs
//
//   SinglePinNets   -- nets with one pin or none
//   LockRouting     -- lock or unlock all copper on chosen nets
//   DuplicateTracks -- track segments lying exactly on top of each other
//
// SINGLE-PIN NETS ARE ALMOST ALWAYS A MISTAKE. A net connected to exactly one
// pin is a wire that goes nowhere: a net label that never found its partner, a
// pin renamed on one side of a hierarchy, a power net whose only other
// connection was deleted. DRC does not flag it -- there is no violation, the
// net is simply lonely -- and it survives all the way to a board where that
// signal is not connected to anything. A net with ZERO pins is the residue of
// a deleted component and is pure noise in every net list downstream.
//
// LOCK ROUTING exists because "finished" routing is exactly what gets ruined
// by a stray drag. Altium locks components readily but locking every track and
// via on a critical net means selecting them all first. Moveable is the flag,
// and it is INVERTED on every primitive the same way it is on components:
// false means locked.
//
// DUPLICATE TRACKS are two segments with the same endpoints on the same layer
// and net, drawn on top of each other. They come from re-routing over an
// existing path and from paste-in-place. They look like one track, they behave
// like one track, and they emit two identical draws into Gerber -- which some
// fabricators' tooling flags and some silently doubles. Nothing here deletes
// unless asked.

using DXP;
using PCB;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AltiumSpike
{
    public static class NetTools
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
            public int Scanned, Found, Changed, Removed;
            public string CsvPath = "";
            public List<string> Names = new List<string>();
            public List<string> Notes = new List<string>();
            public List<string> Errors = new List<string>();
        }

        // ==================================================================
        // Single-pin nets
        // ==================================================================
        public static Result SinglePinNets(IPCB_ServerInterface pcbServer, IPCB_Board board,
                                           string folder, bool selectPads)
        {
            Result res = new Result();

            List<string> rows = new List<string>();
            rows.Add("Net,PinCount,ViaCount,Verdict");

            List<string> lonely = new List<string>();
            List<string> orphan = new List<string>();

            IPCB_BoardIterator it = board.BoardIterator_Create();
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
                        int pins = n.GetState_PinCount();
                        int vias = 0;
                        try { vias = n.GetState_ViaCount(); } catch { }
                        res.Scanned++;

                        if (pins <= 1 && name.Length > 0)
                        {
                            string verdict = pins == 0
                                ? "NO PINS - residue of a deleted component"
                                : "ONE PIN - connected to nothing";
                            rows.Add(string.Join(",", Csv(name), pins.ToString(Inv),
                                                 vias.ToString(Inv), Csv(verdict)));
                            if (pins == 0) orphan.Add(name); else lonely.Add(name);
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

            res.Names.AddRange(lonely);

            if (lonely.Count > 0)
                res.Notes.Add(lonely.Count + " net(s) reach exactly one pin: " + Join(lonely, 10));
            if (orphan.Count > 0)
                res.Notes.Add(orphan.Count + " net(s) reach no pin at all: " + Join(orphan, 10));
            if (res.Found == 0)
                res.Notes.Add("Every net reaches at least two pins.");

            // Selecting the pads makes the finding navigable instead of a list
            // of names you then have to hunt for.
            if (selectPads && lonely.Count > 0)
            {
                HashSet<string> want = new HashSet<string>(lonely, StringComparer.OrdinalIgnoreCase);
                List<IPCB_Primitive> pads = new List<IPCB_Primitive>();

                it = board.BoardIterator_Create();
                try
                {
                    it.AddFilter_ObjectSet(new TObjectSet(TObjectId.ePadObject));
                    it.AddFilter_AllLayers();
                    it.AddFilter_Method(TIterationMethod.eProcessAll);
                    IPCB_Primitive p = it.FirstPCBObject();
                    while (p != null)
                    {
                        try
                        {
                            IPCB_Net nn = p.GetState_Net();
                            if (nn != null && want.Contains(nn.GetState_Name() ?? "")) pads.Add(p);
                        }
                        catch { }
                        p = it.NextPCBObject();
                    }
                }
                catch { }
                finally { board.BoardIterator_Destroy(ref it); }

                Select(board, pads, res);
            }

            if (!string.IsNullOrEmpty(folder))
            {
                try
                {
                    string path = Path.Combine(folder, "single_pin_nets.csv");
                    File.WriteAllLines(path, rows, new UTF8Encoding(false));
                    res.CsvPath = path;
                    Log.Write("wrote " + path + " (" + res.Found + " net(s))");
                }
                catch (Exception ex)
                {
                    res.Errors.Add("Could not write the CSV -- " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            return res;
        }

        // ==================================================================
        // Lock / unlock routing
        // ==================================================================
        public sealed class LockOptions
        {
            public string NetFilter = "";     // blank = every net; else exact name, or a prefix with *
            public bool Lock = true;
            public bool IncludeVias = true;
            public bool OnlySelection = false;
        }

        public static Result LockRouting(IPCB_ServerInterface pcbServer, IPCB_Board board, LockOptions opt)
        {
            Result res = new Result();

            string filter = (opt.NetFilter ?? "").Trim();
            bool prefix = filter.EndsWith("*");
            string stem = prefix ? filter.Substring(0, filter.Length - 1) : filter;

            List<IPCB_Primitive> targets = new List<IPCB_Primitive>();

            List<TObjectId> kinds = new List<TObjectId>();
            kinds.Add(TObjectId.eTrackObject);
            kinds.Add(TObjectId.eArcObject);
            if (opt.IncludeVias) kinds.Add(TObjectId.eViaObject);

            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(kinds.ToArray()));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);

                IPCB_Primitive p = it.FirstPCBObject();
                while (p != null)
                {
                    try
                    {
                        res.Scanned++;
                        if (opt.OnlySelection && !p.GetState_Selected()) { p = it.NextPCBObject(); continue; }

                        string net = "";
                        IPCB_Net n = p.GetState_Net();
                        if (n != null) net = n.GetState_Name() ?? "";

                        bool match;
                        if (filter.Length == 0) match = true;
                        else if (prefix) match = net.StartsWith(stem, StringComparison.OrdinalIgnoreCase);
                        else match = string.Equals(net, filter, StringComparison.OrdinalIgnoreCase);

                        if (match && net.Length > 0) targets.Add(p);
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

            res.Found = targets.Count;
            if (targets.Count == 0)
            {
                res.Notes.Add(filter.Length == 0
                    ? "No netted copper found."
                    : "Nothing on a net matching \"" + filter + "\".");
                return res;
            }

            pcbServer.PreProcess();
            try
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    // Moveable is INVERTED: false means locked. Same
                    // convention as components, and just as easy to get
                    // backwards.
                    try { targets[i].SetState_Moveable(!opt.Lock); res.Changed++; }
                    catch (Exception ex)
                    {
                        res.Errors.Add("Item " + i + ": " + ex.GetType().Name + " -- " + ex.Message);
                    }
                }
            }
            finally { pcbServer.PostProcess(); }

            board.ViewManager_FullUpdate();
            res.Notes.Add((opt.Lock ? "Locked " : "Unlocked ") + res.Changed +
                          " primitive(s)" + (filter.Length > 0 ? " on " + filter : "") + ".");
            Log.Write("NetTools.LockRouting: " + res.Changed + " primitives, lock=" + opt.Lock);
            return res;
        }

        // ==================================================================
        // Duplicate tracks
        // ==================================================================
        public static Result DuplicateTracks(IPCB_ServerInterface pcbServer, IPCB_Board board,
                                             double toleranceMM, bool delete)
        {
            Result res = new Result();
            IPCB_LayerUtils lu = pcbServer.LayerUtils();

            // Key on layer + net + the two endpoints, with the endpoints put
            // in a canonical order so a track drawn backwards still matches
            // the one drawn forwards.
            Dictionary<string, IPCB_Primitive> seen =
                new Dictionary<string, IPCB_Primitive>(StringComparer.Ordinal);
            List<IPCB_Primitive> dupes = new List<IPCB_Primitive>();

            double q = toleranceMM > 1e-9 ? toleranceMM : 0.001;

            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(TObjectId.eTrackObject));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);

                IPCB_Track t = it.FirstPCBObject() as IPCB_Track;
                while (t != null)
                {
                    try
                    {
                        res.Scanned++;

                        double x1 = ToMM(t.GetState_X1()), y1 = ToMM(t.GetState_Y1());
                        double x2 = ToMM(t.GetState_X2()), y2 = ToMM(t.GetState_Y2());

                        // Canonical ordering: lower point first.
                        if (x2 < x1 || (Math.Abs(x2 - x1) < 1e-12 && y2 < y1))
                        {
                            double tx = x1, ty = y1;
                            x1 = x2; y1 = y2; x2 = tx; y2 = ty;
                        }

                        string layer = "";
                        try { layer = lu.AsString(t.GetState_V7Layer()); } catch { }
                        string net = "";
                        try { IPCB_Net n = t.GetState_Net(); if (n != null) net = n.GetState_Name() ?? ""; }
                        catch { }

                        string key = layer + "|" + net + "|" +
                                     Q(x1, q) + "," + Q(y1, q) + "|" +
                                     Q(x2, q) + "," + Q(y2, q) + "|" +
                                     Q(ToMM(t.GetState_Width()), q);

                        if (seen.ContainsKey(key)) dupes.Add(t);
                        else seen[key] = t;
                    }
                    catch { }
                    t = it.NextPCBObject() as IPCB_Track;
                }
            }
            catch (Exception ex)
            {
                res.Errors.Add("Track scan failed -- " + ex.GetType().Name + ": " + ex.Message);
            }
            finally { board.BoardIterator_Destroy(ref it); }

            res.Found = dupes.Count;
            if (dupes.Count == 0)
            {
                res.Notes.Add("No duplicated track segments among " + res.Scanned + " scanned.");
                return res;
            }

            if (delete)
            {
                // After the iterator is destroyed, never during it.
                pcbServer.PreProcess();
                try
                {
                    for (int i = 0; i < dupes.Count; i++)
                    {
                        try { board.RemovePCBObject(dupes[i]); res.Removed++; }
                        catch (Exception ex)
                        {
                            res.Errors.Add("Could not remove one track -- " + ex.GetType().Name);
                        }
                    }
                }
                finally { pcbServer.PostProcess(); }
                res.Notes.Add("Removed " + res.Removed + " duplicate(s); one copy of each was kept.");
            }
            else
            {
                Select(board, dupes, res);
                res.Notes.Add(res.Found + " duplicate(s) selected. One copy of each was left unselected, " +
                              "so deleting the selection keeps the routing intact.");
            }

            board.ViewManager_FullUpdate();
            Log.Write("NetTools.DuplicateTracks: " + res.Found + " found, " + res.Removed + " removed");
            return res;
        }

        private static string Q(double v, double q)
        {
            return Math.Round(v / q, MidpointRounding.AwayFromZero).ToString("0", Inv);
        }

        private static string Join(List<string> items, int max)
        {
            List<string> take = new List<string>();
            for (int i = 0; i < items.Count && i < max; i++) take.Add(items[i]);
            string s = string.Join(", ", take.ToArray());
            if (items.Count > max) s += " … and " + (items.Count - max) + " more";
            return s;
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
