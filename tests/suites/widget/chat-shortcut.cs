using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
// S6: the new chat shortcut. A fake registrar records calls; no real shortcut is ever registered.
static partial class H4
{
    class FakeRegistrar : IShortcutRegistrar
    {
        public List<string> Calls = new List<string>();
        public Func<uint, uint, int> Result = delegate { return 0; };
        public int Register(IntPtr window, int id, uint modifiers, uint key)
        {
            int r = Result(modifiers, key);
            Calls.Add("reg " + id + " " + modifiers + " " + key + " -> " + r);
            return r;
        }
        public void Unregister(IntPtr window, int id) { Calls.Add("unreg " + id); }
    }

    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    static WidgetWindow ShortcutWidget(string dir, string field8, FakeRegistrar reg)
    {
        File.WriteAllText(Path.Combine(dir, "settings.txt"), "-6000,0,0,0,default,system,0,1," + field8);
        WidgetWindow w = new WidgetWindow(dir, account, null, null, 300, 0, true);
        w.ShortcutRegistrar = reg;
        w.ShowActivated = false;
        w.Show();
        return w;
    }

    static string NoteOf(WidgetWindow w) { TextBlock t = (TextBlock)Get(w, "shortcutNote"); return t.Visibility == Visibility.Visible ? t.Text : ""; }

    static async Task S6()
    {
        // R-E-07: the rule
        uint C = ShortcutText.Ctrl, A = ShortcutText.Alt, S = ShortcutText.Shift;
        Check("rule: Ctrl+C refused", !ShortcutText.IsAllowed(C, Key.C));
        Check("rule: Alt+letter refused", !ShortcutText.IsAllowed(A, Key.N));
        Check("rule: Alt+F4 refused", !ShortcutText.IsAllowed(A, Key.F4));
        Check("rule: Ctrl+F5 refused", !ShortcutText.IsAllowed(C, Key.F5));
        Check("rule: a lone function key refused", !ShortcutText.IsAllowed(0, Key.F5));
        Check("rule: Shift+Ctrl alone with Tab refused", !ShortcutText.IsAllowed(C | S, Key.Tab));
        Check("rule: Shift plus one other needs Ctrl or Alt", !ShortcutText.IsAllowed(S, Key.C));
        Check("rule: the suggested Ctrl+Alt+Shift+C is allowed", ShortcutText.IsAllowed(C | A | S, Key.C));
        Check("rule: Alt+Shift+C and Ctrl+Shift+F9 allowed", ShortcutText.IsAllowed(A | S, Key.C) && ShortcutText.IsAllowed(C | S, Key.F9));

        // Stored text: key names only, damaged text refused
        uint m; Key k;
        Check("text: suggested parses", ShortcutText.TryParse("Ctrl+Alt+Shift+C", out m, out k) && m == (C | A | S) && k == Key.C);
        Check("text: a comma key is stored by name and shown as a comma", ShortcutText.Format(C | A, Key.OemComma) == "Ctrl+Alt+OemComma" && ShortcutText.Display("Ctrl+Alt+OemComma") == "Ctrl+Alt+,");
        Check("text: a digit shows without the D", ShortcutText.Display("Ctrl+Alt+D1") == "Ctrl+Alt+1");
        foreach (string bad in new[] { "off", "", "garbage", "Ctrl+Ctrl+C", "Ctrl+Alt+", "Ctrl+Alt+NotAKey", "Ctrl+Alt+123", "Win+Alt+C", "Ctrl+C", "Ctrl+Alt+LWin", new string('x', 60) })
            Check("text: refused [" + bad.Substring(0, Math.Min(20, bad.Length)) + "]", !ShortcutText.TryParse(bad, out m, out k));

        // R-E-01: with no registrar, nothing is registered, even with a saved shortcut
        string d = Fresh("s6-none");
        FakeRegistrar never = new FakeRegistrar();
        File.WriteAllText(Path.Combine(d, "settings.txt"), "-6000,0,0,0,default,system,0,1,Ctrl+Alt+Shift+C");
        WidgetWindow w0 = new WidgetWindow(d, account, null, null, 300, 0, true);
        w0.ShowActivated = false; w0.Show();
        Check("no registrar: shown with a saved shortcut, nothing registered, no note", w0.ShortcutRegistrar == null && NoteOf(w0) == "" && (int)Get(w0, "shortcutId") == 0);
        w0.Close();
        Check("default: shortcut off", NewWidgetDefaults());

        // R-E-06: registered at SourceInitialized; the hotkey message opens one chat; a wrong id opens none
        d = Fresh("s6-hotkey");
        FakeRegistrar reg = new FakeRegistrar();
        WidgetWindow w = ShortcutWidget(d, "Ctrl+Alt+Shift+C", reg);
        await Task.Delay(200);
        Check("startup: the saved shortcut is registered once, with the right keys", reg.Calls.Count == 1 && reg.Calls[0] == "reg 1 7 " + KeyInterop.VirtualKeyFromKey(Key.C) + " -> 0", string.Join(" | ", reg.Calls));
        IntPtr handle = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        int chatsBefore = Application.Current.Windows.OfType<ChatWindow>().Count();
        PostMessage(handle, 0x0312, (IntPtr)2, IntPtr.Zero);
        await Task.Delay(300);
        Check("hotkey: a message with another id opens nothing", Application.Current.Windows.OfType<ChatWindow>().Count() == chatsBefore);
        PostMessage(handle, 0x0312, (IntPtr)1, IntPtr.Zero);
        await Task.Delay(400);
        ChatWindow[] chats = Application.Current.Windows.OfType<ChatWindow>().ToArray();
        Check("hotkey: the message opens one chat, off screen in test mode", chats.Length == chatsBefore + 1 && chats.Last().Left == -6000);
        foreach (ChatWindow c in chats) c.Close();

        // R-E-04: change releases the old one; a failure keeps the old one
        Check("change: to Alt+Shift+N registers the new one, then releases the old", w.SetShortcut("Alt+Shift+N") == null
            && reg.Calls.Skip(1).SequenceEqual(new[] { "reg 2 5 " + KeyInterop.VirtualKeyFromKey(Key.N) + " -> 0", "unreg 1" }) && w.shortcutSetting == "Alt+Shift+N", string.Join(" | ", reg.Calls));
        Check("change: saved as field 8", File.ReadAllText(Path.Combine(d, "settings.txt")).Split(',')[8] == "Alt+Shift+N");
        reg.Result = delegate { return 1409; };
        string problem = w.SetShortcut("Ctrl+Alt+OemComma");
        Check("change: a taken one is refused with a clear message (R-E-02)", problem == "The chat shortcut Ctrl+Alt+, is in use by another app. Choose another in Settings.", problem ?? "null");
        Check("change: the old one stays registered and saved", (int)Get(w, "shortcutId") == 2 && w.shortcutSetting == "Alt+Shift+N" && !reg.Calls.Last().StartsWith("unreg"));
        reg.Result = delegate { return 0; };

        // R-E-03: a good usage check doesn't clear the card's shortcut note
        TextBlock note = (TextBlock)Get(w, "shortcutNote");
        Call(w, "ShowShortcutNote", "The chat shortcut Ctrl+Alt+Shift+C is in use by another app. Choose another in Settings.");
        Call(w, "ApplyResult", new FetchResult(FetchStatus.Ok));
        Check("note: a good check leaves the shortcut note", note.Visibility == Visibility.Visible && note.Text.StartsWith("The chat shortcut"));

        // Turning off releases it and clears the note
        Check("off: releases and saves off", w.SetShortcut("off") == null && reg.Calls.Last() == "unreg 2" && w.shortcutSetting == "off" && note.Visibility == Visibility.Collapsed);
        int callsAfterOff = reg.Calls.Count;
        w.Close();
        Check("close: nothing left to release after off", reg.Calls.Count == callsAfterOff);

        // Closing releases a live shortcut
        d = Fresh("s6-close");
        reg = new FakeRegistrar();
        w = ShortcutWidget(d, "Ctrl+Alt+Shift+C", reg);
        await Task.Delay(100);
        w.Close();
        Check("close: the shortcut is released", reg.Calls.Last() == "unreg 1", string.Join(" | ", reg.Calls));

        // R-E-02 at startup, R-E-05 bad saved text
        d = Fresh("s6-taken");
        reg = new FakeRegistrar(); reg.Result = delegate { return 1409; };
        w = ShortcutWidget(d, "Ctrl+Alt+Shift+C", reg);
        await Task.Delay(100);
        Check("startup: a taken shortcut shows on the card", NoteOf(w) == "The chat shortcut Ctrl+Alt+Shift+C is in use by another app. Choose another in Settings.", NoteOf(w));
        w.Close();
        foreach (string bad in new[] { "Ctrl+Alt+", "nonsense", "Ctrl+C" })
        {
            d = Fresh("s6-bad");
            reg = new FakeRegistrar();
            bool threw = false;
            try { w = ShortcutWidget(d, bad, reg); await Task.Delay(100); } catch (Exception) { threw = true; }
            Check("bad saved text [" + bad + "]: no crash, nothing registered, a note", !threw && reg.Calls.Count == 0 && NoteOf(w).StartsWith("The saved chat shortcut isn't valid"), NoteOf(w));
            w.Close();
        }
        d = Fresh("s6-off");
        reg = new FakeRegistrar();
        w = ShortcutWidget(d, "off", reg);
        await Task.Delay(100);
        Check("off saved: nothing registered, no note", reg.Calls.Count == 0 && NoteOf(w) == "");

        // Settings: capture, refusal, Set, Turn off
        SettingsWindow sw = new SettingsWindow(w, d);
        TextBlock hint = (TextBlock)Get(sw, "shortcutHint");
        Check("settings: off suggests the default", hint.Text.Contains(ShortcutText.Suggested), hint.Text);
        sw.Capture(ModifierKeys.Control, Key.C);
        Check("settings: Ctrl+C is refused with the rule", Get(sw, "capturedShortcut") == null && hint.Text.StartsWith("Use two of Ctrl, Alt and Shift"), hint.Text);
        sw.Capture(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows, Key.C);
        Check("settings: the Windows key is refused", Get(sw, "capturedShortcut") == null);
        Call(sw, "ApplyShortcut", (object)null);
        Check("settings: Set with nothing captured asks for keys", hint.Text == "Click the box and press the keys you want first." && reg.Calls.Count == 0);
        sw.Capture(ModifierKeys.Control | ModifierKeys.Alt, Key.OemComma);
        Check("settings: Ctrl+Alt+, captured and shown with a comma", (string)Get(sw, "capturedShortcut") == "Ctrl+Alt+OemComma" && ((TextBox)Get(sw, "shortcutBox")).Text == "Ctrl+Alt+,");
        Call(sw, "ApplyShortcut", (string)Get(sw, "capturedShortcut"));
        Check("settings: Set registers and saves by key name", w.shortcutSetting == "Ctrl+Alt+OemComma" && reg.Calls.Count == 1
            && File.ReadAllText(Path.Combine(d, "settings.txt")).Split(',').Length == 9, File.ReadAllText(Path.Combine(d, "settings.txt")));
        sw.Close();
        WidgetWindow reload = new WidgetWindow(d, account, null, null, 300, 0, true);
        Check("settings: the comma shortcut reloads intact", reload.shortcutSetting == "Ctrl+Alt+OemComma");
        reload.Close();
        sw = new SettingsWindow(w, d);
        Call(sw, "ApplyShortcut", "off");
        Check("settings: Turn off releases and saves off", w.shortcutSetting == "off" && reg.Calls.Last() == "unreg 1");
        sw.Close();
        w.Close();
    }

    static bool NewWidgetDefaults()
    {
        WidgetWindow w = NewWidget(Fresh("s6-default"), true);
        bool off = w.shortcutSetting == "off";
        w.Close();
        return off;
    }
}
