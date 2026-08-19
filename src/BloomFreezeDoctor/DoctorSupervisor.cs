using System.Diagnostics;
using BloomFreezeDoctor.Gathering;
using BloomFreezeDoctor.Outbox;

namespace BloomFreezeDoctor;

/// <summary>What the window needs to render, published by the supervisor as things change.</summary>
public sealed record DoctorStatus
{
    /// <summary>One line per watched Bloom, in the card's own vocabulary: Running, Frozen, and so on.</summary>
    public required IReadOnlyList<string> BloomLines { get; init; }

    /// <summary>A line about the outbox when it is not empty, or null.</summary>
    public string? OutboxLine { get; init; }

    /// <summary>The most recent thing that happened, for the bottom of the window.</summary>
    public string? LastEvent { get; init; }
}

/// <summary>
/// The Doctor's brain: finds Blooms, watches each one, and when a watcher asks for a report, gathers it,
/// queues it, and tries to file it.
///
/// Everything here runs off the UI thread. That is a hard requirement rather than a preference: the
/// Doctor has a visible window (decision D1), and a Freeze Doctor whose own window goes white while it
/// diagnoses a freeze would be its own worst advertisement.
/// </summary>
public sealed class DoctorSupervisor : IDisposable
{
    /// <summary>How often to look for Blooms that have started or gone away.</summary>
    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(5);

    /// <summary>How often to try the outbox again while we are running and it is not empty.</summary>
    private static readonly TimeSpan DrainInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long to stay alive after the last Bloom has gone, if reports are still waiting to be sent.
    /// The Doctor is never *pinned* by this (see plan §3.6) — it is a courtesy window in case the network
    /// comes back, not a dependency.
    /// </summary>
    private static readonly TimeSpan LingerForOutbox = TimeSpan.FromMinutes(10);

    private readonly ReportOutbox _outbox;
    private readonly string _project;
    private readonly string _targetProcessName;
    private readonly bool _forceFiling;
    private readonly Dictionary<int, BloomTargetWatcher> _watchers = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _stopping = new();

    // Explicitly System.Threading timers, not System.Windows.Forms ones. The distinction is the whole
    // point: these must tick on the thread pool, because a Doctor that did its work on the UI thread
    // would freeze its own window while diagnosing a freeze.
    private System.Threading.Timer? _discovery;
    private System.Threading.Timer? _drain;
    private DateTimeOffset? _lastBloomSeenAt;
    private string? _lastEvent;

    /// <summary>
    /// Creates the supervisor.
    /// </summary>
    /// <param name="project">Tracker project to file into — `AUT` while testing, `BL` in earnest.</param>
    /// <param name="targetProcessName">
    /// Process name to watch, "Bloom" in production. Overridable so the freeze stub can stand in for
    /// Bloom during testing, which is the only way to exercise this without breaking a real Bloom.
    /// </param>
    /// <param name="forceFiling">
    /// Files reports even from developer builds. For deliberate end-to-end tests only; without it a
    /// developer machine gathers to disk and never files, which is what keeps our own work off the
    /// tracker.
    /// </param>
    public DoctorSupervisor(
        string project = "BL",
        string targetProcessName = "Bloom",
        bool forceFiling = false,
        ReportOutbox? outbox = null
    )
    {
        _project = project;
        _targetProcessName = targetProcessName;
        _forceFiling = forceFiling;
        _outbox = outbox ?? new ReportOutbox();
    }

    /// <summary>Raised whenever the status changes, so the window can redraw. Fires on a background thread.</summary>
    public event EventHandler<DoctorStatus>? StatusChanged;

    /// <summary>Raised when a report has been filed, so the window can say so and offer a restart.</summary>
    public event EventHandler<string>? ReportFiled;

    /// <summary>Raised when the Doctor has nothing left to do and should exit.</summary>
    public event EventHandler? NothingLeftToDo;

    /// <summary>The queue of reports waiting to be sent.</summary>
    public ReportOutbox Outbox => _outbox;

    /// <summary>Starts watching. Drains the outbox first, which is the moment that matters most.</summary>
    public void Start()
    {
        // Drain on startup, because the most likely next event after a freeze is the user restarting
        // Bloom — which starts us — so this is the reliable route by which yesterday's report gets out.
        _ = Task.Run(() => DrainAsync(_stopping.Token));

        _discovery = new System.Threading.Timer(
            _ => Discover(),
            null,
            TimeSpan.Zero,
            DiscoveryInterval
        );
        _drain = new System.Threading.Timer(
            _ => _ = DrainAsync(_stopping.Token),
            null,
            DrainInterval,
            DrainInterval
        );
    }

    /// <summary>
    /// Adopts a specific process, as Bloom asks us to when it launches us. Also used by `--report-now`.
    /// </summary>
    public void Adopt(int processId)
    {
        var facts = GatherContextBuilder.DescribeRunningProcess(processId);
        if (facts == null)
            return;
        AdoptFacts(facts);
    }

    /// <summary>
    /// Gathers and files a report for a process right now, whatever state it is in. This is the CTRL-key
    /// "Report now" of the card, and it is also how support gets a snapshot of a Bloom that is merely
    /// slow rather than frozen.
    /// </summary>
    public async Task<string?> ReportNowAsync(int processId, CancellationToken cancellation)
    {
        var facts = GatherContextBuilder.DescribeRunningProcess(processId);
        if (facts == null)
            return null;
        var verdict = new DetectorVerdict
        {
            State = TargetState.Healthy,
            // Deliberately NOT ReportReason.Frozen: this Bloom may be perfectly healthy, and a card
            // titled "UI frozen" about a healthy Bloom would send someone hunting a freeze that never
            // happened.
            Report = ReportReason.RequestedByPerson,
            Explanation =
                "a person asked for this report deliberately (the Report now button, or --report-now); "
                + "Bloom was not necessarily frozen",
        };
        return await GatherFileAndRecordAsync(facts, verdict, mayFile: true, cancellation)
            .ConfigureAwait(false);
    }

    /// <summary>Looks for Blooms we are not yet watching, and forgets ones that have gone.</summary>
    private void Discover()
    {
        try
        {
            var running = Process.GetProcessesByName(_targetProcessName);
            foreach (var process in running)
            {
                var facts = GatherContextBuilder.DescribeRunningProcess(process.Id);
                if (facts != null)
                    AdoptFacts(facts);
            }

            lock (_lock)
            {
                if (_watchers.Count > 0)
                    _lastBloomSeenAt = DateTimeOffset.UtcNow;

                // Drop watchers whose process has exited. The watcher itself reports the exit first, so
                // by the time we get here its story has been told.
                foreach (var id in _watchers.Keys.ToList())
                {
                    if (running.Any(p => p.Id == id))
                        continue;
                    _watchers[id].Dispose();
                    _watchers.Remove(id);
                }
            }

            PublishStatus();
            ConsiderExiting();
        }
        catch (Exception)
        {
            // Discovery failing must never stop the Doctor; the next tick will try again.
        }
    }

    private void AdoptFacts(BloomTargetFacts facts)
    {
        lock (_lock)
        {
            if (_watchers.ContainsKey(facts.ProcessId))
                return;

            // A headless or automation run legitimately has no window, so watching it would only produce
            // false zombie reports (plan §3.3).
            if (BloomChannel.IsHeadlessOrAutomationRun(facts.CommandLine))
                return;

            Process process;
            try
            {
                process = Process.GetProcessById(facts.ProcessId);
            }
            catch (Exception)
            {
                return;
            }

            var watcher = new BloomTargetWatcher(facts, new WindowsTargetProbe(process));
            watcher.ReportWanted += OnReportWanted;
            watcher.Observed += (_, _) => PublishStatus();
            _watchers[facts.ProcessId] = watcher;
            watcher.Start();
            Note($"watching Bloom {facts.ProcessId} ({facts.Channel})");
        }
    }

    /// <summary>
    /// A watcher has decided something is wrong. Gather, queue and try to send — on a worker thread, and
    /// without letting one failure take down the Doctor.
    /// </summary>
    private void OnReportWanted(object? sender, ReportWantedEventArgs e)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                Note($"gathering evidence about Bloom {e.Target.ProcessId}: {e.Verdict.Explanation}");
                var issue = await GatherFileAndRecordAsync(
                        e.Target,
                        e.Verdict,
                        e.MayFile || _forceFiling,
                        _stopping.Token
                    )
                    .ConfigureAwait(false);
                if (issue != null)
                    ReportFiled?.Invoke(this, issue);
            }
            catch (Exception ex)
            {
                Note($"gathering failed: {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    private async Task<string?> GatherFileAndRecordAsync(
        BloomTargetFacts facts,
        DetectorVerdict verdict,
        bool mayFile,
        CancellationToken cancellation
    )
    {
        var alive = IsAlive(facts.ProcessId);
        var artifacts = Path.Combine(Path.GetTempPath(), "BloomFreezeDoctor", $"gather-{facts.ProcessId}-{Guid.NewGuid():N}");
        var context = GatherContextBuilder.Build(facts, verdict, alive, artifacts);

        var report = await new EvidenceGatherer()
            .GatherAsync(context, mayFile, cancellation)
            .ConfigureAwait(false);

        var bundle = _outbox.Enqueue(report, _project, facts.Channel, verdict.Report.ToString());
        Note(
            report.MayFile
                ? $"report queued ({report.Summary})"
                : "report gathered to disk only (developer or automation run, or a debugged process)"
        );
        TryDeleteDirectory(artifacts);
        PublishStatus();

        if (!report.MayFile)
            return null;

        await DrainAsync(cancellation).ConfigureAwait(false);
        return _outbox
            .List()
            .FirstOrDefault(b => b.Directory == bundle.Directory)
            ?.Metadata.IssueId;
    }

    private async Task DrainAsync(CancellationToken cancellation)
    {
        try
        {
            if (_outbox.Pending().Count == 0)
                return;
            var filed = await _outbox.DrainAsync(new YouTrackSubmitter(), cancellation)
                .ConfigureAwait(false);
            if (filed > 0)
                Note($"filed {filed} report(s)");
            PublishStatus();
            ConsiderExiting();
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Note($"could not send reports: {e.GetType().Name}");
        }
    }

    /// <summary>
    /// Decides whether there is anything left worth staying alive for: a Bloom to watch, or a report
    /// waiting that might yet get out. Deliberately does NOT wait indefinitely on the outbox — a zombie
    /// Bloom or a permanently offline machine must not pin the Doctor forever (plan §3.6).
    /// </summary>
    private void ConsiderExiting()
    {
        lock (_lock)
        {
            if (_watchers.Count > 0)
                return;
        }
        if (_lastBloomSeenAt == null)
            return; // we have not seen a Bloom yet; wait for one rather than exiting immediately

        var waited = DateTimeOffset.UtcNow - _lastBloomSeenAt.Value;
        var pending = _outbox.Pending().Count;
        if (pending > 0 && waited < LingerForOutbox)
            return;

        NothingLeftToDo?.Invoke(this, EventArgs.Empty);
    }

    private void PublishStatus()
    {
        List<string> lines;
        lock (_lock)
        {
            lines = _watchers
                .Values.Select(w =>
                    $"Bloom {w.Target.ProcessId} ({w.Target.Channel}): {Describe(w.State)}"
                )
                .ToList();
        }
        if (lines.Count == 0)
            lines.Add("Bloom Status: Not running");

        var pending = _outbox.Pending().Count;
        StatusChanged?.Invoke(
            this,
            new DoctorStatus
            {
                BloomLines = lines,
                OutboxLine = pending switch
                {
                    0 => null,
                    1 => "1 report waiting to send",
                    _ => $"{pending} reports waiting to send",
                },
                LastEvent = _lastEvent,
            }
        );
    }

    /// <summary>The card's own vocabulary, so the window says what the card promised it would say.</summary>
    private static string Describe(TargetState state) =>
        state switch
        {
            TargetState.Healthy => "Running",
            TargetState.Suspect => "Running (not answering just now)",
            TargetState.Frozen => "Frozen",
            TargetState.Zombie => "Stuck in the background with no window",
            TargetState.Exited => "Not running",
            _ => state.ToString(),
        };

    private void Note(string message)
    {
        _lastEvent = $"{DateTime.Now:HH:mm:ss}  {message}";
        Debug.WriteLine("[FreezeDoctor] " + message);
        PublishStatus();
    }

    private static bool IsAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception)
        {
            // Anything still in there has already been moved into the bundle; a leftover temp folder is
            // untidy, not harmful.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stopping.Cancel();
        _discovery?.Dispose();
        _drain?.Dispose();
        lock (_lock)
        {
            foreach (var watcher in _watchers.Values)
                watcher.Dispose();
            _watchers.Clear();
        }
        _stopping.Dispose();
    }
}
