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
// Worker A's independent S6 checks (chat shortcut) and the S5-1 retest. Never registers a real global shortcut:
// the registrar here only imitates Windows' rule that a key combination can be registered once, whatever the id.
// h11.exe <out folder>
static class H11
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static string root, account;
    static int fails, passes;
    static object Get(object o, string f) { return o.GetType().GetField(f, Any).GetValue(o); }
    static void Log(string s) { Console.WriteLine(s); Console.Out.Flush(); }
    static void Check(string what, bool ok, string info = "")
    {
        Log((ok ? "PASS " : "FAIL ") + what + (info.Length > 0 ? " [" + info + "]" : ""));
        if (ok) passes++; else fails++;
    }

    // Imitates RegisterHotKey: ERROR_HOTKEY_ALREADY_REGISTERED (1409) when the combination is taken, by any id.
    class WindowsLikeRegistrar : IShortcutRegistrar
    {
        public Dictionary<int, string> Held = new Dictionary<int, string>();
        public List<string> Calls = new List<string>();
        public int Register(IntPtr window, int id, uint modifiers, uint key)
        {
            string combo = modifiers + ":" + key;
            Calls.Add("register " + id + " " + combo);
            if (Held.ContainsValue(combo)) return 1409;
            Held[id] = combo;
            return 0;
        }
        public void Unregister(IntPtr window, int id) { Calls.Add("unregister " + id); Held.Remove(id); }
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
        string d = Path.Combine(root, "w");
        if (Directory.Exists(d)) Directory.Delete(d, true);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "settings.txt"), "-6000,0,0,0,default,system,0,1,off");
        WidgetWindow w = new WidgetWindow(d, account, null, null, 300, 0, true);
        WindowsLikeRegistrar reg = new WindowsLikeRegistrar();
        w.ShortcutRegistrar = reg;
        w.ShowActivated = false;
        w.Loaded += async delegate
        {
            try { Texts(); Shortcut(w, reg, d); History(); }
            catch (Exception e) { Log("HARNESS ERROR " + e); fails++; }
            await Task.Delay(10);
            Log("passes=" + passes + " fails=" + fails);
            Process.GetCurrentProcess().Kill();
        };
        app.Run(w);
    }

    static void Texts()
    {
        // Every ordinary key with Ctrl+Alt: saved text round-trips to the same key, and the display reads like the key cap.
        List<string> badRound = new List<string>(), oddDisplay = new List<string>();
        foreach (Key k in Enum.GetValues(typeof(Key)).Cast<Key>().Distinct())
        {
            if (!ShortcutText.IsAllowed(ShortcutText.Ctrl | ShortcutText.Alt, k)) continue;
            string text = ShortcutText.Format(ShortcutText.Ctrl | ShortcutText.Alt, k);
            uint m; Key back;
            if (!ShortcutText.TryParse(text, out m, out back) || back != k || m != (ShortcutText.Ctrl | ShortcutText.Alt)) badRound.Add(text);
            string shown = ShortcutText.Display(text);
            string tail = shown.Substring("Ctrl+Alt+".Length);
            if (tail.StartsWith("Oem") || tail == "Next" || tail == "Prior" || tail.Length == 0) oddDisplay.Add(k + " shows " + shown + " (saved " + text + ")");
        }
        Check("text: every allowed key round-trips through settings.txt", badRound.Count == 0, string.Join(", ", badRound));
        Check("text: every allowed key displays like its key cap", oddDisplay.Count == 0, string.Join("; ", oddDisplay));
        uint mm; Key kk;
        Check("text: a trailing space in a hand-edited setting", ShortcutText.TryParse("Ctrl+Alt+Shift+C ", out mm, out kk) || true, ShortcutText.TryParse("Ctrl+Alt+Shift+C ", out mm, out kk) ? "accepted" : "refused (shows the invalid note)");
        Check("text: lower-case modifiers refused", !ShortcutText.TryParse("ctrl+alt+C", out mm, out kk));
        Check("text: AltGr-like Ctrl+Alt+E is allowed (owner's keyboard may type a character with it)", ShortcutText.TryParse("Ctrl+Alt+E", out mm, out kk));
    }

    static void Shortcut(WidgetWindow w, WindowsLikeRegistrar reg, string d)
    {
        string first = w.SetShortcut("Ctrl+Alt+Shift+C");
        Check("shortcut: first Set works", first == null && reg.Held.Count == 1, first ?? string.Join(" | ", reg.Calls));
        string again = w.SetShortcut("Ctrl+Alt+Shift+C");
        Check("shortcut: setting the same shortcut again isn't reported as another app's", again == null, again ?? "");
        Check("shortcut: and it is still registered once", reg.Held.Count == 1, string.Join(" | ", reg.Calls));
        string change = w.SetShortcut("Ctrl+Alt+K");
        Check("shortcut: a change registers the new one and releases the old", change == null && reg.Held.Count == 1 && reg.Held.Values.First().EndsWith(":" + KeyInterop.VirtualKeyFromKey(Key.K)), string.Join(" | ", reg.Calls));
        string back = w.SetShortcut("Ctrl+Alt+Shift+C");
        Check("shortcut: changing back to the first works", back == null && reg.Held.Count == 1, back ?? "");
        string fields = File.ReadAllText(Path.Combine(d, "settings.txt"));
        Check("shortcut: field 8 saved", fields.Split(',')[8] == "Ctrl+Alt+Shift+C", fields);
        w.SetShortcut("off");
        Check("shortcut: off releases everything", reg.Held.Count == 0 && File.ReadAllText(Path.Combine(d, "settings.txt")).Split(',')[8] == "off");

        // Settings: pressing the same keys as the current shortcut and clicking Set
        w.SetShortcut("Ctrl+Alt+Shift+C");
        SettingsWindow s = new SettingsWindow(w, d);
        s.Left = -6000; s.ShowActivated = false; s.ShowInTaskbar = false; s.Show();
        s.Capture(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, Key.C);
        object apply = s.GetType().GetMethod("ApplyShortcut", Any).Invoke(s, new object[] { Get(s, "capturedShortcut") });
        string hint = ((TextBlock)Get(s, "shortcutHint")).Text;
        Check("settings: Set with the current shortcut gives no error", !hint.Contains("in use by another app"), hint);
        s.Capture(ModifierKeys.Control | ModifierKeys.Alt, Key.PageDown);
        Check("settings: PageDown capture reads as PageDown", ((TextBox)Get(s, "shortcutBox")).Text.EndsWith("PageDown"), ((TextBox)Get(s, "shortcutBox")).Text);
        s.Close();
    }

    static void History()
    {
        string d = Path.Combine(root, "hist");
        if (Directory.Exists(d)) Directory.Delete(d, true);
        Directory.CreateDirectory(d);
        string f = Path.Combine(d, "usage-history.csv");
        double now = Math.Floor((DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
        UsageHistory.Append(f, now, 150, now + 86400, "0123456789abcdef");
        UsageHistory.Append(f, now + 300, 150, now + 86400, "0123456789abcdef");
        List<UsageHistory.Row> rows = UsageHistory.Read(f);
        Check("S5-1: 150% stored as a readable 120% row, and the hour rule then skips the repeat", rows.Count == 1 && rows[0].Percent == 120, rows.Count + " rows; " + File.ReadAllText(f).Trim());
        UsageHistory.Append(f, now + 600, -5, 1e300, "0123456789abcdef");
        rows = UsageHistory.Read(f);
        Check("S5-1: -5% with reset 1e300 stored as 0% with reset 0, readable", rows.Count == 2 && rows[1].Percent == 0 && rows[1].Reset == 0, File.ReadAllText(f).Trim());
    }
}
