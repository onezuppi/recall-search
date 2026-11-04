# Recall Search: design

Status: approved by the user on 2026-11-03.

## 1. Intent

The user remembers seeing some text, on a screenshot, a photo or in a
document, but does not remember which file or folder it was in. Today the
user cannot find it. Recall Search finds it by a fragment of the text, by
the meaning of the text, or by what the image looks like.

The entry point is PowerToys Command Palette. Prior art is Glimpse
(github.com/cgranier/glimpse): images only, CLIP on GPU, EmbeddingGemma 2
for OCR text on CPU. Recall Search differs in four ways:

- it indexes documents as well as images;
- it runs EmbeddingGemma 2 image embeddings, including on the AMD NPU
  (an existing Windows port, see section 9);
- it has strong ignore rules so that a developer machine with millions of
  files can be indexed at all;
- the model and the compute device (CPU, GPU, NPU) are user settings.

## 2. Goals

1. Find a file by a text fragment seen on an image or in a document.
2. Find a file by meaning (cross-language, paraphrase).
3. Find an image by visual description, and "more like this".
4. Index the user's chosen folders completely, without touching system
   folders, dependency trees or build output.
5. Notice new files in frequently used folders quickly. Do the bulk work at
   night on AC power. Never rescan what did not change.
6. Work for any Windows user on the machine, with no hardcoded paths.
7. Let the user choose file types, folders, exclusions, models and devices
   in a settings screen.

## 3. Non-goals (version 1)

- Source code search. The user decided code is not a real use case. Code
  files are excluded by extension (section 5.4).
- Audio and video of any kind (decision of 2026-11-04, user). Sections 5 and 7 below still mention audio
  embedding; read them as "not built". No speech-to-text, no audio encoder, no video frames. Only text and images
  are indexed.
- Mail files (MSG, EML). XD, PDN. Legacy DOC and XLS. These are later
  candidates.
- Cloud sync, accounts, sharing, telemetry.
- Replacing Windows Search or Everything for file-name search.

## 4. Anti-goals (failure even if it "works")

- An index that never finishes because it walks `node_modules` and caches.
- Downloading OneDrive cloud-only files to index them.
- Showing one user's files to another user.
- Indexing or displaying secrets (keys, tokens, authenticator files).
- A laptop that is hot or drains its battery during the day because of
  background indexing.
- Rescanning unchanged folders on every run.

## 5. What gets indexed

### 5.1 Scope

The user picks folders. Two kinds:

- **Indexed folders**: covered by the nightly pass.
- **Watched folders**: also followed live (section 6.2).

Defaults on first run (resolved per user, never hardcoded):

- Known folders through the Windows Known Folders API: Desktop, Downloads,
  Documents, Pictures, Screenshots, Camera Roll, Videos\Captures. These may
  live in OneDrive and may have localized names (on the reference machine,
  Desktop is `OneDrive\Рабочий стол`).
- App folders, if the app is installed, read from the app's own settings:
  ShareX screenshots, Telegram Desktop downloads, Lightshot, Greenshot,
  WhatsApp, Yandex Disk.

Watched by default: Desktop, Downloads, Screenshots, ShareX screenshots,
Telegram Desktop downloads.

### 5.2 File types (version 1)

Each type can be turned on or off in settings.

| Group | Extensions | What is extracted |
|---|---|---|
| Images | jpg, jpeg, png, webp, bmp, gif (first frame), tif, tiff, heic | OCR text with word boxes; image embedding |
| Photoshop | psd | composite image (OCR + image embedding); text-layer text; layer names |
| PDF | pdf | text layer; OCR of pages without a text layer |
| Office | docx, xlsx, pptx | text |
| Text | txt, md | text |
| Audio | ogg, opus, mp3, m4a, wav | audio embedding (section 7.1) |

Images embedded in documents are processed too (user decision): pictures
inside DOCX and PPTX, and images on PDF pages, including pages that also
have a text layer. Each embedded image gets OCR and an image embedding, and
a hit on it points to the document (and page or slide where known). Tiny
images (icons, bullets) are skipped by a minimum size, a builder setting.

HEIC needs the Windows HEIF extension. If it is missing, HEIC files are
skipped and the settings screen says why.

### 5.3 Reference data from the user's machine

Measured on 2026-11-03 (metadata only):

- `C:\projects`: 5.05 M files, 46 git repos, 764 `.gitignore` files. Most
  files are `.js` (1.5 M), `.ts` (1.1 M), `.map` (0.5 M), inside
  `node_modules`, `.pnpm`, `.angular\cache`, `.godot\imported`, tool caches
  and data dumps.
- `OneDrive\Documents`: 422 k files. 65% are inside dependency or build
  folders. `ShareX\Screenshots` holds 4.5 k screenshots.
- `Downloads`: 324 files. `C:\odata`: 580 files, including 9 PSD and
  authenticator `.mafile` files.

Any ignore design must make `C:\projects` indexable in reasonable time.

### 5.4 Ignore rules

Applied in this order. A folder that is ignored is never entered.

1. **System locations, always**: `Windows`, `Program Files*`, `ProgramData`,
   `AppData`, `$Recycle.Bin`, `System Volume Information`, temp folders,
   other users' profiles. This holds even if the user adds a whole drive.
2. **No reparse points**: junctions and symlinks are not followed.
3. **No hydration**: OneDrive cloud-only files (offline / recall-on-access
   attributes) are not opened. They are indexed when they become local.
4. **Built-in folder names** for the main languages and tools: `node_modules`,
   `.pnpm`, `bower_components`, `vendor`, `Pods`, `.git`, `.svn`, `.hg`,
   `bin`, `obj`, `target`, `dist`, `build`, `out`, `coverage`, `.angular`,
   `.next`, `.nuxt`, `.svelte-kit`, `.parcel-cache`, `.turbo`, `.godot`,
   `.gradle`, `DerivedData`, `.terraform`, `.venv`, `venv`, `env`,
   `site-packages`, `__pycache__`, `.mypy_cache`, `.pytest_cache`, `.tox`,
   `.idea`, `.vs`, `.vscode`, `.cache`. The list is data, editable in
   settings.
5. **`.gitignore` files** are applied automatically wherever they exist,
   with git semantics (nested files, negation, directory patterns).
6. **User ignore files**: a `.recallignore` file in any folder, gitignore
   syntax, applies to that folder and below. The settings screen can also
   hold global patterns.
7. **Excluded extensions**: all source code and build artifacts (`.js`,
   `.ts`, `.cs`, `.py`, `.map`, `.min.js`, `.pyc`, `.class`, `.dex`,
   `.jar`, `.dll`, `.exe`, `.pdb`, `.lib`, `.rlib`, `.rmeta`, `.d`,
   `.wasm`, Godot `.import` / `.ctex`, and so on), fonts, archives, video,
   `.json`, `.jsonl`, `.log`,
   `.lock`. Practically: version 1 indexes only the allow-list in 5.2, so
   this list matters for reporting, not for correctness.
8. **Secrets, always, not configurable**: `.pem`, `.key`, `.pfx`, `.p12`,
   `.mafile`, `.kdbx`, `.env*`, `id_rsa*`, `*.ppk`, and files named like
   credential stores. Never read, never listed.
9. **Size limits**: text-like files over 2 MB and images over 50 MB are
   skipped (PSD limit: 500 MB). Values are settings.
10. **Heavy-folder hint**: after a pass, the settings screen lists the
    folders that contributed the most entries and offers one-click
    exclusion. It never excludes on its own.

### 5.5 Duplicates

Files are identified by a content hash. Identical content at several paths
(git worktrees, copies, re-downloads) is extracted and embedded once and
listed under every path.

## 6. When indexing runs

### 6.1 Nightly pass

- A per-user scheduled task runs at night, only on AC power.
- The machine is kept awake while the pass runs (power request), without
  changing power settings. If AC is removed, the pass pauses and resumes
  from where it stopped.
- The user can also start a pass manually.

First full indexing (my call): it may take several nights. Work is queued
by priority: watched folders first, then the newest files. The index is
searchable while incomplete, and the extension shows how much is left.
Text extraction and OCR run before image embedding, so text search fills in
first.

### 6.2 Watched folders

New or changed files in watched folders are queued and processed at once,
also on battery (user decision). Only the nightly bulk pass waits for AC.

### 6.3 Change detection

Never rescan what did not change. Two levels:

1. **NTFS USN change journal** (preferred). A small Windows service reads
   the journal for each NTFS volume and reports changes under registered
   folders since the last stored USN. The user agreed to an admin-installed
   service. If the journal was truncated or reset (journal id changed), the
   affected folders fall back to level 2 once.
2. **Metadata walk** (fallback, and for non-NTFS volumes). Enumerate
   folders with ignore rules applied before descending, compare name, size,
   last-write time and file id with the stored snapshot. Read and hash only
   new or changed files. Folder last-write time must not be used to skip a
   subtree: NTFS changes it only when direct children are added, removed
   or renamed.

## 7. Models and devices

The user wants model settings, with different Gemma models for text only,
for images, and for audio, each runnable on CPU, GPU or NPU.

### 7.1 Model roles

| Role | Input | Default model | Notes |
|---|---|---|---|
| Text embedding | OCR text, document text, queries | EmbeddingGemma 2, text part | used for "search by meaning" |
| Image embedding | image pixels, visual queries | EmbeddingGemma 2, vision part | text and image share one vector space, so a text query matches images |
| Audio embedding | audio files, text queries | EmbeddingGemma 2, audio encoder | same vector space as text and images |
| OCR | images, scanned PDF pages | Windows.Media.Ocr | languages from installed Windows OCR packs; see below |

OCR languages (my call): Windows.Media.Ocr uses one language per call. The
default is Russian and English. Each image is recognized with each enabled
language and the texts are stored together (search is over text, so
duplicates do not hurt). The builder measures the cost; if it is too high,
the fallback is one pass in the user's main language plus a second pass only
when the first returns little text.

A role can be turned off. With image embedding off, images are still found
by OCR text.

EmbeddingGemma 2 (google/embeddinggemma-2) maps text, images, video and
audio into one 768-dimensional space: a 270M text model plus independent
vision (170M) and audio (300M) encoders. Unused encoders need not be
loaded. Audio input is limited to about 327 s; longer files are split into
overlapping chunks, and a hit points to the time offset. Audio search in
version 1 is by meaning and sound (a text query matches the embedding). It
does not give exact spoken-word search; speech-to-text is a later option.
(Sources: Hugging Face model card google/embeddinggemma-2, Google AI model
card for EmbeddingGemma 2.)

### 7.1.1 Model catalog and downloads

- Default model: EmbeddingGemma 2 (user decision).
- Variants are downloaded from Hugging Face on demand from a catalog in the
  settings screen, pinned to exact revisions, resumable, size- and
  hash-checked (user decision).
- Known CPU/GPU source: `onnx-community/embeddinggemma-2-ONNX`, with
  separate text, vision and audio ONNX files in fp32, fp16, q8, q4 and
  q4f16. Precision is chosen per encoder. The model page reports worst-case
  cosine similarity to fp32 of 0.9998 (fp16), 0.9997 (q8) and 0.975 (q4).
  Default for CPU/GPU: q8 (my call). Not verified with ONNX Runtime from
  .NET yet; the page shows Transformers.js.
- Lighter text-only options for weak machines (user decision): the
  text-only part of EmbeddingGemma 2 (about 270M), and smaller text
  embedding models from the catalog. Choosing a text-only model turns off
  image and audio embedding; images are then found by OCR text only.
- The NPU port covers text and vision. Audio on the NPU is not needed
  (user decision); audio runs on CPU or GPU only, and the NPU option is not
  offered for the audio role.

### 7.2 Devices

Each role has a device setting: CPU, GPU, NPU, or Auto.

- **CPU and GPU**: ONNX Runtime (GPU through DirectML). The model export and
  its exact revision are pinned.
- **NPU (AMD XDNA2)**: the existing native Windows port of Taconite
  (section 9). Only available when the AMD NPU driver and XRT are present.
- **Auto**: NPU if available, else GPU, else CPU.

The settings screen shows, per device, whether it is available and a
measured speed (single-file latency and throughput, measured separately).

### 7.3 Vector compatibility

Vectors from different models are not comparable. Every stored vector
records model id, model revision and device. Changing a model starts a
background re-embedding of that role; until it finishes, search uses the
old vectors. Vectors of the same model from different devices are treated
as compatible only if a check shows cosine similarity of at least 0.999 on
a reference set (the NPU port reaches 0.9994 or better against its float32
reference on the user's data).

My calls, to keep this simple:

- An embedding space is the pair (model revision, device class). If the
  check passes, devices share one space; if it fails, changing the device
  is treated like changing the model (background re-embedding).
- Auto picks the device once per role when the agent starts. It does not
  switch devices in the middle of a pass. If the chosen device fails, the
  pass stops for that role and the settings screen shows the error.

## 8. Search and results

- Query syntax and ranking can follow Glimpse: exact text hits first, then
  fuzzy text, meaning and visual hits merged by reciprocal rank fusion.
  Filters: `in:<folder name>`, `type:<group>`, `after:`, `before:`,
  `~` for visual only, `like:<id>`.
- Text search: SQLite FTS5 with the trigram tokenizer (any 3+ character
  fragment).
- Vector search: in-memory brute force with SIMD is enough at this scale;
  revisit above ~1 M vectors.
- Command Palette page: thumbnail grid, details pane with preview, the
  snippet with the match, folder, date and the reason for the match. Actions:
  open, show in folder, copy path, copy image, copy text, more like this.
- A Command Palette fallback item "Search files for '...'" on the home
  page after 3+ characters.

## 9. Existing assets

- `embeddinggemma2-windows-taconite` (local, branch `perf/host-pipeline`):
  Taconite 0.2.0 ported to Windows MSVC and AMD XRT, running EmbeddingGemma 2
  vision and text on the XDNA2 NPU. Measured on the reference laptop
  (Ryzen AI 9 HX 370): about 0.87-0.95 s per 640x480 image alone, about
  0.73-0.76 s per image in batches of 3. For the NPU path, model weights,
  NPU kernels and the model must not be replaced or reconverted. This
  constraint does not apply to the CPU/GPU path, which uses a separate ONNX
  export.
  Interface today: a Rust library crate (`taconite-embeddinggemma2`, built
  with the MSVC toolchain) with `embed_text`, `embed_rgb` and the batched
  `embed_rgb_pipelined`, plus example CLIs. There is no C ABI and no
  long-running server yet. Choosing between a C ABI and a long-lived child
  process is left to the builder; either way the model must stay loaded
  between files, and batching must be used.
- ONNX exports for CPU/GPU: `onnx-community/embeddinggemma-2-ONNX` has
  text, vision and audio parts (section 7.1.1). Checking its vectors
  against the NPU path (section 7.3) is an early milestone.
- The model folder `embeddinggemma2-model`.
- The Glimpse code (MIT) can be studied and reused with attribution.

## 10. Architecture

Parts, and who decided them:

- **Change service** (user decision: yes, admin service). Runs as a Windows
  service. Reads USN journals. Talks to per-user agents over a named pipe.
  Before it reports any change to an agent it checks, by impersonating that
  agent's user, that the user can read the registered folder. It never
  returns file contents.
- **Per-user agent** (my call). Starts at logon in the user's session and
  runs as that user, so Windows file permissions apply naturally. Owns the
  user's index under `%LOCALAPPDATA%`. Runs extraction, OCR and embeddings.
  Answers queries over a named pipe whose ACL allows only that user. The
  pipe is created with first-instance protection, and the client checks
  that the server process belongs to the same user and runs from the
  installed path (Glimpse does not check this).
- **Command Palette extension** (user decision). A thin client of the agent,
  like Glimpse.
- **Settings screen** (user decision: needed; placement is my call). A
  small window opened from the extension (Command Palette pages are too
limited for folder pickers and editable lists): file types,
  indexed and watched folders, exclusions and ignore patterns, models and
  devices per role, schedule, index statistics, heavy-folder hint.

Technology (my call, open to the builder within these bounds): C# / .NET 9
for the agent, extension and settings, matching the Command Palette
template. The NPU path is the existing Rust crate (section 9), called
through a C ABI or a long-lived child process. SQLite for the index.

## 10.1 Distribution

The product is for the author and for other people (user decision). So:

- Installer: per-machine install (the change service needs admin), the
  agent and settings app, and the signed MSIX for the Command Palette
  extension. Without a code-signing certificate the extension needs
  Developer Mode, as in Glimpse; signing is a release milestone.
- Must work on machines without an NPU, without a GPU, and without
  OneDrive. The NPU path is optional and detected at runtime.
- Models are not bundled; they are downloaded on first run with the
  user's consent. The settings screen shows the Gemma terms of use before
  the first download.
- Third-party licenses are listed in the repository (Taconite, ONNX
  Runtime, SQLite, parsers, any reused Glimpse code).

## 11. Success

- The user's own case: a text seen once on a screenshot or in a document
  is found from a fragment or a paraphrase.
- `C:\projects`, `OneDrive\Documents`, Downloads and `C:\odata` are fully
  indexed on the reference machine, and a night with no changes finishes in
  minutes.
- Benchmarks always report separately: single-file latency, throughput, and
  re-indexing time from cache (no model work). Estimates are never reported
  as measurements.

## 12. Open questions for the user

None at the moment. Decisions taken on 2026-11-03: audience (author and
others), images inside documents (yes), battery (watched folders at once),
audio (EmbeddingGemma 2 audio encoder), model catalog (EmbeddingGemma 2
default, Hugging Face variants, lighter text-only models).

## 13. Left to the builder

- Exact ignore lists beyond those named, schema, file layout, hash
  function, thread and queue design, OCR batching, thumbnail cache, and the
  internal protocol format.
