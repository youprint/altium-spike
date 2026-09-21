// ReleaseBundle.cs
//
// The file half of the release packager: naming, collection, zipping,
// archive verification and delivery. Deliberately free of any Altium type,
// so it compiles standalone and is exercised by a test harness with no PCB
// editor in the loop. ReleasePackager.cs adds the one thing that does need
// Altium -- running the Output Job.
//
// The split is the same one used for the via fence, and for the same reason:
// this is where a release goes wrong quietly. A zip that is missing two
// drill files, or that silently overwrote last week's RevA, looks exactly
// like a successful run from the outside.
//
// FILE SAFETY RULES, all enforced below:
//   - Source files are COPIED, never moved. A failed package run costs
//     nothing and leaves the Output Job's own output untouched.
//   - An existing destination archive is NEVER overwritten. Two different
//     builds under one release name is worse than a failed run.
//   - The archive is reopened and its entry count checked BEFORE delivery,
//     while it is still in a temp folder rather than on the release share.
//   - A previous release archive sitting in the output folder is excluded,
//     so releases cannot nest inside each other.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace AltiumSpike
{
    public static class ReleaseBundle
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public sealed class Options
        {
            public string ProjectName = "Project";
            public string Revision = "RevA";
            public string OutputFolder = "";        // where the OutJob writes
            public string DestinationFolder = "";   // network drive or anywhere
            public string OutJobPath = "";          // .OutJob to run
            public bool GenerateOutputs = false;    // false = package only
            public bool DryRun = false;             // plan it, touch nothing
            public bool IncludeSubfolders = true;
            public DateTime Stamp = DateTime.Now;

            // Extensions worth shipping. Anything else in the output folder is
            // left behind rather than swept into the archive: an OutJob folder
            // routinely accumulates logs, previews and .htm reports that a
            // fabricator does not want and that make the zip look untrustworthy.
            public List<string> Extensions = new List<string>(new string[] {
                ".gbr", ".gbx", ".gtl", ".gbl", ".gts", ".gbs", ".gto", ".gbo",
                ".gm1", ".gko", ".gpt", ".gpb", ".gd1", ".gg1",
                ".txt", ".drl", ".xln", ".nc",
                ".ipc", ".zip", ".tgz",
                ".step", ".stp", ".stpz",
                ".csv", ".xls", ".xlsx", ".pdf",
            });
        }

        public sealed class Result
        {
            public bool Ok;
            public string ArchivePath = "";
            public string ArchiveName = "";
            public int FilesCollected;
            public long BytesCollected;
            public long ArchiveBytes;
            public int EntriesVerified;
            public bool Generated;
            public bool WasDryRun;
            public List<string> Collected = new List<string>();
            public List<string> Skipped = new List<string>();
            public List<string> Errors = new List<string>();

            // Kept as data rather than written straight to the log, so this
            // class stays free of both Altium and WPF and can be exercised by
            // the test harness. The caller drains it into Log.
            public List<string> Diagnostics = new List<string>();

            public string Summarise()
            {
                string s;
                if (WasDryRun) s = "DRY RUN -- nothing was written.\n";
                else if (Ok) s = "Release package created.\n";
                else s = "Release package FAILED.\n";

                s += "Archive: " + (ArchiveName.Length > 0 ? ArchiveName : "(none)") + "\n";
                s += FilesCollected + " file(s), " + Human(BytesCollected) + " collected";
                if (ArchiveBytes > 0) s += " -> " + Human(ArchiveBytes) + " zipped";
                s += ".\n";
                if (EntriesVerified > 0)
                    s += "Archive reopened and verified: " + EntriesVerified + " entries.\n";
                if (Generated) s += "Output Job was run before packaging.\n";
                if (Skipped.Count > 0)
                    s += Skipped.Count + " file(s) skipped by extension filter.\n";
                if (ArchivePath.Length > 0 && !WasDryRun && Ok)
                    s += "Delivered to " + ArchivePath + "\n";
                if (Errors.Count > 0)
                {
                    s += "Errors (" + Errors.Count + "):\n";
                    foreach (string e in Errors) s += "  " + e + "\n";
                }
                return s;
            }
        }

        public static string Human(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0", Inv) + " KB";
            return (bytes / 1048576.0).ToString("0.0", Inv) + " MB";
        }

        // ------------------------------------------------------------------
        // Naming
        //
        // ProjectName_RevA_20260921.zip. Characters illegal in a Windows file
        // name are replaced rather than stripped, so two projects differing
        // only by a slash cannot collapse onto one archive name.
        // ------------------------------------------------------------------
        // The illegal set is written out EXPLICITLY rather than taken from
        // Path.GetInvalidFileNameChars(), which is platform-dependent: on
        // Linux it returns only '/' and NUL, so ':', '|', '?' and '*' would
        // pass straight through. The archive name has to be valid on Windows
        // and on whatever network share it lands on no matter where this code
        // happens to run, and a test that only passes on one OS is not a test.
        private const string IllegalNameChars = "<>:\"/\\|?*";

        public static string SafeToken(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == ' ') { sb.Append('_'); continue; }
                bool bad = c < 32 || IllegalNameChars.IndexOf(c) >= 0;
                sb.Append(bad ? '-' : c);
            }
            return sb.ToString().Trim('_', '-', '.');
        }

        public static string ArchiveName(Options opt)
        {
            string proj = SafeToken(opt.ProjectName);
            string rev = SafeToken(opt.Revision);
            string date = opt.Stamp.ToString("yyyyMMdd", Inv);

            if (proj.Length == 0) proj = "Project";

            string stem = rev.Length > 0 ? proj + "_" + rev + "_" + date
                                         : proj + "_" + date;
            return stem + ".zip";
        }

        // ------------------------------------------------------------------
        // Collection
        // ------------------------------------------------------------------
        public static List<string> Collect(Options opt, Result res)
        {
            List<string> keep = new List<string>();

            if (!Directory.Exists(opt.OutputFolder))
            {
                res.Errors.Add("Output folder does not exist: " + opt.OutputFolder);
                return keep;
            }

            string[] all;
            try
            {
                all = Directory.GetFiles(opt.OutputFolder, "*",
                    opt.IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                res.Errors.Add("Could not list the output folder -- " + ex.GetType().Name + ": " + ex.Message);
                return keep;
            }

            HashSet<string> wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string e in opt.Extensions) wanted.Add(e);

            for (int i = 0; i < all.Length; i++)
            {
                string ext = Path.GetExtension(all[i]);

                // A previous release archive sitting in the output folder must
                // never be swallowed into the next one.
                string name = Path.GetFileName(all[i]);
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                    name.StartsWith(SafeToken(opt.ProjectName), StringComparison.OrdinalIgnoreCase))
                {
                    res.Skipped.Add(name + " (previous release archive)");
                    continue;
                }

                if (wanted.Contains(ext)) keep.Add(all[i]);
                else res.Skipped.Add(name);
            }

            keep.Sort(StringComparer.OrdinalIgnoreCase);
            return keep;
        }

        public static Result Bundle(Options opt)
        {
            Result res = new Result();
            res.WasDryRun = opt.DryRun;

            if (string.IsNullOrEmpty(opt.OutputFolder))
            { res.Errors.Add("Choose the folder the Output Job writes to."); return res; }
            if (string.IsNullOrEmpty(opt.DestinationFolder))
            { res.Errors.Add("Choose a destination folder for the release."); return res; }

            res.ArchiveName = ArchiveName(opt);

            List<string> files = Collect(opt, res);
            res.FilesCollected = files.Count;
            foreach (string f in files)
            {
                res.Collected.Add(Path.GetFileName(f));
                try { res.BytesCollected += new FileInfo(f).Length; } catch { }
            }

            if (files.Count == 0)
            {
                res.Errors.Add("No matching files in " + opt.OutputFolder +
                               ". Generate the outputs first, or widen the extension list.");
                return res;
            }

            string destPath = Path.Combine(opt.DestinationFolder, res.ArchiveName);
            res.ArchivePath = destPath;

            if (opt.DryRun)
            {
                res.Ok = res.Errors.Count == 0;
                return res;
            }

            // Refusing to overwrite is deliberate. Two different builds under
            // one release name is a worse outcome than a failed run.
            if (File.Exists(destPath))
            {
                res.Errors.Add("A release already exists at " + destPath +
                               ". Bump the revision or move the old one aside.");
                return res;
            }

            string temp = Path.Combine(Path.GetTempPath(),
                "AltiumSpike_" + Guid.NewGuid().ToString("N"));
            string stage = Path.Combine(temp, Path.GetFileNameWithoutExtension(res.ArchiveName));
            string tempZip = temp + ".zip";

            try
            {
                Directory.CreateDirectory(stage);

                // Copy, never move: the OutJob's own output stays where it is
                // so a failed package run costs nothing.
                foreach (string f in files)
                {
                    string target = Path.Combine(stage, Path.GetFileName(f));

                    // Subfolder collection can present two files with the same
                    // leaf name. Disambiguate rather than silently dropping one.
                    int n = 1;
                    while (File.Exists(target))
                    {
                        target = Path.Combine(stage,
                            Path.GetFileNameWithoutExtension(f) + "_" + n + Path.GetExtension(f));
                        n++;
                    }
                    File.Copy(f, target);
                }

                ZipFile.CreateFromDirectory(stage, tempZip, CompressionLevel.Optimal, true);
                res.ArchiveBytes = new FileInfo(tempZip).Length;

                // Verify BEFORE delivering. Reopening the archive and counting
                // entries catches a truncated or half-written zip while it is
                // still in a temp folder rather than on the release share.
                using (ZipArchive za = ZipFile.OpenRead(tempZip))
                {
                    res.EntriesVerified = za.Entries.Count;
                }

                if (res.EntriesVerified < files.Count)
                {
                    res.Errors.Add("Archive verification failed: " + res.EntriesVerified +
                                   " entries for " + files.Count + " files. Nothing was delivered.");
                    return res;
                }

                Directory.CreateDirectory(opt.DestinationFolder);

                // File.Move across volumes works on .NET, and a network share
                // is a different volume. Copy-then-delete would be equivalent
                // but leaves a window where both exist.
                File.Move(tempZip, destPath);

                res.Ok = true;
                res.Diagnostics.Add("delivered " + destPath + " (" +
                                    Human(res.ArchiveBytes) + ", " + res.EntriesVerified + " entries)");
            }
            catch (Exception ex)
            {
                res.Errors.Add(ex.GetType().Name + " -- " + ex.Message);
                res.Diagnostics.Add("Bundle threw " + ex.GetType().FullName + ": " + ex.ToString());
            }
            finally
            {
                try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            }

            return res;
        }
    }
}
