// BoardExport.cs
//
// C# port of ExportBoardData.pas -- produces the SAME three CSVs, with the
// same headers, the same column order and the same 3-decimal formatting:
//
//     footprint_sizes.csv
//       Designator,Pattern,Width,Height,CenterOffsetX,CenterOffsetY,
//       X,Y,Rotation,Layer,Locked
//     pad_nets.csv
//       Designator,PadNumber,NetName,X,Y
//     board_geometry.csv
//       Type,Name,Index,X,Y,Diameter
//
// Read-only: like the DelphiScript, this never mutates the board.
//
// API NOTES -- every call below was read from the SDK assembly metadata,
// not inferred. The C# surface is shaped differently from DelphiScript:
//
//   DelphiScript            C#
//   ------------------      -------------------------------------------
//   Component.Name.Text     comp.GetState_Name().GetState_Text()
//   Component.Pattern       comp.GetState_Pattern()
//   Component.x / .y        comp.GetState_XLocation() / _YLocation()
//   Component.Rotation      comp.GetState_Rotation()
//   Component.Layer         comp.GetState_Layer()        -> TV6_Layer
//   Component.Moveable      comp.GetState_Moveable()
//   Component.Bounding...   comp.BoundingRectangleNoNameComment()
//                                                        -> CoordRect
//   CoordToMMs(c)           EDP.Utils.CoordToMMs(c)
//   Pad.Name                pad.GetState_Name()
//   Pad.Net.Name            pad.GetState_Net().GetState_Name()
//   Pad.HoleSize            pad.GetState_HoleSize()
//   Board.BoardOutline      board.GetState_BoardOutline()
//   Outline.PointCount      outline.GetState_PointCount()
//   Segments[i].vx/.vy      outline.GetState_Segments(i) -> PolySegment
//   Iterator.AddFilter_
//     LayerSet(AllLayers)   iterator.AddFilter_AllLayers()
//
// DecimalSeparator: DelphiScript needed a global locale guard. .NET does
// not -- every format call below passes CultureInfo.InvariantCulture
// explicitly, so a comma-decimal Windows locale cannot corrupt the CSVs.
// This machine is fr-FR, so that matters here.

using DXP;
using PCB;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AltiumSpike
{
    public static class BoardExport
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // Two separate mismatches with Delphi's FormatFloat, both found by
        // diffing real output against ExportBoardData.pas:
        //
        //  1. NEGATIVE ZERO. .NET keeps the sign on a value that rounds to
        //     zero, so -0.00004 prints "-0.000"; Delphi prints "0.000".
        //     Assigning 0 to a -0.0 double yields +0.0, which fixes it.
        //
        //  2. MIDPOINT ROUNDING. Math.Round defaults to banker's rounding
        //     (midpoint-to-even), so 33.4165 -> 33.416. Delphi rounds away
        //     from zero -> 33.417. Four pad coordinates on this board land
        //     exactly on a midpoint, so this is not hypothetical.
        //     MidpointRounding.AwayFromZero matches FormatFloat.
        private static string F3(double v)
        {
            double r = Math.Round(v, 3, MidpointRounding.AwayFromZero);
            if (r == 0) r = 0;
            return r.ToString("0.000", Inv);
        }

        private static string F1(double v)
        {
            double r = Math.Round(v, 1, MidpointRounding.AwayFromZero);
            if (r == 0) r = 0;
            return r.ToString("0.0", Inv);
        }
        private static double MM(int coord) { return EDP.Utils.CoordToMMs(coord); }

        // A designator or net name containing a comma would corrupt the row.
        // The DelphiScript concatenated raw; we quote defensively instead.
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

        // ------------------------------------------------------------------
        // footprint_sizes.csv
        // ------------------------------------------------------------------
        public static int FootprintSizes(IPCB_Board board, string folder)
        {
            List<string> lines = new List<string>();
            lines.Add("Designator,Pattern,Width,Height,CenterOffsetX,CenterOffsetY,X,Y,Rotation,Layer,Locked");
            int rows = 0;

            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(TObjectId.eComponentObject));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);

                IPCB_Component comp = it.FirstPCBObject() as IPCB_Component;
                while (comp != null)
                {
                    try
                    {
                        string designator = comp.GetState_Name().GetState_Text();
                        string pattern = comp.GetState_Pattern();

                        CoordRect r = comp.BoundingRectangleNoNameComment();
                        double widthMM = MM(r.GetX2() - r.GetX1());
                        double heightMM = MM(r.GetY2() - r.GetY1());

                        double xMM = MM(comp.GetState_XLocation());
                        double yMM = MM(comp.GetState_YLocation());

                        // anchor-to-true-centre offset, exactly as the .pas computes it
                        double offX = xMM - (MM(r.GetX1()) + widthMM / 2.0);
                        double offY = yMM - (MM(r.GetY1()) + heightMM / 2.0);

                        string layer = (comp.GetState_Layer() == TV6_Layer.eV6_BottomLayer) ? "Bottom" : "Top";

                        // INVERTED, as documented in LockComponents.pas:
                        // Moveable == false means the component is LOCKED.
                        string locked = comp.GetState_Moveable() ? "False" : "True";

                        lines.Add(string.Join(",",
                            Csv(designator), Csv(pattern),
                            F3(widthMM), F3(heightMM),
                            F3(offX), F3(offY),
                            F3(xMM), F3(yMM),
                            F1(comp.GetState_Rotation()),
                            layer, locked));
                        rows++;
                    }
                    catch (Exception ex)
                    {
                        lines.Add("# error reading a component -- skipped");
                        Log.Write("footprint_sizes: skipped a component -- " + ex.GetType().Name + ": " + ex.Message);
                    }

                    comp = it.NextPCBObject() as IPCB_Component;
                }
            }
            finally
            {
                board.BoardIterator_Destroy(ref it);
            }

            Save(folder, "footprint_sizes.csv", lines);
            return rows;
        }

        // ------------------------------------------------------------------
        // pad_nets.csv
        // ------------------------------------------------------------------
        public static int PadNets(IPCB_Board board, string folder)
        {
            List<string> lines = new List<string>();
            lines.Add("Designator,PadNumber,NetName,X,Y");
            int rows = 0;

            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(TObjectId.eComponentObject));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);

                IPCB_Component comp = it.FirstPCBObject() as IPCB_Component;
                while (comp != null)
                {
                    string designator = "?";
                    try
                    {
                        designator = comp.GetState_Name().GetState_Text();

                        IPCB_GroupIterator gi = comp.GroupIterator_Create();
                        try
                        {
                            gi.AddFilter_ObjectSet(new TObjectSet(TObjectId.ePadObject));
                            gi.AddFilter_AllLayers();

                            IPCB_Pad pad = gi.FirstPCBObject() as IPCB_Pad;
                            while (pad != null)
                            {
                                try
                                {
                                    string padNumber = pad.GetState_Name();

                                    IPCB_Net net = pad.GetState_Net();
                                    string netName = (net != null) ? net.GetState_Name() : "NO_NET";

                                    lines.Add(string.Join(",",
                                        Csv(designator), Csv(padNumber), Csv(netName),
                                        F3(MM(pad.GetState_XLocation())),
                                        F3(MM(pad.GetState_YLocation()))));
                                    rows++;
                                }
                                catch (Exception ex)
                                {
                                    lines.Add("# error reading a pad on " + designator + " -- skipped");
                                    Log.Write("pad_nets: skipped a pad on " + designator + " -- " + ex.Message);
                                }

                                pad = gi.NextPCBObject() as IPCB_Pad;
                            }
                        }
                        finally
                        {
                            comp.GroupIterator_Destroy(ref gi);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Write("pad_nets: skipped component " + designator + " -- " + ex.Message);
                    }

                    comp = it.NextPCBObject() as IPCB_Component;
                }
            }
            finally
            {
                board.BoardIterator_Destroy(ref it);
            }

            Save(folder, "pad_nets.csv", lines);
            return rows;
        }

        // ------------------------------------------------------------------
        // board_geometry.csv
        // ------------------------------------------------------------------
        public static int BoardGeometry(IPCB_Board board, string folder)
        {
            List<string> lines = new List<string>();
            lines.Add("Type,Name,Index,X,Y,Diameter");
            int rows = 0;

            // --- ordered outline vertices (close back to Index 0) ---
            try
            {
                IPCB_BoardOutline outline = board.GetState_BoardOutline();
                int n = outline.GetState_PointCount();
                for (int i = 0; i < n; i++)
                {
                    PolySegment seg = outline.GetState_Segments(i);
                    lines.Add("Outline,Board," + i.ToString(Inv) + "," +
                              F3(MM(seg.GetVx())) + "," + F3(MM(seg.GetVy())) + ",");
                    rows++;
                }
            }
            catch (Exception ex)
            {
                lines.Add("# error reading Board.BoardOutline");
                Log.Write("board_geometry: outline failed -- " + ex.GetType().Name + ": " + ex.Message);
            }

            // --- mounting holes, identified the same way the .pas does ---
            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(TObjectId.eComponentObject));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);

                IPCB_Component comp = it.FirstPCBObject() as IPCB_Component;
                while (comp != null)
                {
                    try
                    {
                        string name = comp.GetState_Name().GetState_Text();
                        string patternU = (comp.GetState_Pattern() ?? "").ToUpperInvariant();
                        string nameU = (name ?? "").ToUpperInvariant();

                        bool isHole = patternU.Contains("MOUNTING HOLE") || nameU.StartsWith("HOLE");
                        if (isHole)
                        {
                            IPCB_GroupIterator gi = comp.GroupIterator_Create();
                            try
                            {
                                gi.AddFilter_ObjectSet(new TObjectSet(TObjectId.ePadObject));
                                gi.AddFilter_AllLayers();

                                IPCB_Pad pad = gi.FirstPCBObject() as IPCB_Pad;
                                if (pad != null)
                                {
                                    lines.Add("Hole," + Csv(name) + ",," +
                                              F3(MM(comp.GetState_XLocation())) + "," +
                                              F3(MM(comp.GetState_YLocation())) + "," +
                                              F3(MM(pad.GetState_HoleSize())));
                                    rows++;
                                }
                            }
                            finally
                            {
                                comp.GroupIterator_Destroy(ref gi);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Write("board_geometry: skipped a component -- " + ex.Message);
                    }

                    comp = it.NextPCBObject() as IPCB_Component;
                }
            }
            finally
            {
                board.BoardIterator_Destroy(ref it);
            }

            Save(folder, "board_geometry.csv", lines);
            return rows;
        }
    }
}
