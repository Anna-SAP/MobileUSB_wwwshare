using System.IO;

namespace UsbShareMonitor
{
    /// <summary>Build-time helper: writes the icon embedded into the .exe.</summary>
    internal static class IconGen
    {
        private static void Main(string[] args)
        {
            File.WriteAllBytes(args[0], AppIcon.BuildIco(true, 16, 20, 24, 32, 40, 48, 64, 256));
        }
    }
}
