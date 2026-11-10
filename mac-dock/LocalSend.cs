using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace MacDock
{
    sealed class LsPeer
    {
        public string Alias, Ip, Protocol = "https", Fingerprint, Model, Type;
        public int Port = 53317;
        public DateTime Seen = DateTime.UtcNow;
    }

    // Native implementation of the LocalSend v2 protocol (https://github.com/localsend/localsend/blob/main/documents/protocol.md):
    // UDP multicast + subnet-scan discovery, an HTTPS server for receiving, and an HTTPS client for sending.
    sealed class LocalSendNode
    {
        public const string MulticastGroup = "224.0.0.167";
        const string Api = "/api/localsend/v2/";

        public readonly int Port;
        public readonly string Alias;
        public string SaveDir;
        public volatile bool ReceiveEnabled = true;
        public string Fingerprint;
        public Func<string, int, string, bool> Confirm;    // (sender alias, file count, summary) -> accept?
        public Action<string, string[]> Received;           // (sender alias, saved paths)
        public Action<string> Log = s => { };

        readonly Dictionary<string, LsPeer> peers = new Dictionary<string, LsPeer>();
        readonly object gate = new object();
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        X509Certificate2 cert;
        static X509Certificate2 ClientCert;
        TcpListener listener;
        UdpClient udp;
        volatile bool stopped;

        class Session { public string Sender; public Dictionary<string, SFile> Files = new Dictionary<string, SFile>(); public List<string> Saved = new List<string>(); public int Done; }
        class SFile { public string Name, Token; public long Size; }
        readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();

        public LocalSendNode(int port, string alias, string saveDir, string certPath)
        {
            Port = port; Alias = alias; SaveDir = saveDir;
            cert = LoadCert(certPath); ClientCert = cert;
            Fingerprint = Hex(SHA256.Create().ComputeHash(cert.RawData));
        }

        // ---------- certificate ----------
        static X509Certificate2 LoadCert(string path)
        {
            const X509KeyStorageFlags fl = X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet;
            try { if (File.Exists(path)) return new X509Certificate2(path, "", fl); } catch { }
            using (RSA rsa = RSA.Create(2048))
            {
                var req = new CertificateRequest("CN=LocalSend User, O=LocalSend, C=US", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                X509Certificate2 self = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
                byte[] pfx = self.Export(X509ContentType.Pfx, "");
                try { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, pfx); } catch { }
                return new X509Certificate2(pfx, "", fl);
            }
        }

        static string Hex(byte[] b) { return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant(); }

        // ---------- info ----------
        Dictionary<string, object> Info(bool announce, bool withAnnounce)
        {
            var d = new Dictionary<string, object>
            {
                { "alias", Alias }, { "version", "2.0" }, { "deviceModel", "Windows" }, { "deviceType", "desktop" },
                { "fingerprint", Fingerprint }, { "port", Port }, { "protocol", "https" }, { "download", false }
            };
            if (withAnnounce) d["announce"] = announce;
            return d;
        }

        public List<LsPeer> Peers()
        {
            lock (gate) return peers.Values.Where(p => (DateTime.UtcNow - p.Seen).TotalMinutes < 10).OrderBy(p => p.Alias).ToList();
        }

        void AddPeer(Dictionary<string, object> d, string ip)
        {
            string fp = Str(d, "fingerprint");
            if (string.IsNullOrEmpty(fp) || fp == Fingerprint) return;
            var p = new LsPeer { Alias = Str(d, "alias") ?? ip, Ip = ip, Fingerprint = fp, Model = Str(d, "deviceModel"), Type = Str(d, "deviceType"), Protocol = Str(d, "protocol") ?? "https" };
            object port; if (d.TryGetValue("port", out port) && port != null) { try { p.Port = Convert.ToInt32(port); } catch { } }
            lock (gate) peers[fp] = p;
        }

        static string Str(Dictionary<string, object> d, string k) { object o; return d != null && d.TryGetValue(k, out o) && o != null ? o.ToString() : null; }

        // ---------- lifecycle ----------
        public void Start()
        {
            listener = new TcpListener(IPAddress.Any, Port);
            listener.Start();
            new Thread(AcceptLoop) { IsBackground = true, Name = "LocalSend server" }.Start();
            try { StartUdp(); } catch (Exception ex) { Log("udp: " + ex.Message); }
        }

        public void Stop()
        {
            stopped = true;
            try { listener.Stop(); } catch { }
            try { udp.Close(); } catch { }
        }

        // ---------- discovery ----------
        void StartUdp()
        {
            udp = new UdpClient(AddressFamily.InterNetwork);
            udp.ExclusiveAddressUse = false;
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
            IPAddress group = IPAddress.Parse(MulticastGroup);
            foreach (IPAddress ip in LocalIPv4())
                try { udp.JoinMulticastGroup(group, ip); } catch { }
            new Thread(() =>
            {
                while (!stopped)
                {
                    try
                    {
                        var from = new IPEndPoint(IPAddress.Any, 0);
                        byte[] data = udp.Receive(ref from);
                        var d = json.DeserializeObject(Encoding.UTF8.GetString(data)) as Dictionary<string, object>;
                        if (d == null) continue;
                        string fp = Str(d, "fingerprint");
                        if (fp == Fingerprint) continue;
                        AddPeer(d, from.Address.ToString());
                        object ann; bool isAnn = d.TryGetValue("announce", out ann) && ann is bool && (bool)ann;
                        if (isAnn)
                        {
                            LsPeer p; lock (gate) peers.TryGetValue(fp, out p);
                            if (p != null) ThreadPool.QueueUserWorkItem(_ => { try { RegisterWith(p.Ip, p.Port, p.Protocol); } catch { } });
                        }
                    }
                    catch { if (stopped) return; Thread.Sleep(200); }
                }
            }) { IsBackground = true, Name = "LocalSend udp" }.Start();
        }

        static List<IPAddress> LocalIPv4()
        {
            var l = new List<IPAddress>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation u in ni.GetIPProperties().UnicastAddresses)
                        if (u.Address.AddressFamily == AddressFamily.InterNetwork) l.Add(u.Address);
                }
            }
            catch { }
            return l;
        }

        // Multicast announce on every interface, plus a unicast scan of the local /24 subnets (the protocol's fallback).
        public void Discover(bool scan)
        {
            byte[] msg = Encoding.UTF8.GetBytes(json.Serialize(Info(true, true)));
            foreach (IPAddress ip in LocalIPv4())
            {
                try
                {
                    using (var c = new UdpClient(new IPEndPoint(ip, 0)))
                    {
                        c.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
                        c.Send(msg, msg.Length, new IPEndPoint(IPAddress.Parse(MulticastGroup), 53317));
                        if (Port != 53317) c.Send(msg, msg.Length, new IPEndPoint(IPAddress.Parse(MulticastGroup), Port));
                    }
                }
                catch { }
            }
            if (!scan) return;
            var own = LocalIPv4();
            var targets = new List<string>();
            foreach (IPAddress ip in own)
            {
                byte[] b = ip.GetAddressBytes();
                for (int i = 1; i < 255; i++)
                {
                    if (i == b[3]) continue;
                    targets.Add(b[0] + "." + b[1] + "." + b[2] + "." + i);
                }
            }
            Parallel.ForEach(targets.Distinct(), new ParallelOptions { MaxDegreeOfParallelism = 64 }, t =>
            {
                try { RegisterWith(t, 53317, "https", 700); } catch { }
            });
        }

        // POST our info to a peer's /register; the reply is the peer's info.
        public bool RegisterWith(string ip, int port, string protocol, int timeoutMs = 3000)
        {
            string url = protocol + "://" + ip + ":" + port + Api + "register";
            string reply = Post(url, json.Serialize(Info(false, false)), timeoutMs);
            var d = json.DeserializeObject(reply) as Dictionary<string, object>;
            if (d == null) return false;
            if (!d.ContainsKey("port")) d["port"] = port;
            if (!d.ContainsKey("protocol")) d["protocol"] = protocol;
            AddPeer(d, ip);
            return true;
        }

        // ---------- HTTP client ----------
        sealed class LsHttpException : Exception
        {
            public int Status;
            public LsHttpException(int status, string body) : base("HTTP " + status + (string.IsNullOrEmpty(body) ? "" : " " + body)) { Status = status; }
        }

        // Minimal HTTP/1.1 client over SslStream. Newer LocalSend peers are TLS 1.3-only and require a client certificate (mutual TLS);
        // HttpWebRequest never presents it, so we hand SslStream our cert through a LocalCertificateSelectionCallback.
        // Returns the response body; throws LsHttpException on a non-2xx status.
        static string HttpCall(string url, string method, string contentType, Stream body, long len, int timeoutMs)
        {
            var u = new Uri(url);
            using (var tc = new TcpClient())
            {
                var ar = tc.BeginConnect(u.Host, u.Port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(Math.Min(timeoutMs, 10000))) throw new TimeoutException("connect timed out");
                tc.EndConnect(ar);
                tc.ReceiveTimeout = tc.SendTimeout = timeoutMs;
                Stream st = tc.GetStream();
                if (u.Scheme == "https")
                {
                    var ssl = new SslStream(st, false, (s, c, ch, e) => true, // peers use self-signed certs
                        (s, host, local, remote, issuers) => ClientCert);
                    var certs = ClientCert != null ? new X509CertificateCollection(new X509Certificate[] { ClientCert }) : null;
                    try { ssl.AuthenticateAsClient(u.Host, certs, (SslProtocols)(3072 | 12288), false); }
                    catch (ArgumentException) { ssl.AuthenticateAsClient(u.Host, certs, SslProtocols.Tls12, false); }
                    st = ssl;
                }
                string head = method + " " + u.PathAndQuery + " HTTP/1.1\r\nHost: " + u.Host + ":" + u.Port + "\r\nConnection: close\r\n"
                    + (contentType != null ? "Content-Type: " + contentType + "\r\n" : "") + "Content-Length: " + len + "\r\n\r\n";
                byte[] hb = Encoding.ASCII.GetBytes(head);
                st.Write(hb, 0, hb.Length);
                if (body != null) body.CopyTo(st, 81920);
                st.Flush();
                var ms = new MemoryStream();
                var buf = new byte[16384];
                try { int n; while ((n = st.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n); }
                catch (IOException) { if (ms.Length == 0) throw; } // peer closed without close_notify after replying
                byte[] all = ms.ToArray();
                int he = -1;
                for (int i = 0; i + 3 < all.Length; i++) if (all[i] == 13 && all[i + 1] == 10 && all[i + 2] == 13 && all[i + 3] == 10) { he = i; break; }
                if (he < 0) throw new IOException("malformed HTTP response");
                string[] lines = Encoding.ASCII.GetString(all, 0, he).Split(new[] { "\r\n" }, StringSplitOptions.None);
                string[] sl = lines[0].Split(' ');
                int status = sl.Length > 1 ? int.Parse(sl[1]) : 0;
                bool chunked = lines.Any(l => l.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase) && l.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0);
                byte[] payload;
                if (!chunked) { payload = new byte[all.Length - he - 4]; Array.Copy(all, he + 4, payload, 0, payload.Length); }
                else
                {
                    var o = new MemoryStream(); int p = he + 4;
                    while (p < all.Length)
                    {
                        int e = p; while (e + 1 < all.Length && !(all[e] == 13 && all[e + 1] == 10)) e++;
                        string hex = Encoding.ASCII.GetString(all, p, e - p); int sc = hex.IndexOf(';'); if (sc >= 0) hex = hex.Substring(0, sc);
                        int size = Convert.ToInt32(hex.Trim(), 16);
                        if (size == 0) break;
                        o.Write(all, e + 2, Math.Min(size, all.Length - e - 2)); p = e + 2 + size + 2;
                    }
                    payload = o.ToArray();
                }
                string text = Encoding.UTF8.GetString(payload);
                if (status < 200 || status > 299) throw new LsHttpException(status, text);
                return text;
            }
        }

        static string Post(string url, string body, int timeoutMs)
        {
            byte[] b = Encoding.UTF8.GetBytes(body);
            return HttpCall(url, "POST", "application/json", new MemoryStream(b), b.Length, timeoutMs);
        }

        // ---------- sending ----------
        // Returns a human readable result; throws nothing.
        public string SendFiles(LsPeer peer, string[] paths, Action<string> progress)
        {
            try
            {
                string baseUrl = peer.Protocol + "://" + peer.Ip + ":" + peer.Port + Api;
                var files = new Dictionary<string, object>();
                var byId = new Dictionary<string, string>();
                int n = 0;
                foreach (string p in paths)
                {
                    var fi = new FileInfo(p);
                    string id = "f" + (n++);
                    byId[id] = p;
                    files[id] = new Dictionary<string, object>
                    {
                        { "id", id }, { "fileName", fi.Name }, { "size", fi.Length }, { "fileType", Mime(fi.Extension) }
                    };
                }
                var body = new Dictionary<string, object> { { "info", Info(false, false) }, { "files", files } };
                string reply;
                Log("send: prepare-upload -> " + baseUrl + " files=" + byId.Count);
                try { reply = Post(baseUrl + "prepare-upload", json.Serialize(body), 120000); }
                catch (Exception ex)
                {
                    Log("send: prepare-upload failed: " + ex.GetType().Name + " " + ex.Message + (ex.InnerException != null ? " / " + ex.InnerException.Message : ""));
                    var he = ex as LsHttpException;
                    if (he != null && he.Status == 403) return peer.Alias + " declined the transfer.";
                    if (he != null && he.Status == 409) return peer.Alias + " is busy with another transfer.";
                    throw;
                }
                if (string.IsNullOrWhiteSpace(reply)) return "Nothing to send (" + peer.Alias + " has no use for these files).";
                var resp = json.DeserializeObject(reply) as Dictionary<string, object>;
                string session = Str(resp, "sessionId");
                var tokens = resp["files"] as Dictionary<string, object>;
                int sent = 0;
                foreach (KeyValuePair<string, string> kv in byId)
                {
                    object tok; if (!tokens.TryGetValue(kv.Key, out tok)) continue; // receiver skipped this file
                    var fi = new FileInfo(kv.Value);
                    if (progress != null) progress("Sending " + fi.Name + " (" + (sent + 1) + "/" + tokens.Count + ")");
                    string url = baseUrl + "upload?sessionId=" + Uri.EscapeDataString(session) + "&fileId=" + Uri.EscapeDataString(kv.Key) + "&token=" + Uri.EscapeDataString(tok.ToString());
                    using (FileStream f = new FileStream(kv.Value, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        HttpCall(url, "POST", Mime(fi.Extension), f, fi.Length, 600000);
                    Log("send: uploaded " + fi.Name);
                    sent++;
                }
                return "Sent " + sent + (sent == 1 ? " file" : " files") + " to " + peer.Alias + ".";
            }
            catch (Exception ex)
            {
                string detail = ex.Message;
                if (ex.InnerException != null) detail += " (" + ex.InnerException.Message + ")";
                Log("send: FAILED " + ex);
                return "Could not send to " + peer.Alias + ": " + detail;
            }
        }

        static string Mime(string ext)
        {
            try
            {
                var k = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(ext ?? "");
                object o = k != null ? k.GetValue("Content Type") : null;
                if (o != null) return o.ToString();
            }
            catch { }
            return "application/octet-stream";
        }

        // ---------- HTTPS server ----------
        void AcceptLoop()
        {
            while (!stopped)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); }
                catch { if (stopped) return; continue; }
                ThreadPool.QueueUserWorkItem(_ => { try { Serve(c); } catch (Exception ex) { Log("serve: " + ex.Message); } finally { try { c.Close(); } catch { } } });
            }
        }

        sealed class Req
        {
            public string Method, Path; public Dictionary<string, string> Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public byte[] Lead; public Stream Stream; public string Remote;
        }

        void Serve(TcpClient c)
        {
            c.ReceiveTimeout = c.SendTimeout = 600000;
            string remote = ((IPEndPoint)c.Client.RemoteEndPoint).Address.ToString();
            if (remote.StartsWith("::ffff:")) remote = remote.Substring(7);
            using (var ssl = new SslStream(c.GetStream(), false))
            {
                ssl.AuthenticateAsServer(cert, false, SslProtocols.Tls12, false);
                Req rq = ReadRequest(ssl);
                if (rq == null) return;
                rq.Remote = remote;
                int code = 404; string body = "{}";
                try { Handle(rq, out code, out body); }
                catch (Exception ex) { Log("handle: " + ex.Message); code = 500; body = "{\"message\":\"error\"}"; }
                byte[] payload = Encoding.UTF8.GetBytes(body ?? "");
                string head = "HTTP/1.1 " + code + " " + (code == 200 ? "OK" : code == 204 ? "No Content" : "Error") + "\r\nContent-Type: application/json\r\nContent-Length: " + (code == 204 ? 0 : payload.Length) + "\r\nConnection: close\r\n\r\n";
                byte[] hb = Encoding.ASCII.GetBytes(head);
                ssl.Write(hb, 0, hb.Length);
                if (code != 204) ssl.Write(payload, 0, payload.Length);
                ssl.Flush();
            }
        }

        static Req ReadRequest(Stream s)
        {
            var buf = new List<byte>();
            var one = new byte[4096];
            int idx = -1;
            while (idx < 0)
            {
                int n = s.Read(one, 0, one.Length);
                if (n <= 0) return null;
                for (int i = 0; i < n; i++) buf.Add(one[i]);
                idx = IndexOfHeaderEnd(buf);
                if (buf.Count > 65536 && idx < 0) return null;
            }
            string head = Encoding.ASCII.GetString(buf.GetRange(0, idx).ToArray());
            string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length < 2) return null;
            var rq = new Req { Method = first[0], Stream = s };
            string target = first[1];
            int q = target.IndexOf('?');
            rq.Path = q >= 0 ? target.Substring(0, q) : target;
            if (q >= 0)
                foreach (string kv in target.Substring(q + 1).Split('&'))
                {
                    int e = kv.IndexOf('=');
                    if (e > 0) rq.Query[Uri.UnescapeDataString(kv.Substring(0, e))] = Uri.UnescapeDataString(kv.Substring(e + 1).Replace('+', ' '));
                }
            for (int i = 1; i < lines.Length; i++)
            {
                int c = lines[i].IndexOf(':');
                if (c > 0) rq.Headers[lines[i].Substring(0, c).Trim()] = lines[i].Substring(c + 1).Trim();
            }
            rq.Lead = buf.GetRange(idx + 4, buf.Count - idx - 4).ToArray();
            return rq;
        }

        static int IndexOfHeaderEnd(List<byte> b)
        {
            for (int i = 0; i + 3 < b.Count; i++)
                if (b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10) return i;
            return -1;
        }

        // Body as a stream: bytes already read past the headers first, then the socket, limited to Content-Length (or chunked).
        Stream BodyStream(Req rq)
        {
            string te; if (rq.Headers.TryGetValue("Transfer-Encoding", out te) && te.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
                return new ChunkedStream(new PrefixStream(rq.Lead, rq.Stream));
            string cl; long len = 0; if (rq.Headers.TryGetValue("Content-Length", out cl)) long.TryParse(cl, out len);
            return new LimitStream(new PrefixStream(rq.Lead, rq.Stream), len);
        }

        string ReadText(Req rq)
        {
            using (Stream b = BodyStream(rq))
            using (var ms = new MemoryStream()) { b.CopyTo(ms); return Encoding.UTF8.GetString(ms.ToArray()); }
        }

        void Handle(Req rq, out int code, out string body)
        {
            code = 200; body = "{}";
            string path = rq.Path;
            if (path.StartsWith("/api/localsend/v1/")) { code = 404; return; }
            if (!path.StartsWith(Api)) { code = 404; return; }
            string ep = path.Substring(Api.Length).TrimEnd('/');

            if (ep == "info" && rq.Method == "GET") { body = json.Serialize(Info(false, false)); return; }

            if (ep == "register" && rq.Method == "POST")
            {
                var d = json.DeserializeObject(ReadText(rq)) as Dictionary<string, object>;
                if (d != null) AddPeer(d, rq.Remote);
                body = json.Serialize(Info(false, false));
                return;
            }

            if (ep == "prepare-upload" && rq.Method == "POST")
            {
                var d = json.DeserializeObject(ReadText(rq)) as Dictionary<string, object>;
                var info = d != null && d.ContainsKey("info") ? d["info"] as Dictionary<string, object> : null;
                var files = d != null && d.ContainsKey("files") ? d["files"] as Dictionary<string, object> : null;
                if (files == null || files.Count == 0) { code = 400; body = "{\"message\":\"Request body malformed\"}"; return; }
                if (info != null) AddPeer(info, rq.Remote);
                string sender = Str(info, "alias") ?? rq.Remote;

                var sess = new Session { Sender = sender };
                long total = 0;
                var summary = new StringBuilder();
                foreach (KeyValuePair<string, object> kv in files)
                {
                    var f = kv.Value as Dictionary<string, object>;
                    if (f == null) continue;
                    string name = SafeName(Str(f, "fileName") ?? kv.Key);
                    long size = 0; long.TryParse(Str(f, "size") ?? "0", out size);
                    total += size;
                    sess.Files[kv.Key] = new SFile { Name = name, Size = size, Token = Guid.NewGuid().ToString("N") };
                    if (sess.Files.Count <= 5) summary.AppendLine(" - " + name + "  (" + FormatSize(size) + ")");
                }
                if (sess.Files.Count > 5) summary.AppendLine(" ... and " + (sess.Files.Count - 5) + " more");
                bool ok = ReceiveEnabled && (Confirm == null || Confirm(sender, sess.Files.Count, summary.ToString() + "\r\nTotal " + FormatSize(total)));
                if (!ok) { code = 403; body = "{\"message\":\"Rejected\"}"; return; }

                string id = Guid.NewGuid().ToString("N");
                lock (gate) sessions[id] = sess;
                var tokens = sess.Files.ToDictionary(k => k.Key, k => (object)k.Value.Token);
                body = json.Serialize(new Dictionary<string, object> { { "sessionId", id }, { "files", tokens } });
                return;
            }

            if (ep == "upload" && rq.Method == "POST")
            {
                string sid, fid, tok;
                rq.Query.TryGetValue("sessionId", out sid); rq.Query.TryGetValue("fileId", out fid); rq.Query.TryGetValue("token", out tok);
                Session sess = null; SFile f = null;
                lock (gate) { if (sid != null) sessions.TryGetValue(sid, out sess); }
                if (sess == null || fid == null || !sess.Files.TryGetValue(fid, out f) || f.Token != tok) { code = 403; body = "{\"message\":\"Invalid token or IP address\"}"; return; }
                Directory.CreateDirectory(SaveDir);
                string dest = UniquePath(Path.Combine(SaveDir, f.Name));
                string part = dest + ".part";
                using (Stream b = BodyStream(rq))
                using (FileStream fs = File.Create(part)) b.CopyTo(fs, 81920);
                File.Move(part, dest);
                bool all;
                lock (gate) { sess.Saved.Add(dest); sess.Done++; all = sess.Done >= sess.Files.Count; if (all) sessions.Remove(sid); }
                if (all && Received != null) { string[] saved = sess.Saved.ToArray(); string who = sess.Sender; ThreadPool.QueueUserWorkItem(_ => { try { Received(who, saved); } catch { } }); }
                return;
            }

            if (ep == "cancel" && rq.Method == "POST")
            {
                string sid; if (rq.Query.TryGetValue("sessionId", out sid)) lock (gate) sessions.Remove(sid);
                return;
            }

            code = 404;
        }

        static string SafeName(string n)
        {
            n = n.Replace('\\', '/');
            n = n.Substring(n.LastIndexOf('/') + 1);
            foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            n = n.Trim().Trim('.');
            return n.Length == 0 ? "file" : n;
        }

        static string UniquePath(string p)
        {
            if (!File.Exists(p)) return p;
            string dir = Path.GetDirectoryName(p), name = Path.GetFileNameWithoutExtension(p), ext = Path.GetExtension(p);
            for (int i = 1; ; i++) { string c = Path.Combine(dir, name + " (" + i + ")" + ext); if (!File.Exists(c)) return c; }
        }

        public static string FormatSize(long b)
        {
            if (b < 1024) return b + " B";
            if (b < 1024 * 1024) return (b / 1024.0).ToString("0.#") + " KB";
            if (b < 1024L * 1024 * 1024) return (b / 1048576.0).ToString("0.#") + " MB";
            return (b / 1073741824.0).ToString("0.##") + " GB";
        }

        // ---------- stream helpers ----------
        sealed class PrefixStream : Stream
        {
            readonly byte[] lead; int pos; readonly Stream inner;
            public PrefixStream(byte[] lead, Stream inner) { this.lead = lead; this.inner = inner; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (pos < lead.Length) { int n = Math.Min(count, lead.Length - pos); Buffer.BlockCopy(lead, pos, buffer, offset, n); pos += n; return n; }
                return inner.Read(buffer, offset, count);
            }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long o, SeekOrigin s) { throw new NotSupportedException(); }
            public override void SetLength(long v) { throw new NotSupportedException(); }
            public override void Write(byte[] b, int o, int c) { throw new NotSupportedException(); }
        }

        sealed class LimitStream : Stream
        {
            readonly Stream inner; long left;
            public LimitStream(Stream inner, long len) { this.inner = inner; left = len; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (left <= 0) return 0;
                int n = inner.Read(buffer, offset, (int)Math.Min(count, left));
                if (n > 0) left -= n; else left = 0;
                return n;
            }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long o, SeekOrigin s) { throw new NotSupportedException(); }
            public override void SetLength(long v) { throw new NotSupportedException(); }
            public override void Write(byte[] b, int o, int c) { throw new NotSupportedException(); }
        }

        sealed class ChunkedStream : Stream
        {
            readonly Stream inner; long left; bool done;
            public ChunkedStream(Stream inner) { this.inner = inner; }
            string Line()
            {
                var sb = new StringBuilder(); int b;
                while ((b = inner.ReadByte()) >= 0) { if (b == 10) break; if (b != 13) sb.Append((char)b); }
                return sb.ToString();
            }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (done) return 0;
                if (left == 0)
                {
                    string l = Line();
                    int semi = l.IndexOf(';'); if (semi >= 0) l = l.Substring(0, semi);
                    left = l.Trim().Length == 0 ? 0 : Convert.ToInt64(l.Trim(), 16);
                    if (left == 0) { done = true; return 0; }
                }
                int n = inner.Read(buffer, offset, (int)Math.Min(count, left));
                left -= n;
                if (left == 0) Line();
                return n;
            }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long o, SeekOrigin s) { throw new NotSupportedException(); }
            public override void SetLength(long v) { throw new NotSupportedException(); }
            public override void Write(byte[] b, int o, int c) { throw new NotSupportedException(); }
        }
    }

    // The dock-wide LocalSend node plus the UI glue used by the Control Center tile.
    static class LocalSend
    {
        static LocalSendNode node;
        static readonly object gate = new object();
        public static string Status = "";   // last transfer result, shown in the panel

        public static LocalSendNode Node
        {
            get
            {
                lock (gate)
                {
                    if (node != null) return node;
                    try
                    {
                        string dl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "LocalSend");
                        try { if (File.Exists(FolderFile)) { string saved = File.ReadAllText(FolderFile).Trim(); if (saved.Length > 0) dl = saved; } } catch { }
                        var n = new LocalSendNode(53317, Environment.MachineName, dl, Path.Combine(AppPaths.DataDir, "localsend.pfx"));
                        n.Confirm = (who, count, summary) =>
                            AutoAccept ? true : MessageBox.Show(who + " wants to send you " + count + (count == 1 ? " file" : " files") + ":\r\n\r\n" + summary + "\r\n\r\nAccept?\r\nSaved to: " + n.SaveDir,
                                "LocalSend", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1, (MessageBoxOptions)0x40000) == DialogResult.Yes;
                        n.Received = (who, paths) =>
                        {
                            Status = "Received " + paths.Length + (paths.Length == 1 ? " file" : " files") + " from " + who;
                            try { Process.Start("explorer.exe", "/select,\"" + paths[0] + "\""); } catch { }
                        };
                        n.Log = s => { try { Program.Log("localsend " + s); } catch { } };
                        n.Start();
                        node = n;
                    }
                    catch (Exception ex) { Status = "Port 53317 busy (is the LocalSend app running?)"; Program.Log("localsend start: " + ex.Message); }
                    return node;
                }
            }
        }

        static string AutoFile { get { return Path.Combine(AppPaths.DataDir, "localsend-auto.txt"); } }
        // Autosave: accept incoming files without a prompt (saved to the chosen folder). Persisted.
        public static bool AutoAccept
        {
            get { try { return File.Exists(AutoFile) && File.ReadAllText(AutoFile).Trim() == "1"; } catch { return false; } }
            set { try { File.WriteAllText(AutoFile, value ? "1" : "0"); } catch { } }
        }

        static string FolderFile { get { return Path.Combine(AppPaths.DataDir, "localsend-folder.txt"); } }

        // Lets the user pick where received files go (remembered in %APPDATA%\MacDock\localsend-folder.txt).
        public static void ChooseFolder()
        {
            var t = new Thread(() =>
            {
                LocalSendNode n = Node; if (n == null) return;
                using (var dlg = new FolderBrowserDialog { Description = "Choose where LocalSend saves received files", SelectedPath = n.SaveDir, ShowNewFolderButton = true })
                {
                    var owner = new Form { TopMost = true, ShowInTaskbar = false, Opacity = 0, StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-2000, -2000), Size = new System.Drawing.Size(1, 1) };
                    owner.Show();
                    DialogResult r = dlg.ShowDialog(owner);
                    owner.Close();
                    if (r != DialogResult.OK || string.IsNullOrEmpty(dlg.SelectedPath)) return;
                    n.SaveDir = dlg.SelectedPath;
                    try { File.WriteAllText(FolderFile, dlg.SelectedPath); } catch { }
                    Status = "Saving to " + dlg.SelectedPath;
                }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
        }

        // ---- Explorer "Share with MacDock LocalSend" verb: files queued by `MacDock.exe --send <path>` wait here until a device is picked.
        public static readonly List<string> Pending = new List<string>();
        const string VerbKey = @"Software\Classes\*\shell\MacDockLocalSend";
        static string SpoolDir { get { return Path.Combine(AppPaths.DataDir, "ls-send"); } }

        // Self-heal: always registered at startup (missing key or moved exe path gets rewritten).
        public static void EnsureVerb()
        {
            try
            {
                string want = "\"" + Path.GetFullPath(Application.ExecutablePath) + "\" --send \"%1\"";
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(VerbKey + @"mmand"))
                    if (k != null && string.Equals(k.GetValue("") as string, want, StringComparison.Ordinal)) return;
            }
            catch { }
            SetVerb(true);
        }

        public static bool VerbInstalled
        {
            get { try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(VerbKey)) return k != null; } catch { return false; } }
        }

        public static void SetVerb(bool on)
        {
            try
            {
                if (!on) { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(VerbKey, false); return; }
                string exe = Path.GetFullPath(Application.ExecutablePath);
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(VerbKey))
                {
                    k.SetValue("", "Share with MacDock LocalSend");
                    k.SetValue("Icon", "\"" + exe + "\"");
                    using (var c = k.CreateSubKey("command")) c.SetValue("", "\"" + exe + "\" --send \"%1\"");
                }
            }
            catch (Exception ex) { Program.Log("localsend verb: " + ex.Message); }
        }

        // Client side of `--send`: queue the path, wake the running dock (or start one).
        public static void QueueSend(params string[] paths)
        {
            try
            {
                Directory.CreateDirectory(SpoolDir);
                foreach (string path in paths)
                    File.WriteAllText(Path.Combine(SpoolDir, Guid.NewGuid().ToString("N") + ".txt"), path);
            }
            catch { }
            try { EventWaitHandle.OpenExisting("MacDockSendEvent").Set(); }
            catch { try { Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--detached") { UseShellExecute = false }); } catch { } }
        }

        // Dock side: wait for the event, give Explorer ~700 ms to deliver the rest of a multi-selection, then open the panel.
        public static void WatchSends(Action<string> navigate, Control ui)
        {
            var evt = new EventWaitHandle(false, EventResetMode.AutoReset, "MacDockSendEvent");
            var t = new Thread(() =>
            {
                bool first = true;
                while (true)
                {
                    if (first) { first = false; Thread.Sleep(3000); if (!Directory.Exists(SpoolDir) || Directory.GetFiles(SpoolDir).Length == 0) continue; }
                    else { evt.WaitOne(); Thread.Sleep(700); }
                    try
                    {
                        if (!Directory.Exists(SpoolDir)) continue;
                        var got = new List<string>();
                        foreach (string f in Directory.GetFiles(SpoolDir, "*.txt"))
                        {
                            try { string p = File.ReadAllText(f).Trim(); File.Delete(f); if (File.Exists(p) && !got.Contains(p)) got.Add(p); } catch { }
                        }
                        if (got.Count == 0) continue;
                        lock (Pending) { foreach (string p in got) if (!Pending.Contains(p)) Pending.Add(p); }
                        Status = Pending.Count + (Pending.Count == 1 ? " file" : " files") + " ready to send";
                        ui.BeginInvoke(new Action(() => navigate("localsend")));
                    }
                    catch { }
                }
            }) { IsBackground = true };
            t.Start();
        }

        // Sends the queued files to the chosen device (no picker).
        public static void SendPending(LsPeer peer)
        {
            string[] files;
            lock (Pending) { files = Pending.ToArray(); Pending.Clear(); }
            if (files.Length == 0) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string result = Node.SendFiles(peer, files, s => Status = s);
                Status = result;
                MessageBox.Show(result, "LocalSend", MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1, (MessageBoxOptions)0x40000);
            });
        }

        // "Send to > MacDock LocalSend" shortcut (per-user; delete the .lnk to remove). Rewritten at startup if missing or stale.
        public static void EnsureSendTo()
        {
            try
            {
                string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\SendTo", "MacDock LocalSend.lnk");
                string exe = Path.GetFullPath(Application.ExecutablePath);
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                dynamic sh = Activator.CreateInstance(t);
                if (File.Exists(lnk))
                {
                    dynamic cur = sh.CreateShortcut(lnk);
                    if (string.Equals((string)cur.TargetPath, exe, StringComparison.OrdinalIgnoreCase) && (string)cur.Arguments == "--send") return;
                }
                dynamic s = sh.CreateShortcut(lnk);
                s.TargetPath = exe; s.Arguments = "--send"; s.IconLocation = exe + ",0"; s.Description = "Send with MacDock LocalSend";
                s.Save();
            }
            catch (Exception ex) { Program.Log("localsend sendto: " + ex.Message); }
        }

        public static void EnsureStarted()
        {
            ThreadPool.QueueUserWorkItem(_ => { var n = Node; if (n != null) n.Discover(false); });
        }

        // Pick files (STA dialog) and send them in the background; the result is shown in a message box.
        public static void PickAndSend(LsPeer peer)
        {
            var t = new Thread(() =>
            {
                string[] files;
                using (var dlg = new OpenFileDialog { Multiselect = true, Title = "Send to " + peer.Alias, CheckFileExists = true })
                {
                    var owner = new Form { TopMost = true, ShowInTaskbar = false, Opacity = 0, StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-2000, -2000), Size = new System.Drawing.Size(1, 1) };
                    owner.Show();
                    DialogResult r = dlg.ShowDialog(owner);
                    files = dlg.FileNames;
                    owner.Close();
                    if (r != DialogResult.OK || files.Length == 0) return;
                }
                string result = Node.SendFiles(peer, files, s => Status = s);
                Status = result;
                MessageBox.Show(result, "LocalSend", MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1, (MessageBoxOptions)0x40000);
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
        }
    }
}
