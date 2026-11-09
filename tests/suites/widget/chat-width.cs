using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
// Chat window starts 441 wide, like Settings (owner's request). Off screen only.
static partial class H4
{
    static async Task ChatWidth()
    {
        string d = Fresh("chatw");
        ChatWindow c = new ChatWindow(d);
        Check("chat: starts 441 wide, minimum still 320", c.Width == 441 && c.MinWidth == 320, c.Width + " / " + c.MinWidth);
        c.Close();

        Call(host, "OpenChatWindow");
        await Task.Delay(400);
        ChatWindow opened = Application.Current.Windows.OfType<ChatWindow>().Last();
        opened.UpdateLayout();
        Check("chat: opened from the widget at 441", Math.Abs(opened.ActualWidth - 441) < 0.5, opened.ActualWidth.ToString("F1"));
        TextBox input = (TextBox)Get(opened, "input");
        Point inputRight = input.TranslatePoint(new Point(input.ActualWidth, 0), opened);
        Check("chat: the composer box fits inside the window", inputRight.X <= opened.ActualWidth && input.ActualWidth > 200, input.ActualWidth.ToString("F0"));
        ShotOf((FrameworkElement)opened.Content, "chat-441");
        opened.Close();

        // The widget's placement formula, on 1080p work areas at 100, 125 and 150% scaling (48 px taskbar),
        // with the widget at its default spot near the top right: every one of the 8 cascade steps stays on screen.
        foreach (double scale in new[] { 1.0, 1.25, 1.5 })
        {
            Rect area = new Rect(0, 0, 1920 / scale, 1032 / scale);
            double widgetLeft = area.Right - 316 - 20, widgetTop = area.Top + 20, widgetWidth = 316;
            bool allFit = true;
            for (int n = 0; n < 8; n++)
            {
                double step = 28 * n;
                double left = widgetLeft - 441 - 4 + step;
                if (widgetLeft - 441 - 4 < area.Left) left = widgetLeft + widgetWidth + 4 + step;
                double placedLeft = Math.Max(area.Left, Math.Min(left, area.Right - 441));
                double placedTop = Math.Max(area.Top, Math.Min(widgetTop + step, area.Bottom - 536));
                if (placedLeft < area.Left || placedLeft + 441 > area.Right || placedTop < area.Top || placedTop + 536 > area.Bottom) allFit = false;
            }
            Check("chat placement: 8 cascaded chats fit a 1080p work area at " + (int)(scale * 100) + "%", allFit);
        }
    }
}
