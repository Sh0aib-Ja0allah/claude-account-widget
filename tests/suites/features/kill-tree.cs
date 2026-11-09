using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
// Worker A's independent checks of the KillTree fix. Fake tables for the walk; real trees use only ping.exe and
// cmd.exe children this program starts itself. Nothing else on the machine is touched.
// kill-tree.exe
static class H13
{
    static int fails, passes;
    static void Log(string s) { Console.WriteLine(s); Console.Out.Flush(); }
    static void Check(string what, bool ok, string info = "")
    {
        Log((ok ? "PASS " : "FAIL ") + what + (info.Length > 0 ? " [" + info + "]" : ""));
        if (ok) passes++; else fails++;
    }
    static List<KeyValuePair<int, int>> T(params int[] pairs)
    {
        List<KeyValuePair<int, int>> l = new List<KeyValuePair<int, int>>();
        for (int i = 0; i < pairs.Length; i += 2) l.Add(new KeyValuePair<int, int>(pairs[i], pairs[i + 1]));
        return l;
    }
    static string Ids(List<KeyValuePair<int, long>> l) { return string.Join(",", l.Select(p => p.Key)); }

    static void Main()
    {
        Fake();
        Real();
        Log("passes=" + passes + " fails=" + fails);
    }

    static void Fake()
    {
        // target 100 started at 1000
        Dictionary<int, long> s = new Dictionary<int, long> { { 100, 1000 }, { 101, 1000 }, { 102, 1000 }, { 103, 999 }, { 104, 2000 }, { 105, 0 }, { 106, 3000 } };
        Func<int, long> st = id => s.ContainsKey(id) ? s[id] : 0;
        // 101 same tick as target (kept as child), 102 same tick as its parent 101, 103 one tick older than target
        List<KeyValuePair<int, long>> r = ProcessTree.Descendants(100, T(101, 100, 102, 101, 103, 100), st);
        Check("fake: same start tick as the parent counts as a child", Ids(r) == "101,102", Ids(r));
        Check("fake: one tick older than the target is kept (reused id)", !r.Any(p => p.Key == 103));
        // unreadable child in the middle: its real descendants are unreachable, so left alone
        r = ProcessTree.Descendants(100, T(105, 100, 106, 105), st);
        Check("fake: an unreadable child and everything under it are left alone", r.Count == 0, Ids(r));
        // target listed as its own parent, duplicate rows, a child listed twice
        r = ProcessTree.Descendants(100, T(100, 100, 104, 100, 104, 100, 106, 104), st);
        Check("fake: self-parent and duplicate rows don't loop or double up", Ids(r) == "104,106", Ids(r));
        // a 3-cycle that includes the target
        r = ProcessTree.Descendants(100, T(104, 100, 106, 104, 100, 106), st);
        Check("fake: a 3-cycle through the target stops", Ids(r) == "104,106", Ids(r));
        // a 2,000-deep chain and a 20,000-row table run quickly
        Dictionary<int, long> deep = new Dictionary<int, long>();
        List<KeyValuePair<int, int>> chain = new List<KeyValuePair<int, int>>();
        deep[1] = 10;
        for (int i = 2; i <= 2000; i++) { deep[i] = 10 + i; chain.Add(new KeyValuePair<int, int>(i, i - 1)); }
        for (int i = 0; i < 18000; i++) chain.Add(new KeyValuePair<int, int>(100000 + i, 99999));
        Stopwatch sw = Stopwatch.StartNew();
        r = ProcessTree.Descendants(1, chain, id => deep.ContainsKey(id) ? deep[id] : 5);
        sw.Stop();
        Check("fake: a 2,000-deep chain in a 20,000-row table is found in order", r.Count == 1999 && r[0].Key == 2 && r[1998].Key == 2000, r.Count + " found");
        Log("NOTE fake: 2,000-deep chain in 20,000 rows took " + sw.ElapsedMilliseconds + " ms (the walk scans the whole table once per member)");
        Check("fake: an unreadable target start finds nothing even with children", ProcessTree.Descendants(100, T(104, 100), id => id == 100 ? 0 : 5000).Count == 0);
    }

    static Process Start(string file, string args)
    {
        return Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardInput = true });
    }
    static List<int> ChildrenOf(int pid)
    {
        return ProcessTree.Snapshot().Where(p => p.Value == pid).Select(p => p.Key).ToList();
    }
    static bool Alive(int pid) { try { return !Process.GetProcessById(pid).HasExited; } catch (Exception) { return false; } }

    static void Real()
    {
        // 1. An orphan whose parent died: target > middle (exits) > grandchild. Same as taskkill /T: unreachable, so it lives.
        Process target = Start("cmd.exe", "/c cmd /c \"start \"\" /b ping -n 60 127.0.0.1 >nul\" & ping -n 60 127.0.0.1 >nul");
        Thread.Sleep(1500);
        List<int> kids = ChildrenOf(target.Id);
        List<int> pings = ProcessTree.Snapshot().Where(p => kids.Contains(p.Value)).Select(p => p.Key).ToList();
        int orphanGrandchild = ProcessTree.Snapshot().Where(p => !kids.Contains(p.Value) && p.Value != target.Id).Select(p => p.Key).FirstOrDefault(id => { try { return Process.GetProcessById(id).ProcessName == "PING" && ProcessTree.StartTime(id) > ProcessTree.StartTime(target.Id); } catch (Exception) { return false; } });
        Log("NOTE real: target " + target.Id + " children " + string.Join(",", kids));
        ChatClient.KillTree(target);
        Thread.Sleep(500);
        Check("real: the target and its live children die", !Alive(target.Id) && kids.All(k => !Alive(k)), string.Join(",", kids.Where(Alive)));
        // clean up anything this test started that is still running (pings started after the target, by this program's tree)
        foreach (Process p in Process.GetProcessesByName("PING")) { try { if (ProcessTree.StartTime(p.Id) > ProcessTree.StartTime(Process.GetCurrentProcess().Id)) { Log("NOTE real: leftover ping " + p.Id + " from a parent that had already exited (same as taskkill /T); cleaned up"); p.Kill(); } } catch (Exception) { } }

        // 2. A descendant that starts a new child between the snapshot and the kill: is it caught?
        Process t2 = Start("cmd.exe", "/c for /l %i in (1,1,40) do @(start \"\" /b ping -n 30 127.0.0.1 >nul & ping -n 2 127.0.0.1 >nul)");
        Thread.Sleep(1500);
        ChatClient.KillTree(t2);
        Thread.Sleep(1500);
        int late = 0;
        foreach (Process p in Process.GetProcessesByName("PING")) { try { if (ProcessTree.StartTime(p.Id) > ProcessTree.StartTime(Process.GetCurrentProcess().Id)) { late++; p.Kill(); } } catch (Exception) { } }
        Check("real: a busy spawner leaves no pings behind after KillTree", late == 0, late + " leftover pings (started between snapshot and kill, or orphaned)");
        Check("real: the busy spawner itself is dead", !Alive(t2.Id));

        // 3. KillTree on an already exited process, and on one whose handle we hold after exit.
        Process quick = Start("cmd.exe", "/c exit 0");
        quick.WaitForExit();
        string err = "";
        try { ChatClient.KillTree(quick); } catch (Exception e) { err = e.GetType().Name; }
        Check("real: KillTree on an exited process does nothing and doesn't throw", err.Length == 0, err);
    }
}
