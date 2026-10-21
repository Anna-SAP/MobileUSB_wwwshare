using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("USB 共享流量监控")]
[assembly: AssemblyProduct("USB 共享流量监控")]
[assembly: AssemblyDescription("实时统计手机 USB 网络共享消耗的流量")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace UsbShareMonitor
{
    internal static class Program
    {
        internal const string AppName = "USB 共享流量监控";
        internal const string AppId = "UsbShareMonitor";

        /// <summary>"1.1.0", taken from AssemblyVersion above so there is only one place to bump.</summary>
        internal static readonly string Version = typeof(Program).Assembly.GetName().Version.ToString(3);

        /// <summary>Folder holding data.ini, the CSV history and the event log.</summary>
        internal static string DataDir { get; private set; }

        /// <summary>True when started with --data (portable or test instance).</summary>
        internal static bool CustomDataDir { get; private set; }

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int processId);

        [STAThread]
        private static void Main(string[] args)
        {
            string dataArg = ArgValue(args, "--data");
            CustomDataDir = !string.IsNullOrWhiteSpace(dataArg);
            DataDir = CustomDataDir
                ? NormalizeDir(dataArg)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppId);

            // One instance per data folder; launching again just pokes the running instance.
            string scope = CustomDataDir ? "." + Fnv(DataDir.ToUpperInvariant()) : "";
            string showName = @"Local\" + AppId + ".Show" + scope;
            string exitName = @"Local\" + AppId + ".Exit" + scope;
            bool exitRequested = HasFlag(args, "--exit");

            using var mutex = new Mutex(true, @"Local\" + AppId + ".Instance" + scope, out bool firstInstance);
            if (!firstInstance)
            {
                if (!exitRequested) AllowSetForegroundWindow(-1); // let the running instance come to the front
                Signal(exitRequested ? exitName : showName);
                return;
            }
            if (exitRequested) return; // nothing to stop

            using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, showName);
            using var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, exitName);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => LogCrash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => LogCrash(e.ExceptionObject as Exception);

            using (var app = new TrayApp(HasFlag(args, "--tray"), showEvent, exitEvent))
                Application.Run(app);
            mutex.ReleaseMutex();
        }

        internal static void LogCrash(Exception ex)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                File.AppendAllText(Path.Combine(DataDir, "events.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  程序异常：" + ex + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Nowhere left to report to.
            }
        }

        private static void Signal(string name)
        {
            try
            {
                using var ev = EventWaitHandle.OpenExisting(name);
                ev.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // The other instance is still starting up or already gone.
            }
        }

        private static bool HasFlag(string[] args, string name) =>
            Array.Exists(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

        private static string ArgValue(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        private static string NormalizeDir(string path)
        {
            string full = Path.GetFullPath(path.Trim().Trim('"'));
            string trimmed = full.TrimEnd('\\', '/');
            return trimmed.EndsWith(":") ? full : trimmed;
        }

        private static string Fnv(string s)
        {
            uint h = 2166136261;
            foreach (char c in s) { h ^= c; h *= 16777619; }
            return h.ToString("x8");
        }
    }
}
