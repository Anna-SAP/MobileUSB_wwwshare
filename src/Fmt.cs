using System;
using System.Globalization;

namespace UsbShareMonitor
{
    internal static class Fmt
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly string[] Units = { "KB", "MB", "GB", "TB" };
        private static readonly string[] WeekDays = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };

        /// <summary>Human readable size using 1024-based units, the way carriers and phones count.</summary>
        public static string Bytes(long bytes)
        {
            if (bytes < 1024) return bytes.ToString(Inv) + " B";
            double v = bytes;
            int unit = -1;
            do { v /= 1024; unit++; } while (v >= 1024 && unit < Units.Length - 1);
            return v.ToString(v >= 100 ? "0.0" : "0.00", Inv) + " " + Units[unit];
        }

        public static string Speed(double bytesPerSecond) => Bytes((long)Math.Max(0, bytesPerSecond)) + "/s";

        public static string WeekDay(DateTime d) => WeekDays[(int)d.DayOfWeek];

        public static string Day(DateTime d) => d.ToString("yyyy-MM-dd", Inv) + " " + WeekDay(d);
    }
}
