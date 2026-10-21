using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace UsbShareMonitor
{
    internal sealed class ViewState
    {
        public List<AdapterInfo> Adapters;
        public DayRecord Today;
        public long MonthRx, MonthTx;
        public int MonthDays;
        public double RxSpeed, TxSpeed;
    }

    /// <summary>Main window. Closing it only hides it; the tray menu's "退出" really quits.</summary>
    internal sealed class MainForm : Form
    {
        private static readonly Color Muted = Color.FromArgb(0x5F, 0x66, 0x70);
        private static readonly Color CardBack = Color.FromArgb(0xF2, 0xF5, 0xF9);
        private static readonly Color DownColor = Color.FromArgb(0x0B, 0x63, 0xCE);
        private static readonly Color UpColor = Color.FromArgb(0x18, 0x80, 0x38);
        private static readonly Color TotalColor = Color.FromArgb(0x20, 0x24, 0x2A);
        private static readonly Color OnlineColor = Color.FromArgb(0x1E, 0xA4, 0x4F);
        private static readonly Color PendingColor = Color.FromArgb(0xE0, 0x8A, 0x00);
        private static readonly Color OfflineColor = Color.FromArgb(0xA0, 0xA6, 0xAD);

        private readonly TrafficStore _store;
        private readonly float _scale;
        private Dot _dot;
        private Label _statusTitle, _statusDetail, _todayTitle;
        private Label _rxValue, _txValue, _totalValue, _rxSub, _txSub, _totalSub;
        private ListView _dayList, _monthList;
        private ListViewItem _todayRow, _monthRow;
        private Font _boldRowFont;
        private CheckBox _autoStart, _voice;
        private LinkLabel _voicePreview;
        private readonly ToolTip _tips = new();
        private bool _syncingAutoStart, _syncingVoice;
        private DateTime _listedDay = DateTime.MinValue;

        public MainForm(TrafficStore store, Icon icon)
        {
            _store = store;
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) _scale = g.DpiX / 96f;

            SuspendLayout();
            AutoScaleMode = AutoScaleMode.None; // sizes below are scaled by hand with S()
            Font = new Font("Microsoft YaHei UI", 9f);
            Text = Program.AppName;
            Icon = icon;
            BackColor = Color.White;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(S(600), S(640));
            MinimumSize = new Size(S(520), S(520));

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(S(16), S(14), S(16), S(10)),
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(BuildStatus(), 0, 0);
            root.Controls.Add(BuildToday(), 0, 1);
            root.Controls.Add(BuildHistory(), 0, 2);
            root.Controls.Add(BuildFooter(), 0, 3);
            Controls.Add(root);
            ResumeLayout(true);
        }

        public bool AllowClose { get; set; }
        public event Action<bool> AutoStartToggled;
        public event Action HiddenToTray;
        public event Action OpenHistoryRequested;
        public event Action OpenFolderRequested;
        public event Action<bool> VoiceToggled;
        public event Action VoicePreviewRequested;

        public void SetAutoStartChecked(bool on)
        {
            _syncingAutoStart = true;
            _autoStart.Checked = on;
            _syncingAutoStart = false;
        }

        public void SetVoiceChecked(bool on)
        {
            _syncingVoice = true;
            _voice.Checked = on;
            _syncingVoice = false;
        }

        public void SetVoiceTip(string tip)
        {
            _tips.SetToolTip(_voice, tip);
            _tips.SetToolTip(_voicePreview, "按当前流量播报一次，听听效果");
        }

        public void UpdateView(ViewState v, bool rebuild)
        {
            var main = v.Adapters.FirstOrDefault(a => a.IsUp) ?? v.Adapters.FirstOrDefault();
            if (main == null)
            {
                _dot.ForeColor = OfflineColor;
                SetText(_statusTitle, "未检测到 USB 网络共享");
                SetText(_statusDetail, "请用数据线连接手机，并在手机上开启“USB 网络共享”");
            }
            else
            {
                _dot.ForeColor = main.IsUp ? OnlineColor : PendingColor;
                SetText(_statusTitle, (main.IsUp ? "监控中 · " : "网卡未就绪 · ") + main.Name);
                string detail = main.Description;
                if (!string.IsNullOrEmpty(main.IPv4)) detail += "  ·  " + main.IPv4;
                if (v.Adapters.Count > 1) detail += $"  ·  共 {v.Adapters.Count} 个共享网卡";
                SetText(_statusDetail, detail);
            }

            SetText(_todayTitle, "今日  " + Fmt.Day(v.Today.Date));
            SetText(_rxValue, Fmt.Bytes(v.Today.Rx));
            SetText(_txValue, Fmt.Bytes(v.Today.Tx));
            SetText(_totalValue, Fmt.Bytes(v.Today.Total));
            SetText(_rxSub, "↓ " + Fmt.Speed(v.RxSpeed));
            SetText(_txSub, "↑ " + Fmt.Speed(v.TxSpeed));
            SetText(_totalSub, "本月 " + Fmt.Bytes(v.MonthRx + v.MonthTx));

            if (rebuild || _listedDay != v.Today.Date) RebuildLists(v.Today.Date);
            else UpdateCurrentRows(v);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Only the user's own close (X button, Alt+F4) hides; shutdown / Task Manager really close.
            if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                HiddenToTray?.Invoke();
                return;
            }
            base.OnFormClosing(e);
        }

        private int S(int px) => (int)Math.Round(px * _scale);

        private Control BuildStatus()
        {
            var p = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 2,
                RowCount = 2,
                Margin = new Padding(0, 0, 0, S(12)),
            };
            p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _dot = new Dot { Size = new Size(S(11), S(11)), ForeColor = OfflineColor, Margin = new Padding(0, S(7), S(8), 0) };
            _statusTitle = new Label { AutoSize = true, Font = new Font(Font.FontFamily, 11f, FontStyle.Bold), Margin = new Padding(0) };
            _statusDetail = new Label { AutoSize = true, ForeColor = Muted, Margin = new Padding(0, S(3), 0, 0) };
            p.Controls.Add(_dot, 0, 0);
            p.Controls.Add(_statusTitle, 1, 0);
            p.Controls.Add(_statusDetail, 1, 1);
            return p;
        }

        private Control BuildToday()
        {
            var p = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 3,
                RowCount = 2,
                Margin = new Padding(0),
            };
            for (int i = 0; i < 3; i++) p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
            _todayTitle = new Label { AutoSize = true, Font = new Font(Font.FontFamily, 10f, FontStyle.Bold), Margin = new Padding(0, 0, 0, S(6)) };
            p.Controls.Add(_todayTitle, 0, 0);
            p.SetColumnSpan(_todayTitle, 3);
            p.Controls.Add(Card("下载", DownColor, 0, out _rxValue, out _rxSub), 0, 1);
            p.Controls.Add(Card("上传", UpColor, 1, out _txValue, out _txSub), 1, 1);
            p.Controls.Add(Card("合计", TotalColor, 2, out _totalValue, out _totalSub), 2, 1);
            return p;
        }

        private Control Card(string caption, Color accent, int index, out Label value, out Label sub)
        {
            var card = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = CardBack,
                Padding = new Padding(S(12), S(10), S(12), S(10)),
                Margin = new Padding(index == 0 ? 0 : S(5), 0, index == 2 ? 0 : S(5), 0),
            };
            card.Controls.Add(new Label { Text = caption, AutoSize = true, ForeColor = Muted, Margin = new Padding(0) }, 0, 0);
            value = new Label
            {
                Text = "0 B",
                AutoSize = true,
                ForeColor = accent,
                Font = new Font(Font.FontFamily, 15f, FontStyle.Bold),
                Margin = new Padding(0, S(4), 0, S(4)),
            };
            sub = new Label { Text = " ", AutoSize = true, ForeColor = Muted, Margin = new Padding(0) };
            card.Controls.Add(value, 0, 1);
            card.Controls.Add(sub, 0, 2);
            return card;
        }

        private Control BuildHistory()
        {
            var tabs = new TabControl { Dock = DockStyle.Fill, Margin = new Padding(0, S(14), 0, S(8)), Padding = new Point(S(12), S(4)) };
            _dayList = CreateList(new[] { "日期", "下载", "上传", "合计" }, new[] { 34f, 22f, 22f, 22f });
            _monthList = CreateList(new[] { "月份", "下载", "上传", "合计", "天数" }, new[] { 26f, 21f, 21f, 21f, 11f });
            tabs.TabPages.Add(Page("每日明细", _dayList));
            tabs.TabPages.Add(Page("每月汇总", _monthList));
            _boldRowFont = new Font(Font, FontStyle.Bold);
            return tabs;
        }

        private static TabPage Page(string title, Control content)
        {
            var page = new TabPage(title) { BackColor = Color.White, Padding = new Padding(0) };
            page.Controls.Add(content);
            return page;
        }

        private ListView CreateList(string[] headers, float[] weights)
        {
            var lv = new HistoryListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                BorderStyle = BorderStyle.None,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                SmallImageList = new ImageList { ImageSize = new Size(1, S(24)) }, // taller rows
            };
            for (int i = 0; i < headers.Length; i++)
                lv.Columns.Add(headers[i], S(100), i == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right);
            lv.Resize += (s, e) => FitColumns(lv, weights);
            return lv;
        }

        private static void FitColumns(ListView lv, float[] weights)
        {
            // Always leave room for the vertical scrollbar so a long history never needs a horizontal one.
            int width = lv.Width - SystemInformation.VerticalScrollBarWidth - 2;
            if (width <= 0) return;
            float sum = weights.Sum();
            int used = 0;
            for (int i = 0; i < lv.Columns.Count; i++)
            {
                int w = i == lv.Columns.Count - 1 ? width - used : (int)(width * weights[i] / sum);
                lv.Columns[i].Width = Math.Max(20, w);
                used += w;
            }
        }

        private Control BuildFooter()
        {
            var p = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 6,
                RowCount = 2,
                Margin = new Padding(0),
            };
            p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            _autoStart = new CheckBox { Text = "开机自启动", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, S(4), 0, S(4)) };
            _autoStart.CheckedChanged += (s, e) => { if (!_syncingAutoStart) AutoStartToggled?.Invoke(_autoStart.Checked); };
            _voice = new CheckBox { Text = "语音播报", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(S(14), S(4), 0, S(4)) };
            _voice.CheckedChanged += (s, e) => { if (!_syncingVoice) VoiceToggled?.Invoke(_voice.Checked); };
            _voicePreview = new LinkLabel { Text = "试听", AutoSize = true, Anchor = AnchorStyles.Left, LinkBehavior = LinkBehavior.HoverUnderline, Margin = new Padding(S(2), S(4), 0, S(4)) };
            _voicePreview.LinkClicked += (s, e) => VoicePreviewRequested?.Invoke();
            var hint = new Label
            {
                Text = "关闭窗口后将驻留系统托盘继续统计，右键托盘图标选择“退出”可完全退出。",
                AutoSize = true,
                ForeColor = Muted,
                Margin = new Padding(0, S(8), 0, 0),
            };
            p.Controls.Add(_autoStart, 0, 0);
            p.Controls.Add(_voice, 1, 0);
            p.Controls.Add(_voicePreview, 2, 0);
            p.Controls.Add(FooterButton("打开历史记录", () => OpenHistoryRequested?.Invoke()), 4, 0);
            p.Controls.Add(FooterButton("打开数据目录", () => OpenFolderRequested?.Invoke()), 5, 0);
            p.Controls.Add(hint, 0, 1);
            p.SetColumnSpan(hint, 6);
            return p;
        }

        private Button FooterButton(string text, Action onClick)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(S(10), S(3), S(10), S(3)),
                Margin = new Padding(S(8), 0, 0, 0),
                UseVisualStyleBackColor = true,
            };
            b.Click += (s, e) => onClick();
            return b;
        }

        private void RebuildLists(DateTime today)
        {
            _listedDay = today;
            var days = _store.Days.Where(d => d.Total > 0).ToList();
            if (!days.Exists(d => d.Date == today)) days.Add(_store.Get(today));
            days.Sort((a, b) => b.Date.CompareTo(a.Date));

            _dayList.BeginUpdate();
            _dayList.Items.Clear();
            _todayRow = null;
            foreach (var d in days)
            {
                var row = new ListViewItem(new[] { Fmt.Day(d.Date), Fmt.Bytes(d.Rx), Fmt.Bytes(d.Tx), Fmt.Bytes(d.Total) });
                if (d.Date == today) { row.Font = _boldRowFont; _todayRow = row; }
                _dayList.Items.Add(row);
            }
            _dayList.EndUpdate();

            var thisMonth = new DateTime(today.Year, today.Month, 1);
            _monthList.BeginUpdate();
            _monthList.Items.Clear();
            _monthRow = null;
            foreach (var g in days.GroupBy(d => new DateTime(d.Date.Year, d.Date.Month, 1)).OrderByDescending(g => g.Key))
            {
                long rx = g.Sum(d => d.Rx), tx = g.Sum(d => d.Tx);
                var row = new ListViewItem(new[]
                {
                    g.Key.ToString("yyyy年MM月", CultureInfo.InvariantCulture),
                    Fmt.Bytes(rx), Fmt.Bytes(tx), Fmt.Bytes(rx + tx),
                    g.Count(d => d.Total > 0).ToString(CultureInfo.InvariantCulture),
                });
                if (g.Key == thisMonth) { row.Font = _boldRowFont; _monthRow = row; }
                _monthList.Items.Add(row);
            }
            _monthList.EndUpdate();
        }

        private void UpdateCurrentRows(ViewState v)
        {
            if (_todayRow != null)
            {
                SetSub(_todayRow, 1, Fmt.Bytes(v.Today.Rx));
                SetSub(_todayRow, 2, Fmt.Bytes(v.Today.Tx));
                SetSub(_todayRow, 3, Fmt.Bytes(v.Today.Total));
            }
            if (_monthRow != null)
            {
                SetSub(_monthRow, 1, Fmt.Bytes(v.MonthRx));
                SetSub(_monthRow, 2, Fmt.Bytes(v.MonthTx));
                SetSub(_monthRow, 3, Fmt.Bytes(v.MonthRx + v.MonthTx));
                SetSub(_monthRow, 4, v.MonthDays.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static void SetText(Control c, string text)
        {
            if (c.Text != text) c.Text = text;
        }

        private static void SetSub(ListViewItem row, int index, string text)
        {
            if (row.SubItems[index].Text != text) row.SubItems[index].Text = text;
        }
    }

    /// <summary>Round status light.</summary>
    internal sealed class Dot : Control
    {
        public Dot()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(ForeColor);
            e.Graphics.FillEllipse(brush, 0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        }
    }

    /// <summary>Flicker-free list with the modern Explorer look.</summary>
    internal sealed class HistoryListView : ListView
    {
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

        public HistoryListView() => DoubleBuffered = true;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetWindowTheme(Handle, "Explorer", null);
        }
    }
}
