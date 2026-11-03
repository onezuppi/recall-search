# Plan: fast recurring scan

Goal: a pass over unchanged folders must not walk every file. Baseline: 14 min 14 s for 205,974 indexable files
(`docs/benchmarks/2026-11-plan1.md`, pass 2).

## Options ledger

| Option | Needs admin | Catches in-place edits while agent was off | Verdict |
|---|---|---|---|
| NTFS USN change journal (`FSCTL_QUERY_USN_JOURNAL`, `FSCTL_READ_USN_JOURNAL`) | Yes. Opening `\\.\C:` as a normal user fails with error 5 (checked on this machine, non-admin shell). | Yes, exact | Rejected as default. It would need an elevated helper or a service, which adds an installer, an IPC surface and a privilege boundary. NTFS only, per volume. |
| Elevated helper that only reads the journal | Yes, once, at install | Yes | **Built as an opt-in addition** (see "Change-journal service"). Recommended: it is the only exact way to see in-place overwrites. |
| Windows Search index (`SystemIndex`) | No | Only inside its own indexed folders | Not built. It covers Documents, Desktop and the profile, not arbitrary roots such as `C:\projects`. It has no journal-gap signal, so it cannot replace a full walk. |
| Persist directory mtime, relist only changed directories | No | No (see below) | **Chosen.** |
| Rely on watchers only, no startup scan | No | No | Rejected. Anything changed while the agent was off is lost. |

Directory last-write time changes when an entry is added, removed or renamed in that directory. It does not change
when an existing file is overwritten in place. Save-by-rename (Office, most editors) is caught. A plain in-place
overwrite while the agent was off is not caught by a fast pass. This is bounded by a full scan.

## Design

- New tables in the index: `dirs(path, root, mtime)` and `meta(key, value)`.
- `DirectoryMemory` (Core/Scan): the stored directory mtimes of one root, with a children lookup.
- `Scanner.Scan(root, ct, memory)`: for each directory, read its mtime (free for children of a listed
  directory, one stat for children of a skipped one). Equal to the stored mtime: do not list files, descend into the
  remembered child directories. Different or unknown: list as before. The mtime is read before listing, so a change
  during listing is seen next pass.
- Ignore scope (`.gitignore`) is computed lazily, only for directories that are actually listed.
- `ChangeSet.Diff(..., unchangedDirs)`: a file counts as removed only when its directory was listed (or gone).
- `RootScanPlanner` (Core/Scan): picks full or fast per root and commits directory state after the pass.
  Full is used when the mode is `Full`, no `full:<root>` stamp exists, the policy fingerprint (enabled groups,
  global ignore patterns, size limits) differs from the stamp, or the last full scan is older than the interval
  (default 7 days).
- State is committed only after the files of the pass were processed. Directories that contain a transiently
  skipped file (locked, IO error) are forgotten so the next fast pass relists them.
- `IndexRunner.RunAsync(..., ScanMode mode = ScanMode.Full)`. The default stays Full, so existing callers and tests
  keep their behaviour. `AgentHost` passes `Auto` for the startup pass and `Full` for watcher-overflow rescans.
- `Recall.Agent --once --fast` exposes the fast mode for benchmarks.

## Steps (test first)

1. `DirectoryMemory` + `Scanner` fast walk: tests in `IncrementalScanTests`.
2. `ChangeSet.Diff` with unchanged directories.
3. Store tables and `RootScanPlanner` (full triggers, commit, retry dirs).
4. `IndexRunner` and `AgentHost` wiring, `--fast`.
5. Benchmark before and after, `docs/benchmarks/2026-11-fast-scan.md`.

## Concurrent walk

`Scanner(policy, threads)` lists directories from a shared queue with N workers and hands files to the caller through a
blocking collection. Statistics are updated under one lock per directory. Measured on 200k folders: 1 thread 4 min 39 s,
8 threads 1 min 18 s, 16 threads 45 s. Default `clamp(cores, 4, 16)`; `--scan-threads N` overrides.

## Change-journal service (optional, needs administrator once)

- `Recall.UsnService` (scheduled task, SYSTEM, installed to `%ProgramFiles%\RecallSearch\Journal` by
  `scripts/install-usn-service.ps1`) reads the NTFS change journal and answers on the pipe `RecallSearch.Journal`.
  It is stateless: the agent keeps one cursor (journal id and next USN) per root in the index (`meta` table).
- Request: volume, cursor, roots, known extensions. Answer: `ok` with the files created, renamed in or written to under
  the roots, or `no-cursor` / `gap` / `unavailable`. Folder changes and deletions are not listed: the directory-time pass
  already finds them.
- Trust: the service checks, under the caller's identity, that the caller can list each root, and returns only those.
  The agent connects only to a server whose image is the installed exe path (administrator-only folder).
- Startup in the agent: `no-cursor` or `gap` forces one full walk for that root and stores the cursor taken before it.
  `ok` means the listed files are indexed, the directory pass runs, and the weekly full walk is skipped for that root.
  No service: behaviour is exactly the unprivileged design above.
- Limits: needs NTFS and a journal that has not wrapped past the cursor (otherwise `gap`, one full walk). Cannot be
  run in the unprivileged test run; `NativeJournalTests` runs only from an elevated shell.
