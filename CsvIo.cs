// CsvIo.cs
//
// Shared input plumbing for the Import Board Data commands: file picker,
// CSV line splitting, and culture-proof number parsing.
//
// The DelphiScripts each prompted with InputBox and a hard-coded default
// path under C:\Scripts\Input\. A real file picker is better, and the
// last-used input folder is remembered the same way the export folder is.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace AltiumSpike
{
    public static class CsvIo
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // Returns null if the user cancels.
        public static string PickCsv(string title, string suggestedName)
        {
            try
            {
                Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();
                dlg.Title = title;
                dlg.Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*";
                dlg.CheckFileExists = true;
                dlg.Multiselect = false;

                string last = Settings.GetInputFolder();
                if (last != null) dlg.InitialDirectory = last;
                if (!string.IsNullOrEmpty(suggestedName)) dlg.FileName = suggestedName;

                bool? ok = dlg.ShowDialog();
                if (ok == true && !string.IsNullOrEmpty(dlg.FileName))
                {
                    try { Settings.SetInputFolder(Path.GetDirectoryName(dlg.FileName)); } catch { }
                    return dlg.FileName;
                }
            }
            catch (Exception ex)
            {
                Log.Exception("CsvIo.PickCsv", ex);
            }
            return null;
        }

        public static string[] ReadLines(string path)
        {
            return File.ReadAllLines(path);
        }

        // Mirrors SplitCSVLine* in the DelphiScripts: plain comma split with
        // each field trimmed. Quoted fields containing commas are handled too,
        // which the .pas versions did not do.
        public static List<string> Split(string line)
        {
            List<string> fields = new List<string>();
            if (line == null) { fields.Add(""); return fields; }

            System.Text.StringBuilder cur = new System.Text.StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else cur.Append(c);
                }
                else if (c == '"') inQuotes = true;
                else if (c == ',') { fields.Add(cur.ToString().Trim()); cur.Clear(); }
                else cur.Append(c);
            }
            fields.Add(cur.ToString().Trim());
            return fields;
        }

        public static string At(List<string> f, int i)
        {
            return (i >= 0 && i < f.Count) ? f[i] : "";
        }

        // Equivalent of IsNumericString* + TryParseFloat* in the .pas files.
        // Always invariant: a comma-decimal Windows locale cannot affect this,
        // so no DecimalSeparator save/restore dance is needed.
        public static bool TryNum(string s, out double value)
        {
            value = 0;
            if (s == null) return false;
            s = s.Trim();
            if (s.Length == 0) return false;
            return double.TryParse(s, NumberStyles.Float, Inv, out value);
        }
    }
}
