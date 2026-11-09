using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
// C3b: the account label comes from the token's profile, not the possibly stale ~/.claude.json block.
// Only a local fake profile address is used; nothing reaches Anthropic.
static partial class H4
{
    const string OwnerProfile = "{\"account\":{\"uuid\":\"x\",\"email\":\"owner@example.com\",\"full_name\":\"Sample\"},\"organization\":{\"uuid\":\"y\",\"organization_type\":\"claude_max\",\"rate_limit_tier\":\"default_claude_max_20x\"}}";

    const string Amber = "Couldn't verify account";

    static string NoteText(WidgetWindow w) { return ((TextBlock)Get(w, "accountNote")).Text; }
    static string EmailText(WidgetWindow w) { return ((TextBlock)Get(w, "email")).Text; }
    static string PlanText(WidgetWindow w) { return ((TextBlock)Get(w, "planText")).Text; }
    static int Hits(string srv, string path) { string log = Path.Combine(srv, "requests.log"); return File.Exists(log) ? File.ReadAllLines(log).Count(l => l.Contains("\t" + path)) : 0; }

    static async Task Check1(WidgetWindow w)
    {
        Set(w, "blockedUntil", DateTime.MinValue);
        Set(w, "rejectedStamp", DateTime.MinValue);
        Call(w, "FetchUsage", false);
        await WaitFor(() => !(bool)Get(w, "fetching"), 10000);
        await Task.Delay(100);
    }

    static async Task Acct()
    {
        // Parsing
        ProfileClient.Profile pr = ProfileClient.Parse(OwnerProfile);
        Check("parse: email and the 20x tier as Ultra", pr != null && pr.Email == "owner@example.com" && pr.Plan == "Ultra");
        pr = ProfileClient.Parse("{\"account\":{\"email\":\"a@example.com\"},\"organization\":{\"rate_limit_tier\":\"default_claude_max_5x\"}}");
        Check("parse: an unmapped tier leaves the plan to the existing labels", pr != null && pr.Plan == null);
        foreach (string bad in new[] { "{not json", "[]", "{}", "{\"account\":{}}", "{\"account\":{\"email\":5}}", "{\"account\":{\"email\":\"no-at-sign\"}}",
            "{\"account\":{\"email\":\"two@@example.com\"}}", "{\"account\":{\"email\":\"sp ace@example.com\"}}", "{\"account\":{\"email\":\"a@example.com\\u202E\"}}",
            "{\"account\":\"a@example.com\"}", "{\"account\":{\"email\":\"" + new string('a', 300) + "@x.com\"}}", null })
        {
            bool threw = false; ProfileClient.Profile r = null;
            try { r = ProfileClient.Parse(bad); } catch (Exception) { threw = true; }
            Check("parse: malformed reply gives nothing [" + (bad == null ? "null" : bad.Length > 30 ? bad.Substring(0, 30) : bad) + "]", !threw && r == null);
        }

        string srv = Fresh("acct-server");
        double reset = Epoch(DateTime.UtcNow.AddDays(3));
        File.WriteAllText(Path.Combine(srv, "figure.txt"), "40," + reset.ToString("F0") + ",ok");
        File.WriteAllText(Path.Combine(srv, "profile.json"), OwnerProfile);
        Process server = Process.Start(new ProcessStartInfo(fakeExe, TestPorts.Get(18775) + " \"" + srv + "\"") { UseShellExecute = false, CreateNoWindow = true });
        try
        {
            await WaitFor(() => File.Exists(Path.Combine(srv, "ready.txt")), 5000);
            string baseUrl = TestPorts.Base(18775);
            long exp = (long)(DateTime.UtcNow.AddDays(30) - new DateTime(1970, 1, 1)).TotalMilliseconds;

            // Today's state: Claude Code's file has a stale label and plan; the token belongs to the expected account
            string d = Fresh("acct-today");
            string stale = Path.Combine(d, "claude.json");
            File.WriteAllText(stale, "{\"oauthAccount\":{\"emailAddress\":\"stale.label@example.com\",\"planDisplayName\":\"Max\"}}");
            string creds = Path.Combine(d, "creds.json");
            File.WriteAllText(creds, "{\"claudeAiOauth\":{\"accessToken\":\"fake-token-one\",\"expiresAt\":" + exp + ",\"subscriptionType\":\"max\"}}");
            File.WriteAllText(Path.Combine(d, "expected-account.txt"), "owner@example.com");
            string staleBefore = Stamp(stale), credsBefore = Stamp(creds);
            WidgetWindow w = new WidgetWindow(d, stale, creds, baseUrl + "/usage", 300, 0, true);
            w.ProfileUrl = baseUrl + "/profile";
            Call(w, "Refresh");
            Check("today, before the profile check: tiers differ, so the amber note, not the danger warning", EmailText(w) == "stale.label@example.com" && NoteText(w) == Amber, NoteText(w));
            await Check1(w);
            Check("today: the token's email is shown", EmailText(w) == "owner@example.com", EmailText(w));
            Check("today: a mild note, not the wrong-account warning", NoteText(w) == "Signed in (verified)", NoteText(w));
            Check("today: the plan comes from the token's tier", PlanText(w) == "Ultra", PlanText(w));
            Check("profile request: sent with the token, once", Hits(srv, "/profile") == 1 && File.ReadAllLines(Path.Combine(srv, "requests.log")).Last(l => l.Contains("/profile")).EndsWith("\tauth"));
            await Check1(w);
            Check("profile request: not repeated on the next usage check with the same token", Hits(srv, "/profile") == 1 && Hits(srv, "/usage") >= 2);
            Check("never writes Claude Code's files", Stamp(stale) == staleBefore && Stamp(creds) == credsBefore);
            Check("no token in any file the widget wrote", Directory.GetFiles(d).Where(f => f != creds).All(f => !File.ReadAllText(f).Contains("fake-token-one")));

            // Token change: the verified account is dropped at once, then checked again
            File.WriteAllText(creds, "{\"claudeAiOauth\":{\"accessToken\":\"fake-token-two\",\"expiresAt\":" + exp + ",\"subscriptionType\":\"pro\"}}");
            File.SetLastWriteTimeUtc(creds, DateTime.UtcNow.AddSeconds(5));
            Call(w, "Refresh");
            Check("token change: the old verified account is dropped at once", EmailText(w) == "stale.label@example.com" && NoteText(w) == Amber, EmailText(w) + " / " + NoteText(w));
            File.WriteAllText(Path.Combine(srv, "profile.json"), "{\"account\":{\"email\":\"someone.else@example.com\"},\"organization\":{\"rate_limit_tier\":\"default_claude_pro\"}}");
            await Check1(w);
            // A real mismatch: the token belongs to another account than the expected one
            Check("real mismatch: the danger warning shows for a verified other account", EmailText(w) == "someone.else@example.com" && NoteText(w) == "Warning: not your expected account", NoteText(w));
            Check("real mismatch: the plan isn't the stale file's", PlanText(w) == "Pro", PlanText(w));
            Check("token change: the profile was asked again", Hits(srv, "/profile") == 2);
            w.Close();

            // The call failing, in three ways: falls back to the file, unverified, never the danger warning
            foreach (string mode in new[] { "500", "401", "redirect" })
            {
                string fd = Fresh("acct-fail");
                File.WriteAllText(Path.Combine(fd, "claude.json"), "{\"oauthAccount\":{\"emailAddress\":\"stale.label@example.com\",\"planDisplayName\":\"Max\"}}");
                File.WriteAllText(Path.Combine(fd, "creds.json"), "{\"claudeAiOauth\":{\"accessToken\":\"fake-token-one\",\"expiresAt\":" + exp + "}}");
                File.WriteAllText(Path.Combine(fd, "expected-account.txt"), "owner@example.com");
                File.WriteAllText(Path.Combine(srv, "profile-mode.txt"), mode);
                WidgetWindow fw = new WidgetWindow(fd, Path.Combine(fd, "claude.json"), Path.Combine(fd, "creds.json"), baseUrl + "/usage", 300, 0, true);
                fw.ProfileUrl = baseUrl + "/profile";
                await Check1(fw);
                Check("profile " + mode + ": the file's label, marked unverified, no danger", EmailText(fw) == "stale.label@example.com" && NoteText(fw) == Amber && PlanText(fw) == "Max", NoteText(fw));
                fw.Close();
            }
            File.Delete(Path.Combine(srv, "profile-mode.txt"));
            File.WriteAllText(Path.Combine(srv, "profile.json"), "<html>oops</html>");
            string md = Fresh("acct-malformed");
            File.WriteAllText(Path.Combine(md, "claude.json"), "{\"oauthAccount\":{\"emailAddress\":\"owner@example.com\"}}");
            File.WriteAllText(Path.Combine(md, "creds.json"), "{\"claudeAiOauth\":{\"accessToken\":\"t\",\"expiresAt\":" + exp + "}}");
            WidgetWindow mw = new WidgetWindow(md, Path.Combine(md, "claude.json"), Path.Combine(md, "creds.json"), baseUrl + "/usage", 300, 0, true);
            mw.ProfileUrl = baseUrl + "/profile";
            await Check1(mw);
            Check("malformed reply: no crash, the file's label with the normal note", EmailText(mw) == "owner@example.com" && NoteText(mw) == "Signed in to Claude Code", NoteText(mw));
            mw.Close();

            // Verified and matching both the file and the expected account: the plain note
            File.WriteAllText(Path.Combine(srv, "profile.json"), OwnerProfile);
            md = Fresh("acct-match");
            File.WriteAllText(Path.Combine(md, "claude.json"), "{\"oauthAccount\":{\"emailAddress\":\"Owner@Example.com\"}}");
            File.WriteAllText(Path.Combine(md, "creds.json"), "{\"claudeAiOauth\":{\"accessToken\":\"t\",\"expiresAt\":" + exp + "}}");
            File.WriteAllText(Path.Combine(md, "expected-account.txt"), "owner@example.com");
            mw = new WidgetWindow(md, Path.Combine(md, "claude.json"), Path.Combine(md, "creds.json"), baseUrl + "/usage", 300, 0, true);
            mw.ProfileUrl = baseUrl + "/profile";
            await Check1(mw);
            Check("all agree: the plain note", NoteText(mw) == "Signed in to Claude Code" && EmailText(mw) == "owner@example.com", NoteText(mw));
            mw.Close();

            // C2: no profile request after a failed usage check (a rejected token, a rate limit)
            foreach (string mode in new[] { "401", "429" })
            {
                string cd = Fresh("acct-c2");
                File.WriteAllText(Path.Combine(cd, "claude.json"), "{\"oauthAccount\":{\"emailAddress\":\"stale.label@example.com\"}}");
                File.WriteAllText(Path.Combine(cd, "creds.json"), "{\"claudeAiOauth\":{\"accessToken\":\"t\",\"expiresAt\":" + exp + "}}");
                File.WriteAllText(Path.Combine(srv, "figure.txt"), "40," + reset.ToString("F0") + "," + mode);
                int profilesBefore = Hits(srv, "/profile"), usageBefore = Hits(srv, "/usage");
                WidgetWindow cw = new WidgetWindow(cd, Path.Combine(cd, "claude.json"), Path.Combine(cd, "creds.json"), baseUrl + "/usage", 300, 0, true);
                cw.ProfileUrl = baseUrl + "/profile";
                await Check1(cw);
                Check("C2: usage " + mode + " sends no profile request", Hits(srv, "/usage") == usageBefore + 1 && Hits(srv, "/profile") == profilesBefore);
                cw.Close();
            }
            File.WriteAllText(Path.Combine(srv, "figure.txt"), "40," + reset.ToString("F0") + ",ok");

            // C1 ruling: unverified, file email isn't the expected one; the tiers decide red or amber
            File.WriteAllText(Path.Combine(srv, "profile-mode.txt"), "500");
            string[,] tierCases = {
                { "default_claude_max_20x", "default_claude_max_20x", "red" },
                { "default_claude_max_5x", "default_claude_max_20x", "amber" },
                { "", "default_claude_max_20x", "amber" },
                { "default_claude_max_20x", "", "amber" },
                { "", "", "amber" },
            };
            for (int i = 0; i < tierCases.GetLength(0); i++)
            {
                string td = Fresh("acct-tier");
                string fileTier = tierCases[i, 0], tokenTier = tierCases[i, 1];
                File.WriteAllText(Path.Combine(td, "claude.json"), "{\"oauthAccount\":{\"emailAddress\":\"intruder@example.com\"" + (fileTier.Length > 0 ? ",\"organizationRateLimitTier\":\"" + fileTier + "\"" : "") + "}}");
                File.WriteAllText(Path.Combine(td, "creds.json"), "{\"claudeAiOauth\":{\"accessToken\":\"t\",\"expiresAt\":" + exp + (tokenTier.Length > 0 ? ",\"rateLimitTier\":\"" + tokenTier + "\"" : "") + "}}");
                File.WriteAllText(Path.Combine(td, "expected-account.txt"), "owner@example.com");
                WidgetWindow tw = new WidgetWindow(td, Path.Combine(td, "claude.json"), Path.Combine(td, "creds.json"), baseUrl + "/usage", 300, 0, true);
                tw.ProfileUrl = baseUrl + "/profile";
                await Check1(tw);
                bool red = NoteText(tw) == "Warning: not your expected account";
                bool amberShown = NoteText(tw) == Amber && ((System.Windows.Controls.Border)Get(tw, "miniBadge")).Visibility == System.Windows.Visibility.Visible;
                string label = "file tier '" + fileTier + "', token tier '" + tokenTier + "'";
                Check("C1: unverified, " + label + ": " + tierCases[i, 2], tierCases[i, 2] == "red" ? red : amberShown, NoteText(tw));
                tw.Close();
            }
            File.Delete(Path.Combine(srv, "profile-mode.txt"));

            // C1 ruling: a token change triggers an early check, within the manual gap rule
            string ed = Fresh("acct-early");
            File.WriteAllText(Path.Combine(ed, "claude.json"), "{\"oauthAccount\":{\"emailAddress\":\"stale.label@example.com\"}}");
            string ec = Path.Combine(ed, "creds.json");
            File.WriteAllText(ec, "{\"claudeAiOauth\":{\"accessToken\":\"t1\",\"expiresAt\":" + exp + "}}");
            File.WriteAllText(Path.Combine(ed, "expected-account.txt"), "owner@example.com");
            WidgetWindow ew = new WidgetWindow(ed, Path.Combine(ed, "claude.json"), ec, baseUrl + "/usage", 300, 30, true);
            ew.ProfileUrl = baseUrl + "/profile";
            await Check1(ew);
            Check("early: the first check verifies", EmailText(ew) == "owner@example.com");
            int u0 = Hits(srv, "/usage"), p0 = Hits(srv, "/profile");
            Call(ew, "CheckTokenChange");
            await Task.Delay(300);
            Check("early: no token change, no extra check", Hits(srv, "/usage") == u0);
            File.WriteAllText(ec, "{\"claudeAiOauth\":{\"accessToken\":\"t2\",\"expiresAt\":" + exp + "}}");
            File.SetLastWriteTimeUtc(ec, DateTime.UtcNow.AddSeconds(3));
            Call(ew, "CheckTokenChange");
            await Task.Delay(300);
            Check("early: within the 30 s manual gap it waits", Hits(srv, "/usage") == u0);
            Set(ew, "lastAttempt", DateTime.UtcNow.AddSeconds(-31));
            Call(ew, "CheckTokenChange");
            await WaitFor(() => !(bool)Get(ew, "fetching") && Hits(srv, "/profile") > p0, 8000);
            await Task.Delay(200);
            Check("early: after the gap, the token change triggers a usage and profile check", Hits(srv, "/usage") == u0 + 1 && Hits(srv, "/profile") == p0 + 1);
            Check("early: verified again after the early check", EmailText(ew) == "owner@example.com" && NoteText(ew) != Amber, NoteText(ew));
            Call(ew, "CheckTokenChange");
            await Task.Delay(300);
            Check("early: no repeat once the new token is checked", Hits(srv, "/usage") == u0 + 1);
            ew.Close();

            // Program test mode derives the profile address from the fake usage address
            if (exe != null && File.Exists(exe))
            {
                string pd = Fresh("acct-proc");
                File.WriteAllText(Path.Combine(pd, "settings.txt"), "-6000,0,0,0,default,system,0,1,off");
                string pc = Path.Combine(pd, "creds.json");
                File.WriteAllText(pc, "{\"claudeAiOauth\":{\"accessToken\":\"t\",\"expiresAt\":" + exp + "}}");
                int before = Hits(srv, "/profile");
                Process p = StartWidget(pd, account, pc, baseUrl + "/usage", "300");
                bool asked = await WaitFor(() => Hits(srv, "/profile") > before, 10000);
                Check("exe test mode: asks the fake profile address, never Anthropic", asked);
                p.Kill(); p.WaitForExit(5000);
            }
        }
        finally
        {
            try { server.Kill(); } catch (Exception) { }
        }
    }
}
