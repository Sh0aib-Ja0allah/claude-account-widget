using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
// Fixes from the S4 review: a cut-off last line, row bounds, and the chart reading extreme rows.
static partial class H4
{
    static async Task Fixes2()
    {
        double now = Epoch(DateTime.UtcNow);
        double reset = now + 3 * 86400;
        string key = AccountKeys.Of("sample@example.com");
        string good = now.ToString("F0") + ",40," + reset.ToString("F0") + "," + key;

        // S4-1: a last line without its newline doesn't swallow the next row
        foreach (string fragment in new[] { (now - 60).ToString("F0") + ",45", "17940", (now - 60).ToString("F0") + ",45," + reset.ToString("F0") + "," + key.Substring(0, 8) })
        {
            string d = Fresh("fix2-cut");
            string f = Path.Combine(d, "usage-history.csv");
            File.WriteAllText(f, (now - 7200).ToString("F0") + ",30," + reset.ToString("F0") + "," + key + "\r\n" + fragment);
            UsageHistory.Append(f, now, 47, reset, key);
            List<UsageHistory.Row> rows = UsageHistory.Read(f);
            Check("cut-off line [" + fragment.Length + " chars]: the new row is readable and nothing absurd is read",
                rows.Count == 2 && rows[1].Percent == 47 && rows.All(r => r.Time <= now + 1), string.Join(" | ", File.ReadAllLines(f)));
        }
        string e = Path.Combine(Fresh("fix2-empty"), "usage-history.csv");
        File.WriteAllText(e, "");
        UsageHistory.Append(e, now, 10, reset, key);
        Check("empty file: no blank first line", File.ReadAllText(e) == good.Replace(",40,", ",10,") + "\r\n", File.ReadAllText(e));

        // Row bounds
        Dictionary<string, string> bad = new Dictionary<string, string>
        {
            { "time 1e300", "1e300,40,0," + key },
            { "time 2 days ahead", (now + 2 * 86400).ToString("F0") + ",40,0," + key },
            { "time 100 days old", (now - 100 * 86400).ToString("F0") + ",40,0," + key },
            { "time from a merged fragment", "17940" + now.ToString("F0") + ",40,0," + key },
            { "percent -5", now.ToString("F0") + ",-5,0," + key },
            { "percent 250", now.ToString("F0") + ",250,0," + key },
            { "reset -7", now.ToString("F0") + ",40,-7," + key },
            { "reset 1e300", now.ToString("F0") + ",40,1e300," + key },
            { "reset 61 days after the row", now.ToString("F0") + ",40," + (now + 61 * 86400).ToString("F0") + "," + key },
            { "reset 8 days before the row", now.ToString("F0") + ",40," + (now - 8 * 86400).ToString("F0") + "," + key },
        };
        foreach (KeyValuePair<string, string> b in bad) Check("row bounds: " + b.Key + " is refused", UsageHistory.Parse(b.Value, now) == null);
        Check("row bounds: a normal row is read", UsageHistory.Parse(good, now) != null);
        Check("row bounds: reset 0 is read", UsageHistory.Parse(now.ToString("F0") + ",40,0," + key, now) != null);
        Check("row bounds: 110% is read", UsageHistory.Parse(now.ToString("F0") + ",110," + reset.ToString("F0") + "," + key, now) != null);
        Check("row bounds: a reset that just passed is read", UsageHistory.Parse(now.ToString("F0") + ",40," + (now - 3600).ToString("F0") + "," + key, now) != null);

        // The chart with extreme rows doesn't crash and draws only the good one
        string cd = Fresh("fix2-chart");
        File.WriteAllText(Path.Combine(cd, "usage-history.csv"), string.Join("\r\n", bad.Values) + "\r\n" + good + "\r\n");
        WidgetWindow w = NewWidget(cd, true);
        bool threw = false;
        UsageHistoryWindow hw = null;
        try
        {
            hw = ChartFor(w, cd);
            ThemeOf(host, "light"); ThemeOf(host, "dark");
            hw.Reload();
        }
        catch (Exception ex) { threw = true; Log("THREW " + ex); }
        Canvas chart = hw == null ? null : (Canvas)Get(hw, "chart");
        Check("chart: extreme rows don't crash; only the good point is drawn", !threw && chart != null
            && chart.Children.OfType<System.Windows.Shapes.Ellipse>().Count() == 1 && chart.Children.OfType<TextBlock>().Any(t => t.Text == "40%"));
        if (hw != null) hw.Close();
        w.Close();
        await Task.Delay(10);
    }
}
