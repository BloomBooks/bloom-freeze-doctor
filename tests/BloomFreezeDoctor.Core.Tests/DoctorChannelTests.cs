using BloomFreezeDoctor.Contract;
using NUnit.Framework;

namespace BloomFreezeDoctor.Tests;

/// <summary>
/// Tests for the shared-memory contract between Bloom and the Doctor.
///
/// **The layout is pinned here BY VALUE, on purpose.** There is one definition of it now — this project,
/// which BloomDesktop references as a package — so the two sides can no longer hold copies that disagree.
/// What they can still do is get out of step over a *version*: this repo changes the layout, Bloom carries
/// on referencing the version before it, and the mismatch shows up as reports full of plausible nonsense
/// rather than as an error, because Bloom publishes to one set of offsets and the Doctor reads another.
///
/// Pinning the numbers means changing the layout has to be deliberate here, and BloomDesktop's own test
/// pins the layout it was compiled against — so Bloom's build fails rather than Bloom quietly publishing
/// to the wrong place. If you are changing the layout, bump SchemaVersion, update these numbers, and
/// expect to update Bloom's side too.
/// </summary>
[TestFixture]
public class DoctorChannelTests
{
    /// <summary>A pid unlikely to collide with a real Bloom while tests run.</summary>
    private const int TestProcessId = 999_001;

    [Test]
    public void The_layout_is_pinned_so_a_change_cannot_pass_unnoticed()
    {
        // If you are here because this test failed: the layout changed. Bump SchemaVersion, update these
        // numbers, and publish a version Bloom can move up to — Bloom's own pinned test will fail until
        // its side is updated to match, which is the point.
        Assert.Multiple(() =>
        {
            Assert.That(DoctorChannelLayout.SchemaVersion, Is.EqualTo(1), "schema version");
            Assert.That(DoctorChannelLayout.Size, Is.EqualTo(4096), "page size");
            Assert.That(
                DoctorChannelLayout.NameFor(1234),
                Is.EqualTo(@"Local\BloomFreezeDoctor.v1.1234"),
                "the name must stay in the Local namespace, and must include pid and version"
            );
        });
    }

    [Test]
    public void What_Bloom_writes_is_what_the_Doctor_reads()
    {
        using var writer = new DoctorChannelWriter(TestProcessId);
        Assert.That(writer.IsOpen, Is.True, "setup: the channel should have been created");

        writer.SetActivity("Publishing to BloomPUB: compressing images");
        writer.SetLongOperation(true);
        writer.SetDebuggerAttached(false);
        writer.SetServerWorkerCounts(busy: 7, blocked: 3);
        writer.SetShutdownPhase(0);
        writer.RecordUiTick();
        writer.RecordWatchdogTick();

        Assert.That(
            DoctorChannelReader.TryRead(TestProcessId, out var snapshot),
            Is.True,
            "the Doctor should be able to read what Bloom just wrote"
        );
        Assert.Multiple(() =>
        {
            Assert.That(snapshot!.ProcessId, Is.EqualTo(TestProcessId));
            Assert.That(snapshot.Activity, Is.EqualTo("Publishing to BloomPUB: compressing images"));
            Assert.That(snapshot.LongOperationInProgress, Is.True);
            Assert.That(snapshot.DebuggerAttached, Is.False);
            Assert.That(snapshot.ServerBusyWorkers, Is.EqualTo(7));
            Assert.That(snapshot.ServerBlockedWorkers, Is.EqualTo(3));
            Assert.That(snapshot.UiTicks, Is.EqualTo(1));
            Assert.That(snapshot.WatchdogTicks, Is.EqualTo(1));
            Assert.That(snapshot.CleanExitRecorded, Is.False);
        });
    }

    [Test]
    public void The_heartbeat_ages_as_time_passes()
    {
        // The age, not the count, is what the detector uses — so it has to be real. Both sides read
        // Environment.TickCount64, which is comparable across processes, unlike a wall clock.
        using var writer = new DoctorChannelWriter(TestProcessId);
        writer.RecordUiTick();

        DoctorChannelReader.TryRead(TestProcessId, out var fresh);
        Thread.Sleep(120);
        DoctorChannelReader.TryRead(TestProcessId, out var older);

        Assert.That(fresh!.UiHeartbeatAge, Is.LessThan(TimeSpan.FromMilliseconds(100)));
        Assert.That(
            older!.UiHeartbeatAge,
            Is.GreaterThan(fresh.UiHeartbeatAge),
            "a heartbeat that is not being bumped must look older as time passes"
        );
    }

    [Test]
    public void A_heartbeat_that_never_ticked_reads_as_infinitely_old_rather_than_as_fresh()
    {
        // The dangerous failure direction: an unticked heartbeat must not read as "just now", or a Bloom
        // that wedged during startup would look perfectly healthy forever.
        using var writer = new DoctorChannelWriter(TestProcessId);
        writer.SetActivity("starting up");

        DoctorChannelReader.TryRead(TestProcessId, out var snapshot);

        Assert.That(snapshot!.UiHeartbeatAge, Is.EqualTo(TimeSpan.MaxValue));
        Assert.That(snapshot.WatchdogHeartbeatAge, Is.EqualTo(TimeSpan.MaxValue));
    }

    [Test]
    public void A_Bloom_that_publishes_nothing_is_simply_absent_not_an_error()
    {
        // Every Bloom in the field today is this case, so it must be quiet and cheap: the Doctor falls
        // back to watching from outside.
        var read = DoctorChannelReader.TryRead(TestProcessId + 12345, out var snapshot);

        Assert.That(read, Is.False);
        Assert.That(snapshot, Is.Null);
    }

    [Test]
    public void The_clean_exit_proof_and_shutdown_phase_survive_for_the_reader()
    {
        using var writer = new DoctorChannelWriter(TestProcessId);
        writer.SetShutdownPhase(3);
        writer.RecordCleanExit();

        DoctorChannelReader.TryRead(TestProcessId, out var snapshot);

        Assert.That(snapshot!.ShutdownPhase, Is.EqualTo(3));
        Assert.That(snapshot.CleanExitRecorded, Is.True);
    }

    [Test]
    public void An_over_long_activity_string_is_truncated_rather_than_corrupting_the_page()
    {
        // Activity text comes from Bloom's own breadcrumbs, which include file paths; a long one must not
        // run over the next field.
        using var writer = new DoctorChannelWriter(TestProcessId);
        writer.SetActivity(new string('x', DoctorChannelLayout.ActivityMaxBytes * 3));
        writer.SetServerWorkerCounts(busy: 5, blocked: 1);

        DoctorChannelReader.TryRead(TestProcessId, out var snapshot);

        Assert.That(
            snapshot!.Activity.Length,
            Is.LessThan(DoctorChannelLayout.ActivityMaxBytes),
            "it must have been truncated"
        );
        Assert.That(
            snapshot.ServerBusyWorkers,
            Is.EqualTo(5),
            "and it must not have overwritten the field that follows it"
        );
    }

    [Test]
    public void An_activity_string_with_multibyte_characters_survives_being_written_and_read()
    {
        // Regression test. Truncating on a character boundary was added so a cut book title could not leave a
        // broken byte in the page — and the first version of that check read one byte past the end whenever no
        // truncation was needed. The resulting exception was swallowed, which left the write sequence at an odd
        // value, which every reader treats as "a write is in progress" — silently disabling the channel for the
        // rest of the run. So this test covers both the short case and the over-long one.
        using var writer = new DoctorChannelWriter(TestProcessId);

        writer.SetActivity("Publishing “Ekkitaaki Fulfulde” — étape 2");
        Assert.That(DoctorChannelReader.TryRead(TestProcessId, out var shortOne), Is.True);
        Assert.That(shortOne!.Activity, Is.EqualTo("Publishing “Ekkitaaki Fulfulde” — étape 2"));

        // Over-long, ending mid-character if cut naively.
        writer.SetActivity(new string('é', DoctorChannelLayout.ActivityMaxBytes));
        Assert.That(
            DoctorChannelReader.TryRead(TestProcessId, out var longOne),
            Is.True,
            "the channel must still be readable after a truncating write"
        );
        Assert.That(
            longOne!.Activity,
            Does.Not.Contain("�"),
            "and must not contain a replacement character from a half-written multi-byte sequence"
        );

        // The channel must still work afterwards — the point of keeping the sequence even.
        writer.RecordUiTick();
        Assert.That(DoctorChannelReader.TryRead(TestProcessId, out var after), Is.True);
        Assert.That(after!.UiTicks, Is.EqualTo(1));
    }

    [Test]
    public void Writing_never_throws_even_when_the_channel_could_not_be_created()
    {
        // Two writers for one pid: the second cannot create the section. Bloom must survive that without
        // noticing, because publishing diagnostics is never worth failing a startup over.
        using var first = new DoctorChannelWriter(TestProcessId);
        using var second = new DoctorChannelWriter(TestProcessId);

        Assert.That(second.IsOpen, Is.False, "setup: the second writer should have failed to create it");
        Assert.DoesNotThrow(() =>
        {
            second.RecordUiTick();
            second.SetActivity("this goes nowhere");
            second.RecordCleanExit();
        });
    }
}
