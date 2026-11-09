using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Documents;

static class H2
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static string root, p2;
    static WidgetWindow widget;
    static int fails;
    static object Get(object o, string f) { return (o is Type ? (Type)o : o.GetType()).GetField(f, Any).GetValue(o is Type ? null : o); }
    static object Call(object o, string m, params object[] a) { return o.GetType().GetMethod(m, Any).Invoke(o, a); }
    static void Log(string s) { Console.WriteLine(s); Console.Out.Flush(); }
    static void Check(string what, bool ok, string info = "") { Log((ok ? "PASS " : "FAIL ") + what + (info.Length > 0 ? " [" + info + "]" : "")); if (!ok) fails++; }
    static void Theme(string theme) { typeof(WidgetWindow).GetField("themeSetting", Any).SetValue(widget, theme); Call(widget, "ApplyTheme"); Call(widget, "ApplyAccent"); }
    static void Shot(FrameworkElement e, string name)
    {
        e.UpdateLayout();
        RenderTargetBitmap bmp = new RenderTargetBitmap((int)e.ActualWidth, (int)e.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(e);
        PngBitmapEncoder png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using (FileStream f = File.Create(Path.Combine(root, name + ".png"))) png.Save(f);
    }
    [STAThread]
    static void Main(string[] args)
    {
        root = args[1]; p2 = args[2];
        string data = Path.Combine(root, "data");
        if (Directory.Exists(data)) Directory.Delete(data, true);
        Directory.CreateDirectory(data);
        string account = Path.Combine(root, "account.json");
        File.WriteAllText(account, "{\"oauthAccount\":{\"emailAddress\":\"sample@example.com\",\"planDisplayName\":\"Ultra\"}}");
        Application app = new Application();
        app.ShutdownMode = ShutdownMode.OnMainWindowClose;
        app.DispatcherUnhandledException += (s, e) => { Log("UNHANDLED " + e.Exception); fails++; e.Handled = true; };
        widget = new WidgetWindow(data, account, null, null, 300, 0);
        widget.ShowActivated = false;
        widget.Loaded += async delegate
        {
            widget.Left = -6000;
            try { await Run(args[0], data); }
            catch (Exception e) { Log("HARNESS ERROR " + e); fails++; }
            Log("fails=" + fails);
            widget.Close();
        };
        app.Run(widget);
        Log("app ended");
        foreach (System.Diagnostics.ProcessThread th in System.Diagnostics.Process.GetCurrentProcess().Threads) { }
        System.Diagnostics.Process.GetCurrentProcess().Kill();
    }
    static string F(string n) { return Path.Combine(p2, n); }
    static async Task<T> Wait<T>(FilePreviewPane pane, int ms = 60000) where T : class
    {
        for (int i = 0; i < ms / 100; i++) { T b = pane.Body as T; if (b != null) { await Task.Delay(500); return b; } await Task.Delay(100); }
        Log("timeout waiting for " + typeof(T).Name + ", body=" + (pane.Body == null ? "null" : pane.Body.GetType().Name + " " + (pane.Body is MessageBody ? ((MessageBody)pane.Body).Message : "")));
        return null;
    }
    static string BoxText(FilePreviewPane pane)
    {
        RichTextBox box = (RichTextBox)Get((TextBody)pane.Body, "box");
        return new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text;
    }
    static async Task Run(string mode, string data)
    {
        ChatWindow w = new ChatWindow(data);
        w.ShowActivated = false; w.Left = -6000; w.Top = 50; w.Width = 1560; w.Height = 960;
        w.Show();
        await Task.Delay(300);
        StackPanel messages = (StackPanel)Get(w, "messages");
        string[] files = { "report.pdf", "letter.docx", "deck.pptx", "book.xlsx", "notes.md", "page.html", "data.csv", "tool.py", "big.png", "pic.svg" };
        Grid chat = (Grid)Get(w, "chatArea");
        foreach (string n in files) messages.Children.Add((UIElement)Call(w, "BuildFileCard", F(n)));
        chat.UpdateLayout();
        ((UIElement)Get(w, "emptyState")).Visibility = Visibility.Collapsed;
        if (mode == "cards" || mode == "all")
        {
            int shown = 0;
            for (int t = 0; t < 900 && shown < files.Length; t++)
            {
                await Task.Delay(100);
                shown = 0;
                foreach (UIElement u in messages.Children) { Border c = u as Border; if (c == null) continue; StackPanel st = c.Child as StackPanel; if (st != null && ((Border)st.Children[0]).Visibility == Visibility.Visible) shown++; }
            }
            Check("cards with thumbnails", shown == files.Length, shown + "/" + files.Length);
            Shot((FrameworkElement)w.Content, "cards-top");
            ScrollViewer sv = FindScroll(messages);
            if (sv != null) { sv.ScrollToEnd(); await Task.Delay(400); Shot((FrameworkElement)w.Content, "cards-end"); sv.ScrollToHome(); }
        }
        if (mode == "cards") { w.Close(); return; }
        if (mode.StartsWith("m-"))
        {
            Call(w, "ShowPreviewPanel", F(mode == "m-table" ? "data.csv" : mode == "m-text" ? "notes.md" : "report.pdf"));
            FilePreviewPane pn = (FilePreviewPane)Get(w, "previewPane");
            await Task.Delay(3000);
            if (mode == "m-ocr") await ((PageViewer)pn.Body).SelectPage(0);
            if (mode == "m-print") await ((PageViewer)pn.Body).BuildPrint(new Size(816, 1056));
            if (mode == "m-popup") { Call(pn, "ShowVersions"); await Task.Delay(300); ((System.Windows.Controls.Primitives.Popup)Get(pn, "versionPopup")).IsOpen = false; }
            if (mode == "m-float") { Call(w, "ClosePreviewPanel"); FilePreviewWindow fw2 = new FilePreviewWindow(F("report.pdf"), w, () => (List<string>)Get(w, "chatFiles"), delegate { }); fw2.ShowActivated = false; fw2.Left = -5000; fw2.Show(); await Task.Delay(3000); fw2.Close(); }
            if (mode == "m-tableprint") { }
            w.Close();
            return;
        }

        Call(w, "ShowPreviewPanel", F("report.pdf"));
        FilePreviewPane pane = (FilePreviewPane)Get(w, "previewPane");
        PageViewer pv = await Wait<PageViewer>(pane);
        await Task.Delay(1500);
        Check("pdf page viewer", pv != null);
        Check("details", pane.DetailLine.Contains("PDF document") && pane.DetailLine.Contains("6 pages"), pane.DetailLine);
        Shot((FrameworkElement)w.Content, "panel-pdf");
        pv.GoTo(2); await Task.Delay(1200);
        Check("page label", ((TextBlock)Get(pv, "pageLabel")).Text == "Page 3 of 6", ((TextBlock)Get(pv, "pageLabel")).Text);
        Call(pv, "ZoomBy", 1.5); await Task.Delay(1000);
        Check("zoom label", ((TextBlock)Get(pv, "zoomLabel")).Text.EndsWith("%"), ((TextBlock)Get(pv, "zoomLabel")).Text);
        Shot((FrameworkElement)w.Content, "panel-pdf-zoom");
        typeof(PageViewer).GetField("fit", Any).SetValue(pv, true); Call(pv, "ApplyZoom"); await Task.Delay(500);

        pane.SetFindText("needle phrase");
        for (int t = 0; t < 400 && !((TextBlock)Get(pane, "findCount")).Text.Contains(" of 2") && ((TextBlock)Get(pane, "findCount")).Text != "No matches"; t++) await Task.Delay(100);
        Check("find two words across pages", ((TextBlock)Get(pane, "findCount")).Text.Contains("of 2"), ((TextBlock)Get(pane, "findCount")).Text);
        await Task.Delay(800);
        Shot((FrameworkElement)w.Content, "panel-pdf-find");
        pane.StepFind(1); await Task.Delay(800);
        Check("find step wraps", ((TextBlock)Get(pane, "findCount")).Text == "1 of 2", ((TextBlock)Get(pane, "findCount")).Text);
        pane.CloseFind();

        await pv.SelectPage(0); await Task.Delay(300);
        string sel = pv.SelectedText;
        Check("selection text", sel.Contains("Report page 1"), sel.Substring(0, Math.Min(60, sel.Length)).Replace("\n", " / "));
        Check("selection bar visible", ((Border)Get(pane, "selectionBar")).Visibility == Visibility.Visible);
        Shot((FrameworkElement)w.Content, "panel-pdf-select");
        pv.ClearSelection();
        pv.GoTo(0); await Task.Delay(700); await pv.SelectPage(0); await Task.Delay(300); Shot((FrameworkElement)w.Content, "panel-select-visible"); pv.ClearSelection();
        Call(pane, "ShowBody", new LoadingBody()); await Task.Delay(200); Shot((FrameworkElement)w.Content, "panel-loading"); Call(pane, "ShowBody", pv);
        Call(w, "AskAboutSelection", "report.pdf", "some words");
        Check("ask fills input", ((TextBox)Get(w, "input")).Text.Contains("About report.pdf:") && ((TextBox)Get(w, "input")).Text.Contains("some words"));
        Shot((FrameworkElement)w.Content, "panel-ask");
        ((TextBox)Get(w, "input")).Text = "";

        DocumentPaginator pg = await pv.BuildPrint(new Size(816, 1056));
        Check("print pages (pdf)", pg.PageCount == 6, pg.PageCount.ToString());

        pane.Open(F("letter.docx")); await Task.Delay(200); PageViewer dv = await Wait<PageViewer>(pane); await Task.Delay(1000);
        Check("docx via pages", dv != null, pane.DetailLine);
        Shot((FrameworkElement)w.Content, "panel-docx");
        Check("file position", ((TextBlock)Get(pane, "positionText")).Text == "2 of 10", ((TextBlock)Get(pane, "positionText")).Text);
        pane.Step(1);
        await Task.Delay(200);
        PageViewer sv2 = await Wait<PageViewer>(pane); await Task.Delay(1000);
        Check("arrow moved to deck", Path.GetFileName(pane.CurrentFile) == "deck.pptx", pane.CurrentFile);
        for (int i = 0; i < 100 && !pane.DetailLine.Contains("slides"); i++) await Task.Delay(100);
        Check("slides details", pane.DetailLine.Contains("4 slides"), pane.DetailLine);
        PresentationSource src = PresentationSource.FromVisual(w);
        sv2 = (PageViewer)pane.Body;
        sv2.HandleKey(new KeyEventArgs(Keyboard.PrimaryDevice, src, 0, Key.Right)); await Task.Delay(800);
        Check("right arrow next slide", ((TextBlock)Get(sv2, "pageLabel")).Text == "Slide 2 of 4", ((TextBlock)Get(sv2, "pageLabel")).Text);
        Shot((FrameworkElement)w.Content, "panel-pptx");

        pane.Open(F("book.xlsx")); await Task.Delay(300);
        PageViewer xv = null;
        for (int i = 0; i < 600; i++) { xv = pane.Body as PageViewer; if (xv != null && pane.DetailLine.Contains("sheet")) break; await Task.Delay(100); }
        await Task.Delay(1000);
        Check("sheets: hidden skipped", pane.DetailLine.Contains("2 sheets"), pane.DetailLine);
        List<Border> tabs = (List<Border>)Get(xv, "tabs");
        Check("sheet tab names", tabs.Count == 2 && ((TextBlock)tabs[0].Child).Text == "Sales" && ((TextBlock)tabs[1].Child).Text == "Costs", string.Join(",", tabs.Select(t => ((TextBlock)t.Child).Text)));
        Shot((FrameworkElement)w.Content, "panel-xlsx");
        xv.GoTo(1); await Task.Delay(800); Shot((FrameworkElement)w.Content, "panel-xlsx2");

        pane.Open(F("notes.md")); await Task.Delay(100); TextBody md = await Wait<TextBody>(pane);
        Check("markdown body", md != null, pane.DetailLine);
        Shot((FrameworkElement)w.Content, "panel-md");
        int fc = await md.Find("needle", null); Check("md find", fc == 1, fc.ToString());
        Shot((FrameworkElement)w.Content, "panel-md-find");
        md.ClearFind();
        DocumentPaginator mp = await md.BuildPrint(new Size(816, 1056)); mp.ComputePageCount(); Check("md print", mp.PageCount >= 1, mp.PageCount.ToString());
        pane.SetSource(true); await Task.Delay(100); md = await Wait<TextBody>(pane); Shot((FrameworkElement)w.Content, "panel-md-code");

        string live = F("live.md");
        File.WriteAllText(live, "# First\n\nline a\nline b\n");
        pane.Open(live); await Task.Delay(100); md = await Wait<TextBody>(pane); await Task.Delay(800);
        File.WriteAllText(live, "# Second\n\nline a\nline c\nline d\n");
        await Task.Delay(1800);
        string liveText = BoxText(pane);
        Check("live refresh", liveText.Contains("Second"), liveText.Substring(0, Math.Min(20, liveText.Length)).Trim());
        List<FileVersion> vs = FileVersions.List(live);
        Check("two versions", vs.Count == 2, vs.Count.ToString());
        Check("version button", ((Border)Get(pane, "versionButton")).Visibility == Visibility.Visible, ((TextBlock)Get(pane, "versionLabel")).Text);
        pane.ShowVersion(vs[0]); await Task.Delay(1000);
        Check("old version shown", BoxText(pane).Contains("First"), pane.DetailLine);
        pane.Compare(vs[0]); await Task.Delay(1000);
        Check("compare details", pane.DetailLine.Contains("added"), pane.DetailLine);
        Shot((FrameworkElement)w.Content, "panel-diff");
        Call(pane, "ShowVersions"); await Task.Delay(300);
        Check("version menu rows", ((StackPanel)Get(pane, "versionRows")).Children.Count == 2);
        ((System.Windows.Controls.Primitives.Popup)Get(pane, "versionPopup")).IsOpen = false;

        string cmp = F("cmp.pdf"); File.Copy(F("report.pdf"), cmp, true);
        pane.Open(cmp); await Task.Delay(100); await Wait<PageViewer>(pane); await Task.Delay(800);
        File.Copy(Path.Combine(Path.GetDirectoryName(p2), "page-samples", "sample.pdf"), cmp, true);
        await Task.Delay(2500);
        List<FileVersion> cvs = FileVersions.List(cmp);
        Check("pdf versions", cvs.Count == 2, cvs.Count.ToString());
        pane.Compare(cvs[0]); await Task.Delay(100); CompareBody cb = await Wait<CompareBody>(pane); await Task.Delay(1500);
        Check("side by side compare", cb != null, pane.DetailLine);
        Shot((FrameworkElement)w.Content, "panel-compare-pages");
        pane.Open(F("page.html")); await Task.Delay(100); PageViewer hv = await Wait<PageViewer>(pane); await Task.Delay(1200);
        Check("html as page", hv != null, pane.DetailLine);
        Shot((FrameworkElement)w.Content, "panel-html");
        await hv.SelectPage(0); string ht = hv.SelectedText; hv.ClearSelection();
        Check("script did not run", ht.Contains("Static") && !ht.Contains("SCRIPT"), ht.Replace("\n", " / "));
        pane.SetSource(true); await Task.Delay(100); await Wait<TextBody>(pane); Shot((FrameworkElement)w.Content, "panel-html-code");

        pane.Open(F("pic.svg")); await Task.Delay(100); await Wait<PageViewer>(pane); await Task.Delay(800); Shot((FrameworkElement)w.Content, "panel-svg");

        pane.Open(F("arabic.html")); await Task.Delay(100); hv = await Wait<PageViewer>(pane); await Task.Delay(800); await hv.SelectPage(0); ht = hv.SelectedText; hv.ClearSelection();
        Log("arabic text: " + new string(ht.Take(50).Select(c => c).ToArray())); Log("arabic ocr: " + ht.Length + " chars, arabic=" + ht.Count(c => c >= 0x600 && c <= 0x6FF));
        Check("arabic text read", ht.Count(c => c >= 0x600 && c <= 0x6FF) > 20);

        pane.Open(F("data.csv")); await Task.Delay(100); TableBody tb = await Wait<TableBody>(pane);
        Check("csv rows", tb != null && tb.RowCount == 203 && tb.ColumnCount == 3, tb == null ? "" : pane.DetailLine);
        fc = await tb.Find("needle", null); Check("csv find", fc == 1, fc.ToString()); await Task.Delay(300);
        Check("csv selected", tb.SelectedText.Contains("needle"), tb.SelectedText);
        Shot((FrameworkElement)w.Content, "panel-csv");
        tb.ClearFind();
        mp = await tb.BuildPrint(new Size(816, 1056)); mp.ComputePageCount(); Check("csv print", mp.PageCount >= 1, mp.PageCount.ToString());

        pane.Open(F("tool.py")); await Task.Delay(100); await Wait<TextBody>(pane); Shot((FrameworkElement)w.Content, "panel-code");
        Theme("light"); await Task.Delay(1200); Shot((FrameworkElement)w.Content, "panel-code-light");
        pane.Open(F("data.csv")); await Task.Delay(100); await Wait<TableBody>(pane); Shot((FrameworkElement)w.Content, "panel-csv-light");
        pane.Open(F("report.pdf")); await Task.Delay(100); await Wait<PageViewer>(pane); await Task.Delay(1200); Shot((FrameworkElement)w.Content, "panel-pdf-light");
        Theme("dark"); await Task.Delay(800);

        pane.Open(F("big.png")); await Task.Delay(100); ImageBody ib = await Wait<ImageBody>(pane); await Task.Delay(400);
        double fitZoom = ib.Zoom; Check("image fits", fitZoom < 1, fitZoom.ToString("0.00") + " " + pane.DetailLine);
        Shot((FrameworkElement)w.Content, "panel-image");
        ib.ActualSize(); await Task.Delay(300); Check("actual size", ib.Zoom == 1);
        ib.ZoomAround(1.5, null); await Task.Delay(300); Check("zoom in", Math.Abs(ib.Zoom - 1.5) < 0.01);
        Shot((FrameworkElement)w.Content, "panel-image-zoom");

        Call(w, "TogglePreviewFull"); await Task.Delay(500);
        Check("preview full hides chat", ((Grid)Get(w, "chatArea")).Visibility == Visibility.Collapsed);
        Shot((FrameworkElement)w.Content, "panel-full");
        Call(w, "TogglePreviewFull"); await Task.Delay(300);
        Check("preview full back", ((Grid)Get(w, "chatArea")).Visibility == Visibility.Visible);

        Call(w, "ClosePreviewPanel");
        FilePreviewWindow fw = new FilePreviewWindow(F("report.pdf"), w, () => (List<string>)Get(w, "chatFiles"), delegate { });
        fw.ShowActivated = false; fw.Left = -5000; fw.Top = 60; fw.Show();
        await Task.Delay(300);
        PageViewer fv = await Wait<PageViewer>(fw.Pane); await Task.Delay(1000);
        Check("floating window pages", fv != null, fw.Pane.DetailLine);
        Shot((FrameworkElement)fw.Content, "floating-pdf");
        fw.Close();
        w.Close();
    }
    static ScrollViewer FindScroll(DependencyObject d) { while (d != null) { if (d is ScrollViewer) return (ScrollViewer)d; d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d); } return null; }
}
