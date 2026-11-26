using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace MacDock
{
    // Keeps every slow or blocking lookup the dock needs off the UI thread, so a frozen app, a sleeping drive or a slow
    // shell extension can never freeze the dock. A background thread takes a snapshot about once a second (open windows,
    // Recycle Bin state, which pinned targets exist, file stamps, dock.txt stamp); the UI only reads the latest snapshot.
    static class DockScan
    {
        internal sealed class Snap
        {
            public Dictionary<string, List<IntPtr>> Running;
            public bool TrashFull;
            public Dictionary<string, Kind?> Kinds; // null: target does not exist
            public Dictionary<string, long> Stamps;
            public DateTime ConfigStamp;
            public string Sig;
        }

        static volatile Snap latest;
        static int scanStart; static volatile bool scanning;
        public static int RunningMs { get { return scanning ? unchecked(Environment.TickCount - scanStart) : 0; } }
        static volatile string[] wanted = new string[0];
        static readonly AutoResetEvent wake = new AutoResetEvent(false);
        static readonly object gate = new object();
        static string configPath;
        static bool trashFull; static long trashAt;

        public static Snap Latest { get { return latest; } }
        public static void Kick() { wake.Set(); }

        // Targets the UI will ask about (pinned entries); running windows' exes are added by the scan itself.
        public static void Want(IEnumerable<string> targets)
        {
            var a = targets.Where(t => !string.IsNullOrEmpty(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var cur = wanted;
            if (a.Length != cur.Length || a.Except(cur, StringComparer.OrdinalIgnoreCase).Any()) { wanted = a; Kick(); }
        }

        // First snapshot, taken on the UI thread at startup before the background thread exists.
        public static Snap ScanNow(string cfg) { configPath = cfg; var s = Scan(); latest = s; return s; }

        public static void Start(Control ui, Action changed)
        {
            var t = new Thread(() =>
            {
                while (true)
                {
                    wake.WaitOne(1000);
                    try
                    {
                        if (ui.IsDisposed) return;
                        var sw = Stopwatch.StartNew();
                        scanStart = Environment.TickCount; scanning = true;
                        Snap old = latest, s; try { s = Scan(); } finally { scanning = false; }
                        latest = s;
                        if (sw.ElapsedMilliseconds >= 1000) Program.Log("SLOW DockScan (background, dock unaffected) " + sw.ElapsedMilliseconds + " ms");
                        if (old == null || old.Sig != s.Sig || old.TrashFull != s.TrashFull || old.ConfigStamp != s.ConfigStamp || !SameStamps(old, s))
                            if (ui.IsHandleCreated) ui.BeginInvoke(changed);
                    }
                    catch (Exception ex) { Program.Log(ex); }
                }
            });
            t.IsBackground = true; t.Name = "DockScan"; t.SetApartmentState(ApartmentState.STA); t.Start();
        }

        static bool SameStamps(Snap a, Snap b)
        {
            foreach (var kv in b.Stamps) { long v; if (a.Stamps.TryGetValue(kv.Key, out v) && v != kv.Value) return false; }
            return true;
        }

        static Snap Scan()
        {
            lock (gate)
            {
                var s = new Snap();
                s.Running = DockForm.EnumerateRunningWindows();
                long now = Environment.TickCount;
                if (trashAt == 0 || now - trashAt > 3000) { trashFull = RecycleBin.IsFull(); trashAt = now == 0 ? 1 : now; } // SHQueryRecycleBin walks every drive
                s.TrashFull = trashFull;
                s.Kinds = new Dictionary<string, Kind?>(StringComparer.OrdinalIgnoreCase);
                s.Stamps = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (string t in wanted)
                {
                    Kind k; s.Kinds[t] = DockForm.TryClassify(t, out k) ? (Kind?)k : null;
                    s.Stamps[t] = DockForm.FileStamp(t);
                }
                foreach (string t in s.Running.Keys) if (!s.Stamps.ContainsKey(t)) s.Stamps[t] = DockForm.FileStamp(t);
                try { s.ConfigStamp = configPath != null && File.Exists(configPath) ? File.GetLastWriteTime(configPath) : DateTime.MinValue; } catch { }
                var sb = new System.Text.StringBuilder();
                foreach (var kv in s.Running.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                { sb.Append(kv.Key).Append('='); foreach (IntPtr h in kv.Value) sb.Append(h.ToInt64()).Append(','); sb.Append('|'); }
                foreach (var kv in s.Kinds) sb.Append(kv.Key).Append(':').Append(kv.Value).Append('|');
                s.Sig = sb.ToString();
                return s;
            }
        }

        // A copy for one rebuild: the UI may hand these lists to items, so they never share the snapshot's own lists.
        public static Dictionary<string, List<IntPtr>> RunningCopy()
        {
            var r = new Dictionary<string, List<IntPtr>>(StringComparer.OrdinalIgnoreCase);
            Snap s = latest; if (s == null) return r;
            foreach (var kv in s.Running) r[kv.Key] = new List<IntPtr>(kv.Value);
            return r;
        }

        public static bool TrashFull() { Snap s = latest; return s != null ? s.TrashFull : trashFull; }

        // Unknown target (just pinned): classify it now, once, and ask the scanner to track it.
        public static bool Classify(string target, out Kind kind)
        {
            Snap s = latest; Kind? k;
            if (s != null && target != null && s.Kinds.TryGetValue(target, out k)) { kind = k ?? Kind.File; return k.HasValue; }
            Program.Log("DockScan: untracked target classified on UI " + target);
            return DockForm.TryClassify(target, out kind);
        }

        public static long Stamp(string target)
        {
            Snap s = latest; long v;
            if (s != null && target != null && s.Stamps.TryGetValue(target, out v)) return v;
            if (string.IsNullOrEmpty(target) || target.Length < 3 || target[1] != ':') return 0; // FileStamp is 0 for these anyway
            return DockForm.FileStamp(target);
        }
    }

    // Builds dock icons on one background (STA) thread. The UI waits at most a moment for a new icon, then shows a
    // placeholder and swaps the real icon in when it is ready; stale icons keep showing while they are refreshed.
    static class IconWorker
    {
        sealed class Job { public string Key; public Item It; public bool Disk, Force; public Bitmap Result; public readonly ManualResetEvent Done = new ManualResetEvent(false); }
        static readonly Queue<Job> queue = new Queue<Job>();
        static readonly Dictionary<string, Job> pending = new Dictionary<string, Job>(StringComparer.OrdinalIgnoreCase);
        static Thread thread;
        internal static Func<Item, Bitmap> Build;      // DockForm.BuildIcon
        internal static Func<Item, bool> WebFallback;  // true: do not keep this icon on disk
        internal static Action<string, Bitmap> Ready;  // runs on the UI thread

        // Returns the icon if it is ready within waitMs, else null (the result arrives later through Ready).
        public static Bitmap Request(Control ui, string key, Item it, bool disk, bool force, int waitMs)
        {
            Job j;
            lock (queue)
            {
                if (!pending.TryGetValue(key, out j))
                {
                    j = new Job { Key = key, It = it, Disk = disk, Force = force };
                    pending[key] = j; queue.Enqueue(j); Monitor.Pulse(queue);
                    if (thread == null) { thread = new Thread(() => Loop(ui)); thread.IsBackground = true; thread.Name = "IconWorker"; thread.SetApartmentState(ApartmentState.STA); thread.Start(); }
                }
            }
            if (waitMs > 0 && j.Done.WaitOne(waitMs)) { lock (queue) pending.Remove(key); return j.Result; }
            return null;
        }

        public static bool IsPending(string key) { lock (queue) return pending.ContainsKey(key); }

        static void Loop(Control ui)
        {
            while (true)
            {
                Job j;
                lock (queue) { while (queue.Count == 0) Monitor.Wait(queue); j = queue.Dequeue(); }
                var sw = Stopwatch.StartNew();
                Bitmap b = null;
                try
                {
                    if (j.Disk && !j.Force) b = IconDisk.TryLoad(j.Key, j.It.Target);
                    if (b == null) { b = Build(j.It); if (b != null && j.Disk && !WebFallback(j.It)) IconDisk.Save(j.Key, j.It.Target, b); }
                }
                catch (Exception ex) { Program.Log(ex); }
                if (sw.ElapsedMilliseconds >= 1000) Program.Log("SLOW icon build (background, dock unaffected) " + sw.ElapsedMilliseconds + " ms " + j.Key);
                j.Result = b; j.Done.Set();
                Job jj = j;
                try { if (!ui.IsDisposed && ui.IsHandleCreated) ui.BeginInvoke((Action)(() => { bool mine; lock (queue) { Job p; mine = pending.TryGetValue(jj.Key, out p) && p == jj; if (mine) pending.Remove(jj.Key); } if (mine && jj.Result != null) Ready(jj.Key, jj.Result); })); }
                catch { }
            }
        }
    }
}
