# Fast recurring scan benchmark, 2026-11-04

Measured on the developer machine only. The numbers describe this machine and these folders, not a general result.

Command: `Recall.Agent --once --db <scratch db> --root ...` (Release build, `--fast` where noted). Roots: `C:\projects`,
the user's Documents folder (OneDrive redirected), the Downloads folder and `C:\odata`. The index was empty before pass 1.
Other sessions were editing `C:\projects` during the run, so "no change" passes saw a few real edits.

| Pass | Wall time | Directories listed | Directories reused | Added / Changed / Removed |
|---|---|---|---|---|
| 1. Full, empty index | 30 min 15 s | 200,477 | 0 | 66,180 / 0 / 0 |
| 2. `--fast`, right after pass 1 | 31.9 s | 28 | 200,450 | 13 / 3 / 0 |
| 3. `--fast`, nothing changed | **6.2 s** | 0 | 200,478 | 0 / 0 / 0 |
| 4. Full, nothing changed (baseline) | **6 min 5 s** | 200,479 | 0 | 2 / 5 / 0 |

Pass 2 time is mostly the 16 files that changed during pass 1 (28 directories relisted, 16 hashed).

## Reading the numbers

- Fast no-change pass against the full no-change pass on the same index: 6.2 s against 6 min 5 s, about 59 times faster.
- The Plan 1 baseline was 14 min 14 s for the same folder types. The full pass here took 6 min 5 s, so the baseline moved
  with the state of the disk cache, the folder contents and background load. Compare only passes 3 and 4, which ran
  back to back. Against the Plan 1 figure the gain is larger, but that comparison is not like for like.
- Pass 1 file counts differ from Plan 1 (66,180 against 205,974 listed) because the folder contents changed. Do not compare them.

## Limits

A fast pass cannot see a file overwritten in place, because the directory time does not change. A full walk is forced
at least every 7 days, after any ignore-configuration change, and after a watcher overflow. Not measured: the share of
real edits that are in-place overwrites.
