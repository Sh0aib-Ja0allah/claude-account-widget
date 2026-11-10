using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
// The sign-in guard (quick mode, so CI runs it). Everything happens in a decoy home in the scratch folder through
// SignInGuard.HomeOverride; the real profile is never named or touched. The decoy files hold a fixed canary.
// Network paths use 203.0.113.1, an address reserved for documentation, and the guard is checked never to open them.
//   sign-in-guard.exe <out folder>
static class H19
{
    static string root, home, acct, creds, proj;
    static List<string> opened;
    static int fails, passes;
    static void Log(string s) { Console.WriteLine(s); Console.Out.Flush(); }
    static void Check(string what, bool ok, string info = "")
    {
        Log((ok ? "PASS " : "FAIL ") + what + (info.Length > 0 ? " [" + info.Replace("\r", "\\r").Replace("\n", "\\n") + "]" : ""));
        if (ok) passes++; else fails++;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateHardLinkW(string link, string existing, IntPtr security);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.I1)]
    static extern bool CreateSymbolicLinkW(string link, string target, int flags);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern uint GetShortPathNameW(string path, StringBuilder shortPath, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder name, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern uint QueryDosDeviceW(string device, StringBuilder target, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetVolumeInformationW(string root, StringBuilder name, int nameSize, out uint serial, out uint maxComponent, out uint flags, StringBuilder fs, int fsSize);

    static void Main(string[] args)
    {
        root = args[0];
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        string locked = null;
        try { Run(out locked); }
        catch (Exception e) { Log("HARNESS ERROR " + e); fails++; }
        finally
        {
            if (locked != null) Unlock(locked);
            SignInGuard.HomeOverride = null;
            SignInGuard.OpenLogForTests = null;
            SignInGuard.DosDeviceForTests = null;
        }
        Log("passes=" + passes + " fails=" + fails);
    }

    static Dictionary<string, object> Input(string key, object value)
    {
        Dictionary<string, object> d = new Dictionary<string, object>();
        if (key != null) d[key] = value;
        return d;
    }

    static GuardResult Read(string path) { return SignInGuard.Check("Read", Input("file_path", path), proj); }
    static GuardResult Write(string path) { return SignInGuard.Check("Write", Input("file_path", path), proj); }
    static GuardResult Search(string tool, string path, string working) { return SignInGuard.Check(tool, Input(path == null ? null : "path", path), working); }
    static GuardResult Command(string tool, string command) { return SignInGuard.Check(tool, Input("command", command), proj); }

    static string Show(string s)
    {
        s = s.Replace(home, "<home>").Replace(root, "<root>");
        return s.Length > 120 ? s.Substring(0, 60) + "..." + s.Substring(s.Length - 40) : s;
    }

    // Each path gives the expected answer; the info lists any that don't.
    static void All(string id, GuardResult expected, Func<string, GuardResult> check, params string[] paths)
    {
        List<string> wrong = new List<string>();
        foreach (string p in paths)
        {
            GuardResult r = check(p);
            if (r != expected) wrong.Add(Show(p) + " -> " + r);
        }
        Check(id + " " + expected + " for " + paths.Length + " form(s)", wrong.Count == 0, string.Join("; ", wrong.ToArray()));
    }

    // DEC-17: each path gives the expected answer and the guard hands none of them to a Win32 file API.
    static void Untouched(string id, GuardResult expected, params string[] paths)
    {
        List<string> wrong = new List<string>();
        foreach (string p in paths)
        {
            lock (opened) opened.Clear();
            GuardResult r = Read(p);
            List<string> touched = new List<string>();
            lock (opened)
            {
                foreach (string o in opened)
                {
                    if (!o.Equals(home, StringComparison.OrdinalIgnoreCase) && !TargetLike(o)) touched.Add(o);
                }
            }
            if (r != expected || touched.Count > 0) wrong.Add(Show(p) + " -> " + r + (touched.Count > 0 ? " opened " + string.Join(", ", touched.ToArray()) : ""));
        }
        Check(id + " " + expected + " and never opened, for " + paths.Length + " form(s)", wrong.Count == 0, string.Join("; ", wrong.ToArray()));
    }

    // The guard lists and opens the decoy sign-in files themselves on each check; those opens are expected.
    static bool TargetLike(string path)
    {
        return path.StartsWith(home, StringComparison.OrdinalIgnoreCase) && (SignInGuard.TargetFiles().Contains(path) || path.EndsWith("backups", StringComparison.OrdinalIgnoreCase));
    }

    // Each path is SignIn or Unresolved, never Clear, and nothing on the network or a device was touched.
    static void NeverClear(string id, params string[] paths)
    {
        List<string> wrong = new List<string>();
        foreach (string p in paths)
        {
            lock (opened) opened.Clear();
            GuardResult r = Read(p);
            List<string> touched = new List<string>();
            lock (opened)
            {
                foreach (string o in opened)
                {
                    if (Regex.IsMatch(o, @"203\.0\.113|pipe|Mup|Redirector|\\UNC\\|COM1|PhysicalDrive", RegexOptions.IgnoreCase) || o.StartsWith(@"\\.\")) touched.Add(o);
                }
            }
            if (r == GuardResult.Clear || touched.Count > 0) wrong.Add(Show(p) + " -> " + r + (touched.Count > 0 ? " opened " + string.Join(", ", touched.ToArray()) : ""));
        }
        Check(id + " never Clear and nothing on the network or a device opened, for " + paths.Length + " form(s)", wrong.Count == 0, string.Join("; ", wrong.ToArray()));
    }

    static string Short(string path)
    {
        StringBuilder sb = new StringBuilder(1024);
        uint n = GetShortPathNameW(path, sb, sb.Capacity);
        return n > 0 && n < sb.Capacity ? sb.ToString() : path;
    }

    static bool Junction(string link, string target)
    {
        ProcessStartInfo info = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"");
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;
        using (Process p = Process.Start(info)) { p.StandardOutput.ReadToEnd(); p.WaitForExit(); }
        return Directory.Exists(link);
    }

    static string RealDosDevice(char letter)
    {
        StringBuilder sb = new StringBuilder(1024);
        return QueryDosDeviceW(letter + ":", sb, sb.Capacity) == 0 ? null : sb.ToString();
    }

    // A letter with no definition at all right now, so the stand-in never shadows a real drive.
    static string FreeDrive()
    {
        for (char c = 'Q'; c <= 'Y'; c++)
        {
            StringBuilder sb = new StringBuilder(1024);
            if (QueryDosDeviceW(c + ":", sb, sb.Capacity) == 0) return c + ":";
        }
        return null;
    }

    static void Unlock(string path)
    {
        try
        {
            FileSecurity fs = File.GetAccessControl(path);
            foreach (FileSystemAccessRule rule in fs.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType == AccessControlType.Deny) fs.RemoveAccessRuleSpecific(rule);
            }
            File.SetAccessControl(path, fs);
            string dir = Path.GetDirectoryName(path);
            DirectorySecurity ds = Directory.GetAccessControl(dir);
            foreach (FileSystemAccessRule rule in ds.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType == AccessControlType.Deny) ds.RemoveAccessRuleSpecific(rule);
            }
            Directory.SetAccessControl(dir, ds);
        }
        catch (Exception e) { Log("note: couldn't unlock " + Show(path) + ": " + e.Message); }
    }

    static void Run(out string locked)
    {
        locked = null;
        home = Path.Combine(root, "home");
        proj = Path.Combine(root, "proj");
        Directory.CreateDirectory(Path.Combine(home, ".claude", "backups"));
        Directory.CreateDirectory(proj);
        creds = Path.Combine(home, ".claude", ".credentials.json");
        acct = Path.Combine(home, ".claude.json");
        const string canary = "{\"decoy\":\"canary-7f3a\"}";
        File.WriteAllText(creds, canary);
        File.WriteAllText(acct, canary);
        File.WriteAllText(acct + ".backup", canary);
        File.WriteAllText(acct + ".tmp.123.abc", canary);
        File.WriteAllText(Path.Combine(home, ".claude", "backups", "x.json"), canary);
        File.WriteAllText(Path.Combine(home, ".claude", "settings.json"), "{}");
        string plain = Path.Combine(proj, "plain.txt");
        File.WriteAllText(plain, "ordinary");
        File.WriteAllText(Path.Combine(proj, "other.txt"), "ordinary too");
        SignInGuard.HomeOverride = home;
        opened = new List<string>();
        SignInGuard.OpenLogForTests = delegate(string path) { lock (opened) opened.Add(path); };

        // H19-01, 02: the struct and identities.
        Type info = typeof(SignInGuard).GetNestedType("FileInfo", BindingFlags.NonPublic | BindingFlags.Public);
        Check("H19-01 FileInfo is 52 bytes, like BY_HANDLE_FILE_INFORMATION", info != null && Marshal.SizeOf(info) == 52, info == null ? "no type" : Marshal.SizeOf(info).ToString());
        string link = Path.Combine(proj, "notes.txt");
        bool hard = CreateHardLinkW(link, acct, IntPtr.Zero);
        uint serial, max, flags;
        GetVolumeInformationW(Path.GetPathRoot(root), null, 0, out serial, out max, out flags, null, 0);
        string a = SignInGuard.Identity(acct), b = SignInGuard.Identity(link), c = SignInGuard.Identity(plain);
        Check("H19-02 two names for one file have one identity, two files differ, and the volume serial is the volume's",
            hard && a != null && a == b && c != null && c != a && a.StartsWith(serial.ToString("x8") + ":"), Show(a + " " + b + " " + c + " serial " + serial.ToString("x8")));

        // H19-03 to 08: the exact targets in every spelling.
        All("H19-03 exact paths", GuardResult.SignIn, Read, creds, acct);
        All("H19-04 mixed case and slashes", GuardResult.SignIn, Read, acct.ToUpperInvariant(), creds.Replace('\\', '/'), acct.Replace('\\', '/').ToUpperInvariant(), "  \"" + creds + "\"  ");
        All("H19-05 ~", GuardResult.SignIn, Read, "~/.claude.json", "~\\.claude\\.credentials.json", "~/.CLAUDE/.Credentials.JSON");
        All("H19-06 $HOME and ${HOME}", GuardResult.SignIn, Read, "$HOME/.claude.json", "${HOME}/.claude/.credentials.json");
        All("H19-07 %USERPROFILE% and $env:USERPROFILE", GuardResult.SignIn, Read, "%USERPROFILE%\\.claude.json", "$env:USERPROFILE\\.claude\\.credentials.json", "$env:HOME/.claude.json");
        All("H19-08 file: URI", GuardResult.SignIn, Read, new Uri(acct).AbsoluteUri, new Uri(creds).AbsoluteUri);

        // H19-09 to 12: copies.
        All("H19-09 .claude.json.backup", GuardResult.SignIn, Read, acct + ".backup", "~/.claude.json.backup");
        All("H19-10 .claude.json.tmp.<pid>.<hex>", GuardResult.SignIn, Read, acct + ".tmp.123.abc");
        GuardResult before = Read(Path.Combine(proj, "late.txt"));
        string late = acct + ".tmp.999.def";
        File.WriteAllText(late, canary);
        bool lateLink = CreateHardLinkW(Path.Combine(proj, "late.txt"), late, IntPtr.Zero);
        Check("H19-11 a tmp copy made after the guard first ran is a target (a hard link to it is refused)",
            before == GuardResult.Clear && lateLink && Read(Path.Combine(proj, "late.txt")) == GuardResult.SignIn, before + " " + lateLink);
        All("H19-12 ~/.claude/backups", GuardResult.SignIn, Read, "~/.claude/backups/x.json", Path.Combine(home, ".claude", "backups", "x.json"));

        // H19-13 to 20: the B2 probe forms.
        StringBuilder vol = new StringBuilder(100);
        GetVolumeNameForVolumeMountPointW(Path.GetPathRoot(home), vol, 100);
        StringBuilder dev = new StringBuilder(260);
        QueryDosDeviceW(Path.GetPathRoot(home).TrimEnd('\\'), dev, 260);
        string rel = acct.Substring(3);
        All("H19-13 \\\\?\\C:\\ form", GuardResult.SignIn, Read, @"\\?\" + acct);
        All("H19-14 \\\\?\\Volume{GUID} form", GuardResult.SignIn, Read, vol + rel);
        All("H19-15 GLOBALROOT form", GuardResult.SignIn, Read, @"\\?\GLOBALROOT" + dev + "\\" + rel);
        All("H19-16 ::$DATA", GuardResult.SignIn, Read, acct + "::$DATA", link + "::$DATA");
        All("H19-17 :stream", GuardResult.SignIn, Read, acct + ":x", link + ":x");
        string dots = acct.Substring(0, 3) + new StringBuilder().Insert(0, @".\", 520) + rel;
        All("H19-18 a path over 1,000 characters with \\.\\ segments", GuardResult.SignIn, Read, dots, acct.Substring(0, 3) + new StringBuilder().Insert(0, @".\", 520) + link.Substring(3));
        string shortAcct = Short(acct);
        if (shortAcct == acct) Log("SKIP H19-19 8.3 names are off on this volume, so there's no short name to test");
        else All("H19-19 the 8.3 short name", GuardResult.SignIn, Read, shortAcct, Short(creds));
        All("H19-20 \\\\.\\C:\\ form", GuardResult.SignIn, Read, @"\\.\" + acct);

        // H19-21 to 24: links and junctions.
        All("H19-21 a hard link", GuardResult.SignIn, Read, link);
        string sym = Path.Combine(proj, "sym.txt");
        if (CreateSymbolicLinkW(sym, acct, 2) && File.Exists(sym)) All("H19-22 a file symlink", GuardResult.SignIn, Read, sym);
        else Log("SKIP H19-22 file symlinks need Developer Mode or admin here (error " + Marshal.GetLastWin32Error() + ")");
        string j = Path.Combine(proj, "j"), h = Path.Combine(proj, "h");
        bool junctions = Junction(j, Path.Combine(home, ".claude")) && Junction(h, home);
        Check("H19-23 a junction to the decoy .claude", junctions && Read(Path.Combine(j, ".credentials.json")) == GuardResult.SignIn && Read(Path.Combine(j, "settings.json")) == GuardResult.Clear);
        Check("H19-24 a junction to the decoy home, read through its short name and searched", junctions
            && Read(Path.Combine(h, Path.GetFileName(shortAcct))) == GuardResult.SignIn && Search("Grep", h, proj) == GuardResult.SignIn);

        // H19-25 to 28: forms it can't resolve and that don't name a target.
        All("H19-25 a bad volume GUID", GuardResult.Unresolved, Read, @"\\?\Volume{00000000-0000-0000-0000-000000000000}\x.txt");
        All("H19-26 GLOBALROOT to a missing device", GuardResult.Unresolved, Read, @"\\?\GLOBALROOT\Device\HarddiskVolume999\x.txt");
        string deniedDir = Path.Combine(proj, "denied");
        Directory.CreateDirectory(deniedDir);
        locked = Path.Combine(deniedDir, "locked.txt");
        File.WriteAllText(locked, "x");
        SecurityIdentifier me = WindowsIdentity.GetCurrent().User;
        FileSecurity fs = File.GetAccessControl(locked);
        fs.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.ReadAttributes | FileSystemRights.ReadData, AccessControlType.Deny));
        File.SetAccessControl(locked, fs);
        DirectorySecurity ds = Directory.GetAccessControl(deniedDir);
        ds.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.ListDirectory, AccessControlType.Deny));
        Directory.SetAccessControl(deniedDir, ds);
        All("H19-27 an access-denied file", GuardResult.Unresolved, Read, locked);
        All("H19-28 an overlong path to an ordinary file", GuardResult.Unresolved, Read, proj + "\\" + new StringBuilder().Insert(0, @".\", 300) + "plain.txt");

        // H19-29 to 31: files that don't exist yet.
        All("H19-29 a new ordinary file, also in new folders", GuardResult.Clear, Write, Path.Combine(proj, "new.txt"), Path.Combine(proj, "a", "b", "new.txt"), "relative-new.txt");
        All("H19-30 a new file named like a copy", GuardResult.SignIn, Write, "~/.claude.json.new", Path.Combine(h, ".claude.json.new"));
        All("H19-31 a missing \\\\?\\Volume path named .claude.json", GuardResult.SignIn, Write, @"\\?\Volume{00000000-0000-0000-0000-000000000000}\x\.claude.json");

        // H19-32 to 35: search roots.
        Check("H19-32 Grep and Glob over the home folder", Search("Grep", home, proj) == GuardResult.SignIn && Search("Glob", "~", proj) == GuardResult.SignIn && Search("Grep", "~/.claude", proj) == GuardResult.SignIn);
        Check("H19-33 a search from the drive root", Search("Grep", Path.GetPathRoot(home), proj) == GuardResult.SignIn && Search("Glob", Path.GetPathRoot(home).Replace('\\', '/'), proj) == GuardResult.SignIn);
        Check("H19-34 a search through a junction to the home folder, and an absolute Glob pattern over it", junctions && Search("Grep", h, proj) == GuardResult.SignIn
            && SignInGuard.Check("Glob", Input("pattern", h.Replace('\\', '/') + "/**/*.json"), proj) == GuardResult.SignIn);
        Check("H19-35 no path searches the working folder: the home folder is refused, a sibling folder is clear",
            Search("Grep", null, home) == GuardResult.SignIn && Search("Grep", proj, home) == GuardResult.Clear && Search("Glob", null, proj) == GuardResult.Clear
            && Search("Glob", null, null) == GuardResult.Unresolved);

        // H19-36 to 39: commands, for Bash and PowerShell alike.
        string[] oldRows = { "cat ~/.claude/.credentials.json", "type %USERPROFILE%\\.claude.json", "cat ~/.claude.json.backup", "grep oauthAccount x", "claude auth status", "claude setup-token",
            "cd ~/.claude && cat .cr*", "cat ~/.claude/.c*s.json", "Get-Content ~/.claude.json" };
        string[] trickRows = { "cat ~/.c?aude.json", "cat $HOME/.claude.jso[n]", "cat ~/cl`aude", "type cl^aude.json", "cat \"cla\"\"ude.json\"", "gc ('~/.cla'+'ude.json')",
            "cat ~/.{c,x}laude.json", "cat ~/*claude*", "cat C:\\x\\cla\\ude" };
        string[] fine = { "ls", "git status", "python script.py", "dir", "npm test", "cat *.json", "ls -a .*", "echo [1]", "Get-ChildItem *.txt",
            "ls src/*.js*", "cp *.c* out/", "Get-ChildItem -Filter *.jso*", "ls config/*.j*", "ls *on" };
        foreach (string tool in new[] { "Bash", "PowerShell" })
        {
            bool s5 = tool == "PowerShell";
            All(s5 ? "H19-38 PowerShell: the old pattern rows" : "H19-36 Bash: the old pattern rows", GuardResult.SignIn, x => Command(tool, x), oldRows);
            All(s5 ? "H19-38 PowerShell: wildcard and quoting tricks" : "H19-37 Bash: wildcard and quoting tricks", GuardResult.SignIn, x => Command(tool, x), trickRows);
            All("H19-39 " + tool + ": ordinary commands", GuardResult.Clear, x => Command(tool, x), fine);
        }
        Dictionary<string, object> described = Input("command", "ls");
        described["description"] = "List the credentials folder";
        Check("H19-39 a command's description is only shown, so it isn't checked", SignInGuard.Check("Bash", described, proj) == GuardResult.Clear);

        // H19-40: the Reviewer's probe table, plus every string field.
        List<string> probe = new List<string> { acct, creds, vol + rel, @"\\?\GLOBALROOT" + dev + "\\" + rel, @"\\localhost\" + acct.Substring(0, 1) + "$\\" + rel,
            @"\\127.0.0.1\" + acct.Substring(0, 1) + "$\\" + rel, dots, acct + ".backup", acct + ".tmp.123.abc", acct + "::$DATA", acct + ".", acct + " " };
        List<string> open = new List<string>();
        foreach (string p in probe)
        {
            GuardResult r = Read(p);
            if (r == GuardResult.Clear) open.Add(Show(p));
        }
        Dictionary<string, object> nested = Input("edits", new object[] { Input("file_path", acct) });
        GuardResult n1 = SignInGuard.Check("MultiEdit", nested, proj), n2 = SignInGuard.Check("WebFetch", Input("url", new Uri(acct).AbsoluteUri), proj);
        GuardResult n3 = SignInGuard.Check("Write", Input("content", "// see " + plain), proj), n4 = SignInGuard.Check("Write", Input("content", "line one" + Environment.NewLine + acct), proj);
        Check("H19-40 no probe row is Clear; a nested path field and a path-like extra field can refuse, and other text is never unresolved",
            open.Count == 0 && n1 == GuardResult.SignIn && n2 == GuardResult.SignIn && n3 == GuardResult.Clear && n4 == GuardResult.Clear, string.Join("; ", open.ToArray()) + " " + n1 + " " + n2 + " " + n3 + " " + n4);

        // H19-41 to 48 (DEC-17): network paths, pipes and devices are never opened.
        Untouched("H19-41 a network path named like a sign-in file", GuardResult.SignIn, @"\\203.0.113.1\s\.claude.json", "//203.0.113.1/s/.claude/.credentials.json");
        Untouched("H19-41 a network path that names nothing, and the dot-UNC form", GuardResult.Unresolved, @"\\203.0.113.1\s\x.txt", @"\\.\UNC\203.0.113.1\s\x.txt");
        lock (opened) opened.Clear();
        Check("H19-41 a search over a network path is unresolved and never opened", Search("Grep", @"\\203.0.113.1\s", proj) == GuardResult.Unresolved
            && Search("Glob", null, @"\\203.0.113.1\s") == GuardResult.Unresolved && !opened.Exists(o => o.Contains("203.0.113")));
        Untouched("H19-42 the question-mark UNC form", GuardResult.Unresolved, @"\\?\UNC\203.0.113.1\s\x.txt");
        Untouched("H19-43 WebDAV and file://host", GuardResult.Unresolved, @"\\203.0.113.1@SSL\s\x", @"\\203.0.113.1@8080\s\x", "file://203.0.113.1/s/x");
        Untouched("H19-44 a named pipe", GuardResult.Unresolved, @"\\.\pipe\x", @"\\?\pipe\x");
        Untouched("H19-45 devices and device names", GuardResult.Unresolved, @"\\.\COM1", Path.Combine(proj, "nul.txt"), "CON", Path.Combine(proj, "com1"), Path.Combine(proj, "con .log"), @"\\.\PhysicalDrive0");
        Untouched("H19-46 GLOBALROOT to the network redirector", GuardResult.Unresolved, @"\\?\GLOBALROOT\Device\Mup\203.0.113.1\s\x", @"\\?\GLOBALROOT\Device\LanmanRedirector\203.0.113.1\s\x");
        string fake = FreeDrive();
        string localTarget = @"\??\" + proj;
        string fakeTarget = @"\??\UNC\203.0.113.1\s";
        SignInGuard.DosDeviceForTests = delegate(char letter)
        {
            if (fake == null || letter != fake[0]) return RealDosDevice(letter);
            return fakeTarget;
        };
        Untouched("H19-47 a drive letter whose target is a share", GuardResult.Unresolved, fake + "\\x.txt", fake + "/x.txt");
        fakeTarget = @"\Device\LanmanRedirector\;" + fake + @"0000000000001234\203.0.113.1\s";
        Untouched("H19-47 a mapped network drive", GuardResult.Unresolved, fake + "\\x.txt");
        fakeTarget = localTarget;
        Check("H19-47 a subst drive to a local folder resolves to it: a hard link there is refused, an ordinary file is clear",
            Read(fake + "\\notes.txt") == GuardResult.SignIn && Read(fake + "\\plain.txt") == GuardResult.Clear);
        SignInGuard.DosDeviceForTests = null;
        lock (opened) opened.Clear();
        GuardResult local = Read(plain);
        Check("H19-48 a local scratch path is clear, and the open log does record it", local == GuardResult.Clear && opened.Contains(plain), local + " " + opened.Count);

        // H19-51 to 54 (Worker A's g1 review): .. in device paths, dot segments in \\?\ paths, and the log seeing every call.
        string volumeDevice = dev.ToString().TrimStart('\\');
        string volumeName = volumeDevice.Substring(volumeDevice.LastIndexOf('\\') + 1);
        NeverClear("H19-51 .. climbing out of a device path's drive or volume",
            @"\\.\C:\..\UNC\203.0.113.1\s\x", "//./C:/../UNC/203.0.113.1/s/x", @"\\.\C:\a\..\..\UNC\203.0.113.1\s\x", @"\\.\C:\..\pipe\x",
            @"\\.\GLOBALROOT\" + volumeDevice + @"\..\Mup\203.0.113.1\s\x",
            @"\\.\GLOBALROOT\" + volumeDevice + @"\..\" + volumeName + "\\" + link.Substring(3),
            @"\\.\GLOBALROOT\" + volumeDevice + @"\..\" + volumeName + "\\" + Short(acct).Substring(3));
        All("H19-52 a question-mark path with a . or .. segment", GuardResult.Unresolved, Read, @"\\?\" + proj + @"\..\proj\plain.txt", @"\\?\" + proj + @"\.\plain.txt");
        All("H19-52 the same paths without them, or as a plain path", GuardResult.Clear, Read, @"\\?\" + plain, proj + @"\..\proj\plain.txt");
        All("H19-53 ordinary wildcards are clear (also in H19-39)", GuardResult.Clear, x => Command("Bash", x), "ls src/*.js*", "cp *.c* out/", "Get-ChildItem -Filter *.jso*", "ls config/*.j*", "ls *on");
        All("H19-53 wildcards that spell a sign-in name are still refused", GuardResult.SignIn, x => Command("Bash", x), "cat ~/.c?aude.json", "cat ~/*cla*", "cat ~/.cr*", "cat ~/*dentia*", "cat ~/.c*s.json");
        lock (opened) opened.Clear();
        Read(plain);
        string driveRoot = plain.Substring(0, 3);
        Check("H19-54 the open log also sees the drive-type check and the backups folder", opened.Contains(driveRoot) && opened.Exists(o => o.EndsWith(@"\.claude\backups", StringComparison.OrdinalIgnoreCase)),
            string.Join(", ", opened.ConvertAll(o => Show(o)).ToArray()));

        // H19-49, 50 (DEC-23): the guard's comment is honest about the check-then-use gap.
        string source = File.ReadAllText(Path.Combine(root, "..", "..", "..", "snapshot", "widget.cs"));
        int start = source.IndexOf("static class SignInGuard");
        int header = start < 0 ? -1 : source.LastIndexOf("enum GuardResult", start);
        string comment = header < 0 ? "" : source.Substring(header, start - header);
        Check("H19-49 the guard's header comment says it's best effort, checked before the tool opens the path",
            comment.Contains("best effort, not a lock") && comment.Contains("a link swapped in between"));
        Check("H19-50 widget.cs makes no unqualified \"don't get past\" or \"can't get past\" claim",
            !Regex.IsMatch(source, @"(don't|can't|cannot|do not) get past", RegexOptions.IgnoreCase));
    }
}
