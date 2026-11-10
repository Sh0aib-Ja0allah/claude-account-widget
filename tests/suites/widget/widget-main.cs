using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
// Tests for the new widget features (slices S0 onwards). Every window stays off screen; no real input, notice,
// shortcut or network request. Process tests run the scratch -OutDir build against a local fake server.
static partial class H4
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static string root, exe, fakeExe, account;
    static int fails, passes;
    static WidgetWindow host;
    static object Get(object o, string f) { return o.GetType().GetField(f, Any).GetValue(o); }
    static void Set(object o, string f, object v) { o.GetType().GetField(f, Any).SetValue(o, v); }
    static object Call(object o, string m, params object[] a)
    {
        MethodInfo info = o.GetType().GetMethods(Any).First(x => x.Name == m && x.GetParameters().Length == a.Length);
        return info.Invoke(o, a);
    }
    static void Log(string s) { Console.WriteLine(s); Console.Out.Flush(); }
    static void Check(string what, bool ok, string info = "")
    {
        Log((ok ? "PASS " : "FAIL ") + what + (info.Length > 0 ? " [" + info.Replace("\r", "\\r").Replace("\n", "\\n") + "]" : ""));
        if (ok) passes++; else fails++;
    }
    static void Note(string s) { Log("NOTE " + s); }
    static string Fresh(string name)
    {
        string d = Path.Combine(root, name);
        if (Directory.Exists(d)) Directory.Delete(d, true);
        Directory.CreateDirectory(d);
        return d;
    }
    static WidgetWindow NewWidget(string data, bool testMode)
    {
        return new WidgetWindow(data, account, null, null, 300, 0, testMode);
    }

    [STAThread]
    static void Main(string[] args)
    {
        root = args[1];
        exe = args.Length > 2 ? args[2] : null;
        fakeExe = args.Length > 3 ? args[3] : null;
        Directory.CreateDirectory(root);
        account = Path.Combine(root, "account.json");
        File.WriteAllText(account, "{\"oauthAccount\":{\"emailAddress\":\"sample@example.com\",\"planDisplayName\":\"Max\"}}");
        Application app = new Application();
        app.ShutdownMode = ShutdownMode.OnMainWindowClose;
        app.DispatcherUnhandledException += (s, e) => { Log("UNHANDLED " + e.Exception); fails++; e.Handled = true; };
        string hostData = Fresh("host");
        File.WriteAllText(Path.Combine(hostData, "settings.txt"), "-6000,0,0,0,default,system");
        host = NewWidget(hostData, true);
        host.ShowActivated = false;
        host.Loaded += async delegate
        {
            try { await Run(args[0]); }
            catch (Exception e) { Log("HARNESS ERROR " + e); fails++; }
            Log("passes=" + passes + " fails=" + fails);
            host.Close();
        };
        app.Run(host);
        Process.GetCurrentProcess().Kill();
    }

    static async Task Run(string mode)
    {
        if (mode == "png") { await Png(); return; }
        string[] sections = { "s0", "s0proc", "s1", "s2", "s3", "fixes", "s4", "s5", "fixes2", "s6", "fixes3", "s7", "fixes4", "fixes5", "fixes6", "acct", "live", "layout", "chatw", "switch", "notes", "c3d", "bypass" };
        foreach (string section in sections)
        {
            if (mode != "all" && mode != section) continue;
            Log("=== " + section);
            try
            {
                if (section == "s0") await S0();
                if (section == "s0proc") await S0Process();
                if (section == "s1") await S1();
                if (section == "s2") await S2();
                if (section == "s3") await S3();
                if (section == "fixes") await Fixes();
                if (section == "s4") await S4();
                if (section == "s5") await S5();
                if (section == "fixes2") await Fixes2();
                if (section == "s6") await S6();
                if (section == "fixes3") await Fixes3();
                if (section == "s7") await S7();
                if (section == "fixes4") await Fixes4();
                if (section == "fixes5") await Fixes5();
                if (section == "fixes6") await Fixes6();
                if (section == "acct") await Acct();
                if (section == "live") await Live();
                if (section == "layout") await Layout();
                if (section == "chatw") await ChatWidth();
                if (section == "switch") await Switches();
                if (section == "notes") await Notes();
                if (section == "c3d") await SidebarMenu();
                if (section == "bypass") { await BypassSwitch(); await BypassChip(); }
            }
            catch (Exception e) { Log("SECTION ERROR " + section + " " + e); fails++; }
        }
    }

    // ---------- S0: shared pieces ----------
    static async Task S0()
    {
        // R-0-01 atomic write
        string d = Fresh("atomic");
        string a = Path.Combine(d, "a.txt"), b = Path.Combine(d, "b.txt");
        string text = "12.5,30,1,0,default,system \u00e9\u0645";
        File.WriteAllText(a, text);
        WidgetFiles.WriteAtomic(b, text);
        Check("atomic: new file same bytes as WriteAllText", File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b)));
        Check("atomic: no byte order mark", File.ReadAllBytes(b)[0] == (byte)'1');
        WidgetFiles.WriteAtomic(b, "second");
        Check("atomic: replace existing", File.ReadAllText(b) == "second");
        Check("atomic: no temp left", !File.Exists(b + ".widget.tmp"));
        Directory.CreateDirectory(b + ".widget.tmp"); // the temp write now fails part way
        bool threw = false;
        try { WidgetFiles.WriteAtomic(b, "third"); } catch (Exception) { threw = true; }
        Check("atomic: interrupted write throws", threw);
        Check("atomic: interrupted write keeps old file", File.ReadAllText(b) == "second");
        Directory.Delete(b + ".widget.tmp");
        File.SetAttributes(b, FileAttributes.ReadOnly);
        threw = false;
        try { WidgetFiles.WriteAtomic(b, "fourth"); } catch (UnauthorizedAccessException) { threw = true; } catch (IOException) { threw = true; }
        Check("atomic: read-only target throws a caught type and keeps the file", threw && File.ReadAllText(b) == "second");
        Check("atomic: read-only failure leaves no temp", !File.Exists(b + ".widget.tmp"));
        File.SetAttributes(b, FileAttributes.Normal);

        // R-0-06 period rule
        double r = 1794049200;
        Check("period: forward 7 days is new", UsagePeriod.IsNew(r, r + 7 * 86400));
        Check("period: forward exactly 1 hour is new", UsagePeriod.IsNew(r, r + 3600));
        Check("period: forward 59m59s is not", !UsagePeriod.IsNew(r, r + 3599));
        Check("period: same is not", !UsagePeriod.IsNew(r, r));
        Check("period: backward 7 days is not", !UsagePeriod.IsNew(r, r - 7 * 86400));
        Check("period: new 0 is not", !UsagePeriod.IsNew(r, 0));
        Check("period: largest 0 (first sight) is not", !UsagePeriod.IsNew(0, r));
        Check("period: NaN is not", !UsagePeriod.IsNew(double.NaN, r) && !UsagePeriod.IsNew(r, double.NaN));
        double largest = 0; int news = 0;
        foreach (double v in new[] { r, r + 7200, r, r + 7200, r, r + 7200 })
        {
            if (UsagePeriod.IsNew(largest, v)) news++;
            largest = Math.Max(largest, v);
        }
        Check("period: alternating A, B, A counts once", news == 1, news.ToString());

        // R-0-04 settings fields 6 to 8
        string sd = Fresh("settings");
        string sf = Path.Combine(sd, "settings.txt");
        File.WriteAllText(sf, "100,120,1,0,blue,dark");
        WidgetWindow w = NewWidget(sd, true);
        Check("settings: old 6-field file loads", (string)Get(w, "accentName") == "blue" && (string)Get(w, "themeSetting") == "dark" && w.Left == 100 && w.Top == 120);
        Check("settings: defaults alerts off, update on, shortcut off", !w.alertsOn && w.updateCheckOn && w.shortcutSetting == "off");
        w.alertsOn = true; w.updateCheckOn = false; w.shortcutSetting = "Ctrl+Alt+Shift+C";
        w.SaveSettings();
        Check("settings: 9 fields written", File.ReadAllText(sf) == "100,120,1,0,blue,dark,1,0,Ctrl+Alt+Shift+C", File.ReadAllText(sf));
        w.Close();
        WidgetWindow w2 = NewWidget(sd, true);
        Check("settings: round trip", w2.alertsOn && !w2.updateCheckOn && w2.shortcutSetting == "Ctrl+Alt+Shift+C" && (string)Get(w2, "accentName") == "blue");
        w2.Close();
        File.WriteAllText(sf, "100,120,1,0,blue,dark,x,y,");
        WidgetWindow w3 = NewWidget(sd, true);
        Check("settings: odd values give defaults", !w3.alertsOn && w3.updateCheckOn && w3.shortcutSetting == "off");
        w3.Close();
        File.WriteAllText(sf, "garbage");
        WidgetWindow w4 = NewWidget(sd, true);
        Check("settings: unreadable file gives defaults", !w4.alertsOn && w4.updateCheckOn && w4.shortcutSetting == "off" && w4.Topmost);
        w4.Close();

        // R-0-03 test mode positions
        File.WriteAllText(sf, "-6000,0,0,0,default,system");
        WidgetWindow tm = NewWidget(sd, true);
        Check("test mode: saved off-screen position kept", tm.Left == -6000 && tm.Top == 0, tm.Left + "," + tm.Top);
        tm.Close();
        WidgetWindow real = NewWidget(sd, false);
        Check("normal mode: off-screen position still clamped", real.Left > -6000, real.Left.ToString());
        real.Close();
        Check("test mode: host shown off screen, not active", host.Left == -6000 && !host.IsActive);
        Call(host, "OpenChatWindow");
        await Task.Delay(300);
        ChatWindow chat = Application.Current.Windows.OfType<ChatWindow>().LastOrDefault();
        IntPtr chatHandle = chat == null ? IntPtr.Zero : new System.Windows.Interop.WindowInteropHelper(chat).Handle;
        Check("test mode: chat opened from the widget is off screen, shown unactivated, not the foreground window",
            chat != null && chat.Left == -6000 && !chat.ShowActivated && GetForegroundWindow() != chatHandle,
            chat == null ? "none" : chat.Left + " " + chat.ShowActivated);
        if (chat != null) Note("chat IsActive=" + chat.IsActive + ": ChatWindow's own Loaded handler focuses its input (widget.cs ChatWindow ctor), which activates it inside the app; it is not the foreground window");
        if (chat != null) chat.Close();

        // 0-02 seams null by default
        WidgetWindow plain = NewWidget(Fresh("plain"), false);
        Check("seams: null by default", plain.ShowNotice == null && plain.ShortcutRegistrar == null && plain.ReleasesUrl == null);
        plain.Close();

        // 0-07 OnUsageUpdated waits for ShowNotice, fires once per new file, only for the signed-in account
        string ud = Fresh("updated");
        string uf = Path.Combine(ud, "usage.json");
        File.WriteAllText(uf, "{\"account\":\"sample@example.com\",\"updated\":1000,\"five_hour\":null,\"seven_day\":{\"used\":40,\"resets\":1794049200}}");
        WidgetWindow uw = NewWidget(ud, true);
        Check("hook: no ShowNotice, nothing recorded", double.IsNaN((double)Get(uw, "lastUpdatedSeen")));
        uw.ShowNotice = delegate(string t, string x, Action c) { };
        Call(uw, "Refresh");
        Check("hook: first refresh with ShowNotice sees the file", (double)Get(uw, "lastUpdatedSeen") == 1000);
        File.WriteAllText(uf, "{\"account\":\"other@example.com\",\"updated\":2000,\"five_hour\":null,\"seven_day\":{\"used\":40,\"resets\":1794049200}}");
        Call(uw, "Refresh");
        Check("hook: another account's file is ignored", (double)Get(uw, "lastUpdatedSeen") == 1000);
        File.WriteAllText(uf, "{\"account\":\"sample@example.com\",\"updated\":3000,\"five_hour\":null,\"seven_day\":{\"used\":41,\"resets\":1794049200}}");
        Call(uw, "Refresh");
        Check("hook: a new file for the account is seen", (double)Get(uw, "lastUpdatedSeen") == 3000);
        uw.Close();

        // TestNotices writer
        string log = Path.Combine(Fresh("notices"), "test-notices.log");
        bool closed = false;
        TestNotices.Writer(log)("Title", "Body", delegate { closed = true; });
        string line = File.ReadAllText(log);
        Check("test notices: line written and closed called", closed && line.EndsWith("\tTitle\tBody\r\n"), line);

        // R-0-05 one version
        Check("version: one valid SemVer constant", VersionTag.Parse(Program.AppVersion) != null && !Program.AppVersion.StartsWith("v"), Program.AppVersion);
    }

    // ---------- S0: Program forms, in separate processes ----------
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    struct RECT { public int Left, Top, Right, Bottom; }

    static List<RECT> VisibleWindows(int pid, out bool foreground)
    {
        List<RECT> list = new List<RECT>();
        IntPtr fg = GetForegroundWindow();
        bool isFg = false;
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == pid && IsWindowVisible(h))
            {
                RECT r; GetWindowRect(h, out r); list.Add(r);
                if (h == fg) isFg = true;
            }
            return true;
        }, IntPtr.Zero);
        foreground = isFg;
        return list;
    }

    static Process StartWidget(params string[] a)
    {
        ProcessStartInfo info = new ProcessStartInfo(exe, string.Join(" ", a.Select(x => "\"" + x + "\"")));
        info.UseShellExecute = false;
        return Process.Start(info);
    }

    static async Task<bool> WaitFor(Func<bool> f, int ms)
    {
        for (int i = 0; i < ms / 100; i++) { if (f()) return true; await Task.Delay(100); }
        return f();
    }

    static bool FormsLoaded(Process p)
    {
        p.Refresh();
        foreach (ProcessModule m in p.Modules)
            if (m.ModuleName.StartsWith("System.Windows.Forms", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static async Task S0Process()
    {
        if (exe == null || !File.Exists(exe)) { Check("process: build present", false, exe ?? "none"); return; }
        string srv = Fresh("server");
        long reset = (long)(DateTime.UtcNow.AddDays(3) - new DateTime(1970, 1, 1)).TotalSeconds;
        File.WriteAllText(Path.Combine(srv, "figure.txt"), "42," + reset);
        Process server = Process.Start(new ProcessStartInfo(fakeExe, TestPorts.Get(18765) + " \"" + srv + "\"") { UseShellExecute = false, CreateNoWindow = true });
        await WaitFor(() => File.Exists(Path.Combine(srv, "ready.txt")), 5000);
        string creds = Path.Combine(root, "creds.json");
        long exp = (long)(DateTime.UtcNow.AddDays(30) - new DateTime(1970, 1, 1)).TotalMilliseconds;
        File.WriteAllText(creds, "{\"claudeAiOauth\":{\"accessToken\":\"fake-test-token\",\"expiresAt\":" + exp + ",\"subscriptionType\":\"max\"}}");
        string url = TestPorts.Base(18765) + "/usage";
        try
        {
            // 2-argument form: off screen, not in front, no fetching.
            string d2 = Fresh("p2arg");
            File.WriteAllText(Path.Combine(d2, "settings.txt"), "-6000,0,0,0,default,system");
            Process p = StartWidget(d2, account);
            bool fg = false; List<RECT> rects = null;
            bool seen = await WaitFor(delegate { rects = VisibleWindows(p.Id, out fg); return rects.Count > 0; }, 8000);
            await Task.Delay(1500);
            rects = VisibleWindows(p.Id, out fg);
            Check("2-arg: window shown", seen && !p.HasExited);
            Check("2-arg: every window off screen", rects.Count > 0 && rects.All(x => x.Right < SystemParameters.VirtualScreenLeft), string.Join(";", rects.Select(x => x.Left + "," + x.Top)));
            Check("2-arg: not the foreground window", !fg);
            Check("2-arg: no usage file, no request", !File.Exists(Path.Combine(d2, "usage.json")) && !File.Exists(Path.Combine(srv, "requests.log")));
            Check("2-arg: System.Windows.Forms not loaded", !FormsLoaded(p));
            p.Kill(); p.WaitForExit(5000);

            // 5-argument form: fetches from the fake server with the version in the User-Agent.
            foreach (int count in new[] { 5, 6 })
            {
                string d5 = Fresh("p" + count + "arg");
                File.WriteAllText(Path.Combine(d5, "settings.txt"), "-6000,0,0,0,default,system");
                if (File.Exists(Path.Combine(srv, "requests.log"))) File.Delete(Path.Combine(srv, "requests.log"));
                Process q = count == 5 ? StartWidget(d5, account, creds, url, "300") : StartWidget(d5, account, creds, url, "300", "");
                bool got = await WaitFor(() => File.Exists(Path.Combine(d5, "usage.json")), 10000);
                string usage = got ? File.ReadAllText(Path.Combine(d5, "usage.json")) : "";
                Check(count + "-arg: usage file written from the fake server", usage.Contains("\"seven_day\":{\"used\":42,\"resets\":" + reset + "}"), usage);
                string req = File.Exists(Path.Combine(srv, "requests.log")) ? File.ReadAllText(Path.Combine(srv, "requests.log")) : "";
                Check(count + "-arg: User-Agent carries the version", req.Contains("\tClaudeAccountWidget/" + Program.AppVersion + "\t"), req);
                bool f2; List<RECT> r2 = VisibleWindows(q.Id, out f2);
                Check(count + "-arg: off screen and not in front", r2.Count > 0 && r2.All(x => x.Right < SystemParameters.VirtualScreenLeft) && !f2);
                Check(count + "-arg: System.Windows.Forms not loaded", !FormsLoaded(q));
                Check(count + "-arg: no notice log yet", !File.Exists(Path.Combine(d5, "test-notices.log")));
                q.Kill(); q.WaitForExit(5000);
            }
        }
        finally
        {
            try { server.Kill(); } catch (Exception) { }
        }
    }
}
