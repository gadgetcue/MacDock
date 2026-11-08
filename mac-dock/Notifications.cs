// Feeds for the top bar: Windows notifications and the current media session.
//
// Notifications are read from Windows' own notification database with the SQLite that ships in
// System32 (winsqlite3.dll). The WinRT listener API takes ~12 seconds per call on this kind of
// setup, which is far too slow for a live island; reading the database takes a few milliseconds.
// Media uses the WinRT media-session API through reflection, so the build needs no Windows SDK.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MacDock
{
    sealed class NotificationInfo
    {
        public long Id;
        public string App;
        public string AppId;
        public string Title;
        public string Body;
        public string Launch;          // the toast's launch string (a chat id, a protocol link, ...), if it has one
        public string ActivationType;  // the toast's activationType ("protocol" for a link), if it says
        public DateTime Time;
    }

    sealed class MediaInfo
    {
        public string Title;
        public string Artist;
        public bool Playing;
        public System.Drawing.Bitmap Art;   // SMTC thumbnail, or null
    }

    // Minimal reflection helpers for WinRT objects.
    static class Rt
    {
        static readonly Type Extensions = Type.GetType(
            "System.WindowsRuntimeSystemExtensions, System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");

        public static Type Winrt(string name)
        {
            return Type.GetType(name + ", Windows, ContentType=WindowsRuntime");
        }

        public static object Get(object o, string prop)
        {
            return o.GetType().GetProperty(prop).GetValue(o, null);
        }

        public static object GetStatic(Type t, string prop)
        {
            return t.GetProperty(prop, BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
        }

        // Picks the overload whose parameter types accept the given arguments.
        static MethodInfo Find(Type t, BindingFlags flags, string name, object[] args)
        {
            return t.GetMethods(BindingFlags.Public | flags).First(x =>
            {
                if (x.Name != name) return false;
                ParameterInfo[] ps = x.GetParameters();
                if (ps.Length != args.Length) return false;
                for (int i = 0; i < ps.Length; i++)
                    if (args[i] != null && !ps[i].ParameterType.IsInstanceOfType(args[i])) return false;
                return true;
            });
        }

        public static object Call(object o, string method, params object[] args)
        {
            return Find(o.GetType(), BindingFlags.Instance, method, args).Invoke(o, args);
        }

        public static object CallStatic(Type t, string method, params object[] args)
        {
            return Find(t, BindingFlags.Static, method, args).Invoke(null, args);
        }

        // Calls a method that returns IAsyncOperation<T>, blocks until it finishes, and returns T.
        public static object CallAsync(object o, string method, params object[] args)
        {
            MethodInfo m = Find(o.GetType(), BindingFlags.Instance, method, args);
            return Await(m.Invoke(o, args), m.ReturnType);
        }

        public static object CallStaticAsync(Type t, string method, params object[] args)
        {
            MethodInfo m = Find(t, BindingFlags.Static, method, args);
            return Await(m.Invoke(null, args), m.ReturnType);
        }

        static object Await(object operation, Type operationType)
        {
            Type result = operationType.GetGenericArguments()[0];

            MethodInfo asTask = Extensions.GetMethods().First(m =>
                m.Name == "AsTask" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1 &&
                m.GetParameters()[0].ParameterType.Name.StartsWith("IAsyncOperation") &&
                !m.GetParameters()[0].ParameterType.Name.Contains("Progress"));

            object task = asTask.MakeGenericMethod(result).Invoke(null, new[] { operation });
            return task.GetType().GetProperty("Result").GetValue(task, null);
        }
    }

    // Friendly app names, keyed by the id Windows uses for an app (its AppUserModelID).
    static class AppDirectory
    {
        public static readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public static readonly List<KeyValuePair<string, string>> Apps = new List<KeyValuePair<string, string>>();
        static bool built;

        // Enumerates the "AppsFolder" through the Shell COM object: every app Windows lists in Start,
        // including Store apps. Slow-ish, so call it from a worker thread.
        public static void Build()
        {
            if (built) return;
            built = true;
            lastBuild = DateTime.Now;
            Watch();
            Apply(Enumerate());
        }

        static DateTime lastBuild = DateTime.MinValue;
        static volatile bool startMenuChanged;
        static readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();

        // A shortcut added or removed in the Start Menu forces the next Refresh, whatever the throttle says.
        static void Watch()
        {
            foreach (string dir in StartMenuDirs())
            {
                try
                {
                    var w = new FileSystemWatcher(dir) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite };
                    FileSystemEventHandler h = (o, e) => startMenuChanged = true;
                    w.Created += h; w.Deleted += h; w.Changed += h; w.Renamed += (o, e) => startMenuChanged = true;
                    w.EnableRaisingEvents = true;
                    watchers.Add(w);
                }
                catch { }
            }
        }

        static IEnumerable<string> StartMenuDirs()
        {
            string[] dirs = {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\Start Menu\Programs") };
            foreach (string d in dirs) if (Directory.Exists(d)) yield return d;
        }

        // Start Menu shortcut name -> its resolved targets. Several shortcuts can share a name.
        static Dictionary<string, List<string>> ShortcutTargets()
        {
            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                object sh = Activator.CreateInstance(t);
                foreach (string dir in StartMenuDirs())
                    foreach (string lnk in Directory.GetFiles(dir, "*.lnk", SearchOption.AllDirectories))
                    {
                        string target = null;
                        try
                        {
                            object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
                            target = sc.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null) as string;
                            if (!string.IsNullOrEmpty(target)) target = Environment.ExpandEnvironmentVariables(target);
                        }
                        catch { }
                        string name = Path.GetFileNameWithoutExtension(lnk);
                        List<string> l;
                        if (!map.TryGetValue(name, out l)) map[name] = l = new List<string>();
                        l.Add(target);
                    }
            }
            catch (Exception ex) { Program.Log(ex); }
            return map;
        }

        // A target on a local drive that no longer exists (an uninstalled program). Empty, URL, shell: and Store targets are never "missing".
        static bool TargetMissing(string target)
        {
            if (string.IsNullOrEmpty(target) || target.Length < 3 || target[1] != ':' || target[2] != '\\') return false;
            if (target.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) >= 0) return false; // Store folder: not listable, so absence proves nothing
            return !File.Exists(target) && !Directory.Exists(target);
        }

        // The existing shortcut targets the Start Menu holds for an app name, for matching it to an uninstall entry.
        public static List<string> TargetsFor(string name)
        {
            List<string> l;
            return ShortcutTargets().TryGetValue(name, out l) ? l.Where(x => !string.IsNullOrEmpty(x)).ToList() : new List<string>();
        }

        // Reads the AppsFolder again, so apps installed since (a Brave or Edge web app, a new program) show up without
        // restarting MacDock. At most every ten seconds. Returns true when the list changed.
        public static bool Refresh()
        {
            if (!built || ((DateTime.Now - lastBuild).TotalSeconds < 10 && !startMenuChanged)) return false;
            startMenuChanged = false;
            lastBuild = DateTime.Now;
            List<KeyValuePair<string, string>> fresh = Enumerate();
            if (fresh.Count == 0) return false;
            lock (Names)
            {
                bool same = fresh.Count == Apps.Count;
                for (int i = 0; same && i < fresh.Count; i++)
                    same = Apps[i].Value == fresh[i].Value && Apps[i].Key == fresh[i].Key;
                if (same) return false;
            }
            Apply(fresh);
            return true;
        }

        static void Apply(List<KeyValuePair<string, string>> list)
        {
            lock (Names)
            {
                Names.Clear();
                Apps.Clear();
                foreach (KeyValuePair<string, string> app in list)
                {
                    Names[app.Value] = app.Key;
                    Apps.Add(app);
                }
            }
        }

        static List<KeyValuePair<string, string>> Enumerate()
        {
            var list = new List<KeyValuePair<string, string>>();
            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                object shell = Activator.CreateInstance(shellType);
                object folder = shellType.InvokeMember("NameSpace", BindingFlags.InvokeMethod, null, shell, new object[] { "shell:AppsFolder" });
                object items = folder.GetType().InvokeMember("Items", BindingFlags.InvokeMethod, null, folder, null);
                Dictionary<string, List<string>> links = ShortcutTargets();
                foreach (object item in (IEnumerable)items)
                {
                    string name = (string)item.GetType().InvokeMember("Name", BindingFlags.GetProperty, null, item, null);
                    string id = (string)item.GetType().InvokeMember("Path", BindingFlags.GetProperty, null, item, null);
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(id)) continue;
                    // Leftovers of uninstalled programs: an id that is a missing path, or a name whose every Start Menu shortcut points at a missing file.
                    if (id.IndexOf('!') < 0)
                    {
                        List<string> l;
                        if (TargetMissing(id)) continue;
                        if (links.TryGetValue(name, out l) && l.Count > 0 && l.All(TargetMissing)) continue;
                    }
                    list.Add(new KeyValuePair<string, string>(name, id));
                }
            }
            catch (Exception ex) { Program.Log(ex); }
            return list;
        }

        static string Squash(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in s ?? "") if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        // An id Windows doesn't list (a toast sender like "WhatsApp.Root") matched to the AppsFolder app whose name it contains.
        public static KeyValuePair<string, string>? Match(string id)
        {
            string flat = Squash(id);
            lock (Names)
            {
                if (Names.ContainsKey(id)) return null;
                KeyValuePair<string, string>? best = null;
                foreach (var app in Apps)
                {
                    string n = Squash(app.Key);
                    if (n.Length < 4 || flat.IndexOf(n, StringComparison.Ordinal) < 0) continue;
                    if (best == null || n.Length > Squash(best.Value.Key).Length) best = app;
                }
                return best;
            }
        }

        public static string NameFor(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            string name;
            lock (Names) if (Names.TryGetValue(id, out name)) return name;
            var match = Match(id);
            if (match != null) return match.Value.Key;

            // Fall back to something readable from the id itself.
            string tail = id;
            int bang = tail.IndexOf('!');
            if (bang > 0) tail = tail.Substring(0, bang);
            int backslash = tail.LastIndexOf('\\');
            if (backslash >= 0) tail = Path.GetFileNameWithoutExtension(tail.Substring(backslash + 1));
            int underscore = tail.IndexOf('_');
            if (underscore > 0) tail = tail.Substring(0, underscore);
            int dot = tail.LastIndexOf('.');
            if (dot >= 0 && dot < tail.Length - 1) tail = tail.Substring(dot + 1);
            return tail;
        }
    }

    static class NotificationFeed
    {
        const int HistorySize = 20;

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPStr)] string file, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int sqlite3_close(IntPtr db);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPStr)] string sql, int length, out IntPtr stmt, IntPtr tail);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int sqlite3_step(IntPtr stmt);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr sqlite3_column_blob(IntPtr stmt, int column);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int sqlite3_column_bytes(IntPtr stmt, int column);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern long sqlite3_column_int64(IntPtr stmt, int column);

        static readonly string SourceDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Notifications");
        static readonly string CopyDir = Path.Combine(Path.GetTempPath(), "MacDockNotifications");
        // The -shm index file is skipped: SQLite rebuilds it from the -wal file, and Windows keeps part of it locked.
        static readonly string[] Files = { "wpndatabase.db", "wpndatabase.db-wal" };

        static bool primed;
        static DateTime lastStamp = DateTime.MinValue;

        // What the user dismissed here. Windows' own list may keep (or fail to clear) these, and the poll re-reads
        // its database, so dismissed notifications are filtered out locally: by id, or everything up to a "Clear" time.
        static readonly HashSet<long> dismissed = new HashSet<long>();
        static DateTime clearedUpTo = DateTime.MinValue;
        static readonly object dismissLock = new object();

        // Remembered across restarts (Windows may not let us clear its own list): "all <ticks>" and "id <n>" lines.
        static readonly string DismissFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacDock", "dismissed.txt");

        static void LoadDismissed()
        {
            try
            {
                if (!File.Exists(DismissFile)) return;
                foreach (string line in File.ReadAllLines(DismissFile))
                {
                    string[] p = line.Split(' ');
                    long v;
                    if (p.Length != 2 || !long.TryParse(p[1], out v)) continue;
                    if (p[0] == "all") { DateTime t = new DateTime(v); if (t > clearedUpTo) clearedUpTo = t; }
                    else if (p[0] == "id") dismissed.Add(v);
                }
            }
            catch { }
        }

        static void SaveDismissed(string line)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(DismissFile)); File.AppendAllText(DismissFile, line + "\r\n"); }
            catch { }
        }

        static NotificationFeed() { LoadDismissed(); }

        public static bool IsDismissed(NotificationInfo n)
        {
            lock (dismissLock) return n.Time <= clearedUpTo || dismissed.Contains(n.Id);
        }

        public static void Dismiss(long id)
        {
            lock (dismissLock) { if (dismissed.Add(id)) SaveDismissed("id " + id); }
        }

        // Nothing that has arrived so far may come back.
        public static void DismissAll()
        {
            lock (dismissLock) { clearedUpTo = DateTime.Now; SaveDismissed("all " + clearedUpTo.Ticks); }
        }
        static readonly HashSet<long> seen = new HashSet<long>();
        static List<NotificationInfo> history = new List<NotificationInfo>();

        // Returns the notifications Windows currently holds (newest first). Notifications that appeared since
        // the previous call are returned in "added". The first call only records what already exists, so old
        // notifications don't pop up at startup. Cheap when nothing changed: it only checks file timestamps.
        public static List<NotificationInfo> Poll(out List<NotificationInfo> added)
        {
            added = new List<NotificationInfo>();
            try
            {
                DateTime stamp = DateTime.MinValue;
                foreach (string f in Files)
                {
                    string path = Path.Combine(SourceDir, f);
                    if (File.Exists(path)) stamp = stamp > File.GetLastWriteTimeUtc(path) ? stamp : File.GetLastWriteTimeUtc(path);
                }
                if (stamp == lastStamp && primed) return history;
                lastStamp = stamp;

                List<NotificationInfo> rows = ReadDatabase();
                if (rows == null) { lastStamp = DateTime.MinValue; return history; }

                foreach (NotificationInfo n in rows)
                {
                    if (seen.Add(n.Id) && primed) added.Add(n);
                }
                history = rows;
                primed = true;
            }
            catch (Exception ex) { Program.Log(ex); lastStamp = DateTime.MinValue; }
            return history;
        }

        // Copies the database (plus its write-ahead log) so Windows' lock never matters, then reads the newest toasts.
        static List<NotificationInfo> ReadDatabase()
        {
            Directory.CreateDirectory(CopyDir);
            string staleIndex = Path.Combine(CopyDir, "wpndatabase.db-shm");
            if (File.Exists(staleIndex)) File.Delete(staleIndex);

            foreach (string f in Files)
            {
                string source = Path.Combine(SourceDir, f), target = Path.Combine(CopyDir, f);
                if (!File.Exists(source)) { if (File.Exists(target)) File.Delete(target); continue; }

                // Windows may hold a byte-range lock for a moment; retry briefly.
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var output = new FileStream(target, FileMode.Create, FileAccess.Write))
                            input.CopyTo(output);
                        break;
                    }
                    catch (IOException)
                    {
                        if (attempt >= 4) { lastStamp = DateTime.MinValue; return null; } // try again on the next poll
                        System.Threading.Thread.Sleep(40);
                    }
                }
            }

            var result = new List<NotificationInfo>();
            IntPtr db;
            // Read-write on our private copy so SQLite can rebuild the -shm index; the original is never touched.
            if (sqlite3_open_v2(Path.Combine(CopyDir, "wpndatabase.db"), out db, 2 /* SQLITE_OPEN_READWRITE */, IntPtr.Zero) != 0) return null;
            try
            {
                string sql = "SELECT n.Id, h.PrimaryId, n.ArrivalTime, n.Payload FROM Notification n " +
                             "JOIN NotificationHandler h ON n.HandlerId = h.RecordId WHERE n.Type = 'toast' " +
                             "ORDER BY n.ArrivalTime DESC LIMIT " + HistorySize * 3; // extra rows: some may be dismissed
                IntPtr stmt;
                if (sqlite3_prepare_v2(db, sql, -1, out stmt, IntPtr.Zero) != 0) return null;
                try
                {
                    while (sqlite3_step(stmt) == 100 /* SQLITE_ROW */)
                    {
                        NotificationInfo info = Parse(
                            sqlite3_column_int64(stmt, 0), Text(stmt, 1), sqlite3_column_int64(stmt, 2), Text(stmt, 3));
                        if (info != null && !IsDismissed(info) && result.Count < HistorySize) result.Add(info);
                    }
                }
                finally { sqlite3_finalize(stmt); }
            }
            finally { sqlite3_close(db); }
            return result;
        }

        static string Text(IntPtr stmt, int column)
        {
            IntPtr p = sqlite3_column_blob(stmt, column);
            int length = sqlite3_column_bytes(stmt, column);
            if (p == IntPtr.Zero || length <= 0) return null;
            var bytes = new byte[length];
            Marshal.Copy(p, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
        }

        static NotificationInfo Parse(long id, string appId, long arrival, string payload)
        {
            if (payload == null) return null;

            var texts = new List<string>();
            foreach (Match m in Regex.Matches(payload, @"<text[^>]*>(.*?)</text>", RegexOptions.Singleline))
            {
                string t = WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
                if (t.Length > 0) texts.Add(t);
            }
            if (texts.Count == 0) return null;

            DateTime time;
            try { time = DateTime.FromFileTime(arrival); } catch { time = DateTime.Now; }

            // What a click should pass to the app: the toast's own launch string and activation type.
            string launch = null, activation = null;
            Match toast = Regex.Match(payload, @"<toast\b([^>]*)>", RegexOptions.Singleline);
            if (toast.Success)
            {
                Match l = Regex.Match(toast.Groups[1].Value, @"\blaunch\s*=\s*""([^""]*)""");
                Match a = Regex.Match(toast.Groups[1].Value, @"\bactivationType\s*=\s*""([^""]*)""");
                if (l.Success) launch = WebUtility.HtmlDecode(l.Groups[1].Value);
                if (a.Success) activation = a.Groups[1].Value;
            }

            return new NotificationInfo
            {
                Id = id,
                App = AppDirectory.NameFor(appId),
                AppId = AppDirectory.Match(appId) != null ? AppDirectory.Match(appId).Value.Value : appId,
                Title = texts[0],
                Body = texts.Count > 1 ? string.Join(" ", texts.Skip(1)) : "",
                Launch = launch,
                ActivationType = activation,
                Time = time
            };
        }

        // Asks Windows to clear its notification list. This call is quick; only the fetch API is slow.
        public static void ClearAll()
        {
            try
            {
                Type type = Rt.Winrt("Windows.UI.Notifications.Management.UserNotificationListener");
                object listener = Rt.GetStatic(type, "Current");
                if (Rt.Call(listener, "GetAccessStatus").ToString() == "Allowed") Rt.Call(listener, "ClearNotifications");
            }
            catch (Exception ex) { Program.Log(ex); }
            lastStamp = DateTime.MinValue; // re-read on the next poll
        }
    }

    static class MediaFeed
    {
        static object manager;
        static object session;

        public static MediaInfo Poll()
        {
            try
            {
                if (manager == null)
                {
                    Type type = Rt.Winrt("Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager");
                    manager = Rt.CallStaticAsync(type, "RequestAsync");
                }

                session = Rt.Call(manager, "GetCurrentSession");
                if (session == null) return null;

                object props = Rt.CallAsync(session, "TryGetMediaPropertiesAsync");
                string title = props == null ? null : (string)Rt.Get(props, "Title");
                if (string.IsNullOrEmpty(title)) return null;

                string artist = (string)Rt.Get(props, "Artist") ?? "";
                string key = title + "\n" + artist;
                if (key != artKey) { artKey = key; art = LoadArt(props); }

                return new MediaInfo
                {
                    Title = title,
                    Artist = artist,
                    Art = art,
                    Playing = Rt.Get(Rt.Call(session, "GetPlaybackInfo"), "PlaybackStatus").ToString() == "Playing"
                };
            }
            catch { manager = null; session = null; return null; }
        }

        static string artKey;
        static System.Drawing.Bitmap art;

        static System.Drawing.Bitmap LoadArt(object props)
        {
            try
            {
                object thumb = Rt.Get(props, "Thumbnail");
                if (thumb == null) return null;
                object ras = Rt.CallAsync(thumb, "OpenReadAsync");
                if (ras == null) return null;
                Type ext = Type.GetType("System.IO.WindowsRuntimeStreamExtensions, System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
                var m = ext.GetMethods().First(x => x.Name == "AsStreamForRead" && x.GetParameters().Length == 1);
                using (var st = (Stream)m.Invoke(null, new[] { ras }))
                using (var ms = new MemoryStream())
                {
                    st.CopyTo(ms);
                    if (ms.Length == 0) return null;
                    ms.Position = 0;
                    using (var img = System.Drawing.Image.FromStream(ms))
                        return new System.Drawing.Bitmap(img);
                }
            }
            catch { return null; }
        }

        public static void TogglePlayPause() { Fire("TryTogglePlayPauseAsync"); }
        public static void Next() { Fire("TrySkipNextAsync"); }
        public static void Previous() { Fire("TrySkipPreviousAsync"); }

        static void Fire(string method)
        {
            object s = session;
            if (s == null) return;
            Task.Run(() => { try { Rt.Call(s, method); } catch { } });
        }
    }
}
