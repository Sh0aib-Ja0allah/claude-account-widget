using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
// Worker A's independent S2 checks (usage alerts). Off screen only; notices are captured in memory; no real tray, no network.
// usage-alerts.exe <out folder>
static class H7
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static string root, account;
    static int fails, passes;
    static object Get(object o, string f) { return o.GetType().GetField(f, Any).GetValue(o); }
    static void Set(object o, string f, object v) { o.GetType().GetField(f, Any).SetValue(o, v); }
    static void Call(object o, string m) { o.GetType().GetMethod(m, Any, null, Type.EmptyTypes, null).Invoke(o, null); }
    static void Log(string s) { Console.WriteLine(s); Console.Out.Flush(); }
    static void Check(string what, bool ok, string info = "")
    {
        Log((ok ? "PASS " : "FAIL ") + what + (info.Length > 0 ? " [" + info.Replace("\r", "\\r").Replace("\n", "\\n") + "]" : ""));
        if (ok) passes++; else fails++;
    }
    static string Fresh(string name)
    {
        string d = Path.Combine(root, name);
        if (Directory.Exists(d)) { foreach (string f in Directory.GetFiles(d)) File.SetAttributes(f, FileAttributes.Normal); Directory.Delete(d, true); }
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "settings.txt"), "-6000,0,0,0,default,system");
        return d;
    }
    static double Now() { return (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds; }
    static string Title(string[] n) { return n == null ? "none" : n[0] + " / " + n[1]; }
    static long updatedCounter = 1000;
    static void WriteUsage(string d, string email, string used, double resets)
    {
        updatedCounter++;
        File.WriteAllText(Path.Combine(d, "usage.json"), "{\"account\":\"" + email + "\",\"updated\":" + updatedCounter + ",\"five_hour\":{\"used\":10,\"resets\":0},\"seven_day\":{\"used\":" + used + ",\"resets\":" + ((long)resets) + "}}");
    }
    static int unhandled;

    [STAThread]
    static void Main(string[] args)
    {
        root = args[0];
        Directory.CreateDirectory(root);
        account = Path.Combine(root, "account.json");
        File.WriteAllText(account, "{\"oauthAccount\":{\"emailAddress\":\"sample@example.com\",\"planDisplayName\":\"Max\"}}");
        Application app = new Application();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.DispatcherUnhandledException += (s, e) => { Log("UNHANDLED " + e.Exception.GetType().Name + ": " + e.Exception.Message); unhandled++; e.Handled = true; };
        Window host = new Window { Left = -6000, Top = 0, Width = 50, Height = 50, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        host.Loaded += async delegate
        {
            try { Pure(); await Widget(); }
            catch (Exception e) { Log("HARNESS ERROR " + e); fails++; }
            Log("passes=" + passes + " fails=" + fails);
            Process.GetCurrentProcess().Kill();
        };
        host.Show();
        app.Run();
    }

    // ---------- UsageAlerts.Evaluate and AlertState ----------
    static void Pure()
    {
        TimeZoneInfo utc = TimeZoneInfo.Utc;
        double now = Now();
        double r = Math.Floor(now) + 3 * 86400;
        AlertState.Entry e;
        string[] n;

        foreach (double[] c in new[] { new[] { 69.4, 0 }, new[] { 69.5, 70 }, new[] { 68.5, 0 }, new[] { 70.5, 70 }, new[] { 89.4, 70 }, new[] { 89.5, 90 }, new[] { 90.5, 90 }, new[] { 100.0, 90 }, new[] { 250.0, 90 }, new[] { -5.0, 0 } })
        {
            e = null;
            n = UsageAlerts.Evaluate(ref e, c[0], r, now, utc);
            string want = c[1] == 0 ? "none" : "Claude usage at " + c[1] + "%";
            Check("rounding: first sight " + c[0] + " gives " + want, (n == null ? "none" : n[0]) == want, Title(n));
        }
        Log("NOTE rounding: Math.Round is banker's rounding, so 68.5 shows 68 and 69.5 shows 70; the card's \"% left\" uses Math.Round(100 - used) and agrees in every case above");

        e = null;
        n = UsageAlerts.Evaluate(ref e, 95, r, now, utc);
        Check("first sight 95: only the 90 notice, both marked", n != null && n[0] == "Claude usage at 90%" && e.Fired70 && e.Fired90, Title(n));
        Check("first sight: no reset wording", n != null && !n[1].Contains("reset since"), n == null ? "" : n[1]);
        e = null;
        n = UsageAlerts.Evaluate(ref e, 40, r, now, utc);
        Check("first sight 40: no notice, entry created", n == null && e != null && e.Largest == r && !e.Fired70);
        n = UsageAlerts.Evaluate(ref e, 72, r, now, utc);
        Check("same period 72: 70 notice", n != null && n[0] == "Claude usage at 70%", Title(n));
        n = UsageAlerts.Evaluate(ref e, 80, r, now, utc);
        Check("same period 80: nothing again", n == null, Title(n));
        n = UsageAlerts.Evaluate(ref e, 91, r + 1800, now, utc);
        Check("wobble +30 min and 91: 90 notice, no reset", n != null && n[0] == "Claude usage at 90%" && !n[1].Contains("reset since"), Title(n));
        n = UsageAlerts.Evaluate(ref e, 93, r - 3600, now, utc);
        Check("reset moves back an hour: nothing", n == null && e.Largest == r + 1800, Title(n) + " largest " + e.Largest);
        n = UsageAlerts.Evaluate(ref e, 95, 0, now, utc);
        Check("reset 0: nothing, largest kept", n == null && e.Largest == r + 1800, Title(n));
        n = UsageAlerts.Evaluate(ref e, 3, r + 7 * 86400, now, utc);
        Check("new period at 3%: reset notice", n != null && n[0] == "Claude 7-day limit has reset" && n[1].StartsWith("Usage is at 3%."), Title(n));
        Check("new period: flags cleared", !e.Fired70 && !e.Fired90 && e.Largest == r + 7 * 86400);
        n = UsageAlerts.Evaluate(ref e, 3, r + 7 * 86400, now, utc);
        Check("new period again with same figures: nothing", n == null, Title(n));
        n = UsageAlerts.Evaluate(ref e, 75, r + 14 * 86400, now, utc);
        Check("reset plus 75: one combined notice", n != null && n[0] == "Claude usage at 70%" && n[1].StartsWith("Your 7-day limit has reset since the last check."), Title(n));
        n = UsageAlerts.Evaluate(ref e, 96, r + 21 * 86400, now, utc);
        Check("reset plus 96: one combined 90 notice, both marked", n != null && n[0] == "Claude usage at 90%" && n[1].Contains("reset since") && e.Fired70 && e.Fired90, Title(n));

        // creeping reset, each step under an hour
        e = null; UsageAlerts.Evaluate(ref e, 10, r, now, utc);
        int resets = 0;
        for (int i = 1; i <= 6; i++) { n = UsageAlerts.Evaluate(ref e, 10, r + i * 3000, now, utc); if (n != null) resets++; }
        Check("creeping reset 50 min at a time for 5 h: never a new period", resets == 0, resets.ToString());
        Log("NOTE creeping: a reset time that creeps forward in steps under an hour never counts as a new period (largest follows it); fine for a fixed 7-day reset, worth knowing if DL-07 shows a rolling window");

        // stale and odd input
        e = null;
        n = UsageAlerts.Evaluate(ref e, 95, now - 60, now, utc);
        Check("stale file (reset passed): nothing, no entry", n == null && e == null);
        e = null;
        n = UsageAlerts.Evaluate(ref e, double.NaN, r, now, utc);
        Check("no used figure: nothing, no entry", n == null && e == null);
        e = null;
        n = UsageAlerts.Evaluate(ref e, 75, -100, now, utc);
        Check("negative reset: treated like unknown, 70 notice with no time", n != null && n[0] == "Claude usage at 70%" && !n[1].Contains("Resets"), Title(n));
        foreach (double big in new[] { 253402300800.0, 1.8e12, 1e300 })
        {
            e = null; string err = "";
            try { n = UsageAlerts.Evaluate(ref e, 75, big, now, utc); }
            catch (Exception x) { err = x.GetType().Name; }
            Check("reset far in the future (" + big.ToString("G4") + ") doesn't throw", err.Length == 0, err.Length == 0 ? Title(n) : err);
        }

        // reset time text in a real zone
        TimeZoneInfo cairo = null;
        try { cairo = TimeZoneInfo.FindSystemTimeZoneById("Egypt Standard Time"); } catch (Exception) { }
        if (cairo != null)
        {
            e = null;
            double sat11utc = (new DateTime(2026, 11, 14, 11, 0, 0, DateTimeKind.Utc) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            n = UsageAlerts.Evaluate(ref e, 75, sat11utc, sat11utc - 86400, cairo);
            Check("reset time shown in the given zone", n != null && n[1].Contains("Resets Sat 1:00 PM."), Title(n));
        }

        // AlertState file
        string d = Fresh("state");
        string sf = Path.Combine(d, "alert-state.txt");
        string key = AccountKeys.Of("sample@example.com");
        Check("account key: case and spaces don't matter", AccountKeys.Of("  Sample@Example.COM ") == key && key.Length == 16, key);
        File.WriteAllText(sf, "\uFEFF" + key + ",1794049200,1,0\r\n"
            + "ABCDEF0123456789,1,1,1\r\n"            // upper case key
            + "0123456789abcdef,NaN,1,1\r\n"
            + "0123456789abcdee,1e400,1,1\r\n"
            + "0123456789abcded,5,2,0\r\n"
            + "0123456789abcdec,5,1\r\n"
            + "garbage\r\n\r\n"
            + "  fedcba9876543210,-50,0,1  \r\n");
        AlertState st = new AlertState(sf);
        Check("state: BOM-prefixed first line still loads", st.Get(key) != null && st.Get(key).Largest == 1794049200 && st.Get(key).Fired70 && !st.Get(key).Fired90);
        Check("state: damaged lines skipped", st.Get("abcdef0123456789") == null && st.Get("0123456789abcdef") == null && st.Get("0123456789abcdee") == null && st.Get("0123456789abcded") == null && st.Get("0123456789abcdec") == null);
        Check("state: padded line loads, negative largest becomes 0", st.Get("fedcba9876543210") != null && st.Get("fedcba9876543210").Largest == 0 && st.Get("fedcba9876543210").Fired90);
        Check("state: save writes only good lines", st.Save() && File.ReadAllLines(sf).Length == 2, File.ReadAllText(sf));
        File.SetAttributes(sf, FileAttributes.ReadOnly);
        Check("state: read-only file, Save returns false without throwing", !st.Save());
        File.SetAttributes(sf, FileAttributes.Normal);
    }

    // ---------- the widget path ----------
    static async Task Widget()
    {
        string d = Fresh("widget");
        List<string> shown = new List<string>();
        List<Action> closers = new List<Action>();
        WidgetWindow w = new WidgetWindow(d, account, null, null, 300, 0, true);
        w.ShowNotice = delegate(string t, string x, Action c) { shown.Add(t + " | " + x); closers.Add(c); };
        double r = Math.Floor(Now()) + 3 * 86400;
        string sf = Path.Combine(d, "alert-state.txt");
        string key = AccountKeys.Of("sample@example.com");

        WriteUsage(d, "sample@example.com", "75", r); Call(w, "Refresh");
        Check("default off: no notice", shown.Count == 0, string.Join(" ; ", shown));
        Check("default off: state still written with 70 marked", File.Exists(sf) && File.ReadAllText(sf).StartsWith(key + "," + (long)r + ",1,0"), File.Exists(sf) ? File.ReadAllText(sf) : "missing");
        Check("state never holds the email", !File.ReadAllText(sf).Contains("example.com") && !File.ReadAllText(sf).Contains("sample"));
        w.SetAlerts(true);
        Check("turning on saves field 6 at once", File.ReadAllText(Path.Combine(d, "settings.txt")).Split(',')[6] == "1", File.ReadAllText(Path.Combine(d, "settings.txt")));
        WriteUsage(d, "sample@example.com", "76", r); Call(w, "Refresh");
        Check("turning on doesn't replay the 70 crossing", shown.Count == 0, string.Join(" ; ", shown));
        DateTime stamp = File.GetLastWriteTimeUtc(sf);
        await Task.Delay(30);
        WriteUsage(d, "sample@example.com", "77", r); Call(w, "Refresh");
        Check("unchanged state isn't rewritten", File.GetLastWriteTimeUtc(sf) == stamp);
        WriteUsage(d, "sample@example.com", "91", r); Call(w, "Refresh");
        Check("91: one 90 notice", shown.Count == 1 && shown[0].StartsWith("Claude usage at 90%"), string.Join(" ; ", shown));
        Call(w, "Refresh"); Call(w, "Refresh");
        Check("refresh with the same file: no repeat", shown.Count == 1);

        WriteUsage(d, "other@example.com", "99", r + 7 * 86400); Call(w, "Refresh");
        Check("another account's file: no notice, no state line for it", shown.Count == 1 && File.ReadAllLines(sf).Length == 1, File.ReadAllText(sf));
        WriteUsage(d, "Sample@Example.com", "95", r); Call(w, "Refresh");
        Check("same account in another case: no repeat", shown.Count == 1, string.Join(" ; ", shown));

        // queue: the second notice waits for the first to close
        WriteUsage(d, "sample@example.com", "2", r + 7 * 86400); Call(w, "Refresh");
        WriteUsage(d, "sample@example.com", "72", r + 7 * 86400); Call(w, "Refresh");
        Check("queue: reset notice waits behind the open 90 notice", shown.Count == 1, string.Join(" ; ", shown));
        closers[0]();
        Check("queue: closing shows the next one", shown.Count == 2 && shown[1].StartsWith("Claude 7-day limit has reset"), string.Join(" ; ", shown));
        closers[0]();
        Check("queue: a second close signal from the old notice is ignored", shown.Count == 2, string.Join(" ; ", shown));
        closers[1]();
        Check("queue: then the 70 notice", shown.Count == 3 && shown[2].StartsWith("Claude usage at 70%"), string.Join(" ; ", shown));
        closers[2]();

        // restart: a new widget on the same folder repeats nothing
        w.Close();
        List<string> shown2 = new List<string>();
        WidgetWindow w2 = new WidgetWindow(d, account, null, null, 300, 0, true);
        w2.ShowNotice = delegate(string t, string x, Action c) { shown2.Add(t); if (c != null) c(); };
        Call(w2, "Refresh");
        WriteUsage(d, "sample@example.com", "74", r + 7 * 86400); Call(w2, "Refresh");
        Check("restart: nothing repeated", shown2.Count == 0 && w2.alertsOn, string.Join(" ; ", shown2));

        // a notice that throws is skipped and the widget keeps going
        int calls = 0;
        w2.ShowNotice = null;
        Set(w2, "notices", null);
        w2.ShowNotice = delegate(string t, string x, Action c) { calls++; throw new InvalidOperationException("tray broke"); };
        WriteUsage(d, "sample@example.com", "93", r + 7 * 86400);
        int before = unhandled;
        Call(w2, "Refresh");
        Check("throwing notice: skipped, nothing unhandled", calls == 1 && unhandled == before, calls + " calls");
        WriteUsage(d, "sample@example.com", "3", r + 14 * 86400); Call(w2, "Refresh");
        Check("throwing notice: the next one is still tried", calls == 2, calls + " calls");

        // a file with a huge reset time must not take the widget down
        before = unhandled;
        string err = "";
        try { WriteUsage(d, "sample@example.com", "75", 1.8e12); Call(w2, "Refresh"); }
        catch (Exception x) { err = (x.InnerException ?? x).GetType().Name; }
        Check("usage file with a reset in milliseconds (1.8e12) doesn't throw from Refresh", err.Length == 0 && unhandled == before, err);
        w2.Close();

        // Settings switch
        string sd = Fresh("switch");
        WidgetWindow w3 = new WidgetWindow(sd, account, null, null, 300, 0, true);
        SettingsWindow s = new SettingsWindow(w3, sd);
        s.Left = -6000; s.ShowActivated = false; s.ShowInTaskbar = false; s.Show();
        Border sw = (Border)Get(s, "alertsSwitch");
        KeyEventArgs k = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(sw), 0, Key.Space) { RoutedEvent = Keyboard.KeyDownEvent };
        sw.RaiseEvent(k);
        Check("switch: on at once, field 6 = 1, no Save needed", w3.alertsOn && File.ReadAllText(Path.Combine(sd, "settings.txt")).Split(',')[6] == "1", File.ReadAllText(Path.Combine(sd, "settings.txt")));
        k = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(sw), 0, Key.Space) { RoutedEvent = Keyboard.KeyDownEvent };
        sw.RaiseEvent(k);
        Check("switch: off again, field 6 = 0", !w3.alertsOn && File.ReadAllText(Path.Combine(sd, "settings.txt")).Split(',')[6] == "0");
        s.Close(); w3.Close();
    }
}
