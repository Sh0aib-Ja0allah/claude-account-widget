using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
// The bypass setting (quick mode): chat-bypass.txt and its DPAPI marker, when bypass loads on, turning it on and off,
// and laptop access clearing it. Scratch data folders only; windows are test copies kept off screen.
//   bypass-setting.exe <out folder>
static class H21
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static string root, account;
    static int fails, passes;
    static void Log(string s) { Console.WriteLine(s); Console.Out.Flush(); }
    static void Check(string what, bool ok, string info = "")
    {
        Log((ok ? "PASS " : "FAIL ") + what + (info.Length > 0 ? " [" + info.Replace("\r", "\\r").Replace("\n", "\\n") + "]" : ""));
        if (ok) passes++; else fails++;
    }
    static object Get(object o, string f) { return o.GetType().GetField(f, Any).GetValue(o); }
    static object Call(object o, string m, params object[] a) { return o.GetType().GetMethod(m, Any).Invoke(o, a); }

    [STAThread]
    static void Main(string[] args)
    {
        root = args[0];
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        account = Path.Combine(root, "account.json");
        File.WriteAllText(account, "{}");
        Application app = new Application();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.DispatcherUnhandledException += (s, e) => { Log("UNHANDLED " + e.Exception); fails++; e.Handled = true; };
        try { Run(); }
        catch (Exception e) { Log("HARNESS ERROR " + e); fails++; }
        finally { WidgetWindow.BypassLoadForTests = false; }
        Log("passes=" + passes + " fails=" + fails);
        Process.GetCurrentProcess().Kill();
    }

    static string Fresh(string name)
    {
        string d = Path.Combine(root, name);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "settings.txt"), "-6000,0,0,0,default,system,0,0,");
        return d;
    }
    static string MarkerFile(string d) { return Path.Combine(d, BypassState.FileName); }
    static void Laptop(string d, bool on)
    {
        string f = Path.Combine(d, "chat-access.txt");
        if (on) File.WriteAllText(f, "laptop"); else if (File.Exists(f)) File.Delete(f);
    }
    static void Marker(string d) { File.WriteAllText(MarkerFile(d), BypassState.Protect(BypassState.Marker, BypassState.Entropy)); }
    static bool Loads(string d) { return new BypassState(d, false).ForNewChats; }
    static WidgetWindow Widget(string d) { return new WidgetWindow(d, account, null, null, 300, 0, true); }
    static SettingsWindow Settings(WidgetWindow w, string d)
    {
        SettingsWindow s = new SettingsWindow(w, d);
        s.WindowStartupLocation = WindowStartupLocation.Manual; s.Left = -6000; s.Top = 0; s.ShowActivated = false; s.ShowInTaskbar = false;
        s.Show();
        return s;
    }
    static string Msg(SettingsWindow s) { return ((TextBlock)Get(s, "message")).Text; }

    static void Run()
    {
        // ---------- H21-21 to 25: the marker itself ----------
        string blob = BypassState.Protect(BypassState.Marker, BypassState.Entropy);
        Check("H21-21 the marker round-trips: Protect then Unprotect with the same entropy gives the exact text, and the file text isn't readable",
            BypassState.Unprotect(blob, BypassState.Entropy) == BypassState.Marker && !blob.Contains("bypass") && Convert.FromBase64String(blob).Length > 0);
        string d = Fresh("marker");
        Laptop(d, true);
        File.WriteAllText(MarkerFile(d), BypassState.Marker);
        bool plain = BypassState.HasMarker(d) || Loads(d);
        File.WriteAllText(MarkerFile(d), BypassState.Protect(BypassState.Marker, "some other entropy"));
        bool other = BypassState.HasMarker(d) || Loads(d);
        File.WriteAllText(MarkerFile(d), BypassState.Protect("bypass confirmed 2", BypassState.Entropy));
        bool otherText = BypassState.HasMarker(d) || Loads(d);
        File.WriteAllText(MarkerFile(d), "!!not base64!!");
        bool corrupt = BypassState.HasMarker(d) || Loads(d);
        File.WriteAllText(MarkerFile(d), Convert.ToBase64String(Encoding.UTF8.GetBytes("random bytes, not a DPAPI blob")));
        bool notBlob = BypassState.HasMarker(d) || Loads(d);
        byte[] cut = Convert.FromBase64String(blob);
        File.WriteAllText(MarkerFile(d), Convert.ToBase64String(cut, 0, cut.Length - 8));
        bool truncated = BypassState.HasMarker(d) || Loads(d);
        File.WriteAllText(MarkerFile(d), "");
        bool empty = BypassState.HasMarker(d) || Loads(d);
        Check("H21-22 the plain text \"bypass confirmed 1\" reads as off", !plain);
        Check("H21-23 a blob made with other entropy, or of other text, reads as off", !other && !otherText);
        Check("H21-24 corrupt Base64, Base64 that isn't a blob and a truncated blob read as off", !corrupt && !notBlob && !truncated);
        Check("H21-25 an empty file reads as off", !empty);

        // ---------- H21-01 to 05, 26: the load rules ----------
        d = Fresh("load");
        Laptop(d, true);
        bool missing = Loads(d);
        File.WriteAllText(MarkerFile(d), "1");
        bool one = Loads(d);
        File.WriteAllText(MarkerFile(d), "on");
        bool word = Loads(d);
        Check("H21-01 no chat-bypass.txt: off", !missing);
        Check("H21-02 chat-bypass.txt holding 1: off", !one);
        Check("H21-03 chat-bypass.txt holding on: off", !word);
        Marker(d);
        Laptop(d, false);
        bool noLaptop = Loads(d);
        Check("H21-04 a valid marker without laptop access: off", !noLaptop);
        Laptop(d, true);
        bool both = Loads(d);
        WidgetWindow plainCopy = Widget(d);
        WidgetWindow.BypassLoadForTests = true;
        WidgetWindow allowed = Widget(d);
        WidgetWindow.BypassLoadForTests = false;
        Check("H21-05 a valid marker with laptop access: on; a test copy: always off (unless the test seam allows the load)",
            both && !new BypassState(d, true).ForNewChats && !plainCopy.BypassForNewChats && plainCopy.Bypass.Blocked && allowed.BypassForNewChats && allowed.Bypass.LoadedOn,
            both + " " + plainCopy.BypassForNewChats + " " + allowed.BypassForNewChats);
        Laptop(d, false);
        WidgetWindow.BypassLoadForTests = true;
        WidgetWindow noAccess = Widget(d);
        WidgetWindow.BypassLoadForTests = false;
        Check("H21-26 a widget starting with a valid marker but no laptop access has bypass off", !noAccess.BypassForNewChats && !noAccess.Bypass.LoadedOn);

        // ---------- H21-06 to 09: turning on ----------
        d = Fresh("on");
        Laptop(d, true);
        BypassState state = new BypassState(d, false);
        string error = state.TurnOn();
        string written = File.ReadAllText(MarkerFile(d)).Trim();
        Check("H21-06 Turn on writes chat-bypass.txt", error == null && File.Exists(MarkerFile(d)), error ?? "");
        Check("H21-07 the file is a DPAPI marker that unprotects to the exact text with the DEC-21 entropy, not plain text",
            BypassState.Unprotect(written, "ClaudeAccountWidget bypass marker v1") == "bypass confirmed 1" && !written.Contains("bypass"));
        Check("H21-08 after Turn on, new chats use bypass, and a fresh start loads it on", state.ForNewChats && Loads(d));
        d = Fresh("on-locked");
        Laptop(d, true);
        File.WriteAllText(MarkerFile(d), "off");
        state = new BypassState(d, false);
        using (new FileStream(MarkerFile(d), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) error = state.TurnOn();
        Check("H21-28 the turn-on error text ends without a line break", error != null && !error.EndsWith("\n") && !error.EndsWith("\r"), error ?? "null");
        Check("H21-09 a failed write (file locked) leaves bypass off and says why", error != null && error.StartsWith("Couldn't turn on bypass permissions: ") && !state.ForNewChats && !Loads(d), error ?? "null");
        Check("H21-09 Turn on refuses in a test copy and without laptop access",
            new BypassState(d, true).TurnOn() == "Couldn't turn on bypass permissions: this is a test copy"
            && new BypassState(Fresh("on-nolaptop"), false).TurnOn() == "Couldn't turn on bypass permissions: laptop access is off");

        // ---------- H21-10 to 14: turning off ----------
        d = Fresh("off");
        Laptop(d, true);
        Marker(d);
        state = new BypassState(d, false);
        error = state.TurnOff();
        Check("H21-10 Turn off deletes chat-bypass.txt", error == null && !File.Exists(MarkerFile(d)) && !state.ForNewChats && !state.ClearPending);
        Marker(d);
        state = new BypassState(d, false);
        using (new FileStream(MarkerFile(d), FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) error = state.TurnOff();
        Check("H21-11 when the delete fails, the file is overwritten with off", error == null && File.ReadAllText(MarkerFile(d)) == "off" && !Loads(d) && !state.ClearPending, error ?? "");
        if (File.Exists(MarkerFile(d))) File.Delete(MarkerFile(d));
        Marker(d);
        File.SetAttributes(MarkerFile(d), FileAttributes.ReadOnly);
        BypassState readOnly = new BypassState(d, false);
        string readOnlyError = readOnly.TurnOff();
        Check("H21-27 a read-only marker is still cleared: the file is gone, with no error", readOnlyError == null && !File.Exists(MarkerFile(d)) && !readOnly.ClearPending && !Loads(d), readOnlyError ?? "");
        Marker(d);
        state = new BypassState(d, false);
        FileStream held = new FileStream(MarkerFile(d), FileMode.Open, FileAccess.Read, FileShare.None);
        error = state.TurnOff();
        bool shape = error != null && error.StartsWith("Couldn't save the change: ")
            && error.EndsWith(". New chats won't use bypass now, but it could come back after the widget restarts. Close anything using chat-bypass.txt and turn it off again.")
            && !error.Contains("..");
        Check("H21-12 when both fail, memory is off and the exact error text is returned", shape && !state.ForNewChats && state.ClearPending, error ?? "null");
        state.RetryClear();
        bool stillPending = state.ClearPending && File.Exists(MarkerFile(d));
        held.Dispose();
        state.RetryClear();
        Check("H21-13 the retry keeps trying while the file is locked, then clears it", stillPending && !state.ClearPending && !File.Exists(MarkerFile(d)) && !state.ForNewChats);

        // H21-14: the widget retries at the next chat start and at exit.
        d = Fresh("retry");
        Laptop(d, true);
        Marker(d);
        WidgetWindow.BypassLoadForTests = true;
        WidgetWindow w = Widget(d);
        WidgetWindow.BypassLoadForTests = false;
        held = new FileStream(MarkerFile(d), FileMode.Open, FileAccess.Read, FileShare.None);
        error = w.Bypass.TurnOff();
        held.Dispose();
        bool pendingBefore = w.Bypass.ClearPending && File.Exists(MarkerFile(d));
        Call(w, "OpenChatWindow");
        bool atChat = !w.Bypass.ClearPending && !File.Exists(MarkerFile(d));
        ChatWindow.CloseAll();
        Marker(d);
        WidgetWindow.BypassLoadForTests = true;
        WidgetWindow w2 = Widget(d);
        WidgetWindow.BypassLoadForTests = false;
        held = new FileStream(MarkerFile(d), FileMode.Open, FileAccess.Read, FileShare.None);
        w2.Bypass.TurnOff();
        held.Dispose();
        w2.Left = -6000; w2.ShowActivated = false; w2.ShowInTaskbar = false;
        w2.Show();
        w2.Close();
        Check("H21-14 after a failed clear the widget tries again when the next chat starts, and again at exit",
            error != null && pendingBefore && atChat && !w.BypassForNewChats && !w2.Bypass.ClearPending && !File.Exists(MarkerFile(d)), pendingBefore + " " + atChat + " " + w2.Bypass.ClearPending);

        // ---------- H21-15 to 17: laptop access ----------
        d = Fresh("laptop-on");
        Marker(d);
        w = Widget(d);
        SettingsWindow s = Settings(w, d);
        Call(s, "SetLaptop", true);
        Check("H21-15 turning laptop access on clears bypass first", File.Exists(Path.Combine(d, "chat-access.txt")) && !File.Exists(MarkerFile(d)) && !Loads(d) && (bool)Get(s, "laptopOn"), Msg(s));
        s.Close();
        d = Fresh("laptop-locked");
        Marker(d);
        w = Widget(d);
        s = Settings(w, d);
        using (new FileStream(MarkerFile(d), FileMode.Open, FileAccess.Read, FileShare.None)) Call(s, "SetLaptop", true);
        Check("H21-16 a failed clear keeps laptop access off and shows the error",
            !File.Exists(Path.Combine(d, "chat-access.txt")) && !(bool)Get(s, "laptopOn") && Msg(s).StartsWith("Couldn't change laptop access: "), Msg(s));
        s.Close();
        d = Fresh("laptop-cycle");
        Laptop(d, true);
        Marker(d);
        WidgetWindow.BypassLoadForTests = true;
        w = Widget(d);
        WidgetWindow.BypassLoadForTests = false;
        bool startedOn = w.BypassForNewChats;
        s = Settings(w, d);
        Call(s, "SetLaptop", false);
        bool offAfterOff = !w.BypassForNewChats && !File.Exists(MarkerFile(d));
        Call(s, "SetLaptop", true);
        bool offAfterOn = !w.BypassForNewChats && !Loads(d);
        s.Close();
        d = Fresh("laptop-deleted");
        Laptop(d, true);
        Marker(d);
        Laptop(d, false);   // chat-access.txt deleted by hand
        WidgetWindow.BypassLoadForTests = true;
        w = Widget(d);
        WidgetWindow.BypassLoadForTests = false;
        bool deletedOff = !w.BypassForNewChats;
        s = Settings(w, d);
        Call(s, "SetLaptop", true);
        bool deletedThenOn = !w.BypassForNewChats && !File.Exists(MarkerFile(d)) && !Loads(d);
        s.Close();
        Check("H21-17 laptop off then on, or chat-access.txt deleted then laptop on, leaves bypass off, so it needs the warning again",
            startedOn && offAfterOff && offAfterOn && deletedOff && deletedThenOn, startedOn + " " + offAfterOff + " " + offAfterOn + " " + deletedOff + " " + deletedThenOn);

        // ---------- H21-18 to 20: S2 ----------
        d = Fresh("s2");
        Laptop(d, true);
        w = Widget(d);
        WidgetWindow.BypassLoadForTests = true;
        WidgetWindow running = Widget(d);
        WidgetWindow.BypassLoadForTests = false;
        File.WriteAllText(MarkerFile(d), BypassState.Marker);
        bool plainLater = running.BypassForNewChats;
        Marker(d);
        bool blobLater = running.BypassForNewChats;
        ChatWindow chat = new ChatWindow(d, running.BypassForNewChats);
        Check("H21-18 a marker written while the widget runs (plain text or a valid blob) doesn't make new chats bypass",
            !plainLater && !blobLater && !(bool)Get(chat, "bypassMode"));
        chat.Close();
        string error2 = running.Bypass.TurnOn();
        Laptop(d, false);
        chat = new ChatWindow(d, running.BypassForNewChats);
        bool normal = running.BypassForNewChats && !(bool)Get(chat, "bypassMode");
        chat.Close();
        Laptop(d, true);
        chat = new ChatWindow(d, running.BypassForNewChats);
        bool bypassChat = (bool)Get(chat, "bypassMode");
        chat.Close();
        Check("H21-19 with bypass on in memory, a chat whose own read of laptop access is off is a normal chat", error2 == null && normal && bypassChat, error2 ?? "");
        running.SaveSettings();
        string[] fields = File.ReadAllText(Path.Combine(d, "settings.txt")).Split(',');
        Check("H21-20 settings.txt still has 9 fields and says nothing about bypass", fields.Length == 9 && !File.ReadAllText(Path.Combine(d, "settings.txt")).Contains("bypass"), fields.Length.ToString());
    }
}
