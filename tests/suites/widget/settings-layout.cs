using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
// Settings layout (owner's hand-test requests): hidden scroll bar, 441 wide, at most 756 tall and never taller than
// the work area, with Close and Save pinned below the scrolling fields. Measured off screen, never on the real screen.
static partial class H4
{
    static async Task Layout()
    {
        Check("size: width 441", new SettingsWindow(NewWidget(Fresh("lay-w"), true), Path.Combine(root, "lay-w")).Width == 441);
        Check("size: height cap 756 on a big screen", SettingsWindow.FitHeight(1032) == 756 && SettingsWindow.FitHeight(2000) == 756);
        Check("size: clamped to a smaller work area", SettingsWindow.FitHeight(688) == 688 && SettingsWindow.FitHeight(500) == 500);

        // 1080p with a 48 px taskbar: 1032 px of work area, which is 1032, 825.6 and 688 DIPs at 100, 125 and 150%.
        foreach (double scale in new[] { 1.0, 1.25, 1.5 })
        {
            double area = 1032 / scale;
            string d = Fresh("lay-" + (int)(scale * 100));
            File.WriteAllText(Path.Combine(d, "chat-read-folders.txt"), string.Join("\r\n", Enumerable.Range(1, 6).Select(i => "C:\\Folder" + i)) + "\r\n");
            WidgetWindow w = NewWidget(d, true);
            SettingsWindow sw = new SettingsWindow(w, d);
            sw.MaxHeight = SettingsWindow.FitHeight(area);
            Call(w, "ShowOffScreen", sw);
            sw.UpdateLayout();
            await Task.Delay(100);
            ScrollViewer scroll = (ScrollViewer)Get(sw, "scroll");
            Border card = (Border)Get(sw, "card");
            string tag = (int)(scale * 100) + "%";
            Check(tag + ": window fits the work area", sw.ActualHeight <= area + 0.5, sw.ActualHeight.ToString("F0") + " of " + area.ToString("F0"));
            Check(tag + ": the scroll bar is hidden", scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Hidden && scroll.ComputedVerticalScrollBarVisibility != Visibility.Visible);
            foreach (string name in new[] { "Close", "Save" })
            {
                Border button = FindButton(card, name);
                Point bottom = button.TranslatePoint(new Point(0, button.ActualHeight), card);
                Check(tag + ": " + name + " is pinned inside the visible card", button != null && bottom.Y <= card.ActualHeight - card.Padding.Bottom + 0.5 && bottom.Y > 0,
                    bottom.Y.ToString("F0") + " of " + card.ActualHeight.ToString("F0"));
            }
            if (scroll.ExtentHeight > scroll.ViewportHeight + 1)
            {
                double before = scroll.VerticalOffset;
                scroll.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(scroll), 0, Key.PageDown) { RoutedEvent = Keyboard.KeyDownEvent });
                sw.UpdateLayout();
                // Under heavy load the scroll can land a few frames later, so wait up to about a second for it.
                for (int i = 0; i < 20 && scroll.VerticalOffset <= before; i++)
                {
                    await Task.Delay(50);
                    sw.UpdateLayout();
                }
                Check(tag + ": PageDown scrolls the hidden scroll", scroll.VerticalOffset > before, before.ToString("F0") + " to " + scroll.VerticalOffset.ToString("F0"));
                scroll.ScrollToTop(); sw.UpdateLayout();
                ((FrameworkElement)Get(sw, "laptopSwitch")).BringIntoView();
                sw.UpdateLayout();
                Check(tag + ": a field below the fold is scrolled into view (what Tab focus does)", scroll.VerticalOffset > 0, scroll.VerticalOffset.ToString("F0"));
            }
            else
            {
                Note(tag + ": everything fits without scrolling (extent " + scroll.ExtentHeight.ToString("F0") + ", viewport " + scroll.ViewportHeight.ToString("F0") + ")");
            }
            if (scale == 1.5)
            {
                foreach (string theme in new[] { "dark", "light" }) { ThemeOf(host, theme); sw.UpdateLayout(); ShotOf(card, "settings-150-" + theme); }
                ThemeOf(host, "dark");
            }
            sw.Close(); w.Close();
        }
    }

    // The last matching text button in the tree, so the footer's Close wins over the header's x.
    static Border FindButton(DependencyObject node, string name)
    {
        Border last = null;
        Border b = node as Border;
        TextBlock label = b == null ? null : b.Child as TextBlock;
        if (label != null && System.Windows.Automation.AutomationProperties.GetName(b) == name && label.Text == name) last = b;
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++)
        {
            Border found = FindButton(System.Windows.Media.VisualTreeHelper.GetChild(node, i), name);
            if (found != null) last = found;
        }
        return last;
    }
}
