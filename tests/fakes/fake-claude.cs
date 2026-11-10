using System;
using System.IO;
using System.Text;
using System.Threading;

// Stands in for Claude Code. Logs what it was sent, then plays the JSONL script named by FAKE_SCRIPT.
// Script lines starting with # are commands: #sleep ms, #wait (never finishes), #exit n, #stderr text, #read (logs the
// next stdin line as REPLY, such as the window's answer to a can_use_tool request).
static class Fake
{
    static int Main()
    {
        string log = Environment.GetEnvironmentVariable("FAKE_LOG");
        string script = Environment.GetEnvironmentVariable("FAKE_SCRIPT");
        StreamReader input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        string first = input.ReadLine();
        if (log != null) File.AppendAllText(log, "ARGS " + Environment.CommandLine + "\nSTDIN " + first + "\n", new UTF8Encoding(false));
        // The system prompt file, as Claude Code would read it: its path, and its text on one line with \n escaped.
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 1; i + 1 < args.Length; i++)
        {
            if (args[i] != "--append-system-prompt-file") continue;
            string text = File.Exists(args[i + 1]) ? Shared(args[i + 1]) : "(missing)";
            if (log != null) File.AppendAllText(log, "SYSPROMPTFILE " + args[i + 1] + "\nSYSPROMPT " + text.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n") + "\n", new UTF8Encoding(false));
        }
        for (int i = 1; i + 1 < args.Length; i++)
        {
            if (args[i] != "--settings") continue;
            string text = File.Exists(args[i + 1]) ? Shared(args[i + 1]) : "(missing)";
            if (log != null) File.AppendAllText(log, "SETTINGSFILE " + args[i + 1] + "\nSETTINGS " + text + "\n", new UTF8Encoding(false));
        }
        Stream output = Console.OpenStandardOutput();
        foreach (string line in File.ReadAllLines(script, new UTF8Encoding(false)))
        {
            if (line.StartsWith("#sleep ")) Thread.Sleep(int.Parse(line.Substring(7)));
            else if (line == "#wait") Thread.Sleep(600000);
            else if (line.StartsWith("#exit ")) return int.Parse(line.Substring(6));
            else if (line.StartsWith("#stderr ")) Console.Error.WriteLine(line.Substring(8));
            else if (line == "#read")
            {
                string reply = input.ReadLine();
                if (log != null) File.AppendAllText(log, "REPLY " + reply + "\n", new UTF8Encoding(false));
            }
            else
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(line + "\n");
                output.Write(bytes, 0, bytes.Length);
                output.Flush();
            }
        }
        return 0;
    }

    // Claude Code (Node) opens files sharing read, write and delete, so it can read a file the widget holds open.
    static string Shared(string path)
    {
        using (FileStream f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (StreamReader r = new StreamReader(f, new UTF8Encoding(false))) return r.ReadToEnd();
    }
}
