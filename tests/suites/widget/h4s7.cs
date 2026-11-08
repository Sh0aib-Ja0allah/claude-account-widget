using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
// S7: the update notice. Requests go only to the local fake server; no browser is opened.
static partial class H4
{
    const string RelBase = "https://github.com/Sh0aib-Ja0allah/claude-account-widget/releases/tag/";

    static string Release(string tag, bool draft, bool pre, string link = null)
    {
        return "{\"tag_name\":\"" + tag + "\",\"draft\":" + (draft ? "true" : "false") + ",\"prerelease\":" + (pre ? "true" : "false")
            + ",\"name\":\"<script>ignored</script>\",\"body\":\"ignored\",\"html_url\":\"" + (link ?? RelBase + tag) + "\"}";
    }

    static int Requests(string srv)
    {
        string log = Path.Combine(srv, "requests.log");
        return File.Exists(log) ? File.ReadAllLines(log).Count(l => l.Contains("/releases")) : 0;
    }

    static async Task S7()
    {
        // R-F-02: SemVer order
        string[] order = { "0.1.0", "0.2.0-alpha", "0.2.0-alpha.1", "0.2.0-alpha.beta", "0.2.0-beta", "0.2.0-beta.2", "0.2.0-beta.11", "0.2.0-rc.1", "0.2.0", "v0.2.1", "0.10.0", "1.0.0" };
        bool sorted = true;
        for (int i = 0; i + 1 < order.Length; i++)
            if (VersionTag.Compare(VersionTag.Parse(order[i]), VersionTag.Parse(order[i + 1])) >= 0) { sorted = false; Log("order broken at " + order[i]); }
        Check("tags: SemVer order holds", sorted);
        Check("tags: build data ignored, v prefix accepted", VersionTag.Compare(VersionTag.Parse("v1.2.3+build.7"), VersionTag.Parse("1.2.3")) == 0 && VersionTag.Parse("v1.2.3+b").Text == "1.2.3");
        foreach (string bad in new[] { "", "1.0", "1.0.0.0", "1.0.0-", "x1.0.0", "1.0.0-a..b", "1234567890.0.0", "v1.0.0-" + new string('a', 30), "1.0.0 ", "1.0.0-<b>" })
            Check("tags: refused [" + (bad.Length > 20 ? bad.Substring(0, 20) + "..." : bad) + "]", VersionTag.Parse(bad) == null);

        // R-F-06: links
        Check("links: a release tag page is accepted", UpdateClient.IsReleaseLink(RelBase + "v0.2.0"));
        foreach (string bad in new[] {
            "http://github.com/Sh0aib-Ja0allah/claude-account-widget/releases/tag/v0.2.0",
            "https://github.com.evil.example/Sh0aib-Ja0allah/claude-account-widget/releases/tag/v0.2.0",
            "https://evil.example/Sh0aib-Ja0allah/claude-account-widget/releases/tag/v0.2.0",
            "https://github.com/Sh0aib-Ja0allah/claude-account-widget/releases/../../../evil/repo",
            "https://github.com/Sh0aib-Ja0allah/claude-account-widget/releases/%2e%2e/%2e%2e/evil",
            "https://github.com/Sh0aib-Ja0allah/claude-account-widget/releases/tag/v1?x=1",
            "https://github.com/Sh0aib-Ja0allah/claude-account-widget/releases/tag/v1#x",
            "https://github.com:8443/Sh0aib-Ja0allah/claude-account-widget/releases/tag/v1",
            "https://user@github.com/Sh0aib-Ja0allah/claude-account-widget/releases/tag/v1",
            "https://github.com/someone-else/claude-account-widget/releases/tag/v1",
            "https://github.com/Sh0aib-Ja0allah/claude-account-widget/issues/1",
            "https://github.com\\Sh0aib-Ja0allah/claude-account-widget/releases/tag/v1",
            "javascript:alert(1)", "file:///C:/Windows/System32/calc.exe", "",
            "https://github.com/Sh0aib-Ja0allah/claude-account-widget/releases/tag/v1\u202E" })
            Check("links: refused [" + (bad.Length > 60 ? bad.Substring(0, 60) + "..." : bad) + "]", !UpdateClient.IsReleaseLink(bad));

        // R-F-01: the release list
        string json = "[" + Release("v0.3.0", true, false) + "," + Release("v0.2.0-beta.1", false, true) + "," + Release("v0.1.0", false, true) + "," + Release("bogus", false, false)
            + "," + Release("v0.9.0", false, false, "https://evil.example/x") + "]";
        string[] n = UpdateClient.Newest(json, "0.1.0");
        Check("list: drafts ignored, prereleases included, bad tags and links skipped", n != null && n[0] == "v0.2.0-beta.1" && n[1] == RelBase + "v0.2.0-beta.1", n == null ? "null" : n[0]);
        Check("list: nothing newer gives null", UpdateClient.Newest("[" + Release("v0.1.0", false, true) + "]", "0.1.0") == null);
        foreach (string bad in new[] { "{not json", "{}", "null", "[1,2,3]", "[{\"tag_name\":5}]", "[" + new string('[', 100) + new string(']', 100) + "]", "" })
        {
            bool threw = false; string[] r = null;
            try { r = UpdateClient.Newest(bad, "0.1.0"); } catch (Exception) { threw = true; }
            Check("list: odd reply [" + (bad.Length > 20 ? bad.Substring(0, 20) : bad) + "] gives null, no crash", !threw && r == null);
        }

        // Through the fake server
        string srv = Fresh("s7-server");
        File.WriteAllText(Path.Combine(srv, "figure.txt"), "40,0,ok");
        File.WriteAllText(Path.Combine(srv, "releases.json"), "[" + Release("v9.2.0", false, false) + "," + Release("v0.0.1", false, true) + "]");
        Process server = Process.Start(new ProcessStartInfo(fakeExe, "18771 \"" + srv + "\"") { UseShellExecute = false, CreateNoWindow = true });
        try
        {
            await WaitFor(() => File.Exists(Path.Combine(srv, "ready.txt")), 5000);
            string url = "http://127.0.0.1:18771/releases?per_page=5";

            // R-F-04: no Authorization, no token read
            string body = UpdateClient.Download(url);
            string req = File.ReadAllText(Path.Combine(srv, "requests.log"));
            Check("request: list read", body != null && body.Contains("v9.2.0"));
            Check("request: no Authorization header, versioned User-Agent", req.Contains("\t/releases?per_page=5\tClaudeAccountWidget/" + Program.AppVersion + "\tnoauth"), req);

            // R-F-03: failures mean null and no crash
            foreach (string mode in new[] { "403", "big" })
            {
                File.WriteAllText(Path.Combine(srv, "releases-mode.txt"), mode);
                Check("request: " + mode + " gives null", UpdateClient.Download(url) == null);
            }
            File.WriteAllText(Path.Combine(srv, "releases-mode.txt"), "slow");
            Stopwatch sw = Stopwatch.StartNew();
            string slow = await Task.Run(() => UpdateClient.Download(url));
            Check("request: a slow server times out with null", slow == null && sw.Elapsed.TotalSeconds < 19, sw.Elapsed.TotalSeconds.ToString("F1") + " s");
            File.Delete(Path.Combine(srv, "releases-mode.txt"));
            File.Move(Path.Combine(srv, "releases.json"), Path.Combine(srv, "releases.hold"));
            Check("request: 404 gives null", UpdateClient.Download(url) == null);
            File.Move(Path.Combine(srv, "releases.hold"), Path.Combine(srv, "releases.json"));
            Check("request: a non-list address gives null via Newest", UpdateClient.Newest(UpdateClient.Download("http://127.0.0.1:18771/usage"), "0.1.0") == null);

            // R-F-05, R-F-08, R-F-10: on the card
            string d = Fresh("s7-card");
            WidgetWindow w = NewWidget(d, true);
            w.ReleasesUrl = url;
            List<string> opened = new List<string>();
            w.OpenLink = delegate(string link) { opened.Add(link); };
            w.ShowActivated = false; w.Show();
            int before = Requests(srv);
            Task t = w.CheckForUpdate();
            if (t != null) await t;
            DockPanel line = (DockPanel)Get(w, "updateLine");
            TextBlock text = (TextBlock)Get(w, "updateText");
            Check("card: a newer release shows one line", line.Visibility == Visibility.Visible && text.Text == "Version 9.2.0 is available", text.Text);
            Check("card: one request made", Requests(srv) == before + 1);
            Check("card: a second check the same day sends nothing", w.CheckForUpdate() == null && Requests(srv) == before + 1);
            text.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            Check("card: clicking opens the release link", opened.Count == 1 && opened[0] == RelBase + "v9.2.0");
            w.Close();

            WidgetWindow w2 = NewWidget(d, true);
            w2.ReleasesUrl = url; w2.ShowActivated = false; w2.Show();
            await Task.Delay(100);
            Check("restart: no request within the day, and the notice is back", w2.CheckForUpdate() == null && Requests(srv) == before + 1
                && ((DockPanel)Get(w2, "updateLine")).Visibility == Visibility.Visible);
            Call(w2, "DismissUpdate");
            Check("dismiss: hidden and remembered", ((DockPanel)Get(w2, "updateLine")).Visibility == Visibility.Collapsed && File.ReadAllText(Path.Combine(d, "update-check.txt")).Contains("dismissed=v9.2.0"));
            w2.Close();
            UpdateState st = UpdateState.Load(Path.Combine(d, "update-check.txt"));
            st.Last = 1; st.Save(Path.Combine(d, "update-check.txt"));
            File.WriteAllText(Path.Combine(srv, "releases.json"), "[" + Release("v9.2.0", false, false) + "]");
            WidgetWindow w3 = NewWidget(d, true);
            w3.ReleasesUrl = url; w3.ShowActivated = false; w3.Show();
            t = w3.CheckForUpdate(); if (t != null) await t;
            Check("dismiss: the same version stays hidden after a new check", ((DockPanel)Get(w3, "updateLine")).Visibility == Visibility.Collapsed && Requests(srv) == before + 2);
            st = UpdateState.Load(Path.Combine(d, "update-check.txt")); st.Last = 1; st.Save(Path.Combine(d, "update-check.txt"));
            File.WriteAllText(Path.Combine(srv, "releases.json"), "[" + Release("v9.3.0-rc.1", false, true) + "]");
            t = w3.CheckForUpdate(); if (t != null) await t;
            TextBlock text3 = (TextBlock)Get(w3, "updateText");
            Check("dismiss: a newer version shows again", ((DockPanel)Get(w3, "updateLine")).Visibility == Visibility.Visible && text3.Text == "Version 9.3.0-rc.1 is available", text3.Text);

            // R-F-10: the longest tag fits in both themes
            foreach (string theme in new[] { "dark", "light" })
            {
                ThemeOf(w3, theme);
                text3.Text = "Version " + new string('9', 9) + "." + new string('9', 9) + "." + new string('9', 9) + "-rc.1 is available";
                w3.UpdateLayout();
                FrameworkElement column = (FrameworkElement)VisualTreeHelper.GetParent((DependencyObject)Get(w3, "updateLine"));
                DockPanel l3 = (DockPanel)Get(w3, "updateLine");
                Border dismiss = l3.Children.OfType<Border>().First();
                Check("fit " + theme + ": the line and dismiss icon stay inside the card column", l3.ActualWidth <= column.ActualWidth + 0.5
                    && dismiss.TranslatePoint(new Point(dismiss.ActualWidth, 0), column).X <= column.ActualWidth + 8, l3.ActualWidth.ToString("F1") + " of " + column.ActualWidth.ToString("F1"));
                ShotOf((FrameworkElement)Get(w3, "cardBorder"), "update-" + theme);
            }
            ThemeOf(w3, "dark");
            w3.Close();

            // A failed attempt counts: offline means the next try is a day later
            string fd = Fresh("s7-fail");
            File.Move(Path.Combine(srv, "releases.json"), Path.Combine(srv, "releases.hold"));
            WidgetWindow wf = NewWidget(fd, true);
            wf.ReleasesUrl = url;
            int b2 = Requests(srv);
            t = wf.CheckForUpdate(); if (t != null) await t;
            Check("failed: the attempt is saved and counts for the day", Requests(srv) == b2 + 1 && wf.CheckForUpdate() == null && UpdateState.Load(Path.Combine(fd, "update-check.txt")).Last > 0);
            File.Move(Path.Combine(srv, "releases.hold"), Path.Combine(srv, "releases.json"));
            st = UpdateState.Load(Path.Combine(fd, "update-check.txt")); st.Last = Epoch(DateTime.UtcNow) + 86400 * 30; st.Save(Path.Combine(fd, "update-check.txt"));
            t = wf.CheckForUpdate(); if (t != null) await t;
            Check("future: a last attempt in the future counts as due", Requests(srv) == b2 + 2);
            wf.Close();

            // R-F-09: off means no request
            string od = Fresh("s7-off");
            WidgetWindow wo = NewWidget(od, true);
            wo.ReleasesUrl = url;
            SettingsWindow swin = new SettingsWindow(wo, od);
            Border track = (Border)Get(swin, "updateSwitch");
            track.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            int b3 = Requests(srv);
            Check("off: the Settings switch turns it off and saves field 7", !wo.updateCheckOn && File.ReadAllText(Path.Combine(od, "settings.txt")).Split(',')[7] == "0");
            Check("off: no request", wo.CheckForUpdate() == null && Requests(srv) == b3 && !File.Exists(Path.Combine(od, "update-check.txt")));
            swin.Close(); wo.Close();
            WidgetWindow nourl = NewWidget(Fresh("s7-nourl"), true);
            Check("tests: no address means no request", nourl.ReleasesUrl == null && nourl.CheckForUpdate() == null);
            nourl.Close();

            // R-F-07: a damaged or tampered file shows nothing and can't open a bad link
            string td = Fresh("s7-tamper");
            Dictionary<string, string> tamper = new Dictionary<string, string>
            {
                { "evil link", "last=1\r\ntag=v9.9.9\r\nlink=https://evil.example/x\r\n" },
                { "bad tag", "last=1\r\ntag=v9.9.9<script>\r\nlink=" + RelBase + "v9.9.9\r\n" },
                { "link to another repo", "last=1\r\ntag=v9.9.9\r\nlink=https://github.com/x/y/releases/tag/v9.9.9\r\n" },
                { "junk", "\0\0\0garbage\r\n=\r\nlast=abc\r\n" },
                { "huge", new string('x', 10000) },
            };
            foreach (KeyValuePair<string, string> tp in tamper)
            {
                File.WriteAllText(Path.Combine(td, "update-check.txt"), tp.Value);
                WidgetWindow wt = NewWidget(td, true);
                List<string> op = new List<string>();
                wt.OpenLink = delegate(string link) { op.Add(link); };
                wt.ShowActivated = false; wt.Show();
                await Task.Delay(50);
                Call(wt, "OpenUpdateLink");
                Check("tampered (" + tp.Key + "): nothing shown or opened", ((DockPanel)Get(wt, "updateLine")).Visibility == Visibility.Collapsed && op.Count == 0);
                wt.Close();
            }
            File.WriteAllText(Path.Combine(td, "update-check.txt"), "last=1\r\ntag=v9.9.9\r\nlink=" + RelBase + "v9.9.9\r\n");
            WidgetWindow wc = NewWidget(td, true);
            List<string> opc = new List<string>();
            wc.OpenLink = delegate(string link) { opc.Add(link); };
            wc.ShowActivated = false; wc.Show();
            await Task.Delay(50);
            File.WriteAllText(Path.Combine(td, "update-check.txt"), "last=1\r\ntag=v9.9.9\r\nlink=" + RelBase + "v9.9.8\r\n");
            Call(wc, "OpenUpdateLink");
            Check("tampered after showing: a changed link isn't opened on click", opc.Count == 0);
            wc.Close();
        }
        finally
        {
            try { server.Kill(); } catch (Exception) { }
        }
    }
}
