using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FreezeStub;

/// <summary>
/// Stands in for Bloom while we prove out the Freeze Doctor's mechanics. It has a real WinForms
/// message pump on a real STA thread, and it can be told to fail in each way the Doctor must
/// detect, so we never have to break a real Bloom to test detection.
///
/// Commands arrive as a single word written to a file next to the exe (the "command file"), which
/// keeps the stub free of any IPC that might itself be the thing that hangs. Poll interval is
/// short because a test should not have to wait.
/// </summary>
internal static class Program
{
    /// <summary>Name of the file we watch for one-word commands. See <see cref="Apply"/>.</summary>
    internal const string CommandFileName = "freezestub-command.txt";

    private static Form _form = null!;
    private static string _commandPath = null!;

    [STAThread]
    private static int Main(string[] args)
    {
        _commandPath = Path.Combine(AppContext.BaseDirectory, CommandFileName);
        // Start from a clean slate: a command left over from a previous run must not fire at startup.
        TryDelete(_commandPath);

        ApplicationConfiguration.Initialize();

        _form = new Form
        {
            // The Doctor finds its target by process, but a recognizable title makes manual
            // testing and window enumeration easy to eyeball.
            Text = "FreezeStub - pretending to be Bloom",
            Width = 520,
            Height = 240,
        };
        var status = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Text =
                $"pid {Environment.ProcessId}\r\n"
                + "healthy - write a command to\r\n"
                + CommandFileName,
        };
        _form.Controls.Add(status);

        // A UI-thread timer is the same mechanism Bloom will use for its heartbeat, so polling
        // for commands here also exercises "the UI thread is dispatching WM_TIMER" for free:
        // when the UI thread is blocked, this stops firing, which is exactly the signal under test.
        var timer = new System.Windows.Forms.Timer { Interval = 250 };
        timer.Tick += (_, _) => PollForCommand(status);
        timer.Start();

        Application.Run(_form);
        return 0;
    }

    private static void PollForCommand(Label status)
    {
        if (!File.Exists(_commandPath))
            return;

        string command;
        try
        {
            command = File.ReadAllText(_commandPath).Trim().ToLowerInvariant();
        }
        catch (IOException)
        {
            return; // still being written; we'll read it on the next tick
        }
        TryDelete(_commandPath);
        if (command.Length == 0)
            return;

        status.Text = $"pid {Environment.ProcessId}\r\ncommand: {command}";
        Apply(command, status);
    }

    /// <summary>
    /// Carries out one command. Each case corresponds to a state or failure mode in the plan;
    /// the comments name which, because that mapping is the whole point of this stub.
    /// </summary>
    private static void Apply(string command, Label status)
    {
        switch (command)
        {
            // State 1, the ordinary case: UI thread blocked in a plain wait. IsHungAppWindow
            // should see this.
            case "sleep":
                Thread.Sleep(TimeSpan.FromMinutes(5));
                break;

            // State 1, the case that matters most (plan section 3.1): blocked in a managed wait on
            // an STA thread. CoWaitForMultipleHandles pumps a restricted message set, so the window
            // may still answer WM_NULL and read as healthy while the UI is dead. WM_TIMER is not
            // dispatched, so the heartbeat should catch it.
            case "stawait":
                using (var never = new ManualResetEventSlim(false))
                    never.Wait(TimeSpan.FromMinutes(5));
                break;

            // State 1, spinning rather than blocked: per-thread CPU sampling should tell these
            // apart (plan section 4.2).
            case "spin":
                var until = Stopwatch.StartNew();
                while (until.Elapsed < TimeSpan.FromMinutes(5)) { }
                break;

            // State 2: a hard crash that runs no orderly shutdown. No ProcessExit handler fires,
            // which is what section 3.5's proof-of-clean-exit relies on.
            case "failfast":
                Environment.FailFast("FreezeStub was told to fail fast");
                break;

            // State 2: an unhandled exception, for comparison with failfast's exit code.
            case "throw":
                throw new InvalidOperationException("FreezeStub was told to throw");

            // State 3: the window goes away but the process lives on, held up by a foreground
            // thread - the zombie of section 3.6.
            case "zombie":
                new Thread(() => Thread.Sleep(TimeSpan.FromMinutes(10)))
                {
                    IsBackground = false,
                    Name = "FreezeStub zombie keeper",
                }.Start();
                _form.Close();
                break;

            // The control case: a clean, orderly exit that should leave proof behind.
            case "quit":
                Application.Exit();
                break;

            default:
                status.Text = $"pid {Environment.ProcessId}\r\nunknown command: {command}";
                break;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
