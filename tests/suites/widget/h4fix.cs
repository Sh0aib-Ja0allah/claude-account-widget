using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
// Fixes from review: A1 (huge reset times crashed the widget) and the output folder's full-path check.
static partial class H4
{
    // A .NET Runtime crash event for this process since the given time, or null. Windows Error Reporting can keep a
    // crashed process alive for a while, so HasExited alone doesn't prove it survived.
    static string CrashLogged(DateTime since, int pid)
    {
        using (EventLog log = new EventLog("Application"))
        {
            for (int i = log.Entries.Count - 1; i >= 0 && i >= log.Entries.Count - 400; i--)
            {
                EventLogEntry entry = log.Entries[i];
                if (entry.TimeGenerated < since) break;
                if ((entry.Source == ".NET Runtime" || entry.Source == "Application Error") && entry.Message.Contains("ClaudeAccountWidget")
                    && (entry.Message.Contains(pid.ToString()) || entry.Message.IndexOf("0x" + pid.ToString("x"), StringComparison.OrdinalIgnoreCase) >= 0))
                    return entry.Source + ": " + entry.Message.Split('\n')[0];
            }
        }
        return null;
    }

    static async Task Fixes()
    {
        // S3-1: under an hour never says 60m
        DateTime fr = new DateTime(2026, 11, 14, 11, 0, 0, DateTimeKind.Utc);
        string sixty = WidgetWindow.ForecastText(16000 / (160 + 59.7 / 60), fr, fr.AddDays(-7).AddHours(160), TimeZoneInfo.Utc);
        Check("forecast: 59m 42s left reads as an hour, not 60m", sixty == "Full in 1h 0m", sixty ?? "null");
        Check("forecast: 59m left still reads 59m", WidgetWindow.ForecastText(100 * 160 / (160 + 59.0 / 60), fr, fr.AddDays(-7).AddHours(160), TimeZoneInfo.Utc) == "Full in 59m");

        DateTime now = DateTime.UtcNow;
        double nowEpoch = Epoch(now);
        double normal = Epoch(now.AddDays(3));
        Dictionary<string, double> bad = new Dictionary<string, double>
        {
            { "milliseconds 1.8e12", 1.8e12 }, { "past year 9999", 2.534e11 }, { "1e300", 1e300 },
            { "zero", 0 }, { "negative", -5 }, { "61 days ahead", Epoch(now.AddDays(61)) },
        };

        // The helper
        foreach (KeyValuePair<string, double> b in bad)
            Check("reset helper: " + b.Key + " is unknown", ResetTimes.ToUtc(b.Value, now) == null);
        Check("reset helper: NaN and infinity are unknown", ResetTimes.ToUtc(double.NaN, now) == null && ResetTimes.ToUtc(double.PositiveInfinity, now) == null);
        DateTime? n = ResetTimes.ToUtc(normal, now);
        Check("reset helper: a normal time converts", n.HasValue && Math.Abs((n.Value - now.AddDays(3)).TotalSeconds) < 2);
        Check("reset helper: a past time is kept (it means a reset)", ResetTimes.ToUtc(nowEpoch - 3600, now).HasValue);

        // Alerts: no throw, unknown reset fires without a time
        foreach (KeyValuePair<string, double> b in bad)
        {
            AlertState.Entry e = null; string[] r = null; bool threw = false;
            try { r = UsageAlerts.Evaluate(ref e, 75, b.Value, nowEpoch, TimeZoneInfo.Local); } catch (Exception) { threw = true; }
            Check("alerts: " + b.Key + " doesn't throw and is treated as unknown", !threw && r != null && r[1] == "7-day limit." && e.Largest == 0, threw ? "threw" : (r == null ? "null" : r[1]));
        }

        // The card: no throw from Refresh, unknown reset shown as such, alerts on
        foreach (KeyValuePair<string, double> b in bad)
        {
            string d = Fresh("fix-a1-" + bad.Keys.ToList().IndexOf(b.Key));
            Recorder rec = new Recorder();
            Usage(d, 75, b.Value);
            WidgetWindow w = AlertWidget(d, rec);
            bool threw = false;
            try { Tick(w); Tick(w); } catch (Exception ex) { threw = true; Log("THREW " + ex.GetType().Name); }
            TextBlock resetLine = (TextBlock)Get(Get(w, "sevenDay"), "Reset");
            Check("card: " + b.Key + " doesn't crash and shows the reset as unknown", !threw && resetLine.Text == "Reset time unknown", resetLine.Text);
            w.Close();
        }
        string nd = Fresh("fix-a1-normal");
        Usage(nd, 40, normal);
        WidgetWindow nw = NewWidget(nd, true);
        Call(nw, "Refresh");
        Check("card: a normal reset still shows its time", ((TextBlock)Get(Get(nw, "sevenDay"), "Reset")).Text.StartsWith("Resets "));
        nw.Close();

        // The real exe keeps running with a bad usage.json across its 10-second refresh
        if (exe != null && File.Exists(exe))
        {
            string pd = Fresh("fix-a1-proc");
            File.WriteAllText(Path.Combine(pd, "settings.txt"), "-6000,0,0,0,default,system,1,1,off");
            Usage(pd, 75, 1.8e12);
            DateTime started = DateTime.Now.AddSeconds(-1);
            Process p = StartWidget(pd, account);
            await Task.Delay(14000);
            bool alive = !p.HasExited;
            string crash = CrashLogged(started, p.Id);
            Check("exe: still running 14 s after loading a usage file with reset 1.8e12, and no crash logged", alive && crash == null, crash ?? "");
            if (alive) { p.Kill(); p.WaitForExit(5000); }

            // A2: an "updated" value too large for DateTime
            foreach (string odd in new[] { "1e300", "-1e300", "638000000000000000", "NaN" })
            {
                Check("updated helper: " + odd + " is unknown", UpdatedTimes.ToUtc(double.Parse(odd, System.Globalization.CultureInfo.InvariantCulture), DateTime.UtcNow) == null);
            }
            Check("updated helper: a normal time converts", UpdatedTimes.ToUtc(Epoch(DateTime.UtcNow.AddMinutes(-5)) * 1000, DateTime.UtcNow).HasValue);
            string ud = Fresh("fix-a2");
            File.WriteAllText(Path.Combine(ud, "usage.json"), "{\"account\":\"sample@example.com\",\"updated\":1e300,\"five_hour\":null,\"seven_day\":{\"used\":40,\"resets\":" + normal.ToString("F0") + "}}");
            WidgetWindow uw = NewWidget(ud, true);
            bool uthrew = false;
            try { Call(uw, "Refresh"); } catch (Exception) { uthrew = true; }
            string updatedText = ((TextBlock)Get(uw, "updated")).Text;
            Check("card: updated 1e300 doesn't crash and shows time unknown", !uthrew && updatedText == "Updated time unknown", updatedText);
            uw.Close();
            string pd2 = Fresh("fix-a2-proc");
            File.WriteAllText(Path.Combine(pd2, "settings.txt"), "-6000,0,0,0,default,system,1,1,off");
            File.WriteAllText(Path.Combine(pd2, "usage.json"), "{\"account\":\"sample@example.com\",\"updated\":1e300,\"five_hour\":null,\"seven_day\":{\"used\":40,\"resets\":" + normal.ToString("F0") + "}}");
            started = DateTime.Now.AddSeconds(-1);
            Process p2 = StartWidget(pd2, account);
            await Task.Delay(14000);
            bool alive2 = !p2.HasExited;
            string crash2 = CrashLogged(started, p2.Id);
            Check("exe: still running 14 s with updated 1e300, and no crash logged", alive2 && crash2 == null, crash2 ?? "");
            if (alive2) { p2.Kill(); p2.WaitForExit(5000); }
        }

        // Output folder: a drive-relative path is refused even when this process's current folder is inside the output folder
        string outFolder = Fresh("fix-out");
        string sub = Path.Combine(outFolder, "inner");
        Directory.CreateDirectory(sub);
        string savedDir = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = sub;
            string drive = sub.Substring(0, 2);
            string relative = drive + "note.md";
            bool insideByOldRule = Path.IsPathRooted(relative) && Path.GetFullPath(relative).StartsWith(outFolder + "\\", StringComparison.OrdinalIgnoreCase);
            Note("evidence: " + relative + " passes IsPathRooted and resolves to " + Path.GetFullPath(relative) + " here, inside the output folder = " + insideByOldRule);
            Check("output folder: the old rule would have judged " + relative + " inside", insideByOldRule);

            ChatClient client = (ChatClient)FormatterServices.GetUninitializedObject(typeof(ChatClient));
            ChatRequest request = new ChatRequest();
            request.Laptop = true;
            request.DefaultOutputFolder = outFolder;
            request.CurrentOutputFolder = delegate { return outFolder; };
            Set(client, "request", request);
            Func<string, bool> allow = delegate(string path)
            {
                Dictionary<string, object> input = new Dictionary<string, object>();
                input["file_path"] = path;
                return ((Task<bool>)Call(client, "AllowInOutputFolder", "Write", input)).Result;
            };
            Check("output folder: drive-relative " + relative + " now needs a click", !allow(relative));
            Check("output folder: a full path inside still needs no click", allow(Path.Combine(sub, "note.md")));
            Check("output folder: a full path outside still needs a click", !allow(Path.Combine(root, "elsewhere.md")));
            Check("output folder: rooted without a drive needs a click", !allow("\\note.md"));
        }
        finally
        {
            Environment.CurrentDirectory = savedDir;
        }
    }
}
