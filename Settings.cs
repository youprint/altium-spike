// Settings.cs
//
// Remembers the export folder between runs: picker on first use, silent
// reuse afterwards. Stored per-user in
//     %APPDATA%\AltiumSpike\settings.txt
// rather than beside the DLL, because the DLL lives under C:\ProgramData
// and gets overwritten wholesale on every Deploy.ps1 run.

using System;
using System.Collections.Generic;
using System.IO;

namespace AltiumSpike
{
    public static class Settings
    {
        private const string OutputFolderKey = "OutputFolder=";
        private const string InputFolderKey = "InputFolder=";

        private static string SettingsPath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AltiumSpike");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "settings.txt");
            }
        }

        public static string GetOutputFolder()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return null;
                foreach (string line in File.ReadAllLines(SettingsPath))
                {
                    if (line.StartsWith(OutputFolderKey, StringComparison.OrdinalIgnoreCase))
                    {
                        string v = line.Substring(OutputFolderKey.Length).Trim();
                        if (v.Length > 0 && Directory.Exists(v)) return v;
                        // Remembered but gone (moved, USB unplugged) -- fall
                        // through to the picker rather than failing silently.
                        Log.Write("remembered output folder no longer exists: " + v);
                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception("Settings.GetOutputFolder", ex);
            }
            return null;
        }

        public static void SetOutputFolder(string folder)
        {
            try
            {
                List<string> keep = new List<string>();
                if (File.Exists(SettingsPath))
                    foreach (string line in File.ReadAllLines(SettingsPath))
                        if (!line.StartsWith(OutputFolderKey, StringComparison.OrdinalIgnoreCase))
                            keep.Add(line);
                keep.Add(OutputFolderKey + folder);
                File.WriteAllLines(SettingsPath, keep);
                Log.Write("remembered output folder: " + folder);
            }
            catch (Exception ex)
            {
                Log.Exception("Settings.SetOutputFolder", ex);
            }
        }

        // Returns null if the user cancels.
        public static string PickOutputFolder(string title)
        {
            try
            {
                Microsoft.Win32.OpenFolderDialog dlg = new Microsoft.Win32.OpenFolderDialog();
                dlg.Title = title;
                dlg.Multiselect = false;
                string current = GetOutputFolder();
                if (current != null) dlg.InitialDirectory = current;

                bool? ok = dlg.ShowDialog();
                if (ok == true && !string.IsNullOrEmpty(dlg.FolderName))
                    return dlg.FolderName;
            }
            catch (Exception ex)
            {
                Log.Exception("Settings.PickOutputFolder", ex);
            }
            return null;
        }

        // Generic file picker, used for the .OutJob. Returns null on cancel,
        // which every caller treats as "leave the field alone" rather than
        // "clear it" -- cancelling a browse should never destroy a path the
        // user already typed.
        public static string PickFile(string title, string filter)
        {
            try
            {
                Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();
                dlg.Title = title;
                dlg.Filter = filter;
                dlg.Multiselect = false;
                dlg.CheckFileExists = true;

                string current = GetInputFolder();
                if (current != null) dlg.InitialDirectory = current;

                bool? ok = dlg.ShowDialog();
                if (ok == true && !string.IsNullOrEmpty(dlg.FileName))
                {
                    try
                    {
                        string dir = Path.GetDirectoryName(dlg.FileName);
                        if (!string.IsNullOrEmpty(dir)) SetInputFolder(dir);
                    }
                    catch { }
                    return dlg.FileName;
                }
            }
            catch (Exception ex)
            {
                Log.Exception("Settings.PickFile", ex);
            }
            return null;
        }


        // --- input folder (remembered from the last CSV you picked) ---

        public static string GetInputFolder()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return null;
                foreach (string line in File.ReadAllLines(SettingsPath))
                {
                    if (line.StartsWith(InputFolderKey, StringComparison.OrdinalIgnoreCase))
                    {
                        string v = line.Substring(InputFolderKey.Length).Trim();
                        if (v.Length > 0 && Directory.Exists(v)) return v;
                        return null;
                    }
                }
            }
            catch (Exception ex) { Log.Exception("Settings.GetInputFolder", ex); }
            return null;
        }

        public static void SetInputFolder(string folder)
        {
            try
            {
                if (string.IsNullOrEmpty(folder)) return;
                List<string> keep = new List<string>();
                if (File.Exists(SettingsPath))
                    foreach (string line in File.ReadAllLines(SettingsPath))
                        if (!line.StartsWith(InputFolderKey, StringComparison.OrdinalIgnoreCase))
                            keep.Add(line);
                keep.Add(InputFolderKey + folder);
                File.WriteAllLines(SettingsPath, keep);
            }
            catch (Exception ex) { Log.Exception("Settings.SetInputFolder", ex); }
        }

        // First run -> ask and remember. Later runs -> reuse silently.
        public static string ResolveOutputFolder()
        {
            string folder = GetOutputFolder();
            if (folder != null) return folder;

            folder = PickOutputFolder("Choose a folder for the exported CSVs");
            if (folder != null) SetOutputFolder(folder);
            return folder;
        }

        // ------------------------------------------------------------------
        // Generic key=value slots, used by the via fence to remember pitch,
        // offset, via size and net between sessions.
        //
        // Retyping four numbers and a net name on every run is the kind of
        // friction that stops a tool being used, and a fence is something you
        // redo repeatedly as a layout changes. Values are stored as written
        // by the user, not parsed, so a malformed entry can be corrected in
        // the window rather than silently reset here.
        //
        // Keys must not contain '=' -- the file is one key=value per line and
        // the first '=' is the separator.
        // ------------------------------------------------------------------
        public static string GetValue(string key, string fallback)
        {
            try
            {
                if (string.IsNullOrEmpty(key) || !File.Exists(SettingsPath)) return fallback;
                string prefix = key + "=";
                foreach (string line in File.ReadAllLines(SettingsPath))
                {
                    if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        string v = line.Substring(prefix.Length).Trim();
                        return v.Length > 0 ? v : fallback;
                    }
                }
            }
            catch (Exception ex) { Log.Exception("Settings.GetValue(" + key + ")", ex); }
            return fallback;
        }

        public static void SetValue(string key, string value)
        {
            try
            {
                if (string.IsNullOrEmpty(key) || key.IndexOf('=') >= 0) return;
                string prefix = key + "=";
                List<string> keep = new List<string>();
                if (File.Exists(SettingsPath))
                    foreach (string line in File.ReadAllLines(SettingsPath))
                        if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            keep.Add(line);
                keep.Add(prefix + (value ?? ""));
                File.WriteAllLines(SettingsPath, keep);
            }
            catch (Exception ex) { Log.Exception("Settings.SetValue(" + key + ")", ex); }
        }
    }
}
