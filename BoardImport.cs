// BoardImport.cs
//
// C# port of the four placement/lock DelphiScripts:
//
//   PlaceObjectsFromCSV.pas   -> PlaceObjects      (objects.csv, 17 cols)
//   PlacePolygonPours.pas     -> PlacePours        (pours.csv)
//   PlaceRegionsFromCSV.pas   -> PlaceRegions      (regions.csv)
//   LockComponents.pas        -> SetLock(true/false)
//
// These WRITE to the board, unlike BoardExport. Every batch is wrapped in
// PreProcess/PostProcess and every component edit in BeginModify/EndModify
// with try/finally, because the .pas headers record that a dangling edit
// transaction blocks File > Save afterwards ("a command is currently
// active").
//
// API differences from DelphiScript worth knowing -- the C# surface is NOT
// a rename of the Delphi one:
//
//   Delphi                  C#
//   ---------------------   --------------------------------------------
//   Track.x1/.y1/.x2/.y2    SetState_X1/_Y1/_X2/_Y2
//   Via.x/.y                SetState_XLocation/_YLocation
//   Via.LowLayer (TLayer)   SetState_LowLayer(IV7_Layer)  <- different type,
//                           obtained via pcbServer.LayerUtils().FromString()
//   Arc.XCenter/.YCenter    SetState_CenterX/_CenterY
//   Fill.X1Location..       SetState_LocationX/_LocationY + _Width/_Length
//                           <- a CENTRE + EXTENT model, not two corners.
//                           The CSV keeps two corners and we convert.
//   Region contour X[i]     IPCB_Contour.AddPoint(x, y)  (0-based, cleaner
//                           than the 1-indexed X[i]/Y[i] the .pas used)
//
// No DecimalSeparator guard: all parsing is invariant (see CsvIo.TryNum).

using DXP;
using PCB;
using System;
using System.Collections.Generic;

namespace AltiumSpike
{
    public static class BoardImport
    {
        public class Result
        {
            public int Placed;
            public List<string> Missing = new List<string>();
            public List<string> Errors = new List<string>();

            public string Summarise(string noun)
            {
                string s = "Placed " + Placed + " " + noun + "." + "\n" +
                           "Not found / missing (" + Missing.Count + "):\n";
                foreach (string m in Missing) s += "  " + m + "\n";
                s += "Row errors (" + Errors.Count + "):\n";
                foreach (string e in Errors) s += "  " + e + "\n";
                return s;
            }
        }

        private static int MM(double mm) { return EDP.Utils.MMsToCoord(mm); }

        private static TV6_Layer StrToLayer(string s)
        {
            string u = (s ?? "").Trim().ToUpperInvariant();
            if (u == "BOTTOM" || u == "BOT" || u == "B") return TV6_Layer.eV6_BottomLayer;
            if (u == "KEEPOUT" || u == "KEEP-OUT") return TV6_Layer.eV6_KeepOutLayer;
            return TV6_Layer.eV6_TopLayer;
        }

        private static IPCB_Net FindNet(IPCB_Board board, string netName)
        {
            string target = (netName ?? "").Trim().ToUpperInvariant();
            if (target.Length == 0) return null;

            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(TObjectId.eNetObject));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);

                IPCB_Net n = it.FirstPCBObject() as IPCB_Net;
                while (n != null)
                {
                    if ((n.GetState_Name() ?? "").ToUpperInvariant() == target) return n;
                    n = it.NextPCBObject() as IPCB_Net;
                }
            }
            finally
            {
                board.BoardIterator_Destroy(ref it);
            }
            return null;
        }

        // =================================================================
        // objects.csv -- Component / Track / Via / Arc / Fill / Text / Coordinate
        // =================================================================
        public static Result PlaceObjects(IPCB_ServerInterface pcbServer, IPCB_Board board, string csvPath)
        {
            Result r = new Result();
            string[] lines = CsvIo.ReadLines(csvPath);

            pcbServer.PreProcess();
            try
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    string raw = lines[i];
                    if (string.IsNullOrWhiteSpace(raw)) continue;

                    List<string> f = CsvIo.Split(raw);
                    string where = "Line " + (i + 1);

                    if (f.Count < 17)
                    {
                        if (i == 0 && CsvIo.At(f, 0).ToUpperInvariant() == "OBJECTTYPE") continue;
                        r.Errors.Add(where + ": expected 17 fields, got " + f.Count);
                        continue;
                    }

                    string objType = CsvIo.At(f, 0).ToUpperInvariant();
                    string designator = CsvIo.At(f, 1);
                    double x1, y1, x2, y2, rot, width, hole, dia, radius, a0, a1, height;

                    // header row: first line whose X1 is not a number
                    if (i == 0 && !CsvIo.TryNum(CsvIo.At(f, 2), out x1)) continue;

                    string layerStr = CsvIo.At(f, 7);
                    string netStr = CsvIo.At(f, 11);
                    string textStr = CsvIo.At(f, 15);

                    try
                    {
                        if (objType == "COMPONENT")
                        {
                            if (!CsvIo.TryNum(CsvIo.At(f, 2), out x1)) { r.Errors.Add(where + ": bad X1"); continue; }
                            if (!CsvIo.TryNum(CsvIo.At(f, 3), out y1)) { r.Errors.Add(where + ": bad Y1"); continue; }
                            if (!CsvIo.TryNum(CsvIo.At(f, 6), out rot)) { r.Errors.Add(where + ": bad Rotation"); continue; }

                            IPCB_Component comp = board.GetPcbComponentByRefDes(designator);
                            if (comp == null)
                            {
                                r.Missing.Add("Component " + designator + " not found (" + where + ")");
                                continue;
                            }

                            TV6_Layer target = StrToLayer(layerStr);
                            comp.BeginModify();
                            try
                            {
                                // Layer change performs the flip and must happen
                                // BEFORE position/rotation -- see the .pas notes.
                                if (comp.GetState_Layer() != target) comp.SetState_Layer(target);
                                comp.SetState_XLocation(MM(x1));
                                comp.SetState_YLocation(MM(y1));
                                comp.SetState_Rotation(rot);
                            }
                            finally
                            {
                                comp.EndModify();
                            }
                            r.Placed++;
                        }
                        else if (objType == "TRACK")
                        {
                            if (!CsvIo.TryNum(CsvIo.At(f, 2), out x1) || !CsvIo.TryNum(CsvIo.At(f, 3), out y1) ||
                                !CsvIo.TryNum(CsvIo.At(f, 4), out x2) || !CsvIo.TryNum(CsvIo.At(f, 5), out y2) ||
                                !CsvIo.TryNum(CsvIo.At(f, 8), out width))
                            { r.Errors.Add(where + ": bad Track geometry"); continue; }

                            IPCB_Net net = null;
                            if (netStr.Trim().Length > 0)
                            {
                                net = FindNet(board, netStr);
                                if (net == null) { r.Missing.Add("Net " + netStr.Trim() + " not found (" + where + ")"); continue; }
                            }

                            IPCB_Track tr = pcbServer.PCBObjectFactory(
                                TObjectId.eTrackObject, TDimensionKind.eNoDimension,
                                TObjectCreationMode.eCreate_Default) as IPCB_Track;
                            tr.SetState_X1(MM(x1));
                            tr.SetState_Y1(MM(y1));
                            tr.SetState_X2(MM(x2));
                            tr.SetState_Y2(MM(y2));
                            tr.SetState_Width(MM(width));
                            tr.SetState_Layer(StrToLayer(layerStr));
                            if (net != null) tr.SetState_Net(net);
                            board.AddPCBObject(tr);
                            r.Placed++;
                        }
                        else if (objType == "VIA")
                        {
                            if (!CsvIo.TryNum(CsvIo.At(f, 2), out x1) || !CsvIo.TryNum(CsvIo.At(f, 3), out y1) ||
                                !CsvIo.TryNum(CsvIo.At(f, 9), out hole) || !CsvIo.TryNum(CsvIo.At(f, 10), out dia))
                            { r.Errors.Add(where + ": bad Via geometry"); continue; }

                            IPCB_Net net = null;
                            if (netStr.Trim().Length > 0)
                            {
                                net = FindNet(board, netStr);
                                if (net == null) { r.Missing.Add("Net " + netStr.Trim() + " not found (" + where + ")"); continue; }
                            }

                            IPCB_Via via = pcbServer.PCBObjectFactory(
                                TObjectId.eViaObject, TDimensionKind.eNoDimension,
                                TObjectCreationMode.eCreate_Default) as IPCB_Via;
                            via.SetState_XLocation(MM(x1));
                            via.SetState_YLocation(MM(y1));
                            via.SetState_HoleSize(MM(hole));
                            via.SetState_Size(MM(dia));

                            IPCB_LayerUtils lu = pcbServer.LayerUtils();
                            via.SetState_LowLayer(lu.FromString("Top Layer"));
                            via.SetState_HighLayer(lu.FromString("Bottom Layer"));

                            if (net != null) via.SetState_Net(net);
                            board.AddPCBObject(via);
                            r.Placed++;
                        }
                        else if (objType == "ARC")
                        {
                            if (!CsvIo.TryNum(CsvIo.At(f, 2), out x1) || !CsvIo.TryNum(CsvIo.At(f, 3), out y1) ||
                                !CsvIo.TryNum(CsvIo.At(f, 12), out radius) ||
                                !CsvIo.TryNum(CsvIo.At(f, 13), out a0) || !CsvIo.TryNum(CsvIo.At(f, 14), out a1) ||
                                !CsvIo.TryNum(CsvIo.At(f, 8), out width))
                            { r.Errors.Add(where + ": bad Arc geometry"); continue; }

                            IPCB_Net net = null;
                            if (netStr.Trim().Length > 0)
                            {
                                net = FindNet(board, netStr);
                                if (net == null) { r.Missing.Add("Net " + netStr.Trim() + " not found (" + where + ")"); continue; }
                            }

                            IPCB_Arc arc = pcbServer.PCBObjectFactory(
                                TObjectId.eArcObject, TDimensionKind.eNoDimension,
                                TObjectCreationMode.eCreate_Default) as IPCB_Arc;
                            arc.SetState_CenterX(MM(x1));
                            arc.SetState_CenterY(MM(y1));
                            arc.SetState_Radius(MM(radius));
                            arc.SetState_StartAngle(a0);
                            arc.SetState_EndAngle(a1);
                            arc.SetState_LineWidth(MM(width));
                            arc.SetState_Layer(StrToLayer(layerStr));
                            if (net != null) arc.SetState_Net(net);
                            board.AddPCBObject(arc);
                            r.Placed++;
                        }
                        else if (objType == "FILL")
                        {
                            if (!CsvIo.TryNum(CsvIo.At(f, 2), out x1) || !CsvIo.TryNum(CsvIo.At(f, 3), out y1) ||
                                !CsvIo.TryNum(CsvIo.At(f, 4), out x2) || !CsvIo.TryNum(CsvIo.At(f, 5), out y2))
                            { r.Errors.Add(where + ": bad Fill geometry"); continue; }
                            if (!CsvIo.TryNum(CsvIo.At(f, 6), out rot)) rot = 0;

                            IPCB_Net net = null;
                            if (netStr.Trim().Length > 0)
                            {
                                net = FindNet(board, netStr);
                                if (net == null) { r.Missing.Add("Net " + netStr.Trim() + " not found (" + where + ")"); continue; }
                            }

                            IPCB_Fill fill = pcbServer.PCBObjectFactory(
                                TObjectId.eFillObject, TDimensionKind.eNoDimension,
                                TObjectCreationMode.eCreate_Default) as IPCB_Fill;

                            // CSV keeps the .pas two-corner contract. The C# API
                            // is corner + extent, and the extent names are the
                            // opposite of what they sound like. Established by
                            // placing a test fill and reading back what Altium
                            // reported, NOT from the names:
                            //
                            //   SetState_LocationX/Y -> the LOWER-LEFT corner
                            //                           (the Properties panel
                            //                           displays the centre, which
                            //                           is what misled the first
                            //                           attempt)
                            //   SetState_Length      -> the X extent
                            //   SetState_Width       -> the Y extent
                            fill.SetState_LocationX(MM(Math.Min(x1, x2)));
                            fill.SetState_LocationY(MM(Math.Min(y1, y2)));
                            fill.SetState_Length(MM(Math.Abs(x2 - x1)));
                            fill.SetState_Width(MM(Math.Abs(y2 - y1)));
                            fill.SetState_Rotation(rot);
                            fill.SetState_Layer(StrToLayer(layerStr));
                            if (net != null) fill.SetState_Net(net);
                            board.AddPCBObject(fill);
                            r.Placed++;
                        }
                        else if (objType == "TEXT")
                        {
                            if (!CsvIo.TryNum(CsvIo.At(f, 2), out x1) || !CsvIo.TryNum(CsvIo.At(f, 3), out y1))
                            { r.Errors.Add(where + ": bad Text position"); continue; }
                            if (!CsvIo.TryNum(CsvIo.At(f, 6), out rot)) rot = 0;
                            if (!CsvIo.TryNum(CsvIo.At(f, 16), out height)) { r.Errors.Add(where + ": bad Height"); continue; }
                            if (!CsvIo.TryNum(CsvIo.At(f, 8), out width)) width = 0.1;
                            if (textStr.Trim().Length == 0) { r.Errors.Add(where + ": Text row has empty content"); continue; }

                            IPCB_Text txt = pcbServer.PCBObjectFactory(
                                TObjectId.eTextObject, TDimensionKind.eNoDimension,
                                TObjectCreationMode.eCreate_Default) as IPCB_Text;
                            txt.SetState_XLocation(MM(x1));
                            txt.SetState_YLocation(MM(y1));
                            txt.SetState_Rotation(rot);
                            txt.SetState_Text(textStr);
                            txt.SetState_Size(MM(height));
                            txt.SetState_Width(MM(width));
                            txt.SetState_Layer(StrToLayer(layerStr));
                            board.AddPCBObject(txt);
                            r.Placed++;
                        }
                        else if (objType == "COORDINATE")
                        {
                            if (!CsvIo.TryNum(CsvIo.At(f, 2), out x1) || !CsvIo.TryNum(CsvIo.At(f, 3), out y1))
                            { r.Errors.Add(where + ": bad Coordinate position"); continue; }

                            IPCB_Coordinate co = pcbServer.PCBObjectFactory(
                                TObjectId.eCoordinateObject, TDimensionKind.eNoDimension,
                                TObjectCreationMode.eCreate_Default) as IPCB_Coordinate;
                            co.SetState_XLocation(MM(x1));
                            co.SetState_YLocation(MM(y1));
                            co.SetState_Layer(StrToLayer(layerStr));
                            board.AddPCBObject(co);
                            r.Placed++;
                        }
                        else
                        {
                            r.Errors.Add(where + ": unrecognized ObjectType \"" + CsvIo.At(f, 0) +
                                "\" (expected Component/Track/Via/Arc/Fill/Text/Coordinate -- " +
                                "Pour/Region have their own commands).");
                        }
                    }
                    catch (Exception ex)
                    {
                        r.Errors.Add(where + ": " + ex.GetType().Name + " -- " + ex.Message);
                        Log.Exception("PlaceObjects " + where, ex);
                    }
                }
            }
            finally
            {
                pcbServer.PostProcess();
            }

            board.ViewManager_FullUpdate();
            return r;
        }

        // =================================================================
        // pours.csv -- Net,Layer,X1,Y1,X2,Y2   (rectangular solid pours)
        // =================================================================
        public static Result PlacePours(IPCB_ServerInterface pcbServer, IPCB_Board board, string csvPath)
        {
            Result r = new Result();
            string[] lines = CsvIo.ReadLines(csvPath);

            pcbServer.PreProcess();
            try
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i])) continue;
                    List<string> f = CsvIo.Split(lines[i]);
                    string where = "Line " + (i + 1);

                    if (f.Count < 6) { r.Errors.Add(where + ": expected 6 fields, got " + f.Count); continue; }

                    double x1, y1, x2, y2;
                    if (i == 0 && !CsvIo.TryNum(CsvIo.At(f, 2), out x1)) continue;   // header

                    if (!CsvIo.TryNum(CsvIo.At(f, 2), out x1) || !CsvIo.TryNum(CsvIo.At(f, 3), out y1) ||
                        !CsvIo.TryNum(CsvIo.At(f, 4), out x2) || !CsvIo.TryNum(CsvIo.At(f, 5), out y2))
                    { r.Errors.Add(where + ": bad rectangle"); continue; }

                    string netStr = CsvIo.At(f, 0).Trim();
                    IPCB_Net net = FindNet(board, netStr);
                    if (net == null) { r.Missing.Add(netStr + " (" + where + ")"); continue; }

                    try
                    {
                        IPCB_Polygon poly = pcbServer.PCBObjectFactory(
                            TObjectId.ePolyObject, TDimensionKind.eNoDimension,
                            TObjectCreationMode.eCreate_Default) as IPCB_Polygon;

                        poly.SetState_Layer(StrToLayer(CsvIo.At(f, 1)));
                        poly.SetState_PolyHatchStyle(TPolyHatchStyle.ePolySolid);
                        poly.SetState_Net(net);
                        poly.SetState_Name(netStr + "_Pour");

                        // Build the outline BEFORE adding to the board -- the .pas
                        // notes that an empty polygon may be discarded as invalid.
                        poly.SetState_PointCount(4);
                        SetSeg(poly, 0, x1, y1);
                        SetSeg(poly, 1, x2, y1);
                        SetSeg(poly, 2, x2, y2);
                        SetSeg(poly, 3, x1, y2);

                        board.AddPCBObject(poly);
                        poly.Rebuild();
                        r.Placed++;
                    }
                    catch (Exception ex)
                    {
                        r.Errors.Add(where + ": " + ex.GetType().Name + " -- " + ex.Message);
                        Log.Exception("PlacePours " + where, ex);
                    }
                }
            }
            finally
            {
                pcbServer.PostProcess();
            }

            board.ViewManager_FullUpdate();
            return r;
        }

        private static void SetSeg(IPCB_Polygon poly, int index, double xmm, double ymm)
        {
            // Segments[i] returns a COPY in DelphiScript; read-modify-write is
            // required there and harmless here, so keep the same shape.
            // PolySegment exposes plain properties (Vx/Vy/Kind), not
            // SetState_* accessors like the interfaces do.
            PolySegment seg = poly.GetState_Segments(index);
            seg.Vx = MM(xmm);
            seg.Vy = MM(ymm);
            seg.Kind = TPolySegmentType.ePolySegmentLine;
            poly.SetState_Segments(index, seg);
        }

        // =================================================================
        // regions.csv -- RegionKind,Layer,Net,X1,Y1,X2,Y2
        // =================================================================
        public static Result PlaceRegions(IPCB_ServerInterface pcbServer, IPCB_Board board, string csvPath)
        {
            Result r = new Result();
            string[] lines = CsvIo.ReadLines(csvPath);

            pcbServer.PreProcess();
            try
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i])) continue;
                    List<string> f = CsvIo.Split(lines[i]);
                    string where = "Line " + (i + 1);

                    if (f.Count < 7) { r.Errors.Add(where + ": expected 7 fields, got " + f.Count); continue; }

                    double x1, y1, x2, y2;
                    if (i == 0 && !CsvIo.TryNum(CsvIo.At(f, 3), out x1)) continue;   // header

                    if (!CsvIo.TryNum(CsvIo.At(f, 3), out x1) || !CsvIo.TryNum(CsvIo.At(f, 4), out y1) ||
                        !CsvIo.TryNum(CsvIo.At(f, 5), out x2) || !CsvIo.TryNum(CsvIo.At(f, 6), out y2))
                    { r.Errors.Add(where + ": bad rectangle"); continue; }

                    string netStr = CsvIo.At(f, 2).Trim();
                    IPCB_Net net = null;
                    if (netStr.Length > 0)
                    {
                        net = FindNet(board, netStr);
                        if (net == null) { r.Missing.Add(netStr + " (" + where + ")"); continue; }
                    }

                    try
                    {
                        IPCB_Region region = pcbServer.PCBObjectFactory(
                            TObjectId.eRegionObject, TDimensionKind.eNoDimension,
                            TObjectCreationMode.eCreate_Default) as IPCB_Region;

                        region.SetState_Layer(StrToLayer(CsvIo.At(f, 1)));
                        if (net != null) region.SetState_Net(net);

                        // Region's outline is a separate Contour object, NOT
                        // Polygon's Segments array -- the .pas learned this the
                        // hard way ("Undeclared identifier: PointCount").
                        IPCB_Contour contour = region.GetMainContour().Replicate();
                        contour.Clear();
                        contour.AddPoint(MM(x1), MM(y1));
                        contour.AddPoint(MM(x2), MM(y1));
                        contour.AddPoint(MM(x2), MM(y2));
                        contour.AddPoint(MM(x1), MM(y2));
                        region.SetOutlineContour(contour);

                        board.AddPCBObject(region);
                        r.Placed++;
                    }
                    catch (Exception ex)
                    {
                        r.Errors.Add(where + ": " + ex.GetType().Name + " -- " + ex.Message);
                        Log.Exception("PlaceRegions " + where, ex);
                    }
                }
            }
            finally
            {
                pcbServer.PostProcess();
            }

            board.ViewManager_FullUpdate();
            return r;
        }

        // =================================================================
        // lock list -- one designator per line
        // =================================================================
        public static Result SetLock(IPCB_ServerInterface pcbServer, IPCB_Board board, string csvPath, bool locked)
        {
            Result r = new Result();
            string[] lines = CsvIo.ReadLines(csvPath);

            pcbServer.PreProcess();
            try
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    string designator = (lines[i] ?? "").Trim();
                    if (designator.Length == 0) continue;
                    if (i == 0 && designator.ToUpperInvariant() == "DESIGNATOR") continue;

                    IPCB_Component comp = board.GetPcbComponentByRefDes(designator);
                    if (comp == null) { r.Missing.Add(designator); continue; }

                    try
                    {
                        comp.BeginModify();
                        try
                        {
                            // INVERTED: Moveable == false means LOCKED.
                            comp.SetState_Moveable(!locked);
                        }
                        finally
                        {
                            comp.EndModify();
                        }
                        r.Placed++;
                    }
                    catch (Exception ex)
                    {
                        r.Missing.Add(designator + " (runtime error: " + ex.Message + ")");
                        Log.Exception("SetLock " + designator, ex);
                    }
                }
            }
            finally
            {
                pcbServer.PostProcess();
            }

            board.ViewManager_FullUpdate();
            return r;
        }
    }
}
