// SpikeModule.cs
//
// Altium extension module. The three-stage RunSpike probe that proved
// the toolchain was removed on 21-Sep-2026 once it had done its job;
// git-free history of it is in SpikeModule.cs.prespike. What remains is
// the real tool: the ExportBoardData command.
//
// (original header) Minimal feasibility test -- goal
// is purely to confirm the toolchain (Altium loads this DLL, the SDK
// references resolve, PCB objects are reachable from a live document)
// before investing in porting the 5 DelphiScript tools.
//
// STATUS 21-Sep-2026, from DXP_Startup.log:
//   "Starting server AltiumSpike" / "Server AltiumSpike started" -- the
//   DLL loads and this module is constructed, once per click of the menu
//   item. So loading is SOLVED. What is unproven is whether RunSpike is
//   ever dispatched, and whether a WPF MessageBox can display from inside
//   Altium's host process. Everything below therefore logs to
//   Desktop\AltiumSpike.log first and shows a window second.
//
// Contract read from assembly metadata (not inferred):
//   DXP.ServerModule : IServerModule        ctor(IClient, String)
//     protected abstract IServerDocument NewDocumentInstance(String, String)
//     protected virtual  void InitializeCommands()
//     .CommandLauncher -> ICommandLauncher
//   DXP.CommandLauncher : ICommandLauncher
//     RegisterCommand(String, CommandProc, GetStateProc)
//   delegate CommandProc  (IServerDocumentView, ref String)
//   delegate GetStateProc (IServerDocumentView, ref String,
//                          ref Boolean, ref Boolean, ref Boolean,
//                          ref String, ref String)

using DXP;
using PCB;
using System;
using System.Windows;

namespace AltiumSpike
{
    public class SpikeModule : ServerModule
    {
        private readonly IClient client;

        public SpikeModule(IClient client)
            : base(client, "AltiumSpike")
        {
            this.client = client;
            Log.Write("SpikeModule constructed (module name 'AltiumSpike')");
        }

        // ServerModule's ONLY abstract member. This module contributes no
        // document types of its own, so there is no instance to hand back.
        protected override IServerDocument NewDocumentInstance(string kind, string fileName)
        {
            Log.Write("NewDocumentInstance(" + kind + ", " + fileName + ") -> null");
            return null;
        }

        protected override void InitializeCommands()
        {
            Log.Write("InitializeCommands entered");
            try
            {
                base.InitializeCommands();
            }
            catch (Exception ex)
            {
                Log.Exception("base.InitializeCommands", ex);
            }

            object raw = null;
            try { raw = this.CommandLauncher; }
            catch (Exception ex) { Log.Exception("get CommandLauncher", ex); }

            Log.Write("CommandLauncher is " +
                (raw == null ? "NULL" : raw.GetType().FullName));

            DXP.CommandLauncher launcher = raw as DXP.CommandLauncher;
            if (launcher == null)
            {
                Log.Write("FATAL: launcher is not a DXP.CommandLauncher -- cannot register.");
                return;
            }

            // We do not know which spelling Altium dispatches: the bare command
            // name from the .Ins, or the fully qualified PLID from the .rcs.
            // Registering both costs nothing and removes the guess.
            // Each command is registered under both spellings because Altium
            // may dispatch the bare .Ins name or the fully qualified PLID.
            // The window is the primary entry point now; the individual
            // commands stay registered so they remain scriptable and so an
            // old .rcs or a shortcut does not break.
            Register(launcher, "ShowWindow", RunShowWindow);
            Register(launcher, "AltiumSpike:ShowWindow", RunShowWindow);

            Register(launcher, "ExportBoardData", RunExport);
            Register(launcher, "AltiumSpike:ExportBoardData", RunExport);
            Register(launcher, "ExportJlcBom", RunExportJlc);
            Register(launcher, "AltiumSpike:ExportJlcBom", RunExportJlc);

            Register(launcher, "PlaceObjects", RunPlaceObjects);
            Register(launcher, "AltiumSpike:PlaceObjects", RunPlaceObjects);
            Register(launcher, "PlacePours", RunPlacePours);
            Register(launcher, "AltiumSpike:PlacePours", RunPlacePours);
            Register(launcher, "PlaceRegions", RunPlaceRegions);
            Register(launcher, "AltiumSpike:PlaceRegions", RunPlaceRegions);
            Register(launcher, "LockComponents", RunLock);
            Register(launcher, "AltiumSpike:LockComponents", RunLock);
            Register(launcher, "UnlockComponents", RunUnlock);
            Register(launcher, "AltiumSpike:UnlockComponents", RunUnlock);

            Log.Write("InitializeCommands finished");
        }


        private void Register(DXP.CommandLauncher launcher, string name, DXP.CommandProc proc)
        {
            try
            {
                launcher.RegisterCommand(name, proc, GetSpikeState);
                Log.Write("Registered command '" + name + "'");
            }
            catch (Exception ex)
            {
                Log.Exception("RegisterCommand('" + name + "')", ex);
            }
        }

        // If Altium asks whether the command is available and we answer badly,
        // the click is swallowed. Force it enabled and visible.
        private void GetSpikeState(IServerDocumentView view, ref string parameters,
                                   ref bool enabled, ref bool checkedState, ref bool visible,
                                   ref string caption, ref string description)
        {
            enabled = true;
            visible = true;
            Log.Write("GetSpikeState queried -> enabled=true visible=true");
        }




        // --- the single window that replaces the menus ---
        private void RunShowWindow(IServerDocumentView view, ref string parameters)
        {
            Log.Write(">>> ShowWindow DISPATCHED");
            try
            {
                SpikeWindow.ShowSingleton(client);
            }
            catch (Exception ex)
            {
                Log.Exception("RunShowWindow", ex);
                Log.Say("AltiumSpike", "Could not open the window: " +
                    ex.GetType().Name + " -- " + ex.Message);
            }
            Log.Write("<<< ShowWindow returned");
        }

        // ---------------- Import Board Data commands ----------------
        //
        // All five share the same shape: get the board, pick a CSV, run the
        // batch, report. Board acquisition is factored out so a failure to
        // reach the PCB server is reported identically everywhere.

        private bool TryGetBoard(string title, out IPCB_ServerInterface pcbServer, out IPCB_Board board)
        {
            pcbServer = null;
            board = null;
            try
            {
                client.StartServer("PCB");
                pcbServer = client.GetServerModuleByName("PCB") as IPCB_ServerInterface;
            }
            catch (Exception ex)
            {
                Log.Exception(title + "/PCBServer", ex);
                Log.Say(title, "Could not obtain PCBServer: " + ex.Message);
                return false;
            }

            if (pcbServer == null)
            {
                Log.Say(title, "Could not obtain PCBServer.");
                return false;
            }

            board = pcbServer.GetCurrentPCBBoard();
            if (board == null)
            {
                Log.Say(title, "No active PCB document. Open the target PCB and try again.");
                return false;
            }
            return true;
        }

        private void RunImport(string title, string suggestedCsv,
                               Func<IPCB_ServerInterface, IPCB_Board, string, BoardImport.Result> action,
                               string noun)
        {
            Log.Write(">>> " + title + " DISPATCHED");
            try
            {
                IPCB_ServerInterface pcbServer;
                IPCB_Board board;
                if (!TryGetBoard(title, out pcbServer, out board)) return;

                string csv = CsvIo.PickCsv(title, suggestedCsv);
                if (csv == null) { Log.Write(title + ": cancelled at file picker"); return; }

                Log.Write(title + ": reading " + csv);
                BoardImport.Result r = action(pcbServer, board, csv);
                Log.Say(title, r.Summarise(noun));
            }
            catch (Exception ex)
            {
                Log.Exception(title, ex);
                Log.Say(title, "FAILED: " + ex.GetType().Name + " -- " + ex.Message);
            }
            Log.Write("<<< " + title + " returned");
        }

        private void RunPlaceObjects(IServerDocumentView view, ref string parameters)
        {
            RunImport("Place Objects From CSV", "objects.csv", BoardImport.PlaceObjects, "object(s)");
        }

        private void RunPlacePours(IServerDocumentView view, ref string parameters)
        {
            RunImport("Place Polygon Pours", "pours.csv", BoardImport.PlacePours, "polygon pour(s)");
        }

        private void RunPlaceRegions(IServerDocumentView view, ref string parameters)
        {
            RunImport("Place Regions From CSV", "regions.csv", BoardImport.PlaceRegions, "region(s)");
        }

        private void RunLock(IServerDocumentView view, ref string parameters)
        {
            RunImport("Lock Components", "locked_components.csv",
                (s, b, p) => BoardImport.SetLock(s, b, p, true), "component(s) locked");
        }

        private void RunUnlock(IServerDocumentView view, ref string parameters)
        {
            RunImport("Unlock Components", "locked_components.csv",
                (s, b, p) => BoardImport.SetLock(s, b, p, false), "component(s) unlocked");
        }


        // --- JLCPCB / LCSC assembly export: BOM + CPL + missing-parts list ---
        private void RunExportJlc(IServerDocumentView view, ref string parameters)
        {
            const string title = "JLCPCB BOM + CPL";
            Log.Write(">>> " + title + " DISPATCHED");
            try
            {
                IPCB_ServerInterface pcbServer;
                IPCB_Board board;
                if (!TryGetBoard(title, out pcbServer, out board)) return;

                string folder = Settings.ResolveOutputFolder();
                if (folder == null) { Log.Write(title + ": folder selection cancelled"); return; }

                JlcExport.JlcResult r = JlcExport.Export(pcbServer, board, folder);

                string msg = "Exported to " + folder + ":\n" +
                             "  bom_jlcpcb.csv -- " + r.Parts + " distinct part(s)\n" +
                             "  cpl_jlcpcb.csv -- " + r.Placements + " placement(s)\n" +
                             "  bom_missing_lcsc.csv -- " + r.MissingLcsc.Count +
                             " component(s) EXCLUDED (no LCSC number)";

                if (r.MissingLcsc.Count > 0)
                {
                    msg += "\n\nThose components are in neither the BOM nor the CPL -- " +
                           "JLCPCB can neither quote nor place a part with no LCSC number. " +
                           "Check bom_missing_lcsc.csv and add part numbers for anything " +
                           "that should be assembled.\n\nParameter names found on this board:\n  " +
                           (r.ParameterNamesSeen.Count == 0
                               ? "(none -- these components carry no parameters)"
                               : string.Join(", ", r.ParameterNamesSeen));
                }

                Log.Say(title, msg);
            }
            catch (Exception ex)
            {
                Log.Exception(title, ex);
                Log.Say(title, "FAILED: " + ex.GetType().Name + " -- " + ex.Message);
            }
            Log.Write("<<< " + title + " returned");
        }

        // --- ExportBoardData: the C# equivalent of ExportBoardData.pas ---
        private void RunExport(IServerDocumentView view, ref string parameters)
        {
            Log.Write(">>> RunExport DISPATCHED");
            try
            {
                IPCB_ServerInterface pcbServer;
                try
                {
                    client.StartServer("PCB");
                    pcbServer = client.GetServerModuleByName("PCB") as IPCB_ServerInterface;
                }
                catch (Exception ex)
                {
                    Log.Exception("RunExport/PCBServer", ex);
                    Log.Say("Export Board Data", "Could not obtain PCBServer: " + ex.Message);
                    return;
                }

                if (pcbServer == null)
                {
                    Log.Say("Export Board Data", "Could not obtain PCBServer.");
                    return;
                }

                IPCB_Board board = pcbServer.GetCurrentPCBBoard();
                if (board == null)
                {
                    Log.Say("Export Board Data",
                        "No active PCB document. Open the target PCB and try again.");
                    return;
                }

                string folder = Settings.ResolveOutputFolder();
                if (folder == null)
                {
                    Log.Write("RunExport: folder selection cancelled");
                    return;
                }

                int footprints = BoardExport.FootprintSizes(board, folder);
                int pads = BoardExport.PadNets(board, folder);
                int geometry = BoardExport.BoardGeometry(board, folder);

                Log.Say("Export Board Data",
                    "Exported to " + folder + ":\n" +
                    "  footprint_sizes.csv -- " + footprints + " component(s), incl. Locked column\n" +
                    "  pad_nets.csv -- " + pads + " pad(s)\n" +
                    "  board_geometry.csv -- " + geometry + " row(s) (outline vertices + mounting holes)");
            }
            catch (Exception ex)
            {
                Log.Exception("RunExport", ex);
                Log.Say("Export Board Data", "FAILED: " + ex.GetType().Name + " -- " + ex.Message);
            }
            Log.Write("<<< RunExport returned");
        }

    }
}
