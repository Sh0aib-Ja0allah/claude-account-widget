using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
// C3d: the chat sidebar's account row and its menu of the widget's own actions. Off screen; no popup is opened.
static partial class H4
{
    static void ClickBorder(Border b)
    {
        b.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
    }

    static MenuItem ItemNamed(ContextMenu m, string header) { return m.Items.OfType<MenuItem>().FirstOrDefault(i => (i.Header as string) == header); }

    static async Task SidebarMenu()
    {
        double reset = Epoch(DateTime.UtcNow.AddDays(3));
        string d = Fresh("c3d");
        File.WriteAllText(Path.Combine(d, "settings.txt"), "-6000,0,0,0,default,system,0,1,off");
        Usage(d, 52, reset);
        WidgetWindow w = NewWidget(d, true);
        w.ShowActivated = false; w.Show();
        await Task.Delay(200);
        Call(w, "OpenChatWindow");
        await Task.Delay(300);
        ChatWindow chat = Application.Current.Windows.OfType<ChatWindow>().Last();
        Border row = (Border)Get(chat, "accountRow");
        TextBlock email = (TextBlock)Get(chat, "accountEmail");
        TextBlock plan = (TextBlock)Get(chat, "accountPlanLine");
        TextBlock initial = (TextBlock)Get(chat, "accountInitial");
        Check("row: shown in a chat opened from the widget", row.Visibility == Visibility.Visible);
        Check("row: email, plan and usage from the widget", email.Text == "sample@example.com" && plan.Text == "Max · 52% used" && initial.Text == "S", email.Text + " | " + plan.Text + " | " + initial.Text);
        Check("row: keyboard-reachable with an accessible name", row.Focusable && System.Windows.Automation.AutomationProperties.GetName(row).Contains("sample@example.com"));

        // Kept current
        Usage(d, 61, reset);
        Call(w, "Refresh");
        Check("row: follows a usage change", plan.Text == "Max · 61% used", plan.Text);

        // The menu
        ContextMenu shown = null;
        chat.ShowAccountMenu = delegate(ContextMenu m) { shown = m; };
        ClickBorder(row);
        Check("menu: opens from the row", shown != null);
        List<string> order = shown.Items.Cast<object>().Select(o => o is MenuItem ? (string)((MenuItem)o).Header : o is Separator ? "---" : "header").ToList();
        Check("menu: header, then every widget action in order", string.Join(" | ", order) == "header | --- | Usage history | Settings | Ring color | Always on top | Start with Windows | Refresh now | --- | Exit", string.Join(" | ", order));
        string headerText = string.Join(" ", ((StackPanel)shown.Items[0]).Children.OfType<TextBlock>().Select(t => t.Text));
        Check("menu: header has email, plan and 7-day %", headerText == "sample@example.com Max · 61% of the 7-day limit used", headerText);
        Check("menu: Start with Windows disabled in a test copy", !ItemNamed(shown, "Start with Windows").IsEnabled);
        Check("menu: six ring colours with the current one ticked", ItemNamed(shown, "Ring color").Items.Count == 6 && ItemNamed(shown, "Ring color").Items.OfType<MenuItem>().Single(i => i.IsChecked).Tag as string == "default");

        // Shared actions stay in sync with the widget's own menu
        MenuItem top = ItemNamed(shown, "Always on top");
        bool wasTop = w.Topmost;
        top.IsChecked = !wasTop;
        top.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check("shared: Always on top changes the widget and ticks its own menu", w.Topmost == !wasTop && ((MenuItem)Get(w, "topmostItem")).IsChecked == !wasTop
            && File.ReadAllText(Path.Combine(d, "settings.txt")).Split(',')[2] == (!wasTop ? "1" : "0"));
        MenuItem blue = ItemNamed(shown, "Ring color").Items.OfType<MenuItem>().First(i => (string)i.Tag == "blue");
        blue.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        MenuItem widgetBlue = ((MenuItem)Get(w, "accentItem")).Items.OfType<MenuItem>().First(i => (string)i.Tag == "blue");
        Check("shared: a ring colour changes the widget and ticks its own menu", (string)Get(w, "accentName") == "blue" && widgetBlue.IsChecked);
        shown = null; ClickBorder(row);
        Check("menu: reopened, it shows the new state", ItemNamed(shown, "Always on top").IsChecked == !wasTop
            && ItemNamed(shown, "Ring color").Items.OfType<MenuItem>().Single(i => i.IsChecked).Tag as string == "blue");
        Check("row: the initial follows the ring colour", ((Border)Get(chat, "accountInitialDisc")).Background is System.Windows.Media.SolidColorBrush);
        ItemNamed(shown, "Ring color").Items.OfType<MenuItem>().First(i => (string)i.Tag == "default").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        ItemNamed(shown, "Settings").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        ItemNamed(shown, "Usage history").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Task.Delay(300);
        Check("shared: Settings and Usage history open (off screen in this test)", Application.Current.Windows.OfType<SettingsWindow>().Any(x => x.Left == -6000)
            && Application.Current.Windows.OfType<UsageHistoryWindow>().Any(x => x.Left == -6000));
        foreach (Window x in Application.Current.Windows.OfType<Window>().Where(x => x is SettingsWindow || x is UsageHistoryWindow).ToArray()) x.Close();

        // The gear opens Settings
        Border gear = ((Grid)row.Child).Children.OfType<Border>().Last();
        shown = null;
        ClickBorder(gear);
        await Task.Delay(300);
        Check("gear: opens Settings, without opening the menu", shown == null && Application.Current.Windows.OfType<SettingsWindow>().Count() == 1 && System.Windows.Automation.AutomationProperties.GetName(gear) == "Settings");
        foreach (SettingsWindow x in Application.Current.Windows.OfType<SettingsWindow>().ToArray()) x.Close();

        // Rendered sidebar, both themes
        Border panel = (Border)Get(chat, "historyPanel");
        panel.Visibility = Visibility.Visible;
        chat.Width = 900; chat.UpdateLayout();
        foreach (string theme in new[] { "dark", "light" }) { ThemeOf(host, theme); chat.UpdateLayout(); ShotOf((FrameworkElement)chat.Content, "sidebar-account-" + theme); }
        ThemeOf(host, "dark");

        // Exit asks first when a chat is open, and a "no" keeps everything
        string asked = null;
        w.ConfirmExit = delegate(string q) { asked = q; return false; };
        shown = null; ClickBorder(row);
        ItemNamed(shown, "Exit").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check("exit: asks first with a chat open, and no keeps the widget and chat", asked == "Exit closes the widget and the open chat window." && w.IsVisible && chat.IsVisible, asked ?? "not asked");
        Set(chat, "busy", true);
        asked = null;
        ((MenuItem)((ContextMenu)w.ContextMenu).Items.OfType<MenuItem>().First(i => (i.Header as string) == "Exit")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check("exit: the widget's own menu asks too, and mentions a running reply", asked != null && asked.StartsWith("A chat is still working on a reply."), asked ?? "not asked");
        Set(chat, "busy", false);
        w.ConfirmExit = delegate(string q) { return true; };
        ItemNamed(shown, "Exit").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Task.Delay(200);
        Check("exit: yes closes the widget and every chat", !w.IsVisible && !chat.IsVisible && ChatWindow.OpenCount == 0);

        // No chats open: Exit doesn't ask
        WidgetWindow solo = NewWidget(Fresh("c3d-solo"), true);
        solo.ShowActivated = false; solo.Show();
        bool askedSolo = false;
        solo.ConfirmExit = delegate(string q) { askedSolo = true; return false; };
        solo.ActionExit();
        Check("exit: no chats open, no question", !askedSolo && !solo.IsVisible);

        // D1: before any usage figure, just the plan in the row and "usage not checked yet" in the menu
        string nd = Fresh("c3d-nousage");
        File.WriteAllText(Path.Combine(nd, "settings.txt"), "-6000,0,0,0,default,system,0,1,off");
        WidgetWindow nw = NewWidget(nd, true);
        nw.ShowActivated = false; nw.Show();
        Call(nw, "OpenChatWindow");
        await Task.Delay(300);
        ChatWindow nc = Application.Current.Windows.OfType<ChatWindow>().Last();
        ContextMenu nm = null;
        nc.ShowAccountMenu = delegate(ContextMenu m) { nm = m; };
        ClickBorder((Border)Get(nc, "accountRow"));
        string nPlan = ((TextBlock)Get(nc, "accountPlanLine")).Text;
        string nHeader = string.Join(" ", ((StackPanel)nm.Items[0]).Children.OfType<TextBlock>().Select(t => t.Text));
        Check("D1: no usage yet, the row shows just the plan", nPlan == "Max", nPlan);
        Check("D1: no usage yet, the menu header says so", nHeader == "sample@example.com Max \u00b7 usage not checked yet", nHeader);
        nw.ConfirmExit = delegate(string q) { return true; };
        nw.ActionExit();

        // An unknown account still gets the row and every action
        string ud = Fresh("c3d-unknown");
        File.WriteAllText(Path.Combine(ud, "settings.txt"), "-6000,0,0,0,default,system,0,1,off");
        WidgetWindow uw = new WidgetWindow(ud, Path.Combine(ud, "missing.json"), null, null, 300, 0, true);
        uw.ShowActivated = false; uw.Show();
        Call(uw, "OpenChatWindow");
        await Task.Delay(300);
        ChatWindow uc = Application.Current.Windows.OfType<ChatWindow>().Last();
        ContextMenu um = null;
        uc.ShowAccountMenu = delegate(ContextMenu m) { um = m; };
        ClickBorder((Border)Get(uc, "accountRow"));
        Check("unknown account: the row shows without an email, and the menu keeps every action", ((TextBlock)Get(uc, "accountEmail")).Text == "Account unknown"
            && ((TextBlock)Get(uc, "accountInitial")).Text == "?" && um != null && um.Items.OfType<MenuItem>().Count() == 7);
        uw.ConfirmExit = delegate(string q) { return true; };
        uw.ActionExit();

        // A chat that isn't from a widget (the chat harness) shows no row
        ChatWindow bare = new ChatWindow(Fresh("c3d-bare"));
        Check("no widget: no account row", ((Border)Get(bare, "accountRow")).Visibility == Visibility.Collapsed);
        bare.Close();
    }
}
