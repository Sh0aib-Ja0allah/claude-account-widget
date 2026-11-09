using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Text.RegularExpressions;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
// Long chats: fixture checks (H18-01) and the L1 timing harness (P-01 to P-10, m1 to m10). Timings are reported, never
// a pass or fail, unless "enforce" is given: then a median over 3 times its target fails (never in quick mode or CI).
// Windows stay off screen (test mode); the fake claude answers sends. Created files in the notes are pointed at small
// real files written to the scratch folder at run time, so their thumbnails load as they would for a user.
//   long-chats-perf.exe <out folder> <fixtures folder> <second fixtures folder> <fake claude folder> [runs] [enforce]
static class HP
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static string root, data, hist;
    static WidgetWindow widget;
    static int fails, passes;
    static readonly StringBuilder table = new StringBuilder();
    static readonly Dictionary<string, List<double>> results = new Dictionary<string, List<double>>();

    static object Get(object o, string f) { return o.GetType().GetField(f, Any).GetValue(o); }
    static object Call(object o, string m, params object[] a)
    {
        MethodInfo info = o.GetType().GetMethods(Any).First(x => x.Name == m && x.GetParameters().Length == a.Length);
        return info.Invoke(o, a);
    }
    static void Log(string s) { Console.WriteLine(s); Console.Out.Flush(); }
    static void Check(string what, bool ok, string info = "")
    {
        Log((ok ? "PASS " : "FAIL ") + what + (info.Length > 0 ? " [" + info.Replace("\n", "\\n") + "]" : ""));
        if (ok) passes++; else fails++;
    }
    static bool warmUp, enforce;
    static string filesFolder;
    static readonly List<string> createdFiles = new List<string>();
    static long idleHeap, idlePrivate;
    static void Record(string metric, string size, double value)
    {
        if (warmUp) return;
        string key = metric + " " + size;
        if (!results.ContainsKey(key)) results[key] = new List<double>();
        results[key].Add(value);
        Log("PERF " + key + " " + value.ToString("F1"));
    }
    static void Flush(Window w)
    {
        w.UpdateLayout();
        w.Dispatcher.Invoke(DispatcherPriority.Render, new Action(delegate { }));
        w.Dispatcher.Invoke(DispatcherPriority.Background, new Action(delegate { }));
    }
    static void Theme(string theme) { Set(widget, "themeSetting", theme); Call(widget, "ApplyTheme"); Call(widget, "ApplyAccent"); }
    static void Set(object o, string f, object v) { o.GetType().GetField(f, Any).SetValue(o, v); }

    [STAThread]
    static void Main(string[] args)
    {
        root = args[0];
        string fixtures = args[1], second = args[2], fakeDir = args[3];
        int runs = args.Length > 4 ? int.Parse(args[4]) : 5;
        enforce = args.Length > 5 && args[5] == "enforce";
        data = Path.Combine(root, "data");
        hist = Path.Combine(root, "hist");
        foreach (string d in new[] { data, hist }) { if (Directory.Exists(d)) Directory.Delete(d, true); Directory.CreateDirectory(d); }
        File.WriteAllText(Path.Combine(data, "chat-history-folder.txt"), hist);
        string account = Path.Combine(root, "account.json");
        File.WriteAllText(account, "{\"oauthAccount\":{\"emailAddress\":\"sample@example.com\",\"planDisplayName\":\"Max\"}}");
        Environment.SetEnvironmentVariable("PATH", fakeDir + ";" + Environment.GetEnvironmentVariable("PATH"));
        Environment.SetEnvironmentVariable("FAKE_LOG", Path.Combine(root, "fake-log.txt"));
        string script = Path.Combine(root, "reply.jsonl");
        File.WriteAllLines(script, new[] { "{\"type\":\"system\",\"subtype\":\"init\",\"model\":\"fake-model-1\"}", "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"Short answer.\"}" }, new UTF8Encoding(false));
        Environment.SetEnvironmentVariable("FAKE_SCRIPT", script);

        // H18-01: the generator is deterministic
        foreach (string name in new[] { "L100.md", "L300.md", "L600.md" })
        {
            byte[] a = File.ReadAllBytes(Path.Combine(fixtures, name)), b = File.ReadAllBytes(Path.Combine(second, name));
            Check("H18-01 " + name + ": two runs of the generator give the same bytes", a.SequenceEqual(b), a.Length + " bytes");
        }

        Application app = new Application();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.DispatcherUnhandledException += (s, e) => { Log("UNHANDLED " + e.Exception); fails++; e.Handled = true; };
        widget = new WidgetWindow(data, account, null, null, 300, 0, true);
        widget.ShowActivated = false;
        widget.WindowStartupLocation = WindowStartupLocation.Manual;
        widget.Left = -6000;
        widget.Loaded += async delegate
        {
            widget.Left = -6000;
            try { await Measure(fixtures, runs); }
            catch (Exception e) { Log("HARNESS ERROR " + e); fails++; }
            File.WriteAllText(Path.Combine(root, "results.md"), table.ToString(), new UTF8Encoding(false));
            Log("passes=" + passes + " fails=" + fails);
            Process.GetCurrentProcess().Kill();
        };
        widget.Show();
        app.Run();
    }

    // Six images and six text files, made here so nothing binary is committed.
    static void WriteFiles()
    {
        filesFolder = Path.Combine(root, "files");
        Directory.CreateDirectory(filesFolder);
        for (int i = 0; i < 6; i++)
        {
            string image = Path.Combine(filesFolder, "picture-" + i + ".png");
            DrawingVisual visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb((byte)(40 * i), 120, 200)), null, new Rect(0, 0, 800, 500));
                dc.DrawEllipse(Brushes.White, null, new Point(400, 250), 120 + 20 * i, 90);
            }
            RenderTargetBitmap bmp = new RenderTargetBitmap(800, 500, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            PngBitmapEncoder png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bmp));
            using (FileStream f = File.Create(image)) png.Save(f);
            string text = Path.Combine(filesFolder, "notes-" + i + ".txt");
            File.WriteAllText(text, "Notes " + i + "\nfirst line\nsecond line\nthird line\n", new UTF8Encoding(false));
            createdFiles.Add(image);
            createdFiles.Add(text);
        }
    }

    // Each created file in the note points at one of those files, in turn.
    static string PointAtFiles(string note)
    {
        int next = 0;
        return Regex.Replace(note, "^- `[a-z]+-report\\.docx`$", delegate(Match m)
        {
            return "- `" + createdFiles[next++ % createdFiles.Count].Replace('\\', '/') + "`";
        }, RegexOptions.Multiline);
    }

    // Image cards show a picture and text cards their opening lines once their thumbnails load.
    static int LoadedThumbnails(DependencyObject node)
    {
        int count = 0;
        Border b = node as Border;
        if (b != null && b.Background is ImageBrush) count++;
        TextBlock t = node as TextBlock;
        if (t != null && t.FontSize == 10.5 && t.FontFamily != null && t.FontFamily.Source == "Consolas") count++;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) count += LoadedThumbnails(VisualTreeHelper.GetChild(node, i));
        return count;
    }

    static ChatWindow NewChat()
    {
        ChatWindow w = new ChatWindow(data);
        w.ShowActivated = false;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -6000; w.Top = 50; w.Width = 441; w.Height = 700;
        w.Show();
        Flush(w);
        return w;
    }

    static async Task Measure(string fixtures, int runs)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        idleHeap = GC.GetTotalMemory(true);
        Process self = Process.GetCurrentProcess();
        self.Refresh();
        idlePrivate = self.PrivateMemorySize64;
        string month = Path.Combine(hist, "2026-01");
        Directory.CreateDirectory(month);
        WriteFiles();
        foreach (string size in new[] { "L100", "L300", "L600" })
        {
            string note = Path.Combine(month, "2026-01-15 0930 - " + size.ToLowerInvariant() + ".md");
            // A fresh copy before every run, because m9's send saves two more turns into the note.
            // One unrecorded warm-up run per size, so JIT compilation and first-use caches don't count.
            for (int run = -1; run < runs; run++)
            {
                File.WriteAllText(note, PointAtFiles(File.ReadAllText(Path.Combine(fixtures, size + ".md"))), new UTF8Encoding(false));
                await MeasureOnce(size, note, run);
            }
        }
        Summarise();
    }

    static async Task MeasureOnce(string size, string note, int run)
    {
        warmUp = run < 0;
        if (run < 0) run = 0;
        Process me = Process.GetCurrentProcess();
        Theme("dark");
        // m1: reading the note
        Stopwatch sw = Stopwatch.StartNew();
        List<ChatTurn> turns; DateTime started;
        bool read = ChatNoteWriter.TryRead(note, out turns, out started);
        sw.Stop();
        Record("m1-read", size, sw.Elapsed.TotalMilliseconds);
        if (run == 0) Check("P-01 " + size + ": the fixture reads as a chat", read && turns.Count == int.Parse(size.Substring(1)), turns.Count + " turns");
        List<string> replies = turns.Where(t => !t.FromUser).Select(t => t.Text).ToList();

        ChatWindow chat = NewChat();
        // m2: Markdown to FlowDocument for every reply, as RenderReply builds it
        sw = Stopwatch.StartNew();
        foreach (string text in replies)
        {
            TextColors colors = new TextColors(WidgetWindow.IsLight, false);
            colors.Compact = true;
            colors.BuildCode = (Func<string, string, Block>)Delegate.CreateDelegate(typeof(Func<string, string, Block>), chat, typeof(ChatWindow).GetMethod("BuildCodeBlock", Any));
            DocumentBuilder.Markdown(text, colors);
        }
        sw.Stop();
        Record("m2-markdown", size, sw.Elapsed.TotalMilliseconds);
        // m3: ReplyWidth for every reply
        sw = Stopwatch.StartNew();
        foreach (string text in replies) Call(chat, "ReplyWidth", text);
        sw.Stop();
        Record("m3-replywidth", size, sw.Elapsed.TotalMilliseconds);
        chat.Close();

        chat = NewChat();
        // m4: opening, which builds every turn on the UI thread (includes m2 and m3 work); m5: the first layout and render pass
        Stopwatch fromOpen = Stopwatch.StartNew();
        sw = Stopwatch.StartNew();
        Call(chat, "OpenSavedChat", new ChatEntry { Path = note, Title = size, Started = DateTime.Now, Text = "" });
        sw.Stop();
        double build = sw.Elapsed.TotalMilliseconds;
        Record("m4-open-build", size, build);
        sw = Stopwatch.StartNew();
        Flush(chat);
        sw.Stop();
        Record("m5-first-layout", size, sw.Elapsed.TotalMilliseconds);
        Record("m4+m5-open-total", size, build + sw.Elapsed.TotalMilliseconds);
        StackPanel messages = (StackPanel)Get(chat, "messages");
        int cards = ((List<ChatTurn>)Get(chat, "turns")).Sum(t => t.CreatedFiles.Count);
        for (int i = 0; i < 400 && LoadedThumbnails(messages) < cards; i++) await Task.Delay(25);
        fromOpen.Stop();
        Record("mf-thumbnails-ready", size, fromOpen.Elapsed.TotalMilliseconds);
        if (run == 0) Check("P-0f " + size + ": every created file's thumbnail loads (" + cards + " cards)", LoadedThumbnails(messages) >= cards, LoadedThumbnails(messages) + " of " + cards);
        if (run == 0) Check("P-04 " + size + ": every turn is built when opening (the 0.2.1 behaviour)", ((List<ChatTurn>)Get(chat, "turns")).Count == turns.Count, messages.Children.Count + " elements");
        Record("elements", size, messages.Children.Count);

        // m6: memory after opening
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long heapAfter = GC.GetTotalMemory(true);
        me.Refresh();
        // Above the process with the widget and no chat open, measured once at the start.
        Record("m6-heap-mb", size, (heapAfter - idleHeap) / 1048576.0);
        Record("m6-private-mb", size, (me.PrivateMemorySize64 - idlePrivate) / 1048576.0);

        // m7: theme switch, which redraws every reply
        sw = Stopwatch.StartNew();
        Theme("light");
        Flush(chat);
        sw.Stop();
        Record("m7-theme", size, sw.Elapsed.TotalMilliseconds);

        // m8: a 200 px resize
        sw = Stopwatch.StartNew();
        chat.Width = chat.Width + 200;
        Flush(chat);
        sw.Stop();
        Record("m8-resize", size, sw.Elapsed.TotalMilliseconds);

        // m10: scrolling from the bottom to the top in 20 steps
        ScrollViewer scroller = (ScrollViewer)Get(chat, "scroller");
        scroller.ScrollToBottom();
        Flush(chat);
        double extent = scroller.ScrollableHeight, worst = 0, total = 0;
        for (int step = 1; step <= 20; step++)
        {
            sw = Stopwatch.StartNew();
            scroller.ScrollToVerticalOffset(extent * (20 - step) / 20.0);
            Flush(chat);
            sw.Stop();
            worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds);
            total += sw.Elapsed.TotalMilliseconds;
        }
        Record("m10-scroll-worst-step", size, worst);
        Record("m10-scroll-total", size, total);

        // m9: sending in a long chat, from the click until the user's bubble is laid out (L600 is over the request limit)
        if (size != "L600")
        {
            ((TextBox)Get(chat, "input")).Text = "One more question.";
            int before = messages.Children.Count;
            sw = Stopwatch.StartNew();
            Call(chat, "Send");
            Flush(chat);
            sw.Stop();
            Record("m9-send", size, sw.Elapsed.TotalMilliseconds);
            if (run == 0) Check("P-09 " + size + ": the user's bubble appears", messages.Children.Count > before);
            for (int i = 0; i < 1200 && (bool)Get(chat, "busy"); i++) await Task.Delay(25);
        }
        chat.Close();
        Theme("dark");
        await Task.Delay(50);
    }

    // The targets set for L300 on 2026-11-09. Reported always; with "enforce", a median over 3 times its target fails.
    static void Targets()
    {
        var targets = new[]
        {
            new { Id = "T-1", Metric = "m4+m5-open-total", Target = 500.0 },
            new { Id = "T-4", Metric = "m7-theme", Target = 400.0 },
            new { Id = "T-5", Metric = "m8-resize", Target = 150.0 },
            new { Id = "T-6", Metric = "m9-send", Target = 100.0 },
            new { Id = "T-7", Metric = "m6-heap-mb", Target = 58.4 / 2 },
        };
        table.Append("\n| Target | L300 median | Target | Fails above (3x) | Met |\n|---|---|---|---|---|\n");
        foreach (var t in targets)
        {
            List<double> v;
            if (!results.TryGetValue(t.Metric + " L300", out v)) continue;
            double median = Median(v);
            table.Append("| " + t.Id + " " + t.Metric + " | " + median.ToString("F1") + " | " + t.Target.ToString("F1") + " | " + (3 * t.Target).ToString("F1") + " | " + (median <= t.Target ? "yes" : "no") + " |\n");
            Log("TARGET " + t.Id + " " + t.Metric + " L300 median " + median.ToString("F1") + " target " + t.Target.ToString("F1") + (median <= t.Target ? " met" : " not met"));
            if (enforce) Check("P " + t.Id + " " + t.Metric + ": L300 median within 3x the target", median <= 3 * t.Target, median.ToString("F1") + " vs " + (3 * t.Target).ToString("F1"));
        }
    }

    static double Median(List<double> v) { List<double> s = v.OrderBy(x => x).ToList(); return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2; }

    static void Summarise()
    {
        table.Append("| Metric | L100 median | L100 worst | L300 median | L300 worst | L600 median | L600 worst |\n|---|---|---|---|---|---|---|\n");
        foreach (string metric in results.Keys.Select(k => k.Substring(0, k.LastIndexOf(' '))).Distinct())
        {
            table.Append("| ").Append(metric);
            foreach (string size in new[] { "L100", "L300", "L600" })
            {
                List<double> v;
                if (results.TryGetValue(metric + " " + size, out v)) table.Append(" | ").Append(Median(v).ToString("F1")).Append(" | ").Append(v.Max().ToString("F1"));
                else table.Append(" | - | -");
            }
            table.Append(" |\n");
            Check("P " + metric + ": measured", true);
        }
        Targets();
        Log(table.ToString());
    }
}
