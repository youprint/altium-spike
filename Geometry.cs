// Geometry.cs
//
//   Fillet      -- round the corners between selected tracks
//   Distribute  -- space selected objects evenly along X or Y
//   ScaleAbout  -- scale selected objects about the centre of their extent
//
// The maths lives in FilletGeometry.cs, which has no Altium dependency and is
// unit-tested. This file finds the corners, edits the tracks and drops in the
// arcs.
//
// FILLET WORKS ON THE SELECTION, PAIRWISE. Every pair of selected tracks that
// share an endpoint is a corner. A track selected on its own is skipped, and
// so is a corner whose segments are collinear or where the radius will not
// fit. Each corner is either filleted completely or left exactly as it was;
// there is no half-applied state, because a track shortened without its arc
// is a broken connection.
//
// SCALE DOES NOT TOUCH WIDTHS. Scaling a selection about a point moves
// geometry; it deliberately leaves track widths, hole sizes and pad sizes
// alone, because scaling those turns a manufacturable board into one that
// misses its design rules everywhere at once. If you want a scaled copy of a
// footprint, scale the positions and set the widths on purpose.

using DXP;
using PCB;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace AltiumSpike
{
    public static class Geometry
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static double ToMM(int coord) { return EDP.Utils.CoordToMMs(coord); }
        private static int ToCoord(double mm) { return EDP.Utils.MMsToCoord(mm); }

        public sealed class Result
        {
            public int Considered, Applied, Skipped;
            public double LargestFittingRadius;
            public List<string> Reasons = new List<string>();
            public List<string> Errors = new List<string>();
        }

        private static List<IPCB_Primitive> Selection(IPCB_Board board, TObjectId[] kinds, List<string> errors)
        {
            List<IPCB_Primitive> picked = new List<IPCB_Primitive>();
            int count;
            try { count = board.GetState_SelectecObjectCount(); }
            catch (Exception ex)
            {
                errors.Add("Could not read the selection -- " + ex.GetType().Name + ": " + ex.Message);
                return picked;
            }

            for (int i = 0; i < count; i++)
            {
                try
                {
                    IPCB_Primitive p = board.GetState_SelectecObject(i);
                    if (p == null) continue;
                    TObjectId id = p.GetState_ObjectID();
                    for (int k = 0; k < kinds.Length; k++)
                        if (id == kinds[k]) { picked.Add(p); break; }
                }
                catch { }
            }
            return picked;
        }

        // ==================================================================
        // Fillet
        // ==================================================================
        public static Result FilletCorners(IPCB_ServerInterface pcbServer, IPCB_Board board,
                                           double radiusMM, bool clampRadius)
        {
            Result res = new Result();

            if (radiusMM <= 0.0) { res.Errors.Add("Radius must be greater than 0."); return res; }

            List<IPCB_Primitive> sel = Selection(board, new TObjectId[] { TObjectId.eTrackObject }, res.Errors);
            List<IPCB_Track> tracks = new List<IPCB_Track>();
            for (int i = 0; i < sel.Count; i++)
            {
                IPCB_Track t = sel[i] as IPCB_Track;
                if (t != null) tracks.Add(t);
            }

            if (tracks.Count < 2)
            {
                res.Errors.Add("Select at least two connected tracks. " +
                               (tracks.Count == 0 ? "Nothing suitable is selected." : "Only one track is selected."));
                return res;
            }

            const double coincidence = 0.001;   // mm

            // Each track end can only be filleted once; a corner where three
            // tracks meet is not a fillet, it is a junction.
            HashSet<string> usedEnds = new HashSet<string>(StringComparer.Ordinal);

            pcbServer.PreProcess();
            try
            {
                for (int i = 0; i < tracks.Count; i++)
                {
                    for (int j = i + 1; j < tracks.Count; j++)
                    {
                        try
                        {
                            IPCB_Track a = tracks[i], b = tracks[j];

                            Vec a1 = new Vec(ToMM(a.GetState_X1()), ToMM(a.GetState_Y1()));
                            Vec a2 = new Vec(ToMM(a.GetState_X2()), ToMM(a.GetState_Y2()));
                            Vec b1 = new Vec(ToMM(b.GetState_X1()), ToMM(b.GetState_Y1()));
                            Vec b2 = new Vec(ToMM(b.GetState_X2()), ToMM(b.GetState_Y2()));

                            // Which ends meet?
                            Vec corner, farA, farB;
                            bool aUsesEnd1, bUsesEnd1;

                            if (Vec.Dist(a1, b1) <= coincidence)
                            { corner = a1; farA = a2; farB = b2; aUsesEnd1 = true; bUsesEnd1 = true; }
                            else if (Vec.Dist(a1, b2) <= coincidence)
                            { corner = a1; farA = a2; farB = b1; aUsesEnd1 = true; bUsesEnd1 = false; }
                            else if (Vec.Dist(a2, b1) <= coincidence)
                            { corner = a2; farA = a1; farB = b2; aUsesEnd1 = false; bUsesEnd1 = true; }
                            else if (Vec.Dist(a2, b2) <= coincidence)
                            { corner = a2; farA = a1; farB = b1; aUsesEnd1 = false; bUsesEnd1 = false; }
                            else continue;

                            string ka = i + (aUsesEnd1 ? "a" : "b");
                            string kb = j + (bUsesEnd1 ? "a" : "b");
                            if (usedEnds.Contains(ka) || usedEnds.Contains(kb)) continue;

                            res.Considered++;

                            double r = radiusMM;
                            Fillet f = FilletGeometry.Corner(corner, farA, farB, r);

                            if (!f.Ok && clampRadius && f.MaxRadius > 1e-6)
                            {
                                // Back off to the largest radius this corner
                                // will take, rather than skipping it.
                                r = f.MaxRadius * 0.98;
                                f = FilletGeometry.Corner(corner, farA, farB, r);
                            }

                            if (!f.Ok)
                            {
                                res.Skipped++;
                                if (res.Reasons.Count < 8) res.Reasons.Add(f.Why);
                                if (f.MaxRadius > res.LargestFittingRadius) res.LargestFittingRadius = f.MaxRadius;
                                continue;
                            }

                            // Shorten both tracks to their tangent points.
                            if (aUsesEnd1)
                            { a.SetState_X1(ToCoord(f.Tangent1.X)); a.SetState_Y1(ToCoord(f.Tangent1.Y)); }
                            else
                            { a.SetState_X2(ToCoord(f.Tangent1.X)); a.SetState_Y2(ToCoord(f.Tangent1.Y)); }

                            if (bUsesEnd1)
                            { b.SetState_X1(ToCoord(f.Tangent2.X)); b.SetState_Y1(ToCoord(f.Tangent2.Y)); }
                            else
                            { b.SetState_X2(ToCoord(f.Tangent2.X)); b.SetState_Y2(ToCoord(f.Tangent2.Y)); }

                            IPCB_Arc arc = pcbServer.PCBObjectFactory(
                                TObjectId.eArcObject, TDimensionKind.eNoDimension,
                                TObjectCreationMode.eCreate_Default) as IPCB_Arc;

                            arc.SetState_CenterX(ToCoord(f.Centre.X));
                            arc.SetState_CenterY(ToCoord(f.Centre.Y));
                            arc.SetState_Radius(ToCoord(f.Radius));
                            arc.SetState_StartAngle(f.StartAngleDeg);
                            arc.SetState_EndAngle(f.EndAngleDeg);
                            arc.SetState_LineWidth(a.GetState_Width());
                            arc.SetState_V7Layer(a.GetState_V7Layer());

                            // The arc carries the net, or it is a stranded
                            // piece of copper in the middle of a connection.
                            try
                            {
                                IPCB_Net net = a.GetState_Net();
                                if (net != null) arc.SetState_Net(net);
                            }
                            catch { }

                            board.AddPCBObject(arc);

                            usedEnds.Add(ka);
                            usedEnds.Add(kb);
                            res.Applied++;
                        }
                        catch (Exception ex)
                        {
                            res.Errors.Add("Corner " + i + "/" + j + ": " + ex.GetType().Name + " -- " + ex.Message);
                        }
                    }
                }
            }
            finally { pcbServer.PostProcess(); }

            board.ViewManager_FullUpdate();
            Log.Write("Geometry.Fillet: " + res.Applied + " corner(s) rounded, " + res.Skipped + " skipped");
            return res;
        }

        // ==================================================================
        // Distribute
        // ==================================================================
        public static Result Distribute(IPCB_ServerInterface pcbServer, IPCB_Board board, bool horizontal)
        {
            Result res = new Result();

            List<IPCB_Primitive> sel = Selection(board, new TObjectId[] {
                TObjectId.eComponentObject, TObjectId.eViaObject, TObjectId.ePadObject,
                TObjectId.eTextObject, TObjectId.eFillObject }, res.Errors);

            if (sel.Count < 3)
            {
                res.Errors.Add("Select at least three objects to distribute" +
                               (sel.Count > 0 ? " (only " + sel.Count + " selected)." : "."));
                return res;
            }

            // Position is taken from the bounding box centre, so a component
            // and a via distribute against the same reference.
            List<int> order = new List<int>();
            List<double> pos = new List<double>();
            List<CoordRect> boxes = new List<CoordRect>();

            for (int i = 0; i < sel.Count; i++)
            {
                CoordRect r = sel[i].BoundingRectangle();
                boxes.Add(r);
                double c = horizontal
                    ? (ToMM(r.GetX1()) + ToMM(r.GetX2())) / 2.0
                    : (ToMM(r.GetY1()) + ToMM(r.GetY2())) / 2.0;
                pos.Add(c);
                order.Add(i);
            }

            // Sorted, or distributing would shuffle objects past each other.
            order.Sort(delegate (int x, int y) { return pos[x].CompareTo(pos[y]); });

            double[] sorted = new double[order.Count];
            for (int i = 0; i < order.Count; i++) sorted[i] = pos[order[i]];
            double[] spaced = FilletGeometry.Distribute(sorted);

            res.Considered = sel.Count;

            pcbServer.PreProcess();
            try
            {
                for (int i = 0; i < order.Count; i++)
                {
                    int idx = order[i];
                    double delta = spaced[i] - pos[idx];
                    if (Math.Abs(delta) < 1e-9) continue;

                    try
                    {
                        if (Move(sel[idx], horizontal ? delta : 0.0, horizontal ? 0.0 : delta))
                            res.Applied++;
                        else
                            res.Skipped++;
                    }
                    catch (Exception ex)
                    {
                        res.Errors.Add("Object " + idx + ": " + ex.GetType().Name + " -- " + ex.Message);
                    }
                }
            }
            finally { pcbServer.PostProcess(); }

            board.ViewManager_FullUpdate();
            Log.Write("Geometry.Distribute: " + res.Applied + " object(s) moved");
            return res;
        }

        // ==================================================================
        // Scale
        // ==================================================================
        public static Result ScaleSelection(IPCB_ServerInterface pcbServer, IPCB_Board board, double factor)
        {
            Result res = new Result();

            if (factor <= 0.0) { res.Errors.Add("Scale factor must be greater than 0."); return res; }
            if (Math.Abs(factor - 1.0) < 1e-9) { res.Errors.Add("A factor of 1 changes nothing."); return res; }

            List<IPCB_Primitive> sel = Selection(board, new TObjectId[] {
                TObjectId.eComponentObject, TObjectId.eViaObject, TObjectId.ePadObject,
                TObjectId.eTextObject, TObjectId.eFillObject, TObjectId.eTrackObject,
                TObjectId.eArcObject }, res.Errors);

            if (sel.Count == 0) { res.Errors.Add("Nothing suitable is selected."); return res; }

            // Anchor on the centre of the selection's extent, so scaling does
            // not walk the whole group across the board.
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            for (int i = 0; i < sel.Count; i++)
            {
                CoordRect r = sel[i].BoundingRectangle();
                minX = Math.Min(minX, ToMM(r.GetX1()));
                maxX = Math.Max(maxX, ToMM(r.GetX2()));
                minY = Math.Min(minY, ToMM(r.GetY1()));
                maxY = Math.Max(maxY, ToMM(r.GetY2()));
            }
            Vec anchor = new Vec((minX + maxX) / 2.0, (minY + maxY) / 2.0);

            res.Considered = sel.Count;

            pcbServer.PreProcess();
            try
            {
                for (int i = 0; i < sel.Count; i++)
                {
                    try
                    {
                        IPCB_Primitive p = sel[i];
                        TObjectId id = p.GetState_ObjectID();

                        if (id == TObjectId.eTrackObject)
                        {
                            IPCB_Track t = p as IPCB_Track;
                            if (t == null) { res.Skipped++; continue; }
                            Vec p1 = FilletGeometry.Scale(new Vec(ToMM(t.GetState_X1()), ToMM(t.GetState_Y1())), anchor, factor);
                            Vec p2 = FilletGeometry.Scale(new Vec(ToMM(t.GetState_X2()), ToMM(t.GetState_Y2())), anchor, factor);
                            t.SetState_X1(ToCoord(p1.X)); t.SetState_Y1(ToCoord(p1.Y));
                            t.SetState_X2(ToCoord(p2.X)); t.SetState_Y2(ToCoord(p2.Y));
                            res.Applied++;
                        }
                        else if (id == TObjectId.eArcObject)
                        {
                            IPCB_Arc a = p as IPCB_Arc;
                            if (a == null) { res.Skipped++; continue; }
                            Vec c = FilletGeometry.Scale(new Vec(ToMM(a.GetState_CenterX()), ToMM(a.GetState_CenterY())), anchor, factor);
                            a.SetState_CenterX(ToCoord(c.X));
                            a.SetState_CenterY(ToCoord(c.Y));
                            // The radius has to scale with the centre or the
                            // arc stops meeting the tracks it joined.
                            a.SetState_Radius(ToCoord(ToMM(a.GetState_Radius()) * factor));
                            res.Applied++;
                        }
                        else
                        {
                            CoordRect r = p.BoundingRectangle();
                            Vec c = new Vec((ToMM(r.GetX1()) + ToMM(r.GetX2())) / 2.0,
                                            (ToMM(r.GetY1()) + ToMM(r.GetY2())) / 2.0);
                            Vec n = FilletGeometry.Scale(c, anchor, factor);
                            if (Move(p, n.X - c.X, n.Y - c.Y)) res.Applied++;
                            else res.Skipped++;
                        }
                    }
                    catch (Exception ex)
                    {
                        res.Errors.Add("Object " + i + ": " + ex.GetType().Name + " -- " + ex.Message);
                    }
                }
            }
            finally { pcbServer.PostProcess(); }

            board.ViewManager_FullUpdate();
            Log.Write("Geometry.Scale: " + res.Applied + " object(s) scaled by " + factor.ToString("0.###", Inv));
            return res;
        }

        // ==================================================================
        // Flip components to the other side
        //
        // FlipComponent() does the whole job -- layer, mirroring of the
        // footprint, and the designator with it. Doing it by hand (set layer,
        // mirror text, mirror pad offsets) gets the pads right and the
        // silkscreen backwards, which is how a board comes back with every
        // reference designator mirrored.
        // ==================================================================
        public static Result FlipComponents(IPCB_ServerInterface pcbServer, IPCB_Board board)
        {
            Result res = new Result();

            List<IPCB_Primitive> sel = Selection(board, new TObjectId[] { TObjectId.eComponentObject },
                                                 res.Errors);
            if (sel.Count == 0)
            { res.Errors.Add("Select the components to flip first."); return res; }

            res.Considered = sel.Count;

            pcbServer.PreProcess();
            try
            {
                for (int i = 0; i < sel.Count; i++)
                {
                    IPCB_Component c = sel[i] as IPCB_Component;
                    if (c == null) { res.Skipped++; continue; }

                    bool opened = false;
                    try
                    {
                        c.BeginModify();
                        opened = true;
                        c.FlipComponent();
                        res.Applied++;
                    }
                    catch (Exception ex)
                    {
                        res.Errors.Add("Component " + i + ": " + ex.GetType().Name + " -- " + ex.Message);
                        res.Skipped++;
                    }
                    finally
                    {
                        // A component left mid-modify blocks File > Save with
                        // no useful message.
                        if (opened) { try { c.EndModify(); } catch { } }
                    }
                }
            }
            finally { pcbServer.PostProcess(); }

            board.ViewManager_FullUpdate();
            Log.Write("Geometry.FlipComponents: " + res.Applied + " flipped");
            return res;
        }

        // Moves whatever kinds expose a position. Returns false for anything
        // that does not, so the caller can count it as skipped rather than
        // pretending it moved.
        private static bool Move(IPCB_Primitive p, double dxMM, double dyMM)
        {
            TObjectId id = p.GetState_ObjectID();

            if (id == TObjectId.eComponentObject)
            {
                IPCB_Component c = p as IPCB_Component;
                if (c == null) return false;
                c.BeginModify();
                try
                {
                    c.SetState_XLocation(c.GetState_XLocation() + ToCoord(dxMM));
                    c.SetState_YLocation(c.GetState_YLocation() + ToCoord(dyMM));
                }
                finally { c.EndModify(); }
                return true;
            }
            if (id == TObjectId.eViaObject)
            {
                IPCB_Via v = p as IPCB_Via;
                if (v == null) return false;
                v.SetState_XLocation(v.GetState_XLocation() + ToCoord(dxMM));
                v.SetState_YLocation(v.GetState_YLocation() + ToCoord(dyMM));
                return true;
            }
            if (id == TObjectId.ePadObject)
            {
                IPCB_Pad pad = p as IPCB_Pad;
                if (pad == null) return false;
                pad.SetState_XLocation(pad.GetState_XLocation() + ToCoord(dxMM));
                pad.SetState_YLocation(pad.GetState_YLocation() + ToCoord(dyMM));
                return true;
            }
            if (id == TObjectId.eTextObject)
            {
                IPCB_Text t = p as IPCB_Text;
                if (t == null) return false;
                t.SetState_XLocation(t.GetState_XLocation() + ToCoord(dxMM));
                t.SetState_YLocation(t.GetState_YLocation() + ToCoord(dyMM));
                return true;
            }
            if (id == TObjectId.eFillObject)
            {
                IPCB_Fill f = p as IPCB_Fill;
                if (f == null) return false;
                f.SetState_LocationX(f.GetState_LocationX() + ToCoord(dxMM));
                f.SetState_LocationY(f.GetState_LocationY() + ToCoord(dyMM));
                return true;
            }
            return false;
        }
    }
}
