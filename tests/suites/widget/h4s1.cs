using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Media.Imaging;
// S1: the Settings window. Every window stays off screen; the folder dialog is replaced through PickFolder.
static partial class H4
{
    static string Stamp(string file)
    {
        if (!File.Exists(file)) return "missing";
        using (MD5 md5 = MD5.Create())
            return BitConverter.ToString(md5.ComputeHash(File.ReadAllBytes(file))) + "@" + File.GetLastWriteTimeUtc(file).Ticks;
    }

    static string[] DataFiles = { "expected-account.txt", "chat-history-folder.txt", "chat-read-folders.txt", "chat-access.txt", "settings.txt" };

    static string AllStamps(string dir)
    {
        return string.Join("|", DataFiles.Select(f => Stamp(Path.Combine(dir, f)))) + "|" + Stamp(account);
    }

    static void ThemeOf(WidgetWindow w, string theme)
    {
        Set(w, "themeSetting", theme);
        Call(w, "ApplyTheme");
        Call(w, "ApplyAccent");
    }

    static void ShotOf(FrameworkElement e, string name)
    {
        e.UpdateLayout();
        RenderTargetBitmap bmp = new RenderTargetBitmap((int)Math.Max(1, e.ActualWidth), (int)Math.Max(1, e.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(e);
        PngBitmapEncoder png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using (FileStream f = File.Create(Path.Combine(root, name + ".png"))) png.Save(f);
        Log("SHOT " + Path.Combine(root, name + ".png"));
    }

    static string Msg(SettingsWindow s) { return ((TextBlock)Get(s, "message")).Text; }
    static TextBox Box(SettingsWindow s, string f) { return (TextBox)Get(s, f); }

    static List<string> ReadRows(SettingsWindow s)
    {
        List<string> rows = new List<string>();
        foreach (UIElement row in ((StackPanel)Get(s, "readList")).Children)
        {
            DockPanel dock = row as DockPanel;
            if (dock == null) { rows.Add("HINT:" + ((TextBlock)row).Text); continue; }
            rows.Add(string.Join("+", dock.Children.OfType<TextBlock>().Select(t => t.Text)));
        }
        return rows;
    }

    static async Task S1()
    {
        // R-A-01: menu item, one window, test mode keeps it off screen and out of the foreground
        bool hasItem = host.ContextMenu.Items.OfType<MenuItem>().Any(m => (m.Header as string) == "Settings");
        Check("settings: menu has Settings", hasItem);
        Call(host, "OpenSettings");
        Call(host, "OpenSettings");
        await Task.Delay(300);
        SettingsWindow[] open = Application.Current.Windows.OfType<SettingsWindow>().ToArray();
        IntPtr handle = open.Length == 0 ? IntPtr.Zero : new System.Windows.Interop.WindowInteropHelper(open[0]).Handle;
        Check("settings: opening twice gives one window", open.Length == 1, open.Length.ToString());
        Check("settings: test mode keeps it off screen and not in front", open.Length == 1 && open[0].Left == -6000 && GetForegroundWindow() != handle);

        // R-A-02: both themes, and a theme change redraws
        if (open.Length == 1)
        {
            SettingsWindow sw = open[0];
            Border card = (Border)Get(sw, "card");
            ThemeOf(host, "dark");
            await Task.Delay(100);
            Brush darkFill = card.Background;
            Color darkWarn = ((SolidColorBrush)Get(sw, "warnText")).Color;
            ShotOf(card, "settings-dark");
            ThemeOf(host, "light");
            await Task.Delay(100);
            Check("settings: theme change redraws the card", !ReferenceEquals(darkFill, card.Background));
            Check("settings: warning color follows the theme", ((SolidColorBrush)Get(sw, "warnText")).Color != darkWarn);
            Check("settings: text uses the live theme brush", ((TextBox)Get(sw, "expectedBox")).Foreground == WidgetWindow.Text && WidgetWindow.Text.Color.R < 0x80, WidgetWindow.Text.Color.ToString());
            ShotOf(card, "settings-light");
            ThemeOf(host, "dark");
            sw.Close();
            await Task.Delay(100);
            Check("settings: closing clears the single-window slot", Get(host, "settingsWindow") == null);
            int handlers = typeof(WidgetWindow).GetField("AppearanceChanged", Any).GetValue(null) == null ? 0
                : ((Delegate)typeof(WidgetWindow).GetField("AppearanceChanged", Any).GetValue(null)).GetInvocationList().Count(x => x.Target is SettingsWindow);
            Check("settings: closed window stops following the theme", handlers == 0, handlers.ToString());
        }

        // R-A-03, R-A-04, R-A-05: expected account applies at once, clearing removes it, sign-in files untouched
        string d = Fresh("s1-account");
        WidgetWindow w = NewWidget(d, true);
        string accountBefore = Stamp(account);
        SettingsWindow s = new SettingsWindow(w, d);
        Box(s, "expectedBox").Text = "someone.else@example.com";
        s.Save();
        TextBlock note = (TextBlock)Get(w, "accountNote");
        Check("expected: saved and applied without a restart", File.ReadAllText(Path.Combine(d, "expected-account.txt")) == "someone.else@example.com" && note.Text == "Couldn't verify account", note.Text); // C3b/C1: unverified with no tiers gives the amber note (h4acct covers red and amber)
        Box(s, "expectedBox").Text = "  ";
        s.Save();
        Check("expected: clearing removes the file and the warning", !File.Exists(Path.Combine(d, "expected-account.txt")) && note.Text == "Signed in to Claude Code", note.Text);
        Box(s, "expectedBox").Text = "SAMPLE@example.com";
        s.Save();
        Check("expected: matching account in another case shows no warning", note.Text == "Signed in to Claude Code", note.Text);
        Check("expected: the account file is never written", Stamp(account) == accountBefore);
        s.Close(); w.Close();

        // R-A-06: load then save with no edits writes nothing
        d = Fresh("s1-noop");
        File.WriteAllText(Path.Combine(d, "expected-account.txt"), "sample@example.com\r\n");
        File.WriteAllText(Path.Combine(d, "chat-history-folder.txt"), "\"%USERPROFILE%\\Notes\"\r\n");
        File.WriteAllText(Path.Combine(d, "chat-read-folders.txt"), "Q:\\gone\r\n\r\n  %USERPROFILE%  \r\n");
        File.WriteAllText(Path.Combine(d, "chat-access.txt"), "Laptop\n");
        File.WriteAllText(Path.Combine(d, "settings.txt"), "-6000,0,0,0,default,system");
        w = NewWidget(d, true);
        await Task.Delay(50);
        string before = AllStamps(d);
        s = new SettingsWindow(w, d);
        s.Save();
        Check("no edits: nothing written", AllStamps(d) == before);
        Check("no edits: message says so", Msg(s) == "Nothing to save.", Msg(s));
        Check("laptop: odd case 'Laptop' reads as on, like the chat", (bool)Get(s, "laptopOn"));

        // R-A-07, R-A-08: a missing folder survives adding another, %VAR% and quotes stay as written
        List<string> rows = ReadRows(s);
        Check("read folders: missing folder shown as not found", rows.Count == 2 && rows[0] == "not found+Q:\\gone" && rows[1] == "%USERPROFILE%", string.Join(" | ", rows));
        string extra = Fresh("s1-extra");
        string pickedTitle = null, pickedButton = null;
        s.PickFolder = delegate(string t, string b) { pickedTitle = t; pickedButton = b; return extra; };
        Call(s, "Pick", "Choose a folder every chat can read", "Add folder");
        ((List<string>)Get(s, "readLines")).Add(extra);
        s.Save();
        string[] lines = File.ReadAllLines(Path.Combine(d, "chat-read-folders.txt"));
        Check("read folders: missing line kept, new one added", lines.Length == 3 && lines[0] == "Q:\\gone" && lines[1] == "%USERPROFILE%" && lines[2] == extra, string.Join(" | ", lines));
        Check("read folders: picker gets the settings wording", pickedTitle == "Choose a folder every chat can read" && pickedButton == "Add folder");
        Check("history: untouched when only another field changed", File.ReadAllText(Path.Combine(d, "chat-history-folder.txt")) == "\"%USERPROFILE%\\Notes\"\r\n");
        Box(s, "historyBox").Text = "%USERPROFILE%\\Chats";
        s.Save();
        Check("history: %VAR% path saved as written", File.ReadAllText(Path.Combine(d, "chat-history-folder.txt")) == "%USERPROFILE%\\Chats");
        Check("expected: untouched by other saves", File.ReadAllText(Path.Combine(d, "expected-account.txt")) == "sample@example.com\r\n");

        // Removing through the row button
        StackPanel list = (StackPanel)Get(s, "readList");
        Border remove = ((DockPanel)list.Children[0]).Children.OfType<Border>().First();
        remove.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        s.Save();
        lines = File.ReadAllLines(Path.Combine(d, "chat-read-folders.txt"));
        Check("read folders: remove takes out only that line", lines.Length == 2 && lines[0] == "%USERPROFILE%" && lines[1] == extra, string.Join(" | ", lines));

        // R-A-11: the note is shown, and a new chat uses the new values
        string historyTarget = Path.Combine(Fresh("s1-hist"), "chats");
        Box(s, "historyBox").Text = historyTarget;
        s.Save();
        ChatWindow chat = new ChatWindow(d);
        Check("new chat: uses the saved history folder", (string)Get(chat, "historyFolder") == historyTarget, (string)Get(chat, "historyFolder") ?? "null");
        chat.Close();
        bool noteShown = FindTexts(s).Any(t => t == "Open chats keep what they started with. New chats use these.");
        Check("settings: open-chats note shown", noteShown);
        s.Close(); w.Close();

        // R-A-09, R-A-10: laptop access needs the warning confirmed, off removes the file, odd content is off
        d = Fresh("s1-laptop");
        w = NewWidget(d, true);
        s = new SettingsWindow(w, d);
        string access = Path.Combine(d, "chat-access.txt");
        Call(s, "ToggleLaptop");
        Check("laptop: toggle shows the warning and writes nothing", !File.Exists(access) && ((Border)Get(s, "laptopWarning")).Visibility == Visibility.Visible);
        Check("laptop: warning has the README wording", FindTexts(s).Any(t => t.Contains("Read files on your fixed drives, without asking.")) && FindTexts(s).Any(t => t.Contains("a command can do anything your Windows account can do")));
        Call(s, "SetLaptop", true);
        Check("laptop: Turn on writes the file", File.Exists(access) && File.ReadAllText(access) == "laptop" && ((Border)Get(s, "laptopWarning")).Visibility == Visibility.Collapsed);
        Call(s, "ToggleLaptop");
        Check("laptop: off removes the file at once", !File.Exists(access) && !(bool)Get(s, "laptopOn"));
        s.Close();
        File.WriteAllText(access, "laptop please");
        s = new SettingsWindow(w, d);
        Check("laptop: unusual content reads as off", !(bool)Get(s, "laptopOn"));
        string accessBefore = Stamp(access);
        s.Save();
        Check("laptop: unusual file left alone by Save", Stamp(access) == accessBefore);

        // R-A-12: bad history paths are refused and nothing is written
        Box(s, "expectedBox").Text = "x@example.com";
        string beforeBad = AllStamps(d);
        foreach (string bad in new[] { "notes\\x", "%UNSET_WIDGET_TEST%\\x", "\\rooted-without-drive" })
        {
            Box(s, "historyBox").Text = bad;
            s.Save();
            Check("history: refused " + bad, AllStamps(d) == beforeBad && Msg(s).Contains("Nothing was saved"), Msg(s));
        }
        Box(s, "historyBox").Text = "%USERPROFILE%\\x";
        s.Save();
        Check("history: valid %VAR% path accepted", File.Exists(Path.Combine(d, "chat-history-folder.txt")) && File.ReadAllText(Path.Combine(d, "chat-history-folder.txt")) == "%USERPROFILE%\\x" && Msg(s) == "Saved.", Msg(s));
        Box(s, "historyBox").Text = "";
        s.Save();
        Check("history: empty removes the file", !File.Exists(Path.Combine(d, "chat-history-folder.txt")));

        // R-A-13: a read-only file shows a message, other fields still save, widget keeps working
        string expectedFile = Path.Combine(d, "expected-account.txt");
        File.SetAttributes(expectedFile, FileAttributes.ReadOnly);
        Box(s, "expectedBox").Text = "y@example.com";
        Box(s, "historyBox").Text = "%USERPROFILE%\\y";
        bool threw = false;
        try { s.Save(); } catch (Exception e) { threw = true; Log("THREW " + e); }
        Check("read-only: no crash, message names the file", !threw && Msg(s).StartsWith("Couldn't save expected-account.txt"), Msg(s));
        Check("read-only: other field still saved", File.ReadAllText(Path.Combine(d, "chat-history-folder.txt")) == "%USERPROFILE%\\y");
        Check("read-only: old value kept", File.ReadAllText(expectedFile) == "x@example.com" && w.ExpectedAccount == "x@example.com", w.ExpectedAccount);
        threw = false;
        try { Call(w, "Refresh"); } catch (Exception) { threw = true; }
        Check("read-only: widget keeps refreshing", !threw);
        File.SetAttributes(expectedFile, FileAttributes.Normal);
        s.Close(); w.Close();

        // F1 (Worker A): one full-path rule for Settings and the chat's readers
        foreach (string bad in new[] { "C:notes", "C:", "c:x\\y", "\\notes", "notes", "%UNSET_WIDGET_TEST%\\x", "\\\\server", "" })
            Check("full path: refused [" + bad + "]", SettingPaths.FullFolder(bad) == null && (bad.Length == 0 || SettingsWindow.HistoryProblem(bad) != null));
        foreach (string good in new[] { "C:\\Notes\\Chats", "C:/Notes", "\"C:\\Notes\"", "\\\\server\\share\\x", "%USERPROFILE%\\x" })
            Check("full path: accepted [" + good + "]", SettingPaths.FullFolder(good) != null && SettingsWindow.HistoryProblem(good) == null, SettingPaths.FullFolder(good) ?? "null");
        string rd = Fresh("s1-f1");
        File.WriteAllText(Path.Combine(rd, "chat-history-folder.txt"), "C:notes");
        File.WriteAllText(Path.Combine(rd, "chat-read-folders.txt"), "C:Users\r\n" + Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\r\n");
        ChatWindow fc = new ChatWindow(rd);
        Check("chat reader: drive-relative history folder is refused", Get(fc, "historyFolder") == null);
        List<string> rf = (List<string>)Get(fc, "readFolders");
        Check("chat reader: drive-relative read line is skipped, full one kept", rf.Count == 1 && rf[0] == Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), string.Join("|", rf));
        fc.Close();
        File.WriteAllText(Path.Combine(rd, "chat-history-folder.txt"), Path.Combine(rd, "hist"));
        fc = new ChatWindow(rd);
        Check("chat reader: a normal history folder still works", (string)Get(fc, "historyFolder") == Path.Combine(rd, "hist"));
        fc.Close();

        // F2, F3 (Worker A): Tab order and the close button's name
        WidgetWindow tw = NewWidget(Fresh("s1-tab"), true);
        SettingsWindow ts = new SettingsWindow(tw, Path.Combine(root, "s1-tab"));
        Call(tw, "ShowOffScreen", ts);
        ts.UpdateLayout();
        List<string> order = new List<string>();
        WalkFocusable(ts, order);
        string joined = string.Join(" > ", order);
        Check("tab order: history box before Choose", joined.IndexOf("Chat history folder") >= 0 && joined.IndexOf("Chat history folder") < joined.IndexOf("Choose"), joined);
        bool pinnedLayout = typeof(WidgetWindow).GetProperty("OffScreenWindows", Any) != null;
        if (!pinnedLayout) Note("tab order: this stage predates the pinned footer (C3c), so the K1 checks are skipped; order here is " + joined);
        if (pinnedLayout)
        Check("tab order: starts at the first field, ends with the pinned Close and Save (Worker A's K1)", order.Count > 3 && order[0] == "Expected account"
            && order[order.Count - 2] == "Close" && order[order.Count - 1] == "Save" && order.Count(x => x == "Close") == 1, joined);
        Border headerX = FindHeaderClose(ts);
        if (pinnedLayout)
        Check("tab order: the header's x is out of the Tab order but still named", headerX != null && !KeyboardNavigation.GetIsTabStop(headerX)
            && System.Windows.Automation.AutomationProperties.GetName(headerX) == "Close");
        ts.Close(); tw.Close();

        // A-06: the chat's picker call keeps its wording
        ParameterInfo[] ps = typeof(FolderPicker).GetMethod("Choose", Any).GetParameters();
        Check("picker: defaults keep the chat wording", ps.Length == 3 && (string)ps[1].DefaultValue == "Choose a folder Claude can read in this chat" && (string)ps[2].DefaultValue == "Add folder");
    }

    // Focusable elements in visual order, by their accessible name.
    static void WalkFocusable(DependencyObject node, List<string> names)
    {
        UIElement e = node as UIElement;
        if (e != null && e.Focusable && KeyboardNavigation.GetIsTabStop(e) && (e is TextBox || e is Border))
        {
            string name = System.Windows.Automation.AutomationProperties.GetName(e);
            names.Add(name.Length > 0 ? name : "(unnamed " + e.GetType().Name + ")");
        }
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++) WalkFocusable(VisualTreeHelper.GetChild(node, i), names);
    }

    // The header's x: a named Close border whose child is the icon glyph, not the word.
    static Border FindHeaderClose(DependencyObject node)
    {
        Border b = node as Border;
        TextBlock t = b == null ? null : b.Child as TextBlock;
        if (t != null && System.Windows.Automation.AutomationProperties.GetName(b) == "Close" && t.Text != "Close") return b;
        int n = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++)
        {
            Border found = FindHeaderClose(VisualTreeHelper.GetChild(node, i));
            if (found != null) return found;
        }
        return null;
    }

    static List<string> FindTexts(DependencyObject rootElement)
    {
        List<string> texts = new List<string>();
        Walk(rootElement, texts);
        return texts;
    }

    static void Walk(object node, List<string> texts)
    {
        TextBlock tb = node as TextBlock;
        if (tb != null) texts.Add(tb.Text);
        DependencyObject d = node as DependencyObject;
        if (d == null) return;
        foreach (object child in LogicalTreeHelper.GetChildren(d)) Walk(child, texts);
    }
}
