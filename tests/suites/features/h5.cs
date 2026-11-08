using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
// Worker A's independent S0 checks. Off screen only; no real input, notice, shortcut or network.
// h5.exe <out folder> <statusline.js>
static class H5
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static string root, account, statusline;
    static int fails, passes;
    static object Get(object o, string f) { return o.GetType().GetField(f, Any).GetValue(o); }
    static void Set(object o, string f, object v) { o.GetType().GetField(f, Any).SetValue(o, v); }
    static void Call(object o, string m) { o.GetType().GetMethod(m, Any, null, Type.EmptyTypes, null).Invoke(o, null); }
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
    static WidgetWindow NewWidget(string data) { return new WidgetWindow(data, account, null, null, 300, 0, true); }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool MoveFileEx(string from, string to, int flags);

    [STAThread]
    static void Main(string[] args)
    {
        root = args[0]; statusline = args[1];
        Directory.CreateDirectory(root);
        account = Path.Combine(root, "account.json");
        File.WriteAllText(account, "{\"oauthAccount\":{\"emailAddress\":\"sample@example.com\",\"planDisplayName\":\"Max\"}}");
        Application app = new Application();
        app.DispatcherUnhandledException += (s, e) => { Log("UNHANDLED " + e.Exception); fails++; e.Handled = true; };
        try { Run(); } catch (Exception e) { Log("HARNESS ERROR " + e); fails++; }
        Log("passes=" + passes + " fails=" + fails);
        Process.GetCurrentProcess().Kill();
    }

    static void Run()
    {
        // 1. Two writers racing: WriteAtomic (Fetch's path) against a statusline-style temp + rename.
        string d = Fresh("race");
        string usage = Path.Combine(d, "usage.json");
        File.WriteAllText(usage, "{\"w\":0}");
        int widgetErrors = 0, otherErrors = 0, badReads = 0, reads = 0; string oddType = "";
        bool stop = false;
        Thread other = new Thread(delegate()
        {
            for (int i = 0; i < 400; i++)
            {
                string t = usage + "." + 99999 + ".tmp";
                try { File.WriteAllText(t, "{\"s\":" + i + "}"); if (!MoveFileEx(t, usage, 1)) otherErrors++; } catch (Exception) { otherErrors++; }
            }
        });
        Thread reader = new Thread(delegate()
        {
            while (!stop)
            {
                try
                {
                    string s = File.ReadAllText(usage); reads++;
                    if (!(s.StartsWith("{") && s.EndsWith("}"))) badReads++;
                }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        });
        other.Start(); reader.Start();
        for (int i = 0; i < 400; i++)
        {
            try { WidgetFiles.WriteAtomic(usage, "{\"w\":" + i + "}"); }
            catch (IOException) { widgetErrors++; }
            catch (UnauthorizedAccessException) { widgetErrors++; }
            catch (Exception e) { oddType = e.GetType().Name; }
        }
        other.Join(); stop = true; reader.Join();
        string final = File.ReadAllText(usage);
        Check("race: WriteAtomic only throws types Fetch and SaveSettings catch", oddType.Length == 0, oddType);
        Check("race: a reader never sees a half-written file", badReads == 0, badReads + " bad of " + reads + " reads");
        Check("race: final file whole", final.StartsWith("{") && final.EndsWith("}"), final);
        Check("race: no .widget.tmp left", !File.Exists(usage + ".widget.tmp"));
        Log("NOTE race: WriteAtomic failed " + widgetErrors + "/400 and the other writer " + otherErrors + "/400 while racing (a failed check retries later)");

        // 2. A reader holding the file open without delete sharing: the write fails cleanly and leaves the old file.
        File.WriteAllText(usage, "{\"old\":1}");
        bool threw = false; string type = "";
        using (FileStream held = new FileStream(usage, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { WidgetFiles.WriteAtomic(usage, "{\"new\":1}"); }
            catch (Exception e) { threw = true; type = e.GetType().Name; }
        }
        Check("held file: write fails with a caught type", threw && (type == "IOException" || type == "UnauthorizedAccessException"), type);
        Check("held file: old file kept, no temp left", File.ReadAllText(usage) == "{\"old\":1}" && !File.Exists(usage + ".widget.tmp"));
        WidgetFiles.WriteAtomic(usage, "{\"new\":1}");
        Check("held file: next write after release works", File.ReadAllText(usage) == "{\"new\":1}");

        // 3. A file written by the real statusline.js reaches the hook (the "either writer" claim).
        string sd = Fresh("statusline");
        File.Copy(statusline, Path.Combine(sd, "statusline.js"));
        File.WriteAllText(Path.Combine(sd, "settings.txt"), "-6000,0,0,0,default,system");
        string home = Fresh("fakehome");
        File.WriteAllText(Path.Combine(home, ".claude.json"), "{\"oauthAccount\":{\"emailAddress\":\"sample@example.com\"}}");
        WidgetWindow w = NewWidget(sd);
        w.ShowNotice = delegate(string a, string b, Action c) { };
        string input = "{\"rate_limits\":{\"five_hour\":{\"used_percentage\":12,\"resets_at\":1794049200},\"seven_day\":{\"used_percentage\":55,\"resets_at\":1794303827}}}";
        bool ran = RunNode(Path.Combine(sd, "statusline.js"), home, input);
        string written = File.Exists(Path.Combine(sd, "usage.json")) ? File.ReadAllText(Path.Combine(sd, "usage.json")) : "";
        Check("statusline: wrote usage.json for the fake account", ran && written.Contains("\"account\":\"sample@example.com\"") && written.Contains("\"updated\":"), written);
        Call(w, "Refresh");
        double seen = (double)Get(w, "lastUpdatedSeen");
        Check("statusline: hook sees the statusline's updated value", !double.IsNaN(seen) && written.Contains("\"updated\":" + ((long)seen).ToString()), seen.ToString("R"));
        Thread.Sleep(20);
        RunNode(Path.Combine(sd, "statusline.js"), home, input);
        Call(w, "Refresh");
        double seen2 = (double)Get(w, "lastUpdatedSeen");
        Check("statusline: a second statusline write is seen as new", seen2 > seen, seen + " -> " + seen2);
        Call(w, "Refresh");
        Check("statusline: refresh with no new write keeps the value", (double)Get(w, "lastUpdatedSeen") == seen2);
        File.WriteAllText(Path.Combine(home, ".claude.json"), "{\"oauthAccount\":{\"emailAddress\":\"Sample@Example.com\"}}");
        RunNode(Path.Combine(sd, "statusline.js"), home, input);
        Call(w, "Refresh");
        Check("statusline: account matched without case", (double)Get(w, "lastUpdatedSeen") > seen2);
        w.Close();

        // 4. Files the hook must ignore.
        string hd = Fresh("hookodd");
        File.WriteAllText(Path.Combine(hd, "settings.txt"), "-6000,0,0,0,default,system");
        string hf = Path.Combine(hd, "usage.json");
        WidgetWindow h = NewWidget(hd);
        h.ShowNotice = delegate(string a, string b, Action c) { };
        File.WriteAllText(hf, "{\"account\":\"sample@example.com\",\"five_hour\":null,\"seven_day\":{\"used\":40,\"resets\":1794049200}}");
        Call(h, "Refresh");
        Check("hook: file with no updated value is ignored", double.IsNaN((double)Get(h, "lastUpdatedSeen")));
        File.WriteAllText(hf, "{not json");
        Call(h, "Refresh");
        Check("hook: broken file is ignored", double.IsNaN((double)Get(h, "lastUpdatedSeen")));
        File.WriteAllText(hf, "{\"account\":\"\",\"updated\":5,\"seven_day\":{\"used\":40,\"resets\":1794049200}}");
        Call(h, "Refresh");
        Check("hook: file with an empty account is ignored", double.IsNaN((double)Get(h, "lastUpdatedSeen")));
        h.Close();

        // 5. Settings edge cases.
        string nd = Fresh("nosettings");
        WidgetWindow n = NewWidget(nd);
        Check("settings: no file gives defaults alerts off, update on, shortcut off", !n.alertsOn && n.updateCheckOn && n.shortcutSetting == "off");
        bool onScreen = n.Left >= SystemParameters.VirtualScreenLeft && n.Left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
        Log("NOTE settings: test mode with no settings.txt places the widget at " + n.Left + "," + n.Top + (onScreen ? " (on screen; every harness writes an off-screen settings.txt first)" : ""));
        n.Close();
        string cd = Fresh("comma");
        File.WriteAllText(Path.Combine(cd, "settings.txt"), "-6000,0,0,0,default,system");
        WidgetWindow c1 = NewWidget(cd);
        c1.shortcutSetting = "Ctrl+Alt+,";
        c1.SaveSettings(); c1.Close();
        WidgetWindow c2 = NewWidget(cd);
        Log("NOTE settings: a shortcut stored as \"Ctrl+Alt+,\" reloads as \"" + c2.shortcutSetting + "\" (S6 must store key names such as OemComma, never the character)");
        c2.Close();
        File.WriteAllText(Path.Combine(cd, "settings.txt"), "-6000,0,0,0,default,system,1,0,Ctrl+Alt+Shift+C,extra,fields");
        WidgetWindow c3 = NewWidget(cd);
        Check("settings: extra fields after 9 are ignored", c3.alertsOn && !c3.updateCheckOn && c3.shortcutSetting == "Ctrl+Alt+Shift+C");
        c3.SaveSettings(); c3.Close();
        Check("settings: save writes exactly 9 fields", File.ReadAllText(Path.Combine(cd, "settings.txt")).Split(',').Length == 9, File.ReadAllText(Path.Combine(cd, "settings.txt")));
        string ld = Fresh("legacy7");
        File.WriteAllText(Path.Combine(ld, "settings.txt"), "-6000,0,0,0,default,system,1");
        WidgetWindow l7 = NewWidget(ld);
        Check("settings: 7-field file turns alerts on and keeps update on, shortcut off", l7.alertsOn && l7.updateCheckOn && l7.shortcutSetting == "off");
        l7.Close();
    }

    static bool RunNode(string script, string home, string input)
    {
        ProcessStartInfo info = new ProcessStartInfo("node", "\"" + script + "\"");
        info.UseShellExecute = false; info.RedirectStandardInput = true; info.RedirectStandardOutput = true; info.CreateNoWindow = true;
        info.EnvironmentVariables["USERPROFILE"] = home;
        info.EnvironmentVariables["HOME"] = home;
        using (Process p = Process.Start(info))
        {
            p.StandardInput.Write(input); p.StandardInput.Close();
            p.StandardOutput.ReadToEnd();
            return p.WaitForExit(15000) && p.ExitCode == 0;
        }
    }
}
