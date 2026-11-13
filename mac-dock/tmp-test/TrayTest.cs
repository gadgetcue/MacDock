using System; using System.Threading;
namespace MacDock { static class TrayTest {
 [STAThread] static void Main(string[] a) {
  if (a.Length > 0 && a[0] == "scan") { var ev = new ManualResetEvent(false); TrayIcons.Scan(() => ev.Set()); ev.WaitOne(30000); foreach (var t in TrayIcons.Cached) Console.WriteLine(t.Key + " hidden=" + t.Hidden + " icon=" + (t.Icon != null)); return; }
  foreach (var t in TrayIcons.Cached) if (t.Key == a[0]) { TrayIcons.Click(t, int.Parse(a[1])); Thread.Sleep(6000); }
 } } }
