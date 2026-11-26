// Discovery and pairing of new Bluetooth devices (classic and LE) through Windows.Devices.Enumeration.
// WinRT events cannot be added with plain reflection, so Hook() goes through WindowsRuntimeMarshal.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MacDock
{
    static class BtScan
    {
        const string Selector =
            "(System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\" OR System.Devices.Aep.ProtocolId:=\"{bb7bb05e-5972-42b5-94fc-76eaa7084d49}\")" +
            " AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#False";

        static readonly object Gate = new object();
        static readonly Dictionary<string, object> Found = new Dictionary<string, object>();
        static readonly Dictionary<string, string> State = new Dictionary<string, string>(); // id -> "Pairing..." / "Failed"
        static object watcher;
        static int generation;
        public static volatile int Version;

        public static bool Scanning { get { return watcher != null; } }

        // Starts a fresh scan and returns its number; an older scan is stopped.
        public static int Start()
        {
            lock (Gate)
            {
                StopLocked();
                Found.Clear(); State.Clear();
                Version++;
                int mine = ++generation;
                try
                {
                    Type info = Rt.Winrt("Windows.Devices.Enumeration.DeviceInformation");
                    Type kind = Rt.Winrt("Windows.Devices.Enumeration.DeviceInformationKind");
                    object w = Rt.CallStatic(info, "CreateWatcher", Selector, new string[0], Enum.Parse(kind, "AssociationEndpoint"));
                    Hook(w, "Added", (s, d) => { lock (Gate) { if (mine != generation) return; Found[(string)Rt.Get(d, "Id")] = d; Version++; } });
                    Hook(w, "Updated", (s, u) =>
                    {
                        lock (Gate)
                        {
                            object d;
                            if (mine != generation || !Found.TryGetValue((string)Rt.Get(u, "Id"), out d)) return;
                            string before = (string)Rt.Get(d, "Name");
                            Rt.Call(d, "Update", u);
                            if (before != (string)Rt.Get(d, "Name")) Version++;
                        }
                    });
                    Hook(w, "Removed", (s, u) => { lock (Gate) { if (mine == generation && Found.Remove((string)Rt.Get(u, "Id"))) Version++; } });
                    Rt.Call(w, "Start");
                    watcher = w;
                }
                catch (Exception ex) { Program.Log("BT scan failed: " + (ex.InnerException ?? ex).Message); }
                return mine;
            }
        }

        public static bool Current(int scan) { return scan == generation && Scanning; }

        public static void Stop(int scan) { lock (Gate) if (scan == generation) StopLocked(); }

        static void StopLocked()
        {
            if (watcher == null) return;
            Program.Log("BT scan stopped, " + Found.Values.Count(d => !string.IsNullOrWhiteSpace((string)Rt.Get(d, "Name"))) + " named of " + Found.Count + " found");
            try { Rt.Call(watcher, "Stop"); } catch { }
            watcher = null;
            Version++;
        }

        // Named devices only (as macOS lists them): id, name, state.
        public static List<string[]> Devices()
        {
            lock (Gate)
                return Found.Select(p => new[] { p.Key, (string)Rt.Get(p.Value, "Name"), State.ContainsKey(p.Key) ? State[p.Key] : "Pair" })
                    .Where(d => !string.IsNullOrWhiteSpace(d[1])).OrderBy(d => d[1], StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        // Blocks until pairing ends; call on a worker thread.
        public static bool Pair(string id)
        {
            object device;
            string name;
            lock (Gate)
            {
                if (!Found.TryGetValue(id, out device) || (State.ContainsKey(id) && State[id] == "Pairing...")) return false;
                name = (string)Rt.Get(device, "Name");
                State[id] = "Pairing..."; Version++;
            }
            string status;
            try
            {
                object custom = Rt.Get(Rt.Get(device, "Pairing"), "Custom");
                Hook(custom, "PairingRequested", (s, a) => Answer(name, a));
                Type kinds = Rt.Winrt("Windows.Devices.Enumeration.DevicePairingKinds");
                status = Rt.Get(Rt.CallAsync(custom, "PairAsync", Enum.ToObject(kinds, 15)), "Status").ToString();
            }
            catch (Exception ex) { status = (ex.InnerException ?? ex).Message; }
            Program.Log("BT pair " + name + ": " + status);
            bool ok = status == "Paired" || status == "AlreadyPaired";
            lock (Gate) { if (ok) { Found.Remove(id); State.Remove(id); } else State[id] = "Failed - retry"; Version++; }
            return ok;
        }

        static void Answer(string name, object args)
        {
            string kind = Rt.Get(args, "PairingKind").ToString();
            if (kind == "ProvidePin")
            {
                string typed = Microsoft.VisualBasic.Interaction.InputBox("Enter the PIN for " + name + ".", "Bluetooth Pairing");
                if (typed.Length > 0) Rt.Call(args, "Accept", typed);
                return;
            }
            if (kind == "ConfirmPinMatch" && !Ask("Does " + name + " show the code " + Rt.Get(args, "Pin") + "?", MessageBoxButtons.YesNo)) return;
            if (kind == "DisplayPin")
            {
                string pin = (string)Rt.Get(args, "Pin");
                Task.Run(() => Ask("Type " + pin + " on " + name + ", then press Enter.", MessageBoxButtons.OK));
            }
            Rt.Call(args, "Accept");
        }

        static bool Ask(string text, MessageBoxButtons buttons)
        {
            return MessageBox.Show(text, "Bluetooth Pairing", buttons, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1, MessageBoxOptions.DefaultDesktopOnly) != DialogResult.No;
        }

        static void Hook(object target, string name, Action<object, object> handler)
        {
            EventInfo ev = target.GetType().GetEvent(name);
            Type type = ev.EventHandlerType;
            ParameterExpression[] ps = type.GetMethod("Invoke").GetParameters().Select(p => Expression.Parameter(p.ParameterType)).ToArray();
            Delegate d = Expression.Lambda(type, Expression.Invoke(Expression.Constant(handler), ps.Select(p => (Expression)Expression.Convert(p, typeof(object)))), ps).Compile();
            Delegate add = Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(type, typeof(EventRegistrationToken)), target, ev.GetAddMethod());
            Delegate remove = Delegate.CreateDelegate(typeof(Action<EventRegistrationToken>), target, ev.GetRemoveMethod());
            typeof(WindowsRuntimeMarshal).GetMethod("AddEventHandler").MakeGenericMethod(type).Invoke(null, new object[] { add, remove, d });
        }
    }
}
