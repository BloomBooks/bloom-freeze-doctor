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

1. **Where should this repo live, and when?** It is currently a local-only git repo at
   `C:\github\bloom-freeze-doctor`, so nothing here can be picked up on another machine yet. The card
   asks for a public `BloomBooks/bloom-freeze-doctor` (MIT, © SIL Global 2026). Creating a public repo
   under the org is an outward-facing step, so it is waiting on your say-so — including whether to
   start it **private** and flip it public at release.

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

**Next:** finish the spike — the suspend-safety test (kill the probe mid-suspend and see whether the
stub resumes), exit codes for each failure mode, state 3 detection and ending a zombie, log-to-pid
mapping against the real Blooms, and a read-only pass over an installed Bloom rather than a dev build.
