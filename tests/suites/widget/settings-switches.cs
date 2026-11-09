using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
// Settings switch rows: the description never runs under the switch, in both themes and with long text.
static partial class H4
{
    static IEnumerable<TextBlock> TextBlocksIn(DependencyObject node)
    {
        TextBlock t = node as TextBlock;
        if (t != null) yield return t;
        int n = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++) foreach (TextBlock x in TextBlocksIn(VisualTreeHelper.GetChild(node, i))) yield return x;
    }

    // The widest text right edge in the row, and the switch's left edge, both in row coordinates.
    static bool RowClear(Panel row, Border track, out string info)
    {
        double trackLeft = track.TranslatePoint(new Point(0, 0), row).X;
        double rightmost = 0;
        foreach (UIElement child in row.Children)
        {
            if (child == track) continue;
            foreach (TextBlock t in TextBlocksIn(child))
            {
                // A wrapped TextBlock never draws past its own width, so its right edge bounds the text.
                double right = t.TranslatePoint(new Point(t.ActualWidth, 0), row).X;
                rightmost = Math.Max(rightmost, right);
            }
        }
        info = "text ends at " + rightmost.ToString("F1") + ", switch starts at " + trackLeft.ToString("F1");
        return rightmost <= trackLeft - 11.5;
    }

    static async Task Switches()
    {
        string d = Fresh("switch");
        WidgetWindow w = NewWidget(d, true);
        SettingsWindow sw = new SettingsWindow(w, d);
        Call(w, "ShowOffScreen", sw);
        sw.UpdateLayout();
        await Task.Delay(100);
        foreach (string theme in new[] { "dark", "light" })
        {
            ThemeOf(host, theme);
            sw.UpdateLayout();
            foreach (string field in new[] { "alertsSwitch", "updateSwitch", "laptopSwitch" })
            {
                Border track = (Border)Get(sw, field);
                Panel row = (Panel)VisualTreeHelper.GetParent(track);
                string info;
                Check(theme + ": " + field + " text stays clear of the switch", RowClear(row, track, out info), info);
            }
        }

        // A long, translation-length title and description in the same rows
        StackPanel host2 = new StackPanel();
        string longTitle = "Benachrichtigungen bei Nutzungsgrenzen und Zurücksetzungen";
        string longHint = "Zeigt eine Windows-Benachrichtigung, wenn Ihre 7-Tage-Nutzung 70 % und 90 % erreicht und wenn das Limit zurückgesetzt wird, einschließlich sehr langer Wörter wie Donaudampfschifffahrtsgesellschaftskapitän.";
        List<Border> tracks = new List<Border>();
        for (int i = 0; i < 3; i++)
        {
            Border track = new Border(), knob = new Border();
            tracks.Add(track);
            host2.Children.Add((UIElement)Call(sw, "BuildSwitchRow", track, knob, longTitle, longHint, (Action)delegate { }));
        }
        ScrollViewer scroll = (ScrollViewer)Get(sw, "scroll");
        StackPanel fields = (StackPanel)scroll.Content;
        fields.Children.Add(host2);
        sw.UpdateLayout();
        for (int i = 0; i < tracks.Count; i++)
        {
            string info;
            Check("long text row " + (i + 1) + ": stays clear of the switch", RowClear((Panel)VisualTreeHelper.GetParent(tracks[i]), tracks[i], out info), info);
        }

        // The same layout drawn at 125% and 150% DPI: layout is in device-independent units, so nothing moves
        foreach (int dpi in new[] { 96, 120, 144 })
        {
            Border card = (Border)Get(sw, "card");
            RenderTargetBitmap bmp = new RenderTargetBitmap((int)(card.ActualWidth * dpi / 96), (int)(card.ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
            bmp.Render(card);
            PngBitmapEncoder png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bmp));
            using (FileStream f = File.Create(Path.Combine(root, "switch-rows-" + dpi + ".png"))) png.Save(f);
            Border t0 = (Border)Get(sw, "laptopSwitch");
            string info;
            Check("at " + (dpi * 100 / 96) + "% scale: laptop row still clear", RowClear((Panel)VisualTreeHelper.GetParent(t0), t0, out info), info);
        }
        scroll.ScrollToBottom();
        sw.UpdateLayout();
        foreach (string theme in new[] { "dark", "light" }) { ThemeOf(host, theme); sw.UpdateLayout(); ShotOf((FrameworkElement)Get(sw, "card"), "switch-rows-" + theme); }
        ThemeOf(host, "dark");
        sw.Close(); w.Close();
    }
}
