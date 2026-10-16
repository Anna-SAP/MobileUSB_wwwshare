using System.Windows.Forms;
using Microsoft.Win32;

namespace UsbShareMonitor
{
    /// <summary>Per-user "start with Windows" entry under HKCU\...\Run (no admin rights needed).</summary>
    internal static class AutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

        public static string Command =>
            Quote(Application.ExecutablePath) + " --tray" + (Program.CustomDataDir ? " --data " + Quote(Program.DataDir) : "");

        public static bool IsEnabled()
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (!(run?.GetValue(Program.AppId) is string)) return false;
            // Task Manager's "Startup apps" can disable the entry without removing it (odd first byte = disabled).
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return !(approved?.GetValue(Program.AppId) is byte[] flags && flags.Length > 0 && (flags[0] & 1) == 1);
        }

        public static void Set(bool enable)
        {
            using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enable) run.SetValue(Program.AppId, Command);
                else run.DeleteValue(Program.AppId, false);
            }
            // Turning it on here should override an earlier "disabled" from Task Manager.
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, true);
            approved?.DeleteValue(Program.AppId, false);
        }

        /// <summary>Keeps the entry pointing at this exe if the user moved it.</summary>
        public static void RefreshCommand()
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (run?.GetValue(Program.AppId) is string current && current != Command) run.SetValue(Program.AppId, Command);
        }

        // A trailing backslash would escape the closing quote, so double it.
        private static string Quote(string s) => "\"" + (s.EndsWith("\\") ? s + "\\" : s) + "\"";
    }
}
