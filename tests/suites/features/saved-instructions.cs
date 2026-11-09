using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
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

    static void Main(string[] args)
    {
        root = args[0];
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        try { Store(); Launch(); }
        catch (Exception e) { Log("HARNESS ERROR " + e); fails++; }
        Log("passes=" + passes + " fails=" + fails);
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
}
