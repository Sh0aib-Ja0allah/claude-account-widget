using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
// Long chats and starting claude, the checks that need no window and no claude (quick mode, so CI runs them).
// Nothing is ever started: a dummy claude.exe is only found on PATH, and StartFaultForTests stops each start.
//   long-chats.exe <out folder>
static class H18
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static string root;
    static int fails, passes;
    static void Log(string s) { Console.WriteLine(s); Console.Out.Flush(); }
    static void Check(string what, bool ok, string info = "")
    {
        Log((ok ? "PASS " : "FAIL ") + what + (info.Length > 0 ? " [" + info.Replace("\r", "\\r").Replace("\n", "\\n") + "]" : ""));
        if (ok) passes++; else fails++;
    }

    static void Main(string[] args)
    {
        root = args[0];
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        string savedPath = Environment.GetEnvironmentVariable("PATH"), savedTemp = Environment.GetEnvironmentVariable("TEMP"), savedTmp = Environment.GetEnvironmentVariable("TMP");
        try { Start(); }
        catch (Exception e) { Log("HARNESS ERROR " + e); fails++; }
        finally
        {
            ChatClient.StartFaultForTests = null;
            ChatClient.StartGateForTests = null;
            Environment.SetEnvironmentVariable("PATH", savedPath);
            Environment.SetEnvironmentVariable("TEMP", savedTemp);
            Environment.SetEnvironmentVariable("TMP", savedTmp);
        }
        Log("passes=" + passes + " fails=" + fails);
    }

    static ChatRequest Request(string instructions)
    {
        ChatRequest r = new ChatRequest();
        r.Prompt = "Hello";
        r.Model = "";
        r.Effort = "";
        r.Instructions = instructions;
        return r;
    }

    static ChatResult Ask(ChatRequest r)
    {
        return new ChatClient().Ask(r, delegate(string a, string b) { return Task.FromResult(false); }).Result;
    }

    static int PromptFiles(string temp)
    {
        string folder = Path.Combine(temp, "ClaudeAccountWidget-chat");
        return Directory.Exists(folder) ? Directory.GetFiles(folder, "*.txt").Length : 0;
    }

    // H18-34, 35, 37 in quick mode
    static void Start()
    {
        string temp = Path.Combine(root, "temp");
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("TEMP", temp);
        Environment.SetEnvironmentVariable("TMP", temp);

        // H18-34: no claude on PATH
        string empty = Path.Combine(root, "empty path");
        Directory.CreateDirectory(empty);
        Environment.SetEnvironmentVariable("PATH", empty);
        bool faultCalled = false;
        ChatClient.StartFaultForTests = delegate(ProcessStartInfo info) { faultCalled = true; return new IOException("never"); };
        ChatResult result = Ask(Request("x"));
        Check("H18-34 no claude on PATH: NotFound, nothing prepared to start, no prompt file", result.Outcome == ChatOutcome.NotFound && !faultCalled && PromptFiles(temp) == 0);

        // A dummy claude.exe that is only ever found: every start below is stopped by the fault seam first.
        string bin = Path.Combine(root, "bin dir");
        Directory.CreateDirectory(bin);
        string claude = Path.Combine(bin, "claude.exe");
        File.WriteAllText(claude, "not a program");
        Environment.SetEnvironmentVariable("PATH", bin);

        // H18-35: each failed start maps as at 1768b9e, and the prompt file written before it is deleted
        foreach (Exception fault in new Exception[] { new System.ComponentModel.Win32Exception(2), new IOException("in use"), new UnauthorizedAccessException("denied"), new FileNotFoundException("gone") })
        {
            Exception thisFault = fault;
            int filesWhenStarting = -1;
            ChatClient.StartFaultForTests = delegate(ProcessStartInfo info) { filesWhenStarting = PromptFiles(temp); return thisFault; };
            result = Ask(Request("Some instructions"));
            ChatOutcome expected = fault is System.ComponentModel.Win32Exception ? ChatOutcome.NotFound : ChatOutcome.Failed;
            Check("H18-35 " + fault.GetType().Name + " at start: " + expected + ", and the prompt file that was written is deleted", result.Outcome == expected && filesWhenStarting == 1 && PromptFiles(temp) == 0,
                result.Outcome + ", files at start " + filesWhenStarting + ", after " + PromptFiles(temp));
        }

        // H18-37: Ask starts exactly what BuildStart gives, for each kind of request
        ProcessStartInfo captured = null;
        string fileOnDisk = null;
        ChatClient.StartFaultForTests = delegate(ProcessStartInfo info)
        {
            captured = info;
            Match m = Regex.Match(info.Arguments, "--append-system-prompt-file \"([^\"]+)\"");
            fileOnDisk = m.Success && File.Exists(m.Groups[1].Value) ? File.ReadAllText(m.Groups[1].Value) : null;
            return new IOException("stop before starting");
        };
        foreach (string kind in new[] { "plain", "instructions", "folder", "laptop", "output", "recall" })
        {
            ChatRequest r = Request(kind == "instructions" ? "Answer in French" : null);
            if (kind == "folder") r.Folders.Add(Path.Combine(root, "attached folder"));
            if (kind == "laptop" || kind == "output")
            {
                r.Laptop = true;
                r.DefaultOutputFolder = Path.Combine(root, "Claude");
                string chosen = Path.Combine(root, "chosen output");
                r.CurrentOutputFolder = delegate { return kind == "output" ? chosen : null; };
            }
            if (kind == "recall") r.MemoryFolder = Path.Combine(root, "history");
            captured = null;
            fileOnDisk = null;
            Ask(r);
            string promptFile = captured == null ? "" : Regex.Match(captured.Arguments, "--append-system-prompt-file \"([^\"]+)\"").Groups[1].Value;
            ProcessStartInfo direct = (ProcessStartInfo)typeof(ChatClient).GetMethod("BuildStart", Any).Invoke(null, new object[] { claude, r, promptFile.Replace('/', '\\') });
            string expectedText = (string)typeof(ChatClient).GetMethod("SystemPromptText", Any).Invoke(null, new object[] { r });
            Check("H18-37 " + kind + ": the program, arguments and folder are BuildStart's, unchanged", captured != null && captured.FileName == direct.FileName
                && captured.Arguments == direct.Arguments && captured.WorkingDirectory == direct.WorkingDirectory, captured == null ? "no start" : captured.Arguments.Length.ToString());
            Check("H18-37 " + kind + ": the prompt file holds the system prompt text when claude starts", fileOnDisk == expectedText);
        }
        Check("H18-37 every prompt file is gone afterwards", PromptFiles(temp) == 0);
    }
}
