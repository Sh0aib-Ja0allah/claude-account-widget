using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
// W-B01 to 20 (u2-switch): the Settings switch for bypass permissions, its warning, and its storage through
// chat-bypass.txt. Off screen only; test copies, with WidgetWindow.BypassLoadForTests standing in for a real copy.
static partial class H4
{
    static WidgetWindow BypassWidget(string d)
    {
        WidgetWindow.BypassLoadForTests = true;
        try { return NewWidget(d, true); }
        finally { WidgetWindow.BypassLoadForTests = false; }
    }

    static SettingsWindow BypassSettings(WidgetWindow w, string d)
    {
        SettingsWindow sw = new SettingsWindow(w, d);
        Call(w, "ShowOffScreen", sw);
        sw.UpdateLayout();
        return sw;
    }

    static void PressBorder(Border b)
    {
        b.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
    }

    static Border TextButtonIn(DependencyObject node, string text)
    {
        Border b = node as Border;
        TextBlock t = b == null ? null : b.Child as TextBlock;
        if (t != null && t.Text == text) return b;
        int n = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++)
        {
            Border found = TextButtonIn(VisualTreeHelper.GetChild(node, i), text);
            if (found != null) return found;
        }
        return null;
    }

    static void Escape(Window w)
    {
        KeyEventArgs e = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(w), 0, Key.Escape);
        e.RoutedEvent = Keyboard.PreviewKeyDownEvent;
        w.RaiseEvent(e);
    }

    static string SettingsMessage(SettingsWindow sw) { return ((TextBlock)Get(sw, "message")).Text; }
    static bool KnobOn(SettingsWindow sw) { return ((Border)Get(sw, "bypassKnob")).HorizontalAlignment == HorizontalAlignment.Right; }
    static string BypassFile(string d) { return Path.Combine(d, BypassState.FileName); }

    static async Task BypassSwitch()
    {
        await Task.Yield();
        // ---------- W-B01 to 06: the switch row and opening the warning ----------
        string d = Fresh("wb-row");
        WidgetWindow w = BypassWidget(d);
        SettingsWindow sw = BypassSettings(w, d);
        Border track = (Border)Get(sw, "bypassSwitch");
        Border warning = (Border)Get(sw, "bypassWarning");
        bool hint = TextBlocksIn(sw).Any(t => t.Text == "New chats edit files, run commands and open sites without asking. Needs laptop access. Sign-in files are refused by name, which scripts can get past.");
        Check("W-B01 the switch row has the exact title and hint", hint && TextBlocksIn(sw).Any(t => t.Text == "Bypass permissions for chats")
            && System.Windows.Automation.AutomationProperties.GetName(track) == "Bypass permissions for chats");
        Check("W-B02 without laptop access the switch is greyed out with its tooltip", !track.IsEnabled && track.Opacity < 1 && (string)track.ToolTip == "Turn on laptop access first");
        Call(sw, "ToggleBypass");
        Check("W-B02 without laptop access a click says so and changes nothing",
            SettingsMessage(sw) == "Turn on laptop access first." && !w.BypassForNewChats && warning.Visibility == Visibility.Collapsed && !File.Exists(BypassFile(d)));
        sw.Close(); w.Close();

        string dc = Fresh("wb-copy");
        File.WriteAllText(Path.Combine(dc, "chat-access.txt"), "laptop");
        WidgetWindow copy = NewWidget(dc, true);
        SettingsWindow cs = BypassSettings(copy, dc);
        Border copyTrack = (Border)Get(cs, "bypassSwitch");
        Call(cs, "ToggleBypass");
        Check("W-B03 in a test copy the switch is greyed out, says why, and a click changes nothing",
            !copyTrack.IsEnabled && (string)copyTrack.ToolTip == "Not available in a test copy" && SettingsMessage(cs) == "Bypass permissions aren't available in a test copy."
            && !copy.BypassForNewChats && ((Border)Get(cs, "bypassWarning")).Visibility == Visibility.Collapsed && !File.Exists(BypassFile(dc)));
        cs.Close(); copy.Close();

        d = Fresh("wb-flow");
        File.WriteAllText(Path.Combine(d, "chat-access.txt"), "laptop");
        w = BypassWidget(d);
        sw = BypassSettings(w, d);
        track = (Border)Get(sw, "bypassSwitch");
        warning = (Border)Get(sw, "bypassWarning");
        PressBorder(track);
        sw.UpdateLayout();
        Check("W-B04 with laptop access the switch is enabled, and a click opens the warning without turning anything on",
            track.IsEnabled && track.Opacity == 1 && warning.Visibility == Visibility.Visible && !w.BypassForNewChats && !File.Exists(BypassFile(d)) && !KnobOn(sw));
        PressBorder(TextButtonIn(warning, "Cancel"));
        Check("W-B05 Cancel closes the warning and leaves bypass off", warning.Visibility == Visibility.Collapsed && !w.BypassForNewChats && !File.Exists(BypassFile(d)));
        PressBorder(track);
        Escape(sw);
        Check("W-B06 Esc closes the warning first, keeps the window open, and leaves bypass off",
            warning.Visibility == Visibility.Collapsed && sw.IsVisible && !w.BypassForNewChats && !File.Exists(BypassFile(d)));

        // ---------- W-B07 to 12: the warning, Turn on, layout and Tab order ----------
        PressBorder(track);
        sw.UpdateLayout();
        List<string> texts = TextBlocksIn(warning).Select(t => t.Text).ToList();
        string[] expected =
        {
            "New chat windows will, without asking you first:",
            "• Read, edit and create files anywhere your Windows account can.",
            "• Run commands and programs, including ones that delete files or install software.",
            "• Open web sites and send them data, which can include your files.",
            "Still blocked or still asked: Claude Code refuses reads and searches that name its sign-in files or their copies. Changes to settings, git and tool-configuration files, this widget's own settings, start-up items and network paths still show a card.",
            "Not blocked: this isn't an operating-system lock. A script or program Claude runs, such as Python, Node or PowerShell, can still read any of your files, including the sign-in files, and send them to a web site. Text in a web page, file or note could trick Claude into doing that, and no card would stop it. Changes can outlast the chat, for example to other programs' settings.",
            "Open chats keep their current mode; only new chats use this.",
            "Cancel",
            "Turn on",
        };
        Check("W-B07 the warning's texts are exact, in order (DEC-9)", texts.SequenceEqual(expected), string.Join(" | ", texts.ToArray()));
        Check("W-B08 the warning promises nothing it can't keep (no can't, cannot, impossible or always blocked)",
            !texts.Any(t => System.Text.RegularExpressions.Regex.IsMatch(t, @"can't|cannot|impossible|always blocked|stay blocked", System.Text.RegularExpressions.RegexOptions.IgnoreCase)));
        Border turnOn = TextButtonIn(warning, "Turn on");
        ScrollViewer scroll = (ScrollViewer)Get(sw, "scroll");
        turnOn.BringIntoView();
        sw.UpdateLayout();
        double top = turnOn.TranslatePoint(new Point(0, 0), scroll).Y;
        Check("W-B09 with the warning open the window is 441 wide, no taller than 756 on a 768-high work area, and Turn on scrolls into view",
            Math.Abs(sw.ActualWidth - 441) < 0.5 && SettingsWindow.FitHeight(768) == 756 && sw.ActualHeight <= sw.MaxHeight + 0.5 && top >= -0.5 && top + turnOn.ActualHeight <= scroll.ViewportHeight + 0.5,
            sw.ActualWidth.ToString("F0") + "x" + sw.ActualHeight.ToString("F0") + " top " + top.ToString("F0") + " viewport " + scroll.ViewportHeight.ToString("F0"));
        List<string> order = new List<string>();
        WalkFocusable(sw, order);
        int laptopAt = order.IndexOf("Laptop access for chats"), bypassAt = order.IndexOf("Bypass permissions for chats");
        int cancelAt = order.LastIndexOf("Cancel"), turnOnAt = order.LastIndexOf("Turn on"), closeAt = order.LastIndexOf("Close");
        Check("W-B10 Tab order: laptop switch, bypass switch, then the warning's Cancel and Turn on, before the pinned Close",
            laptopAt >= 0 && laptopAt < bypassAt && bypassAt < cancelAt && cancelAt < turnOnAt && turnOnAt < closeAt, string.Join(" > ", order.ToArray()));
        PressBorder(turnOn);
        sw.UpdateLayout();
        string marker = File.Exists(BypassFile(d)) ? File.ReadAllText(BypassFile(d)).Trim() : "";
        Check("W-B11 Turn on: on for new chats, with the exact message, the switch on and the warning closed",
            w.BypassForNewChats && SettingsMessage(sw) == "Bypass permissions are on for new chats. Open chats keep their mode." && KnobOn(sw) && warning.Visibility == Visibility.Collapsed);
        Check("W-B12 Turn on saves a valid protected marker in chat-bypass.txt, and settings.txt doesn't mention bypass",
            BypassState.Unprotect(marker, BypassState.Entropy) == BypassState.Marker
            && (!File.Exists(Path.Combine(d, "settings.txt")) || !File.ReadAllText(Path.Combine(d, "settings.txt")).Contains("bypass")));

        // ---------- W-B13 to 16: turning off, failures, laptop access ----------
        PressBorder(track);
        Check("W-B13 Turn off: off at once with the exact message, and the file is gone",
            !w.BypassForNewChats && SettingsMessage(sw) == "Bypass permissions are off for new chats. Chats showing Bypass on keep it until you close them." && !KnobOn(sw) && !File.Exists(BypassFile(d))
            && warning.Visibility == Visibility.Collapsed);
        PressBorder(track);
        PressBorder(TextButtonIn(warning, "Turn on"));
        string failed;
        using (new FileStream(BypassFile(d), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            PressBorder(track);
            failed = SettingsMessage(sw);
        }
        Check("W-B14 a turn-off that can't change the file shows the exact error, and the switch and new chats are off",
            failed == "Couldn't save the change: " + failed.Substring(26, failed.IndexOf(". New chats") - 26) + ". New chats won't use bypass now, but it could come back after the widget restarts. Close anything using chat-bypass.txt and turn it off again."
            && failed.StartsWith("Couldn't save the change: ") && !w.BypassForNewChats && !KnobOn(sw), failed);
        sw.Close(); w.Close();

        d = Fresh("wb-laptop");
        File.WriteAllText(Path.Combine(d, "chat-access.txt"), "laptop");
        w = BypassWidget(d);
        sw = BypassSettings(w, d);
        track = (Border)Get(sw, "bypassSwitch");
        warning = (Border)Get(sw, "bypassWarning");
        PressBorder(track);
        PressBorder(TextButtonIn(warning, "Turn on"));
        bool wasOn = w.BypassForNewChats;
        Call(sw, "SetLaptop", false);
        Check("W-B15 turning laptop access off turns bypass off too, with the combined message",
            wasOn && !w.BypassForNewChats && !File.Exists(BypassFile(d)) && SettingsMessage(sw) == "Laptop access and bypass permissions are off for new chats." && !KnobOn(sw) && !track.IsEnabled,
            SettingsMessage(sw));
        Call(sw, "SetLaptop", true);
        bool offAfter = !w.BypassForNewChats && !KnobOn(sw) && SettingsMessage(sw) == "Laptop access is on for new chats.";
        PressBorder(track);
        Check("W-B16 turning laptop access back on never turns bypass on; the switch needs the warning again",
            offAfter && warning.Visibility == Visibility.Visible && !w.BypassForNewChats);
        sw.Close(); w.Close();

        // ---------- W-B17 to 20: restarts ----------
        d = Fresh("wb-restart");
        File.WriteAllText(Path.Combine(d, "chat-access.txt"), "laptop");
        w = BypassWidget(d);
        sw = BypassSettings(w, d);
        PressBorder((Border)Get(sw, "bypassSwitch"));
        PressBorder(TextButtonIn((Border)Get(sw, "bypassWarning"), "Turn on"));
        sw.Close(); w.Close();
        w = BypassWidget(d);
        sw = BypassSettings(w, d);
        Check("W-B17 after a restart, on stays on and the switch shows it", w.BypassForNewChats && w.Bypass.LoadedOn && KnobOn(sw));
        PressBorder((Border)Get(sw, "bypassSwitch"));
        sw.Close(); w.Close();
        w = BypassWidget(d);
        sw = BypassSettings(w, d);
        Check("W-B18 after a restart, off stays off", !w.BypassForNewChats && !KnobOn(sw));
        PressBorder((Border)Get(sw, "bypassSwitch"));
        PressBorder(TextButtonIn((Border)Get(sw, "bypassWarning"), "Turn on"));
        string shown;
        FileStream held = new FileStream(BypassFile(d), FileMode.Open, FileAccess.Read, FileShare.None);
        PressBorder((Border)Get(sw, "bypassSwitch"));
        shown = SettingsMessage(sw);
        sw.Close();
        // The widget's exit retry runs while the file is still locked, so the marker survives, as after a crash.
        w.Close();
        held.Dispose();
        Check("W-B19 a failed turn-off showed the could-come-back error first", shown.StartsWith("Couldn't save the change: ") && shown.Contains("it could come back after the widget restarts"), shown);
        w = BypassWidget(d);
        sw = BypassSettings(w, d);
        Check("W-B20 after that failed turn-off and a restart, bypass is honestly shown on", w.BypassForNewChats && KnobOn(sw));
        PressBorder((Border)Get(sw, "bypassSwitch"));
        sw.Close(); w.Close();
    }
}
