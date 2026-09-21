// StackupTable.cs
//
// Reads the board's physical layer stack and draws it as a ruled table on a
// chosen mechanical or drill-drawing layer: one row per physical layer, top
// to bottom, with material, thickness, copper weight and dielectric constant,
// and a totals row carrying the finished board thickness.
//
// WHY THE PHYSICAL CLASS. Altium's stack is walked with
// IPCB_LayerStackBaseHelper.First/Next over a TLayerClassID.
// eLayerClass_Electrical gives you the copper only -- an eight-layer board
// would render as eight rows with no dielectric between them, which is not a
// stackup, it is a layer list. eLayerClass_Physical interleaves copper and
// dielectric in true Z order, which is what a fabricator reads.
//
// WHY THE THICKNESS COMES FROM TWO DIFFERENT CALLS. There is no single
// "thickness" on IPCB_LayerObject. Copper carries
// IPCB_ElectricalLayer.GetState_CopperThickness(); dielectric carries
// IPCB_DielectricLayer.GetState_DielectricHeight(). A layer that answers
// neither is reported with a blank thickness rather than a zero, because a
// zero in a stackup table is a statement about the board and a blank is a
// statement about this tool.
//
// COPPER WEIGHT is derived, not read: 1 oz of copper is 34.79 um by
// definition. The column shows the nearest common weight when the thickness
// is within 10% of it and leaves it blank otherwise, so an unusual foil is
// not mislabelled as 1 oz.
//
// RE-RUNNING REPLACES. The generator owns the rectangle it draws into and
// clears it first (PcbDraw.ClearArea), so a second run updates the table
// instead of drawing a second one on top of the first.

using DXP;
using PCB;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace AltiumSpike
{
    public static class StackupTable
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public sealed class Options
        {
            public string LayerName = "Drill Drawing";
            public double OriginXMM = 10.0;      // top-left corner of the table
            public double OriginYMM = 10.0;
            public double TextHeightMM = 1.2;
            public double StrokeMM = 0.15;
            public double LineWidthMM = 0.15;
            public double RowPaddingMM = 0.8;    // vertical breathing room in a row
            public double CellPaddingMM = 1.0;   // horizontal padding either side
            public bool ImperialToo = true;      // show mil alongside mm
            public bool ReplaceExisting = true;
            public string Title = "LAYER STACKUP";
        }

        public sealed class Result
        {
            public int Rows;
            public int PrimitivesDrawn;
            public int PrimitivesRemoved;
            public double BoardThicknessMM;
            public double WidthMM;
            public double HeightMM;
            public List<string> Errors = new List<string>();

            public string Summarise()
            {
                string s = "Drew a " + Rows + "-row stackup table (" +
                           PrimitivesDrawn + " primitives).\n";
                s += "Finished thickness " + BoardThicknessMM.ToString("0.000", Inv) + " mm (" +
                     (BoardThicknessMM / 0.0254).ToString("0.0", Inv) + " mil).\n";
                if (PrimitivesRemoved > 0)
                    s += "Replaced a previous table (" + PrimitivesRemoved + " primitives removed).\n";
                if (Errors.Count > 0)
                {
                    s += "Errors (" + Errors.Count + "):\n";
                    foreach (string e in Errors) s += "  " + e + "\n";
                }
                return s;
            }
        }

        private sealed class Row
        {
            public string Layer = "";
            public string Kind = "";
            public string Material = "";
            public string Thickness = "";
            public string Weight = "";
            public string Er = "";
        }

        private static string MM3(double mm) { return mm.ToString("0.000", Inv); }
        private static string Mil2(double mm) { return (mm / 0.0254).ToString("0.00", Inv); }

        // 1 oz copper == 34.79 um, by definition. Anything not within 10% of a
        // common weight is left blank rather than rounded into a lie.
        private static string CopperWeight(double mm)
        {
            double[] oz = { 0.5, 1.0, 2.0, 3.0, 4.0 };
            for (int i = 0; i < oz.Length; i++)
            {
                double nominal = oz[i] * 0.03479;
                if (Math.Abs(mm - nominal) <= nominal * 0.10)
                    return oz[i].ToString("0.#", Inv) + " oz";
            }
            return "";
        }

        // ------------------------------------------------------------------
        // Read the stack
        // ------------------------------------------------------------------
        private static List<Row> ReadStack(IPCB_Board board, Options opt, Result res)
        {
            List<Row> rows = new List<Row>();
            double total = 0.0;

            IPCB_LayerStack stack;
            try { stack = board.GetState_LayerStack(); }
            catch (Exception ex)
            {
                res.Errors.Add("Could not read the layer stack -- " + ex.GetType().Name + ": " + ex.Message);
                return rows;
            }
            if (stack == null) { res.Errors.Add("This board has no layer stack."); return rows; }

            IPCB_LayerObject lo;
            try { lo = stack.First(TLayerClassID.eLayerClass_Physical); }
            catch (Exception ex)
            {
                res.Errors.Add("Could not walk the physical stack -- " + ex.GetType().Name + ": " + ex.Message);
                return rows;
            }

            int guard = 0;
            while (lo != null && guard++ < 512)
            {
                Row r = new Row();
                try { r.Layer = lo.GetState_LayerName(); } catch { r.Layer = "?"; }

                double thickness = double.NaN;

                IPCB_ElectricalLayer el = lo as IPCB_ElectricalLayer;
                IPCB_DielectricLayer dl = lo as IPCB_DielectricLayer;

                // Order matters: a dielectric is never electrical, but some
                // layer objects implement both interfaces, and for those the
                // copper reading is the meaningful one.
                if (el != null)
                {
                    r.Kind = "Copper";
                    try { thickness = PcbDraw.ToMM(el.GetState_CopperThickness()); } catch { }
                    if (!double.IsNaN(thickness)) r.Weight = CopperWeight(thickness);
                    r.Material = "Copper";
                }
                else if (dl != null)
                {
                    try
                    {
                        TDielectricType dt = dl.GetState_DielectricType();
                        r.Kind = dt == TDielectricType.eCore ? "Core"
                               : dt == TDielectricType.ePrePreg ? "Prepreg"
                               : dt == TDielectricType.eSurfaceMaterial ? "Surface"
                               : dt == TDielectricType.eFilm ? "Film"
                               : "Dielectric";
                    }
                    catch { r.Kind = "Dielectric"; }

                    try { r.Material = dl.GetState_DielectricMaterial(); } catch { }
                    try { thickness = PcbDraw.ToMM(dl.GetState_DielectricHeight()); } catch { }
                    try
                    {
                        double er = dl.GetState_DielectricConstant();
                        if (er > 0.0) r.Er = er.ToString("0.00", Inv);
                    }
                    catch { }
                }
                else
                {
                    r.Kind = "Layer";
                }

                if (!double.IsNaN(thickness))
                {
                    total += thickness;
                    r.Thickness = opt.ImperialToo
                        ? MM3(thickness) + " / " + Mil2(thickness)
                        : MM3(thickness);
                }

                rows.Add(r);

                try { lo = stack.Next(TLayerClassID.eLayerClass_Physical, lo); }
                catch (Exception ex)
                {
                    res.Errors.Add("Stack walk stopped early -- " + ex.GetType().Name + ": " + ex.Message);
                    break;
                }
            }

            if (guard >= 512)
                res.Errors.Add("Stack walk hit its 512-layer guard; the table may be truncated.");

            res.BoardThicknessMM = total;
            return rows;
        }

        // ------------------------------------------------------------------
        // Draw
        // ------------------------------------------------------------------
        public static Result Generate(IPCB_ServerInterface pcbServer, IPCB_Board board, Options opt)
        {
            Result res = new Result();

            IV7_Layer layer = PcbDraw.Layer(pcbServer, opt.LayerName);
            if (layer == null)
            {
                res.Errors.Add("Layer \"" + opt.LayerName + "\" does not exist on this board.");
                return res;
            }

            List<Row> rows = ReadStack(board, opt, res);
            if (rows.Count == 0)
            {
                if (res.Errors.Count == 0) res.Errors.Add("The physical layer stack is empty.");
                return res;
            }

            string thicknessHeader = opt.ImperialToo ? "Thickness mm / mil" : "Thickness mm";
            string[] headers = { "Layer", "Type", "Material", thicknessHeader, "Cu wt", "Er" };

            // Column widths from the widest cell, header included.
            double[] w = new double[6];
            for (int c = 0; c < 6; c++)
                w[c] = PcbDraw.TextWidthMM(headers[c], opt.TextHeightMM);

            for (int i = 0; i < rows.Count; i++)
            {
                string[] cells = { rows[i].Layer, rows[i].Kind, rows[i].Material,
                                   rows[i].Thickness, rows[i].Weight, rows[i].Er };
                for (int c = 0; c < 6; c++)
                    w[c] = Math.Max(w[c], PcbDraw.TextWidthMM(cells[c], opt.TextHeightMM));
            }
            for (int c = 0; c < 6; c++) w[c] += opt.CellPaddingMM * 2.0;

            double rowH = opt.TextHeightMM + opt.RowPaddingMM * 2.0;
            double tableW = 0.0;
            for (int c = 0; c < 6; c++) tableW += w[c];

            // title + header + data rows + totals
            int lineCount = rows.Count + 3;
            double tableH = rowH * lineCount;

            res.WidthMM = tableW;
            res.HeightMM = tableH;

            double x0 = opt.OriginXMM;
            double yTop = opt.OriginYMM;
            double yBot = yTop - tableH;      // drawn downward from the origin

            pcbServer.PreProcess();
            try
            {
                if (opt.ReplaceExisting)
                {
                    // A small margin catches a previous table drawn with
                    // slightly different column widths.
                    res.PrimitivesRemoved = PcbDraw.ClearArea(board, layer,
                        x0 - 1.0, yBot - 1.0, x0 + tableW + 1.0, yTop + 1.0);
                }

                int drawn = 0;

                // frame
                PcbDraw.Line(pcbServer, board, layer, x0, yTop, x0 + tableW, yTop, opt.LineWidthMM);
                PcbDraw.Line(pcbServer, board, layer, x0, yBot, x0 + tableW, yBot, opt.LineWidthMM);
                PcbDraw.Line(pcbServer, board, layer, x0, yTop, x0, yBot, opt.LineWidthMM);
                PcbDraw.Line(pcbServer, board, layer, x0 + tableW, yTop, x0 + tableW, yBot, opt.LineWidthMM);
                drawn += 4;

                // title row, spanning the full width
                double yTitle = yTop - rowH;
                PcbDraw.Line(pcbServer, board, layer, x0, yTitle, x0 + tableW, yTitle, opt.LineWidthMM);
                PcbDraw.Text(pcbServer, board, layer,
                             x0 + opt.CellPaddingMM, yTitle + opt.RowPaddingMM,
                             opt.TextHeightMM, opt.StrokeMM, opt.Title);
                drawn += 2;

                // horizontal rules under the header and each data row
                for (int i = 1; i <= lineCount - 1; i++)
                {
                    double y = yTop - rowH * (i + 1);
                    if (y <= yBot + 1e-9) break;
                    PcbDraw.Line(pcbServer, board, layer, x0, y, x0 + tableW, y, opt.LineWidthMM);
                    drawn++;
                }

                // vertical column rules, below the title row only
                double xAcc = x0;
                for (int c = 0; c < 5; c++)
                {
                    xAcc += w[c];
                    PcbDraw.Line(pcbServer, board, layer, xAcc, yTitle, xAcc, yBot, opt.LineWidthMM);
                    drawn++;
                }

                // header text
                double yHeader = yTitle - rowH;
                xAcc = x0;
                for (int c = 0; c < 6; c++)
                {
                    PcbDraw.Text(pcbServer, board, layer,
                                 xAcc + opt.CellPaddingMM, yHeader + opt.RowPaddingMM,
                                 opt.TextHeightMM, opt.StrokeMM, headers[c]);
                    xAcc += w[c];
                    drawn++;
                }

                // data rows
                for (int i = 0; i < rows.Count; i++)
                {
                    double y = yHeader - rowH * (i + 1);
                    string[] cells = { rows[i].Layer, rows[i].Kind, rows[i].Material,
                                       rows[i].Thickness, rows[i].Weight, rows[i].Er };
                    xAcc = x0;
                    for (int c = 0; c < 6; c++)
                    {
                        if (!string.IsNullOrEmpty(cells[c]))
                        {
                            PcbDraw.Text(pcbServer, board, layer,
                                         xAcc + opt.CellPaddingMM, y + opt.RowPaddingMM,
                                         opt.TextHeightMM, opt.StrokeMM, cells[c]);
                            drawn++;
                        }
                        xAcc += w[c];
                    }
                    res.Rows++;
                }

                // totals row
                double yTotal = yHeader - rowH * (rows.Count + 1);
                string total = "TOTAL " +
                    (opt.ImperialToo
                        ? MM3(res.BoardThicknessMM) + " mm / " + Mil2(res.BoardThicknessMM) + " mil"
                        : MM3(res.BoardThicknessMM) + " mm");
                PcbDraw.Text(pcbServer, board, layer,
                             x0 + opt.CellPaddingMM, yTotal + opt.RowPaddingMM,
                             opt.TextHeightMM, opt.StrokeMM, total);
                drawn++;

                res.PrimitivesDrawn = drawn;
            }
            catch (Exception ex)
            {
                res.Errors.Add(ex.GetType().Name + " -- " + ex.Message);
                Log.Exception("StackupTable.Generate", ex);
            }
            finally
            {
                pcbServer.PostProcess();
            }

            board.ViewManager_FullUpdate();
            Log.Write("StackupTable: " + res.Rows + " row(s), " + res.PrimitivesDrawn +
                      " primitive(s) on " + opt.LayerName);
            return res;
        }
    }
}
