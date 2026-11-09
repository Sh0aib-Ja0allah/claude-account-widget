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
using System.Windows.Input;
// Worker A's independent S7 checks (update notice) and the S6 fix retest. Its own local server on 127.0.0.1:18774;
// no real GitHub request, no real browser, no real global shortcut.
// update-notice.exe <out folder>
static class H12
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static string root, account;
    static int fails, passes;
    static object Get(object o, string f) { return o.GetType().GetField(f, Any).GetValue(o); }
    static void Log(string s) { Console.WriteLine(s); Console.Out.Flush(); }
    static void Check(string what, bool ok, string info = "")
    {
        Log((ok ? "PASS " : "FAIL ") + what + (info.Length > 0 ? " [" + info.Replace("\r", "\\r").Replace("\n", "\\n") + "]" : ""));
        if (ok) passes++; else fails++;
    }
    static readonly string Base = TestPorts.Base(18774) + "/";
    const string Rel = "https://github.com/Sh0aib-Ja0allah/claude-account-widget/releases/tag/";
    static string mode = "ok";
    static List<string> requests = new List<string>();
    // The widget flow follows Program.AppVersion (2026-11-08): it serves the current version (must show nothing), then the next patch.
    static string Next = NextPatch(Program.AppVersion);
    static string NextPatch(string v) { string[] p = v.Split('.'); return p[0] + "." + p[1] + "." + (int.Parse(p[2]) + 1); }
    static string ListOf(string version) { return "[{\"tag_name\":\"v" + version + "\",\"html_url\":\"" + Rel + "v" + version + "\",\"draft\":false,\"prerelease\":true}]"; }
    static string releases = ListOf(Next);

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
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        lock (requests) requests.Add(c.Request.Url.PathAndQuery + "\t" + c.Request.UserAgent + "\t" + (c.Request.Headers["Authorization"] ?? "noauth") + "\t" + c.Request.Headers["Cookie"]);
                        string m = mode;
                        if (c.Request.Url.AbsolutePath == "/target") { Send(c, 200, releases); return; }
                        if (m == "redirect") { c.Response.StatusCode = 302; c.Response.RedirectLocation = Base + "target"; c.Response.Close(); return; }
                        if (m == "drip")
                        {
                            c.Response.StatusCode = 200; c.Response.SendChunked = true;
                            Stream s = c.Response.OutputStream;
                            byte[] start = Encoding.UTF8.GetBytes("[");
                            s.Write(start, 0, 1); s.Flush();
                            for (int i = 0; i < 24; i++) { Thread.Sleep(1000); s.Write(Encoding.UTF8.GetBytes(" "), 0, 1); s.Flush(); }
                            byte[] rest = Encoding.UTF8.GetBytes(releases.Substring(1));
                            s.Write(rest, 0, rest.Length); c.Response.Close(); return;
                        }
                        if (m == "huge") { Send(c, 200, "[" + new string(' ', 1100 * 1024) + "]"); return; }
                        if (m == "object") { Send(c, 200, "{\"message\":\"Not Found\"}"); return; }
                        if (m == "deep") { Send(c, 200, new string('[', 5000) + new string(']', 5000)); return; }
                        if (m == "html") { Send(c, 200, "<html>rate limited</html>"); return; }
                        Send(c, 200, releases);
                    }
                    catch (Exception) { }
                });
            }
        }) { IsBackground = true }.Start();
    }
    static void Send(HttpListenerContext c, int code, string body)
    {
        byte[] b = Encoding.UTF8.GetBytes(body);
        c.Response.StatusCode = code; c.Response.ContentType = "application/json";
        c.Response.OutputStream.Write(b, 0, b.Length); c.Response.Close();
    }

    [STAThread]
    static void Main(string[] args)
    {
        root = args[0];
        Directory.CreateDirectory(root);
        account = Path.Combine(root, "account.json");
        File.WriteAllText(account, "{\"oauthAccount\":{\"emailAddress\":\"sample@example.com\",\"planDisplayName\":\"Max\"}}");
        Serve();
        Application app = new Application();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.DispatcherUnhandledException += (s, e) => { Log("UNHANDLED " + e.Exception); fails++; e.Handled = true; };
        string d = Path.Combine(root, "w");
        if (Directory.Exists(d)) Directory.Delete(d, true);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "settings.txt"), "-6000,0,0,0,default,system,0,1,off");
        WidgetWindow w = new WidgetWindow(d, account, null, null, 300, 0, true);
        w.ShowActivated = false;
        w.Loaded += async delegate
        {
            try { Pure(); await Widget(w, d); await Download(); }
            catch (Exception e) { Log("HARNESS ERROR " + e); fails++; }
            Log("passes=" + passes + " fails=" + fails);
            Process.GetCurrentProcess().Kill();
        };
        app.Run(w);
    }

    static int Cmp(string a, string b) { return VersionTag.Compare(VersionTag.Parse(a), VersionTag.Parse(b)); }

    static void Pure()
    {
        Check("tags: 0.1.0, v0.1.0 and 0.1.0+build are equal (not offered)", Cmp("0.1.0", "v0.1.0") == 0 && Cmp("0.1.0+b7", "0.1.0") == 0);
        Check("tags: a pre-release of the current version is older (not offered)", Cmp("v0.1.0-rc.1", "0.1.0") < 0);
        Check("tags: 1.0.0-alpha.1 < 1.0.0-alpha.beta < 1.0.0-beta.2 < 1.0.0-beta.11 < 1.0.0", Cmp("1.0.0-alpha.1", "1.0.0-alpha.beta") < 0 && Cmp("1.0.0-alpha.beta", "1.0.0-beta.2") < 0 && Cmp("1.0.0-beta.2", "1.0.0-beta.11") < 0 && Cmp("1.0.0-beta.11", "1.0.0") < 0);
        Check("tags: 999999999.0.0 parses, 1000000000.0.0 doesn't", VersionTag.Parse("999999999.0.0") != null && VersionTag.Parse("1000000000.0.0") == null);
        Check("tags: odd shapes refused", new[] { "V1.0.0", "1.0", "1.0.0.0", "1.0.0-", "1.0.0+", " 1.0.0", "1.0.0\n", "v1.0.0-a..b", "Ù¡.Ù .Ù " }.All(t => VersionTag.Parse(t) == null));
        Log("NOTE tags: leading zeros (v01.02.003) " + (VersionTag.Parse("v01.02.003") == null ? "refused" : "accepted and compared as numbers; harmless"));

        string[] good = { Rel + "v0.2.0", "https://github.com/Sh0aib-Ja0allah/claude-account-widget/releases/latest", "https://github.com/sh0aib-ja0allah/CLAUDE-account-widget/releases/tag/v0.2.0-rc.1+b7", "https://GITHUB.com/Sh0aib-Ja0allah/claude-account-widget/releases/tag/v0.2.0", "https://github.com:443/Sh0aib-Ja0allah/claude-account-widget/releases/tag/v0.2.0" };
        string[] bad = {
            "https://github.com/Sh0aib-Ja0allah/claude-account-widget-evil/releases/tag/v1", "https://github.com/Sh0aib-Ja0allah/claude-account-widget/releasesx/v1",
            "https://github.com.evil.com/Sh0aib-Ja0allah/claude-account-widget/releases/tag/v1", "https://github.com./Sh0aib-Ja0allah/claude-account-widget/releases/tag/v1",
            "https://github.com/Sh0aib-Ja0allah/claude-account-widget/releases/tag/v1\t", "https://git\thub.com/Sh0aib-Ja0allah/claude-account-widget/releases/tag/v1",
            "https://github.com//Sh0aib-Ja0allah/claude-account-widget/releases/tag/v1", "http://github.com/Sh0aib-Ja0allah/claude-account-widget/releases/tag/v1",
            "https://github.com/Sh0aib-Ja0allah/claude-account-widget/releases/tag/v1/./../../x", "https://xn--gthub-n4a.com/Sh0aib-Ja0allah/claude-account-widget/releases/tag/v1",
            "https://gÑ–thub.com/Sh0aib-Ja0allah/claude-account-widget/releases/tag/v1", "https://github.com/Sh0aib-Ja0allah/claude-account-widget/releases/tag/v1;x",
            "https://github.com/Sh0aib-Ja0allah/claude-account-widget/releases/tag/v1 x", " https://github.com/Sh0aib-Ja0allah/claude-account-widget/releases/tag/v1",
            "https://github.com/Sh0aib-Ja0allah/claude-account-widget/releases/tag/v\u212A", "https://github.com/Sh0aib-Ja0allah/claude-account-widget/releases/tag/\uFF561", "https://github.com/Sh0aib-Ja0allah/claude-account-widget/releases/tag/v1\u00A0", "https://github.com/Sh0aib-Ja0allah/claude-account-widget/releases/tag/v\u0131", "https://github.com/Sh0aib-Ja0allah/claude-account-widget/releases/tag/v1\u0301", "https://github.com/Sh0aib-Ja0allah/claude-account-widget/releases/tag/v1\u200B" };
        Check("links: good release links accepted", good.All(UpdateClient.IsReleaseLink), string.Join(" ", good.Where(x => !UpdateClient.IsReleaseLink(x))));
        List<string> passed = bad.Where(UpdateClient.IsReleaseLink).ToList();
        Check("links: look-alike and odd links refused", passed.Count == 0, string.Join(" | ", passed));
        Log("NOTE links accepted from the odd list (all still on github.com under the repo's releases): " + (passed.Count == 0 ? "none" : string.Join(" | ", passed)));

        Check("list: a JSON object (GitHub's error shape) gives nothing", UpdateClient.Newest("{\"message\":\"Not Found\"}", "0.1.0") == null);
        Check("list: draft as the string \"false\" is skipped", UpdateClient.Newest("[{\"tag_name\":\"v0.2.0\",\"html_url\":\"" + Rel + "v0.2.0\",\"draft\":\"false\"}]", "0.1.0") == null);
        Check("list: 5,000-deep nesting gives nothing and doesn't throw", Safe(delegate { return UpdateClient.Newest(new string('[', 5000) + new string(']', 5000), "0.1.0"); }) == "null");
        string[] n = UpdateClient.Newest("[{\"tag_name\":\"v0.1.1-rc.1\",\"html_url\":\"" + Rel + "v0.1.1-rc.1\",\"draft\":false},{\"tag_name\":\"v0.1.0\",\"html_url\":\"" + Rel + "v0.1.0\",\"draft\":false},{\"tag_name\":\"v9.0.0\",\"html_url\":\"https://github.com/other/repo/releases/tag/v9.0.0\",\"draft\":false}]", "0.1.0");
        Check("list: newest valid pre-release offered, the higher tag with a foreign link ignored", n != null && n[0] == "v0.1.1-rc.1", n == null ? "null" : n[0]);
        Check("list: tag and link from different releases can't pair up", UpdateClient.Newest("[{\"tag_name\":\"v5.0.0\"},{\"html_url\":\"" + Rel + "v5.0.0\"}]", "0.1.0") == null);

        string f = Path.Combine(root, "state.txt");
        double now = Math.Floor((DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
        File.WriteAllText(f, "last=" + (long)(now - 86399) + "\r\n");
        Check("state: 23h59m59s after the last attempt isn't due", !UpdateState.Load(f).Due(now));
        File.WriteAllText(f, "last=" + (long)(now - 86400) + "\r\n");
        Check("state: exactly 24 h is due", UpdateState.Load(f).Due(now));
        File.WriteAllText(f, "last=-5\r\nlast=1e5\r\n");
        Check("state: unparseable last values mean due", UpdateState.Load(f).Due(now));
        File.WriteAllText(f, "tag=v0.2.0\r\nlink=" + Rel + "v0.2.0\r\ntag=v0.3.0\r\n");
        UpdateState st = UpdateState.Load(f);
        Log("NOTE state: a second tag line replaces the first, so tag v0.3.0 pairs with link .../v0.2.0: offer=" + (st.Offer("0.1.0") ?? "none") + " link=" + st.Link);
        File.WriteAllText(f, "tag=v0.2.0\r\nlink=" + Rel + "v0.2.0\r\ndismissed=v0.2.0\r\n");
        Check("state: dismissed same tag gives no offer", UpdateState.Load(f).Offer("0.1.0") == null);
        File.WriteAllText(f, "tag=v0.2.0\r\nlink=" + Rel + "v0.2.0\r\ndismissed=v9.9.9\r\n");
        Check("state: a dismissed tag higher than the offer hides it too", UpdateState.Load(f).Offer("0.1.0") == null);
    }

    static string Safe(Func<string[]> f)
    {
        try { string[] r = f(); return r == null ? "null" : string.Join(",", r); }
        catch (Exception e) { return "threw " + e.GetType().Name; }
    }

    static async Task Widget(WidgetWindow w, string d)
    {
        string uf = Path.Combine(d, "update-check.txt");
        List<string> opened = new List<string>();
        w.OpenLink = delegate(string link) { opened.Add(link); };
        TextBlock line = (TextBlock)Get(w, "updateText");

        Check("widget: no release address in test mode without arg 6 means no request", w.ReleasesUrl == null && w.CheckForUpdate() == null && requests.Count == 0);
        w.ReleasesUrl = Base + "releases";
        mode = "redirect";
        Task t = w.CheckForUpdate();
        if (t != null) await t;
        Check("widget: a redirect isn't followed (one request, nothing shown)", requests.Count == 1 && !requests.Any(r => r.StartsWith("/target")) && line.Text == "", string.Join(" | ", requests));
        Check("widget: the request carries no Authorization or cookie, and a versioned User-Agent", requests[0].Contains("\tClaudeAccountWidget/" + Program.AppVersion + "\tnoauth\t"), requests[0]);
        File.Delete(uf);
        mode = "ok";
        releases = ListOf(Program.AppVersion);
        t = w.CheckForUpdate();
        if (t != null) await t;
        Check("widget: the current version (" + Program.AppVersion + ") offered shows no line", line.Text == "" && requests.Count == 2, line.Text + "; " + requests.Count + " requests");
        File.Delete(uf);
        releases = ListOf(Next);
        t = w.CheckForUpdate();
        if (t != null) await t;
        Check("widget: the next version (" + Next + ") offered is shown", line.Text == "Version " + Next + " is available", line.Text);
        Check("widget: a second check the same day sends nothing", w.CheckForUpdate() == null && requests.Count == 3);
        // tamper with the link after it was shown
        File.WriteAllText(uf, File.ReadAllText(uf).Replace("tag/v" + Next, "tag/v" + Next + "x"));
        w.GetType().GetMethod("OpenUpdateLink", Any).Invoke(w, null);
        Check("widget: a link changed after showing isn't opened", opened.Count == 0, string.Join(" ", opened));
        File.WriteAllText(uf, File.ReadAllText(uf).Replace("tag/v" + Next + "x", "tag/v" + Next));
        w.GetType().GetMethod("OpenUpdateLink", Any).Invoke(w, null);
        Check("widget: the shown link opens", opened.Count == 1 && opened[0] == Rel + "v" + Next, string.Join(" ", opened));

        // Settings switch
        SettingsWindow s = new SettingsWindow(w, d);
        s.Left = -6000; s.ShowActivated = false; s.ShowInTaskbar = false; s.Show();
        Border sw = (Border)Get(s, "updateSwitch");
        sw.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(sw), 0, Key.Space) { RoutedEvent = Keyboard.KeyDownEvent });
        Check("switch: off at once, field 7 = 0", !w.updateCheckOn && File.ReadAllText(Path.Combine(d, "settings.txt")).Split(',')[7] == "0");
        File.Delete(uf);
        Check("switch: off means no request even when due", w.CheckForUpdate() == null && requests.Count == 3);
        sw.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(sw), 0, Key.Space) { RoutedEvent = Keyboard.KeyDownEvent });
        Check("switch: on again, field 7 = 1", w.updateCheckOn && File.ReadAllText(Path.Combine(d, "settings.txt")).Split(',')[7] == "1");
        s.Close();

        // S6 fix retest (no registrar in test mode, so imitate one)
        ShortcutRegistrarLike reg = new ShortcutRegistrarLike();
        w.ShortcutRegistrar = reg;
        string a = w.SetShortcut("Ctrl+Alt+Shift+C");
        string b = w.SetShortcut("Ctrl+Alt+Shift+C");
        Check("S6-1: setting the current shortcut again: no error, no second registration", a == null && b == null && reg.Calls.Count == 1, string.Join(" | ", reg.Calls) + " " + b);
        Check("S6-2: PageDown and OemBackslash display friendly names", ShortcutText.Display("Ctrl+Alt+Next") == "Ctrl+Alt+PageDown" && ShortcutText.Display("Ctrl+Alt+OemBackslash") == "Ctrl+Alt+<", ShortcutText.Display("Ctrl+Alt+Next") + " / " + ShortcutText.Display("Ctrl+Alt+OemBackslash"));
        w.SetShortcut("off");
    }

    class ShortcutRegistrarLike : IShortcutRegistrar
    {
        public List<string> Calls = new List<string>();
        Dictionary<int, string> held = new Dictionary<int, string>();
        public int Register(IntPtr window, int id, uint m, uint k) { Calls.Add("register " + id); string c = m + ":" + k; if (held.ContainsValue(c)) return 1409; held[id] = c; return 0; }
        public void Unregister(IntPtr window, int id) { Calls.Add("unregister " + id); held.Remove(id); }
    }

    static async Task Download()
    {
        foreach (string m in new[] { "huge", "object", "deep", "html" })
        {
            mode = m;
            string body = UpdateClient.Download(Base + "releases");
            string r = Safe(delegate { return UpdateClient.Newest(body, "0.1.0"); });
            Check("download: " + m + " reply gives no offer and doesn't throw", r == "null", (body == null ? "body null" : "body " + body.Length) + "; " + r);
        }
        mode = "drip";
        Stopwatch sw = Stopwatch.StartNew();
        string dripped = await Task.Run(() => UpdateClient.Download(Base + "releases"));
        sw.Stop();
        Log("NOTE download: a server dripping one byte a second for 24 s kept Download busy for " + (sw.ElapsedMilliseconds / 1000.0).ToString("F1") + " s and returned " + (dripped == null ? "null" : dripped.Length + " chars") + " (each read has a 15 s timeout, the whole request doesn't)");
        UpdateClient.Deadline = TimeSpan.FromSeconds(6);
        sw = Stopwatch.StartNew();
        string capped = await Task.Run(() => UpdateClient.Download(Base + "releases"));
        sw.Stop();
        Check("download: with a 6 s deadline, a 24 s drip gives up by 8 s with nothing", capped == null && sw.ElapsedMilliseconds < 8000, (sw.ElapsedMilliseconds / 1000.0).ToString("F1") + " s, " + (capped == null ? "null" : capped.Length + " chars"));
        UpdateClient.Deadline = TimeSpan.FromSeconds(30);
    }
}
