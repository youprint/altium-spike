// Tests.cs -- release bundle checks.
//
// Compiles the PLUGIN'S OWN ReleaseBundle.cs, not a copy, so these
// assertions cannot drift from what ships.
//
// These are the failures worth catching, because every one of them looks
// like a successful run from the outside: an archive missing two drill
// files, a release that silently replaced last week's, a previous zip nested
// inside the new one, two same-named files from different subfolders where
// one quietly won.

using AltiumSpike;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace BundleTest
{
    static class T
    {
        static int passed, failed;

        static void Ok(bool cond, string what)
        {
            if (cond) { passed++; Console.WriteLine("  PASS  " + what); }
            else { failed++; Console.WriteLine("  FAIL  " + what); }
        }

        static void Eq(string actual, string expected, string what)
        {
            if (actual == expected) { passed++; Console.WriteLine("  PASS  " + what); }
            else
            {
                failed++;
                Console.WriteLine("  FAIL  " + what + "  (got \"" + actual + "\", expected \"" + expected + "\")");
            }
        }

        static string root;

        static string Sandbox(string name)
        {
            string d = Path.Combine(root, name);
            Directory.CreateDirectory(d);
            return d;
        }

        static void Write(string dir, string name, string content)
        {
            string p = Path.Combine(dir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, content);
        }

        static ReleaseBundle.Options Opt(string outDir, string destDir)
        {
            ReleaseBundle.Options o = new ReleaseBundle.Options();
            o.ProjectName = "Daughterboard";
            o.Revision = "RevA";
            o.OutputFolder = outDir;
            o.DestinationFolder = destDir;
            o.Stamp = new DateTime(2026, 9, 21);
            return o;
        }

        static void Main()
        {
            root = Path.Combine(Path.GetTempPath(), "bundletest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            Console.WriteLine("release bundle");
            Console.WriteLine();

            try
            {
                // -----------------------------------------------------------
                Console.WriteLine("1. archive naming");
                {
                    ReleaseBundle.Options o = Opt("x", "y");
                    Eq(ReleaseBundle.ArchiveName(o), "Daughterboard_RevA_20260921.zip",
                       "ProjectName_Rev_YYYYMMDD.zip");

                    o.Revision = "";
                    Eq(ReleaseBundle.ArchiveName(o), "Daughterboard_20260921.zip",
                       "revision omitted when blank");

                    o.Revision = "Rev/A";
                    o.ProjectName = "My Board: v2";
                    Eq(ReleaseBundle.ArchiveName(o), "My_Board-_v2_Rev-A_20260921.zip",
                       "illegal characters replaced, spaces become underscores");

                    Eq(ReleaseBundle.SafeToken("a<b>c:d\"e|f?g*h"), "a-b-c-d-e-f-g-h",
                       "every Windows-illegal character is substituted, not dropped");
                }

                // -----------------------------------------------------------
                Console.WriteLine();
                Console.WriteLine("2. collection filters by extension");
                {
                    string o = Sandbox("out2"), d = Sandbox("dest2");
                    Write(o, "top.gtl", "gerber");
                    Write(o, "drill.drl", "drill");
                    Write(o, "bom.csv", "bom");
                    Write(o, "build.log", "noise");
                    Write(o, "preview.htm", "noise");

                    ReleaseBundle.Result r = new ReleaseBundle.Result();
                    List<string> got = ReleaseBundle.Collect(Opt(o, d), r);

                    Ok(got.Count == 3, "3 fabrication files kept");
                    Ok(r.Skipped.Count == 2, "log and htm left behind");
                }

                // -----------------------------------------------------------
                Console.WriteLine();
                Console.WriteLine("3. a previous release is never nested inside the new one");
                {
                    string o = Sandbox("out3"), d = Sandbox("dest3");
                    Write(o, "top.gtl", "gerber");
                    Write(o, "Daughterboard_RevA_20260101.zip", "last week");

                    ReleaseBundle.Result r = new ReleaseBundle.Result();
                    List<string> got = ReleaseBundle.Collect(Opt(o, d), r);

                    Ok(got.Count == 1, "only the gerber collected");
                    bool noted = r.Skipped.Exists(delegate (string s) { return s.Contains("previous release"); });
                    Ok(noted, "the old archive is reported as skipped, not silently ignored");
                }

                // -----------------------------------------------------------
                Console.WriteLine();
                Console.WriteLine("4. happy path -- zip, verify, deliver");
                {
                    string o = Sandbox("out4"), d = Sandbox("dest4");
                    Write(o, "top.gtl", "a");
                    Write(o, "bot.gbl", "b");
                    Write(o, "drill.drl", "c");

                    ReleaseBundle.Result r = ReleaseBundle.Bundle(Opt(o, d));

                    Ok(r.Ok, "run reported success");
                    Ok(r.Errors.Count == 0, "no errors");
                    Ok(r.FilesCollected == 3, "3 files collected");
                    Ok(r.EntriesVerified == 3, "archive reopened and 3 entries counted");

                    string zip = Path.Combine(d, "Daughterboard_RevA_20260921.zip");
                    Ok(File.Exists(zip), "archive delivered to the destination");

                    using (ZipArchive za = ZipFile.OpenRead(zip))
                        Ok(za.Entries.Count == 3, "archive really does contain 3 entries");

                    Ok(File.Exists(Path.Combine(o, "top.gtl")),
                       "source files COPIED, not moved -- originals still there");
                }

                // -----------------------------------------------------------
                Console.WriteLine();
                Console.WriteLine("5. an existing release is never overwritten");
                {
                    string o = Sandbox("out5"), d = Sandbox("dest5");
                    Write(o, "top.gtl", "new build");
                    Write(d, "Daughterboard_RevA_20260921.zip", "PRECIOUS ORIGINAL");

                    ReleaseBundle.Result r = ReleaseBundle.Bundle(Opt(o, d));

                    Ok(!r.Ok, "run refused");
                    bool said = r.Errors.Exists(delegate (string s) { return s.Contains("already exists"); });
                    Ok(said, "refusal explains why");
                    Eq(File.ReadAllText(Path.Combine(d, "Daughterboard_RevA_20260921.zip")),
                       "PRECIOUS ORIGINAL", "the existing release is untouched");
                }

                // -----------------------------------------------------------
                Console.WriteLine();
                Console.WriteLine("6. same leaf name in two subfolders -- neither is lost");
                {
                    string o = Sandbox("out6"), d = Sandbox("dest6");
                    Write(o, Path.Combine("gerber", "art.gtl"), "one");
                    Write(o, Path.Combine("drill", "art.gtl"), "two");

                    ReleaseBundle.Result r = ReleaseBundle.Bundle(Opt(o, d));

                    Ok(r.FilesCollected == 2, "both files collected from subfolders");
                    Ok(r.EntriesVerified == 2, "both survive into the archive");

                    using (ZipArchive za = ZipFile.OpenRead(Path.Combine(d, "Daughterboard_RevA_20260921.zip")))
                        Ok(za.Entries.Count == 2, "no silent overwrite inside the zip");
                }

                // -----------------------------------------------------------
                Console.WriteLine();
                Console.WriteLine("7. dry run writes nothing");
                {
                    string o = Sandbox("out7"), d = Sandbox("dest7");
                    Write(o, "top.gtl", "a");

                    ReleaseBundle.Options opt = Opt(o, d);
                    opt.DryRun = true;
                    ReleaseBundle.Result r = ReleaseBundle.Bundle(opt);

                    Ok(r.WasDryRun, "flagged as a dry run");
                    Ok(r.FilesCollected == 1, "still reports what it would package");
                    Ok(Directory.GetFiles(d).Length == 0, "destination is empty");
                }

                // -----------------------------------------------------------
                Console.WriteLine();
                Console.WriteLine("8. refusals are explained, not silent");
                {
                    string o = Sandbox("out8"), d = Sandbox("dest8");

                    ReleaseBundle.Result empty = ReleaseBundle.Bundle(Opt(o, d));
                    Ok(!empty.Ok && empty.Errors.Count > 0, "empty output folder is an explained failure");

                    ReleaseBundle.Options noOut = Opt("", d);
                    ReleaseBundle.Result r2 = ReleaseBundle.Bundle(noOut);
                    Ok(!r2.Ok && r2.Errors.Count > 0, "missing output folder is an explained failure");

                    ReleaseBundle.Options noDest = Opt(o, "");
                    ReleaseBundle.Result r3 = ReleaseBundle.Bundle(noDest);
                    Ok(!r3.Ok && r3.Errors.Count > 0, "missing destination is an explained failure");

                    ReleaseBundle.Options missing = Opt(Path.Combine(root, "does_not_exist"), d);
                    ReleaseBundle.Result r4 = ReleaseBundle.Bundle(missing);
                    Ok(!r4.Ok && r4.Errors.Count > 0, "nonexistent output folder is an explained failure");
                }

                // -----------------------------------------------------------
                Console.WriteLine();
                Console.WriteLine("9. destination folder is created if absent");
                {
                    string o = Sandbox("out9");
                    string d = Path.Combine(root, "dest9", "nested", "deep");
                    Write(o, "top.gtl", "a");

                    ReleaseBundle.Result r = ReleaseBundle.Bundle(Opt(o, d));
                    Ok(r.Ok, "run succeeded into a folder that did not exist");
                    Ok(File.Exists(Path.Combine(d, "Daughterboard_RevA_20260921.zip")), "archive is there");
                }
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }

            Console.WriteLine();
            Console.WriteLine("passed " + passed + ", failed " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }
    }
}
