// SchPlacement.cs
//
// Places components on the focused schematic sheet from a components CSV,
// then connects them from a nets CSV with a wire stub and a net label on each
// pin. The rules -- what the CSVs mean, what makes them wrong, where a pin's
// electrical end is, where the label goes -- live in SchPlacementPlan.cs and
// are asserted by tests/SchPlacementTests. This file is only the Altium half.
//
// SYMBOLS COME FROM YOUEDA. The default library is YouEDA's shared
// youeda.SchLib; a row can name another .SchLib in a Library column. An LCSC
// number is turned into YouEDA's symbol name through its
// family-classification.csv files, and every resolved name is checked against
// the library's own component list (ILibCompInfoReader, which reads the file
// without opening it as a document) before anything is placed.
//
// ORDER OF WORK, and why.
//
//   1. Parse, lay out, resolve.  Every error in either CSV or any library
//      lookup is collected, and if there is even one, nothing is placed.
//   2. Place.  IIntegratedLibraryManager.PlaceLibraryComponent, one call per
//      part, with the location and orientation in its parameter string. The
//      new component is found by the unique ID that was not on the sheet
//      before the call -- never by designator, since a fresh one reads "U?".
//   3. Finish.  Inside PreProcess/PostProcess: set the designator, correct the
//      location or orientation if the call did not honour them, mirror.
//   4. Connect.  Read each placed component's pins AFTER step 3 (rotation and
//      mirroring move them), then add a stub and a label per connection.
//
// Components already on the sheet with the same designator are skipped, not
// duplicated, so a second run over the same CSVs places nothing new.
//
// NOTHING IS SAVED. The sheet is changed in memory; saving is the user's call.
//
// VERIFIED ON A SHEET (schematic self-test, 2026-09-25 20:38, 8 pass / 0 fail,
// Sheet1.SchDoc with youeda.SchLib):
//   - PlaceLibraryComponent places from YouEDA's SchLib even though that file
//     has no FileHeader symbol index, and it honours the
//     "Orientation=|Location.X=|Location.Y=" keys exactly; step 3 found
//     nothing to correct. Orientation counts counter-clockwise.
//   - Pin Location + Length along the pin's orientation is the electrical end
//     (every tip points away from the body, and tips rotate with the part).
//   - Wire vertices and label anchors read back where they were put, for
//     left- and down-pointing pins (the right-aligned label cases).
//
//   - Connectivity as Altium's compiler sees it (sample LDO circuit, Daughterboard
//     project): compiling reported "Net VIN_RAW has only one pin (Pin D1-2)" --
//     the compiler traced that label through its stub to the diode's anode --
//     and, once designators were checked project-wide, no other error. 5/5
//     placed, 11/11 labelled.
//
//   - A 14-part / 47-pin test (test2 CSVs, 21:27): every label on the pin
//     the nets CSV named, 14/14 nets and 47/47 pins identical to the netlist
//     predicted offline from the same CSVs; pins matched by number, by name
//     and by Node; all four rotations; a 14-pin IC.
//
//   - Mirror at 0 degrees (21:42). SetState_IsMirrored(true) had set a flag
//     and mirrored nothing; Mirror(location) flips pins and artwork -- the
//     self-test's reflected-tips check passed and Q201 came out with its gate
//     on the right, orientation and location undisturbed.
//
// STILL UNVERIFIED:
//   - Mirror combined with a rotation (every mirrored part so far was at 0).
//   - Multi-part symbols (none exist in youeda.SchLib to test with).
//   - Undo: the robot messages pass null as the broadcast target (the SDK
//     types the slot as ISch_BasicContainer and exposes no broadcast object).
//     The change itself does not depend on them.

using DXP;
using SCH;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AltiumSpike
{
    public static class SchPlacement
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public sealed class Options
        {
            public string ComponentsCsv = "";
            public string NetsCsv = "";             // optional
            public string DefaultLibrary = "";      // usually ...\EasyEdaAltium\youeda.SchLib
            public string OutputFolder = "";
            public double StubMil = 300.0;
            public double LabelInsetMil = 50.0;
            public double AutoOriginXMil = 1000.0;
            public double AutoOriginYMil = 1000.0;
            public double AutoPitchMil = 1500.0;
            public int AutoColumns = 8;
            public bool DryRun;
        }

        public sealed class Result
        {
            public bool Refused;                // stopped before touching the sheet
            public int PartsInCsv, PartsPlaced, PartsSkipped, PartsFailed, PartsAutoLaid, PartsCorrected;
            public int NodesInCsv, Labelled, LabelsFailed, Nets;
            public List<string> Notes = new List<string>();
            public List<string> Warnings = new List<string>();
            public List<string> Errors = new List<string>();
            public string PartsCsvPath = "";
            public string LabelsCsvPath = "";
        }

        // One placed pin, in sheet coordinates.
        public sealed class PinGeom
        {
            public ISch_Pin Pin;
            public SchPlacementPlan.PinInfo Info = new SchPlacementPlan.PinInfo();
            public long X, Y;           // body end (Location)
            public int OrientDeg;
            public long Length;
            public long TipX, TipY;     // electrical end
        }

        public static string DefaultYouEdaLibrary()
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(docs, "EasyEdaAltium", "youeda.SchLib");
        }

        // ==================================================================
        // Entry point
        // ==================================================================
        public static Result Run(IClient client, Options o)
        {
            Result res = new Result();

            // ---- 1. parse, lay out, resolve ----------------------------
            SchPlacementPlan.Plan plan;
            try
            {
                List<List<string>> comps = ReadRows(o.ComponentsCsv);
                List<List<string>> nets = string.IsNullOrWhiteSpace(o.NetsCsv) ? null : ReadRows(o.NetsCsv);
                plan = SchPlacementPlan.Parse(comps, nets);
            }
            catch (Exception ex)
            {
                res.Refused = true;
                res.Errors.Add("could not read the CSVs -- " + ex.GetType().Name + ": " + ex.Message);
                return res;
            }

            res.PartsInCsv = plan.Parts.Count;
            res.NodesInCsv = plan.Nodes.Count;
            res.Nets = plan.NetCount;
            res.Warnings.AddRange(plan.Warnings);

            if (plan.Ok)
            {
                SchPlacementPlan.AutoLayout(plan, o.AutoOriginXMil, o.AutoOriginYMil, o.AutoPitchMil, o.AutoColumns);
                foreach (SchPlacementPlan.Part p in plan.Parts) if (p.AutoPlaced) res.PartsAutoLaid++;
            }

            ISch_ServerInterface server = null;
            ISch_Document doc = null;
            string why;
            if (!TryGetSheet(client, out server, out doc, out why))
                plan.Errors.Add(why);

            Dictionary<string, string> libraryOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (plan.Ok && server != null)
                Resolve(server, plan, o, libraryOf, res);

            // ---- designators already in use ----------------------------
            //
            // Designators are unique per PROJECT, not per sheet. The first
            // version looked only at the focused sheet and placed D1, C1, U1,
            // C2 and R1 onto a sheet whose project already had all five on
            // another sheet -- five "Duplicate Component Designators" errors at
            // compile. Same sheet = a re-run, left alone; another sheet = a
            // collision, and nothing is placed.
            HashSet<string> skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (plan.Ok && doc != null)
            {
                HashSet<string> onThisSheet = new HashSet<string>(ComponentsByDesignator(doc).Keys, StringComparer.OrdinalIgnoreCase);
                int sheets;
                List<string> unread;
                Dictionary<string, string> elsewhere = ProjectDesignators(client, server, doc, out sheets, out unread);
                res.Notes.Add("Designators checked against this sheet and " + sheets + " other sheet(s) of the project (" +
                              elsewhere.Count + " designator(s) in use there).");
                foreach (string u in unread)
                    res.Warnings.Add(u + " is in the project but could not be read, so its designators were not checked");
                skip = SchPlacementPlan.ClassifyExisting(plan, onThisSheet, elsewhere);
            }

            if (!plan.Ok)
            {
                res.Refused = true;
                res.Errors.AddRange(plan.Errors);
                res.Notes.Add("Nothing was placed: " + plan.Errors.Count + " problem(s) must be fixed first.");
                return res;
            }

            res.PartsSkipped = skip.Count;
            if (skip.Count > 0)
                res.Warnings.Add(skip.Count + " designator(s) are already on this sheet and were left alone: " +
                                 Sample(skip, 10));

            List<string[]> partRows = new List<string[]>();
            List<string[]> labelRows = new List<string[]>();

            if (o.DryRun)
            {
                int toPlace = plan.Parts.Count - skip.Count;
                int toLabel = 0;
                foreach (SchPlacementPlan.Node n in plan.Nodes) if (!skip.Contains(n.Designator)) toLabel++;
                foreach (SchPlacementPlan.Part p in plan.Parts)
                    partRows.Add(PartRow(p, libraryOf[p.Designator], skip.Contains(p.Designator) ? "already on sheet" : "would place", ""));
                res.Notes.Add("Dry run -- the sheet was not changed.");
                res.Notes.Add(toPlace + " component(s) would be placed" +
                              (res.PartsAutoLaid > 0 ? " (" + res.PartsAutoLaid + " laid out automatically)" : "") +
                              ", " + toLabel + " pin connection(s) across " + plan.NetCount + " net(s) labelled.");
                WriteReports(o.OutputFolder, partRows, labelRows, res);
                return res;
            }

            // ---- 2. place -----------------------------------------------
            IIntegratedLibraryManager ilm = null;
            try { ilm = EDP.Utils.LoadIntegratedLibraryManager(); }
            catch (Exception ex) { res.Errors.Add("IntegratedLibraryManager unavailable -- " + ex.Message); }
            if (ilm == null)
            {
                res.Refused = true;
                res.Errors.Add("could not reach Altium's integrated library manager, so nothing was placed");
                return res;
            }

            HashSet<string> knownIds = ComponentIds(doc);
            Dictionary<string, ISch_Component> placed = new Dictionary<string, ISch_Component>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> partStatus = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (SchPlacementPlan.Part p in plan.Parts)
            {
                if (skip.Contains(p.Designator)) { partStatus[p.Designator] = "already on sheet"; continue; }
                string err;
                ISch_Component c = PlaceOne(doc, ilm, p, libraryOf[p.Designator], knownIds, out err);
                if (c == null)
                {
                    res.PartsFailed++;
                    res.Errors.Add(p.Designator + ": " + err);
                    partStatus[p.Designator] = "FAILED: " + err;
                    continue;
                }
                placed[p.Designator] = c;
            }

            // ---- 3 + 4. finish and connect ------------------------------
            Context ctx = new Context(client, server, doc);
            ctx.Begin();
            try
            {
                foreach (SchPlacementPlan.Part p in plan.Parts)
                {
                    ISch_Component c;
                    if (!placed.TryGetValue(p.Designator, out c)) continue;
                    string note;
                    bool corrected = Finish(ctx, c, p, out note);
                    if (corrected) res.PartsCorrected++;
                    res.PartsPlaced++;
                    partStatus[p.Designator] = corrected ? "placed, corrected: " + note : "placed";
                }

                long stub = EDP.Utils.MilsToCoord(o.StubMil);
                long inset = EDP.Utils.MilsToCoord(o.LabelInsetMil);
                Dictionary<string, List<PinGeom>> pinsOf = new Dictionary<string, List<PinGeom>>(StringComparer.OrdinalIgnoreCase);

                foreach (SchPlacementPlan.Node n in plan.Nodes)
                {
                    ISch_Component c;
                    if (skip.Contains(n.Designator)) continue;
                    if (!placed.TryGetValue(n.Designator, out c))
                    {
                        res.LabelsFailed++;
                        labelRows.Add(LabelRow(n, null, "FAILED", n.Designator + " was not placed"));
                        continue;
                    }

                    List<PinGeom> pins;
                    if (!pinsOf.TryGetValue(n.Designator, out pins))
                    {
                        pins = Pins(c);
                        pinsOf[n.Designator] = pins;
                    }

                    List<SchPlacementPlan.PinInfo> infos = new List<SchPlacementPlan.PinInfo>();
                    foreach (PinGeom g in pins) infos.Add(g.Info);
                    string problem;
                    int idx = SchPlacementPlan.MatchPin(infos, n.Pin, out problem);
                    if (idx < 0)
                    {
                        res.LabelsFailed++;
                        res.Errors.Add(n.Designator + "." + n.Pin + " (" + n.Net + "): " + problem);
                        labelRows.Add(LabelRow(n, null, "FAILED", problem));
                        continue;
                    }

                    string err;
                    if (AddStubAndLabel(ctx, pins[idx], n.Net, stub, inset, null, out err))
                    {
                        res.Labelled++;
                        labelRows.Add(LabelRow(n, pins[idx], "labelled", ""));
                    }
                    else
                    {
                        res.LabelsFailed++;
                        res.Errors.Add(n.Designator + "." + n.Pin + " (" + n.Net + "): " + err);
                        labelRows.Add(LabelRow(n, pins[idx], "FAILED", err));
                    }
                }
            }
            finally
            {
                ctx.End();
            }

            foreach (SchPlacementPlan.Part p in plan.Parts)
            {
                string st;
                if (!partStatus.TryGetValue(p.Designator, out st)) st = "";
                partRows.Add(PartRow(p, libraryOf[p.Designator], st, ""));
            }

            res.Notes.Add(res.PartsPlaced + " of " + res.PartsInCsv + " component(s) placed" +
                          (res.PartsSkipped > 0 ? ", " + res.PartsSkipped + " already on the sheet" : "") +
                          (res.PartsFailed > 0 ? ", " + res.PartsFailed + " FAILED" : "") + ".");
            if (res.PartsCorrected > 0)
                res.Notes.Add(res.PartsCorrected + " needed their location or orientation set after placement -- " +
                              "the placement call did not honour them.");
            res.Notes.Add(res.Labelled + " pin connection(s) labelled across " + res.Nets + " net(s)" +
                          (res.LabelsFailed > 0 ? ", " + res.LabelsFailed + " FAILED" : "") + ".");
            res.Notes.Add("The sheet is modified in memory only. Review it, then save it yourself.");

            WriteReports(o.OutputFolder, partRows, labelRows, res);
            return res;
        }

        // ==================================================================
        // Sheet and library access
        // ==================================================================

        public static bool TryGetSheet(IClient client, out ISch_ServerInterface server, out ISch_Document doc, out string why)
        {
            server = null;
            doc = null;
            why = null;
            try
            {
                client.StartServer("SCH");
                server = client.GetServerModuleByName("SCH") as ISch_ServerInterface;
            }
            catch (Exception ex)
            {
                Log.Exception("SchPlacement.TryGetSheet", ex);
                why = "could not start the schematic server -- " + ex.Message;
                return false;
            }
            if (server == null) { why = "could not obtain the schematic server"; return false; }

            doc = server.GetCurrentSchDocument();
            if (doc == null) { why = "no schematic has focus -- click into the target .SchDoc first"; return false; }

            // Sheet or library is decided by the object's own ID and the file
            // extension, NOT by "doc is ISch_Lib". The first version used the
            // cast and refused a focused sheet as a "library"; SDK wrappers
            // have satisfied casts they should not before (see the
            // IPCB_ElectricalLayer trap in AGENTS.md). The cast's answer is
            // still recorded so the next report settles which it was.
            string name = "";
            try { name = doc.GetState_DocumentName() ?? ""; } catch { }
            TObjectId id = TObjectId.eSheet;
            bool idKnown = false;
            try { id = doc.GetState_ObjectId(); idKnown = true; } catch { }
            bool castSaysLib = doc is ISch_Lib;
            bool libByName = name.EndsWith(".SchLib", StringComparison.OrdinalIgnoreCase);
            bool libById = idKnown && id == TObjectId.eSchLib;

            LastSheetDiagnosis = Path.GetFileName(name) + " (object id " + (idKnown ? id.ToString() : "unreadable") +
                                 ", cast to ISch_Lib " + (castSaysLib ? "succeeds" : "fails") + ")";
            Log.Write("SchPlacement.TryGetSheet: " + LastSheetDiagnosis);

            if (libById || libByName)
            {
                why = "the focused document is the schematic LIBRARY " + LastSheetDiagnosis +
                      " -- click into the target .SchDoc, then run again";
                doc = null;
                return false;
            }
            return true;
        }

        // What TryGetSheet last saw, for reports: name, object id, and what
        // the ISch_Lib cast said.
        public static string LastSheetDiagnosis = "";

        // The library's component names, read from the file without opening
        // it as a document. Null when it cannot be read.
        public static HashSet<string> ReadLibraryNames(ISch_ServerInterface server, string path, out string why)
        {
            why = null;
            if (!File.Exists(path)) { why = "library not found: " + path; return null; }

            ILibCompInfoReader reader = null;
            try
            {
                reader = server.CreateLibCompInfoReader(path);
                if (reader == null) { why = "Altium returned no reader for " + path; return null; }
                reader.ReadAllComponentInfo();
                int n = reader.NumComponentInfos();
                HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < n; i++)
                {
                    IComponentInfo info = reader.GetState_ComponentInfo(i);
                    if (info == null) continue;
                    string name = info.GetState_CompName();
                    if (!string.IsNullOrEmpty(name)) names.Add(name);
                }

                // The reader lists what the library's FileHeader index says
                // (CompCount=, LibRef0=, ...), not what is stored in it.
                // YouEDA writes youeda.SchLib through AltiumSharp with no such
                // index -- 0 symbols reported for a 2 MB library -- so an empty
                // answer means "cannot check in advance", not "no symbols".
                if (names.Count == 0)
                {
                    why = Path.GetFileName(path) + " has no symbol index (its FileHeader has no CompCount/LibRef " +
                          "entries, as YouEDA writes it), so its symbol names cannot be checked in advance";
                    return null;
                }
                return names;
            }
            catch (Exception ex)
            {
                why = "could not read " + Path.GetFileName(path) + " -- " + ex.GetType().Name + ": " + ex.Message;
                return null;
            }
            finally
            {
                if (reader != null) { try { server.DestroyCompInfoReader(ref reader); } catch { } }
            }
        }

        // LCSC -> symbol name from YouEDA's family runs next to the library,
        // newest run first so its names win.
        public static Dictionary<string, string> LoadYouEdaMap(string libraryPath, List<string> notes)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string dir = Path.GetDirectoryName(libraryPath);
                string runs = Path.Combine(dir ?? "", "Family Libraries");
                if (!Directory.Exists(runs)) return map;

                List<string> files = new List<string>(
                    Directory.GetFiles(runs, "family-classification.csv", SearchOption.AllDirectories));
                files.Sort(delegate (string a, string b) { return File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)); });

                int total = 0;
                foreach (string f in files) total += SchPlacementPlan.AddClassification(map, ReadRows(f));
                if (notes != null && files.Count > 0)
                    notes.Add(total + " LCSC number(s) mapped from " + files.Count + " YouEDA family-classification file(s).");
            }
            catch (Exception ex)
            {
                if (notes != null) notes.Add("YouEDA classification files could not be read -- " + ex.Message);
            }
            return map;
        }

        private static void Resolve(ISch_ServerInterface server, SchPlacementPlan.Plan plan, Options o,
                                    Dictionary<string, string> libraryOf, Result res)
        {
            Dictionary<string, HashSet<string>> namesCache = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Dictionary<string, string>> mapCache = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            foreach (SchPlacementPlan.Part p in plan.Parts)
            {
                string lib = p.Library.Length > 0 ? p.Library : o.DefaultLibrary;
                if (string.IsNullOrWhiteSpace(lib))
                {
                    plan.Errors.Add(p.Designator + ": no library given and no default library set");
                    continue;
                }
                if (!Path.IsPathRooted(lib) && !string.IsNullOrEmpty(o.ComponentsCsv))
                    lib = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(o.ComponentsCsv) ?? "", lib));
                libraryOf[p.Designator] = lib;

                HashSet<string> names;
                if (!namesCache.TryGetValue(lib, out names))
                {
                    string why = null;
                    if (!File.Exists(lib)) why = "library not found: " + lib;
                    else if (lib.EndsWith(".SchLib", StringComparison.OrdinalIgnoreCase))
                        names = ReadLibraryNames(server, lib, out why);
                    else
                        res.Warnings.Add(Path.GetFileName(lib) + " is not a .SchLib, so its symbol names were not " +
                                         "checked in advance; a wrong LibRef will fail at placement");

                    if (why != null && !File.Exists(lib)) plan.Errors.Add(p.Designator + ": " + why);
                    else if (why != null) res.Warnings.Add(why + " -- names will be checked at placement instead");
                    else if (names != null)
                        res.Notes.Add(Path.GetFileName(lib) + ": " + names.Count + " symbol(s).");
                    namesCache[lib] = names;
                }
                if (!File.Exists(lib)) continue;

                Dictionary<string, string> map;
                if (!mapCache.TryGetValue(lib, out map))
                {
                    map = LoadYouEdaMap(lib, res.Notes);
                    mapCache[lib] = map;
                }

                string err;
                if (!SchPlacementPlan.ResolveLibRef(p, map, names, Path.GetFileName(lib), out err))
                    plan.Errors.Add(err);
            }
        }

        // ==================================================================
        // Placement
        // ==================================================================

        public static ISch_Component PlaceOne(ISch_Document doc, IIntegratedLibraryManager ilm,
                                              SchPlacementPlan.Part p, string libraryPath,
                                              HashSet<string> knownIds, out string error)
        {
            error = null;
            int x = EDP.Utils.MilsToCoord(p.XMil);
            int y = EDP.Utils.MilsToCoord(p.YMil);
            string parameters = "Orientation=" + (p.Rotation / 90).ToString(Inv) +
                                "|Location.X=" + x.ToString(Inv) +
                                "|Location.Y=" + y.ToString(Inv);

            bool ok;
            try
            {
                ok = ilm.PlaceLibraryComponent(p.LibRef, libraryPath, parameters);
            }
            catch (Exception ex)
            {
                error = "PlaceLibraryComponent(" + p.LibRef + ") threw " + ex.GetType().Name + ": " + ex.Message;
                return null;
            }

            ISch_Component found = null;
            int fresh = 0;
            ISch_Iterator it = doc.SchIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(new TObjectId[] { TObjectId.eSchComponent }));
                ISch_BasicContainer o = it.FirstSchObject();
                while (o != null)
                {
                    ISch_Component c = o as ISch_Component;
                    string id = c == null ? null : c.GetState_UniqueId();
                    if (id != null && !knownIds.Contains(id))
                    {
                        fresh++;
                        knownIds.Add(id);
                        if (found == null) found = c;
                    }
                    o = it.NextSchObject();
                }
            }
            finally { doc.SchIterator_Destroy(ref it); }

            if (found == null)
            {
                error = "PlaceLibraryComponent(" + p.LibRef + ", " + Path.GetFileName(libraryPath) + ") returned " +
                        (ok ? "true" : "false") + " and no new component appeared on the sheet";
                return null;
            }
            if (fresh > 1)
                Log.Write("SchPlacement: " + fresh + " new components appeared placing " + p.Designator + " -- took the first");
            return found;
        }

        // Designator, and location / orientation / mirror if the placement
        // call did not already set them. True if anything had to be corrected.
        public static bool Finish(Context ctx, ISch_Component c, SchPlacementPlan.Part p, out string note)
        {
            note = "";
            bool corrected = false;
            int x = EDP.Utils.MilsToCoord(p.XMil);
            int y = EDP.Utils.MilsToCoord(p.YMil);
            TRotationBy90 want = Rotation(p.Rotation);

            ctx.BeginModify(c);
            try
            {
                ISch_Designator d = c.GetState_SchDesignator();
                if (d != null) d.SetState_Text(p.Designator);

                TRotationBy90 got = c.GetState_Orientation();
                if (got != want)
                {
                    c.SetState_Orientation(want);
                    note += "orientation " + Degrees(got) + " -> " + p.Rotation + "; ";
                    corrected = true;
                }

                Point at = c.GetState_Location();
                if (at.X != x || at.Y != y)
                {
                    c.MoveToXY(x, y);
                    note += "location " + Mil(at.X) + "," + Mil(at.Y) + " -> " + Mil(x) + "," + Mil(y) + "; ";
                    corrected = true;
                }

                // Mirror(point), NOT SetState_IsMirrored(true). The setter only
                // flips the flag: the first real test (Q201, 2026-09-25 21:27)
                // came out with its gate still on the left, labelled correctly
                // but not mirrored. Mirror() moves the graphics and the pins,
                // about the component's own location. Flipping in sheet space
                // can change a rotated part's orientation, so that is read
                // back and restored.
                if (p.Mirror && !c.GetState_IsMirrored())
                {
                    c.Mirror(c.GetState_Location());
                    if (!c.GetState_IsMirrored()) note += "Mirror() left the mirrored flag unset; ";
                    TRotationBy90 afterMirror = c.GetState_Orientation();
                    if (afterMirror != want)
                    {
                        c.SetState_Orientation(want);
                        note += "orientation " + Degrees(afterMirror) + " after mirroring -> " + p.Rotation + "; ";
                        corrected = true;
                    }
                    Point m = c.GetState_Location();
                    if (m.X != x || m.Y != y)
                    {
                        c.MoveToXY(x, y);
                        note += "moved back to " + Mil(x) + "," + Mil(y) + " after mirroring; ";
                        corrected = true;
                    }
                }
            }
            finally
            {
                ctx.EndModify(c);
            }
            c.GraphicallyInvalidate();
            return corrected;
        }

        // ==================================================================
        // Pins, stubs, labels
        // ==================================================================

        public static List<PinGeom> Pins(ISch_Component c)
        {
            List<PinGeom> list = new List<PinGeom>();
            int part = 0;
            try { part = c.GetState_CurrentPartID(); } catch { }

            ISch_Iterator it = c.SchIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(new TObjectId[] { TObjectId.ePin }));
                ISch_BasicContainer o = it.FirstSchObject();
                while (o != null)
                {
                    ISch_Pin pin = o as ISch_Pin;
                    if (pin != null)
                    {
                        PinGeom g = new PinGeom();
                        g.Pin = pin;
                        g.Info.Designator = pin.GetState_Designator() ?? "";
                        g.Info.Name = pin.GetState_Name() ?? "";
                        g.Info.Hidden = pin.GetState_IsHidden();
                        int owner = pin.GetState_OwnerPartId();
                        g.Info.OnPlacedPart = owner == 0 || owner == part;

                        Point loc = pin.GetState_Location();
                        g.X = loc.X;
                        g.Y = loc.Y;
                        g.OrientDeg = Degrees(pin.GetState_Orientation());
                        g.Length = pin.GetState_PinLength();
                        SchPlacementPlan.PinTip(g.X, g.Y, g.OrientDeg, g.Length, out g.TipX, out g.TipY);
                        list.Add(g);
                    }
                    o = it.NextSchObject();
                }
            }
            finally { c.SchIterator_Destroy(ref it); }
            return list;
        }

        // Adds the wire stub and the net label, reads both back, and returns
        // false with the reason if either did not land where it was put.
        public static bool AddStubAndLabel(Context ctx, PinGeom pin, string net, long stubLength, long inset,
                                           List<ISch_BasicContainer> created, out string error)
        {
            error = null;
            SchPlacementPlan.Stub s = SchPlacementPlan.StubFor(pin.TipX, pin.TipY, pin.OrientDeg, stubLength, inset);

            ISch_Wire wire = ctx.Server.SchObjectFactory(TObjectId.eWire, TObjectCreationMode.eCreate_Default) as ISch_Wire;
            if (wire == null) { error = "the object factory returned no wire"; return false; }
            wire.SetState_Location(Pt(s.X1, s.Y1));
            wire.InsertVertex(1);
            wire.SetState_Vertex(1, Pt(s.X1, s.Y1));
            wire.InsertVertex(2);
            wire.SetState_Vertex(2, Pt(s.X2, s.Y2));
            ctx.Register(wire);
            if (created != null) created.Add(wire);

            ISch_NetLabel label = ctx.Server.SchObjectFactory(TObjectId.eNetLabel, TObjectCreationMode.eCreate_Default) as ISch_NetLabel;
            if (label == null) { error = "the object factory returned no net label"; return false; }
            label.SetState_Location(Pt(s.LabelX, s.LabelY));
            label.SetState_Text(net);
            label.SetState_Orientation(s.LabelRotation == 90 ? TRotationBy90.eRotate90 : TRotationBy90.eRotate0);
            label.SetState_Justification(s.LabelRightAligned ? TTextJustification.eJustify_BottomRight
                                                             : TTextJustification.eJustify_BottomLeft);
            ctx.Register(label);
            if (created != null) created.Add(label);

            // Read back. A wire whose first vertex is not on the pin tip, or a
            // label not on the wire, connects nothing -- and looks fine.
            int count = wire.GetState_VerticesCount();
            Point v1 = wire.GetState_Vertex(1);
            Point v2 = wire.GetState_Vertex(2);
            if (count != 2 || v1.X != s.X1 || v1.Y != s.Y1 || v2.X != s.X2 || v2.Y != s.Y2)
            {
                error = "wire read back as " + count + " vertices (" + Mil(v1.X) + "," + Mil(v1.Y) + ")-(" +
                        Mil(v2.X) + "," + Mil(v2.Y) + "), not (" + Mil(s.X1) + "," + Mil(s.Y1) + ")-(" +
                        Mil(s.X2) + "," + Mil(s.Y2) + ")";
                return false;
            }
            Point la = label.GetState_Location();
            if (!SchPlacementPlan.OnSegment(la.X, la.Y, s.X1, s.Y1, s.X2, s.Y2))
            {
                error = "net label read back at " + Mil(la.X) + "," + Mil(la.Y) + ", off its wire";
                return false;
            }
            if (label.GetState_Text() != net)
            {
                error = "net label reads \"" + label.GetState_Text() + "\", not \"" + net + "\"";
                return false;
            }
            return true;
        }

        // ==================================================================
        // Process control
        // ==================================================================

        // PreProcess/PostProcess around a batch, and the robot messages that
        // register objects for undo. See the header on the null broadcast slot.
        public sealed class Context
        {
            public readonly ISch_ServerInterface Server;
            public readonly ISch_Document Doc;
            private readonly IProcessControl pc;
            private readonly ISch_RobotManager robot;
            private bool open;

            public Context(IClient client, ISch_ServerInterface server, ISch_Document doc)
            {
                Server = server;
                Doc = doc;
                try { pc = client.GetProcessControl(); } catch (Exception ex) { Log.Exception("SchPlacement/ProcessControl", ex); }
                try { robot = server.GetState_RobotManager(); } catch (Exception ex) { Log.Exception("SchPlacement/RobotManager", ex); }
            }

            public void Begin()
            {
                if (pc == null || open) return;
                try { pc.PreProcess(Doc, ""); open = true; }
                catch (Exception ex) { Log.Exception("SchPlacement/PreProcess", ex); }
            }

            public void End()
            {
                if (pc != null && open)
                {
                    try { pc.PostProcess(Doc, ""); }
                    catch (Exception ex) { Log.Exception("SchPlacement/PostProcess", ex); }
                    open = false;
                }
                try { Doc.GraphicallyInvalidate(); } catch { }
            }

            public void BeginModify(ISch_BasicContainer o) { Send(o, SCHConstant.SCHMBeginModify); }
            public void EndModify(ISch_BasicContainer o) { Send(o, SCHConstant.SCHMEndModify); }

            public void Register(ISch_BasicContainer o)
            {
                Doc.RegisterSchObjectInContainer(o);
                Send(Doc, SCHConstant.SCHMPrimitiveRegistration, o);
            }

            public void Remove(ISch_BasicContainer o)
            {
                Doc.RemoveSchObject(o);
                Send(Doc, SCHConstant.SCHMPrimitiveRegistration, o);
            }

            private void Send(ISch_BasicContainer source, int message, ISch_BasicContainer data = null)
            {
                if (robot == null) return;
                try { robot.SendMessage(source, null, (ushort)message, data); }
                catch (Exception ex) { Log.Write("SchPlacement: robot message " + message + " failed -- " + ex.Message); }
            }
        }

        // ==================================================================
        // Sheet census
        // ==================================================================

        public static int Count(ISch_Document doc, TObjectId kind)
        {
            int n = 0;
            ISch_Iterator it = doc.SchIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(new TObjectId[] { kind }));
                ISch_BasicContainer o = it.FirstSchObject();
                while (o != null) { n++; o = it.NextSchObject(); }
            }
            finally { doc.SchIterator_Destroy(ref it); }
            return n;
        }

        public static HashSet<string> ComponentIds(ISch_Document doc)
        {
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            ISch_Iterator it = doc.SchIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(new TObjectId[] { TObjectId.eSchComponent }));
                ISch_BasicContainer o = it.FirstSchObject();
                while (o != null)
                {
                    string id = o.GetState_UniqueId();
                    if (id != null) ids.Add(id);
                    o = it.NextSchObject();
                }
            }
            finally { doc.SchIterator_Destroy(ref it); }
            return ids;
        }

        // Designator -> sheet file name, for every OTHER .SchDoc in the
        // project that owns the focused document. A sheet that is not loaded
        // is opened hidden (read only -- nothing is changed or saved); one
        // that still cannot be read is returned in `unread` so the caller
        // says so rather than silently checking less.
        public static Dictionary<string, string> ProjectDesignators(IClient client, ISch_ServerInterface server,
                                                                    ISch_Document current, out int sheets,
                                                                    out List<string> unread)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            sheets = 0;
            unread = new List<string>();

            IDXPProject project = null;
            try
            {
                IDXPWorkSpace ws = client.GetDXPWorkspace();
                if (ws != null)
                {
                    IDXPDocument focused = ws.DM_FocusedDocument();
                    if (focused != null) project = focused.DM_Project();
                    if (project == null) project = ws.DM_FocusedProject();
                }
            }
            catch (Exception ex) { Log.Exception("SchPlacement.ProjectDesignators/workspace", ex); }
            if (project == null) return map;

            string currentPath = "";
            try { currentPath = current.GetState_DocumentName() ?? ""; } catch { }

            int n = 0;
            try { n = project.DM_LogicalDocumentCount(); } catch { }
            for (int i = 0; i < n; i++)
            {
                string path = "";
                try
                {
                    IDXPDocument d = project.DM_LogicalDocuments(i);
                    if (d != null) path = d.DM_FullPath() ?? "";
                }
                catch { }
                if (!path.EndsWith(".SchDoc", StringComparison.OrdinalIgnoreCase)) continue;
                if (SamePath(path, currentPath)) continue;

                ISch_Document sd = null;
                try { sd = server.GetSchDocumentByPath(path); } catch { }
                if (sd == null)
                {
                    try { client.OpenDocumentShowOrHide("SCH", path, false); } catch { }
                    try { sd = server.GetSchDocumentByPath(path); } catch { }
                }
                if (sd == null) { unread.Add(Path.GetFileName(path)); continue; }

                sheets++;
                foreach (string des in ComponentsByDesignator(sd).Keys)
                    if (!map.ContainsKey(des)) map[des] = Path.GetFileName(path);
            }
            return map;
        }

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                if (Path.IsPathRooted(a) && Path.IsPathRooted(b))
                    return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
            }
            catch { }
            return string.Equals(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase);
        }

        public static Dictionary<string, ISch_Component> ComponentsByDesignator(ISch_Document doc)
        {
            Dictionary<string, ISch_Component> map = new Dictionary<string, ISch_Component>(StringComparer.OrdinalIgnoreCase);
            ISch_Iterator it = doc.SchIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(new TObjectId[] { TObjectId.eSchComponent }));
                ISch_BasicContainer o = it.FirstSchObject();
                while (o != null)
                {
                    ISch_Component c = o as ISch_Component;
                    if (c != null)
                    {
                        ISch_Designator d = c.GetState_SchDesignator();
                        string t = d == null ? "" : (d.GetState_Text() ?? "");
                        if (t.Length > 0 && !map.ContainsKey(t)) map[t] = c;
                    }
                    o = it.NextSchObject();
                }
            }
            finally { doc.SchIterator_Destroy(ref it); }
            return map;
        }

        // ==================================================================
        // Helpers
        // ==================================================================

        public static TRotationBy90 Rotation(int deg)
        {
            switch (((deg % 360) + 360) % 360)
            {
                case 90: return TRotationBy90.eRotate90;
                case 180: return TRotationBy90.eRotate180;
                case 270: return TRotationBy90.eRotate270;
                default: return TRotationBy90.eRotate0;
            }
        }

        public static int Degrees(TRotationBy90 r)
        {
            if (r == TRotationBy90.eRotate90) return 90;
            if (r == TRotationBy90.eRotate180) return 180;
            if (r == TRotationBy90.eRotate270) return 270;
            return 0;
        }

        public static Point Pt(long x, long y)
        {
            Point p = new Point();
            p.X = checked((int)x);
            p.Y = checked((int)y);
            return p;
        }

        public static string Mil(long coord)
        {
            double v = Math.Round(EDP.Utils.CoordToMils((int)coord), 3, MidpointRounding.AwayFromZero);
            if (v == 0) v = 0;
            return v.ToString("0.###", Inv);
        }

        private static string Sample(IEnumerable<string> items, int max)
        {
            List<string> l = new List<string>(items);
            l.Sort(SchPlacementPlan.CompareDesignators);
            if (l.Count <= max) return string.Join(", ", l);
            return string.Join(", ", l.GetRange(0, max)) + " and " + (l.Count - max) + " more";
        }

        public static List<List<string>> ReadRows(string path)
        {
            List<List<string>> rows = new List<List<string>>();
            foreach (string line in CsvIo.ReadLines(path)) rows.Add(CsvIo.Split(line));
            return rows;
        }

        private static string[] PartRow(SchPlacementPlan.Part p, string lib, string status, string detail)
        {
            return new string[]
            {
                p.Designator, p.Lcsc, p.LibRef, lib,
                p.XMil.ToString("0.###", Inv), p.YMil.ToString("0.###", Inv),
                p.Rotation.ToString(Inv), p.Mirror ? "yes" : "no", p.AutoPlaced ? "yes" : "no",
                status, detail
            };
        }

        private static string[] LabelRow(SchPlacementPlan.Node n, PinGeom g, string status, string detail)
        {
            return new string[]
            {
                n.Net, n.Designator, n.Pin,
                g == null ? "" : g.Info.Designator,
                g == null ? "" : g.Info.Name,
                g == null ? "" : Mil(g.TipX), g == null ? "" : Mil(g.TipY),
                status, detail
            };
        }

        private static void WriteReports(string folder, List<string[]> parts, List<string[]> labels, Result res)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
            try
            {
                res.PartsCsvPath = Path.Combine(folder, "sch_placement.csv");
                WriteCsv(res.PartsCsvPath,
                         "Designator,LCSC,LibRef,Library,X mil,Y mil,Rotation,Mirror,Auto,Status,Detail", parts);
                if (labels.Count > 0)
                {
                    res.LabelsCsvPath = Path.Combine(folder, "sch_net_labels.csv");
                    WriteCsv(res.LabelsCsvPath,
                             "Net,Designator,Pin,Pin number,Pin name,Tip X mil,Tip Y mil,Status,Detail", labels);
                }
            }
            catch (Exception ex)
            {
                res.Warnings.Add("report CSV not written -- " + ex.Message);
            }
        }

        private static void WriteCsv(string path, string header, List<string[]> rows)
        {
            List<string> lines = new List<string>();
            lines.Add(header);
            foreach (string[] r in rows)
            {
                string[] q = new string[r.Length];
                for (int i = 0; i < r.Length; i++) q[i] = Csv(r[i]);
                lines.Add(string.Join(",", q));
            }
            File.WriteAllLines(path, lines, new UTF8Encoding(false));
        }

        private static string Csv(string s)
        {
            if (s == null) return "";
            if (s.IndexOf(',') < 0 && s.IndexOf('"') < 0 && s.IndexOf('\n') < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }
}
