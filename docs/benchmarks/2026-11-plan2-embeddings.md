# Plan 2 benchmark: text embeddings on CPU, 2026-11-04

Measured on the developer machine only: AMD Ryzen AI 9 HX 370 (12 cores, 24 threads), 31 GB RAM, Windows 11, Release
build, ONNX Runtime CPU provider, 12 intra-op threads (half of the logical processors, the default of `EmbedderHost`).
Model: `intfloat/multilingual-e5-small`, int8 file `onnx/model_qint8_avx512_vnni.onnx`, 384 dimensions.

Corpus: Downloads, `C:\odata` and a copy of this repository's `docs` folder. 341 files added, 506 stored text segments
(1.2 M characters), of which 74 text, 464 PDF, 83 image-OCR and 45 Office rows when counted per file x segment. After
chunking (1000 characters, 100 overlap): 1263 chunks. This is a small corpus. Do not extrapolate to `C:\projects`.

Command: `Recall.Agent --once --db <scratch db> --root ...` twice. The first run has no model and builds the text index. The
second adds `--embedding-model multilingual-e5-small`, so it measures the embedding stage alone.

## Indexing (the embedding stage)

| Item | Value |
|---|---|
| Chunks embedded | 1263 (506 segments) |
| Busy time of the stage | 57.2 s |
| Throughput | about 22 chunks per second (about 21 k characters per second) |
| Peak working set of the process | 1190 MB |
| Working set after the model was unloaded and GC ran | 105 MB |
| Same run with `EnableCpuMemArena = false` | 94 s, peak 765 MB (slower, not adopted) |
| Run with nothing pending (re-index from cache) | 0.06 s, model not loaded, peak 54 MB |

The peak includes batches of 16 chunks of up to 512 tokens and the ONNX Runtime arena. The text pass before it had its
own peak of 2639 MB (extraction and OCR, not related to this plan).

## Model latency (single process, warm file cache)

| Item | Value |
|---|---|
| Cold model load (session creation) | 1.9 s the first time, about 0.7 s when the files are cached by the OS |
| 1 chunk of 1000 characters | 27 ms (median of 5) |
| 16 chunks of 1000 characters | 179 ms (median of 5), about 11 ms per chunk |
| One query embedding | 4 ms |

Single-chunk latency and batch throughput are reported separately on purpose.

## Search latency (hybrid, 10 results, `SearchEngine` with the model host)

| Index | Model state | Time |
|---|---|---|
| 1632 vector rows | cold (unload delay 0, every query loads the model) | 730 ms per query, 1.5 s the first time |
| 1632 vector rows | warm (model kept 60 s after the last use) | 20 ms |
| 1632 vector rows | text only (no embedder) | 3 ms |
| about 98 k joined vector rows (the same vectors repeated 60 times) | warm | 790 ms |

The vector scan goes through SQLite joins, about 8 microseconds per row. Extrapolation, not a measurement: the search
passes 300 ms at about 40 k vector rows. A realistic index of this machine will be larger, so the next plan needs an
in-memory vector table or a scan that skips the joins. Embeddings are off by default for this reason.

## Similarity floor

E5 int8 scores are compressed into a narrow, high range. 18 queries (15 with a known right file, 3 nonsense) over the
corpus above:

- the best right file scored 0.836 to 0.897 (ranks: 8 queries rank 1, 4 queries rank 2, 3 queries rank 29 or worse);
- the top hit of the three nonsense queries scored 0.816, 0.834 and 0.840;
- a floor of 0.84 drops two of three nonsense top hits and keeps every right file that ranked in the top 2.

`ModelSpec.MinSimilarity` is set to 0.84 for this model. With 18 queries this is a rough calibration, not a tuned value.
Worst cases: "salary slip" (English, Russian payroll document) ranked the right file 36th with 0.786, and "срок договора и размер платы"
ranked an English contract 29th. Cross-language matching works for some pairs ("coffee receipt" found the Russian-English
receipt, "hotel guest platform" found the Russian project description) and is weak for others.

## Other notes

- Batch and single-text vectors differ slightly (cosine 0.9966 in the test) because dynamic int8 quantization scales
  activations per batch. Chunks are sorted by length before batching to keep the padding small.
- Not measured: GPU and NPU (not implemented in this slice), image vectors, battery draw.
