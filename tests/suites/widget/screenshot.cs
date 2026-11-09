using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shapes = System.Windows.Shapes;
// S8: docs/widget.png from a test copy with a sample account. Everything is rendered off screen to bitmaps;
// no window is shown on the real screen, no menu or popup is opened, and no real account is read.
static partial class H4
{
    static BitmapSource Grab(FrameworkElement e)
    {
        e.UpdateLayout();
        RenderTargetBitmap bmp = new RenderTargetBitmap((int)Math.Ceiling(e.ActualWidth * 2), (int)Math.Ceiling(e.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        bmp.Render(e);
        bmp.Freeze();
        return bmp;
    }

    static WidgetWindow SampleWidget(string name, string theme, string accent, bool collapsed)
    {
        string d = Fresh("png-" + name);
        string acct = Path.Combine(d, "account.json");
        File.WriteAllText(acct, "{\"oauthAccount\":{\"emailAddress\":\"you@example.com\",\"planDisplayName\":\"Ultra\"}}");
        // 4.5 days into the window at 52%, so the card shows Below pace and the forecast says it lasts.
        double reset = Epoch(DateTime.UtcNow.AddDays(2.5));
        File.WriteAllText(Path.Combine(d, "usage.json"), "{\"account\":\"you@example.com\",\"updated\":" + (Epoch(DateTime.UtcNow) * 1000).ToString("F0")
            + ",\"five_hour\":null,\"seven_day\":{\"used\":52,\"resets\":" + reset.ToString("F0") + "}}");
        File.WriteAllText(Path.Combine(d, "settings.txt"), "-6000,0,1,0," + accent + "," + theme + ",0,1,off");
        WidgetWindow w = new WidgetWindow(d, acct, null, null, 300, 0, true);
        w.ShowActivated = false;
        w.Show();
        Call(w, "ApplyTheme");
        Call(w, "ApplyAccent");
        Call(w, "Refresh");
        if (collapsed) Call(w, "Collapse", false);
        return w;
    }

    // A copy of a menu laid out in a plain panel with the widget's own menu style, so nothing pops up on screen.
    static FrameworkElement CopyMenu(IEnumerable<object> source, Func<MenuItem, bool> isChecked)
    {
        StackPanel items = new StackPanel();
        foreach (object o in source)
        {
            MenuItem item = o as MenuItem;
            if (item == null)
            {
                Separator sep = new Separator();
                sep.Style = (Style)Application.Current.FindResource(MenuItem.SeparatorStyleKey);
                items.Children.Add(sep);
                continue;
            }
            MenuItem copy = new MenuItem();
            copy.Header = item.Header;
            copy.IsCheckable = item.IsCheckable;
            copy.IsChecked = isChecked(item);
            Shapes.Ellipse swatch = item.Icon as Shapes.Ellipse;
            if (swatch != null)
            {
                Shapes.Ellipse e = new Shapes.Ellipse();
                e.Width = swatch.Width; e.Height = swatch.Height; e.Fill = swatch.Fill;
                copy.Icon = e;
            }
            if (item.Items.Count > 0) copy.Items.Add(new MenuItem { Header = "placeholder" });
            items.Children.Add(copy);
        }
        ContentControl panel = new ContentControl();
        panel.Template = (ControlTemplate)Application.Current.FindResource("MenuPanel");
        panel.FontFamily = new FontFamily("Segoe UI");
        panel.FontSize = 12.5;
        panel.Focusable = false;
        panel.Content = items;
        return panel;
    }

    static async Task Png()
    {
        // Dark: card, small icon and the menu (as before)
        ThemeOf(host, "dark");
        WidgetWindow darkCard = SampleWidget("dark", "dark", "orange", false);
        WidgetWindow darkMini = SampleWidget("darkmini", "dark", "orange", true);
        await Task.Delay(1500);
        Call(darkCard, "ApplyThemeAndRefresh");
        await Task.Delay(1500);
        BitmapSource cardDark = Grab(darkCard);
        BitmapSource miniDark = Grab(darkMini);

        // The menu, laid out in an off-screen window instead of opened as a popup
        Window menuHost = new Window();
        menuHost.WindowStyle = WindowStyle.None; menuHost.AllowsTransparency = true; menuHost.Background = Brushes.Transparent;
        menuHost.ShowInTaskbar = false; menuHost.ShowActivated = false; menuHost.SizeToContent = SizeToContent.WidthAndHeight;
        menuHost.Left = -6000; menuHost.Top = 0;
        StackPanel menus = new StackPanel(); menus.Orientation = Orientation.Horizontal;
        FrameworkElement main = CopyMenu(darkCard.ContextMenu.Items.Cast<object>(), i => (string)i.Header == "Always on top" || (string)i.Header == "Start with Windows");
        MenuItem ring = (MenuItem)darkCard.ContextMenu.Items.Cast<object>().First(o => o is MenuItem && (string)((MenuItem)o).Header == "Ring color");
        FrameworkElement sub = CopyMenu(ring.Items.Cast<object>(), i => (string)i.Tag == "orange");
        sub.Margin = new Thickness(-14, 2, 0, 0);
        sub.VerticalAlignment = VerticalAlignment.Top;
        main.VerticalAlignment = VerticalAlignment.Top;
        menus.Children.Add(main);
        menus.Children.Add(sub);
        menuHost.Content = menus;
        menuHost.Show();
        await Task.Delay(500);
        BitmapSource menuBmp = Grab(menus);

        // Light: card and small icon
        ThemeOf(host, "light");
        WidgetWindow lightCard = SampleWidget("light", "light", "blue", false);
        WidgetWindow lightMini = SampleWidget("lightmini", "light", "blue", true);
        await Task.Delay(1500);
        Call(lightCard, "ApplyThemeAndRefresh");
        await Task.Delay(1500);
        BitmapSource cardLight = Grab(lightCard);
        BitmapSource miniLight = Grab(lightMini);
        ThemeOf(host, "dark");

        // Compose at 2x on the same background as before
        Canvas canvas = new Canvas();
        canvas.Width = 700; canvas.Height = 502 + Math.Max(0, menuBmp.Height / 2 - 190);
        canvas.Background = new SolidColorBrush(Color.FromRgb(203, 208, 214));
        Action<BitmapSource, double, double> put = delegate(BitmapSource b, double x, double y)
        {
            Image img = new Image(); img.Source = b; img.Width = b.PixelWidth / 2.0; img.Height = b.PixelHeight / 2.0;
            Canvas.SetLeft(img, x); Canvas.SetTop(img, y); canvas.Children.Add(img);
        };
        double cardW = cardDark.PixelWidth / 2.0;
        put(cardDark, 24, 24);
        put(cardLight, 700 - 24 - cardLight.PixelWidth / 2.0, 24);
        double below = 24 + Math.Max(cardDark.PixelHeight, cardLight.PixelHeight) / 2.0 + 18;
        put(miniDark, 44, below + 40);
        put(miniLight, 44, below + 40 + miniDark.PixelHeight / 2.0 + 4);
        put(menuBmp, 200, below);
        canvas.Height = Math.Max(below + menuBmp.PixelHeight / 2.0, below + 40 + (miniDark.PixelHeight + miniLight.PixelHeight) / 2.0) + 24;
        Window compose = new Window();
        compose.WindowStyle = WindowStyle.None; compose.ShowInTaskbar = false; compose.ShowActivated = false;
        compose.Left = -6000; compose.Top = 0; compose.SizeToContent = SizeToContent.WidthAndHeight;
        compose.Content = canvas;
        compose.Show();
        await Task.Delay(300);
        BitmapSource final = Grab(canvas);
        string outFile = Path.Combine(root, "widget.png");
        PngBitmapEncoder png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(final));
        using (FileStream f = File.Create(outFile)) png.Save(f);
        Log("SHOT " + outFile + " " + final.PixelWidth + "x" + final.PixelHeight);
        Check("png: written", File.Exists(outFile));
        foreach (Window w in new Window[] { compose, menuHost, darkCard, darkMini, lightCard, lightMini }) w.Close();
    }
}
