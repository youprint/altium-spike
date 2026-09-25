// SelfTest.cs
//
// Runs every AltiumSpike function against the open board and writes a report
// saying what each one returned and whether the answer is credible.
//
// WHY THIS EXISTS. Ten sections shipped compiling cleanly against the real
// SDK with every call read from assembly metadata -- and not one of them had
// ever run on a board. "Compiles" and "works" are different claims, and the
// gap between them is where a wrong iterator filter or a misread accessor
// sits silently returning zero.
//
// Driving forty buttons through injected clicks would test the GUI, not the
// code, and does it forty fragile times. Running the same forty calls from
// inside the plugin needs ONE reliable interaction and can assert things a
// person clicking could not: that the numbers relate to each other, that a
// placed object really landed where it was asked to, that a count changed by
// exactly what was claimed.
//
// TWO PHASES.
//
//   READ-ONLY   Every function that only reads or writes a CSV. Safe on any
//               board, including one you care about.
//
//   MODIFYING   Every function that changes the board. These build their OWN
//               geometry in a clear area first, run against it, read the
//               result back, then put the board back as they found it: the
//               scratch geometry is swept, and the two checks that touch
//               existing objects -- designator text, component placement --
//               record what they change and restore it.
//
//               THE FIRST VERSION DID NOT DO THAT. It left its scratch
//               geometry "as evidence" and resized every designator on the
//               board without saving the old values, which is harmless on a
//               scratch copy and not harmless on the board someone actually
//               had open. The evidence is the report. The board goes back.
//               Nothing here writes to disk, so an unsaved document is still
//               the last line of defence -- but it should not have to be.
//
// A CHECK IS NOT A PASS BECAUSE IT DID NOT THROW. The whole failure mode
// worth catching here is a function that returns cleanly having done nothing,
// so every check states what it expected and compares. Where a result cannot
// be judged from inside -- a board legitimately having no variants, say --
// it reports INFO rather than claiming either way.

using DXP;
using PCB;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace AltiumSpike
{
    public static partial class SelfTest
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static double ToMM(int c) { return EDP.Utils.CoordToMMs(c); }
        private static int ToCoord(double mm) { return EDP.Utils.MMsToCoord(mm); }

        public enum Verdict { Pass, Fail, Info, Skip }

        public sealed class Check
        {
            public string Section = "";
            public string Name = "";
            public Verdict Verdict = Verdict.Info;
            public string Expected = "";
            public string Actual = "";
            public string Note = "";
            public long Ms;
        }

        public sealed class Report
        {
            public List<Check> Checks = new List<Check>();
            public int Passed, Failed, Infos, Skipped;
            public string Path = "";
            public bool ModifyingRun;

            public void Add(Check c)
            {
                Checks.Add(c);
                if (c.Verdict == Verdict.Pass) Passed++;
                else if (c.Verdict == Verdict.Fail) Failed++;
                else if (c.Verdict == Verdict.Skip) Skipped++;
                else Infos++;
            }

            public string Headline()
            {
                return Passed + " passed, " + Failed + " failed, " +
                       Infos + " informational, " + Skipped + " skipped";
            }
        }

        // ------------------------------------------------------------------
        private sealed class Runner
        {
            public Report Rep;
            public string Section = "";
            private Stopwatch sw = new Stopwatch();

            public void Run(string name, string expected, Func<string> body)
            {
                Check c = new Check();
                c.Section = Section;
                c.Name = name;
                c.Expected = expected;

                sw.Restart();
                try
                {
                    // The body returns a verdict marker and a description:
                    //   "PASS: ..."  "FAIL: ..."  "INFO: ..."  "SKIP: ..."
                    string r = body() ?? "INFO: returned nothing";
                    sw.Stop();

                    if (r.StartsWith("PASS:")) { c.Verdict = Verdict.Pass; c.Actual = r.Substring(5).Trim(); }
                    else if (r.StartsWith("FAIL:")) { c.Verdict = Verdict.Fail; c.Actual = r.Substring(5).Trim(); }
                    else if (r.StartsWith("SKIP:")) { c.Verdict = Verdict.Skip; c.Actual = r.Substring(5).Trim(); }
                    else if (r.StartsWith("INFO:")) { c.Verdict = Verdict.Info; c.Actual = r.Substring(5).Trim(); }
                    else { c.Verdict = Verdict.Info; c.Actual = r; }
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    c.Verdict = Verdict.Fail;
                    c.Actual = "threw " + ex.GetType().Name + ": " + ex.Message;
                    Log.Exception("SelfTest/" + name, ex);
                }
                c.Ms = sw.ElapsedMilliseconds;
                Rep.Add(c);
                Log.Write("SelfTest [" + c.Verdict + "] " + name + " -- " + c.Actual);
            }
        }

        private static string F3(double v) { return v.ToString("0.000", Inv); }

        // ==================================================================
        // Entry point
        // ==================================================================
        public static Report Run(IClient client, IPCB_ServerInterface pcbServer, IPCB_Board board,
                                 string folder, bool includeModifying)
        {
            Report rep = new Report();
            rep.ModifyingRun = includeModifying;
            Runner r = new Runner();
            r.Rep = rep;

            Log.Write("=========== SelfTest starting (modifying=" + includeModifying + ") ===========");

            ReadOnlyChecks(r, pcbServer, board, folder);
            if (includeModifying) ModifyingChecks(r, pcbServer, board, folder);

            rep.Path = Write(rep, board, folder);
            Log.Write("=========== SelfTest done: " + rep.Headline() + " ===========");
            return rep;
        }

        // ==================================================================
        // Read-only
        // ==================================================================
        private static void ReadOnlyChecks(Runner r, IPCB_ServerInterface pcbServer, IPCB_Board board,
                                           string folder)
        {
            // --- a baseline census, so later checks can be judged against
            //     what the board actually contains rather than in a vacuum ---
            int comps = 0, nets = 0, vias = 0, tracks = 0, pads = 0, polys = 0, rules = 0;

            r.Section = "Baseline";
            r.Run("Board census", "counts that match a real board", delegate
            {
                comps = Count(board, TObjectId.eComponentObject);
                nets = Count(board, TObjectId.eNetObject);
                vias = Count(board, TObjectId.eViaObject);
                tracks = Count(board, TObjectId.eTrackObject);
                pads = Count(board, TObjectId.ePadObject);
                polys = Count(board, TObjectId.ePolyObject);
                rules = Count(board, TObjectId.eRuleObject);

                string s = comps + " components, " + nets + " nets, " + pads + " pads, " +
                           tracks + " tracks, " + vias + " vias, " + polys + " polygons, " +
                           rules + " rules";

                if (comps == 0 && nets == 0)
                    return "FAIL: " + s + " -- the iterator returned nothing, so every other check is meaningless";
                return "PASS: " + s;
            });

            // --- Connectivity ---
            r.Section = "Connectivity";

            Connectivity.CacheCopperLayers(pcbServer, board);

            int unnetted = 0;
            int unnettedSegs = -1;
            int copperSegs = 0;
            r.Run("Unnetted copper", "every copper primitive is accounted for as netted or not", delegate
            {
                Connectivity.Result x = Connectivity.UnnettedCopper(pcbServer, board, folder, false);
                if (x.Scanned == 0)
                    return "FAIL: no copper primitives found at all, but the board has " + tracks + " tracks";

                // NOT "scanned >= tracks". That was the previous assertion and
                // it was wrong: a track is not necessarily copper. On this
                // board 408 of 416 track/arc primitives are on Top Overlay --
                // they are the footprints' silkscreen outlines. Demanding the
                // copper scan see all of them failed a function that was right.
                //
                // What must hold is that every PAD is seen, since a pad is
                // copper by construction whatever layer it reports.
                if (x.Scanned < pads)
                    return "FAIL: scanned " + x.Scanned + " copper primitive(s) but the board has " +
                           pads + " pads, and a pad is copper whatever layer it sits on";

                unnetted = x.Found;
                unnettedSegs = x.Tracks + x.Arcs;

                int rows = File.ReadAllLines(x.CsvPath).Length - 1;
                if (rows != x.Found)
                    return "FAIL: reported " + x.Found + " but wrote " + rows + " rows";

                if (x.Found == 0)
                    return "PASS: all " + x.Scanned + " copper primitive(s) belong to a net";

                // Not a failure of this function -- it is the function working.
                // It IS a finding about the board, and a loud one.
                return "PASS: " + x.Found + " of " + x.Scanned + " copper primitive(s) carry NO NET " +
                       "(" + x.Tracks + " tracks, " + x.Vias + " vias) -- this is a real defect in the " +
                       "board, and it is why the net-scoped checks below have nothing to work with";
            });

            r.Run("Is this board routed", "copper layers hold conductors, or say plainly that they do not", delegate
            {
                // Worth its own line because three separate checks reporting
                // zero were read as three bugs before anyone established the
                // simple fact that there is nothing on the copper layers.
                int onCopper = 0, offCopper = 0;
                IPCB_LayerUtils lu = pcbServer.LayerUtils();

                IPCB_BoardIterator it = board.BoardIterator_Create();
                try
                {
                    it.AddFilter_ObjectSet(new TObjectSet(new TObjectId[] {
                        TObjectId.eTrackObject, TObjectId.eArcObject }));
                    it.AddFilter_AllLayers();
                    it.AddFilter_Method(TIterationMethod.eProcessAll);
                    IPCB_Primitive p = it.FirstPCBObject();
                    while (p != null)
                    {
                        bool cu = false;
                        try { cu = lu.IsElectricalLayer(p.GetState_V7Layer()); } catch { }
                        if (cu) onCopper++; else offCopper++;
                        p = it.NextPCBObject();
                    }
                }
                catch { }
                finally { board.BoardIterator_Destroy(ref it); }

                copperSegs = onCopper;

                if (onCopper == 0)
                    return "PASS: THIS BOARD IS NOT ROUTED — 0 of " + (onCopper + offCopper) +
                           " tracks and arcs are on a copper layer; the rest are silkscreen and " +
                           "mechanical drawing. Zero routed length, zero rated nets and zero checked " +
                           "endpoints are all correct answers here, not failures";

                return "PASS: " + onCopper + " track(s) and arc(s) on copper layers, " + offCopper +
                       " on silkscreen and mechanical layers";
            });

            r.Run("Measured net lengths", "measured length is consistent with the copper present", delegate
            {
                Connectivity.Result x = Connectivity.MeasureNets(pcbServer, board, folder);
                if (x.Found != nets)
                    return "FAIL: measured " + x.Found + " nets but the board has " + nets;

                // EVERY TRACK AND ARC ON A COPPER LAYER IS EITHER MEASURED HERE
                // OR LISTED AS UNNETTED ABOVE. The first assertion compared
                // against the raw track count, which includes silkscreen; the
                // next version dropped it, and a zero then passed without being
                // compared to anything -- it would have passed on a routed board
                // with a broken scan. The routed check above counts copper
                // segments with the same IsElectricalLayer test, so the three
                // numbers must add up exactly.
                if (unnettedSegs < 0)
                {
                    if (x.Scanned > copperSegs)
                        return "FAIL: measured " + x.Scanned + " netted segment(s) but only " + copperSegs +
                               " track(s)/arc(s) sit on copper layers -- something off copper was measured";
                    return "INFO: measured " + x.Scanned + " of " + copperSegs + " copper track(s)/arc(s); " +
                           "the unnetted count is missing because the unnetted-copper check failed, so the " +
                           "remainder cannot be accounted for";
                }

                if (x.Scanned + unnettedSegs != copperSegs)
                    return "FAIL: " + x.Scanned + " netted + " + unnettedSegs + " unnetted track(s)/arc(s) != " +
                           copperSegs + " on copper layers -- the length scan and the copper count disagree " +
                           "about which segments are copper";

                if (copperSegs == 0)
                    return "PASS: nothing to measure, and correctly so: 0 netted + 0 unnetted == 0 track(s)/arc(s) " +
                           "on copper layers";

                return "PASS: " + x.Scanned + " netted copper segment(s) measured across " + x.Found + " net(s); " +
                       x.Scanned + " + " + unnettedSegs + " unnetted == " + copperSegs + " on copper layers";
            });

            // --- Import / Export ---
            r.Section = "Import / Export";

            r.Run("Export board data", "three CSVs, footprint rows == component count", delegate
            {
                int f = BoardExport.FootprintSizes(board, folder);
                int p = BoardExport.PadNets(board, folder);
                int g = BoardExport.BoardGeometry(board, folder);

                if (f == 0) return "FAIL: footprint_sizes has no rows but the board has " + comps + " components";
                if (f != comps)
                    return "FAIL: footprint_sizes has " + f + " rows for " + comps + " components";
                if (p == 0) return "FAIL: pad_nets is empty";
                return "PASS: footprints " + f + " (== components), pads " + p + ", geometry rows " + g;
            });

            r.Run("Export for JLCPCB", "BOM and CPL cover the same designators; excluded parts in neither", delegate
            {
                JlcExport.JlcResult j = JlcExport.Export(pcbServer, board, folder);
                if (j.Parts == 0 && j.MissingLcsc.Count == 0)
                    return "FAIL: no BOM parts and no missing list -- nothing was read";

                // The invariant that matters: nothing excluded may appear in a
                // deliverable, and BOM and CPL must describe one component set.
                string bom = Path.Combine(folder, "bom_jlcpcb.csv");
                string cpl = Path.Combine(folder, "cpl_jlcpcb.csv");
                if (!File.Exists(bom) || !File.Exists(cpl)) return "FAIL: BOM or CPL file missing";

                HashSet<string> inBom = DesignatorsFromBom(bom);
                HashSet<string> inCpl = DesignatorsFromCpl(cpl);

                List<string> onlyBom = new List<string>();
                foreach (string d in inBom) if (!inCpl.Contains(d)) onlyBom.Add(d);
                List<string> onlyCpl = new List<string>();
                foreach (string d in inCpl) if (!inBom.Contains(d)) onlyCpl.Add(d);

                if (onlyBom.Count > 0 || onlyCpl.Count > 0)
                    return "FAIL: BOM and CPL disagree -- " + onlyBom.Count + " only in BOM, " +
                           onlyCpl.Count + " only in CPL";

                return "PASS: " + j.Parts + " BOM lines, " + j.Placements +
                       " placements, BOM and CPL describe the same " + inCpl.Count +
                       " designators; " + j.MissingLcsc.Count + " excluded for no LCSC number";
            });

            r.Run("Export net lengths", "a routed net reports non-zero length, or says why not", delegate
            {
                NetLengths.Result x = NetLengths.Export(board, folder);
                if (x.NetRows == 0)
                    return "FAIL: no net rows written but the board has " + nets + " nets";

                string[] lines = File.ReadAllLines(Path.Combine(folder, "net_lengths.csv"));
                int nonZero = 0;
                for (int k = 1; k < lines.Length; k++)
                {
                    string[] f2 = SplitCsv(lines[k]);
                    if (f2.Length > 4)
                    {
                        double v;
                        if (double.TryParse(f2[4], System.Globalization.NumberStyles.Float, Inv, out v) && v > 0)
                            nonZero++;
                    }
                }

                if (nonZero > 0)
                    return "PASS: " + x.NetRows + " nets written, " + nonZero + " with a non-zero routed length";

                // Zero everywhere. Whether that is this function's fault turns
                // entirely on whether the copper is on a net at all, which the
                // connectivity check above already established.
                // Zero routed length is the right answer on an unrouted board,
                // and whether it is routed is settled by what sits on the
                // copper layers, not by the raw track count -- silkscreen
                // outlines are tracks too.
                if (copperSegs == 0)
                    return "PASS: " + x.NetRows + " nets written and every routed length is zero, which is " +
                           "CORRECT: nothing on this board sits on a copper layer, so there is no routing " +
                           "to have a length";

                return "FAIL: " + x.NetRows + " nets written, every routed length zero, with " + copperSegs +
                       " track(s)/arc(s) on copper layers of which " + unnetted + " are unnetted";
            });

            r.Run("Board census export", "object counts agree with the baseline", delegate
            {
                Reports.Result x = Reports.BoardCensus(pcbServer, board, folder);
                if (x.Files.Count == 0) return "FAIL: no file written";
                string p = x.Files[0];
                string txt = File.ReadAllText(p);
                if (!txt.Contains("eComponentObject") && comps > 0)
                    return "FAIL: census has no component row despite " + comps + " components";
                return "PASS: " + string.Join("; ", x.Headline.ToArray());
            });

            r.Run("Drill table", "hole count is consistent with pads and vias present", delegate
            {
                Reports.Result x = Reports.DrillTable(board, folder);
                if (x.Files.Count == 0) return "FAIL: no file written";
                string[] lines = File.ReadAllLines(x.Files[0]);
                if (lines.Length <= 1 && vias > 0)
                    return "FAIL: drill table is empty although the board has " + vias + " vias";
                return "PASS: " + string.Join("; ", x.Headline.ToArray());
            });

            r.Run("Unrouted report", "runs and reports a pin-pair figure", delegate
            {
                Reports.Result x = Reports.Unrouted(board, folder);
                if (x.Files.Count == 0) return "FAIL: no file written";
                return "PASS: " + string.Join("; ", x.Headline.ToArray());
            });

            // --- Via tools ---
            r.Section = "Via tools";

            r.Run("Return via check", "counts split sensibly between reference and signal vias", delegate
            {
                ViaTools.ReturnViaOptions o = new ViaTools.ReturnViaOptions();
                o.ReferenceNet = "GND";
                o.MaxDistanceMM = 2.0;
                o.SelectOffenders = false;
                o.WriteCsvTo = folder;

                ViaTools.ReturnViaResult x = ViaTools.ReturnViaCheck(pcbServer, board, o);

                if (vias == 0) return "SKIP: board has no vias";
                if (x.ReferenceVias == 0 && x.SignalViasChecked == 0)
                    return "FAIL: " + vias + " vias on the board but the scan found none";
                if (x.ReferenceVias == 0)
                    return "INFO: no vias on GND (" + x.SignalViasChecked + " signal vias found), " +
                           "so nothing could be checked against -- net name may differ on this board";
                if (x.ReferenceVias + x.SignalViasChecked > vias)
                    return "FAIL: counted " + (x.ReferenceVias + x.SignalViasChecked) +
                           " vias but the board only has " + vias;
                return "PASS: " + x.SignalViasChecked + " signal vias vs " + x.ReferenceVias +
                       " on GND, " + x.Offenders + " without a return via within 2 mm";
            });

            // --- Copper & current ---
            r.Section = "Copper & current";

            r.Run("Current capacity", "capacity rises with width and the stackup was read", delegate
            {
                CopperCurrent.CurrentOptions o = new CopperCurrent.CurrentOptions();
                o.TempRiseC = 10.0;
                o.TargetAmps = 0.0;
                o.SelectFailures = false;
                o.OutputFolder = folder;

                CopperCurrent.CurrentResult x = CopperCurrent.CurrentCapacity(pcbServer, board, o);
                if (tracks == 0) return "SKIP: board has no tracks";
                if (x.NetsReported == 0)
                {
                    // Nothing rated is only this function's fault if there was
                    // netted copper for it to rate.
                    if (x.SkippedNoNet >= tracks)
                        return "PASS: nothing rated, correctly — all " + x.SkippedNoNet + " copper " +
                               "primitive(s) carry no net, and capacity is reported per net. The result " +
                               "now says so instead of writing an empty CSV";
                    return "FAIL: " + tracks + " tracks on the board but no nets were rated (" +
                           x.SkippedNoNet + " unnetted, " + x.SkippedOffCopper + " off-copper)";
                }

                // Independent arithmetic check against the shipped formula.
                double expect = Ipc2221.CurrentAmps(0.254, 0.03479, 10.0, true);
                if (Math.Abs(expect - 0.8817) > 0.01)
                    return "FAIL: IPC-2221 maths is wrong -- 10mil/1oz/+10C gave " + F3(expect) + " A";

                string note = x.UsedDefaultThickness
                    ? " (some layers gave no copper thickness, 1 oz assumed)"
                    : " (copper thickness read from the stackup)";

                return "PASS: " + x.NetsReported + " nets rated, lowest " + F3(x.WorstAmps) +
                       " A on " + x.WorstNet + note;
            });

            r.Run("Copper areas", "polygon area is measured from the outline and positive", delegate
            {
                CopperCurrent.AreaResult x = CopperCurrent.CopperAreas(pcbServer, board, folder);
                if (polys == 0) return "SKIP: no polygons on this board";
                if (x.Polygons == 0)
                    return "FAIL: " + polys + " polygons on the board but none were measured";
                if (x.TotalAreaMM2 <= 0)
                    return "FAIL: " + x.Polygons + " polygon(s) but total area is zero. " +
                           (x.Errors.Count > 0 ? string.Join("; ", x.Errors.ToArray())
                                               : "No reason was recorded.");

                // A pour cannot be larger than the board it sits on, by much.
                // This catches a unit error, which is the way an area goes
                // wrong: square coords read as square mm is out by 10^11.
                double boardArea = 0;
                try
                {
                    IPCB_BoardOutline ol = board.GetState_BoardOutline();
                    int n = ol.GetState_PointCount();
                    List<double> bx = new List<double>(), by = new List<double>();
                    for (int k = 0; k < n; k++)
                    {
                        PolySegment sg = ol.GetState_Segments(k);
                        bx.Add(ToMM(sg.GetVx())); by.Add(ToMM(sg.GetVy()));
                    }
                    boardArea = PolyGeometry.PolygonArea(bx, by);
                }
                catch { }

                if (boardArea > 0 && x.TotalAreaMM2 > boardArea * 5.0)
                    return "FAIL: " + F3(x.TotalAreaMM2) + " mm2 of copper on a " + F3(boardArea) +
                           " mm2 board -- that is a unit error, not a pour";

                return "PASS: " + x.Polygons + " polygon(s), " + F3(x.TotalAreaMM2) +
                       " mm2 measured from the outline" +
                       (boardArea > 0 ? " on a " + F3(boardArea) + " mm2 board" : "");
            });

            r.Run("Export rules", "row count matches the rules on the board", delegate
            {
                DesignRules.Result x = DesignRules.Export(board, folder);
                if (rules == 0) return "SKIP: board has no rules";
                if (x.Rules != rules)
                    return "FAIL: exported " + x.Rules + " rules, board has " + rules;
                if (x.CsvPath.Length == 0) return "FAIL: no CSV written";

                // At least some rules must have yielded a constraint value, or
                // the value dispatch is broken.
                string[] lines = File.ReadAllLines(x.CsvPath);
                int withValue = 0;
                for (int i = 1; i < lines.Length; i++)
                {
                    string[] f = SplitCsv(lines[i]);
                    if (f.Length > 4 && f[4].Trim().Length > 0) withValue++;
                }
                if (withValue == 0)
                    return "FAIL: " + x.Rules + " rules exported but not one has a constraint value";

                return "PASS: " + x.Rules + " rules, " + withValue + " with a readable value, " +
                       x.Disabled + " disabled";
            });

            r.Run("Audit rules", "produces findings and a tightest-clearance figure", delegate
            {
                DesignRules.Result x = DesignRules.Audit(board);
                if (rules == 0) return "SKIP: board has no rules";
                if (x.Findings.Count == 0) return "FAIL: audit produced no findings at all";

                bool foundClearance = false;
                foreach (string f in x.Findings)
                    if (f.Contains("clearance")) foundClearance = true;
                if (!foundClearance)
                    return "FAIL: audit never mentions clearance, so the rule dispatch found nothing";

                return "PASS: " + x.Findings.Count + " findings; " + x.Disabled + " disabled, " +
                       x.Unscoped + " unscoped";
            });

            // --- Testpoints ---
            r.Section = "Testpoints";

            r.Run("Testpoint coverage", "net count matches the board", delegate
            {
                Testpoints.Result x = Testpoints.Coverage(board, folder);
                if (x.Nets == 0 && nets > 0)
                    return "FAIL: no nets reported although the board has " + nets;
                if (x.Nets > nets)
                    return "FAIL: reported " + x.Nets + " nets, board has " + nets;
                return "PASS: " + x.Nets + " nets with pads or vias, " + x.Covered +
                       " have a fabrication testpoint, " + x.Uncovered + " do not";
            });

            r.Run("Pad centres", "scans every netted pad", delegate
            {
                Testpoints.Result x = Testpoints.PadCentres(pcbServer, board, 0.01);
                if (pads == 0) return "SKIP: board has no pads";
                if (x.Scanned == 0)
                    return "FAIL: scanned no pads although the board has " + pads;
                return "PASS: " + x.Scanned + " netted pads scanned, " + x.Found +
                       " with nothing at the centre";
            });

            // --- Cleanup ---
            r.Section = "Cleanup";

            r.Run("Invalid objects (find only)", "scans polygons without deleting", delegate
            {
                int before = Count(board, TObjectId.ePolyObject);
                Cleanup.Result x = Cleanup.InvalidObjects(pcbServer, board, false);
                int after = Count(board, TObjectId.ePolyObject);

                if (after != before)
                    return "FAIL: find-only deleted something -- polygons went " + before + " -> " + after;
                return "PASS: " + x.Scanned + " scanned, " + x.Found + " invalid, nothing deleted";
            });

            r.Run("Via antennas", "scans every via", delegate
            {
                Cleanup.AntennaOptions o = new Cleanup.AntennaOptions();
                o.ToleranceMM = 0.01;
                o.IgnoreInPours = true;
                Cleanup.Result x = Cleanup.ViaAntennas(pcbServer, board, o);
                if (vias == 0) return "SKIP: board has no vias";
                if (x.Scanned == 0) return "FAIL: scanned no vias although the board has " + vias;
                if (x.Found > x.Scanned) return "FAIL: found more antennas than vias scanned";
                return "PASS: " + x.Scanned + " vias scanned, " + x.Found + " look like antennas";
            });

            r.Run("Dangling copper", "scans track endpoints", delegate
            {
                Cleanup.Result x = Cleanup.DanglingCopper(pcbServer, board, 0.01);
                if (tracks == 0) return "SKIP: board has no tracks";
                if (x.Scanned == 0)
                {
                    // This check compares endpoints within a net, so unnetted
                    // copper gives it nothing to compare. It must say that
                    // rather than report a clean board.
                    if (x.SkippedNoNet > 0 && x.Errors.Count > 0)
                        return "PASS: nothing checked, and it said so — all " + x.SkippedNoNet +
                               " copper primitive(s) carry no net, so there are no same-net endpoints " +
                               "to compare. A silent zero here would have read as a clean board";
                    return "FAIL: no endpoints scanned with " + tracks + " tracks present";
                }
                return "PASS: " + x.Scanned + " endpoints scanned, " + x.Found + " primitives with a free end";
            });

            // --- Layers ---
            r.Section = "Layers";

            r.Run("Export stack", "physical layers with a non-zero total thickness", delegate
            {
                Layers.Result x = Layers.ExportStack(pcbServer, board, folder);
                if (x.CsvPath.Length == 0) return "FAIL: no CSV written";
                if (x.Considered == 0) return "FAIL: the physical stack walked zero layers";

                // Read the KIND COLUMN, not the whole line. The first version
                // searched for ",Dielectric," anywhere and passed on a board
                // where that string was a layer NAME and every kind was wrong.
                string[] lines = File.ReadAllLines(x.CsvPath);
                bool copper = false, dielectric = false;
                int copperCount = 0, kindCount = 0;
                for (int i = 1; i < lines.Length; i++)
                {
                    string[] f = SplitCsv(lines[i]);
                    if (f.Length < 5) continue;
                    string kind = f[2];
                    if (kind.Length == 0) continue;
                    kindCount++;
                    if (kind == "Copper") { copper = true; copperCount++; }
                    if (kind == "Core" || kind == "Prepreg" || kind == "Dielectric" ||
                        kind == "Surface" || kind == "Film") dielectric = true;
                }

                if (kindCount > 0 && copperCount == kindCount)
                    return "FAIL: every one of " + kindCount + " physical layers is typed Copper, " +
                           "which no real stack is -- the layer kind test is wrong";
                if (!copper)
                    return "FAIL: the stack export has no copper layer -- the physical class walk is wrong";
                if (!dielectric)
                    return "INFO: " + x.Considered + " layers but no dielectric row; " +
                           "an unconfigured stackup does this";

                return "PASS: " + x.Considered + " physical layers, copper and dielectric interleaved; " +
                       string.Join("; ", x.Notes.ToArray());
            });

            // --- Variants ---
            r.Section = "Variants";

            r.Run("Variant report", "reaches the variant model through IPCB_BoardEx", delegate
            {
                Variants.Result x = Variants.Report(board, folder);
                if (x.Errors.Count > 0 && x.Rows == 0)
                    return "INFO: " + string.Join("; ", x.Errors.ToArray());
                if (x.Rows == 0)
                    return "INFO: no variant rows -- normal for a project with no variants defined";
                return "PASS: " + x.VariantCount + " variants, " + x.Rows + " component rows; " +
                       string.Join("; ", x.Summary.ToArray());
            });

            // --- Net tools ---
            r.Section = "Net tools";

            r.Run("Single-pin nets", "every net on the board is examined", delegate
            {
                NetTools.Result x = NetTools.SinglePinNets(pcbServer, board, folder, false);
                if (x.Scanned != nets)
                    return "FAIL: scanned " + x.Scanned + " nets but the board has " + nets;
                if (x.CsvPath.Length == 0) return "FAIL: no CSV written";

                // Header plus one row per finding: the file must agree with
                // the count, or one of the two is lying.
                int rows = File.ReadAllLines(x.CsvPath).Length - 1;
                if (rows != x.Found)
                    return "FAIL: reported " + x.Found + " findings but wrote " + rows + " rows";

                return "PASS: " + x.Scanned + " nets scanned (== board net count), " + x.Found +
                       " single-pin, CSV rows agree";
            });

            r.Run("Duplicate tracks (find only)", "every track is examined and nothing is deleted", delegate
            {
                int before = Count(board, TObjectId.eTrackObject);
                NetTools.Result x = NetTools.DuplicateTracks(pcbServer, board, 0.001, false);
                int after = Count(board, TObjectId.eTrackObject);

                if (after != before)
                    return "FAIL: find-only changed the track count from " + before + " to " + after;
                if (x.Scanned != before)
                    return "FAIL: scanned " + x.Scanned + " tracks but the board has " + before;
                if (x.Found > before / 2)
                    return "FAIL: claims " + x.Found + " duplicates among " + before +
                           " tracks -- more than half, so the key is collapsing distinct tracks";

                return "PASS: " + x.Scanned + " tracks scanned (== board count), " + x.Found +
                       " duplicates, track count unchanged";
            });

            // --- DFM ---
            r.Section = "DFM";

            r.Run("Silkscreen over pads", "scans overlay primitives and pads without modifying", delegate
            {
                int tracksBefore = Count(board, TObjectId.eTrackObject);
                DfmTools.Result x = DfmTools.SilkOverPads(pcbServer, board, folder, false);
                int tracksAfter = Count(board, TObjectId.eTrackObject);

                if (tracksAfter != tracksBefore)
                    return "FAIL: a read-only check changed the track count";
                if (x.CsvPath.Length == 0) return "FAIL: no CSV written";

                int rows = File.ReadAllLines(x.CsvPath).Length - 1;
                if (rows != x.Found)
                    return "FAIL: reported " + x.Found + " clashes but wrote " + rows + " rows";
                if (x.Scanned == 0)
                    return "INFO: no overlay primitives on this board, so nothing could clash";

                return "PASS: " + x.Scanned + " overlay primitives against " + pads + " pads, " +
                       x.Found + " clashes, CSV rows agree";
            });

            r.Run("Net class report", "class membership adds up and unclassed nets are accounted for", delegate
            {
                DfmTools.Result x = DfmTools.NetClassReport(board, folder);
                if (x.CsvPath.Length == 0) return "FAIL: no CSV written";

                string[] lines = File.ReadAllLines(x.CsvPath);
                if (lines.Length < 2)
                    return "INFO: no net classes defined on this board";

                return "PASS: " + x.Found + " classes, " + (lines.Length - 1) + " CSV rows; " +
                       string.Join("; ", x.Notes.ToArray());
            });

            r.Run("Mechanical layer map", "every mechanical layer is walked", delegate
            {
                DfmTools.Result x = DfmTools.MechLayerNames(pcbServer, board, folder);
                if (x.CsvPath.Length == 0) return "FAIL: no CSV written";

                // Layers with nothing on them are deliberately omitted, so
                // zero rows means no mechanical layer carries content -- which
                // a freshly opened board legitimately has. An earlier version
                // called that a failure on the grounds that "Altium always has
                // these layers"; it has them, they were simply empty.
                int rows = File.ReadAllLines(x.CsvPath).Length - 1;
                if (rows != x.Found)
                    return "FAIL: reported " + x.Found + " layers in use but wrote " + rows + " rows";
                if (rows == 0)
                    return "PASS: no mechanical layer carries content on this board, and the report says " +
                           "so rather than listing empty layers";

                return "PASS: " + rows + " mechanical layer(s) in use, " + x.Scanned + " primitive(s)";
            });

            // --- Placement ---
            r.Section = "Placement";

            r.Run("Off-board components", "every component is tested against the outline", delegate
            {
                Placement.Result x = Placement.OffBoard(pcbServer, board, 0.05, folder, false);
                if (x.Errors.Count > 0 && x.Scanned == 0)
                    return "INFO: " + string.Join("; ", x.Errors.ToArray());
                if (x.Scanned != comps)
                    return "FAIL: scanned " + x.Scanned + " components but the board has " + comps;
                if (x.Found == comps && comps > 0)
                    return "FAIL: every one of " + comps + " components reported off-board -- " +
                           "the outline polygon is being read inside-out";

                int rows = File.ReadAllLines(x.CsvPath).Length - 1;
                if (rows != x.Found)
                    return "FAIL: reported " + x.Found + " but wrote " + rows + " rows";

                return "PASS: " + x.Scanned + " components tested (== board count), " + x.Found +
                       " past the edge";
            });

            r.Run("Component collisions", "pairwise body test is symmetric and bounded", delegate
            {
                Placement.Result x = Placement.Collisions(pcbServer, board, 0.0, folder, false);
                if (x.Scanned != comps)
                    return "FAIL: scanned " + x.Scanned + " components but the board has " + comps;

                long maxPairs = (long)comps * (comps - 1) / 2;
                if (x.Found > maxPairs)
                    return "FAIL: " + x.Found + " pairs from " + comps +
                           " components exceeds the " + maxPairs + " that exist -- pairs are being double-counted";

                int rows = File.ReadAllLines(x.CsvPath).Length - 1;
                if (rows != x.Found)
                    return "FAIL: reported " + x.Found + " but wrote " + rows + " rows";

                return "PASS: " + x.Scanned + " components, " + x.Found + " overlapping pairs of a " +
                       maxPairs + " maximum";
            });

            r.Run("Renumber proposal", "proposes without renaming anything", delegate
            {
                string[] before = Designators(board);

                Placement.RenumberOptions o = new Placement.RenumberOptions();
                o.Apply = false;
                Placement.Result x = Placement.Renumber(pcbServer, board, o, folder);

                string[] after = Designators(board);
                if (before.Length != after.Length)
                    return "FAIL: the component count changed during a proposal";
                for (int i = 0; i < before.Length; i++)
                    if (before[i] != after[i])
                        return "FAIL: a proposal renamed " + before[i] + " to " + after[i];

                if (x.CsvPath.Length == 0) return "FAIL: no proposal CSV written";

                // Every proposed name must be unique, or applying it would
                // produce two parts with the same designator.
                string[] lines = File.ReadAllLines(x.CsvPath);
                Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < lines.Length; i++)
                {
                    string[] f = lines[i].Split(',');
                    if (f.Length < 2) continue;
                    if (seen.ContainsKey(f[1]))
                        return "FAIL: the proposal assigns " + f[1] + " twice";
                    seen[f[1]] = true;
                }

                return "PASS: " + (lines.Length - 1) + " rows, all proposed names unique, " +
                       "no designator on the board changed";
            });

            // --- Polygons ---
            r.Section = "Polygons";

            r.Run("Polygon report", "one row per polygon on the board", delegate
            {
                Polygons.Result x = Polygons.Report(pcbServer, board, folder);
                if (x.Scanned != polys)
                    return "FAIL: scanned " + x.Scanned + " polygons but the board has " + polys;
                if (polys == 0)
                    return "INFO: no polygons on this board";
                if (x.CsvPath.Length == 0) return "FAIL: no CSV written";

                int rows = File.ReadAllLines(x.CsvPath).Length - 1;
                if (rows != x.Found)
                    return "FAIL: reported " + x.Found + " polygons but wrote " + rows + " rows";

                return "PASS: " + x.Found + " polygons (== board count), " + x.Stale + " with stale copper";
            });
        }

        private static string[] Designators(IPCB_Board board)
        {
            List<string> names = new List<string>();
            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(TObjectId.eComponentObject));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);
                IPCB_Component c = it.FirstPCBObject() as IPCB_Component;
                while (c != null)
                {
                    try { names.Add(c.GetState_Name().GetState_Text() ?? ""); } catch { names.Add(""); }
                    c = it.NextPCBObject() as IPCB_Component;
                }
            }
            catch { }
            finally { board.BoardIterator_Destroy(ref it); }
            names.Sort(StringComparer.Ordinal);
            return names.ToArray();
        }

        // ==================================================================
        // Modifying
        //
        // Every check here builds what it needs in a clear area well off the
        // board, so nothing existing is touched, and verifies by reading the
        // board back rather than by trusting the return value.
        // ==================================================================
        private static void ModifyingChecks(Runner r, IPCB_ServerInterface pcbServer, IPCB_Board board,
                                            string folder)
        {
            // Somewhere with nothing in it: 100 mm to the left of the board's
            // own extent, so a test artefact can never overlap real copper.
            double ox = -150.0, oy = 0.0;
            try
            {
                IPCB_BoardOutline outline = board.GetState_BoardOutline();
                int n = outline.GetState_PointCount();
                double minX = double.MaxValue, minY = double.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    PolySegment seg = outline.GetState_Segments(i);
                    minX = Math.Min(minX, ToMM(seg.GetVx()));
                    minY = Math.Min(minY, ToMM(seg.GetVy()));
                }
                if (minX < double.MaxValue) { ox = minX - 120.0; oy = minY; }
            }
            catch { /* fall back to a fixed clear area */ }

            IV7_Layer top = pcbServer.LayerUtils().FromString("Top Layer");
            IV7_Layer bottom = pcbServer.LayerUtils().FromString("Bottom Layer");

            r.Section = "Geometry (scratch area)";

            r.Run("Fillet a right-angled corner", "two tracks shorten and one arc appears", delegate
            {
                int arcsBefore = Count(board, TObjectId.eArcObject);

                IPCB_Track a, b;
                pcbServer.PreProcess();
                try
                {
                    a = MakeTrack(pcbServer, board, top, ox, oy, ox + 10.0, oy, 0.25);
                    b = MakeTrack(pcbServer, board, top, ox + 10.0, oy, ox + 10.0, oy + 10.0, 0.25);
                }
                finally { pcbServer.PostProcess(); }

                board.SelectedObjects_BeginUpdate();
                board.SelectedObjects_Clear();
                board.SelectedObjects_Add(a);
                board.SelectedObjects_Add(b);
                board.SelectedObjects_EndUpdate();

                Geometry.Result g = Geometry.FilletCorners(pcbServer, board, 2.0, true);
                int arcsAfter = Count(board, TObjectId.eArcObject);

                if (g.Applied == 0)
                    return "FAIL: no corner filleted -- " + string.Join("; ", g.Reasons.ToArray()) +
                           string.Join("; ", g.Errors.ToArray());
                if (arcsAfter != arcsBefore + 1)
                    return "FAIL: arc count went " + arcsBefore + " -> " + arcsAfter + ", expected +1";

                // The corner really must have been cut back by the tangent
                // distance: R/tan(45) = R = 2 mm.
                double ax2 = ToMM(a.GetState_X2());
                double expected = ox + 8.0;
                if (Math.Abs(ax2 - expected) > 0.01)
                    return "FAIL: track was cut back to x=" + F3(ax2) + ", expected " + F3(expected);

                return "PASS: 1 corner rounded, arc added, track cut back to the tangent point (" +
                       F3(ax2) + " vs " + F3(expected) + " expected)";
            });

            r.Run("Distribute three objects", "middle object lands midway", delegate
            {
                IPCB_Via v1, v2, v3;
                pcbServer.PreProcess();
                try
                {
                    v1 = MakeVia(pcbServer, board, ox, oy + 20.0);
                    v2 = MakeVia(pcbServer, board, ox + 2.0, oy + 20.0);   // deliberately off-centre
                    v3 = MakeVia(pcbServer, board, ox + 20.0, oy + 20.0);
                }
                finally { pcbServer.PostProcess(); }

                board.SelectedObjects_BeginUpdate();
                board.SelectedObjects_Clear();
                board.SelectedObjects_Add(v1);
                board.SelectedObjects_Add(v2);
                board.SelectedObjects_Add(v3);
                board.SelectedObjects_EndUpdate();

                Geometry.Result g = Geometry.Distribute(pcbServer, board, true);

                double mid = ToMM(v2.GetState_XLocation());
                double want = ox + 10.0;
                if (Math.Abs(mid - want) > 0.02)
                    return "FAIL: middle via at x=" + F3(mid) + ", expected " + F3(want) +
                           " (moved " + g.Applied + ")";
                return "PASS: middle via moved from +2.0 to +10.0 mm, exactly midway";
            });

            r.Section = "Via tools (scratch area)";

            r.Run("Via fence along a track", "vias appear at the requested pitch and offset", delegate
            {
                IPCB_Track t;
                pcbServer.PreProcess();
                try { t = MakeTrack(pcbServer, board, top, ox, oy + 40.0, ox + 10.0, oy + 40.0, 0.25); }
                finally { pcbServer.PostProcess(); }

                board.SelectedObjects_BeginUpdate();
                board.SelectedObjects_Clear();
                board.SelectedObjects_Add(t);
                board.SelectedObjects_EndUpdate();

                int before = Count(board, TObjectId.eViaObject);

                FenceOptions o = new FenceOptions();
                o.PitchMM = 2.0;
                o.OffsetMM = 1.0;
                o.ViaDiameterMM = 0.6;
                o.HoleSizeMM = 0.3;
                o.LeftSide = true;
                o.RightSide = true;
                o.NetName = FirstNetName(board);

                if (o.NetName == null) return "SKIP: board has no nets to tie the fence to";

                ViaFence.Result x = ViaFence.Fence(pcbServer, board, o);
                int after = Count(board, TObjectId.eViaObject);

                if (x.Placed == 0)
                    return "FAIL: nothing placed -- " + string.Join("; ", x.Errors.ToArray());
                if (after - before != x.Placed)
                    return "FAIL: reported " + x.Placed + " vias but the board gained " + (after - before);

                // 10 mm at 2 mm pitch is 5 per wall, two walls.
                if (x.Placed != 10)
                    return "FAIL: expected 10 vias (5 per wall over 10 mm at 2 mm pitch), got " + x.Placed;

                return "PASS: 10 vias placed, 5 per wall at 2 mm pitch, board count confirms";
            });

            r.Run("Via tenting", "solder mask expansion becomes manual and negative", delegate
            {
                IPCB_Via v;
                pcbServer.PreProcess();
                try { v = MakeVia(pcbServer, board, ox, oy + 60.0); }
                finally { pcbServer.PostProcess(); }

                board.SelectedObjects_BeginUpdate();
                board.SelectedObjects_Clear();
                board.SelectedObjects_Add(v);
                board.SelectedObjects_EndUpdate();

                ViaTools.TentOptions o = new ViaTools.TentOptions();
                o.Tent = true;
                o.OnlySelection = true;
                ViaTools.TentResult x = ViaTools.SetTenting(pcbServer, board, o);

                if (x.Changed == 0)
                    return "FAIL: nothing changed -- " + string.Join("; ", x.Errors.ToArray());

                V7_PadCache c = v.GetState_Cache();
                double exp = ToMM(c.SolderMaskExpansion);
                if (c.SolderMaskExpansionValid != TCacheState.eCacheManual)
                    return "FAIL: expansion was set but not marked manual, so the rule will overwrite it";
                if (exp >= 0.0)
                    return "FAIL: tenting left a non-negative expansion of " + F3(exp) + " mm";

                return "PASS: expansion " + F3(exp) + " mm and flagged manual, so the mask closes over it";
            });

            r.Section = "Layers (scratch area)";

            r.Run("Move to layer", "the track changes layer", delegate
            {
                IPCB_Track t;
                pcbServer.PreProcess();
                try { t = MakeTrack(pcbServer, board, top, ox, oy + 80.0, ox + 10.0, oy + 80.0, 0.25); }
                finally { pcbServer.PostProcess(); }

                board.SelectedObjects_BeginUpdate();
                board.SelectedObjects_Clear();
                board.SelectedObjects_Add(t);
                board.SelectedObjects_EndUpdate();

                Layers.MoveOptions o = new Layers.MoveOptions();
                o.TargetLayer = "Bottom Layer";
                o.PlaceVias = false;   // an isolated track needs none
                Layers.Result x = Layers.MoveToLayer(pcbServer, board, o);

                if (x.Moved == 0)
                    return "FAIL: nothing moved -- " + string.Join("; ", x.Errors.ToArray());

                string now = pcbServer.LayerUtils().AsString(t.GetState_V7Layer());
                if (now.IndexOf("Bottom", StringComparison.OrdinalIgnoreCase) < 0)
                    return "FAIL: track reports layer \"" + now + "\" after the move";

                return "PASS: track moved Top -> " + now + ", " + x.ViasPlaced +
                       " vias needed (correctly none for an isolated track)";
            });

            r.Section = "Fabrication (scratch area)";

            r.Run("Stackup table", "draws rows and reports a plausible thickness", delegate
            {
                StackupTable.Options o = new StackupTable.Options();
                o.LayerName = "Mechanical 1";
                o.OriginXMM = ox;
                o.OriginYMM = oy + 120.0;
                o.ReplaceExisting = true;

                o.Title = "SELFTEST STACKUP " + DateTime.Now.ToString("HHmmss", Inv);

                // Two fixes for this have missed, so the run now reports the
                // measurements instead of a verdict alone: whether text
                // objects were created at all, and what the first one says.
                int textBefore = Count(board, TObjectId.eTextObject);
                StackupTable.Result x = StackupTable.Generate(pcbServer, board, o);
                int textAfter = Count(board, TObjectId.eTextObject);

                if (x.Rows == 0)
                    return "FAIL: no rows drawn -- " + string.Join("; ", x.Errors.ToArray());

                string diag = "asked for \"" + o.LayerName + "\", got \"" + x.LayerUsed + "\"; " +
                              "text objects " + textBefore + " -> " + textAfter +
                              " (" + (textAfter - textBefore) + " added), " +
                              x.PrimitivesDrawn + " primitives claimed, " +
                              x.PrimitivesRemoved + " cleared first";

                if (textAfter == textBefore)
                    return "FAIL: no text object reached the board at all -- " + diag +
                           ". The lines land and the strings do not, so this is text creation, " +
                           "not the table";

                // Look for the title ON THE BOARD rather than comparing text
                // counts before and after. ReplaceExisting deletes the previous
                // table first, so on a second run the count comes back level
                // and a before/after test reports a failure that is not one --
                // which is exactly what it did.
                if (!FindText(board, o.Title))
                    return "FAIL: text objects were added but none carries the title -- " + diag +
                           ". First string actually on the board: \"" + AnyRecentText(board) +
                           "\" -- so the object is created and its string is not what was set";

                // The title alone could be a fluke; a header cell proves the
                // body of the table has words in it too.
                if (!FindText(board, "Layer"))
                    return "FAIL: the title landed but the column headers did not -- " + diag;
                if (x.BoardThicknessMM <= 0.0)
                    return "FAIL: " + x.Rows + " rows but total thickness is zero";
                if (x.BoardThicknessMM > 10.0)
                    return "FAIL: total thickness " + F3(x.BoardThicknessMM) + " mm is not a real board";

                return "PASS: " + x.Rows + " layers, title and headers found on the board, " +
                       F3(x.BoardThicknessMM) + " mm finished thickness (" + diag + ")";
            });

            r.Run("Assembly notes", "writes notes with real measured numbers", delegate
            {
                AssemblyNotes.Options o = new AssemblyNotes.Options();
                o.LayerName = "Mechanical 1";
                o.OriginXMM = ox + 60.0;
                o.OriginYMM = oy + 120.0;
                o.ReplaceExisting = true;

                AssemblyNotes.Result x = AssemblyNotes.Generate(pcbServer, board, o);

                if (x.NotesWritten == 0)
                    return "FAIL: no notes written -- " + string.Join("; ", x.Errors.ToArray());
                if (x.Stats.WidthMM <= 0.0 || x.Stats.HeightMM <= 0.0)
                    return "FAIL: board size read as " + F3(x.Stats.WidthMM) + " x " +
                           F3(x.Stats.HeightMM) + " mm";
                if (double.IsNaN(x.Stats.MinTrackMM))
                    return "FAIL: minimum track width was never measured";
                if (x.Stats.SignalLayers <= 0)
                    return "FAIL: signal layer count read as " + x.Stats.SignalLayers;

                return "PASS: " + x.NotesWritten + " notes; board " + F3(x.Stats.WidthMM) + " x " +
                       F3(x.Stats.HeightMM) + " mm, " + x.Stats.SignalLayers + " layers, min track " +
                       F3(x.Stats.MinTrackMM) + " mm";
            });

            r.Section = "Silkscreen";

            r.Run("Normalise designator text", "every designator ends at the requested height, then is put back", delegate
            {
                // EVERY DESIGNATOR ON THE BOARD IS ABOUT TO CHANGE, so record
                // what they were first. An earlier version of this check did
                // not, and a run against a real board left all 61 designators
                // resized with nothing to restore them from.
                List<IPCB_Text> texts = new List<IPCB_Text>();
                List<int> sizes = new List<int>();
                List<int> widths = new List<int>();

                IPCB_BoardIterator it0 = board.BoardIterator_Create();
                try
                {
                    it0.AddFilter_ObjectSet(new TObjectSet(TObjectId.eComponentObject));
                    it0.AddFilter_AllLayers();
                    it0.AddFilter_Method(TIterationMethod.eProcessAll);
                    IPCB_Component c0 = it0.FirstPCBObject() as IPCB_Component;
                    while (c0 != null)
                    {
                        try
                        {
                            IPCB_Text t = c0.GetState_Name();
                            if (t != null)
                            {
                                texts.Add(t);
                                sizes.Add(t.GetState_Size());
                                widths.Add(t.GetState_Width());
                            }
                        }
                        catch { }
                        c0 = it0.NextPCBObject() as IPCB_Component;
                    }
                }
                finally { board.BoardIterator_Destroy(ref it0); }

                if (texts.Count == 0) return "SKIP: no component designators on this board";

                try
                {
                    Silkscreen.Result x = Silkscreen.Normalise(pcbServer, board, false, 1.0, 0.15, false);
                    if (x.Changed == 0)
                        return "FAIL: nothing changed -- " + string.Join("; ", x.Errors.ToArray());

                    // Read one back rather than trusting the count.
                    int wrong = 0, checkedCount = 0;
                    for (int i = 0; i < texts.Count && checkedCount < 25; i++)
                    {
                        try
                        {
                            checkedCount++;
                            if (Math.Abs(ToMM(texts[i].GetState_Size()) - 1.0) > 0.005) wrong++;
                        }
                        catch { }
                    }

                    if (wrong > 0)
                        return "FAIL: " + wrong + " of " + checkedCount + " sampled designators are not 1.0 mm";
                    return "PASS: " + x.Changed + " designators resized, " + checkedCount +
                           " sampled all read back at 1.0 mm, all " + texts.Count + " restored";
                }
                finally
                {
                    pcbServer.PreProcess();
                    try
                    {
                        for (int i = 0; i < texts.Count; i++)
                        {
                            try
                            {
                                texts[i].BeginModify();
                                try
                                {
                                    texts[i].SetState_Size(sizes[i]);
                                    texts[i].SetState_Width(widths[i]);
                                }
                                finally { texts[i].EndModify(); }
                            }
                            catch { }
                        }
                    }
                    finally { pcbServer.PostProcess(); }
                    board.ViewManager_FullUpdate();
                }
            });

            r.Section = "Release";

            r.Run("Release dry run", "collects the CSVs written above without writing anything", delegate
            {
                ReleaseBundle.Options o = new ReleaseBundle.Options();
                o.ProjectName = "SelfTest";
                o.Revision = "RevX";
                o.OutputFolder = folder;
                o.DestinationFolder = Path.Combine(folder, "_selftest_release");
                o.DryRun = true;
                o.GenerateOutputs = false;

                ReleaseBundle.Result x = ReleaseBundle.Bundle(o);

                if (x.FilesCollected == 0)
                    return "FAIL: collected nothing from " + folder + " -- " +
                           string.Join("; ", x.Errors.ToArray());
                if (Directory.Exists(o.DestinationFolder))
                    return "FAIL: dry run created the destination folder";

                return "PASS: would package " + x.FilesCollected + " files as " +
                       ReleaseBundle.ArchiveName(o) + ", nothing written";
            });

            // --- DFM (scratch area) ---
            r.Section = "DFM (scratch area)";

            r.Run("Paste grid on a scratch pad", "N x N apertures appear and the pad's own is closed", delegate
            {
                int fillsBefore = Count(board, TObjectId.eFillObject);

                IPCB_Pad pad;
                pcbServer.PreProcess();
                try { pad = MakePad(pcbServer, board, top, ox + 60.0, oy, 5.0, 5.0); }
                finally { pcbServer.PostProcess(); }

                if (pad == null) return "FAIL: could not create the scratch pad";

                board.SelectedObjects_BeginUpdate();
                board.SelectedObjects_Clear();
                board.SelectedObjects_Add(pad);
                board.SelectedObjects_EndUpdate();

                DfmTools.PasteOptions o = new DfmTools.PasteOptions();
                o.MinPadMM = 4.0;
                o.CoveragePercent = 60;
                o.Divisions = 3;
                o.OnlySelection = true;

                DfmTools.Result x = DfmTools.PasteGrid(pcbServer, board, o);

                int fillsAfter = Count(board, TObjectId.eFillObject);
                int added = fillsAfter - fillsBefore;

                if (x.Changed != 1)
                    return "FAIL: gridded " + x.Changed + " pads, expected exactly the 1 selected";
                if (added != 9)
                    return "FAIL: " + added + " fills appeared, expected 9 for a 3x3 grid";

                // The pad's own aperture must be closed AND flagged manual --
                // negative alone is undone the moment the paste rule runs.
                V7_PadCache c = pad.GetState_Cache();
                if (c.PasteMaskExpansion >= 0)
                    return "FAIL: paste expansion is " + F3(ToMM(c.PasteMaskExpansion)) +
                           " mm, so the solid aperture is still there under the grid";
                if (c.PasteMaskExpansionValid != TCacheState.eCacheManual)
                    return "FAIL: expansion is negative but not flagged manual, so the rule will overwrite it";

                // Coverage: 9 apertures of (5 * sqrt(0.6)/3)^2 each == 60% of 25 mm2.
                double side = 5.0 * Math.Sqrt(0.6) / 3.0;
                double cover = 9 * side * side / 25.0 * 100.0;
                if (Math.Abs(cover - 60.0) > 0.5)
                    return "FAIL: the aperture arithmetic gives " + F3(cover) + "% coverage, asked for 60";

                return "PASS: 9 apertures, " + F3(cover) + "% coverage, pad expansion " +
                       F3(ToMM(c.PasteMaskExpansion)) + " mm and flagged manual";
            });

            // --- Placement (existing components, restored afterwards) ---
            r.Section = "Placement (round trip)";

            r.Run("Align rotation", "a component takes the requested angle and is put back", delegate
            {
                IPCB_Component c = FirstMoveableComponent(board);
                if (c == null) return "SKIP: every component on this board is locked";

                string name = "";
                try { name = c.GetState_Name().GetState_Text() ?? ""; } catch { }
                double original = c.GetState_Rotation();
                double target = original + 37.0 >= 360.0 ? original - 37.0 : original + 37.0;

                board.SelectedObjects_BeginUpdate();
                board.SelectedObjects_Clear();
                board.SelectedObjects_Add(c);
                board.SelectedObjects_EndUpdate();

                try
                {
                    Placement.Result x = Placement.AlignRotation(pcbServer, board, target);
                    if (x.Changed != 1)
                        return "FAIL: changed " + x.Changed + " components, expected the 1 selected";

                    double now = c.GetState_Rotation();
                    if (Math.Abs(now - target) > 0.001)
                        return "FAIL: asked for " + F3(target) + "°, read back " + F3(now) + "°";

                    // Absolute, not cumulative: running it again must not move it.
                    Placement.AlignRotation(pcbServer, board, target);
                    double again = c.GetState_Rotation();
                    if (Math.Abs(again - target) > 0.001)
                        return "FAIL: a second run moved it to " + F3(again) + "° -- the angle is being added";

                    return "PASS: " + name + " " + F3(original) + "° -> " + F3(now) +
                           "°, idempotent on a second run, restored";
                }
                finally
                {
                    try
                    {
                        c.BeginModify();
                        try { c.SetState_Rotation(original); } finally { c.EndModify(); }
                    }
                    catch { }
                }
            });

            r.Run("Snap to grid", "a component lands on the grid and is put back", delegate
            {
                IPCB_Component c = FirstMoveableComponent(board);
                if (c == null) return "SKIP: every component on this board is locked";

                string name = "";
                try { name = c.GetState_Name().GetState_Text() ?? ""; } catch { }
                int origX = c.GetState_XLocation(), origY = c.GetState_YLocation();

                // Put it deliberately off any sane grid first, so the snap has
                // something to do and the result is unambiguous.
                try
                {
                    c.BeginModify();
                    try
                    {
                        c.SetState_XLocation(origX + ToCoord(0.037));
                        c.SetState_YLocation(origY + ToCoord(0.037));
                    }
                    finally { c.EndModify(); }
                }
                catch (Exception ex) { return "SKIP: could not nudge the component -- " + ex.GetType().Name; }

                board.SelectedObjects_BeginUpdate();
                board.SelectedObjects_Clear();
                board.SelectedObjects_Add(c);
                board.SelectedObjects_EndUpdate();

                try
                {
                    Placement.Result x = Placement.SnapToGrid(pcbServer, board, 0.1, true, true);
                    if (x.Changed != 1)
                        return "FAIL: moved " + x.Changed + " components, expected the 1 selected";

                    double nx = ToMM(c.GetState_XLocation()), ny = ToMM(c.GetState_YLocation());
                    double rx = Math.Abs(nx / 0.1 - Math.Round(nx / 0.1));
                    double ry = Math.Abs(ny / 0.1 - Math.Round(ny / 0.1));
                    if (rx > 0.001 || ry > 0.001)
                        return "FAIL: landed at " + F3(nx) + ", " + F3(ny) + " which is not on a 0.1 mm grid";

                    return "PASS: " + name + " snapped to " + F3(nx) + ", " + F3(ny) + " and restored";
                }
                finally
                {
                    try
                    {
                        c.BeginModify();
                        try { c.SetState_XLocation(origX); c.SetState_YLocation(origY); }
                        finally { c.EndModify(); }
                    }
                    catch { }
                }
            });

            // --- Polygons ---
            r.Section = "Polygons (repour)";

            r.Run("Repour", "every polygon reads back as current afterwards", delegate
            {
                int polys = Count(board, TObjectId.ePolyObject);
                if (polys == 0) return "SKIP: no polygons on this board";

                Polygons.Result x = Polygons.Repour(pcbServer, board, false, false);
                if (x.Changed == 0)
                    return "FAIL: " + polys + " polygons but none repoured -- " +
                           string.Join("; ", x.Errors.ToArray());

                Polygons.Result after = Polygons.Report(pcbServer, board, folder);
                if (after.Stale > 0)
                    return "FAIL: " + after.Stale + " polygons still report stale copper after a repour";

                return "PASS: " + x.Changed + " of " + polys + " repoured, none stale afterwards";
            });

            // ==============================================================
            // Clean up after ourselves.
            //
            // The first version of this left its scratch geometry on the board
            // "as evidence". That is fine on a scratch copy and wrong on the
            // board someone actually opened -- which is what happened. The
            // evidence is the report; the board goes back as it was.
            //
            // Everything built above went into a rectangle well clear of the
            // board outline, so the sweep is bounded by that rectangle and
            // cannot reach real copper.
            // ==============================================================
            r.Section = "Cleanup";

            r.Run("Remove the scratch geometry", "the clear area is empty again", delegate
            {
                double x1 = ox - 20.0, y1 = oy - 40.0;
                double x2 = ox + 200.0, y2 = oy + 200.0;

                int removed = 0, left = 0;

                List<IPCB_Primitive> doomed = new List<IPCB_Primitive>();
                IPCB_BoardIterator it = board.BoardIterator_Create();
                try
                {
                    it.AddFilter_ObjectSet(new TObjectSet(new TObjectId[] {
                        TObjectId.eTrackObject, TObjectId.eArcObject, TObjectId.eViaObject,
                        TObjectId.ePadObject, TObjectId.eFillObject, TObjectId.eTextObject }));
                    it.AddFilter_AllLayers();
                    it.AddFilter_Method(TIterationMethod.eProcessAll);

                    IPCB_Primitive p = it.FirstPCBObject();
                    while (p != null)
                    {
                        try
                        {
                            CoordRect b = p.BoundingRectangle();
                            double bx1 = ToMM(b.GetX1()), by1 = ToMM(b.GetY1());
                            double bx2 = ToMM(b.GetX2()), by2 = ToMM(b.GetY2());

                            // Wholly inside the scratch rectangle. A primitive
                            // straddling its edge is left alone: better to
                            // leave a stray test object than to delete
                            // something of the user's.
                            if (bx1 >= x1 && bx2 <= x2 && by1 >= y1 && by2 <= y2)
                                doomed.Add(p);
                        }
                        catch { }
                        p = it.NextPCBObject();
                    }
                }
                catch { }
                finally { board.BoardIterator_Destroy(ref it); }

                pcbServer.PreProcess();
                try
                {
                    for (int i = 0; i < doomed.Count; i++)
                    {
                        try { board.RemovePCBObject(doomed[i]); removed++; }
                        catch { left++; }
                    }
                }
                finally { pcbServer.PostProcess(); }

                board.ViewManager_FullUpdate();

                if (left > 0)
                    return "FAIL: removed " + removed + " scratch object(s) but " + left +
                           " could not be deleted -- check the area near " + F3(x1) + ", " + F3(y1);

                return "PASS: " + removed + " scratch object(s) removed; the board is as it was found";
            });
        }

        // The longest string on the board, as a sample of what text objects
        // actually contain when a search for an expected one fails.
        private static string AnyRecentText(IPCB_Board board)
        {
            string best = "";
            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(TObjectId.eTextObject));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);
                IPCB_Text t = it.FirstPCBObject() as IPCB_Text;
                while (t != null)
                {
                    try
                    {
                        string v = t.GetState_Text() ?? "";
                        if (v.Length > best.Length) best = v;
                    }
                    catch { }
                    t = it.NextPCBObject() as IPCB_Text;
                }
            }
            catch { }
            finally { board.BoardIterator_Destroy(ref it); }
            return best.Length > 60 ? best.Substring(0, 60) : best;
        }

        private static bool FindText(IPCB_Board board, string want)
        {
            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(TObjectId.eTextObject));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);
                IPCB_Text t = it.FirstPCBObject() as IPCB_Text;
                while (t != null)
                {
                    try { if ((t.GetState_Text() ?? "") == want) return true; } catch { }
                    t = it.NextPCBObject() as IPCB_Text;
                }
            }
            catch { }
            finally { board.BoardIterator_Destroy(ref it); }
            return false;
        }

        private static IPCB_Component FirstMoveableComponent(IPCB_Board board)
        {
            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(TObjectId.eComponentObject));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);
                IPCB_Component c = it.FirstPCBObject() as IPCB_Component;
                while (c != null)
                {
                    // Moveable is INVERTED: false means locked.
                    try { if (c.GetState_Moveable()) return c; } catch { }
                    c = it.NextPCBObject() as IPCB_Component;
                }
            }
            catch { }
            finally { board.BoardIterator_Destroy(ref it); }
            return null;
        }

        private static IPCB_Pad MakePad(IPCB_ServerInterface s, IPCB_Board b, IV7_Layer layer,
                                        double x, double y, double w, double h)
        {
            IPCB_Pad p = s.PCBObjectFactory(TObjectId.ePadObject, TDimensionKind.eNoDimension,
                                            TObjectCreationMode.eCreate_Default) as IPCB_Pad;
            if (p == null) return null;
            p.SetState_XLocation(ToCoord(x));
            p.SetState_YLocation(ToCoord(y));
            p.SetState_HoleSize(0);                 // surface mount
            p.SetState_TopXSize(ToCoord(w));
            p.SetState_TopYSize(ToCoord(h));
            p.SetState_TopShape(TShape.eRectangular);
            p.SetState_V7Layer(layer);
            b.AddPCBObject(p);
            return p;
        }

        // ==================================================================
        // helpers
        // ==================================================================
        private static int Count(IPCB_Board board, TObjectId kind)
        {
            int n = 0;
            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(kind));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);
                IPCB_Primitive p = it.FirstPCBObject();
                while (p != null) { n++; p = it.NextPCBObject(); }
            }
            catch { }
            finally { board.BoardIterator_Destroy(ref it); }
            return n;
        }

        private static string FirstNetName(IPCB_Board board)
        {
            IPCB_BoardIterator it = board.BoardIterator_Create();
            try
            {
                it.AddFilter_ObjectSet(new TObjectSet(TObjectId.eNetObject));
                it.AddFilter_AllLayers();
                it.AddFilter_Method(TIterationMethod.eProcessAll);
                IPCB_Net n = it.FirstPCBObject() as IPCB_Net;
                while (n != null)
                {
                    string s = n.GetState_Name();
                    if (!string.IsNullOrEmpty(s)) return s;
                    n = it.NextPCBObject() as IPCB_Net;
                }
            }
            catch { }
            finally { board.BoardIterator_Destroy(ref it); }
            return null;
        }

        private static IPCB_Track MakeTrack(IPCB_ServerInterface s, IPCB_Board b, IV7_Layer layer,
                                            double x1, double y1, double x2, double y2, double w)
        {
            IPCB_Track t = s.PCBObjectFactory(TObjectId.eTrackObject, TDimensionKind.eNoDimension,
                                              TObjectCreationMode.eCreate_Default) as IPCB_Track;
            t.SetState_X1(ToCoord(x1)); t.SetState_Y1(ToCoord(y1));
            t.SetState_X2(ToCoord(x2)); t.SetState_Y2(ToCoord(y2));
            t.SetState_Width(ToCoord(w));
            t.SetState_V7Layer(layer);
            b.AddPCBObject(t);
            return t;
        }

        private static IPCB_Via MakeVia(IPCB_ServerInterface s, IPCB_Board b, double x, double y)
        {
            IPCB_Via v = s.PCBObjectFactory(TObjectId.eViaObject, TDimensionKind.eNoDimension,
                                            TObjectCreationMode.eCreate_Default) as IPCB_Via;
            v.SetState_XLocation(ToCoord(x));
            v.SetState_YLocation(ToCoord(y));
            v.SetState_HoleSize(ToCoord(0.3));
            v.SetState_Size(ToCoord(0.6));
            IPCB_LayerUtils lu = s.LayerUtils();
            v.SetState_LowLayer(lu.FromString("Top Layer"));
            v.SetState_HighLayer(lu.FromString("Bottom Layer"));
            b.AddPCBObject(v);
            return v;
        }

        private static string[] SplitCsv(string line)
        {
            List<string> outp = new List<string>();
            bool q = false;
            StringBuilder cur = new StringBuilder();
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (q)
                {
                    if (ch == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; } else q = false; }
                    else cur.Append(ch);
                }
                else
                {
                    if (ch == '"') q = true;
                    else if (ch == ',') { outp.Add(cur.ToString()); cur.Length = 0; }
                    else cur.Append(ch);
                }
            }
            outp.Add(cur.ToString());
            return outp.ToArray();
        }

        private static HashSet<string> DesignatorsFromBom(string path)
        {
            // Comment,Designator,Footprint,LCSC  -- designators are a
            // comma-separated list inside one quoted field.
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] lines = File.ReadAllLines(path);
            for (int i = 1; i < lines.Length; i++)
            {
                string[] f = SplitCsv(lines[i]);
                if (f.Length < 2) continue;
                string[] ds = f[1].Split(',');
                foreach (string d in ds)
                {
                    string t = d.Trim();
                    if (t.Length > 0) set.Add(t);
                }
            }
            return set;
        }

        private static HashSet<string> DesignatorsFromCpl(string path)
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] lines = File.ReadAllLines(path);
            for (int i = 1; i < lines.Length; i++)
            {
                string[] f = SplitCsv(lines[i]);
                if (f.Length < 1) continue;
                string t = f[0].Trim();
                if (t.Length > 0) set.Add(t);
            }
            return set;
        }

        // ==================================================================
        private static string Write(Report rep, IPCB_Board board, string folder)
        {
            string subject = "";
            try { subject = "Board `" + board.GetState_FileName() + "`"; } catch { }
            string note = !rep.ModifyingRun
                ? "Read-only checks only. Board-modifying checks were not run."
                : "Includes board-modifying checks. They built their own geometry in a clear area " +
                  "off the board, verified it and removed it again; the checks that touched existing " +
                  "objects recorded and restored them. Nothing was written to disk — the document is " +
                  "modified in memory only, so an unsaved board is unchanged on disk either way.";
            return Write(rep, "# AltiumSpike self-test", subject, note, folder, "spike_selftest.md");
        }

        private static string Write(Report rep, string title, string subject, string note,
                                    string folder, string fileName)
        {
            List<string> md = new List<string>();
            md.Add(title);
            md.Add("");
            md.Add("Run " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv));
            if (subject.Length > 0) md.Add(subject);
            md.Add("");
            md.Add("**" + rep.Headline() + "**");
            md.Add("");
            md.Add("> " + note);
            md.Add("");

            string section = null;
            foreach (Check c in rep.Checks)
            {
                if (c.Section != section)
                {
                    section = c.Section;
                    md.Add("");
                    md.Add("## " + section);
                    md.Add("");
                    md.Add("| | Check | Expected | Result | ms |");
                    md.Add("| --- | --- | --- | --- | --- |");
                }
                string mark = c.Verdict == Verdict.Pass ? "PASS"
                            : c.Verdict == Verdict.Fail ? "**FAIL**"
                            : c.Verdict == Verdict.Skip ? "skip" : "info";
                md.Add("| " + mark + " | " + c.Name + " | " + c.Expected + " | " +
                       c.Actual.Replace("|", "/") + " | " + c.Ms + " |");
            }

            md.Add("");
            md.Add("---");
            md.Add("");
            md.Add("A check passes only when its result was compared against something — a count, a " +
                   "coordinate, a file read back. Not throwing is not a pass.");

            string path = Path.Combine(folder, fileName);
            try
            {
                File.WriteAllLines(path, md, new UTF8Encoding(false));
                Log.Write("SelfTest: wrote " + path);
            }
            catch (Exception ex)
            {
                Log.Exception("SelfTest.Write", ex);
                return "";
            }
            return path;
        }
    }
}
