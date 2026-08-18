# Phase 0 spike findings

Answers to the open questions in `PLAN.md` §11, measured rather than assumed. Each entry says how it
was obtained so it can be re-checked. Tools live in `spike/` (`FreezeStub`, `Probe`).

Machine: Windows 11 Pro 26200, .NET SDK 10.0.303, WebView2 151.0.4129.86.
Targets: `FreezeStub` (our stand-in, net8.0-windows x64) and two running dev Blooms (read-only).

---

## 1. CONFIRMED — `DiagnosticsClient.WriteDump` is the right primary path

`DumpType.Normal` over the runtime's own diagnostics IPC pipe:

| Target state | Result |
| --- | --- |
| Healthy | 2.2 MB in **712 ms**, ClrMD walked 4/4 managed threads |
| UI thread blocked in `Thread.Sleep` | 2.2 MB in **531 ms**, ClrMD walked 3/3 threads, 14 frames deep |
| UI thread blocked in an STA managed wait | same, full stack recovered |

**The pipe answers while the UI thread is wedged** — §11's biggest worry, and the answer is yes. The
dump is small enough to attach, and ClrMD reads managed stacks back out of it. §4.1's plan of "one
dump pipeline, resolved to text locally" stands, and the suspending live attach stays a fallback we
may never need.

The recovered stack is exactly the artifact the plan promises:

```
System.Threading.Thread.SleepInternal
System.Threading.Thread.Sleep
FreezeStub.Program.Apply
FreezeStub.Program.PollForCommand
FreezeStub.Program+<>c__DisplayClass3_0.<Main>b__0
System.Windows.Forms.NativeWindow.Callback
(dynamicClass).IL_STUB_ReversePInvoke
...
System.Windows.Forms.Application+ThreadContext.RunMessageLoop
FreezeStub.Program.Main
```

For a real Bloom freeze that middle section names the actual blocking call.

## 2. CONFIRMED, and worse than the plan assumed — Tier A is blind to STA managed waits

The plan (§3.1) said `IsHungAppWindow` has this false negative. Measured, it is **total, not
marginal**. With the UI thread genuinely stuck in `ManualResetEventSlim.Wait` → `Monitor.Wait`
(verified from the dump), after 8 seconds:

| Signal | Reading | Truth |
| --- | --- | --- |
| `IsHungAppWindow` | **False** | frozen |
| `Process.Responding` (.NET) | **True** | frozen |
| `SendMessageTimeout(WM_NULL, 2s)` | **answered in 0 ms** | frozen |

All three say healthy. This is inherent: `CoWaitForMultipleHandles` must dispatch *sent* messages for
cross-apartment COM to work, so no `SendMessage`-based probe can ever detect it. There is no cleverer
Tier A trick to find — we checked.

**Consequences for the plan:**

- **The heartbeat is not an enhancement, it is the only detector for this class.** It should move up
  the §9.1 backport list — arguably ahead of the clean-exit proof — because without it a whole family
  of freezes is invisible in the field. Bloom's UI thread awaits WebView2 constantly, so we should
  expect this to be a *common* Bloom freeze shape, not an exotic one.
- **The CTRL "Report now" button is Tier A's escape hatch for this class**, not just a test
  affordance: a user whose Bloom is dead-but-"responsive" pressing that button is the only way Tier A
  ever hears about it. That is an argument for the card's UI (D1) beyond what D1 itself said.

## 3. CONFIRMED with a caveat — `IsHungAppWindow` needs ~5 seconds

With the UI thread blocked in a plain `Thread.Sleep`, at 3 seconds: `IsHungAppWindow` was still
False while `SendMessageTimeout` had already timed out and `Process.Responding` was already False.
Windows does not mark a window hung immediately.

Harmless at our 20 s / 60 s thresholds (§3.2, D3), but it means **`SendMessageTimeout` is the
faster-reacting signal of the two** and should be what the state machine samples; `IsHungAppWindow`
is corroboration.

## 4. CONFIRMED — CDP discovery via the WebView2 child's command line works

Reading `--remote-debugging-port` out of `msedgewebview2.exe` command lines (WMI `Win32_Process`)
found ports **8091** and **8094** on this machine, and both answered `/json/version` and `/json/list`
(Edg/151.0.4129.86, with page targets). Those correspond to the two running Blooms' `httpPort + 2`
(8089+2, 8092+2), so §4.3's arithmetic holds for current builds and the discovery fallback works.

**Spike bug to carry into Phase 1:** the probe reports ports globally instead of attributing each to
its parent Bloom pid. The WMI query already selects `ParentProcessId`; Phase 1 must filter on it, or
a machine with two Blooms will hand the Doctor the wrong port.

## 5. CONFIRMED — a cheap read-only probe set that works on any Bloom

All of these worked with no cooperation, no admin, and nothing that perturbs the target: top-level
window enumeration with visible/enabled/class/title (which will expose hidden modal dialogs and, by
its emptiness, state 3), per-thread CPU deltas over a sampling window (spin vs deadlock),
`CheckRemoteDebuggerPresent`, module enumeration filtered to unexplained DLLs, and
`IsWow64Process2` for architecture.

## 6. CONFIRMED THE DANGER — a suspending attach can strand Bloom permanently, so we are dropping it

The plan's §4.1 said the OS does not undo ClrMD's suspension if the reader dies, and required a
resume guarantee. Measured, and it is worse than "requires care":

1. `DataTarget.AttachToProcess(pid, suspend: true)` — stub stops responding, as expected.
2. Probe **hard-killed** mid-attach (`TerminateProcess`).
3. Stub checked at +1 s, +4 s, +10 s, +20 s: **alive, still not responding, every time.**
4. A second *clean* attach-and-dispose afterwards **did not** revive it either — as the suspend-count
   semantics predict, since our new attach increments the count and its dispose only decrements what
   it added.

**There is no recovery short of killing Bloom.** A Doctor crash during diagnosis would convert a
recoverable hang into an unrecoverable one — the single worst thing this tool could do.

**Design change: remove the suspending attach entirely.** Not "guard it with a child process" as §4.1
proposed — delete it. Finding 1 means we do not need it, and there is a safe fallback (next finding).
The principle worth keeping: **prefer mechanisms whose failure cannot leave the target suspended.**
`WriteDump` qualifies inherently, because the *target's own runtime* does the work — if we die, it
simply finishes or abandons on its own.

## 7. CONFIRMED — a non-suspending attach is a safe, fast fallback

`DataTarget.AttachToProcess(pid, suspend: false)` against the stub frozen in an STA managed wait:
walked all 3 managed threads with stacks in **197 ms**, correctly showing
`Monitor.ObjWait` / `Monitor.Wait` — the true blocking location — and the stub stayed alive and
untouched.

So the ladder becomes, in order:

1. `DiagnosticsClient.WriteDump(Normal)` — a dump artifact *and* stacks; the target does the work.
2. `AttachToProcess(suspend: false)` — stacks only, ~200 ms, cannot strand the target.
3. **Never** `suspend: true`.

If neither works we report the OS-level evidence and say plainly that managed stacks were
unavailable. That is a much better failure than a bricked Bloom.

## 8. CONFIRMED — the clean-exit proof behaves exactly as §3.5 requires

Added a `ProcessExit` handler to the stub that writes a tiny proof file, then measured every exit
path. The middle column is the one §3.5 depends on:

| How it ended | Exit code | Proof left? |
| --- | --- | --- |
| Clean quit (`Application.Exit`) | `0` | **yes** — `source=ProcessExit shutdownPhase=1` |
| `Environment.FailFast` | `0x80131623` | **no** |
| Unhandled exception | `0xE0434352` | **no** |
| Hard kill — Task Manager, or a debugger stop | `-1` (`0xFFFFFFFF`) | **no** |
| Zombie: window closed, foreground thread alive | still running, **no window handle** | no |

`ProcessExit` fires for the orderly exit and for nothing else. §3.5's inversion — report any exit that
leaves no proof — is therefore implementable exactly as written, the phase counter comes along for
free, and the exit codes the plan guessed at are confirmed (they were recalled, not tested; both were
right). The zombie row also confirms state 3 is detectable from outside: process alive, window handle
gone.

## 9. CONFIRMED, and the naive alternative is provably wrong — mapping logs to pids

Matching each log's opening `App Launched with [exe]` line against every Bloom's start time and exe
path, run against the real Blooms on this machine: `Log.txt` → pid 58460, correctly.

**And it caught the trap §4.4 warned about, in the wild.** The most-recently-*modified* log on this
machine (`Log-tmpkkmwaa.txt`, 17:16) belongs to a *different* Bloom — one from another worktree — while
the log actually belonging to the live pid (`Log.txt`) was modified nearly an hour earlier. "Newest
file wins" would have attached the wrong log to the report. No handle enumeration needed; this
heuristic replaces it.

Bonus: that line carries the **whole command line**, including `--automation`, `--label` and
`--vite-port`. That is exactly what §3.3 needs to recognise automation and headless runs and not
report them.

## 10. Incidental but reassuring — debugger detection fired on a real Bloom

`CheckRemoteDebuggerPresent` returned **True** for the developer's running Bloom (pid 58460), which is
being debugged from Visual Studio. The §3.5 defence works against a real target, not just in theory —
and it is a reminder of how often a developer's Bloom is in exactly the state that must never be
reported.

## 11. Wait chains — nothing yet, as predicted

`GetThreadWaitChain` returned no chains worth printing for a healthy process or for one blocked in a
managed wait, consistent with §4.2's warning that WCT does not see `Monitor`/`SemaphoreSlim`/async
waits. Still to test against a genuine cross-thread deadlock, but the plan already treats WCT as a
bonus rather than a foundation.

---

## Still to do in this spike

- **Wait chains against a genuine cross-thread deadlock.** Low stakes: the plan already treats WCT as
  a bonus rather than a foundation, and findings 1 and 7 give us managed stacks either way.
- **A read-only pass over an *installed* Bloom (6.3/6.4)** rather than a dev build, to confirm two
  things we currently know only from reading the source: that 6.3 really does listen on the hardcoded
  CDP port 9222, and that channel detection from the installed path (`…/Bloom/current/Bloom.dll` ⇒
  `Release`) behaves. Deferred deliberately — launching the developer's installed Bloom opens their
  real collection, so it wants doing at a moment that suits them.
- **Ending a zombie** (§3.6) — detection is confirmed (finding 8); the kill-and-verify-the-token-frees
  half is not yet exercised.

## Carry-over notes for Phase 1

- Attribute each CDP port to its **parent** Bloom pid (finding 4).
- Sample `CheckRemoteDebuggerPresent` while the target lives and keep it sticky (finding 10 shows how
  routinely a developer Bloom is under a debugger).
- Prefer `SendMessageTimeout` over `IsHungAppWindow` as the sampled signal (finding 3).
- Never call `AttachToProcess` with `suspend: true` (finding 6). If someone adds it later, the review
  question is "what happens to Bloom if this process is killed on the next line?"

## YouTrack capability findings (done, §5 depends on these)

Tested against the real tracker with the shipped `auto_report_creator` token, using project **AUT**
("Unit testing", id `77-2`); the test card was created and **deleted** afterwards, leaving nothing
behind.

- **Identity works:** the token authenticates as `auto_report_creator`.
- **It can search** — `GET /issues?query=…` returns results, including finding an issue by a
  fingerprint string in its *description*. So §5.2's server-side dedupe (comment on the existing card
  instead of filing a new one) is available, not just a hoped-for enhancement.
- **It can attach files**, and the default visibility is `UnlimitedVisibility`.
- **It can restrict an attachment to the Developers group** (`{"$type":"LimitedVisibility",
  "permittedGroups":[{"id":"25-3"}]}` — the response confirms group `25-3` = "Developers"). D2's
  privacy requirement is therefore implementable exactly as decided.
- **It cannot enumerate groups** (`admin/groups` → 404), so the group id stays hardcoded, as Bloom
  already does.
- **It can delete AUT issues**, which makes automated end-to-end tests self-cleaning.
