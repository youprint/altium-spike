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

            Register(launcher, "ExportNetLengths", RunExportNetLengths);
            Register(launcher, "AltiumSpike:ExportNetLengths", RunExportNetLengths);
            Register(launcher, "ViaFence", RunViaFence);
            Register(launcher, "AltiumSpike:ViaFence", RunViaFence);

            Register(launcher, "StackupTable", RunStackupTable);
            Register(launcher, "AltiumSpike:StackupTable", RunStackupTable);
            Register(launcher, "AssemblyNotes", RunAssemblyNotes);
            Register(launcher, "AltiumSpike:AssemblyNotes", RunAssemblyNotes);
            Register(launcher, "PackageRelease", RunPackageRelease);
            Register(launcher, "AltiumSpike:PackageRelease", RunPackageRelease);

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

        // --- shared PCB acquisition for the two high-speed commands ---
        private bool TryGetPcb(string title, out IPCB_ServerInterface pcbServer, out IPCB_Board board)
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

            if (pcbServer == null) { Log.Say(title, "Could not obtain PCBServer."); return false; }

            board = pcbServer.GetCurrentPCBBoard();
            if (board == null)
            {
                Log.Say(title, "No active PCB document. Open the target PCB and try again.");
                return false;
            }
            return true;
        }

        // --- ExportNetLengths: per-net and per-pin-pair length and delay ---
        private void RunExportNetLengths(IServerDocumentView view, ref string parameters)
        {
            Log.Write(">>> RunExportNetLengths DISPATCHED");
            const string title = "Export Net Lengths";
            try
            {
                IPCB_ServerInterface pcbServer;
                IPCB_Board board;
                if (!TryGetPcb(title, out pcbServer, out board)) return;

                string folder = Settings.ResolveOutputFolder();
                if (folder == null) { Log.Write("RunExportNetLengths: folder selection cancelled"); return; }

                NetLengths.Result res = NetLengths.Export(board, folder);

                string msg = "Exported to " + folder + ":\n" +
                             "  net_lengths.csv -- " + res.NetRows + " net(s)\n" +
                             "  pin_pair_lengths.csv -- " + res.PinPairRows + " pin pair(s)";
                foreach (string n in res.Notes) msg += "\n  note: " + n;
                Log.Say(title, msg);
            }
            catch (Exception ex)
            {
                Log.Exception("RunExportNetLengths", ex);
                Log.Say(title, "FAILED: " + ex.GetType().Name + " -- " + ex.Message);
            }
            Log.Write("<<< RunExportNetLengths returned");
        }

        // --- ViaFence: fence the current selection using the REMEMBERED
        //     parameters from the window.
        //
        //     There is deliberately no prompt here. The menu route exists so
        //     a fence can be repeated on a new selection without reaching for
        //     the window, which is the common case once pitch and offset are
        //     settled; anyone who needs to change them opens the window,
        //     where the fields are. Defaults match FenceOptions so a
        //     first run before the window has ever been opened still behaves.
        private void RunViaFence(IServerDocumentView view, ref string parameters)
        {
            Log.Write(">>> RunViaFence DISPATCHED");
            const string title = "Via Fence";
            try
            {
                IPCB_ServerInterface pcbServer;
                IPCB_Board board;
                if (!TryGetPcb(title, out pcbServer, out board)) return;

                FenceOptions opt = new FenceOptions();
                opt.PitchMM = Num(Settings.GetValue("FencePitch", "1.0"), 1.0);
                opt.OffsetMM = Num(Settings.GetValue("FenceOffset", "0.5"), 0.5);
                opt.ViaDiameterMM = Num(Settings.GetValue("FenceDia", "0.6"), 0.6);
                opt.HoleSizeMM = Num(Settings.GetValue("FenceHole", "0.3"), 0.3);
                opt.NetName = Settings.GetValue("FenceNet", "GND");
                opt.LeftSide = Settings.GetValue("FenceLeft", "1") != "0";
                opt.RightSide = Settings.GetValue("FenceRight", "1") != "0";

                ViaFence.Result r = ViaFence.Fence(pcbServer, board, opt);
                Log.Say(title, r.Summarise());
            }
            catch (Exception ex)
            {
                Log.Exception("RunViaFence", ex);
                Log.Say(title, "FAILED: " + ex.GetType().Name + " -- " + ex.Message);
            }
            Log.Write("<<< RunViaFence returned");
        }

        // --- StackupTable / AssemblyNotes / PackageRelease ---
        //
        // Like ViaFence, these run from the parameters the window remembered.
        // The window is where you set them up; the commands are for repeating
        // the same operation without opening it, which is what you want when
        // the settings have already been decided for a project.

        private void RunStackupTable(IServerDocumentView view, ref string parameters)
        {
            Log.Write(">>> RunStackupTable DISPATCHED");
            const string title = "Layer Stackup Table";
            try
            {
                IPCB_ServerInterface pcbServer;
                IPCB_Board board;
                if (!TryGetPcb(title, out pcbServer, out board)) return;

                StackupTable.Options opt = new StackupTable.Options();
                opt.LayerName = Settings.GetValue("StackLayer", "Drill Drawing");
                opt.OriginXMM = Num(Settings.GetValue("StackX", "10.0"), 10.0);
                opt.OriginYMM = Num(Settings.GetValue("StackY", "10.0"), 10.0);
                opt.TextHeightMM = Num(Settings.GetValue("StackTextH", "1.2"), 1.2);
                opt.ImperialToo = Settings.GetValue("StackImperial", "1") != "0";
                opt.ReplaceExisting = Settings.GetValue("StackReplace", "1") != "0";

                Log.Say(title, StackupTable.Generate(pcbServer, board, opt).Summarise());
            }
            catch (Exception ex)
            {
                Log.Exception("RunStackupTable", ex);
                Log.Say(title, "FAILED: " + ex.GetType().Name + " -- " + ex.Message);
            }
            Log.Write("<<< RunStackupTable returned");
        }

        private void RunAssemblyNotes(IServerDocumentView view, ref string parameters)
        {
            Log.Write(">>> RunAssemblyNotes DISPATCHED");
            const string title = "Assembly Notes";
            try
            {
                IPCB_ServerInterface pcbServer;
                IPCB_Board board;
                if (!TryGetPcb(title, out pcbServer, out board)) return;

                AssemblyNotes.Options opt = new AssemblyNotes.Options();
                opt.LayerName = Settings.GetValue("NotesLayer", "Mechanical 1");
                opt.OriginXMM = Num(Settings.GetValue("NotesX", "10.0"), 10.0);
                opt.OriginYMM = Num(Settings.GetValue("NotesY", "10.0"), 10.0);
                opt.SurfaceFinish = Settings.GetValue("NotesFinish", "ENIG");
                opt.IpcClass = Settings.GetValue("NotesIpc", "2");
                opt.ReplaceExisting = Settings.GetValue("NotesReplace", "1") != "0";

                Log.Say(title, AssemblyNotes.Generate(pcbServer, board, opt).Summarise());
            }
            catch (Exception ex)
            {
                Log.Exception("RunAssemblyNotes", ex);
                Log.Say(title, "FAILED: " + ex.GetType().Name + " -- " + ex.Message);
            }
            Log.Write("<<< RunAssemblyNotes returned");
        }

        private void RunPackageRelease(IServerDocumentView view, ref string parameters)
        {
            Log.Write(">>> RunPackageRelease DISPATCHED");
            const string title = "Package Release";
            try
            {
                ReleaseBundle.Options opt = new ReleaseBundle.Options();
                opt.ProjectName = Settings.GetValue("RelProject", "");
                opt.Revision = Settings.GetValue("RelRev", "RevA");
                opt.OutJobPath = Settings.GetValue("RelOutJob", "");
                opt.OutputFolder = Settings.GetValue("RelOutFolder", "");
                opt.DestinationFolder = Settings.GetValue("RelDest", "");
                opt.GenerateOutputs = Settings.GetValue("RelGenerate", "0") != "0";
                opt.Stamp = DateTime.Now;

                if (opt.ProjectName.Length == 0)
                {
                    Log.Say(title, "No project name has been set. Open the AltiumSpike window " +
                                   "and fill in the release fields once; this command reuses them.");
                    return;
                }

                ReleaseBundle.Result r = ReleasePackager.Package(client, opt);
                foreach (string d in r.Diagnostics) Log.Write("ReleasePackager: " + d);
                Log.Say(title, r.Summarise());
            }
            catch (Exception ex)
            {
                Log.Exception("RunPackageRelease", ex);
                Log.Say(title, "FAILED: " + ex.GetType().Name + " -- " + ex.Message);
            }
            Log.Write("<<< RunPackageRelease returned");
        }

        // Same comma/point tolerance as the window: the settings file holds
        // whatever the user typed, and this machine's locale is fr-FR.
        private static double Num(string raw, double fallback)
        {
            if (raw == null) return fallback;
            string s = raw.Trim().Replace(',', '.');
            double v;
            if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out v)) return v;
            return fallback;
        }

    }
}
