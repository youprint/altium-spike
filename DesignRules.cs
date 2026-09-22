// DesignRules.cs
//
// Export every design rule to CSV, and audit the set for the mistakes that
// make a rule quietly stop doing its job.
//
// WHY THIS IS WORTH HAVING. Rules are the thing a board is actually checked
// against, and they live in a dialog you scroll. A rule that got disabled
// during a debugging session, or whose scope query stopped matching anything
// after a net was renamed, does not announce itself: DRC simply passes.
// Nothing in Altium tells you a rule is inert. Having them as a CSV means
// you can diff two revisions, review them away from the dialog, and see the
// priority order at a glance.
//
// CONSTRAINT VALUES ARE PARTIAL, ON PURPOSE. Each rule kind stores its
// constraint on its own interface, and there are dozens of kinds. The ones
// below were read out of the SDK metadata and are correct; the rest export
// with an empty Value column rather than a guess. A wrong clearance number
// in a rules report is worse than a blank one, because a blank invites you
// to go and look.

using DXP;
using PCB;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AltiumSpike
{
    public static class DesignRules
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static double ToMM(int coord) { return EDP.Utils.CoordToMMs(coord); }

        private static string F3(double v)
        {
            double r = Math.Round(v, 3, MidpointRounding.AwayFromZero);
            if (r == 0) r = 0;
            return r.ToString("0.000", Inv);
        }

        private static string Csv(string s)
        {
            if (s == null) return "";
            if (s.IndexOf(',') < 0 && s.IndexOf('"') < 0 && s.IndexOf('\n') < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        public sealed class Result
        {
            public int Rules;
            public int Disabled;
            public int Unscoped;
            public string CsvPath = "";
            public List<string> Findings = new List<string>();
            public List<string> Errors = new List<string>();
        }

        private sealed class RuleRow
        {
            public string Name = "";
            public string Kind = "";
            public bool Enabled;
            public int Priority;
            public string Scope1 = "";
            public string Scope2 = "";
            public string Value = "";
            public string Units = "";
            public double Numeric = double.NaN;
            public string Comment = "";
        }

        // The kinds whose constraint member was confirmed in the SDK metadata.
        // Anything else exports with an empty value rather than a guess.
        private static void ReadValue(IPCB_Rule rule, TRuleKind kind, IPCB_Board board, RuleRow row)
        {
            try
            {
                if (kind == TRuleKind.eRule_Clearance)
                {
                    IPCB_ClearanceConstraint c = rule as IPCB_ClearanceConstraint;
                    if (c != null) { row.Numeric = ToMM(c.GetState_Gap()); row.Units = "mm"; }
                }
                else if (kind == TRuleKind.eRule_MaxMinWidth)
                {
                    IPCB_MaxMinWidthConstraint w = rule as IPCB_MaxMinWidthConstraint;
                    if (w != null)
                    {
                        IV7_Layer top = TopLayer(board);
                        if (top != null) { row.Numeric = ToMM(w.GetState_MinWidth(top)); row.Units = "mm min"; }
                    }
                }
                else if (kind == TRuleKind.eRule_HoleToHoleClearance)
                {
                    IPCB_HoleToHoleClearanceRule h = rule as IPCB_HoleToHoleClearanceRule;
                    if (h != null) { row.Numeric = ToMM(h.GetState_Gap()); row.Units = "mm"; }
                }
                else if (kind == TRuleKind.eRule_PowerPlaneClearance)
                {
                    IPCB_PowerPlaneClearanceRule p = rule as IPCB_PowerPlaneClearanceRule;
                    if (p != null) { row.Numeric = ToMM(p.GetState_Clearance()); row.Units = "mm"; }
                }
                else if (kind == TRuleKind.eRule_ComponentClearance)
                {
                    IPCB_ComponentClearanceConstraint c = rule as IPCB_ComponentClearanceConstraint;
                    if (c != null) { row.Numeric = ToMM(c.GetState_Gap()); row.Units = "mm"; }
                }
                else
                {
                    IPCB_SilkToSilkClearanceRule ss = rule as IPCB_SilkToSilkClearanceRule;
                    if (ss != null) { row.Numeric = ToMM(ss.GetState_SilkToSilkClearance()); row.Units = "mm"; }
                    else
                    {
                        IPCB_SilkToSolderMaskClearanceRule sm = rule as IPCB_SilkToSolderMaskClearanceRule;
                        if (sm != null) { row.Numeric = ToMM(sm.GetState_SilkToMaskGap()); row.Units = "mm"; }
                    }
                }
            }
            catch { /* a rule that will not answer exports with a blank value */ }

            if (!double.IsNaN(row.Numeric)) row.Value = F3(row.Numeric);
        }

        private static IV7_Layer TopLayer(IPCB_Board board)
        {
            try
            {
                IPCB_LayerStack stack = board.GetState_LayerStack();
                if (stack == null) return null;
                IPCB_LayerObject lo = stack.First(TLayerClassID.eLayerClass_Electrical);
                if (lo == null) return null;
                return lo.V7_LayerID();
            }
            catch { return null; }
        }

        private static List<RuleRow> Read(IPCB_Board board, Result res)
        {
            List<RuleRow> rows = new List<RuleRow>();

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
                        RuleRow row = new RuleRow();
                        try { row.Name = rule.GetState_Name() ?? ""; } catch { }
                        try { row.Enabled = rule.GetState_DRCEnabled(); } catch { }
                        try { row.Priority = rule.Priority(); } catch { }
                        try { row.Scope1 = rule.GetState_Scope1Expression() ?? ""; } catch { }
                        try { row.Scope2 = rule.GetState_Scope2Expression() ?? ""; } catch { }
                        try { row.Comment = rule.GetState_Comment() ?? ""; } catch { }

                        TRuleKind kind = TRuleKind.eRule_Clearance;
                        try { kind = rule.GetState_RuleKind(); row.Kind = kind.ToString(); } catch { }

                        ReadValue(rule, kind, board, row);
                        rows.Add(row);
                    }
                    catch { }

                    rule = it.NextPCBObject() as IPCB_Rule;
                }
            }
            catch (Exception ex)
            {
                res.Errors.Add("Rule scan failed -- " + ex.GetType().Name + ": " + ex.Message);
            }
            finally { board.BoardIterator_Destroy(ref it); }

            return rows;
        }

        // ==================================================================
        public static Result Export(IPCB_Board board, string folder)
        {
            Result res = new Result();
            List<RuleRow> rows = Read(board, res);
            res.Rules = rows.Count;

            rows.Sort(delegate (RuleRow a, RuleRow b)
            {
                int k = string.Compare(a.Kind, b.Kind, StringComparison.Ordinal);
                if (k != 0) return k;
                return a.Priority.CompareTo(b.Priority);
            });

            List<string> lines = new List<string>();
            lines.Add("Kind,Priority,Name,Enabled,Value,Units,Scope1,Scope2,Comment");

            for (int i = 0; i < rows.Count; i++)
            {
                RuleRow r = rows[i];
                if (!r.Enabled) res.Disabled++;
                lines.Add(string.Join(",",
                    Csv(r.Kind), r.Priority.ToString(Inv), Csv(r.Name),
                    r.Enabled ? "True" : "False",
                    Csv(r.Value), Csv(r.Units),
                    Csv(r.Scope1), Csv(r.Scope2), Csv(r.Comment)));
            }

            try
            {
                string path = Path.Combine(folder, "design_rules.csv");
                File.WriteAllLines(path, lines, new UTF8Encoding(false));
                res.CsvPath = path;
                Log.Write("wrote " + path + " (" + rows.Count + " rule(s))");
            }
            catch (Exception ex)
            {
                res.Errors.Add("Could not write the CSV -- " + ex.GetType().Name + ": " + ex.Message);
            }

            return res;
        }

        // ==================================================================
        // Audit
        //
        // Every finding here is a rule that exists but is not doing what its
        // name suggests. None of them is an error Altium will ever report.
        // ==================================================================
        public static Result Audit(IPCB_Board board)
        {
            Result res = new Result();
            List<RuleRow> rows = Read(board, res);
            res.Rules = rows.Count;

            List<string> disabled = new List<string>();
            List<string> unscoped = new List<string>();

            // Tightest enabled clearance and width, which is what a fab house
            // actually quotes against.
            double tightestClearance = double.MaxValue;
            string tightestClearanceName = "";
            double tightestWidth = double.MaxValue;
            string tightestWidthName = "";

            Dictionary<string, List<RuleRow>> byKind =
                new Dictionary<string, List<RuleRow>>(StringComparer.Ordinal);

            for (int i = 0; i < rows.Count; i++)
            {
                RuleRow r = rows[i];

                if (!r.Enabled) { res.Disabled++; disabled.Add(r.Kind + " / " + r.Name); }

                // A scope of "All" is fine. An EMPTY scope matches nothing,
                // which makes the rule inert while still looking present in
                // the dialog.
                if (r.Scope1.Trim().Length == 0)
                { res.Unscoped++; unscoped.Add(r.Kind + " / " + r.Name); }

                if (r.Enabled && !double.IsNaN(r.Numeric))
                {
                    if (r.Kind == TRuleKind.eRule_Clearance.ToString() && r.Numeric > 0 &&
                        r.Numeric < tightestClearance)
                    { tightestClearance = r.Numeric; tightestClearanceName = r.Name; }

                    if (r.Kind == TRuleKind.eRule_MaxMinWidth.ToString() && r.Numeric > 0 &&
                        r.Numeric < tightestWidth)
                    { tightestWidth = r.Numeric; tightestWidthName = r.Name; }
                }

                List<RuleRow> list;
                if (!byKind.TryGetValue(r.Kind, out list)) { list = new List<RuleRow>(); byKind[r.Kind] = list; }
                list.Add(r);
            }

            res.Findings.Add(rows.Count + " rule(s) across " + byKind.Count + " kind(s).");

            if (disabled.Count == 0) res.Findings.Add("No disabled rules.");
            else
            {
                res.Findings.Add(disabled.Count + " DISABLED rule(s) — they look present but check nothing:");
                for (int i = 0; i < disabled.Count && i < 12; i++) res.Findings.Add("   " + disabled[i]);
                if (disabled.Count > 12) res.Findings.Add("   … and " + (disabled.Count - 12) + " more");
            }

            if (unscoped.Count > 0)
            {
                res.Findings.Add(unscoped.Count + " rule(s) with an EMPTY scope — these match nothing:");
                for (int i = 0; i < unscoped.Count && i < 12; i++) res.Findings.Add("   " + unscoped[i]);
                if (unscoped.Count > 12) res.Findings.Add("   … and " + (unscoped.Count - 12) + " more");
            }

            // Duplicate priorities within one kind make which rule wins
            // depend on ordering rather than on intent.
            List<string> kinds = new List<string>(byKind.Keys);
            kinds.Sort(StringComparer.Ordinal);
            int clashes = 0;
            for (int i = 0; i < kinds.Count; i++)
            {
                List<RuleRow> list = byKind[kinds[i]];
                Dictionary<int, int> seen = new Dictionary<int, int>();
                for (int j = 0; j < list.Count; j++)
                {
                    int c;
                    seen.TryGetValue(list[j].Priority, out c);
                    seen[list[j].Priority] = c + 1;
                }
                foreach (KeyValuePair<int, int> kv in seen)
                    if (kv.Value > 1) clashes++;
            }
            if (clashes > 0)
                res.Findings.Add(clashes + " priority clash(es) — two rules of one kind at the same priority, " +
                                 "so which wins depends on ordering rather than intent.");

            if (tightestClearance < double.MaxValue)
                res.Findings.Add("Tightest enabled clearance: " + F3(tightestClearance) +
                                 " mm (" + tightestClearanceName + ")");
            else
                res.Findings.Add("No enabled clearance rule with a readable value.");

            if (tightestWidth < double.MaxValue)
                res.Findings.Add("Tightest enabled minimum width: " + F3(tightestWidth) +
                                 " mm (" + tightestWidthName + ")");

            return res;
        }
    }
}
