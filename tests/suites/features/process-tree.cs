using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
// Tests for the KillTree fix: real descendants die, a process whose parent id merely matches survives.
static class HK
{
    static int fails, passes;
    static void Check(string what, bool ok, string info = "")
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + what + (info.Length > 0 ? " [" + info + "]" : ""));
        if (ok) passes++; else fails++;
    }

    static List<int> ChildrenOf(int pid)
    {
        return ProcessTree.Snapshot().Where(p => p.Value == pid).Select(p => p.Key).ToList();
    }

    // A process we may not open (such as conhost) still counts as alive while it exists.
    static bool Alive(int pid)
    {
        try { Process p = Process.GetProcessById(pid); try { return !p.HasExited; } catch (System.ComponentModel.Win32Exception) { return true; } }
        catch (ArgumentException) { return false; }
    }

    static string NameOf(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName; } catch (Exception) { return ""; }
    }

    // The spawner child, not the conhost.exe that Windows attaches to a console process.
    static int WaitChild(int pid)
    {
        for (int i = 0; i < 100; i++)
        {
            foreach (int c in ChildrenOf(pid)) if (NameOf(c) == "spawner") return c;
            Thread.Sleep(50);
        }
        return 0;
    }

    static void Main(string[] args)
    {
        string spawner = args[0];

        // Pure rule, with a made-up process table
        Dictionary<int, long> starts = new Dictionary<int, long> { { 100, 1000 }, { 200, 1100 }, { 300, 1200 }, { 400, 900 }, { 500, 0 }, { 600, 1150 }, { 700, 1050 } };
        List<KeyValuePair<int, int>> table = new List<KeyValuePair<int, int>>
        {
            new KeyValuePair<int, int>(100, 1),    // target
            new KeyValuePair<int, int>(200, 100),  // child
            new KeyValuePair<int, int>(300, 200),  // grandchild
            new KeyValuePair<int, int>(400, 100),  // orphan: parent id matches, started before the target
            new KeyValuePair<int, int>(500, 100),  // start time unreadable
            new KeyValuePair<int, int>(600, 400),  // child of the orphan
            new KeyValuePair<int, int>(700, 300),  // parent id is the grandchild, but started before it (reused id)
        };
        Func<int, long> startOf = id => starts.ContainsKey(id) ? starts[id] : 0;
        List<int> found = ProcessTree.Descendants(100, table, startOf).Select(p => p.Key).ToList();
        Check("rule: child and grandchild found, parents first", found.SequenceEqual(new[] { 200, 300 }), string.Join(",", found));
        Check("rule: orphan with a matching parent id but an earlier start is left alone", !found.Contains(400) && !found.Contains(600));
        Check("rule: unreadable start time is left alone", !found.Contains(500));
        Check("rule: a process older than its recorded parent is left alone at any depth", !found.Contains(700));
        Check("rule: unreadable target start finds nothing", ProcessTree.Descendants(100, table, id => id == 100 ? 0 : startOf(id)).Count == 0);
        Check("rule: a loop in the table doesn't hang", ProcessTree.Descendants(1, new List<KeyValuePair<int, int>> { new KeyValuePair<int, int>(1, 2), new KeyValuePair<int, int>(2, 1) }, id => 5).Count == 1);

        // Real processes: an unrelated sleeper started first, then a three-level tree
        Process solo = Process.Start(new ProcessStartInfo(spawner, "solo") { UseShellExecute = false, CreateNoWindow = true });
        Thread.Sleep(300);
        Process top = Process.Start(new ProcessStartInfo(spawner, "top") { UseShellExecute = false, CreateNoWindow = true });
        int child = WaitChild(top.Id);
        int leaf = child == 0 ? 0 : WaitChild(child);
        Check("real: tree started", child != 0 && leaf != 0, top.Id + ">" + child + ">" + leaf);
        Check("real: start times readable and ordered", ProcessTree.StartTime(solo.Id) < ProcessTree.StartTime(top.Id) && ProcessTree.StartTime(top.Id) <= ProcessTree.StartTime(child));

        // The solo sleeper is made to look like a child of the target, as a reused parent id would
        List<KeyValuePair<int, int>> real = ProcessTree.Snapshot().Select(p => p.Key == solo.Id ? new KeyValuePair<int, int>(p.Key, top.Id) : p).ToList();
        List<KeyValuePair<int, long>> realFound = ProcessTree.Descendants(top.Id, real, ProcessTree.StartTime);
        List<int> realIds = realFound.Select(p => p.Key).ToList();
        Check("real: the tree's child and leaf are found, child first", realIds.Contains(child) && realIds.Contains(leaf) && realIds.IndexOf(child) < realIds.IndexOf(leaf),
            string.Join(",", realIds.Select(id => id + ":" + NameOf(id))));
        Check("real: everything else found is the tree's own conhost", realIds.Where(id => id != child && id != leaf).All(id => NameOf(id) == "conhost"));
        List<int> conhosts = realIds.Where(id => NameOf(id) == "conhost").ToList();
        Check("real: the older process with a matching parent id is not", !realFound.Any(p => p.Key == solo.Id));
        Check("reuse guard: a wrong start time means no kill", !ProcessTree.Terminate(child, ProcessTree.StartTime(child) - 1) && Alive(child));

        // KillTree on the real target
        ChatClient.KillTree(top);
        Thread.Sleep(500);
        Check("KillTree: target, child and grandchild are gone", !Alive(top.Id) && !Alive(child) && !Alive(leaf));
        Check("KillTree: the tree's conhost processes are gone too", conhosts.All(id => !Alive(id)), string.Join(",", conhosts));
        Check("KillTree: the unrelated older process is still running", Alive(solo.Id));
        bool threw = false;
        try { ChatClient.KillTree(top); } catch (Exception) { threw = true; }
        Check("KillTree: calling it on an exited process is harmless", !threw);
        solo.Kill();

        // KillTree on a single process with no children
        Process lone = Process.Start(new ProcessStartInfo(spawner, "solo") { UseShellExecute = false, CreateNoWindow = true });
        Thread.Sleep(200);
        ChatClient.KillTree(lone);
        Check("KillTree: a lone process is stopped", lone.HasExited);

        Console.WriteLine("passes=" + passes + " fails=" + fails);
    }
}
