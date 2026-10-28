using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Kumo
{
    // Lightweight current-weather fetcher: approximate location from IP (geojs.io / ipwho.is) + Open-Meteo (no API key, no account).
    public sealed class WeatherService : IDisposable
    {
        public volatile bool Enabled = true;
        public bool HasData { get; private set; }
        public float TemperatureC { get; private set; }
        public int WeatherCode { get; private set; }
        public bool IsDay { get; private set; }
        public string City { get; private set; }

        private volatile bool _running = true;
        private readonly Thread _worker;
        private double _lat = double.NaN, _lon = double.NaN;
        private DateTime _locFetchedUtc = DateTime.MinValue;
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);

        public WeatherService()
        {
            City = "";
            IsDay = true;
            _worker = new Thread(WorkerLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "KumoWeather" };
            _worker.Start();
        }

        public void RefreshSoon()
        {
            try { _wake.Set(); } catch { }
        }

        private static string HttpGet(string url)
        {
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { }
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = 8000;
            req.ReadWriteTimeout = 8000;
            req.UserAgent = "Kumo/" + KumoBuildInfo.Version + " (+https://github.com/" + KumoBuildInfo.RepoSlug + ")";
            req.AutomaticDecompression = DecompressionMethods.GZip; // .NET 4 Deflate decoder rejects some servers' streams
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                return sr.ReadToEnd();
            }
        }

        private static bool TryReadNumber(string json, string key, out double val)
        {
            val = 0;
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"?(-?[0-9]+(?:\\.[0-9]+)?)\"?");
            return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out val);
        }

        private static string ReadString(string json, string key)
        {
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? Regex.Unescape(m.Groups[1].Value) : "";
        }

        private bool ResolveLocation()
        {
            string[] urls = new string[] { "https://ipwho.is/", "https://get.geojs.io/v1/ip/geo.json" };
            for (int i = 0; i < urls.Length; i++)
            {
                try
                {
                    string js = HttpGet(urls[i]);
                    double la, lo;
                    if (TryReadNumber(js, "latitude", out la) && TryReadNumber(js, "longitude", out lo) &&
                        Math.Abs(la) <= 90 && Math.Abs(lo) <= 180 && !(la == 0 && lo == 0))
                    {
                        _lat = la; _lon = lo;
                        City = ReadString(js, "city");
                        _locFetchedUtc = DateTime.UtcNow;
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        private bool FetchWeather()
        {
            string url = string.Format(CultureInfo.InvariantCulture,
                "https://api.open-meteo.com/v1/forecast?latitude={0:0.####}&longitude={1:0.####}&current=temperature_2m,weather_code,is_day&timezone=auto",
                _lat, _lon);
            string js = HttpGet(url);
            int ci = js.IndexOf("\"current\":{", StringComparison.Ordinal);
            if (ci < 0) return false;
            int end = js.IndexOf('}', ci);
            string cur = (end > ci) ? js.Substring(ci, end - ci + 1) : js.Substring(ci);
            double t, code, day;
            if (!TryReadNumber(cur, "temperature_2m", out t) || !TryReadNumber(cur, "weather_code", out code)) return false;
            if (!TryReadNumber(cur, "is_day", out day)) day = 1;
            TemperatureC = (float)t;
            WeatherCode = (int)code;
            IsDay = day >= 0.5;
            HasData = true;
            return true;
        }

        private void WorkerLoop()
        {
            Thread.Sleep(2500);
            while (_running)
            {
                int waitMs = 20 * 60 * 1000;
                if (Enabled)
                {
                    try
                    {
                        bool locOk = !double.IsNaN(_lat) && (DateTime.UtcNow - _locFetchedUtc).TotalHours < 6;
                        if (!locOk) locOk = ResolveLocation() || !double.IsNaN(_lat);
                        if (!locOk || !FetchWeather()) waitMs = 5 * 60 * 1000;
                    }
                    catch { waitMs = 5 * 60 * 1000; }
                }
                try { _wake.WaitOne(waitMs); } catch { Thread.Sleep(waitMs); }
            }
        }

        public void Dispose()
        {
            _running = false;
            RefreshSoon();
        }
    }
}
