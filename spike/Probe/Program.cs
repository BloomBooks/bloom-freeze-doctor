using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Runtime;

namespace Probe;

/// <summary>
/// Phase 0 spike probe. Answers the plan's open technical questions against a real process and
/// prints how it got each answer, so the results can be pasted into the plan as findings rather
/// than assumptions.
///
/// Usage: Probe &lt;pid&gt; [--waitchain] [--dump] [--attach] [--logmap]
/// Nothing that perturbs the target runs unless its flag is given; --attach in particular
/// SUSPENDS the target (see plan section 4.1) and must never be aimed at a Bloom someone is using.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || !int.TryParse(args[0], out var pid))
        {
            Console.WriteLine("usage: Probe <pid> [--waitchain] [--dump] [--attach] [--logmap]");
            Console.WriteLine("  (no flags = read-only probes that cannot disturb the target)");
            return 1;
        }

        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            Console.WriteLine($"no process with pid {pid}");
            return 1;
        }

        var exe = SafeMainModule(process);
        Console.WriteLine($"=== target: pid {pid} {process.ProcessName} ===");
        Console.WriteLine($"exe:     {exe}");
        Console.WriteLine($"started: {SafeStartTime(process)}");
        Console.WriteLine($"arch:    {Architecture(process)}");
        Console.WriteLine($"channel: {DeriveChannel(exe)}  (reports are only filed for real channels)");
        Console.WriteLine($"cmdline: {CommandLineOf(pid)}");
        Console.WriteLine();

        ReportResponsiveness(process);
        ReportWindows(pid);
        ReportThreadCpu(process);
        ReportDebugger(process);
        ReportSuspiciousModules(process);
        ReportCdp(process);

        if (args.Contains("--logmap"))
            ReportLogMapping();
        if (args.Contains("--waitchain"))
            ReportWaitChains(process);
        if (args.Contains("--dump"))
            ReportDumpAndReadItBack(pid);
        if (args.Contains("--attach"))
        {
            // --hold N keeps the suspension open for N seconds so an experimenter can kill THIS
            // process mid-attach and find out whether the target ever resumes. That is the safety
            // question in plan section 4.1, and the answer decides whether the fallback path needs
            // a resume guarantee.
            var hold = 0;
            var holdArg = args.FirstOrDefault(a => a.StartsWith("--hold=", StringComparison.Ordinal));
            if (holdArg != null)
                int.TryParse(holdArg.Substring("--hold=".Length), out hold);
            ReportLiveAttach(pid, hold);
        }

        // The zero-risk alternative: attach without suspending. Nothing we can die holding, so it
        // can never leave the target stopped. The question is whether the stacks it reads are
        // usable, since the target keeps running underneath us.
        if (args.Contains("--attach-nosuspend"))
            ReportNoSuspendAttach(pid);

        return 0;
    }

    private static void ReportNoSuspendAttach(int pid)
    {
        Console.WriteLine("--- ClrMD live attach WITHOUT suspend (cannot strand the target) ---");
        var sw = Stopwatch.StartNew();
        try
        {
            using var target = DataTarget.AttachToProcess(pid, suspend: false);
            var version = target.ClrVersions.FirstOrDefault();
            if (version == null)
            {
                Console.WriteLine("  no CLR found");
                return;
            }
            using var runtime = version.CreateRuntime();
            var withStacks = runtime.Threads.Count(t => t.EnumerateStackTrace().Any());
            sw.Stop();
            Console.WriteLine(
                $"  walked {runtime.Threads.Length} managed thread(s), {withStacks} with stacks, in {sw.ElapsedMilliseconds} ms"
            );
            PrintLikelyUiThread(runtime);
        }
        catch (Exception e)
        {
            Console.WriteLine($"  FAILED after {sw.ElapsedMilliseconds} ms: {e.GetType().Name}: {e.Message}");
        }
        Console.WriteLine();
    }

    #region responsiveness — plan section 3.1

    /// <summary>
    /// The two Tier A liveness signals, side by side, because the interesting result is when they
    /// DISAGREE: a UI thread blocked in an STA managed wait still answers WM_NULL while the UI is
    /// dead to the user.
    /// </summary>
    private static void ReportResponsiveness(Process process)
    {
        Console.WriteLine("--- responsiveness ---");
        var hwnd = process.MainWindowHandle;
        if (hwnd == IntPtr.Zero)
        {
            Console.WriteLine("MainWindowHandle: none (candidate for state 3, or a headless run)");
            return;
        }

        Console.WriteLine($"MainWindowHandle:  0x{hwnd.ToInt64():X}");
        Console.WriteLine($"IsHungAppWindow:   {IsHungAppWindow(hwnd)}");
        Console.WriteLine($"Responding (.NET): {SafeResponding(process)}");

        var sw = Stopwatch.StartNew();
        var answered = SendMessageTimeout(
            hwnd,
            WM_NULL,
            IntPtr.Zero,
            IntPtr.Zero,
            SMTO_ABORTIFHUNG,
            2000,
            out _
        );
        sw.Stop();
        Console.WriteLine(
            $"SendMessageTimeout(WM_NULL, 2s): {(answered != IntPtr.Zero ? "answered" : "TIMED OUT")} after {sw.ElapsedMilliseconds} ms"
        );
        Console.WriteLine($"IsWindowEnabled:   {IsWindowEnabled(hwnd)} (false suggests a modal dialog is up)");
        Console.WriteLine();
    }

    #endregion

    #region windows — plan section 4.2

    private static void ReportWindows(int pid)
    {
        Console.WriteLine("--- top-level windows (hidden modal dialogs show up here) ---");
        var count = 0;
        EnumWindows(
            (hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out var owner);
                if (owner != pid)
                    return true;
                var title = new StringBuilder(256);
                GetWindowText(hwnd, title, title.Capacity);
                var cls = new StringBuilder(256);
                GetClassName(hwnd, cls, cls.Capacity);
                Console.WriteLine(
                    $"  0x{hwnd.ToInt64():X8} visible={IsWindowVisible(hwnd),-5} enabled={IsWindowEnabled(hwnd),-5} "
                        + $"class={cls} title=\"{title}\""
                );
                count++;
                return true;
            },
            IntPtr.Zero
        );
        if (count == 0)
            Console.WriteLine("  (none — this is what state 3 looks like)");
        Console.WriteLine();
    }

    #endregion

    #region thread CPU — plan section 4.2 (spin vs deadlock)

    private static void ReportThreadCpu(Process process)
    {
        Console.WriteLine("--- per-thread CPU over 3s (a spin loop shows here; a deadlock does not) ---");
        var first = SnapshotThreadTimes(process);
        Thread.Sleep(3000);
        process.Refresh();
        var second = SnapshotThreadTimes(process);

        var moved = 0;
        foreach (var (id, after) in second.OrderByDescending(kv => kv.Value))
        {
            if (!first.TryGetValue(id, out var before))
                continue;
            var delta = after - before;
            if (delta <= TimeSpan.FromMilliseconds(50))
                continue;
            Console.WriteLine($"  thread {id,-8} burned {delta.TotalMilliseconds,8:F0} ms of CPU");
            moved++;
        }
        Console.WriteLine(
            moved == 0
                ? "  (no thread burned measurable CPU — consistent with a deadlock or an idle process)"
                : $"  ({moved} thread(s) active)"
        );
        Console.WriteLine();
    }

    private static Dictionary<int, TimeSpan> SnapshotThreadTimes(Process process)
    {
        var result = new Dictionary<int, TimeSpan>();
        foreach (ProcessThread t in process.Threads)
        {
            try
            {
                result[t.Id] = t.TotalProcessorTime;
            }
            catch (Exception)
            {
                // A thread can exit between enumeration and the read; it simply has no delta.
            }
        }
        return result;
    }

    #endregion

    #region debugger — plan section 3.5 (must be sampled while alive)

    private static void ReportDebugger(Process process)
    {
        Console.WriteLine("--- debugger ---");
        var present = false;
        var ok = CheckRemoteDebuggerPresent(process.Handle, ref present);
        Console.WriteLine(
            ok
                ? $"CheckRemoteDebuggerPresent: {present}"
                : $"CheckRemoteDebuggerPresent failed, win32 {Marshal.GetLastWin32Error()}"
        );
        Console.WriteLine();
    }

    #endregion

    #region modules — plan section 4.2 (injected AV / shell hooks)

    private static void ReportSuspiciousModules(Process process)
    {
        Console.WriteLine("--- loaded modules not from Windows, the app folder, or a known vendor ---");
        try
        {
            var appDir = Path.GetDirectoryName(SafeMainModule(process)) ?? "?";
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var interesting = 0;
            foreach (ProcessModule m in process.Modules)
            {
                var file = m.FileName ?? "";
                if (
                    file.StartsWith(windows, StringComparison.OrdinalIgnoreCase)
                    || file.StartsWith(appDir, StringComparison.OrdinalIgnoreCase)
                    || file.Contains(@"\Program Files\dotnet\", StringComparison.OrdinalIgnoreCase)
                    || file.Contains(@"\Microsoft\EdgeWebView\", StringComparison.OrdinalIgnoreCase)
                    || file.Contains(@"\Microsoft\Edge", StringComparison.OrdinalIgnoreCase)
                )
                    continue;
                Console.WriteLine($"  {file}");
                interesting++;
            }
            Console.WriteLine($"  ({process.Modules.Count} modules total, {interesting} unexplained)");
        }
        catch (Exception e)
        {
            Console.WriteLine($"  could not enumerate modules: {e.GetType().Name}: {e.Message}");
        }
        Console.WriteLine();
    }

    #endregion

    #region CDP — plan section 4.3

    /// <summary>
    /// Finds the WebView2 debugging port the way Tier A must: by reading it out of the
    /// msedgewebview2.exe children's command lines, since Bloom's own HTTP port is owned by
    /// http.sys (pid 4) and invisible in the TCP table.
    /// </summary>
    private static void ReportCdp(Process process)
    {
        Console.WriteLine("--- WebView2 / CDP discovery ---");
        var found = new HashSet<int>();
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, CommandLine FROM Win32_Process WHERE Name = 'msedgewebview2.exe'"
            );
            foreach (var o in searcher.Get())
            {
                var commandLine = o["CommandLine"] as string ?? "";
                const string marker = "--remote-debugging-port=";
                var at = commandLine.IndexOf(marker, StringComparison.Ordinal);
                if (at < 0)
                    continue;
                var rest = commandLine.Substring(at + marker.Length);
                var digits = new string(rest.TakeWhile(char.IsDigit).ToArray());
                if (int.TryParse(digits, out var port))
                    found.Add(port);
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"  WMI query failed: {e.GetType().Name}: {e.Message}");
        }

        if (found.Count == 0)
            Console.WriteLine("  no --remote-debugging-port found on any msedgewebview2.exe");
        foreach (var port in found)
            Console.WriteLine($"  candidate CDP port {port} -> {ProbeCdp(port)}");
        Console.WriteLine();
    }

    private static string ProbeCdp(int port)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var version = http.GetStringAsync($"http://127.0.0.1:{port}/json/version").Result;
            var targets = http.GetStringAsync($"http://127.0.0.1:{port}/json/list").Result;
            var pages = targets.Split("\"type\": \"page\"").Length - 1;
            var browser = version.Contains("Browser")
                ? version.Split('"').FirstOrDefault(s => s.StartsWith("Edg", StringComparison.Ordinal)) ?? "?"
                : "?";
            return $"ANSWERED ({browser}, {pages} page target(s))";
        }
        catch (Exception e)
        {
            return $"no answer ({e.GetType().Name})";
        }
    }

    #endregion

    #region log mapping — plan section 4.4

    /// <summary>
    /// Works out which Log*.txt belongs to which running Bloom WITHOUT handle enumeration, by
    /// matching the "App Launched with [exe]" line each log opens with against each process's
    /// start time and exe path. If this holds up it replaces the NtQuerySystemInformation approach
    /// the plan reached for.
    /// </summary>
    private static void ReportLogMapping()
    {
        Console.WriteLine("--- mapping Bloom logs to pids by their 'App Launched' line ---");
        var dir = Path.Combine(Path.GetTempPath(), "SIL", "Bloom");
        if (!Directory.Exists(dir))
        {
            Console.WriteLine($"  no log directory at {dir}");
            return;
        }

        var blooms = Process
            .GetProcessesByName("Bloom")
            .Select(p => (p.Id, Exe: SafeMainModule(p), Started: SafeStartTime(p)))
            .ToList();
        Console.WriteLine($"  {blooms.Count} Bloom process(es) running");

        var logs = Directory
            .GetFiles(dir, "Log*.txt")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTime)
            .Take(12)
            .ToList();

        foreach (var log in logs)
        {
            var launch = ReadLaunchLine(log.FullName);
            var match = blooms.FirstOrDefault(b =>
                launch.Exe != null
                && b.Exe != null
                && SameApp(b.Exe, launch.Exe)
                && launch.TimeOfDay != null
                && Math.Abs((b.Started.TimeOfDay - launch.TimeOfDay.Value).TotalSeconds) < 90
            );
            var verdict = match.Id != 0 ? $"pid {match.Id}" : "no running process";
            Console.WriteLine(
                $"  {log.Name,-24} modified {log.LastWriteTime:MM-dd HH:mm}  launched {launch.TimeOfDay,-10} -> {verdict}"
            );
            if (launch.Exe != null)
                Console.WriteLine($"      from: {launch.Exe}");
        }
        Console.WriteLine();
    }

    /// <summary>The two logs may name Bloom.exe vs Bloom.dll for the same app; compare folders.</summary>
    private static bool SameApp(string exePath, string loggedPath)
    {
        var a = Path.GetDirectoryName(exePath) ?? exePath;
        var b = Path.GetDirectoryName(loggedPath) ?? loggedPath;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static (TimeSpan? TimeOfDay, string? Exe) ReadLaunchLine(string path)
    {
        try
        {
            // Share everything: the owning Bloom holds this file open for writing.
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete
            );
            using var reader = new StreamReader(stream);
            for (var i = 0; i < 40; i++)
            {
                var line = reader.ReadLine();
                if (line == null)
                    break;
                const string marker = "App Launched with [";
                var at = line.IndexOf(marker, StringComparison.Ordinal);
                if (at < 0)
                    continue;
                var exe = line.Substring(at + marker.Length).TrimEnd(']');
                var stamp = line.Split('\t')[0].Trim();
                return (
                    DateTime.TryParse(stamp, out var parsed) ? parsed.TimeOfDay : null,
                    exe
                );
            }
        }
        catch (Exception) { }
        return (null, null);
    }

    #endregion

    #region wait chains — plan section 4.2

    private static void ReportWaitChains(Process process)
    {
        Console.WriteLine("--- wait chains (expect little for managed locks; see plan 4.2) ---");
        var session = OpenThreadWaitChainSession(0, IntPtr.Zero);
        if (session == IntPtr.Zero)
        {
            Console.WriteLine($"  OpenThreadWaitChainSession failed, win32 {Marshal.GetLastWin32Error()}");
            return;
        }
        try
        {
            foreach (ProcessThread t in process.Threads)
            {
                var nodes = new WAITCHAIN_NODE_INFO[WCT_MAX_NODE_COUNT];
                var count = WCT_MAX_NODE_COUNT;
                var cycle = 0;
                if (!GetThreadWaitChain(session, IntPtr.Zero, 0, t.Id, ref count, nodes, out cycle))
                    continue;
                if (count <= 1)
                    continue; // just the thread itself: nothing to say
                Console.WriteLine($"  thread {t.Id}{(cycle != 0 ? "  *** DEADLOCK CYCLE ***" : "")}");
                for (var i = 0; i < count; i++)
                {
                    var n = nodes[i];
                    if (n.ObjectType == WCT_OBJECT_TYPE.Thread)
                        Console.WriteLine(
                            $"      [{i}] thread {n.ThreadId} in pid {n.ProcessId}, status {n.ObjectStatus}"
                        );
                    else
                        Console.WriteLine($"      [{i}] {n.ObjectType}, status {n.ObjectStatus}");
                }
            }
        }
        finally
        {
            CloseThreadWaitChainSession(session);
        }
        Console.WriteLine();
    }

    #endregion

    #region dump — plan section 4.1 (the primary path)

    /// <summary>
    /// The load-bearing question: does the runtime's own dump (over the diagnostics IPC pipe)
    /// produce something small that ClrMD can still walk managed stacks from? If yes, section 4.1
    /// needs only one pipeline and no suspending live attach.
    /// </summary>
    private static void ReportDumpAndReadItBack(int pid)
    {
        Console.WriteLine("--- DiagnosticsClient.WriteDump, then read it back with ClrMD ---");
        var path = Path.Combine(Path.GetTempPath(), $"freezedoctor-spike-{pid}.dmp");
        try
        {
            File.Delete(path);
        }
        catch (Exception) { }

        var sw = Stopwatch.StartNew();
        try
        {
            var client = new DiagnosticsClient(pid);
            client.WriteDump(DumpType.Normal, path, logDumpGeneration: false);
            sw.Stop();
        }
        catch (Exception e)
        {
            Console.WriteLine($"  WriteDump FAILED after {sw.ElapsedMilliseconds} ms: {e.GetType().Name}: {e.Message}");
            Console.WriteLine("  (if the pipe is unreachable, section 4.1's fallback is what runs)");
            return;
        }

        var size = new FileInfo(path).Length;
        Console.WriteLine($"  wrote {size / 1024.0 / 1024.0:F1} MB in {sw.ElapsedMilliseconds} ms -> {path}");

        try
        {
            using var target = DataTarget.LoadDump(path);
            var clrVersion = target.ClrVersions.FirstOrDefault();
            if (clrVersion == null)
            {
                Console.WriteLine("  ClrMD found NO CLR in the dump — managed stacks unavailable");
                return;
            }
            using var runtime = clrVersion.CreateRuntime();
            var withFrames = 0;
            var deepest = 0;
            foreach (var thread in runtime.Threads)
            {
                var frames = thread.EnumerateStackTrace().Take(64).ToList();
                if (frames.Count == 0)
                    continue;
                withFrames++;
                deepest = Math.Max(deepest, frames.Count);
            }
            Console.WriteLine(
                $"  ClrMD read the dump: {runtime.Threads.Length} managed thread(s), "
                    + $"{withFrames} with walkable stacks, deepest {deepest} frames"
            );
            PrintLikelyUiThread(runtime);
        }
        catch (Exception e)
        {
            Console.WriteLine($"  ClrMD could not read the dump: {e.GetType().Name}: {e.Message}");
        }
        Console.WriteLine();
    }

    /// <summary>
    /// Prints the stack we would actually put at the top of a report: the main thread's.
    /// This is the artifact a human triaging a freeze reads first.
    /// </summary>
    private static void PrintLikelyUiThread(ClrRuntime runtime)
    {
        var main =
            runtime.Threads.FirstOrDefault(t =>
                t.EnumerateStackTrace().Any(f => f.Method?.Name == "Main")
            ) ?? runtime.Threads.FirstOrDefault(t => t.EnumerateStackTrace().Any());
        if (main == null)
            return;
        Console.WriteLine($"  --- likely UI thread (os id {main.OSThreadId}) top frames ---");
        foreach (var frame in main.EnumerateStackTrace().Take(18))
            Console.WriteLine($"      {Describe(frame)}");
    }

    private static string Describe(ClrStackFrame frame)
    {
        if (frame.Method == null)
            return frame.FrameName ?? "(native)";
        var type = frame.Method.Type?.Name ?? "?";
        return $"{type}.{frame.Method.Name}";
    }

    #endregion

    #region live attach — plan section 4.1 (the fallback, and its safety question)

    /// <summary>
    /// The fallback path, and the one with teeth: attaching with suspend:true stops the target's
    /// threads, and the OS does NOT resume them if we die holding the attach. Run this ONLY
    /// against the stub.
    /// </summary>
    private static void ReportLiveAttach(int pid, int holdSeconds)
    {
        Console.WriteLine("--- ClrMD live attach WITH SUSPEND (target is stopped while we read) ---");
        Console.WriteLine("    !!! never aim this at a Bloom someone is using !!!");
        var sw = Stopwatch.StartNew();
        try
        {
            using var target = DataTarget.AttachToProcess(pid, suspend: true);
            var version = target.ClrVersions.FirstOrDefault();
            if (version == null)
            {
                Console.WriteLine("  no CLR found in the live process");
                return;
            }
            using var runtime = version.CreateRuntime();
            var threadsWithStacks = runtime.Threads.Count(t => t.EnumerateStackTrace().Any());
            sw.Stop();
            Console.WriteLine(
                $"  attached and walked {runtime.Threads.Length} managed thread(s) "
                    + $"({threadsWithStacks} with stacks) in {sw.ElapsedMilliseconds} ms of suspension"
            );
            PrintLikelyUiThread(runtime);

            if (holdSeconds > 0)
            {
                Console.WriteLine(
                    $"  HOLDING the suspension for {holdSeconds}s — kill this process now to test"
                );
                Console.WriteLine($"  (this probe is pid {Environment.ProcessId})");
                Console.Out.Flush();
                Thread.Sleep(TimeSpan.FromSeconds(holdSeconds));
                Console.WriteLine("  releasing normally");
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"  attach FAILED after {sw.ElapsedMilliseconds} ms: {e.GetType().Name}: {e.Message}");
        }
        Console.WriteLine();
    }

    #endregion

    #region channel — plan section 3.3

    /// <summary>
    /// Works out Bloom's release channel from the path we can see from outside, mirroring
    /// Bloom's own ApplicationUpdateSupport.ChannelName. A "Developer/*" answer means the Doctor
    /// gathers but never files (plan §3.3), and that is the defence that catches a `pnpm go` run
    /// whether or not a debugger happens to be attached.
    ///
    /// One deliberate difference from Bloom's version: Bloom asks about its entry assembly, so it
    /// tests for a path ending in Bloom.dll. From outside we see the PROCESS, whose main module is
    /// Bloom.exe, so the developer-build test must not require ".dll" or every dev build would be
    /// misread as Release — the most dangerous direction to get this wrong.
    /// </summary>
    internal static string DeriveChannel(string exePath)
    {
        var path = exePath.Replace('\\', '/');
        if (path.Contains("/output/Debug/", StringComparison.OrdinalIgnoreCase))
            return "Developer/Debug";
        if (path.Contains("/output/Release/", StringComparison.OrdinalIgnoreCase))
            return "Developer/Release";
        var match = System.Text.RegularExpressions.Regex.Match(
            path,
            @"/Bloom([^/]*)/current/",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
        );
        if (match.Success)
        {
            var channel = match.Groups[1].Value;
            if (!string.IsNullOrEmpty(channel))
                return channel.Replace("-arm64", "");
        }
        return "Release";
    }

    /// <summary>
    /// The target's command line, which tells us whether this is an automation or headless run that
    /// must not be reported (plan §3.3), and where the WebView2 debug port is.
    /// </summary>
    private static string CommandLineOf(int pid)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}"
            );
            foreach (var o in searcher.Get())
                return (o["CommandLine"] as string) ?? "?";
        }
        catch (Exception e)
        {
            return $"? ({e.GetType().Name})";
        }
        return "?";
    }

    #endregion

    #region helpers and interop

    private static string SafeMainModule(Process p)
    {
        try
        {
            return p.MainModule?.FileName ?? "?";
        }
        catch (Exception)
        {
            return "? (access denied)";
        }
    }

    private static DateTime SafeStartTime(Process p)
    {
        try
        {
            return p.StartTime;
        }
        catch (Exception)
        {
            return DateTime.MinValue;
        }
    }

    private static bool SafeResponding(Process p)
    {
        try
        {
            return p.Responding;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Architecture(Process p)
    {
        try
        {
            if (!IsWow64Process2(p.Handle, out var processMachine, out _))
                return "? (IsWow64Process2 failed)";
            // IMAGE_FILE_MACHINE_UNKNOWN means "not emulated": the process is native to the host.
            return processMachine == 0 ? $"native ({RuntimeInformation.OSArchitecture})" : $"emulated (machine 0x{processMachine:X})";
        }
        catch (Exception e)
        {
            return $"? ({e.GetType().Name})";
        }
    }

    private const uint WM_NULL = 0x0000;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const int WCT_MAX_NODE_COUNT = 16;

    private enum WCT_OBJECT_TYPE
    {
        CriticalSection = 1,
        SendMessage,
        Mutex,
        Alpc,
        Com,
        ThreadWait,
        ProcessWait,
        Thread,
        ComActivation,
        Unknown,
        Max,
    }

    private enum WCT_OBJECT_STATUS
    {
        NoAccess = 1,
        Running,
        Blocked,
        PidOnly,
        PidOnlyRpcss,
        Owned,
        NotOwned,
        Abandoned,
        Unknown,
        Error,
        Max,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WAITCHAIN_NODE_INFO
    {
        public WCT_OBJECT_TYPE ObjectType;
        public WCT_OBJECT_STATUS ObjectStatus;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string ObjectName;

        public long Timeout;
        public bool Alertable;

        // The union's thread branch overlays ObjectName; we read it via these fields instead,
        // which is valid for WCT_OBJECT_TYPE.Thread nodes.
        public int ProcessId;
        public int ThreadId;
        public int WaitTime;
        public int ContextSwitches;
    }

    [DllImport("user32.dll")]
    private static extern bool IsHungAppWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeoutMs,
        out IntPtr result
    );

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CheckRemoteDebuggerPresent(IntPtr process, ref bool present);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process2(
        IntPtr process,
        out ushort processMachine,
        out ushort nativeMachine
    );

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr OpenThreadWaitChainSession(uint flags, IntPtr callback);

    [DllImport("advapi32.dll")]
    private static extern void CloseThreadWaitChainSession(IntPtr session);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetThreadWaitChain(
        IntPtr session,
        IntPtr context,
        uint flags,
        int threadId,
        ref int nodeCount,
        [In, Out] WAITCHAIN_NODE_INFO[] nodeInfoArray,
        out int isCycle
    );

    #endregion
}
