# Search on a large index, 2026-11-05

The measurement uses the `tools/Recall.SearchBench` tool on a synthetic index: 20 000 files, one segment of 180 words each, a vocabulary of 20 000 words, and 20 000 vectors of 384 numbers. Machine: AMD Ryzen AI 9 HX 370, Release build, best of three runs after warm-up. The numbers vary by 10-20 ms. There are no measurements on hundreds of thousands of files. Index generation takes about 17 ms per file, so a hundred thousand files take almost half an hour. Growth on larger indexes is estimated with the formulas below, not measured.

Run:

```cmd
Recall.SearchBench --db <new file> --files 20000 --dim 384
Recall.SearchBench --db <file> --run
```

## Result

| Query | Before | After |
|---|---|---|
| A two-letter word found in a quarter of the texts | 994 ms | about 100 ms |
| A word of three or more letters found in almost every file (FTS) | 450 ms | 109 ms |
| Scan of all vectors (search by meaning) | 105-127 ms | 1 ms while the copy is in memory (first search after changes 105-127 ms) |
| Rare words, phrases, `in:` and `type:` filters, file name | 9-25 ms | unchanged |

## What changed

- **Short words (1-2 letters).** Files are scanned from newest to oldest by the `files_mtime` index. The search stops at the needed number of matches. A fast `instr` over case variants filters the rows first, and only then the managed function runs. For a word that does not exist at all, the scan still covers the whole database (about 1.5 us per segment in SQLite).
- **Frequent words in FTS.** The snippet column in the query is empty. The engine replaces it anyway with a text window for each returned result. Without the `in:`, `type:`, `after:`, and `before:` filters, segments are first selected from the FTS table by bm25. Joins with files run only for the best ones.
- **Vectors.** A copy of all vectors of one space lies in one array. It is rebuilt after any change to the vectors. It is dropped after 2 minutes without a search. It is not created if it is larger than 512 MB (then search works as before, from SQLite).

## Memory

The vector copy takes `number of vectors x dimension x 4 bytes`. 40 000 vectors of 384 numbers are about 61 MB. 100 000 are 154 MB. 500 000 are 768 MB (above the limit, so no cache is created). For Gemma 2 (768 numbers) it is twice as much.

## Limits

- A word found in every document is still sorted fully by bm25: about 5 us per match.
- File names are searched by scanning paths.
- The cache reset by counter works while the same process that answers search also writes the vectors (the agent). Another process that writes to the database will not reset the cache.
