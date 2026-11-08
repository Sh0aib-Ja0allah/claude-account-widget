// Test helper: "top" starts a "child", which starts a "leaf"; every level then sleeps. "solo" just sleeps.
using System; using System.Diagnostics; using System.Threading;
static class Spawner {
  static void Main(string[] a) {
    string self = Process.GetCurrentProcess().MainModule.FileName;
    string next = a[0] == "top" ? "child" : a[0] == "child" ? "leaf" : null;
    if (next != null) Process.Start(new ProcessStartInfo(self, next) { UseShellExecute = false, CreateNoWindow = true });
    Thread.Sleep(120000);
  }
}
