using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Media;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace UsbShareMonitor
{
    /// <summary>
    /// Says the announcements. Windows has no child voice, so by default its own Chinese voice is pitched up to sound
    /// like a little girl. With an Azure Speech key in data.ini a real child voice is used instead, and the local voice
    /// takes over whenever Azure can't be reached. Must be used from the UI thread.
    /// </summary>
    internal sealed class Speech : IDisposable
    {
        private readonly TrafficStore _store;
        private readonly AzureVoice _azure;
        private LocalVoice _local;
        private string _lastAzureError, _lastLocalError;
        private bool _disposed;

        public Speech(TrafficStore store)
        {
            _store = store;
            VoiceAnnouncer.Seed(store, "voiceName", "");
            VoiceAnnouncer.Seed(store, "voicePitch", "");
            VoiceAnnouncer.Seed(store, "azureKey", "");
            VoiceAnnouncer.Seed(store, "azureRegion", "");
            VoiceAnnouncer.Seed(store, "azureVoice", AzureVoice.DefaultVoice);

            string key = store.GetSetting("azureKey")?.Trim(), region = store.GetSetting("azureRegion")?.Trim();
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(region)) return;
            string voice = store.GetSetting("azureVoice")?.Trim();
            _azure = new AzureVoice(key, region, string.IsNullOrEmpty(voice) ? AzureVoice.DefaultVoice : voice,
                Path.Combine(store.DataDir, "voice_cache"));
            store.Log("语音播报使用 Azure 语音：" + _azure.VoiceName + "（" + region + "）");
        }

        public async void Speak(string text)
        {
            if (_disposed) return;
            if (_azure != null)
            {
                try
                {
                    _local?.Stop();
                    await _azure.SpeakAsync(text);
                    _lastAzureError = null;
                    return;
                }
                catch (Exception ex)
                {
                    string error = AzureVoice.Describe(ex);
                    if (error != _lastAzureError) _store.Log("Azure 语音不可用，本次改用本机语音：" + error);
                    _lastAzureError = error;
                    if (_disposed) return;
                }
            }
            SpeakLocally(text);
        }

        public void Dispose()
        {
            _disposed = true;
            _azure?.Dispose();
            _local?.Dispose();
        }

        private void SpeakLocally(string text)
        {
            try
            {
                if (_local == null)
                {
                    _local = new LocalVoice(_store.GetSetting("voiceName"), _store.GetSetting("voicePitch"));
                    _store.Log($"语音播报使用本机语音：{_local.Name}（音调 {_local.Pitch:+0;-0;0}）");
                    if (!_local.IsChinese)
                        _store.Log("未找到中文语音，请在 Windows 设置 > 时间和语言 > 语音 中添加“中文(简体，中国)”语音");
                }
                _local.Speak(text);
                _lastLocalError = null;
            }
            catch (Exception ex)
            {
                if (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
                if (ex.Message != _lastLocalError) _store.Log("本机语音播报失败：" + ex.Message);
                _lastLocalError = ex.Message;
                _local?.Dispose();
                _local = null;
            }
        }
    }

    /// <summary>
    /// SAPI through late-bound COM. Unlike System.Speech it also reaches the OneCore voices, such as Microsoft Yaoyao,
    /// the youngest-sounding of the Chinese voices that come with Windows.
    /// </summary>
    internal sealed class LocalVoice : IDisposable
    {
        private const int SpeakAsync = 1, PurgeBeforeSpeak = 2, IsXml = 8;

        // SAPI pitch scale is -10..10. +10 lifts Yaoyao from about 200 Hz to 260 Hz, the range of a young girl.
        private const int DefaultPitch = 10;

        private static readonly string[] Categories =
        {
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech_OneCore\Voices",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech\Voices",
        };

        private object _voice;

        public LocalVoice(string preferred, string pitch)
        {
            _voice = Com.Create("SAPI.SpVoice");
            object best = null;
            int bestScore = int.MinValue;
            foreach (object token in Tokens())
            {
                int score = Score(token, preferred);
                if (score > bestScore) { best = token; bestScore = score; }
            }
            if (best != null) Com.PutRef(_voice, "Voice", best);

            object current = Com.Get(_voice, "Voice");
            Name = Description(current);
            IsChinese = Attribute(current, "Language").Split(';').Contains("804");
            string age = Attribute(current, "Age");
            bool young = age == "Child" || age == "Teen";
            Pitch = int.TryParse(pitch, out int p) ? Math.Max(-10, Math.Min(10, p)) : young ? 0 : DefaultPitch;
        }

        public string Name { get; }
        public int Pitch { get; }
        public bool IsChinese { get; }

        public void Speak(string text) =>
            Com.Call(_voice, "Speak", $"<pitch absmiddle=\"{Pitch}\">{SecurityElement.Escape(text)}</pitch>",
                SpeakAsync | PurgeBeforeSpeak | IsXml);

        public void Stop()
        {
            try { Com.Call(_voice, "Speak", "", SpeakAsync | PurgeBeforeSpeak); }
            catch (Exception) { }
        }

        public void Dispose()
        {
            if (_voice == null) return;
            Stop();
            Marshal.ReleaseComObject(_voice);
            _voice = null;
        }

        private static IEnumerable<object> Tokens()
        {
            foreach (string id in Categories)
            {
                object tokens;
                try
                {
                    object category = Com.Create("SAPI.SpObjectTokenCategory");
                    Com.Call(category, "SetId", id, false);
                    tokens = Com.Call(category, "EnumerateTokens");
                }
                catch (Exception)
                {
                    continue; // this category doesn't exist on every edition of Windows
                }
                int count = (int)Com.Get(tokens, "Count");
                for (int i = 0; i < count; i++) yield return Com.Call(tokens, "Item", i);
            }
        }

        /// <summary>A Chinese female voice, younger first. A name from data.ini (voiceName) beats everything.</summary>
        private static int Score(object token, string preferred)
        {
            string name = Description(token);
            int score = 0;
            if (!string.IsNullOrWhiteSpace(preferred) && name.IndexOf(preferred.Trim(), StringComparison.OrdinalIgnoreCase) >= 0) score += 1000;
            if (Attribute(token, "Language").Split(';').Contains("804")) score += 100;
            if (Attribute(token, "Gender") == "Female") score += 20;
            string age = Attribute(token, "Age");
            if (age == "Child") score += 40;
            else if (age == "Teen") score += 30;
            if (name.IndexOf("Yaoyao", StringComparison.OrdinalIgnoreCase) >= 0) score += 5;
            return score;
        }

        private static string Description(object token)
        {
            try { return (string)Com.Call(token, "GetDescription", 0) ?? ""; }
            catch (Exception) { return ""; }
        }

        private static string Attribute(object token, string name)
        {
            try { return (string)Com.Call(token, "GetAttribute", name) ?? ""; }
            catch (Exception) { return ""; }
        }
    }

    /// <summary>
    /// Azure neural text-to-speech over its REST API. Each sentence is fetched once and kept as a WAV in voice_cache,
    /// so a figure like "今日总计1.5GB" is never downloaded twice.
    /// </summary>
    internal sealed class AzureVoice : IDisposable
    {
        public const string DefaultVoice = "zh-CN-XiaoyouNeural"; // 晓悠，女童声

        private readonly string _key, _region, _cacheDir;
        private SoundPlayer _player;

        public AzureVoice(string key, string region, string voice, string cacheDir)
        {
            _key = key;
            _region = region.ToLowerInvariant();
            VoiceName = voice;
            _cacheDir = cacheDir;
            // Without a target-framework attribute (plain csc build) .NET 4.8 would only offer TLS 1.0.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        public string VoiceName { get; }

        public async Task SpeakAsync(string text)
        {
            string file = Path.Combine(_cacheDir, Hash(VoiceName + "\n" + text) + ".wav");
            if (!File.Exists(file)) await Task.Run(() => Download(text, file));
            Stop();
            try
            {
                _player = new SoundPlayer(file);
                _player.Play();
            }
            catch (Exception)
            {
                File.Delete(file); // a damaged cache entry is fetched again next time
                throw;
            }
        }

        public void Dispose() => Stop();

        /// <summary>A short reason for the log; never includes the key.</summary>
        public static string Describe(Exception ex)
        {
            if (ex is WebException we && we.Response is HttpWebResponse r)
            {
                int code = (int)r.StatusCode;
                string hint = code == 401 ? "密钥无效或与区域不匹配" : code == 429 ? "请求太频繁或额度已用完" : code == 400 ? "语音名称或参数有误" : r.StatusDescription;
                return $"HTTP {code}，{hint}";
            }
            return ex.Message;
        }

        private void Stop()
        {
            _player?.Stop();
            _player?.Dispose();
            _player = null;
        }

        private void Download(string text, string file)
        {
            if (!Regex.IsMatch(_region, "^[a-z0-9]+$")) throw new InvalidOperationException("azureRegion 格式不对，应类似 eastasia");
            var request = (HttpWebRequest)WebRequest.Create($"https://{_region}.tts.speech.microsoft.com/cognitiveservices/v1");
            request.Method = "POST";
            request.ContentType = "application/ssml+xml";
            request.UserAgent = Program.AppId;
            request.Headers["Ocp-Apim-Subscription-Key"] = _key;
            request.Headers["X-Microsoft-OutputFormat"] = "riff-16khz-16bit-mono-pcm";
            request.Timeout = request.ReadWriteTimeout = 15000;

            string ssml = "<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"zh-CN\">" +
                          $"<voice name=\"{SecurityElement.Escape(VoiceName)}\">{SecurityElement.Escape(text)}</voice></speak>";
            byte[] body = Encoding.UTF8.GetBytes(ssml);
            using (var s = request.GetRequestStream()) s.Write(body, 0, body.Length);

            Directory.CreateDirectory(_cacheDir);
            string tmp = file + ".tmp";
            using (var response = request.GetResponse())
            using (var input = response.GetResponseStream())
            using (var output = File.Create(tmp))
                input.CopyTo(output);
            if (File.Exists(file)) File.Delete(file);
            File.Move(tmp, file);
        }

        private static string Hash(string s)
        {
            using var sha = SHA1.Create();
            return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(s)).Select(b => b.ToString("x2")));
        }
    }

    /// <summary>Late-bound COM calls, so no interop assembly has to ship with the exe.</summary>
    internal static class Com
    {
        public static object Create(string progId) => Activator.CreateInstance(Type.GetTypeFromProgID(progId, true));

        public static object Call(object target, string method, params object[] args) =>
            target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, args);

        public static object Get(object target, string property) =>
            target.GetType().InvokeMember(property, BindingFlags.GetProperty, null, target, null);

        public static void PutRef(object target, string property, object value) =>
            target.GetType().InvokeMember(property, BindingFlags.PutRefDispProperty, null, target, new[] { value });
    }
}
