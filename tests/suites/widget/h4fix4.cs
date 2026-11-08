using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
// Fixes from the S6 review: S6-1 (setting the current shortcut again) and S6-2 (alias key names).
static partial class H4
{
    // Imitates Windows: the same keys can't be registered twice, whatever the id.
    class WindowsLikeRegistrar : IShortcutRegistrar
    {
        public Dictionary<int, string> Held = new Dictionary<int, string>();
        public List<string> Calls = new List<string>();
        public HashSet<string> TakenElsewhere = new HashSet<string>();
        public int Register(IntPtr window, int id, uint modifiers, uint key)
        {
            string keys = modifiers + ":" + key;
            Calls.Add("reg " + id + " " + keys);
            if (Held.ContainsValue(keys) || TakenElsewhere.Contains(keys)) return 1409;
            Held[id] = keys;
            return 0;
        }
        public void Unregister(IntPtr window, int id) { Calls.Add("unreg " + id); Held.Remove(id); }
    }

    static async Task Fixes4()
    {
        string d = Fresh("fix4-same");
        File.WriteAllText(Path.Combine(d, "settings.txt"), "-6000,0,0,0,default,system,0,1,Ctrl+Alt+Shift+C");
        WidgetWindow w = new WidgetWindow(d, account, null, null, 300, 0, true);
        WindowsLikeRegistrar reg = new WindowsLikeRegistrar();
        w.ShortcutRegistrar = reg;
        w.ShowActivated = false; w.Show();
        await Task.Delay(100);
        Check("S6-1: startup registers once", reg.Held.Count == 1 && reg.Calls.Count == 1);
        string r = w.SetShortcut("Ctrl+Alt+Shift+C");
        Check("S6-1: setting the current shortcut again says nothing is wrong and registers nothing", r == null && reg.Calls.Count == 1 && reg.Held.Count == 1, r ?? "null");
        SettingsWindow sw = new SettingsWindow(w, d);
        sw.Capture(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, Key.C);
        Call(sw, "ApplyShortcut", (string)Get(sw, "capturedShortcut"));
        string hint = ((System.Windows.Controls.TextBlock)Get(sw, "shortcutHint")).Text;
        Check("S6-1: through Settings, Set with the current keys says it works", hint == "Press Ctrl+Alt+Shift+C anywhere to open a new chat.", hint);
        sw.Close();

        Check("change: a different combination is registered before the old is released", w.SetShortcut("Ctrl+Shift+F9") == null
            && reg.Calls.Skip(1).Take(2).SequenceEqual(new[] { "reg 2 6:" + KeyInterop.VirtualKeyFromKey(Key.F9), "unreg 1" }) && reg.Held.Count == 1, string.Join(" | ", reg.Calls));
        reg.TakenElsewhere.Add("3:" + KeyInterop.VirtualKeyFromKey(Key.N));
        string taken = w.SetShortcut("Ctrl+Alt+N");
        Check("change: one taken elsewhere fails and keeps the old shortcut", taken != null && taken.Contains("in use by another app") && w.shortcutSetting == "Ctrl+Shift+F9" && reg.Held.Count == 1 && reg.Held.ContainsKey(2));
        Check("change: changing back to the first works", w.SetShortcut("Ctrl+Alt+Shift+C") == null && reg.Held.Count == 1 && w.shortcutSetting == "Ctrl+Alt+Shift+C");
        w.Close();

        // S6-2: friendly names for display; stored names unchanged and reloaded intact
        Dictionary<Key, string> shown = new Dictionary<Key, string>
        {
            { Key.PageDown, "Ctrl+Alt+PageDown" }, { Key.PageUp, "Ctrl+Alt+PageUp" }, { Key.OemBackslash, "Ctrl+Alt+<" },
            { Key.OemPipe, "Ctrl+Alt+\\" }, { Key.Oem1, "Ctrl+Alt+;" }, { Key.NumPad3, "Ctrl+Alt+Num 3" }, { Key.F12, "Ctrl+Alt+F12" },
        };
        foreach (KeyValuePair<Key, string> pair in shown)
        {
            string stored = ShortcutText.Format(ShortcutText.Ctrl | ShortcutText.Alt, pair.Key);
            string fd = Fresh("fix4-names");
            File.WriteAllText(Path.Combine(fd, "settings.txt"), "-6000,0,0,0,default,system,0,1," + stored);
            WidgetWindow rw = new WidgetWindow(fd, account, null, null, 300, 0, true);
            uint m; Key k;
            bool round = ShortcutText.TryParse(rw.shortcutSetting, out m, out k) && k == pair.Key && rw.shortcutSetting == stored;
            Check("S6-2: " + pair.Key + " shows as " + pair.Value + " and reloads as stored", round && ShortcutText.Display(stored) == pair.Value, stored + " -> " + ShortcutText.Display(stored));
            rw.Close();
        }
    }
}
