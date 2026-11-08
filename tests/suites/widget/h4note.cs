using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
// The account note line on the card fits beside the plan chip, with the full sentence in the tooltip.
static partial class H4
{
    static async Task Notes()
    {
        string d = Fresh("notes");
        WidgetWindow w = NewWidget(d, true);
        w.ShowActivated = false; w.Left = -6000; w.Show();
        await Task.Delay(200);
        TextBlock note = (TextBlock)Get(w, "accountNote");
        TextBlock planText = (TextBlock)Get(w, "planText");
        Border chip = (Border)Get(w, "planChip");
        StackPanel status = (StackPanel)VisualTreeHelper.GetParent(note);
        FrameworkElement column = (FrameworkElement)VisualTreeHelper.GetParent(status);
        object good = typeof(WidgetWindow).GetField("Good", Any).GetValue(null);
        object warn = typeof(WidgetWindow).GetField("Warn", Any).GetValue(null);
        object danger = typeof(WidgetWindow).GetField("Danger", Any).GetValue(null);
        string[][] cases = {
            new[] { "Signed in to Claude Code", "good", "Ultra", "" },
            new[] { "Signed in (verified)", "good", "Ultra", "Anthropic confirmed this account. Claude Code's saved account file still names another email, which other tools may show. You don't need to do anything." },
            new[] { "Warning: not your expected account", "danger", "Ultra", "" },
            new[] { "Couldn't verify account", "warn", "Ultra", "Couldn't verify the account with Anthropic. Claude Code's file shows another email." },
            new[] { "Couldn't read the Claude Code account file", "warn", "", "" },
            new[] { "Claude Code has no account signed in", "danger", "", "" },
        };
        foreach (string theme in new[] { "dark", "light" })
        {
            ThemeOf(host, theme);
            foreach (string[] c in cases)
            {
                object color = c[1] == "good" ? good : c[1] == "warn" ? warn : danger;
                Call(w, "ShowAccount", "owner@example.com", c[0], color, c[3].Length > 0 ? c[3] : null);
                planText.Text = c[2];
                chip.Visibility = c[2].Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                w.UpdateLayout();
                status.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double needed = status.DesiredSize.Width;
                Check(theme + ": \"" + c[0] + "\"" + (c[2].Length > 0 ? " + " + c[2] : "") + " fits the card", needed <= column.ActualWidth + 0.5,
                    needed.ToString("F1") + " of " + column.ActualWidth.ToString("F1"));
                if (c[3].Length > 0) Check(theme + ": \"" + c[0] + "\" has the full sentence as its tooltip", (note.ToolTip as string) == c[3]);
            }
        }
        // The real paths set the short text and the tooltip
        Call(w, "Refresh");
        Check("tooltip: a plain note's tooltip is the note itself", (note.ToolTip as string) == note.Text, note.Text);
        ShotOf((FrameworkElement)Get(w, "cardBorder"), "note-card");
        ThemeOf(host, "dark");
        w.Close();
        Note("layout is in device-independent units, so the fit is the same at 100, 125 and 150% scaling");
    }
}
