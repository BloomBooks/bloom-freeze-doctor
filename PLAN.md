# Bloom Freeze Doctor — Plan (BL-16719)

Status: **live working document**, revision 6. It is written to the Bloom team rather than to the
public, so it addresses the reader directly and records arguments as well as conclusions — that history
is the useful part, and it is kept deliberately. Revision markers (`[rev2]` … `[rev6]`) mark what
changed and why: rev 2 folded in a review by Fable, rev 3–5 the decisions in §10 as they were settled,
and rev 6 the first Phase 0 spike findings (see `docs/SPIKE-FINDINGS.md`, which supersedes this
document wherever the two disagree, because it was measured).

All nine decisions in §10 are settled, D7's signing mechanism included `[rev7]`. What remains is the
spike work itemized at the end of the findings document, and the building.

## 1. The problem, and the three states we must diagnose

Bloom users report "Bloom froze" (BL-16697 is the live example) and we get essentially nothing to
work with: the user's problem report is written *after* they killed Bloom, so it describes a healthy
new process, and the log tells us only what Bloom managed to log before it stopped responding. Three
distinct failure states all reach us as "it froze", and all three currently produce no usable
evidence:

| # | State | What is observable from outside | Why it is hard today |
| --- | --- | --- | --- |
| 1 | **UI unresponsive** for longer than a slow network explains | The main window stops pumping messages, while the process, its server, and its WebView2 browser processes may all still be alive and answering | Nobody is looking at the moment it happens; by the time the user acts the evidence is gone |
| 2 | **Hard exit with no report** to Sentry or YouTrack | The process disappears; exit code, Event Log 1000/1002, and WER files are the only traces | Bloom's own reporters never ran, so we hear about it only if the user emails us |
| 3 | **UI gone, process still alive** | No visible top-level window, process still running | `ProgramExit.EnsureBloomReallyQuits` exists *because* this has happened; users see "Bloom won't restart" and we see nothing |

The Freeze Doctor is a small separate Windows app whose whole job is to be watching when one of these
happens, gather everything obtainable at that instant, and file a YouTrack card by itself.

## 2. Shape of the solution

- A **separate .NET 8 Windows app**, `BloomFreezeDoctor.exe`, in its own public repo
  (`github.com/BloomBooks/bloom-freeze-doctor`, MIT, © SIL Global 2026). Until that repo exists it
  lives at `C:\github\bloom-freeze-doctor`.
- **A small status window**, as the card describes — see §2.1. **Decided `[rev3]`:** the card's UI
  wins over the "no UI" idea. English only; no localization.
- **Two capability tiers, deliberately separated:**
  - **Tier A — no cooperation from Bloom.** Everything obtainable about a process we did not
    instrument. **This works against the Bloom 6.3/6.4 already installed in the field**, which is
    where the current pain is.
  - **Tier B — Bloom cooperates.** Bloom publishes a live state channel (heartbeat, breadcrumbs,
    in-flight API calls, ports, its log path, clean-exit and "already reported" markers) and
    auto-launches the Doctor. Much richer, but needs a Bloom release.
- Tier A first, so we have something to hand a frozen user before 6.5 ships. Tier B is a separate
  BloomDesktop PR, developable in parallel.

### 2.1 The window `[rev3]`

Per the card: a smallish always-available window, easy to shrink out of the way, **English only**.

- **What it says.** `Bloom Status: Not running / Running / Frozen`, one line per watched Bloom since
  the Doctor can watch several, plus — usefully, given §5.1 — a line for the outbox when it is not
  empty (`1 report waiting to send`). After it files, the display changes to say so and names the
  card, and a Windows notification appears.
- **Buttons.** `Restart Bloom` after a report. `Report now` appears only while CTRL is held, per the
  card, for testing and for the "Bloom is merely slow" support case.
- **Notification without packaging pain.** A tray `NotifyIcon` with a balloon tip is native to
  WinForms and needs neither an AppUserModelID nor a registered COM activator — the concern that made
  a toast *with a button* expensive in rev2 evaporates now that the interactive surface is a real
  window. Minimizing sends it to the tray, which is what "shrink easily" wants.
- **Launched by Bloom ⇒ starts minimized to the tray**; launched by hand ⇒ shows the window. Bloom
  auto-launches the Doctor on every run (D5), so a window that popped up each time you started Bloom
  would quickly get the Doctor uninstalled. It never steals focus and is never always-on-top; it
  remembers its size and position.
- **The Doctor must not freeze while diagnosing a freeze.** All gathering, dumping and uploading runs
  off the UI thread; the window's job is only to render state that a background worker publishes. Its
  own responsiveness is now a visible feature, so this is not optional.
- PerMonitorV2 DPI awareness, so it is not blurry — cheap, and the first thing anyone notices.

## 3. Detection

### 3.1 Two independent signals, because neither alone is trustworthy

**Signal 1 (Tier A): the window stopped pumping.** `IsHungAppWindow(hwnd)` plus
`SendMessageTimeout(hwnd, WM_NULL, …)` on Bloom's main window works on any Bloom version and keeps
working when the managed process is wedged. **Its known false negative matters here** `[rev2]`: a
WinForms UI thread blocked in a managed wait on an STA (`WaitHandle.WaitOne`, `Task.Wait` →
`CoWaitForMultipleHandles`) still pumps a restricted message set, so it answers `WM_NULL` and reads
as healthy while the user sees a dead UI. Given how much of Bloom's UI thread work awaits WebView2,
that failure mode is likely *common* for us, not exotic.

**Signal 2 (Tier B): the UI-thread heartbeat went stale.** A 500 ms WinForms timer bumps a counter in
shared memory. `WM_TIMER` is *not* dispatched by those restricted pumps, so this catches exactly what
signal 1 misses. It has the opposite weakness — `WM_TIMER` is lowest-priority and can starve on a
busy-but-alive UI — so a stale heartbeat alone never triggers a report `[rev2]`: we require it plus a
second signal (window hung, or no forward progress in the breadcrumb/in-flight-API state).

This is the real argument for Tier B: not just richer reports, but a detector that sees the freezes
Tier A misses. Tier A ships first because it works on installed versions, not because it is enough.

### 3.2 State machine, evaluated once a second per watched process

- `Healthy` → `Suspect` at **20 s** → `Frozen` (report) at **60 s**.
- `Frozen` → `Recovered` if it starts responding again — **we still report.** A 60 s freeze that
  recovered is precisely BL-16697, and it is *better* evidence than one the user killed.
- Process exits while `Suspect`/`Frozen` → one card, "froze, then died or was killed" — not two.
- Process exits while `Healthy` → §3.4.
- No visible top-level window for **30 s** while the process lives → state 3.

### 3.3 Not crying wolf

- **Legitimate long operations.** Publishing, uploading, or making a PDF can block the UI thread for
  a long time. Tier A cannot tell — and at the 60 s threshold it may **suspend a Bloom that is
  merely busy**, which is a real risk to the user's work (§4.1), so total suspension is budgeted at a
  couple of seconds `[rev2]`. A hung window whose process is burning CPU on one thread is reported as
  a *spin*, not a deadlock. Tier B: Bloom marks "long operation: X" in shared memory and the
  threshold rises to **5 minutes** for those.
- **Debuggers:** `CheckRemoteDebuggerPresent` — never report a process someone is debugging, and never
  auto-kill one. Sampled while the target is alive and remembered as a sticky flag, because a dead
  process cannot be asked (see §3.5, where stopping the debugger is the case that matters most).
- **Developer builds:** channel comes from the exe path (`…/Bloom{Channel}/current/Bloom.dll`, or
  `output/Debug` ⇒ `Developer/Debug`, matching `ApplicationUpdateSupport.ChannelName`). On developer
  channels we write the report to disk and skip YouTrack unless `--force` or
  `--youtrack-project=AUT`.
- **Modal dialogs** make the main window *disabled*, not hung: check
  `IsWindowEnabled`/`GetLastActivePopup` and report *which* dialog — an off-screen or
  behind-the-window modal dialog is itself a Bloom bug class worth a card.
- **Headless Bloom is not state 3** `[rev2]`. Bloom has legitimate windowless command-line modes
  (`hydrate`, `upload`, `createArtifacts`, …). Both the no-window rule and the "adopt any Bloom
  process" scan filter on the command line.
- **Bad network.** At freeze time we probe DNS plus an HTTP HEAD to bloomlibrary.org and record the
  answer, so "the internet died" is visible in the card rather than guessed at. It does not suppress
  the report: Bloom blocking its UI thread on a dead network is a bug worth a card.

### 3.4 State 2 without cooperation

The Doctor holds an open handle, so it can read the **exit code** after death. That code is **weak
evidence only** `[rev2]`: `ProgramExit` itself calls `Environment.Exit(1)` when a clean shutdown
stalls 20 s — the same code Task Manager's "End task" produces — and `Main` returns 1 or calls
`Environment.Exit(-1)` on a dozen handled startup errors. So classification combines:

- exit code (`0xE0434352` unhandled managed exception, `0x80131623` FailFast, `0xC0000005` AV — to be
  confirmed in the spike, not asserted),
- the **log tail**, which is decisive for the interesting cases: "Forcing Bloom to close after normal
  shutdown timed out." identifies a stalled-shutdown force-exit, which gets **its own reportable
  verdict** `[rev2]` rather than being suppressed as a probable user kill,
- Event Log 1000/1002 and .NET Runtime entries for that pid, and WER `ReportQueue`/`ReportArchive`.

A bare unexplained exit with no corroboration is logged locally and not reported — **Phase 1 reports
an exit only when there is strong reason to suspect a crash** (Decision D4).

### 3.5 State 2 with cooperation: require proof of a clean exit `[rev4]`

Phase 3 inverts the logic. Instead of hunting for evidence of a crash, Bloom **proves** it shut down
properly, and anything that fails to prove it is reportable. That is a far better test, because it
needs no guessing about exit codes.

**How the proof works, and the one Windows wrinkle.** The instinct — "report any shutdown that fails
to properly close the shared memory file" — is right, except that Windows gives the Doctor no way to
tell Bloom's deliberate close apart from the OS reclaiming the handle at process death: both just
decrement the section's reference count. So the durable signal is a **flag written into the shared
memory immediately before the close**, rather than the close itself.

That works better than it may sound, and it corrects something stated earlier in this plan: a
memory-mapped section does **not** die with the process that created it — it lives until the *last*
handle closes, and the Doctor is holding one. So a flag Bloom writes on its way out stays readable
after Bloom is gone, with no polling race. (The abandoned-mutex trick would give the same clean-vs-
killed distinction as an event, since Windows tracks mutex abandonment — but it adds thread-affinity
rules and no information the flag does not already carry, so we are not using it.)

- **The hook is `AppDomain.CurrentDomain.ProcessExit`**, not an edit to each exit path. It runs for a
  normal return from `Main` *and* for `Environment.Exit`, and does **not** run for `FailFast`,
  `TerminateProcess`, or an access violation — precisely the line we want to draw, and one that no
  future exit path in `Program.cs` can forget to honour. Its budget is a couple of seconds, so the
  write must be tiny and must never be able to block; Bloom's shutdown already has form here
  (`ProgramExit` force-quits after 20 s).
- **It records how far shutdown got, not just that it happened.** A phase counter bumped through
  shutdown means a Bloom that dies mid-shutdown tells us *where*, which is the same evidence state 3
  needs. Shutdown duration comes free with it.
- **The forced path is distinguishable, not silent.** `ProgramExit`'s `Environment.Exit(1)` after the
  20 s timeout still runs `ProcessExit`, so it writes its own value: shutdown stalled, then forced.
  That keeps the §3.4 verdict intact rather than laundering it into "clean".
- **A durable on-disk marker remains, for when the Doctor is not there** — a manually launched Doctor,
  a Doctor restarted, or a machine that rebooted. The flag covers the watched case; the file covers
  the unwatched one.

**What must not be reported as a missing proof** `[rev4]`, or this rule becomes a nuisance:

- **The machine went down.** Power loss, hard reset, or an OS crash leaves no proof and is not a Bloom
  bug — and the Doctor died too, so the *next* Doctor startup is what reconciles the orphaned session
  file. Check Event Log 6008 (unexpected shutdown) / 1074 / 6006 and the last boot time before
  blaming Bloom.
- **The Doctor was killed** while Bloom carried on: a stale session file whose pid is still alive is
  not an exit at all.
- **Sleep and hibernate.** A closed lid must not manufacture a six-hour freeze: the detector works off
  a monotonic tick and watches `PowerModeChanged`, so a resume is a resume.
- **A developer stopping the debugger** `[rev4]`. This is the sharpest case, because "Stop Debugging"
  is `TerminateProcess`: no `ProcessExit`, no proof, and therefore an exact impersonation of the crash
  this rule exists to catch — and we do it many times a day. Four defenses, in order of how much they
  carry:
  1. **The channel rule already covers it, and covers it early.** A Bloom launched from
     `output/Debug` is channel `Developer/Debug`, which §3.3 already restricts to writing the report to
     disk and never filing. The Doctor derives that from the target's own main-module path, so it holds
     with no cooperation and even for a Bloom killed three seconds after launch.
  2. **A sticky "was ever debugged" flag.** `CheckRemoteDebuggerPresent` cannot be asked about a dead
     process, so the Doctor must sample it **while the target lives** and remember the answer for the
     rest of that process's life. Checking only at report time would be checking a corpse — that is the
     bug to avoid here.
  3. **Tier B says so authoritatively.** Bloom writes `Debugger.IsAttached` into the shared memory,
     sticky for the run. It costs nothing and beats every outside inference.
  4. **Parent-process and exit-code hints**, as corroboration only: a parent of `devenv.exe` / VS Code /
     `dotnet`, and the exit code a debugger-initiated terminate produces (believed `0x40010004`,
     `DBG_TERMINATE_PROCESS` — to confirm in the spike, not asserted).

  Worth noting where the risk actually lies: a stopped debug session leaves no WER entry and no Event
  Log 1000, so **Phase 1's rule never fires on it anyway**. This false positive is created purely by
  Phase 3's "prove the clean exit" inversion — and Phase 3 is the Bloom-cooperation phase, where
  defence 3 is available. The exposure and its best answer arrive together.

**And the verdict has to be honestly labelled.** A user ending a healthy Bloom in Task Manager leaves
no proof either. That is worth knowing — people usually do it because something felt wrong — but the
card must say "no orderly shutdown; no crash evidence; may be a user-initiated kill" rather than
claiming a crash. This rule does raise report volume, which is what the local dedupe and rate limits
in §5.1–5.2 are for.

### 3.6 Ending a zombie — and never being pinned by one `[rev4]`

Two separate problems, and the second was a flaw in §2/D5 as written: **a Doctor that lingers while its
watched Bloom lives would be held hostage by the very zombie it just reported**, turning one stuck
process into two.

**Watching and staying alive are decoupled.** Once a target has been reported and then either killed or
marked `reported, abandoned`, the Doctor stops watching it. A zombie we cannot kill must never keep the
Doctor running forever; it drops the target and exits if nothing else remains.

**Yes, the Doctor can end a zombie** — `TerminateProcess` needs only `PROCESS_TERMINATE`, which we have
for our own user's processes. The question is when it should, and it should be narrow:

- **State 3 only. Never state 1.** A frozen Bloom may be holding edits that live in the WebView2 DOM and
  have not yet been posted back to C#; killing it throws away the user's work. A state-3 zombie has no
  UI at all, so there is nothing left for the user to reach or save — the process is unusable by
  definition, which is what makes ending it safe.
- **Only after the report is gathered**, and only after a grace period past the 30 s detection, so a
  shutdown that is merely slow gets to finish by itself.
- **Not while a save or publish is in flight** (Tier B knows this from the in-flight table). Bloom's
  saves go through a write-temp-then-replace path that is meant to survive an interrupted process, so a
  kill should leave either the old file or the new one — but waiting costs nothing, and the report names
  whatever was in flight.
- **Escalate, don't just kill.** Tier B first: signal a named event and let Bloom's *watchdog* thread —
  which is independent of the stuck shutdown — call `Environment.Exit(1)` itself. Bloom then exits under
  its own power, so `ProcessExit` runs, §3.5's proof is written, and the token is released properly. That
  is a much better outcome than an external kill, and it is available exactly when the orderly path is
  the thing that jammed. `TerminateProcess` is the fallback for Tier A, or when the watchdog thread is
  wedged too.
- **No token cleanup is needed** — verified `[rev4]`. `UniqueToken` is a `Bloom.locktoken` **file**
  holding a pid and process name, and `SimpleFileLock.TryAcquireLock` treats the lock as free as soon as
  that pid is no longer running. So ending the zombie is a complete cure for the symptom the user
  actually reports — "Bloom won't start", which they experience as palaso's *Waiting for Bloom to
  finish…* dialog giving up after ten seconds. Nothing has to be deleted by hand. (The stale HTTP port
  needs no attention either: `BloomServer` walks up to 20 ports.)
- **Why Bloom's own safety net does not cover this.** `ProgramExit.EnsureBloomReallyQuits` force-exits
  20 s after `Application.Exit()` — but only if something called `ProgramExit.Exit` in the first place.
  The zombie case is largely the one where nothing did, so that net was never armed. The Doctor is
  covering a gap Bloom structurally cannot close from inside.
- **The user is told, not asked** (Decision D9): the window and notification say Bloom was stuck in the
  background and has been closed, and offer Restart Bloom.

## 4. What goes in a report

Every item is best-effort: a section that cannot be gathered says so in one line and the report still
goes out.

### 4.1 The dump pipeline — one primary, one fallback `[rev2]`

Originally this planned two always-on mechanisms. Instead:

**Primary: `DiagnosticsClient.WriteDump(DumpType.Normal)`** over the runtime's own diagnostics IPC
pipe (this is what `dotnet-dump` does). It produces a few-MB dump that **is** managed-walkable
offline with ClrMD — one artifact and one analysis pipeline serving both freeze and crash cases, and
the size the card asks for. Whether its stacks are as complete as we need is the single most
important thing the spike must settle.

**Both measured in the spike, and the fallback changed as a result** `[rev6]`. `WriteDump` produced a
2.2 MB dump in ~0.5 s **even with the UI thread wedged**, and ClrMD walked every managed thread out of
it, naming the blocking call. Meanwhile the suspending live attach that rev 2 planned as the fallback
turned out to be indefensible:

- A probe hard-killed mid-attach left the target **alive and suspended at +1 s, +4 s, +10 s and +20 s**,
  and a later clean attach-and-dispose did **not** revive it. There is no recovery short of killing
  Bloom, so a Doctor crash during diagnosis would turn a recoverable hang into an unrecoverable one.
- **So `suspend: true` is removed from the design entirely** — not guarded with a child process as rev 2
  proposed. The general rule it leaves behind: *prefer mechanisms whose failure cannot leave the target
  suspended.* `WriteDump` qualifies inherently, because the target's own runtime does the work; if we
  die, it simply finishes or abandons on its own.

**The fallback is therefore a non-suspending attach:** `AttachToProcess(pid, suspend: false)` walked
all threads of a frozen stub in **197 ms**, correctly showing `Monitor.Wait`, and cannot strand
anything. If neither mechanism works we report the OS-level evidence and say plainly that managed
stacks were unavailable — a far better failure than a bricked Bloom.

Managed stacks are resolved to **text on the user's machine** and that text is what we attach. This is
deliberate: a stacks-only minidump generally cannot reproduce managed frames later without heap
memory, and a dump big enough to do that is hundreds of MB — unattachable.

A **larger local dump is kept on disk, never uploaded** (`%LOCALAPPDATA%\SIL\BloomFreezeDoctor\
reports\…`, size-capped, 14-day retention), with its path in the card, so if one card turns out to
matter we can ask that one user for that one file. This is an addition beyond the card and is called
out in Decision D2 rather than buried here `[rev2]`.

### 4.2 From the process (no admin, same user)

- **Managed stacks of every thread** — the single most valuable artifact, and small.
- **Wait chains** (`GetThreadWaitChain`) — with realistic expectations `[rev2]`: WCT does **not** see
  .NET `Monitor`/`lock`, `SemaphoreSlim`, or async waits, so the dominant managed deadlock kinds show
  as "waiting on nothing". Keep it for cross-process `SendMessage` chains into WebView2 and
  loader-lock cases; build no triage logic on it. COM chain resolution needs
  `RegisterWaitChainCOMCallback`, which is **not** privileged.
- **Per-thread CPU time sampled twice ~5 s apart** — what distinguishes a spin loop from a deadlock,
  and cheap.
- **Loaded module list**, flagging non-Microsoft/non-SIL DLLs injected into Bloom (antivirus and
  shell-extension hooks are a genuine freeze cause).
- **All top-level windows**: class, title, visible, enabled, owner — catches hidden modal dialogs and
  orphaned forms.
- Handle/GDI/thread counts and working set, for Bloom **and its WebView2 children** — a renderer at
  100% CPU means the freeze is JavaScript, not .NET.

### 4.3 Through WebView2's debug port

Bloom exposes CDP already, but **finding the port needs care** `[rev2]`:

- 6.4/master: `httpPort + 2`, where httpPort is 8089 + 3·n.
- **6.3: hardcoded `--remote-debugging-port=9222`** (verified on the Version6.3 branch — inside a
  cosmetically-named `#region DEBUG`, but active in Release). Tier A must try 9222 too, and note that
  something else may own 9222 on a developer machine, in which case 6.3's WebView2 silently isn't
  listening.
- **Bloom's HTTP port cannot be found from the TCP table**: `HttpListener` is http.sys, so the
  listening port belongs to pid 4 (System), not Bloom. Read `--remote-debugging-port` out of the
  **msedgewebview2.exe child's command line** (same-user readable), with 8089+3·n probing as fallback.
  Tier B just reads the ports from the session file.

What we get: the target list; then per page a `Runtime.evaluate("1+1")` with a short timeout —
**whether the renderer answers while the window is hung is a first-class triage signal** (renderer
alive ⇒ the .NET UI thread is blocked; renderer dead ⇒ JS is wedged). Plus `Performance.getMetrics`,
and console messages and failed network requests captured for ~10 s from attach (CDP keeps no
history; see §9 phase 4 for why we are *not* recording continuously).

### 4.4 From the filesystem and Windows

- **Bloom's log — and this needs the correction that reversed the original plan** `[rev2]`. Bloom
  calls `Logger.Init()` with `startWithNewFile`, so the log is `%TEMP%\SIL\Bloom\Log.txt`,
  **truncated and recreated every run**. `Log-tmpXXXX.txt` appears *only* when `Log.txt` cannot be
  created — i.e. when another Bloom still holds it, which is exactly the zombie/state-3 restart case.
  Two consequences: "previous runs' logs" mostly do not exist (each run destroys the last), and
  "newest-modified file" picks the **new healthy Bloom's** log in the restart-after-freeze case,
  the exact inverse of what we want. So: enumerate all `Log*.txt`, map file → owning pid by handle
  enumeration (`NtQuerySystemInformation`, normally fine same-user), attach every candidate, and
  state the mapping we used. Major events are flushed per write so the tail is current; minor events
  sit in a 5000-char in-memory buffer that only reaches disk on clean shutdown, so a frozen Bloom's
  file lacks them — Tier B's breadcrumb ring is how we recover that.
- **Velopack installer log** (`%LOCALAPPDATA%\Bloom\velopack.log`) and pending-update markers.
- **Event Log**: Application Error/Hang and .NET Runtime entries for Bloom **and
  msedgewebview2.exe**. Application *and* System logs are standard-user readable; only Security needs
  admin `[rev2]`.
- **WER**: per-user `%LOCALAPPDATA%\Microsoft\Windows\WER\Report*` always; machine-wide
  `%ProgramData%\…\WER\ReportArchive` attempted and skipped silently without admin.
- **System stats**: RAM total/available, free disk on the collection and temp drives, CPU count, OS
  build, WebView2 runtime version, whether a cloud-sync client is running and whether the collection
  sits inside a synced folder.

**Admin-only, attempted and never prompted for** (per your instruction): machine-wide WER archive,
Security log, cross-process wait-chain detail for other users' processes.

### 4.5 x64 only — the architecture question closed `[rev5]`

Dump-writing and ClrMD both require the Doctor's architecture to **match the target's**. Rev 2
therefore planned to ship x64 and arm64 helpers side by side. **That is now dropped: the Doctor is x64
only.**

The reason is that Bloom is x64 everywhere. An arm64 Bloom was tried and turned out no faster than the
x64 build under emulation, so there is no arm64 Bloom to match. On Windows-on-ARM, Bloom therefore runs
as an emulated x64 process — and an emulated x64 Doctor attaching to an emulated x64 Bloom is an
architecture *match*, which is all these APIs require. The host CPU is not what has to agree.

This deletes a build target, a runtime `IsWow64Process2` probe, a helper-spawning path, and two Phase 0
unknowns. **The one thing to watch:** if Bloom ever does ship an arm64 build, an arm64 Doctor becomes a
hard requirement rather than a nicety, because an x64 Doctor cannot walk an arm64 process. Worth a note
in whatever revisits that decision.

## 5. Reporting

### 5.1 Gathering and filing are separate steps, because the network is often down `[rev3]`

**Filing is never attempted inline with gathering.** Two reasons, and the second is the stronger one:
a freeze is frequently *accompanied* by a dead network (BL-16697's own log shows DNS failing on
bloomlibrary.org moments before), and much of our user base works on connections that are down more
than they are up. A design that only files when the network happens to be alive at the instant of the
freeze loses precisely the reports we most want.

So the gatherer's output is always a **complete, self-contained report bundle written to disk**, and
submission is a separate retryable step driven by a queue:

- **Outbox.** `%LOCALAPPDATA%\SIL\BloomFreezeDoctor\outbox\<utc-timestamp>-<fingerprint>\` holding
  `report.md` (the finished description), `meta.json` (summary, fingerprint, target project,
  attachment list, occurrence count, when gathered, attempt history) and the artifacts zip. Written
  to a `staging\` sibling first and then renamed into `outbox\`, so a half-written bundle can never
  be submitted or counted.
- **Drained on Doctor startup, and periodically while it runs.** Startup is the guarantee that
  matters: the most likely next event after a freeze is *the user restarting Bloom*, which launches
  the Doctor, which drains the outbox. The periodic attempt while running (with exponential backoff,
  accelerated by `NetworkAvailabilityChanged` as a hint, never gated on it) is just the fast path.
- **The Doctor lingers while the outbox is non-empty** rather than exiting the moment its last Bloom
  goes away (D5), up to a cap — but it never depends on that, because of drain-on-startup.
- **A 4xx is not a network failure.** Auth or permission rejections mark the bundle
  `failed-permanently` and stop the retries, loudly, in the Doctor's own log. Otherwise an expired
  token would turn into an infinite retry loop against YouTrack forever.
- **Dedupe counts queued-but-unfiled bundles**, so three offline freezes with the same fingerprint
  become one card with `occurrences: 3` and one set of artifacts, not three cards or three dumps. The
  rate limit in the next section therefore applies at **gather** time, not at file time.
- **Late-filed cards say so plainly:** every timestamp in UTC and local, the delay between the
  freeze and the filing stated in the summary line, and the Bloom version recorded as it was *at
  freeze time* (the user may well have upgraded in between — itself worth knowing).
- **Retention and eviction:** a bundle that cannot be filed within 30 days is dropped; the outbox is
  capped by count and total size, evicting oldest first. This is disk on a user's machine, so it is
  part of Decision D2 rather than an implementation detail.
- **A useful side effect:** the bundle *is* the report even if it never files. Support can ask a user
  to send that one folder, and `--list-queue` / `--drain` make the queue inspectable for testing and
  for support staff.

### 5.2 The card

- **YouTrack card** in project `BL`, via the same REST calls and the same `auto_report_creator`
  permanent token Bloom already uses (`src/BloomExe/web/YouTrackIssueSubmission.cs`). Decision D6.
- Summary shaped for scanning:
  `Freeze Doctor: UI hung 62s, UI thread in EpubMaker.SaveEpub (Bloom 6.4.0 Release)`.
- Description leads with the **verdict and the evidence for it**, then the fingerprint, then the
  sections. Artifacts zipped and attached, with the developers-only visibility Bloom already uses for
  report comments (Decision D2).
- **Fingerprint + dedupe, local cache first** `[rev2]`. Fingerprint = hash of (state, top ~5 managed
  frames of the blocked thread, Bloom version). A **local** fingerprint cache does the deduping and
  the rate limiting, works offline, and needs no permissions we have not verified. Searching YouTrack
  for an existing card with that fingerprint (to comment instead of creating) is an enhancement,
  gated on confirming the token can query BL at all — Bloom's own code only ever creates.
- **Rate limits:** one card per Bloom run per state; per-machine cap default 3/day.
- **Never duplicate Bloom's own report.** Tier B: Bloom writes a `reported` marker on a successful
  Sentry/YouTrack submission and we defer to it. Tier A: hold off if the log shows
  `*** ProblemReportApi is about to report` — but only **within the last ~60 s** `[rev2]`, since that
  line appears throughout a normal session for nonfatal reports.

## 6. The Bloom ↔ Doctor contract (Tier B)

Constraint from your instructions: this must survive Bloom's server being deadlocked or starved of
workers. So **the primary channel is not the web server, and not request/response at all.**

- **Shared memory, written by Bloom, read by the Doctor, needing no cooperation at read time.** A
  fixed-layout `Local\BloomDoctor-<pid>` MMF: schema version; the UI-thread tick (§3.1 signal 2); a
  separate watchdog-thread tick (distinguishes "UI thread blocked" from "whole process wedged");
  current tab/book/dialog; current long-operation; server busy/blocked worker counts (BloomServer
  already tracks `_countBlockedThreads`); a ring of ~200 breadcrumbs; **and the recent minor-event
  ring** `[rev2]`, which is how the pipe below got deleted. A dedicated low-priority background
  thread refreshes the snapshot; it touches only its own lock-free structures — never a lock the rest
  of Bloom uses, and specifically never `Logger`'s (a frozen thread may hold that one, so anything
  calling `Logger.LogText` from another thread can block forever).
  **Plus the clean-exit proof from §3.5** `[rev4]`: a shutdown phase counter, and the final "shut down
  properly" flag written from `ProcessExit` immediately before the close. The section stays readable
  after Bloom dies, because the Doctor holds a handle to it — which is what lets this be the primary
  exit signal rather than the file below.
- **Breadcrumbs from a few chokepoints, not hundreds of call sites:** API request start/finish with
  url and elapsed ms (`BloomApiHandler.ProcessRequestAsync` is a single dispatch point), tab changes,
  dialog open/close, book select/save, publish steps. The in-flight API table alone may be the whole
  answer for BL-16697-class freezes: "POST /bloom/api/publish/… started 47 s ago, never finished".
- **A session file on disk** (`%LOCALAPPDATA%\SIL\BloomFreezeDoctor\sessions\bloom-<pid>.json`): pid,
  start time, exe path, version, channel, command line, http/ws/cdp ports, **log path**, collection
  name, and the diagnostic snapshot Bloom already assembles for problem reports. Written at startup,
  refreshed every ~10 s so a late-launched Doctor still gets it, **written temp-then-rename** so a
  reader never sees half a JSON `[rev2]`, and joined at exit by `bloom-<pid>.exit.json` (`exitKind`,
  whether we reported, YouTrack/Sentry ids). Files, not shared memory, because these must survive **a
  machine reboot and a Doctor restart** — the cases where no handle is left holding the section open
  `[rev4]`.
- **No named pipe** `[rev2]`. Its only payload was "dump the minor-event log on demand", which is
  now in the MMF — and which would have been served through `Logger`, whose lock the frozen thread
  may hold. Cut.
- **Launch and rendezvous with no handshake protocol.** Bloom writes its session file, then starts
  the Doctor if installed. The Doctor takes a `Local\` (per-session, not `Global\`) mutex; the owner
  watches the sessions directory and adopts **every** session it finds, so one Doctor watches several
  Blooms, and a second instance exits after Bloom's file is on disk. That is the "connect to an
  already-running Doctor" requirement, done through the filesystem instead of IPC. Race handling
  `[rev2]`: start the watcher **before** the initial scan, rescan periodically (FileSystemWatcher
  drops events on buffer overflow and must never be the only signal), and *wait* on the mutex with a
  timeout, treating `AbandonedMutexException` as "take over" rather than peeking and exiting — which
  would otherwise lose the race against a dying owner. A manually launched Doctor with no args scans
  for Bloom processes and adopts them, session file or not.
- **Crash-time dump handshake, cheap when nobody is listening** `[rev2]`. In its
  FailFast/unhandled-exception path Bloom signals a named event and waits while the Doctor dumps it
  from outside (more reliable than self-dumping a corrupted process, and it gets us a real dump for
  state 2 with no admin). But it **first probes Doctor liveness with a zero timeout and waits at most
  2–3 s** — those paths already do Sentry plus a message box, and an unconditional 10 s pause when no
  Doctor is running would just make every crash worse for the user.
- **Protocol drift** is prevented structurally: there is one definition, published as the
  `BloomBooks.FreezeDoctor.Protocol` package from this repo and referenced by BloomDesktop, plus a
  `SchemaVersion` and a test in each repo pinning the layout by value. It began as a copied source file
  in each repo; they drifted within two days, which is what earned the package. [rev9]

## 7. Packaging, repo, CI

- Public repo `BloomBooks/bloom-freeze-doctor`, MIT, © SIL Global 2026. `net8.0-windows`, `WinExe`
  (no console flash), **x64 only** — see §4.5 for why arm64 is not needed.
- **Velopack installer** rather than Inno Setup: the toolchain Bloom already uses, silent per-user
  install with no intro and no license page (what the card asks for), auto-update for free. To
  confirm in Phase 2: that its install UX really matches "no intro, no license", and that it is happy
  packaging a windowless helper.
- GHA on `main`: build, test, bump the patch version and commit it, tag, `vpk pack`, publish a
  release.
- **Antivirus and EDR are a delivery risk, not a footnote** `[rev2]`. An unsigned, freshly-downloaded
  exe that does `OpenProcess(VM_READ)`, suspends threads, writes minidumps and POSTs them is the
  textbook procdump-alike behavioural signature — and users with mysterious freezes skew towards
  managed org machines running exactly that tooling. Consequences: **code signing moves from "later
  hardening" to a Phase 2 gate** (Decision D7), we plan for a Defender false-positive submission, and
  we document the warnings support staff should expect to talk users through.
- Tests on `windows-latest`: a deliberately-hanging stub app lets CI exercise detection, gathering,
  fingerprinting, dedupe and rate limiting with no Bloom present; report rendering is snapshot
  tested; YouTrack submission is tested against the `AUT` project (which Bloom's submitter already
  treats as the test project) and cleaned up.
- **Doctor lifecycle discipline** `[rev2]`, for a thing whose job is to outlive failures: bounded
  handle/CDP-connection use across an 8-hour session, its own rotating log, a crash of the Doctor
  recorded where we will see it, and (Tier B) Bloom relaunching it if it dies.

## 8. How we prove it works

We cannot wait for real freezes, so we build the triggers:

- The CTRL-revealed **Report now** button (§2.1) and a `--report-now <pid>` switch that does the same
  thing without a click, so automated tests can drive it. Either one files a card for a *healthy*
  Bloom. This is the main end-to-end test, and also how support can get a snapshot of a Bloom that is
  merely "slow".
- `--list-queue` and `--drain` to exercise the offline outbox (§5.1) without unplugging anything.
- Bloom gets dev-channel-only hidden triggers: block the UI thread N seconds; block it *in an STA
  managed wait* (the §3.1 false-negative case, which must be seen to fail Tier A and be caught by
  Tier B); `FailFast`; return from `Application.Run` with a foreground thread alive (state 3); spin
  the UI thread at 100% CPU.
- **Dogfooding is a gate, not an afterthought**: the Doctor runs on the team's machines against real
  Bloom use before any installer goes to a user.

## 9. Phasing

| Phase | Content | Why here |
| --- | --- | --- |
| **0. Spike (~1–2 days)** | Against an **installed 6.3 and 6.4** (not a dev build — a dev build is channel `Developer/Debug`, which the Doctor special-cases, and 6.3's CDP is 9222, so a dev build exercises neither field configuration) `[rev2]`: does `DiagnosticsClient.WriteDump` beat ClrMD live attach; kill the Doctor mid-suspend and see what happens to Bloom; does CDP answer while the window is hung; what does a wait chain actually contain; can the token search BL and set attachment visibility (both Phase 1 depends on them); Deliver one sample report. | These are the load-bearing bets. If the dump story or the suspend-safety story is different from what we assume, §4 changes shape — better to know on day one. |
| **1. Tier A Doctor** | Watch/adopt, detect all three states, gather §4, fingerprint/dedupe/rate-limit, file the card, no UI. Runs against 6.3/6.4 in the field. | Something we can hand a frozen user *now*, without waiting for a Bloom release. |
| **2. Ship it** | Public repo, MIT, Velopack installer, GHA bump + release, **signing resolved (gate)**, dogfooding (gate). | Both gates must clear before we ask a user to install it. |
| **3. Bloom cooperation (BloomDesktop PR)** | MMF heartbeat + minor-event ring, breadcrumbs and in-flight API table, session/exit/reported markers, auto-launch and rendezvous, bounded crash-time dump handshake, dev-only freeze triggers, optional CTRL+Help manual launch. | Better data *and* the detector that catches the STA-wait freezes Tier A misses — but gated on a Bloom release, so it runs in parallel with 1–2. |
| **4. Extras** | Admin-only extras, dump retention policy, Doctor self-update. **Continuous CDP console/network recording is cut** `[rev2]` — permanently attaching to every user's WebView2 with Network/Console enabled is standing overhead for speculative benefit; the at-freeze responsiveness probe plus ~10 s capture is the 90% version. Revisit only if a real card shows we needed the history. | Refinements once the core has shown what is actually useful. |

### 9.1 A safe subset to backport into 6.4 — ON HOLD `[rev8]`

> **Deferred deliberately until 6.5 has had field testing.** The Bloom-side work is written and merged on
> `BL-16719-Freeze-Doctor`, and it works — but it has only ever been seen to work on our own machines, on
> freezes we caused on purpose. Backporting a heartbeat into a released line on that basis would be
> premature. Once 6.5 has been in real users' hands and the reports that come back are the ones we
> expected, revisit the ranked list below.


Worth doing, and the value/risk ratio is better than expected: the two most valuable pieces of Tier B
are also the safest, because they are write-only, additive, and impossible to fail in a way that
affects Bloom's behaviour. And Velopack auto-update means a 6.4 patch reaches the installed base in
days, not at 6.5.

**Target `Version6.4` only.** It is live — it had a commit today. `Version6.3` has been untouched since
late June, so shipping from it costs a release-line revival; do that only if a specific stuck user
justifies it, not on spec.

**One constraint governs all of this: define the full Tier B contract first, then backport a strict
subset of it** — same file names, same field names, same `SchemaVersion`. If the patch invents a
throwaway format, the Doctor needs two code paths forever, which is a worse tax than the patch is
worth.

Ranked by what each buys against what it risks:

1. **The UI-thread heartbeat. First, and the spike is why** `[rev6]`. A 500 ms WinForms timer bumping
   a counter, plus a background thread publishing it through the memory-mapped file (cheaper and less
   racy here than rewriting a file twice a second). ~40 lines.
   *What it buys:* the only detector we have for a freeze in an STA managed wait. The spike measured
   Tier A as **totally blind** to that case — `IsHungAppWindow` False, `Process.Responding` True,
   `SendMessageTimeout` answering in 0 ms, while the UI thread sat in `Monitor.Wait` — and the cause is
   inherent to how `CoWaitForMultipleHandles` pumps sent messages, so there is no cleverer outside
   probe to find. Bloom's UI thread awaits WebView2 constantly, so this is likely a *common* Bloom
   freeze shape rather than an exotic one. A 6.4 without this cannot see that whole family of freezes.
   *Ship it together with item 2*, since a heartbeat-detected freeze still needs the log path to
   produce a useful report.
2. **The session file.** At startup, write one small JSON: pid, version, channel,
   exe path, command line, start time, http/ws/cdp ports, collection name, and — the point of the
   exercise — **`Logger.LogPath`**. Write-once, on a background thread a moment after startup, wrapped
   in a catch-everything.
   *What it buys:* it retires the three fiddliest hacks in Tier A at a stroke. §4.4's "which
   `Log*.txt` belongs to this pid" problem (whose naive answer is actively wrong in the
   restart-after-freeze case) becomes a lookup; the 9222-versus-`httpPort+2` guessing goes away; and so
   does reading the CDP port out of the WebView2 child's command line because http.sys hides Bloom's
   own port. Perhaps 30 lines, and it removes several Phase 0 unknowns.
3. **The clean-exit proof.** One `AppDomain.CurrentDomain.ProcessExit` handler writing a tiny record —
   §3.5 in about ten lines. *What it buys:* state 2 stops being exit-code archaeology and becomes
   positive proof, on the installed base. *The one caveat:* this is the only item that touches
   shutdown, and Bloom's shutdown has form (`ProgramExit` force-quits after 20 s). It is the same class
   of work Bloom already does there — the end of `Main` rewrites the whole log file — but it must be a
   sub-kilobyte write that cannot block and cannot throw.
4. **The in-flight API table.** The highest-value item diagnostically — it may simply *answer*
   BL-16697 with "POST /bloom/api/publish/… started 47 s ago and never returned" — but the only one
   that touches a hot path, `BloomApiHandler.ProcessRequestAsync`, which every request goes through. It
   can be made genuinely safe (a couple of array writes into a fixed-size slot indexed by thread, no
   locks, no allocation, nothing that can throw), but "safe if written carefully" is a different claim
   from items 1–3. Patch it only after those have proved themselves, and only with dogfooding first.
   Note it remains the best item *diagnostically* — the reordering above is about what is
   **detectable**, not about what explains a freeze once detected.
5. **The "already reported" marker.** Three lines where `ProblemReportApi` succeeds. Retires §5.2's
   log-scraping hold-off hack. Trivial, do it while we are in there.

**Explicitly not in a patch:** auto-launching the Doctor (changes startup behaviour and needs the
install probe — support can launch it by hand, which is the card's own manual path); the crash-time
dump handshake (adds a wait to a crash path, absolutely not); the minor-event ring (needs `Logger`
changes); the dev-only freeze triggers (no reason to ship them).

## 10. Decisions I need from you

My recommendation first in each.

- **D1 — UI. DECIDED `[rev3]`: the card's window, as specified.** Smallish, easy to shrink out of the
  way (minimize to the tray), English only — no localization. Status line per watched Bloom, a
  Windows notification after filing, a Restart Bloom button, and Report now while CTRL is held. Two
  consequences folded into §2.1: launched *by Bloom* it starts minimized to the tray, since a window
  appearing on every Bloom start would get it uninstalled; and all gathering moves off the UI thread,
  because a Freeze Doctor that freezes while diagnosing a freeze is now visibly absurd.
- **D2 — Consent and privacy. DECIDED `[rev3]`: as recommended.** This files reports, dumps included,
  with no user interaction, whereas Bloom's existing flow always asks. Dumps and logs can contain book
  text, file paths, the user's name and email from Registration, and whatever else is in memory. So:
  **file automatically,
  with (i) developers-only visibility on attachments, as Bloom already does for report comments,
  (ii) the window and notification from D1 telling the user a report was sent and naming the card,
  (iii) plain wording in the install instructions support hands out, and (iv) explicit acknowledgement
  of what sits on the user's own disk — a larger dump we never upload, kept 14 days, plus the outbox
  of queued report bundles from §5.1, kept up to 30 days. Both are additions beyond what the card
  asked for.** Alternatives: ask before sending (defeats the unattended case); no dump without an
  opt-in.
- **D3 — Thresholds. DECIDED `[rev3]`: as recommended.** 20 s suspect / 60 s report; 5 min when Bloom
  reports a long operation; 30 s for state 3. All live in the Doctor's settings file, so dogfooding can
  move them without a release.
- **D4 — Unexplained exits. DECIDED `[rev4]`.** *Phase 1:* as recommended — report an exit only when
  there is strong reason to suspect a crash; stalled-shutdown force-exits get their own verdict; log
  the rest locally in silence. *Phase 3:* invert it — Bloom proves a clean exit and anything without
  the proof is reportable (§3.5), which retires the exit-code guesswork entirely.
- **D5 — Who runs it. DECIDED `[rev4]`: as recommended.** Bloom auto-launches the Doctor whenever it is
  installed, **on every channel for now**, and the Doctor manages its own lifetime rather than being
  killed by Bloom (which would break the multi-Bloom case and the crash path). Two riders:
  - **Deferred, revisit before release:** whether the Release build should auto-launch at all. It is
    fairly harmless, since nothing happens unless the Doctor is installed, but it does cost a
    look-for-an-installation on every startup. Months away; not settled now. Whatever we choose, that
    probe must be cheap and must never delay Bloom's startup path.
  - **The Doctor is never pinned by what it watches** (§3.6) — the flaw in "exits when its last watched
    Bloom is gone", which a zombie Bloom would exploit to keep the Doctor alive forever.
- **D6 — The YouTrack token in a public repo. DECIDED `[rev4]`: as recommended.** Reuse the existing
  `auto_report_creator` token — BloomDesktop is already public and already carries it in the clear, so
  this adds no new exposure. A serverless relay (nothing shipped) stays on the list as later hardening
  for both apps, not a prerequisite.
- **D7 — Signing. FULLY SETTLED, mechanism included `[rev7]`.** BloomBooks already signs from GitHub
  Actions, so the release stays entirely in GHA and no TeamCity step is needed. The pattern comes from
  `BloomBooks/bloompub-viewer`'s `main.yml`:
  - **`sillsdev/codesign/trusted-signing-action@v3`** — an SIL wrapper around **Azure Trusted
    Signing** — authenticated with a `TRUSTED_SIGNING_CREDENTIALS` secret (a JSON blob of
    tenant/client/secret/endpoint/account, masked in the logs). No certificate or private key ever
    touches the runner.
  - Guarded with `if: github.event_name != 'pull_request'`, so pull requests build and test without
    consuming signing quota.
  - It takes `files` (explicit paths) or `files-folder` plus `files-folder-filter`, so **signing the exe
    and the installer is two invocations of the same action**: once over `BloomFreezeDoctor.exe` before
    `vpk pack`, once over the packed installer afterwards. That is how we satisfy the sign-both
    requirement — Velopack ships the exe inside the package, and an unsigned exe inside a signed
    installer is exactly what behavioural AV objects to (§7), which is why Bloom signs `Bloom.exe` and
    `BloomPdfMaker.exe` separately as well.
  - `use-test-certificate: true` exists for exercising the pipeline without producing distributable
    binaries. Worth using while the workflow is being written, rather than signing junk with the real
    certificate.
  - **Gotcha to design around, from the viewer's own comment: signing renames the file.** Theirs goes
    from `BloomPUB-Viewer-Setup-<x.y.z>.exe` to `BloomPub.Viewer Setup <x.y.z>.exe`, and uploading turns
    the spaces into periods; they cope by globbing both forms in the release step. Our release step must
    not assume the name it produced survives signing.
  - (No arm64 question to ask any more — see §4.5.)
- **D8 — Ordering. DECIDED `[rev5]`: as recommended, with two riders.** Tier A leads, because it helps
  someone frozen on 6.3.2 today; whichever is ready first wins, and the Bloom-side changes proceed in
  parallel as time permits. Plus: **a safe subset gets backported to 6.4** — see §9.1 for the ranked
  list and, importantly, for the rule that the full contract is designed *before* the subset is
  backported, so the Doctor never needs two code paths.
- **D9 — Ending a zombie. DECIDED `[rev5]`: as recommended.** Do it automatically under the §3.6 guards
  — state 3 only, after the report, after a grace period, never mid-save, never a debugged process — and
  tell the user afterwards, with a Restart Bloom button. Acting rather than asking is the point: the
  person hurt by a zombie is trying to start Bloom and being told *Waiting for Bloom to finish…*, and
  expecting them to find a tray window first is expecting the support call we are trying to prevent.

## 11. Open technical unknowns (Phase 0 settles these)

- Is `DiagnosticsClient.WriteDump(Normal)` output managed-walkable enough for complete stacks? (High
  confidence from `dotnet-dump`, but unproven for us — and §4.1's whole shape rests on it.)
- Does the diagnostics IPC pipe answer at all when the process is wedged (including inside a GC)?
- Can ClrMD live-attach walk stacks of a wedged process? (The arm64 half of this question is gone —
  see §4.5.)
- Exact exit codes for unhandled managed exceptions and `FailFast` — recalled, not tested.
- Does CDP still answer while the .NET UI thread is blocked? (Expected yes, different process — and
  that is what makes the §4.3 signal valuable.)
- Can we map `Log*.txt` → owning pid reliably by handle enumeration? (§4.4 depends on it; without it,
  the restart-after-freeze case attaches the wrong log.)
- Can `auto_report_creator` query BL, and set attachment visibility?
- How Defender and common EDR products actually react to the Doctor's API combination.
