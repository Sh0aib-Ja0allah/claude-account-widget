// The file preview: a pane that shows in a side panel in full screen and in its own window otherwise.
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml;
using Microsoft.Win32;
using Shapes = System.Windows.Shapes;
using WinFoundation = Windows.Foundation;
using WinPdf = Windows.Data.Pdf;
using WinStorage = Windows.Storage;
using WinStreams = Windows.Storage.Streams;
using WinImaging = Windows.Graphics.Imaging;
using WinOcr = Windows.Media.Ocr;
using COMExceptionAlias = System.Runtime.InteropServices.COMException;

// Awaiting Windows calls normally needs a library that only compiles with the Windows SDK installed.
static class WinRt
{
    internal static Task<T> Run<T>(WinFoundation.IAsyncOperation<T> operation)
    {
        TaskCompletionSource<T> done = new TaskCompletionSource<T>();
        operation.Completed = delegate(WinFoundation.IAsyncOperation<T> o, WinFoundation.AsyncStatus status)
        {
            if (status == WinFoundation.AsyncStatus.Completed) done.TrySetResult(o.GetResults());
            else if (status == WinFoundation.AsyncStatus.Error) done.TrySetException(o.ErrorCode);
            else done.TrySetCanceled();
        };
        return done.Task;
    }

    internal static Task Run(WinFoundation.IAsyncAction operation)
    {
        TaskCompletionSource<bool> done = new TaskCompletionSource<bool>();
        operation.Completed = delegate(WinFoundation.IAsyncAction o, WinFoundation.AsyncStatus status)
        {
            if (status == WinFoundation.AsyncStatus.Completed) done.TrySetResult(true);
            else if (status == WinFoundation.AsyncStatus.Error) done.TrySetException(o.ErrorCode);
            else done.TrySetCanceled();
        };
        return done.Task;
    }
}

enum PreviewKind { Pages, Slides, Sheets, Image, Markdown, Web, Table, Text }

static class PreviewTypes
{
    internal static PreviewKind Classify(string file)
    {
        switch (Path.GetExtension(file).ToLowerInvariant())
        {
            case ".pdf": case ".doc": case ".docx": case ".odt": case ".rtf": return PreviewKind.Pages;
            case ".ppt": case ".pptx": case ".odp": return PreviewKind.Slides;
            case ".xls": case ".xlsx": case ".ods": return PreviewKind.Sheets;
            case ".png": case ".jpg": case ".jpeg": case ".gif": case ".bmp": case ".webp": case ".tif": case ".tiff": return PreviewKind.Image;
            case ".md": case ".markdown": return PreviewKind.Markdown;
            case ".html": case ".htm": case ".svg": return PreviewKind.Web;
            case ".csv": case ".tsv": return PreviewKind.Table;
            default: return PreviewKind.Text;
        }
    }

    internal static bool UsesPages(PreviewKind kind)
    {
        return kind == PreviewKind.Pages || kind == PreviewKind.Slides || kind == PreviewKind.Sheets;
    }

    internal static string Label(string file)
    {
        string extension = Path.GetExtension(file).ToLowerInvariant();
        switch (extension)
        {
            case ".pdf": return "PDF document";
            case ".doc": case ".docx": return "Word document";
            case ".odt": case ".rtf": return "Document";
            case ".ppt": case ".pptx": case ".odp": return "Presentation";
            case ".xls": case ".xlsx": case ".ods": return "Spreadsheet";
            case ".md": case ".markdown": return "Markdown";
            case ".html": case ".htm": return "Web page";
            case ".svg": return "SVG image";
            case ".csv": return "CSV table";
            case ".tsv": return "TSV table";
            case ".txt": return "Text file";
        }
        if (Classify(file) == PreviewKind.Image) return extension.TrimStart('.').ToUpperInvariant() + " image";
        return extension.Length > 0 ? extension.TrimStart('.').ToUpperInvariant() + " file" : "File";
    }

    internal static string Size(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return Math.Round(bytes / 1024.0) + " KB";
        return Math.Round(bytes / 1048576.0, 1) + " MB";
    }

    // Text that isn't UTF-8 is read with the computer's own code page.
    internal static string ReadText(string file)
    {
        byte[] bytes = File.ReadAllBytes(file);
        if (Array.IndexOf(bytes, (byte)0) >= 0 && !(bytes.Length > 1 && bytes[0] == 0xFF && bytes[1] == 0xFE)) throw new InvalidDataException();
        if (bytes.Length > 1 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿'); }
        catch (DecoderFallbackException) { return Encoding.Default.GetString(bytes); }
    }
}

// Converted PDFs and thumbnails stay in a temp folder until the file changes, so reopening is instant.
static class PreviewCache
{
    static readonly string Folder = Path.Combine(Path.GetTempPath(), "ClaudeAccountWidget-preview-cache");
    static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
    static readonly Dictionary<string, Task<string>> Running = new Dictionary<string, Task<string>>();
    static bool pruned;

    internal static string Hash(string text)
    {
        using (SHA1 sha = SHA1.Create())
        {
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }
    }

    static string Key(string file, string tail)
    {
        FileInfo info = new FileInfo(file);
        return Path.Combine(Folder, Hash(info.FullName.ToLowerInvariant() + "|" + info.LastWriteTimeUtc.Ticks + "|" + info.Length + "|" + tail));
    }

    internal static string FindLibreOffice()
    {
        return FindApp("soffice.exe");
    }

    static string FindApp(string name)
    {
        try
        {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + name))
            {
                string found = key == null ? null : key.GetValue(null) as string;
                return found != null && File.Exists(found) ? found : null;
            }
        }
        catch (System.Security.SecurityException) { return null; }
    }

    static string FindEdge()
    {
        string found = FindApp("msedge.exe");
        if (found != null) return found;
        string standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe");
        return File.Exists(standard) ? standard : null;
    }

    // A PDF that can be paged through, or null. An original PDF is copied so the viewer never locks it.
    internal static Task<string> GetPdf(string file)
    {
        lock (Running)
        {
            string key = Key(file, "pdf");
            Task<string> task;
            if (Running.TryGetValue(key, out task)) return task;
            task = Task.Run(() => MakePdf(file, key + ".pdf"));
            Running[key] = task;
            task.ContinueWith(delegate { lock (Running) Running.Remove(key); });
            return task;
        }
    }

    static string MakePdf(string file, string target)
    {
        try
        {
            if (File.Exists(target)) return Touch(target);
            Directory.CreateDirectory(Folder);
            Prune();
            string extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension == ".pdf")
            {
                File.Copy(file, target, true);
                return target;
            }
            PreviewKind kind = PreviewTypes.Classify(file);
            Gate.Wait();
            try
            {
                if (File.Exists(target)) return target;
                return kind == PreviewKind.Web ? RenderWebPage(file, target) : ConvertOffice(file, target, kind == PreviewKind.Sheets);
            }
            finally { Gate.Release(); }
        }
        catch (Exception e)
        {
            if (!(e is IOException || e is UnauthorizedAccessException || e is System.ComponentModel.Win32Exception || e is ArgumentException || e is NotSupportedException || e is InvalidOperationException || e is InvalidDataException)) throw;
            return null;
        }
    }

    static string Touch(string file)
    {
        try { File.SetLastWriteTimeUtc(file, DateTime.UtcNow); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return file;
    }

    static string ConvertOffice(string file, string target, bool oneSheetPerPage)
    {
        string office = FindLibreOffice();
        if (office == null) return null;
        string work = target + ".work";
        Directory.CreateDirectory(work);
        try
        {
            string profile = new Uri(Path.Combine(Path.GetTempPath(), "ClaudeAccountWidget-preview-profile")).AbsoluteUri;

            // Each sheet becomes one whole page, so sheets can have tabs and aren't cut to paper size.
            string format = oneSheetPerPage
                ? "\"pdf:calc_pdf_Export:{\\\"SinglePageSheets\\\":{\\\"type\\\":\\\"boolean\\\",\\\"value\\\":\\\"true\\\"}}\""
                : "pdf";
            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = office;
            start.Arguments = "--headless --norestore --nolockcheck --nodefault \"-env:UserInstallation=" + profile + "\" --convert-to " + format + " --outdir \"" + work + "\" \"" + file + "\"";
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            using (Process running = Process.Start(start))
            {
                if (!running.WaitForExit(90000)) ChatClient.KillTree(running);
            }
            string pdf = Path.Combine(work, Path.GetFileNameWithoutExtension(file) + ".pdf");
            if (!File.Exists(pdf)) return null;
            File.Move(pdf, target);
            return target;
        }
        finally
        {
            try { Directory.Delete(work, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // Edge draws a copy of the page that forbids scripts and has no network, so opening a page never
    // runs anything from it. Edge can't make a PDF with scripting switched off altogether.
    static string RenderWebPage(string file, string target)
    {
        string edge = FindEdge();
        if (edge == null) return null;
        string work = target + ".part";
        string page = target + ".html";
        File.WriteAllText(page, SafePage(file), new UTF8Encoding(true));
        try
        {
            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = edge;
            start.Arguments = "--headless=new --disable-gpu --no-first-run --disable-extensions --no-pdf-header-footer" +
                " \"--host-resolver-rules=MAP * ~NOTFOUND\"" +
                " \"--user-data-dir=" + Path.Combine(Folder, "edge-profile") + "\"" +
                " \"--print-to-pdf=" + work + "\" \"" + new Uri(page).AbsoluteUri + "\"";
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            using (Process running = Process.Start(start))
            {
                running.BeginOutputReadLine();
                running.BeginErrorReadLine();
                if (!running.WaitForExit(60000)) ChatClient.KillTree(running);
            }
            if (!File.Exists(work)) return null;
            File.Move(work, target);
            return target;
        }
        finally
        {
            try { File.Delete(page); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    static string SafePage(string file)
    {
        string fileUri = new Uri(Path.GetFullPath(file)).AbsoluteUri.Replace("'", "%27");
        string head = "<meta charset='utf-8'><meta http-equiv='Content-Security-Policy' content=\"script-src 'none'; connect-src 'none'; frame-src 'none'; object-src 'none'\">" +
            "<base href='" + fileUri.Substring(0, fileUri.LastIndexOf('/') + 1) + "'>";

        // A picture of an SVG can't run anything inside it.
        if (Path.GetExtension(file).Equals(".svg", StringComparison.OrdinalIgnoreCase))
        {
            return "<!doctype html>" + head + "<style>@page{margin:0}html,body{margin:0}img{max-width:100%}</style><img src='" + fileUri + "'>";
        }

        // It goes before everything else in the page, including any script.
        string text = PreviewTypes.ReadText(file);
        Match type = Regex.Match(text, @"^\s*<!doctype[^>]*>", RegexOptions.IgnoreCase);
        return type.Success ? text.Substring(0, type.Length) + head + text.Substring(type.Length) : head + text;
    }

    // Oldest first, once per run: anything past two weeks, then whatever goes over 500 MB.
    static void Prune()
    {
        if (pruned) return;
        pruned = true;
        List<FileInfo> files = new List<FileInfo>(new DirectoryInfo(Folder).GetFiles());
        files.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
        long total = 0;
        foreach (FileInfo file in files) total += file.Length;
        foreach (FileInfo file in files)
        {
            if (file.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-14) && total <= 500L * 1024 * 1024) continue;
            try
            {
                long size = file.Length;
                file.Delete();
                total -= size;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // The first page or the picture itself, ready for a card. Null when there's nothing to show.
    internal static async Task<BitmapSource> GetThumbnail(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            PreviewKind kind = PreviewTypes.Classify(file);
            if (kind == PreviewKind.Image)
            {
                return await Task.Run(() =>
                {
                    BitmapImage picture = new BitmapImage();
                    picture.BeginInit();
                    picture.CacheOption = BitmapCacheOption.OnLoad;
                    picture.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    picture.DecodePixelWidth = 560;
                    picture.UriSource = new Uri(file);
                    picture.EndInit();
                    picture.Freeze();
                    return (BitmapSource)picture;
                });
            }
            if (!PreviewTypes.UsesPages(kind) && kind != PreviewKind.Web) return null;

            string saved = Key(file, "thumb") + ".png";
            byte[] png;
            if (File.Exists(saved)) png = File.ReadAllBytes(saved);
            else
            {
                string pdf = await GetPdf(file);
                if (pdf == null) return null;
                PdfPages pages = await PdfPages.Open(pdf);
                if (pages == null || pages.Count == 0) return null;
                png = await pages.RenderPng(0, 560);
                File.WriteAllBytes(saved, png);
            }
            return PdfPages.Decode(png);
        }
        catch (Exception e)
        {
            if (!(e is IOException || e is UnauthorizedAccessException || e is NotSupportedException || e is COMExceptionAlias || e is ArgumentException || e is InvalidOperationException)) throw;
            return null;
        }
    }
}

// Earlier states of a file Claude worked on. They stay in this app's own folder, never in the vault.
sealed class FileVersion
{
    internal int Number;
    internal string File;
    internal DateTime Time;
}

static class FileVersions
{
    const long MaxFile = 50L * 1024 * 1024;
    const int MaxKept = 20;
    internal static string Root;
    static readonly object Lock = new object();

    static string FolderFor(string file)
    {
        return Path.Combine(Root, PreviewCache.Hash(Path.GetFullPath(file).ToLowerInvariant()));
    }

    internal static Task Capture(IEnumerable<string> files)
    {
        List<string> list = new List<string>(files);
        return Task.Run(() => { foreach (string file in list) CaptureNow(file); });
    }

    internal static void CaptureNow(string file)
    {
        if (Root == null) return;
        lock (Lock)
        {
            try
            {
                FileInfo info = new FileInfo(file);
                if (!info.Exists || info.Length > MaxFile) return;
                string folder = FolderFor(file);
                Directory.CreateDirectory(folder);
                string marker = Path.Combine(folder, "last.txt");
                string[] last = File.Exists(marker) ? File.ReadAllLines(marker) : new string[0];
                string stamp = info.LastWriteTimeUtc.Ticks + "|" + info.Length;
                if (last.Length == 2 && last[0] == stamp) return;

                byte[] bytes = File.ReadAllBytes(file);
                string hash;
                using (SHA1 sha = SHA1.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes));
                bool same = last.Length == 2 && last[1] == hash;
                File.WriteAllLines(marker, new[] { stamp, hash });
                if (same) return;

                string[] kept = Directory.GetFiles(folder, "v*");
                int next = 1;
                foreach (string item in kept) next = Math.Max(next, NumberOf(item) + 1);
                File.WriteAllBytes(Path.Combine(folder, "v" + next.ToString("D5") + Path.GetExtension(file)), bytes);
                Array.Sort(kept, (a, b) => NumberOf(a).CompareTo(NumberOf(b)));
                for (int i = 0; i + MaxKept <= kept.Length; i++) File.Delete(kept[i]);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    static int NumberOf(string file)
    {
        int number;
        return int.TryParse(Path.GetFileNameWithoutExtension(file).Substring(1), out number) ? number : 0;
    }

    // Oldest first.
    internal static List<FileVersion> List(string file)
    {
        List<FileVersion> found = new List<FileVersion>();
        if (Root == null) return found;
        lock (Lock)
        {
            try
            {
                string folder = FolderFor(file);
                if (!Directory.Exists(folder)) return found;
                foreach (string item in Directory.GetFiles(folder, "v*"))
                {
                    FileVersion version = new FileVersion();
                    version.Number = NumberOf(item);
                    version.File = item;
                    version.Time = File.GetCreationTime(item);
                    found.Add(version);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        found.Sort((a, b) => a.Number.CompareTo(b.Number));
        return found;
    }
}

sealed class PdfPages
{
    WinPdf.PdfDocument document;
    Size[] sizes;
    int[] pageMap;

    internal int Count { get { return sizes.Length; } }
    internal Size SizeOf(int index) { return sizes[index]; }

    // These Windows objects are made away from the UI thread. Made on it, they keep the app from
    // exiting, because they can only be released on a thread that has already finished.
    internal static Task<PdfPages> Open(string file)
    {
        return Task.Run(() => OpenNow(file));
    }

    // Null when Windows can't open it, such as a password-protected PDF or a Windows without this feature.
    static async Task<PdfPages> OpenNow(string file)
    {
        try
        {
            WinStorage.StorageFile storage = await WinRt.Run(WinStorage.StorageFile.GetFileFromPathAsync(file));
            WinPdf.PdfDocument document = await WinRt.Run(WinPdf.PdfDocument.LoadFromFileAsync(storage));
            PdfPages opened = new PdfPages();
            opened.document = document;
            opened.sizes = new Size[document.PageCount];
            opened.pageMap = new int[document.PageCount];
            for (uint i = 0; i < document.PageCount; i++)
            {
                using (WinPdf.PdfPage page = document.GetPage(i))
                {
                    opened.sizes[i] = new Size(page.Size.Width, page.Size.Height);
                    opened.pageMap[i] = (int)i;
                }
            }
            return opened;
        }
        catch (Exception e)
        {
            if (e is OutOfMemoryException) throw;
            return null;
        }
    }

    // Leaves out pages that shouldn't be shown, such as hidden sheets.
    internal void Keep(List<int> pages)
    {
        Size[] keptSizes = new Size[pages.Count];
        int[] keptMap = new int[pages.Count];
        for (int i = 0; i < pages.Count; i++)
        {
            keptSizes[i] = sizes[pages[i]];
            keptMap[i] = pageMap[pages[i]];
        }
        sizes = keptSizes;
        pageMap = keptMap;
    }

    internal Task<byte[]> RenderPng(int index, double pixelWidth)
    {
        return Task.Run(() => RenderNow(index, pixelWidth));
    }

    async Task<byte[]> RenderNow(int index, double pixelWidth)
    {
        using (WinPdf.PdfPage page = document.GetPage((uint)pageMap[index]))
        using (WinStreams.InMemoryRandomAccessStream stream = new WinStreams.InMemoryRandomAccessStream())
        {
            WinPdf.PdfPageRenderOptions options = new WinPdf.PdfPageRenderOptions();
            options.DestinationWidth = (uint)Math.Max(1, pixelWidth);
            await WinRt.Run(page.RenderToStreamAsync(stream, options));
            byte[] bytes = new byte[(int)stream.Size];
            using (WinStreams.IInputStream input = stream.GetInputStreamAt(0))
            using (WinStreams.DataReader reader = new WinStreams.DataReader(input))
            {
                await WinRt.Run(reader.LoadAsync((uint)bytes.Length));
                reader.ReadBytes(bytes);
            }
            return bytes;
        }
    }

    internal static BitmapSource Decode(byte[] png)
    {
        BitmapImage picture = new BitmapImage();
        picture.BeginInit();
        picture.CacheOption = BitmapCacheOption.OnLoad;
        picture.StreamSource = new MemoryStream(png);
        picture.EndInit();
        picture.Freeze();
        return picture;
    }
}

sealed class PageWord
{
    internal string Text;
    internal Rect Box;
    internal int Line;
}

// Pages are pictures here, so Windows' own text recognition supplies the words for search and selection.
static class PageReader
{
    static List<WinOcr.OcrEngine> engines;

    static List<WinOcr.OcrEngine> Engines()
    {
        if (engines != null) return engines;
        List<WinOcr.OcrEngine> made = new List<WinOcr.OcrEngine>();
        WinOcr.OcrEngine main = WinOcr.OcrEngine.TryCreateFromUserProfileLanguages();
        if (main != null) made.Add(main);
        foreach (Windows.Globalization.Language language in WinOcr.OcrEngine.AvailableRecognizerLanguages)
        {
            if (main != null && language.LanguageTag == main.RecognizerLanguage.LanguageTag) continue;
            WinOcr.OcrEngine extra = WinOcr.OcrEngine.TryCreateFromLanguage(language);
            if (extra != null) made.Add(extra);
        }
        engines = made;
        return made;
    }

    internal static Task<List<PageWord>> Read(byte[] png)
    {
        return Task.Run(() => ReadNow(png));
    }

    static async Task<List<PageWord>> ReadNow(byte[] png)
    {
        List<PageWord> best = new List<PageWord>();
        try
        {
            WinStreams.InMemoryRandomAccessStream stream = new WinStreams.InMemoryRandomAccessStream();
            using (WinStreams.DataWriter writer = new WinStreams.DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(png);
                await WinRt.Run(writer.StoreAsync());
                await WinRt.Run(writer.FlushAsync());
                writer.DetachStream();
            }
            stream.Seek(0);
            WinImaging.BitmapDecoder decoder = await WinRt.Run(WinImaging.BitmapDecoder.CreateAsync(stream));
            double width = decoder.PixelWidth, height = decoder.PixelHeight;
            WinImaging.SoftwareBitmap bitmap = await WinRt.Run(decoder.GetSoftwareBitmapAsync());

            // Each installed language reads the page, and the one that finds the most real words wins.
            // A page mixing two scripts comes back in its main one.
            double bestScore = -1;
            foreach (WinOcr.OcrEngine engine in Engines())
            {
                WinOcr.OcrResult result = await WinRt.Run(engine.RecognizeAsync(bitmap));
                List<PageWord> words = new List<PageWord>();
                int clean = 0, line = 0;
                foreach (WinOcr.OcrLine ocrLine in result.Lines)
                {
                    foreach (WinOcr.OcrWord ocrWord in ocrLine.Words)
                    {
                        PageWord word = new PageWord();
                        word.Text = ocrWord.Text;
                        word.Line = line;
                        word.Box = new Rect(ocrWord.BoundingRect.X / width, ocrWord.BoundingRect.Y / height,
                            ocrWord.BoundingRect.Width / width, ocrWord.BoundingRect.Height / height);
                        words.Add(word);
                        if (LooksLikeWord(ocrWord.Text)) clean += ocrWord.Text.Length;
                    }
                    line++;
                }
                if (clean > bestScore)
                {
                    bestScore = clean;
                    best = words;
                }
            }
        }
        catch (Exception e)
        {
            if (e is OutOfMemoryException) throw;
        }
        return best;
    }

    static bool LooksLikeWord(string text)
    {
        int letters = 0;
        foreach (char c in text) if (char.IsLetterOrDigit(c)) letters++;
        return letters >= 2 && letters >= text.Length - 1;
    }
}

// What every kind of view can offer. The pane asks and doesn't need to know which kind it has.
abstract class PreviewBody : Grid
{
    internal event Action SelectionChanged;
    protected void RaiseSelection() { if (SelectionChanged != null) SelectionChanged(); }
    internal virtual string SelectedText { get { return ""; } }
    internal virtual void ClearSelection() { }
    internal virtual bool CanFind { get { return false; } }
    internal virtual Task<int> Find(string query, Action<string> progress) { return Task.FromResult(0); }
    internal virtual string FindStep(int direction) { return ""; }
    internal virtual string FindPosition(int count) { return "1 of " + count; }
    internal virtual void FocusView() { Focus(); }
    internal virtual void ClearFind() { }
    internal virtual object SaveState() { return null; }
    internal virtual void RestoreState(object state) { }
    internal virtual bool HandleKey(KeyEventArgs e) { return false; }
    internal virtual string Details { get { return ""; } }
    internal virtual Task<DocumentPaginator> BuildPrint(Size page) { return Task.FromResult<DocumentPaginator>(null); }
    internal virtual void Release() { }
}

static class PreviewChrome
{
    internal static Border Icon(string glyph, string tip, Action action)
    {
        Border button = new Border();
        TextBlock icon = new TextBlock();
        WidgetWindow.BuildHeaderButton(button, icon, glyph, tip, 0);
        button.Margin = new Thickness(1, 0, 1, 0);
        ChatWindow.MakeButton(button, action);
        return button;
    }

    internal static TextBlock Label(string text)
    {
        TextBlock label = new TextBlock();
        label.Text = text;
        label.FontSize = 12;
        label.Foreground = WidgetWindow.Muted;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(6, 0, 6, 0);
        return label;
    }

    internal static Border Bar(UIElement content)
    {
        Border bar = new Border();
        bar.Child = content;
        bar.Padding = new Thickness(4, 3, 4, 3);
        bar.CornerRadius = new CornerRadius(10);
        bar.Background = WidgetWindow.OverlaySoft;
        bar.HorizontalAlignment = HorizontalAlignment.Center;
        bar.Margin = new Thickness(0, 0, 0, 8);
        return bar;
    }

    internal static Border Divider()
    {
        Border line = new Border();
        line.Width = 1;
        line.Margin = new Thickness(5, 5, 5, 5);
        line.Background = WidgetWindow.CardEdge;
        return line;
    }

    internal static StackPanel Row()
    {
        StackPanel row = new StackPanel();
        row.Orientation = Orientation.Horizontal;
        return row;
    }

    internal static Color Mark(bool current)
    {
        return current ? Color.FromArgb(0x90, 0xF5, 0x9E, 0x2E) : Color.FromArgb(0x60, 0xFF, 0xD8, 0x3C);
    }
}

enum PageMode { Continuous, Single }

sealed class PageSlot
{
    internal int Index;
    internal Size Size;
    internal Border Root;
    internal Image Picture;
    internal Canvas Marks;
    internal double RenderedWidth;
    internal Task<List<PageWord>> Reading;
    internal List<PageWord> Words;
}

sealed class FindMatch
{
    internal int Page;
    internal int First;
    internal int Last;
}

sealed class PageViewerState
{
    internal int Page;
    internal double Zoom;
    internal bool Fit;
}

// Pages drawn as pictures from a PDF, with thumbnails, page controls, zoom, search and selection.
sealed class PageViewer : PreviewBody
{
    const double MaxZoom = 5, MinZoom = 0.2;

    readonly PdfPages document;
    readonly PreviewKind kind;
    readonly PageMode mode;
    readonly List<PageSlot> slots = new List<PageSlot>();
    readonly List<Border> thumbs = new List<Border>();
    readonly List<Border> tabs = new List<Border>();
    readonly List<string> sheetNames;
    readonly ScrollViewer scroller = new ScrollViewer();
    readonly StackPanel pagesPanel = new StackPanel();
    readonly ScrollViewer thumbScroller = new ScrollViewer();
    readonly StackPanel thumbPanel = new StackPanel();
    readonly TextBlock pageLabel = PreviewChrome.Label("");
    readonly TextBlock zoomLabel = PreviewChrome.Label("");
    readonly ColumnDefinition thumbColumn = new ColumnDefinition();
    readonly DispatcherTimer settle = new DispatcherTimer();
    readonly List<FindMatch> matches = new List<FindMatch>();

    double zoom = 1;
    bool fit = true;
    int current;
    bool rendering, thumbRendering, released;
    bool thumbsShown;
    int matchIndex = -1;
    int findRun;

    PageSlot selectionSlot;
    int selectionFirst = -1, selectionLast = -1;
    bool dragging;
    Point dragStart;

    internal PageViewer(PdfPages pages, PreviewKind pageKind, List<string> sheets, bool compact)
    {
        document = pages;
        kind = pageKind;
        mode = pageKind == PreviewKind.Pages ? PageMode.Continuous : PageMode.Single;
        sheetNames = sheets;
        Focusable = true;
        FocusVisualStyle = null;

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        ColumnDefinitions.Add(thumbColumn);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        thumbColumn.Width = new GridLength(0);

        UIElement controls = BuildControls(compact);
        Grid.SetColumnSpan(controls, 2);
        Children.Add(controls);

        pagesPanel.HorizontalAlignment = HorizontalAlignment.Center;
        pagesPanel.Margin = new Thickness(0, 2, 0, 6);
        for (int i = 0; i < document.Count; i++) pagesPanel.Children.Add(BuildSlot(i));

        scroller.Content = pagesPanel;
        scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        scroller.Focusable = false;
        scroller.ScrollChanged += delegate { OnScrolled(); };
        scroller.PreviewMouseWheel += OnWheel;
        scroller.SizeChanged += delegate { if (fit) ApplyZoom(); };
        Grid.SetRow(scroller, 1);
        Grid.SetColumn(scroller, 1);
        Children.Add(scroller);

        thumbScroller.Content = thumbPanel;
        thumbScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        thumbScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        thumbScroller.Focusable = false;
        thumbScroller.ScrollChanged += delegate { PumpThumbs(); };
        Grid.SetRow(thumbScroller, 1);
        Children.Add(thumbScroller);
        for (int i = 0; i < document.Count; i++) thumbPanel.Children.Add(BuildThumb(i));

        if (kind == PreviewKind.Sheets && document.Count > 0)
        {
            UIElement tabRow = BuildTabs();
            Grid.SetRow(tabRow, 2);
            Grid.SetColumnSpan(tabRow, 2);
            Children.Add(tabRow);
        }

        settle.Interval = TimeSpan.FromMilliseconds(180);
        settle.Tick += delegate
        {
            settle.Stop();
            PumpRender();
        };
        Loaded += delegate
        {
            if (!compact && kind != PreviewKind.Sheets && document.Count > 1 && ActualWidth > 620) ShowThumbs(true);
            ShowCurrent();
            ApplyZoom();
        };
    }

    UIElement BuildControls(bool compact)
    {
        StackPanel row = PreviewChrome.Row();
        if (!compact && kind != PreviewKind.Sheets && document.Count > 1)
        {
            row.Children.Add(PreviewChrome.Icon("", "Thumbnails", delegate { ShowThumbs(!thumbsShown); }));
            row.Children.Add(PreviewChrome.Divider());
        }
        if (kind != PreviewKind.Sheets && document.Count > 1)
        {
            row.Children.Add(PreviewChrome.Icon("", "Previous page", delegate { GoTo(current - 1); }));
            row.Children.Add(pageLabel);
            row.Children.Add(PreviewChrome.Icon("", "Next page", delegate { GoTo(current + 1); }));
            row.Children.Add(PreviewChrome.Divider());
        }
        row.Children.Add(PreviewChrome.Icon("", "Zoom out", delegate { ZoomBy(1 / 1.2); }));
        row.Children.Add(zoomLabel);
        row.Children.Add(PreviewChrome.Icon("", "Zoom in", delegate { ZoomBy(1.2); }));
        row.Children.Add(PreviewChrome.Icon("", kind == PreviewKind.Pages ? "Fit to width" : "Fit to window", delegate
        {
            fit = true;
            ApplyZoom();
        }));
        return PreviewChrome.Bar(row);
    }

    UIElement BuildSlot(int index)
    {
        PageSlot slot = new PageSlot();
        slot.Index = index;
        slot.Size = document.SizeOf(index);
        slot.Picture = new Image();
        slot.Picture.Stretch = Stretch.Fill;
        slot.Picture.IsHitTestVisible = false;
        RenderOptions.SetBitmapScalingMode(slot.Picture, BitmapScalingMode.HighQuality);
        slot.Marks = new Canvas();
        slot.Marks.IsHitTestVisible = false;

        Grid layers = new Grid();
        layers.Children.Add(slot.Picture);
        layers.Children.Add(slot.Marks);

        slot.Root = new Border();
        slot.Root.Child = layers;
        slot.Root.Background = Brushes.White;
        slot.Root.BorderBrush = WidgetWindow.CardEdge;
        slot.Root.BorderThickness = new Thickness(1);
        slot.Root.Margin = new Thickness(10, 0, 10, 10);
        slot.Root.Cursor = Cursors.IBeam;
        slot.Root.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { OnPageDown(slot, e); };
        slot.Root.MouseMove += delegate(object s, MouseEventArgs e) { OnPageMove(slot, e); };
        slot.Root.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { OnPageUp(slot, e); };
        slots.Add(slot);
        return slot.Root;
    }

    UIElement BuildThumb(int index)
    {
        Image picture = new Image();
        picture.Stretch = Stretch.Uniform;
        Size size = document.SizeOf(index);
        picture.Width = 96;
        picture.Height = Math.Min(160, 96 * size.Height / Math.Max(1, size.Width));
        Border page = new Border();
        page.Background = Brushes.White;
        page.Child = picture;
        page.HorizontalAlignment = HorizontalAlignment.Center;

        TextBlock number = new TextBlock();
        number.Text = (index + 1).ToString();
        number.FontSize = 10.5;
        number.Foreground = WidgetWindow.Muted;
        number.HorizontalAlignment = HorizontalAlignment.Center;
        number.Margin = new Thickness(0, 3, 0, 0);

        StackPanel stack = new StackPanel();
        stack.Children.Add(page);
        stack.Children.Add(number);

        Border thumb = new Border();
        thumb.Child = stack;
        thumb.Padding = new Thickness(5);
        thumb.Margin = new Thickness(0, 0, 6, 4);
        thumb.CornerRadius = new CornerRadius(7);
        thumb.Background = Brushes.Transparent;
        thumb.Cursor = Cursors.Hand;
        thumb.Tag = picture;
        ChatWindow.MakeButton(thumb, delegate { GoTo(index); });
        thumbs.Add(thumb);
        return thumb;
    }

    UIElement BuildTabs()
    {
        StackPanel row = PreviewChrome.Row();
        for (int i = 0; i < document.Count; i++)
        {
            int index = i;
            string name = sheetNames != null && sheetNames.Count == document.Count ? sheetNames[i] : "Sheet " + (i + 1);
            TextBlock text = PreviewChrome.Label(name);
            Border tab = new Border();
            tab.Child = text;
            tab.Padding = new Thickness(6, 4, 6, 4);
            tab.Margin = new Thickness(0, 6, 3, 0);
            tab.CornerRadius = new CornerRadius(7);
            tab.Cursor = Cursors.Hand;
            tab.Background = Brushes.Transparent;
            ChatWindow.MakeButton(tab, delegate { GoTo(index); });
            tabs.Add(tab);
            row.Children.Add(tab);
        }
        ScrollViewer strip = new ScrollViewer();
        strip.Content = row;
        strip.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        strip.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        strip.Focusable = false;
        return strip;
    }

    void ShowThumbs(bool show)
    {
        thumbsShown = show;
        thumbColumn.Width = show ? new GridLength(124) : new GridLength(0);
        thumbScroller.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PumpThumbs();
    }

    internal override string Details
    {
        get
        {
            string unit = kind == PreviewKind.Slides ? "slide" : kind == PreviewKind.Sheets ? "sheet" : "page";
            return document.Count + " " + unit + (document.Count == 1 ? "" : "s");
        }
    }

    double FitZoom()
    {
        if (document.Count == 0) return 1;
        Size size = slots[current].Size;
        double width = Math.Max(120, (scroller.ActualWidth > 0 ? scroller.ActualWidth : 500) - 34);
        double height = Math.Max(120, (scroller.ActualHeight > 0 ? scroller.ActualHeight : 500) - 26);
        double across = width / Math.Max(1, size.Width);
        if (kind == PreviewKind.Pages) return Math.Min(across, 2.2);
        if (kind == PreviewKind.Sheets) return Math.Min(across, 1.6);
        return Math.Min(across, height / Math.Max(1, size.Height));
    }

    void ZoomBy(double factor)
    {
        zoom = Math.Max(MinZoom, Math.Min(MaxZoom, (fit ? FitZoom() : zoom) * factor));
        fit = false;
        ApplyZoom();
    }

    void ApplyZoom()
    {
        if (fit) zoom = FitZoom();
        zoomLabel.Text = Math.Round(zoom * 100) + "%";
        foreach (PageSlot slot in slots)
        {
            slot.Root.Width = slot.Size.Width * zoom + 2;
            slot.Root.Height = slot.Size.Height * zoom + 2;
            DrawMarks(slot);
        }
        settle.Stop();
        settle.Start();
    }

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            ZoomBy(e.Delta > 0 ? 1.1 : 1 / 1.1);
        }
        else if (mode == PageMode.Single && scroller.ScrollableHeight < 1 && kind == PreviewKind.Slides)
        {
            e.Handled = true;
            GoTo(current + (e.Delta > 0 ? -1 : 1));
        }
    }

    internal override bool HandleKey(KeyEventArgs e)
    {
        bool control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (control && (e.Key == Key.OemPlus || e.Key == Key.Add)) ZoomBy(1.2);
        else if (control && (e.Key == Key.OemMinus || e.Key == Key.Subtract)) ZoomBy(1 / 1.2);
        else if (control && (e.Key == Key.D0 || e.Key == Key.NumPad0)) { fit = true; ApplyZoom(); }
        else if (control) return false;
        else if (e.Key == Key.PageDown) GoTo(current + 1);
        else if (e.Key == Key.PageUp) GoTo(current - 1);
        else if (e.Key == Key.Home) GoTo(0);
        else if (e.Key == Key.End) GoTo(document.Count - 1);
        else if (mode == PageMode.Single && (e.Key == Key.Right || e.Key == Key.Down)) GoTo(current + 1);
        else if (mode == PageMode.Single && (e.Key == Key.Left || e.Key == Key.Up)) GoTo(current - 1);
        else return false;
        return true;
    }

    internal void GoTo(int index)
    {
        if (document.Count == 0) return;
        index = Math.Max(0, Math.Min(document.Count - 1, index));
        current = index;
        ShowCurrent();
        if (mode == PageMode.Continuous)
        {
            UpdateLayout();
            Point top = slots[index].Root.TranslatePoint(new Point(0, 0), pagesPanel);
            scroller.ScrollToVerticalOffset(top.Y);
        }
        else
        {
            scroller.ScrollToHome();
            if (fit) ApplyZoom();
        }
        PumpRender();
    }

    void ShowCurrent()
    {
        pageLabel.Text = (kind == PreviewKind.Slides ? "Slide " : "Page ") + (current + 1) + " of " + document.Count;
        if (mode == PageMode.Single)
        {
            for (int i = 0; i < slots.Count; i++) slots[i].Root.Visibility = i == current ? Visibility.Visible : Visibility.Collapsed;
        }
        for (int i = 0; i < thumbs.Count; i++) thumbs[i].Background = i == current ? WidgetWindow.OverlayMid : Brushes.Transparent;
        for (int i = 0; i < tabs.Count; i++) tabs[i].Background = i == current ? WidgetWindow.OverlayMid : Brushes.Transparent;
        if (thumbsShown && thumbs.Count > current) thumbs[current].BringIntoView();
    }

    void OnScrolled()
    {
        if (mode == PageMode.Continuous && slots.Count > 0 && IsLoaded)
        {
            double middle = scroller.ViewportHeight / 2;
            for (int i = 0; i < slots.Count; i++)
            {
                Point top = slots[i].Root.TranslatePoint(new Point(0, 0), scroller);
                if (top.Y + slots[i].Root.ActualHeight + 10 >= middle)
                {
                    if (i != current)
                    {
                        current = i;
                        ShowCurrent();
                    }
                    break;
                }
            }
        }
        settle.Stop();
        settle.Start();
    }

    bool IsSlotVisible(PageSlot slot)
    {
        if (slot.Root.Visibility != Visibility.Visible || !slot.Root.IsLoaded) return false;
        Point top = slot.Root.TranslatePoint(new Point(0, 0), scroller);
        return top.Y + slot.Root.Height > -scroller.ViewportHeight * 0.5 && top.Y < scroller.ViewportHeight * 1.5;
    }

    double PixelScale()
    {
        PresentationSource source = PresentationSource.FromVisual(this);
        return source != null && source.CompositionTarget != null ? source.CompositionTarget.TransformToDevice.M11 : 1;
    }

    async void PumpRender()
    {
        if (rendering || released) return;
        rendering = true;
        try
        {
            while (!released)
            {
                PageSlot next = null;
                double wanted = 0;
                foreach (PageSlot slot in slots)
                {
                    if (!IsSlotVisible(slot)) continue;
                    wanted = Math.Min(4096, slot.Size.Width * zoom * PixelScale());
                    if (slot.Picture.Source == null || Math.Abs(slot.RenderedWidth - wanted) > wanted * 0.15)
                    {
                        next = slot;
                        break;
                    }
                }
                if (next == null) break;
                byte[] png = await document.RenderPng(next.Index, wanted);
                if (released) return;
                next.Picture.Source = await Task.Run(() => PdfPages.Decode(png));
                next.RenderedWidth = wanted;
            }

            // Far-away pages give their memory back and redraw when they scroll into view.
            foreach (PageSlot slot in slots)
            {
                if (Math.Abs(slot.Index - current) > 6 && slot.Picture.Source != null)
                {
                    slot.Picture.Source = null;
                    slot.RenderedWidth = 0;
                }
            }
        }
        catch (Exception e)
        {
            if (e is OutOfMemoryException) throw;
        }
        finally { rendering = false; }

        if (!released && slots.Count > current) StartReading(slots[current]);
    }

    async void PumpThumbs()
    {
        if (thumbRendering || !thumbsShown || released) return;
        thumbRendering = true;
        try
        {
            for (int i = 0; i < thumbs.Count && !released; i++)
            {
                Image picture = (Image)thumbs[i].Tag;
                if (picture.Source != null || !thumbs[i].IsLoaded) continue;
                Point top = thumbs[i].TranslatePoint(new Point(0, 0), thumbScroller);
                if (top.Y + thumbs[i].ActualHeight < 0 || top.Y > thumbScroller.ViewportHeight + 200) continue;
                byte[] png = await document.RenderPng(i, 192 * PixelScale());
                if (released) return;
                picture.Source = PdfPages.Decode(png);
            }
        }
        catch (Exception e)
        {
            if (e is OutOfMemoryException) throw;
        }
        finally { thumbRendering = false; }
    }

    Task<List<PageWord>> StartReading(PageSlot slot)
    {
        if (slot.Reading == null) slot.Reading = ReadWords(slot);
        return slot.Reading;
    }

    async Task<List<PageWord>> ReadWords(PageSlot slot)
    {
        double across = Math.Max(1200, Math.Min(2400, slot.Size.Width * 2));
        double tall = across * slot.Size.Height / Math.Max(1, slot.Size.Width);
        if (tall > 9000) across = across * 9000 / tall;
        List<PageWord> words;
        try
        {
            byte[] png = await document.RenderPng(slot.Index, across);
            words = await PageReader.Read(png);
        }
        catch (Exception e)
        {
            if (e is OutOfMemoryException) throw;
            words = new List<PageWord>();
        }
        slot.Words = words;
        if (dragging && selectionSlot == slot) UpdateDrag(slot, Mouse.GetPosition(slot.Root));
        return words;
    }

    void DrawMarks(PageSlot slot)
    {
        slot.Marks.Children.Clear();
        if (slot.Words == null) return;
        for (int m = 0; m < matches.Count; m++)
        {
            if (matches[m].Page != slot.Index) continue;
            AddMark(slot, matches[m].First, matches[m].Last, PreviewChrome.Mark(m == matchIndex));
        }
        if (selectionSlot == slot && selectionFirst >= 0)
        {
            Color accent = WidgetWindow.AccentColor.HasValue ? WidgetWindow.AccentColor.Value : Color.FromRgb(0x3B, 0x82, 0xF6);
            accent.A = 0x66;
            AddMark(slot, selectionFirst, selectionLast, accent);
        }
    }

    void AddMark(PageSlot slot, int first, int last, Color color)
    {
        double width = slot.Size.Width * zoom, height = slot.Size.Height * zoom;
        SolidColorBrush fill = new SolidColorBrush(color);
        for (int i = first; i <= last && i < slot.Words.Count; i++)
        {
            Rect box = slot.Words[i].Box;
            Shapes.Rectangle mark = new Shapes.Rectangle();
            mark.Fill = fill;
            mark.Width = box.Width * width + 3;
            mark.Height = box.Height * height + 3;
            Canvas.SetLeft(mark, box.X * width - 1.5);
            Canvas.SetTop(mark, box.Y * height - 1.5);
            slot.Marks.Children.Add(mark);
        }
    }

    static int NearestWord(PageSlot slot, Point point)
    {
        double x = point.X / Math.Max(1, slot.Root.ActualWidth), y = point.Y / Math.Max(1, slot.Root.ActualHeight);
        int best = -1;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < slot.Words.Count; i++)
        {
            Rect box = slot.Words[i].Box;
            double dx = x < box.Left ? box.Left - x : x > box.Right ? x - box.Right : 0;
            double dy = y < box.Top ? box.Top - y : y > box.Bottom ? y - box.Bottom : 0;
            double distance = dx * dx + dy * dy * 16;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        return best;
    }

    void OnPageDown(PageSlot slot, MouseButtonEventArgs e)
    {
        Focus();
        PageSlot old = selectionSlot;
        selectionSlot = slot;
        selectionFirst = selectionLast = -1;
        if (old != null) DrawMarks(old);
        RaiseSelection();
        dragging = true;
        dragStart = e.GetPosition(slot.Root);
        slot.Root.CaptureMouse();
        StartReading(slot);
        if (e.ClickCount == 2 && slot.Words != null)
        {
            selectionFirst = selectionLast = NearestWord(slot, dragStart);
            DrawMarks(slot);
        }
        e.Handled = true;
    }

    void OnPageMove(PageSlot slot, MouseEventArgs e)
    {
        if (dragging && selectionSlot == slot) UpdateDrag(slot, e.GetPosition(slot.Root));
    }

    void UpdateDrag(PageSlot slot, Point point)
    {
        if (slot.Words == null || slot.Words.Count == 0) return;
        if ((point - dragStart).Length < 3) return;
        int a = NearestWord(slot, dragStart), b = NearestWord(slot, point);
        selectionFirst = Math.Min(a, b);
        selectionLast = Math.Max(a, b);
        DrawMarks(slot);
    }

    void OnPageUp(PageSlot slot, MouseButtonEventArgs e)
    {
        if (!dragging) return;
        dragging = false;
        slot.Root.ReleaseMouseCapture();
        RaiseSelection();
        e.Handled = true;
    }

    internal override string SelectedText
    {
        get
        {
            if (selectionSlot == null || selectionFirst < 0 || selectionSlot.Words == null) return "";
            return WordsToText(selectionSlot.Words, selectionFirst, selectionLast);
        }
    }

    static string WordsToText(List<PageWord> words, int first, int last)
    {
        StringBuilder text = new StringBuilder();
        for (int i = first; i <= last && i < words.Count; i++)
        {
            if (i > first) text.Append(words[i].Line != words[i - 1].Line ? '\n' : ' ');
            text.Append(words[i].Text);
        }
        return text.ToString();
    }

    // Used by tests and by select-all.
    internal async Task SelectPage(int index)
    {
        PageSlot slot = slots[index];
        await StartReading(slot);
        selectionSlot = slot;
        selectionFirst = 0;
        selectionLast = slot.Words.Count - 1;
        DrawMarks(slot);
        RaiseSelection();
    }

    internal override void ClearSelection()
    {
        PageSlot slot = selectionSlot;
        selectionSlot = null;
        selectionFirst = selectionLast = -1;
        if (slot != null) DrawMarks(slot);
        RaiseSelection();
    }

    internal override bool CanFind { get { return true; } }

    internal override async Task<int> Find(string query, Action<string> progress)
    {
        int run = ++findRun;
        matches.Clear();
        matchIndex = -1;
        foreach (PageSlot slot in slots) DrawMarks(slot);
        string[] parts = query.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return 0;

        for (int p = 0; p < slots.Count; p++)
        {
            if (slots[p].Words == null && progress != null && slots.Count > 1) progress("Reading " + (p + 1) + " of " + slots.Count);
            List<PageWord> words = await StartReading(slots[p]);
            if (run != findRun || released) return -1;
            for (int i = 0; i + parts.Length <= words.Count; i++)
            {
                if (!WordsMatch(words, i, parts)) continue;
                FindMatch match = new FindMatch();
                match.Page = p;
                match.First = i;
                match.Last = i + parts.Length - 1;
                matches.Add(match);
            }
            DrawMarks(slots[p]);
        }
        if (matches.Count > 0) ShowMatch(FirstMatchFromCurrent());
        return matches.Count;
    }

    int FirstMatchFromCurrent()
    {
        for (int i = 0; i < matches.Count; i++) if (matches[i].Page >= current) return i;
        return 0;
    }

    static bool WordsMatch(List<PageWord> words, int at, string[] parts)
    {
        const StringComparison Ignore = StringComparison.CurrentCultureIgnoreCase;
        if (parts.Length == 1) return words[at].Text.IndexOf(parts[0], Ignore) >= 0;
        if (!words[at].Text.EndsWith(parts[0], Ignore)) return false;
        for (int i = 1; i < parts.Length - 1; i++)
        {
            if (!string.Equals(words[at + i].Text, parts[i], Ignore)) return false;
        }
        return words[at + parts.Length - 1].Text.StartsWith(parts[parts.Length - 1], Ignore);
    }

    void ShowMatch(int index)
    {
        int previous = matchIndex;
        matchIndex = index;
        if (previous >= 0 && previous < matches.Count) DrawMarks(slots[matches[previous].Page]);
        FindMatch match = matches[index];
        PageSlot slot = slots[match.Page];
        DrawMarks(slot);
        if (match.Page != current || mode == PageMode.Single)
        {
            if (mode == PageMode.Single || !IsWordVisible(slot, match.First)) GoTo(match.Page);
        }
        if (mode == PageMode.Continuous || scroller.ScrollableHeight > 0)
        {
            UpdateLayout();
            Rect box = slot.Words[match.First].Box;
            Point top = slot.Root.TranslatePoint(new Point(0, 0), pagesPanel);
            double y = top.Y + box.Y * slot.Size.Height * zoom;
            if (y < scroller.VerticalOffset + 20 || y > scroller.VerticalOffset + scroller.ViewportHeight - 40)
            {
                scroller.ScrollToVerticalOffset(y - scroller.ViewportHeight / 3);
            }
        }
    }

    bool IsWordVisible(PageSlot slot, int word)
    {
        if (!slot.Root.IsLoaded) return false;
        Point top = slot.Root.TranslatePoint(new Point(0, 0), scroller);
        double y = top.Y + slot.Words[word].Box.Y * slot.Size.Height * zoom;
        return y >= 0 && y <= scroller.ViewportHeight;
    }

    internal override string FindStep(int direction)
    {
        if (matches.Count == 0) return "";
        ShowMatch((Math.Max(matchIndex, 0) + direction + matches.Count) % matches.Count);
        return (matchIndex + 1) + " of " + matches.Count;
    }

    internal override string FindPosition(int count) { return (matchIndex + 1) + " of " + count; }

    internal override void ClearFind()
    {
        findRun++;
        matches.Clear();
        matchIndex = -1;
        foreach (PageSlot slot in slots) DrawMarks(slot);
    }

    internal override object SaveState()
    {
        PageViewerState state = new PageViewerState();
        state.Page = current;
        state.Zoom = zoom;
        state.Fit = fit;
        return state;
    }

    internal override void RestoreState(object state)
    {
        PageViewerState saved = state as PageViewerState;
        if (saved == null) return;
        zoom = saved.Zoom;
        fit = saved.Fit;
        int page = saved.Page;
        Dispatcher.BeginInvoke(new Action(delegate
        {
            ApplyZoom();
            GoTo(page);
        }), DispatcherPriority.Loaded);
    }

    // Each page goes to the printer as a sharp picture, centered and fitted to the paper.
    internal override async Task<DocumentPaginator> BuildPrint(Size paper)
    {
        FixedDocument printed = new FixedDocument();
        printed.DocumentPaginator.PageSize = paper;
        for (int i = 0; i < document.Count; i++)
        {
            Size size = document.SizeOf(i);
            double scale = Math.Min(paper.Width / Math.Max(1, size.Width), paper.Height / Math.Max(1, size.Height));
            byte[] png = await document.RenderPng(i, Math.Min(4096, size.Width * scale * 2.5));
            Image picture = new Image();
            picture.Source = PdfPages.Decode(png);
            picture.Width = size.Width * scale;
            picture.Height = size.Height * scale;
            FixedPage page = new FixedPage();
            page.Width = paper.Width;
            page.Height = paper.Height;
            FixedPage.SetLeft(picture, (paper.Width - picture.Width) / 2);
            FixedPage.SetTop(picture, (paper.Height - picture.Height) / 2);
            page.Children.Add(picture);
            PageContent content = new PageContent();
            ((System.Windows.Markup.IAddChild)content).AddChild(page);
            printed.Pages.Add(content);
        }
        return printed.DocumentPaginator;
    }

    internal override void Release()
    {
        released = true;
        findRun++;
        settle.Stop();
    }
}

// Colors for code and notes, chosen for the current theme or for white paper.
sealed class TextColors
{
    internal readonly Color Text, Muted, Comment, String, Number, Keyword, Attribute, CodeFill, QuoteEdge, Link, Added, Removed, TableEdge;

    internal TextColors(bool light, bool paper)
    {
        Text = paper ? Colors.Black : WidgetWindow.Text.Color;
        Muted = paper ? Color.FromRgb(0x55, 0x55, 0x55) : WidgetWindow.Muted.Color;
        Comment = WidgetWindow.Hex(light ? "#6A737D" : "#7F848E");
        String = WidgetWindow.Hex(light ? "#22863A" : "#98C379");
        Number = WidgetWindow.Hex(light ? "#B5540A" : "#D19A66");
        Keyword = WidgetWindow.Hex(light ? "#A626A4" : "#C678DD");
        Attribute = WidgetWindow.Hex(light ? "#986801" : "#E5C07B");
        CodeFill = WidgetWindow.Hex(light ? "#12000000" : "#1FFFFFFF");
        QuoteEdge = WidgetWindow.Hex(light ? "#33000000" : "#40FFFFFF");
        Link = WidgetWindow.Hex(light ? "#0B6BCB" : "#7CB7FF");
        Added = WidgetWindow.Hex(light ? "#3328A745" : "#4028A745");
        Removed = WidgetWindow.Hex(light ? "#33D73A49" : "#40D73A49");
        TableEdge = WidgetWindow.Hex(light ? "#33000000" : "#33FFFFFF");
    }
}

// Colors code with one pass of a pattern for its language family.
static class CodeColorer
{
    const int MaxColored = 300 * 1024;
    const int MaxLines = 20000;

    static string Words(string list) { return @"(?<k>\b(?:" + list.Replace(" ", "|") + @")\b)"; }

    const string Num = @"(?<n>\b(?:0[xX][0-9a-fA-F]+|\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)\b)";
    const string CStr = @"(?<s>""(?:[^""\\\n]|\\.)*""|'(?:[^'\\\n]|\\.)*'|`(?:[^`\\]|\\.)*`)";

    static string PatternFor(string extension)
    {
        switch (extension)
        {
            case ".py": case ".pyw":
                return @"(?<c>\#[^\n]*)|(?<s>""""""[\s\S]*?""""""|'''[\s\S]*?'''|""(?:[^""\\\n]|\\.)*""|'(?:[^'\\\n]|\\.)*')|" + Num + "|" +
                    Words("and as assert async await break class continue def del elif else except False finally for from global if import in is lambda None nonlocal not or pass raise return self True try while with yield");
            case ".sh": case ".bash": case ".zsh":
                return @"(?<c>(?<![\w$])\#[^\n]*)|(?<s>""(?:[^""\\]|\\.)*""|'[^']*')|(?<a>\$\{?\w+\}?)|" + Num + "|" +
                    Words("if then else elif fi for while until do done case esac function in return exit export local echo cd set unset source");
            case ".ps1": case ".psm1":
                return @"(?<c>\#[^\n]*|<\#[\s\S]*?\#>)|(?<s>""(?:[^""`]|`.)*""|'[^']*')|(?<a>\$[\w:]+)|" + Num + "|" +
                    @"(?i:" + Words("if else elseif foreach for while do switch function param return try catch finally throw break continue in") + ")";
            case ".bat": case ".cmd":
                return @"(?<c>(?im:^\s*(?:rem\b|::)[^\n]*))|(?<s>""[^""\n]*"")|(?<a>%[^%\s]+%)|(?i:" + Words("if else for in do goto call set echo exit not exist") + ")";
            case ".html": case ".htm": case ".xml": case ".svg": case ".xaml": case ".csproj": case ".config":
                return @"(?<c><!--[\s\S]*?-->)|(?<k></?[\w:.-]+|/?>)|(?<a>\b[\w:.-]+(?=\s*=))|(?<s>""[^""]*""|'[^']*')";
            case ".json":
                return @"(?<a>""(?:[^""\\\n]|\\.)*""(?=\s*:))|(?<s>""(?:[^""\\\n]|\\.)*"")|" + Num + "|" + Words("true false null");
            case ".css": case ".scss": case ".less":
                return @"(?<c>/\*[\s\S]*?\*/)|(?<s>""[^""\n]*""|'[^'\n]*')|(?<a>[\w-]+(?=\s*:[^;{}]*[;}]))|(?<n>\#[0-9a-fA-F]{3,8}\b|\b\d+(?:\.\d+)?(?:px|em|rem|%|vh|vw|s|ms)?\b)|(?<k>@[\w-]+)";
            case ".sql":
                return @"(?<c>--[^\n]*|/\*[\s\S]*?\*/)|(?<s>'(?:[^']|'')*')|" + Num + "|(?i:" +
                    Words("select from where insert into values update set delete create table alter drop index join left right inner outer on group by order having limit as and or not null primary key foreign references view with union all distinct") + ")";
            case ".yml": case ".yaml": case ".toml": case ".ini": case ".env":
                return @"(?<c>(?m:^\s*[\#;][^\n]*))|(?<s>""[^""\n]*""|'[^'\n]*')|(?<a>(?m:^\s*[\w.-]+(?=\s*[:=])))|(?<k>(?m:^\s*\[[^\]\n]+\]))|" + Num + "|" + Words("true false null yes no");
            case ".cs": case ".js": case ".jsx": case ".mjs": case ".ts": case ".tsx": case ".java": case ".c": case ".h": case ".cpp": case ".hpp": case ".cc": case ".go": case ".rs": case ".swift": case ".kt": case ".php": case ".dart": case ".scala":
                return @"(?<c>//[^\n]*|/\*[\s\S]*?\*/)|" + CStr + "|" + Num + "|" +
                    Words("abstract as async await break case catch class const continue def default delete do else enum export extends false final finally fn for foreach from func function go if impl implements import in interface internal let match module namespace new null nil override package private protected public readonly return self static struct super switch this throw throws trait true try type typeof using val var void while yield bool int long float double string char byte");
            default:
                return null;
        }
    }

    internal static void Fill(Paragraph paragraph, string text, string extension, TextColors colors)
    {
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length > MaxLines) text = string.Join("\n", lines, 0, MaxLines) + "\n\n… the rest is too long to show here. Open it in its app.";
        else text = string.Join("\n", lines);

        string pattern = text.Length <= MaxColored ? PatternFor(extension) : null;
        if (pattern == null)
        {
            AddPlain(paragraph, text, null);
            return;
        }
        try
        {
            int at = 0;
            Regex regex = new Regex(pattern, RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(2));
            foreach (Match match in regex.Matches(text))
            {
                if (match.Length == 0) continue;
                if (match.Index > at) AddPlain(paragraph, text.Substring(at, match.Index - at), null);
                AddPlain(paragraph, match.Value, ColorOf(match, colors));
                at = match.Index + match.Length;
            }
            if (at < text.Length) AddPlain(paragraph, text.Substring(at), null);
        }
        catch (RegexMatchTimeoutException)
        {
            paragraph.Inlines.Clear();
            AddPlain(paragraph, text, null);
        }
    }

    static Color? ColorOf(Match match, TextColors colors)
    {
        if (match.Groups["c"].Success) return colors.Comment;
        if (match.Groups["s"].Success) return colors.String;
        if (match.Groups["n"].Success) return colors.Number;
        if (match.Groups["k"].Success) return colors.Keyword;
        if (match.Groups["a"].Success) return colors.Attribute;
        return null;
    }

    static void AddPlain(Paragraph paragraph, string text, Color? color)
    {
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) paragraph.Inlines.Add(new LineBreak());
            if (lines[i].Length == 0) continue;
            Run run = new Run(lines[i]);
            if (color.HasValue) run.Foreground = new SolidColorBrush(color.Value);
            paragraph.Inlines.Add(run);
        }
    }
}

static class DocumentBuilder
{
    internal static FlowDocument Empty(TextColors colors, bool mono)
    {
        FlowDocument document = new FlowDocument();
        document.Foreground = new SolidColorBrush(colors.Text);
        document.FontFamily = new FontFamily(mono ? "Consolas" : "Segoe UI");
        document.FontSize = mono ? 12.5 : 13.5;
        document.PagePadding = new Thickness(10, 4, 14, 14);
        return document;
    }

    internal static FlowDocument Code(string text, string extension, TextColors colors)
    {
        FlowDocument document = Empty(colors, true);
        Paragraph paragraph = new Paragraph();
        paragraph.Margin = new Thickness(0);
        CodeColorer.Fill(paragraph, text, extension, colors);
        document.Blocks.Add(paragraph);
        return document;
    }

    internal static FlowDocument Diff(List<DiffLine> lines, TextColors colors)
    {
        FlowDocument document = Empty(colors, true);
        foreach (DiffLine line in lines)
        {
            Paragraph paragraph = new Paragraph(new Run((line.Change > 0 ? "+ " : line.Change < 0 ? "- " : "  ") + line.Text));
            paragraph.Margin = new Thickness(0);
            if (line.Change > 0) paragraph.Background = new SolidColorBrush(colors.Added);
            else if (line.Change < 0) paragraph.Background = new SolidColorBrush(colors.Removed);
            document.Blocks.Add(paragraph);
        }
        return document;
    }

    static readonly Regex InlinePattern = new Regex(
        @"(?<code>`[^`\n]+`)|(?<image>!\[[^\]]*\]\([^)]*\))|(?<link>\[[^\]]+\]\([^)]*\))|(?<bold>\*\*.+?\*\*|__.+?__)|(?<strike>~~.+?~~)|(?<em>(?<![\w*])\*[^*\s][^*\n]*\*|(?<![\w_])_[^_\s][^_\n]*_)",
        RegexOptions.ExplicitCapture);

    static void AddInline(InlineCollection target, string text, TextColors colors)
    {
        int at = 0;
        foreach (Match match in InlinePattern.Matches(text))
        {
            if (match.Index > at) target.Add(new Run(text.Substring(at, match.Index - at)));
            string value = match.Value;
            if (match.Groups["code"].Success)
            {
                Run code = new Run(value.Substring(1, value.Length - 2));
                code.FontFamily = new FontFamily("Consolas");
                code.Background = new SolidColorBrush(colors.CodeFill);
                target.Add(code);
            }
            else if (match.Groups["image"].Success)
            {
                Run image = new Run("[picture: " + value.Substring(2, value.IndexOf(']') - 2) + "]");
                image.Foreground = new SolidColorBrush(colors.Muted);
                target.Add(image);
            }
            else if (match.Groups["link"].Success)
            {
                Span link = new Span();
                AddInline(link.Inlines, value.Substring(1, value.IndexOf(']') - 1), colors);
                link.Foreground = new SolidColorBrush(colors.Link);
                link.TextDecorations = TextDecorations.Underline;
                target.Add(link);
            }
            else if (match.Groups["bold"].Success)
            {
                Bold bold = new Bold();
                AddInline(bold.Inlines, value.Substring(2, value.Length - 4), colors);
                target.Add(bold);
            }
            else if (match.Groups["strike"].Success)
            {
                Span strike = new Span();
                AddInline(strike.Inlines, value.Substring(2, value.Length - 4), colors);
                strike.TextDecorations = TextDecorations.Strikethrough;
                target.Add(strike);
            }
            else
            {
                Italic italic = new Italic();
                AddInline(italic.Inlines, value.Substring(1, value.Length - 2), colors);
                target.Add(italic);
            }
            at = match.Index + match.Length;
        }
        if (at < text.Length) target.Add(new Run(text.Substring(at)));
    }

    // Written in an Arabic or Hebrew script, a block reads from the right.
    static FlowDirection DirectionOf(string text)
    {
        foreach (char c in text)
        {
            if (!char.IsLetter(c)) continue;
            return (c >= 0x0590 && c <= 0x08FF) || (c >= 0xFB1D && c <= 0xFEFC) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        }
        return FlowDirection.LeftToRight;
    }

    static Paragraph TextParagraph(string text, TextColors colors)
    {
        Paragraph paragraph = new Paragraph();
        paragraph.Margin = new Thickness(0, 0, 0, 9);
        paragraph.LineHeight = 20;
        paragraph.FlowDirection = DirectionOf(text);
        AddInline(paragraph.Inlines, text, colors);
        return paragraph;
    }

    static readonly Regex Heading = new Regex(@"^(#{1,6})\s+(.*?)\s*#*\s*$");
    static readonly Regex Bullet = new Regex(@"^(\s*)([-*+])\s+(.*)$");
    static readonly Regex Numbered = new Regex(@"^(\s*)(\d+)[.)]\s+(.*)$");
    static readonly Regex Rule = new Regex(@"^\s*([-*_])(\s*\1){2,}\s*$");
    static readonly Regex TableRule = new Regex(@"^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$");

    internal static FlowDocument Markdown(string source, TextColors colors)
    {
        FlowDocument document = Empty(colors, false);
        string[] lines = source.Replace("\r\n", "\n").Split('\n');
        int at = 0;
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Trim() != "---") continue;
                at = i + 1;
                break;
            }
        }

        while (at < lines.Length)
        {
            string line = lines[at];
            if (line.Trim().Length == 0)
            {
                at++;
                continue;
            }

            if (line.TrimStart().StartsWith("```") || line.TrimStart().StartsWith("~~~"))
            {
                string fence = line.TrimStart().Substring(0, 3);
                string language = line.TrimStart().Substring(3).Trim();
                StringBuilder code = new StringBuilder();
                at++;
                while (at < lines.Length && !lines[at].TrimStart().StartsWith(fence))
                {
                    if (code.Length > 0) code.Append('\n');
                    code.Append(lines[at]);
                    at++;
                }
                at++;
                Paragraph block = new Paragraph();
                block.FontFamily = new FontFamily("Consolas");
                block.FontSize = 12.5;
                block.Padding = new Thickness(10, 8, 10, 8);
                block.Margin = new Thickness(0, 2, 0, 11);
                block.Background = new SolidColorBrush(colors.CodeFill);
                block.FlowDirection = FlowDirection.LeftToRight;
                CodeColorer.Fill(block, code.ToString(), "." + LanguageExtension(language), colors);
                document.Blocks.Add(block);
                continue;
            }

            Match heading = Heading.Match(line);
            if (heading.Success)
            {
                int level = heading.Groups[1].Length;
                Paragraph title = new Paragraph();
                title.FontWeight = FontWeights.SemiBold;
                title.FontSize = level == 1 ? 23 : level == 2 ? 19 : level == 3 ? 16 : 14;
                title.Margin = new Thickness(0, level <= 2 ? 12 : 8, 0, 7);
                title.FlowDirection = DirectionOf(heading.Groups[2].Value);
                AddInline(title.Inlines, heading.Groups[2].Value, colors);
                document.Blocks.Add(title);
                at++;
                continue;
            }

            if (Rule.IsMatch(line))
            {
                Paragraph rule = new Paragraph();
                rule.BorderBrush = new SolidColorBrush(colors.TableEdge);
                rule.BorderThickness = new Thickness(0, 0, 0, 1);
                rule.Margin = new Thickness(0, 6, 0, 12);
                rule.FontSize = 2;
                document.Blocks.Add(rule);
                at++;
                continue;
            }

            if (line.TrimStart().StartsWith(">"))
            {
                StringBuilder quote = new StringBuilder();
                while (at < lines.Length && lines[at].TrimStart().StartsWith(">"))
                {
                    if (quote.Length > 0) quote.Append(' ');
                    quote.Append(lines[at].TrimStart().Substring(1).Trim());
                    at++;
                }
                Paragraph block = TextParagraph(quote.ToString(), colors);
                block.Foreground = new SolidColorBrush(colors.Muted);
                block.BorderBrush = new SolidColorBrush(colors.QuoteEdge);
                block.BorderThickness = block.FlowDirection == FlowDirection.RightToLeft ? new Thickness(0, 0, 3, 0) : new Thickness(3, 0, 0, 0);
                block.Padding = new Thickness(10, 2, 10, 2);
                document.Blocks.Add(block);
                continue;
            }

            if (line.Contains("|") && at + 1 < lines.Length && TableRule.IsMatch(lines[at + 1]) && lines[at + 1].Contains("-"))
            {
                document.Blocks.Add(BuildTable(lines, ref at, colors));
                continue;
            }

            if (Bullet.IsMatch(line) || Numbered.IsMatch(line))
            {
                document.Blocks.Add(BuildList(lines, ref at, colors));
                continue;
            }

            StringBuilder text = new StringBuilder(line.Trim());
            at++;
            while (at < lines.Length && lines[at].Trim().Length > 0 && !IsBlockStart(lines[at]))
            {
                text.Append(' ').Append(lines[at].Trim());
                at++;
            }
            document.Blocks.Add(TextParagraph(text.ToString(), colors));
        }
        return document;
    }

    static bool IsBlockStart(string line)
    {
        string start = line.TrimStart();
        return start.StartsWith("```") || start.StartsWith("~~~") || start.StartsWith(">") || Heading.IsMatch(line) || Rule.IsMatch(line) || Bullet.IsMatch(line) || Numbered.IsMatch(line);
    }

    static string LanguageExtension(string language)
    {
        switch (language.ToLowerInvariant())
        {
            case "python": return "py";
            case "javascript": return "js";
            case "typescript": return "ts";
            case "csharp": case "c#": return "cs";
            case "bash": case "shell": return "sh";
            case "powershell": return "ps1";
            case "yaml": return "yml";
            default: return language.ToLowerInvariant();
        }
    }

    static List BuildList(string[] lines, ref int at, TextColors colors)
    {
        bool numbered = Numbered.IsMatch(lines[at]);
        int indent = lines[at].Length - lines[at].TrimStart().Length;
        List list = new List();
        list.MarkerStyle = numbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc;
        list.Margin = new Thickness(0, 0, 0, 9);
        list.Padding = new Thickness(22, 0, 0, 0);
        ListItem item = null;
        while (at < lines.Length)
        {
            string line = lines[at];
            if (line.Trim().Length == 0)
            {
                if (at + 1 < lines.Length && (Bullet.IsMatch(lines[at + 1]) || Numbered.IsMatch(lines[at + 1])))
                {
                    at++;
                    continue;
                }
                break;
            }
            Match match = Bullet.Match(line);
            if (!match.Success) match = Numbered.Match(line);
            if (!match.Success)
            {
                if (item == null || IsBlockStart(line)) break;
                Paragraph last = item.Blocks.LastBlock as Paragraph;
                if (last != null) AddInline(last.Inlines, " " + line.Trim(), colors);
                at++;
                continue;
            }
            int depth = match.Groups[1].Length;
            if (depth > indent && item != null)
            {
                item.Blocks.Add(BuildList(lines, ref at, colors));
                continue;
            }
            if (depth < indent) break;
            if (Numbered.IsMatch(line) != numbered) break;
            Paragraph paragraph = new Paragraph();
            paragraph.Margin = new Thickness(0, 0, 0, 3);
            AddInline(paragraph.Inlines, match.Groups[3].Value, colors);
            item = new ListItem(paragraph);
            list.ListItems.Add(item);
            at++;
        }
        return list;
    }

    static string[] SplitTableRow(string line)
    {
        string trimmed = line.Trim();
        if (trimmed.StartsWith("|")) trimmed = trimmed.Substring(1);
        if (trimmed.EndsWith("|")) trimmed = trimmed.Substring(0, trimmed.Length - 1);
        return trimmed.Split('|');
    }

    static Table BuildTable(string[] lines, ref int at, TextColors colors)
    {
        string[] head = SplitTableRow(lines[at]);
        Table table = new Table();
        table.CellSpacing = 0;
        table.Margin = new Thickness(0, 2, 0, 12);
        table.FlowDirection = DirectionOf(lines[at]);
        for (int i = 0; i < head.Length; i++) table.Columns.Add(new TableColumn());
        TableRowGroup group = new TableRowGroup();
        table.RowGroups.Add(group);
        group.Rows.Add(BuildTableRow(head, head.Length, true, colors));
        at += 2;
        while (at < lines.Length && lines[at].Contains("|"))
        {
            group.Rows.Add(BuildTableRow(SplitTableRow(lines[at]), head.Length, false, colors));
            at++;
        }
        return table;
    }

    static TableRow BuildTableRow(string[] cells, int count, bool head, TextColors colors)
    {
        TableRow row = new TableRow();
        for (int i = 0; i < count; i++)
        {
            Paragraph paragraph = new Paragraph();
            paragraph.Margin = new Thickness(0);
            if (head) paragraph.FontWeight = FontWeights.SemiBold;
            AddInline(paragraph.Inlines, i < cells.Length ? cells[i].Trim() : "", colors);
            TableCell cell = new TableCell(paragraph);
            cell.Padding = new Thickness(7, 4, 7, 4);
            cell.BorderBrush = new SolidColorBrush(colors.TableEdge);
            cell.BorderThickness = new Thickness(0, 0, 0, 1);
            if (head) cell.Background = new SolidColorBrush(colors.CodeFill);
            row.Cells.Add(cell);
        }
        return row;
    }
}

sealed class DiffLine
{
    internal int Change;
    internal string Text;
}

static class LineDiff
{
    // Null when the two versions are too long and too different to line up.
    internal static List<DiffLine> Compare(string before, string after)
    {
        string[] a = before.Replace("\r\n", "\n").Split('\n');
        string[] b = after.Replace("\r\n", "\n").Split('\n');
        int start = 0;
        while (start < a.Length && start < b.Length && a[start] == b[start]) start++;
        int endA = a.Length, endB = b.Length;
        while (endA > start && endB > start && a[endA - 1] == b[endB - 1])
        {
            endA--;
            endB--;
        }
        int n = endA - start, m = endB - start;
        if ((long)n * m > 16000000) return null;

        int[,] table = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                table[i, j] = a[start + i] == b[start + j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        List<DiffLine> lines = new List<DiffLine>();
        for (int i = 0; i < start; i++) lines.Add(Line(0, a[i]));
        int x = 0, y = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && a[start + x] == b[start + y])
            {
                lines.Add(Line(0, a[start + x]));
                x++;
                y++;
            }
            else if (x < n && (y == m || table[x + 1, y] >= table[x, y + 1]))
            {
                lines.Add(Line(-1, a[start + x]));
                x++;
            }
            else
            {
                lines.Add(Line(1, b[start + y]));
                y++;
            }
        }
        for (int i = endA; i < a.Length; i++) lines.Add(Line(0, a[i]));
        return lines;
    }

    static DiffLine Line(int change, string text)
    {
        DiffLine line = new DiffLine();
        line.Change = change;
        line.Text = text;
        return line;
    }
}

// Markdown, code, plain text and comparisons. The words can be selected and copied as usual.
sealed class TextBody : PreviewBody
{
    readonly Func<TextColors, FlowDocument> build;
    readonly RichTextBox box = new RichTextBox();
    readonly List<TextRange> found = new List<TextRange>();
    readonly string details;
    int foundIndex = -1;
    string lastQuery;

    internal TextBody(Func<TextColors, FlowDocument> makeDocument, string detailText)
    {
        build = makeDocument;
        details = detailText;
        box.IsReadOnly = true;
        box.IsDocumentEnabled = false;
        box.Background = Brushes.Transparent;
        box.BorderThickness = new Thickness(0);
        box.Foreground = WidgetWindow.Text;
        box.CaretBrush = Brushes.Transparent;
        box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        box.FocusVisualStyle = null;
        box.Document = build(new TextColors(WidgetWindow.IsLight, false));
        box.SelectionChanged += delegate { RaiseSelection(); };
        Children.Add(box);
    }

    internal override string Details { get { return details; } }
    internal override void FocusView() { box.Focus(); }
    internal override string SelectedText { get { return box.Selection.Text.Trim(); } }
    internal override void ClearSelection() { box.Selection.Select(box.Document.ContentStart, box.Document.ContentStart); }
    internal override bool CanFind { get { return true; } }
    internal override object SaveState() { return box.VerticalOffset; }
    internal override void RestoreState(object state)
    {
        if (!(state is double)) return;
        double offset = (double)state;
        Dispatcher.BeginInvoke(new Action(delegate { box.ScrollToVerticalOffset(offset); }), DispatcherPriority.Loaded);
    }

    // Highlights change the document, so clearing them means building it again.
    void Rebuild()
    {
        if (found.Count == 0 && lastQuery == null) return;
        double offset = box.VerticalOffset;
        found.Clear();
        foundIndex = -1;
        lastQuery = null;
        box.Document = build(new TextColors(WidgetWindow.IsLight, false));
        box.ScrollToVerticalOffset(offset);
    }

    internal override Task<int> Find(string query, Action<string> progress)
    {
        Rebuild();
        string wanted = query.Trim();
        if (wanted.Length == 0) return Task.FromResult(0);
        lastQuery = wanted;

        // One string of the whole document, with a map back to where each piece of it sits.
        StringBuilder text = new StringBuilder();
        List<int> offsets = new List<int>();
        List<TextPointer> pointers = new List<TextPointer>();
        TextPointer walk = box.Document.ContentStart;
        while (walk != null)
        {
            TextPointerContext context = walk.GetPointerContext(LogicalDirection.Forward);
            if (context == TextPointerContext.Text)
            {
                string piece = walk.GetTextInRun(LogicalDirection.Forward);
                offsets.Add(text.Length);
                pointers.Add(walk);
                text.Append(piece);
            }
            else if (context == TextPointerContext.ElementEnd && walk.Parent is Paragraph) text.Append('\n');
            else if (context == TextPointerContext.ElementStart && walk.GetAdjacentElement(LogicalDirection.Forward) is LineBreak) text.Append('\n');
            walk = walk.GetNextContextPosition(LogicalDirection.Forward);
        }

        string all = text.ToString();
        List<int[]> spots = new List<int[]>();
        int at = all.IndexOf(wanted, StringComparison.CurrentCultureIgnoreCase);
        while (at >= 0 && spots.Count < 2000)
        {
            spots.Add(new[] { at, wanted.Length });
            at = all.IndexOf(wanted, at + wanted.Length, StringComparison.CurrentCultureIgnoreCase);
        }
        foreach (int[] spot in spots)
        {
            TextPointer start = PointerAt(offsets, pointers, spot[0]);
            TextPointer end = PointerAt(offsets, pointers, spot[0] + spot[1] - 1);
            if (start == null || end == null) continue;
            found.Add(new TextRange(start, end.GetPositionAtOffset(1)));
        }
        foreach (TextRange range in found) range.ApplyPropertyValue(TextElement.BackgroundProperty, new SolidColorBrush(PreviewChrome.Mark(false)));
        if (found.Count > 0) FindStep(1);
        return Task.FromResult(found.Count);
    }

    static TextPointer PointerAt(List<int> offsets, List<TextPointer> pointers, int offset)
    {
        int index = offsets.BinarySearch(offset);
        if (index < 0) index = ~index - 1;
        return index < 0 ? null : pointers[index].GetPositionAtOffset(offset - offsets[index]);
    }

    internal override string FindStep(int direction)
    {
        if (found.Count == 0) return "";
        if (foundIndex >= 0) found[foundIndex].ApplyPropertyValue(TextElement.BackgroundProperty, new SolidColorBrush(PreviewChrome.Mark(false)));
        foundIndex = foundIndex < 0 ? 0 : (foundIndex + direction + found.Count) % found.Count;
        TextRange range = found[foundIndex];
        range.ApplyPropertyValue(TextElement.BackgroundProperty, new SolidColorBrush(PreviewChrome.Mark(true)));
        Rect where = range.Start.GetCharacterRect(LogicalDirection.Forward);
        if (where.Top < 0 || where.Bottom > box.ViewportHeight) box.ScrollToVerticalOffset(box.VerticalOffset + where.Top - box.ViewportHeight / 3);
        return (foundIndex + 1) + " of " + found.Count;
    }

    internal override void ClearFind() { Rebuild(); }

    internal override Task<DocumentPaginator> BuildPrint(Size paper)
    {
        FlowDocument printed = build(new TextColors(true, true));
        printed.PageWidth = paper.Width;
        printed.PageHeight = paper.Height;
        printed.ColumnWidth = paper.Width;
        printed.PagePadding = new Thickness(60);
        return Task.FromResult(((IDocumentPaginatorSource)printed).DocumentPaginator);
    }
}

// CSV and TSV as a real table. Search selects the matching cells.
sealed class TableBody : PreviewBody
{
    readonly DataGrid grid = new DataGrid();
    readonly List<string[]> rows;
    readonly string[] head;
    readonly List<DataGridCellInfo> found = new List<DataGridCellInfo>();
    int foundIndex = -1;

    internal TableBody(string text, string extension)
    {
        List<string[]> parsed = Parse(text, extension);
        head = parsed.Count > 0 ? parsed[0] : new string[0];
        rows = parsed.Count > 1 ? parsed.GetRange(1, parsed.Count - 1) : new List<string[]>();
        int columns = head.Length;
        foreach (string[] row in rows) columns = Math.Max(columns, row.Length);

        Color text1 = WidgetWindow.Text.Color, soft = WidgetWindow.OverlaySoft.Color, edge = WidgetWindow.CardEdge.Color;
        grid.AutoGenerateColumns = false;
        grid.IsReadOnly = true;
        grid.CanUserAddRows = false;
        grid.CanUserDeleteRows = false;
        grid.HeadersVisibility = DataGridHeadersVisibility.Column;
        grid.SelectionUnit = DataGridSelectionUnit.Cell;
        grid.SelectionMode = DataGridSelectionMode.Extended;
        grid.ClipboardCopyMode = DataGridClipboardCopyMode.ExcludeHeader;
        grid.Background = Brushes.Transparent;
        grid.RowBackground = Brushes.Transparent;
        grid.AlternatingRowBackground = new SolidColorBrush(Color.FromArgb(0x0C, soft.R, soft.G, soft.B));
        grid.Foreground = new SolidColorBrush(text1);
        grid.BorderThickness = new Thickness(0);
        grid.GridLinesVisibility = DataGridGridLinesVisibility.All;
        grid.HorizontalGridLinesBrush = new SolidColorBrush(edge);
        grid.VerticalGridLinesBrush = new SolidColorBrush(edge);
        grid.FontSize = 12.5;
        grid.EnableRowVirtualization = true;
        grid.EnableColumnVirtualization = true;

        Style headStyle = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
        headStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(soft)));
        headStyle.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(text1)));
        headStyle.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        headStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5)));
        headStyle.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(edge)));
        headStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
        grid.ColumnHeaderStyle = headStyle;

        Style cellStyle = new Style(typeof(DataGridCell));
        cellStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        cellStyle.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        Trigger selected = new Trigger();
        selected.Property = DataGridCell.IsSelectedProperty;
        selected.Value = true;
        selected.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(PreviewChrome.Mark(true))));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(text1)));
        cellStyle.Triggers.Add(selected);
        grid.CellStyle = cellStyle;

        for (int i = 0; i < columns; i++)
        {
            DataGridTextColumn column = new DataGridTextColumn();
            column.Header = i < head.Length && head[i].Length > 0 ? head[i] : "Column " + (i + 1);
            column.Binding = new System.Windows.Data.Binding("[" + i + "]");
            column.MaxWidth = 420;
            grid.Columns.Add(column);
        }
        // Every row needs every column, or a short row's binding fails.
        for (int r = 0; r < rows.Count; r++)
        {
            if (rows[r].Length < columns)
            {
                string[] full = new string[columns];
                Array.Copy(rows[r], full, rows[r].Length);
                rows[r] = full;
            }
        }
        grid.ItemsSource = rows;
        grid.SelectedCellsChanged += delegate { RaiseSelection(); };
        Children.Add(grid);
    }

    internal override void FocusView() { grid.Focus(); }
    internal int RowCount { get { return rows.Count; } }
    internal int ColumnCount { get { return grid.Columns.Count; } }
    internal override string Details { get { return rows.Count + " row" + (rows.Count == 1 ? "" : "s") + " · " + grid.Columns.Count + " column" + (grid.Columns.Count == 1 ? "" : "s"); } }

    internal static List<string[]> Parse(string text, string extension)
    {
        char separator = '\t';
        if (extension != ".tsv")
        {
            int end = text.IndexOf('\n');
            string first = end >= 0 ? text.Substring(0, end) : text;
            int commas = 0, semicolons = 0;
            foreach (char c in first)
            {
                if (c == ',') commas++;
                else if (c == ';') semicolons++;
            }
            separator = semicolons > commas ? ';' : ',';
        }

        List<string[]> rows = new List<string[]>();
        List<string> row = new List<string>();
        StringBuilder cell = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"' && cell.Length == 0) quoted = true;
            else if (c == separator)
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if (c == '\n')
            {
                row.Add(cell.ToString().TrimEnd('\r'));
                cell.Clear();
                rows.Add(row.ToArray());
                row.Clear();
            }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString().TrimEnd('\r'));
            rows.Add(row.ToArray());
        }
        return rows;
    }

    internal override string SelectedText
    {
        get
        {
            StringBuilder text = new StringBuilder();
            object lastRow = null;
            foreach (DataGridCellInfo cell in grid.SelectedCells)
            {
                if (text.Length > 0) text.Append(cell.Item == lastRow ? '\t' : '\n');
                lastRow = cell.Item;
                string[] row = cell.Item as string[];
                int column = cell.Column == null ? -1 : grid.Columns.IndexOf(cell.Column);
                if (row != null && column >= 0 && column < row.Length) text.Append(row[column]);
            }
            return text.ToString();
        }
    }

    internal override void ClearSelection() { grid.SelectedCells.Clear(); }
    internal override bool CanFind { get { return true; } }

    internal override Task<int> Find(string query, Action<string> progress)
    {
        ClearFind();
        string wanted = query.Trim();
        if (wanted.Length == 0) return Task.FromResult(0);
        foreach (string[] row in rows)
        {
            for (int c = 0; c < row.Length && c < grid.Columns.Count; c++)
            {
                if (row[c] != null && row[c].IndexOf(wanted, StringComparison.CurrentCultureIgnoreCase) >= 0) found.Add(new DataGridCellInfo(row, grid.Columns[c]));
            }
        }
        foreach (DataGridCellInfo cell in found) grid.SelectedCells.Add(cell);
        if (found.Count > 0) FindStep(1);
        return Task.FromResult(found.Count);
    }

    internal override string FindStep(int direction)
    {
        if (found.Count == 0) return "";
        foundIndex = foundIndex < 0 ? 0 : (foundIndex + direction + found.Count) % found.Count;
        grid.ScrollIntoView(found[foundIndex].Item, found[foundIndex].Column);
        return (foundIndex + 1) + " of " + found.Count;
    }

    internal override void ClearFind()
    {
        found.Clear();
        foundIndex = -1;
        grid.SelectedCells.Clear();
    }

    internal override Task<DocumentPaginator> BuildPrint(Size paper)
    {
        TextColors colors = new TextColors(true, true);
        FlowDocument printed = DocumentBuilder.Empty(colors, false);
        printed.FontSize = 10;
        Table table = new Table();
        for (int i = 0; i < grid.Columns.Count; i++) table.Columns.Add(new TableColumn());
        TableRowGroup group = new TableRowGroup();
        table.RowGroups.Add(group);
        group.Rows.Add(PrintRow(head, true));
        foreach (string[] row in rows) group.Rows.Add(PrintRow(row, false));
        printed.Blocks.Add(table);
        printed.PageWidth = paper.Width;
        printed.PageHeight = paper.Height;
        printed.ColumnWidth = paper.Width;
        printed.PagePadding = new Thickness(40);
        return Task.FromResult(((IDocumentPaginatorSource)printed).DocumentPaginator);
    }

    TableRow PrintRow(string[] cells, bool bold)
    {
        TableRow row = new TableRow();
        for (int i = 0; i < grid.Columns.Count; i++)
        {
            Paragraph paragraph = new Paragraph(new Run(i < cells.Length && cells[i] != null ? cells[i] : ""));
            paragraph.Margin = new Thickness(0);
            if (bold) paragraph.FontWeight = FontWeights.SemiBold;
            TableCell cell = new TableCell(paragraph);
            cell.BorderBrush = Brushes.Gray;
            cell.BorderThickness = new Thickness(0, 0, 0, 0.5);
            cell.Padding = new Thickness(3);
            row.Cells.Add(cell);
        }
        return row;
    }
}

// A picture that zooms where the mouse points and drags around once it's larger than the view.
sealed class ImageBody : PreviewBody
{
    readonly BitmapSource picture;
    readonly Image image = new Image();
    readonly ScrollViewer scroller = new ScrollViewer();
    readonly TextBlock zoomLabel = PreviewChrome.Label("");
    double zoom = 1;
    bool fit = true;
    bool dragging;
    Point dragFrom;
    double fromX, fromY;

    internal ImageBody(string file)
    {
        BitmapImage loaded = new BitmapImage();
        loaded.BeginInit();
        loaded.CacheOption = BitmapCacheOption.OnLoad;
        loaded.UriSource = new Uri(file);
        loaded.EndInit();
        loaded.Freeze();
        picture = loaded;

        Focusable = true;
        FocusVisualStyle = null;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        StackPanel row = PreviewChrome.Row();
        row.Children.Add(PreviewChrome.Icon("", "Zoom out", delegate { ZoomAround(1 / 1.2, null); }));
        row.Children.Add(zoomLabel);
        row.Children.Add(PreviewChrome.Icon("", "Zoom in", delegate { ZoomAround(1.2, null); }));
        row.Children.Add(PreviewChrome.Icon("", "Fit to window", delegate { fit = true; Apply(); }));
        row.Children.Add(PreviewChrome.Icon("", "Actual size", ActualSize));
        Children.Add(PreviewChrome.Bar(row));

        image.Source = picture;
        image.Stretch = Stretch.Fill;
        image.HorizontalAlignment = HorizontalAlignment.Center;
        image.VerticalAlignment = VerticalAlignment.Center;
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        scroller.Content = image;
        scroller.Focusable = false;
        scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        scroller.SizeChanged += delegate { if (fit) Apply(); };
        scroller.PreviewMouseWheel += delegate(object s, MouseWheelEventArgs e)
        {
            e.Handled = true;
            ZoomAround(e.Delta > 0 ? 1.15 : 1 / 1.15, e.GetPosition(scroller));
        };
        scroller.PreviewMouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
        {
            if (scroller.ScrollableWidth <= 0 && scroller.ScrollableHeight <= 0) return;
            dragging = true;
            dragFrom = e.GetPosition(scroller);
            fromX = scroller.HorizontalOffset;
            fromY = scroller.VerticalOffset;
            scroller.CaptureMouse();
            scroller.Cursor = Cursors.SizeAll;
            e.Handled = true;
        };
        scroller.PreviewMouseMove += delegate(object s, MouseEventArgs e)
        {
            if (!dragging) return;
            Point now = e.GetPosition(scroller);
            scroller.ScrollToHorizontalOffset(fromX - (now.X - dragFrom.X));
            scroller.ScrollToVerticalOffset(fromY - (now.Y - dragFrom.Y));
        };
        scroller.PreviewMouseLeftButtonUp += delegate
        {
            if (!dragging) return;
            dragging = false;
            scroller.ReleaseMouseCapture();
            scroller.Cursor = null;
        };
        Grid.SetRow(scroller, 1);
        Children.Add(scroller);
        Loaded += delegate { Apply(); };
    }

    internal double Zoom { get { return zoom; } }
    internal override string Details { get { return picture.PixelWidth + " × " + picture.PixelHeight; } }

    double NaturalWidth { get { return picture.PixelWidth; } }
    double NaturalHeight { get { return picture.PixelHeight; } }

    void Apply()
    {
        if (fit)
        {
            double width = Math.Max(40, scroller.ActualWidth - 2), height = Math.Max(40, scroller.ActualHeight - 2);
            zoom = Math.Min(1, Math.Min(width / Math.Max(1, NaturalWidth), height / Math.Max(1, NaturalHeight)));
        }
        zoomLabel.Text = Math.Round(zoom * 100) + "%";
        image.Width = NaturalWidth * zoom;
        image.Height = NaturalHeight * zoom;
    }

    internal void ActualSize()
    {
        fit = false;
        zoom = 1;
        Apply();
    }

    internal void ZoomAround(double factor, Point? point)
    {
        Point at = point ?? new Point(scroller.ViewportWidth / 2, scroller.ViewportHeight / 2);
        Point inImage = scroller.TranslatePoint(at, image);
        double before = zoom;
        fit = false;
        zoom = Math.Max(0.05, Math.Min(16, zoom * factor));
        Apply();
        scroller.UpdateLayout();
        double scale = zoom / before;
        Point after = image.TranslatePoint(new Point(inImage.X * scale, inImage.Y * scale), scroller);
        scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset + after.X - at.X);
        scroller.ScrollToVerticalOffset(scroller.VerticalOffset + after.Y - at.Y);
    }

    internal override bool HandleKey(KeyEventArgs e)
    {
        if (e.Key == Key.OemPlus || e.Key == Key.Add) ZoomAround(1.2, null);
        else if (e.Key == Key.OemMinus || e.Key == Key.Subtract) ZoomAround(1 / 1.2, null);
        else if (e.Key == Key.D0 || e.Key == Key.NumPad0) { fit = true; Apply(); }
        else if (e.Key == Key.D1 || e.Key == Key.NumPad1) ActualSize();
        else return false;
        return true;
    }

    internal override Task<DocumentPaginator> BuildPrint(Size paper)
    {
        double scale = Math.Min(1, Math.Min(paper.Width / Math.Max(1, NaturalWidth), paper.Height / Math.Max(1, NaturalHeight)));
        Image printed = new Image();
        printed.Source = picture;
        printed.Width = NaturalWidth * scale;
        printed.Height = NaturalHeight * scale;
        FixedPage page = new FixedPage();
        page.Width = paper.Width;
        page.Height = paper.Height;
        page.Children.Add(printed);
        PageContent content = new PageContent();
        ((System.Windows.Markup.IAddChild)content).AddChild(page);
        FixedDocument document = new FixedDocument();
        document.DocumentPaginator.PageSize = paper;
        document.Pages.Add(content);
        return Task.FromResult(document.DocumentPaginator);
    }
}

// Two versions next to each other, for files that can't be compared line by line.
sealed class CompareBody : PreviewBody
{
    readonly PageViewer left;
    readonly PageViewer right;

    internal CompareBody(PageViewer older, string olderLabel, PageViewer newer, string newerLabel)
    {
        left = older;
        right = newer;
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition());
        AddSide(older, olderLabel, 0);
        AddSide(newer, newerLabel, 1);
    }

    void AddSide(PageViewer viewer, string label, int column)
    {
        TextBlock title = PreviewChrome.Label(label);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.Margin = new Thickness(0, 0, 0, 5);
        DockPanel.SetDock(title, Dock.Top);
        DockPanel side = new DockPanel();
        side.Children.Add(title);
        side.Children.Add(viewer);
        Grid.SetColumn(side, column);
        Children.Add(side);
    }

    internal override string Details { get { return "Comparing versions"; } }

    internal override void Release()
    {
        left.Release();
        right.Release();
    }
}

sealed class MessageBody : PreviewBody
{
    internal readonly string Message;

    internal MessageBody(string message)
    {
        Message = message;
        TextBlock text = new TextBlock();
        text.Text = message;
        text.FontSize = 13;
        text.Foreground = WidgetWindow.Muted;
        text.TextWrapping = TextWrapping.Wrap;
        text.TextAlignment = TextAlignment.Center;
        text.VerticalAlignment = VerticalAlignment.Center;
        text.Margin = new Thickness(24);
        Children.Add(text);
    }
}

// The shape of a page while the real one is on its way.
sealed class LoadingBody : PreviewBody
{
    internal LoadingBody()
    {
        StackPanel lines = new StackPanel();
        lines.Margin = new Thickness(26, 30, 26, 0);
        double[] widths = { 0.55, 1, 0.92, 1, 0.7, 0, 1, 0.88, 0.95, 0.6 };
        foreach (double width in widths)
        {
            Border line = new Border();
            line.Height = width == 0 ? 8 : 9;
            line.Margin = new Thickness(0, 0, (1 - width) * 180, 11);
            line.CornerRadius = new CornerRadius(4);
            line.Background = width == 0 ? Brushes.Transparent : WidgetWindow.OverlayMid;
            lines.Children.Add(line);
        }

        Border page = new Border();
        page.Child = lines;
        page.Width = 280;
        page.Height = 370;
        page.CornerRadius = new CornerRadius(6);
        page.Background = WidgetWindow.OverlaySoft;
        page.HorizontalAlignment = HorizontalAlignment.Center;
        page.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(page);

        DoubleAnimation pulse = new DoubleAnimation(1, 0.45, new Duration(TimeSpan.FromSeconds(0.9)));
        pulse.AutoReverse = true;
        pulse.RepeatBehavior = RepeatBehavior.Forever;
        pulse.EasingFunction = new SineEase();
        page.BeginAnimation(OpacityProperty, pulse);
    }
}

// The file's own Windows preview, used only when the file can't be turned into pages here.
sealed class NativeBody : PreviewBody
{
    readonly ShellPreviewHost host;

    internal NativeBody(string file)
    {
        host = new ShellPreviewHost(file);
        Children.Add(host);
    }

    internal override void Release() { host.Dispose(); }
}

// Everything the preview shows, wherever it's hosted.
sealed class FilePreviewPane : Border
{
    const long TextLimit = 20 * 1024 * 1024;

    static readonly string[] OpenableTypes =
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".md", ".csv", ".rtf",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".html", ".htm",
    };

    internal event Action CloseRequested;
    internal event Action FullScreenRequested;
    internal event Action HeaderPressed;
    internal event Action<string, string> AskRequested;
    internal Func<List<string>> FileList;

    readonly bool allowNative;
    readonly Grid bodyHost = new Grid();
    readonly ContentControl tileHost = new ContentControl();
    readonly TextBlock nameText = new TextBlock();
    readonly TextBlock detailText = new TextBlock();
    readonly TextBlock positionText = PreviewChrome.Label("");
    readonly StackPanel fileArrows = PreviewChrome.Row();
    readonly StackPanel toolbar = PreviewChrome.Row();
    readonly StackPanel switchRow = PreviewChrome.Row();
    readonly Border previewTab = new Border();
    readonly Border codeTab = new Border();
    readonly Border versionButton = new Border();
    readonly TextBlock versionLabel = new TextBlock();
    readonly Popup versionPopup = new Popup();
    readonly StackPanel versionRows = new StackPanel();
    readonly Border openButton;
    readonly Border fullButton;
    readonly TextBlock fullIcon;
    readonly Border findBar = new Border();
    readonly TextBox findInput = new TextBox();
    readonly TextBlock findCount = PreviewChrome.Label("");
    readonly Border selectionBar = new Border();
    readonly DispatcherTimer refreshTimer = new DispatcherTimer();
    readonly DispatcherTimer findTimer = new DispatcherTimer();
    readonly DispatcherTimer noteTimer = new DispatcherTimer();

    FileSystemWatcher watcher;
    PreviewBody body;
    string path;
    FileVersion viewing;
    FileVersion comparing;
    bool showSource;
    int loadId;
    bool closed;
    string baseDetails = "";

    internal string CurrentFile { get { return path; } }
    internal PreviewBody Body { get { return body; } }
    internal bool IsFindOpen { get { return findBar.Visibility == Visibility.Visible; } }
    internal string DetailLine { get { return detailText.Text; } }

    // True when this file can only show through its own Windows preview, which needs a window of its own.
    internal static bool NeedsNativeHost(string file)
    {
        PreviewKind kind = PreviewTypes.Classify(file);
        if (!PreviewTypes.UsesPages(kind)) return false;
        string extension = Path.GetExtension(file).ToLowerInvariant();
        return extension != ".pdf" && PreviewCache.FindLibreOffice() == null && ShellPreviewHost.FindHandler(extension).HasValue;
    }

    internal FilePreviewPane(bool nativeAllowed)
    {
        allowNative = nativeAllowed;
        Padding = new Thickness(14, 10, 14, 12);
        Resources[SystemColors.ControlBrushKey] = Brushes.Transparent;

        fullButton = PreviewChrome.Icon("", "Full screen", delegate { if (FullScreenRequested != null) FullScreenRequested(); });
        fullIcon = (TextBlock)fullButton.Child;
        openButton = PreviewChrome.Icon("", "Open in its app", OpenInApp);

        DockPanel layout = new DockPanel();
        UIElement header = BuildHeader();
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        UIElement tools = BuildToolbar();
        DockPanel.SetDock(tools, Dock.Top);
        layout.Children.Add(tools);
        UIElement find = BuildFindBar();
        DockPanel.SetDock(find, Dock.Top);
        layout.Children.Add(find);

        BuildSelectionBar();
        Grid area = new Grid();
        area.Children.Add(bodyHost);
        area.Children.Add(selectionBar);
        layout.Children.Add(area);
        Child = layout;

        BuildVersionPopup();

        refreshTimer.Interval = TimeSpan.FromMilliseconds(600);
        refreshTimer.Tick += delegate
        {
            refreshTimer.Stop();
            OnFileChanged();
        };
        findTimer.Interval = TimeSpan.FromMilliseconds(320);
        findTimer.Tick += delegate
        {
            findTimer.Stop();
            RunFind();
        };
        noteTimer.Interval = TimeSpan.FromSeconds(2);
        noteTimer.Tick += delegate
        {
            noteTimer.Stop();
            detailText.Text = baseDetails;
        };
    }

    UIElement BuildHeader()
    {
        nameText.FontSize = 13;
        nameText.FontWeight = FontWeights.SemiBold;
        nameText.Foreground = WidgetWindow.Text;
        nameText.TextTrimming = TextTrimming.CharacterEllipsis;
        detailText.FontSize = 11;
        detailText.Foreground = WidgetWindow.Muted;
        detailText.TextTrimming = TextTrimming.CharacterEllipsis;
        StackPanel text = new StackPanel();
        text.VerticalAlignment = VerticalAlignment.Center;
        text.Children.Add(nameText);
        text.Children.Add(detailText);

        tileHost.Margin = new Thickness(0, 0, 10, 0);
        tileHost.Focusable = false;
        tileHost.IsTabStop = false;
        DockPanel.SetDock(tileHost, Dock.Left);

        Border close = PreviewChrome.Icon("", "Close", delegate { if (CloseRequested != null) CloseRequested(); });
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(fullButton, Dock.Right);

        DockPanel header = new DockPanel();
        header.Background = Brushes.Transparent;
        header.Margin = new Thickness(0, 0, 0, 8);
        header.Children.Add(close);
        header.Children.Add(fullButton);
        header.Children.Add(tileHost);
        header.Children.Add(text);
        header.MouseLeftButtonDown += delegate { if (HeaderPressed != null) HeaderPressed(); };
        return header;
    }

    UIElement BuildToolbar()
    {
        fileArrows.Children.Add(PreviewChrome.Icon("", "Previous file", delegate { Step(-1); }));
        fileArrows.Children.Add(positionText);
        fileArrows.Children.Add(PreviewChrome.Icon("", "Next file", delegate { Step(1); }));
        fileArrows.Margin = new Thickness(0, 0, 8, 0);
        fileArrows.Visibility = Visibility.Collapsed;

        BuildTab(previewTab, "Preview", delegate { SetSource(false); });
        BuildTab(codeTab, "Code", delegate { SetSource(true); });
        switchRow.Children.Add(previewTab);
        switchRow.Children.Add(codeTab);
        switchRow.Margin = new Thickness(0, 0, 6, 0);
        switchRow.VerticalAlignment = VerticalAlignment.Center;
        switchRow.Visibility = Visibility.Collapsed;

        versionLabel.FontSize = 11.5;
        versionLabel.Foreground = WidgetWindow.Muted;
        versionLabel.VerticalAlignment = VerticalAlignment.Center;
        TextBlock chevron = new TextBlock();
        chevron.Text = "";
        chevron.FontFamily = new FontFamily("Segoe MDL2 Assets");
        chevron.FontSize = 8;
        chevron.Foreground = WidgetWindow.Muted;
        chevron.VerticalAlignment = VerticalAlignment.Center;
        chevron.Margin = new Thickness(5, 1, 0, 0);
        StackPanel versionContent = PreviewChrome.Row();
        versionContent.Children.Add(versionLabel);
        versionContent.Children.Add(chevron);
        versionButton.Child = versionContent;
        versionButton.Padding = new Thickness(8, 4, 7, 4);
        versionButton.CornerRadius = new CornerRadius(7);
        versionButton.Background = Brushes.Transparent;
        versionButton.Cursor = Cursors.Hand;
        versionButton.VerticalAlignment = VerticalAlignment.Center;
        versionButton.ToolTip = "Versions";
        versionButton.Visibility = Visibility.Collapsed;
        versionButton.MouseEnter += delegate { versionButton.Background = WidgetWindow.OverlayMid; };
        versionButton.MouseLeave += delegate { versionButton.Background = Brushes.Transparent; };
        ChatWindow.MakeButton(versionButton, ShowVersions);

        StackPanel left = PreviewChrome.Row();
        left.Children.Add(fileArrows);
        left.Children.Add(switchRow);
        left.Children.Add(versionButton);

        toolbar.Children.Add(PreviewChrome.Icon("", "Find (Ctrl+F)", ShowFind));
        toolbar.Children.Add(PreviewChrome.Icon("", "Copy file", CopyFile));
        toolbar.Children.Add(PreviewChrome.Icon("", "Save as", SaveAs));
        toolbar.Children.Add(PreviewChrome.Icon("", "Print", Print));
        toolbar.Children.Add(openButton);
        toolbar.Children.Add(PreviewChrome.Icon("", "Show in folder", ShowInFolder));
        toolbar.HorizontalAlignment = HorizontalAlignment.Right;

        DockPanel row = new DockPanel();
        row.Margin = new Thickness(0, 0, 0, 8);
        DockPanel.SetDock(toolbar, Dock.Right);
        row.Children.Add(toolbar);
        row.Children.Add(left);
        return row;
    }

    static void BuildTab(Border tab, string text, Action action)
    {
        TextBlock label = new TextBlock();
        label.Text = text;
        label.FontSize = 11.5;
        label.Foreground = WidgetWindow.Muted;
        tab.Child = label;
        tab.Padding = new Thickness(9, 4, 9, 4);
        tab.CornerRadius = new CornerRadius(7);
        tab.Background = Brushes.Transparent;
        tab.Cursor = Cursors.Hand;
        ChatWindow.MakeButton(tab, action);
    }

    UIElement BuildFindBar()
    {
        findInput.Width = 190;
        findInput.Background = Brushes.Transparent;
        findInput.BorderThickness = new Thickness(0);
        findInput.Foreground = WidgetWindow.Text;
        findInput.CaretBrush = WidgetWindow.Text;
        findInput.FontSize = 12.5;
        findInput.VerticalAlignment = VerticalAlignment.Center;
        findInput.Margin = new Thickness(8, 0, 4, 0);
        findInput.TextChanged += delegate
        {
            findTimer.Stop();
            findTimer.Start();
        };

        StackPanel row = PreviewChrome.Row();
        row.Children.Add(findInput);
        row.Children.Add(findCount);
        row.Children.Add(PreviewChrome.Icon("", "Previous match", delegate { StepFind(-1); }));
        row.Children.Add(PreviewChrome.Icon("", "Next match", delegate { StepFind(1); }));
        row.Children.Add(PreviewChrome.Icon("", "Close", CloseFind));
        findBar.Child = row;
        findBar.Padding = new Thickness(2, 3, 4, 3);
        findBar.Margin = new Thickness(0, 0, 0, 8);
        findBar.CornerRadius = new CornerRadius(10);
        findBar.Background = WidgetWindow.OverlaySoft;
        findBar.HorizontalAlignment = HorizontalAlignment.Right;
        findBar.Visibility = Visibility.Collapsed;
        return findBar;
    }

    void BuildSelectionBar()
    {
        Border ask = BuildTextButton("Ask Claude", delegate
        {
            string text = body == null ? "" : body.SelectedText;
            if (text.Length > 0 && AskRequested != null) AskRequested(Path.GetFileName(path), text);
        });
        Border copy = BuildTextButton("Copy", delegate
        {
            string text = body == null ? "" : body.SelectedText;
            if (text.Length > 0) SetClipboardText(text);
        });
        StackPanel row = PreviewChrome.Row();
        row.Children.Add(ask);
        row.Children.Add(copy);
        selectionBar.Child = row;
        selectionBar.Padding = new Thickness(4);
        selectionBar.CornerRadius = new CornerRadius(11);
        selectionBar.Background = WidgetWindow.CardFill(WidgetWindow.IsLight);
        selectionBar.BorderBrush = WidgetWindow.CardEdge;
        selectionBar.BorderThickness = new Thickness(1);
        selectionBar.HorizontalAlignment = HorizontalAlignment.Center;
        selectionBar.VerticalAlignment = VerticalAlignment.Bottom;
        selectionBar.Margin = new Thickness(0, 0, 0, 14);
        selectionBar.Visibility = Visibility.Collapsed;
    }

    static Border BuildTextButton(string text, Action action)
    {
        TextBlock label = new TextBlock();
        label.Text = text;
        label.FontSize = 12;
        label.Foreground = WidgetWindow.Text;
        Border button = new Border();
        button.Child = label;
        button.Padding = new Thickness(10, 5, 10, 5);
        button.CornerRadius = new CornerRadius(8);
        button.Background = Brushes.Transparent;
        button.Cursor = Cursors.Hand;
        button.MouseEnter += delegate { button.Background = WidgetWindow.OverlayMid; };
        button.MouseLeave += delegate { button.Background = Brushes.Transparent; };
        ChatWindow.MakeButton(button, action);
        return button;
    }

    void BuildVersionPopup()
    {
        Border panel = new Border();
        panel.Child = versionRows;
        panel.Padding = new Thickness(5);
        panel.CornerRadius = new CornerRadius(11);
        panel.Background = WidgetWindow.CardFill(WidgetWindow.IsLight);
        panel.BorderBrush = WidgetWindow.CardEdge;
        panel.BorderThickness = new Thickness(1);
        panel.MinWidth = 220;
        versionPopup.Child = panel;
        versionPopup.AllowsTransparency = true;
        versionPopup.StaysOpen = false;
        versionPopup.PlacementTarget = versionButton;
        versionPopup.Placement = PlacementMode.Bottom;
    }

    internal void SetFullScreen(bool full)
    {
        fullIcon.Text = full ? "" : "";
        fullButton.ToolTip = full ? "Exit full screen" : "Full screen";
    }

    // Opens a file from the start. A file that's already open keeps its place.
    internal void Open(string file)
    {
        if (path != null && string.Equals(path, file, StringComparison.OrdinalIgnoreCase)) return;
        path = file;
        viewing = null;
        comparing = null;
        showSource = false;
        CloseFind();

        ChatAttachment shown = new ChatAttachment();
        shown.Kind = AttachmentKind.PathOnly;
        shown.Name = Path.GetFileName(file);
        tileHost.Content = ChatWindow.BuildAttachmentTile(shown);
        nameText.Text = shown.Name;
        nameText.ToolTip = file;
        openButton.Visibility = Array.IndexOf(OpenableTypes, Path.GetExtension(file).ToLowerInvariant()) >= 0 ? Visibility.Visible : Visibility.Collapsed;

        WatchFile();
        UpdateArrows();
        ShowBody(new LoadingBody());
        Load(false);
        Task.Run(() => FileVersions.CaptureNow(file)).ContinueWith(delegate { UpdateVersionButton(); }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void WatchFile()
    {
        if (watcher != null) watcher.Dispose();
        watcher = null;
        try
        {
            string folder = Path.GetDirectoryName(path);
            if (folder == null || !Directory.Exists(folder)) return;
            watcher = new FileSystemWatcher(folder, Path.GetFileName(path));
            watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName;
            FileSystemEventHandler changed = delegate { Dispatcher.BeginInvoke(new Action(RestartRefresh)); };
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Deleted += changed;
            watcher.Renamed += delegate { Dispatcher.BeginInvoke(new Action(RestartRefresh)); };
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception e)
        {
            if (!(e is IOException || e is ArgumentException || e is UnauthorizedAccessException)) throw;
        }
    }

    void RestartRefresh()
    {
        if (closed) return;
        refreshTimer.Stop();
        refreshTimer.Start();
    }

    void OnFileChanged()
    {
        if (closed) return;
        Task.Run(() => FileVersions.CaptureNow(path)).ContinueWith(delegate { UpdateVersionButton(); }, TaskScheduler.FromCurrentSynchronizationContext());
        if (viewing == null && comparing == null) Load(true);
    }

    internal void ReloadForTheme()
    {
        if (path == null || closed) return;
        selectionBar.Background = WidgetWindow.CardFill(WidgetWindow.IsLight);
        ((Border)versionPopup.Child).Background = WidgetWindow.CardFill(WidgetWindow.IsLight);
        if (!(body is PageViewer) && !(body is ImageBody) && !(body is LoadingBody)) Load(true);
        UpdateTabs();
    }

    string ShownFile { get { return viewing != null ? viewing.File : path; } }

    async void Load(bool keepPlace)
    {
        int id = ++loadId;
        object state = keepPlace && body != null ? body.SaveState() : null;
        string query = IsFindOpen ? findInput.Text : "";
        PreviewBody made;
        try
        {
            made = comparing != null ? await MakeCompare(comparing) : await MakeBody(ShownFile);
        }
        catch (Exception e)
        {
            if (!IsReadError(e)) throw;
            made = new MessageBody("This file couldn't be previewed. Open it in its app.");
        }
        if (id != loadId || closed)
        {
            made.Release();
            return;
        }
        ShowBody(made);
        if (state != null) made.RestoreState(state);
        else if (Window.GetWindow(this) != null && Window.GetWindow(this).IsActive) made.FocusView();
        UpdateTabs();
        UpdateDetails();
        UpdateVersionButton();
        if (query.Trim().Length > 0) RunFind();
    }

    static bool IsReadError(Exception e)
    {
        return e is IOException || e is UnauthorizedAccessException || e is XmlException || e is NotSupportedException || e is FileFormatException || e is InvalidOperationException || e is ArgumentException || e is COMExceptionAlias;
    }

    void ShowBody(PreviewBody next)
    {
        if (body != null)
        {
            body.Release();
            bodyHost.Children.Clear();
        }
        body = next;
        body.SelectionChanged += UpdateSelectionBar;
        bodyHost.Children.Add(body);
        selectionBar.Visibility = Visibility.Collapsed;
    }

    void UpdateSelectionBar()
    {
        selectionBar.Visibility = body != null && body.SelectedText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    PreviewKind KindOfShown { get { return PreviewTypes.Classify(path); } }

    bool HasSourceView
    {
        get
        {
            PreviewKind kind = KindOfShown;
            return kind == PreviewKind.Markdown || kind == PreviewKind.Web || kind == PreviewKind.Table;
        }
    }

    async Task<PreviewBody> MakeBody(string file)
    {
        if (!File.Exists(file)) return new MessageBody("This file has been moved or deleted.");
        PreviewKind kind = PreviewTypes.Classify(file);
        long length = new FileInfo(file).Length;

        if (kind == PreviewKind.Image)
        {
            if (length > TextLimit) return new MessageBody("This file is too large to preview here. Open it in its app.");
            return new ImageBody(file);
        }

        if (PreviewTypes.UsesPages(kind) || (kind == PreviewKind.Web && !showSource))
        {
            PageViewer pages = await MakePages(file, kind == PreviewKind.Web ? PreviewKind.Pages : kind, false);
            if (pages != null) return pages;
            if (kind == PreviewKind.Web) return await MakeTextBody(file, kind, true);

            string extension = Path.GetExtension(file).ToLowerInvariant();
            if (allowNative && ShellPreviewHost.FindHandler(extension).HasValue) return new NativeBody(file);
            if (extension == ".docx" && length <= TextLimit)
            {
                string words = await Task.Run(() => FilePreviewWindow.ReadWordText(file));
                return new TextBody(colors => DocumentBuilder.Code(words, ".txt", colors), "Words only");
            }
            return new MessageBody(extension == ".pdf"
                ? "This PDF couldn't be opened here. It may need a password. Open it in its app."
                : "This type of file can't be previewed here. Open it in its app.");
        }

        if (length > TextLimit) return new MessageBody("This file is too large to preview here. Open it in its app.");
        return await MakeTextBody(file, kind, showSource);
    }

    async Task<PreviewBody> MakeTextBody(string file, PreviewKind kind, bool source)
    {
        string text;
        try { text = await Task.Run(() => PreviewTypes.ReadText(file)); }
        catch (InvalidDataException) { return new MessageBody("This type of file can't be previewed here. Open it in its app."); }

        string extension = Path.GetExtension(file).ToLowerInvariant();
        if (kind == PreviewKind.Table && !source) return new TableBody(text, extension);

        int lines = 1;
        foreach (char c in text) if (c == '\n') lines++;
        string details = lines + " line" + (lines == 1 ? "" : "s");
        if (kind == PreviewKind.Markdown && !source) return new TextBody(colors => DocumentBuilder.Markdown(text, colors), details);
        return new TextBody(colors => DocumentBuilder.Code(text, extension, colors), details);
    }

    static async Task<PageViewer> MakePages(string file, PreviewKind kind, bool compact)
    {
        string pdf = await PreviewCache.GetPdf(file);
        if (pdf == null) return null;
        PdfPages pages = await PdfPages.Open(pdf);
        if (pages == null) return null;
        List<string> names = null;
        if (kind == PreviewKind.Sheets)
        {
            List<KeyValuePair<string, bool>> sheets = await Task.Run(() => ReadSheets(file));
            names = new List<string>();
            List<int> visible = new List<int>();
            for (int i = 0; i < sheets.Count; i++)
            {
                if (sheets[i].Value) continue;
                names.Add(sheets[i].Key);
                visible.Add(i);
            }
            if (sheets.Count == pages.Count) pages.Keep(visible);
        }
        return new PageViewer(pages, kind, names, compact);
    }

    // Each sheet's name, and whether it's hidden. A format that can't be read here gets numbered tabs instead.
    static List<KeyValuePair<string, bool>> ReadSheets(string file)
    {
        List<KeyValuePair<string, bool>> names = new List<KeyValuePair<string, bool>>();
        if (!Path.GetExtension(file).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)) return names;
        try
        {
            using (System.IO.Packaging.Package package = System.IO.Packaging.Package.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Uri uri = new Uri("/xl/workbook.xml", UriKind.Relative);
                if (!package.PartExists(uri)) return names;
                using (Stream stream = package.GetPart(uri).GetStream(FileMode.Open, FileAccess.Read))
                {
                    XmlReaderSettings settings = new XmlReaderSettings();
                    settings.DtdProcessing = DtdProcessing.Prohibit;
                    settings.XmlResolver = null;
                    using (XmlReader reader = XmlReader.Create(stream, settings))
                    {
                        while (reader.Read())
                        {
                            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "sheet") continue;
                            string state = reader.GetAttribute("state");
                            names.Add(new KeyValuePair<string, bool>(reader.GetAttribute("name") ?? "", state != null && state != "visible"));
                        }
                    }
                }
            }
        }
        catch (Exception e)
        {
            if (!IsReadError(e)) throw;
            names.Clear();
        }
        return names;
    }

    async Task<PreviewBody> MakeCompare(FileVersion older)
    {
        PreviewKind kind = PreviewTypes.Classify(path);
        string olderLabel = "v" + older.Number + " · " + older.Time.ToString("MMM d, HH:mm");
        if (PreviewTypes.UsesPages(kind))
        {
            PageViewer before = await MakePages(older.File, kind, true);
            PageViewer after = await MakePages(path, kind, true);
            if (before == null || after == null) return new MessageBody("These versions couldn't be compared here.");
            return new CompareBody(before, olderLabel, after, "Current");
        }
        if (kind == PreviewKind.Image)
        {
            return new MessageBody("Pictures can't be compared here. Choose a version to see it.");
        }

        string oldText = await Task.Run(() => PreviewTypes.ReadText(older.File));
        string newText = await Task.Run(() => PreviewTypes.ReadText(path));
        List<DiffLine> lines = await Task.Run(() => LineDiff.Compare(oldText, newText));
        if (lines == null) return new MessageBody("These versions are too long to compare here.");
        int added = 0, removed = 0;
        foreach (DiffLine line in lines)
        {
            if (line.Change > 0) added++;
            else if (line.Change < 0) removed++;
        }
        return new TextBody(colors => DocumentBuilder.Diff(lines, colors), "Compared with v" + older.Number + ": " + added + " added, " + removed + " removed");
    }

    void UpdateDetails()
    {
        List<string> parts = new List<string>();
        if (viewing != null) parts.Add("v" + viewing.Number + " · " + viewing.Time.ToString("MMM d, HH:mm"));
        parts.Add(PreviewTypes.Label(path));
        if (body != null && body.Details.Length > 0) parts.Add(body.Details);
        try
        {
            if (File.Exists(ShownFile)) parts.Add(PreviewTypes.Size(new FileInfo(ShownFile).Length));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        baseDetails = string.Join(" · ", parts);
        if (!noteTimer.IsEnabled) detailText.Text = baseDetails;
    }

    void ShowNote(string note)
    {
        detailText.Text = note;
        noteTimer.Stop();
        noteTimer.Start();
    }

    void UpdateTabs()
    {
        switchRow.Visibility = HasSourceView && comparing == null ? Visibility.Visible : Visibility.Collapsed;
        SetTab(previewTab, !showSource);
        SetTab(codeTab, showSource);
    }

    static void SetTab(Border tab, bool active)
    {
        tab.Background = active ? WidgetWindow.OverlayMid : Brushes.Transparent;
        ((TextBlock)tab.Child).Foreground = active ? WidgetWindow.Text : WidgetWindow.Muted;
    }

    internal void SetSource(bool source)
    {
        if (showSource == source) return;
        showSource = source;
        UpdateTabs();
        ShowBody(new LoadingBody());
        Load(false);
    }

    void UpdateVersionButton()
    {
        if (closed || path == null) return;
        List<FileVersion> versions = FileVersions.List(path);
        versionButton.Visibility = versions.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        if (versions.Count == 0) return;
        versionLabel.Text = comparing != null ? "Comparing v" + comparing.Number
            : viewing != null ? "v" + viewing.Number + " of " + versions[versions.Count - 1].Number
            : "v" + versions[versions.Count - 1].Number;
    }

    void ShowVersions()
    {
        versionRows.Children.Clear();
        List<FileVersion> versions = FileVersions.List(path);
        for (int i = versions.Count - 1; i >= 0; i--)
        {
            FileVersion version = versions[i];
            bool latest = i == versions.Count - 1;
            versionRows.Children.Add(BuildVersionRow(version, latest));
        }
        versionPopup.IsOpen = true;
    }

    UIElement BuildVersionRow(FileVersion version, bool latest)
    {
        TextBlock name = new TextBlock();
        name.Text = "v" + version.Number;
        name.FontSize = 12.5;
        name.FontWeight = FontWeights.SemiBold;
        name.Foreground = WidgetWindow.Text;
        name.Margin = new Thickness(0, 0, 9, 0);
        name.VerticalAlignment = VerticalAlignment.Center;
        TextBlock when = new TextBlock();
        when.Text = latest ? "Current" : version.Time.ToString("MMM d, HH:mm");
        when.FontSize = 11.5;
        when.Foreground = WidgetWindow.Muted;
        when.VerticalAlignment = VerticalAlignment.Center;

        DockPanel line = new DockPanel();
        if (!latest)
        {
            Border compare = PreviewChrome.Icon("", "Compare with current", delegate { versionPopup.IsOpen = false; Compare(version); });
            DockPanel.SetDock(compare, Dock.Right);
            line.Children.Add(compare);
        }
        DockPanel.SetDock(name, Dock.Left);
        line.Children.Add(name);
        line.Children.Add(when);

        Border row = new Border();
        row.Child = line;
        row.Padding = new Thickness(8, 4, 4, 4);
        row.MinHeight = 34;
        row.CornerRadius = new CornerRadius(7);
        row.Background = Brushes.Transparent;
        row.Cursor = Cursors.Hand;
        row.MouseEnter += delegate { row.Background = WidgetWindow.OverlayMid; };
        row.MouseLeave += delegate { row.Background = Brushes.Transparent; };
        ChatWindow.MakeButton(row, delegate
        {
            versionPopup.IsOpen = false;
            ShowVersion(latest ? null : version);
        });
        return row;
    }

    internal void ShowVersion(FileVersion version)
    {
        viewing = version;
        comparing = null;
        ShowBody(new LoadingBody());
        Load(false);
    }

    internal void Compare(FileVersion version)
    {
        comparing = version;
        viewing = null;
        ShowBody(new LoadingBody());
        Load(false);
    }

    void UpdateArrows()
    {
        List<string> files = FileList != null ? FileList() : null;
        int index = files == null ? -1 : files.FindIndex(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
        fileArrows.Visibility = files != null && files.Count > 1 && index >= 0 ? Visibility.Visible : Visibility.Collapsed;
        if (index >= 0 && files != null) positionText.Text = (index + 1) + " of " + files.Count;
    }

    internal void Step(int direction)
    {
        List<string> files = FileList != null ? FileList() : null;
        if (files == null || files.Count < 2) return;
        int index = files.FindIndex(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        Open(files[(index + direction + files.Count) % files.Count]);
    }

    internal void ShowFind()
    {
        if (body == null || !body.CanFind)
        {
            ShowNote("Search isn't available for this view.");
            return;
        }
        findBar.Visibility = Visibility.Visible;
        findInput.Focus();
        findInput.SelectAll();
    }

    internal void SetFindText(string text)
    {
        findBar.Visibility = Visibility.Visible;
        findInput.Text = text;
    }

    internal void CloseFind()
    {
        findTimer.Stop();
        findBar.Visibility = Visibility.Collapsed;
        findCount.Text = "";
        if (findInput.Text.Length > 0) findInput.Text = "";
        if (body != null) body.ClearFind();
    }

    async void RunFind()
    {
        findTimer.Stop();
        if (body == null || !IsFindOpen) return;
        PreviewBody searched = body;
        string query = findInput.Text;
        if (query.Trim().Length == 0)
        {
            searched.ClearFind();
            findCount.Text = "";
            return;
        }
        int count = await searched.Find(query, delegate(string note) { findCount.Text = note; });
        if (count < 0 || searched != body || query != findInput.Text) return;
        findCount.Text = count == 0 ? "No matches" : searched.FindPosition(count);
    }

    internal void StepFind(int direction)
    {
        if (body == null) return;
        string position = body.FindStep(direction);
        if (position.Length > 0) findCount.Text = position;
    }

    // Keys for the pane, whichever window holds it. True when the key was used.
    internal bool HandleKey(KeyEventArgs e)
    {
        bool control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (control && key == Key.F) { ShowFind(); return true; }
        if (!IsKeyboardFocusWithin) return false;

        if (alt && key == Key.Left) { Step(-1); return true; }
        if (alt && key == Key.Right) { Step(1); return true; }
        if (findInput.IsKeyboardFocused)
        {
            if (key == Key.Enter)
            {
                StepFind((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
                return true;
            }
            if (key == Key.Escape)
            {
                CloseFind();
                if (body != null) body.Focus();
                return true;
            }
            return false;
        }
        if (key == Key.Escape)
        {
            if (IsFindOpen) { CloseFind(); return true; }
            if (body != null && body.SelectedText.Length > 0) { body.ClearSelection(); return true; }
            if (CloseRequested != null) CloseRequested();
            return true;
        }
        if (control && key == Key.C && body is PageViewer && body.SelectedText.Length > 0)
        {
            SetClipboardText(body.SelectedText);
            return true;
        }
        return body != null && body.HandleKey(e);
    }

    void SetClipboardText(string text)
    {
        try
        {
            Clipboard.SetText(text);
            ShowNote("Copied");
        }
        catch (System.Runtime.InteropServices.ExternalException) { ShowNote("Couldn't copy. Try again."); }
    }

    void CopyFile()
    {
        try
        {
            DataObject data = new DataObject();
            StringCollection files = new StringCollection();
            files.Add(ShownFile);
            data.SetFileDropList(files);
            Clipboard.SetDataObject(data, true);
            ShowNote("File copied");
        }
        catch (System.Runtime.InteropServices.ExternalException) { ShowNote("Couldn't copy. Try again."); }
    }

    void SaveAs()
    {
        SaveFileDialog dialog = new SaveFileDialog();
        dialog.FileName = Path.GetFileName(path);
        string extension = Path.GetExtension(path);
        dialog.Filter = extension.Length > 0 ? PreviewTypes.Label(path) + "|*" + extension + "|All files|*.*" : "All files|*.*";
        dialog.AddExtension = true;
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            if (!string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(ShownFile), StringComparison.OrdinalIgnoreCase)) File.Copy(ShownFile, dialog.FileName, true);
            ShowNote("Saved");
        }
        catch (Exception e)
        {
            if (!(e is IOException || e is UnauthorizedAccessException)) throw;
            ShowNote("Couldn't save there.");
        }
    }

    async void Print()
    {
        if (body == null) return;
        PrintDialog dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return;
        ShowNote("Preparing to print…");
        try
        {
            DocumentPaginator paginator = await body.BuildPrint(new Size(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight));
            if (paginator == null)
            {
                ShowNote("This view can't be printed.");
                return;
            }
            dialog.PrintDocument(paginator, Path.GetFileName(path));
            ShowNote("Sent to the printer");
        }
        catch (Exception e)
        {
            if (!(e is IOException || e is InvalidOperationException || e is System.Printing.PrintSystemException || e is COMExceptionAlias)) throw;
            ShowNote("Couldn't print this file.");
        }
    }

    void OpenInApp()
    {
        try
        {
            ProcessStartInfo start = new ProcessStartInfo(path);
            start.UseShellExecute = true;
            Process.Start(start);
        }
        catch (Exception e)
        {
            if (!(e is System.ComponentModel.Win32Exception || e is InvalidOperationException)) throw;
        }
    }

    void ShowInFolder()
    {
        try { Process.Start("explorer.exe", "/select,\"" + path + "\""); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    internal void Shutdown()
    {
        closed = true;
        loadId++;
        refreshTimer.Stop();
        findTimer.Stop();
        noteTimer.Stop();
        versionPopup.IsOpen = false;
        if (watcher != null) watcher.Dispose();
        watcher = null;
        if (body != null) body.Release();
        body = null;
        bodyHost.Children.Clear();
        path = null;
    }
}

// The preview in a window of its own, for when the chat isn't full screen.
class FilePreviewWindow : Window
{
    internal readonly FilePreviewPane Pane = new FilePreviewPane(true);
    readonly Border card = new Border();

    internal FilePreviewWindow(string filePath, Window owner, Func<List<string>> files, Action<string, string> ask)
    {
        Title = Path.GetFileName(filePath);
        Owner = owner;
        Width = 760;
        Height = 660;
        MinWidth = 420;
        MinHeight = 320;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            UseAeroCaptionButtons = false,
        });
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        WindowStartupLocation = WindowStartupLocation.Manual;
        Resources.MergedDictionaries.Add((System.Windows.ResourceDictionary)System.Windows.Markup.XamlReader.Parse(ChatWindow.ChatXaml));

        card.BorderBrush = WidgetWindow.CardEdge;
        card.BorderThickness = new Thickness(1);
        card.Child = Pane;
        Content = card;

        Pane.FileList = files;
        Pane.AskRequested += ask;
        Pane.CloseRequested += Close;
        Pane.HeaderPressed += delegate { if (WindowState == WindowState.Normal) DragMove(); };
        Pane.FullScreenRequested += delegate { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; };
        StateChanged += delegate
        {
            bool full = WindowState == WindowState.Maximized;
            Pane.SetFullScreen(full);
            card.BorderThickness = full ? new Thickness(7) : new Thickness(1);
            card.BorderBrush = full ? Brushes.Transparent : (Brush)WidgetWindow.CardEdge;
        };

        ApplyAppearance();
        WidgetWindow.AppearanceChanged += ApplyAppearance;
        Closed += delegate
        {
            WidgetWindow.AppearanceChanged -= ApplyAppearance;
            Pane.Shutdown();
        };
        PreviewKeyDown += delegate(object s, KeyEventArgs e)
        {
            if (Pane.HandleKey(e)) e.Handled = true;
        };
        Loaded += delegate
        {
            Pane.Focus();
            Pane.Open(filePath);
        };
    }

    void ApplyAppearance()
    {
        card.Background = WidgetWindow.CardFill(WidgetWindow.IsLight);
        Resources["ChatThumb"] = new SolidColorBrush(WidgetWindow.Muted.Color);
        Pane.ReloadForTheme();
    }

    // Reads only the words of a Word document. Layout and pictures aren't shown here.
    internal static string ReadWordText(string file)
    {
        StringBuilder text = new StringBuilder();
        using (System.IO.Packaging.Package package = System.IO.Packaging.Package.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Uri documentUri = new Uri("/word/document.xml", UriKind.Relative);
            if (!package.PartExists(documentUri)) return "";
            using (Stream stream = package.GetPart(documentUri).GetStream(FileMode.Open, FileAccess.Read))
            {
                XmlReaderSettings settings = new XmlReaderSettings();
                settings.DtdProcessing = DtdProcessing.Prohibit;
                settings.XmlResolver = null;
                using (XmlReader reader = XmlReader.Create(stream, settings))
                {
                    while (reader.Read())
                    {
                        if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t") text.Append(reader.ReadString());
                        else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "tab") text.Append('\t');
                        else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "p") text.Append('\n');
                    }
                }
            }
        }
        return text.ToString();
    }
}
