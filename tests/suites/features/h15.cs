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
using Microsoft.Win32;
// Worker A's checks of the C1 ruling (tier comparison when unverified), the early check after a token change, and
// the test-copy guards (off-screen windows, Start with Windows). Fake Anthropic on 127.0.0.1:18779; fake files only.
// h15.exe <out folder>
static class H15
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    const string Base = "http://127.0.0.1:18779/";
    const string Red = "Warning: not your expected account";
    const string Amber = "Couldn't verify account";
    static string root;
    static int fails, passes;
    static string profileMode = "404";
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
                    lock (log) log.Add(path);
                    if (path == "/usage")
                    {
                        string at = DateTime.UtcNow.AddDays(3).ToString("yyyy-MM-ddTHH:mm:ss+00:00");
                        Send(c, 200, "{\"five_hour\":{\"utilization\":10.0,\"resets_at\":null},\"seven_day\":{\"utilization\":40.0,\"resets_at\":\"" + at + "\"}}");
                    }
                    else if (path == "/profile" && profileMode == "owner") Send(c, 200, "{\"account\":{\"email\":\"owner@example.com\"},\"organization\":{\"rate_limit_tier\":\"default_claude_max_20x\"}}");
                    else Send(c, 404, "{}");
                }
                catch (Exception) { }
            }
        }) { IsBackground = true }.Start();
    }
    static void Send(HttpListenerContext c, int code, string body)
    {
        byte[] b = Encoding.UTF8.GetBytes(body);
        c.Response.StatusCode = code; c.Response.OutputStream.Write(b, 0, b.Length); c.Response.Close();
    }
    static int Count(string p) { lock (log) return log.Count(x => x == p); }

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
            try { await Run(); }
            catch (Exception e) { Log("HARNESS ERROR " + e); fails++; }
            Log("passes=" + passes + " fails=" + fails);
            Process.GetCurrentProcess().Kill();
        };
        host.Show();
        app.Run();
    }

    static int n;
    // fileTier / tokenTier are raw JSON values (with quotes for strings), or null to leave the field out.
    static async Task<WidgetWindow> Widget(string fileTier, string tokenTier)
    {
        string d = Path.Combine(root, "w" + (n++));
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "settings.txt"), "-6000,0,0,0,default,system");
        File.WriteAllText(Path.Combine(d, "expected-account.txt"), "owner@example.com");
        File.WriteAllText(Path.Combine(d, "account.json"), "{\"oauthAccount\":{\"emailAddress\":\"other@example.com\"" + (fileTier == null ? "" : ",\"organizationRateLimitTier\":" + fileTier) + "}}");
        long exp = (long)(DateTime.UtcNow.AddDays(30) - new DateTime(1970, 1, 1)).TotalMilliseconds;
        File.WriteAllText(Path.Combine(d, "creds.json"), "{\"claudeAiOauth\":{\"accessToken\":\"t\",\"expiresAt\":" + exp + (tokenTier == null ? "" : ",\"rateLimitTier\":" + tokenTier) + "}}");
        WidgetWindow w = new WidgetWindow(d, Path.Combine(d, "account.json"), Path.Combine(d, "creds.json"), Base + "usage", 3600, 0, true);
        w.ProfileUrl = Base + "profile";
        w.ShowActivated = false;
        w.Show();
        await Task.Delay(400);
        for (int i = 0; i < 60 && (bool)Get(w, "fetching"); i++) await Task.Delay(50);
        w.GetType().GetMethod("Refresh", Any).Invoke(w, null);
        return w;
    }
    static string Note(WidgetWindow w) { return ((TextBlock)Get(w, "accountNote")).Text; }
    static bool Badge(WidgetWindow w) { return ((UIElement)Get(w, "miniBadge")).Visibility == Visibility.Visible; }

    static async Task Run()
    {
        const string T20 = "\"default_claude_max_20x\"", T5 = "\"default_claude_max_5x\"";
        var cases = new[] {
            new { name = "equal tiers", file = T20, token = T20, want = Red },
            new { name = "different tiers (5x vs 20x)", file = T5, token = T20, want = Amber },
            new { name = "file tier missing", file = (string)null, token = T20, want = Amber },
            new { name = "token tier missing", file = T20, token = (string)null, want = Amber },
            new { name = "both missing", file = (string)null, token = (string)null, want = Amber },
            new { name = "both empty strings", file = "\"\"", token = "\"\"", want = Amber },
            new { name = "case differs", file = "\"DEFAULT_CLAUDE_MAX_20X\"", token = T20, want = Amber },
            new { name = "trailing space in file", file = "\"default_claude_max_20x \"", token = T20, want = Amber },
            new { name = "token tier a number", file = "\"20\"", token = "20", want = Amber },
            new { name = "file tier a number", file = "20", token = "\"20\"", want = Amber },
            new { name = "escaped quote in both", file = "\"a\\\"b\"", token = "\"a\\\"b\"", want = Amber },
            new { name = "10,000-char tier in both", file = "\"" + new string('x', 10000) + "\"", token = "\"" + new string('x', 10000) + "\"", want = Red },
            new { name = "control character in both", file = "\"a\\u0007b\"", token = "\"a\\u0007b\"", want = Amber },
        };
        foreach (var c in cases)
        {
            WidgetWindow w = await Widget(c.file, c.token);
            string note = Note(w);
            string tip = ((TextBlock)Get(w, "accountNote")).ToolTip as string;
            Check("tier: " + c.name + " gives " + (c.want == Red ? "red" : "amber"), note == c.want && Badge(w) && (c.want == Red ? tip == Red : tip == "Couldn't verify the account with Anthropic. Claude Code's file shows another email."), note + " / tip: " + tip);
            w.Close();
        }
        Log("NOTE tier: the comparison is exact (case, spaces and escapes all count), and the file side is read with a regex that stops at the first quote, so an escaped quote never matches the token's parsed value");

        // Verified owner wins over equal tiers.
        profileMode = "owner";
        WidgetWindow v = await Widget(T20, T20);
        Check("verified owner with equal tiers: the mild note, no badge", Note(v) == "Signed in (verified)" && !Badge(v), Note(v));

        // Early check after a token change.
        int before = Count("/profile"), usageBefore = Count("/usage");
        await Task.Delay(11000);
        Check("early check: no extra request in 11 s without a token change", Count("/profile") == before && Count("/usage") == usageBefore, (Count("/usage") - usageBefore) + " usage, " + (Count("/profile") - before) + " profile");
        string creds = Path.Combine(root, "w" + (n - 1), "creds.json");
        File.SetLastWriteTimeUtc(creds, DateTime.UtcNow.AddSeconds(2));
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 25000 && Count("/profile") == before) await Task.Delay(200);
        Check("early check: a token change re-verifies within about 30 s (test gap 0, 10 s tick)", Count("/profile") == before + 1 && Count("/usage") == usageBefore + 1, (sw.ElapsedMilliseconds / 1000.0).ToString("F1") + " s; " + (Count("/usage") - usageBefore) + " usage, " + (Count("/profile") - before) + " profile");
        await Task.Delay(500);
        v.GetType().GetMethod("Refresh", Any).Invoke(v, null);
        Check("early check: verified again after it", Note(v) == "Signed in (verified)", Note(v));
        int after = Count("/usage");
        await Task.Delay(21000);
        Check("early check: no repeat in the next 21 s", Count("/usage") == after, (Count("/usage") - after) + " usage");

        // Test-copy guards.
        Check("test copy: windows open off screen (OffScreenWindows true without ShowTestWindows)", v.OffScreenWindows);
        string runBefore = RunValue();
        MenuItem start = ((ContextMenu)v.ContextMenu).Items.OfType<MenuItem>().FirstOrDefault(m => (m.Header as string) == "Start with Windows");
        if (start != null) start.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check("test copy: Start with Windows is disabled, unticked, and a click leaves the real Run value as it was", start != null && !start.IsEnabled && !start.IsChecked && RunValue() == runBefore, start == null ? "no item" : "enabled=" + start.IsEnabled + " checked=" + start.IsChecked);
        v.Close();
    }
    static string RunValue()
    {
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
        {
            if (k == null) return "no key";
            object val = k.GetValue("ClaudeAccountWidget");
            return val == null ? "absent" : val.ToString();
        }
    }
}
