using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
// Worker A's independent checks of C3d (chat sidebar account row and its menu). Test mode only: windows off screen,
// no real dialog (ConfirmExit seam), no popup (ShowAccountMenu seam), the real Run entry only read.
// sidebar-account.exe <out folder>
static class H16
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static string root;
    static int fails, passes;
    static object Get(object o, string f) { return o.GetType().GetField(f, Any).GetValue(o); }
    static void Set(object o, string f, object v) { o.GetType().GetField(f, Any).SetValue(o, v); }
    static void Call(object o, string m) { o.GetType().GetMethod(m, Any, null, Type.EmptyTypes, null).Invoke(o, null); }
    static void Log(string s) { Console.WriteLine(s); Console.Out.Flush(); }
    static void Check(string what, bool ok, string info = "")
    {
        Log((ok ? "PASS " : "FAIL ") + what + (info.Length > 0 ? " [" + info.Replace("\r", "\\r").Replace("\n", "\\n") + "]" : ""));
        if (ok) passes++; else fails++;
    }
    static void Key(UIElement target, Key key)
    {
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
    }
    static string RunValue()
    {
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
        {
            if (k == null) return "no key";
            object v = k.GetValue("ClaudeAccountWidget");
            return v == null ? "absent" : v.ToString();
        }
    }
    static MenuItem Item(ItemsControl menu, string header) { return menu.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Header as string) == header); }

    [STAThread]
    static void Main(string[] args)
    {
        root = args[0];
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        string account = Path.Combine(root, "account.json");
        File.WriteAllText(account, "{\"oauthAccount\":{\"emailAddress\":\"a.very.long.mailbox.name.for.layout.checks@a-long-subdomain.example.com\",\"planDisplayName\":\"Max\"}}");
        File.WriteAllText(Path.Combine(root, "settings.txt"), "-6000,0,0,0,default,system");
        Application app = new Application();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.DispatcherUnhandledException += (s, e) => { Log("UNHANDLED " + e.Exception); fails++; e.Handled = true; };
        WidgetWindow w = new WidgetWindow(root, account, null, null, 300, 0, true);
        w.ShowActivated = false;
        w.Loaded += async delegate
        {
            try { await Run(w); }
            catch (Exception e) { Log("HARNESS ERROR " + e); fails++; }
            Log("passes=" + passes + " fails=" + fails);
            Process.GetCurrentProcess().Kill();
        };
        w.Show();
        app.Run();
    }

    static async Task<ChatWindow> OpenChat(WidgetWindow w)
    {
        int before = Application.Current.Windows.OfType<ChatWindow>().Count();
        Call(w, "OpenChatWindow");
        await Task.Delay(400);
        return Application.Current.Windows.OfType<ChatWindow>().Last();
    }

    static async Task Run(WidgetWindow w)
    {
        ChatWindow chat = await OpenChat(w);
        ContextMenu shown = null;
        chat.ShowAccountMenu = delegate(ContextMenu m) { shown = m; };
        Border row = (Border)Get(chat, "accountRow");

        // Sync starting from the widget's own right-click menu.
        ContextMenu own = w.ContextMenu;
        MenuItem ownTop = Item(own, "Always on top");
        bool startTop = w.Topmost;
        ownTop.IsChecked = !startTop;
        ownTop.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Key(row, System.Windows.Input.Key.Enter);
        Check("keyboard: Enter on the account row opens the menu", shown != null);
        string hdr = shown == null ? "" : string.Join(" / ", ((StackPanel)shown.Items[0]).Children.OfType<TextBlock>().Select(t => t.Text));
        string side = ((TextBlock)Get(chat, "accountPlanLine")).Text;
        Check("no usage yet: the sidebar line and menu header don't show an empty percent", !side.Contains("  used") && !hdr.Contains(" / of the") && !hdr.Contains("·  of"), "sidebar \"" + side + "\"; header \"" + hdr + "\"; SevenDayText \"" + w.SevenDayText + "\"");
        Check("sync: Always on top changed in the widget's menu shows in the sidebar menu", shown != null && Item(shown, "Always on top").IsChecked == !startTop && w.Topmost == !startTop, "widget=" + w.Topmost);
        MenuItem ownRing = Item(own, "Ring color");
        MenuItem ownBlue = ownRing == null ? null : ownRing.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Header as string) != "Default" && (m.Tag as string) != "default");
        if (ownBlue != null) ownBlue.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        shown = null; Key(row, System.Windows.Input.Key.Space);
        MenuItem ticked = shown == null ? null : Item(shown, "Ring color").Items.OfType<MenuItem>().FirstOrDefault(m => m.IsChecked);
        Check("sync: a ring colour picked in the widget's menu is the one ticked in the sidebar menu (Space opens it too)", ownBlue != null && ticked != null && Equals(ticked.Tag, ownBlue.Tag), (ticked == null ? "none" : ticked.Header + "") + " vs " + (ownBlue == null ? "none" : ownBlue.Header + ""));
        Check("sync: only one ring colour ticked", shown != null && Item(shown, "Ring color").Items.OfType<MenuItem>().Count(m => m.IsChecked) == 1);
        // and back: sidebar menu Always on top flips the widget's own item
        MenuItem sideTop = Item(shown, "Always on top");
        sideTop.IsChecked = startTop;
        sideTop.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check("sync: and the sidebar's Always on top flips the widget's own tick back", ownTop.IsChecked == startTop && w.Topmost == startTop);
        string field = File.ReadAllText(Path.Combine(root, "settings.txt")).Split(',')[2];
        Check("sync: settings.txt field 3 follows", field == (startTop ? "1" : "0"), field);

        // Start with Windows in a test copy: both menus disabled, the real entry untouched.
        string runBefore = RunValue();
        MenuItem sideStart = Item(shown, "Start with Windows"), ownStart = Item(own, "Start with Windows");
        sideStart.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        w.ActionStartup(true); w.ActionStartup(false);
        Check("test copy: Start with Windows disabled in both menus, and clicks or ActionStartup leave the real entry alone", !sideStart.IsEnabled && !ownStart.IsEnabled && RunValue() == runBefore, "real entry " + (RunValue() == "absent" ? "absent" : "present"));

        // Keyboard: the gear opens Settings, not the menu; names for screen readers.
        Border gear = Walk(row).OfType<Border>().FirstOrDefault(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Settings");
        shown = null;
        if (gear != null) Key(gear, System.Windows.Input.Key.Enter);
        await Task.Delay(300);
        Check("keyboard: Enter on the gear opens Settings off screen and not the menu", gear != null && shown == null && Application.Current.Windows.OfType<SettingsWindow>().Any(s => s.Left <= -5000));
        foreach (SettingsWindow s in Application.Current.Windows.OfType<SettingsWindow>().ToList()) s.Close();
        Check("keyboard: the row and the gear are both tab stops", row.Focusable && gear != null && gear.Focusable && KeyboardNavigation.GetIsTabStop(row) && KeyboardNavigation.GetIsTabStop(gear));
        string rowName = System.Windows.Automation.AutomationProperties.GetName(row);
        Check("keyboard: the row's name says what it is and whose account", rowName.StartsWith("Account and widget menu: a.very.long"), rowName);

        // Layout: a long email trims inside the row at narrow widths.
        Border panel = (Border)Get(chat, "historyPanel");
        panel.Visibility = Visibility.Visible;
        TextBlock emailLine = (TextBlock)Get(chat, "accountEmail");
        foreach (double width in new[] { 441.0, 700.0, 1100.0 })
        {
            chat.Width = width; chat.UpdateLayout(); await Task.Delay(100);
            Grid line = (Grid)row.Child;
            Point rowRight = row.TranslatePoint(new Point(row.ActualWidth, 0), chat);
            Point gearRight = gear == null ? new Point() : gear.TranslatePoint(new Point(gear.ActualWidth, 0), chat);
            Point emailRight = emailLine.TranslatePoint(new Point(emailLine.ActualWidth, 0), chat);
            Point gearLeft = gear == null ? new Point() : gear.TranslatePoint(new Point(0, 0), chat);
            bool fits = row.ActualWidth > 0 && gearRight.X <= rowRight.X + 0.5 && emailRight.X <= gearLeft.X + 0.5;
            Check("layout: chat " + width + " wide: the long email trims before the gear, and the gear stays inside the row", fits,
                "row " + row.ActualWidth.ToString("F0") + ", email ends " + emailRight.X.ToString("F0") + ", gear " + gearLeft.X.ToString("F0") + "-" + gearRight.X.ToString("F0") + ", row ends " + rowRight.X.ToString("F0"));
        }

        // Exit with a busy chat.
        ChatWindow second = await OpenChat(w);
        Set(chat, "busy", true);
        string asked = null;
        w.ConfirmExit = delegate(string q) { asked = q; return false; };
        Item(own, "Exit").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check("exit: with a chat working, the question mentions the running reply", asked != null && asked.Contains("still working"), asked ?? "not asked");
        Check("exit: No keeps the widget and both chats", w.IsVisible && ChatWindow.OpenCount == 2, "chats " + ChatWindow.OpenCount);
        Set(chat, "busy", false);
        asked = null;
        w.ConfirmExit = delegate(string q) { asked = q; return true; };
        ContextMenu fromSidebar = w.CreateActionsMenu();
        Item(fromSidebar, "Exit").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Task.Delay(300);
        Check("exit: from the sidebar menu with 2 idle chats, it asks with the count, and Yes closes everything", asked != null && asked.Contains("all 2 chat windows") && ChatWindow.OpenCount == 0 && !w.IsVisible, (asked ?? "not asked") + "; chats " + ChatWindow.OpenCount);
    }

    static IEnumerable<DependencyObject> Walk(DependencyObject o)
    {
        yield return o;
        int n = VisualTreeHelper.GetChildrenCount(o);
        for (int i = 0; i < n; i++) foreach (DependencyObject c in Walk(VisualTreeHelper.GetChild(o, i))) yield return c;
    }
}
