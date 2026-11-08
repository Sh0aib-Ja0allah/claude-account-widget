using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
// Worker A's independent S4 checks (usage history recording). No window needed except the widget path; no network.
// h9.exe <out folder>
static class H9
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static string root, account;
    static int fails, passes;
    static void Log(string s) { Console.WriteLine(s); Console.Out.Flush(); }
    static void Check(string what, bool ok, string info = "")
    {
        Log((ok ? "PASS " : "FAIL ") + what + (info.Length > 0 ? " [" + info.Replace("\r", "\\r").Replace("\n", "\\n") + "]" : ""));
        if (ok) passes++; else fails++;
    }
    static string Fresh(string name)
    {
        string d = Path.Combine(root, name);
        if (Directory.Exists(d)) { foreach (string f in Directory.GetFiles(d)) File.SetAttributes(f, FileAttributes.Normal); Directory.Delete(d, true); }
        Directory.CreateDirectory(d);
        return d;
    }
    static double Now() { return Math.Floor((DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds); }
    const string K1 = "0123456789abcdef", K2 = "fedcba9876543210";

    [STAThread]
    static void Main(string[] args)
    {
        root = args[0];
        Directory.CreateDirectory(root);
        account = Path.Combine(root, "account.json");
        File.WriteAllText(account, "{\"oauthAccount\":{\"emailAddress\":\"sample@example.com\",\"planDisplayName\":\"Max\"}}");
        try { Run(); } catch (Exception e) { Log("HARNESS ERROR " + e); fails++; }
        Log("passes=" + passes + " fails=" + fails);
        Process.GetCurrentProcess().Kill();
    }

    static void Run()
    {
        double now = Now();
        double reset = now + 3 * 86400;

        // 1. A row cut off mid-write (crash, power loss) and a hand edit without a final newline.
        string d = Fresh("torn");
        string f = Path.Combine(d, "usage-history.csv");
        File.WriteAllText(f, ((long)(now - 7200)) + ",40," + (long)reset + "," + K1 + "\r\n" + ((long)(now - 3600)) + ",45");
        UsageHistory.Append(f, now, 47, reset, K1);
        List<UsageHistory.Row> rows = UsageHistory.Read(f);
        string text = File.ReadAllText(f);
        bool bogus = rows.Any(r => r.Percent > 1000 || r.Percent < -1000);
        Check("torn last line: the next append doesn't merge into a bogus row", !bogus, string.Join(" ; ", rows.Select(r => r.Time + "/" + r.Percent + "/" + r.Reset)) + " | file: " + text);
        Check("torn last line: the new row is readable", rows.Any(r => r.Time == now && r.Percent == 47), rows.Count + " rows");

        // 2. The 64 KB look-back: the account's last row hidden behind 2,000 rows of another account.
        d = Fresh("lookback");
        f = Path.Combine(d, "usage-history.csv");
        StringBuilder sb = new StringBuilder();
        sb.Append((long)(now - 600)).Append(",50,").Append((long)reset).Append(",").Append(K1).Append("\r\n");
        for (int i = 0; i < 2000; i++) sb.Append((long)(now - 599 + i % 500)).Append(",").Append(i % 90).Append(",").Append((long)reset).Append(",").Append(K2).Append("\r\n");
        File.WriteAllText(f, sb.ToString());
        long before = new FileInfo(f).Length;
        UsageHistory.Append(f, now, 50, reset, K1);
        bool added = new FileInfo(f).Length > before;
        Log("NOTE look-back: K1's last row sits " + (before / 1024) + " KB back; same figures within the hour " + (added ? "added a duplicate row (harmless, as Worker B expected)" : "were skipped"));
        Check("look-back: a duplicate, if any, is exactly one well-formed row", UsageHistory.Read(f).Count == 2001 + (added ? 1 : 0));

        // 3. Appends and trims at the same time on a file just under 2 MB.
        d = Fresh("contend");
        f = Path.Combine(d, "usage-history.csv");
        sb = new StringBuilder();
        int seed = 0;
        while (sb.Length < UsageHistory.TrimAt - 4000) { sb.Append((long)(now - 80000 + seed % 70000)).Append(",").Append(seed % 100).Append(".5,").Append((long)reset).Append(",").Append(K2).Append("\r\n"); seed++; }
        File.WriteAllText(f, sb.ToString());
        int threads = 4, per = 150; int falseCount = 0; string escaped = "";
        List<Thread> list = new List<Thread>();
        for (int t = 0; t < threads; t++)
        {
            int id = t;
            Thread th = new Thread(delegate()
            {
                for (int i = 0; i < per; i++)
                {
                    try
                    {
                        // each append is unique (percent encodes thread and index), so every one must be written
                        if (!UsageHistory.Append(f, now + 1 + i, id * 25 + i * 0.1 + 0.01, reset, K1)) Interlocked.Increment(ref falseCount);
                        if (i % 25 == 0 && !UsageHistory.Trim(f, now + 1 + i)) { }
                        if (i % 40 == 0) UsageHistory.Read(f);
                    }
                    catch (Exception e) { escaped = e.GetType().Name + ": " + e.Message; }
                }
            });
            list.Add(th); th.Start();
        }
        foreach (Thread th in list) th.Join();
        rows = UsageHistory.Read(f);
        int mine = rows.Count(r => r.Key == K1);
        string[] lines = File.ReadAllLines(f);
        int damaged = lines.Count(l => l.Length > 0 && UsageHistory.Parse(l, now + 1000) == null);
        Check("contention: nothing thrown out of Append, Trim or Read", escaped.Length == 0, escaped);
        Check("contention: no append reported failure", falseCount == 0, falseCount.ToString());
        Check("contention: all " + (threads * per) + " unique appends are in the file", mine == threads * per, mine.ToString());
        Check("contention: no damaged lines", damaged == 0, damaged.ToString());
        Check("contention: file ends at or under 2 MB and 30,000 rows", new FileInfo(f).Length <= UsageHistory.TrimAt + 64 && rows.Count <= UsageHistory.MaxRows + threads * per, new FileInfo(f).Length + " bytes, " + rows.Count + " rows");
        Check("contention: no temp file left", !File.Exists(f + ".widget.tmp"));

        // 4. Rows a hand edit or damage could leave, which S5's chart will read.
        d = Fresh("odd");
        f = Path.Combine(d, "usage-history.csv");
        File.WriteAllText(f, "1e300,50," + (long)reset + "," + K1 + "\r\n"
            + ((long)now) + ",50,1e300," + K1 + "\r\n"
            + ((long)now) + ",-5,0," + K1 + "\r\n"
            + ((long)now) + ",250,-7," + K1 + "\r\n");
        rows = UsageHistory.Read(f);
        Log("NOTE odd rows Parse accepts: " + string.Join(" ; ", rows.Select(r => r.Time.ToString("G3") + "/" + r.Percent + "/" + r.Reset.ToString("G3"))) + " (S5 must convert times with a guard like ResetTimes)");
        bool trimmed = UsageHistory.Trim(f, now);
        Log("NOTE a row timed 1e300 is never too old for Trim (now - time is negative): " + (UsageHistory.Read(f).Any(r => r.Time > 1e20) ? "still there after Trim" : "dropped") + ", Trim returned " + trimmed);

        // 5. A reader holding the file without write sharing: the append fails cleanly.
        d = Fresh("held");
        f = Path.Combine(d, "usage-history.csv");
        File.WriteAllText(f, ((long)(now - 7200)) + ",40," + (long)reset + "," + K1 + "\r\n");
        bool ok;
        using (FileStream held = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            ok = UsageHistory.Append(f, now, 41, reset, K1);
        }
        Check("held file: Append returns false and doesn't throw", !ok);
        Check("held file: next append works", UsageHistory.Append(f, now + 5, 41, reset, K1) && UsageHistory.Read(f).Count == 2);

        // 6. Hour rule edges.
        d = Fresh("hour");
        f = Path.Combine(d, "usage-history.csv");
        UsageHistory.Append(f, now, 33.333, reset, K1);
        UsageHistory.Append(f, now + 3599, 33.334, reset + 0.4, K1);
        Check("hour: 33.333 then 33.334 (same to 2 decimals) and a reset 0.4 s later within the hour: one row", UsageHistory.Read(f).Count == 1, UsageHistory.Read(f).Count.ToString());
        UsageHistory.Append(f, now + 3600, 33.33, reset, K1);
        Check("hour: exactly one hour later: a second row", UsageHistory.Read(f).Count == 2);
        UsageHistory.Append(f, now - 100, 33.33, reset, K1);
        Check("hour: a clock moved back adds a row rather than skipping", UsageHistory.Read(f).Count == 3);
        Check("rows: 2 decimals stored", File.ReadAllText(f).Contains(",33.33,"), File.ReadAllText(f));
    }
}
