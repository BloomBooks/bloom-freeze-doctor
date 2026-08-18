namespace BloomFreezeDoctor;

/// <summary>
/// What the Doctor believes about one watched Bloom.
/// </summary>
public enum TargetState
{
    /// <summary>Responding, with a visible window. Nothing to do.</summary>
    Healthy,

    /// <summary>Not responding, but not for long enough to report yet.</summary>
    Suspect,

    /// <summary>Not responding for long enough that we report it (plan §3.2).</summary>
    Frozen,

    /// <summary>
    /// Alive with no visible window for long enough to count as the zombie of plan §3.6. Note
    /// "visible": a healthy Bloom keeps an invisible window of its own (its splash screen is hidden
    /// rather than closed), so counting any window would mean never detecting this at all.
    /// </summary>
    Zombie,

    /// <summary>The process is gone.</summary>
    Exited,
}

/// <summary>
/// One reading of a watched process, taken by whatever can see the real world. Everything the
/// detector needs is in here, so the detector itself can be tested without a process.
/// </summary>
public readonly record struct TargetObservation
{
    /// <summary>
    /// Monotonic time since watching began. Deliberately NOT a wall clock: the machine can sleep,
    /// and a resumed laptop must not look like a six-hour freeze (plan §3.5).
    /// </summary>
    public required TimeSpan Uptime { get; init; }

    /// <summary>False once the process has gone.</summary>
    public required bool IsAlive { get; init; }

    /// <summary>
    /// Whether the window answered a message probe. The spike settled which probe: use
    /// SendMessageTimeout, because IsHungAppWindow needs about five seconds to make up its mind.
    /// Both are worthless against a UI thread blocked in an STA managed wait, which is what
    /// <see cref="HeartbeatIsStale"/> is for.
    /// </summary>
    public required bool WindowResponds { get; init; }

    /// <summary>Whether the process still has a VISIBLE top-level window. See <see cref="TargetState.Zombie"/>.</summary>
    public required bool HasVisibleWindow { get; init; }

    /// <summary>
    /// Tier B only: Bloom's UI-thread heartbeat has stopped advancing. This is the only signal that
    /// catches a freeze in an STA managed wait, where the window still answers messages. It is never
    /// trusted alone, because WM_TIMER is the lowest-priority message and can starve on a busy but
    /// live UI (plan §3.1).
    /// </summary>
    public bool HeartbeatIsStale { get; init; }

    /// <summary>
    /// Tier B only: no forward progress in Bloom's breadcrumbs or in-flight work. The corroborating
    /// signal that lets a stale heartbeat be believed.
    /// </summary>
    public bool NoForwardProgress { get; init; }

    /// <summary>
    /// A debugger is attached right now. The detector makes this sticky, because a dead process
    /// cannot be asked and a developer who stops the debugger must never generate a report.
    /// </summary>
    public bool DebuggerAttached { get; init; }

    /// <summary>
    /// Tier B only: Bloom says it is deliberately busy (publishing, uploading, making a PDF). Raises
    /// the patience threshold rather than suppressing detection.
    /// </summary>
    public bool LongOperationInProgress { get; init; }
}

/// <summary>
/// The thresholds from decision D3, in one place so that dogfooding can move them without a code
/// change.
/// </summary>
public sealed record DetectorThresholds
{
    /// <summary>How long unresponsive before we start paying attention.</summary>
    public TimeSpan Suspect { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How long unresponsive before we report.</summary>
    public TimeSpan Report { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long unresponsive before we report, when Bloom says it is busy on purpose.</summary>
    public TimeSpan ReportDuringLongOperation { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long alive-with-no-visible-window before we call it a zombie.</summary>
    public TimeSpan Zombie { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A gap between observations larger than this means something stopped the world — the machine
    /// slept, or the Doctor itself was starved — so elapsed "unresponsive" time is not trustworthy
    /// and gets restarted rather than accumulated.
    /// </summary>
    public TimeSpan ImplausibleGap { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>Why the detector is asking for a report; becomes the shape of the card.</summary>
public enum ReportReason
{
    None,

    /// <summary>UI unresponsive past the threshold — plan state 1.</summary>
    Frozen,

    /// <summary>Was frozen and started responding again. Still worth reporting; often better evidence.</summary>
    RecoveredFromFreeze,

    /// <summary>Froze, then the process died or was killed. One card, not two.</summary>
    DiedWhileFrozen,

    /// <summary>Alive with no visible window — plan state 3.</summary>
    Zombie,

    /// <summary>
    /// Exited leaving no proof of an orderly shutdown. Phase 3 only: in Phase 1 an exit needs
    /// separate corroboration, which the detector does not have and does not pretend to.
    /// </summary>
    ExitedWithoutProof,
}

/// <summary>The detector's answer to one observation.</summary>
public readonly record struct DetectorVerdict
{
    /// <summary>What we now believe about the target.</summary>
    public required TargetState State { get; init; }

    /// <summary>Set when this observation is the moment to gather and file. Fires at most once per reason.</summary>
    public required ReportReason Report { get; init; }

    /// <summary>Human-readable justification, for the card and for the Doctor's own log.</summary>
    public required string Explanation { get; init; }

    /// <summary>True when a report is being asked for.</summary>
    public bool ShouldReport => Report != ReportReason.None;
}

/// <summary>
/// Turns a stream of observations of one Bloom into "report now, for this reason" decisions.
///
/// Everything here came out of the Phase 0 spike, so the reasons for the odd-looking rules are
/// recorded in the comments rather than left to be rediscovered:
/// a debugged process is poison forever, not just while the debugger is attached; a stale heartbeat
/// needs a second opinion; and a big gap between observations means the machine slept, not that
/// Bloom hung.
/// </summary>
public sealed class FreezeDetector
{
    private readonly DetectorThresholds _thresholds;

    /// <summary>When the target was last seen to be alive and answering. Null until the first look.</summary>
    private TimeSpan? _lastRespondedAt;

    /// <summary>When the target was last seen to have a visible window.</summary>
    private TimeSpan? _lastHadWindowAt;

    /// <summary>Uptime of the previous observation, to notice gaps that mean the world stopped.</summary>
    private TimeSpan? _previousUptime;

    /// <summary>
    /// Once true, never false again. A developer stopping the debugger is a hard kill that leaves no
    /// proof of shutdown, so without this the most common thing a developer does all day would look
    /// exactly like the crash we are hunting.
    /// </summary>
    private bool _everDebugged;

    private readonly HashSet<ReportReason> _alreadyReported = new();

    /// <summary>Creates a detector, optionally with thresholds other than decision D3's defaults.</summary>
    public FreezeDetector(DetectorThresholds? thresholds = null)
    {
        _thresholds = thresholds ?? new DetectorThresholds();
    }

    /// <summary>The state as of the last observation.</summary>
    public TargetState State { get; private set; } = TargetState.Healthy;

    /// <summary>
    /// True if this target has ever been seen under a debugger, and therefore must never be
    /// reported. Exposed so the gatherer can say why it declined, rather than silently doing nothing.
    /// </summary>
    public bool IsPoisonedByDebugger => _everDebugged;

    /// <summary>
    /// Feeds one observation in and gets back what to do about it. Call this on a steady cadence
    /// (about once a second); the detector works entirely from the timestamps it is given, so a
    /// missed beat costs nothing.
    /// </summary>
    public DetectorVerdict Observe(TargetObservation now)
    {
        if (now.DebuggerAttached)
            _everDebugged = true;

        // A gap far larger than our cadence means the world stopped: the machine slept, or something
        // starved the Doctor. Elapsed unresponsive time measured across such a gap is meaningless, so
        // treat the target as freshly seen rather than accumulating a freeze it never had.
        var slept =
            _previousUptime.HasValue
            && now.Uptime - _previousUptime.Value > _thresholds.ImplausibleGap;
        if (slept)
        {
            _lastRespondedAt = now.Uptime;
            _lastHadWindowAt = now.Uptime;
        }
        _previousUptime = now.Uptime;

        if (!now.IsAlive)
            return ObserveDeadProcess(now);

        if (now.WindowResponds && !BelievesHeartbeatIsStale(now))
            _lastRespondedAt = now.Uptime;
        if (now.HasVisibleWindow)
            _lastHadWindowAt = now.Uptime;

        // First look: nothing to measure from yet.
        _lastRespondedAt ??= now.Uptime;
        _lastHadWindowAt ??= now.Uptime;

        var unresponsiveFor = now.Uptime - _lastRespondedAt.Value;
        var windowlessFor = now.Uptime - _lastHadWindowAt.Value;

        // Zombie is checked first, because a process with no window cannot meaningfully be called
        // unresponsive: there is nothing left to send a message to.
        if (!now.HasVisibleWindow && windowlessFor >= _thresholds.Zombie)
            return Settle(
                TargetState.Zombie,
                ReportReason.Zombie,
                $"alive with no visible window for {Describe(windowlessFor)}"
            );

        var reportAfter = now.LongOperationInProgress
            ? _thresholds.ReportDuringLongOperation
            : _thresholds.Report;

        if (unresponsiveFor >= reportAfter)
        {
            var why = BelievesHeartbeatIsStale(now)
                ? $"UI-thread heartbeat stale for {Describe(unresponsiveFor)} with no forward progress"
                : $"window has not answered for {Describe(unresponsiveFor)}";
            if (now.LongOperationInProgress)
                why += ", despite Bloom reporting a long operation";
            return Settle(TargetState.Frozen, ReportReason.Frozen, why);
        }

        if (unresponsiveFor >= _thresholds.Suspect)
            return Settle(
                TargetState.Suspect,
                ReportReason.None,
                $"unresponsive for {Describe(unresponsiveFor)}; watching"
            );

        // Responding again after we had decided it was frozen. Report it: a freeze the user waited
        // out is at least as informative as one they killed, and we caught this one live.
        if (State == TargetState.Frozen)
            return Settle(
                TargetState.Healthy,
                ReportReason.RecoveredFromFreeze,
                "started responding again after being reported frozen"
            );

        return Settle(TargetState.Healthy, ReportReason.None, "responding");
    }

    private DetectorVerdict ObserveDeadProcess(TargetObservation now)
    {
        // Died while we already thought it was in trouble: that is one story, not two, and the
        // freeze is the interesting half.
        if (State is TargetState.Frozen or TargetState.Suspect)
            return Settle(
                TargetState.Exited,
                ReportReason.DiedWhileFrozen,
                $"exited while {State.ToString().ToLowerInvariant()}"
            );

        // Otherwise this is plan §3.4/§3.5 territory, and the detector is deliberately not the judge:
        // whether a bare exit is reportable depends on evidence it does not have (a clean-exit proof,
        // Event Log entries, WER files). The watcher asks the exit classifier about it.
        return Settle(TargetState.Exited, ReportReason.None, "exited while apparently healthy");
    }

    /// <summary>
    /// A stale heartbeat is only believed when something else agrees, because WM_TIMER is the
    /// lowest-priority message and a busy-but-live UI can starve it (plan §3.1).
    /// </summary>
    private static bool BelievesHeartbeatIsStale(TargetObservation now) =>
        now.HeartbeatIsStale && (now.NoForwardProgress || !now.WindowResponds);

    /// <summary>
    /// Records the new state and suppresses a repeat of a reason we have already reported, so one
    /// freeze produces one card however long it lasts.
    /// </summary>
    private DetectorVerdict Settle(TargetState state, ReportReason reason, string explanation)
    {
        State = state;
        if (reason != ReportReason.None && !_alreadyReported.Add(reason))
            reason = ReportReason.None;
        return new DetectorVerdict
        {
            State = state,
            Report = reason,
            Explanation = explanation,
        };
    }

    private static string Describe(TimeSpan span) =>
        span.TotalSeconds < 90
            ? $"{span.TotalSeconds:F0}s"
            : $"{span.TotalMinutes:F1} minutes";
}
