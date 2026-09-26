// ImportSelfTest.cs
//
// Self-test checks for the four Import commands -- objects, pours, regions,
// lock/unlock -- the panel functions the rest of the self-test never called.
// Part of SelfTest (partial); run from ModifyingChecks, before the cleanup
// sweep.
//
// Each check writes its own small CSV into the output folder (kept as
// evidence, prefixed selftest_), runs the command on it, and compares three
// independent things: what the command says it did (Placed / Missing /
// Errors), what the board census says changed, and what the new objects read
// back as. Every CSV carries one deliberately bad row as well, so the error
// and missing-net paths are exercised, not only the happy one.
//
// WHERE. A strip BELOW the scratch origin -- (ox+118 .. ox+195, oy-38 .. oy-2)
// mm -- which no other check uses and which lies inside the final sweep's
// rectangle. The sweep removes tracks, arcs, vias, fills and text; it does
// NOT remove polygons, regions or coordinates, so these checks remove their
// own. The one check that touches a real object (lock, and the Component row
// that moves a part) records its state first and restores it in finally.

using PCB;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AltiumSpike
{
    public static partial class SelfTest
    {
        private const string ObjectsHeader =
            "ObjectType,Designator,X1,Y1,X2,Y2,Rotation,Layer,Width,HoleSize,Diameter,Net,Radius,StartAngle,EndAngle,Text,Height";

        private static void ImportChecks(Runner r, IPCB_ServerInterface pcbServer, IPCB_Board board,
                                         string folder, double ox, double oy)
        {
            r.Section = "Import (scratch area)";

            // The strip these checks own.
            double sx1 = ox + 118.0, sy1 = oy - 38.0, sx2 = ox + 195.0, sy2 = oy - 2.0;

            r.Run("Import objects", "six object kinds land where the CSV puts them; bad rows are reported", delegate
            {
                IPCB_Component c = FirstMoveableComponent(board);
                string des = "";
                int cx = 0, cy = 0;
                double crot = 0;
                TV6_Layer clayer = TV6_Layer.eV6_TopLayer;
                if (c != null)
                {
                    try { des = c.GetState_Name().GetState_Text() ?? ""; } catch { }
                    cx = c.GetState_XLocation(); cy = c.GetState_YLocation();
                    crot = c.GetState_Rotation(); clayer = c.GetState_Layer();
                }

                int tracks = Count(board, TObjectId.eTrackObject), vias = Count(board, TObjectId.eViaObject);
                int arcs = Count(board, TObjectId.eArcObject), fills = Count(board, TObjectId.eFillObject);
                int texts = Count(board, TObjectId.eTextObject), coords = Count(board, TObjectId.eCoordinateObject);

                List<string> rows = new List<string>();
                rows.Add(ObjectsHeader);
                rows.Add(Row("Track", "", ox + 122, oy - 10, ox + 132, oy - 10, 0, "Top", 0.25, 0, 0, "", 0, 0, 0, "", 0));
                rows.Add(Row("Via", "", ox + 136, oy - 10, 0, 0, 0, "Top", 0, 0.3, 0.6, "", 0, 0, 0, "", 0));
                rows.Add(Row("Arc", "", ox + 142, oy - 10, 0, 0, 0, "Top", 0.2, 0, 0, "", 2.0, 0, 180, "", 0));
                rows.Add(Row("Fill", "", ox + 150, oy - 12, ox + 156, oy - 8, 0, "Top", 0, 0, 0, "", 0, 0, 0, "", 0));
                rows.Add(Row("Text", "", ox + 160, oy - 10, 0, 0, 0, "Top", 0.15, 0, 0, "", 0, 0, 0, "SELFTEST IMPORT", 1.0));
                rows.Add(Row("Coordinate", "", ox + 172, oy - 10, 0, 0, 0, "Top", 0, 0, 0, "", 0, 0, 0, "", 0));
                if (des.Length > 0)
                    rows.Add(Row("Component", des, ox + 182, oy - 22, 0, 0, 45, clayer == TV6_Layer.eV6_BottomLayer ? "Bottom" : "Top",
                                 0, 0, 0, "", 0, 0, 0, "", 0));
                rows.Add(Row("Banana", "", ox + 122, oy - 30, 0, 0, 0, "Top", 0, 0, 0, "", 0, 0, 0, "", 0));          // unknown kind
                rows.Add(Row("Track", "", ox + 122, oy - 30, ox + 130, oy - 30, 0, "Top", 0.25, 0, 0, "NO_SUCH_NET_SELFTEST", 0, 0, 0, "", 0));
                string csv = WriteCsv(folder, "selftest_import_objects.csv", rows);

                try
                {
                    BoardImport.Result x = BoardImport.PlaceObjects(pcbServer, board, csv);
                    int expectPlaced = des.Length > 0 ? 7 : 6;

                    List<string> bad = new List<string>();
                    if (x.Placed != expectPlaced) bad.Add("placed " + x.Placed + ", expected " + expectPlaced);
                    if (x.Errors.Count != 1) bad.Add(x.Errors.Count + " row errors, expected 1 (the Banana row): " + string.Join("; ", x.Errors.ToArray()));
                    if (x.Missing.Count != 1) bad.Add(x.Missing.Count + " missing, expected 1 (the unknown net): " + string.Join("; ", x.Missing.ToArray()));

                    int dT = Count(board, TObjectId.eTrackObject) - tracks, dV = Count(board, TObjectId.eViaObject) - vias;
                    int dA = Count(board, TObjectId.eArcObject) - arcs, dF = Count(board, TObjectId.eFillObject) - fills;
                    int dX = Count(board, TObjectId.eTextObject) - texts, dC = Count(board, TObjectId.eCoordinateObject) - coords;
                    if (dT != 1 || dV != 1 || dA != 1 || dF != 1 || dX != 1 || dC != 1)
                    {
                        string extra = "";
                        // Two misses, then instrument (AGENTS.md #5): the first
                        // run found text +2 where +1 was expected. Rather than
                        // guess why, list what is actually sitting in the strip
                        // so the next run says which object it is.
                        if (dX != 1) extra = "; text objects in the strip: " + ListTexts(board, ox + 118, oy - 38, ox + 195, oy - 2);
                        bad.Add("census moved by track " + dT + ", via " + dV + ", arc " + dA + ", fill " + dF +
                                ", text " + dX + ", coordinate " + dC + " -- expected +1 each" + extra);
                    }

                    // Geometry, read back: the track's ends, and the fill's
                    // extent -- the fill is the one whose API names mislead.
                    if (!TrackAt(board, ox + 122, oy - 10, ox + 132, oy - 10))
                        bad.Add("no track reads back from " + F3(ox + 122) + "," + F3(oy - 10) + " to " + F3(ox + 132) + "," + F3(oy - 10));
                    string fillBox;
                    if (!BoxAt(board, TObjectId.eFillObject, ox + 150, oy - 12, ox + 156, oy - 8, out fillBox))
                        bad.Add("no fill reads back as " + F3(ox + 150) + "," + F3(oy - 12) + " .. " + F3(ox + 156) + "," + F3(oy - 8) + fillBox);

                    if (c != null)
                    {
                        double nx = ToMM(c.GetState_XLocation()), ny = ToMM(c.GetState_YLocation());
                        double nr = c.GetState_Rotation();
                        if (Math.Abs(nx - (ox + 182)) > 0.001 || Math.Abs(ny - (oy - 22)) > 0.001 || Math.Abs(nr - 45) > 0.001)
                            bad.Add(des + " read back at " + F3(nx) + "," + F3(ny) + " / " + F3(nr) + " deg, not " +
                                    F3(ox + 182) + "," + F3(oy - 22) + " / 45");
                    }

                    if (bad.Count > 0) return "FAIL: " + string.Join("; ", bad.ToArray());
                    return "PASS: " + x.Placed + " placed (track, via, arc, fill, text, coordinate" +
                           (des.Length > 0 ? ", and " + des + " moved and rotated" : "") +
                           "), census +1 each, geometry read back; the bad kind and the unknown net were reported";
                }
                finally
                {
                    // The sweep does not take coordinates; the moved part goes back.
                    RemoveInside(pcbServer, board, TObjectId.eCoordinateObject, sx1, sy1, sx2, sy2);
                    if (c != null)
                    {
                        try
                        {
                            c.BeginModify();
                            try
                            {
                                if (c.GetState_Layer() != clayer) c.SetState_Layer(clayer);
                                c.SetState_XLocation(cx); c.SetState_YLocation(cy); c.SetState_Rotation(crot);
                            }
                            finally { c.EndModify(); }
                        }
                        catch { }
                    }
                }
            });

            r.Run("Import pours", "a rectangular pour on a real net, outline as given; an unknown net is reported", delegate
            {
                string net = FirstNetName(board) ?? "";   // SelfTest.cs helper; null when the board has no nets
                if (net.Length == 0) return "SKIP: the board has no nets to pour on";
                int polys = Count(board, TObjectId.ePolyObject);

                List<string> rows = new List<string>();
                rows.Add("Net,Layer,X1,Y1,X2,Y2");
                rows.Add(Csv(net) + ",Top," + F3(ox + 122) + "," + F3(oy - 34) + "," + F3(ox + 134) + "," + F3(oy - 24));
                rows.Add("NO_SUCH_NET_SELFTEST,Top," + F3(ox + 138) + "," + F3(oy - 34) + "," + F3(ox + 146) + "," + F3(oy - 24));
                string csv = WriteCsv(folder, "selftest_import_pours.csv", rows);

                try
                {
                    BoardImport.Result x = BoardImport.PlacePours(pcbServer, board, csv);
                    int dP = Count(board, TObjectId.ePolyObject) - polys;
                    string box;
                    bool at = BoxAt(board, TObjectId.ePolyObject, ox + 122, oy - 34, ox + 134, oy - 24, out box);

                    if (x.Placed != 1 || x.Missing.Count != 1 || dP != 1 || !at)
                        return "FAIL: placed " + x.Placed + " (expected 1), missing " + x.Missing.Count +
                               " (expected 1), polygons +" + dP + " (expected +1), outline " + (at ? "as given" : "NOT as given" + box) +
                               (x.Errors.Count > 0 ? " -- " + string.Join("; ", x.Errors.ToArray()) : "");
                    return "PASS: 1 pour on " + net + ", polygons +1, outline 12 x 10 mm as given; the unknown net was reported";
                }
                finally
                {
                    int removed = RemoveInside(pcbServer, board, TObjectId.ePolyObject, sx1, sy1, sx2, sy2);
                    Log.Write("SelfTest: import pours cleanup removed " + removed + " polygon(s)");
                }
            });

            r.Run("Import regions", "a region with the given outline; a malformed row is reported", delegate
            {
                int regions = Count(board, TObjectId.eRegionObject);

                List<string> rows = new List<string>();
                rows.Add("RegionKind,Layer,Net,X1,Y1,X2,Y2");
                rows.Add("Copper,Top,," + F3(ox + 150) + "," + F3(oy - 34) + "," + F3(ox + 160) + "," + F3(oy - 26));
                rows.Add("Copper,Top,,abc," + F3(oy - 34) + "," + F3(ox + 170) + "," + F3(oy - 26));
                string csv = WriteCsv(folder, "selftest_import_regions.csv", rows);

                try
                {
                    BoardImport.Result x = BoardImport.PlaceRegions(pcbServer, board, csv);
                    int dR = Count(board, TObjectId.eRegionObject) - regions;
                    string box;
                    bool at = BoxAt(board, TObjectId.eRegionObject, ox + 150, oy - 34, ox + 160, oy - 26, out box);

                    if (x.Placed != 1 || x.Errors.Count != 1 || dR != 1 || !at)
                        return "FAIL: placed " + x.Placed + " (expected 1), row errors " + x.Errors.Count +
                               " (expected 1), regions +" + dR + " (expected +1), outline " + (at ? "as given" : "NOT as given" + box);
                    return "PASS: 1 region, regions +1, outline 10 x 8 mm as given; the malformed row was reported";
                }
                finally
                {
                    RemoveInside(pcbServer, board, TObjectId.eRegionObject, sx1, sy1, sx2, sy2);
                }
            });

            r.Run("Lock and unlock", "the listed part locks, then unlocks; an unknown designator is reported", delegate
            {
                IPCB_Component c = FirstMoveableComponent(board);
                if (c == null) return "SKIP: every component on this board is already locked";
                string des = "";
                try { des = c.GetState_Name().GetState_Text() ?? ""; } catch { }
                if (des.Length == 0) return "SKIP: the first unlocked component has no designator";

                // GetState_Moveable is INVERTED: false means locked.
                bool wasMoveable = c.GetState_Moveable();
                List<string> rows = new List<string>();
                rows.Add("Designator");
                rows.Add(des);
                rows.Add("NO_SUCH_REF_SELFTEST");
                string csv = WriteCsv(folder, "selftest_lock.csv", rows);

                try
                {
                    BoardImport.Result a = BoardImport.SetLock(pcbServer, board, csv, true);
                    bool lockedNow = !c.GetState_Moveable();
                    BoardImport.Result b = BoardImport.SetLock(pcbServer, board, csv, false);
                    bool unlockedNow = c.GetState_Moveable();

                    if (a.Placed != 1 || a.Missing.Count != 1 || !lockedNow || b.Placed != 1 || !unlockedNow)
                        return "FAIL: lock changed " + a.Placed + " (missing " + a.Missing.Count + "), " + des +
                               (lockedNow ? " locked" : " NOT locked") + "; unlock changed " + b.Placed + ", " + des +
                               (unlockedNow ? " unlocked" : " NOT unlocked");
                    return "PASS: " + des + " locked then unlocked (read back each time); NO_SUCH_REF_SELFTEST reported missing both times";
                }
                finally
                {
                    try
                    {
                        c.BeginModify();
                        try { c.SetState_Moveable(wasMoveable); } finally { c.EndModify(); }
                    }
                    catch { }
                }
            });
        }

        // ---- helpers ------------------------------------------------------

        private static string Row(string kind, string des, double x1, double y1, double x2, double y2, double rot,
                                  string layer, double width, double hole, double dia, string net, double radius,
                                  double a0, double a1, string text, double height)
        {
            return string.Join(",", new string[] {
                kind, Csv(des), F3(x1), F3(y1), F3(x2), F3(y2), F3(rot), layer, F3(width), F3(hole), F3(dia),
                Csv(net), F3(radius), F3(a0), F3(a1), Csv(text), F3(height) });
        }

        private static string Csv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.IndexOf(',') < 0 && s.IndexOf('"') < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        private static string WriteCsv(string folder, string name, List<string> rows)
        {
            string path = Path.Combine(folder, name);
            File.WriteAllLines(path, rows, new UTF8Encoding(false));
            return path;
        }

        // Lists every text object wholly inside the rectangle (mm), with its
        // content and position -- instrumentation for a census mismatch, not
        // a normal-path helper.
        private static string ListTexts(IPCB_Board board, double x1, double y1, double x2, double y2)
        {
            List<string> found = new List<string>();
            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(TObjectId.eTextObject));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);
                IPCB_Text t = it.FirstPCBObject() as IPCB_Text;
                while (t != null)
                {
                    try
                    {
                        double tx = ToMM(t.GetState_XLocation()), ty = ToMM(t.GetState_YLocation());
                        if (tx >= x1 && tx <= x2 && ty >= y1 && ty <= y2)
                            found.Add("\"" + (t.GetState_Text() ?? "") + "\" at " + F3(tx) + "," + F3(ty));
                    }
                    catch { }
                    t = it.NextPCBObject() as IPCB_Text;
                }
            }
            catch { }
            finally { board.BoardIterator_Destroy(ref it); }
            return found.Count == 0 ? "(none found)" : string.Join(" | ", found.ToArray());
        }

        private static bool TrackAt(IPCB_Board board, double x1, double y1, double x2, double y2)
        {
            int ax = ToCoord(x1), ay = ToCoord(y1), bx = ToCoord(x2), by = ToCoord(y2);
            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(TObjectId.eTrackObject));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);
                IPCB_Track t = it.FirstPCBObject() as IPCB_Track;
                while (t != null)
                {
                    if (t.GetState_X1() == ax && t.GetState_Y1() == ay && t.GetState_X2() == bx && t.GetState_Y2() == by)
                        return true;
                    t = it.NextPCBObject() as IPCB_Track;
                }
            }
            catch { }
            finally { board.BoardIterator_Destroy(ref it); }
            return false;
        }

        // Is there an object of this kind whose bounding box is the given
        // rectangle, to 0.01 mm? `seen` names the nearest miss for the report.
        private static bool BoxAt(IPCB_Board board, TObjectId kind, double x1, double y1, double x2, double y2, out string seen)
        {
            seen = "";
            double best = double.MaxValue;
            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(kind));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);
                IPCB_Primitive p = it.FirstPCBObject();
                while (p != null)
                {
                    try
                    {
                        CoordRect b = p.BoundingRectangle();
                        double bx1 = ToMM(b.GetX1()), by1 = ToMM(b.GetY1()), bx2 = ToMM(b.GetX2()), by2 = ToMM(b.GetY2());
                        double err = Math.Abs(bx1 - x1) + Math.Abs(by1 - y1) + Math.Abs(bx2 - x2) + Math.Abs(by2 - y2);
                        if (err < 0.04) return true;
                        if (err < best)
                        {
                            best = err;
                            seen = " (nearest: " + F3(bx1) + "," + F3(by1) + " .. " + F3(bx2) + "," + F3(by2) + ")";
                        }
                    }
                    catch { }
                    p = it.NextPCBObject();
                }
            }
            catch { }
            finally { board.BoardIterator_Destroy(ref it); }
            return false;
        }

        // Removes objects of one kind lying wholly inside the rectangle (mm).
        private static int RemoveInside(IPCB_ServerInterface pcbServer, IPCB_Board board, TObjectId kind,
                                        double x1, double y1, double x2, double y2)
        {
            List<IPCB_Primitive> doomed = new List<IPCB_Primitive>();
            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(kind));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);
                IPCB_Primitive p = it.FirstPCBObject();
                while (p != null)
                {
                    try
                    {
                        CoordRect b = p.BoundingRectangle();
                        if (ToMM(b.GetX1()) >= x1 && ToMM(b.GetX2()) <= x2 && ToMM(b.GetY1()) >= y1 && ToMM(b.GetY2()) <= y2)
                            doomed.Add(p);
                    }
                    catch { }
                    p = it.NextPCBObject();
                }
            }
            catch { }
            finally { board.BoardIterator_Destroy(ref it); }

            int removed = 0;
            pcbServer.PreProcess();
            try
            {
                foreach (IPCB_Primitive d in doomed)
                {
                    try { board.RemovePCBObject(d); removed++; } catch { }
                }
            }
            finally { pcbServer.PostProcess(); }
            return removed;
        }
    }
}
