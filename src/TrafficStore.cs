using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace UsbShareMonitor
{
    internal sealed class DayRecord
    {
        public DateTime Date;
        public long Rx; // 下载（接收）字节数
        public long Tx; // 上传（发送）字节数
        public long Total => Rx + Tx;
    }

    /// <summary>Raw counters last read from one adapter instance, keyed by its interface GUID.</summary>
    internal sealed class Baseline
    {
        public long Rx, Tx;
        public DateTime LastSeen;

        /// <summary>Runtime only: the adapter was seen gone, so its counters restart from zero when it returns.</summary>
        public bool Detached;
    }

    /// <summary>
    /// Keeps settings, adapter baselines and per-day totals in one file (data.ini). It is written atomically,
    /// so totals and baselines can never disagree after a crash. The daily totals are mirrored to a CSV for people.
    /// </summary>
    internal sealed class TrafficStore
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private const string DayFormat = "yyyy-MM-dd";
        private const string StampFormat = "yyyy-MM-ddTHH:mm:ss";

        private readonly SortedDictionary<DateTime, DayRecord> _days = new();
        private bool _csvBlocked;

        public TrafficStore(string dataDir)
        {
            DataDir = dataDir;
            Directory.CreateDirectory(dataDir);
            bool hadData = File.Exists(DataPath) || File.Exists(DataPath + ".bak");
            if (!TryLoad(DataPath)) TryLoad(DataPath + ".bak");
            IsFirstRun = !hadData;
        }

        public string DataDir { get; }
        public string DataPath => Path.Combine(DataDir, "data.ini");
        public string CsvPath => Path.Combine(DataDir, "daily_traffic.csv");
        public string LogPath => Path.Combine(DataDir, "events.log");

        public Dictionary<string, string> Settings { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Baseline> Baselines { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IsFirstRun { get; }
        public bool Dirty { get; private set; }

        /// <summary>All recorded days, oldest first.</summary>
        public IEnumerable<DayRecord> Days => _days.Values;

        public DayRecord Get(DateTime day) =>
            _days.TryGetValue(day.Date, out var r) ? r : new DayRecord { Date = day.Date };

        public void Add(DateTime day, long rx, long tx)
        {
            if (rx <= 0 && tx <= 0) return;
            if (!_days.TryGetValue(day.Date, out var r)) _days[day.Date] = r = new DayRecord { Date = day.Date };
            r.Rx += Math.Max(0, rx);
            r.Tx += Math.Max(0, tx);
            Dirty = true;
        }

        public void MonthTotals(DateTime day, out long rx, out long tx, out int activeDays)
        {
            rx = tx = 0;
            activeDays = 0;
            foreach (var d in _days.Values)
            {
                if (d.Date.Year != day.Year || d.Date.Month != day.Month) continue;
                rx += d.Rx;
                tx += d.Tx;
                if (d.Total > 0) activeDays++;
            }
        }

        public string GetSetting(string key) => Settings.TryGetValue(key, out var v) ? v : null;

        public void MarkDirty() => Dirty = true;

        public void Save()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# USB 共享流量监控数据文件。如需修改 [settings] 里的设置（keywords、语音播报等），请先从托盘菜单退出程序。");
            sb.AppendLine("[settings]");
            foreach (var kv in Settings) sb.Append(kv.Key).Append('=').AppendLine(kv.Value);
            sb.AppendLine("[adapters]");
            foreach (var kv in Baselines)
                sb.Append(kv.Key).Append('=')
                  .Append(kv.Value.Rx.ToString(Inv)).Append(',')
                  .Append(kv.Value.Tx.ToString(Inv)).Append(',')
                  .AppendLine(kv.Value.LastSeen.ToString(StampFormat, Inv));
            sb.AppendLine("[days]");
            foreach (var d in _days.Values)
                if (d.Total > 0)
                    sb.Append(d.Date.ToString(DayFormat, Inv)).Append('=')
                      .Append(d.Rx.ToString(Inv)).Append(',')
                      .AppendLine(d.Tx.ToString(Inv));

            WriteAtomic(DataPath, sb.ToString(), keepBackup: true);
            Dirty = false;
            ExportCsv();
        }

        public void Log(string message)
        {
            try
            {
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > 1024 * 1024)
                {
                    string old = Path.Combine(DataDir, "events.old.log");
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(LogPath, old);
                }
                File.AppendAllText(LogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv) + "  " + message + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Logging must never take the monitor down.
            }
        }

        private bool TryLoad(string path)
        {
            if (!File.Exists(path)) return false;
            try
            {
                string section = "";
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    if (line[0] == '[')
                    {
                        section = line.Trim('[', ']').Trim().ToLowerInvariant();
                        continue;
                    }
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    string[] parts = val.Split(',');
                    switch (section)
                    {
                        case "settings":
                            Settings[key] = val;
                            break;
                        case "adapters":
                            Baselines[key] = new Baseline
                            {
                                Rx = long.Parse(parts[0], Inv),
                                Tx = long.Parse(parts[1], Inv),
                                LastSeen = parts.Length > 2 ? DateTime.ParseExact(parts[2], StampFormat, Inv) : DateTime.Now,
                            };
                            break;
                        case "days":
                            var date = DateTime.ParseExact(key, DayFormat, Inv);
                            _days[date] = new DayRecord { Date = date, Rx = long.Parse(parts[0], Inv), Tx = long.Parse(parts[1], Inv) };
                            break;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Settings.Clear();
                Baselines.Clear();
                _days.Clear();
                Log("读取 " + Path.GetFileName(path) + " 失败，已另存为 .corrupt 备查：" + ex.Message);
                try { File.Copy(path, path + ".corrupt", true); } catch { }
                return false;
            }
        }

        private void ExportCsv()
        {
            var sb = new StringBuilder();
            sb.AppendLine("日期,星期,下载(字节),上传(字节),合计(字节),下载,上传,合计");
            foreach (var d in _days.Values)
            {
                if (d.Total <= 0) continue;
                sb.Append(d.Date.ToString(DayFormat, Inv)).Append(',')
                  .Append(Fmt.WeekDay(d.Date)).Append(',')
                  .Append(d.Rx.ToString(Inv)).Append(',')
                  .Append(d.Tx.ToString(Inv)).Append(',')
                  .Append(d.Total.ToString(Inv)).Append(',')
                  .Append(Fmt.Bytes(d.Rx)).Append(',')
                  .Append(Fmt.Bytes(d.Tx)).Append(',')
                  .AppendLine(Fmt.Bytes(d.Total));
            }
            try
            {
                // Once we know the file is held open (Excel), don't stall the UI thread retrying every save.
                WriteAtomic(CsvPath, sb.ToString(), keepBackup: false, retries: _csvBlocked ? 0 : 2, allowOverwrite: true);
                _csvBlocked = false;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // data.ini is already saved, so the CSV simply catches up on a later save.
                if (!_csvBlocked) Log("暂时无法写入 daily_traffic.csv（可能正被 Excel 打开），稍后自动重试：" + ex.Message);
                _csvBlocked = true;
                try { File.Delete(CsvPath + ".tmp"); } catch { }
            }
        }

        private static void WriteAtomic(string path, string content, bool keepBackup, int retries = 3, bool allowOverwrite = false)
        {
            string tmp = path + ".tmp";
            byte[] bom = Encoding.UTF8.GetPreamble(); // BOM so Excel shows the Chinese headers correctly
            byte[] body = Encoding.UTF8.GetBytes(content);
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bom, 0, bom.Length);
                fs.Write(body, 0, body.Length);
                fs.Flush(true);
            }
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Replace(tmp, path, keepBackup ? path + ".bak" : null, true);
                    else File.Move(tmp, path);
                    return;
                }
                catch (IOException) when (attempt < retries)
                {
                    // Virus scanners and file watchers briefly hold freshly written files.
                    Thread.Sleep(50 * (attempt + 1));
                }
                catch (IOException) when (allowOverwrite)
                {
                    // Replace needs delete access; a plain overwrite only needs write sharing.
                    File.Copy(tmp, path, true);
                    File.Delete(tmp);
                    return;
                }
            }
        }
    }
}
