using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
// Hand-test fixes: live test copies open their windows on screen; automated ones keep them off screen.
// Test copies never read or change the real "Start with Windows" entry. No window is shown on the real screen here.
static partial class H4
{
    static string RunValue()
    {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
        {
            object v = key == null ? null : key.GetValue("ClaudeAccountWidget");
            return v == null ? "(none)" : v.ToString();
        }
    }

    static async Task Live()
    {
        // Who keeps windows off screen
        WidgetWindow auto = NewWidget(Fresh("live-auto"), true);
        Check("automated test mode: windows stay off screen", auto.OffScreenWindows);
        auto.ShowTestWindows = true;
        Check("live test mode: windows show normally", !auto.OffScreenWindows);
        auto.ShowTestWindows = false;
        WidgetWindow real = NewWidget(Fresh("live-real"), false);
        Check("normal build: windows show normally", !real.OffScreenWindows);

        // The automated path still opens Settings, history and chat off screen
        auto.ShowActivated = false;
        auto.Left = -6000;
        auto.Show();
        Call(auto, "OpenSettings");
        Call(auto, "OpenHistory");
        Call(auto, "OpenChatWindow");
        await Task.Delay(300);
        Check("automated: Settings off screen", Application.Current.Windows.OfType<SettingsWindow>().All(x => x.Left == -6000));
        Check("automated: history off screen", Application.Current.Windows.OfType<UsageHistoryWindow>().All(x => x.Left == -6000));
        Check("automated: chat off screen", Application.Current.Windows.OfType<ChatWindow>().All(x => x.Left == -6000));
        foreach (Window x in Application.Current.Windows.OfType<Window>().Where(x => x is SettingsWindow || x is UsageHistoryWindow || x is ChatWindow).ToArray()) x.Close();

        string src = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(root)), "snapshot", "widget.cs"));
        Check("Program: live sets ShowTestWindows (by reading the source)", src.Contains("bool live = isTest && args.Length >= 7 && args[6] == \"live\";\r\n        widget.ShowTestWindows = live;"));

        // Start with Windows in a test copy
        string before = RunValue();
        MenuItem startup = (MenuItem)Get(auto, "startupItem");
        Check("test copy: Start with Windows is shown disabled and unticked", !startup.IsEnabled && !startup.IsChecked && startup.Header as string == "Start with Windows");
        startup.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check("test copy: a click can't change the real startup entry", RunValue() == before, before);
        MenuItem realStartup = (MenuItem)Get(real, "startupItem");
        Check("normal build: Start with Windows stays enabled", realStartup.IsEnabled);
        auto.Close();
        real.Close();
    }
}
