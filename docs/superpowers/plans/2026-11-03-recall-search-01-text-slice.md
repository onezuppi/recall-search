# Recall Search, Plan 1: Text Search Slice Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A per-user background agent that indexes the user's folders under the agreed ignore rules, extracts text from documents and OCR text from images (including images inside documents), answers fragment searches over a secured named pipe, and a Command Palette extension that shows the results.

**Architecture:** `Recall.Core` (class library) holds config, ignore rules, the metadata-walk scanner, extractors, OCR, the SQLite index and search. `Recall.Agent` (exe, runs as the logged-on user) hosts the indexer, a folder watcher for watched folders, and the pipe server. `Recall.CmdPal` (MSIX COM server) is a thin pipe client. Embeddings, the NPU, the USN service, the night schedule, the settings window and the installer are later plans (see "Roadmap" at the end).

**Tech Stack:** .NET 10 (SDK 10.0.201 is installed), C#, `Microsoft.Data.Sqlite` (SQLite FTS5 with the trigram tokenizer), `System.IO.Hashing` (XxHash128), `DocumentFormat.OpenXml`, `UglyToad.PdfPig`, `Windows.Media.Ocr` and `Windows.Graphics.Imaging` through the Windows SDK projection, `Microsoft.CommandPalette.Extensions`, `Shmuelie.WinRTServer`, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-03-recall-search-design.md`

## Global Constraints

- Target framework: `net10.0-windows10.0.26100.0`, `TargetPlatformMinVersion` `10.0.19041.0`, platforms x64 and ARM64.
- No hardcoded user paths. Known folders come from `SHGetKnownFolderPath`.
- Never open a file that has `FILE_ATTRIBUTE_OFFLINE` (0x1000) or `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` (0x400000) or `FILE_ATTRIBUTE_RECALL_ON_OPEN` (0x40000). Never follow reparse points.
- Secrets are never read and never listed: `.pem`, `.key`, `.pfx`, `.p12`, `.mafile`, `.kdbx`, `.env`, `.env.*`, `id_rsa*`, `*.ppk`.
- Size limits (defaults, configurable): text-like files 2 MB, images 50 MB.
- Index and config live in `%LOCALAPPDATA%\RecallSearch\` (`config.json`, `index.db`, `recall.log`).
- Pipe name: `RecallSearch.<user SID>`. Only the current user's SID has access.
- Code identifiers, comments and commit messages in English. User-facing strings in English for now (localization is a later plan).
- Benchmarks report single-file latency, throughput and re-index time from cache separately. Estimates are never reported as measurements.

## Review Focus

1. A folder tree with millions of files under `node_modules`-like folders: the scanner must not enter them at all (test in Task 3: a pruned folder's children are never enumerated).
2. OneDrive cloud-only files: listed nowhere, never opened, no download triggered (test in Task 3 with the offline attribute).
3. A second run with no changes must not extract or hash anything (test in Task 7 with a counting extractor).
4. Another process that creates the pipe first (pipe squatting): the extension must refuse to talk to it (test in Task 9: `FirstPipeInstance` fails for the second server, and the client checks the server's user).
5. Files that fail to parse (corrupt PDF, locked file, unsupported image): the run continues, the file is recorded as failed with the reason, and it is retried only when it changes (test in Task 7).

---

## File Structure

```
RecallSearch.sln
Directory.Build.props                  common TFM, nullable, warnings as errors
src/Recall.Core/
  Config/RecallConfig.cs              config model, load/save, defaults
  Config/KnownFolders.cs              SHGetKnownFolderPath + app folder detection
  Ignore/BuiltInRules.cs              folder names, system paths, secret patterns, type groups
  Ignore/GitIgnoreMatcher.cs          gitignore semantics for .gitignore and .recallignore
  Ignore/IgnorePolicy.cs              combines all rules: ShouldEnterDirectory / ShouldIndexFile
  Scan/FileRecord.cs                  record of one file's metadata
  Scan/Scanner.cs                     metadata walk with pruning
  Scan/ChangeSet.cs                   diff of a scan against the stored snapshot
  Extract/IExtractor.cs               extractor contract and ExtractResult
  Extract/TextExtractor.cs            txt, md
  Extract/OpenXmlExtractor.cs         docx, pptx, xlsx text and embedded images
  Extract/PdfExtractor.cs             pdf text layer and page images
  Extract/ImageExtractor.cs           standalone images -> OCR
  Ocr/IOcr.cs, Ocr/WindowsOcr.cs      Windows.Media.Ocr wrapper, multi-language
  Index/IndexStore.cs                 SQLite schema, files/contents/FTS, dedupe by hash
  Index/IndexRunner.cs                scan -> diff -> hash -> extract -> store
  Search/QueryParser.cs               fragments, "phrases", in:, type:, after:, before:
  Search/SearchEngine.cs              FTS5 query, ranking, snippets
  Ipc/Protocol.cs                     request/response records, JSON source generation
  Ipc/PipeServer.cs, Ipc/PipeClient.cs  secured per-user pipe
src/Recall.Agent/
  Program.cs                          single instance, host, logging
  AgentHost.cs                        initial run, watcher queue, pipe dispatch
src/Recall.CmdPal/                    MSIX extension (template-based)
  Program.cs, RecallExtension.cs, SearchPage.cs, Commands.cs, Package.appxmanifest
tests/Recall.Core.Tests/              xUnit, one file per Core area
scripts/install-cmdpal.ps1            build + register extension (Developer Mode)
```

---

### Task 1: Solution scaffold and config

**Files:**
- Create: `RecallSearch.sln`, `Directory.Build.props`, `src/Recall.Core/Recall.Core.csproj`, `src/Recall.Core/Config/RecallConfig.cs`, `src/Recall.Core/Config/KnownFolders.cs`, `tests/Recall.Core.Tests/Recall.Core.Tests.csproj`, `tests/Recall.Core.Tests/ConfigTests.cs`, `.gitignore`

**Interfaces:**
- Produces:
  - `sealed record RootFolder(string Path, bool Watched, string? Name = null)`
  - `sealed record RecallConfig(IReadOnlyList<RootFolder> Roots, IReadOnlySet<string> EnabledGroups, IReadOnlyList<string> GlobalIgnorePatterns, long MaxTextBytes = 2*1024*1024, long MaxImageBytes = 50*1024*1024, IReadOnlyList<string> OcrLanguages = ["ru", "en-US"])`
  - `static RecallConfig RecallConfig.CreateDefault()` - known folders Desktop, Downloads, Documents, Pictures, Screenshots, Camera Roll, Videos\Captures that exist; watched: Desktop, Downloads, Screenshots, plus detected app folders.
  - `static RecallConfig RecallConfig.Load(string path)` / `void Save(string path)` - JSON, camelCase; missing file returns `CreateDefault()`.
  - `static string RecallPaths.DataDir` = `%LOCALAPPDATA%\RecallSearch`
  - `static string? KnownFolders.Get(Guid folderId)`; `static IEnumerable<RootFolder> KnownFolders.DetectAppFolders()` (ShareX screenshots folder from `Documents\ShareX\ApplicationConfig.json` key `CustomScreenshotsPath` or default `Documents\ShareX\Screenshots`; Telegram Desktop `Downloads\Telegram Desktop`; existing only).
  - Type group names (string constants in `BuiltInRules`, Task 2): `images`, `pdf`, `office`, `text`.

- [ ] **Step 1: Write the failing tests** in `ConfigTests.cs`:
  - `Default_config_uses_existing_known_folders`: every `Roots[i].Path` exists, none contains the literal user name segment taken from a hardcoded string (assert paths start with `Environment.GetFolderPath(UserProfile)` or the OneDrive root).
  - `Desktop_resolves_through_known_folder_api`: `KnownFolders.Get(KnownFolderIds.Desktop)` equals `Environment.GetFolderPath(SpecialFolder.DesktopDirectory)`.
  - `Save_then_load_roundtrips`: save to a temp file, load, records are equal (compare lists element-wise).
  - `Missing_file_loads_default`.
- [ ] **Step 2: Run** `dotnet test tests/Recall.Core.Tests` - expected: build fails (types missing).
- [ ] **Step 3: Implement** the scaffold (`Directory.Build.props`: `TargetFramework` from Global Constraints, `Nullable` enable, `TreatWarningsAsErrors` true, `ImplicitUsings` enable) and the types above. `KnownFolders.Get` P/Invokes `shell32!SHGetKnownFolderPath` with `KF_FLAG_DONT_VERIFY`; IDs: Desktop `B4BFCC3A-DB2C-424C-B029-7FE99A87C641`, Downloads `374DE290-123F-4565-9164-39C4925E467B`, Documents `FDD39AD0-238F-46AF-ADB4-6C85480369C7`, Pictures `33E28130-4E1E-4676-835A-98395C3BC3BB`, Screenshots `B7BEDE81-DF94-4682-A7D8-57A52620B86F`, CameraRoll `AB5FB87B-7CE2-4F83-915D-550846C9537B`; Videos\Captures = Videos (`18989B1D-99B5-455B-841C-AB7C74E4DDFC`) + `\Captures`.
- [ ] **Step 4: Run** `dotnet test tests/Recall.Core.Tests` - expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "feat(core): solution scaffold and config with known folders"`

### Task 2: Ignore policy

**Files:**
- Create: `src/Recall.Core/Ignore/BuiltInRules.cs`, `src/Recall.Core/Ignore/GitIgnoreMatcher.cs`, `src/Recall.Core/Ignore/IgnorePolicy.cs`, `tests/Recall.Core.Tests/IgnoreTests.cs`

**Interfaces:**
- Consumes: `RecallConfig` (Task 1).
- Produces:
  - `static class BuiltInRules` with `IReadOnlySet<string> SkipFolderNames` (spec 5.4 item 4, case-insensitive), `bool IsSystemPath(string fullPath)` (spec 5.4 item 1, resolved from known folders and `%SystemRoot%`, `%ProgramFiles%`, `%ProgramFiles(x86)%`, `%ProgramData%`, `%LOCALAPPDATA%`, `%APPDATA%`, `%TEMP%`, `<drive>\$Recycle.Bin`, `<drive>\System Volume Information`, other profiles under `C:\Users` except the current one), `bool IsSecret(string fileName)`, `string? GroupOf(string extension)` mapping: images = jpg jpeg png webp bmp gif tif tiff heic; pdf = pdf; office = docx xlsx pptx; text = txt md.
  - `sealed class GitIgnoreMatcher` - `static GitIgnoreMatcher Parse(string baseDir, IEnumerable<string> lines)`; `MatchResult Match(string fullPath, bool isDirectory)` returning `Ignored`, `Included` (negation) or `None`.
  - `sealed class IgnorePolicy(RecallConfig config)` - `DirectoryDecision EnterDirectory(string fullPath, FileAttributes attrs, IReadOnlyList<GitIgnoreMatcher> scope)`; `FileDecision IndexFile(string fullPath, long size, FileAttributes attrs, IReadOnlyList<GitIgnoreMatcher> scope)`; `IReadOnlyList<GitIgnoreMatcher> ScopeFor(string dir, IReadOnlyList<GitIgnoreMatcher> parentScope)` reads `.gitignore` and `.recallignore` in `dir` if present. Decisions are enums with a reason (`Enter`, `SkipSystem`, `SkipBuiltIn`, `SkipIgnoreFile`, `SkipReparse`; `Index`, `SkipSecret`, `SkipType`, `SkipSize`, `SkipCloudOnly`, `SkipIgnoreFile`).

- [ ] **Step 1: Write the failing tests** in `IgnoreTests.cs`:
  - `Built_in_folders_are_not_entered`: `node_modules`, `.git`, `.pnpm`, `.angular`, `.godot`, `__pycache__`, `site-packages`, `bin`, `obj` -> `SkipBuiltIn`.
  - `System_paths_are_not_entered_even_inside_a_root`: `%SystemRoot%`, `%LOCALAPPDATA%`, `%TEMP%` -> `SkipSystem`.
  - `Reparse_point_directory_is_not_entered`: attrs with `ReparsePoint` -> `SkipReparse`.
  - `Secrets_are_skipped`: `a.pem`, `x.mafile`, `.env`, `.env.local`, `id_rsa`, `id_rsa.pub`, `k.ppk` -> `SkipSecret`; `environment.md` -> not secret.
  - `Cloud_only_file_is_skipped`: attrs `0x400000` -> `SkipCloudOnly`; `Offline` -> `SkipCloudOnly`.
  - `Type_allow_list`: `a.JPEG` -> Index; `a.js`, `a.json`, `a.map`, `a.dll` -> `SkipType`; disabling group `images` makes `a.png` `SkipType`.
  - `Size_limits`: `a.txt` 2 MB + 1 -> `SkipSize`; `a.png` 50 MB + 1 -> `SkipSize`.
  - Gitignore semantics (temp dir with real files): `*.log` ignores nested `a/b/x.log`; `build/` ignores the folder only; `!keep.log` re-includes; `/root-only.txt` matches only at the base; a nested `.gitignore` adds rules for its subtree; `.recallignore` behaves the same; `config.GlobalIgnorePatterns` apply everywhere.
- [ ] **Step 2: Run** `dotnet test --filter IgnoreTests` - expected: FAIL (types missing).
- [ ] **Step 3: Implement.** `GitIgnoreMatcher`: translate each pattern to a regex per gitignore rules (`**`, `*`, `?`, `[...]`, leading `/` anchors to base, trailing `/` directory-only, `!` negation, last match wins, `#` comments, escaped `\#` and `\!`). Matching is case-insensitive (NTFS). Order of checks in `IndexFile`: secret, cloud-only, type, ignore files, size.
- [ ] **Step 4: Run** `dotnet test --filter IgnoreTests` - expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "feat(core): ignore policy with built-in rules and gitignore semantics"`

### Task 3: Scanner with pruning

**Files:**
- Create: `src/Recall.Core/Scan/FileRecord.cs`, `src/Recall.Core/Scan/Scanner.cs`, `tests/Recall.Core.Tests/ScannerTests.cs`

**Interfaces:**
- Consumes: `IgnorePolicy` (Task 2).
- Produces:
  - `sealed record FileRecord(string Path, long Size, DateTime LastWriteUtc, string Group)` (file ids are not needed until the USN plan)
  - `sealed class Scanner(IgnorePolicy policy)` - `IEnumerable<FileRecord> Scan(RootFolder root, CancellationToken ct)`; `ScanStats Stats { get; }` with `DirectoriesEntered`, `DirectoriesSkipped` (per reason), `FilesListed`, `FilesSkipped` (per reason). The stats feed the heavy-folder hint of plan 5: also keep the top 50 directories by files listed beneath them.

- [ ] **Step 1: Write the failing tests** in `ScannerTests.cs` (temp tree built in the test):
  - `Pruned_folder_children_are_never_enumerated`: tree with `proj/node_modules/pkg/a.md` and `proj/notes.md`; a test `IgnorePolicy` subclass hook counts `EnterDirectory` calls; assert `node_modules` is asked once and `pkg` never.
  - `Junction_is_not_followed`: create `cmd /c mklink /J link target` (no admin needed); file inside target appears once.
  - `Offline_file_is_not_listed_and_not_opened`: set `FileAttributes.Offline` on `a.txt`; it is absent; `Stats.FilesSkipped[SkipCloudOnly] == 1`.
  - `Lists_allowed_files_with_metadata`: `a.md` size and `LastWriteUtc` equal `FileInfo` values.
- [ ] **Step 2: Run** `dotnet test --filter ScannerTests` - expected: FAIL.
- [ ] **Step 3: Implement** with an explicit stack and `DirectoryInfo.EnumerateFileSystemInfos` using `EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true, RecurseSubdirectories = false }`; read attributes from the enumerated entry (no extra stat call); call `EnterDirectory` before pushing a child.
- [ ] **Step 4: Run** `dotnet test --filter ScannerTests` - expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "feat(core): metadata scanner that prunes before descending"`

### Task 4: Index store with dedupe and trigram FTS

**Files:**
- Create: `src/Recall.Core/Index/IndexStore.cs`, `tests/Recall.Core.Tests/IndexStoreTests.cs`

**Interfaces:**
- Consumes: `FileRecord` (Task 3).
- Produces:
  - `sealed class IndexStore : IDisposable` - `static IndexStore Open(string dbPath)` (creates schema, WAL mode).
  - Schema: `files(id INTEGER PK, path TEXT UNIQUE COLLATE NOCASE, root TEXT, size INTEGER, mtime INTEGER, content_id INTEGER NULL, status TEXT, error TEXT NULL)`; `contents(id INTEGER PK, hash BLOB UNIQUE, grp TEXT, extracted_at INTEGER)`; `segments(id INTEGER PK, content_id INTEGER, locator TEXT, text TEXT)` where `locator` is `""`, `page:3`, `slide:2` or `image:page:3:1`; `segments_fts` = FTS5 over `segments.text` with `tokenize='trigram'`, external content.
  - `IReadOnlyDictionary<string, (long Size, long Mtime)> Snapshot(string root)`
  - `long? FindContent(UInt128 hash)`
  - `long AddContent(UInt128 hash, string group, IReadOnlyList<Segment> segments)` with `sealed record Segment(string Locator, string Text)`
  - `void UpsertFile(FileRecord f, string root, long? contentId, string status, string? error)`; status values `ok`, `failed`.
  - `void RemoveFiles(IEnumerable<string> paths)`; orphan contents (no file references them) are deleted in the same transaction.

- [ ] **Step 1: Write the failing tests**:
  - `Same_content_two_paths_stored_once`: two `UpsertFile` with the same `contentId` -> one row in `contents`, two in `files`.
  - `Trigram_finds_fragment_inside_word`: add segment "Пароль от wifi: молоко123"; FTS query `"оло"` matches.
  - `Removing_last_path_deletes_orphan_content`.
  - `Snapshot_returns_size_and_mtime_per_path_for_root`.
- [ ] **Step 2: Run** `dotnet test --filter IndexStoreTests` - expected: FAIL.
- [ ] **Step 3: Implement** with `Microsoft.Data.Sqlite`. Verify at open that FTS5 trigram is available (`CREATE VIRTUAL TABLE ... tokenize='trigram'` succeeds); if not, throw with a clear message.
- [ ] **Step 4: Run** - expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "feat(core): SQLite index with content dedupe and trigram FTS"`

### Task 5: Document extractors

**Files:**
- Create: `src/Recall.Core/Extract/IExtractor.cs`, `TextExtractor.cs`, `OpenXmlExtractor.cs`, `PdfExtractor.cs`, `tests/Recall.Core.Tests/ExtractorTests.cs`

**Interfaces:**
- Produces:
  - `sealed record EmbeddedImage(string Locator, byte[] Bytes)`
  - `sealed record ExtractResult(IReadOnlyList<Segment> Segments, IReadOnlyList<EmbeddedImage> Images)`
  - `interface IExtractor { bool CanHandle(string extension); Task<ExtractResult> ExtractAsync(string path, CancellationToken ct); }`
  - `TextExtractor`: UTF-8 with BOM detection; if invalid UTF-8, decode as Windows-1251 (register `CodePagesEncodingProvider`).
  - `OpenXmlExtractor`: docx paragraphs (locator `""`), pptx per slide (`slide:N`), xlsx shared strings and inline strings per sheet (`sheet:Name`); images from `ImagePart`s with locators `image:slide:N:K` or `image:K`; images smaller than 64x64 px or under 4 KB are dropped.
  - `PdfExtractor` (PdfPig): text per page (`page:N`); images per page via `page.GetImages()` (`image:page:N:K`); a page with less than 20 characters of text and exactly one image covering most of the page is a scan: its image is passed on for OCR.

- [ ] **Step 1: Write the failing tests** (fixtures generated in the test with OpenXml SDK and `PdfDocumentBuilder`):
  - `Docx_text_and_embedded_png_are_extracted`
  - `Pptx_text_has_slide_locators`
  - `Xlsx_shared_strings_are_extracted`
  - `Pdf_text_has_page_locators`
  - `Txt_in_cp1251_is_decoded` (bytes for "Привет" in 1251)
  - `Tiny_images_are_dropped`
  - `Corrupt_docx_throws_ExtractException` (random bytes with .docx name) - `sealed class ExtractException(string reason, Exception? inner)`.
- [ ] **Step 2: Run** `dotnet test --filter ExtractorTests` - expected: FAIL.
- [ ] **Step 3: Implement** the three extractors.
- [ ] **Step 4: Run** - expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "feat(core): text, OpenXML and PDF extractors with embedded images"`

### Task 6: OCR and image extractor

**Files:**
- Create: `src/Recall.Core/Ocr/IOcr.cs`, `src/Recall.Core/Ocr/WindowsOcr.cs`, `src/Recall.Core/Extract/ImageExtractor.cs`, `tests/Recall.Core.Tests/OcrTests.cs`

**Interfaces:**
- Consumes: `RecallConfig.OcrLanguages`, `ExtractResult`, `EmbeddedImage`.
- Produces:
  - `interface IOcr { Task<string> RecognizeAsync(byte[] image, CancellationToken ct); }`
  - `sealed class WindowsOcr(IReadOnlyList<string> languages) : IOcr` - decodes with `BitmapDecoder`, scales so the longer side is at most `OcrEngine.MaxImageDimension`, runs one `OcrEngine` per available language, returns the texts joined by `\n` with duplicate lines removed. `IReadOnlyList<string> AvailableLanguages` reports which configured languages are installed.
  - `sealed class ImageExtractor(IOcr ocr) : IExtractor` - standalone image files -> one segment with locator `""`.
  - Word boxes are not stored in this plan (later plan for highlight).

- [ ] **Step 1: Write the failing tests**:
  - `Recognizes_rendered_english_digits`: render "Invoice 4821 total" at 36 px into a PNG (System.Drawing in the test project), OCR text contains `4821`.
  - `Recognizes_rendered_russian_text`: "Счёт оплачен" contains `оплачен`; test is skipped with a message if `ru` is not in `AvailableLanguages`.
  - `Unsupported_bytes_throw_ExtractException`.
- [ ] **Step 2: Run** `dotnet test --filter OcrTests` - expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** - expected: PASS (or the Russian test skipped with its message).
- [ ] **Step 5: Commit** `git commit -m "feat(core): Windows OCR wrapper and image extractor"`

### Task 7: Index runner

**Files:**
- Create: `src/Recall.Core/Scan/ChangeSet.cs`, `src/Recall.Core/Index/IndexRunner.cs`, `tests/Recall.Core.Tests/IndexRunnerTests.cs`

**Interfaces:**
- Consumes: `Scanner`, `IndexStore`, extractors, `IOcr`.
- Produces:
  - `sealed record ChangeSet(IReadOnlyList<FileRecord> Added, IReadOnlyList<FileRecord> Changed, IReadOnlyList<string> Removed)`; `static ChangeSet ChangeSet.Diff(IEnumerable<FileRecord> scanned, IReadOnlyDictionary<string,(long Size,long Mtime)> snapshot)`.
  - `sealed class IndexRunner(IndexStore store, IgnorePolicy policy, IReadOnlyList<IExtractor> extractors, IOcr ocr)` - `Task<RunReport> RunAsync(IReadOnlyList<RootFolder> roots, IProgress<RunProgress>? progress, CancellationToken ct)`; `Task IndexFilesAsync(IReadOnlyList<string> paths, CancellationToken ct)` for watcher events. Roots with `Watched = true` run first; within a root, newest `LastWriteUtc` first.
  - `RunReport`: counts of added, changed, removed, extracted, deduped, failed, and elapsed time split into scan, hash, extract, OCR.
  - Hash: XxHash128 of the full file, streamed.
  - Embedded images from `ExtractResult.Images` go through `IOcr`; each becomes a segment with the image's locator.
  - A failed file is stored with `status = failed` and the reason; it is retried only when its size or mtime changes.

- [ ] **Step 1: Write the failing tests** (temp folder, fake `IOcr` returning fixed text, counting extractor wrapper):
  - `First_run_indexes_all_then_second_run_extracts_nothing`: second `RunReport.Extracted == 0`, and the counting extractor saw zero calls.
  - `Modified_file_is_reextracted_and_old_text_is_gone`.
  - `Deleted_file_is_removed_from_search`.
  - `Copy_of_a_file_is_deduped`: `Deduped == 1`, extractor called once.
  - `Corrupt_file_is_recorded_failed_and_not_retried_until_changed`.
  - `Docx_embedded_image_text_is_searchable` (fake OCR returns "скрин внутри").
- [ ] **Step 2: Run** `dotnet test --filter IndexRunnerTests` - expected: FAIL.
- [ ] **Step 3: Implement.** Extraction runs on a bounded channel with `Environment.ProcessorCount / 2` workers (minimum 1); all writes go through one writer task.
- [ ] **Step 4: Run** - expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "feat(core): incremental index runner with dedupe and failure tracking"`

### Task 8: Query parser and search

**Files:**
- Create: `src/Recall.Core/Search/QueryParser.cs`, `src/Recall.Core/Search/SearchEngine.cs`, `tests/Recall.Core.Tests/SearchTests.cs`

**Interfaces:**
- Consumes: `IndexStore` (opens its own read connection to the same db).
- Produces:
  - `sealed record ParsedQuery(IReadOnlyList<string> Fragments, IReadOnlyList<string> Phrases, string? In, string? Type, DateTime? After, DateTime? Before)`; `static ParsedQuery QueryParser.Parse(string text)`. `after:2026`, `after:2026-05`, `after:2026-05-14` all valid; `in:` matches a root name or folder-name prefix; `type:` is a group name.
  - `sealed record SearchHit(string Path, string Group, string Locator, string Snippet, DateTime Modified, long Size, double Score)`
  - `sealed class SearchEngine(string dbPath)` - `IReadOnlyList<SearchHit> Search(string text, int limit)`.
  - Fragments shorter than 3 characters fall back to `LIKE` on segments and file names (trigram cannot match them). File-name matches are included (path column), ranked below text matches.
  - Ranking: phrase hits first, then all-fragments hits by bm25, then file-name hits. One hit per file (best segment).

- [ ] **Step 1: Write the failing tests**:
  - `Parses_filters_and_phrases`: `"network map" moca in:notes type:pdf after:2026-05` -> exact fields.
  - `Fragment_matches_inside_words`.
  - `Phrase_beats_scattered_words`.
  - `Type_and_date_filters_apply`.
  - `Two_char_query_uses_fallback`.
  - `One_hit_per_file_with_best_snippet`.
- [ ] **Step 2: Run** `dotnet test --filter SearchTests` - expected: FAIL.
- [ ] **Step 3: Implement.** Snippet via FTS5 `snippet()` with `[` `]` markers stripped for display, at most 160 characters.
- [ ] **Step 4: Run** - expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "feat(core): query parser and FTS search"`

### Task 9: Secured per-user pipe

**Files:**
- Create: `src/Recall.Core/Ipc/Protocol.cs`, `src/Recall.Core/Ipc/PipeServer.cs`, `src/Recall.Core/Ipc/PipeClient.cs`, `tests/Recall.Core.Tests/PipeTests.cs`

**Interfaces:**
- Produces:
  - `sealed record IpcRequest(string Op, string? Query = null, int Limit = 48)`; ops: `search`, `status`, `ping`.
  - `sealed record IpcHit(string Path, string Group, string Locator, string Snippet, DateTime Modified, long Size)`
  - `sealed record IpcStatus(long FilesIndexed, long FilesPending, long FilesFailed, DateTime? LastRunUtc)`
  - `sealed record IpcResponse(bool Ok, string? Error = null, IpcHit[]? Hits = null, IpcStatus? Status = null)`
  - Wire format: one UTF-8 JSON line each way (source-generated `JsonSerializerContext`, camelCase), max request 64 KB.
  - `static string PipeNames.ForCurrentUser()` = `RecallSearch.<SID>`.
  - `sealed class PipeServer(Func<IpcRequest, CancellationToken, Task<IpcResponse>> handler)` - `Task RunAsync(CancellationToken ct)`; the first listening instance is created with `NamedPipeServerStreamAcl.Create(..., PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, ..., pipeSecurity)` and later instances without `FirstPipeInstance`, where `pipeSecurity` grants `FullControl` to the current user SID only.
  - `static class PipeClient` - `Task<IpcResponse> SendAsync(IpcRequest r, TimeSpan timeout, CancellationToken ct)`; after connect, calls `GetNamedPipeServerProcessId` and checks that the server process token's user SID equals the current user SID and that its image path equals `AgentPaths.ExpectedExe` (`static string ExpectedExe { get; set; }`, default `%LOCALAPPDATA%\Programs\RecallSearch\Recall.Agent.exe`, settable for tests); otherwise throws `UntrustedServerException`.

- [ ] **Step 1: Write the failing tests**:
  - `Roundtrip_search_request`.
  - `Second_server_on_same_name_fails` (expects `UnauthorizedAccessException` or `IOException`).
  - `Client_rejects_server_from_unexpected_path` (expected path set to a different directory).
  - `Oversized_request_is_rejected_without_crash`.
- [ ] **Step 2: Run** `dotnet test --filter PipeTests` - expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** - expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "feat(core): per-user secured pipe with server identity check"`

### Task 10: Agent host

**Files:**
- Create: `src/Recall.Agent/Recall.Agent.csproj`, `src/Recall.Agent/Program.cs`, `src/Recall.Agent/AgentHost.cs`, `tests/Recall.Core.Tests/AgentHostTests.cs` (agent logic kept testable through `AgentHost`)

**Interfaces:**
- Consumes: everything above.
- Produces:
  - `Recall.Agent.exe` - single instance via a named mutex `Local\RecallSearch.Agent`; `--once` runs one index pass and exits (prints `RunReport`); default mode: initial pass, then watcher, then pipe server until logoff.
  - `sealed class AgentHost(RecallConfig config, string dbPath)` - `Task RunAsync(CancellationToken ct)`; `FileSystemWatcher` per watched root (`IncludeSubdirectories = true`, `InternalBufferSize = 64 KB`), events debounced 2 s, deduped by path, passed through `IgnorePolicy` and then `IndexRunner.IndexFilesAsync`; on watcher buffer overflow, schedules a rescan of that root.
  - Log to `%LOCALAPPDATA%\RecallSearch\recall.log`, rolling at 5 MB.
  - Autostart at logon and the night schedule are not in this plan.

- [ ] **Step 1: Write the failing tests**:
  - `New_file_in_watched_root_becomes_searchable_within_5_seconds`.
  - `File_in_ignored_subfolder_of_watched_root_is_not_indexed` (`node_modules/x.md`).
  - `Watcher_overflow_triggers_rescan` (raise the `Error` event through a test seam).
- [ ] **Step 2: Run** `dotnet test --filter AgentHostTests` - expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** - expected: PASS.
- [ ] **Step 5: Manual check on the real machine:** `dotnet run --project src/Recall.Agent -- --once` with roots `C:\projects`, `%USERPROFILE%\OneDrive\Documents`, Downloads, `C:\odata`. Record in `docs/benchmarks/2026-11-plan1.md`: files listed, directories skipped by reason, wall time of the first run, wall time of a second run with no changes (re-index from cache), and per-stage times from `RunReport`. Expected: the second run extracts nothing. These numbers are measurements of this machine only.
- [ ] **Step 6: Commit** `git commit -m "feat(agent): agent host with watcher, pipe server and one-shot mode"`

### Task 11: Command Palette extension

**Files:**
- Create: `src/Recall.CmdPal/Recall.CmdPal.csproj`, `Program.cs`, `RecallExtension.cs`, `SearchPage.cs`, `Commands.cs`, `Package.appxmanifest`, `app.manifest`, `Assets/*.png`, `scripts/install-cmdpal.ps1`
- Modify: `RecallSearch.sln`

**Interfaces:**
- Consumes: `Ipc/Protocol.cs` and `Ipc/PipeClient.cs` compiled in by link (no reference to the rest of Core, keeps SQLite out of the package).
- Produces:
  - Command `Recall Search` (stable id `RecallSearch.Search`) opening a `DynamicListPage`: debounce 150 ms, results as list items with title = snippet, subtitle = `<folder> · <date> · <locator>`, details pane with file name, folder, size, date, locator; actions: Open (default), Show in folder, Copy path, Copy text (snippet).
  - Fallback item `Search files for "<query>"` (stable id `RecallSearch.Search.Fallback`) shown for 3+ characters.
  - When the agent is not running: an item "Recall Search agent is not running" with a Start command (`%LOCALAPPDATA%\Programs\RecallSearch\Recall.Agent.exe` in this plan).
  - When `UntrustedServerException` is thrown: an error item, no results shown.
  - `Package.appxmanifest`: COM server class id (new GUID), `com.microsoft.commandpalette` app extension, capability `runFullTrust` only.
  - `Microsoft.CommandPalette.Extensions` version: the one matching the installed Command Palette 0.11.x; verify the template builds on .NET 10 first, fall back to `net9.0-windows10.0.26100.0` for this project only if it does not, and note it in the commit.

- [ ] **Step 1: Build check:** `dotnet build src/Recall.CmdPal -p:Platform=x64` - expected: success.
- [ ] **Step 2: Implement** the page, fallback and commands.
- [ ] **Step 3: Register and check by hand:** `pwsh scripts/install-cmdpal.ps1` (Developer Mode), start the agent, open Command Palette, type `Recall Search` and a fragment from a known screenshot in `ShareX\Screenshots`, then a fragment from a known PDF in Downloads. Expected: both files appear; Open and Show in folder work; the fallback item appears on the home page.
- [ ] **Step 4: Commit** `git commit -m "feat(cmdpal): Command Palette extension for text search"`

---

## Roadmap (later plans, one per subsystem)

Each later plan ends in working software on its own.

2. **Embeddings on CPU/GPU:** model catalog and Hugging Face downloads (pinned, resumable, hash-checked), ONNX Runtime with DirectML, EmbeddingGemma 2 text, vision and audio (q8 default), vectors per (model revision, device class), hybrid ranking with reciprocal rank fusion, `~` and `like:`, audio files with chunking, PSD composite and text layers.
3. **NPU bridge:** C ABI or long-lived child process over `taconite-embeddinggemma2`, batching, the device compatibility check (cosine >= 0.999) against the ONNX path.
4. **Change service and schedule:** Windows service reading the USN journal, impersonation checks, per-user registration; night pass on AC with a power request; agent autostart at logon.
5. **Settings window:** types, folders, watched folders, exclusions and patterns, heavy-folder hint, models and devices per role, schedule, index statistics.
6. **Distribution:** installer, signed MSIX, Gemma terms shown before the first download, third-party licenses, test matrix without NPU, GPU or OneDrive.
