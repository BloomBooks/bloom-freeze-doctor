using System.Diagnostics;

namespace BloomFreezeDoctor;

/// <summary>What the watcher knows about the Bloom it is watching, for the report's header.</summary>
public sealed record BloomTargetFacts
{
    /// <summary>The process id.</summary>
    public required int ProcessId { get; init; }

    /// <summary>Full path to Bloom.exe.</summary>
    public required string ExePath { get; init; }

    /// <summary>Release channel, derived from <see cref="ExePath"/>.</summary>
    public required string Channel { get; init; }

    /// <summary>The command line, which says whether this is an automation or headless run.</summary>
    public required string CommandLine { get; init; }

    /// <summary>When the process started, used to identify its log file.</summary>
    public required DateTime StartTime { get; init; }

    /// <summary>
    /// True when this Bloom must never produce a filed report: a developer build, or an automation
    /// run. We still gather (and write to disk), because that is how we test the gatherer.
    /// </summary>
    public bool NeverFile =>
        BloomChannel.IsDeveloperChannel(Channel) || BloomChannel.IsHeadlessOrAutomationRun(CommandLine);
}

/// <summary>Raised when the detector decides this Bloom is worth reporting.</summary>
public sealed class ReportWantedEventArgs : EventArgs
{
    /// <summary>The Bloom in question.</summary>
    public required BloomTargetFacts Target { get; init; }

    /// <summary>What the detector concluded, and why.</summary>
    public required DetectorVerdict Verdict { get; init; }

    /// <summary>
    /// True if a report may actually be filed. False for developer and automation runs, which are
    /// gathered to disk and no further — the guard that keeps our own daily work off the tracker.
    /// </summary>
    public required bool MayFile { get; init; }
}

/// <summary>
/// Watches one Bloom process: takes a reading every second, feeds it to a <see cref="FreezeDetector"/>,
/// and raises <see cref="ReportWanted"/> when there is something to report.
///
/// Runs on a background timer, never on a UI thread. That is a requirement rather than a preference:
/// the Doctor has a window of its own (decision D1), and a Doctor whose window goes white while it
/// diagnoses a freeze would be its own worst advertisement.
/// </summary>
public sealed class BloomTargetWatcher : IDisposable
{
    private readonly ITargetProbe _probe;
    private readonly FreezeDetector _detector;
    private readonly Stopwatch _monotonic = Stopwatch.StartNew();
    private readonly TimeSpan _cadence;
    private Timer? _timer;

    /// <summary>Guards against a slow reading overlapping the next tick.</summary>
    private int _observing;

    /// <summary>Creates a watcher for a Bloom already identified.</summary>
    public BloomTargetWatcher(
        BloomTargetFacts target,
        ITargetProbe probe,
        DetectorThresholds? thresholds = null,
        TimeSpan? cadence = null
    )
    {
        Target = target;
        _probe = probe;
        _detector = new FreezeDetector(thresholds);
        _cadence = cadence ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>The Bloom being watched.</summary>
    public BloomTargetFacts Target { get; }

    /// <summary>The detector's current opinion.</summary>
    public TargetState State => _detector.State;

    /// <summary>True once this target has been seen under a debugger and can never be reported.</summary>
    public bool IsPoisonedByDebugger => _detector.IsPoisonedByDebugger;

    /// <summary>Raised on the watcher's background thread when a report is wanted.</summary>
    public event EventHandler<ReportWantedEventArgs>? ReportWanted;

    /// <summary>Raised whenever an observation is taken, for the status window to render.</summary>
    public event EventHandler<DetectorVerdict>? Observed;

    /// <summary>Begins watching.</summary>
    public void Start()
    {
        _timer ??= new Timer(_ => Tick(), null, TimeSpan.Zero, _cadence);
    }

    /// <summary>
    /// Takes one reading and acts on it. Public so a test can drive the watcher deterministically
    /// instead of waiting on a timer.
    /// </summary>
    public void Tick()
    {
        // A reading that overruns its slot must not stack up behind itself.
        if (Interlocked.Exchange(ref _observing, 1) == 1)
            return;
        try
        {
            var observation = _probe.Observe(_monotonic.Elapsed);
            var verdict = _detector.Observe(observation);
            Observed?.Invoke(this, verdict);

            if (!verdict.ShouldReport)
                return;

            // Two independent reasons never to file, checked here rather than left to the gatherer so
            // that the decision is in one place: this target has been under a debugger at some point,
            // or it is a developer/automation run.
            var mayFile = !_detector.IsPoisonedByDebugger && !Target.NeverFile;
            ReportWanted?.Invoke(
                this,
                new ReportWantedEventArgs
                {
                    Target = Target,
                    Verdict = verdict,
                    MayFile = mayFile,
                }
            );
        }
        catch (Exception)
        {
            // A watcher that throws stops watching, and then we learn nothing at all. Swallowing here
            // is deliberate, and is the reason ITargetProbe promises not to throw: this is the net
            // under that promise, not a substitute for it.
        }
        finally
        {
            Interlocked.Exchange(ref _observing, 0);
        }
    }

    /// <summary>Stops watching and releases the timer.</summary>
    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>
    /// Gathers the facts about a running Bloom process. Returns null if the process went away while we
    /// were asking, which is entirely possible and not an error.
    /// </summary>
    public static BloomTargetFacts? DescribeProcess(Process process, string commandLine)
    {
        try
        {
            var exe = process.MainModule?.FileName ?? "";
            return new BloomTargetFacts
            {
                ProcessId = process.Id,
                ExePath = exe,
                Channel = BloomChannel.DeriveFromExePath(exe),
                CommandLine = commandLine,
                StartTime = process.StartTime,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }
}
