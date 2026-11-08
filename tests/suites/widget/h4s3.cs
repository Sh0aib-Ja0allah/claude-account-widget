using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
// S3: the limit forecast line.
static partial class H4
{
    static DateTime U(int month, int day, int hour, int minute = 0) { return new DateTime(2026, month, day, hour, minute, 0, DateTimeKind.Utc); }

    static void F(string what, double used, DateTime reset, DateTime now, TimeZoneInfo zone, string expected)
    {
        string got = WidgetWindow.ForecastText(used, reset, now, zone);
        Check("forecast: " + what, got == expected, got ?? "null");
    }

    static async Task S3()
    {
        TimeZoneInfo utc = TimeZoneInfo.Utc;
        DateTime reset = U(11, 14, 11);          // a Saturday; the window started Sat 11-07 11:00 UTC
        DateTime start = reset.AddDays(-7);
        DateTime mid = start.AddHours(84);       // Tue 11-10 23:00 UTC

        // R-B-01, R-B-02, R-B-03, R-B-05
        F("no figure", double.NaN, reset, mid, utc, null);
        F("reset passed", 50, reset, reset.AddMinutes(1), utc, null);
        F("reset unknown", 50, DateTime.MinValue, mid, utc, null);
        F("0% hidden", 0, reset, mid, utc, null);
        F("4.9% hidden", 4.9, reset, mid, utc, null);
        F("first 12 hours hidden", 50, reset, start.AddHours(11.9), utc, null);
        F("99.4 in the first 12 hours hidden", 99.4, reset, start.AddHours(11), utc, null);
        F("99.6 in the first 12 hours rounds to 100", 99.6, reset, start.AddHours(11), utc, "Limit reached");
        F("100% in the first hours", 100, reset, start.AddHours(2), utc, "Limit reached");
        F("over 100%", 104, reset, mid, utc, "Limit reached");
        F("low use mid-window won't reach", 25, reset, mid, utc, "Lasts until reset");
        F("exactly on pace (50 at 84h) reaches at the reset, counted as won't reach", 50, reset, mid, utc, "Lasts until reset");
        F("mid-window over pace gives a day and time", 60, reset, mid, utc, "Full by Fri 7:00 AM");
        F("within 24 hours gives hours and minutes", 84, reset, mid, utc, "Full in 16h 0m");
        F("under an hour gives minutes", 99, reset, mid, utc, "Full in 51m");
        F("99.4 mid-window projects minutes", 99.4, reset, mid, utc, "Full in 31m");

        // R-B-04: local time across a week boundary and a clock change (US Pacific left daylight time on Sun 2026-11-01)
        TimeZoneInfo pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        DateTime r2 = U(11, 5, 18);
        DateTime s2 = r2.AddDays(-7);            // Thu 10-29 18:00 UTC
        F("after a clock change, standard time is used", 48, r2, s2.AddHours(48), pacific, "Full by Mon 2:00 PM");
        F("before a clock change, daylight time is used", 30, r2, s2.AddHours(12), pacific, "Full by Sat 3:00 AM");
        F("week boundary: Sunday into Monday", 75, U(11, 9, 12), U(11, 2, 12).AddHours(120), utc, "Full by Mon 4:00 AM");

        // On the card: shown for the signed-in account, hidden for another, and it fits on one line
        string d = Fresh("s3-card");
        double resets = Epoch(DateTime.UtcNow.AddHours(84));   // 3.5 days in
        WidgetWindow w = NewWidget(d, true);
        Usage(d, 70, resets);
        Call(w, "Refresh");
        TextBlock line = (TextBlock)Get(w, "forecastText");
        Check("card: forecast shown under the pace row", line.Visibility == Visibility.Visible && line.Text.StartsWith("Full by "), line.Text);
        Check("card: raw figure kept", (double)Get(w, "sevenRaw") == 70);
        Usage(d, 70.4, resets);
        Call(w, "Refresh");
        Check("card: percent and color unchanged by the raw figure", ((TextBlock)Get(Get(w, "sevenDay"), "Percent")).Text == "70%");
        Usage(d, 70, resets, "other@example.com");
        Call(w, "Refresh");
        Check("card: hidden when the usage file is another account's", line.Visibility == Visibility.Collapsed);
        Usage(d, 20, resets);
        Call(w, "Refresh");
        Check("card: low use shows won't reach", line.Text == "Lasts until reset" && line.Visibility == Visibility.Visible, line.Text);
        Usage(d, 70, 0);
        Call(w, "Refresh");
        Check("card: no reset time hides it", line.Visibility == Visibility.Collapsed);
        w.Close();

        // R-B-06: the longest text fits on one line in the real card column, both themes
        foreach (string theme in new[] { "dark", "light" })
        {
            ThemeOf(host, theme);
            Usage(Path.Combine(root, "host"), 70, resets);
            Call(host, "Refresh");
            TextBlock hl = (TextBlock)Get(host, "forecastText");
            hl.Text = "Full by Wed 12:59 PM";
            host.UpdateLayout();
            FrameworkElement column = (FrameworkElement)VisualTreeHelper.GetParent(hl);
            FormattedText ft = new FormattedText(hl.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(hl.FontFamily, hl.FontStyle, hl.FontWeight, hl.FontStretch), hl.FontSize, Brushes.Black, 1.0);
            foreach (string longest in new[] { "Full by Wed 12:59 PM", "Lasts until reset", "Full in 23h 59m", "Limit reached" }) { hl.Text = longest; host.UpdateLayout(); ft = new FormattedText(hl.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(hl.FontFamily, hl.FontStyle, hl.FontWeight, hl.FontStretch), hl.FontSize, Brushes.Black, 1.0);
            Check("card: " + longest + " fits in " + theme, ft.WidthIncludingTrailingWhitespace <= hl.ActualWidth + 0.5 && hl.ActualWidth <= column.ActualWidth + 0.5,
                ft.WidthIncludingTrailingWhitespace.ToString("F1") + " of " + hl.ActualWidth.ToString("F1") + " (column " + column.ActualWidth.ToString("F1") + ")"); }
            ShotOf((FrameworkElement)Get(host, "cardBorder"), "forecast-" + theme);
        }
        ThemeOf(host, "dark");
        await Task.Delay(10);
    }
}
