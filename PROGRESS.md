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

**Next:** the parts that talk to the real world — a `BloomTargetWatcher` that produces
`TargetObservation`s from a live process (window probe, visible-window count, sticky debugger flag,
log/port discovery), then the gatherer and the report bundle, then the outbox, then the window.
