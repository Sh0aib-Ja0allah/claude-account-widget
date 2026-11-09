using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
// Worker A's independent checks of C3b (account verified through the token's profile). Own fake Anthropic on
// 127.0.0.1:18777; fake credentials with a marker token; never the real Anthropic address.
// account-profile.exe <out folder>
static class H14
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly string Base = TestPorts.Base(18777) + "/";
    const string Token = "TOKEN-MARKER-7f3a9c";
    static string root;
    static int fails, passes;
    static string profileMode = "owner", usageMode = "ok";
    static List<string> log = new List<string>();
    static object Get(object o, string f) { return o.GetType().GetField(f, Any).GetValue(o); }
    static void Log(string s) { Console.WriteLine(s); Console.Out.Flush(); }
    static void Check(string what, bool ok, string info = "")
    {
        Log((ok ? "PASS " : "FAIL ") + what + (info.Length > 0 ? " [" + info.Replace("\r", "\\r").Replace("\n", "\\n") + "]" : ""));
        if (ok) passes++; else fails++;
    }

    static void Serve()
    {
        HttpListener l = new HttpListener();
        l.Prefixes.Add(Base);
        l.Start();
        new Thread(delegate()
        {
            while (true)
            {
                HttpListenerContext c = l.GetContext();
                try
                {
                    string path = c.Request.Url.AbsolutePath;
                    lock (log) log.Add(path + "\t" + (c.Request.Headers["Authorization"] ?? "noauth"));
                    if (path == "/usage")
                    {
                        if (usageMode == "429") { c.Response.Headers["Retry-After"] = "1"; Send(c, 429, "{}"); continue; }
                        if (usageMode == "401") { Send(c, 401, "{}"); continue; }
                        string at = DateTime.UtcNow.AddDays(3).ToString("yyyy-MM-ddTHH:mm:ss+00:00");
                        Send(c, 200, "{\"five_hour\":{\"utilization\":10.0,\"resets_at\":null},\"seven_day\":{\"utilization\":40.0,\"resets_at\":\"" + at + "\"}}");
                        continue;
                    }
                    if (path == "/profile")
                    {
                        if (profileMode == "404") { Send(c, 404, "{}"); continue; }
                        if (profileMode == "renamed") { Send(c, 200, "{\"account\":{\"email_address\":\"owner@example.com\"},\"organization\":{\"rate_limit_tier\":\"default_claude_max_20x\"}}"); continue; }
                        string who = profileMode == "intruder" ? "intruder@example.com" : "owner@example.com";
                        Send(c, 200, "{\"account\":{\"email\":\"" + who + "\",\"uuid\":\"x\"},\"organization\":{\"rate_limit_tier\":\"default_claude_max_20x\"}}");
                        continue;
                    }
                    Send(c, 404, "{}");
                }
                catch (Exception) { }
            }
        }) { IsBackground = true }.Start();
    }
    static void Send(HttpListenerContext c, int code, string body)
    {
        byte[] b = Encoding.UTF8.GetBytes(body);
        c.Response.StatusCode = code; c.Response.ContentType = "application/json";
        c.Response.OutputStream.Write(b, 0, b.Length); c.Response.Close();
    }
    static int Count(string path) { lock (log) return log.Count(x => x.StartsWith(path + "\t")); }

    [STAThread]
    static void Main(string[] args)
    {
        root = args[0];
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        Serve();
        Application app = new Application();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.DispatcherUnhandledException += (s, e) => { Log("UNHANDLED " + e.Exception); fails++; e.Handled = true; };
        Window host = new Window { Left = -6000, Top = 0, Width = 50, Height = 50, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        host.Loaded += async delegate
        {
            try { ParseChecks(); await Flow(); }
            catch (Exception e) { Log("HARNESS ERROR " + e); fails++; }
            Log("passes=" + passes + " fails=" + fails);
            Process.GetCurrentProcess().Kill();
        };
        host.Show();
        app.Run();
    }

    static string P(string email) { ProfileClient.Profile p = ProfileClient.Parse("{\"account\":{\"email\":" + email + "}}"); return p == null ? "null" : p.Email; }

    static void ParseChecks()
    {
        Check("parse: a plain email is accepted", P("\"owner@example.com\"") == "owner@example.com");
        string[] refused = { "\"\"", "null", "42", "[\"a@b.com\"]", "\"@b.com\"", "\"a@\"", "\"a@@b.com\"", "\"a@b.com \"", "\"a@b.com\\u00a0\"", "\"a@b\\u200b.com\"",
            "\"a@b.com\\u202e\"", "\"a@b.com\\u0000\"", "\"a\\nb@c.com\"", "\"<a@b.com>\"", "\"x\\\"@b.com\"", "\"" + new string('a', 250) + "@b.com\"" };
        List<string> through = refused.Where(e => P(e) != "null").ToList();
        Check("parse: empty, non-string, bad @, whitespace, NBSP, zero-width, RTL override, NUL, newline, <>, quote and 256-char values refused", through.Count == 0, string.Join(" | ", through));
        Check("parse: 254 characters accepted, 255 refused", P("\"" + new string('a', 248) + "@b.com\"") != "null" && P("\"" + new string('a', 249) + "@b.com\"") == "null");
        Log("NOTE parse: a Cyrillic look-alike (\"owner@ex\u0430mple.com\") is " + (P("\"owner@ex\\u0430mple.com\"") == "null" ? "refused" : "accepted; it then differs from the expected account, so it warns (safe direction)"));
        Check("parse: account as an array, email nested elsewhere, or a top-level email give nothing",
            ProfileClient.Parse("{\"account\":[{\"email\":\"a@b.com\"}]}") == null && ProfileClient.Parse("{\"email\":\"a@b.com\"}") == null && ProfileClient.Parse("{\"user\":{\"account\":{\"email\":\"a@b.com\"}}}") == null);
        Check("parse: a renamed field (email_address) gives nothing", ProfileClient.Parse("{\"account\":{\"email_address\":\"a@b.com\"}}") == null);
        Check("parse: 30-deep nesting and an HTML page give nothing without throwing", Safe("{\"a\":" + new string('[', 30) + new string(']', 30) + "}") && Safe("<html>no</html>"));
        Check("parse: plan only for default_claude_max_20x", ProfileClient.Parse("{\"account\":{\"email\":\"a@b.com\"},\"organization\":{\"rate_limit_tier\":\"default_claude_max_20x\"}}").Plan == "Ultra"
            && ProfileClient.Parse("{\"account\":{\"email\":\"a@b.com\"},\"organization\":{\"rate_limit_tier\":\"DEFAULT_CLAUDE_MAX_20X\"}}").Plan == null);
    }
    static bool Safe(string body) { try { return ProfileClient.Parse(body) == null; } catch (Exception) { return false; } }

    static async Task<WidgetWindow> Widget(string name, string label, string expected)
    {
        string d = Path.Combine(root, name);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "settings.txt"), "-6000,0,0,0,default,system");
        if (expected != null) File.WriteAllText(Path.Combine(d, "expected-account.txt"), expected);
        File.WriteAllText(Path.Combine(d, "account.json"), "{\"oauthAccount\":{\"emailAddress\":\"" + label + "\",\"planDisplayName\":\"Pro\"}}");
        long exp = (long)(DateTime.UtcNow.AddDays(30) - new DateTime(1970, 1, 1)).TotalMilliseconds;
        File.WriteAllText(Path.Combine(d, "creds.json"), "{\"claudeAiOauth\":{\"accessToken\":\"" + Token + "\",\"expiresAt\":" + exp + ",\"subscriptionType\":\"pro\"}}");
        WidgetWindow w = new WidgetWindow(d, Path.Combine(d, "account.json"), Path.Combine(d, "creds.json"), Base + "usage", 3600, 0, true);
        w.ProfileUrl = Base + "profile";
        w.ShowActivated = false;
        w.Show();
        await Task.Delay(200);
        return w;
    }
    static async Task Fetch(WidgetWindow w)
    {
        w.GetType().GetMethod("FetchUsage", Any).Invoke(w, new object[] { true });
        await Task.Delay(100);
        for (int i = 0; i < 100 && (bool)Get(w, "fetching"); i++) await Task.Delay(50);
        w.GetType().GetMethod("Refresh", Any).Invoke(w, null);
    }
    static string Shown(WidgetWindow w) { return ((TextBlock)Get(w, "email")).Text + " | " + ((TextBlock)Get(w, "accountNote")).Text; }
    static bool Danger(WidgetWindow w) { return ((UIElement)Get(w, "miniBadge")).Visibility == Visibility.Visible; }

    static async Task Flow()
    {
        // 1. Today's state: stale label, expected owner, profile says owner.
        WidgetWindow w = await Widget("today", "stale@example.com", "owner@example.com");
        Log("NOTE today: the widget runs its first usage check at startup; right after it the card shows: " + Shown(w));
        await Fetch(w);
        Check("today: after the check, the verified owner with the mild note, no danger", Shown(w) == "owner@example.com | Signed in (verified)" && !Danger(w), Shown(w));
        Check("today: plan shows Ultra from the profile", ((TextBlock)Get(w, "planText")).Text == "Ultra", ((TextBlock)Get(w, "planText")).Text);
        int p1 = Count("/profile");
        await Fetch(w); await Fetch(w);
        Check("frequency: once verified, two more usage checks ask no profile", Count("/profile") == p1, Count("/profile") + " profile, " + Count("/usage") + " usage");
        Check("token: the profile request carried the token as a Bearer header", log.Any(x => x == "/profile\tBearer " + Token));

        // 2. Token rewritten (Claude Code refreshes it): verified dropped at once.
        string creds = Path.Combine(root, "today", "creds.json");
        File.SetLastWriteTimeUtc(creds, DateTime.UtcNow.AddSeconds(5));
        w.GetType().GetMethod("Refresh", Any).Invoke(w, null);
        Log("NOTE token refresh: right after the credentials file changes, the card shows \"" + Shown(w) + "\" until the next usage check re-verifies (up to the poll interval, 5 min in real use)");
        Check("token change: verified dropped at once; amber meanwhile, never red", Shown(w) == "stale@example.com | Couldn't verify account", Shown(w));
        profileMode = "intruder";
        await Fetch(w);
        Check("real mismatch: a verified different account gives the danger warning", Shown(w) == "intruder@example.com | Warning: not your expected account" && Danger(w), Shown(w));
        profileMode = "owner";

        // 3. Profile never works: is the guard still on?
        profileMode = "404";
        int before = Count("/profile");
        WidgetWindow f = await Widget("broken", "intruder@example.com", "owner@example.com");
        for (int i = 0; i < 4; i++) await Fetch(f);
        Check("profile 404, label really differs from expected: what the owner sees", Danger(f), Shown(f) + "; danger=" + Danger(f) + "; " + (Count("/profile") - before) + " profile requests over 4 usage checks plus the startup one");
        profileMode = "renamed";
        WidgetWindow g = await Widget("renamed", "intruder@example.com", "owner@example.com");
        await Fetch(g); await Fetch(g);
        Check("profile reply with a renamed field (email_address), label really differs: warning shown", Danger(g), Shown(g));
        profileMode = "owner";

        // 4. Usage rejected or rate limited: is the profile still asked?
        usageMode = "401";
        before = Count("/profile");
        WidgetWindow r = await Widget("rejected", "stale@example.com", "owner@example.com");
        await Fetch(r);
        Check("usage 401: no profile request with the rejected token", Count("/profile") == before, (Count("/profile") - before) + " profile requests");
        usageMode = "429";
        before = Count("/profile");
        WidgetWindow q = await Widget("limited", "stale@example.com", "owner@example.com");
        await Fetch(q);
        Check("usage 429: no profile request while rate limited", Count("/profile") == before, (Count("/profile") - before) + " profile requests");
        usageMode = "ok";

        // 5. No token anywhere the widget writes.
        List<string> leaks = new List<string>();
        foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == "creds.json") continue;
            try { if (File.ReadAllText(file).Contains(Token)) leaks.Add(file); } catch (Exception) { }
        }
        Check("token: not in any file the widget wrote", leaks.Count == 0, string.Join(", ", leaks));
        Check("token: the account and credentials files were never rewritten", File.ReadAllText(Path.Combine(root, "today", "account.json")).Contains("stale@example.com"));
        foreach (Window x in new Window[] { w, f, g, r, q }) x.Close();
    }
}
