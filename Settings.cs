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
    }
}
