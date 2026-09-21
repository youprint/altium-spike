// PcbDraw.cs
//
// Small drawing primitives shared by the stackup table and the assembly
// notes: put a line on a layer, put a string on a layer, resolve a layer by
// name. Both features are "compute some text, then draw it", and without
// this they would each grow their own half-correct copy of the same three
// calls.
//
// LAYERS ARE RESOLVED BY NAME, not by a TV6_Layer switch. The enum has
// eV6_Mechanical1..16 plus DrillDrawing, DrillGuide and the overlays, and a
// hand-written switch over that list is both long and wrong the moment
// someone renames Mechanical 13 to "Fab Notes" in Layer Stack Manager.
// pcbServer.LayerUtils().FromString() is Altium's own resolver and already
// understands both the canonical names and the user's names.

using DXP;
using PCB;
using System;
using System.Collections.Generic;

namespace AltiumSpike
{
    public static class PcbDraw
    {
        public static double ToMM(int coord) { return EDP.Utils.CoordToMMs(coord); }
        public static int ToCoord(double mm) { return EDP.Utils.MMsToCoord(mm); }

        // The layers worth offering in a dropdown. Mechanical 1 is the
        // conventional home for fabrication drawing content, and Drill
        // Drawing is where a stackup table usually ends up, so those lead.
        public static readonly string[] DrawableLayers = new string[]
        {
            "Mechanical 1", "Mechanical 2", "Mechanical 3", "Mechanical 4",
            "Mechanical 5", "Mechanical 6", "Mechanical 7", "Mechanical 8",
            "Mechanical 9", "Mechanical 10", "Mechanical 11", "Mechanical 12",
            "Mechanical 13", "Mechanical 14", "Mechanical 15", "Mechanical 16",
            "Drill Drawing", "Drill Guide",
            "Top Overlay", "Bottom Overlay",
        };

        // Returns null when the name does not resolve, so callers can report
        // "no such layer" rather than silently drawing onto Top Layer.
        public static IV7_Layer Layer(IPCB_ServerInterface pcbServer, string layerName)
        {
            try
            {
                IPCB_LayerUtils lu = pcbServer.LayerUtils();
                return lu.FromString((layerName ?? "").Trim());
            }
            catch (Exception ex)
            {
                Log.Write("PcbDraw.Layer(\"" + layerName + "\") failed -- " +
                          ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        public static IPCB_Track Line(IPCB_ServerInterface pcbServer, IPCB_Board board, IV7_Layer layer,
                                      double x1, double y1, double x2, double y2, double widthMM)
        {
            IPCB_Track t = pcbServer.PCBObjectFactory(
                TObjectId.eTrackObject, TDimensionKind.eNoDimension,
                TObjectCreationMode.eCreate_Default) as IPCB_Track;

            t.SetState_X1(ToCoord(x1));
            t.SetState_Y1(ToCoord(y1));
            t.SetState_X2(ToCoord(x2));
            t.SetState_Y2(ToCoord(y2));
            t.SetState_Width(ToCoord(widthMM));
            t.SetState_V7Layer(layer);

            board.AddPCBObject(t);
            return t;
        }

        public static IPCB_Text Text(IPCB_ServerInterface pcbServer, IPCB_Board board, IV7_Layer layer,
                                     double x, double y, double heightMM, double strokeMM, string s)
        {
            IPCB_Text t = pcbServer.PCBObjectFactory(
                TObjectId.eTextObject, TDimensionKind.eNoDimension,
                TObjectCreationMode.eCreate_Default) as IPCB_Text;

            t.SetState_XLocation(ToCoord(x));
            t.SetState_YLocation(ToCoord(y));
            t.SetState_Rotation(0.0);
            t.SetState_Text(s ?? "");
            t.SetState_Size(ToCoord(heightMM));
            t.SetState_Width(ToCoord(strokeMM));
            t.SetState_V7Layer(layer);

            board.AddPCBObject(t);
            return t;
        }

        // Altium's default stroke font is fixed-pitch at roughly 0.6 of the
        // character height per advance. Column widths and the table frame are
        // sized from this rather than measured, because the SDK exposes no
        // text metrics and a wrong guess here only costs a little whitespace.
        public const double CharWidthRatio = 0.62;

        public static double TextWidthMM(string s, double heightMM)
        {
            if (string.IsNullOrEmpty(s)) return 0.0;
            return s.Length * heightMM * CharWidthRatio;
        }

        // ------------------------------------------------------------------
        // Re-running a generator should replace its previous output, not pile
        // a second table on top of the first. Altium has no "tag" field on a
        // primitive, so the marker is a zero-visible-consequence convention:
        // every object a generator draws goes on one layer, and the generator
        // owns a rectangular region of that layer. Deleting by bounding box
        // is precise enough and cannot touch anything on another layer.
        //
        // Returns the number of primitives removed.
        // ------------------------------------------------------------------
        public static int ClearArea(IPCB_Board board, IV7_Layer layer,
                                    double x1, double y1, double x2, double y2)
        {
            double lo_x = Math.Min(x1, x2), hi_x = Math.Max(x1, x2);
            double lo_y = Math.Min(y1, y2), hi_y = Math.Max(y1, y2);

            List<IPCB_Primitive> doomed = new List<IPCB_Primitive>();

            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(
                    new TObjectId[] { TObjectId.eTrackObject, TObjectId.eTextObject }));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);

                IPCB_Primitive p = it.FirstPCBObject();
                while (p != null)
                {
                    try
                    {
                        IV7_Layer pl = p.GetState_V7Layer();
                        if (pl != null && pl.Equals(layer))
                        {
                            CoordRect r = p.BoundingRectangle();
                            double px1 = ToMM(r.GetX1()), py1 = ToMM(r.GetY1());
                            double px2 = ToMM(r.GetX2()), py2 = ToMM(r.GetY2());

                            // fully inside the owned region
                            if (px1 >= lo_x - 1e-6 && px2 <= hi_x + 1e-6 &&
                                py1 >= lo_y - 1e-6 && py2 <= hi_y + 1e-6)
                                doomed.Add(p);
                        }
                    }
                    catch { /* a primitive that will not answer is left alone */ }

                    p = it.NextPCBObject();
                }
            }
            finally
            {
                board.BoardIterator_Destroy(ref it);
            }

            // Removal happens after iteration finishes. Deleting from under a
            // live iterator is how you get a crash inside Altium rather than
            // an exception you can catch.
            int removed = 0;
            for (int i = 0; i < doomed.Count; i++)
            {
                try { board.RemovePCBObject(doomed[i]); removed++; }
                catch (Exception ex)
                {
                    Log.Write("PcbDraw.ClearArea: could not remove a primitive -- " +
                              ex.GetType().Name + ": " + ex.Message);
                }
            }
            return removed;
        }
    }
}
