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

## 6. Wait chains — nothing yet, as predicted

`GetThreadWaitChain` returned no chains worth printing for a healthy process or for one blocked in a
managed wait, consistent with §4.2's warning that WCT does not see `Monitor`/`SemaphoreSlim`/async
waits. Still to test against a genuine cross-thread deadlock, but the plan already treats WCT as a
bonus rather than a foundation.

---

## Still to do in this spike

- ClrMD live attach with `suspend: true`, **and the safety test**: kill the probe mid-suspend and see
  whether the stub ever resumes (§4.1's non-negotiable rule).
- Exit codes for `FailFast`, an unhandled throw, a clean exit, and a debugger stop (§3.4, §3.5).
- State 3 detection against the stub's `zombie` command, and ending it (§3.6).
- `--logmap` against the real Blooms: does matching the log's `App Launched with [exe]` line to
  process start time and exe path replace handle enumeration (§4.4)?
- Wait chains against a real deadlock.
- A read-only pass over an *installed* Bloom (6.3/6.4), not just dev builds.

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
