# BloomFreezeDoctor.Contract

The shared contract between [Bloom](https://github.com/BloomBooks/BloomDesktop) and the
[Bloom Freeze Doctor](https://github.com/BloomBooks/bloom-freeze-doctor) — a diagnostic tool that
watches Bloom and reports freezes, unreported crashes, and processes whose window has gone but
which are still running.

This package is **not generally useful on its own.** It exists so that one wire format has one
definition instead of two hand-maintained copies. It contains:

- **`DoctorChannel`** — a small fixed-layout page in shared memory, written by Bloom and read by the
  Doctor, carrying a UI-thread heartbeat and what Bloom believes it is doing. Shared memory rather
  than a request/response API because the Doctor has to be able to read it when Bloom is wedged, and
  a wedged Bloom cannot answer anything.
- **`DoctorSession`** — a small JSON file per Bloom run, holding the facts that must outlive the
  process: above all which log file that run is writing to, which a watcher cannot reliably work out
  from outside.
- **`DoctorSignals`** — the named events the two use to reach each other: the Doctor announcing that
  it is watching, asking a stuck Bloom to exit, and the handshake around dumping a dying one.

Both sides pin the layout by value in a test, so a change to it has to be made deliberately in both
repositories.

Licensed under the MIT licence. © SIL Global.
