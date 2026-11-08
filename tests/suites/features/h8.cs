using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
// Worker A's independent S3 checks (limit forecast). Off screen only.
// h8.exe <out folder>
static class H8
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static string root, account;
    static int fails, passes;
    static object Get(object o, string f) { return o.GetType().GetField(f, Any).GetValue(o); }
    static void Call(object o, string m) { o.GetType().GetMethod(m, Any, null, Type.EmptyTypes, null).Invoke(o, null); }
    static void Log(string s) { Console.WriteLine(s); Console.Out.Flush(); }
    static void Check(string what, bool ok, string info = "")
    {
        Log((ok ? "PASS " : "FAIL ") + what + (info.Length > 0 ? " [" + info + "]" : ""));
        if (ok) passes++; else fails++;
    }
    static string F(double used, double elapsedHours, TimeZoneInfo zone = null)
    {
        DateTime reset = new DateTime(2026, 11, 18, 11, 0, 0, DateTimeKind.Utc);
        DateTime now = reset.AddDays(-7).AddHours(elapsedHours);
        return WidgetWindow.ForecastText(used, reset, now, zone ?? TimeZoneInfo.Utc) ?? "hidden";
    }

    [STAThread]
    static void Main(string[] args)
    {
        root = args[0];
        Directory.CreateDirectory(root);
        account = Path.Combine(root, "account.json");
        File.WriteAllText(account, "{\"oauthAccount\":{\"emailAddress\":\"sample@example.com\",\"planDisplayName\":\"Max\"}}");
        Application app = new Application();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.DispatcherUnhandledException += (s, e) => { Log("UNHANDLED " + e.Exception); fails++; e.Handled = true; };
        string d = Path.Combine(root, "w");
        if (Directory.Exists(d)) Directory.Delete(d, true);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "settings.txt"), "-6000,0,0,0,default,system");
        WidgetWindow w = new WidgetWindow(d, account, null, null, 300, 0, true);
        w.ShowActivated = false;
        w.Loaded += async delegate
        {
            try { Table(); await Card(w, d); }
            catch (Exception e) { Log("HARNESS ERROR " + e); fails++; }
            Log("passes=" + passes + " fails=" + fails);
            Process.GetCurrentProcess().Kill();
        };
        app.Run(w);
    }

    static void Table()
    {
        // Rules from the top. Elapsed hours since the window started (reset minus 7 days).
        Check("NaN used: hidden", F(double.NaN, 100) == "hidden");
        DateTime reset = new DateTime(2026, 11, 18, 11, 0, 0, DateTimeKind.Utc);
        Check("reset passed: hidden", WidgetWindow.ForecastText(50, reset, reset, TimeZoneInfo.Utc) == null);
        Check("100: Limit reached", F(100, 30) == "Limit reached");
        Check("150: Limit reached", F(150, 30) == "Limit reached");
        Check("99.5 rounds to 100 like the card: Limit reached", F(99.5, 160) == "Limit reached", F(99.5, 160));
        Check("100 in the first hour still says Limit reached", F(100, 0.5) == "Limit reached", F(100, 0.5));
        Check("11.9 h in: hidden", F(50, 11.9) == "hidden", F(50, 11.9));
        Check("exactly 12 h in: shown", F(50, 12) != "hidden", F(50, 12));
        Check("4.9%: hidden", F(4.9, 100) == "hidden", F(4.9, 100));
        Check("5%: shown", F(5, 100) != "hidden", F(5, 100));
        Check("halfway at 49%: Lasts until reset", F(49, 84) == "Lasts until reset", F(49, 84));
        Check("halfway at exactly 50%: full exactly at reset counts as lasting", F(50, 84) == "Lasts until reset", F(50, 84));
        Check("halfway at 51%: Full by (more than a day away)", F(51, 84).StartsWith("Full by "), F(51, 84));
        // 99% after 160 h: 1/(99/160) = 1.6162 h = 1 h 36.97 m
        Check("160 h at 99%: Full in 1h 36m (minutes rounded down)", F(99, 160) == "Full in 1h 36m", F(99, 160));
        // 99.4% after 160 h: 0.6/(99.4/160) = 0.96579 h = 57.95 m
        Check("160 h at 99.4%: Full in 58m (minutes rounded up)", F(99.4, 160) == "Full in 58m", F(99.4, 160));
        // 59.7 minutes left: u = 16000/160.995
        double u = 16000 / (160 + 59.7 / 60);
        string at597 = F(u, 160);
        Check("59.7 min left doesn't say 60m", at597 != "Full in 60m", at597);
        // under a minute
        double u2 = 16000 / (160 + 0.4 / 60);
        Check("24 s left: Full in 1m (Math.Round keeps it under 100)", F(u2, 160) == "Full in 1m" || F(u2, 160) == "Limit reached", F(u2, 160) + " at " + u2.ToString("R"));
        // exactly 24 h left: 168-elapsed... used u with hours=24: (100-u)*e/u = 24 at e = 100 -> u = 10000/124
        Check("exactly 24 h left: Full by", F(10000.0 / 124, 100).StartsWith("Full by"), F(10000.0 / 124, 100));
        Check("just under 24 h left: Full in 23h 59m", F(10000.0 / 123.99, 100) == "Full in 23h 59m", F(10000.0 / 123.99, 100));
        // reset more than 7 days ahead (allowed up to 60 by ResetTimes): elapsed negative, hidden
        Check("reset 30 days ahead: hidden", WidgetWindow.ForecastText(50, reset.AddDays(23), reset.AddDays(-7).AddHours(50), TimeZoneInfo.Utc) == null);
        // a real zone: Cairo is UTC+2 in November 2026 (no DST then)
        TimeZoneInfo cairo = null;
        try { cairo = TimeZoneInfo.FindSystemTimeZoneById("Egypt Standard Time"); } catch (Exception) { }
        if (cairo != null)
        {
            // 60% after 84 h: 40/(60/84) = 56 h from now. now = Nov 14 23:00 UTC; full = Nov 17 07:00 UTC = Tue 9:00 AM Cairo
            Check("Cairo: Full by Tue 9:00 AM", F(60, 84, cairo) == "Full by Tue 9:00 AM", F(60, 84, cairo));
        }
    }

    static async Task Card(WidgetWindow w, string d)
    {
        TextBlock forecast = (TextBlock)Get(w, "forecastText");
        long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        Func<long> nowMs = delegate { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; };
        string updatedOverride = null;
        Action<string, long> write = delegate(string used, long resets)
        {
            File.WriteAllText(Path.Combine(d, "usage.json"), "{\"account\":\"sample@example.com\",\"updated\":" + (updatedOverride ?? nowMs().ToString()) + ",\"five_hour\":{\"used\":10,\"resets\":0},\"seven_day\":{\"used\":" + used + ",\"resets\":" + resets + "}}");
            Call(w, "Refresh");
            w.UpdateLayout();
        };
        // 4 days into the window (3 days left), 80% used: full in 2 days
        write("80", now + 3 * 86400);
        Check("card: forecast shown", forecast.Visibility == Visibility.Visible && forecast.Text.StartsWith("Full by"), forecast.Text);
        write("80", 1800000000000);
        Check("card: reset in milliseconds hides the forecast and doesn't throw", forecast.Visibility == Visibility.Collapsed);
        write("80", now - 60);
        Check("card: reset passed hides the forecast", forecast.Visibility == Visibility.Collapsed);
        write("80", 0);
        Check("card: no reset time hides the forecast", forecast.Visibility == Visibility.Collapsed);

        // A2: a damaged "updated" value must not take the widget down (Ago() at HEAD has the same AddMilliseconds)
        foreach (string bad in new[] { "1e300", "-1e300", "638000000000000000" })
        {
            updatedOverride = bad; string err = "";
            try { write("80", now + 3 * 86400); } catch (Exception x) { err = (x.InnerException ?? x).GetType().Name; }
            Check("card: updated " + bad + " doesn't throw from Refresh", err.Length == 0, err);
        }
        updatedOverride = null;

        // Fit: every text the rules can make, in the rendered column
        write("80", now + 3 * 86400);
        await Task.Delay(100);
        FrameworkElement column = (FrameworkElement)forecast.Parent;
        double room = column.ActualWidth;
        List<string> worst = new List<string>();
        foreach (string day in new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" })
            foreach (string t in new[] { "10:58 PM", "12:59 AM", "11:00 AM" }) worst.Add("Full by " + day + " " + t);
        worst.AddRange(new[] { "Full in 23h 59m", "Full in 59m", "Lasts until reset", "Limit reached" });
        double widest = 0; string widestText = "";
        foreach (string text in worst)
        {
            FormattedText f = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(forecast.FontFamily, forecast.FontStyle, forecast.FontWeight, forecast.FontStretch), forecast.FontSize, Brushes.Black);
            if (f.WidthIncludingTrailingWhitespace > widest) { widest = f.WidthIncludingTrailingWhitespace; widestText = text; }
        }
        Check("fit: the widest possible text fits the column", room > 0 && widest <= room, widestText + " " + widest.ToString("F1") + " px in " + room.ToString("F1") + " px");
    }
}
