using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
// Long chats and starting claude, the checks that need no claude (quick mode, so CI runs them). Windows stay off screen.
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

    [STAThread]
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
        System.Windows.Application app = new System.Windows.Application();
        app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        app.DispatcherUnhandledException += (sender, args2) => { Log("UNHANDLED " + args2.Exception); fails++; args2.Handled = true; };
        app.Startup += async delegate
        {
            try { await Window(); }
            catch (Exception e) { Log("HARNESS ERROR " + e); fails++; }
            Log("passes=" + passes + " fails=" + fails);
            Process.GetCurrentProcess().Kill();
        };
        app.Run();
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

    // ---------- H18-10 to 16: a reopened chat draws only its newest turns (L3) ----------
    static object Get(object o, string f) { return o.GetType().GetField(f, Any).GetValue(o); }
    static object Call(object o, string m, params object[] a)
    {
        MethodInfo info = o.GetType().GetMethods(Any).First(x => x.Name == m && x.GetParameters().Length == a.Length);
        return info.Invoke(o, a);
    }

    static string WriteChat(string folder, string name, int count, out List<ChatTurn> written)
    {
        written = new List<ChatTurn>();
        for (int i = 0; i < count; i++)
        {
            ChatTurn t = new ChatTurn();
            t.FromUser = i % 2 == 0;
            t.Text = (t.FromUser ? "Question " : "Answer with **bold** and a list:\n\n- one\n- two\n\nNumber ") + i;
            if (!t.FromUser && i % 50 == 1) t.CreatedFiles.Add("file-" + i + ".txt");
            written.Add(t);
        }
        string path = Path.Combine(folder, name + ".md");
        ChatNoteWriter writer = new ChatNoteWriter(folder, path, new DateTime(2026, 3, 4, 5, 6, 7));
        writer.Save(written);
        return path;
    }

    static string WithoutUpdated(string note)
    {
        return string.Join("\n", note.Replace("\r\n", "\n").Split('\n').Where(l => !l.StartsWith("updated: ")).ToArray());
    }

    static ChatWindow OpenIn(string data, string note)
    {
        ChatWindow w = new ChatWindow(data);
        w.ShowActivated = false;
        w.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
        w.Left = -6000; w.Top = 50; w.Width = 441; w.Height = 700;
        w.Show();
        Call(w, "OpenSavedChat", new ChatEntry { Path = note, Title = "t", Started = DateTime.Now, Text = "" });
        w.UpdateLayout();
        return w;
    }

    static async Task Window()
    {
        string data = Path.Combine(root, "window-data");
        string hist = Path.Combine(data, "hist");
        Directory.CreateDirectory(hist);
        File.WriteAllText(Path.Combine(data, "chat-history-folder.txt"), hist);
        File.WriteAllText(Path.Combine(data, "settings.txt"), "-6000,0,0,0,default,system");
        int batch = (int)typeof(ChatWindow).GetField("FirstBatch", Any).GetValue(null);

        // A 40-turn chat drawn in full, for the element bound
        List<ChatTurn> forty;
        string fortyNote = WriteChat(hist, "forty", 40, out forty);
        ChatWindow full = OpenIn(data, fortyNote);
        // (40 is over the first batch, so draw the rest by hand to count a full 40-turn chat)
        int fullFrom = (int)Get(full, "renderedFrom");
        System.Windows.Controls.StackPanel fullMessages = (System.Windows.Controls.StackPanel)Get(full, "messages");
        int drawnBatch = fullMessages.Children.Count;
        List<ChatTurn> fullTurns = (List<ChatTurn>)Get(full, "turns");
        int perTurn = (int)Math.Ceiling(drawnBatch / (double)(fullTurns.Count - fullFrom));
        full.Close();

        List<ChatTurn> six;
        string sixHundred = WriteChat(hist, "six-hundred", 600, out six);
        string before = File.ReadAllText(sixHundred);
        ChatWindow w = OpenIn(data, sixHundred);
        List<ChatTurn> turns = (List<ChatTurn>)Get(w, "turns");
        int from = (int)Get(w, "renderedFrom");
        System.Windows.Controls.StackPanel messages = (System.Windows.Controls.StackPanel)Get(w, "messages");
        Check("H18-10 opening 600 turns draws about one batch: at most what 40 turns draw", turns.Count == 600 && messages.Children.Count <= perTurn * 40 && from >= 600 - batch - 1,
            messages.Children.Count + " elements from turn " + from + ", " + perTurn + " per turn");

        // H18-11: the drawn turns are exactly from renderedFrom to the end, and start with a question
        bool suffix = turns.Take(from).All(t => t.First == null && t.Regenerate == null)
            && turns.Skip(from).Where(t => t.FromUser).All(t => t.First != null && messages.Children.Contains(t.First))
            && turns.Skip(from).Where(t => !t.FromUser).All(t => t.Regenerate != null) && turns[from].FromUser;
        Check("H18-11 the drawn turns are a suffix that starts with a question; earlier turns aren't drawn", suffix, "from " + from);
        Check("H18-11 only the newest reply can be regenerated", turns.Last().Regenerate.Visibility == System.Windows.Visibility.Visible
            && turns.Skip(from).Where(t => !t.FromUser).Take(turns.Skip(from).Count(t => !t.FromUser) - 1).All(t => t.Regenerate.Visibility != System.Windows.Visibility.Visible));

        // H18-12: the file list has every created file, drawn or not, in order
        List<string> chatFiles = (List<string>)Get(w, "chatFiles");
        List<string> made = six.SelectMany(t => t.CreatedFiles).ToList();
        Check("H18-12 chatFiles lists every created file in the chat, in order", chatFiles.SequenceEqual(made), chatFiles.Count + " of " + made.Count);

        // H18-13: the prompt comes from the data: the same as for the same turns with nothing drawn
        ChatWindow bare = new ChatWindow(data);
        ((List<ChatTurn>)Get(bare, "turns")).AddRange(six);
        // Each request has a fresh random turn marker, so it's normalised before comparing.
        string drawnPrompt = Regex.Replace((string)Call(w, "BuildPrompt", "Next question", new List<ChatAttachment>()), @"\[turn-[0-9a-f]{32} ", "[turn-X ");
        string dataPrompt = Regex.Replace((string)Call(bare, "BuildPrompt", "Next question", new List<ChatAttachment>()), @"\[turn-[0-9a-f]{32} ", "[turn-X ");
        Check("H18-13 the prompt is byte-identical whatever is drawn", drawnPrompt == dataPrompt && drawnPrompt.Contains("Question 0") && drawnPrompt.Contains("Number 599"), drawnPrompt.Length + " vs " + dataPrompt.Length);
        bare.Close();

        // H18-14: saving writes the same note
        ((ChatNoteWriter)Get(w, "noteWriter")).Save(turns);
        Check("H18-14 re-saving the windowed chat writes the same note, apart from updated:", WithoutUpdated(File.ReadAllText(sixHundred)) == WithoutUpdated(before));
        w.Close();

        // H18-15, 16: an empty chat, and one shorter than a batch
        List<ChatTurn> none;
        ChatWindow e = OpenIn(data, WriteChat(hist, "empty", 0, out none));
        Check("H18-15 an empty chat opens with nothing drawn", ((List<ChatTurn>)Get(e, "turns")).Count == 0 && (int)Get(e, "renderedFrom") == 0
            && ((System.Windows.Controls.StackPanel)Get(e, "messages")).Children.Count == 0);
        e.Close();
        List<ChatTurn> ten;
        ChatWindow s = OpenIn(data, WriteChat(hist, "ten", 10, out ten));
        List<ChatTurn> sTurns = (List<ChatTurn>)Get(s, "turns");
        Check("H18-16 a chat shorter than a batch is drawn in full", (int)Get(s, "renderedFrom") == 0 && sTurns.Where(t => t.FromUser).All(t => t.First != null));
        s.Close();
        await Task.Delay(10);
    }
}
