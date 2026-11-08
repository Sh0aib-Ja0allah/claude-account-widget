using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
// Fixes from the S7 review: S7-1 (a tag ending in a newline) and S7-2 (a server dripping bytes).
static partial class H4
{
    static async Task Fixes5()
    {
        foreach (string bad in new[] { "v0.2.0\n", "1.0.0\n", "1.0.0\r\n", "1.0.0-rc.1\n", "1.0.0+b\n" })
            Check("S7-1: tag with a trailing newline refused [" + bad.Replace("\r", "\\r").Replace("\n", "\\n") + "]", VersionTag.Parse(bad) == null);
        Check("S7-1: the same tag without it still parses", VersionTag.Parse("v0.2.0") != null && VersionTag.Parse("1.0.0-rc.1") != null);
        Check("S7-1: a release list with a newline tag offers nothing", UpdateClient.Newest("[{\"tag_name\":\"v0.2.0\\n\",\"draft\":false,\"html_url\":\"https://github.com/Sh0aib-Ja0allah/claude-account-widget/releases/tag/v0.2.0\"}]", "0.1.0") == null);

        // S7-2: a server that sends one byte a second for 45 s is cut off at the deadline
        string srv = Fresh("fix5-server");
        File.WriteAllText(Path.Combine(srv, "figure.txt"), "40,0,ok");
        File.WriteAllText(Path.Combine(srv, "releases-mode.txt"), "drip");
        Process server = Process.Start(new ProcessStartInfo(fakeExe, "18773 \"" + srv + "\"") { UseShellExecute = false, CreateNoWindow = true });
        try
        {
            await WaitFor(() => File.Exists(Path.Combine(srv, "ready.txt")), 5000);
            TimeSpan saved = UpdateClient.Deadline;
            UpdateClient.Deadline = TimeSpan.FromSeconds(6);
            Stopwatch sw = Stopwatch.StartNew();
            string got = await Task.Run(() => UpdateClient.Download("http://127.0.0.1:18773/releases"));
            double took = sw.Elapsed.TotalSeconds;
            UpdateClient.Deadline = saved;
            Check("S7-2: a dripping server is cut off at the total deadline (6 s here)", got == null && took >= 5 && took < 10, took.ToString("F1") + " s");
            Check("S7-2: the real deadline is 30 s", UpdateClient.Deadline == TimeSpan.FromSeconds(30));
            sw = Stopwatch.StartNew();
            got = await Task.Run(() => UpdateClient.Download("http://127.0.0.1:18773/releases"));
            took = sw.Elapsed.TotalSeconds;
            Check("S7-2: with the real 30 s deadline it gives up by about 30 s, not 45", got == null && took >= 28 && took < 36, took.ToString("F1") + " s");
        }
        finally
        {
            try { server.Kill(); } catch (Exception) { }
        }
    }
}
