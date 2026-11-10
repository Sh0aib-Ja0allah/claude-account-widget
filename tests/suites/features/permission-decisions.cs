using System;
using System.Collections.Generic;
using System.IO;
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
        string[] fine = { "npm test", "dir", "git status", "git commit -m x", "grep -r foo .", "rg foo src", "python build.py", "ls -la notes", "Get-ChildItem -Recurse src" };
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
    }

    static object[] Concat(this object[] a, object[] b)
    {
        object[] all = new object[a.Length + b.Length];
        a.CopyTo(all, 0);
        b.CopyTo(all, a.Length);
        return all;
    }
}
