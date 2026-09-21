// ReleasePackager.cs
//
// The Altium half of the release packager: run the project's Output Job,
// then hand off to ReleaseBundle for naming, zipping, verification and
// delivery. Everything that does not need a PCB editor lives there and is
// unit-tested; this file holds only the part that does.
//
// WHY IT DRIVES AN OUTPUT JOB RATHER THAN BYPASSING ONE
//
// There is no SDK call that writes a Gerber, an NC drill file, an ODB++
// archive or a STEP model. Both Altium SDK assemblies were searched: what
// exists is IWSM_OutputJobDocument, which enumerates and creates *outputers*
// -- the Output Job machinery itself -- and DXP.CommandLauncher.LaunchCommand,
// which runs Altium's own processes. The fabrication writers live behind
// those processes.
//
// Reimplementing RS-274X and Excellon here was considered and rejected. The
// hard parts of a Gerber writer are aperture generation, polygon fill
// decomposition and drill symbol assignment, and a subtly wrong one produces
// files that look fine in a viewer and fail at the fab. Altium's writers are
// the ones your fabricator already trusts.
//
// So "bypasses the standard Output Job" is delivered as "you never open the
// Output Job dialogs": you keep one .OutJob holding your fab settings, and
// this runs it and does everything after it in a single click.
//
// THE ONE UNVERIFIED CALL IN THIS PROJECT
//
// Every other Altium call in AltiumSpike was read out of assembly metadata
// before being written. RunOutJob is the exception: the process name and its
// parameter string come from Altium's process reference, which is not in the
// SDK metadata, so it could not be confirmed here the way everything else
// was. It is isolated in this file, it logs exactly what it sent, and the
// whole feature still works with GenerateOutputs switched off -- which is
// the default until the launch has been seen to work on a real install.

using DXP;
using System;
using System.IO;

namespace AltiumSpike
{
    public static class ReleasePackager
    {
        private static bool RunOutJob(IClient client, ReleaseBundle.Options opt, ReleaseBundle.Result res)
        {
            if (string.IsNullOrEmpty(opt.OutJobPath))
            {
                res.Errors.Add("No Output Job selected, so nothing could be generated.");
                return false;
            }
            if (!File.Exists(opt.OutJobPath))
            {
                res.Errors.Add("Output Job not found: " + opt.OutJobPath);
                return false;
            }

            try
            {
                object raw = client.GetCommandLauncher();
                DXP.CommandLauncher launcher = raw as DXP.CommandLauncher;
                if (launcher == null)
                {
                    res.Errors.Add("Could not reach Altium's command launcher, so the Output Job was not run.");
                    return false;
                }

                // WorkspaceManager's output generator. The parameter spelling
                // is from Altium's process reference, not from SDK metadata --
                // see the header. If this turns out to be wrong on a given
                // installation, the log line below records exactly what was
                // sent, which is the fastest way to correct it.
                string parameters =
                    "ObjectKind=OutputBatch|" +
                    "OutputMedium=|" +
                    "DocumentPath=" + opt.OutJobPath;

                Log.Write("ReleasePackager: LaunchCommand(\"WorkspaceManager:GenerateReport\", \"" +
                          parameters + "\")");

                client.StartServer("WorkspaceManager");
                launcher.LaunchCommand("WorkspaceManager:GenerateReport", ref parameters, null);

                res.Generated = true;
                return true;
            }
            catch (Exception ex)
            {
                res.Errors.Add("Running the Output Job failed -- " + ex.GetType().Name + ": " + ex.Message);
                Log.Exception("ReleasePackager.RunOutJob", ex);
                return false;
            }
        }

        // ------------------------------------------------------------------
        // Generate (optionally) then package.
        // ------------------------------------------------------------------
        public static ReleaseBundle.Result Package(IClient client, ReleaseBundle.Options opt)
        {
            if (!opt.GenerateOutputs) return ReleaseBundle.Bundle(opt);

            if (opt.DryRun)
            {
                // Say so loudly rather than quietly packaging whatever happens
                // to be left over from the last real run.
                ReleaseBundle.Result planned = ReleaseBundle.Bundle(opt);
                planned.Errors.Add("Dry run: the Output Job was NOT launched; the file list " +
                                   "is whatever is already in the output folder.");
                return planned;
            }

            ReleaseBundle.Result gen = new ReleaseBundle.Result();
            if (!RunOutJob(client, opt, gen)) return gen;

            ReleaseBundle.Result res = ReleaseBundle.Bundle(opt);
            res.Generated = true;
            foreach (string e in gen.Errors) res.Errors.Add(e);
            return res;
        }
    }
}
