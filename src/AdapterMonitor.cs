using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace UsbShareMonitor
{
    internal sealed class AdapterInfo
    {
        public string Id;          // interface GUID, stable for one device instance
        public string Name;        // e.g. "以太网 5" (changes across reconnects, display only)
        public string Description; // e.g. "Remote NDIS based Internet Sharing Device #2"
        public string IPv4;
        public bool IsUp;
        public NetworkInterface Nic;
    }

    internal sealed class SampleResult
    {
        public List<AdapterInfo> Adapters = new();
        public long Rx, Tx;                    // bytes since the previous sample
        public List<string> Events = new();    // connect / disconnect lines for the event log
        public bool BaselinesChanged;
    }

    /// <summary>
    /// Finds the phone's USB tethering adapter(s) by driver description / hardware ID rather than by
    /// interface name, and turns their raw byte counters into per-sample deltas that survive
    /// reconnects, counter resets and app restarts.
    /// </summary>
    internal sealed class AdapterMonitor : IDisposable
    {
        public const string DefaultKeywords = "Remote NDIS;RNDIS;UsbNcm;Apple Mobile Device Ethernet";
        private const string NetworkClassKey = @"SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}\";
        private const double RescanSeconds = 5;

        private readonly Dictionary<string, Baseline> _baselines;
        private readonly string[] _keywords;
        private readonly Dictionary<string, string> _pnpIds = new(StringComparer.OrdinalIgnoreCase);
        private List<AdapterInfo> _adapters = new();
        private bool _baselineOnly;
        private volatile bool _rescanRequested = true;
        private DateTime _lastScanUtc;

        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();

        public AdapterMonitor(Dictionary<string, Baseline> baselines, string keywords, bool firstRun)
        {
            _baselines = baselines;
            _keywords = (keywords ?? DefaultKeywords)
                .Split(new[] { ';', ',', '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(k => k.Trim()).Where(k => k.Length > 0).ToArray();
            _baselineOnly = firstRun;

            // Forget adapters not seen for a month. Counters last read before this boot have been reset since.
            var now = DateTime.Now;
            var bootTime = now - TimeSpan.FromMilliseconds(GetTickCount64());
            foreach (var id in _baselines.Where(kv => kv.Value.LastSeen < now.AddDays(-30)).Select(kv => kv.Key).ToList())
                _baselines.Remove(id);
            foreach (var b in _baselines.Values)
                if (b.LastSeen < bootTime) b.Detached = true;

            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        }

        /// <summary>Traffic already on the counters when the app ran for the very first time (not counted).</summary>
        public long PreexistingRx { get; private set; }
        public long PreexistingTx { get; private set; }

        public void RequestRescan() => _rescanRequested = true;

        public SampleResult Sample()
        {
            var result = new SampleResult();
            if (_rescanRequested || (DateTime.UtcNow - _lastScanUtc).TotalSeconds >= RescanSeconds) Rescan(result);

            var now = DateTime.Now;
            foreach (var a in _adapters)
            {
                long rx, tx;
                try
                {
                    var stats = a.Nic.GetIPStatistics(); // 64-bit counters (GetIfEntry2)
                    rx = stats.BytesReceived;
                    tx = stats.BytesSent;
                }
                catch (Exception)
                {
                    _rescanRequested = true; // unplugged since the last scan
                    continue;
                }

                if (!_baselines.TryGetValue(a.Id, out var b))
                {
                    _baselines[a.Id] = b = new Baseline();
                    result.BaselinesChanged = true;
                    if (_baselineOnly) { PreexistingRx += rx; PreexistingTx += tx; }
                    else { result.Rx += rx; result.Tx += tx; } // a new adapter instance counts from zero
                }
                else if (b.Detached || rx < b.Rx || tx < b.Tx)
                {
                    // Reconnected, re-enabled or rebooted: the counters started again from zero.
                    result.Rx += rx;
                    result.Tx += tx;
                    b.Detached = false;
                    result.BaselinesChanged = true;
                }
                else
                {
                    result.Rx += rx - b.Rx;
                    result.Tx += tx - b.Tx;
                }
                b.Rx = rx;
                b.Tx = tx;
                b.LastSeen = now;
            }
            _baselineOnly = false;
            result.Adapters = _adapters;
            return result;
        }

        public void Dispose()
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        }

        private void OnNetworkChanged(object sender, EventArgs e) => _rescanRequested = true;

        private void Rescan(SampleResult result)
        {
            _rescanRequested = false;
            _lastScanUtc = DateTime.UtcNow;
            NetworkInterface[] all;
            try { all = NetworkInterface.GetAllNetworkInterfaces(); }
            catch (Exception) { return; }

            var found = new List<AdapterInfo>();
            foreach (var nic in all)
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                if (!IsUsbTethering(nic)) continue;
                found.Add(new AdapterInfo
                {
                    Id = nic.Id,
                    Name = nic.Name,
                    Description = nic.Description,
                    IsUp = nic.OperationalStatus == OperationalStatus.Up,
                    IPv4 = FirstIPv4(nic),
                    Nic = nic,
                });
            }

            foreach (var a in found)
            {
                var old = _adapters.Find(o => SameId(o.Id, a.Id));
                if (old == null) result.Events.Add($"检测到 USB 共享网卡：{a.Name}（{a.Description}）");
                else if (old.IsUp != a.IsUp) result.Events.Add($"USB 共享网卡{(a.IsUp ? "已就绪" : "未就绪")}：{a.Name}");
            }
            foreach (var o in _adapters)
                if (!found.Exists(a => SameId(a.Id, o.Id))) result.Events.Add($"USB 共享网卡已断开：{o.Name}（{o.Description}）");
            foreach (var kv in _baselines)
                if (!found.Exists(a => SameId(a.Id, kv.Key))) kv.Value.Detached = true;

            _adapters = found;
        }

        private bool IsUsbTethering(NetworkInterface nic)
        {
            if (ContainsKeyword(nic.Description)) return true;
            if (!_pnpIds.TryGetValue(nic.Id, out var pnp))
            {
                // Hardware ID, e.g. USB\VID_04E8&PID_6863&RNDIS\..., in case the driver uses another description.
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(NetworkClassKey + nic.Id + @"\Connection");
                    pnp = key?.GetValue("PnPInstanceId") as string ?? "";
                }
                catch (Exception)
                {
                    pnp = "";
                }
                _pnpIds[nic.Id] = pnp;
            }
            return ContainsKeyword(pnp);
        }

        private bool ContainsKeyword(string text) =>
            !string.IsNullOrEmpty(text) && _keywords.Any(k => text.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);

        private static bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static string FirstIPv4(NetworkInterface nic)
        {
            try
            {
                foreach (var u in nic.GetIPProperties().UnicastAddresses)
                    if (u.Address.AddressFamily == AddressFamily.InterNetwork) return u.Address.ToString();
            }
            catch (Exception)
            {
                // Adapter is going away; the next scan will notice.
            }
            return "";
        }
    }
}
