using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
// Worker A's independent S1 checks (Settings window). Off screen only; the folder dialog is replaced; no real input.
// h6.exe <out folder>
static class H6
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static string root, account;
    static int fails, passes;
    static object Get(object o, string f) { return o.GetType().GetField(f, Any).GetValue(o); }
    static void Log(string s) { Console.WriteLine(s); Console.Out.Flush(); }
    static void Check(string what, bool ok, string info = "")
    {
        Log((ok ? "PASS " : "FAIL ") + what + (info.Length > 0 ? " [" + info.Replace("\r", "\\r").Replace("\n", "\\n") + "]" : ""));
        if (ok) passes++; else fails++;
    }
    static string Fresh(string name)
    {
        string d = Path.Combine(root, name);
        if (Directory.Exists(d)) { foreach (string f in Directory.GetFiles(d)) File.SetAttributes(f, FileAttributes.Normal); Directory.Delete(d, true); }
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "settings.txt"), "-6000,0,0,0,default,system");
        return d;
    }
    static string[] Names = { "expected-account.txt", "chat-history-folder.txt", "chat-read-folders.txt", "chat-access.txt", "settings.txt" };
    static string Stamp(string f)
    {
        if (!File.Exists(f)) return "missing";
        using (MD5 m = MD5.Create()) return BitConverter.ToString(m.ComputeHash(File.ReadAllBytes(f))) + "@" + File.GetLastWriteTimeUtc(f).Ticks;
    }
    static string Stamps(string d) { return string.Join("|", Names.Select(n => Stamp(Path.Combine(d, n)))) + "|" + Stamp(account); }
    static string Diff(string a, string b)
    {
        string[] x = a.Split('|'), y = b.Split('|');
        List<string> changed = new List<string>();
        for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) changed.Add(i < Names.Length ? Names[i] : "account");
        return changed.Count == 0 ? "none" : string.Join(",", changed);
    }
    static WidgetWindow NewWidget(string d) { return new WidgetWindow(d, account, null, null, 300, 0, true); }
    static SettingsWindow Open(WidgetWindow w, string d)
    {
        SettingsWindow s = new SettingsWindow(w, d);
        s.WindowStartupLocation = WindowStartupLocation.Manual; s.Left = -6000; s.Top = 0; s.ShowActivated = false; s.ShowInTaskbar = false;
        s.Show();
        return s;
    }
    static string Msg(SettingsWindow s) { return ((TextBlock)Get(s, "message")).Text; }
    static TextBox Box(SettingsWindow s, string f) { return (TextBox)Get(s, f); }
    static List<string> ChatFolders(string d)
    {
        return (List<string>)typeof(ChatWindow).GetMethod("ReadExtraFolders", Any).Invoke(null, new object[] { d });
    }
    static void Key(UIElement target, Key key, RoutedEvent ev)
    {
        PresentationSource src = PresentationSource.FromVisual(target);
        KeyEventArgs e = new KeyEventArgs(Keyboard.PrimaryDevice, src, 0, key);
        e.RoutedEvent = ev;
        target.RaiseEvent(e);
    }

    [STAThread]
    static void Main(string[] args)
    {
        root = args[0];
        Directory.CreateDirectory(root);
        account = Path.Combine(root, "account.json");
        File.WriteAllText(account, "{\"oauthAccount\":{\"emailAddress\":\"sample@example.com\",\"planDisplayName\":\"Max\"}}");
        Application app = new Application();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.DispatcherUnhandledException += (s, e) => { Log("UNHANDLED " + e.Exception); fails++; e.Handled = true; };
        try { Run(); } catch (Exception e) { Log("HARNESS ERROR " + e); fails++; }
        Log("passes=" + passes + " fails=" + fails);
        Process.GetCurrentProcess().Kill();
    }

    static void Run()
    {
        // ---------- (b) HistoryProblem: what it refuses and accepts ----------
        string longPath = "C:\\" + new string('a', 300);
        string[][] cases = {
            new[] { "refuse", "notes\\x" }, new[] { "refuse", ".\\x" }, new[] { "refuse", "..\\x" }, new[] { "refuse", "~\\notes" },
            new[] { "refuse", "\\x" }, new[] { "refuse", "/x" }, new[] { "refuse", "C:notes" }, new[] { "refuse", "C:" },
            new[] { "refuse", "%UNSET_WIDGET_A%\\x" }, new[] { "refuse", "C:\\a|b" }, new[] { "refuse", "C:\\x:y" },
            new[] { "refuse", "file:///C:/x" }, new[] { "refuse", "http://example.com/x" }, new[] { "refuse", longPath },
            new[] { "refuse", "\"notes\\x\"" },
            new[] { "accept", "C:\\Notes\\Chats" }, new[] { "accept", "C:/Notes/Chats" }, new[] { "accept", "\"C:\\Notes\\Chats\"" },
            new[] { "accept", "%USERPROFILE%\\Notes" }, new[] { "accept", "%SystemDrive%\\Notes" }, new[] { "accept", "\\\\server\\share\\chats" },
            new[] { "accept", "Z:\\Not\\There\\Yet" }, new[] { "accept", "  C:\\Notes  " }, new[] { "accept", "C:\\" },
        };
        foreach (string[] c in cases)
        {
            string p = SettingsWindow.HistoryProblem(c[1]);
            bool refused = p != null;
            Check("history rule: " + c[0] + " " + c[1].Substring(0, Math.Min(40, c[1].Length)), refused == (c[0] == "refuse"), refused ? p : "accepted");
        }
        foreach (string odd in new[] { "\\\\server", "\\\\server\\", "C:\\100%done", "C:\\CON", "\\\\?\\C:\\x", "\\\\.\\C:\\x" })
        {
            string p = SettingsWindow.HistoryProblem(odd);
            Log("NOTE history rule: " + odd + " -> " + (p ?? "accepted"));
        }
        Log("NOTE history rule: C:notes would resolve to " + Path.GetFullPath("C:notes") + " in this process (depends on the current folder of drive C)");

        // ---------- (a) R-A-06: hand-made fixtures, no edits, nothing written ----------
        string d = Fresh("noedit");
        Encoding bom = new UTF8Encoding(true);
        File.WriteAllText(Path.Combine(d, "expected-account.txt"), "  Sample@Example.com \r\n\r\n", bom);
        File.WriteAllText(Path.Combine(d, "chat-history-folder.txt"), "\"%USERPROFILE%\\Chats Folder\"\n", bom);
        File.WriteAllText(Path.Combine(d, "chat-read-folders.txt"), "\r\n  \"C:\\Windows\"  \nZ:\\No\\Drive\r\n\t%UNSET_WIDGET_A%\\x\r\n# not a comment, just a line\r\n\r\nC:\\Users\r\n", bom);
        File.WriteAllText(Path.Combine(d, "chat-access.txt"), "  LAPTOP \r\n");
        File.SetLastWriteTimeUtc(Path.Combine(d, "expected-account.txt"), new DateTime(2020, 1, 1));
        WidgetWindow w = NewWidget(d);
        string before = Stamps(d);
        SettingsWindow s = Open(w, d);
        Check("no edits: expected box shows the trimmed value", Box(s, "expectedBox").Text == "Sample@Example.com", Box(s, "expectedBox").Text);
        Check("no edits: history box keeps quotes and %VAR%", Box(s, "historyBox").Text == "\"%USERPROFILE%\\Chats Folder\"", Box(s, "historyBox").Text);
        Check("no edits: odd-case laptop file reads as on, like the chat", (bool)Get(s, "laptopOn"));
        s.Save();
        Check("no edits: Save with BOMs, mixed line ends, blanks and odd case writes nothing", Stamps(d) == before, Diff(before, Stamps(d)) + "; " + Msg(s));
        s.Save();
        Check("no edits: second Save writes nothing", Stamps(d) == before, Diff(before, Stamps(d)));
        s.Close();
        Check("no edits: Close writes nothing", Stamps(d) == before, Diff(before, Stamps(d)));
        SettingsWindow s2 = Open(w, d);
        Box(s2, "historyBox").Text = "\"%USERPROFILE%\\Chats Folder\"  ";
        Box(s2, "expectedBox").Text = " Sample@Example.com";
        s2.Save();
        Check("no edits: only added spaces count as no change", Stamps(d) == before, Diff(before, Stamps(d)) + "; " + Msg(s2));
        s2.Close();

        // An invalid history value already on disk doesn't block a save of something else.
        string dv = Fresh("badondisk");
        File.WriteAllText(Path.Combine(dv, "chat-history-folder.txt"), "notes\\x");
        WidgetWindow wv = NewWidget(dv);
        SettingsWindow sv = Open(wv, dv);
        string bv = Stamps(dv);
        sv.Save();
        Check("bad on disk: no-edit Save says nothing to save and writes nothing", Stamps(dv) == bv && Msg(sv) == "Nothing to save.", Msg(sv));
        Box(sv, "expectedBox").Text = "x@example.com";
        sv.Save();
        Check("bad on disk: another field still saves, history file untouched",
            File.Exists(Path.Combine(dv, "expected-account.txt")) && File.ReadAllText(Path.Combine(dv, "chat-history-folder.txt")) == "notes\\x", Msg(sv));
        sv.Close();

        // ---------- (a) R-A-07: missing lines survive edits to other lines ----------
        string dr = Fresh("readlines");
        string extra = Path.Combine(root, "extra folder"); Directory.CreateDirectory(extra);
        string original = "Z:\\No\\Drive\r\n%UNSET_WIDGET_A%\\x\r\n\"C:\\Windows\"\r\n  C:\\Users  \r\n\r\n%SystemRoot%\r\n";
        File.WriteAllText(Path.Combine(dr, "chat-read-folders.txt"), original);
        List<string> chatBefore = ChatFolders(dr);
        WidgetWindow wr = NewWidget(dr);
        SettingsWindow sr = Open(wr, dr);
        string picked = extra;
        sr.PickFolder = delegate(string t, string b) { return picked; };
        StackPanel list = (StackPanel)Get(sr, "readList");
        int notFound = list.Children.OfType<DockPanel>().Count(p => p.Children.OfType<TextBlock>().Any(t => t.Text == "not found"));
        Check("read lines: two missing lines marked not found", notFound == 2, notFound.ToString());
        Click(sr, "Add folder");
        sr.Save();
        string[] saved = File.ReadAllLines(Path.Combine(dr, "chat-read-folders.txt"));
        string[] want = { "Z:\\No\\Drive", "%UNSET_WIDGET_A%\\x", "\"C:\\Windows\"", "C:\\Users", "%SystemRoot%", extra };
        Check("read lines: missing drive and unset %VAR% lines kept in order, quotes kept, new folder last", saved.SequenceEqual(want), string.Join(" | ", saved));
        List<string> chatAfter = ChatFolders(dr);
        Check("read lines: the chat reads the same folders as before plus the new one",
            chatAfter.Take(chatBefore.Count).SequenceEqual(chatBefore) && chatAfter.Count == chatBefore.Count + 1 && chatAfter.Last() == Path.GetFullPath(extra),
            string.Join(";", chatBefore) + " => " + string.Join(";", chatAfter));
        picked = "C:\\Windows";
        int rowsBefore = ((List<string>)Get(sr, "readLines")).Count;
        Click(sr, "Add folder");
        Check("read lines: picking a folder already listed in quotes adds nothing", ((List<string>)Get(sr, "readLines")).Count == rowsBefore);
        Log("NOTE read lines: %SystemRoot% and C:\\Windows are both listed; the duplicate check compares text, not expanded paths");
        // Remove the unset %VAR% line through its button.
        Border remove = FindByName(sr, "Remove %UNSET_WIDGET_A%\\x") as Border;
        Check("read lines: remove button found by its name", remove != null);
        if (remove != null) Click(remove);
        sr.Save();
        saved = File.ReadAllLines(Path.Combine(dr, "chat-read-folders.txt"));
        Check("read lines: removing one line keeps the missing drive line", saved.SequenceEqual(new[] { "Z:\\No\\Drive", "\"C:\\Windows\"", "C:\\Users", "%SystemRoot%", extra }), string.Join(" | ", saved));
        Log("NOTE read lines: on the first edit, blank lines and spaces around lines are dropped; the chat ignores both");
        sr.Close();

        // ---------- (c) Esc, Enter/Space, tab order ----------
        string dk = Fresh("keys");
        WidgetWindow wk = NewWidget(dk);
        SettingsWindow sk = Open(wk, dk);
        Border sw = (Border)Get(sk, "laptopSwitch");
        Border warning = (Border)Get(sk, "laptopWarning");
        Key(sw, System.Windows.Input.Key.Space, Keyboard.KeyDownEvent);
        Check("keys: Space on the laptop switch shows the warning", warning.Visibility == Visibility.Visible);
        Check("keys: and writes nothing", !File.Exists(Path.Combine(dk, "chat-access.txt")));
        Key(Box(sk, "expectedBox"), System.Windows.Input.Key.Escape, Keyboard.PreviewKeyDownEvent);
        Check("keys: Esc (from a text box) closes the warning first", warning.Visibility == Visibility.Collapsed && sk.IsVisible);
        Check("keys: Esc on the warning writes nothing", !File.Exists(Path.Combine(dk, "chat-access.txt")));
        Key(sw, System.Windows.Input.Key.Enter, Keyboard.KeyDownEvent);
        Border turnOn = FindByName(sk, "Turn on") as Border;
        if (turnOn != null) Key(turnOn, System.Windows.Input.Key.Enter, Keyboard.KeyDownEvent);
        Check("keys: Enter on Turn on writes laptop", File.Exists(Path.Combine(dk, "chat-access.txt")) && File.ReadAllText(Path.Combine(dk, "chat-access.txt")) == "laptop");
        Key(sw, System.Windows.Input.Key.Space, Keyboard.KeyDownEvent);
        Check("keys: Space on the switch turns it off at once", !File.Exists(Path.Combine(dk, "chat-access.txt")) && warning.Visibility == Visibility.Collapsed);
        Box(sk, "expectedBox").Text = "k@example.com";
        Border save = FindByName(sk, "Save") as Border;
        if (save != null) Key(save, System.Windows.Input.Key.Enter, Keyboard.KeyDownEvent);
        Check("keys: Enter on Save saves", File.Exists(Path.Combine(dk, "expected-account.txt")), Msg(sk));
        List<string> order = TabOrder(sk);
        Log("NOTE tab order (tree order of focusable elements): " + string.Join(" > ", order));
        int iExp = order.IndexOf("Expected account"), iHist = order.IndexOf("Chat history folder"), iChoose = order.IndexOf("Choose");
        int iAdd = order.IndexOf("Add folder"), iSwitch = order.IndexOf("Laptop access for chats"), iSave = order.IndexOf("Save");
        Check("tab order: fields come in the order they are shown", iExp >= 0 && iExp < iHist && iHist < iAdd && iAdd < iSwitch && iSwitch < iSave, string.Join(",", new[] { iExp, iHist, iChoose, iAdd, iSwitch, iSave }));
        Check("tab order: history box before its Choose button", iHist >= 0 && iHist < iChoose, iHist + " vs " + iChoose);
        Key(sk, System.Windows.Input.Key.Escape, Keyboard.PreviewKeyDownEvent);
        Check("keys: Esc with no warning closes the window", !sk.IsVisible);

        // ---------- locked laptop file ----------
        string dl = Fresh("lockedlaptop");
        string acc = Path.Combine(dl, "chat-access.txt");
        File.WriteAllText(acc, "laptop");
        WidgetWindow wl = NewWidget(dl);
        SettingsWindow sl = Open(wl, dl);
        using (FileStream held = new FileStream(acc, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Key((Border)Get(sl, "laptopSwitch"), System.Windows.Input.Key.Space, Keyboard.KeyDownEvent);
        }
        Check("locked laptop file: turning off fails with a message and the switch stays on",
            File.Exists(acc) && (bool)Get(sl, "laptopOn") && Msg(sl).StartsWith("Couldn't change laptop access"), Msg(sl));
        sl.Close();

        // ---------- expected account: spaces only clears, and the warning follows ----------
        string de = Fresh("expected");
        File.WriteAllText(Path.Combine(de, "expected-account.txt"), "other@example.com");
        WidgetWindow we = NewWidget(de);
        SettingsWindow se = Open(we, de);
        Box(se, "expectedBox").Text = "   ";
        se.Save();
        Check("expected: spaces only removes the file and clears the field", !File.Exists(Path.Combine(de, "expected-account.txt")) && we.ExpectedAccount == "", Msg(se));
        se.Close();
    }

    static void Click(SettingsWindow s, string name) { Border b = FindByName(s, name) as Border; if (b != null) Click(b); else Log("NOTE no element named " + name); }
    static void Click(Border b) { Key(b, System.Windows.Input.Key.Enter, Keyboard.KeyDownEvent); }

    static IEnumerable<DependencyObject> Walk(DependencyObject o)
    {
        yield return o;
        int n = VisualTreeHelper.GetChildrenCount(o);
        for (int i = 0; i < n; i++) foreach (DependencyObject c in Walk(VisualTreeHelper.GetChild(o, i))) yield return c;
    }
    static DependencyObject FindByName(Window w, string name)
    {
        w.UpdateLayout();
        return Walk(w).FirstOrDefault(o => System.Windows.Automation.AutomationProperties.GetName(o) == name && ((UIElement)o).IsVisible);
    }
    static List<string> TabOrder(Window w)
    {
        w.UpdateLayout();
        List<string> list = new List<string>();
        foreach (DependencyObject o in Walk(w))
        {
            UIElement e = o as UIElement;
            if (e == null || !e.Focusable || !e.IsVisible || !KeyboardNavigation.GetIsTabStop(e)) continue;
            if (e is ScrollViewer || e is Window) continue;
            string n = System.Windows.Automation.AutomationProperties.GetName(e);
            if (string.IsNullOrEmpty(n) && e is Border) { TextBlock t = ((Border)e).Child as TextBlock; n = t == null ? "" : t.Text; }
            if (string.IsNullOrEmpty(n) && e is FrameworkElement && ((FrameworkElement)e).ToolTip is string) n = (string)((FrameworkElement)e).ToolTip;
            list.Add(string.IsNullOrEmpty(n) ? e.GetType().Name : n);
        }
        return list;
    }
}
