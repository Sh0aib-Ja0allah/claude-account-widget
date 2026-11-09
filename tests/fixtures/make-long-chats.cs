using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
// Writes three long saved chats for the long-chat tests: L100, L300 and L600 (turns). The same seed always gives the same
// bytes. Only invented words, the sample address you@example.com and bare file names: no real names, paths or addresses.
//   make-long-chats.exe <output folder>
static class LongChats
{
    static uint state;
    static int Next(int n) { state = state * 1664525u + 1013904223u; return (int)((state >> 8) % (uint)n); }

    static readonly string[] Syllables = { "ka", "lo", "mi", "ren", "to", "vu", "sel", "dar", "fen", "quo", "zi", "bra", "nim", "ost", "pel", "ul", "tav", "ori", "esk", "mun" };

    static string Word()
    {
        StringBuilder w = new StringBuilder();
        int parts = 1 + Next(3);
        for (int i = 0; i < parts; i++) w.Append(Syllables[Next(Syllables.Length)]);
        return w.ToString();
    }

    static string Sentence(int words)
    {
        StringBuilder s = new StringBuilder();
        for (int i = 0; i < words; i++)
        {
            string w = Word();
            if (i == 0) w = char.ToUpperInvariant(w[0]) + w.Substring(1);
            s.Append(i == 0 ? "" : " ").Append(w);
            if (i > 0 && i < words - 1 && Next(9) == 0) s.Append(',');
        }
        return s.Append('.').ToString();
    }

    static string Paragraph(int sentences)
    {
        StringBuilder p = new StringBuilder();
        for (int i = 0; i < sentences; i++) p.Append(i == 0 ? "" : " ").Append(Sentence(6 + Next(10)));
        return p.ToString();
    }

    static string Reply(int index, bool huge)
    {
        StringBuilder r = new StringBuilder();
        r.Append(Paragraph(2 + Next(3))).Append("\n\n");
        if (index % 3 == 0)
        {
            for (int i = 0; i < 3 + Next(3); i++) r.Append("- **").Append(Word()).Append("**: ").Append(Sentence(5 + Next(6))).Append('\n');
            r.Append('\n');
        }
        if (index % 10 == 0)
        {
            r.Append("| ").Append(Word()).Append(" | ").Append(Word()).Append(" | ").Append(Word()).Append(" |\n|---|---|---|\n");
            for (int i = 0; i < 4; i++) r.Append("| ").Append(Word()).Append(" | ").Append(Next(1000)).Append(" | ").Append(Sentence(3)).Append(" |\n");
            r.Append('\n');
        }
        if (index % 5 == 0)
        {
            r.Append("```csharp\n");
            for (int i = 0; i < 4 + Next(5); i++) r.Append("var ").Append(Word()).Append(" = ").Append(Word()).Append("(").Append(Next(100)).Append(");\n");
            r.Append("```\n\n");
        }
        if (index % 4 == 1) r.Append("## ").Append(Sentence(3).TrimEnd('.')).Append("\n\n");
        r.Append(Paragraph(2 + Next(3))).Append("\n\n").Append(Paragraph(3 + Next(2))).Append("\n\n").Append(Paragraph(3 + Next(2)));
        while (huge && r.Length < 8500) r.Append("\n\n").Append(Paragraph(6));
        return r.ToString();
    }

    static void Append(StringBuilder note, string speaker, string text, List<string> created, List<string> attached)
    {
        int longest = 3;
        foreach (Match run in Regex.Matches(text, "`+")) longest = Math.Max(longest, run.Length);
        string fence = new string('`', longest + 1);
        note.Append("### ").Append(speaker).Append("\n\n").Append(fence).Append("text\n").Append(text).Append('\n').Append(fence).Append("\n\n");
        if (created.Count > 0)
        {
            note.Append("Created:\n\n");
            foreach (string f in created) note.Append("- `").Append(f).Append("`\n");
            note.Append('\n');
        }
        if (attached.Count > 0)
        {
            note.Append("Attached:\n\n");
            foreach (string f in attached) note.Append("- `").Append(f).Append("`\n");
            note.Append('\n');
        }
    }

    static string Note(int turns)
    {
        state = (uint)(20261109 + turns);
        int replies = turns / 2;
        HashSet<int> huge = new HashSet<int> { replies / 4, replies / 2, 3 * replies / 4 };
        StringBuilder note = new StringBuilder();
        note.Append("---\ndate: 2026-01-15\ntype: claude-chat\ntags:\n  - claude-chat\nai-first: true\nstarted: 2026-01-15T09:30:00\nupdated: 2026-01-15T18:45:00\n");
        note.Append("source: claude-account-widget\ntitle: \"Long chat L").Append(turns).Append("\"\n---\n\n");
        note.Append("## For future Claude\n\nThis is a saved chat from the Claude Account Widget, started on 2026-01-15.\n\n## Conversation\n\n");
        for (int i = 0; i < replies; i++)
        {
            List<string> attached = new List<string>();
            if (i % 20 == 7) attached.Add(Word() + "-notes.txt");
            string question = Sentence(5 + Next(12)) + (Next(4) == 0 ? " " + Sentence(6) : "") + (i % 37 == 3 ? " Send it to you@example.com." : "");
            Append(note, "You", question, new List<string>(), attached);
            List<string> created = new List<string>();
            if (i % 25 == 12) created.Add(Word() + "-report.docx");
            Append(note, i % 2 == 0 ? "Claude (sonnet)" : "Claude", Reply(i, huge.Contains(i)), created, new List<string>());
        }
        return note.ToString();
    }

    static int Main(string[] args)
    {
        Directory.CreateDirectory(args[0]);
        foreach (int turns in new[] { 100, 300, 600 })
        {
            string text = Note(turns);
            File.WriteAllText(Path.Combine(args[0], "L" + turns + ".md"), text, new UTF8Encoding(false));
            Console.WriteLine("L" + turns + " " + text.Length + " characters");
        }
        return 0;
    }
}
