# Plan: fast recurring scan

Goal: a pass over unchanged folders must not walk every file. Baseline: 14 min 14 s for 205,974 indexable files
(`docs/benchmarks/2026-11-plan1.md`, pass 2).

## Options ledger

| Option | Needs admin | Catches in-place edits while agent was off | Verdict |
|---|---|---|---|
| NTFS USN change journal (`FSCTL_QUERY_USN_JOURNAL`, `FSCTL_READ_USN_JOURNAL`) | Yes. Opening `\\.\C:` as a normal user fails with error 5 (checked on this machine, non-admin shell). | Yes, exact | Rejected as default. It would need an elevated helper or a service, which adds an installer, an IPC surface and a privilege boundary. NTFS only, per volume. |
| Elevated helper that only reads the journal | Yes, once, at install | Yes | Possible later as an opt-in accelerator. Not part of this change. |
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
