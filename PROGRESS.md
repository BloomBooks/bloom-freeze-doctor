# Progress log — Bloom Freeze Doctor (BL-16719)

Newest entry last. This file plus `PLAN.md` and `docs/SPIKE-FINDINGS.md` are meant to be enough for
someone (or another Claude session, on another machine) to pick the work up cold.

## How to pick this up

- **The plan** is `PLAN.md`. All nine decisions in §10 are settled; revision markers (`[rev2]`…
  `[rev5]`) show what changed and why.
- **What we have proved** is `docs/SPIKE-FINDINGS.md`. Read it before trusting §4 or §11 of the plan.
- **Two repos are involved.** This one holds the Doctor. Bloom-side changes go on branch
  `BL-16719-Freeze-Doctor` in `C:\github\BloomDesktop` (targeting `master`; they will be backported
  to `Version6.4` later, per plan §9.1, after thorough testing on master).
- **Spike tools**: `spike/FreezeStub` is a freezable stand-in for Bloom; `spike/Probe` answers the
  plan's open questions against a pid. Build with `dotnet build` in each folder.
  - `Probe <pid>` — read-only probes, safe against a Bloom someone is using.
  - `Probe <pid> --dump --waitchain --logmap` — adds a dump (safe) and diagnostics.
  - `Probe <pid> --attach` — **SUSPENDS the target.** Stub only, never a real Bloom.
  - Drive the stub by writing one word to `freezestub-command.txt` next to its exe:
    `sleep`, `stawait`, `spin`, `failfast`, `throw`, `zombie`, `quit`.

## Open questions for John

- Nothing blocking at the moment. Repo question resolved: it is public at
  **https://github.com/BloomBooks/bloom-freeze-doctor**, with the README making the
  very-early-work-in-progress state unmistakable.

---

## 2026-08-18 — Phase 0 spike, first pass

**Plan changes requested and made:** dropped arm64 entirely (§4.5 rewritten — Bloom is x64
everywhere, and an emulated x64 Doctor attaching to an emulated x64 Bloom is an architecture match, so
the second build target, the `IsWow64Process2` helper-spawning path and two unknowns all disappear);
signing both the exe **and** the installer recorded as settled rather than optional (D7).

**Built:** `spike/FreezeStub` (a WinForms stand-in that can freeze or crash on command, one failure
mode per plan state) and `spike/Probe` (a console probe that answers §11's questions against a pid).

**Findings — the two load-bearing bets both resolved, one happily and one not.** Full detail in
`docs/SPIKE-FINDINGS.md`:

- **The dump path works, including on a wedged process.** `DiagnosticsClient.WriteDump(Normal)`
  produced a 2.2 MB dump in ~0.5–0.7 s and ClrMD walked every managed thread out of it, naming the
  exact blocking call — while the UI thread was frozen. §4.1 needs one pipeline, not two.
- **Tier A is completely blind to freezes in STA managed waits.** With the UI thread genuinely stuck
  in `Monitor.Wait`, `IsHungAppWindow` said False, `Process.Responding` said True, and
  `SendMessageTimeout(WM_NULL)` answered in 0 ms. This is inherent, not a tuning problem. It makes the
  UI-thread heartbeat the *only* detector for a freeze class we should expect to be common in Bloom,
  and it makes the CTRL "Report now" button Tier A's only route to hearing about one.
- `IsHungAppWindow` needs ~5 s to make up its mind, so `SendMessageTimeout` is the signal the state
  machine should sample, with `IsHungAppWindow` as corroboration.
- CDP port discovery through the WebView2 child's command line works (found and talked to 8091 and
  8094). Carry-over bug for Phase 1: attribute ports to the parent Bloom pid, or two Blooms will
  cross-wire.
- **YouTrack: everything D2 and §5.2 need is available.** The shipped token can search (so
  fingerprint dedupe is real), attach files, and restrict an attachment to the Developers group. Tested
  in project AUT; the test card was deleted afterwards.

**Suggested plan amendment, for your nod:** move the heartbeat to the front of the §9.1 backport list.
Finding 2 means a 6.4 without it cannot see a whole class of freezes, which changes the value ordering
that list was built on.

## 2026-08-18 — Phase 0 spike, second pass; repo published

**Repo is live and public:** https://github.com/BloomBooks/bloom-freeze-doctor (MIT, © SIL Global 2026).
Checked the tree for secrets before pushing; the only hit was the phrase "no new secret handling" in
prose. **Backport list reordered** so the heartbeat leads, per John.

**Four more findings, one of which changed the design** (detail in `docs/SPIKE-FINDINGS.md` §6–10):

- **The suspending ClrMD attach is out of the design.** Hard-killing the probe mid-attach left the stub
  alive and suspended indefinitely (+20 s and counting), and a later clean attach did not revive it.
  A Doctor crash during diagnosis would have turned a recoverable hang into an unrecoverable one. Rev 2
  wanted to guard it with a child process; measurement says delete it instead — especially since
  `WriteDump` already works on wedged processes and `suspend: false` walks all threads in ~200 ms
  without being able to strand anything.
- **The §3.5 clean-exit proof works exactly as designed.** `ProcessExit` fired for the orderly exit and
  for nothing else: clean quit left proof (`shutdownPhase=1`); `FailFast` (`0x80131623`), an unhandled
  exception (`0xE0434352`) and a hard kill (`-1`) all left none. The exit codes the plan guessed at were
  right. State 3 also confirmed detectable: process alive, window handle gone.
- **Log-to-pid mapping works, and the naive alternative is provably wrong.** Matching each log's
  `App Launched with [exe]` line to process start time and exe path found the right log for the live
  pid — while the most-recently-*modified* log on this machine belonged to a Bloom from a different
  worktree. "Newest file wins" would have attached the wrong log. No handle enumeration needed. Bonus:
  that line carries the full command line, which is what §3.3 needs to spot automation runs.
- **Debugger detection fired on a real Bloom** — the developer's running Bloom is under the VS debugger,
  a live reminder of how often the state that must never be reported is the state we are looking at.

## 2026-08-18 — installed-Bloom verification, and Phase 1 started

**Verified against a real installed Bloom 6.3.2** — the very version in BL-16697. Launched it, probed
it read-only, then closed it cleanly (it popped its Community Forum notice on the way up, which was
Bloom's own doing, not ours). Everything the plan assumed about field configuration holds: channel
derived as `Release` from `AppData\Local\Bloom\current\Bloom.exe`; **CDP answered on the hardcoded port
9222**, confirming from behaviour what we had only read in the 6.3 source; log mapping picked the right
`Log.txt` for the live pid; and `WriteDump` produced **7.5 MB in 1.4 s** with 49 managed threads, 36
walkable, the UI thread reading `Bloom.Program.Main → Run → message loop → WaitMessage`.

Two findings from that pass worth keeping:

- **Deriving the channel needs one deliberate difference from Bloom's own code.** Bloom tests for a
  path ending in `Bloom.dll`, because it asks about its entry assembly; from outside we see the
  process, whose main module is `Bloom.exe`. Copying Bloom's logic verbatim would classify every
  developer build as **Release** — the dangerous direction, since it is what would make a `pnpm go`
  session file cards. Ours tests for `/output/Debug/` without requiring the extension.
- **`pnpm go` runs are not debugger-attached.** `go.mjs` → `watchBloomExe.mjs` → `dotnet watch`, with
  no debugger anywhere. It does not matter, though: the channel check catches those runs regardless,
  which is the more reliable guard.
- A healthy Bloom has **two** top-level windows titled "Bloom", one invisible. It is the splash screen,
  which `SplashScreen.cs` deliberately `Hide()`s rather than `Close()`s so that closing it cannot close
  a dialog it owns. Hence zombie detection must count *visible* windows, and `Process.MainWindowHandle`
  should not be trusted to identify Bloom's real window.

**Phase 1 started:** `src/BloomFreezeDoctor.Core` now holds `FreezeDetector`, the detection state
machine, with 12 passing tests in `tests/BloomFreezeDoctor.Core.Tests`. It is deliberately free of any
process or window API so it can be tested without a frozen Bloom, and every non-obvious rule cites the
spike finding behind it. Thresholds live in `DetectorThresholds` (decision D3's numbers).

## 2026-08-18 — Phase 1: the real-world layer works end to end

`BloomFreezeDoctor.Core` now watches an actual process. **38 tests passing**, and verified against the
stub: `Healthy → Suspect → Frozen`, reporting exactly once, with `IsHungAppWindow` agreeing as
corroboration and `mayFile=True` for a target that looks like a real installed Bloom.

- **`BloomChannel`** — channel from the exe path, plus headless/automation recognition. Both are pure
  string functions with tests naming the trap each avoids (the `Bloom.exe`-vs-`Bloom.dll` difference
  from Bloom's own code, and matching console verbs as whole arguments so a collection folder called
  `upload` does not silence a user's reports).
- **`BloomLogLocator`** — identifies a process's log by its `App Launched with [exe]` line. A test
  reproduces the real arrangement measured in the spike, where the newest log belonged to a Bloom in a
  different worktree.
- **`WindowsTargetProbe`** — one read-only observation. Picks the main window explicitly (visible,
  top-level, largest) rather than trusting `Process.MainWindowHandle`, for the splash-screen reason.
- **`BloomTargetWatcher`** — background timer, never a UI thread; decides in one place whether a report
  may be filed (no debugger, ever; no developer or automation runs). Probe failures are swallowed
  deliberately: a watcher that throws stops watching.

## 2026-08-19 — the exit classifier, the gatherer, and D7 settled

**D7's mechanism is settled**, from the `bloompub-viewer` precedent John pointed at: BloomBooks signs in
GitHub Actions via `sillsdev/codesign/trusted-signing-action@v3` (Azure Trusted Signing, credentials in a
`TRUSTED_SIGNING_CREDENTIALS` secret). No TeamCity step. Signing exe *and* installer is two invocations of
that action. Two things worth remembering, now recorded in the plan: `use-test-certificate: true` for
developing the workflow, and **signing renames the file**, so the release step must not assume the name it
produced survives.

**`ExitClassifier`** implements both regimes of D4, separate from the detector on purpose. A test asserts
the same evidence reaches opposite conclusions under Phase 1 and Phase 3 rules, since that distinction is
what the phasing rests on. `WindowsExitEvidenceCollector` fills it from the Application and System event
logs, both WER folders, Bloom's log tail and the boot time.

**The gatherer works end to end.** Against a stub frozen in an STA managed wait it produces, in six
seconds:

> Freeze Doctor: UI frozen — The UI thread is blocked in `System.Threading.Monitor.ObjWait`. [Release]

…followed by the full managed stack, the CPU sample ruling out a spin, the window inventory, WebView2
children, unexpected modules, and a 2.2 MB dump attached. `ReportFingerprint` proved **stable across two
runs with different pids**, which is what dedupe depends on.

Two report-quality touches worth knowing when reading cards: when Windows calls the window responsive
while we are reporting a freeze, the report **names that contradiction** as the STA-managed-wait signature
instead of leaving a reader to puzzle over it; and the window inventory hides the half-dozen
infrastructure windows every WinForms process carries, counting them instead, so the one that matters is
not buried.

**Next, in order:** the report bundle and outbox (§5.1 — gather to disk, file later, which is how a report
survives the dead network that so often accompanies a freeze); the YouTrack submitter with fingerprint
dedupe, exercised against project `AUT` first; the remaining collectors (wait chains, CDP, Bloom log,
event log and WER, system stats, network probe); then the status window (§2.1). After that, the Bloom-side
changes on `BL-16719-Freeze-Doctor` in BloomDesktop, heartbeat first.

**Note for whoever picks this up:** `spike/Probe` still holds gathering logic (wait chains, CDP probing,
system facts) not yet ported into `Core`. Port it rather than rewrite it — it has been tested against real
Blooms.

## 2026-08-19 — a report reaches the tracker, end to end

**The whole pipeline works.** Gathered from a stub frozen in an STA managed wait → queued on disk → filed
to the tracker's `AUT` test project, with a 2.3 MB dump attached and **restricted to the Developers
group** (D2's requirement, verified on the card itself, not just in code). Then a second occurrence from
a **fresh outbox** — so local folding could not mask the result — found that card by fingerprint and
commented on it rather than filing a second. Test card and comment deleted afterwards; nothing left
behind.

`ReportOutbox` and `YouTrackSubmitter` are the new pieces. **63 tests passing.** The outbox's tests care
mostly about what it refuses to do: duplicate cards, retry a permanent rejection forever, drop reports
when the daily limit is hit, grow without bound, or let one corrupt bundle break the queue.

Two decisions inside worth knowing:

- **Attachment restriction is a second call**, because the multipart upload cannot carry a visibility
  object. If it fails, the attachment is **deleted** rather than left unrestricted — failing to attach is
  a far smaller problem than exposing a dump containing a user's book text.
- **The card says how old the report is** and how many times the problem happened, so a report filed
  three weeks late cannot read as though it were fresh, and Bloom's version is labelled as its version
  *at the time*.

## 2026-08-19 — the report is complete for Tier A

All six collectors are in and wired into the default gatherer, in report order. A gather now takes about
eight seconds and produces: managed stacks, process state, wait chains, the WebView2 interrogation,
Bloom's log with the installer log and Windows' crash records, and the machine-and-network state.

Verified along the way:

- **The WebView2 responsiveness probe works both ways** — tested against a real Chromium rather than
  only against Bloom, so it needed no launching of the developer's Bloom. Healthy pages answered in
  2–3 ms; a page deliberately wedged with `while(true){}` was correctly reported as not answering within
  four seconds while its siblings answered normally. That distinction (renderer wedged versus .NET
  wedged) is the most valuable single signal in the section.
- **The fingerprint distinguishes freeze *kinds*.** A `Thread.Sleep` freeze and a `Monitor.Wait` freeze
  produced different fingerprints, which is what we want — the same blocking call is the same problem,
  a different one is not.
- **Running the machine collector here found something real:** Bloom's collections folder on this machine
  sits inside OneDrive. Exactly the kind of thing nobody thinks to ask a user about.

Two things worth knowing when reading a card: the wait-chain section **says in its own output** that an
empty result is expected for a managed deadlock, because otherwise an empty section reads as an
all-clear; and the log collector **copies** Bloom's log into the bundle rather than referencing it, since
Bloom overwrites `Log.txt` on its very next run.

## 2026-08-19 — it is an application now, and it files reports by itself

`BloomFreezeDoctor.exe` runs end to end unaided. Started against a stand-in process, it discovered it,
watched it, **detected the freeze at the 60-second threshold on its own**, gathered, queued, and filed
`AUT-20846` with a 2.3 MB dump restricted to the Developers group — staying responsive throughout. Test
card deleted.

The **rendezvous (§6) is verified**: a second instance launched while the first was running handed off and
exited with code 0, leaving one Doctor. No handshake protocol — whoever holds the `Local\` mutex is the
Doctor, and Bloom's only job is to make sure one is running.

Also added `.github/workflows/build-and-release.yml`: build, test, publish, sign the exe, `vpk pack`, sign
the installer, draft a release. PRs build and test but never sign. The release step globs rather than
naming files, because signing renames them.

**A trap worth knowing about, because it caught me even though the spike had already established it.**
My first run of this test looked like the Doctor failing to detect anything. It was not failing: I had
frozen the stub with the **STA managed wait**, which is exactly the freeze that cannot be seen from
outside — the frozen stub even reported `Responding=True`. To test Tier A detection, freeze with a plain
block (`sleep`); the STA case (`stawait`) needs the heartbeat only Bloom can publish, and is the reason
Tier B exists.

## 2026-08-19 — Tier B works on a real Bloom, and Bloom's side is complete

**The headline: the Doctor catches the freeze that cannot be seen from outside.** With Bloom publishing a
heartbeat, a deliberately frozen Bloom produced:

> **Verdict:** UI-thread heartbeat stale for 1.9 minutes with no forward progress
> - The UI thread is blocked in `System.Threading.Monitor.ObjWait`.
> - WebView2 answers normally, so the block is in Bloom's .NET UI thread, not the browser.

…while Windows reported `Responding = True` throughout. Measured directly on the channel:
`uiAge=364.8s` alongside `watchdogAge=0.2s`. That contrast is the whole justification for Tier B and it
now exists rather than being argued.

**Bloom's side is done** (branch `BL-16719-Freeze-Doctor`, two commits):

- **The heartbeat**, plus a background watchdog beat so "the UI thread is blocked" can be told from "the
  whole process is wedged", and the clean-exit proof from `ProcessExit`.
- **The session file**, which proved its own worth on the first real run: Bloom recorded its log as
  `Log-tmplo0jmp.txt`, the fallback name — so anything inferring "the newest `Log*.txt`" would have
  attached a different run's log. It also carries the ports, which cannot be discovered from outside at
  all (http.sys owns Bloom's).
- **The in-flight API table**, instrumented at the inner dispatch where the work and the lock-waiting
  actually happen. Published through the existing activity field, so no layout change was needed.
- **Auto-launch**: Bloom started the Doctor one second after itself, with no handshake — Bloom's only job
  is to make sure one is running.
- **`FreezeSimulator`**, inert unless `BLOOM_SIMULATE_FREEZE` is set *and* the build is a developer one.

**A safeguard proved itself by accident.** The auto-launched Doctor was pointed at the real `BL` project
by default, detected the freeze, gathered the whole report — and filed nothing, recording
`State: NotForFiling` because Bloom was a developer build. Exactly the intended behaviour, tested without
meaning to.

**And a process fix.** Three times in one afternoon I tested against a stale binary — twice Bloom's, once
the Doctor's — because the *test project* builds its own copy of Core, so a green test run says nothing
about what is in `BloomFreezeDoctor.exe`. There is now a `build.ps1` that builds the whole solution, runs
the tests, stops any running Doctor first (a running instance locks its own DLLs and the build fails with
an MSB3027 that reads as a mystery), and prints the exe's timestamp so staleness is visible at a glance.
**Use it before testing the app by hand.**

**Next:** the zombie-ending path (§3.6/D9) and the crash-time dump handshake (§6), then verifying the
Velopack packaging and the signed release workflow. The **6.4 backport in §9.1 is deliberately on hold**
until 6.5 has had field testing — John's call, and the right one: backporting a heartbeat we have only
ever seen work on our own machines would be premature.

## Day 4 — zombie-ending, the handshake, and a preflight round that earned its keep

The zombie-ending path and the dump handshake are done, and the Bloom side went through `preflight`
(PR [#8218](https://github.com/BloomBooks/BloomDesktop/pull/8218), still draft).

**Note on commit `ab27289` in this repo.** Its message describes only the seqlock parity fix, but it
actually carried the whole mirror of that round: the `DoctorSession` changes (the already-reported flag
living on the session rather than inside `Exit`, and pruning treating a Doctor-forced exit as *unexplained*
so the zombie evidence survives), a `BloomTargetWatcher` and `DoctorSupervisor` touch, and the matching
tests. Those had been sitting uncommitted while the Bloom side was being worked; `git add -A` swept them
in. The code is right and tested — the message just understates it. Recorded here rather than rewriting
pushed history.

**Two bugs worth remembering, because they are the same bug twice.** Both were in how Bloom describes what
it is doing, and both were found by review rather than by testing:

1. The activity line has now been got wrong **three times in three ways**, and the two failure directions
   are opposites: the refresh overwrote what Bloom stated; then "starting up" was written straight to the
   shared page so there was nothing to carry forward; then it was carried forward for ever and described an
   idle Bloom hours later. Any fix for one direction walks straight into the other. It is now a pure
   `Compose(stated, request, hasHandledARequest)` with both directions pinned by a test — and the first
   version of *that* test was itself unsound, asserting whichever outcome the ambient static state produced.
2. The seqlock counter has now silently disabled the channel **twice** — first the non-atomic increment
   (fixed by `_writeLock`), then an increment placed outside the inner `try`, where one throw inverted the
   parity for the rest of the run. Readers then treat every resting value as "write in progress" and give
   up, so the Doctor falls back to watching from outside, which is blind to the exact freeze this exists to
   catch, with nothing saying why. **The lesson both times: this invariant was being held up by careful
   statement ordering, which is fragile in proportion to how badly it fails.** It is now restored in a
   `finally` from whatever value was actually reached, so no ordering of failures can leave it odd.

**One real gap left open, deliberately.** Nothing in Bloom calls `SetLongOperation`, so
`LongOperationInProgress` is permanently false and the Doctor's five-minute grace for legitimately slow
work **does not exist** — every freeze is judged at one minute. Work behind a modal progress dialog is
safe either way (`ShowDialog` pumps messages, so the heartbeat keeps ticking), but anything that blocks the
UI thread for over a minute without pumping would be filed as a freeze. Which operations to mark cannot be
inferred: "a request has run for a minute" is the *same signal* as the freeze itself. So it is left unwired
with the cost written up at the method, and raised with John as the open decision on the PR.

**Also:** the freeze simulator now works on **Alpha** as well as developer builds, on John's call —
reproducing a freeze usually means working with somebody who is actually experiencing one, and those people
run Alpha, not a build from source.

**On the two "byte-identical" contract files:** they are identical in substance but *not* literally
byte-for-byte, because BloomDesktop's csharpier pre-commit hook wraps three lines more narrowly than this
repo's formatting. Worth knowing before anyone tries to enforce byte-equality mechanically; the pinned
layout tests in both repos are the real drift guard.
