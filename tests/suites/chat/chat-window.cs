using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// Tests the chat features with a fake claude. Every window stays off screen.
static class H3
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static string root, data, hist, desk, fakeDir, realPath;
    static WidgetWindow widget;
    static int fails;
    static JavaScriptSerializer json = new JavaScriptSerializer();

    static object Get(object o, string f) { return o.GetType().GetField(f, Any).GetValue(o); }
    static void Set(object o, string f, object v) { o.GetType().GetField(f, Any).SetValue(o, v); }
    static object Call(object o, string m, params object[] a)
    {
        MethodInfo info = o.GetType().GetMethods(Any).First(x => x.Name == m && x.GetParameters().Length == a.Length);
        return info.Invoke(o, a);
    }
    static void Log(string s) { Console.WriteLine(s); Console.Out.Flush(); }
    static void Check(string what, bool ok, string info = "")
    {
        Log((ok ? "PASS " : "FAIL ") + what + (info.Length > 0 ? " [" + info.Replace("\n", "\\n") + "]" : ""));
        if (!ok) fails++;
    }
    static void Note(string s) { Log("NOTE " + s); }
    static void Theme(string theme) { widget.GetType().GetField("themeSetting", Any).SetValue(widget, theme); Call(widget, "ApplyTheme"); Call(widget, "ApplyAccent"); }
    static void Shot(FrameworkElement e, string name)
    {
        e.UpdateLayout();
        RenderTargetBitmap bmp = new RenderTargetBitmap((int)e.ActualWidth, (int)e.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(e);
        PngBitmapEncoder png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using (FileStream f = File.Create(Path.Combine(root, name + ".png"))) png.Save(f);
        Log("SHOT " + Path.Combine(root, name + ".png"));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);
    [STAThread]
    static void Main(string[] args)
    {
        root = args[1];
        data = Path.Combine(root, "data");
        hist = Path.Combine(root, "hist");
        desk = Path.Combine(root, "desk");
        fakeDir = args[2];
        foreach (string d in new[] { data, hist, desk }) { if (Directory.Exists(d)) Directory.Delete(d, true); Directory.CreateDirectory(d); }
        File.WriteAllText(Path.Combine(data, "chat-history-folder.txt"), hist);
        string account = Path.Combine(root, "account.json");
        File.WriteAllText(account, "{\"oauthAccount\":{\"emailAddress\":\"sample@example.com\",\"planDisplayName\":\"Ultra\"}}");
        realPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", fakeDir + ";" + realPath);
        Environment.SetEnvironmentVariable("FAKE_LOG", Path.Combine(root, "fake-log.txt"));
        typeof(ChatWindow).GetField("desktopFolder", Any).SetValue(null, desk);

        Application app = new Application();
        app.ShutdownMode = ShutdownMode.OnMainWindowClose;
        if (args[0] == "crash") SetErrorMode(0x0002);
        else app.DispatcherUnhandledException += (s, e) => { Log("UNHANDLED " + e.Exception); fails++; e.Handled = true; };
        widget = new WidgetWindow(data, account, null, null, 300, 0);
        widget.ShowActivated = false;
        widget.WindowStartupLocation = WindowStartupLocation.Manual;
        widget.Left = -6000;
        widget.Loaded += async delegate
        {
            widget.Left = -6000;
            try { await Run(args[0]); }
            catch (Exception e) { Log("HARNESS ERROR " + e); fails++; }
            Log("fails=" + fails);
            widget.Close();
        };
        app.Run(widget);
        System.Diagnostics.Process.GetCurrentProcess().Kill();
    }

    static async Task Run(string mode)
    {
        if (mode == "crash")
        {
            ChatWindow cw = NewWindow();
            await Task.Delay(200);
            string poison = "See [https://a.example/b](https://a.example/b) for more.";
            Script(Sys, Result(poison));
            await SendNow(cw, "poison");
            Log("SURVIVED");
            return;
        }
        // No mode makes a real Claude request: the old "real" mode was removed when the tests moved into the repo.
        string[] sections = { "format", "live", "regen", "edit", "search", "instructions", "start" };
        foreach (string section in sections)
        {
            if (mode != "all" && mode != section) continue;
            Log("=== " + section);
            try
            {
                if (section == "format") await Format();
                if (section == "live") await Live();
                if (section == "regen") await Regen();
                if (section == "edit") await Edit();
                if (section == "search") await Search();
                if (section == "instructions") await Instructions();
                if (section == "start") await StartOffThread();
            }
            catch (Exception e) { Log("FAIL section " + section + " threw: " + e); fails++; }
        }
    }

    // ---------- helpers ----------
    static ChatWindow NewWindow(double width = 396, double height = 536, string dataFolder = null)
    {
        ChatWindow w = new ChatWindow(dataFolder ?? data);
        w.ShowActivated = false;
        w.Left = -6000; w.Top = 50; w.Width = width; w.Height = height;
        w.Show();
        return w;
    }
    static string Q(string s) { return json.Serialize(s); }
    static string Start(string type, string extra = "") { return "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"" + type + "\"" + extra + "}}}"; }
    static string Delta(string t) { return "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":" + Q(t) + "}}}"; }
    static string Result(string t) { return "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":" + Q(t) + "}"; }
    static string ToolUse(string name, string input) { return "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"" + name + "\",\"input\":" + input + "}]}}"; }
    const string Sys = "{\"type\":\"system\",\"subtype\":\"init\",\"model\":\"fake-model-1\"}";
    static int scriptNo;
    static void Script(params string[] lines)
    {
        string path = Path.Combine(root, "script" + (++scriptNo) + ".jsonl");
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
        Environment.SetEnvironmentVariable("FAKE_SCRIPT", path);
    }
    static void ReplyScript(string text, int ms = 0)
    {
        List<string> l = new List<string> { Sys, Start("text"), Delta(text) };
        if (ms > 0) l.Add("#sleep " + ms);
        l.Add(Result(text));
        Script(l.ToArray());
    }
    static void FailScript(int exit = 1) { Script(Sys, Start("text"), Delta("partial words"), "#sleep 400", "#stderr boom something broke", "#exit " + exit); }
    static void HoldScript() { Script(Sys, Start("text"), Delta("partial words"), "#wait"); }
    static bool Busy(ChatWindow w) { return (bool)Get(w, "busy"); }
    static async Task Idle(ChatWindow w, int ms = 30000)
    {
        for (int i = 0; i < ms / 25 && Busy(w); i++) await Task.Delay(25);
        if (Busy(w)) Log("FAIL timed out waiting for request to end");
        await Task.Delay(30);
    }
    static async Task SendNow(ChatWindow w, string text)
    {
        ((TextBox)Get(w, "input")).Text = text;
        Call(w, "Send");
        await Idle(w);
    }
    static async Task Exchange(ChatWindow w, string q, string a) { ReplyScript(a); await SendNow(w, q); }
    static List<ChatTurn> Turns(ChatWindow w) { return (List<ChatTurn>)Get(w, "turns"); }
    static StackPanel Messages(ChatWindow w) { return (StackPanel)Get(w, "messages"); }
    static List<Border> Bubbles(ChatWindow w) { return (List<Border>)Get(w, "bubbles"); }
    static Dictionary<Border, string> ReplyTexts(ChatWindow w) { return (Dictionary<Border, string>)Get(w, "replyTexts"); }
    static TextBox Input(ChatWindow w) { return (TextBox)Get(w, "input"); }
    static string Status(ChatWindow w) { return ((TextBlock)Get(w, "statusText")).Text; }
    static bool StatusVisible(ChatWindow w) { return ((Border)Get(w, "statusRow")).Visibility == Visibility.Visible; }
    static bool RetryVisible(ChatWindow w) { return ((Border)Get(w, "retryButton")).Visibility == Visibility.Visible && StatusVisible(w); }
    static List<ChatAttachment> Pending(ChatWindow w) { return (List<ChatAttachment>)Get(w, "pending"); }
    static List<ChatAttachment> SentList(ChatWindow w) { return (List<ChatAttachment>)Get(w, "sent"); }
    static string NotePath(ChatWindow w) { object n = Get(w, "noteWriter"); return n == null ? null : ((ChatNoteWriter)n).NotePath; }
    static string BannerText(ChatWindow w) { return ((TextBlock)Get(w, "editBannerText")).Text; }
    static bool BannerVisible(ChatWindow w) { return ((Border)Get(w, "editBanner")).Visibility == Visibility.Visible; }
    static void Press(ChatWindow w, UIElement target, Key key)
    {
        KeyEventArgs e = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(w), 0, key);
        e.RoutedEvent = Keyboard.KeyDownEvent;
        target.RaiseEvent(e);
    }
    static void Esc(ChatWindow w)
    {
        KeyEventArgs e = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(w), 0, Key.Escape);
        e.RoutedEvent = Keyboard.PreviewKeyDownEvent;
        Call(w, "OnPreviewKey", w, e);
    }
    static IEnumerable<ChatTurn> Replies(ChatWindow w) { return Turns(w).Where(t => !t.FromUser); }
    static string TextOf(FlowDocument d) { return new TextRange(d.ContentStart, d.ContentEnd).Text; }
    static string ReplyShown(Border bubble) { return TextOf(((RichTextBox)bubble.Child).Document); }
    static ChatAttachment Att(string name, AttachmentKind kind = AttachmentKind.Text)
    {
        ChatAttachment a = new ChatAttachment();
        a.Kind = kind; a.Name = name; a.FullPath = Path.Combine(root, name); a.Text = "contents of " + name;
        if (kind == AttachmentKind.Pdf || kind == AttachmentKind.Image) { a.SendBytes = new byte[] { 1, 2, 3, 4 }; a.MediaType = kind == AttachmentKind.Pdf ? "application/pdf" : "image/png"; a.Text = null; }
        return a;
    }
    static List<string> FakeStdin()
    {
        string log = Path.Combine(root, "fake-log.txt");
        if (!File.Exists(log)) return new List<string>();
        return File.ReadAllLines(log, new UTF8Encoding(false)).Where(l => l.StartsWith("STDIN ")).ToList();
    }
    static string Border2Edit(Border bubble)
    {
        return "";
    }
    // The edit button sits beside a user's bubble.
    static Border EditButton(ChatTurn turn)
    {
        DependencyObject first = turn.First;
        StackPanel row = first as StackPanel;
        return row != null && row.Orientation == Orientation.Horizontal ? row.Children[0] as Border : null;
    }

    // ---------- 1. formatted replies ----------
    static IEnumerable<object> Walk(object node)
    {
        yield return node;
        IEnumerable children = null;
        if (node is FlowDocument) children = ((FlowDocument)node).Blocks;
        else if (node is Paragraph) children = ((Paragraph)node).Inlines;
        else if (node is Span) children = ((Span)node).Inlines;
        else if (node is List) children = ((List)node).ListItems;
        else if (node is ListItem) children = ((ListItem)node).Blocks;
        else if (node is Table) children = ((Table)node).RowGroups;
        else if (node is TableRowGroup) children = ((TableRowGroup)node).Rows;
        else if (node is TableRow) children = ((TableRow)node).Cells;
        else if (node is TableCell) children = ((TableCell)node).Blocks;
        if (children == null) yield break;
        foreach (object child in children) foreach (object x in Walk(child)) yield return x;
    }
    static FlowDocument Doc(ChatWindow w, Border bubble) { return ((RichTextBox)bubble.Child).Document; }

    // Renders one reply and reports a thrown error as a failed check instead of stopping.
    static FlowDocument Try(ChatWindow w, string what, string text)
    {
        try
        {
            Border b = (Border)Call(w, "AddReplyBubble", text);
            return Doc(w, b);
        }
        catch (TargetInvocationException e)
        {
            Check("render: " + what, false, "THREW " + e.InnerException.GetType().Name + ": " + e.InnerException.Message);
            return null;
        }
    }
    static List<Hyperlink> Links(FlowDocument d) { return Walk(d).OfType<Hyperlink>().ToList(); }

    const string Sample =
        "# Heading one\n\n## Heading two\n\n### Heading three\n\nA paragraph with **bold**, *italic*, ~~gone~~, `inline code` and a [Markdown link](https://example.com/page) plus a bare https://example.org/path. Line two stays on its own line.\nSecond line.\n\n" +
        "- First\n- Second\n  - Nested one\n  - Nested two\n    - Deeper\n- Third\n\n1. Step one\n2. Step two\n   1. Sub a\n   2. Sub b\n3. Step three\n\n" +
        "> A quote that runs on for a little while so that it wraps in the small window and shows its edge.\n\n" +
        "| Name | Count | Notes on the row |\n|---|---:|---|\n| Alpha | 12 | Short |\n| Beta **bold** | 3400 | A longer note that has to wrap in a narrow bubble |\n\n" +
        "```python\ndef hello(name):\n    # a comment\n    return f\"hello {name} and this line is deliberately long so that it must scroll inside the code block\"\n```\n\n" +
        "---\n\n![a chart](https://example.com/chart.png)\n\nLast line.";
    const string ArabicSample =
        "# \u0639\u0646\u0648\u0627\u0646 \u0631\u0626\u064a\u0633\u064a\n\n\u0647\u0630\u0647 \u0641\u0642\u0631\u0629 \u0639\u0631\u0628\u064a\u0629 \u0641\u064a\u0647\u0627 **\u062e\u0637 \u0639\u0631\u064a\u0636** \u0648\u0643\u0644\u0645\u0629 English \u0641\u064a \u0627\u0644\u0648\u0633\u0637.\n\n" +
        "- \u0627\u0644\u0639\u0646\u0635\u0631 \u0627\u0644\u0623\u0648\u0644\n- \u0627\u0644\u0639\u0646\u0635\u0631 \u0627\u0644\u062b\u0627\u0646\u064a\n  - \u0639\u0646\u0635\u0631 \u0645\u062a\u062f\u0627\u062e\u0644\n\n1. \u062e\u0637\u0648\u0629 \u0623\u0648\u0644\u0649\n2. \u062e\u0637\u0648\u0629 \u062b\u0627\u0646\u064a\u0629\n\n" +
        "> \u0627\u0642\u062a\u0628\u0627\u0633 \u0642\u0635\u064a\u0631\n\n| \u0627\u0644\u0627\u0633\u0645 | \u0627\u0644\u0639\u062f\u062f |\n|---|---|\n| \u0623\u062d\u0645\u062f | 12 |\n\n```python\nprint('code stays left to right')\n```\n";

    static async Task Format()
    {
        ChatWindow w = NewWindow();
        await Task.Delay(300);
        FlowDocument d = Try(w, "sample", Sample);
        Check("sample renders", d != null);
        Check("three heading sizes", Walk(d).OfType<Paragraph>().Where(p => p.FontWeight == FontWeights.SemiBold).Select(p => p.FontSize).Distinct().Count() >= 3);
        Check("bold", Walk(d).OfType<Bold>().Any());
        Check("italic", Walk(d).OfType<Italic>().Any());
        List<List> lists = Walk(d).OfType<List>().ToList();
        Check("nested lists", lists.Count == 5, lists.Count + " lists");
        Check("numbered markers", lists.Any(l => l.MarkerStyle == TextMarkerStyle.Decimal));
        Check("table", Walk(d).OfType<Table>().Count() == 1);
        Check("table cells", Walk(d).OfType<TableCell>().Count() == 9, Walk(d).OfType<TableCell>().Count().ToString());
        Check("code block is a UI block", Walk(d).OfType<BlockUIContainer>().Count() == 1);
        Check("bare and md links work", Links(d).Count == 2, string.Join(",", Links(d).Select(l => (string)l.ToolTip)));
        Check("image stays text", TextOf(d).Contains("[picture: a chart]") && !Walk(d).OfType<InlineUIContainer>().Any());
        Check("line break kept", Walk(d).OfType<LineBreak>().Any());

        // links that must not work
        foreach (string[] c in new[] {
            new[] { "js md", "[click](javascript:alert(1))" }, new[] { "js bare", "javascript:alert(1)" }, new[] { "file md", "[x](file:///C:/Windows/notepad.exe)" },
            new[] { "file bare", "file:///C:/Windows/notepad.exe" }, new[] { "exe path", "[x](C:\\Windows\\notepad.exe)" }, new[] { "unc", "[x](\\\\server\\share\\a.exe)" },
            new[] { "ms protocol", "[x](ms-settings:privacy)" }, new[] { "data", "[x](data:text/html,<b>x</b>)" }, new[] { "userinfo", "[x](https://good.com@evil.com/)" },
            new[] { "userinfo bare", "https://good.com@evil.com/" }, new[] { "angle js", "<javascript:alert(1)>" }, new[] { "relative", "[x](/etc/passwd)" }, new[] { "empty", "[x]()" },
            new[] { "spaced js", "[x]( javascript:alert(1) )" }, new[] { "upper JS", "[x](JAVASCRIPT:alert(1))" }, new[] { "tab js", "[x](java\tscript:alert(1))" } })
        {
            FlowDocument l = Try(w, c[0], c[1]);
            if (l != null) Check("no hyperlink: " + c[0], Links(l).Count == 0, TextOf(l).Trim());
        }
        // links that should work, and their real address
        foreach (string[] c in new[] {
            new[] { "https", "[x](https://a.example/b)", "https://a.example/b" }, new[] { "title", "[x](https://a.example/b \"Title\")", "https://a.example/b" },
            new[] { "angle", "<https://a.example/b>", "https://a.example/b" }, new[] { "bare trailing dot", "See https://a.example/b.", "https://a.example/b" },
            new[] { "bare in parens", "(see https://a.example/b)", "https://a.example/b" }, new[] { "parens in md url", "[x](https://en.wikipedia.org/wiki/A_(b))", "https://en.wikipedia.org/wiki/A_(b)" },
            new[] { "parens in bare url", "https://en.wikipedia.org/wiki/A_(b)", "https://en.wikipedia.org/wiki/A_(b)" }, new[] { "inside bold", "**[x](https://a.example/b)**", "https://a.example/b" },
            new[] { "url as link text", "[https://a.example/b](https://a.example/b)", "https://a.example/b" }, new[] { "url text differs", "[https://shown.example](https://real.example/)", "https://real.example/" } })
        {
            FlowDocument l = Try(w, c[0], c[1]);
            if (l == null) continue;
            List<Hyperlink> found = Links(l);
            Check("link: " + c[0], found.Count == 1 && (string)found[0].ToolTip == c[2], string.Join(",", found.Select(x => (string)x.ToolTip)) + " | " + TextOf(l).Trim());
        }

        // odd markdown must not throw
        foreach (string[] c in new[] {
            new[] { "unclosed fence", "text\n\n```python\nprint(1)\nmore" }, new[] { "unclosed tilde", "~~~\ncode" }, new[] { "fence only", "```" }, new[] { "empty code", "```\n```" },
            new[] { "unclosed bold", "**never closed" }, new[] { "partial link", "[text](https://exa" }, new[] { "partial table", "| a | b |\n|--" }, new[] { "table only head", "| a | b |\n|---|---|" },
            new[] { "image in link", "[![alt](https://a/b.png)](https://a/)" }, new[] { "image no alt", "![](x.png)" }, new[] { "deep list", string.Join("\n", Enumerable.Range(0, 30).Select(i => new string(' ', i * 2) + "- item" + i)) },
            new[] { "deep bold", new string('*', 200) + "x" + new string('*', 200) }, new[] { "only hashes", "#" }, new[] { "only spaces", "   \n  " }, new[] { "crlf", "# a\r\n\r\n- b\r\n- c\r\n" }, new[] { "hr only", "---" },
            new[] { "nested emphasis", "***both*** **_x_** _**y**_" }, new[] { "quote nested", "> > deep" }, new[] { "table in list", "- a\n  | b | c |\n  |---|---|\n  | 1 | 2 |" }, new[] { "code in list", "1. a\n   ```js\n   x\n   ```\n2. b" }, new[] { "table no pipes rule", "a | b\n---\nc | d" },
            new[] { "html tags", "<script>alert(1)</script> <b>x</b>" }, new[] { "very long line", new string('x', 25000) }, new[] { "long table", "| a |\n|---|\n" + string.Join("\n", Enumerable.Range(0, 500).Select(i => "| r" + i + " |")) } })
        {
            FlowDocument l = Try(w, c[0], c[1]);
            if (l != null) Check("no crash: " + c[0], true);
        }
        Check("unclosed fence shows the code", Walk(Try(w, "uf", "```python\nprint(1)")).OfType<BlockUIContainer>().Count() == 1);
        Check("html stays literal", TextOf(Try(w, "html", "<script>alert(1)</script>")).Contains("<script>"));

        // numbering and structure Claude commonly writes
        FlowDocument steps = Try(w, "split numbers", "1. **One**\n\n   Explanation of one.\n\n2. **Two**\n\n   Explanation of two.\n\n3. **Three**");
        List<List> numbered = Walk(steps).OfType<List>().ToList();
        Check("numbering survives text between items", numbered.Count == 1 || numbered.Select(l => l.StartIndex).SequenceEqual(new[] { 1, 2, 3 }), "lists=" + numbered.Count + " starts=" + string.Join(",", numbered.Select(l => l.StartIndex)));
        FlowDocument later = Try(w, "start at 5", "5. Five\n6. Six");
        Check("list keeps its first number", Walk(later).OfType<List>().First().StartIndex == 5, "StartIndex=" + Walk(later).OfType<List>().First().StartIndex);
        FlowDocument tableAfter = Try(w, "table after text", "Here's the table:\n| a | b |\n|---|---|\n| 1 | 2 |");
        Check("table right after a line of text", Walk(tableAfter).OfType<Table>().Any(), TextOf(tableAfter).Trim());
        FlowDocument escaped = Try(w, "escaped pipe", "| a | b |\n|---|---|\n| x \\| y | `p|q` |");
        Check("escaped pipe stays in its cell", Walk(escaped).OfType<TableCell>().Count() == 4 && TextOf(escaped).Contains("x | y"), TextOf(escaped).Trim());
        FlowDocument leading = Try(w, "code blanks", "```\n\n\nfirst\n```");
        Note("leading blank code lines: " + Json(((BlockUIContainer)Walk(leading).OfType<BlockUIContainer>().First()).Child is Border ? "rendered" : "?"));
        FlowDocument innerFence = Try(w, "inner fence", "````md\n```python\nx\n```\n````\n\nafter");
        Check("longer fence holds a shorter one", Walk(innerFence).OfType<BlockUIContainer>().Count() == 1 && TextOf(innerFence).Contains("after"), Walk(innerFence).OfType<BlockUIContainer>().Count() + " blocks");
        FlowDocument infoFence = Try(w, "info closes", "```\nline\n```python\nstill code\n```\n\nafter");
        Check("a fence with a language doesn't close a block", Walk(infoFence).OfType<BlockUIContainer>().Count() == 1, Walk(infoFence).OfType<BlockUIContainer>().Count() + " blocks");

        await Retest(w);
        // Arabic
        FlowDocument ar = Try(w, "arabic", ArabicSample);
        Check("arabic heading RTL", Walk(ar).OfType<Paragraph>().First().FlowDirection == FlowDirection.RightToLeft);
        Check("arabic paragraph RTL", Walk(ar).OfType<Paragraph>().Skip(1).First().FlowDirection == FlowDirection.RightToLeft);
        Check("arabic table RTL", Walk(ar).OfType<Table>().First().FlowDirection == FlowDirection.RightToLeft);
        List arList = Walk(ar).OfType<List>().First();
        Paragraph arItem = (Paragraph)arList.ListItems.First().Blocks.First();
        Check("arabic list RTL", arList.FlowDirection == FlowDirection.RightToLeft || arItem.FlowDirection == FlowDirection.RightToLeft, "list=" + arList.FlowDirection + " item=" + arItem.FlowDirection);
        Border code = (Border)Walk(ar).OfType<BlockUIContainer>().First().Child;
        Check("arabic chat: code stays LTR", code.FlowDirection == FlowDirection.LeftToRight);
        w.Close();

        // bubble widths
        foreach (double[] size in new[] { new double[] { 396, 536 }, new double[] { 320, 380 }, new double[] { 1920, 1040 } })
        {
            ChatWindow s = NewWindow(size[0], size[1]);
            await Task.Delay(200);
            ScrollViewer scroller = (ScrollViewer)Get(s, "scroller");
            Border tiny = (Border)Call(s, "AddReplyBubble", "Hello there, friend.");
            Border wide = (Border)Call(s, "AddReplyBubble", Sample);
            Border longUrl = (Border)Call(s, "AddReplyBubble", "https://example.com/" + new string('a', 300));
            Border heading = (Border)Call(s, "AddReplyBubble", "# Short heading");
            Border bold = (Border)Call(s, "AddReplyBubble", "**A short bold line**");
            Border inlineCode = (Border)Call(s, "AddReplyBubble", "`iiiiiiiiiiiiiiiiiiii`");
            Border realCode = (Border)Call(s, "AddReplyBubble", "Run `kubectl get pods --all-namespaces` now.");
            Border arab = (Border)Call(s, "AddReplyBubble", "\u0647\u0630\u0647 \u0641\u0642\u0631\u0629 \u0642\u0635\u064a\u0631\u0629");
            await Task.Delay(300);
            s.UpdateLayout();
            string tag = (int)size[0] + "x" + (int)size[1];
            double view = scroller.ViewportWidth;
            Check(tag + " sample fits viewport", wide.ActualWidth <= view + 0.5, "bubble=" + wide.ActualWidth.ToString("0") + " view=" + view.ToString("0"));
            Check(tag + " long url fits viewport", longUrl.ActualWidth <= view + 0.5, "bubble=" + longUrl.ActualWidth.ToString("0") + " view=" + view.ToString("0"));
            Check(tag + " short reply on one line", tiny.ActualHeight < 45, "h=" + tiny.ActualHeight.ToString("0") + " w=" + tiny.ActualWidth.ToString("0"));
            Check(tag + " short reply small bubble", tiny.ActualWidth < 220, "w=" + tiny.ActualWidth.ToString("0"));
            Check(tag + " heading on one line", heading.ActualHeight < 62, "h=" + heading.ActualHeight.ToString("0") + " w=" + heading.ActualWidth.ToString("0"));
            Check(tag + " bold on one line", bold.ActualHeight < 45, "h=" + bold.ActualHeight.ToString("0") + " w=" + bold.ActualWidth.ToString("0"));
            Check(tag + " inline code on one line", inlineCode.ActualHeight < 45, "h=" + inlineCode.ActualHeight.ToString("0") + " w=" + inlineCode.ActualWidth.ToString("0"));
            if (size[0] > 500) Check(tag + " sentence with inline code on one line", realCode.ActualHeight < 45, "h=" + realCode.ActualHeight.ToString("0") + " w=" + realCode.ActualWidth.ToString("0"));
            Check(tag + " arabic short on one line", arab.ActualHeight < 45, "h=" + arab.ActualHeight.ToString("0") + " w=" + arab.ActualWidth.ToString("0"));
            Check(tag + " no horizontal page scroll", scroller.ExtentWidth <= view + 0.5, "extent=" + scroller.ExtentWidth.ToString("0") + " view=" + view.ToString("0"));
            s.Close();
        }

        // screenshots with both themes and the theme switch
        foreach (double[] size in new[] { new double[] { 396, 900 }, new double[] { 1920, 1400 } })
        {
            ChatWindow s = NewWindow(size[0], size[1]);
            await Task.Delay(200);
            string tag = size[0] < 500 ? "small" : "large";
            Theme("dark");
            Border a = (Border)Call(s, "AddReplyBubble", Sample);
            await Task.Delay(400);
            Shot((FrameworkElement)s.Content, "fmt-" + tag + "-dark");
            Brush before = ((FlowDocument)Doc(s, a)).Foreground;
            RichTextBox box = (RichTextBox)a.Child;
            FlowDocument oldDoc = box.Document;
            Theme("light");
            await Task.Delay(400);
            Check(tag + " theme switch redraws reply", !ReferenceEquals(box.Document, oldDoc));
            Color fg = ((SolidColorBrush)box.Document.Foreground).Color;
            Check(tag + " light text is dark", fg.R < 128, fg.ToString());
            Shot((FrameworkElement)s.Content, "fmt-" + tag + "-light");
            s.Close();

            ChatWindow r = NewWindow(size[0], size[1]);
            await Task.Delay(200);
            Theme("light");
            Call(r, "AddReplyBubble", ArabicSample);
            await Task.Delay(400);
            Shot((FrameworkElement)r.Content, "fmt-arabic-" + tag + "-light");
            Theme("dark");
            await Task.Delay(400);
            Shot((FrameworkElement)r.Content, "fmt-arabic-" + tag + "-dark");
            r.Close();
        }
    }
    static string Json(string s) { return s; }

    // Text inside a chat code block.
    static string CodeOf(BlockUIContainer block)
    {
        StackPanel stack = (StackPanel)((Border)block.Child).Child;
        return TextOf(((RichTextBox)stack.Children[1]).Document).TrimEnd();
    }
    static FlowDocument PreviewDoc(string text)
    {
        return DocumentBuilder.Markdown(text, new TextColors(false, false));
    }
    static string NoEnds(FlowDocument d) { return TextOf(d).Replace("\r\n", "\n").Trim(); }

    static async Task Retest(ChatWindow w)
    {
        // numbered lists
        foreach (string[] c in new[] {
            new[] { "start 0", "0. zero\n1. one", "" }, new[] { "start 5", "5. five\n6. six", "5" }, new[] { "int max", "2147483647. big\n2147483648. bigger", "" },
            new[] { "past int", "99999999999999999999. huge\n2. two", "" }, new[] { "leading zeros", "007. seven\n008. eight", "7" }, new[] { "paren", "3) three\n4) four", "3" },
            new[] { "negative looks like text", "-1. minus", "" } })
        {
            FlowDocument d = Try(w, "number " + c[0], c[1]);
            try { PreviewDoc(c[1]); } catch (Exception e) { Check("preview number " + c[0], false, "THREW " + e.Message); }
            if (d == null) continue;
            List first = Walk(d).OfType<List>().FirstOrDefault();
            Log("  number " + c[0] + ": lists=" + Walk(d).OfType<List>().Count() + " starts=" + string.Join(",", Walk(d).OfType<List>().Select(l => l.StartIndex)) + " text=" + NoEnds(d).Replace("\n", "|"));
            Check("number no throw: " + c[0], true);
            if (c[2].Length > 0) Check("number start: " + c[0], first != null && first.StartIndex.ToString() == c[2], first == null ? "no list" : first.StartIndex.ToString());
        }
        FlowDocument zero = Try(w, "zero shown", "0. zero\n1. one");
        Check("start 0: the words stay", zero != null && NoEnds(zero).Contains("zero") && NoEnds(zero).Contains("one"));
        FlowDocument huge = Try(w, "huge shown", "99999999999999999999. huge\n2. two");
        Check("huge: the words stay", huge != null && NoEnds(huge).Contains("huge") && NoEnds(huge).Contains("two"));
        FlowDocument gap = Try(w, "nested keeps count", "1. One\n   - sub\n2. Two\n\n   Text.\n\n3. Three\n   1. inner\n   2. inner two\n4. Four");
        Log("  nested+text: starts=" + string.Join(",", Walk(gap).OfType<List>().Where(l => l.MarkerStyle == TextMarkerStyle.Decimal).Select(l => l.StartIndex)));
        FlowDocument bulletGap = Try(w, "bullets then numbers", "- a\n\nText\n\n1. one\n\nText\n\n2. two");
        Check("numbers after bullets keep count", Walk(bulletGap).OfType<List>().Where(l => l.MarkerStyle == TextMarkerStyle.Decimal).Select(l => l.StartIndex).SequenceEqual(new[] { 1, 2 }), string.Join(",", Walk(bulletGap).OfType<List>().Select(l => l.StartIndex)));

        // tables
        FlowDocument t1 = Try(w, "table after text", "Here's the table:\n| a | b |\n|---|---|\n| 1 | 2 |\n\nAfter.");
        Check("table after text: table", Walk(t1).OfType<Table>().Count() == 1 && Walk(t1).OfType<TableCell>().Count() == 4, Walk(t1).OfType<TableCell>().Count() + " cells");
        Check("table after text: lead kept, no raw pipes", NoEnds(t1).StartsWith("Here's the table:") && !NoEnds(t1).Contains("|") && NoEnds(t1).EndsWith("After."), NoEnds(t1));
        FlowDocument t2 = Try(w, "two text lines then table", "One\nTwo\n| a |\n|---|\n| 1 |");
        Check("table after two lines", Walk(t2).OfType<Table>().Count() == 1 && NoEnds(t2).Contains("One") && NoEnds(t2).Contains("Two"), NoEnds(t2));
        FlowDocument t3 = Try(w, "escaped pipe", "| a | b |\n|---|---|\n| x \\| y | z |");
        List<string> cells3 = Walk(t3).OfType<TableCell>().Select(c => new TextRange(c.ContentStart, c.ContentEnd).Text.Trim()).ToList();
        Check("escaped pipe stays in its cell", cells3.Count == 4 && cells3[2] == "x | y" && cells3[3] == "z", string.Join(" ; ", cells3));
        FlowDocument t4 = Try(w, "pipe in code", "| a | b |\n|---|---|\n| `p|q` | z |");
        List<string> cells4 = Walk(t4).OfType<TableCell>().Select(c => new TextRange(c.ContentStart, c.ContentEnd).Text.Trim()).ToList();
        Check("pipe in inline code stays in its cell", cells4.Count == 4 && cells4[2] == "p|q" && cells4[3] == "z", string.Join(" ; ", cells4));
        FlowDocument t5 = Try(w, "unbalanced backtick cell", "| a | b |\n|---|---|\n| `p | q |");
        Check("lone backtick doesn't swallow a cell", Walk(t5).OfType<TableCell>().Count() == 4, string.Join(" ; ", Walk(t5).OfType<TableCell>().Select(c => new TextRange(c.ContentStart, c.ContentEnd).Text.Trim())));
        FlowDocument t6 = Try(w, "escaped backslash in cell", "| a | b |\n|---|---|\n| c:\\\\ | z |");
        Check("double backslash then pipe still splits", Walk(t6).OfType<TableCell>().Count() == 4, string.Join(" ; ", Walk(t6).OfType<TableCell>().Select(c => new TextRange(c.ContentStart, c.ContentEnd).Text.Trim())));
        foreach (string[] c in new[] {
            new[] { "pipe in a sentence", "Use a | b to join them." }, new[] { "shell pipe", "Run cat a | grep b\nthen stop." }, new[] { "pipes on two lines", "a | b\nc | d" },
            new[] { "pipe then a word rule", "a | b\nnot---a rule" }, new[] { "mismatched rule", "a | b | c\n|---|" }, new[] { "code span pipe", "Try `a | b` here." }, new[] { "or in logic", "if x || y then z" },
            new[] { "pipe then rule of dashes", "a | b\n---\nc" } })
        {
            FlowDocument d = Try(w, c[0], c[1]);
            bool noTable = d != null && !Walk(d).OfType<Table>().Any();
            string plain = c[1].Replace("`", "");
            bool same = d != null && (c[0] == "pipe then rule of dashes" ? NoEnds(d).Contains("a | b") : NoEnds(d) == plain);
            Check("not a table: " + c[0], noTable && same, d == null ? "" : NoEnds(d));
        }

        // real tables, in chat and in the file preview
        foreach (string[] c in new[] {
            new[] { "outer pipes", "| a | b |\n|---|---|\n| 1 | 2 |", "4" }, new[] { "no outer pipes", "a | b\n---|---\n1 | 2", "4" }, new[] { "leading pipe only", "| a | b\n|---|---\n| 1 | 2", "4" },
            new[] { "trailing pipe only", "a | b |\n---|---|\n1 | 2 |", "4" }, new[] { "aligned", "| a | b | c |\n|:--|:-:|--:|\n| 1 | 2 | 3 |", "6" }, new[] { "spaced rule", "| a | b |\n| --- | --- |\n| 1 | 2 |", "4" },
            new[] { "one column", "| a |\n|---|\n| 1 |", "2" }, new[] { "one column head only", "| a |\n|---|", "1" }, new[] { "one column no closing", "| a\n|---\n| 1", "2" },
            new[] { "pipe in code header", "| `p|q` | b |\n|---|---|\n| 1 | 2 |", "4" }, new[] { "escaped pipe header", "| x \\| y | b |\n|---|---|\n| 1 | 2 |", "4" } })
        {
            FlowDocument d = Try(w, c[0], c[1]);
            int chatCells = d == null ? -1 : Walk(d).OfType<TableCell>().Count();
            Check("table: " + c[0], d != null && Walk(d).OfType<Table>().Count() == 1 && chatCells.ToString() == c[2], chatCells + " cells; " + (d == null ? "" : NoEnds(d)));
            int previewCells = Walk(PreviewDoc(c[1])).OfType<TableCell>().Count();
            Check("preview table: " + c[0], previewCells.ToString() == c[2], previewCells + " cells");
        }

        // fences
        FlowDocument f1 = Try(w, "language fence inside block", "```\nline\n```python\nstill code\n```\n\nafter");
        Check("fence: language fence stays inside", Walk(f1).OfType<BlockUIContainer>().Count() == 1 && CodeOf(Walk(f1).OfType<BlockUIContainer>().First()).Contains("```python") && NoEnds(f1).EndsWith("after"), Walk(f1).OfType<BlockUIContainer>().Count() + " blocks");
        FlowDocument f2 = Try(w, "4 holds 3", "````md\n```python\nx\n```\n````\n\nafter");
        BlockUIContainer f2b = Walk(f2).OfType<BlockUIContainer>().First();
        Check("fence: 4 holds 3", Walk(f2).OfType<BlockUIContainer>().Count() == 1 && CodeOf(f2b).Replace("\r\n", "\n") == "```python\nx\n```" && NoEnds(f2).EndsWith("after"), CodeOf(f2b).Replace("\r\n", "|"));
        FlowDocument f3 = Try(w, "longer closes shorter", "```\ncode\n`````\n\nafter");
        Check("fence: a longer fence closes", Walk(f3).OfType<BlockUIContainer>().Count() == 1 && NoEnds(f3).EndsWith("after"), Walk(f3).OfType<BlockUIContainer>().Count().ToString());
        FlowDocument f4 = Try(w, "tilde in backticks", "```\n~~~\nx\n```\n\nafter");
        Check("fence: other kind stays inside", CodeOf(Walk(f4).OfType<BlockUIContainer>().First()).Contains("~~~") && NoEnds(f4).EndsWith("after"));
        FlowDocument f5 = Try(w, "closing with spaces", "```\ncode\n```   \n\nafter");
        Check("fence: trailing spaces still close", NoEnds(f5).EndsWith("after") && !CodeOf(Walk(f5).OfType<BlockUIContainer>().First()).Contains("after"));
        FlowDocument f6 = Try(w, "unclosed keeps all", "```js\na\n\nb");
        Check("fence: unclosed keeps blank lines", CodeOf(Walk(f6).OfType<BlockUIContainer>().First()).Replace("\r\n", "\n") == "a\n\nb", CodeOf(Walk(f6).OfType<BlockUIContainer>().First()).Replace("\r\n", "|"));
        FlowDocument f7 = Try(w, "shorter inside 4 unclosed", "````\n```\nx");
        Check("fence: unclosed 4 holds 3", Walk(f7).OfType<BlockUIContainer>().Count() == 1);

        // shared with the file preview
        FlowDocument p1 = PreviewDoc("Intro\n| a | b |\n|---|---|\n| x \\| y | `p|q` |\n\n```\nl\n```py\nm\n```\n\n3. three\n\n> quote");
        Check("preview doc: table", Walk(p1).OfType<TableCell>().Count() == 4 && NoEnds(p1).Contains("x | y") && NoEnds(p1).Contains("p|q"), NoEnds(p1));
        Check("preview doc: fence holds", NoEnds(p1).Contains("```py") && NoEnds(p1).Contains("quote"));
        Check("preview doc: list start", Walk(p1).OfType<List>().First().StartIndex == 3);
        Check("preview doc: no working links", Links(PreviewDoc("[x](https://a.example/) https://b.example/")).Count == 0);
        Check("preview doc: paragraph joins lines", NoEnds(PreviewDoc("one\ntwo")) == "one two", NoEnds(PreviewDoc("one\ntwo")));

        // the stray bracket
        FlowDocument js = Try(w, "js click", "[click](javascript:alert(1))");
        Check("js link: no stray bracket", NoEnds(js) == "click" && Links(js).Count == 0, NoEnds(js));
        Log("  js link in the file preview shows: " + NoEnds(PreviewDoc("[click](javascript:alert(1))")));
        FlowDocument js3 = Try(w, "js deep", "[click](javascript:alert((1)))");
        Check("js link deep parens: not a link", Links(js3).Count == 0, NoEnds(js3));

        // an Arabic screen for looking at
        ChatWindow a = NewWindow(396, 760);
        await Task.Delay(150);
        Theme("light");
        Call(a, "AddReplyBubble", "\u0627\u0642\u062a\u0628\u0627\u0633:\n\n> \u0647\u0630\u0627 \u0627\u0642\u062a\u0628\u0627\u0633 \u0637\u0648\u064a\u0644 \u0628\u0645\u0627 \u064a\u0643\u0641\u064a \u0644\u064a\u0644\u062a\u0641 \u0639\u0644\u0649 \u0623\u0643\u062b\u0631 \u0645\u0646 \u0633\u0637\u0631 \u0648\u0627\u062d\u062f \u0641\u064a \u0627\u0644\u0646\u0627\u0641\u0630\u0629 \u0627\u0644\u0635\u063a\u064a\u0631\u0629\n\n> An English quote in the same reply\n\n- \u0627\u0644\u0639\u0646\u0635\u0631 \u0627\u0644\u0623\u0648\u0644\n- \u0627\u0644\u0639\u0646\u0635\u0631 \u0627\u0644\u062b\u0627\u0646\u064a\n  - \u0639\u0646\u0635\u0631 \u0645\u062a\u062f\u0627\u062e\u0644\n    1. \u0623\u0639\u0645\u0642\n\n3. \u062e\u0637\u0648\u0629 \u062b\u0627\u0644\u062b\u0629\n4. \u062e\u0637\u0648\u0629 \u0631\u0627\u0628\u0639\u0629\n\n- English first item\n- \u0639\u0646\u0635\u0631 \u0639\u0631\u0628\u064a \u062b\u0627\u0646\u064d\n\n- \u0639\u0631\u0628\u064a \u0623\u0648\u0644\u0627\u064b\n- English second item");
        await Task.Delay(300);
        Shot((FrameworkElement)a.Content, "arabic-list-quote-light");
        Theme("dark");
        await Task.Delay(300);
        Shot((FrameworkElement)a.Content, "arabic-list-quote-dark");
        a.Close();

        // numbered screen
        ChatWindow n = NewWindow(396, 760);
        await Task.Delay(150);
        Call(n, "AddReplyBubble", "0. zero\n1. one\n\n5. five\n6. six\n\n1. **One**\n\n   Explanation of one.\n\n2. **Two**\n\n   Explanation of two.\n\n99999999999999999999. huge\n\n2147483647. big\n\n| a | `p|q` |\n|---|---|\n| x \\| y | z |\n\n```\nline\n```python\nstill code\n```");
        await Task.Delay(300);
        Shot((FrameworkElement)n.Content, "lists-tables-fences");
        n.Close();
    }

    // ---------- 2. live replies ----------
    static async Task Live()
    {
        ChatWindow w = NewWindow();
        await Task.Delay(200);
        Script(Sys, Start("thinking"), "#sleep 300",
            Start("text"), Delta("Let me "), "#sleep 250", Delta("check that **file"), "#sleep 400",
            Start("tool_use", ",\"name\":\"Read\""), ToolUse("Read", "{\"file_path\":\"C:\\\\x\\\\notes.txt\"}"), "#sleep 500",
            ToolUse("WebSearch", "{\"query\":\"cats and dogs\"}"), "#sleep 400",
            Start("text"), Delta("The final "), "#sleep 300", Delta("**answer**"), "#sleep 400",
            Result("The final **answer**"));
        List<string> shown = new List<string>();
        List<string> statuses = new List<string>();
        List<int> liveCounts = new List<int>();
        Input(w).Text = "hello";
        Call(w, "Send");
        while (Busy(w))
        {
            Border live = (Border)Get(w, "liveBubble");
            string now = live == null ? "(none)" : ReplyShown(live).Trim();
            if (shown.Count == 0 || shown[shown.Count - 1] != now) shown.Add(now);
            string st = Status(w);
            int dots = st.IndexOf('\u2026');
            string stName = dots > 0 ? st.Substring(0, dots) : st;
            if (statuses.Count == 0 || statuses[statuses.Count - 1] != stName) statuses.Add(stName);
            await Task.Delay(30);
        }
        await Task.Delay(50);
        Log("  live sequence: " + string.Join(" -> ", shown));
        Log("  status sequence: " + string.Join(" -> ", statuses));
        Check("live: text reaches bubble as it comes", shown.Any(s => s.StartsWith("Let me")));
        Check("live: partial grows", shown.Any(s => s == "Let me") && shown.Any(s => s.Contains("check that")), string.Join("|", shown));
        Check("live: restart removes earlier text", shown.Any(s => s.StartsWith("The final")) && !shown.Any(s => s.Contains("check") && s.Contains("final")));
        Check("live: status shows thinking", statuses.Contains("Thinking"));
        Check("live: status shows reading file", statuses.Contains("Reading notes.txt"));
        Check("live: status shows search", statuses.Contains("Searching the web for cats and dogs"));
        Check("live: status shows writing", statuses.Contains("Writing the reply"));
        Check("live: one user + one reply bubble after", Bubbles(w).Count == 2, Bubbles(w).Count.ToString());
        Check("live: final bubble equals result", ReplyTexts(w).Values.Count() == 1 && ReplyTexts(w).Values.First() == "The final **answer**", string.Join("|", ReplyTexts(w).Values));
        Check("live: final text rendered bold", ReplyShown(ReplyTexts(w).Keys.First()).Trim() == "The final answer");
        Check("live: liveBubble cleared", Get(w, "liveBubble") == null);
        Check("live: status hidden after", !StatusVisible(w));
        Check("live: saved turn text", Turns(w)[1].Text == "The final **answer**");
        Check("live: model recorded", Turns(w)[1].Detail.Contains("fake-model-1"), Turns(w)[1].Detail);
        string log = File.ReadAllText(Path.Combine(root, "fake-log.txt"));
        Check("live: asks for partial messages", log.Contains("--include-partial-messages"));

        // result differs from what streamed
        Script(Sys, Start("text"), Delta("streamed words"), "#sleep 400", Result("different final"));
        await SendNow(w, "second");
        Check("live: result replaces streamed text", ReplyTexts(w).Values.Contains("different final") && !ReplyTexts(w).Values.Contains("streamed words"));
        Check("live: only 2 reply bubbles", ReplyTexts(w).Count == 2, ReplyTexts(w).Count.ToString());

        // no streaming at all
        Script(Sys, Result("only a result"));
        await SendNow(w, "third");
        Check("live: reply with no deltas", Turns(w).Last().Text == "only a result");

        // restart with no new text keeps nothing odd
        Script(Sys, Start("text"), Delta("before tool"), "#sleep 300", Start("text"), "#sleep 400", Result("after"));
        await SendNow(w, "fourth");
        Check("live: empty restart then result", Turns(w).Last().Text == "after" && Get(w, "liveBubble") == null);

        // failure removes the live bubble
        int b0 = Bubbles(w).Count, t0 = Turns(w).Count, m0 = Messages(w).Children.Count;
        FailScript();
        Input(w).Text = "will fail"; Call(w, "Send");
        bool sawLive = false;
        while (Busy(w)) { if (Get(w, "liveBubble") != null) sawLive = true; await Task.Delay(20); }
        await Task.Delay(50);
        Check("fail: live bubble was shown", sawLive);
        Check("fail: live bubble removed", Get(w, "liveBubble") == null && Bubbles(w).Count == b0, Bubbles(w).Count + "/" + b0);
        Check("fail: messages back to before", Messages(w).Children.Count == m0 && Turns(w).Count == t0);
        Check("fail: message back in box", Input(w).Text == "will fail", Input(w).Text);
        Check("fail: status with retry", RetryVisible(w) && Status(w).Contains("Your message is back in the box"), Status(w));
        Check("fail: no reply text kept", !ReplyTexts(w).Values.Contains("partial words"));

        // retry succeeds
        ReplyScript("retried answer");
        Press(w, (UIElement)Get(w, "retryButton"), Key.Enter);
        await Task.Delay(30); await Idle(w);
        Check("fail: Try again sends", Turns(w).Last().Text == "retried answer" && Input(w).Text.Length == 0, Input(w).Text);

        // stop removes the live bubble
        b0 = Bubbles(w).Count; t0 = Turns(w).Count;
        HoldScript();
        Input(w).Text = "stop me"; Call(w, "Send");
        for (int i = 0; i < 400 && Get(w, "liveBubble") == null; i++) await Task.Delay(25);
        Check("stop: live bubble shown before stop", Get(w, "liveBubble") != null);
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        Call(w, "StopRequest");
        await Idle(w);
        Check("stop: ends quickly", sw.ElapsedMilliseconds < 6000, sw.ElapsedMilliseconds + "ms");
        Check("stop: bubble removed", Get(w, "liveBubble") == null && Bubbles(w).Count == b0 && Turns(w).Count == t0);
        Check("stop: message back + Stopped", Input(w).Text == "stop me" && Status(w).StartsWith("Stopped."), Status(w));
        Input(w).Text = "";

        // Esc while running stops
        HoldScript();
        Input(w).Text = "esc me"; Call(w, "Send");
        for (int i = 0; i < 200 && Get(w, "liveBubble") == null; i++) await Task.Delay(25);
        Esc(w);
        await Idle(w);
        Check("stop: Esc stops", Status(w).StartsWith("Stopped."), Status(w));
        Input(w).Text = "";

        // error classes and is_error results
        Script(Sys, "#stderr Please run /login. Not logged in", "#exit 1");
        await SendNow(w, "auth");
        Check("fail: not signed in message", Status(w).Contains("isn't signed in"), Status(w));
        Input(w).Text = "";
        Script(Sys, "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":true,\"result\":\"You've hit your usage limit\"}");
        await SendNow(w, "limit");
        Check("fail: usage limit message", Status(w).Contains("usage limit"), Status(w));
        Input(w).Text = "";
        Script(Sys, "not json at all", "#exit 0");
        await SendNow(w, "garbage");
        Check("fail: garbage exit 0", Status(w).Contains("couldn't answer"), Status(w));
        Input(w).Text = "";
        Script(Sys, Start("text"), "{\"type\":\"stream_event\"}", "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\"}}", "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_start\"}}", "{\"type\":\"assistant\"}", "{\"type\":\"assistant\",\"message\":{\"content\":\"text\"}}", ToolUse("Oddball", "{}"), Result("survived"));
        await SendNow(w, "malformed");
        Check("live: malformed events ignored", Turns(w).Last().Text == "survived");

        // formats mid-stream: half a table, fence, bold
        Script(Sys, Start("text"), Delta("| a | b |\n"), "#sleep 150", Delta("|--"), "#sleep 150", Delta("-|---|\n| 1 "), "#sleep 150", Delta("| 2 |\n\n```py\nprint("), "#sleep 150", Delta("1)\n```\n\n**bo"), "#sleep 150", Delta("ld** [li"), "#sleep 150", Delta("nk](https://a.example/)"), "#sleep 150", Result("| a | b |\n|---|---|\n| 1 | 2 |\n\n```py\nprint(1)\n```\n\n**bold** [link](https://a.example/)"));
        int fb = fails;
        await SendNow(w, "partial markdown");
        Check("live: partial markdown mid-stream OK", fails == fb && Turns(w).Last().Text.Contains("bold"));

        // an unclosed fence as it streams
        Script(Sys, Start("text"), Delta("Before\n\n```py\nprint(1)"), "#sleep 350", Delta("\nprint(2)\n\nprint(3)"), "#sleep 350", Delta("\n```\n\nAfter words"), "#sleep 350", Result("Before\n\n```py\nprint(1)\nprint(2)\n\nprint(3)\n```\n\nAfter words"));
        Input(w).Text = "stream fence"; Call(w, "Send");
        List<string> fenceStates = new List<string>();
        while (Busy(w))
        {
            Border lv = (Border)Get(w, "liveBubble");
            if (lv != null)
            {
                FlowDocument ld = ((RichTextBox)lv.Child).Document;
                BlockUIContainer cb = Walk(ld).OfType<BlockUIContainer>().FirstOrDefault();
                string state = (cb == null ? "nocode" : "code:" + CodeOf(cb).Replace("\r\n", "|")) + (TextOf(ld).Contains("After") ? "+after" : "");
                if (fenceStates.Count == 0 || fenceStates[fenceStates.Count - 1] != state) fenceStates.Add(state);
            }
            await Task.Delay(20);
        }
        Log("  fence stream: " + string.Join(" -> ", fenceStates));
        Check("live fence: drawn as code before it closes", fenceStates.Any(x => x == "code:print(1)"), string.Join(" -> ", fenceStates));
        Check("live fence: grows, keeps blank line", fenceStates.Any(x => x.Contains("print(2)||print(3)") && !x.Contains("+after")));
        Check("live fence: closes, text after", fenceStates.Last().EndsWith("+after"));
        Check("live fence: final reply", Turns(w).Last().Text.EndsWith("After words") && Get(w, "liveBubble") == null);

        // long stream stays responsive
        List<string> many = new List<string> { Sys, Start("text") };
        StringBuilder all = new StringBuilder();
        for (int i = 0; i < 3000; i++) { string piece = "word" + i + (i % 12 == 0 ? "\n\n" : " "); many.Add(Delta(piece)); all.Append(piece); if (i % 100 == 0) many.Add("#sleep 20"); }
        many.Add(Result(all.ToString().Trim()));
        Script(many.ToArray());
        Input(w).Text = "long"; Call(w, "Send");
        int maxGap = 0; DateTime last = DateTime.Now; sw = System.Diagnostics.Stopwatch.StartNew();
        while (Busy(w)) { await Task.Delay(10); int gap = (int)(DateTime.Now - last).TotalMilliseconds; if (gap > maxGap) maxGap = gap; last = DateTime.Now; }
        Check("live: long stream finishes", Turns(w).Last().Text.Length > 20000, Turns(w).Last().Text.Length + " chars in " + sw.ElapsedMilliseconds + "ms, worst UI gap " + maxGap + "ms");
        Check("live: long stream UI gap under 1s", maxGap < 1000, maxGap + "ms");

        // theme switch while live and on restore
        Script(Sys, Start("text"), Delta("**themed live** text"), "#sleep 1200", Result("**themed live** text"));
        Input(w).Text = "theme"; Call(w, "Send");
        for (int i = 0; i < 200 && Get(w, "liveBubble") == null; i++) await Task.Delay(25);
        Theme("light"); await Task.Delay(150);
        Border lb = (Border)Get(w, "liveBubble");
        Check("live: theme switch redraws live bubble", lb != null && ((SolidColorBrush)((RichTextBox)lb.Child).Document.Foreground).Color.R < 128);
        await Idle(w);
        Theme("dark");

        // a reply that can't be drawn must not take the window down
        int crashFails = fails;
        string poison = "See [https://a.example/b](https://a.example/b) for more.";
        Script(Sys, Start("text"), Delta(poison), "#sleep 500", Result(poison));
        await SendNow(w, "poison link");
        Log("  poison: unhandled exceptions reached the dispatcher: " + (fails - crashFails) + "; busy=" + Busy(w) + " turns=" + Turns(w).Count + " live=" + (Get(w, "liveBubble") != null) + " note has it=" + File.ReadAllText(NotePath(w)).Contains("poison link"));
        Check("live: link with a url as its words survives", fails == crashFails);
        Input(w).Text = "";
        fails = crashFails + (fails > crashFails ? 1 : 0);

        // an older saved chat holding that link
        ChatWindow maker = NewWindow(700, 800);
        await Task.Delay(100);
        await Exchange(maker, "old note", "OLDREPLY");
        string oldNote = NotePath(maker);
        maker.Close();
        File.WriteAllText(oldNote, File.ReadAllText(oldNote).Replace("OLDREPLY", poison), new UTF8Encoding(false));
        ChatWindow viewer = NewWindow(700, 800);
        await Task.Delay(100);
        string openError = null;
        try { Call(viewer, "OpenSavedChat", new ChatEntry { Path = oldNote, Title = "old note", Started = DateTime.Now, Text = "" }); }
        catch (TargetInvocationException ex) { openError = ex.InnerException.GetType().Name; }
        Check("opening a saved chat with that link survives", openError == null, openError ?? "");
        viewer.Close();

        // closing the window mid-reply
        ChatWindow c = NewWindow();
        await Task.Delay(100);
        HoldScript();
        Input(c).Text = "close me"; Call(c, "Send");
        await Task.Delay(800);
        int fc = fails;
        c.Close();
        await Task.Delay(2500);
        Check("live: close mid-reply is clean", fails == fc);
        w.Close();
    }

    // ---------- 3. regenerate ----------
    static async Task Regen()
    {
        ChatWindow w = NewWindow(700, 800);
        await Task.Delay(200);
        Pending(w).Add(Att("doc.pdf", AttachmentKind.Pdf));
        Pending(w).Add(Att("t.txt"));
        Call(w, "RefreshChips");
        await Exchange(w, "first question", "first answer");
        await Exchange(w, "second question", "second answer");
        ChatTurn lastAsk = Turns(w)[2];
        Pending(w).Add(Att("img.png", AttachmentKind.Image));
        Call(w, "RefreshChips");
        await Exchange(w, "third question", "third answer");
        Check("setup: 6 turns, note saved", Turns(w).Count == 6 && NotePath(w) != null && File.Exists(NotePath(w)));
        Check("setup: sent attachments", SentList(w).Count == 3, SentList(w).Count.ToString());
        List<ChatTurn> replies = Replies(w).ToList();
        Check("regen button only on newest", replies[0].Regenerate.Visibility == Visibility.Collapsed && replies[1].Regenerate.Visibility == Visibility.Collapsed && replies[2].Regenerate.Visibility == Visibility.Visible);

        // failure
        string noteBefore = File.ReadAllText(NotePath(w));
        List<ChatTurn> turnsBefore = new List<ChatTurn>(Turns(w));
        List<UIElement> elementsBefore = Messages(w).Children.Cast<UIElement>().ToList();
        List<ChatAttachment> sentBefore = new List<ChatAttachment>(SentList(w));
        List<Border> bubblesBefore = new List<Border>(Bubbles(w));
        Input(w).Text = "my draft";
        Pending(w).Add(Att("draft.txt")); Call(w, "RefreshChips");
        int stdinBefore = FakeStdin().Count;
        FailScript();
        Press(w, replies[2].Regenerate, Key.Enter);
        await Task.Delay(100);
        Check("regen: busy hides remaining regen buttons", Busy(w) && Replies(w).All(t => t.Regenerate.Visibility == Visibility.Collapsed));
        Check("regen: old reply out while running", !Messages(w).Children.Contains(elementsBefore.Last()));
        Check("regen: turn asked again shown", Bubbles(w).Count >= 5);
        await Idle(w);
        string asked = FakeStdin().Last();
        Check("regen: prompt has no old reply", !asked.Contains("third answer") && asked.Contains("third question") && asked.Contains("second answer"));
        Check("regen: prompt has the attachment again", asked.Contains("img.png"));
        Check("regen: earlier attachments still sent", asked.Contains("doc.pdf") && asked.Contains("t.txt"));
        Check("regen: draft not in prompt", !asked.Contains("my draft") && !asked.Contains("draft.txt"));
        Action<string> sameAsBefore = delegate(string tag)
        {
            Check(tag + ": turns restored", Turns(w).SequenceEqual(turnsBefore));
            Check(tag + ": UI restored", Messages(w).Children.Cast<UIElement>().SequenceEqual(elementsBefore), Messages(w).Children.Count + "/" + elementsBefore.Count);
            Check(tag + ": sent restored", SentList(w).SequenceEqual(sentBefore), SentList(w).Count + "/" + sentBefore.Count);
            Check(tag + ": bubbles restored", Bubbles(w).SequenceEqual(bubblesBefore));
            Check(tag + ": note unchanged", File.ReadAllText(NotePath(w)) == noteBefore);
            Check(tag + ": draft untouched", Input(w).Text == "my draft" && Pending(w).Count == 1 && Pending(w)[0].Name == "draft.txt", Input(w).Text);
            Check(tag + ": button back on newest", replies[2].Regenerate.Visibility == Visibility.Visible);
        };
        sameAsBefore("regen fail");
        Check("regen fail: status", RetryVisible(w) && Status(w).Contains("The earlier reply is still here"), Status(w));

        // stop
        HoldScript();
        Press(w, replies[2].Regenerate, Key.Enter);
        for (int i = 0; i < 200 && Get(w, "liveBubble") == null; i++) await Task.Delay(25);
        Call(w, "StopRequest");
        await Idle(w);
        sameAsBefore("regen stop");
        Check("regen stop: status", RetryVisible(w) && Status(w).StartsWith("Stopped. The earlier"), Status(w));

        // resize while it's out, then fail
        Border oldBubble = bubblesBefore[0];
        double widthBefore = oldBubble.MaxWidth;
        HoldScript();
        Press(w, replies[2].Regenerate, Key.Enter);
        for (int i = 0; i < 200 && Get(w, "liveBubble") == null; i++) await Task.Delay(25);
        Theme("light"); await Task.Delay(200);
        Call(w, "StopRequest");
        await Idle(w);
        Border restoredReply = ReplyTexts(w).Keys.First(b => ReplyTexts(w)[b] == "third answer");
        Check("regen: theme change during request reaches restored reply", ((SolidColorBrush)((RichTextBox)restoredReply.Child).Document.Foreground).Color.R < 128);
        Theme("dark");

        // Retry calls Regenerate, and success rewrites the note
        stdinBefore = FakeStdin().Count;
        FailScript();
        Press(w, replies[2].Regenerate, Key.Enter);
        await Task.Delay(50); await Idle(w);
        ReplyScript("regenerated answer");
        Press(w, (UIElement)Get(w, "retryButton"), Key.Enter);
        await Task.Delay(50); await Idle(w);
        Check("retry regenerates", Turns(w).Count == 6 && Turns(w).Last().Text == "regenerated answer", Turns(w).Last().Text);
        Check("retry didn't send the draft", Input(w).Text == "my draft" && Pending(w).Count == 1);
        string note = File.ReadAllText(NotePath(w));
        Check("regen ok: note rewritten", note.Contains("regenerated answer") && !note.Contains("third answer") && note.Contains("third question"));
        Check("regen ok: one copy of question", Occurrences(note, "third question") == 1, Occurrences(note, "third question").ToString());
        Check("regen ok: attachment name kept in note", note.Contains("img.png"));
        Check("regen ok: old bubble forgotten", !ReplyTexts(w).Values.Contains("third answer"));
        Check("regen ok: sent has attachment once", SentList(w).Count(a => a.Name == "img.png") == 1 && SentList(w).Count == 3, SentList(w).Count.ToString());
        Check("regen ok: newest has button", Turns(w).Last().Regenerate.Visibility == Visibility.Visible && replies[1].Regenerate.Visibility == Visibility.Collapsed);
        Check("regen ok: status hidden", !StatusVisible(w));
        Check("regen ok: messages count same", Messages(w).Children.Count == elementsBefore.Count, Messages(w).Children.Count + "/" + elementsBefore.Count);
        Check("regen ok: reload matches", ReadBack(NotePath(w)).Select(t => t.Text).SequenceEqual(Turns(w).Select(t => t.Text)));

        // a normal send after a failed regenerate doesn't regenerate on retry
        FailScript();
        Press(w, Turns(w).Last().Regenerate, Key.Enter);
        await Task.Delay(50); await Idle(w);
        Input(w).Text = ""; Pending(w).Clear(); Call(w, "RefreshChips");
        FailScript();
        await SendNow(w, "plain message");
        ReplyScript("plain answer");
        Press(w, (UIElement)Get(w, "retryButton"), Key.Enter);
        await Task.Delay(50); await Idle(w);
        Check("retry after a plain failure sends it", Turns(w).Count == 8 && Turns(w)[6].Text == "plain message", Turns(w).Count.ToString());

        // model chosen now is used
        Set(w, "modelChoice", "haiku");
        ReplyScript("haiku answer");
        Press(w, Turns(w).Last().Regenerate, Key.Enter);
        await Task.Delay(50); await Idle(w);
        Check("regen uses the model chosen now", File.ReadAllLines(Path.Combine(root, "fake-log.txt")).Last(l => l.StartsWith("ARGS ")).Contains("--model haiku"));
        Set(w, "modelChoice", "opus");

        // a window resized while the reply is out
        {
            Border restored = Bubbles(w)[Bubbles(w).Count - 1];
            double wide = (double)Call(w, "BubbleWidth");
            HoldScript();
            Press(w, Turns(w).Last().Regenerate, Key.Enter);
            for (int i = 0; i < 200 && Get(w, "liveBubble") == null; i++) await Task.Delay(25);
            w.Width = 380; w.UpdateLayout(); await Task.Delay(200);
            Call(w, "StopRequest"); await Idle(w);
            w.UpdateLayout(); await Task.Delay(100);
            double want = (double)Call(w, "BubbleWidth");
            Check("regen: restored bubbles fit a window resized meanwhile", Math.Abs(restored.MaxWidth - want) < 1, "max=" + restored.MaxWidth.ToString("0") + " want=" + want.ToString("0") + " was=" + wide.ToString("0"));
            Shot((FrameworkElement)w.Content, "regen-restored-after-resize");
            w.Width = 700; w.UpdateLayout(); await Task.Delay(100);
        }

        // regenerate while busy is refused
        HoldScript();
        Input(w).Text = "hold"; Call(w, "Send");
        await Task.Delay(300);
        Check("plain send hides the newest regen button", Replies(w).All(t => t.Regenerate.Visibility == Visibility.Collapsed));
        int tn = Turns(w).Count;
        Call(w, "Regenerate");
        Check("regen while busy refused", Status(w).Contains("Wait for this reply"), Status(w));
        Call(w, "StopRequest"); await Idle(w);
        Input(w).Text = "";

        // created files on the regenerated reply
        string made = Path.Combine(desk, "made.txt"); File.WriteAllText(made, "x");
        ChatTurn last = Turns(w).Last();
        last.CreatedFiles.Add(made);
        Messages(w).Children.Add((UIElement)Call(w, "BuildFileCard", made));
        List<string> chatFiles = (List<string>)Get(w, "chatFiles");
        Check("setup: file card counted", chatFiles.Contains(made));
        int cc = Messages(w).Children.Count;
        FailScript();
        Press(w, last.Regenerate, Key.Enter);
        await Task.Delay(50); await Idle(w);
        Check("regen fail: file card + list restored", chatFiles.Contains(made) && Messages(w).Children.Count == cc && last.CreatedFiles.Contains(made));
        ReplyScript("no files now");
        Press(w, last.Regenerate, Key.Enter);
        await Task.Delay(50); await Idle(w);
        Check("regen ok: old file leaves the list", !chatFiles.Contains(made));
        Check("regen ok: file not deleted", File.Exists(made));
        Check("regen ok: card gone", Messages(w).Children.Count == cc - 1, Messages(w).Children.Count + "/" + (cc - 1));
        w.Close();

        // first-exchange regenerate from an opened note, title retained
        ChatWindow c = NewWindow(700, 800);
        await Task.Delay(100);
        await Exchange(c, "note question", "note answer");
        string path = NotePath(c);
        ChatEntry entry = new ChatEntry { Path = path, Title = "note question", Started = DateTime.Now, Text = "" };
        c.Close();
        ChatWindow o = NewWindow(700, 800);
        await Task.Delay(100);
        Call(o, "OpenSavedChat", entry);
        Check("opened: buttons on newest", Replies(o).Last().Regenerate.Visibility == Visibility.Visible);
        string nb = File.ReadAllText(path);
        FailScript();
        Press(o, Replies(o).Last().Regenerate, Key.Enter);
        await Task.Delay(50); await Idle(o);
        Check("opened: failure restores", Turns(o).Count == 2 && File.ReadAllText(path) == nb && Messages(o).Children.Count >= 3);
        ReplyScript("fresh note answer");
        Press(o, Replies(o).Last().Regenerate, Key.Enter);
        await Task.Delay(50); await Idle(o);
        Check("opened: regen rewrites same note", File.ReadAllText(path).Contains("fresh note answer") && !File.ReadAllText(path).Contains("\nnote answer") && NotePath(o) == path);
        o.Close();
    }
    static int Occurrences(string text, string word) { int n = 0, at = 0; while ((at = text.IndexOf(word, at)) >= 0) { n++; at += word.Length; } return n; }
    static List<ChatTurn> ReadBack(string path) { List<ChatTurn> t; DateTime s; ChatNoteWriter.TryRead(path, out t, out s); return t; }

    // ---------- 4. edit ----------
    static async Task Edit()
    {
        ChatWindow w = NewWindow(700, 800);
        await Task.Delay(200);
        Pending(w).Add(Att("one.txt")); Call(w, "RefreshChips");
        await Exchange(w, "q one", "a one");
        await Exchange(w, "q two", "a two");
        await Exchange(w, "q three", "a three");
        ChatTurn t0 = Turns(w)[0], t2 = Turns(w)[2], t4 = Turns(w)[4];

        // start and cancel
        Input(w).Text = "typed draft";
        ChatAttachment df = Att("draft.txt");
        Pending(w).Add(df); Call(w, "RefreshChips");
        Border editBtn = EditButton(t4);
        Check("edit button found", editBtn != null);
        Press(w, editBtn, Key.Enter);
        Check("edit: text in box", Input(w).Text == "q three", Input(w).Text);
        Check("edit: banner for latest", BannerVisible(w) && BannerText(w) == "Editing your message. Sending replaces it and Claude's reply.", BannerText(w));
        Check("edit: pending swapped", Pending(w).Count == 0);
        Call(w, "CancelEdit");
        Check("cancel: draft restored", Input(w).Text == "typed draft" && Pending(w).Count == 1 && Pending(w)[0] == df, Input(w).Text);
        Check("cancel: banner gone", !BannerVisible(w));
        Check("cancel: nothing changed", Turns(w).Count == 6);

        // with attachments, earlier turn
        Call(w, "StartEdit", t0);
        Check("edit earlier: banner", BannerVisible(w) && BannerText(w) == "Editing an earlier message. Sending replaces it and everything after it.", BannerText(w));
        Check("edit earlier: attachments in chips", Pending(w).Count == 1 && Pending(w)[0].Name == "one.txt");
        Check("edit earlier: chips drawn", ((WrapPanel)Get(w, "chips")).Children.Count == 1);
        // moving to another message keeps the same draft
        Call(w, "StartEdit", t4);
        Check("edit switch: other message shown", Input(w).Text == "q three" && Pending(w).Count == 0);
        Esc(w);
        Check("esc: cancels and restores", Input(w).Text == "typed draft" && !BannerVisible(w) && Pending(w).Count == 1, Input(w).Text);
        Check("esc: window still open", w.IsVisible);

        // Esc with cancel button
        Call(w, "StartEdit", t4);
        DockPanel row = (DockPanel)((Border)Get(w, "editBanner")).Child;
        Press(w, (UIElement)row.Children[0], Key.Enter);
        Check("cancel button cancels", !BannerVisible(w) && Input(w).Text == "typed draft");

        // failed edit of an earlier turn
        string noteBefore = File.ReadAllText(NotePath(w));
        List<ChatTurn> turnsBefore = new List<ChatTurn>(Turns(w));
        List<UIElement> elementsBefore = Messages(w).Children.Cast<UIElement>().ToList();
        List<ChatAttachment> sentBefore = new List<ChatAttachment>(SentList(w));
        Call(w, "StartEdit", t0);
        Input(w).Text = "q one EDITED";
        FailScript();
        Call(w, "Send");
        await Task.Delay(100);
        Check("edit running: later turns out", !Messages(w).Children.Contains(elementsBefore.Last()) && Busy(w));
        Check("edit running: banner hidden", !BannerVisible(w));
        await Idle(w);
        string prompt = FakeStdin().Last();
        Check("edit: prompt drops later turns", prompt.Contains("q one EDITED") && !prompt.Contains("a one") && !prompt.Contains("q two") && !prompt.Contains("q three"));
        Check("edit: prompt keeps attachment", prompt.Contains("one.txt"));
        Check("edit fail: turns restored", Turns(w).SequenceEqual(turnsBefore));
        Check("edit fail: UI restored", Messages(w).Children.Cast<UIElement>().SequenceEqual(elementsBefore), Messages(w).Children.Count + "/" + elementsBefore.Count);
        Check("edit fail: sent restored", SentList(w).SequenceEqual(sentBefore));
        Check("edit fail: note unchanged", File.ReadAllText(NotePath(w)) == noteBefore);
        Check("edit fail: edit back in box", Input(w).Text == "q one EDITED", Input(w).Text);
        Check("edit fail: attachments back", Pending(w).Count == 1 && Pending(w)[0].Name == "one.txt", Pending(w).Count.ToString());
        Check("edit fail: status", RetryVisible(w) && Status(w).Contains("Your edit is back in the box"), Status(w));
        Check("edit fail: still editing, banner shows", BannerVisible(w));
        Check("edit fail: banner says earlier", BannerText(w).StartsWith("Editing an earlier"), BannerText(w));

        // stop the edit too
        HoldScript();
        Call(w, "Send");
        for (int i = 0; i < 200 && Get(w, "liveBubble") == null; i++) await Task.Delay(25);
        Call(w, "StopRequest"); await Idle(w);
        Check("edit stop: all back", Turns(w).SequenceEqual(turnsBefore) && Input(w).Text == "q one EDITED" && File.ReadAllText(NotePath(w)) == noteBefore && Pending(w).Count == 1);

        // cancel after failure gets the old draft back
        Call(w, "CancelEdit");
        Check("cancel after failure: old draft back", Input(w).Text == "typed draft" && Pending(w).Count == 1 && Pending(w)[0] == df, Input(w).Text);
        Check("cancel after failure: chat intact", Turns(w).SequenceEqual(turnsBefore));

        // failing edit of the newest message keeps the right banner
        Call(w, "StartEdit", t4);
        Input(w).Text = "q three EDITED";
        FailScript();
        Call(w, "Send");
        await Task.Delay(50); await Idle(w);
        Check("edit fail newest: banner still says latest", BannerVisible(w) && BannerText(w).StartsWith("Editing your message"), BannerText(w));

        // retry after failure uses the edit path
        ReplyScript("a three new");
        Press(w, (UIElement)Get(w, "retryButton"), Key.Enter);
        await Task.Delay(50); await Idle(w);
        Check("edit retry: replaces, doesn't append", Turns(w).Count == 6 && Turns(w)[4].Text == "q three EDITED" && Turns(w)[5].Text == "a three new", Turns(w).Count.ToString());
        Check("edit retry: draft back", Input(w).Text == "typed draft" && Pending(w).Count == 1, Input(w).Text);
        Input(w).Text = ""; Pending(w).Clear(); Call(w, "RefreshChips");

        // too long: nothing lost
        turnsBefore = new List<ChatTurn>(Turns(w));
        elementsBefore = Messages(w).Children.Cast<UIElement>().ToList();
        Call(w, "StartEdit", Turns(w)[2]);
        string big = new string('z', 300100);
        Input(w).Text = big;
        Call(w, "Send");
        await Task.Delay(50);
        Check("edit too long: chat restored", Turns(w).SequenceEqual(turnsBefore) && Messages(w).Children.Cast<UIElement>().SequenceEqual(elementsBefore));
        Check("edit too long: text kept and told", Input(w).Text == big && Status(w).Contains("too long"), Status(w));
        Check("edit too long: banner right", BannerVisible(w) && BannerText(w).StartsWith("Editing an earlier"), BannerText(w));
        Call(w, "CancelEdit");

        // successful edit of an earlier turn drops later turns
        Input(w).Text = "draft two";
        Call(w, "StartEdit", Turns(w)[0]);
        string notePath = NotePath(w);
        Input(w).Text = "q one FINAL";
        ReplyScript("a one FINAL");
        Call(w, "Send");
        await Task.Delay(50); await Idle(w);
        Check("edit ok: later turns gone", Turns(w).Count == 2 && Turns(w)[0].Text == "q one FINAL" && Turns(w)[1].Text == "a one FINAL", Turns(w).Count.ToString());
        string after = File.ReadAllText(notePath);
        Check("edit ok: note rewritten", after.Contains("q one FINAL") && !after.Contains("q two") && !after.Contains("a three") && NotePath(w) == notePath);
        Check("edit ok: bubbles", Bubbles(w).Count == 2 && ReplyTexts(w).Count == 1, Bubbles(w).Count + "/" + ReplyTexts(w).Count);
        Check("edit ok: draft back, banner gone", Input(w).Text == "draft two" && !BannerVisible(w), Input(w).Text);
        Check("edit ok: attachments kept on turn", Turns(w)[0].Attachments.Count == 1 && SentList(w).Count == 1);
        Check("edit ok: title follows first message", ((TextBlock)Get(w, "chatTitle")).Text == "q one FINAL", ((TextBlock)Get(w, "chatTitle")).Text);
        Input(w).Text = "";

        // the attachment can be removed while editing
        Call(w, "StartEdit", Turns(w)[0]);
        Pending(w).Clear(); Call(w, "RefreshChips");
        Input(w).Text = "q one no file";
        ReplyScript("no file answer");
        Call(w, "Send");
        await Task.Delay(50); await Idle(w);
        Check("edit ok: removed attachment gone", Turns(w)[0].Attachments.Count == 0 && SentList(w).Count == 0 && !File.ReadAllText(notePath).Contains("one.txt"));
        Check("edit ok: prompt has no removed file", !FakeStdin().Last().Contains("one.txt"));

        // edit refused while busy
        HoldScript();
        Input(w).Text = "hold"; Call(w, "Send");
        await Task.Delay(200);
        Call(w, "StartEdit", Turns(w)[0]);
        Check("edit while busy refused", Status(w).Contains("Wait for this reply") && Get(w, "editingTurn") == null, Status(w));
        Call(w, "StopRequest"); await Idle(w);
        Input(w).Text = "";

        // custom title stays when the first message is edited
        ChatNoteWriter nw = (ChatNoteWriter)Get(w, "noteWriter");
        nw.Title = "Custom name";
        ((TextBlock)Get(w, "chatTitle")).Text = "Custom name";
        Call(w, "StartEdit", Turns(w)[0]);
        Input(w).Text = "renamed first message";
        ReplyScript("renamed answer");
        Call(w, "Send"); await Task.Delay(50); await Idle(w);
        Check("edit ok: custom title stays", ((TextBlock)Get(w, "chatTitle")).Text == "Custom name" && File.ReadAllText(NotePath(w)).Contains("title: \"Custom name\""));

        // new chat cancels the edit
        Call(w, "StartEdit", Turns(w)[0]);
        Call(w, "StartNewChat");
        Check("new chat clears edit", Get(w, "editingTurn") == null && !BannerVisible(w) && Input(w).Text.Length == 0, Input(w).Text);

        // screenshot with banner
        await Exchange(w, "shown question", "shown answer");
        Call(w, "StartEdit", Turns(w)[0]);
        await Task.Delay(100);
        Shot((FrameworkElement)w.Content, "edit-banner");
        w.Close();

        // opened note: edit first message
        ChatWindow c = NewWindow(700, 800);
        await Task.Delay(100);
        await Exchange(c, "note q1", "note a1");
        await Exchange(c, "note q2", "note a2");
        string p = NotePath(c);
        c.Close();
        ChatEntry e = new ChatEntry { Path = p, Title = "note q1", Started = DateTime.Now, Text = "" };
        ChatWindow o = NewWindow(700, 800);
        await Task.Delay(100);
        Call(o, "OpenSavedChat", e);
        Check("opened: edit button present", EditButton(Turns(o)[0]) != null);
        Call(o, "StartEdit", Turns(o)[0]);
        Input(o).Text = "note q1 edited";
        FailScript();
        Call(o, "Send");
        await Task.Delay(50); await Idle(o);
        Check("opened: failed edit restores", Turns(o).Count == 4 && Input(o).Text == "note q1 edited" && Messages(o).Children.Count >= 6);
        ReplyScript("note a1 edited");
        Call(o, "Send");
        await Task.Delay(50); await Idle(o);
        Check("opened: edit rewrites note", Turns(o).Count == 2 && File.ReadAllText(p).Contains("note q1 edited") && !File.ReadAllText(p).Contains("note q2") && NotePath(o) == p);
        o.Close();
    }

    // ---------- 5. search ----------
    static string Snip(ChatEntry e, string q, out bool hit) { string s; hit = ChatWindow.MatchesSearch(e, q, out s); return s; }
    static async Task Search()
    {
        ChatEntry a = new ChatEntry { Path = "a", Title = "Budget plan for spring", Started = DateTime.Now, Text = "We talked about rent and groceries.\nThe landlord agreed on a lower price for the flat this year, finally.\nLater on we discussed holidays in the mountains and how to travel there." };
        bool hit; string s;
        s = Snip(a, "", out hit); Check("search: empty matches, no snippet", hit && s == null);
        s = Snip(a, "   ", out hit); Check("search: spaces match, no snippet", hit && s == null);
        s = Snip(a, "budget", out hit); Check("search: title only, no snippet", hit && s == null, s ?? "");
        s = Snip(a, "BUDGET", out hit); Check("search: ignores case", hit);
        s = Snip(a, "landlord", out hit); Check("search: body gets snippet", hit && s != null && s.Contains("landlord"), s ?? "");
        Check("search: snippet starts with ellipsis mid-text", s != null && s.StartsWith("\u2026"), s ?? "");
        Check("search: snippet collapses newlines", s != null && !s.Contains("\n"), s ?? "");
        Check("search: snippet length bound", s != null && s.Length <= 112, (s ?? "").Length.ToString());
        s = Snip(a, "talked", out hit); Check("search: word near start, no leading ellipsis", hit && s != null && !s.StartsWith("\u2026"), s ?? "");
        s = Snip(a, "budget mountains", out hit); Check("search: words across title and body", hit && s != null && s.Contains("mountains"), s ?? "");
        s = Snip(a, "mountains rent", out hit); Check("search: first body word drives snippet", hit && s.Contains("mountains"), s ?? "");
        s = Snip(a, "budget zebra", out hit); Check("search: all words needed", !hit);
        s = Snip(a, "zebra", out hit); Check("search: no match", !hit);
        s = Snip(a, "budget\tspring", out hit); Check("search: tab splits words", hit && s == null);
        s = Snip(new ChatEntry { Title = "x", Text = null }, "y", out hit); Check("search: null body safe", !hit);
        s = Snip(new ChatEntry { Title = "x", Text = "" }, "x", out hit); Check("search: empty body", hit && s == null);
        s = Snip(new ChatEntry { Title = "x", Text = "c++ (a|b) .* [q]" }, "c++ (a|b) .*", out hit); Check("search: regex chars literal", hit, s ?? "");
        s = Snip(new ChatEntry { Title = "\u0645\u064a\u0632\u0627\u0646\u064a\u0629", Text = "\u0627\u0644\u0645\u0635\u0631\u0648\u0641\u0627\u062a \u0627\u0644\u0634\u0647\u0631\u064a\u0629" }, "\u0627\u0644\u0634\u0647\u0631\u064a\u0629", out hit); Check("search: arabic body", hit && s != null && s.Contains("\u0627\u0644\u0634\u0647\u0631\u064a\u0629"));
        s = Snip(new ChatEntry { Title = "T", Text = "x" }, "x", out hit); Check("search: word at very end", hit && s == "x", s ?? "");

        // notes on disk, real rows
        ChatWindow maker = NewWindow(700, 800);
        await Task.Delay(100);
        await Exchange(maker, "Budget plan for spring", "We talked about the landlord and the mountains.");
        Call(maker, "StartNewChat");
        await Task.Delay(1100);
        await Exchange(maker, "Recipe for lentil soup", "Use cumin and onions.");
        Call(maker, "StartNewChat");
        await Task.Delay(1100);
        await Exchange(maker, "\u0645\u064a\u0632\u0627\u0646\u064a\u0629 \u0627\u0644\u0628\u064a\u062a", "\u0627\u0644\u0645\u0635\u0631\u0648\u0641\u0627\u062a \u0627\u0644\u0634\u0647\u0631\u064a\u0629");
        string otherNote = NotePath(maker);
        maker.Close();

        ChatWindow w = NewWindow(1100, 800);
        await Task.Delay(100);
        TextBox box = (TextBox)Get(w, "historySearch");
        StackPanel list = (StackPanel)Get(w, "historyList");
        TextBlock label = (TextBlock)Get(w, "recentsLabel");
        Border panel = (Border)Get(w, "historyPanel");
        Call(w, "RefreshHistory");
        for (int i = 0; i < 100 && list.Children.Count < 3; i++) await Task.Delay(30);
        Check("rows: all shown", list.Children.Count == 3 && label.Text == "Recents", list.Children.Count + " " + label.Text);
        Func<List<string>> rowWords = delegate
        {
            List<string> found = new List<string>();
            foreach (UIElement child in list.Children)
            {
                Border row = child as Border;
                if (row == null) { found.Add("MSG:" + ((TextBlock)child).Text); continue; }
                ChatEntry en = (ChatEntry)row.Tag;
                found.Add(en.Title);
            }
            return found;
        };
        // Waits for the search to finish instead of a fixed 260 ms (approved 2026-11-07): at least 300 ms, which is longer than
        // the 150 ms search timer plus a poll, then until that timer has stopped and the list and label are the same on two
        // polls 50 ms apart. Gives up after 2 s and lets the check decide. Assertions are unchanged.
        System.Windows.Threading.DispatcherTimer searchTimer = (System.Windows.Threading.DispatcherTimer)Get(w, "searchTimer");
        Func<string> listState = delegate { return list.Children.Count + "#" + label.Text + "#" + string.Join("|", rowWords()); };
        Func<Task> settle = async delegate
        {
            await Task.Delay(300);
            string last = null;
            for (int waited = 300; waited < 2000; waited += 50)
            {
                string now = listState();
                if (!searchTimer.IsEnabled && now == last) return;
                last = searchTimer.IsEnabled ? null : now;
                await Task.Delay(50);
            }
        };
        box.Text = "lentil";
        // Waits up to 2 s for the filtered list (the 150 ms search timer can run late on a busy machine); same assertion as before.
        Func<bool> lentilFiltered = delegate { return rowWords().Count == 1 && rowWords()[0].Contains("lentil") && label.Text == "Results"; };
        for (int waited = 0; waited < 2000 && !lentilFiltered(); waited += 50) await Task.Delay(50);
        Check("rows: search filters", lentilFiltered(), string.Join("|", rowWords()) + " " + label.Text);
        box.Text = "landlord mountains"; await settle();
        Check("rows: multi-word in body", rowWords().Count == 1 && rowWords()[0].Contains("Budget"), string.Join("|", rowWords()));
        Func<UIElement, bool> hasSnippet = delegate(UIElement e) { return FindText((DependencyObject)e).Any(t => t.Contains("landlord")); };
        panel.Visibility = Visibility.Visible; w.UpdateLayout(); await Task.Delay(100);
        Check("rows: snippet shown", list.Children.Count == 1 && hasSnippet(list.Children[0]), string.Join("|", FindText(list.Children[0])));
        Theme("light"); await Task.Delay(100);
        Shot((FrameworkElement)w.Content, "search-results-light");
        Theme("dark"); await Task.Delay(100);
        Shot((FrameworkElement)w.Content, "search-results-dark");
        box.Text = "\u0627\u0644\u0634\u0647\u0631\u064a\u0629"; await settle();
        Check("rows: arabic search", rowWords().Count == 1, string.Join("|", rowWords()));
        box.Text = "nothinglikethis"; await settle();
        Check("rows: no-match message", rowWords().Count == 1 && rowWords()[0] == "MSG:No chats match your search." && label.Text == "Results", string.Join("|", rowWords()));
        Shot((FrameworkElement)w.Content, "search-empty");
        box.Text = ""; await settle();
        Check("rows: clear restores", list.Children.Count == 3 && label.Text == "Recents", list.Children.Count + " " + label.Text);

        // Esc
        box.Text = "lentil"; await settle();
        box.Focus();
        await Task.Delay(50);
        if (box.IsKeyboardFocused)
        {
            Esc(w);
            await settle();
            Check("esc: clears search", box.Text.Length == 0 && list.Children.Count == 3, box.Text);
        }
        else
        {
            Note("Esc in the search box not exercised: the box can't take keyboard focus in an inactive off-screen window, and activating it would steal focus from the real screen");
            box.Text = ""; await settle();
        }

        // refresh while filtered stays filtered
        box.Text = "lentil"; await settle();
        Call(w, "RefreshHistory");
        await Task.Delay(300);
        Check("rows: refresh keeps filter", rowWords().Count == 1, string.Join("|", rowWords()));
        box.Text = ""; await settle();

        // a saved chat that was edited is findable under its new words only
        await Exchange(w, "Findable first words", "findable reply words");
        Call(w, "StartEdit", Turns(w)[0]);
        Input(w).Text = "Replacement wording"; ReplyScript("replacement reply");
        Call(w, "Send"); await Task.Delay(50); await Idle(w);
        Call(w, "RefreshHistory"); await Task.Delay(300);
        box.Text = "findable"; await settle();
        Check("rows: edited chat loses old words", rowWords().Count == 1 && rowWords()[0].StartsWith("MSG:"), string.Join("|", rowWords()));
        box.Text = "replacement";
        // Waits up to 2 s for the filtered list (the 150 ms search timer can run late on a busy machine); same assertion as before.
        Func<bool> replacementFound = delegate { return rowWords().Count == 1 && rowWords()[0] == "Replacement wording"; };
        for (int waited = 0; waited < 2000 && !replacementFound(); waited += 50) await Task.Delay(50);
        Check("rows: edited chat found by new words", replacementFound(), string.Join("|", rowWords()));
        box.Text = ""; await settle();

        // the limit
        Note("ReadHistory reads at most the newest 300 notes, so older chats can't be found by search: widget.cs ReadHistory");
        w.Close();

        // no history folder
        string noData = Path.Combine(root, "data-nohistory");
        Directory.CreateDirectory(noData);
        ChatWindow n = NewWindow(1100, 800, noData);
        await Task.Delay(100);
        TextBox nb = (TextBox)Get(n, "historySearch");
        Call(n, "RefreshHistory"); await Task.Delay(100);
        nb.Text = "x"; await settle();
        StackPanel nl = (StackPanel)Get(n, "historyList");
        Check("no folder: message stays, no crash", nl.Children.Count == 1, FindText(nl.Children[0]).FirstOrDefault() ?? "");
        n.Close();
    }
    static IEnumerable<string> FindText(DependencyObject d)
    {
        List<string> all = new List<string>();
        Action<DependencyObject> walk = null;
        walk = delegate(DependencyObject x)
        {
            TextBlock t = x as TextBlock;
            if (t != null) all.Add(t.Text);
            foreach (object child in LogicalTreeHelper.GetChildren(x)) { DependencyObject o = child as DependencyObject; if (o != null) walk(o); }
        };
        walk(d);
        return all;
    }

    // ---------- saved instructions: the system prompt file (C-I1 to C-I3), through the .cmd route ----------
    static List<string> FakeLines(string prefix)
    {
        string log = Path.Combine(root, "fake-log.txt");
        if (!File.Exists(log)) return new List<string>();
        return File.ReadAllLines(log, new UTF8Encoding(false)).Where(l => l.StartsWith(prefix)).Select(l => l.Substring(prefix.Length)).ToList();
    }
    static string Unescape(string s)
    {
        StringBuilder b = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                char c = s[++i];
                b.Append(c == 'n' ? '\n' : c == 'r' ? '\r' : c);
            }
            else b.Append(s[i]);
        }
        return b.ToString();
    }

    static ChatRequest InstructionsRequest(string instructions)
    {
        ChatRequest r = new ChatRequest();
        r.Prompt = "Hello";
        r.Model = "";
        r.Effort = "";
        r.Instructions = instructions;
        return r;
    }

    static async Task<ChatResult> AskOnce(ChatClient client, ChatRequest r)
    {
        return await client.Ask(r, delegate(string a, string b) { return Task.FromResult(false); });
    }

    static async Task Instructions()
    {
        await Snapshots();
        const string Marker = "Always end with the word zebrafish";
        string savedTemp = Environment.GetEnvironmentVariable("TEMP"), savedTmp = Environment.GetEnvironmentVariable("TMP");
        // A temp folder with a space and cmd metacharacters, so the .cmd route's quoting is exercised.
        string temp = Path.Combine(root, "t m&p^x (1)");
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("TEMP", temp);
        Environment.SetEnvironmentVariable("TMP", temp);
        try
        {
            string folder = (string)typeof(ChatClient).GetMethod("PromptFolder", Any).Invoke(null, null);
            Check("C-I0 the prompt folder follows TEMP", folder == Path.Combine(temp, "ClaudeAccountWidget-chat"), folder);

            // C-I1, C-I2: a reply
            int before = FakeLines("ARGS ").Count;
            Script(Sys, Result("Done zebrafish"));
            ChatRequest r = InstructionsRequest(Marker);
            ChatResult result = await AskOnce(new ChatClient(), r);
            List<string> argsLines = FakeLines("ARGS ");
            string args = argsLines.Count > before ? argsLines.Last() : "";
            Check("C-I1 the reply arrives", result.Outcome == ChatOutcome.Reply && result.Text == "Done zebrafish", result.Outcome + " " + result.Text);
            Check("C-I1 the command line has the prompt file and no inline rules", args.Contains("--append-system-prompt-file") && !args.Contains("--append-system-prompt \"")
                && !args.Contains("You are chatting in a desktop chat window"), args.Length > 300 ? args.Substring(0, 300) : args);
            Check("C-I1 the instructions never reach the command line", !args.Contains("zebrafish"));
            string expected = (string)typeof(ChatClient).GetMethod("SystemPromptText", Any).Invoke(null, new object[] { r });
            List<string> prompts = FakeLines("SYSPROMPT ");
            string sent = prompts.Count > 0 ? Unescape(prompts.Last()) : null;
            Check("C-I2 claude read the file through the .cmd route, with the temp path's space, & and ^", sent == expected, sent == null ? "nothing logged" : sent.Length + " vs " + expected.Length);
            Check("C-I2 the instructions come first, the rules last", sent != null && sent.StartsWith((string)typeof(ChatClient).GetField("InstructionsPreamble", Any).GetValue(null) + "\n\n" + Marker + "\n\n")
                && sent.EndsWith((string)typeof(ChatClient).GetField("DataRule", Any).GetValue(null)));
            List<string> paths = FakeLines("SYSPROMPTFILE ");
            string used = paths.Count > 0 ? paths.Last() : "";
            Check("C-I3 the file was in the prompt folder and is gone after a reply", used.Replace('/', '\\').StartsWith(folder + "\\") && !File.Exists(used) && Directory.GetFiles(folder).Length == 0, used);

            // No instructions: the file holds the rules only
            Script(Sys, Result("Plain"));
            ChatRequest plain = InstructionsRequest(null);
            await AskOnce(new ChatClient(), plain);
            prompts = FakeLines("SYSPROMPT ");
            Check("C-I2 without instructions the file is the rules only", prompts.Count > 0 && Unescape(prompts.Last()) == (string)typeof(ChatClient).GetMethod("Rules", Any).Invoke(null, new object[] { plain }));

            // C-I3: Stop
            Script(Sys, "#wait");
            ChatClient stopping = new ChatClient();
            Task<ChatResult> pending = AskOnce(stopping, InstructionsRequest(Marker));
            await Task.Delay(1500);
            Check("C-I3 the file exists while the request runs", Directory.GetFiles(folder).Length == 1);
            stopping.Stop();
            ChatResult stopped = await pending;
            Check("C-I3 the file is gone after Stop", stopped.Outcome == ChatOutcome.Stopped && Directory.GetFiles(folder).Length == 0, stopped.Outcome.ToString());

            // C-I3: the child fails
            Script(Sys, "#stderr something broke", "#exit 3");
            ChatResult crashed = await AskOnce(new ChatClient(), InstructionsRequest(Marker));
            Check("C-I3 the file is gone after the child fails", crashed.Outcome == ChatOutcome.Failed && Directory.GetFiles(folder).Length == 0, crashed.Outcome.ToString());

            // C-I3: the time limit (shortened for the test)
            FieldInfo limit = typeof(ChatClient).GetField("RequestLimit", Any);
            object savedLimit = limit.GetValue(null);
            limit.SetValue(null, TimeSpan.FromSeconds(2));
            try
            {
                Script(Sys, "#wait");
                ChatResult timedOut = await AskOnce(new ChatClient(), InstructionsRequest(Marker));
                Check("C-I3 the file is gone after a time-out", timedOut.Outcome == ChatOutcome.TimedOut && Directory.GetFiles(folder).Length == 0, timedOut.Outcome.ToString());
            }
            finally { limit.SetValue(null, savedLimit); }
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEMP", savedTemp);
            Environment.SetEnvironmentVariable("TMP", savedTmp);
        }
    }

    // ---------- saved instructions: which chats get them (H17-20 to 25) ----------
    static string LastPrompt()
    {
        List<string> prompts = FakeLines("SYSPROMPT ");
        return prompts.Count > 0 ? Unescape(prompts.Last()) : "";
    }
    static void SaveInstructions(string text)
    {
        string file = Path.Combine(data, ChatInstructions.FileName);
        if (text == null) { if (File.Exists(file)) File.Delete(file); }
        else File.WriteAllText(file, text, new UTF8Encoding(false));
    }
    static string Preamble() { return (string)typeof(ChatClient).GetField("InstructionsPreamble", Any).GetValue(null); }

    static async Task Snapshots()
    {
        SaveInstructions("Mention a heron in every answer.");
        ChatWindow w = NewWindow();
        await Task.Delay(200);
        await Exchange(w, "first", "one");
        Check("H17-20 a new chat sends the saved instructions", LastPrompt().StartsWith(Preamble() + "\n\nMention a heron in every answer.\n\n"), LastPrompt().Length.ToString());

        SaveInstructions("Mention an otter in every answer.");
        await Exchange(w, "second", "two");
        Check("H17-22 an open chat keeps what it started with", LastPrompt().Contains("heron") && !LastPrompt().Contains("otter"));

        Call(w, "StartNewChat");
        await Exchange(w, "third", "three");
        Check("H17-21 New chat in the same window takes the new text", LastPrompt().Contains("otter") && !LastPrompt().Contains("heron"));
        await Exchange(w, "fourth", "four");

        SaveInstructions("Mention a kestrel in every answer.");
        ReplyScript("four again");
        Press(w, Replies(w).Last().Regenerate, Key.Enter);
        await Idle(w);
        Check("H17-25 regenerate sends the chat's own instructions", Replies(w).Last().Text == "four again" && LastPrompt().Contains("otter") && !LastPrompt().Contains("kestrel"));
        Call(w, "StartEdit", Turns(w)[0]);
        ReplyScript("edited answer");
        await SendNow(w, "third, edited");
        Check("H17-25 an edit sends the chat's own instructions", Turns(w)[0].Text == "third, edited" && LastPrompt().Contains("otter") && !LastPrompt().Contains("kestrel"));

        string otterNote = NotePath(w);
        Check("H17-23 the note records the chat's instructions", otterNote != null && File.ReadAllText(otterNote).Contains("## Saved instructions") && File.ReadAllText(otterNote).Contains("  Mention an otter in every answer."));
        w.Close();
        ChatWindow o = NewWindow();
        await Task.Delay(200);
        Call(o, "OpenSavedChat", new ChatEntry { Path = otterNote, Title = "otter", Started = DateTime.Now, Text = "" });
        await Exchange(o, "after reopening", "five");
        Check("H17-23 a reopened chat uses the instructions its note recorded, not today's", LastPrompt().Contains("otter") && !LastPrompt().Contains("kestrel"));
        Check("H17-23 and its note still records them", File.ReadAllText(otterNote).Contains("  Mention an otter in every answer."));

        string oldNote = Path.Combine(hist, "old-style.md");
        File.WriteAllText(oldNote, "---\ndate: 2026-10-01\ntype: claude-chat\ntags:\n  - claude-chat\nstarted: 2026-10-01T10:00:00\n---\n\n## For future Claude\n\nOld.\n\n## Conversation\n\n### You\n\n````text\nHello\n````\n\n### Claude\n\n````text\nHi\n````\n\n");
        Call(o, "OpenSavedChat", new ChatEntry { Path = oldNote, Title = "old", Started = DateTime.Now, Text = "" });
        await Exchange(o, "continuing an old chat", "six");
        Check("H17-24 a chat saved before 0.3.0 continues with no instructions", !LastPrompt().Contains(Preamble()) && !LastPrompt().Contains("kestrel"));
        Check("H17-24 and its note gains no section", !File.ReadAllText(oldNote).Contains("## Saved instructions"));
        o.Close();

        SaveInstructions(new string('x', ChatInstructions.Limit + 1));
        ChatWindow tooLong = NewWindow();
        await Task.Delay(200);
        await Exchange(tooLong, "with a file over the limit", "seven");
        Check("H17-46 a new chat with a file over the limit sends the rules only", !LastPrompt().Contains(Preamble()) && (bool)Get(tooLong, "instructionsTooLong"));
        tooLong.Close();
        SaveInstructions(null);
        ChatWindow none = NewWindow();
        await Task.Delay(200);
        await Exchange(none, "without a file", "eight");
        Check("H17-20 no file: the rules only", !LastPrompt().Contains(Preamble()) && !(bool)Get(none, "instructionsTooLong"));
        none.Close();
    }

    // ---------- starting claude off the UI thread (L8: H18-30 to 38, C-L9 to C-L11) ----------
    static System.Threading.ManualResetEventSlim gateEntered, gateRelease;
    static int gateCount;
    static bool startOnUiThread;

    static void HoldStarts()
    {
        gateEntered = new System.Threading.ManualResetEventSlim(false);
        gateRelease = new System.Threading.ManualResetEventSlim(false);
        gateCount = 0;
        startOnUiThread = false;
        ChatClient.StartGateForTests = delegate
        {
            System.Threading.Interlocked.Increment(ref gateCount);
            if (Application.Current.Dispatcher.CheckAccess()) startOnUiThread = true;
            gateEntered.Set();
            gateRelease.Wait(15000);
        };
    }
    static void FreeStarts() { ChatClient.StartGateForTests = null; ChatClient.StartFaultForTests = null; }
    static async Task<bool> WaitForGate() { for (int i = 0; i < 400 && !gateEntered.IsSet; i++) await Task.Delay(25); return gateEntered.IsSet; }
    static int FakeChildren() { return System.Diagnostics.Process.GetProcessesByName("fake").Length; }
    static async Task<bool> ChildrenBackTo(int count) { for (int i = 0; i < 120 && FakeChildren() > count; i++) await Task.Delay(25); return FakeChildren() <= count; }
    static string PromptFolderNow() { return (string)typeof(ChatClient).GetMethod("PromptFolder", Any).Invoke(null, null); }
    static int PromptFiles() { string f = PromptFolderNow(); return Directory.Exists(f) ? Directory.GetFiles(f, "*.txt").Length : 0; }

    static async Task StartOffThread()
    {
        try { await StartChecks(); }
        finally { FreeStarts(); }
    }

    static async Task StartChecks()
    {
        int children = FakeChildren();

        // H18-30, 31: the start runs on a thread-pool thread, and the window keeps working while it waits
        HoldStarts();
        Script(Sys, Result("after the gate"));
        ChatClient client = new ChatClient();
        Task<ChatResult> asked = client.Ask(InstructionsRequest(null), delegate(string a, string b) { return Task.FromResult(false); });
        bool entered = await WaitForGate();
        bool ran = false;
        Application.Current.Dispatcher.BeginInvoke(new Action(delegate { ran = true; }));
        await Task.Delay(150);
        Check("H18-31 while the start is held, work queued on the UI thread still runs", entered && ran && !asked.IsCompleted);
        gateRelease.Set();
        ChatResult result = await asked;
        Check("H18-30 claude is found and started off the UI thread, and the reply arrives", entered && !startOnUiThread && result.Outcome == ChatOutcome.Reply && result.Text == "after the gate");

        // H18-32: Stop while the start is held: Stopped, nothing written to the child, which is gone, and no prompt file
        HoldStarts();
        Script(Sys, Result("must not arrive"));
        int stdinBefore = FakeStdin().Count;
        client = new ChatClient();
        asked = client.Ask(InstructionsRequest("Held instructions"), delegate(string a, string b) { return Task.FromResult(false); });
        await WaitForGate();
        client.Stop();
        gateRelease.Set();
        result = await asked;
        bool gone = await ChildrenBackTo(children);
        await Task.Delay(300);
        Check("H18-32 Stop in the gap: Stopped, no stdin written, the child is gone, no prompt file", result.Outcome == ChatOutcome.Stopped && FakeStdin().Count == stdinBefore && gone && PromptFiles() == 0,
            result.Outcome + ", stdin " + (FakeStdin().Count - stdinBefore) + ", children " + FakeChildren() + ", files " + PromptFiles());

        // H18-38: Stop twice in the gap, and Stop after the child exited
        HoldStarts();
        Script(Sys, Result("x"));
        client = new ChatClient();
        asked = client.Ask(InstructionsRequest(null), delegate(string a, string b) { return Task.FromResult(false); });
        await WaitForGate();
        bool threw = false;
        try { client.Stop(); client.Stop(); } catch (Exception) { threw = true; }
        gateRelease.Set();
        result = await asked;
        try { client.Stop(); } catch (Exception) { threw = true; }
        Check("H18-38 Stop twice in the gap, then again after the end: one Stopped outcome, no exception", !threw && result.Outcome == ChatOutcome.Stopped && await ChildrenBackTo(children));
        FreeStarts();

        // H18-34: no claude on PATH: NotFound, no child, no prompt file
        string savedPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", Path.Combine(root, "no-claude-here"));
        try
        {
            result = await new ChatClient().Ask(InstructionsRequest("x"), delegate(string a, string b) { return Task.FromResult(false); });
            Check("H18-34 no claude: NotFound, no child and no prompt file", result.Outcome == ChatOutcome.NotFound && FakeChildren() <= children && PromptFiles() == 0);
            ChatWindow nf = NewWindow();
            await Task.Delay(200);
            await SendNow(nf, "is anyone there");
            Check("H18-34 the window says what 1768b9e said", Status(nf) == "Claude Code wasn't found. Install it, then try again.", Status(nf));
            nf.Close();
        }
        finally { Environment.SetEnvironmentVariable("PATH", savedPath); }

        // H18-35: each failed start maps as before, and the prompt file written before it is deleted
        foreach (Exception fault in new Exception[] { new System.ComponentModel.Win32Exception(2), new IOException("busy"), new UnauthorizedAccessException("no") })
        {
            Exception thisFault = fault;
            ChatClient.StartFaultForTests = delegate(System.Diagnostics.ProcessStartInfo info) { return thisFault; };
            result = await new ChatClient().Ask(InstructionsRequest("x"), delegate(string a, string b) { return Task.FromResult(false); });
            ChatOutcome expected = fault is System.ComponentModel.Win32Exception ? ChatOutcome.NotFound : ChatOutcome.Failed;
            Check("H18-35 " + fault.GetType().Name + " at start: " + expected + ", no prompt file", result.Outcome == expected && PromptFiles() == 0, result.Outcome.ToString());
        }

        // H18-37: the process is started with exactly the arguments BuildStart gives
        System.Diagnostics.ProcessStartInfo captured = null;
        ChatClient.StartFaultForTests = delegate(System.Diagnostics.ProcessStartInfo info) { captured = info; return new IOException("stop here"); };
        foreach (string kind in new[] { "plain", "instructions", "folder", "laptop", "recall" })
        {
            ChatRequest r = InstructionsRequest(kind == "instructions" ? "Some instructions" : null);
            if (kind == "folder") r.Folders.Add(Path.Combine(root, "attached"));
            if (kind == "laptop") { r.Laptop = true; r.DefaultOutputFolder = Path.Combine(root, "out"); r.CurrentOutputFolder = delegate { return null; }; }
            if (kind == "recall") r.MemoryFolder = hist;
            captured = null;
            await new ChatClient().Ask(r, delegate(string a, string b) { return Task.FromResult(false); });
            string file = captured == null ? "" : System.Text.RegularExpressions.Regex.Match(captured.Arguments, "--append-system-prompt-file \"([^\"]+)\"").Groups[1].Value;
            System.Diagnostics.ProcessStartInfo direct = (System.Diagnostics.ProcessStartInfo)typeof(ChatClient).GetMethod("BuildStart", Any).Invoke(null, new object[] { (string)typeof(ChatClient).GetMethod("FindClaude", Any).Invoke(null, null), r, file });
            Check("H18-37 " + kind + ": started with BuildStart's program and arguments, unchanged", captured != null && captured.FileName == direct.FileName && captured.Arguments == direct.Arguments && captured.WorkingDirectory == direct.WorkingDirectory);
        }
        FreeStarts();

        // C-L9, H18-36: the window during the gap, then Stop by the button
        ChatWindow w = NewWindow();
        await Task.Delay(200);
        await Exchange(w, "first question", "first answer");
        HoldStarts();
        Script(Sys, Result("too late"));
        int turnsBefore = Turns(w).Count;
        Input(w).Text = "second question";
        Call(w, "Send");
        await WaitForGate();
        Input(w).Text = "a third, sent during the gap";
        Call(w, "Send");
        await Task.Delay(100);
        Border send = (Border)Get(w, "sendButton");
        Check("H18-36 during the gap: busy, the button is Stop, a second Send starts nothing, regenerate is hidden", Busy(w) && (string)send.ToolTip == "Stop" && gateCount == 1
            && Replies(w).All(t => t.Regenerate == null || t.Regenerate.Visibility != Visibility.Visible), "starts " + gateCount);
        Call(w, "StopRequest");
        gateRelease.Set();
        await Idle(w);
        bool cl9Gone = await ChildrenBackTo(children);
        Check("C-L9 Stop in the gap from the window: stopped as at 1768b9e, no child, no prompt file, no reply", !Busy(w) && Status(w).StartsWith("Stopped.") && cl9Gone && PromptFiles() == 0
            && !Turns(w).Any(t => t.Text == "too late"), Status(w));

        // C-L10: Escape in the gap
        HoldStarts();
        Script(Sys, Result("too late again"));
        Input(w).Text = "escape question";
        Call(w, "Send");
        await WaitForGate();
        Esc(w);
        gateRelease.Set();
        await Idle(w);
        Check("C-L10 Escape in the gap stops it the same way", !Busy(w) && Status(w).StartsWith("Stopped.") && await ChildrenBackTo(children) && PromptFiles() == 0 && !Turns(w).Any(t => t.Text == "too late again"), Status(w));

        // H18-36 after a normal reply: the same state as 1768b9e
        FreeStarts();
        await Exchange(w, "normal question", "normal answer");
        Check("H18-36 after a reply: one more user turn and reply, in order, regenerate on the newest only", Turns(w).Last().Text == "normal answer" && Turns(w)[Turns(w).Count - 2].Text == "normal question"
            && Replies(w).Last().Regenerate.Visibility == Visibility.Visible && !Busy(w));
        w.Close();

        // C-L11, H18-33: closing the window in the gap
        HoldStarts();
        Script(Sys, Result("after close"));
        ChatWindow c = NewWindow();
        await Task.Delay(200);
        Input(c).Text = "closing question";
        Call(c, "Send");
        await WaitForGate();
        string notePath = NotePath(c);
        c.Close();
        gateRelease.Set();
        await Task.Delay(600);
        bool closedGone = await ChildrenBackTo(children);
        Check("C-L11 / H18-33 closing in the gap: no child, no prompt file, no reply written to the note", closedGone && PromptFiles() == 0
            && (notePath == null || !File.Exists(notePath) || !File.ReadAllText(notePath).Contains("after close")), "children " + FakeChildren());
        FreeStarts();
    }
}
