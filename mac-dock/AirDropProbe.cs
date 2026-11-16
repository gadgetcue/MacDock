using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MacDock
{
    // "MacDock.exe --airdrop-probe": sends mDNS PTR queries for _airdrop._tcp and friends on every IPv4 interface,
    // listens ~8 s and logs every responder to %APPDATA%\MacDock\airdrop-probe.log.
    static class AirDropProbe
    {
        static byte[] Query(string name)
        {
            var m = new List<byte> { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
            foreach (string l in name.Split('.')) { m.Add((byte)l.Length); m.AddRange(Encoding.ASCII.GetBytes(l)); }
            m.AddRange(new byte[] { 0, 0, 12, 0, 1 });
            return m.ToArray();
        }
        public static int Run()
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            var log = new StreamWriter(Path.Combine(AppPaths.DataDir, "airdrop-probe.log")) { AutoFlush = true };
            string[] svcs = { "_airdrop._tcp.local", "_companion-link._tcp.local", "_localsend._tcp.local", "_services._dns-sd._udp.local" };
            var mc = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
            var seen = new HashSet<string>();
            foreach (var ip in System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()).AddressList)
            {
                if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                try
                {
                    var u = new UdpClient(new IPEndPoint(ip, 0));
                    u.Client.ReceiveTimeout = 500;
                    u.JoinMulticastGroup(IPAddress.Parse("224.0.0.251"), ip);
                    foreach (string s in svcs) u.Send(Query(s), Query(s).Length, mc);
                    log.WriteLine("queried from " + ip);
                    var end = DateTime.UtcNow.AddSeconds(8);
                    while (DateTime.UtcNow < end)
                    {
                        try
                        {
                            IPEndPoint from = null; byte[] b = u.Receive(ref from);
                            string txt = Encoding.ASCII.GetString(b).Replace('\0', '.');
                            string key = from.Address.ToString() + txt.Length;
                            if (seen.Add(key)) log.WriteLine("reply from " + from.Address + ": " + System.Text.RegularExpressions.Regex.Replace(txt, @"[^\x20-\x7e]", ".").Substring(0, Math.Min(300, txt.Length)));
                        }
                        catch (SocketException) { }
                    }
                    u.Close();
                }
                catch (Exception ex) { log.WriteLine("err " + ip + " " + ex.Message); }
            }
            log.WriteLine("done, responders: " + seen.Count);
            return 0;
        }
    }
}
