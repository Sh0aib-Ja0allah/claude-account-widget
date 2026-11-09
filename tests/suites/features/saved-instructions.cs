using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
// Saved instructions for new chats (v0.3.0): reading, normalising, the 4,000-character limit and saving.
// In process, on fake data folders only. saved-instructions.exe <out folder>
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
        try { Store(); }
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
}
