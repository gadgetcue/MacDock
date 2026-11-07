using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace MacDock
{
    // "MacDock.exe --ls-selftest [probe <ip> <port>]": loopback test of the LocalSend v2 implementation.
    static class LocalSendTest
    {
        public static int Run(string[] args)
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            Console.SetOut(new StreamWriter(Path.Combine(AppPaths.DataDir, "ls-test.log")) { AutoFlush = true });
            string dir = Path.Combine(AppPaths.DataDir, "ls-test");
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            Directory.CreateDirectory(dir);

            int pi = Array.FindIndex(args, x => x == "probe");
            if (pi >= 0)
            {
                var probe = new LocalSendNode(53320, "probe", Path.Combine(dir, "probe"), Path.Combine(dir, "p.pfx"));
                probe.Start();
                bool ok = probe.RegisterWith(args[pi + 1], int.Parse(args[pi + 2]), "https");
                foreach (LsPeer p in probe.Peers()) Console.WriteLine("peer: " + p.Alias + " " + p.Ip + ":" + p.Port + " " + p.Protocol + " " + p.Model + " " + p.Type);
                Console.WriteLine("register: " + ok);
                if (args.Length > pi + 3)
                {
                    LsPeer target = probe.Peers().First();
                    Console.WriteLine(probe.SendFiles(target, new[] { args[pi + 3] }, s => Console.WriteLine(s)));
                }
                return ok ? 0 : 1;
            }

            var a = new LocalSendNode(53318, "node-A", Path.Combine(dir, "a"), Path.Combine(dir, "a.pfx"));
            var b = new LocalSendNode(53319, "node-B", Path.Combine(dir, "b"), Path.Combine(dir, "b.pfx"));
            string got = null;
            b.Confirm = (who, n, sum) => { Console.WriteLine("B asked to accept " + n + " file(s) from " + who); return true; };
            var done = new ManualResetEvent(false);
            b.Received = (who, paths) => { got = paths[0]; done.Set(); };
            a.Start(); b.Start();

            bool reg = a.RegisterWith("127.0.0.1", 53319, "https");
            Console.WriteLine("A discovered B: " + reg + " peers(A)=" + a.Peers().Count + " peers(B)=" + b.Peers().Count);
            if (!reg || b.Peers().Count != 1) return 1;

            string src = Path.Combine(dir, "payload.bin");
            var data = new byte[5 * 1024 * 1024 + 123];
            new Random(7).NextBytes(data);
            File.WriteAllBytes(src, data);
            string result = a.SendFiles(a.Peers()[0], new[] { src }, null);
            Console.WriteLine(result);
            bool arrived = done.WaitOne(5000);
            bool same = arrived && File.ReadAllBytes(got).SequenceEqual(data);
            Console.WriteLine("received: " + arrived + " identical: " + same + " -> " + got);

            // Decline path
            b.Confirm = (who, n, sum) => false;
            Console.WriteLine(a.SendFiles(a.Peers()[0], new[] { src }, null));

            a.Stop(); b.Stop();
            return same ? 0 : 1;
        }
    }
}
