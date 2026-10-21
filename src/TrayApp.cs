using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

namespace UsbShareMonitor
{
    /// <summary>Owns the tray icon, the 1-second sampling timer and the (usually hidden) main window.</summary>
    internal sealed class TrayApp : ApplicationContext
    {
        private const int SaveIntervalSeconds = 30;

        private readonly TrafficStore _store;
        private readonly AdapterMonitor _monitor;
        private readonly VoiceAnnouncer _announcer;
        private readonly Speech _speech;
        private readonly MainForm _form;
        private readonly NotifyIcon _tray;
        private readonly Timer _timer;
        private readonly Icon _iconOnline, _iconOffline, _windowIcon;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<RegisteredWaitHandle> _waits = new();
        private ToolStripMenuItem _todayMenuItem, _autoStartMenuItem, _voiceMenuItem;

        private List<AdapterInfo> _adapters = new();
        private DateTime _currentDay = DateTime.Today;
        private DateTime _lastSaveUtc = DateTime.MinValue;
        private double _lastSampleAt;
        private bool _hasSampled;
        private double _rxSpeed, _txSpeed;
        private bool? _online;
        private bool _exiting, _formClosed;
        private string _lastTickError;

        public TrayApp(bool startInTray, WaitHandle showSignal, WaitHandle exitSignal)
        {
            _store = new TrafficStore(Program.DataDir);
            if (_store.GetSetting("keywords") == null) _store.Settings["keywords"] = AdapterMonitor.DefaultKeywords;
            _store.Log("程序启动" + (startInTray ? "（托盘模式）" : "") + "，数据目录：" + _store.DataDir);
            if (_store.IsFirstRun) _store.Log("首次运行：以网卡当前计数为起点开始统计");
            _monitor = new AdapterMonitor(_store.Baselines, _store.GetSetting("keywords"), _store.IsFirstRun);
            _announcer = new VoiceAnnouncer(_store);
            _speech = new Speech(_store);

            int small = SystemInformation.SmallIconSize.Width;
            _iconOnline = AppIcon.Create(true, small);
            _iconOffline = AppIcon.Create(false, small);
            _windowIcon = AppIcon.Create(true, 16, 20, 24, 32, 40, 48, 64);

            _form = new MainForm(_store, _windowIcon);
            _form.AutoStartToggled += SetAutoStart;
            _form.HiddenToTray += OnHiddenToTray;
            _form.OpenHistoryRequested += OpenHistory;
            _form.OpenFolderRequested += () => OpenInShell(_store.DataDir);
            _form.VoiceToggled += SetVoice;
            _form.VoicePreviewRequested += PreviewVoice;
            _form.SetVoiceTip($"今日合计每增加 {VoiceAnnouncer.Size(_announcer.Step)} 语音播报一次");
            _form.VisibleChanged += (s, e) => { if (_form.Visible) RefreshWindow(true); };
            _form.FormClosed += (s, e) => { _formClosed = true; ExitApp("窗口被系统关闭"); };
            _ = _form.Handle; // create the window now so background threads can marshal onto the UI thread

            _tray = new NotifyIcon { Icon = _iconOffline, Text = Program.AppName, ContextMenuStrip = BuildTrayMenu() };
            _tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
            _tray.Visible = true;

            try { AutoStart.RefreshCommand(); } catch (Exception) { }
            SyncAutoStartUi();
            SyncVoiceUi();

            Tick(); // the first sample sets the counter baselines
            _timer = new Timer { Interval = 1000 };
            _timer.Tick += (s, e) => Tick();
            _timer.Start();

            _waits.Add(ThreadPool.RegisterWaitForSingleObject(showSignal, (o, t) => Post(ShowWindow), null, Timeout.Infinite, false));
            _waits.Add(ThreadPool.RegisterWaitForSingleObject(exitSignal, (o, t) => Post(() => ExitApp("收到 --exit 指令")), null, Timeout.Infinite, false));
            SystemEvents.SessionEnding += OnSessionEnding;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            if (!startInTray)
            {
                ShowWindow();
                if (_monitor.PreexistingRx + _monitor.PreexistingTx > 0) Post(AskCountPreexisting);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer?.Dispose();
                _speech?.Dispose();
                _tray?.Dispose();
                _form?.Dispose();
                _iconOnline?.Dispose();
                _iconOffline?.Dispose();
                _windowIcon?.Dispose();
            }
            base.Dispose(disposing);
        }

        private void Tick()
        {
            try
            {
                Sample();
                Announce();
                if (_store.Dirty && (DateTime.UtcNow - _lastSaveUtc).TotalSeconds >= SaveIntervalSeconds) Save();
                UpdateTray();
                RefreshWindow(false);
                _lastTickError = null;
            }
            catch (Exception ex)
            {
                if (ex.Message != _lastTickError) _store.Log("统计出错：" + ex);
                _lastTickError = ex.Message;
            }
        }

        private void Sample()
        {
            double now = _clock.Elapsed.TotalSeconds;
            double elapsed = now - _lastSampleAt;
            _lastSampleAt = now;

            var r = _monitor.Sample();
            foreach (var line in r.Events) _store.Log(line);
            if (r.Events.Count > 0 || r.BaselinesChanged) _store.MarkDirty();

            var today = DateTime.Today;
            if (today != _currentDay)
            {
                var done = _store.Get(_currentDay);
                _store.Log($"日结 {Fmt.Day(done.Date)}：下载 {Fmt.Bytes(done.Rx)}，上传 {Fmt.Bytes(done.Tx)}，合计 {Fmt.Bytes(done.Total)}");
                _currentDay = today;
                _store.MarkDirty();
                _lastSaveUtc = DateTime.MinValue; // save right at the day boundary
            }
            _store.Add(today, r.Rx, r.Tx);

            // The first sample, and the first after sleep, cover an unknown span and say nothing about speed.
            bool meaningful = _hasSampled && elapsed > 0.2 && elapsed < 5;
            _rxSpeed = meaningful ? r.Rx / elapsed : 0;
            _txSpeed = meaningful ? r.Tx / elapsed : 0;
            _hasSampled = true;
            _adapters = r.Adapters;
        }

        private void Announce()
        {
            string sentence = _announcer.Update(_store.Get(_currentDay).Total);
            if (sentence == null) return;
            _store.Log("语音播报：" + sentence);
            _speech.Speak(sentence);
        }

        private void Save()
        {
            _lastSaveUtc = DateTime.UtcNow;
            try { _store.Save(); }
            catch (Exception ex) { _store.Log("保存数据失败：" + ex.Message); }
        }

        private void UpdateTray()
        {
            bool online = _adapters.Any(a => a.IsUp);
            if (_online != online)
            {
                _online = online;
                _tray.Icon = online ? _iconOnline : _iconOffline;
            }
            var t = _store.Get(_currentDay);
            string text = $"USB 共享 · 今日 {Fmt.Bytes(t.Total)}\n↓ {Fmt.Bytes(t.Rx)}   ↑ {Fmt.Bytes(t.Tx)}" + (online ? "" : "\n未连接");
            if (text.Length > 63) text = text.Substring(0, 63); // NotifyIcon limit
            if (_tray.Text != text) _tray.Text = text;
        }

        private void RefreshWindow(bool rebuild)
        {
            if (_exiting || !_form.Visible) return;
            _store.MonthTotals(_currentDay, out long monthRx, out long monthTx, out int monthDays);
            _form.UpdateView(new ViewState
            {
                Adapters = _adapters,
                Today = _store.Get(_currentDay),
                MonthRx = monthRx,
                MonthTx = monthTx,
                MonthDays = monthDays,
                RxSpeed = _rxSpeed,
                TxSpeed = _txSpeed,
            }, rebuild);
        }

        private ContextMenuStrip BuildTrayMenu()
        {
            var menu = new ContextMenuStrip();
            var show = new ToolStripMenuItem("显示主窗口", null, (s, e) => ShowWindow());
            show.Font = new Font(show.Font, FontStyle.Bold);
            _todayMenuItem = new ToolStripMenuItem("今日") { Enabled = false };
            _autoStartMenuItem = new ToolStripMenuItem("开机自启动", null, (s, e) => SetAutoStart(!_autoStartMenuItem.Checked));
            _voiceMenuItem = new ToolStripMenuItem("语音播报", null, (s, e) => SetVoice(!_voiceMenuItem.Checked));
            var history = new ToolStripMenuItem("打开历史记录 (CSV)", null, (s, e) => OpenHistory());
            var folder = new ToolStripMenuItem("打开数据目录", null, (s, e) => OpenInShell(_store.DataDir));
            var version = new ToolStripMenuItem("版本 " + Program.Version) { Enabled = false };
            var exit = new ToolStripMenuItem("退出", null, (s, e) => ExitApp("用户从托盘菜单退出"));
            menu.Items.AddRange(new ToolStripItem[]
            {
                show, _todayMenuItem, new ToolStripSeparator(),
                _autoStartMenuItem, _voiceMenuItem, history, folder, new ToolStripSeparator(),
                version, exit,
            });
            menu.Opening += (s, e) =>
            {
                var t = _store.Get(_currentDay);
                _todayMenuItem.Text = $"今日 {Fmt.Bytes(t.Total)}（↓ {Fmt.Bytes(t.Rx)}  ↑ {Fmt.Bytes(t.Tx)}）";
                SyncAutoStartUi();
            };
            return menu;
        }

        private void ShowWindow()
        {
            if (_exiting) return;
            _form.Show();
            if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
            _form.Activate();
        }

        private void OnHiddenToTray()
        {
            if (_store.GetSetting("trayHintShown") == "1") return;
            _tray.ShowBalloonTip(5000, Program.AppName, "程序仍在后台统计流量。\n右键托盘图标选择“退出”可完全关闭。", ToolTipIcon.Info);
            _store.Settings["trayHintShown"] = "1";
            _store.MarkDirty();
        }

        private void AskCountPreexisting()
        {
            long rx = _monitor.PreexistingRx, tx = _monitor.PreexistingTx;
            var answer = MessageBox.Show(_form,
                "首次启动时，USB 共享网卡上已有一部分流量（本次连接或开机以来的累计值）：\n\n" +
                $"下载 {Fmt.Bytes(rx)}，上传 {Fmt.Bytes(tx)}\n\n" +
                $"是否把这部分计入今天（{Fmt.Day(_currentDay)}）？\n选“否”则从现在开始统计。",
                Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer == DialogResult.Yes)
            {
                _store.Add(_currentDay, rx, tx);
                _store.Log($"首次运行：已将启动前的流量计入今天（下载 {Fmt.Bytes(rx)}，上传 {Fmt.Bytes(tx)}）");
                _announcer.Resync(); // an import, not live usage: don't announce it
                Save();
                RefreshWindow(true);
                UpdateTray();
            }
            else
            {
                _store.Log("首次运行：未计入启动前的流量");
            }
        }

        private void SetAutoStart(bool enable)
        {
            try
            {
                AutoStart.Set(enable);
                _store.Log(enable ? "已开启开机自启动：" + AutoStart.Command : "已关闭开机自启动");
            }
            catch (Exception ex)
            {
                MessageBox.Show(_form.Visible ? _form : null, "设置开机自启动失败：" + ex.Message,
                    Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            SyncAutoStartUi();
        }

        private void SyncAutoStartUi()
        {
            bool on = AutoStart.IsEnabled();
            _autoStartMenuItem.Checked = on;
            _form.SetAutoStartChecked(on);
        }

        private void SetVoice(bool enable)
        {
            if (_announcer.Enabled != enable)
            {
                _announcer.Enabled = enable;
                _store.Log(enable ? "已开启语音播报" : "已关闭语音播报");
            }
            SyncVoiceUi();
        }

        private void SyncVoiceUi()
        {
            _voiceMenuItem.Checked = _announcer.Enabled;
            _form.SetVoiceChecked(_announcer.Enabled);
        }

        private void PreviewVoice() => _speech.Speak(_announcer.Sample(_store.Get(_currentDay).Total));

        private void OpenHistory()
        {
            Save(); // so the CSV includes the last few seconds
            OpenInShell(_store.CsvPath);
        }

        private void OpenInShell(string path)
        {
            try
            {
                if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("文件尚未生成", path);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(_form.Visible ? _form : null, "无法打开：" + path + "\n" + ex.Message,
                    Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            RunOnUi(() =>
            {
                if (_exiting) return;
                Sample();
                Save();
                _store.Log(e.Reason == SessionEndReasons.Logoff ? "系统正在注销，已保存数据" : "系统正在关机，已保存数据");
            });
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend)
                RunOnUi(() => { if (!_exiting) { Sample(); Save(); } });
            else if (e.Mode == PowerModes.Resume)
                _monitor.RequestRescan();
        }

        private void ExitApp(string reason)
        {
            if (_exiting) return;
            try
            {
                _timer?.Stop();
                Sample(); // pick up the last second
            }
            catch (Exception)
            {
                // Still save what we have.
            }
            _exiting = true;
            Save();
            _store.Log("程序退出（" + reason + "）");

            SystemEvents.SessionEnding -= OnSessionEnding;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            foreach (var w in _waits) w.Unregister(null);
            _monitor.Dispose();
            _speech.Dispose();
            _tray.Visible = false;
            _form.AllowClose = true;
            if (!_formClosed && !_form.IsDisposed) _form.Close();
            ExitThread();
        }

        private void Post(Action action)
        {
            try
            {
                if (!_form.IsDisposed) _form.BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
                // Window already gone: we are exiting anyway.
            }
        }

        private void RunOnUi(Action action)
        {
            if (_form.IsDisposed) return;
            if (!_form.InvokeRequired) { action(); return; }
            try { _form.Invoke(action); }
            catch (InvalidOperationException) { }
        }
    }
}
