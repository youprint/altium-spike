// Placement.cs
//
//   OffBoard       -- components whose body crosses or clears the board outline
//   Collisions     -- component bodies overlapping each other
//   AlignRotation  -- set every selected component to one rotation
//   SnapToGrid     -- pull component origins onto a placement grid
//   Renumber       -- designators renumbered by position, proposal first
//
// OFF BOARD is the failure that survives DRC. Altium's board-outline clearance
// rule catches copper, not a component body, so a connector hanging 0.4 mm over
// the edge or a part sitting entirely on the scrap area of the panel passes
// every check and is found by the assembler. The test here is the part's body
// rectangle -- BoundingRectangleNoNameComment, which excludes the designator,
// because a designator printed past the edge is cosmetic and a body past the
// edge is not.
//
// The outline is read as its ordered vertices and treated as a polygon. Arc
// segments in the outline are approximated by the straight line between their
// endpoints, so a board with a large radiused corner will read as very slightly
// smaller than it is. That errs toward reporting a part as outside when it is
// marginally inside, which is the safe direction for this check.
//
// COLLISIONS compares body rectangles pairwise. This is not a courtyard check
// -- Altium's component clearance rule does that properly, with real outlines
// -- it is the cheap version that finds the gross mistakes: two parts pasted on
// top of each other, a part dropped onto another during manual placement, a
// footprint whose body is far larger than anyone expected. Rectangles are
// axis-aligned, so a rotated part reports a larger body than it has and two
// diagonal neighbours can read as touching when they are not. Every hit is
// worth a look; not every hit is a fault.
//
// ALIGN ROTATION and SNAP TO GRID are the two placement chores that are pure
// arithmetic and miserable by hand. Rotation is set absolutely, not added, so
// running it twice is the same as running it once.
//
// RENUMBER writes a proposal CSV before it touches anything, and the apply step
// changes designators ON THE PCB ONLY. That desynchronises the board from the
// schematic until you push the change back with Update Schematics, and the CSV
// is what makes that reviewable. Nothing here edits a schematic.

using DXP;
using PCB;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AltiumSpike
{
    public static class Placement
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static double ToMM(int c) { return EDP.Utils.CoordToMMs(c); }
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
            public int Scanned, Found, Changed, Skipped;
            public string CsvPath = "";
            public List<string> Names = new List<string>();
            public List<string> Notes = new List<string>();
            public List<string> Errors = new List<string>();
        }

        private sealed class Part
        {
            public IPCB_Component Comp;
            public string Name = "";
            public double X1, Y1, X2, Y2;   // body rectangle, mm
            public double Cx, Cy;           // component origin, mm
            public bool Bottom;
        }

        // ==================================================================
        // Gather component bodies once; every check below reuses this.
        // ==================================================================
        private static List<Part> Parts(IPCB_ServerInterface pcbServer, IPCB_Board board,
                                        bool onlySelection, Result res)
        {
            List<Part> parts = new List<Part>();

            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(TObjectId.eComponentObject));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);

                IPCB_Component c = it.FirstPCBObject() as IPCB_Component;
                while (c != null)
                {
                    try
                    {
                        res.Scanned++;
                        if (!onlySelection || c.GetState_Selected())
                        {
                            Part p = new Part();
                            p.Comp = c;
                            try { p.Name = c.GetState_Name().GetState_Text() ?? ""; }
                            catch { p.Name = ""; }

                            CoordRect r = c.BoundingRectangleNoNameComment();
                            p.X1 = ToMM(r.Left); p.Y1 = ToMM(r.Bottom);
                            p.X2 = ToMM(r.Right); p.Y2 = ToMM(r.Top);
                            if (p.X2 < p.X1) { double t = p.X1; p.X1 = p.X2; p.X2 = t; }
                            if (p.Y2 < p.Y1) { double t = p.Y1; p.Y1 = p.Y2; p.Y2 = t; }

                            p.Cx = ToMM(c.GetState_XLocation());
                            p.Cy = ToMM(c.GetState_YLocation());

                            try { p.Bottom = c.GetState_Layer() == TV6_Layer.eV6_BottomLayer; }
                            catch { }

                            parts.Add(p);
                        }
                    }
                    catch { }
                    c = it.NextPCBObject() as IPCB_Component;
                }
            }
            catch (Exception ex)
            {
                res.Errors.Add("Component scan failed -- " + ex.GetType().Name + ": " + ex.Message);
            }
            finally { board.BoardIterator_Destroy(ref it); }

            return parts;
        }

        // ==================================================================
        // Off-board components
        // ==================================================================
        public static Result OffBoard(IPCB_ServerInterface pcbServer, IPCB_Board board,
                                      double marginMM, string folder, bool select)
        {
            Result res = new Result();

            List<double> vx = new List<double>();
            List<double> vy = new List<double>();
            try
            {
                IPCB_BoardOutline outline = board.GetState_BoardOutline();
                int n = outline.GetState_PointCount();
                for (int i = 0; i < n; i++)
                {
                    PolySegment seg = outline.GetState_Segments(i);
                    vx.Add(ToMM(seg.GetVx()));
                    vy.Add(ToMM(seg.GetVy()));
                }
            }
            catch (Exception ex)
            {
                res.Errors.Add("Could not read the board outline -- " + ex.GetType().Name + ": " + ex.Message);
                return res;
            }

            if (vx.Count < 3)
            {
                res.Errors.Add("The board outline has " + vx.Count + " vertices, which is not a shape. " +
                               "Define a board shape before running this.");
                return res;
            }

            List<Part> parts = Parts(pcbServer, board, false, res);

            List<string> rows = new List<string>();
            rows.Add("Designator,Side,Corners outside,Worst overhang mm,Verdict");

            List<IPCB_Primitive> offenders = new List<IPCB_Primitive>();
            int fullyOut = 0, straddling = 0;

            for (int i = 0; i < parts.Count; i++)
            {
                Part p = parts[i];

                double[] cx = new double[] { p.X1, p.X2, p.X2, p.X1 };
                double[] cy = new double[] { p.Y1, p.Y1, p.Y2, p.Y2 };

                int outside = 0;
                double worst = 0;
                for (int k = 0; k < 4; k++)
                {
                    bool inside = PolyGeometry.PointInPolygon(vx, vy, cx[k], cy[k]);
                    double d = PolyGeometry.DistanceToEdge(vx, vy, cx[k], cy[k]);
                    if (!inside)
                    {
                        // A corner within the margin of the edge is treated as
                        // on the board -- outline vertices and a footprint body
                        // that meet exactly should not read as a fault.
                        if (d > marginMM) { outside++; if (d > worst) worst = d; }
                    }
                }

                if (outside == 0) continue;

                string verdict = outside == 4
                    ? "ENTIRELY OFF THE BOARD"
                    : "OVERHANGS THE EDGE";
                if (outside == 4) fullyOut++; else straddling++;

                rows.Add(string.Join(",", Csv(p.Name), p.Bottom ? "Bottom" : "Top",
                                     outside.ToString(Inv), F3(worst), Csv(verdict)));
                res.Names.Add(p.Name);
                offenders.Add(p.Comp);
                res.Found++;
            }

            if (res.Found == 0)
                res.Notes.Add("Every component body sits inside the board outline.");
            else
            {
                if (fullyOut > 0)
                    res.Notes.Add(fullyOut + " component(s) are entirely off the board.");
                if (straddling > 0)
                    res.Notes.Add(straddling + " component(s) overhang the edge.");
                res.Notes.Add(Join(res.Names, 10));
                res.Notes.Add("Outline arcs are approximated by their chords, so a radiused corner reads " +
                              "very slightly tight.");
            }

            if (select && offenders.Count > 0) Select(board, offenders, res);
            WriteCsv(folder, "off_board_components.csv", rows, res);
            return res;
        }

        // ==================================================================
        // Component collisions
        // ==================================================================
        public static Result Collisions(IPCB_ServerInterface pcbServer, IPCB_Board board,
                                        double clearanceMM, string folder, bool select)
        {
            Result res = new Result();

            List<Part> parts = Parts(pcbServer, board, false, res);

            // Sorted by left edge so the inner loop can stop as soon as the
            // next part starts beyond the current one's right edge.
            parts.Sort(delegate (Part a, Part b) { return a.X1.CompareTo(b.X1); });

            List<string> rows = new List<string>();
            rows.Add("Designator A,Designator B,Side,Overlap X mm,Overlap Y mm,Verdict");

            Dictionary<string, bool> hit = new Dictionary<string, bool>(StringComparer.Ordinal);
            List<IPCB_Primitive> offenders = new List<IPCB_Primitive>();

            double c = clearanceMM;

            for (int i = 0; i < parts.Count; i++)
            {
                Part a = parts[i];
                for (int j = i + 1; j < parts.Count; j++)
                {
                    Part b = parts[j];
                    if (b.X1 - c > a.X2) break;              // sorted: nothing further can touch a
                    if (a.Bottom != b.Bottom) continue;      // opposite sides never collide

                    double ox, oy;
                    if (!PolyGeometry.RectOverlap(a.X1, a.Y1, a.X2, a.Y2,
                                                  b.X1, b.Y1, b.X2, b.Y2, c, out ox, out oy))
                        continue;

                    bool real = ox > 0 && oy > 0;

                    rows.Add(string.Join(",", Csv(a.Name), Csv(b.Name),
                                         a.Bottom ? "Bottom" : "Top",
                                         F3(ox), F3(oy),
                                         real ? "BODIES OVERLAP" : "CLOSER THAN " + F3(c) + " mm"));
                    res.Found++;

                    if (!hit.ContainsKey(a.Name)) { hit[a.Name] = true; offenders.Add(a.Comp); }
                    if (!hit.ContainsKey(b.Name)) { hit[b.Name] = true; offenders.Add(b.Comp); }
                }
            }

            if (res.Found == 0)
                res.Notes.Add("No component bodies overlap" +
                              (c > 0 ? " or sit closer than " + F3(c) + " mm." : "."));
            else
            {
                res.Notes.Add(res.Found + " pair(s) across " + offenders.Count + " component(s).");
                res.Notes.Add("Bodies are axis-aligned rectangles, so a rotated part reads larger than " +
                              "it is. Check each pair before moving anything.");
            }

            if (select && offenders.Count > 0) Select(board, offenders, res);
            WriteCsv(folder, "component_collisions.csv", rows, res);
            return res;
        }

        // ==================================================================
        // Align rotation
        // ==================================================================
        public static Result AlignRotation(IPCB_ServerInterface pcbServer, IPCB_Board board,
                                           double degrees)
        {
            Result res = new Result();

            List<Part> parts = Parts(pcbServer, board, true, res);
            if (parts.Count == 0)
            { res.Errors.Add("Select the components to rotate first."); return res; }

            double target = degrees % 360.0;
            if (target < 0) target += 360.0;

            res.Found = parts.Count;

            pcbServer.PreProcess();
            try
            {
                for (int i = 0; i < parts.Count; i++)
                {
                    IPCB_Component comp = parts[i].Comp;
                    bool opened = false;
                    try
                    {
                        double now = comp.GetState_Rotation();
                        if (Math.Abs(now - target) < 1e-6) { res.Skipped++; continue; }

                        comp.BeginModify();
                        opened = true;
                        // Absolute, not relative -- running this twice must be
                        // the same as running it once.
                        comp.SetState_Rotation(target);
                        res.Changed++;
                    }
                    catch (Exception ex)
                    {
                        res.Errors.Add(parts[i].Name + ": " + ex.GetType().Name + " -- " + ex.Message);
                        res.Skipped++;
                    }
                    finally { if (opened) { try { comp.EndModify(); } catch { } } }
                }
            }
            finally { pcbServer.PostProcess(); }

            board.ViewManager_FullUpdate();
            res.Notes.Add(res.Changed + " component(s) set to " + F3(target) + "°" +
                          (res.Skipped > 0 ? ", " + res.Skipped + " already there" : "") + ".");
            Log.Write("Placement.AlignRotation: " + res.Changed + " to " + target);
            return res;
        }

        // ==================================================================
        // Snap to grid
        // ==================================================================
        public static Result SnapToGrid(IPCB_ServerInterface pcbServer, IPCB_Board board,
                                        double gridMM, bool onlySelection, bool skipLocked)
        {
            Result res = new Result();

            if (gridMM <= 1e-9)
            { res.Errors.Add("Grid must be greater than 0."); return res; }

            List<Part> parts = Parts(pcbServer, board, onlySelection, res);
            if (parts.Count == 0)
            {
                res.Errors.Add(onlySelection ? "Select the components to snap first."
                                             : "No components on the board.");
                return res;
            }

            res.Found = parts.Count;

            pcbServer.PreProcess();
            try
            {
                for (int i = 0; i < parts.Count; i++)
                {
                    IPCB_Component comp = parts[i].Comp;
                    bool opened = false;
                    try
                    {
                        // Moveable is INVERTED: false means locked.
                        if (skipLocked && !comp.GetState_Moveable()) { res.Skipped++; continue; }

                        double nx = Math.Round(parts[i].Cx / gridMM, MidpointRounding.AwayFromZero) * gridMM;
                        double ny = Math.Round(parts[i].Cy / gridMM, MidpointRounding.AwayFromZero) * gridMM;

                        int cx = ToCoord(nx), cy = ToCoord(ny);
                        if (cx == comp.GetState_XLocation() && cy == comp.GetState_YLocation())
                        { res.Skipped++; continue; }

                        comp.BeginModify();
                        opened = true;
                        comp.SetState_XLocation(cx);
                        comp.SetState_YLocation(cy);
                        res.Changed++;
                    }
                    catch (Exception ex)
                    {
                        res.Errors.Add(parts[i].Name + ": " + ex.GetType().Name + " -- " + ex.Message);
                    }
                    finally { if (opened) { try { comp.EndModify(); } catch { } } }
                }
            }
            finally { pcbServer.PostProcess(); }

            board.ViewManager_FullUpdate();
            res.Notes.Add(res.Changed + " component(s) moved onto a " + F3(gridMM) + " mm grid" +
                          (res.Skipped > 0 ? ", " + res.Skipped + " already on it or locked" : "") + ".");
            res.Notes.Add("This moves the component origin, which on many footprints is not the middle " +
                          "of the body.");
            Log.Write("Placement.SnapToGrid: " + res.Changed + " moved, grid " + gridMM);
            return res;
        }

        // ==================================================================
        // Renumber designators by position
        // ==================================================================
        public sealed class RenumberOptions
        {
            public bool TopToBottom = true;    // rows first, top row leftmost = 1
            public double RowHeightMM = 5.0;   // parts within this band count as the same row
            public bool OnlySelection = false;
            public bool Apply = false;         // false writes the proposal only
        }

        public static Result Renumber(IPCB_ServerInterface pcbServer, IPCB_Board board,
                                      RenumberOptions opt, string folder)
        {
            Result res = new Result();

            if (opt.RowHeightMM <= 0)
            { res.Errors.Add("Row height must be greater than 0."); return res; }

            List<Part> parts = Parts(pcbServer, board, opt.OnlySelection, res);
            if (parts.Count == 0)
            {
                res.Errors.Add(opt.OnlySelection ? "Select the components to renumber first."
                                                 : "No components on the board.");
                return res;
            }

            // Order: rows from the top, left to right inside a row. Rows are
            // found from the spacing that is there, not from fixed band
            // boundaries -- see PolyGeometry.RowIndices for why.
            double[] ys = new double[parts.Count];
            for (int i = 0; i < parts.Count; i++) ys[i] = parts[i].Cy;
            int[] rowOf = PolyGeometry.RowIndices(ys, opt.RowHeightMM, opt.TopToBottom);

            Dictionary<Part, int> rowIndex = new Dictionary<Part, int>();
            for (int i = 0; i < parts.Count; i++) rowIndex[parts[i]] = rowOf[i];

            parts.Sort(delegate (Part a, Part b)
            {
                int ra = rowIndex[a], rb = rowIndex[b];
                if (ra != rb) return ra.CompareTo(rb);
                return a.Cx.CompareTo(b.Cx);
            });

            // Numbering runs per prefix: R1..Rn, C1..Cn, U1..Un.
            Dictionary<string, int> next = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            List<string> rows = new List<string>();
            rows.Add("Old,New,X mm,Y mm,Side,Changed");

            List<string> oldNames = new List<string>();
            List<string> newNames = new List<string>();

            for (int i = 0; i < parts.Count; i++)
            {
                string old = parts[i].Name;
                string prefix = PolyGeometry.DesignatorPrefix(old);
                if (prefix.Length == 0) { res.Skipped++; continue; }

                int n;
                if (!next.TryGetValue(prefix, out n)) n = 1;
                next[prefix] = n + 1;

                string neu = prefix + n.ToString(Inv);
                bool changed = !string.Equals(old, neu, StringComparison.Ordinal);

                rows.Add(string.Join(",", Csv(old), Csv(neu), F3(parts[i].Cx), F3(parts[i].Cy),
                                     parts[i].Bottom ? "Bottom" : "Top", changed ? "yes" : "no"));

                oldNames.Add(old);
                newNames.Add(neu);
                if (changed) res.Found++;
            }

            WriteCsv(folder, "renumber_proposal.csv", rows, res);

            if (!opt.Apply)
            {
                res.Notes.Add(res.Found + " of " + oldNames.Count + " designators would change.");
                res.Notes.Add("Nothing has been modified. Read the proposal, then apply it.");
                if (res.Skipped > 0)
                    res.Notes.Add(res.Skipped + " skipped for having no letter prefix.");
                return res;
            }

            if (res.Found == 0)
            {
                res.Notes.Add("Every designator already matches its position. Nothing to do.");
                return res;
            }

            // Two passes through a scratch name. Renaming straight to the
            // target collides the moment a name in use is also a target --
            // R3 becoming R1 while R1 still exists.
            string tag = "~SPK" + DateTime.Now.ToString("HHmmss", Inv) + "~";

            pcbServer.PreProcess();
            try
            {
                for (int pass = 0; pass < 2; pass++)
                {
                    int k = 0;
                    for (int i = 0; i < parts.Count; i++)
                    {
                        if (PolyGeometry.DesignatorPrefix(parts[i].Name).Length == 0) continue;
                        string want = pass == 0 ? tag + k.ToString(Inv) : newNames[k];
                        k++;

                        IPCB_Component comp = parts[i].Comp;
                        bool opened = false;
                        try
                        {
                            comp.BeginModify();
                            opened = true;
                            comp.GetState_Name().SetState_Text(want);
                            if (pass == 1) res.Changed++;
                        }
                        catch (Exception ex)
                        {
                            res.Errors.Add(oldNames.Count > k - 1 ? oldNames[k - 1] : "component " + i +
                                           ": " + ex.GetType().Name + " -- " + ex.Message);
                        }
                        finally { if (opened) { try { comp.EndModify(); } catch { } } }
                    }
                }
            }
            finally { pcbServer.PostProcess(); }

            board.ViewManager_FullUpdate();
            res.Notes.Add(res.Changed + " designator(s) renumbered on the PCB.");
            res.Notes.Add("THE SCHEMATIC IS NOW OUT OF STEP. Push these back with Design > " +
                          "Update Schematics, using the proposal CSV as the record of what changed.");
            Log.Write("Placement.Renumber: applied to " + res.Changed + " components");
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
