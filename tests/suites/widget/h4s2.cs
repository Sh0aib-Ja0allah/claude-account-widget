using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
// S2: usage alerts. Notices are recorded through the ShowNotice seam; no real balloon is shown.
static partial class H4
{
    static double Epoch(DateTime utc) { return Math.Floor((utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds); }
    static int updatedCounter = 1000;

    // Writes usage.json the way statusline.js does: a temp file, then a rename.
    static void Usage(string dir, double used, double resets, string who = "sample@example.com")
    {
        string file = Path.Combine(dir, "usage.json");
        string temp = file + ".123.tmp";
        File.WriteAllText(temp, "{\"account\":\"" + who + "\",\"updated\":" + (updatedCounter++) + ",\"five_hour\":null,\"seven_day\":{\"used\":"
            + used.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"resets\":" + resets.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + "}}");
        if (File.Exists(file)) File.Delete(file);
        File.Move(temp, file);
    }

    class Recorder
    {
        public List<string> Titles = new List<string>();
        public List<string> Texts = new List<string>();
        public bool Throw;
        public void Show(string t, string x, Action closed)
        {
            if (Throw) throw new InvalidOperationException("notifier failed");
            Titles.Add(t); Texts.Add(x);
            if (closed != null) closed();
        }
        public string All { get { return string.Join(" | ", Titles); } }
    }

    static WidgetWindow AlertWidget(string dir, Recorder r, bool alertsOn = true, string accountFile = null)
    {
        WidgetWindow w = new WidgetWindow(dir, accountFile ?? account, null, null, 300, 0, true);
        w.alertsOn = alertsOn;
        w.ShowNotice = r.Show;
        return w;
    }

    static void Tick(WidgetWindow w) { Call(w, "Refresh"); }

    static async Task S2()
    {
        double reset = Epoch(DateTime.UtcNow.AddDays(3));
        TimeZoneInfo utc = TimeZoneInfo.Utc;

        // Pure rule
        AlertState.Entry e = null;
        string[] n = UsageAlerts.Evaluate(ref e, 75, 1794049200, 1794000000, utc);
        Check("rule: first sight at 75 gives the 70 notice with the reset time", n != null && n[0] == "Claude usage at 70%" && n[1] == "7-day limit. Resets Sat 11:00 AM.", n == null ? "null" : n[0] + " / " + n[1]);
        e = null; n = UsageAlerts.Evaluate(ref e, 69.4, 1794049200, 1794000000, utc);
        Check("rule: 69.4 rounds to 69, no notice", n == null && !e.Fired70);
        e = null; n = UsageAlerts.Evaluate(ref e, 69.6, 1794049200, 1794000000, utc);
        Check("rule: 69.6 rounds to 70, like the card", n != null && n[0] == "Claude usage at 70%");
        e = null; n = UsageAlerts.Evaluate(ref e, 75, 0, 1794000000, utc);
        Check("rule: no reset time leaves the time out", n != null && n[1] == "7-day limit.", n == null ? "null" : n[1]);
        e = null; n = UsageAlerts.Evaluate(ref e, double.NaN, 1794049200, 1794000000, utc);
        Check("rule: no figure, no entry", n == null && e == null);

        // R-C-01: crossing 70 once; 10-second refreshes never repeat it; the constructor's first refresh can't use it up
        string d = Fresh("s2-c01");
        Usage(d, 75, reset);
        Recorder r = new Recorder();
        WidgetWindow w = new WidgetWindow(d, account, null, null, 300, 0, true);
        w.alertsOn = true;
        Check("c01: nothing before ShowNotice is set", !File.Exists(Path.Combine(d, "alert-state.txt")));
        w.ShowNotice = r.Show;
        Tick(w); Tick(w); Tick(w);
        Check("c01: crossing 70 fires once over repeated refreshes", r.Titles.Count == 1 && r.Titles[0] == "Claude usage at 70%", r.All);
        Usage(d, 76, reset); Tick(w);
        Check("c01: a new file still in the 70s doesn't repeat", r.Titles.Count == 1, r.All);

        // R-C-02: crossing 90 once; jump past both gives only 90
        Usage(d, 92, reset); Tick(w); Usage(d, 95, reset); Tick(w);
        Check("c02: crossing 90 fires once", r.Titles.Count == 2 && r.Titles[1] == "Claude usage at 90%", r.All);
        w.Close();
        d = Fresh("s2-c02b"); r = new Recorder();
        Usage(d, 50, reset); w = AlertWidget(d, r); Tick(w);
        Usage(d, 95, reset); Tick(w);
        Check("c02: a jump from 50 to 95 fires only 90", r.All == "Claude usage at 90%", r.All);
        w.Close();

        // R-C-03: first sight at 95 and at 75
        d = Fresh("s2-c03"); r = new Recorder(); Usage(d, 95, reset); w = AlertWidget(d, r); Tick(w);
        Check("c03: starting at 95 fires one 90 notice", r.All == "Claude usage at 90%", r.All);
        w.Close();
        d = Fresh("s2-c03b"); r = new Recorder(); Usage(d, 75, reset); w = AlertWidget(d, r); Tick(w);
        Check("c03: starting at 75 fires one 70 notice", r.All == "Claude usage at 70%", r.All);
        w.Close();

        // R-C-04: restart in the same period doesn't repeat
        r = new Recorder(); Usage(d, 77, reset); w = AlertWidget(d, r); Tick(w);
        Check("c04: restart in the same period doesn't repeat", r.Titles.Count == 0, r.All);
        string stateText = File.ReadAllText(Path.Combine(d, "alert-state.txt"));
        Check("state: holds a hash, not the email", !stateText.Contains("sample") && System.Text.RegularExpressions.Regex.IsMatch(stateText, "^[0-9a-f]{16},[0-9]+,1,0\\r\\n$"), stateText);

        // R-C-05: a real reset fires one reset notice and re-arms 70 and 90
        double next = reset + 7 * 86400;
        Usage(d, 3, next); Tick(w);
        Check("c05: reset gives one reset notice", r.All == "Claude 7-day limit has reset" && r.Texts[0].StartsWith("Usage is at 3%."), r.All + " / " + string.Join(" | ", r.Texts));
        Usage(d, 4, next); Tick(w);
        Check("c05: no second reset notice", r.Titles.Count == 1);
        Usage(d, 72, next); Tick(w);
        Check("c05: 70 is armed again after the reset", r.Titles.Count == 2 && r.Titles[1] == "Claude usage at 70%", r.All);
        w.Close();

        // R-C-06: under an hour isn't a new period, and a falling percent alone doesn't re-arm
        d = Fresh("s2-c06"); r = new Recorder(); Usage(d, 75, reset); w = AlertWidget(d, r); Tick(w);
        Usage(d, 75, reset + 59 * 60); Tick(w); Usage(d, 75, reset - 59 * 60); Tick(w); Usage(d, 75, reset + 59 * 60); Tick(w);
        Check("c06: reset time moving under an hour either way is not a reset", r.Titles.Count == 1, r.All);
        Usage(d, 40, reset); Tick(w); Usage(d, 75, reset); Tick(w);
        Check("c06: falling to 40% with the same reset doesn't re-arm 70", r.Titles.Count == 1, r.All);
        w.Close();

        // R-C-13: reset time 0
        d = Fresh("s2-c13"); r = new Recorder(); Usage(d, 75, 0); w = AlertWidget(d, r); Tick(w);
        Check("c13: reset 0 at 75 still fires 70 once", r.All == "Claude usage at 70%", r.All);
        Usage(d, 76, reset); Tick(w); Usage(d, 76, 0); Tick(w); Usage(d, 77, reset); Tick(w);
        Check("c13: the time arriving later is the same period", r.Titles.Count == 1, r.All);
        w.Close();

        // R-C-14: a reset and a crossing in one update give one notice
        d = Fresh("s2-c14"); r = new Recorder(); Usage(d, 20, reset); w = AlertWidget(d, r); Tick(w);
        Usage(d, 72, next); Tick(w);
        Check("c14: reset plus crossing gives one combined notice", r.Titles.Count == 1 && r.Titles[0] == "Claude usage at 70%" && r.Texts[0].StartsWith("Your 7-day limit has reset since the last check."), r.All + " / " + string.Join(" | ", r.Texts));
        w.Close();

        // R-C-14: the queue waits for the one before to close, or for the limit
        List<Action> closers = new List<Action>(); List<string> shown = new List<string>();
        NoticeQueue q = new NoticeQueue(delegate(string t, string x, Action c) { shown.Add(t); closers.Add(c); });
        q.Limit = TimeSpan.FromMilliseconds(400);
        q.Enqueue("one", ""); q.Enqueue("two", ""); q.Enqueue("three", "");
        Check("queue: only the first shows", shown.Count == 1 && q.Waiting == 2);
        closers[0]();
        Check("queue: closing shows the next", shown.Count == 2 && shown[1] == "two");
        closers[0]();
        Check("queue: a stale closed signal is ignored", shown.Count == 2);
        await Task.Delay(700);
        Check("queue: the limit moves on without a closed signal", shown.Count == 3 && shown[2] == "three", shown.Count.ToString());
        closers[2](); await Task.Delay(500);
        Check("queue: empty and idle afterwards", !q.Showing && q.Waiting == 0);
        int calls = 0;
        NoticeQueue bad = new NoticeQueue(delegate(string t, string x, Action c) { calls++; if (t == "boom") throw new IOException("no forms"); c(); });
        bad.Enqueue("boom", ""); bad.Enqueue("after", "");
        Check("queue: a throwing notice is skipped and the next shows", calls == 2 && !bad.Showing);

        // R-C-15: a saved file whose reset has passed fires nothing
        d = Fresh("s2-c15"); r = new Recorder(); Usage(d, 85, Epoch(DateTime.UtcNow.AddHours(-2))); w = AlertWidget(d, r); Tick(w);
        Check("c15: stale file fires nothing and saves no state", r.Titles.Count == 0 && !File.Exists(Path.Combine(d, "alert-state.txt")));
        w.Close();

        // R-C-08: another account's figures never alert, and each account has its own state
        d = Fresh("s2-c08"); r = new Recorder();
        string acct2 = Path.Combine(d, "account2.json");
        File.WriteAllText(acct2, "{\"oauthAccount\":{\"emailAddress\":\"sample@example.com\"}}");
        Usage(d, 75, reset, "other@example.com"); w = AlertWidget(d, r, true, acct2); Tick(w);
        Check("c08: another account's file never alerts", r.Titles.Count == 0);
        Usage(d, 75, reset); Tick(w);
        Check("c08: own account alerts", r.All == "Claude usage at 70%", r.All);
        File.WriteAllText(acct2, "{\"oauthAccount\":{\"emailAddress\":\"other@example.com\"}}");
        File.SetLastWriteTimeUtc(acct2, DateTime.UtcNow.AddMinutes(1));
        Usage(d, 75, reset, "other@example.com"); Tick(w);
        Check("c08: the other account has its own state", r.Titles.Count == 2 && File.ReadAllLines(Path.Combine(d, "alert-state.txt")).Length == 2, r.All);
        w.Close();

        // R-C-09: alerts off fires nothing; turning on doesn't replay
        d = Fresh("s2-c09"); r = new Recorder(); Usage(d, 75, reset); w = AlertWidget(d, r, false); Tick(w);
        Check("c09: alerts off, nothing fires", r.Titles.Count == 0);
        SettingsWindow sw = new SettingsWindow(w, d);
        Call(sw, "ShowSwitches");
        System.Windows.Controls.Border alertsTrack = (System.Windows.Controls.Border)Get(sw, "alertsSwitch");
        alertsTrack.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        string[] fields = File.ReadAllText(Path.Combine(d, "settings.txt")).Split(',');
        Check("c09: the Settings switch turns alerts on and saves field 6", w.alertsOn && fields.Length == 9 && fields[6] == "1", string.Join(",", fields));
        Usage(d, 76, reset); Tick(w);
        Check("c09: turning on doesn't replay the old crossing", r.Titles.Count == 0, r.All);
        Usage(d, 91, reset); Tick(w);
        Check("c09: the next crossing fires", r.All == "Claude usage at 90%", r.All);
        alertsTrack.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        Check("c09: the switch turns them off again", !w.alertsOn && File.ReadAllText(Path.Combine(d, "settings.txt")).Split(',')[6] == "0");
        sw.Close(); w.Close();

        // R-C-10: a damaged state file doesn't crash; an unwritable one can't repeat in the session
        d = Fresh("s2-c10"); r = new Recorder();
        File.WriteAllText(Path.Combine(d, "alert-state.txt"), "junk\r\n,,,\r\nzzzzzzzzzzzzzzzz,1,1,1\r\n0123456789abcdef,NaN,1,1\r\n0123456789abcdef,1e400,1,0\r\n" + new string('x', 100000) + "\r\n");
        Usage(d, 75, reset); w = AlertWidget(d, r);
        bool threw = false;
        try { Tick(w); } catch (Exception) { threw = true; }
        Check("c10: damaged state file, no crash, alert still works", !threw && r.All == "Claude usage at 70%", r.All);
        w.Close();
        d = Fresh("s2-c10b"); r = new Recorder();
        Directory.CreateDirectory(Path.Combine(d, "alert-state.txt.widget.tmp"));
        File.WriteAllText(Path.Combine(d, "alert-state.txt"), "");
        File.SetAttributes(Path.Combine(d, "alert-state.txt"), FileAttributes.ReadOnly);
        Usage(d, 75, reset); w = AlertWidget(d, r); Tick(w); Usage(d, 76, reset); Tick(w); Usage(d, 77, reset); Tick(w);
        Check("c10: unwritable state fires once in the session", r.Titles.Count == 1, r.All);
        File.SetAttributes(Path.Combine(d, "alert-state.txt"), FileAttributes.Normal);
        w.Close();

        // R-C-11: a notifier that throws never breaks the widget
        d = Fresh("s2-c11"); r = new Recorder(); r.Throw = true; Usage(d, 75, reset); w = AlertWidget(d, r);
        threw = false;
        try { Tick(w); Usage(d, 95, reset); Tick(w); } catch (Exception ex) { threw = true; Log("THREW " + ex); }
        Check("c11: throwing notifier doesn't break refresh", !threw);
        r.Throw = false; Usage(d, 96, reset); Tick(w);
        Check("c11: state moved on, nothing replayed", r.Titles.Count == 0, r.All);
        w.Close();


        // R-C-07, R-C-12: failed checks change nothing; the status line writing while paused still alerts
        string srv = Fresh("s2-server");
        File.WriteAllText(Path.Combine(srv, "figure.txt"), "75," + reset.ToString("F0") + ",ok");
        Process server = Process.Start(new ProcessStartInfo(fakeExe, "18767 \"" + srv + "\"") { UseShellExecute = false, CreateNoWindow = true });
        try
        {
            await WaitFor(() => File.Exists(Path.Combine(srv, "ready.txt")), 5000);
            string creds = Path.Combine(root, "creds.json");
            long exp = (long)(DateTime.UtcNow.AddDays(30) - new DateTime(1970, 1, 1)).TotalMilliseconds;
            File.WriteAllText(creds, "{\"claudeAiOauth\":{\"accessToken\":\"fake-test-token\",\"expiresAt\":" + exp + "}}");
            d = Fresh("s2-c07"); r = new Recorder();
            w = new WidgetWindow(d, account, creds, "http://127.0.0.1:18767/usage", 300, 0, true);
            w.alertsOn = true; w.ShowNotice = r.Show;
            Call(w, "FetchUsage", false);
            await WaitFor(() => r.Titles.Count > 0, 8000);
            Check("c07: a good check alerts", r.All == "Claude usage at 70%", r.All);
            foreach (string mode in new[] { "429", "401", "500", "badjson", "noseven" })
            {
                File.WriteAllText(Path.Combine(srv, "figure.txt"), "95," + (reset + 7 * 86400).ToString("F0") + "," + mode);
                Set(w, "blockedUntil", DateTime.MinValue);
                Set(w, "rejectedStamp", DateTime.MinValue);
                string before = File.ReadAllText(Path.Combine(d, "usage.json"));
                Call(w, "FetchUsage", false);
                await WaitFor(() => !(bool)Get(w, "fetching"), 8000);
                await Task.Delay(200);
                Check("c07: " + mode + " changes nothing", r.Titles.Count == 1 && File.ReadAllText(Path.Combine(d, "usage.json")) == before, r.All);
            }
            File.WriteAllText(Path.Combine(srv, "figure.txt"), "95," + reset.ToString("F0") + ",429");
            Set(w, "blockedUntil", DateTime.MinValue);
            Call(w, "FetchUsage", false);
            await WaitFor(() => !(bool)Get(w, "fetching"), 8000);
            Check("c12: fetching is paused after 429", (DateTime)Get(w, "blockedUntil") > DateTime.UtcNow);
            Usage(d, 92, reset); Tick(w);
            Check("c12: the status line's new figure still alerts while paused", r.Titles.Count == 2 && r.Titles[1] == "Claude usage at 90%", r.All);
            w.Close();

            // R-X-05: the Program test mode route records notices in test-notices.log without loading System.Windows.Forms
            string pd = Fresh("s2-proc");
            File.WriteAllText(Path.Combine(pd, "settings.txt"), "-6000,0,0,0,default,system,1,1,off");
            File.WriteAllText(Path.Combine(srv, "figure.txt"), "75," + reset.ToString("F0") + ",ok");
            Process p = StartWidget(pd, account, creds, "http://127.0.0.1:18767/usage", "300");
            string log = Path.Combine(pd, "test-notices.log");
            bool got = await WaitFor(() => File.Exists(log), 10000);
            await Task.Delay(500);
            string text = got ? File.ReadAllText(log) : "";
            Check("x05: test mode writes the notice to test-notices.log", text.Contains("\tClaude usage at 70%\t7-day limit. Resets "), text);
            Check("x05: notices hold no email", !text.Contains("@"));
            Check("x05: System.Windows.Forms not loaded in test mode", !p.HasExited && !FormsLoaded(p));
            p.Kill(); p.WaitForExit(5000);
        }
        finally
        {
            try { server.Kill(); } catch (Exception) { }
        }
    }
}
