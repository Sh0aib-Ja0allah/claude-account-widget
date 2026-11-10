using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
// Permission decisions (quick mode, so CI runs it): PermissionDecision.Decide and ProtectedTargets, judged without
// claude. Everything happens in scratch folders, with a decoy home through SignInGuard.HomeOverride.
// Network paths use 203.0.113.1, an address reserved for documentation; the guard never opens them.
//   permission-decisions.exe <out folder>
static class H20
{
    static string root, home, proj;
    static DecisionContext context;
    static int fails, passes;
    static readonly List<string> Laptop = new List<string> { "Read", "Grep", "Glob", "Edit", "Write", "Bash", "WebSearch", "WebFetch" };
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
        try { Run(); }
        catch (Exception e) { Log("HARNESS ERROR " + e); fails++; }
        finally { SignInGuard.HomeOverride = null; }
        Log("passes=" + passes + " fails=" + fails);
    }

    static Dictionary<string, object> In(params string[] pairs)
    {
        Dictionary<string, object> d = new Dictionary<string, object>();
        for (int i = 0; i + 1 < pairs.Length; i += 2) d[pairs[i]] = pairs[i + 1];
        return d;
    }

    static PermissionOutcome Decide(string tool, Dictionary<string, object> input, bool bypass, List<string> tools)
    {
        string message;
        return PermissionDecision.Decide(tool, input, bypass, tools, context, out message);
    }

    static string Message(string tool, Dictionary<string, object> input, bool bypass, List<string> tools)
    {
        string message;
        PermissionDecision.Decide(tool, input, bypass, tools, context, out message);
        return message;
    }

    // v0.3.0's Answer, without the output folder (the caller still handles that).
    static PermissionOutcome Old(string tool, Dictionary<string, object> input)
    {
        if (Array.IndexOf(new[] { "Read", "Grep", "Glob", "WebSearch" }, tool) >= 0) return PermissionOutcome.Allow;
        if (tool == "WebFetch" && PermissionDecision.IsGivenSite(input, context)) return PermissionOutcome.Allow;
        return Array.IndexOf(new[] { "Bash", "Edit", "Write", "WebFetch" }, tool) >= 0 ? PermissionOutcome.Card : PermissionOutcome.Refuse;
    }

    static string Show(string s)
    {
        s = s.Replace(home, "<home>").Replace(root, "<root>");
        return s.Length > 100 ? s.Substring(0, 60) + "..." + s.Substring(s.Length - 30) : s;
    }

    // Each call gets the expected outcome; the info lists any that don't.
    static void Rows(string id, PermissionOutcome expected, bool bypass, List<string> tools, params object[] calls)
    {
        List<string> wrong = new List<string>();
        for (int i = 0; i + 1 < calls.Length; i += 2)
        {
            string tool = (string)calls[i];
            Dictionary<string, object> input = (Dictionary<string, object>)calls[i + 1];
            PermissionOutcome r = Decide(tool, input, bypass, tools);
            if (r != expected)
            {
                string shown = "";
                foreach (KeyValuePair<string, object> p in input) shown += p.Value as string;
                wrong.Add(tool + " " + Show(shown) + " -> " + r);
            }
        }
        Check(id + " " + (bypass ? "bypass" : "normal") + ": " + expected + " for " + calls.Length / 2 + " call(s)", wrong.Count == 0, string.Join("; ", wrong.ToArray()));
    }

    static object[] Writes(params string[] paths)
    {
        List<object> calls = new List<object>();
        foreach (string p in paths) { calls.Add("Write"); calls.Add(In("file_path", p, "content", "x")); }
        return calls.ToArray();
    }

    static object[] Commands(string tool, params string[] commands)
    {
        List<object> calls = new List<object>();
        foreach (string c in commands) { calls.Add(tool); calls.Add(In("command", c, "description", "a step")); }
        return calls.ToArray();
    }

    static void Run()
    {
        home = Path.Combine(root, "home");
        proj = Path.Combine(root, "proj");
        string widget = Path.Combine(root, "widget"), prompts = Path.Combine(root, "temp", "ClaudeAccountWidget-chat"), startup = Path.Combine(root, "startup");
        foreach (string d in new[] { Path.Combine(home, ".claude"), Path.Combine(proj, ".git", "hooks"), Path.Combine(proj, "notes"), widget, prompts, startup }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(home, ".claude", ".credentials.json"), "{\"decoy\":\"canary-7f3a\"}");
        File.WriteAllText(Path.Combine(home, ".claude.json"), "{\"decoy\":\"canary-7f3a\"}");
        File.WriteAllText(Path.Combine(widget, "settings.txt"), "x");
        File.WriteAllText(Path.Combine(proj, "notes", "a.txt"), "ordinary");
        SignInGuard.HomeOverride = home;
        context = new DecisionContext();
        context.WorkingFolder = proj;
        context.WidgetFolder = widget;
        context.PromptFolder = prompts;
        context.Links.Add("https://docs.example.com/start");
        context.StartupFolders = new List<string> { startup };
        string plain = Path.Combine(proj, "notes", "a.txt");

        // H20-01 to 06: a normal chat decides as v0.3.0 did.
        object[][] classes =
        {
            new object[] { "Read", In("file_path", plain), "Grep", In("pattern", "x", "path", proj), "Glob", In("pattern", "**/*.txt", "path", proj), "Glob", In("pattern", "*.md") },
            new object[] { "WebSearch", In("query", "weather") },
            new object[] { "WebFetch", In("url", "https://docs.example.com/other", "prompt", "x"), "WebFetch", In("url", "https://example.org/", "prompt", "x") },
            new object[] { "Edit", In("file_path", plain, "old_string", "a", "new_string", "b"), "Write", In("file_path", Path.Combine(proj, "new.txt"), "content", "x") },
            new object[] { "Bash", In("command", "npm test"), "Bash", In("command", "git status") },
            new object[] { "NotebookEdit", In("notebook_path", plain), "Task", In("prompt", "x"), "PowerShell", In("command", "dir"), "Unknown", In() },
        };
        string[] names = { "reads and searches", "web search", "the user's sites and other sites", "edits and writes", "commands", "tools it wasn't given" };
        for (int k = 0; k < classes.Length; k++)
        {
            List<string> wrong = new List<string>();
            for (int i = 0; i < classes[k].Length; i += 2)
            {
                string tool = (string)classes[k][i];
                Dictionary<string, object> input = (Dictionary<string, object>)classes[k][i + 1];
                PermissionOutcome now = Decide(tool, input, false, Laptop), then = k == 5 ? PermissionOutcome.Refuse : Old(tool, input);
                if (now != then) wrong.Add(tool + " " + now + " vs " + then);
            }
            Check("H20-0" + (k + 1) + " normal: " + names[k] + " decide as in v0.3.0", wrong.Count == 0, string.Join("; ", wrong.ToArray()));
        }

        // H20-07 to 18: protected targets in bypass, and the same names under ordinary folders.
        Rows("H20-07 a .git folder", PermissionOutcome.Card, true, Laptop, Writes(Path.Combine(proj, ".git", "hooks", "pre-commit"), Path.Combine(proj, ".git", "config")));
        Rows("H20-08 other tool folders", PermissionOutcome.Card, true, Laptop, Writes(Path.Combine(proj, ".vscode", "settings.json"), Path.Combine(proj, ".idea", "x.xml"),
            Path.Combine(proj, ".husky", "pre-push"), Path.Combine(proj, ".cargo", "config.toml"), Path.Combine(proj, ".devcontainer", "x.json"), Path.Combine(proj, ".yarn", "x"), Path.Combine(proj, ".mvn", "x")));
        Rows("H20-09 .claude folders, in a project and the home folder", PermissionOutcome.Card, true, Laptop, Writes(Path.Combine(proj, ".claude", "settings.json"), "~/.claude/settings.json", "~/.claude/dev-mods/x.js", "~/.claude/hooks/x.ps1"));
        Rows("H20-10 ~/.config/git", PermissionOutcome.Card, true, Laptop, Writes("~/.config/git/config", "~/.config/git/hooks/x"));
        Rows("H20-11 protected file names", PermissionOutcome.Card, true, Laptop, Writes("~/.gitconfig", Path.Combine(proj, ".gitmodules"), "~/.bashrc", "~/.zshrc", Path.Combine(proj, ".npmrc"),
            Path.Combine(proj, ".mcp.json"), Path.Combine(proj, "pyrightconfig.json"), Path.Combine(proj, "lefthook.yml"), Path.Combine(proj, "gradle", "wrapper", "gradle-wrapper.properties"), Path.Combine(proj, ".pre-commit-config.yaml")));
        Rows("H20-12 the widget's own folder", PermissionOutcome.Card, true, Laptop, Writes(Path.Combine(widget, "settings.txt"), Path.Combine(widget, "chat-access.txt"), Path.Combine(widget, "chat-bypass.txt"), Path.Combine(widget, "anything.json")));
        Rows("H20-13 the prompt folder", PermissionOutcome.Card, true, Laptop, Writes(Path.Combine(prompts, "x.settings.json"), Path.Combine(prompts, "y.txt")));
        Rows("H20-14 the Startup folder", PermissionOutcome.Card, true, Laptop, Writes(Path.Combine(startup, "run.cmd")));
        Rows("H20-15 PowerShell profiles", PermissionOutcome.Card, true, Laptop, Writes(Path.Combine(home, "Documents", "WindowsPowerShell", "Microsoft.PowerShell_profile.ps1"), Path.Combine(home, "Documents", "PowerShell", "profile.ps1")));
        Rows("H20-16 network paths", PermissionOutcome.Card, true, Laptop, Writes(@"\\203.0.113.1\s\x.txt", "//203.0.113.1/s/x.txt", @"\\?\UNC\203.0.113.1\s\x.txt"));
        Rows("H20-17 Edit on a protected target", PermissionOutcome.Card, true, Laptop, "Edit", In("file_path", Path.Combine(proj, ".git", "config"), "old_string", "a", "new_string", "b"), "Edit", In("file_path", Path.Combine(widget, "settings.txt"), "old_string", "a", "new_string", "b"));
        Rows("H20-18 the same names under ordinary folders, and reads of protected files", PermissionOutcome.Allow, true, Laptop, Writes(Path.Combine(proj, "notes", "settings.json"), Path.Combine(proj, "notes", "config"),
            Path.Combine(proj, "gitconfig.txt"), Path.Combine(proj, "notes", "pre-commit"), Path.Combine(proj, "my.git", "x"), Path.Combine(root, "widget-notes", "settings.txt"), plain)
            .Concat(new object[] { "Read", In("file_path", Path.Combine(proj, ".git", "config")), "Read", In("file_path", Path.Combine(widget, "settings.txt")) }));

        // H20-19 to 24: commands in bypass (best effort).
        string[] git = { "git config user.email x@example.com", "git -c core.hooksPath=x commit -m y", "git config --global alias.x y" };
        string[] startupCommands = { @"reg add HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v x /d y", @"Set-ItemProperty -Path HKCU:\Software\Microsoft\Windows\CurrentVersion\Run -Name x -Value y",
            @"copy x.cmd ""%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup""", "notepad $PROFILE", "schtasks /create /tn x /tr y /sc onlogon", "echo x >> ~/.bashrc", "copy x " + startup };
        string[] other = { "git commit --no-verify -m x", "notepad " + Path.Combine(widget, "settings.txt"), "type " + prompts + "\\x.txt", "copy x \\\\203.0.113.1\\s\\", "echo x > .git/hooks/pre-commit" };
        string[] grepHome = { "grep -r token ~", "grep -R token ~/", "rg secret ~", "findstr /s /i token C:\\", "grep -r token /c/", "grep -rn token " + home.Replace('\\', '/') };
        string[] psHome = { "Get-ChildItem -Recurse $HOME | Select-String x", "gci -r $env:USERPROFILE", "dir /s %USERPROFILE%", "Get-ChildItem C:\\ -Recurse -Filter *.json" };
        string[] fine = { "npm test", "dir", "git status", "git commit -m x", "grep -r foo .", "rg foo src", "python build.py", "ls -la notes", "Get-ChildItem -Recurse src",
            "rg foo " + Path.Combine(home, "proj"), "grep -r foo ~/proj/src", "rg TODO $HOME/proj", "find ~/proj", @"Get-ChildItem -Recurse $env:USERPROFILE\proj", @"grep -r x C:\work" };
        Rows("H20-19 git settings", PermissionOutcome.Card, true, Laptop, Commands("Bash", git));
        Rows("H20-20 start-up items, profiles and rc files", PermissionOutcome.Card, true, Laptop, Commands("Bash", startupCommands));
        Rows("H20-21 --no-verify, the widget's folders, network paths and hooks", PermissionOutcome.Card, true, Laptop, Commands("Bash", other));
        Rows("H20-22 recursive searches from the home folder or a drive root", PermissionOutcome.Card, true, Laptop, Commands("Bash", grepHome));
        Rows("H20-23 PowerShell-style recursive listings of the home folder or a drive root", PermissionOutcome.Card, true, Laptop, Commands("Bash", psHome));
        Rows("H20-24 ordinary commands", PermissionOutcome.Allow, true, Laptop, Commands("Bash", fine));

        // H20-25 to 27: the order.
        Dictionary<string, object> both = In("file_path", "~/.claude/.credentials.json", "content", "x");
        Check("H20-25 a sign-in file beats a protected folder: refused with the sign-in message, in bypass and normal",
            Decide("Write", both, true, Laptop) == PermissionOutcome.Refuse && Message("Write", both, true, Laptop) == "Claude Code's sign-in files are always blocked."
            && Decide("Bash", In("command", "git config x && cat ~/.claude.json"), true, Laptop) == PermissionOutcome.Refuse
            && Decide("Read", In("file_path", "~/.claude.json"), false, Laptop) == PermissionOutcome.Refuse && Message("Read", In("file_path", "~/.claude.json"), false, Laptop) == PermissionDecision.SignInMessage);
        Rows("H20-26 a path the guard can't resolve", PermissionOutcome.Card, true, Laptop, Writes(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\x.txt", @"\\.\pipe\x"));
        List<string> memoryOnly = new List<string> { "Read", "Grep", "Glob" };
        Check("H20-27 a tool the chat wasn't given is refused in both modes, with the usual message",
            Decide("Write", In("file_path", plain), true, memoryOnly) == PermissionOutcome.Refuse && Decide("Bash", In("command", "dir"), false, memoryOnly) == PermissionOutcome.Refuse
            && Message("Write", In("file_path", "~/.claude.json"), true, memoryOnly) == PermissionDecision.DeclinedMessage && Decide(null, In(), true, Laptop) == PermissionOutcome.Refuse);

        // H20-28 to 30: PowerShell (S5).
        List<string> withPs = new List<string>(Laptop) { "PowerShell" };
        Check("H20-28 PowerShell in the tools: a card in a normal chat, allowed in bypass, sign-in names refused",
            Decide("PowerShell", In("command", "Get-ChildItem"), false, withPs) == PermissionOutcome.Card && Decide("PowerShell", In("command", "Get-ChildItem"), true, withPs) == PermissionOutcome.Allow
            && Decide("PowerShell", In("command", "Get-Content ~/.claude.json"), true, withPs) == PermissionOutcome.Refuse);
        Rows("H20-29 PowerShell not in the tools", PermissionOutcome.Refuse, true, Laptop, Commands("PowerShell", "dir", "Get-ChildItem"));
        Rows("H20-29 PowerShell not in the tools", PermissionOutcome.Refuse, false, Laptop, Commands("PowerShell", "dir"));
        List<string> cardRows = new List<string>();
        foreach (string[] set in new[] { git, startupCommands, other, grepHome, psHome }) cardRows.AddRange(set);
        Rows("H20-30 PowerShell commands get the Bash checks", PermissionOutcome.Card, true, withPs, Commands("PowerShell", cardRows.ToArray()));
        Rows("H20-30 PowerShell commands get the Bash checks", PermissionOutcome.Allow, true, withPs, Commands("PowerShell", fine));

        // H20-31 (DEC-18): a path the guard can't resolve is a card in every mode, reads included.
        object[] unresolved = { "Read", In("file_path", @"\\?\UNC\203.0.113.1\s\x.txt"), "Read", In("file_path", @"\\.\pipe\x"), "Read", In("file_path", @"\\?\Volume{00000000-0000-0000-0000-000000000000}\x.txt"),
            "Grep", In("pattern", "x", "path", @"\\203.0.113.1\s") };
        Rows("H20-31 unresolved reads and searches", PermissionOutcome.Card, false, Laptop, unresolved);
        Rows("H20-31 unresolved reads and searches", PermissionOutcome.Card, true, Laptop, unresolved);

        // H20-32 (Worker A's g2 review): a write to an ordinary-looking file that is another name for a protected file.
        string gitConfig = Path.Combine(proj, ".git", "config");
        File.WriteAllText(gitConfig, "[core]");
        string alias = Path.Combine(proj, "notes", "innocent.txt");
        bool linked = CreateHardLinkW(alias, gitConfig, IntPtr.Zero);
        Check("H20-32 bypass: a write or edit to a file with another name (a hard link) gets a card; a single-name file doesn't", linked
            && Decide("Write", In("file_path", alias, "content", "x"), true, Laptop) == PermissionOutcome.Card
            && Decide("Edit", In("file_path", alias, "old_string", "a", "new_string", "b"), true, Laptop) == PermissionOutcome.Card
            && Decide("Write", In("file_path", plain, "content", "x"), true, Laptop) == PermissionOutcome.Allow);
        Rows("H20-22 recursive searches from the home folder itself, with a trailing slash or quotes", PermissionOutcome.Card, true, Laptop,
            Commands("Bash", "rg foo \"" + home + "\"", "grep -r foo " + home + "\\", "rg x $HOME/", "find ~ -name x", "find / -name x"));

        G3();
    }

    static object[] Concat(this object[] a, object[] b)
    {
        object[] all = new object[a.Length + b.Length];
        a.CopyTo(all, 0);
        b.CopyTo(all, a.Length);
        return all;
    }

    // ---------- g3: the ask list, the per-request settings file, the deny rules and held files ----------
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern bool CreateHardLinkW(string link, string existing, IntPtr security);
    const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
    static object Static(string name, params object[] args) { return typeof(ChatClient).GetMethod(name, Any).Invoke(null, args); }

    static ChatRequest Plain()
    {
        ChatRequest r = new ChatRequest();
        r.Prompt = "Hello";
        r.Model = "";
        r.Effort = "";
        return r;
    }

    static string Arg(string arguments, string flag)
    {
        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(arguments, flag + " \"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    static List<string> AskList(string json)
    {
        Dictionary<string, object> settings = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
        Dictionary<string, object> permissions = (Dictionary<string, object>)settings["permissions"];
        List<string> ask = new List<string>();
        foreach (object o in (System.Collections.IEnumerable)permissions["ask"]) ask.Add((string)o);
        if (settings.Count != 1 || permissions.Count != 1) ask.Add("(other keys)");
        return ask;
    }

    static void G3()
    {
        string savedTemp = Environment.GetEnvironmentVariable("TEMP"), savedTmp = Environment.GetEnvironmentVariable("TMP"), savedPath = Environment.GetEnvironmentVariable("PATH");
        string temp = Path.Combine(root, "temp");
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("TEMP", temp);
        Environment.SetEnvironmentVariable("TMP", temp);
        try { G3Checks(temp); }
        finally
        {
            ChatClient.StartFaultForTests = null;
            Environment.SetEnvironmentVariable("TEMP", savedTemp);
            Environment.SetEnvironmentVariable("TMP", savedTmp);
            Environment.SetEnvironmentVariable("PATH", savedPath);
        }
    }

    static void G3Checks(string temp)
    {
        string claude = Path.Combine(root, "bin dir", "claude.exe");
        string prompt = Path.Combine(temp, "ClaudeAccountWidget-chat", "0123456789abcdef0123456789abcdef.txt");

        // H20-40 to 44: the ask list is the --tools list, for every kind of chat, and nothing else is in the file.
        ChatRequest memory = Plain(); memory.MemoryFolder = Path.Combine(root, "memory");
        ChatRequest links = Plain(); links.Links.Add("https://example.com/a");
        ChatRequest folders = Plain(); folders.Folders.Add(Path.Combine(root, "attached"));
        ChatRequest laptop = Plain(); laptop.Laptop = true; laptop.DefaultOutputFolder = Path.Combine(root, "out"); laptop.CurrentOutputFolder = delegate { return null; };
        ChatRequest none = Plain();
        var kinds = new[] { new { Id = "H20-40", Name = "memory only", R = memory }, new { Id = "H20-41", Name = "links", R = links }, new { Id = "H20-42", Name = "an attached folder", R = folders },
            new { Id = "H20-43", Name = "laptop", R = laptop }, new { Id = "H20-43", Name = "no tools at all", R = none } };
        foreach (var k in kinds)
        {
            ProcessStartInfo start = (ProcessStartInfo)Static("BuildStart", claude, k.R, prompt);
            string tools = Arg(start.Arguments, "--tools");
            List<string> want = tools.Length == 0 ? new List<string>() : new List<string>(tools.Split(','));
            List<string> ask = AskList((string)Static("SettingsJson", (List<string>)Static("ToolsFor", k.R)));
            Check(k.Id + " " + k.Name + ": the settings file's ask list is exactly the --tools list, with no allow rules", ask.SequenceEqual(want), string.Join(",", ask.ToArray()) + " vs " + tools);
        }
        List<string> withPs = new List<string> { "Read", "Grep", "Glob", "Edit", "Write", "Bash", "PowerShell", "WebSearch", "WebFetch" };
        Check("H20-44 with PowerShell added, it's asked for too", AskList((string)Static("SettingsJson", withPs)).SequenceEqual(withPs));

        // H20-45 to 47: the settings file is the request's own, beside its prompt file; no fixed-name file is left.
        ProcessStartInfo laptopStart = (ProcessStartInfo)Static("BuildStart", claude, laptop, prompt);
        string settingsArg = Arg(laptopStart.Arguments, "--settings");
        Check("H20-45 --settings names <prompt name>.settings.json in the prompt folder", settingsArg == Path.Combine(temp, "ClaudeAccountWidget-chat", "0123456789abcdef0123456789abcdef.settings.json").Replace('\\', '/'), settingsArg);
        string source = File.ReadAllText(Path.Combine(root, "..", "..", "..", "snapshot", "widget.cs"));
        Check("H20-46 no fixed-name ask-first.json anywhere", !source.Contains("ask-first") && !laptopStart.Arguments.Contains("ask-first"));

        Directory.CreateDirectory(Path.GetDirectoryName(claude));
        File.WriteAllText(claude, "not a program");
        Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(claude));
        string folder = Path.Combine(temp, "ClaudeAccountWidget-chat");
        bool writeBlockedSettings = false, writeBlockedPrompt = false, readOk = false, bothThere = false, deleteBlocked = false;
        string seenSettings = null;
        ChatClient.StartFaultForTests = delegate(ProcessStartInfo info)
        {
            string p = Arg(info.Arguments, "--append-system-prompt-file").Replace('/', '\\');
            string s = Arg(info.Arguments, "--settings").Replace('/', '\\');
            bothThere = File.Exists(p) && File.Exists(s);
            try { using (new FileStream(s, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { } } catch (IOException) { writeBlockedSettings = true; }
            try { using (new FileStream(p, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { } } catch (IOException) { writeBlockedPrompt = true; }
            try { File.Delete(s); } catch (IOException) { deleteBlocked = true; } catch (UnauthorizedAccessException) { deleteBlocked = true; }
            try
            {
                using (FileStream f = new FileStream(s, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader r = new StreamReader(f)) seenSettings = r.ReadToEnd();
                using (new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { }
                readOk = true;
            }
            catch (IOException) { }
            return new IOException("stop here");
        };
        ChatResult failed = new ChatClient().Ask(laptop, delegate(string a, string b) { return Task.FromResult(false); }).Result;
        ChatClient.StartFaultForTests = null;
        System.Threading.Thread.Sleep(300);
        Check("H20-47 a failed start: Failed, and the request's prompt and settings files are both gone", failed.Outcome == ChatOutcome.Failed && bothThere
            && Directory.GetFiles(folder, "*.txt").Length == 0 && Directory.GetFiles(folder, "*.settings.json").Length == 0, failed.Outcome + " " + bothThere);
        Check("H20-57 while the request is live, the settings file can't be opened for writing or deleted", writeBlockedSettings && deleteBlocked);
        Check("H20-58 while the request is live, the prompt file can't be opened for writing", writeBlockedPrompt);
        Check("H20-59 both can be read, sharing write as Claude Code does, and the settings file holds the ask list", readOk && seenSettings != null && AskList(seenSettings).SequenceEqual((List<string>)Static("ToolsFor", laptop)));
        System.Collections.IDictionary held = (System.Collections.IDictionary)typeof(ChatClient).GetField("heldFiles", Any).GetValue(null);
        Check("H20-60 after the request, no file is held open", held.Count == 0, held.Count.ToString());

        // H20-48, 49: the deny rules.
        List<string> rules = (List<string>)Static("DenyRules", (List<string>)Static("ToolsFor", laptop));
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Replace('\\', '/');
        List<string> missing = new List<string>();
        foreach (string b in new[] { "~", "//" + home.Substring(0, 1).ToUpperInvariant() + home.Substring(2), "//" + home.Substring(0, 1).ToLowerInvariant() + home.Substring(2) })
        {
            foreach (string target in new[] { "/.claude/.credentials.json", "/.claude.json", "/.claude.json*", "/.claude/backups/**" })
            {
                foreach (string tool in new[] { "Read", "Edit", "Write" }) if (!rules.Contains(tool + "(" + b + target + ")")) missing.Add(tool + "(<b>" + target + ")");
            }
        }
        foreach (string pattern in new[] { "claude auth *", "claude login*", "claude logout*", "*claude setup-token*", "*.credentials.json*", "*.claude.json*", "*.claude/.cred*", "*.claude\\.cred*" })
        {
            if (!rules.Contains("Bash(" + pattern + ")")) missing.Add("Bash(" + pattern + ")");
        }
        Check("H20-48 --disallowedTools has every DEC-3 rule: both drive cases and ~, the copies, the backups and the command patterns", missing.Count == 0 && laptopStart.Arguments.Contains("\"Read(~/.claude.json*)\""), string.Join(" ", missing.ToArray()));
        List<string> psRules = (List<string>)Static("DenyRules", withPs);
        Check("H20-49 PowerShell rules appear only when PowerShell is a tool", !rules.Any(r => r.StartsWith("PowerShell(")) && psRules.Count(r => r.StartsWith("PowerShell(")) == 8);
    }
}
