using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
// S4: recording usage history.
static partial class H4
{
    static string[] HistLines(string dir)
    {
        string f = Path.Combine(dir, "usage-history.csv");
        return File.Exists(f) ? File.ReadAllLines(f) : new string[0];
    }

    static async Task WaitHistory(WidgetWindow w)
    {
        Task<bool> t = w.historyWrite;
        if (t != null) await t;
    }

    static async Task S4()
    {
        double now = Epoch(DateTime.UtcNow);
        double reset = Epoch(DateTime.UtcNow.AddDays(3));
        string key = AccountKeys.Of("sample@example.com");
        Check("key: 16 hex, ignores case and spaces", key.Length == 16 && key == AccountKeys.Of("  SAMPLE@Example.com "));

        // Append rules (pure file level)
        string d = Fresh("s4-append");
        string f = Path.Combine(d, "usage-history.csv");
        Check("append: first row", UsageHistory.Append(f, now, 40, reset, key) && HistLines(d).Length == 1);
        Check("append: row format", HistLines(d)[0] == now.ToString("F0") + ",40," + reset.ToString("F0") + "," + key, HistLines(d)[0]);
        UsageHistory.Append(f, now + 300, 40, reset, key);
        Check("append: unchanged within an hour adds nothing", HistLines(d).Length == 1);
        UsageHistory.Append(f, now + 3601, 40, reset, key);
        Check("append: unchanged after an hour adds a row", HistLines(d).Length == 2);
        UsageHistory.Append(f, now + 3700, 40.5, reset, key);
        Check("append: changed percent adds a row", HistLines(d).Length == 3);
        UsageHistory.Append(f, now + 3800, 40.5, reset + 7 * 86400, key);
        Check("append: changed reset adds a row", HistLines(d).Length == 4);
        string other = AccountKeys.Of("other@example.com");
        UsageHistory.Append(f, now + 3900, 40.5, reset + 7 * 86400, other);
        Check("append: another account's first row is added", HistLines(d).Length == 5);
        UsageHistory.Append(f, now + 4000, 40.5, reset + 7 * 86400, key);
        Check("append: compares with the same account's last row", HistLines(d).Length == 5);
        UsageHistory.Append(f, now + 4100, 33.333333, reset + 7 * 86400, key);
        Check("append: percent kept to two decimals", HistLines(d).Last().Split(',')[1] == "33.33", HistLines(d).Last());

        // R-D-04: damaged lines skipped
        string dj = Fresh("s4-junk");
        string fj = Path.Combine(dj, "usage-history.csv");
        File.WriteAllText(fj, "junk\r\n1,2,3\r\n" + now + ",50," + reset + "," + key + "\r\n" + now + ",x,1," + key + "\r\n" + now + ",50,1,ABCDEF0123456789\r\n"
            + new string('9', 5000) + ",1,1," + key + "\r\n" + now + ",NaN,1," + key + "\r\n" + now + ",60," + reset + "," + key + "\r\n" + now + ",70," + reset);
        List<UsageHistory.Row> rows = UsageHistory.Read(fj);
        Check("damaged: only the two good rows read", rows.Count == 2 && rows[0].Percent == 50 && rows[1].Percent == 60, rows.Count.ToString());
        Check("damaged: trim drops them", UsageHistory.Trim(fj, now) && UsageHistory.Read(fj).Count == 2 && HistLines(dj).Length == 2);

        // R-D-03: retention, size cap, no re-trim on every append
        string dt = Fresh("s4-trim");
        string ft = Path.Combine(dt, "usage-history.csv");
        StringBuilder big = new StringBuilder();
        for (int i = 0; i < 1000; i++) big.Append((now - 100 * 86400 + i) + ",10," + reset + "," + key + "\r\n");
        for (int i = 0; i < 35000; i++) big.Append((now - 30 * 86400 + i * 60) + "," + (i % 100) + "," + reset + "," + key + "\r\n");
        File.WriteAllText(ft, big.ToString());
        long before = new FileInfo(ft).Length;
        Check("trim fixture: over 1.5 MB", before > 1500000, before.ToString());
        UsageHistory.Trim(ft, now);
        string[] tl = HistLines(dt);
        Check("trim: at most 30,000 rows", tl.Length == 30000, tl.Length.ToString());
        Check("trim: nothing older than 90 days", UsageHistory.Read(ft).All(r => now - r.Time <= 90 * 86400));
        Check("trim: keeps the newest rows", tl.Last().StartsWith((now - 30 * 86400 + 34999 * 60).ToString("F0")));
        long trimmedSize = new FileInfo(ft).Length;
        Check("trim: result well under the 2 MB trigger", trimmedSize < UsageHistory.TrimAt * 0.8, trimmedSize.ToString());
        string firstLine = tl[0];
        for (int i = 0; i < 200; i++) UsageHistory.Append(ft, now + i * 10, 50 + (i % 2), reset, key);
        Check("trim: 200 appends to a full recent file don't re-trim", HistLines(dt)[0] == firstLine && HistLines(dt).Length == 30200);
        Check("trim: over 30,000 rows trims back to 30,000", UsageHistory.Trim(ft, now + 3000) && HistLines(dt).Length == 30000);
        DateTime stamp = File.GetLastWriteTimeUtc(ft);
        await Task.Delay(50);
        Check("trim: a clean file under 2 MB isn't rewritten", !UsageHistory.Trim(ft, now + 3000) && File.GetLastWriteTimeUtc(ft) == stamp);
        StringBuilder huge = new StringBuilder();
        for (int i = 0; i < 60000; i++) huge.Append((now - 60000 + i) + ",20," + reset + "," + key + "\r\n");
        File.WriteAllText(ft, huge.ToString());
        UsageHistory.Append(ft, now + 1, 21, reset, key);
        Check("trim: passing 2 MB on append trims to 30,000", HistLines(dt).Length == 30000 && HistLines(dt).Last().Split(',')[1] == "21", HistLines(dt).Length.ToString());

        // Startup trim runs off the UI thread and leaves a clean file alone
        string ds = Fresh("s4-startup");
        string fs = Path.Combine(ds, "usage-history.csv");
        File.WriteAllText(fs, (now - 200 * 86400) + ",5," + reset + "," + key + "\r\n" + now + ",6," + reset + "," + key + "\r\n");
        WidgetWindow sw = NewWidget(ds, true);
        await WaitHistory(sw);
        Check("startup: old rows trimmed", HistLines(ds).Length == 1 && HistLines(ds)[0].Contains(",6,"));
        sw.Close();

        // R-D-01, R-D-02, R-D-05 through real checks against the fake server
        string srv = Fresh("s4-server");
        File.WriteAllText(Path.Combine(srv, "figure.txt"), "42," + reset.ToString("F0") + ",ok");
        Process server = Process.Start(new ProcessStartInfo(fakeExe, "18769 \"" + srv + "\"") { UseShellExecute = false, CreateNoWindow = true });
        try
        {
            await WaitFor(() => File.Exists(Path.Combine(srv, "ready.txt")), 5000);
            string creds = Path.Combine(root, "creds.json");
            long exp = (long)(DateTime.UtcNow.AddDays(30) - new DateTime(1970, 1, 1)).TotalMilliseconds;
            File.WriteAllText(creds, "{\"claudeAiOauth\":{\"accessToken\":\"fake-test-token\",\"expiresAt\":" + exp + "}}");
            string dc = Fresh("s4-checks");
            WidgetWindow w = new WidgetWindow(dc, account, creds, "http://127.0.0.1:18769/usage", 300, 0, true);
            Func<Task> check = async delegate
            {
                Set(w, "blockedUntil", DateTime.MinValue);
                Set(w, "rejectedStamp", DateTime.MinValue);
                Call(w, "FetchUsage", false);
                await WaitFor(() => !(bool)Get(w, "fetching"), 8000);
                await Task.Delay(100);
                await WaitHistory(w);
            };
            await WaitHistory(w);
            await check();
            string[] hl = HistLines(dc);
            Check("checks: a good check adds a row with the file's figures", hl.Length == 1 && hl[0].Split(',')[1] == "42" && hl[0].EndsWith("," + reset.ToString("F0") + "," + key), string.Join(" | ", hl));
            await check();
            Check("checks: the same figures again add nothing", HistLines(dc).Length == 1);
            File.WriteAllText(Path.Combine(srv, "figure.txt"), "43," + reset.ToString("F0") + ",ok");
            await check();
            Check("checks: a change adds a row", HistLines(dc).Length == 2);
            foreach (string mode in new[] { "500", "429", "badjson", "noseven" })
            {
                File.WriteAllText(Path.Combine(srv, "figure.txt"), "60," + reset.ToString("F0") + "," + mode);
                await check();
                Check("checks: " + mode + " adds no row", HistLines(dc).Length == 2);
            }
            File.WriteAllText(Path.Combine(srv, "figure.txt"), "44," + reset.ToString("F0") + ",ok");
            Call(w, "Refresh"); Call(w, "Refresh");
            Check("checks: 10-second refreshes add no row", HistLines(dc).Length == 2);

            // R-D-02: a failed history write doesn't fail the check
            File.SetAttributes(Path.Combine(dc, "usage-history.csv"), FileAttributes.ReadOnly);
            await check();
            Task<bool> last = w.historyWrite;
            bool wrote = last != null && last.Result;
            string note = ((System.Windows.Controls.TextBlock)Get(w, "checkNote")).Text;
            Check("read-only history: write fails quietly, the check succeeds", !wrote && note.Length == 0 && File.ReadAllText(Path.Combine(dc, "usage.json")).Contains("\"used\":44"), "wrote=" + wrote + " note=" + note);
            File.SetAttributes(Path.Combine(dc, "usage-history.csv"), FileAttributes.Normal);
            w.Close();

            // R-D-05: the row's key comes from the usage file's account, even if the sign-in changed during the check
            string dk = Fresh("s4-key");
            WidgetWindow kw = NewWidget(dk, true);
            File.WriteAllText(Path.Combine(dk, "usage.json"), "{\"account\":\"other@example.com\",\"updated\":5,\"five_hour\":null,\"seven_day\":{\"used\":12,\"resets\":" + reset.ToString("F0") + "}}");
            Call(kw, "AfterSuccessfulCheck");
            await WaitHistory(kw);
            string[] kl = HistLines(dk);
            Check("key: taken from the usage file's account, not the sign-in", kl.Length == 1 && kl[0].EndsWith("," + other), kl.Length == 0 ? "none" : kl[0]);
            File.WriteAllText(Path.Combine(dk, "usage.json"), "{\"account\":\"sample@example.com\",\"updated\":6,\"five_hour\":null,\"seven_day\":{\"used\":13,\"resets\":1.8e12}}");
            Call(kw, "AfterSuccessfulCheck");
            await WaitHistory(kw);
            kl = HistLines(dk);
            Check("history: a bad reset is stored as 0, no crash", kl.Length == 2 && kl[1].Split(',')[2] == "0", kl.Last());
            Check("history: no email in the file", !File.ReadAllText(Path.Combine(dk, "usage-history.csv")).Contains("@"));
            kw.Close();
        }
        finally
        {
            try { server.Kill(); } catch (Exception) { }
        }
    }
}
