using System;
using System.IO;
using System.Text;
using System.Threading;

// Stands in for Claude Code. Logs what it was sent, then plays the JSONL script named by FAKE_SCRIPT.
// Script lines starting with # are commands: #sleep ms, #wait (never finishes), #exit n, #stderr text.
static class Fake
{
    static int Main()
    {
        string log = Environment.GetEnvironmentVariable("FAKE_LOG");
        string script = Environment.GetEnvironmentVariable("FAKE_SCRIPT");
        StreamReader input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        string first = input.ReadLine();
        if (log != null) File.AppendAllText(log, "ARGS " + Environment.CommandLine + "\nSTDIN " + first + "\n", new UTF8Encoding(false));
        Stream output = Console.OpenStandardOutput();
        foreach (string line in File.ReadAllLines(script, new UTF8Encoding(false)))
        {
            if (line.StartsWith("#sleep ")) Thread.Sleep(int.Parse(line.Substring(7)));
            else if (line == "#wait") Thread.Sleep(600000);
            else if (line.StartsWith("#exit ")) return int.Parse(line.Substring(6));
            else if (line.StartsWith("#stderr ")) Console.Error.WriteLine(line.Substring(8));
            else
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(line + "\n");
                output.Write(bytes, 0, bytes.Length);
                output.Flush();
            }
        }
        return 0;
    }
}
