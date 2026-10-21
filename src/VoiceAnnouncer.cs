using System;
using System.Globalization;

namespace UsbShareMonitor
{
    /// <summary>
    /// Decides when to speak: each time today's total passes another multiple of the step (512 MB by default).
    /// Whatever is already counted when it starts, or arrives in one lump such as the first-run import, becomes the
    /// starting point instead of being announced.
    /// </summary>
    internal sealed class VoiceAnnouncer
    {
        public const string DefaultText = "流量用去{used}，今日总计{total}";
        private const long MB = 1024 * 1024;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private readonly TrafficStore _store;
        private long _level = -1; // whole steps covered by today's total; -1 until the next Update syncs it

        public VoiceAnnouncer(TrafficStore store)
        {
            _store = store;
            Seed(store, "voice", "1");
            Seed(store, "voiceStepMB", "512");
            Seed(store, "voiceText", DefaultText);
            long mb = long.TryParse(store.GetSetting("voiceStepMB"), NumberStyles.Integer, Inv, out long v) && v > 0 ? v : 512;
            Step = mb * MB;
        }

        public long Step { get; }

        public bool Enabled
        {
            get => _store.GetSetting("voice") != "0";
            set
            {
                _store.Settings["voice"] = value ? "1" : "0";
                _store.MarkDirty();
            }
        }

        /// <summary>Take the current total as the new starting point without speaking.</summary>
        public void Resync() => _level = -1;

        /// <summary>The sentence to speak if today's total reached a new step since the last call, otherwise null.</summary>
        public string Update(long todayTotal)
        {
            long level = todayTotal / Step;
            long previous = _level;
            _level = level; // keeps tracking while muted, and follows the drop to 0 at midnight
            if (previous < 0 || level <= previous || !Enabled) return null;
            return Sentence((level - previous) * Step, level * Step);
        }

        /// <summary>What an announcement at the current total sounds like, for “试听”.</summary>
        public string Sample(long todayTotal) => Sentence(Step, Math.Max(1, todayTotal / Step) * Step);

        /// <summary>Round figures read aloud better than "1.50 GB": "512MB", "1GB", "1.5GB".</summary>
        public static string Size(long bytes) =>
            bytes < 1024 * MB
                ? (bytes / (double)MB).ToString("0.#", Inv) + "MB"
                : (bytes / (double)(1024 * MB)).ToString("0.##", Inv) + "GB";

        internal static void Seed(TrafficStore store, string key, string value)
        {
            if (store.GetSetting(key) != null) return;
            store.Settings[key] = value;
            store.MarkDirty();
        }

        private string Sentence(long used, long total)
        {
            string text = _store.GetSetting("voiceText");
            if (string.IsNullOrWhiteSpace(text)) text = DefaultText;
            return text.Replace("{used}", Size(used)).Replace("{total}", Size(total));
        }
    }
}
