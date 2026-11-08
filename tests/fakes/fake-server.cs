// Fake usage and release server for widget tests. Never contacts the network.
// fakeusage.exe <port> <folder>
//   GET /usage    -> reads <folder>\figure.txt "used,resetEpoch[,mode]" (mode: ok, 429, 401, 500, badjson, noseven)
//   GET /releases -> serves <folder>\releases.json if present, else 404 (mode file releases-mode.txt: 403, 404, slow, big)
// Every request is logged to <folder>\requests.log as: epoch<TAB>path<TAB>user-agent<TAB>authorization-present
using System; using System.IO; using System.Net; using System.Text; using System.Threading; using System.Globalization;
static class FakeUsage {
  static void Main(string[] a) {
    string dir = a[1];
    HttpListener l = new HttpListener(); l.Prefixes.Add("http://127.0.0.1:" + a[0] + "/"); l.Start();
    File.WriteAllText(Path.Combine(dir, "ready.txt"), "ready");
    while (true) {
      HttpListenerContext c = l.GetContext();
      try { Handle(c, dir); } catch (Exception e) { try { File.AppendAllText(Path.Combine(dir, "server-errors.log"), e.Message + "\r\n"); } catch { } }
      try { c.Response.Close(); } catch { }
    }
  }
  static void Send(HttpListenerContext c, int code, string body) {
    c.Response.StatusCode = code; byte[] b = Encoding.UTF8.GetBytes(body);
    c.Response.ContentType = "application/json"; c.Response.OutputStream.Write(b, 0, b.Length);
  }
  static void Handle(HttpListenerContext c, string dir) {
    string path = c.Request.Url.AbsolutePath;
    long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
    File.AppendAllText(Path.Combine(dir, "requests.log"), now + "\t" + c.Request.Url.PathAndQuery + "\t" + c.Request.UserAgent + "\t" + (c.Request.Headers["Authorization"] != null ? "auth" : "noauth") + "\r\n");
    if (path.StartsWith("/usage")) {
      string[] f = File.ReadAllText(Path.Combine(dir, "figure.txt")).Trim().Split(',');
      string mode = f.Length > 2 ? f[2] : "ok";
      if (mode == "429") { c.Response.Headers["Retry-After"] = "60"; Send(c, 429, "{}"); return; }
      if (mode == "401") { Send(c, 401, "{}"); return; }
      if (mode == "500") { Send(c, 500, "{}"); return; }
      if (mode == "badjson") { Send(c, 200, "{not json"); return; }
      double used = double.Parse(f[0], CultureInfo.InvariantCulture);
      long reset = long.Parse(f[1], CultureInfo.InvariantCulture);
      string at = reset > 0 ? "\"" + new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(reset).ToString("yyyy-MM-ddTHH:mm:ss+00:00", CultureInfo.InvariantCulture) + "\"" : "null";
      string seven = "{\"utilization\":" + used.ToString("R", CultureInfo.InvariantCulture) + ",\"resets_at\":" + at + "}";
      string five = "{\"utilization\":10.0,\"resets_at\":null}";
      Send(c, 200, mode == "noseven" ? "{\"five_hour\":" + five + "}" : "{\"five_hour\":" + five + ",\"seven_day\":" + seven + "}");
      return;
    }
    if (path.StartsWith("/profile")) {
      // profile.json is the reply; profile-mode.txt can say 500, 401 or redirect. The Authorization header is logged above, never its value.
      string modeFile = Path.Combine(dir, "profile-mode.txt");
      string mode = File.Exists(modeFile) ? File.ReadAllText(modeFile).Trim() : "";
      if (mode == "500") { Send(c, 500, "{}"); return; }
      if (mode == "401") { Send(c, 401, "{}"); return; }
      if (mode == "redirect") { c.Response.Redirect("http://127.0.0.1:1/elsewhere"); return; }
      string prof = Path.Combine(dir, "profile.json");
      if (File.Exists(prof)) Send(c, 200, File.ReadAllText(prof)); else Send(c, 404, "{}");
      return;
    }
    if (path.StartsWith("/releases")) {
      string modeFile = Path.Combine(dir, "releases-mode.txt");
      string mode = File.Exists(modeFile) ? File.ReadAllText(modeFile).Trim() : "";
      if (mode == "403") { Send(c, 403, "{}"); return; }
      if (mode == "slow") { Thread.Sleep(20000); }
      if (mode == "drip")
      {
        // A byte every second for 45 s: each read finishes quickly, so only a total deadline stops it.
        c.Response.StatusCode = 200; c.Response.SendChunked = true; c.Response.ContentType = "application/json";
        Stream o = c.Response.OutputStream;
        for (int i = 0; i < 45; i++) { try { o.WriteByte(i == 0 ? (byte)'[' : (byte)' '); o.Flush(); } catch { return; } Thread.Sleep(1000); }
        try { o.WriteByte((byte)']'); o.Flush(); } catch { }
        return;
      }
      if (mode == "big") { Send(c, 200, "[" + new string(' ', 2 * 1024 * 1024) + "]"); return; }
      string rel = Path.Combine(dir, "releases.json");
      if (File.Exists(rel)) Send(c, 200, File.ReadAllText(rel)); else Send(c, 404, "{}");
      return;
    }
    Send(c, 404, "{}");
  }
}
