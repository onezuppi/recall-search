# Recall Search: plan 2, text embeddings on CPU

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Search by meaning over indexed text. Segments get vectors during indexing, and queries are ranked by FTS plus vector similarity.

**Architecture:** A new `Recall.Core/Embed` folder holds the model abstraction (`IEmbedder`), the model catalog, the Hugging Face downloader, the ONNX Runtime CPU embedder and a ref-counted loader (`EmbedderHost`) that keeps the model in memory only while someone uses it. Vectors live in an `embeddings` table next to `segments`. `IndexRunner` gets an embedding stage that fills missing vectors in batches after the files of a root are stored. `SearchEngine` fuses FTS hits and vector hits with weighted reciprocal rank fusion. Everything is off while `RecallConfig.EmbeddingModel` is null.

**Tech Stack:** .NET 10, Microsoft.ML.OnnxRuntime (CPU), Microsoft.ML.Tokenizers, System.Numerics.Tensors, Microsoft.Data.Sqlite, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-03-recall-search-design.md` (sections 7.1.1, 7.2, 7.3, 8). Roadmap ideas used: `docs/roadmap.md` ("model in memory only during indexing").

## Decisions

- **Model: `intfloat/multilingual-e5-small`**, revision `614241f622f53c4eeff9890bdc4f31cfecc418b3`, file `onnx/model_qint8_avx512_vnni.onnx` (118 MB, int8) and `onnx/sentencepiece.bpe.model` (5 MB). Why: MIT license; 118M parameters, 384 dimensions, so a vector is 1.5 KB; trained on 100 languages including Russian, so a Russian query finds an English document; ready ONNX file, no export step. EmbeddingGemma 2 (spec default) is 3 to 10 times larger per encoder and its text part is 768-dimensional; it stays a later catalog entry. `paraphrase-multilingual-MiniLM-L12-v2` is similar in size but is trained for sentence similarity, not for query-to-passage retrieval.
- E5 needs the prefixes `query: ` and `passage: ` and mean pooling over the attention mask, then L2 normalization. Max input 512 tokens.
- **Embedding space id** = `"{modelId}@{revision}"`, stored per vector (spec 7.3). A different model or revision makes old vectors invisible to search, and the stage re-embeds. Device is not part of the id in this slice because only CPU exists.
- **Chunking:** a segment can be a whole 2 MB text file. Each segment is split into chunks of about 1000 characters with 100 characters overlap, at most 16 chunks per segment (the first 16 k characters). Constants live in one place (`EmbedChunker`).
- **Where vectors are made:** one stage at the end of each root's processing (`ProcessAsync` caller), not inside per-file workers. It selects segments without a vector for the current space and embeds them in batches of 16 chunks. Reasons: batching across files, one code path for new files, retry after failures, model switch and indexes from plan 1.
- **Search:** vectors are scanned from SQLite per query (brute force, SIMD dot product, top 50 per file path). No in-memory cache in this slice. Revisit above about 200 k chunks (measured in Task 8).
- **Fusion:** score = `2.0 / (60 + rankFts) + 1.0 / (60 + rankVec)`. FTS weight 2 keeps exact text hits ahead of purely semantic hits, per spec section 8. File-name hits stay appended last, as today. Vector hits under a similarity floor are dropped; the floor is calibrated in Task 8 (E5 scores are compressed into a high range).
- **Failure rule:** if the model is not downloaded, search falls back to FTS only, and the stage is skipped with a log line. A hash mismatch deletes the bad file and throws.
- **Out of scope:** GPU and NPU (the factory throws `NotSupportedException` for them), image and audio vectors, the settings UI, the pipe protocol, `src/Recall.CmdPal`.

## Global Constraints

- Source files stay under about 200 lines. No hardcoded user or machine paths: models live under `RecallPaths.DataDir\models`.
- Never use the em dash or guillemets in code, comments, docs.
- `TreatWarningsAsErrors` is on. Existing tests must keep passing with embeddings off.
- Do not edit `src/Recall.Core/Ipc/*` or `src/Recall.CmdPal/*`.

## Review Focus

- Segment longer than 16 chunks: only the first 16 are embedded, no exception. (Task 4)
- Empty or whitespace-only text, or text of only emoji: no chunk, no vector, no crash. (Task 4)
- Two paths with the same content: vectors exist once. File removed: its vectors are deleted with its segments. (Task 2)
- Model changed in config: old vectors are ignored by search and new ones are created. (Tasks 2, 5)
- Model not downloaded, or download interrupted: search still returns FTS hits; a resumed download completes and passes the hash check. (Tasks 5, 6)
- Query with filters only (`type:pdf`): no query vector is computed. (Task 6)

---

## File Structure

| File | Responsibility |
|---|---|
| `src/Recall.Core/Embed/IEmbedder.cs` | `EmbedKind`, `IEmbedder`, `EmbeddingUnavailableException` |
| `src/Recall.Core/Embed/ModelCatalog.cs` | `ModelFile`, `ModelSpec`, `ModelCatalog` (pinned entries) |
| `src/Recall.Core/Embed/ModelDownloader.cs` | download with Range resume, SHA-256 check, atomic rename |
| `src/Recall.Core/Embed/E5Tokenizer.cs` | text to token ids with the XLM-R id mapping |
| `src/Recall.Core/Embed/OnnxEmbedder.cs` | ONNX Runtime CPU session, pooling, normalization |
| `src/Recall.Core/Embed/EmbedderHost.cs` | lazy load, ref-count, delayed unload |
| `src/Recall.Core/Embed/EmbedChunker.cs` | segment text to chunks |
| `src/Recall.Core/Index/EmbeddingStage.cs` | pending segments to vectors, batch loop |
| `src/Recall.Core/Index/IndexSchema.cs` | `embeddings` table and delete trigger |
| `src/Recall.Core/Index/IndexStore.cs` | `PendingSegments`, `AddEmbeddings` |
| `src/Recall.Core/Search/VectorSearch.cs` | query vector, scan, top-K |
| `src/Recall.Core/Search/RankFusion.cs` | weighted RRF |
| `src/Recall.Core/Search/SearchEngine.cs` | optional embedder, hybrid merge |
| `src/Recall.Core/Config/RecallConfig.cs` | `EmbeddingModel`, `EmbeddingDevice` |
| `src/Recall.Agent/*` | wiring only |
| `tests/Recall.Core.Tests/Embed*.cs`, `Hybrid*.cs`, `Support/FakeEmbedder.cs` | tests |

---

### Task 1: Abstractions and catalog

**Files:**
- Create: `src/Recall.Core/Embed/IEmbedder.cs`, `src/Recall.Core/Embed/ModelCatalog.cs`
- Modify: `src/Recall.Core/Recall.Core.csproj` (add `Microsoft.ML.OnnxRuntime` 1.*, `Microsoft.ML.Tokenizers` 2.*, `System.Numerics.Tensors` 10.*)
- Test: `tests/Recall.Core.Tests/EmbedCatalogTests.cs`

**Interfaces:**
- Produces:
  - `enum EmbedKind { Passage, Query }`
  - `interface IEmbedder : IDisposable { string Space { get; } int Dimensions { get; } float[][] Embed(IReadOnlyList<string> texts, EmbedKind kind); }` Vectors are L2-normalized.
  - `sealed class EmbeddingUnavailableException(string message, Exception? inner = null) : Exception`
  - `sealed record ModelFile(string RemotePath, string Sha256, long Size)`
  - `sealed record ModelSpec(string Id, string Repo, string Revision, int Dimensions, int MaxTokens, IReadOnlyList<ModelFile> Files) { string Space => $"{Id}@{Revision}"; }`
  - `static class ModelCatalog { IReadOnlyList<ModelSpec> All; ModelSpec? Find(string id); }`
  - `ModelSpec.Files[0]` is the ONNX file, `Files[1]` is the SentencePiece model.

- [ ] **Step 1: Write the failing test** `Catalog_pins_e5_small_with_hashes`: `ModelCatalog.Find("multilingual-e5-small")` is not null, `Dimensions == 384`, `Revision == "614241f622f53c4eeff9890bdc4f31cfecc418b3"`, `Space == "multilingual-e5-small@614241f622f53c4eeff9890bdc4f31cfecc418b3"`, two files whose `Sha256` are 64 hex characters, ONNX size `118346824`. Second test `Find_is_case_insensitive_and_null_for_unknown`.
- [ ] **Step 2: Run** `dotnet test tests/Recall.Core.Tests --filter EmbedCatalog` and expect a compile failure.
- [ ] **Step 3: Implement** the types above. Pin these values:
  - ONNX: `onnx/model_qint8_avx512_vnni.onnx`, sha256 `dd476dd0c2514e9b9be83aeb3853fac0763e0bdf4a71645407587d77c48a2d88`, size 118346824.
  - SentencePiece: `onnx/sentencepiece.bpe.model`, sha256 `cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865`, size 5069051.
  - Repo `intfloat/multilingual-e5-small`, `MaxTokens` 512.
- [ ] **Step 4: Run the tests**, expect PASS. Run `dotnet build` for the whole solution to confirm the packages restore.
- [ ] **Step 5: Commit** `feat(embed): embedder abstraction and pinned model catalog`.

### Task 2: Embedding store

**Files:**
- Modify: `src/Recall.Core/Index/IndexSchema.cs`, `src/Recall.Core/Index/IndexStore.cs`
- Create: `src/Recall.Core/Index/PendingSegment.cs` (record)
- Test: `tests/Recall.Core.Tests/EmbeddingStoreTests.cs`

**Interfaces:**
- Produces:
  - `sealed record PendingSegment(long SegmentId, string Text)`
  - `IReadOnlyList<PendingSegment> IndexStore.PendingSegments(string space, int limit)`: segments of contents that have at least one file with status ok and no row in `embeddings` for `space`, ordered by segment id. Text is cut to the first 20 000 characters.
  - `void IndexStore.AddEmbeddings(string space, IReadOnlyList<(long SegmentId, int Chunk, float[] Vector)> rows)`: one transaction. A segment with no chunks (blank text) gets a marker row so it is not pending again: `chunk = -1` with an empty blob.
  - Table: `embeddings(segment_id INTEGER NOT NULL, space TEXT NOT NULL, chunk INTEGER NOT NULL, vec BLOB NOT NULL, PRIMARY KEY(segment_id, space, chunk)) WITHOUT ROWID` and trigger `segments_ad_emb AFTER DELETE ON segments` deleting its rows. Vectors are stored as little-endian float32.

- [ ] **Step 1: Write failing tests:** `Pending_lists_segments_without_vectors`; `Added_vectors_are_not_pending_again` (also for the blank-marker row); `Other_space_stays_pending` (add under space A, pending for B still lists it); `Removing_the_file_deletes_vectors` (count rows in `embeddings` is 0 after `RemoveFiles`); `Shared_content_has_one_set_of_vectors` (two files, same hash, `PendingSegments` returns the segment once).
- [ ] **Step 2: Run** and expect FAIL (members missing).
- [ ] **Step 3: Implement** the DDL and the two methods under the existing `_gate` lock. Use `MemoryMarshal.AsBytes` for the blob.
- [ ] **Step 4: Run** the new tests and the full `IndexStoreTests`: PASS.
- [ ] **Step 5: Commit** `feat(index): embeddings table with pending-segment query`.

### Task 3: Tokenizer and ONNX CPU embedder

**Files:**
- Create: `src/Recall.Core/Embed/E5Tokenizer.cs`, `src/Recall.Core/Embed/OnnxEmbedder.cs`
- Test: `tests/Recall.Core.Tests/EmbedTokenizerTests.cs`, `tests/Recall.Core.Tests/EmbedOnnxTests.cs`, `tests/Recall.Core.Tests/Support/ModelFixture.cs`

**Interfaces:**
- Consumes: `ModelSpec`, `IEmbedder` (Task 1).
- Produces:
  - `sealed class E5Tokenizer(string sentencePiecePath, int maxTokens) { int[] Encode(string text); }` Returns ids including `<s>` (0) first and `</s>` (2) last, truncated to `maxTokens`.
  - `sealed class OnnxEmbedder : IEmbedder { OnnxEmbedder(ModelSpec spec, string modelDir, int threads); }`. Input names `input_ids`, `attention_mask` (check the model's input list at load; add `token_type_ids` zeros only if present). Output `last_hidden_state`, mean pooled over the mask, L2 normalized. Prefix `query: ` or `passage: ` is added inside `Embed`. Batches pad to the longest sequence with id 1. Missing model files throw `EmbeddingUnavailableException`.
  - `ModelFixture` (test support): resolves `RecallPaths.DataDir\models\<id>\<revision>`; `Available` is false when the files are missing, so model tests use `[SkippableFact]` and `Skip.IfNot(fixture.Available, ...)`.

- [ ] **Step 1: Download the model once for local tests** with the Task 4 downloader is not yet available, so for now use `curl` into the fixture path (`%LOCALAPPDATA%\RecallSearch\models\multilingual-e5-small\<revision>\`). Verify sha256 with `sha256sum`.
- [ ] **Step 2: Write failing tokenizer test** `Ids_match_reference_tokenizer` with the ids from Hugging Face `tokenizers` 0.22.2 on the pinned `tokenizer.json`:
  - `"query: Привет, мир"` gives `[0, 41, 1294, 12, 1813, 18454, 4, 11373, 2]`
  - `"passage: Hello world"` gives `[0, 46692, 12, 35378, 8999, 2]`
  - `"passage: Счёт № 42 от 03.11.2026 😀"` gives `[0, 46692, 12, 589, 96732, 438, 4828, 183, 5098, 39618, 4046, 21119, 2]`
  - plus `Truncates_to_max_tokens` (3000 words, max 512, length is 512 and last id is 2).
  The tokenizer reads `sentencepiece.bpe.model` from the fixture. Skip when not available.
- [ ] **Step 3: Run** and expect FAIL.
- [ ] **Step 4: Implement `E5Tokenizer`.** Spike first: load the model with `SentencePieceTokenizer.Create(stream, addBeginOfSentence: false, addEndOfSentence: false)` and compare ids. The fairseq mapping is `<s>`=0, `<pad>`=1, `</s>`=2, `<unk>`=3 and every other SentencePiece id is shifted by +1. If the library output does not reproduce the reference ids exactly, stop and report: do not add a hand-written Unigram decoder without agreement.
- [ ] **Step 5: Write failing embedder tests** (skippable): `Vectors_are_normalized_with_384_dimensions`; `Russian_query_is_closer_to_matching_passage` (query "как оплатить счёт за электричество" is closer to passage "Квитанция за свет: оплата до 10 числа" than to "Рецепт борща со сметаной", and also closer to the English passage "Electricity bill payment is due on the 10th" than to "Recipe for borscht with sour cream"); `Batch_equals_single` (cosine of batch vs single result at least 0.999); `Missing_files_throw_unavailable` (temp empty dir, no skip needed).
- [ ] **Step 6: Implement `OnnxEmbedder`:** `SessionOptions` with `IntraOpNumThreads = threads`, `GraphOptimizationLevel.ORT_ENABLE_ALL`, CPU provider only. `Dispose` releases the session.
- [ ] **Step 7: Run** the tests: PASS (or skipped if the model is missing, which must be reported).
- [ ] **Step 8: Commit** `feat(embed): E5 tokenizer and ONNX CPU embedder`.

### Task 4: Chunker and downloader

**Files:**
- Create: `src/Recall.Core/Embed/EmbedChunker.cs`, `src/Recall.Core/Embed/ModelDownloader.cs`
- Test: `tests/Recall.Core.Tests/EmbedChunkerTests.cs`, `tests/Recall.Core.Tests/ModelDownloaderTests.cs`

**Interfaces:**
- Produces:
  - `static class EmbedChunker { const int ChunkChars = 1000, Overlap = 100, MaxChunks = 16; static IReadOnlyList<string> Split(string text); }` Collapses runs of whitespace, trims, splits on a space near the boundary when possible, returns an empty list for blank text.
  - `sealed class ModelDownloader(HttpClient http) { Task<string> EnsureAsync(ModelSpec spec, string modelsRoot, IProgress<double>? progress, CancellationToken ct); static string DirectoryOf(string modelsRoot, ModelSpec spec); }` Files go to `<root>/<id>/<revision>/<file name>`. URL: `https://huggingface.co/{Repo}/resolve/{Revision}/{RemotePath}`. A file that already exists with the right size and hash is skipped. Downloads go to `<name>.part`, resume with `Range: bytes=N-` (server answers 206; on 200 restart from zero), hash is computed over the finished file, a mismatch deletes the `.part` and throws `InvalidDataException`, success renames atomically. Returns the directory.

- [ ] **Step 1: Write failing chunker tests:** `Blank_and_emoji_only_text_gives_zero_chunks_or_one` (blank gives 0; the string "😀😀" gives 1 chunk, no crash); `Short_text_is_one_chunk`; `Long_text_is_capped_at_16_chunks` (100 000 characters gives exactly 16); `Chunks_overlap` (the tail of chunk 0 appears at the head of chunk 1); `No_chunk_exceeds_limit_plus_margin`.
- [ ] **Step 2: Implement** `Split`, run: PASS.
- [ ] **Step 3: Write failing downloader tests** with a fake `HttpMessageHandler` serving a 10 KB payload and a hand-made `ModelSpec` (real sha256 of the payload): `Downloads_and_verifies`; `Hash_mismatch_deletes_part_and_throws`; `Resumes_from_part_file` (pre-create `.part` with the first 4 KB, assert the request carries `Range: bytes=4096-` and the final file is correct); `Existing_valid_file_is_not_requested_again` (handler call count 0); `Server_ignoring_range_restarts` (handler answers 200 with the full body).
- [ ] **Step 4: Implement** `ModelDownloader`, run: PASS.
- [ ] **Step 5: Commit** `feat(embed): chunker and resumable hash-checked model downloader`.

### Task 5: Config, host and embedding stage

**Files:**
- Create: `src/Recall.Core/Embed/EmbedderHost.cs`, `src/Recall.Core/Index/EmbeddingStage.cs`, `tests/Recall.Core.Tests/Support/FakeEmbedder.cs`
- Modify: `src/Recall.Core/Config/RecallConfig.cs`, `src/Recall.Core/Index/IndexRunner.cs`
- Test: `tests/Recall.Core.Tests/EmbedHostTests.cs`, `tests/Recall.Core.Tests/IndexRunnerEmbeddingTests.cs`, extend `ConfigTests.cs`

**Interfaces:**
- Produces:
  - `enum EmbeddingDevice { Cpu, Gpu, Npu }` in `Recall.Core.Config`. `RecallConfig.EmbeddingModel` (`string?`, default null = off) and `RecallConfig.EmbeddingDevice` (`EmbeddingDevice`, default `Cpu`) as `init` properties. JSON stores the device as a string (add `JsonStringEnumConverter` to the existing options).
  - `sealed class EmbedderHost(Func<IEmbedder> factory, TimeSpan unloadDelay) : IDisposable { Lease Acquire(); }` and `readonly struct Lease : IDisposable { IEmbedder Embedder { get; } }`. The first `Acquire` calls the factory (thread-safe, one load). The last `Dispose` schedules the unload after `unloadDelay`; a new `Acquire` before it fires cancels it. `TimeSpan.Zero` unloads at once. Property `bool IsLoaded`. Static `EmbedderHost.Create(RecallConfig, string modelsRoot)` returns null when `EmbeddingModel` is null, and a host whose factory throws `NotSupportedException` for Gpu and Npu and `EmbeddingUnavailableException` for an unknown id or missing files.
  - `FakeEmbedder` (tests): deterministic 8-dimensional hashing vectors, so equal text gives equal vectors; counts calls; `Space` is settable.
  - `sealed class EmbeddingStage(IndexStore store, EmbedderHost host)` with `Task<EmbedStats> RunAsync(CancellationToken ct)` where `sealed record EmbedStats(long Segments, long Chunks, TimeSpan Busy)`. Loop: `PendingSegments(space, 64)` until empty, chunk, embed in batches of 16 chunks, `AddEmbeddings`. The space comes from a lease taken only when something is pending, so an empty queue never loads the model.
  - `IndexRunner` gets an optional last constructor parameter `EmbedderHost? embedder = null`. `RunReport` gets two new trailing members with defaults: `long EmbeddedChunks = 0`, `TimeSpan Embed = default`. The stage runs once at the end of `RunAsync` (after all roots) and at the end of `IndexFilesAsync`. `EmbeddingUnavailableException` is caught, counted in neither Failed nor Skipped, and ignored; other exceptions propagate.

- [ ] **Step 1: Write failing tests.**
  - Host: `Loads_once_for_concurrent_acquires`; `Unloads_after_last_lease_with_zero_delay`; `Stays_loaded_during_delay_and_reacquire_cancels_unload` (delay 200 ms); `Create_returns_null_when_off`; `Create_throws_not_supported_for_gpu`.
  - Config: `Embedding_fields_round_trip_and_default_off`.
  - Runner: `Indexing_creates_vectors_for_new_segments` (write two txt files with different text, run, `embeddings` has rows, space equals the fake's); `Unchanged_second_run_does_not_call_embedder` (call count stays); `Model_load_only_when_pending` (empty folder: `IsLoaded` never true and factory call count 0); `Changed_space_reembeds` (second runner with the fake in another space; both spaces have rows); `Unavailable_model_does_not_fail_the_pass` (factory throws `EmbeddingUnavailableException`; FTS hits still exist; `Failed == 0`); `Cancellation_keeps_finished_batches`; `Model_is_unloaded_after_the_pass` (zero delay, `IsLoaded` false after `RunAsync`).
- [ ] **Step 2: Run** and expect FAIL.
- [ ] **Step 3: Implement** the types and the runner hook. Keep `IndexRunner.cs` under 200 lines by keeping the stage in its own file; if the file passes 200 lines, move `Counters` and `Report` to `RunCounters.cs`.
- [ ] **Step 4: Run** the whole `Recall.Core.Tests`: PASS.
- [ ] **Step 5: Commit** `feat(index): embedding stage, model host and config fields`.

### Task 6: Hybrid search

**Files:**
- Create: `src/Recall.Core/Search/VectorSearch.cs`, `src/Recall.Core/Search/RankFusion.cs`
- Modify: `src/Recall.Core/Search/SearchEngine.cs`
- Test: `tests/Recall.Core.Tests/HybridSearchTests.cs`, `tests/Recall.Core.Tests/RankFusionTests.cs`

**Interfaces:**
- Consumes: `EmbedderHost`, `IndexStore.AddEmbeddings` (for test setup), `ParsedQuery` filters.
- Produces:
  - `static class RankFusion { static IReadOnlyList<(string Key, double Score)> Fuse(IReadOnlyList<string> fts, IReadOnlyList<string> vec, double ftsWeight = 2.0, double vecWeight = 1.0, int k = 60); }` Descending score; equal scores keep FTS order first.
  - `sealed class VectorSearch { static IReadOnlyList<SearchHit> Find(SqliteConnection db, ParsedQuery q, float[] queryVector, string space, int limit, float minSimilarity); }` Streams `embeddings` rows for `space` joined to `segments`, `contents`, `files` with the same `Filters(q)` as the FTS queries (make that method `internal` and shared), scores with `TensorPrimitives.Dot`, keeps the best chunk per file path, returns the top `limit` as `SearchHit` with `Score` = similarity, `Snippet` from the best segment text start, `SegmentId` set so `AttachContext` still works.
  - `SearchEngine(string dbPath, EmbedderHost? embedder = null)`. `Search` flow: run FTS as today into `ftsHits` (names excluded), if `embedder != null` and the query has text (`!q.IsEmpty` and at least one term) take a lease, embed `string.Join(" ", terms)` as `EmbedKind.Query`, call `VectorSearch.Find` with `limit * 3`, fuse by path, cut to `limit`, then append name hits as today. `EmbeddingUnavailableException` means FTS only. Constant `MinSimilarity` is set in Task 8.

- [ ] **Step 1: Write failing tests.**
  - Fusion: `Both_lists_beat_single_list`; `Fts_rank_one_beats_vector_rank_one`; `Empty_vector_list_keeps_fts_order`.
  - Hybrid (fake embedder, vectors written by running the embedding stage on stored segments): `Semantic_hit_without_shared_words_is_found` (use the fake with a lookup table so that "invoice" and "bill payment" map to the same vector; query "invoice" finds the file whose text only contains "bill payment"); `Exact_hit_ranks_before_semantic_only_hit`; `Filters_apply_to_vector_hits` (`type:pdf`); `Same_content_two_paths_both_listed`; `Old_space_vectors_are_ignored`; `Filter_only_query_does_not_embed` (fake call count 0); `Unavailable_model_falls_back_to_fts`; `Without_embedder_results_are_unchanged` (existing `SearchTests` all still pass).
- [ ] **Step 2: Run** and expect FAIL.
- [ ] **Step 3: Implement** `RankFusion`, `VectorSearch`, the `SearchEngine` change. If `SearchEngine.cs` goes over 200 lines, move the three `Run*` SQL methods into `FtsQueries.cs` in the same commit.
- [ ] **Step 4: Run** all tests: PASS.
- [ ] **Step 5: Commit** `feat(search): hybrid ranking with vector similarity`.

### Task 7: Agent wiring and model download

**Files:**
- Modify: `src/Recall.Agent/AgentHost.cs`, `src/Recall.Agent/AgentRequestHandler.cs`, `src/Recall.Agent/Program.cs`
- Test: `tests/Recall.Core.Tests/AgentHostTests.cs` (one added test)

**Interfaces:**
- Consumes: `EmbedderHost.Create`, `ModelDownloader.EnsureAsync`, `SearchEngine(dbPath, embedder)`, `IndexRunner(..., embedder)`.
- Produces: the agent and the `--once` mode build one `EmbedderHost` from the config (unload delay 60 s in the agent, zero for `--once`), pass it to the runner and to the handler. If `EmbeddingModel` is set and the model files are missing, the agent downloads them before the first pass (log lines with progress at 10% steps) and logs failures without stopping. `AgentRequestHandler` gets the host as a new trailing optional constructor parameter.

- [ ] **Step 1: Write failing test** `Agent_with_embeddings_off_behaves_as_before` is already covered by existing tests, so add `Agent_survives_embedding_model_that_cannot_be_downloaded` (config with model id "multilingual-e5-small", an `HttpClient` seam that fails; the pass still indexes a text file and search finds it by text). Add an `AgentOptions` trailing optional member `HttpClient? Http` for the seam and `string? ModelsRoot`.
- [ ] **Step 2: Run**, expect FAIL.
- [ ] **Step 3: Implement** the wiring. Keep the diff small: another session edits folder settings and may touch `Program.cs` and `AgentHost.cs`.
- [ ] **Step 4: Run** the full test suite: PASS.
- [ ] **Step 5: Commit** `feat(agent): load embeddings from config, download model on demand`.

### Task 8: Measurement and documentation

**Files:**
- Create: `docs/benchmarks/2026-11-plan2-embeddings.md`
- Modify: `docs/roadmap.md` (remove the "model in memory only while indexing" item that is now done; keep the game-mode item), `README.md` (short Russian section: how to switch embeddings on in config, model size, what is downloaded where)
- Modify: `src/Recall.Core/Search/SearchEngine.cs` (set `MinSimilarity` from the measured values)

- [ ] **Step 1: Download the model through the agent path** (`Recall.Agent --once` with a scratch config where `EmbeddingModel` is set) so the real downloader and hash check are exercised once.
- [ ] **Step 2: Measure on a scratch database with a real corpus** (Release build, `--once`, a folder with real documents and screenshots, not `C:\projects`; record file and chunk counts). Report separately, per the spec: (a) cold model load time; (b) single-batch latency for one chunk and for 16 chunks; (c) throughput in chunks per second for the full stage; (d) peak working set of the agent process during the stage and after the unload (read with `Get-Process`, sample every 500 ms); (e) re-run with nothing changed (no model work, time of the pass). State the CPU model and thread count used. Estimates are never written as measurements.
- [ ] **Step 3: Calibrate `MinSimilarity`:** for 10 Russian and 5 English queries over the scratch index, print the similarity of relevant and irrelevant top hits, pick a floor that drops the irrelevant ones, and write the table in the benchmark note.
- [ ] **Step 4: Measure query latency:** first search after unload (includes model load) and a warm search; scan time of the vector table at the measured chunk count. Write down the chunk count at which the scan becomes slower than 300 ms, as an extrapolation marked as such.
- [ ] **Step 5: Run the full test suite; then commit** `docs: plan 2 benchmark note, roadmap and readme`.

## Self-review

- Spec coverage: 7.1.1 download with hash check and pinned revision (Task 4), lighter text model chosen (Task 1), 7.2 device field with CPU only (Task 5, GPU and NPU explicitly out), 7.3 space id per model revision and re-embedding on change (Tasks 2, 5), 8 FTS first then vector merge with RRF and brute-force scan (Task 6), roadmap load-only-when-needed (Task 5).
- Not covered on purpose: image and audio vectors, device check of 0.999 cosine, settings screen, in-memory vector cache.
- Types used across tasks: `Space` (string), `EmbedKind`, `EmbedderHost.Acquire()` returning `Lease`, `PendingSegments(space, limit)`, `AddEmbeddings(space, rows)` match their definitions.
- Proportion: the plan is about 1.5 times the length of its spec sections that apply. Bodies are not given.
