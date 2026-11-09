using System;
using System.Linq;
using System.Threading.Tasks;
// Release links, strict: every odd link from Worker A's S7 review must be refused, with no exceptions.
static partial class H4
{
    static async Task Fixes6()
    {
        string rel = "https://github.com/Sh0aib-Ja0allah/claude-account-widget/releases/tag/";
        string[] good = { rel + "v0.2.0", rel + "v0.2.0-rc.1", rel + "v1.0.0+build.7", "https://GITHUB.com/Sh0aib-Ja0allah/claude-account-widget/releases/tag/v0.2.0",
            "https://github.com:443/Sh0aib-Ja0allah/claude-account-widget/releases/tag/v0.2.0", "https://github.com/Sh0aib-Ja0allah/claude-account-widget/releases/latest" };
        foreach (string g in good) Check("strict links: accepted [" + g.Substring(8) + "]", UpdateClient.IsReleaseLink(g));
        string[] bad = {
            " " + rel + "v1", rel + "v1 ", rel + "v1 x", rel + "v1\t", "\t" + rel + "v1", rel + "v1\n", rel + "v1\u00A0", "\u00A0" + rel + "v1",
            rel + "v1;x", rel + "v1;", rel + "v1/./../../x", rel + "./v1", rel + "v1/.", rel + ".hidden",
            "https://github.com//Sh0aib-Ja0allah/claude-account-widget/releases/tag/v1", rel + "/v1", rel.Replace("/tag/", "//tag/") + "v1",
            "https://github.com/Sh0aib-Ja0allah/claude-account-widget/releases", "https://github.com/Sh0aib-Ja0allah/claude-account-widget/releases/",
            "https://github.com/Sh0aib-Ja0allah/claude-account-widget-evil/releases/tag/v1", "https://github.com/Sh0aib-Ja0allah/claude-account-widget/releasesx/v1",
            "https://github.com.evil.com/Sh0aib-Ja0allah/claude-account-widget/releases/tag/v1", "https://github.com./Sh0aib-Ja0allah/claude-account-widget/releases/tag/v1",
            "https://git\thub.com/Sh0aib-Ja0allah/claude-account-widget/releases/tag/v1", "http://github.com/Sh0aib-Ja0allah/claude-account-widget/releases/tag/v1",
            "https://xn--gthub-n4a.com/Sh0aib-Ja0allah/claude-account-widget/releases/tag/v1", "https://g\u0456thub.com/Sh0aib-Ja0allah/claude-account-widget/releases/tag/v1",
            rel + "v1\u202E", rel + "v1%20x", rel + "v1?x", rel + "v1#x", "https://github.com:8443/Sh0aib-Ja0allah/claude-account-widget/releases/tag/v1",
            "https://user@github.com/Sh0aib-Ja0allah/claude-account-widget/releases/tag/v1", rel + "v1'", rel + "v1\"", rel + "v1<x>", rel + "v1,x", rel + "v1!",
        };
        string[] passed = bad.Where(UpdateClient.IsReleaseLink).ToArray();
        Check("strict links: every odd link refused, no exceptions (" + bad.Length + " cases)", passed.Length == 0,
            string.Join(" | ", passed.Select(p => p.Replace("\t", "\\t").Replace("\n", "\\n").Replace("\u00A0", "\\u00A0"))));
        Check("strict links: a list whose only link has a leading space offers nothing",
            UpdateClient.Newest("[{\"tag_name\":\"v0.2.0\",\"draft\":false,\"html_url\":\" " + rel + "v0.2.0\"}]", "0.1.0") == null);
        await Task.Delay(1);
    }
}
