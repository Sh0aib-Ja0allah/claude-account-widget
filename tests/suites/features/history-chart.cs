using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
// Worker A's independent S5 checks (usage history window), with extreme and damaged rows. Off screen only.
// history-chart.exe <out folder>
static class H10
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static string root, account, key;
    static int fails, passes, unhandled;
    static object Get(object o, string f) { return o.GetType().GetField(f, Any).GetValue(o); }
    static void Log(string s) { Console.WriteLine(s); Console.Out.Flush(); }
    static void Check(string what, bool ok, string info = "")
    {
        Log((ok ? "PASS " : "FAIL ") + what + (info.Length > 0 ? " [" + info.Replace("\r", "\\r").Replace("\n", "\\n") + "]" : ""));
        if (ok) passes++; else fails++;
    }
    static double Now() { return Math.Floor((DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds); }

    [STAThread]
    static void Main(string[] args)
    {
        root = args[0];
        Directory.CreateDirectory(root);
        account = System.IO.Path.Combine(root, "account.json");
        File.WriteAllText(account, "{\"oauthAccount\":{\"emailAddress\":\"sample@example.com\",\"planDisplayName\":\"Max\"}}");
        key = AccountKeys.Of("sample@example.com");
        Application app = new Application();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.DispatcherUnhandledException += (s, e) => { Log("UNHANDLED " + e.Exception.GetType().Name + ": " + e.Exception.Message); unhandled++; e.Handled = true; };
        string d = System.IO.Path.Combine(root, "w");
        if (Directory.Exists(d)) Directory.Delete(d, true);
        Directory.CreateDirectory(d);
        File.WriteAllText(System.IO.Path.Combine(d, "settings.txt"), "-6000,0,0,0,default,system");
        long reset = (long)Now() + 3 * 86400;
        File.WriteAllText(System.IO.Path.Combine(d, "usage.json"), "{\"account\":\"sample@example.com\",\"updated\":" + (long)(Now() * 1000) + ",\"five_hour\":{\"used\":10,\"resets\":0},\"seven_day\":{\"used\":40,\"resets\":" + reset + "}}");
        WidgetWindow w = new WidgetWindow(d, account, null, null, 300, 0, true);
        w.ShowActivated = false;
        w.Loaded += async delegate
        {
            try { await Run(w, d); }
            catch (Exception e) { Log("HARNESS ERROR " + e); fails++; }
            Log("passes=" + passes + " fails=" + fails);
            Process.GetCurrentProcess().Kill();
        };
        app.Run(w);
    }

    static IEnumerable<DependencyObject> Walk(DependencyObject o)
    {
        yield return o;
        int n = VisualTreeHelper.GetChildrenCount(o);
        for (int i = 0; i < n; i++) foreach (DependencyObject c in Walk(VisualTreeHelper.GetChild(o, i))) yield return c;
    }

    // Every shape and label on the chart canvases has finite coordinates inside the canvas.
    static string BadGeometry(Window h)
    {
        List<string> bad = new List<string>();
        foreach (string name in new[] { "chart", "bars" })
        {
            Canvas c = (Canvas)Get(h, name);
            foreach (UIElement e in c.Children)
            {
                double l = Canvas.GetLeft(e), t = Canvas.GetTop(e);
                if (double.IsInfinity(l) || double.IsInfinity(t)) bad.Add(name + ":" + e.GetType().Name + " at " + l + "," + t);
                Line line = e as Line;
                if (line != null && new[] { line.X1, line.X2, line.Y1, line.Y2 }.Any(v => double.IsNaN(v) || double.IsInfinity(v) || v < -2 || v > c.Width + 2 && (v == line.X1 || v == line.X2) || v > c.Height + 2 && (v == line.Y1 || v == line.Y2)))
                    bad.Add(name + ":Line " + line.X1 + "," + line.Y1 + "-" + line.X2 + "," + line.Y2);
                Polyline poly = e as Polyline;
                if (poly != null && poly.Points.Any(p => double.IsNaN(p.X) || double.IsNaN(p.Y) || double.IsInfinity(p.X) || double.IsInfinity(p.Y) || p.X < -2 || p.X > c.Width + 2 || p.Y < -2 || p.Y > c.Height + 2))
                    bad.Add(name + ":Polyline " + string.Join(" ", poly.Points.Take(6).Select(p => p.X.ToString("G4") + "," + p.Y.ToString("G4"))));
                Rectangle rect = e as Rectangle;
                if (rect != null && (double.IsNaN(rect.Height) || rect.Height < 0 || rect.Height > c.Height + 2 || double.IsNaN(l) || l < -2 || l > c.Width + 2))
                    bad.Add(name + ":Rectangle h=" + rect.Height + " at " + l);
            }
        }
        return bad.Count == 0 ? "" : string.Join(" ; ", bad.Take(6));
    }

    static string Texts(Window h)
    {
        return string.Join(" | ", Walk(h).OfType<TextBlock>().Where(t => t.IsVisible && !string.IsNullOrEmpty(t.Text)).Select(t => t.Text).Take(30));
    }

    static async Task<Window> Open(WidgetWindow w, string d, string csv)
    {
        File.WriteAllText(System.IO.Path.Combine(d, "usage-history.csv"), csv);
        UsageHistoryWindow h = new UsageHistoryWindow(w, System.IO.Path.Combine(d, "usage-history.csv"));
        h.Left = -6000; h.Top = 0; h.ShowActivated = false; h.ShowInTaskbar = false;
        h.Show();
        h.UpdateLayout();
        await Task.Delay(50);
        return h;
    }

    static string Row(double time, string percent, double reset, string k)
    {
        return time.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," + percent + "," + reset.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," + k + "\r\n";
    }

    static string PeriodsCsv(double now, double prev)
    {
        StringBuilder sb = new StringBuilder();
        string[] peaks = { "69.5", "89.5", "100", "12", "70.4", "90.4", "0", "55" };
        for (int p = 11; p >= 1; p--)
        {
            double r = prev - (p - 1) * 7 * 86400;
            if (now - (r - 3600) > 90 * 86400) continue;
            sb.Append(Row(r - 3 * 86400, "5", r, key));
            sb.Append(Row(r - 3600, peaks[p % peaks.Length], r, key));
        }
        return sb.ToString();
    }

    static async Task Run(WidgetWindow w, string d)
    {
        double now = Now();
        double reset = now + 3 * 86400;
        double prev = reset - 7 * 86400;
        StringBuilder normal = new StringBuilder();
        for (int i = 0; i < 40; i++) normal.Append(Row(now - 4 * 86400 + i * 8000, (i * 1.1).ToString(System.Globalization.CultureInfo.InvariantCulture), reset, key));

        var cases = new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>("empty file", ""),
            new KeyValuePair<string, string>("only damaged lines", "garbage\r\n1,2\r\n,,,\r\n"),
            new KeyValuePair<string, string>("normal week", normal.ToString()),
            new KeyValuePair<string, string>("one row", Row(now - 3600, "12", reset, key)),
            new KeyValuePair<string, string>("time 1e300", normal + Row(1e300, "50", reset, key)),
            new KeyValuePair<string, string>("time 1.79e14 (torn merge)", normal + Row(179401794048501, "47", reset, key)),
            new KeyValuePair<string, string>("time 10 years ahead", normal + Row(now + 3650 * 86400.0, "50", reset, key)),
            new KeyValuePair<string, string>("time 1 (1970)", Row(1, "50", reset, key) + normal),
            new KeyValuePair<string, string>("reset 1e300", normal + Row(now - 60, "50", 1e300, key)),
            new KeyValuePair<string, string>("reset 1.8e12 (ms)", normal + Row(now - 60, "50", 1.8e12, key)),
            new KeyValuePair<string, string>("reset -7", normal + Row(now - 60, "50", -7, key)),
            new KeyValuePair<string, string>("all resets 0", Row(now - 7200, "10", 0, key) + Row(now - 3600, "20", 0, key)),
            new KeyValuePair<string, string>("percent -5 and 250", normal + Row(now - 120, "-5", reset, key) + Row(now - 60, "250", reset, key)),
            new KeyValuePair<string, string>("percent 1e300", normal + Row(now - 60, "1e300", reset, key)),
            new KeyValuePair<string, string>("two rows at the same second", Row(now - 60, "10", reset, key) + Row(now - 60, "90", reset, key)),
            new KeyValuePair<string, string>("only another account", Row(now - 60, "50", reset, "fedcba9876543210")),
            new KeyValuePair<string, string>("earlier periods incl. odd peak", Row(prev - 7 * 86400 - 100, "300", prev - 7 * 86400, key) + Row(prev - 100, "64", prev, key) + normal),
            new KeyValuePair<string, string>("two resets 2 h apart", Row(now - 9000, "30", reset, key) + Row(now - 5000, "35", reset + 7200, key) + Row(now - 60, "36", reset + 7200, key)),
            new KeyValuePair<string, string>("reset moving back a week mid-period", Row(now - 9000, "30", reset, key) + Row(now - 5000, "35", reset - 7 * 86400, key) + Row(now - 60, "37", reset, key)),
            new KeyValuePair<string, string>("rows from the far end of 91 days", Row(now - 90.9 * 86400, "70", now - 90.9 * 86400 + 3 * 86400, key) + Row(now - 91 * 86400 - 5, "99", now - 88 * 86400, key) + normal),
            new KeyValuePair<string, string>("row 23 h ahead", normal + Row(now + 23 * 3600, "45", reset, key)),
            new KeyValuePair<string, string>("reset 0 rows inside the period", Row(now - 9000, "30", reset, key) + Row(now - 5000, "33", 0, key) + Row(now - 60, "36", reset, key)),
            new KeyValuePair<string, string>("every period over (last reset passed)", Row(now - 3 * 86400, "50", now - 86400, key) + Row(now - 2 * 86400, "60", now - 86400, key)),
            new KeyValuePair<string, string>("reset exactly a week after the row", Row(now - 60, "20", now - 60 + 7 * 86400, key)),
            new KeyValuePair<string, string>("12 earlier periods at 69.5, 89.5, 100", PeriodsCsv(now, prev)),            new KeyValuePair<string, string>("rows after the reset passed", Row(now - 600, "80", now - 300, key) + Row(now - 60, "2", now + 7 * 86400 - 300, key)),
        };
        foreach (var c in cases)
        {
            int before = unhandled; string err = ""; Window h = null;
            try { h = await Open(w, d, c.Value); }
            catch (Exception e) { err = (e.InnerException ?? e).GetType().Name + ": " + (e.InnerException ?? e).Message; }
            Check("history: " + c.Key + ": opens without an exception", err.Length == 0 && unhandled == before, err);
            if (h != null)
            {
                string bad = BadGeometry(h);
                Check("history: " + c.Key + ": everything drawn inside the chart", bad.Length == 0, bad);
                Log("NOTE " + c.Key + ": " + Texts(h));
                h.Close();
            }
        }

        // 30,000 rows: opening time
        StringBuilder big = new StringBuilder();
        for (int i = 0; i < 30000; i++) big.Append(Row(now - 89 * 86400 + i * 256, ((i % 700) / 7.0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), Math.Floor((now - 89 * 86400 + i * 256 - prev) / (7 * 86400)) * 7 * 86400 + prev + 7 * 86400, key));
        Stopwatch sw = Stopwatch.StartNew();
        Window hb = await Open(w, d, big.ToString());
        sw.Stop();
        Check("history: 30,000 rows open in under 2 s", sw.ElapsedMilliseconds < 2000, sw.ElapsedMilliseconds + " ms");
        Check("history: 30,000 rows drawn inside the chart", BadGeometry(hb).Length == 0, BadGeometry(hb));
        int shapes = ((Canvas)Get(hb, "chart")).Children.Count + ((Canvas)Get(hb, "bars")).Children.Count;
        Log("NOTE 30,000 rows: " + shapes + " elements on the two canvases");
        hb.Close();
    }
}
