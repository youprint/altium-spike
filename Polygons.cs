// Polygons.cs
//
//   Report   -- every polygon with its settings and pour state
//   Repour   -- rebuild polygons that are out of date
//
// A POLYGON THAT HAS NOT BEEN REPOURED SINCE THE LAST EDIT IS THE MOST
// EXPENSIVE THING ON A PCB. The screen shows the copper it had when it was
// last poured, DRC checks that same stale copper, and Gerber gets what is
// there -- so a ground pour with a hole under a part you moved an hour ago
// looks fine everywhere and arrives wrong. Altium marks these internally
// (CopperPourInvalid) and does not put it in front of you.
//
// The report is the settings sheet nobody keeps: pour-over behaviour, remove
// dead copper, island and neck thresholds, clearance grid, and -- the two that
// matter most -- whether the polygon is poured at all and whether its copper
// is stale. IgnoreViolations is flagged loudly, because a polygon set to
// ignore violations is a polygon whose DRC results mean nothing.
//
// PourOver is an enum in the SDK and reads back as an integer through
// Internal_GetState_PourOver. The three documented values are mapped by name
// and anything else is printed as its number rather than guessed at.
//
// Repour invalidates and rebuilds. It does NOT reorder the pour index, so
// overlapping polygons keep their existing precedence -- changing that
// silently would move copper on a board someone had already signed off.

using DXP;
using PCB;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AltiumSpike
{
    public static class Polygons
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
            public int Scanned, Found, Changed, Stale;
            public string CsvPath = "";
            public List<string> Names = new List<string>();
            public List<string> Notes = new List<string>();
            public List<string> Errors = new List<string>();
        }

        // One coord is CoordToMMs(1) mm, so an area in square coords converts
        // by that ratio squared. Computed from a large value to keep the
        // integer conversion's own rounding out of the ratio.
        private static double SqCoordToMM2(double sq)
        {
            double mmPerCoord = EDP.Utils.CoordToMMs(1000000) / 1000000.0;
            return sq * mmPerCoord * mmPerCoord;
        }

        private static string PourOver(int v)
        {
            if (v == 0) return "Don't pour over";
            if (v == 1) return "Pour over all same-net objects";
            if (v == 2) return "Pour over same-net polygons only";
            return "value " + v.ToString(Inv);
        }

        // ==================================================================
        // Report
        // ==================================================================
        public static Result Report(IPCB_ServerInterface pcbServer, IPCB_Board board, string folder)
        {
            Result res = new Result();
            IPCB_LayerUtils lu = pcbServer.LayerUtils();

            List<string> rows = new List<string>();
            rows.Add("Name,Net,Layer,Poured,Copper stale,Pour over,Remove dead,Remove islands," +
                     "Island threshold mm2,Remove necks,Neck width mm,Track mm,Clearance grid mm," +
                     "Ignore violations,Pour index,Area mm2");

            List<string> stale = new List<string>();
            List<string> unpoured = new List<string>();
            List<string> ignoring = new List<string>();

            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(TObjectId.ePolyObject));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);

                IPCB_Polygon p = it.FirstPCBObject() as IPCB_Polygon;
                while (p != null)
                {
                    try
                    {
                        res.Scanned++;

                        string name = "";
                        try { name = p.GetState_Name() ?? ""; } catch { }
                        if (name.Length == 0) { try { name = p.GetDefaultName() ?? ""; } catch { } }

                        string net = "";
                        try { IPCB_Net n = p.GetState_Net(); if (n != null) net = n.GetState_Name() ?? ""; }
                        catch { }

                        string layer = "";
                        try { layer = lu.AsString(p.GetState_V7Layer()); } catch { }

                        bool poured = false, invalid = false, ignore = false;
                        try { poured = p.GetState_Poured(); } catch { }
                        try { invalid = p.GetState_CopperPourInvalid(); } catch { }
                        try { ignore = p.GetState_IgnoreViolations(); } catch { }

                        string over = "";
                        try { over = PourOver(p.Internal_GetState_PourOver()); } catch { }

                        bool dead = false, islands = false, necks = false;
                        double islandThresh = 0;
                        int neckW = 0, track = 0, grid = 0, index = 0;
                        double area = 0;
                        try { dead = p.GetState_RemoveDead(); } catch { }
                        try { islands = p.GetState_RemoveIslandsByArea(); } catch { }
                        // Square COORDS, not mm2. Read raw it prints as
                        // 250000000000 for a 1.6 mm2 threshold, which reads
                        // like a corrupt value rather than a setting.
                        try { islandThresh = SqCoordToMM2(p.GetState_IslandAreaThreshold()); } catch { }
                        try { necks = p.GetState_RemoveNarrowNecks(); } catch { }
                        try { neckW = p.GetState_NeckWidthThreshold(); } catch { }
                        try { track = p.GetState_TrackSize(); } catch { }
                        try { grid = p.GetState_Grid(); } catch { }
                        try { index = p.GetState_PourIndex(); } catch { }
                        try { area = p.GetState_AreaSize(); } catch { }

                        rows.Add(string.Join(",",
                            Csv(name), Csv(net), Csv(layer),
                            poured ? "yes" : "NO",
                            invalid ? "YES - REPOUR" : "no",
                            Csv(over),
                            dead ? "yes" : "no",
                            islands ? "yes" : "no",
                            F3(islandThresh),
                            necks ? "yes" : "no",
                            F3(ToMM(neckW)),
                            F3(ToMM(track)),
                            F3(ToMM(grid)),
                            ignore ? "YES" : "no",
                            index.ToString(Inv),
                            F3(area)));

                        if (invalid) { stale.Add(name); res.Stale++; }
                        if (!poured) unpoured.Add(name);
                        if (ignore) ignoring.Add(name);
                        res.Found++;
                    }
                    catch { }
                    p = it.NextPCBObject() as IPCB_Polygon;
                }
            }
            catch (Exception ex)
            {
                res.Errors.Add("Polygon scan failed -- " + ex.GetType().Name + ": " + ex.Message);
            }
            finally { board.BoardIterator_Destroy(ref it); }

            if (res.Found == 0)
                res.Notes.Add("No polygons on this board.");
            else
            {
                res.Notes.Add(res.Found + " polygon(s).");
                if (stale.Count > 0)
                    res.Notes.Add(stale.Count + " have stale copper and must be repoured: " + Join(stale, 8));
                if (unpoured.Count > 0)
                    res.Notes.Add(unpoured.Count + " are not poured at all: " + Join(unpoured, 8));
                if (ignoring.Count > 0)
                    res.Notes.Add(ignoring.Count + " ignore violations, so DRC says nothing about them: " +
                                  Join(ignoring, 8));
                if (stale.Count == 0 && unpoured.Count == 0 && ignoring.Count == 0)
                    res.Notes.Add("All poured, all current, none ignoring violations.");
            }

            WriteCsv(folder, "polygons.csv", rows, res);
            return res;
        }

        // ==================================================================
        // Repour
        // ==================================================================
        public static Result Repour(IPCB_ServerInterface pcbServer, IPCB_Board board,
                                    bool onlyStale, bool onlySelection)
        {
            Result res = new Result();

            List<IPCB_Polygon> targets = new List<IPCB_Polygon>();

            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(TObjectId.ePolyObject));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);

                IPCB_Polygon p = it.FirstPCBObject() as IPCB_Polygon;
                while (p != null)
                {
                    try
                    {
                        res.Scanned++;
                        bool take = true;
                        if (onlySelection && !p.GetState_Selected()) take = false;
                        if (take && onlyStale)
                        {
                            bool invalid = false;
                            try { invalid = p.GetState_CopperPourInvalid(); } catch { }
                            if (invalid) res.Stale++;
                            take = invalid;
                        }
                        if (take) targets.Add(p);
                    }
                    catch { }
                    p = it.NextPCBObject() as IPCB_Polygon;
                }
            }
            catch (Exception ex)
            {
                res.Errors.Add("Polygon scan failed -- " + ex.GetType().Name + ": " + ex.Message);
            }
            finally { board.BoardIterator_Destroy(ref it); }

            res.Found = targets.Count;
            if (targets.Count == 0)
            {
                res.Notes.Add(onlyStale
                    ? "Every polygon's copper is already current."
                    : (onlySelection ? "No polygons selected." : "No polygons on this board."));
                return res;
            }

            pcbServer.PreProcess();
            try
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    IPCB_Polygon p = targets[i];
                    string name = "";
                    try { name = p.GetState_Name() ?? ""; } catch { }

                    bool opened = false;
                    try
                    {
                        p.BeginModify();
                        opened = true;
                        p.SetState_CopperPourInvalid();
                        p.Rebuild();
                        p.SetState_CopperPourValid();
                        res.Changed++;
                        res.Names.Add(name);
                    }
                    catch (Exception ex)
                    {
                        res.Errors.Add((name.Length > 0 ? name : "polygon " + i) + ": " +
                                       ex.GetType().Name + " -- " + ex.Message);
                    }
                    finally { if (opened) { try { p.EndModify(); } catch { } } }
                }
            }
            finally { pcbServer.PostProcess(); }

            board.ViewManager_FullUpdate();
            res.Notes.Add(res.Changed + " polygon(s) repoured.");
            res.Notes.Add("Pour order was left alone, so overlapping polygons keep the precedence they had.");
            Log.Write("Polygons.Repour: " + res.Changed + " of " + targets.Count);
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

        private static string Join(List<string> items, int max)
        {
            List<string> take = new List<string>();
            for (int i = 0; i < items.Count && i < max; i++) take.Add(items[i]);
            string s = string.Join(", ", take.ToArray());
            if (items.Count > max) s += " … and " + (items.Count - max) + " more";
            return s;
        }
    }
}
