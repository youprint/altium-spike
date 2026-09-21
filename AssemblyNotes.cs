// AssemblyNotes.cs
//
// Gathers board statistics and writes a standardised, numbered set of
// fabrication notes as text on a chosen layer.
//
// MEASURED VERSUS SPECIFIED -- the distinction this file is built around.
//
// A fabricator reading "MINIMUM TRACE WIDTH: 0.127 mm" needs to know whether
// that is what the board actually contains or what the designer told Altium
// to allow. They are different numbers and they fail differently: a rule set
// to 0.1 mm on a board whose narrowest track is 0.2 mm quotes you for finer
// lithography than you need, and a rule looser than the artwork tells the fab
// nothing at all.
//
//   Trace width   MEASURED by walking every track and arc on the copper
//                 layers. This is ground truth and cannot be stale.
//   Hole size     MEASURED from pads and vias.
//   Clearance     TAKEN FROM THE RULE, and labelled as such. Measuring true
//                 minimum copper-to-copper clearance means an all-pairs
//                 geometric analysis across every layer -- that is what DRC
//                 is for, and duplicating it badly here would produce a
//                 number worse than no number.
//   Layer count   from the electrical stack.
//   Dimensions    from the board outline's bounding box.
//
// Each note that comes from a rule says "PER DESIGN RULES" in its text, so
// the distinction survives into the artwork rather than living only here.
//
// The notes are a template with substitutions, not free prose. Fabricators
// read hundreds of these; a house-standard wording they can skim beats
// anything cleverer.

using DXP;
using PCB;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace AltiumSpike
{
    public static class AssemblyNotes
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public sealed class Options
        {
            public string LayerName = "Mechanical 1";
            public double OriginXMM = 10.0;     // top-left; notes run downward
            public double OriginYMM = 10.0;
            public double TextHeightMM = 1.2;
            public double StrokeMM = 0.15;
            public double LineSpacingMM = 2.2;
            public bool ImperialToo = true;
            public bool ReplaceExisting = true;
            public string Title = "FABRICATION NOTES";
            public string SurfaceFinish = "ENIG";
            public string SolderMaskColour = "GREEN";
            public string SilkscreenColour = "WHITE";
            public string IpcClass = "2";
        }

        public sealed class Stats
        {
            public double WidthMM, HeightMM;
            public int SignalLayers;
            public double BoardThicknessMM;
            public double MinTrackMM = double.NaN;      // measured
            public double MinHoleMM = double.NaN;       // measured
            public double MinClearanceMM = double.NaN;  // from rules
            public double RuleMinWidthMM = double.NaN;  // from rules
            public int TrackCount, ViaCount, PadCount;
        }

        public sealed class Result
        {
            public int NotesWritten;
            public int PrimitivesRemoved;
            public Stats Stats = new Stats();
            public List<string> Notes = new List<string>();
            public List<string> Errors = new List<string>();

            public string Summarise()
            {
                string s = "Wrote " + NotesWritten + " note line(s).\n";
                s += "Board " + Stats.WidthMM.ToString("0.00", Inv) + " x " +
                     Stats.HeightMM.ToString("0.00", Inv) + " mm, " +
                     Stats.SignalLayers + " signal layer(s).\n";
                if (!double.IsNaN(Stats.MinTrackMM))
                    s += "Narrowest track measured " + Stats.MinTrackMM.ToString("0.000", Inv) + " mm.\n";
                if (PrimitivesRemoved > 0)
                    s += "Replaced previous notes (" + PrimitivesRemoved + " primitives removed).\n";
                if (Errors.Count > 0)
                {
                    s += "Errors (" + Errors.Count + "):\n";
                    foreach (string e in Errors) s += "  " + e + "\n";
                }
                return s;
            }
        }

        private static string Dim(double mm, bool imperial)
        {
            string s = mm.ToString("0.000", Inv) + " mm";
            if (imperial) s += " (" + (mm / 0.0254).ToString("0.0", Inv) + " mil)";
            return s;
        }

        // ------------------------------------------------------------------
        // Board outline bounding box
        // ------------------------------------------------------------------
        private static void ReadOutline(IPCB_Board board, Stats st, Result res)
        {
            try
            {
                IPCB_BoardOutline outline = board.GetState_BoardOutline();
                int n = outline.GetState_PointCount();
                if (n == 0) { res.Errors.Add("Board outline has no vertices."); return; }

                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;

                for (int i = 0; i < n; i++)
                {
                    PolySegment seg = outline.GetState_Segments(i);
                    double x = PcbDraw.ToMM(seg.GetVx());
                    double y = PcbDraw.ToMM(seg.GetVy());
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }

                st.WidthMM = maxX - minX;
                st.HeightMM = maxY - minY;
            }
            catch (Exception ex)
            {
                res.Errors.Add("Could not read the board outline -- " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // Stack: signal layer count and finished thickness
        // ------------------------------------------------------------------
        private static void ReadStack(IPCB_Board board, Stats st, Result res)
        {
            try { st.SignalLayers = board.GetState_LayerStack_V7().SignalLayerCount(); }
            catch (Exception ex)
            {
                res.Errors.Add("Could not read the signal layer count -- " + ex.GetType().Name);
                Log.Write("AssemblyNotes: SignalLayerCount failed -- " + ex.Message);
            }

            try
            {
                IPCB_LayerStack stack = board.GetState_LayerStack();
                if (stack == null) return;

                double total = 0.0;
                IPCB_LayerObject lo = stack.First(TLayerClassID.eLayerClass_Physical);
                int guard = 0;
                while (lo != null && guard++ < 512)
                {
                    IPCB_ElectricalLayer el = lo as IPCB_ElectricalLayer;
                    IPCB_DielectricLayer dl = lo as IPCB_DielectricLayer;
                    try
                    {
                        if (el != null) total += PcbDraw.ToMM(el.GetState_CopperThickness());
                        else if (dl != null) total += PcbDraw.ToMM(dl.GetState_DielectricHeight());
                    }
                    catch { }
                    lo = stack.Next(TLayerClassID.eLayerClass_Physical, lo);
                }
                st.BoardThicknessMM = total;
            }
            catch (Exception ex)
            {
                res.Errors.Add("Could not total the stack thickness -- " + ex.GetType().Name);
                Log.Write("AssemblyNotes: stack total failed -- " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // Measured minima: narrowest copper, smallest finished hole
        // ------------------------------------------------------------------
        private static void Measure(IPCB_Board board, Stats st, Result res)
        {
            double minTrack = double.MaxValue;
            double minHole = double.MaxValue;
            int tracks = 0, vias = 0, pads = 0;

            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(new TObjectId[] {
                    TObjectId.eTrackObject, TObjectId.eArcObject,
                    TObjectId.eViaObject, TObjectId.ePadObject }));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);

                IPCB_Primitive p = it.FirstPCBObject();
                while (p != null)
                {
                    try
                    {
                        TObjectId id = p.GetState_ObjectID();

                        if (id == TObjectId.eTrackObject)
                        {
                            IPCB_Track t = p as IPCB_Track;
                            if (t != null && IsCopper(p))
                            {
                                double w = PcbDraw.ToMM(t.GetState_Width());
                                if (w > 1e-9 && w < minTrack) minTrack = w;
                                tracks++;
                            }
                        }
                        else if (id == TObjectId.eArcObject)
                        {
                            IPCB_Arc a = p as IPCB_Arc;
                            if (a != null && IsCopper(p))
                            {
                                double w = PcbDraw.ToMM(a.GetState_LineWidth());
                                if (w > 1e-9 && w < minTrack) minTrack = w;
                                tracks++;
                            }
                        }
                        else if (id == TObjectId.eViaObject)
                        {
                            IPCB_Via v = p as IPCB_Via;
                            if (v != null)
                            {
                                double h = PcbDraw.ToMM(v.GetState_HoleSize());
                                if (h > 1e-9 && h < minHole) minHole = h;
                                vias++;
                            }
                        }
                        else if (id == TObjectId.ePadObject)
                        {
                            IPCB_Pad pad = p as IPCB_Pad;
                            if (pad != null)
                            {
                                // Surface-mount pads report a zero hole; only
                                // a drilled pad contributes to the minimum.
                                double h = PcbDraw.ToMM(pad.GetState_HoleSize());
                                if (h > 1e-9 && h < minHole) minHole = h;
                                pads++;
                            }
                        }
                    }
                    catch { /* one unreadable primitive must not lose the whole scan */ }

                    p = it.NextPCBObject();
                }
            }
            catch (Exception ex)
            {
                res.Errors.Add("Board scan failed -- " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                board.BoardIterator_Destroy(ref it);
            }

            st.TrackCount = tracks;
            st.ViaCount = vias;
            st.PadCount = pads;
            if (minTrack < double.MaxValue) st.MinTrackMM = minTrack;
            if (minHole < double.MaxValue) st.MinHoleMM = minHole;
        }

        // Silkscreen and mechanical tracks are not copper and must not drag
        // the "minimum trace width" note down to the width of a text stroke.
        private static bool IsCopper(IPCB_Primitive p)
        {
            try
            {
                TV6_Layer l = p.GetState_Layer();
                if (l == TV6_Layer.eV6_TopLayer || l == TV6_Layer.eV6_BottomLayer) return true;
                // Mid layers are contiguous in the enum between top and bottom.
                int v = (int)l;
                return v > (int)TV6_Layer.eV6_TopLayer && v < (int)TV6_Layer.eV6_BottomLayer;
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------
        // Rules: the tightest clearance and the tightest width constraint
        // ------------------------------------------------------------------
        private static void ReadRules(IPCB_Board board, Stats st, Result res)
        {
            double minGap = double.MaxValue;
            double minWidth = double.MaxValue;

            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(TObjectId.eRuleObject));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);

                IPCB_Rule rule = it.FirstPCBObject() as IPCB_Rule;
                while (rule != null)
                {
                    try
                    {
                        // A disabled rule constrains nothing, and reporting it
                        // would understate what the board is actually allowed
                        // to contain.
                        if (rule.GetState_DRCEnabled())
                        {
                            TRuleKind kind = rule.GetState_RuleKind();

                            if (kind == TRuleKind.eRule_Clearance)
                            {
                                IPCB_ClearanceConstraint c = rule as IPCB_ClearanceConstraint;
                                if (c != null)
                                {
                                    double g = PcbDraw.ToMM(c.GetState_Gap());
                                    if (g > 1e-9 && g < minGap) minGap = g;
                                }
                            }
                            else if (kind == TRuleKind.eRule_MaxMinWidth)
                            {
                                IPCB_MaxMinWidthConstraint mw = rule as IPCB_MaxMinWidthConstraint;
                                if (mw != null)
                                {
                                    // MinWidth is per layer. Top Layer is
                                    // representative for the usual case; a
                                    // layer-specific rule is reported by its
                                    // tightest value across top and bottom.
                                    double v = MinWidthOn(mw, board, "Top Layer");
                                    double v2 = MinWidthOn(mw, board, "Bottom Layer");
                                    if (!double.IsNaN(v2) && (double.IsNaN(v) || v2 < v)) v = v2;
                                    if (!double.IsNaN(v) && v > 1e-9 && v < minWidth) minWidth = v;
                                }
                            }
                        }
                    }
                    catch { /* an exotic rule that will not answer is skipped */ }

                    rule = it.NextPCBObject() as IPCB_Rule;
                }
            }
            catch (Exception ex)
            {
                res.Errors.Add("Rule scan failed -- " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                board.BoardIterator_Destroy(ref it);
            }

            if (minGap < double.MaxValue) st.MinClearanceMM = minGap;
            if (minWidth < double.MaxValue) st.RuleMinWidthMM = minWidth;
        }

        // GetState_MinWidth is keyed by layer, so the layer object has to be
        // resolved from the stack first. Returns NaN when the layer does not
        // exist or the rule will not answer for it.
        private static double MinWidthOn(IPCB_MaxMinWidthConstraint mw, IPCB_Board board, string layerName)
        {
            IV7_Layer l = LayerByName(board, layerName);
            if (l == null) return double.NaN;

            try { return PcbDraw.ToMM(mw.GetState_MinWidth(l)); }
            catch { return double.NaN; }
        }

        private static IV7_Layer LayerByName(IPCB_Board board, string name)
        {
            try
            {
                IPCB_LayerStack stack = board.GetState_LayerStack();
                if (stack == null) return null;
                IPCB_LayerObject lo = stack.First(TLayerClassID.eLayerClass_Electrical);
                int guard = 0;
                while (lo != null && guard++ < 256)
                {
                    if (string.Equals(lo.GetState_LayerName(), name, StringComparison.OrdinalIgnoreCase))
                        return lo.V7_LayerID();
                    lo = stack.Next(TLayerClassID.eLayerClass_Electrical, lo);
                }
            }
            catch { }
            return null;
        }

        // ------------------------------------------------------------------
        // Build the note text
        // ------------------------------------------------------------------
        public static List<string> BuildNotes(Stats st, Options opt)
        {
            List<string> n = new List<string>();
            bool imp = opt.ImperialToo;

            n.Add("1. FABRICATE TO IPC-6012 CLASS " + opt.IpcClass + ".");
            n.Add("2. BOARD SIZE: " + st.WidthMM.ToString("0.00", Inv) + " x " +
                  st.HeightMM.ToString("0.00", Inv) + " MM" +
                  (imp ? "  (" + (st.WidthMM / 25.4).ToString("0.000", Inv) + " x " +
                         (st.HeightMM / 25.4).ToString("0.000", Inv) + " IN)" : "") + ".");
            n.Add("3. LAYER COUNT: " + st.SignalLayers + " COPPER LAYERS.");

            if (st.BoardThicknessMM > 1e-9)
                n.Add("4. FINISHED BOARD THICKNESS: " + Dim(st.BoardThicknessMM, imp).ToUpperInvariant() +
                      " +/-10%.");
            else
                n.Add("4. FINISHED BOARD THICKNESS: PER STACKUP TABLE.");

            if (!double.IsNaN(st.MinTrackMM))
                n.Add("5. MINIMUM TRACE WIDTH ON ARTWORK: " + Dim(st.MinTrackMM, imp).ToUpperInvariant() + ".");
            else
                n.Add("5. MINIMUM TRACE WIDTH ON ARTWORK: NO COPPER TRACKS FOUND.");

            if (!double.IsNaN(st.MinClearanceMM))
                n.Add("6. MINIMUM CLEARANCE PER DESIGN RULES: " +
                      Dim(st.MinClearanceMM, imp).ToUpperInvariant() + ".");
            else
                n.Add("6. MINIMUM CLEARANCE PER DESIGN RULES: NOT SPECIFIED.");

            if (!double.IsNaN(st.MinHoleMM))
                n.Add("7. SMALLEST DRILLED HOLE ON ARTWORK: " + Dim(st.MinHoleMM, imp).ToUpperInvariant() +
                      " FINISHED.");
            else
                n.Add("7. SMALLEST DRILLED HOLE ON ARTWORK: NO DRILLED HOLES.");

            n.Add("8. SURFACE FINISH: " + opt.SurfaceFinish.ToUpperInvariant() + ".");
            n.Add("9. SOLDER MASK: " + opt.SolderMaskColour.ToUpperInvariant() +
                  ", BOTH SIDES, OVER BARE COPPER.");
            n.Add("10. SILKSCREEN: " + opt.SilkscreenColour.ToUpperInvariant() +
                  ", BOTH SIDES. NO SILKSCREEN ON PADS.");
            n.Add("11. ALL HOLES PLATED THROUGH UNLESS NOTED.");
            n.Add("12. ELECTRICAL TEST 100% NETLIST TESTED TO IPC-9252.");
            n.Add("13. NO DESIGN CHANGES WITHOUT WRITTEN APPROVAL.");

            return n;
        }

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

            ReadOutline(board, res.Stats, res);
            ReadStack(board, res.Stats, res);
            Measure(board, res.Stats, res);
            ReadRules(board, res.Stats, res);

            List<string> notes = BuildNotes(res.Stats, opt);
            res.Notes = notes;

            // Width of the region this generator owns, for the clear-and-redraw.
            double widest = PcbDraw.TextWidthMM(opt.Title, opt.TextHeightMM);
            for (int i = 0; i < notes.Count; i++)
                widest = Math.Max(widest, PcbDraw.TextWidthMM(notes[i], opt.TextHeightMM));

            double blockH = opt.LineSpacingMM * (notes.Count + 2);

            pcbServer.PreProcess();
            try
            {
                if (opt.ReplaceExisting)
                    res.PrimitivesRemoved = PcbDraw.ClearArea(board, layer,
                        opt.OriginXMM - 1.0, opt.OriginYMM - blockH - 1.0,
                        opt.OriginXMM + widest + 2.0, opt.OriginYMM + 1.0);

                double y = opt.OriginYMM;

                PcbDraw.Text(pcbServer, board, layer, opt.OriginXMM, y,
                             opt.TextHeightMM * 1.35, opt.StrokeMM * 1.4, opt.Title);
                y -= opt.LineSpacingMM * 1.6;

                for (int i = 0; i < notes.Count; i++)
                {
                    PcbDraw.Text(pcbServer, board, layer, opt.OriginXMM, y,
                                 opt.TextHeightMM, opt.StrokeMM, notes[i]);
                    y -= opt.LineSpacingMM;
                    res.NotesWritten++;
                }
            }
            catch (Exception ex)
            {
                res.Errors.Add(ex.GetType().Name + " -- " + ex.Message);
                Log.Exception("AssemblyNotes.Generate", ex);
            }
            finally
            {
                pcbServer.PostProcess();
            }

            board.ViewManager_FullUpdate();
            Log.Write("AssemblyNotes: " + res.NotesWritten + " note(s) on " + opt.LayerName);
            return res;
        }
    }
}
