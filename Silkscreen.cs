// Silkscreen.cs
//
//   CentreDesignators  -- move each designator onto its component's centre
//   AutoPosition       -- hand manual designators back to Altium's autoposition
//   ShowHide           -- turn designators and comments on or off in bulk
//   Normalise          -- one text height and stroke width across the board
//
// WHY THE COMPONENT CENTRE IS NOT THE COMPONENT'S X/Y. A component's location
// is its ANCHOR, which is wherever pin 1 or the library origin happens to
// sit, and on plenty of footprints that is nowhere near the middle of the
// part. Centring a designator on the anchor puts it off to one side and looks
// worse than leaving it alone. The centre used here is the middle of
// BoundingRectangleNoNameComment -- the body of the part with the designator
// and comment text excluded, which is the only rectangle that does not move
// as a consequence of what this function is about to do.
//
// EVERY EDIT IS WRAPPED IN BeginModify/EndModify with try/finally. A
// component left mid-modify blocks File > Save afterwards with no useful
// message, and the only way out is to close the document and lose the work.

using DXP;
using PCB;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace AltiumSpike
{
    public static class Silkscreen
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static double ToMM(int coord) { return EDP.Utils.CoordToMMs(coord); }
        private static int ToCoord(double mm) { return EDP.Utils.MMsToCoord(mm); }

        public sealed class Result
        {
            public int Considered, Changed, Skipped;
            public List<string> Notes = new List<string>();
            public List<string> Errors = new List<string>();
        }

        private static List<IPCB_Component> Components(IPCB_Board board, bool onlySelection, List<string> errors)
        {
            List<IPCB_Component> comps = new List<IPCB_Component>();

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
                        if (!onlySelection || c.GetState_Selected()) comps.Add(c);
                    }
                    catch { }
                    c = it.NextPCBObject() as IPCB_Component;
                }
            }
            catch (Exception ex)
            {
                errors.Add("Component scan failed -- " + ex.GetType().Name + ": " + ex.Message);
            }
            finally { board.BoardIterator_Destroy(ref it); }

            return comps;
        }

        // ==================================================================
        // Centre designators
        // ==================================================================
        public static Result CentreDesignators(IPCB_ServerInterface pcbServer, IPCB_Board board,
                                               bool onlySelection, bool alsoComment)
        {
            Result res = new Result();
            List<IPCB_Component> comps = Components(board, onlySelection, res.Errors);
            res.Considered = comps.Count;
            if (comps.Count == 0)
            {
                res.Errors.Add(onlySelection ? "No components are selected." : "No components on this board.");
                return res;
            }

            pcbServer.PreProcess();
            try
            {
                for (int i = 0; i < comps.Count; i++)
                {
                    IPCB_Component c = comps[i];
                    bool opened = false;
                    try
                    {
                        // The body of the part, with the designator and comment
                        // text excluded -- see the note at the top of the file.
                        CoordRect body = c.BoundingRectangleNoNameComment();
                        double cx = (ToMM(body.GetX1()) + ToMM(body.GetX2())) / 2.0;
                        double cy = (ToMM(body.GetY1()) + ToMM(body.GetY2())) / 2.0;

                        c.BeginModify();
                        opened = true;

                        IPCB_Text name = c.GetState_Name();
                        if (name != null)
                        {
                            // A designator still on autoposition will snap
                            // straight back, so it is released first.
                            c.SetState_NameAutoPos(TTextAutoposition.eAutoPos_Manual);

                            CoordRect nb = name.BoundingRectangle();
                            double w = ToMM(nb.GetX2() - nb.GetX1());
                            double h = ToMM(nb.GetY2() - nb.GetY1());

                            // X/Y on a text object is its lower-left, not its
                            // middle, so half the extent comes back off.
                            name.SetState_XLocation(ToCoord(cx - w / 2.0));
                            name.SetState_YLocation(ToCoord(cy - h / 2.0));
                            res.Changed++;
                        }
                        else res.Skipped++;

                        if (alsoComment)
                        {
                            IPCB_Text comment = c.GetState_Comment();
                            if (comment != null)
                            {
                                c.SetState_CommentAutoPos(TTextAutoposition.eAutoPos_Manual);
                                CoordRect cb = comment.BoundingRectangle();
                                double w = ToMM(cb.GetX2() - cb.GetX1());
                                double h = ToMM(cb.GetY2() - cb.GetY1());
                                comment.SetState_XLocation(ToCoord(cx - w / 2.0));
                                comment.SetState_YLocation(ToCoord(cy - h / 2.0));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        res.Errors.Add("Component " + i + ": " + ex.GetType().Name + " -- " + ex.Message);
                        res.Skipped++;
                    }
                    finally
                    {
                        // Without this a thrown exception leaves the component
                        // mid-modify and File > Save stops working.
                        if (opened) { try { c.EndModify(); } catch { } }
                    }
                }
            }
            finally { pcbServer.PostProcess(); }

            board.ViewManager_FullUpdate();
            res.Notes.Add("Designators were switched to manual positioning; autoposition would pull them back.");
            Log.Write("Silkscreen.CentreDesignators: " + res.Changed + " moved");
            return res;
        }

        // ==================================================================
        // Autoposition
        // ==================================================================
        public static Result AutoPosition(IPCB_ServerInterface pcbServer, IPCB_Board board,
                                          bool onlySelection, TTextAutoposition where, bool alsoComment)
        {
            Result res = new Result();
            List<IPCB_Component> comps = Components(board, onlySelection, res.Errors);
            res.Considered = comps.Count;
            if (comps.Count == 0)
            {
                res.Errors.Add(onlySelection ? "No components are selected." : "No components on this board.");
                return res;
            }

            pcbServer.PreProcess();
            try
            {
                for (int i = 0; i < comps.Count; i++)
                {
                    IPCB_Component c = comps[i];
                    bool opened = false;
                    try
                    {
                        c.BeginModify();
                        opened = true;
                        c.SetState_NameAutoPos(where);
                        if (alsoComment) c.SetState_CommentAutoPos(where);
                        res.Changed++;
                    }
                    catch (Exception ex)
                    {
                        res.Errors.Add("Component " + i + ": " + ex.GetType().Name + " -- " + ex.Message);
                        res.Skipped++;
                    }
                    finally { if (opened) { try { c.EndModify(); } catch { } } }
                }
            }
            finally { pcbServer.PostProcess(); }

            board.ViewManager_FullUpdate();
            Log.Write("Silkscreen.AutoPosition: " + res.Changed + " set to " + where);
            return res;
        }

        // ==================================================================
        // Show / hide
        // ==================================================================
        public static Result ShowHide(IPCB_ServerInterface pcbServer, IPCB_Board board,
                                      bool onlySelection, bool show, bool designators, bool comments)
        {
            Result res = new Result();

            if (!designators && !comments)
            { res.Errors.Add("Choose designators, comments, or both."); return res; }

            List<IPCB_Component> comps = Components(board, onlySelection, res.Errors);
            res.Considered = comps.Count;
            if (comps.Count == 0)
            {
                res.Errors.Add(onlySelection ? "No components are selected." : "No components on this board.");
                return res;
            }

            pcbServer.PreProcess();
            try
            {
                for (int i = 0; i < comps.Count; i++)
                {
                    IPCB_Component c = comps[i];
                    bool opened = false;
                    try
                    {
                        c.BeginModify();
                        opened = true;
                        if (designators) c.SetState_NameOn(show);
                        if (comments) c.SetState_CommentOn(show);
                        res.Changed++;
                    }
                    catch (Exception ex)
                    {
                        res.Errors.Add("Component " + i + ": " + ex.GetType().Name + " -- " + ex.Message);
                        res.Skipped++;
                    }
                    finally { if (opened) { try { c.EndModify(); } catch { } } }
                }
            }
            finally { pcbServer.PostProcess(); }

            board.ViewManager_FullUpdate();
            Log.Write("Silkscreen.ShowHide: " + res.Changed + " component(s), show=" + show);
            return res;
        }

        // ==================================================================
        // Normalise text size
        // ==================================================================
        public static Result Normalise(IPCB_ServerInterface pcbServer, IPCB_Board board,
                                       bool onlySelection, double heightMM, double strokeMM, bool alsoComment)
        {
            Result res = new Result();

            if (heightMM <= 0.0) { res.Errors.Add("Height must be greater than 0."); return res; }
            if (strokeMM <= 0.0) { res.Errors.Add("Stroke width must be greater than 0."); return res; }

            // A stroke wider than about a fifth of the height fills the
            // letterforms in and the text stops being readable once it is
            // screened onto a board.
            if (strokeMM > heightMM * 0.25)
                res.Notes.Add("Stroke is more than a quarter of the height; the text will look heavy " +
                              "and may fill in on the screen.");

            List<IPCB_Component> comps = Components(board, onlySelection, res.Errors);
            res.Considered = comps.Count;
            if (comps.Count == 0)
            {
                res.Errors.Add(onlySelection ? "No components are selected." : "No components on this board.");
                return res;
            }

            pcbServer.PreProcess();
            try
            {
                for (int i = 0; i < comps.Count; i++)
                {
                    IPCB_Component c = comps[i];
                    bool opened = false;
                    try
                    {
                        c.BeginModify();
                        opened = true;

                        IPCB_Text name = c.GetState_Name();
                        if (name != null)
                        {
                            name.SetState_Size(ToCoord(heightMM));
                            name.SetState_Width(ToCoord(strokeMM));
                            res.Changed++;
                        }

                        if (alsoComment)
                        {
                            IPCB_Text comment = c.GetState_Comment();
                            if (comment != null)
                            {
                                comment.SetState_Size(ToCoord(heightMM));
                                comment.SetState_Width(ToCoord(strokeMM));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        res.Errors.Add("Component " + i + ": " + ex.GetType().Name + " -- " + ex.Message);
                        res.Skipped++;
                    }
                    finally { if (opened) { try { c.EndModify(); } catch { } } }
                }
            }
            finally { pcbServer.PostProcess(); }

            board.ViewManager_FullUpdate();
            Log.Write("Silkscreen.Normalise: " + res.Changed + " designator(s) resized");
            return res;
        }
    }
}
