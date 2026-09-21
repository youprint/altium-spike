// Log.cs
//
// Diagnostic logging that cannot fail silently.
//
// The previous iteration reported everything through MessageBox. When the
// menu item did nothing we could not tell whether the command never fired
// or whether the message box itself refused to display inside Altium's
// host process -- both look identical from the outside. A log file removes
// that ambiguity: if RunSpike is reached, there is a line for it, whether
// or not any window appears.

using System;
using System.IO;

namespace AltiumSpike
{
    public static class Log
    {
        private static readonly object gate = new object();
        private static string path;

        private static string Path_
        {
            get
            {
                if (path != null) return path;
                try
                {
                    string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    if (!string.IsNullOrEmpty(desktop) && Directory.Exists(desktop))
                        path = System.IO.Path.Combine(desktop, "AltiumSpike.log");
                }
                catch { }
                if (path == null)
                    path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AltiumSpike.log");
                return path;
            }
        }

        public static void Write(string message)
        {
            try
            {
                lock (gate)
                {
                    File.AppendAllText(Path_,
                        DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
                }
            }
            catch { /* logging must never throw into Altium */ }
        }

        public static void Exception(string where, Exception ex)
        {
            Write(where + " THREW " + ex.GetType().FullName + ": " + ex.Message);
            Write("    " + (ex.StackTrace ?? "(no stack)").Replace("\n", "\n    "));
            if (ex.InnerException != null)
                Write("    inner: " + ex.InnerException.GetType().FullName + ": " + ex.InnerException.Message);
        }

        // MessageBox may or may not display from inside Altium's process --
        // that is one of the things we are testing. Always log; try to show.
        public static void Say(string title, string message)
        {
            Write("[" + title + "] " + message.Replace("\r\n", " | ").Replace("\n", " | "));
            try
            {
                System.Windows.MessageBox.Show(message, title);
                Write("    (MessageBox returned normally)");
            }
            catch (Exception ex)
            {
                Exception("MessageBox.Show", ex);
            }
        }
    }
}
