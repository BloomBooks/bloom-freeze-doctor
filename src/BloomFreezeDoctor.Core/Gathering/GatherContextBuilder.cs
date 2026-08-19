using System.Diagnostics;

namespace BloomFreezeDoctor.Gathering;

/// <summary>
/// Works out the things a gather needs to know about its target that take looking up: which log file is
/// this Bloom's, and where its WebView2 is listening.
///
/// Kept in one place because both the Doctor and its test harnesses need it, and because getting either
/// answer wrong is quiet rather than loud — the report simply carries the wrong log or talks to another
/// Bloom's browser, and nobody notices until a card misleads someone.
/// </summary>
public static class GatherContextBuilder
{
    /// <summary>
    /// Builds the context for gathering evidence about one Bloom. Never throws: every lookup here is a
    /// nice-to-have, and a report with a missing log beats no report.
    /// </summary>
    public static GatherContext Build(
        BloomTargetFacts target,
        DetectorVerdict verdict,
        bool processWasAlive,
        string artifactDirectory,
        string? logDirectory = null
    )
    {
        return new GatherContext
        {
            Target = target,
            Verdict = verdict,
            ProcessWasAlive = processWasAlive,
            ArtifactDirectory = artifactDirectory,
            BloomLogPath = FindLog(target, logDirectory),
            CdpPort = FindCdpPort(target),
        };
    }

    /// <summary>
    /// Identifies this Bloom's log by matching each candidate's "App Launched with" line against the
    /// process's own exe folder and start time. See <see cref="BloomLogLocator"/> for why the obvious
    /// alternative is wrong.
    /// </summary>
    private static string? FindLog(BloomTargetFacts target, string? logDirectory)
    {
        try
        {
            var candidates = BloomLogLocator.ReadCandidates(logDirectory);
            var chosen = BloomLogLocator.ChooseFor(candidates, target.ExePath, target.StartTime);
            return chosen?.Path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Finds the WebView2 debugging port for this Bloom, attributed to *this* process rather than
    /// whichever WebView2 happened to answer first — which matters as soon as a machine runs two Blooms,
    /// routine on a developer's machine.
    ///
    /// Falls back to the port 6.3 hardcodes, but only after the command lines, and only if this Bloom
    /// looks like a 6.3: on a machine where something else owns 9222 we would otherwise interrogate a
    /// stranger's browser and put the answers on a Bloom card.
    /// </summary>
    private static int? FindCdpPort(BloomTargetFacts target)
    {
        try
        {
            var fromCommandLine = WebView2Processes.FindDebuggingPort(target.ProcessId);
            if (fromCommandLine.HasValue)
                return fromCommandLine;

            // No WebView2 child advertised a port. If this Bloom has WebView2 children at all, it is
            // not running the 6.3 arrangement, so guessing 9222 would be guessing about someone else.
            var children = WebView2Processes.FindChildrenOf(target.ProcessId);
            return children.Count == 0 ? null : (int?)null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Describes a running process as a target, reading its command line so automation runs can be
    /// recognised. Returns null if the process went away while we were asking.
    /// </summary>
    public static BloomTargetFacts? DescribeRunningProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return BloomTargetWatcher.DescribeProcess(
                process,
                WebView2Processes.ReadCommandLine(processId)
            );
        }
        catch (Exception)
        {
            return null;
        }
    }
}
