using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Text.RegularExpressions;
// Saved instructions for new chats (v0.3.0): reading, normalising, the 4,000-character limit and saving.
// In process, on fake data folders only; no claude is ever started. saved-instructions.exe <out folder>
static class H17
{
    static string root;
    static int fails, passes;
    static void Log(string s) { Console.WriteLine(s); Console.Out.Flush(); }
    static void Check(string what, bool ok, string info = "")
    {
        Log((ok ? "PASS " : "FAIL ") + what + (info.Length > 0 ? " [" + info.Replace("\r", "\\r").Replace("\n", "\\n") + "]" : ""));
        if (ok) passes++; else fails++;
    }
    static string Fresh(string name)
    {
        string d = Path.Combine(root, name);
        if (Directory.Exists(d)) Directory.Delete(d, true);
        Directory.CreateDirectory(d);
        return d;
    }
    static string FileIn(string d) { return Path.Combine(d, ChatInstructions.FileName); }
    static void Write(string d, string text) { File.WriteAllText(FileIn(d), text, new UTF8Encoding(false)); }
    static string Show(string s) { return s == null ? "null" : s.Length > 60 ? s.Substring(0, 60) + "... (" + s.Length + ")" : s; }

    [STAThread]
    static void Main(string[] args)
    {
        root = args[0];
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        try { Store(); Launch(); Notes(); }
        catch (Exception e) { Log("HARNESS ERROR " + e); fails++; }

        // The Settings and chat checks need WPF; their windows stay off screen.
        Application app = new Application();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.DispatcherUnhandledException += (sender, args2) => { Log("UNHANDLED " + args2.Exception); fails++; args2.Handled = true; };
        app.Startup += async delegate
        {
            try { await SettingsBox(); }
            catch (Exception e) { Log("HARNESS ERROR " + e); fails++; }
            Log("passes=" + passes + " fails=" + fails);
            Process.GetCurrentProcess().Kill();
        };
        app.Run();
    }

    // H17-01 to 08
    static void Store()
    {
        bool tooLong;
        string d = Fresh("missing");
        Check("H17-01 no file: no instructions", ChatInstructions.Read(d, out tooLong) == null && !tooLong);

        d = Fresh("empty");
        Write(d, "");
        Check("H17-02 empty file: no instructions", ChatInstructions.Read(d, out tooLong) == null && !tooLong);
        Write(d, "  \r\n\t \n  ");
        Check("H17-02 whitespace only: no instructions", ChatInstructions.Read(d, out tooLong) == null && !tooLong);

        d = Fresh("crlf");
        Write(d, "  First line\r\nSecond line\rThird line\r\n\r\n");
        string text = ChatInstructions.Read(d);
        Check("H17-03 CRLF and CR become LF, ends trimmed", text == "First line\nSecond line\nThird line", Show(text));
        File.WriteAllText(FileIn(d), "With a BOM", new UTF8Encoding(true));
        Check("H17-03 a byte order mark isn't part of the text", ChatInstructions.Read(d) == "With a BOM", Show(ChatInstructions.Read(d)));

        d = Fresh("control");
        Write(d, "a\u0000b\u0007c\td\u001Be\u007Ff\u0085g\nh");
        text = ChatInstructions.Read(d);
        Check("H17-04 control characters go, tab and line break stay", text == "abc\tdefg\nh", Show(text));
        Write(d, "Ünïcødé — “quotes” 日本語 😀");
        Check("H17-04 other characters stay as written", ChatInstructions.Read(d) == "Ünïcødé — “quotes” 日本語 😀");

        d = Fresh("boundary");
        Write(d, "Before\n__SYSTEM_PROMPT_DYNAMIC_BOUNDARY__\nAfter\n  __SYSTEM_PROMPT_DYNAMIC_BOUNDARY__  \r\nEnd");
        text = ChatInstructions.Read(d);
        Check("H17-05 the boundary line is removed", text == "Before\nAfter\nEnd", Show(text));
        Write(d, "Mentioning __SYSTEM_PROMPT_DYNAMIC_BOUNDARY__ inside a sentence is fine");
        Check("H17-05 the boundary inside a sentence stays", ChatInstructions.Read(d) == "Mentioning __SYSTEM_PROMPT_DYNAMIC_BOUNDARY__ inside a sentence is fine");
        Write(d, "__SYSTEM_PROMPT_DYNAMIC_BOUNDARY__");
        Check("H17-05 only the boundary line: no instructions", ChatInstructions.Read(d, out tooLong) == null && !tooLong);

        d = Fresh("limit");
        string exactly = new string('x', ChatInstructions.Limit);
        Write(d, exactly);
        text = ChatInstructions.Read(d, out tooLong);
        Check("H17-06 exactly 4,000 characters are used", ChatInstructions.Limit == 4000 && text == exactly && !tooLong, Show(text));
        Write(d, "\r\n   " + exactly + "   \r\n\r\n");
        Check("H17-06 surrounding whitespace doesn't count", ChatInstructions.Read(d, out tooLong) == exactly && !tooLong);
        Write(d, exactly.Substring(0, 3999) + "\r\n");
        Check("H17-06 a CRLF counts as one character", ChatInstructions.Read(d, out tooLong) == exactly.Substring(0, 3999) && !tooLong);

        Write(d, exactly + "y");
        text = ChatInstructions.Read(d, out tooLong);
        Check("H17-07 4,001 characters: not used, reported as too long", text == null && tooLong, Show(text));
        Check("H17-07 the one-argument Read gives nothing for too long", ChatInstructions.Read(d) == null);
        Check("H17-07 the file over the limit is left as it is", File.ReadAllText(FileIn(d)) == exactly + "y");

        d = Fresh("save");
        Check("H17-08 Save writes the text as typed", ChatInstructions.Save(d, "Line one\r\nLine two") && File.ReadAllText(FileIn(d)) == "Line one\r\nLine two");
        byte[] bytes = File.ReadAllBytes(FileIn(d));
        Check("H17-08 saved as UTF-8 without a byte order mark", bytes.Length > 2 && bytes[0] == (byte)'L');
        Check("H17-08 read back normalised", ChatInstructions.Read(d) == "Line one\nLine two");
        Check("H17-08 no temporary file left", Directory.GetFiles(d).Length == 1, string.Join(",", Directory.GetFiles(d).Select(Path.GetFileName).ToArray()));
        Check("H17-08 Save of empty text deletes the file", ChatInstructions.Save(d, "") && !File.Exists(FileIn(d)));
        ChatInstructions.Save(d, "Again");
        Check("H17-08 Save of whitespace deletes the file", ChatInstructions.Save(d, " \r\n\t ") && !File.Exists(FileIn(d)));
        Check("H17-08 Save of empty text with no file is fine", ChatInstructions.Save(d, null) && !File.Exists(FileIn(d)));
        ChatInstructions.Save(d, "Keep me");
        bool usable = ChatInstructions.Save(d, exactly + "y");
        Check("H17-08 Save over the limit keeps the text on disk but unused, and says so", !usable && File.ReadAllText(FileIn(d)) == exactly + "y"
            && ChatInstructions.Read(d, out tooLong) == null && tooLong);
        Check("H17-08 Save at the limit with CRLF line breaks is allowed", ChatInstructions.Save(d, exactly.Substring(0, 3998) + "\r\nz") && ChatInstructions.Read(d) == exactly.Substring(0, 3998) + "\nz");
    }

    // ---------- H17-10 to 16: launching through the system prompt file ----------
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    [DllImport("shell32.dll", SetLastError = true)] static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string line, out int count);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);

    static List<string> Tokens(string arguments)
    {
        int count;
        IntPtr list = CommandLineToArgvW("x.exe " + arguments, out count);
        List<string> tokens = new List<string>();
        try
        {
            for (int i = 1; i < count; i++) tokens.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(list, i * IntPtr.Size)));
        }
        finally { LocalFree(list); }
        return tokens;
    }

    static object Static(string name, params object[] args)
    {
        MethodInfo m = typeof(ChatClient).GetMethods(Any).First(x => x.Name == name && x.GetParameters().Length == args.Length);
        return m.Invoke(null, args);
    }
    static string Const(string name) { return (string)typeof(ChatClient).GetField(name, Any).GetValue(null); }

    static ChatRequest Request(string instructions)
    {
        ChatRequest r = new ChatRequest();
        r.Prompt = "Hello";
        r.Model = "";
        r.Effort = "";
        r.Instructions = instructions;
        return r;
    }

    // The 1768b9e argument list for a request, written out by hand, with the old inline rules flag.
    static List<string> OldTokens(ChatRequest r, string tools, string rules, List<string> addDirs, string settings)
    {
        List<string> t = new List<string> { "-p", "--safe-mode", "--restricted", "--no-session-persistence", "--strict-mcp-config",
            "--permission-mode", "manual", "--permission-prompt-tool", "stdio",
            "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages" };
        if (r.Model.Length > 0) { t.Add("--model"); t.Add(r.Model); }
        if (r.Effort.Length > 0) { t.Add("--effort"); t.Add(r.Effort); }
        t.Add("--tools"); t.Add(tools);
        t.Add("--append-system-prompt"); t.Add(rules);
        if (addDirs.Count > 0) { t.Add("--add-dir"); t.AddRange(addDirs); }
        t.Add("--settings"); t.Add(settings);
        t.Add("--disallowedTools");
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (string file in new[] { Path.Combine(home, ".claude", ".credentials.json"), Path.Combine(home, ".claude.json") })
        {
            string rule = "//" + file.Replace('\\', '/').Replace(":", "");
            t.Add("Read(" + rule + ")"); t.Add("Edit(" + rule + ")"); t.Add("Write(" + rule + ")");
        }
        t.AddRange(new[] { "Bash(claude auth *)", "Bash(claude login*)", "Bash(claude logout*)", "Bash(*.credentials.json*)" });
        return t;
    }

    static void Launch()
    {
        string savedTemp = Environment.GetEnvironmentVariable("TEMP"), savedTmp = Environment.GetEnvironmentVariable("TMP");
        string temp = Path.Combine(root, "temp");
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("TEMP", temp);
        Environment.SetEnvironmentVariable("TMP", temp);
        try { LaunchChecks(temp); }
        finally
        {
            Environment.SetEnvironmentVariable("TEMP", savedTemp);
            Environment.SetEnvironmentVariable("TMP", savedTmp);
        }
    }

    static void LaunchChecks(string temp)
    {
        const string Marker = "Answer like a pirate called Quillfeather";
        string promptFile = Path.Combine(temp, "ClaudeAccountWidget-chat", "f.txt");
        string settings = Path.Combine(temp, "ClaudeAccountWidget-ask-first.json");
        string fileArg = promptFile.Replace('\\', '/');

        // H17-10: three kinds of request; the only change from 1768b9e is the rules flag
        ChatRequest plain = Request(Marker);
        ChatRequest laptop = Request(Marker);
        laptop.Laptop = true; laptop.Model = "opus"; laptop.Effort = "high";
        laptop.DefaultOutputFolder = Path.Combine(root, "out folder");
        laptop.CurrentOutputFolder = delegate { return null; };
        ChatRequest reading = Request(null);
        reading.MemoryFolder = Path.Combine(root, "memory");
        reading.ReadFolders.Add(Path.Combine(root, "notes"));
        reading.Links.Add("https://example.com/page");
        List<string> drives = DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed).Select(x => x.Name.Replace('\\', '/')).ToList();
        var cases = new[]
        {
            new { Name = "plain", R = plain, Tools = "", Rules = Const("StyleRule") + Const("NoRecallRule") + Const("DataRule"), Dirs = new List<string>() },
            new { Name = "laptop", R = laptop, Tools = "Read,Grep,Glob,Edit,Write,Bash,WebSearch,WebFetch",
                Rules = Const("StyleRule") + Const("NoRecallRule") + Const("LaptopRule") + (string)Static("OutputRule", laptop) + Const("DataRule"), Dirs = drives },
            new { Name = "reading", R = reading, Tools = "Read,Grep,Glob,WebFetch",
                Rules = Const("StyleRule") + Const("RecallRule") + Const("FolderOnlyRule") + Const("LinkRule") + (string)Static("ReadFoldersRule", reading) + Const("DataRule"),
                Dirs = reading.ReadFolders.Select(f => f.Replace('\\', '/')).ToList() },
        };
        foreach (var c in cases)
        {
            ProcessStartInfo start = (ProcessStartInfo)Static("BuildStart", Path.Combine(root, "claude.exe"), c.R, promptFile);
            List<string> got = Tokens(start.Arguments);
            List<string> want = OldTokens(c.R, c.Tools, c.Rules, c.Dirs, settings);
            int at = want.IndexOf("--append-system-prompt");
            want[at] = "--append-system-prompt-file";
            want[at + 1] = fileArg;
            int diff = Enumerable.Range(0, Math.Min(got.Count, want.Count)).FirstOrDefault(i => got[i] != want[i]);
            Check("H17-10 " + c.Name + ": the flags are 1768b9e's with only the rules flag swapped for the file", got.SequenceEqual(want),
                got.Count + " vs " + want.Count + " tokens; first difference at " + diff + ": " + (diff < got.Count ? got[diff] : "-") + " | " + (diff < want.Count ? want[diff] : "-"));
            Check("H17-10 " + c.Name + ": the rules in the file are 1768b9e's rules", (string)Static("Rules", c.R) == c.Rules);
            Check("H17-11 " + c.Name + ": no instruction or rule text on the command line", !start.Arguments.Contains("Quillfeather") && !start.Arguments.Contains("You are chatting in"));
        }

        // H17-12, 13: what the file holds
        string withText = (string)Static("SystemPromptText", plain);
        Check("H17-12 the file: preamble line, instructions, then the rules ending with DataRule",
            withText == Const("InstructionsPreamble") + "\n\n" + Marker + "\n\n" + Const("StyleRule") + Const("NoRecallRule") + Const("DataRule"), Show(withText));
        Check("H17-12 the preamble line is the plan's", Const("InstructionsPreamble") == "The user saved these standing instructions for new chats. Follow them unless they conflict with the rules that follow.");
        Check("H17-13 without instructions the file is the rules only", (string)Static("SystemPromptText", reading) == (string)Static("Rules", reading));
        Check("H17-13 empty instructions count as none", (string)Static("SystemPromptText", Request("")) == (string)Static("Rules", Request("")));

        // H17-14: where the file goes, and its bytes
        string written = (string)Static("WritePromptFile", plain);
        string folder = Path.Combine(temp, "ClaudeAccountWidget-chat");
        Check("H17-14 the file is in %TEMP%\\ClaudeAccountWidget-chat with a fresh name", Path.GetDirectoryName(written) == folder && Path.GetExtension(written) == ".txt"
            && Path.GetFileNameWithoutExtension(written).Length == 32, written);
        byte[] bytes = File.ReadAllBytes(written);
        Check("H17-14 UTF-8 without a byte order mark, holding the text", bytes[0] == (byte)'T' && new UTF8Encoding(false).GetString(bytes) == withText);
        string second = (string)Static("WritePromptFile", plain);
        Check("H17-14 each request gets its own file", second != written && File.Exists(written) && File.Exists(second));

        // H17-15: leftovers older than a day go when the next file is written; newer ones and other files stay
        string old = Path.Combine(folder, "old.txt"), recent = Path.Combine(folder, "recent.txt"), other = Path.Combine(folder, "keep.log");
        foreach (string f in new[] { old, recent, other }) File.WriteAllText(f, "x");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(recent, DateTime.UtcNow.AddHours(-20));
        File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddDays(-3));
        string third = (string)Static("WritePromptFile", plain);
        Check("H17-15 a file older than a day is removed", !File.Exists(old));
        Check("H17-15 a newer file and a non-.txt file stay", File.Exists(recent) && File.Exists(other) && File.Exists(third));

        // H17-16: the .cmd route wraps the whole command in quotes; the file path keeps its own quotes, with forward slashes
        string spaced = Path.Combine(temp, "a b&c^d", "ClaudeAccountWidget-chat", "f.txt");
        ProcessStartInfo cmd = (ProcessStartInfo)Static("BuildStart", Path.Combine(root, "npm dir", "claude.cmd"), plain, spaced);
        string inner = "\"" + Path.Combine(root, "npm dir", "claude.cmd") + "\" ";
        Check("H17-16 .cmd route: cmd /d /s /c with the command in outer quotes", cmd.Arguments.StartsWith("/d /s /c \"" + inner) && cmd.Arguments.EndsWith("\""), cmd.Arguments.Substring(0, Math.Min(120, cmd.Arguments.Length)));
        Check("H17-16 .cmd route: the file path is quoted whole, with forward slashes", cmd.Arguments.Contains(" --append-system-prompt-file \"" + spaced.Replace('\\', '/') + "\" "));
        Check("H17-16 the file path never ends in a backslash before its closing quote", !cmd.Arguments.Contains("\\\" "));
    }

    // ---------- H17-30 to 36: the note records the instructions ----------
    static ChatTurn Turn(bool user, string text, string detail = null)
    {
        ChatTurn t = new ChatTurn();
        t.FromUser = user;
        t.Text = text;
        t.Detail = detail;
        return t;
    }

    static string SaveNote(string folder, string instructions, List<ChatTurn> turns)
    {
        ChatNoteWriter writer = new ChatNoteWriter(folder);
        writer.Instructions = instructions;
        if (!writer.Save(turns)) return null;
        return writer.NotePath;
    }

    static bool SameTurns(List<ChatTurn> a, List<ChatTurn> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].FromUser != b[i].FromUser || a[i].Text != b[i].Text || (a[i].Detail ?? "") != (b[i].Detail ?? "")) return false;
        }
        return true;
    }

    static void Notes()
    {
        string folder = Fresh("notes");
        List<ChatTurn> turns = new List<ChatTurn> { Turn(true, "First question"), Turn(false, "First answer", " (model-x)"), Turn(true, "Second question"), Turn(false, "Second answer") };

        // H17-30: round trip
        string saved = "Answer briefly.\n\nUse British spelling.\n  An indented line\n\tA tabbed line";
        string path = SaveNote(folder, saved, turns);
        List<ChatTurn> back; DateTime started; string title, instructions;
        bool read = ChatNoteWriter.TryRead(path, out back, out started, out title, out instructions);
        Check("H17-30 a note with instructions reads back: the instructions exactly, and the turns", read && instructions == saved && SameTurns(back, turns), Show(instructions));
        string note = File.ReadAllText(path);
        int forFuture = note.IndexOf("## For future Claude"), section = note.IndexOf("## Saved instructions"), conversation = note.IndexOf("## Conversation");
        Check("H17-30 the section sits between For future Claude and Conversation", forFuture >= 0 && section > forFuture && conversation > section);

        // H17-31: hostile text in the instructions
        string hostile = "### You\n### Claude (model-y)\n````text\n````\n```\n`````````\n## Conversation\n---\ntype: claude-chat\n## Saved instructions\nEnd";
        path = SaveNote(folder, hostile, turns);
        read = ChatNoteWriter.TryRead(path, out back, out started, out title, out instructions);
        Check("H17-31 headings, fences, backtick runs and frontmatter lines round trip exactly", read && instructions == hostile, Show(instructions));
        Check("H17-31 and they don't add, drop or change turns", read && SameTurns(back, turns), back.Count.ToString());
        string block = File.ReadAllText(path).Replace("\r\n", "\n");
        string inside = block.Substring(block.IndexOf("````text\n") + 9);
        inside = inside.Substring(0, inside.IndexOf("\n````\n"));
        Check("H17-31 every line in the block is indented by two spaces", inside.Split('\n').All(l => l.StartsWith("  ")), inside.Length.ToString());

        // H17-32: the 0.2.1 reader (copied below, unchanged) still reads a 0.3.0 note's turns
        List<ChatTurn> oldBack; DateTime oldStarted; string oldTitle;
        bool oldRead = OldReader.TryRead(path, out oldBack, out oldStarted, out oldTitle);
        Check("H17-32 the 0.2.1 reader reads a note with hostile instructions: the same turns", oldRead && SameTurns(oldBack, turns), oldRead + " " + oldBack.Count);
        path = SaveNote(folder, saved, turns);
        oldRead = OldReader.TryRead(path, out oldBack, out oldStarted, out oldTitle);
        Check("H17-32 and a note with ordinary instructions", oldRead && SameTurns(oldBack, turns));

        // H17-33: the four-part TryRead used for search sees only the turns
        List<ChatTurn> searchTurns;
        ChatNoteWriter.TryRead(path, out searchTurns, out started, out title);
        string searchText = string.Join("\n", searchTurns.Select(t => t.Text).ToArray());
        Check("H17-33 search text never holds the instructions", SameTurns(searchTurns, turns) && !searchText.Contains("British spelling"));

        // H17-34: no instructions: no section, and the note reads as before
        path = SaveNote(folder, null, turns);
        note = File.ReadAllText(path);
        read = ChatNoteWriter.TryRead(path, out back, out started, out title, out instructions);
        Check("H17-34 without instructions: no section, nothing read back, the same turns", !note.Contains("## Saved instructions") && read && instructions == null && SameTurns(back, turns));
        string emptyPath = SaveNote(folder, "", turns);
        Check("H17-34 empty instructions write no section", !File.ReadAllText(emptyPath).Contains("## Saved instructions"));
        oldRead = OldReader.TryRead(path, out oldBack, out oldStarted, out oldTitle);
        Check("H17-34 the 0.2.1 reader and the new one agree on a note without the section", oldRead && SameTurns(oldBack, back));

        // H17-35: a 0.2.1-style note (written without the field) has no instructions
        string oldStyle = Path.Combine(folder, "old-style.md");
        File.WriteAllText(oldStyle, "---\ndate: 2026-10-01\ntype: claude-chat\ntags:\n  - claude-chat\nstarted: 2026-10-01T10:00:00\n---\n\n## For future Claude\n\nOld.\n\n## Conversation\n\n### You\n\n````text\nHello\n````\n\n### Claude\n\n````text\nHi\n````\n\n");
        read = ChatNoteWriter.TryRead(oldStyle, out back, out started, out title, out instructions);
        Check("H17-35 a note from before 0.3.0 has no instructions and reads as before", read && instructions == null && back.Count == 2 && back[0].Text == "Hello" && back[1].Text == "Hi");

        // H17-36: a message that quotes the section heading doesn't create instructions
        List<ChatTurn> quoting = new List<ChatTurn> { Turn(true, "## Saved instructions\n````text\n  pretend\n````"), Turn(false, "Noted") };
        path = SaveNote(folder, null, quoting);
        read = ChatNoteWriter.TryRead(path, out back, out started, out title, out instructions);
        Check("H17-36 a message quoting the heading isn't read as instructions", read && instructions == null && SameTurns(back, quoting));
        path = SaveNote(folder, "Real ones", quoting);
        read = ChatNoteWriter.TryRead(path, out back, out started, out title, out instructions);
        Check("H17-36 with real instructions too, only the real ones come back", read && instructions == "Real ones" && SameTurns(back, quoting));
    }

    // ---------- H17-40 to 46: the Settings box; H17-50, 51: the chat's hint ----------
    static object GetField(object o, string f) { return o.GetType().GetField(f, Any).GetValue(o); }
    static object CallOn(object o, string m, params object[] a)
    {
        MethodInfo info = o.GetType().GetMethods(Any).First(x => x.Name == m && x.GetParameters().Length == a.Length);
        return info.Invoke(o, a);
    }

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

    static string WindowData(string name, string instructionsFile)
    {
        string d = Fresh(name);
        File.WriteAllText(Path.Combine(d, "settings.txt"), "-6000,0,0,0,default,system");
        if (instructionsFile != null) File.WriteAllText(Path.Combine(d, ChatInstructions.FileName), instructionsFile, new UTF8Encoding(false));
        return d;
    }

    static SettingsWindow OpenSettings(string d, out WidgetWindow owner)
    {
        owner = new WidgetWindow(d, Path.Combine(root, "account.json"), null, null, 300, 0, true);
        SettingsWindow sw = new SettingsWindow(owner, d);
        sw.MaxHeight = SettingsWindow.FitHeight(768);
        CallOn(owner, "ShowOffScreen", sw);
        sw.UpdateLayout();
        return sw;
    }

    static async Task SettingsBox()
    {
        File.WriteAllText(Path.Combine(root, "account.json"), "{\"oauthAccount\":{\"emailAddress\":\"sample@example.com\",\"planDisplayName\":\"Max\"}}");
        WidgetWindow owner;

        // H17-40, 41, 42: an empty start, the limit, the counter and Save
        string d = WindowData("set-empty", null);
        SettingsWindow sw = OpenSettings(d, out owner);
        TextBox box = (TextBox)GetField(sw, "instructionsBox");
        TextBlock count = (TextBlock)GetField(sw, "instructionsCount");
        TextBlock notice = (TextBlock)GetField(sw, "instructionsNotice");
        Check("H17-40 the box: empty, multi-line, limited to 4,000 typed characters", box.Text == "" && box.AcceptsReturn && box.MaxLength == 4000 && box.TextWrapping == TextWrapping.Wrap);
        Check("H17-40 named for screen readers, with the hint below", System.Windows.Automation.AutomationProperties.GetName(box) == "Saved instructions"
            && FindText(sw, "Sent with every new chat. Chats already open keep what they started with."));
        Check("H17-41 the counter starts at 0 / 4,000 and the notice is hidden", count.Text == "0 / 4,000" && notice.Visibility == Visibility.Collapsed, count.Text);
        box.Text = "Answer in short paragraphs.\r\nUse metric units.";
        Check("H17-41 the counter follows the text, a line break counting once", count.Text == "45 / 4,000", count.Text);
        CallOn(sw, "Save");
        string file = Path.Combine(d, ChatInstructions.FileName);
        Check("H17-42 Save writes chat-instructions.txt as typed", File.Exists(file) && File.ReadAllText(file) == "Answer in short paragraphs.\r\nUse metric units.");
        Check("H17-42 and says Saved", ((TextBlock)GetField(sw, "message")).Text == "Saved.", ((TextBlock)GetField(sw, "message")).Text);
        CallOn(sw, "Save");
        Check("H17-42 saving again with no change: Nothing to save", ((TextBlock)GetField(sw, "message")).Text == "Nothing to save.");
        box.Text = "   ";
        CallOn(sw, "Save");
        Check("H17-43 an empty box deletes the file", !File.Exists(file));
        sw.Close(); owner.Close();

        // H17-44, 45: the layout fits a 768-high work area, and Tab reaches the box between the read folders and the shortcut
        d = WindowData("set-layout", new string('w', 3000).Replace("wwwwwwwwww", "word word "));
        sw = OpenSettings(d, out owner);
        await Task.Delay(100);
        sw.UpdateLayout();
        Check("H17-44 Settings stays 441 wide (within display rounding) and at most 756 tall with a long text", Math.Abs(sw.ActualWidth - 441) < 0.5 && sw.ActualHeight <= 756.5, sw.Width + " / " + sw.ActualWidth.ToString("F2") + " x " + sw.ActualHeight.ToString("F2") + " max " + sw.MaxHeight);
        box = (TextBox)GetField(sw, "instructionsBox");
        Check("H17-44 the box grows only to its own limit and scrolls inside", box.ActualHeight <= 150.5 && box.VerticalScrollBarVisibility == ScrollBarVisibility.Hidden, box.ActualHeight.ToString("F0"));
        List<string> order = new List<string>();
        WalkFocusable(sw, order);
        string joined = string.Join(" > ", order);
        int at = order.IndexOf("Saved instructions");
        Check("H17-45 Tab order: after the read folders' Add folder, before the shortcut, Close and Save still last", at > 0 && order[at - 1] == "Add folder"
            && order.IndexOf("New chat shortcut") > at && order[order.Count - 2] == "Close" && order[order.Count - 1] == "Save", joined);
        sw.Close(); owner.Close();

        // H17-46: a file over the limit
        foreach (int size in new[] { 4001, 6000 })
        {
            string big = new string('a', size - 2) + "\r\nb";
            d = WindowData("set-over-" + size, big);
            sw = OpenSettings(d, out owner);
            box = (TextBox)GetField(sw, "instructionsBox");
            count = (TextBlock)GetField(sw, "instructionsCount");
            notice = (TextBlock)GetField(sw, "instructionsNotice");
            string real = size.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
            Check("H17-46 " + size + ": the box shows the whole text, uncut", box.Text == big, box.Text.Length.ToString());
            Check("H17-46 " + size + ": the counter shows the real count in the warning colour", count.Text == real + " / 4,000"
                && count.Foreground == notice.Foreground, count.Text);
            Check("H17-46 " + size + ": the notice is shown, with the plan's wording", notice.Visibility == Visibility.Visible
                && notice.Text == "Your saved instructions are longer than 4,000 characters, so new chats start without them. Shorten them to use them again.");
            CallOn(sw, "Save");
            Check("H17-46 " + size + ": with no change there's nothing to save and the file stays", File.ReadAllText(Path.Combine(d, ChatInstructions.FileName)) == big);
            box.Text = big + " more";
            CallOn(sw, "Save");
            bool tooLong;
            Check("H17-46 " + size + ": saving text still over the limit keeps it on disk, unused, with the notice", File.ReadAllText(Path.Combine(d, ChatInstructions.FileName)) == big + " more"
                && ChatInstructions.Read(d, out tooLong) == null && tooLong && notice.Visibility == Visibility.Visible);
            box.Text = new string('a', 4000);
            Check("H17-46 " + size + ": shortened to 4,000, the notice goes and the counter is normal", notice.Visibility == Visibility.Collapsed && count.Text == "4,000 / 4,000" && count.Foreground != notice.Foreground, count.Text);
            CallOn(sw, "Save");
            Check("H17-46 " + size + ": saved at 4,000 it's used again", ChatInstructions.Read(d) == new string('a', 4000));
            sw.Close(); owner.Close();
        }

        // H17-50, 51: the chat's empty state
        d = WindowData("hint-on", "Be brief.");
        ChatWindow chat = new ChatWindow(d);
        TextBlock hint = (TextBlock)GetField(chat, "instructionsHint");
        Check("H17-50 a chat with instructions says so while it's empty", hint.Text == "Using your saved instructions." && hint.Visibility == Visibility.Visible, hint.Text);
        File.Delete(Path.Combine(d, ChatInstructions.FileName));
        CallOn(chat, "StartNewChat");
        Check("H17-50 New chat without instructions: no line", hint.Visibility == Visibility.Collapsed);
        chat.Close();
        d = WindowData("hint-over", new string('a', 4001));
        chat = new ChatWindow(d);
        hint = (TextBlock)GetField(chat, "instructionsHint");
        Check("H17-51 a file over the limit: the chat says they weren't used", hint.Text == "Saved instructions not used (too long). See Settings." && hint.Visibility == Visibility.Visible, hint.Text);
        Check("H17-51 and the chat has no instructions", GetField(chat, "instructions") == null && (bool)GetField(chat, "instructionsTooLong"));
        chat.Close();
        d = WindowData("hint-none", null);
        chat = new ChatWindow(d);
        Check("H17-50 no file: no line", ((TextBlock)GetField(chat, "instructionsHint")).Visibility == Visibility.Collapsed);
        chat.Close();
    }

    static bool FindText(DependencyObject node, string text)
    {
        TextBlock t = node as TextBlock;
        if (t != null && t.Text == text) return true;
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++) if (FindText(VisualTreeHelper.GetChild(node, i), text)) return true;
        return false;
    }
}

// The 0.2.1 note reader, copied unchanged from widget.cs at 1768b9e, to check that 0.3.0 notes stay readable by it.
static class OldReader
{
    static string UnquoteTitle(string quoted)
    {
        return quoted.Substring(1, quoted.Length - 2).Replace("\\\"", "\"").Replace("\\\\", "\\");
    }

    public static bool TryRead(string path, out List<ChatTurn> turns, out DateTime noteStart, out string title)
    {
        turns = new List<ChatTurn>();
        noteStart = DateTime.Now;
        title = null;
        try
        {
            string[] lines = File.ReadAllText(path).Replace("\r\n", "\n").Split('\n');
            if (lines.Length == 0 || lines[0] != "---") return false;
            bool isChat = false;
            int line = 1;
            for (; line < lines.Length && lines[line] != "---"; line++)
            {
                if (lines[line] == "type: claude-chat") isChat = true;
                if (Regex.IsMatch(lines[line], "^title: \".*\"$")) title = UnquoteTitle(lines[line].Substring(7));
                if (lines[line].StartsWith("started: "))
                {
                    DateTime parsed;
                    if (DateTime.TryParse(lines[line].Substring(9), CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)) noteStart = parsed;
                }
            }
            if (!isChat) return false;

            for (; line < lines.Length; line++)
            {
                bool you = lines[line] == "### You";
                if (!you && !lines[line].StartsWith("### Claude")) continue;

                ChatTurn turn = new ChatTurn();
                turn.FromUser = you;
                if (!you && lines[line].Length > 10) turn.Detail = lines[line].Substring(10);

                int open = line + 1;
                while (open < lines.Length && lines[open].Length == 0) open++;
                if (open >= lines.Length || !Regex.IsMatch(lines[open], "^`{4,}text$")) return false;
                string fence = lines[open].Substring(0, lines[open].Length - 4);

                int close = open + 1;
                while (close < lines.Length && lines[close] != fence) close++;
                if (close >= lines.Length) return false;
                turn.Text = string.Join("\n", lines, open + 1, close - open - 1);

                line = close + 1;
                while (line < lines.Length && lines[line].Length == 0) line++;
                if (line < lines.Length && lines[line] == "Attached:")
                {
                    line++;
                    while (line < lines.Length && lines[line].Length == 0) line++;
                    for (; line < lines.Length && lines[line].StartsWith("- "); line++)
                    {
                        Match name = Regex.Match(lines[line], @"`([^`]*)`|\[\[([^\]]*)\]\]");
                        if (!name.Success) continue;
                        ChatAttachment attachment = new ChatAttachment();
                        attachment.Kind = AttachmentKind.NameOnly;
                        attachment.Name = name.Groups[1].Success ? name.Groups[1].Value : name.Groups[2].Value;
                        attachment.FullPath = attachment.Name;
                        attachment.WasFolder = lines[line].StartsWith("- Folder:");
                        turn.Attachments.Add(attachment);
                    }
                }
                while (line < lines.Length && lines[line].Length == 0) line++;
                if (line < lines.Length && lines[line] == "Created:")
                {
                    line++;
                    while (line < lines.Length && lines[line].Length == 0) line++;
                    for (; line < lines.Length && lines[line].StartsWith("- `") && lines[line].EndsWith("`"); line++)
                    {
                        turn.CreatedFiles.Add(lines[line].Substring(3, lines[line].Length - 4));
                    }
                }
                line--;
                turns.Add(turn);
            }
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
