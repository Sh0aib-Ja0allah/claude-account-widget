using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Shapes = System.Windows.Shapes;
// S5: the usage history chart. Every window stays off screen.
static partial class H4
{
    static string Row(double t, double p, double r, string k) { return t.ToString("F0") + "," + p + "," + r.ToString("F0") + "," + k + "\r\n"; }

    static UsageHistoryWindow ChartFor(WidgetWindow w, string dir)
    {
        UsageHistoryWindow hw = new UsageHistoryWindow(w, Path.Combine(dir, "usage-history.csv"));
        Call(w, "ShowOffScreen", hw);
        hw.UpdateLayout();
        return hw;
    }

    static int Count<T>(Canvas c) { return c.Children.OfType<T>().Count(); }

    static async Task S5()
    {
        string key = AccountKeys.Of("sample@example.com");
        string other = AccountKeys.Of("other@example.com");
        double now = Epoch(DateTime.UtcNow);
        double reset = now + 3 * 86400;      // 4 days into the current period

        // R-D-09: menu item, one window, off screen and not in front
        Check("history: menu has Usage history", host.ContextMenu.Items.OfType<MenuItem>().Any(m => (m.Header as string) == "Usage history"));
        Call(host, "OpenHistory"); Call(host, "OpenHistory");
        await Task.Delay(300);
        UsageHistoryWindow[] open = Application.Current.Windows.OfType<UsageHistoryWindow>().ToArray();
        IntPtr handle = open.Length == 0 ? IntPtr.Zero : new System.Windows.Interop.WindowInteropHelper(open[0]).Handle;
        Check("history: opening twice gives one window, off screen, not in front", open.Length == 1 && open[0].Left == -6000 && GetForegroundWindow() != handle, open.Length.ToString());
        foreach (UsageHistoryWindow o in open) o.Close();
        await Task.Delay(100);
        Check("history: closing clears the single-window slot", Get(host, "historyWindow") == null);

        // R-D-07: empty state
        string d = Fresh("s5-empty");
        WidgetWindow w = NewWidget(d, true);
        UsageHistoryWindow hw = ChartFor(w, d);
        Check("empty: message shown, chart hidden", ((TextBlock)Get(hw, "emptyNote")).Visibility == Visibility.Visible
            && ((TextBlock)Get(hw, "emptyNote")).Text == "No history yet. It starts after the next usage check."
            && ((StackPanel)Get(hw, "chartArea")).Visibility == Visibility.Collapsed);
        ShotOf((FrameworkElement)Get(hw, "card"), "history-empty");
        hw.Close();

        // R-D-07: one point is a dot with its value and no line
        File.WriteAllText(Path.Combine(d, "usage-history.csv"), Row(now - 3600, 41, reset, key));
        hw = ChartFor(w, d);
        Canvas chart = (Canvas)Get(hw, "chart");
        Check("one point: a dot and its value, no line", Count<Shapes.Ellipse>(chart) == 1 && Count<Shapes.Polyline>(chart) == 0
            && chart.Children.OfType<TextBlock>().Any(t => t.Text == "41%"));
        Check("one point: no earlier bars", ((Canvas)Get(hw, "bars")).Visibility == Visibility.Collapsed);
        ShotOf((FrameworkElement)Get(hw, "card"), "history-one");
        hw.Close();

        // Some data: the current period, four earlier ones, another account's rows, a reset of 0 and junk
        StringBuilder sb = new StringBuilder();
        for (int p = 4; p >= 1; p--)
        {
            double r = reset - p * 7 * 86400;
            for (int i = 0; i < 5; i++) sb.Append(Row(r - 6 * 86400 + i * 86400, 15 * p + i * 5, r, key));
        }
        sb.Append("junk line\r\n");
        for (int i = 0; i < 8; i++) sb.Append(Row(reset - 7 * 86400 + 3600 + i * 40000, 5 + i * 6, i == 3 ? 0 : reset, key));
        sb.Append(Row(now - 100, 99, reset, other));
        File.WriteAllText(Path.Combine(d, "usage-history.csv"), sb.ToString());
        List<UsageHistory.Row> rows = UsageHistory.Read(Path.Combine(d, "usage-history.csv"));
        List<UsageHistoryWindow.Period> periods = UsageHistoryWindow.Periods(rows, key);
        Check("periods: split at each reset (R-D-06)", periods.Count == 5, periods.Count.ToString());
        Check("periods: a reset of 0 stays in its period", periods.Last().Rows.Count == 8);
        Check("periods: peaks", periods[0].Peak == 80 && periods[3].Peak == 35 && periods[4].Peak == 47, string.Join(",", periods.Select(p => p.Peak)));
        Check("periods: another account's rows are left out (R-D-05)", periods.All(p => p.Rows.All(r => r.Key == key)));

        foreach (string theme in new[] { "dark", "light" })
        {
            ThemeOf(host, theme);
            hw = ChartFor(w, d);
            chart = (Canvas)Get(hw, "chart");
            Canvas bars = (Canvas)Get(hw, "bars");
            Shapes.Polyline line = chart.Children.OfType<Shapes.Polyline>().FirstOrDefault();
            Check("data " + theme + ": one line with the current period's 8 points only", line != null && line.Points.Count == 8, line == null ? "none" : line.Points.Count.ToString());
            Check("data " + theme + ": dashed pace line", chart.Children.OfType<Shapes.Line>().Any(l => l.StrokeDashArray != null && l.StrokeDashArray.Count == 2));
            Check("data " + theme + ": four earlier bars", bars.Children.OfType<Border>().Count() == 4);
            Check("data " + theme + ": the other account's 99% isn't drawn", !chart.Children.OfType<TextBlock>().Any(t => t.Text == "99%") && !bars.Children.OfType<TextBlock>().Any(t => t.Text == "99%"));
            Check("data " + theme + ": 80% bar uses the card's amber", bars.Children.OfType<Border>().First().Background == WidgetWindow.WarnBrush);
            ShotOf((FrameworkElement)Get(hw, "card"), "history-data-" + theme);
            if (theme == "dark")
            {
                // R-D-08: a theme change redraws the open window
                object fillBefore = ((Border)Get(hw, "card")).Background;
                ThemeOf(host, "light");
                Check("theme: an open window redraws", !ReferenceEquals(fillBefore, ((Border)Get(hw, "card")).Background));
                ThemeOf(host, "dark");
            }
            hw.Close();
        }
        ThemeOf(host, "dark");

        // More than 8 earlier periods show the last 8; a damaged file doesn't crash
        sb = new StringBuilder();
        for (int p = 12; p >= 1; p--) sb.Append(Row(reset - p * 7 * 86400 - 86400, 30, reset - p * 7 * 86400, key));
        File.WriteAllText(Path.Combine(d, "usage-history.csv"), sb.ToString());
        hw = ChartFor(w, d);
        Check("bars: at most 8 earlier periods", ((Canvas)Get(hw, "bars")).Children.OfType<Border>().Count() == 8);
        Check("bars: no current period says so", ((TextBlock)Get(hw, "subtitle")).Text == "No usage recorded since the last reset.", ((TextBlock)Get(hw, "subtitle")).Text);
        hw.Close();
        File.WriteAllBytes(Path.Combine(d, "usage-history.csv"), Encoding.UTF8.GetBytes(new string('\0', 5000) + "\r\n,,,\r\n" + new string('x', 300000)));
        bool threw = false;
        try { hw = ChartFor(w, d); hw.Close(); } catch (Exception e) { threw = true; Log("THREW " + e); }
        Check("damaged file: no crash, empty state", !threw);

        // Signed out: nothing is shown
        File.WriteAllText(Path.Combine(d, "usage-history.csv"), Row(now - 3600, 41, reset, key));
        string signedOut = Path.Combine(d, "nobody.json");
        File.WriteAllText(signedOut, "{\"oauthAccount\":{}}");
        WidgetWindow nobody = new WidgetWindow(d, signedOut, null, null, 300, 0, true);
        hw = ChartFor(nobody, d);
        Check("signed out: empty state", ((TextBlock)Get(hw, "emptyNote")).Visibility == Visibility.Visible);
        hw.Close(); nobody.Close(); w.Close();
    }
}
