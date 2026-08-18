# Bloom Freeze Doctor

> **Very early work in progress.** Nothing here is usable yet. At the time of writing this repository
> contains a design document and a throwaway spike used to test whether that design is workable —
> there is no installer, no released build, and no Doctor to run. Please do not file issues against it
> as though it were a product; if you have wandered in from outside the Bloom team, come back later.

A small Windows companion app for [Bloom](https://github.com/BloomBooks/BloomDesktop) whose only job
is to be watching at the moment Bloom stops working, gather everything that can still be learned, and
report it.

Bloom users tell us "Bloom froze", and we have almost nothing to work with: the problem report they
send is written after they killed it, so it describes a healthy new process. Three quite different
failures arrive looking identical, and none of them currently leaves usable evidence behind:

1. **Bloom's UI stops responding** for longer than a slow network explains.
2. **Bloom exits without managing to report anything** — no Sentry event, no tracker card.
3. **Bloom's UI is gone but the process is still running**, so the user cannot start Bloom again.

The Freeze Doctor watches for all three. When one happens it collects the managed stacks of every
thread, a small dump, wait chains, per-thread CPU, the loaded modules, the window list, whatever the
WebView2 debugging port will tell it, Bloom's log, the Windows Event Log and Error Reporting entries,
and basic system state — then files a report itself, queuing it on disk if the network is down (which,
for many of our users, it often is).

## Repository layout

| Path | What it is |
| --- | --- |
| `PLAN.md` | The design, and the record of every decision behind it |
| `docs/SPIKE-FINDINGS.md` | What we have actually measured, as opposed to assumed |
| `PROGRESS.md` | Running log of where the work stands and how to pick it up |
| `spike/FreezeStub` | A stand-in for Bloom that freezes or crashes on command, so we never have to break a real Bloom to test detection |
| `spike/Probe` | A console tool that answers the design's open technical questions against a running process |

## Status

Phase 0 (spike) is in progress. Two findings so far have already shaped the design, and both are
written up in `docs/SPIKE-FINDINGS.md`:

- The .NET runtime's own diagnostics pipe will write a small, fully analysable dump **even while the
  target's UI thread is wedged** — so a report can name the exact blocking call.
- Watching from outside cannot detect a UI thread blocked in an STA managed wait: Windows reports such
  a window as perfectly healthy. Catching that case requires a heartbeat published by Bloom itself.

## Licence

MIT — see [LICENSE](LICENSE). Copyright © 2026 SIL Global.
