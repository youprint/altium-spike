// SchPlacementPlan.cs
//
// The Altium-free half of "place components and nets on a schematic from CSV".
// Everything here is arithmetic or text: reading the two CSVs, deciding what
// is wrong with them before anything touches a sheet, mapping an LCSC number
// to the symbol name YouEDA gave it, laying out rows that have no position,
// and working out where a pin's wire stub and net label go. No SCH, DXP or
// EDP type appears in this file, so tests/SchPlacementTests compiles it
// directly and every rule below is asserted there.
//
// TWO INPUT FILES.
//
//   components.csv   Designator, and LCSC or LibRef (or both), then optional
//                    Library, X, Y, Rotation, Mirror. X and Y are in mil
//                    unless the header says mm ("X mm", "Y (mm)").
//
//   nets.csv         Net, Designator, Pin -- one row per connection. A single
//                    Node column ("R1.2") works in place of Designator + Pin.
//                    Pin matches the pin's number first, then its name.
//
// NOTHING IS PLACED UNLESS BOTH FILES ARE CLEAN. A half-placed schematic from
// a CSV with one bad row is harder to recover from than a list of errors, so
// Parse collects every problem and the caller refuses to start if there is one.
//
// NET LABELS, NOT WIRES BETWEEN PINS. Each connected pin gets a short wire
// stub pointing away from the body, with the net label sitting on it. That is
// electrically exact, cannot cross a symbol, and cannot make an accidental
// connection -- a real router could do all three.
//
// YOUEDA NAMING. YouEDA's README says the SchLib is keyed by LCSC number; the
// code has not done that since 1.2.8. Each symbol is named from the EasyEDA
// component name via SafeLibraryName (mirrored here character for character),
// and the LCSC number is only a hidden "LCSC Part" parameter. The mapping from
// one to the other comes from YouEDA's family-classification.csv, and the
// caller then checks the resolved name really is in the library.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AltiumSpike
{
    public static class SchPlacementPlan
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public const double MilPerMM = 1000.0 / 25.4;

        public sealed class Part
        {
            public int Line;
            public string Designator = "";
            public string Lcsc = "";
            public string LibRef = "";      // as given, then as resolved
            public string Library = "";     // as given; empty means the default library
            public bool HasPosition;        // X and Y came from the CSV
            public bool AutoPlaced;         // X and Y were assigned by AutoLayout
            public double XMil, YMil;
            public int Rotation;            // 0, 90, 180 or 270
            public bool Mirror;
        }

        public sealed class Node
        {
            public int Line;
            public string Net = "";
            public string Designator = "";
            public string Pin = "";
        }

        public sealed class Plan
        {
            public List<Part> Parts = new List<Part>();
            public List<Node> Nodes = new List<Node>();
            public List<string> Errors = new List<string>();
            public List<string> Warnings = new List<string>();
            public int NetCount;

            public bool Ok { get { return Errors.Count == 0; } }
        }

        // ==================================================================
        // Headers
        // ==================================================================

        private static readonly string[] DesignatorHeaders = { "designator", "ref", "refdes", "ref des", "reference" };
        private static readonly string[] LcscHeaders = { "lcsc", "lcsc part", "lcsc part #", "lcsc part number", "lcsc #", "jlcpcb part #", "jlcpcb part" };
        private static readonly string[] LibRefHeaders = { "libref", "lib ref", "lib reference", "library reference", "symbol" };
        private static readonly string[] LibraryHeaders = { "library", "library path", "lib path", "schlib" };
        private static readonly string[] RotationHeaders = { "rotation", "rot", "orientation", "angle" };
        private static readonly string[] MirrorHeaders = { "mirror", "mirrored" };
        private static readonly string[] NetHeaders = { "net", "net name", "netname" };
        private static readonly string[] PinHeaders = { "pin", "pin number", "pin designator", "pin name" };
        private static readonly string[] NodeHeaders = { "node", "pin ref", "connection" };

        // Lower case, trimmed, runs of spaces collapsed, and a UTF-8 byte order
        // mark dropped -- Excel writes one and it would otherwise hide the
        // first column's name.
        public static string NormHeader(string h)
        {
            string s = (h ?? "").Replace("﻿", "").Trim().ToLowerInvariant();
            return Regex.Replace(s, @"\s+", " ");
        }

        private static int Find(List<string> header, string[] names)
        {
            for (int i = 0; i < header.Count; i++)
            {
                string h = NormHeader(header[i]);
                for (int k = 0; k < names.Length; k++)
                    if (h == names[k]) return i;
            }
            return -1;
        }

        // "X", "X mil", "X (mm)", "x_mm" -> the column, and whether it is mm.
        private static int FindAxis(List<string> header, string axis, out bool mm)
        {
            mm = false;
            for (int i = 0; i < header.Count; i++)
            {
                string h = NormHeader(header[i]);
                if (h == axis || h.StartsWith(axis + " ") || h.StartsWith(axis + "(") ||
                    h.StartsWith(axis + "_") || h.StartsWith(axis + "["))
                {
                    mm = h.Contains("mm");
                    return i;
                }
            }
            return -1;
        }

        private static string At(List<string> f, int i)
        {
            return (i >= 0 && i < f.Count) ? (f[i] ?? "").Trim() : "";
        }

        private static bool Blank(List<string> f)
        {
            for (int i = 0; i < f.Count; i++)
                if (!string.IsNullOrWhiteSpace(f[i])) return false;
            return true;
        }

        private static bool Comment(List<string> f)
        {
            return f.Count > 0 && (f[0] ?? "").TrimStart().StartsWith("#");
        }

        private static bool TryNum(string s, out double v)
        {
            return double.TryParse((s ?? "").Trim(), NumberStyles.Float, Inv, out v);
        }

        // ==================================================================
        // Parse
        // ==================================================================

        // Rows are already split into fields (CsvIo.Split on the plugin side),
        // header row first. Line numbers in messages are 1-based file lines.
        public static Plan Parse(List<List<string>> componentRows, List<List<string>> netRows)
        {
            Plan p = new Plan();
            ParseComponents(p, componentRows);
            ParseNets(p, netRows);
            return p;
        }

        private static void ParseComponents(Plan p, List<List<string>> rows)
        {
            if (rows == null || rows.Count == 0)
            {
                p.Errors.Add("components CSV is empty");
                return;
            }

            List<string> h = rows[0];
            int cDes = Find(h, DesignatorHeaders);
            int cLcsc = Find(h, LcscHeaders);
            int cRef = Find(h, LibRefHeaders);
            int cLib = Find(h, LibraryHeaders);
            int cRot = Find(h, RotationHeaders);
            int cMir = Find(h, MirrorHeaders);
            bool xmm, ymm;
            int cX = FindAxis(h, "x", out xmm);
            int cY = FindAxis(h, "y", out ymm);

            if (cDes < 0)
                p.Errors.Add("components CSV has no Designator column (header: " + string.Join(", ", h) + ")");
            if (cLcsc < 0 && cRef < 0)
                p.Errors.Add("components CSV needs an LCSC or a LibRef column (header: " + string.Join(", ", h) + ")");
            if ((cX < 0) != (cY < 0))
                p.Errors.Add("components CSV has " + (cX < 0 ? "a Y" : "an X") + " column but no " +
                             (cX < 0 ? "X" : "Y") + " column");
            if (p.Errors.Count > 0) return;

            Dictionary<string, int> seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (int i = 1; i < rows.Count; i++)
            {
                List<string> f = rows[i];
                int line = i + 1;
                if (Blank(f) || Comment(f)) continue;

                Part part = new Part();
                part.Line = line;
                part.Designator = At(f, cDes);
                part.Lcsc = NormalizeLcsc(At(f, cLcsc));
                part.LibRef = At(f, cRef);
                part.Library = At(f, cLib);

                if (part.Designator.Length == 0)
                {
                    p.Errors.Add("components line " + line + ": no designator");
                    continue;
                }

                int first;
                if (seen.TryGetValue(part.Designator, out first))
                {
                    p.Errors.Add("components line " + line + ": designator " + part.Designator +
                                 " is already used on line " + first);
                    continue;
                }
                seen[part.Designator] = line;

                string rawLcsc = At(f, cLcsc);
                if (rawLcsc.Length > 0 && part.Lcsc.Length == 0)
                    p.Errors.Add("components line " + line + ": \"" + rawLcsc + "\" is not an LCSC number (C followed by digits)");
                if (part.Lcsc.Length == 0 && part.LibRef.Length == 0 && rawLcsc.Length == 0)
                    p.Errors.Add("components line " + line + ": " + part.Designator + " has neither an LCSC number nor a LibRef");

                string sx = At(f, cX), sy = At(f, cY);
                if (sx.Length > 0 || sy.Length > 0)
                {
                    double x, y;
                    if (sx.Length == 0 || sy.Length == 0)
                        p.Errors.Add("components line " + line + ": " + part.Designator +
                                     " has " + (sx.Length == 0 ? "Y" : "X") + " but no " + (sx.Length == 0 ? "X" : "Y") +
                                     " -- give both, or neither to have it laid out automatically");
                    else if (!TryNum(sx, out x) || !TryNum(sy, out y))
                        p.Errors.Add("components line " + line + ": position \"" + sx + "\", \"" + sy + "\" is not a number pair");
                    else
                    {
                        part.XMil = xmm ? x * MilPerMM : x;
                        part.YMil = ymm ? y * MilPerMM : y;
                        part.HasPosition = true;
                    }
                }

                string sr = At(f, cRot);
                if (sr.Length > 0)
                {
                    int rot;
                    if (!TryRotation(sr, out rot))
                        p.Errors.Add("components line " + line + ": rotation \"" + sr + "\" must be 0, 90, 180 or 270");
                    else part.Rotation = rot;
                }

                string sm = At(f, cMir);
                if (sm.Length > 0)
                {
                    bool mir;
                    if (!TryFlag(sm, out mir))
                        p.Errors.Add("components line " + line + ": mirror \"" + sm + "\" must be yes/no, true/false or 1/0");
                    else part.Mirror = mir;
                }

                p.Parts.Add(part);
            }

            if (p.Parts.Count == 0 && p.Errors.Count == 0)
                p.Errors.Add("components CSV has a header but no component rows");
        }

        private static void ParseNets(Plan p, List<List<string>> rows)
        {
            if (rows == null || rows.Count == 0)
            {
                p.Warnings.Add("no nets given -- components will be placed unconnected");
                return;
            }

            List<string> h = rows[0];
            int cNet = Find(h, NetHeaders);
            int cDes = Find(h, DesignatorHeaders);
            int cPin = Find(h, PinHeaders);
            int cNode = Find(h, NodeHeaders);

            if (cNet < 0)
            {
                p.Errors.Add("nets CSV has no Net column (header: " + string.Join(", ", h) + ")");
                return;
            }
            if ((cDes < 0 || cPin < 0) && cNode < 0)
            {
                p.Errors.Add("nets CSV needs Designator and Pin columns, or a Node column like R1.2 (header: " +
                             string.Join(", ", h) + ")");
                return;
            }

            HashSet<string> placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Part part in p.Parts) placed.Add(part.Designator);

            // designator.pin -> the net it was first given, and on which line
            Dictionary<string, string> netOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> lineOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> perNet = new Dictionary<string, int>(StringComparer.Ordinal);
            List<string> netOrder = new List<string>();

            for (int i = 1; i < rows.Count; i++)
            {
                List<string> f = rows[i];
                int line = i + 1;
                if (Blank(f) || Comment(f)) continue;

                Node n = new Node();
                n.Line = line;
                n.Net = At(f, cNet);

                if (cDes >= 0 && cPin >= 0 && (At(f, cDes).Length > 0 || At(f, cPin).Length > 0))
                {
                    n.Designator = At(f, cDes);
                    n.Pin = At(f, cPin);
                }
                else if (cNode >= 0)
                {
                    string node = At(f, cNode);
                    int dot = node.LastIndexOf('.');
                    if (dot <= 0 || dot == node.Length - 1)
                    {
                        p.Errors.Add("nets line " + line + ": node \"" + node + "\" is not Designator.Pin (e.g. R1.2)");
                        continue;
                    }
                    n.Designator = node.Substring(0, dot).Trim();
                    n.Pin = node.Substring(dot + 1).Trim();
                }

                if (n.Net.Length == 0) { p.Errors.Add("nets line " + line + ": no net name"); continue; }
                if (n.Designator.Length == 0) { p.Errors.Add("nets line " + line + ": no designator"); continue; }
                if (n.Pin.Length == 0) { p.Errors.Add("nets line " + line + ": no pin for " + n.Designator); continue; }

                if (!placed.Contains(n.Designator))
                {
                    p.Warnings.Add("nets line " + line + ": " + n.Designator + " is not in the components CSV -- " +
                                   n.Designator + "." + n.Pin + " (" + n.Net + ") skipped; only components placed " +
                                   "in this run are labelled");
                    continue;
                }

                string key = n.Designator + "." + n.Pin;
                string prev;
                if (netOf.TryGetValue(key, out prev))
                {
                    if (string.Equals(prev, n.Net, StringComparison.Ordinal))
                        p.Warnings.Add("nets line " + line + ": " + key + " is listed in " + n.Net +
                                       " twice (first on line " + lineOf[key] + ") -- labelled once");
                    else
                        p.Errors.Add("nets line " + line + ": " + key + " is in " + n.Net + " but line " + lineOf[key] +
                                     " puts it in " + prev + " -- that would short the two nets");
                    continue;
                }
                netOf[key] = n.Net;
                lineOf[key] = line;

                int c;
                if (!perNet.TryGetValue(n.Net, out c)) { c = 0; netOrder.Add(n.Net); }
                perNet[n.Net] = c + 1;

                p.Nodes.Add(n);
            }

            foreach (string net in netOrder)
                if (perNet[net] == 1)
                    p.Warnings.Add("net " + net + " has only one pin -- it will be labelled, but connects nothing");
            p.NetCount = netOrder.Count;
        }

        public static bool TryRotation(string s, out int rot)
        {
            rot = 0;
            double d;
            if (!TryNum(s, out d)) return false;
            if (Math.Abs(d - Math.Round(d)) > 1e-9) return false;
            int r = (int)Math.Round(d);
            if (r % 90 != 0) return false;
            rot = ((r % 360) + 360) % 360;
            return true;
        }

        public static bool TryFlag(string s, out bool v)
        {
            string t = (s ?? "").Trim().ToLowerInvariant();
            if (t == "" || t == "0" || t == "no" || t == "n" || t == "false") { v = false; return true; }
            if (t == "1" || t == "yes" || t == "y" || t == "true" || t == "x" || t == "mirror" || t == "mirrored")
            { v = true; return true; }
            v = false;
            return false;
        }

        // "c12530", " C12530 " -> "C12530". Anything else -> "".
        public static string NormalizeLcsc(string s)
        {
            string t = (s ?? "").Trim().ToUpperInvariant();
            return Regex.IsMatch(t, @"^C\d+$") ? t : "";
        }

        // ==================================================================
        // Automatic layout for rows with no position
        // ==================================================================

        // Row-major on a grid, in natural designator order (C2 before C10),
        // snapped to 100 mil. If any row does carry a position, the grid starts
        // one pitch to the right of the right-most of them, so the two cannot
        // land on top of each other.
        public static void AutoLayout(Plan p, double originXMil, double originYMil, double pitchMil, int columns)
        {
            if (pitchMil <= 0) throw new ArgumentException("pitch must be positive");
            if (columns < 1) columns = 1;

            List<Part> todo = new List<Part>();
            double maxX = double.NegativeInfinity;
            foreach (Part part in p.Parts)
            {
                if (part.HasPosition) maxX = Math.Max(maxX, part.XMil);
                else todo.Add(part);
            }
            if (todo.Count == 0) return;

            double x0 = originXMil;
            if (!double.IsNegativeInfinity(maxX)) x0 = Math.Max(x0, maxX + pitchMil);
            x0 = Snap(x0, 100.0);
            double y0 = Snap(originYMil, 100.0);

            todo.Sort(delegate (Part a, Part b) { return CompareDesignators(a.Designator, b.Designator); });

            for (int i = 0; i < todo.Count; i++)
            {
                int col = i % columns, row = i / columns;
                todo[i].XMil = Snap(x0 + col * pitchMil, 100.0);
                todo[i].YMil = Snap(y0 + row * pitchMil, 100.0);
                todo[i].AutoPlaced = true;
            }
        }

        public static double Snap(double v, double grid)
        {
            return Math.Round(v / grid, MidpointRounding.AwayFromZero) * grid;
        }

        // Natural order: letters, then the number, then anything after it.
        // R2 < R10 < RN1 < U1; U1A < U1B.
        public static int CompareDesignators(string a, string b)
        {
            string pa, sa, pb, sb;
            long na, nb;
            SplitDesignator(a, out pa, out na, out sa);
            SplitDesignator(b, out pb, out nb, out sb);
            int c = string.Compare(pa, pb, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            c = na.CompareTo(nb);
            if (c != 0) return c;
            c = string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            return string.Compare(a, b, StringComparison.Ordinal);
        }

        private static void SplitDesignator(string d, out string prefix, out long number, out string rest)
        {
            string s = d ?? "";
            int i = 0;
            while (i < s.Length && !char.IsDigit(s[i])) i++;
            prefix = s.Substring(0, i);
            int j = i;
            while (j < s.Length && char.IsDigit(s[j])) j++;
            number = -1;
            if (j > i)
            {
                long n;
                if (long.TryParse(s.Substring(i, j - i), NumberStyles.None, Inv, out n)) number = n;
            }
            rest = s.Substring(j);
        }

        // ==================================================================
        // YouEDA symbol names
        // ==================================================================

        // Character-for-character copy of YouEDA's AltiumSchExporter.SafeLibraryName.
        // If YouEDA changes its rule this must change with it, and the harness
        // pins the behaviour so a drift shows up as a failing case.
        public static string SafeLibraryName(string name)
        {
            string cleaned = Regex.Replace((name ?? string.Empty).Trim(), @"[^A-Za-z0-9_+\-.]", "_");
            cleaned = Regex.Replace(cleaned, @"_+", "_").Trim('_');
            return string.IsNullOrWhiteSpace(cleaned) ? "YouEDA_Component" : cleaned.Substring(0, Math.Min(cleaned.Length, 120));
        }

        // Adds LCSC -> symbol name pairs from one family-classification.csv
        // (LCSC Part, Family, Confidence, Evidence, Name, ...). Pairs already in
        // the map win, so feed the newest file first.
        public static int AddClassification(Dictionary<string, string> map, List<List<string>> rows)
        {
            if (rows == null || rows.Count < 2) return 0;
            int cL = -1, cN = -1;
            for (int i = 0; i < rows[0].Count; i++)
            {
                string h = NormHeader(rows[0][i]);
                if (h == "lcsc part" || h == "lcsc") cL = i;
                else if (h == "name") cN = i;
            }
            if (cL < 0 || cN < 0) return 0;

            int added = 0;
            for (int i = 1; i < rows.Count; i++)
            {
                string lcsc = NormalizeLcsc(At(rows[i], cL));
                string name = At(rows[i], cN);
                if (lcsc.Length == 0 || name.Length == 0 || map.ContainsKey(lcsc)) continue;
                map[lcsc] = SafeLibraryName(name);
                added++;
            }
            return added;
        }

        // Settles part.LibRef. libNames is the library's own component list
        // (case-insensitive), or null when it could not be read -- then the
        // name is taken on trust and the placement itself is the check.
        public static bool ResolveLibRef(Part part, Dictionary<string, string> lcscMap,
                                         HashSet<string> libNames, string libLabel, out string error)
        {
            error = null;
            string candidate;
            string how;

            if (part.LibRef.Length > 0)
            {
                candidate = part.LibRef;
                how = "LibRef " + part.LibRef;
            }
            else
            {
                string mapped;
                if (lcscMap != null && lcscMap.TryGetValue(part.Lcsc, out mapped))
                {
                    candidate = mapped;
                    how = "LCSC " + part.Lcsc + " -> " + mapped;
                }
                else if (libNames != null && libNames.Contains(part.Lcsc))
                {
                    // YouEDA before 1.2.8 named symbols by LCSC number.
                    candidate = part.Lcsc;
                    how = "LCSC " + part.Lcsc + " (an LCSC-named symbol)";
                }
                else
                {
                    error = part.Designator + ": LCSC " + part.Lcsc + " has no symbol name -- no YouEDA " +
                            "family-classification.csv lists it. Give its LibRef in the CSV, or import it in YouEDA " +
                            "with Run full import";
                    return false;
                }
            }

            if (libNames != null)
            {
                string actual = null;
                foreach (string n in libNames)
                    if (string.Equals(n, candidate, StringComparison.OrdinalIgnoreCase)) { actual = n; break; }
                if (actual == null)
                {
                    error = part.Designator + ": " + how + ", but " + libLabel + " has no symbol called " + candidate +
                            " (" + libNames.Count + " symbols in it)";
                    return false;
                }
                candidate = actual;
            }

            part.LibRef = candidate;
            return true;
        }

        // ==================================================================
        // Pin geometry
        // ==================================================================
        //
        // A pin's Location is its body end. The electrical end -- the only
        // point a wire connects to -- is Location + Length in the direction
        // the pin points: 0 right, 90 up, 180 left, 270 down. (Same rule as
        // AltiumSharp's SchDesignators.PinTip.)

        public static void Direction(int orientDeg, out int dx, out int dy)
        {
            switch (((orientDeg % 360) + 360) % 360)
            {
                case 0: dx = 1; dy = 0; return;
                case 90: dx = 0; dy = 1; return;
                case 180: dx = -1; dy = 0; return;
                case 270: dx = 0; dy = -1; return;
            }
            throw new ArgumentException("pin orientation " + orientDeg + " is not a multiple of 90");
        }

        public static void PinTip(long x, long y, int orientDeg, long length, out long tx, out long ty)
        {
            int dx, dy;
            Direction(orientDeg, out dx, out dy);
            tx = x + dx * length;
            ty = y + dy * length;
        }

        public sealed class Stub
        {
            public long X1, Y1;             // the pin tip
            public long X2, Y2;             // the free end
            public long LabelX, LabelY;     // on the wire, Inset from the tip
            public int LabelRotation;       // 0 or 90
            public bool LabelRightAligned;  // text ends at the anchor rather than starting there
        }

        // The label's anchor sits ON the wire, a short inset from the tip, and
        // its text runs outward along the stub: right-aligned when the stub
        // points left or down, so the text never runs back over the symbol.
        public static Stub StubFor(long tipX, long tipY, int orientDeg, long stubLength, long inset)
        {
            if (stubLength <= 0) throw new ArgumentException("stub length must be positive");
            if (inset <= 0 || inset >= stubLength) inset = stubLength / 4;
            if (inset <= 0) inset = 1;

            int dx, dy;
            Direction(orientDeg, out dx, out dy);

            Stub s = new Stub();
            s.X1 = tipX; s.Y1 = tipY;
            s.X2 = tipX + dx * stubLength;
            s.Y2 = tipY + dy * stubLength;
            s.LabelX = tipX + dx * inset;
            s.LabelY = tipY + dy * inset;
            s.LabelRotation = dy != 0 ? 90 : 0;
            s.LabelRightAligned = dx < 0 || dy < 0;
            return s;
        }

        // True when (px, py) lies on the axis-aligned segment, ends included.
        public static bool OnSegment(long px, long py, long x1, long y1, long x2, long y2)
        {
            if (x1 == x2) return px == x1 && py >= Math.Min(y1, y2) && py <= Math.Max(y1, y2);
            if (y1 == y2) return py == y1 && px >= Math.Min(x1, x2) && px <= Math.Max(x1, x2);
            return false;
        }

        // ==================================================================
        // Pin matching
        // ==================================================================

        public sealed class PinInfo
        {
            public string Designator = "";
            public string Name = "";
            public bool Hidden;
            public bool OnPlacedPart = true;    // false for pins of other parts of a multi-part symbol
        }

        // Number first, then name. Returns the index, or -1 with the reason.
        public static int MatchPin(IList<PinInfo> pins, string want, out string problem)
        {
            problem = null;
            string w = (want ?? "").Trim();

            int byNumber = -1, byNumberElsewhere = -1;
            for (int i = 0; i < pins.Count; i++)
            {
                if (!string.Equals(pins[i].Designator, w, StringComparison.OrdinalIgnoreCase)) continue;
                if (pins[i].OnPlacedPart) { byNumber = i; break; }
                if (byNumberElsewhere < 0) byNumberElsewhere = i;
            }
            if (byNumber >= 0)
            {
                if (pins[byNumber].Hidden)
                {
                    problem = "pin " + w + " is hidden -- it connects through its hidden net name, not a label";
                    return -1;
                }
                return byNumber;
            }

            int byName = -1, count = 0;
            for (int i = 0; i < pins.Count; i++)
            {
                if (!pins[i].OnPlacedPart || pins[i].Hidden) continue;
                if (!string.Equals(pins[i].Name, w, StringComparison.OrdinalIgnoreCase)) continue;
                if (count == 0) byName = i;
                count++;
            }
            if (count == 1) return byName;
            if (count > 1)
            {
                problem = count + " pins are named " + w + " -- give the pin number instead";
                return -1;
            }

            if (byNumberElsewhere >= 0)
            {
                problem = "pin " + w + " is on another part of this multi-part symbol; only the first part is placed";
                return -1;
            }

            StringBuilder sb = new StringBuilder();
            int shown = 0;
            for (int i = 0; i < pins.Count && shown < 12; i++)
            {
                if (!pins[i].OnPlacedPart) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(pins[i].Designator);
                if (pins[i].Name.Length > 0 && pins[i].Name != pins[i].Designator) sb.Append(" " + pins[i].Name);
                shown++;
            }
            problem = "no pin numbered or named " + w + " (pins: " + (sb.Length > 0 ? sb.ToString() : "none") + ")";
            return -1;
        }
    }
}
