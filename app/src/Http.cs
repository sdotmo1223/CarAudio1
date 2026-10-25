using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace DreTunes
{
    public sealed class ApiException : Exception
    {
        public readonly bool Down;      // true when nothing is listening at all
        public ApiException(string message, bool down) : base(message) { Down = down; }
    }

    /// <summary>
    /// The two local HTTP servers this app is allowed to talk to, and nothing else.
    ///
    ///   127.0.0.1:8585  the bridge: the DSP, the tune library on disk, the phone page.
    ///   127.0.0.1:8586  rta.exe: the microphone and the live spectrum.
    ///
    /// Both are plain JSON over HTTP. The proxy is explicitly disabled: on a machine with a
    /// configured proxy, HttpWebRequest would otherwise try the proxy for 127.0.0.1 and add a
    /// second of latency (or fail) to every poll - and this app has to work with the network off.
    ///
    /// Errors are classified once, here, so no surface can accidentally render "not connected"
    /// for a bridge that is simply not running.
    /// </summary>
    public sealed class Api
    {
        public string BridgeBase = "http://127.0.0.1:8585";
        public string RtaBase = "http://127.0.0.1:8586";

        static Api()
        {
            ServicePointManager.DefaultConnectionLimit = 12;
            ServicePointManager.Expect100Continue = false;
        }

        public Dictionary<string, object> Get(string path, int timeoutMs)
        {
            return Send(BridgeBase + path, null, timeoutMs);
        }

        public Dictionary<string, object> Post(string path, Dictionary<string, object> body, int timeoutMs)
        {
            return Send(BridgeBase + path, body, timeoutMs);
        }

        public Dictionary<string, object> GetRta(string path, int timeoutMs)
        {
            return Send(RtaBase + path, null, timeoutMs);
        }

        private Dictionary<string, object> Send(string url, Dictionary<string, object> body, int timeoutMs)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Proxy = null;
            req.Method = body == null ? "GET" : "POST";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.KeepAlive = true;
            req.UserAgent = "DreTunes-desktop";

            try
            {
                if (body != null)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(Json.Write(body));
                    req.ContentType = "application/json";
                    req.ContentLength = bytes.Length;
                    using (Stream s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                }

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (Stream s = resp.GetResponseStream())
                using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                {
                    string text = r.ReadToEnd();
                    if (text.Length == 0) throw new ApiException("empty reply from " + Short(url), false);
                    object o;
                    try { o = Json.Parse(text); }
                    catch (Exception) { throw new ApiException("reply from " + Short(url) + " was not JSON", false); }
                    Dictionary<string, object> d = Json.Obj(o);
                    if (d == null) throw new ApiException("reply from " + Short(url) + " was not an object", false);
                    return d;
                }
            }
            catch (WebException we)
            {
                HttpWebResponse r = we.Response as HttpWebResponse;
                if (r != null)
                {
                    // The bridge answers 200 with {"ok":false,"error":...} for application
                    // failures, so a real HTTP error means something is wrong below that.
                    string detail;
                    using (Stream s = r.GetResponseStream())
                    using (StreamReader rd = new StreamReader(s, Encoding.UTF8))
                        detail = rd.ReadToEnd();
                    throw new ApiException(Short(url) + " -> HTTP " + (int)r.StatusCode +
                                           (detail.Length > 0 ? ": " + Trim(detail, 160) : ""), false);
                }
                if (we.Status == WebExceptionStatus.Timeout)
                    throw new ApiException("no answer from " + Origin(url) + " within " +
                                           (timeoutMs / 1000.0).ToString("0.0") + " s", false);
                throw new ApiException(Origin(url) + " is not answering (" + we.Status + ")", true);
            }
        }

        /// <summary>Cheap liveness probe used before deciding to start a process.</summary>
        public bool Alive(string url, int timeoutMs)
        {
            try
            {
                Dictionary<string, object> d = Send(url, null, timeoutMs);
                return Json.B(d, "ok", true) || d != null;
            }
            catch
            {
                return false;
            }
        }

        private static string Origin(string url)
        {
            int i = url.IndexOf('/', 8);
            return i > 0 ? url.Substring(0, i) : url;
        }

        private static string Short(string url)
        {
            int i = url.IndexOf('/', 8);
            return i > 0 ? url.Substring(i) : url;
        }

        private static string Trim(string s, int n)
        {
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= n ? s : s.Substring(0, n) + "\u2026";
        }
    }
}
