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
        Check("W-B02 without laptop access the switch is greyed out with its tooltip, which shows while it's disabled",
            !track.IsEnabled && track.Opacity < 1 && (string)track.ToolTip == "Turn on laptop access first" && ToolTipService.GetShowOnDisabled(track));
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
            !copyTrack.IsEnabled && (string)copyTrack.ToolTip == "Not available in a test copy" && ToolTipService.GetShowOnDisabled(copyTrack) && SettingsMessage(cs) == "Bypass permissions aren't available in a test copy."
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

    // ---------- W-B30 to 44 (u3-chip): the chat chip, New chat lowering the mode (DEC-19), the start-up notice (DEC-21) ----------
    static ChatWindow ChipChat(string d, bool bypass)
    {
        ChatWindow c = new ChatWindow(d, bypass);
        c.ShowActivated = false; c.ShowInTaskbar = false; c.Left = -6000; c.Top = 50; c.Width = 396; c.Height = 536;
        c.Show();
        c.UpdateLayout();
        return c;
    }

    static bool ChipShown(ChatWindow c)
    {
        Border chip = (Border)Get(c, "bypassChip");
        return chip.Visibility == Visibility.Visible && ((DockPanel)Get(c, "chatHeader")).Children.Contains(chip);
    }

    // What UI Automation exposes for the window: every peer's name and help text, walked from the window's own peer.
    static List<string[]> AutomationNames(Window w)
    {
        List<string[]> found = new List<string[]>();
        System.Windows.Automation.Peers.AutomationPeer top = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(w);
        Queue<System.Windows.Automation.Peers.AutomationPeer> todo = new Queue<System.Windows.Automation.Peers.AutomationPeer>();
        if (top != null) todo.Enqueue(top);
        while (todo.Count > 0)
        {
            System.Windows.Automation.Peers.AutomationPeer p = todo.Dequeue();
            found.Add(new[] { p.GetName(), p.GetHelpText() ?? "" });
            List<System.Windows.Automation.Peers.AutomationPeer> kids = p.GetChildren();
            if (kids != null) foreach (System.Windows.Automation.Peers.AutomationPeer k in kids) todo.Enqueue(k);
        }
        return found;
    }
    static string Footer(ChatWindow c) { return ((TextBlock)Get(c, "saveNote")).Text; }
    static void ValidMarker(string d) { File.WriteAllText(BypassFile(d), BypassState.Protect(BypassState.Marker, BypassState.Entropy)); }

    static async Task<List<string>> NoticeLines(string log, int waitMs)
    {
        for (int i = 0; i < waitMs / 50; i++)
        {
            if (File.Exists(log) && File.ReadAllLines(log).Length > 0) break;
            await Task.Delay(50);
        }
        await Task.Delay(200);
        return File.Exists(log) ? File.ReadAllLines(log).ToList() : new List<string>();
    }

    static WidgetWindow NoticeWidget(string d, string log)
    {
        WidgetWindow w = BypassWidget(d);
        w.ShowNotice = TestNotices.Writer(log);
        w.ShowActivated = false; w.ShowInTaskbar = false; w.Left = -6000;
        w.Show();
        return w;
    }

    static async Task BypassChip()
    {
        // W-B30 to 34: the chip and the footer
        string d = Fresh("wb-chip");
        File.WriteAllText(Path.Combine(d, "chat-access.txt"), "laptop");
        string dn = Fresh("wb-chip-nolaptop");
        ChatWindow bypassChat = ChipChat(d, true), normalChat = ChipChat(d, false), noLaptop = ChipChat(dn, true);
        Check("W-B30 the chip shows only in a bypass chat: not in a normal one, nor when the chat's own laptop access is off",
            ChipShown(bypassChat) && bypassChat.BypassMode && !ChipShown(normalChat) && !normalChat.BypassMode && !ChipShown(noLaptop) && !noLaptop.BypassMode);
        WidgetWindow tw = NewWidget(Fresh("wb-chip-theme"), true);
        ThemeOf(tw, "light");
        Color light = ((SolidColorBrush)Get(bypassChat, "bypassFill")).Color, lightText = ((SolidColorBrush)Get(bypassChat, "warningText")).Color;
        ThemeOf(tw, "dark");
        Color dark = ((SolidColorBrush)Get(bypassChat, "bypassFill")).Color, darkText = ((SolidColorBrush)Get(bypassChat, "warningText")).Color;
        Check("W-B31 the chip is amber in both themes", light == WidgetWindow.Hex("#24B26F12") && dark == WidgetWindow.Hex("#2EE5B454")
            && lightText == WidgetWindow.Hex("#8F5700") && darkText == WidgetWindow.Hex("#E5B454"), light + " " + dark);
        tw.Close();
        Border chip = (Border)Get(bypassChat, "bypassChip");
        List<string[]> exposed = AutomationNames(bypassChat), exposedNormal = AutomationNames(normalChat);
        string tip = "This chat edits files, runs commands and opens sites without asking. Sign-in files are refused by name; scripts can get past that. New chats follow Settings; this chat keeps its mode.";
        Check("W-B32 UI Automation exposes the chip once, by its exact name with the tooltip as help text, and it reads Bypass on",
            exposed.Count(x => x[0] == "Bypass permissions on for this chat" && x[1] == tip) == 1 && ((TextBlock)chip.Child).Text == "Bypass on",
            exposed.Count(x => x[0].StartsWith("Bypass")) + " bypass peers");
        Check("W-B32 a normal chat's header has no chip, and UI Automation exposes nothing about bypass",
            !((DockPanel)Get(normalChat, "chatHeader")).Children.Contains((Border)Get(normalChat, "bypassChip")) && !exposedNormal.Any(x => x[0].Contains("Bypass") || x[1].Contains("without asking")),
            string.Join(", ", exposedNormal.Where(x => x[0].Contains("Bypass")).Select(x => x[0]).ToArray()));
        Check("W-B33 the chip's tooltip is exact (DEC-9)", (string)chip.ToolTip == "This chat edits files, runs commands and opens sites without asking. Sign-in files are refused by name; scripts can get past that. New chats follow Settings; this chat keeps its mode.");
        Check("W-B34 the footer says changes and commands run without asking in a bypass chat, and asks in a normal one",
            Footer(bypassChat).EndsWith(" Reads your files. Changes and commands run without asking.") && Footer(normalChat).EndsWith(" Reads your files. Asks before changes or commands."),
            Footer(bypassChat) + " | " + Footer(normalChat));
        string dr = Fresh("wb-chip-read");
        File.WriteAllText(Path.Combine(dr, "chat-access.txt"), "laptop");
        Directory.CreateDirectory(Path.Combine(dr, "history"));
        Directory.CreateDirectory(Path.Combine(dr, "notes"));
        File.WriteAllText(Path.Combine(dr, "chat-history-folder.txt"), Path.Combine(dr, "history"));
        File.WriteAllText(Path.Combine(dr, "chat-read-folders.txt"), Path.Combine(dr, "notes"));
        ChatWindow readBypass = ChipChat(dr, true), readNormal = ChipChat(dr, false);
        Check("W-B34 with read folders, a bypass chat's footer still says changes and commands run without asking; a normal one doesn't",
            Footer(readBypass) == "Can read your chat history and chosen folders. Changes and commands run without asking." && Footer(readNormal) == "Can read your chat history and chosen folders.",
            Footer(readBypass) + " | " + Footer(readNormal));
        readBypass.Close(); readNormal.Close();
        bypassChat.Close(); normalChat.Close(); noLaptop.Close();

        // W-B35 to 39: the mode for the chat's life, and New chat (DEC-19)
        d = Fresh("wb-mode");
        File.WriteAllText(Path.Combine(d, "chat-access.txt"), "laptop");
        ValidMarker(d);
        WidgetWindow w = BypassWidget(d);
        ChatWindow open = ChipChat(d, w.BypassForNewChats);
        open.AttachWidget(w);
        ChatWindow normal = ChipChat(d, false);
        normal.AttachWidget(w);
        Call(open, "StartNewChat");
        Check("W-B38 bypass still on, then New chat: the chat stays in bypass", open.BypassMode && ChipShown(open));
        w.Bypass.TurnOff();
        Check("W-B35 turning bypass off leaves an open bypass chat in bypass, with its chip", open.BypassMode && ChipShown(open));
        open.UpdateLayout();
        Check("W-B36 the mode changes only at New chat: until then the chip and footer stay", ChipShown(open) && Footer(open).EndsWith(" Changes and commands run without asking."));
        Call(open, "StartNewChat");
        Check("W-B37 bypass turned off, then New chat in that window: a normal chat, with no chip and the normal footer",
            !open.BypassMode && !ChipShown(open) && Footer(open).EndsWith(" Asks before changes or commands."), Footer(open));
        w.Bypass.TurnOn();
        Call(normal, "StartNewChat");
        Call(open, "StartNewChat");
        Check("W-B39 bypass turned on later, then New chat: windows opened or lowered to normal stay normal (never raised)",
            w.BypassForNewChats && !normal.BypassMode && !ChipShown(normal) && !open.BypassMode && !ChipShown(open));
        open.Close(); normal.Close();
        w.Bypass.TurnOff();
        w.Close();

        // W-B40 to 44: the start-up notice
        d = Fresh("wb-notice");
        File.WriteAllText(Path.Combine(d, "chat-access.txt"), "laptop");
        ValidMarker(d);
        string log = Path.Combine(d, "notices-1.log");
        WidgetWindow nw = NoticeWidget(d, log);
        List<string> lines = await NoticeLines(log, 3000);
        Call(nw, "AnnounceBypass");
        await Task.Delay(300);
        lines = File.Exists(log) ? File.ReadAllLines(log).ToList() : new List<string>();
        string[] parts = lines.Count == 1 ? lines[0].Split('\t') : new string[0];
        Check("W-B40 bypass loading on at start gives exactly one notice, with the exact title and text",
            lines.Count == 1 && parts.Length == 3 && parts[1] == "Bypass permissions are on"
            && parts[2] == "New chats will edit files, run commands and open sites without asking. If you didn't turn this on, turn it off in Settings.", string.Join(" | ", lines.ToArray()));
        nw.Close();
        string log2 = Path.Combine(d, "notices-2.log");
        nw = NoticeWidget(d, log2);
        List<string> again = await NoticeLines(log2, 3000);
        Check("W-B43 the next widget start with the marker still valid gives one notice again", again.Count == 1 && again[0].Contains("\tBypass permissions are on\t"), string.Join(" | ", again.ToArray()));
        nw.Close();

        string doff = Fresh("wb-notice-off");
        File.WriteAllText(Path.Combine(doff, "chat-access.txt"), "laptop");
        string logOff = Path.Combine(doff, "notices.log");
        WidgetWindow offWidget = NoticeWidget(doff, logOff);
        List<string> none = await NoticeLines(logOff, 800);
        Check("W-B41 bypass off at start: no notice", none.Count == 0 && !offWidget.BypassForNewChats, string.Join(" | ", none.ToArray()));
        string turnedOn = offWidget.Bypass.TurnOn();
        Call(offWidget, "AnnounceBypass");
        List<string> later = await NoticeLines(logOff, 800);
        Check("W-B42 bypass turned on during the run gives no start-up notice", turnedOn == null && offWidget.BypassForNewChats && later.Count == 0, string.Join(" | ", later.ToArray()));
        offWidget.Bypass.TurnOff();
        offWidget.Close();

        string dplain = Fresh("wb-notice-plain");
        File.WriteAllText(Path.Combine(dplain, "chat-access.txt"), "laptop");
        File.WriteAllText(BypassFile(dplain), BypassState.Marker);
        string logPlain = Path.Combine(dplain, "notices.log");
        WidgetWindow plain = NoticeWidget(dplain, logPlain);
        List<string> plainLines = await NoticeLines(logPlain, 800);
        Check("W-B44 a plain-text marker: bypass off and no notice", !plain.BypassForNewChats && plainLines.Count == 0, string.Join(" | ", plainLines.ToArray()));
        plain.Close();
    }
}
