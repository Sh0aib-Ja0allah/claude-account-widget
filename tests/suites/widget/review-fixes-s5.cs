using System;
using Shapes = System.Windows.Shapes;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
// Fixes from the S5 review: S5-1 (out-of-range percent), S5-2 (undated rows), S5-3 (two resets close together).
static partial class H4
{
    static async Task Fixes3()
    {
        double now = Epoch(DateTime.UtcNow);
        double reset = now + 3 * 86400;
        string key = AccountKeys.Of("sample@example.com");

        // S5-1: written rows are always readable, and the hour rule still applies
        string d = Fresh("fix3-clamp");
        string f = Path.Combine(d, "usage-history.csv");
        UsageHistory.Append(f, now, 150, reset, key);
        UsageHistory.Append(f, now + 300, 150, reset, key);
        UsageHistory.Append(f, now + 600, -5, reset, key);
        UsageHistory.Append(f, now + 900, 40, 1e300, key);
        List<UsageHistory.Row> rows = UsageHistory.Read(f);
        Check("S5-1: 150% stored as 120 and readable; the repeat within the hour adds nothing", rows.Count == 3 && rows[0].Percent == 120, string.Join(" | ", File.ReadAllLines(f)));
        Check("S5-1: -5% stored as 0", rows.Count > 1 && rows[1].Percent == 0);
        Check("S5-1: an absurd reset stored as 0 and readable", rows.Count > 2 && rows[2].Reset == 0);
        Check("S5-1: every written line is readable", File.ReadAllLines(f).Length == rows.Count);

        string rd = Fresh("fix3-record");
        WidgetWindow w = NewWidget(rd, true);
        File.WriteAllText(Path.Combine(rd, "usage.json"), "{\"account\":\"sample@example.com\",\"updated\":5,\"five_hour\":null,\"seven_day\":{\"used\":300,\"resets\":" + reset.ToString("F0") + "}}");
        Call(w, "AfterSuccessfulCheck"); await WaitHistory(w);
        Call(w, "AfterSuccessfulCheck"); await WaitHistory(w);
        List<UsageHistory.Row> rr = UsageHistory.Read(Path.Combine(rd, "usage-history.csv"));
        Check("S5-1: a reported 300% gives one readable row, not one per check", rr.Count == 1 && rr[0].Percent == 120 && File.ReadAllLines(Path.Combine(rd, "usage-history.csv")).Length == 1);
        w.Close();

        // S5-2: rows with no reset time
        string ud = Fresh("fix3-undated");
        File.WriteAllText(Path.Combine(ud, "usage-history.csv"),
            Row(now - 7200, 20, 0, key) + Row(now - 3600, 25, 0, key));
        w = NewWidget(ud, true);
        UsageHistoryWindow hw = ChartFor(w, ud);
        string sub = ((TextBlock)Get(hw, "subtitle")).Text;
        Check("S5-2: undated rows say why they aren't drawn", sub == "Anthropic hasn't reported a reset time for this period, so it can't be drawn yet.", sub);
        Check("S5-2: no undated bar with a ? date", ((Canvas)Get(hw, "bars")).Children.OfType<TextBlock>().All(t => t.Text != "?") && ((Canvas)Get(hw, "bars")).Children.OfType<Border>().Count() == 0);
        Check("S5-2: not the empty state", ((TextBlock)Get(hw, "emptyNote")).Visibility == Visibility.Collapsed);
        hw.Close(); w.Close();

        // S5-3: two resets an hour apart. The first period ends where the second starts, so its bar is dated in the past
        string td = Fresh("fix3-two");
        double r1 = reset, r2 = reset + 3600;
        File.WriteAllText(Path.Combine(td, "usage-history.csv"),
            Row(now - 7200, 30, r1, key) + Row(now - 5400, 32, r1, key) + Row(now - 3600, 5, r2, key) + Row(now - 1800, 6, r2, key));
        List<UsageHistoryWindow.Period> ps = UsageHistoryWindow.Periods(UsageHistory.Read(Path.Combine(td, "usage-history.csv")), key);
        Check("S5-3: two periods", ps.Count == 2);
        Check("S5-3: the first ends at the second's start", ps.Count == 2 && ps[0].End == r2 - 7 * 86400 && ps[1].End == r2);
        w = NewWidget(td, true);
        hw = ChartFor(w, td);
        Canvas bars = (Canvas)Get(hw, "bars");
        Check("S5-3: the current period is the second", ((Shapes.Polyline)((Canvas)Get(hw, "chart")).Children.OfType<Shapes.Polyline>().First()).Points.Count == 2);
        Check("S5-3: the first shows as one earlier bar dated where it ended", bars.Children.OfType<Border>().Count() == 1
            && bars.Children.OfType<TextBlock>().Any(t => t.Text == new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(r2 - 7 * 86400).ToLocalTime().ToString("MMM d", System.Globalization.CultureInfo.InvariantCulture)));
        hw.Close(); w.Close();

        // A period whose end is still ahead is never listed as earlier
        string fd = Fresh("fix3-future");
        File.WriteAllText(Path.Combine(fd, "usage-history.csv"),
            Row(now - 7200, 30, reset, key) + Row(now - 3600, 31, 0, key));
        w = NewWidget(fd, true);
        hw = ChartFor(w, fd);
        Check("future end: no bar for a period that hasn't ended", ((Canvas)Get(hw, "bars")).Children.OfType<Border>().Count() == 0);
        hw.Close(); w.Close();
    }
}
