// ViaFence.cs
//
// Places a via fence (a via shield / stitching wall) alongside the tracks and
// arcs currently SELECTED in the PCB editor, at a fixed pitch and a fixed
// offset from the trace centreline.
//
// This is the thing Altium's native via stitching will not do. Stitching
// floods a polygon at a grid pitch; it has no notion of "follow this RF
// trace at 0.4 mm on both sides every 1.2 mm". That distinction matters: a
// fence is only a fence if the spacing is deterministic along the signal,
// because the leakage being suppressed is a function of the gap between
// adjacent vias, not of the average via density.
//
// The geometry lives in FenceGeometry.cs, which has no Altium dependency and
// is unit-tested. This file does three things and nothing else: read the
// selection, ask FenceGeometry where the vias go, and place them.
//
// WORKFLOW
//   1. Select the trace segments to shield in the PCB editor.
//   2. Set pitch / offset / via size / net in the AltiumSpike window.
//   3. Fence selected traces.
//
// NO CLEARANCE CHECK -- BY DESIGN. Every candidate via is placed. Nothing
// here consults the board's clearance rules or tests for existing copper, so
// a fence run through a dense area WILL produce violations. That is the
// intended trade: placement is fast and completely predictable, and Design ->
// Rule Check is the authority on what is legal. The result summary says so
// on every run, not just in this comment.

using DXP;
using PCB;
using System;
using System.Collections.Generic;

namespace AltiumSpike
{
    public static class ViaFence
    {
        private static double ToMM(int coord) { return EDP.Utils.CoordToMMs(coord); }
        private static int ToCoord(double mm) { return EDP.Utils.MMsToCoord(mm); }

        // Kept as an alias so callers read naturally; the real type is the
        // Altium-free one so the geometry stays testable.
        public sealed class Result
        {
            public int Placed;
            public int SegmentsUsed;
            public int SelectedObjects;
            public int SkippedNonTrace;
            public int SkippedDuplicate;
            public int SkippedInnerArc;
            public bool HitCap;
            public List<string> Errors = new List<string>();

            public string Summarise()
            {
                string s = "Placed " + Placed + " via(s) along " + SegmentsUsed +
                           " selected segment(s).\n";
                s += "Selection held " + SelectedObjects + " object(s)";
                if (SkippedNonTrace > 0) s += ", " + SkippedNonTrace + " not a track or arc";
                s += ".\n";
                if (SkippedDuplicate > 0)
                    s += SkippedDuplicate + " candidate(s) merged at segment junctions.\n";
                if (SkippedInnerArc > 0)
                    s += "Inner wall skipped on " + SkippedInnerArc + " arc(s) (offset >= radius).\n";
                if (HitCap)
                    s += "STOPPED at the " + FenceGeometry.MaxVias +
                         " via limit -- check the pitch, it may be too small.\n";
                if (Errors.Count > 0)
                {
                    s += "Errors (" + Errors.Count + "):\n";
                    foreach (string e in Errors) s += "  " + e + "\n";
                }
                s += "No clearance check was performed -- run Design > Rule Check.";
                return s;
            }
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

        public static Result Fence(IPCB_ServerInterface pcbServer, IPCB_Board board, FenceOptions opt)
        {
            Result r = new Result();

            string bad = opt.Validate();
            if (bad != null) { r.Errors.Add(bad); return r; }

            IPCB_Net net = FindNet(board, opt.NetName);
            if (net == null)
            { r.Errors.Add("Net \"" + (opt.NetName ?? "") + "\" not found on this board."); return r; }

            int selCount;
            try { selCount = board.GetState_SelectecObjectCount(); }
            catch (Exception ex)
            {
                r.Errors.Add("Could not read the selection -- " + ex.GetType().Name + ": " + ex.Message);
                return r;
            }
            r.SelectedObjects = selCount;
            Log.Write("ViaFence: selection holds " + selCount + " object(s)");

            if (selCount == 0)
            { r.Errors.Add("Nothing is selected. Select the trace segments to shield first."); return r; }

            // Snapshot the selection BEFORE building anything. Adding vias can
            // disturb the selection set, and walking it by index while it
            // changes underneath would skip segments.
            List<IPCB_Primitive> picked = new List<IPCB_Primitive>();
            for (int i = 0; i < selCount; i++)
            {
                try
                {
                    IPCB_Primitive p = board.GetState_SelectecObject(i);
                    if (p == null) continue;
                    TObjectId id = p.GetState_ObjectID();
                    if (id == TObjectId.eTrackObject || id == TObjectId.eArcObject) picked.Add(p);
                    else r.SkippedNonTrace++;
                }
                catch (Exception ex)
                {
                    r.Errors.Add("Selection item " + i + ": " + ex.GetType().Name + " -- " + ex.Message);
                }
            }

            if (picked.Count == 0)
            {
                r.Errors.Add("The selection contains no tracks or arcs (" +
                             r.SkippedNonTrace + " other object(s) ignored).");
                return r;
            }

            // ---- geometry first, board second ----
            FenceGeometry.Builder builder = new FenceGeometry.Builder(opt);
            for (int i = 0; i < picked.Count; i++)
            {
                try
                {
                    IPCB_Primitive p = picked[i];
                    if (p.GetState_ObjectID() == TObjectId.eTrackObject)
                    {
                        IPCB_Track t = p as IPCB_Track;
                        if (t == null) continue;
                        builder.AddTrack(ToMM(t.GetState_X1()), ToMM(t.GetState_Y1()),
                                         ToMM(t.GetState_X2()), ToMM(t.GetState_Y2()));
                    }
                    else
                    {
                        IPCB_Arc a = p as IPCB_Arc;
                        if (a == null) continue;
                        builder.AddArc(ToMM(a.GetState_CenterX()), ToMM(a.GetState_CenterY()),
                                       ToMM(a.GetState_Radius()),
                                       a.GetState_StartAngle(), a.GetState_EndAngle());
                    }
                    r.SegmentsUsed++;
                }
                catch (Exception ex)
                {
                    r.Errors.Add("Segment " + i + ": " + ex.GetType().Name + " -- " + ex.Message);
                    Log.Exception("ViaFence segment " + i, ex);
                }
            }

            r.SkippedDuplicate = builder.SkippedDuplicate;
            r.SkippedInnerArc = builder.SkippedInnerArc;
            r.HitCap = builder.HitCap;

            IList<FenceCandidate> pts = builder.Points;
            Log.Write("ViaFence: " + pts.Count + " candidate position(s) from " +
                      r.SegmentsUsed + " segment(s)");

            // ---- placement, in one transaction ----
            IPCB_LayerUtils lu = pcbServer.LayerUtils();
            pcbServer.PreProcess();
            try
            {
                for (int i = 0; i < pts.Count; i++)
                {
                    try
                    {
                        IPCB_Via via = pcbServer.PCBObjectFactory(
                            TObjectId.eViaObject, TDimensionKind.eNoDimension,
                            TObjectCreationMode.eCreate_Default) as IPCB_Via;

                        via.SetState_XLocation(ToCoord(pts[i].X));
                        via.SetState_YLocation(ToCoord(pts[i].Y));
                        via.SetState_HoleSize(ToCoord(opt.HoleSizeMM));
                        via.SetState_Size(ToCoord(opt.ViaDiameterMM));
                        via.SetState_LowLayer(lu.FromString("Top Layer"));
                        via.SetState_HighLayer(lu.FromString("Bottom Layer"));
                        via.SetState_Net(net);

                        board.AddPCBObject(via);
                        r.Placed++;
                    }
                    catch (Exception ex)
                    {
                        r.Errors.Add("Via " + i + ": " + ex.GetType().Name + " -- " + ex.Message);
                        Log.Exception("ViaFence via " + i, ex);
                    }
                }
            }
            finally
            {
                pcbServer.PostProcess();
            }

            board.ViewManager_FullUpdate();
            Log.Write("ViaFence: placed " + r.Placed + " via(s) along " + r.SegmentsUsed + " segment(s)");
            return r;
        }
    }
}
