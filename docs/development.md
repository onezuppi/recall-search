# Development

## Build and test

You need the .NET SDK 10 and Windows 10 or 11.

```powershell
dotnet build
dotnet test
```

Install from source, without the release archive:

```powershell
.\scripts\install-agent.ps1
.\scripts\install-cmdpal.ps1
```

`install-cmdpal.ps1` builds the extension in Debug and registers it. Windows Developer Mode is required.

## Release archive

```powershell
.\scripts\build-release.ps1 -Version 0.1.0
```

The result is `dist\RecallSearch-0.1.0-win-x64.zip` and a `.sha256` file. The archive is self-contained: the target machine needs no SDK and no .NET. GitHub Actions builds the same archive for a `v*` tag (see `.github/workflows/release.yml`).

To publish a version:

1. Add the changes to `CHANGELOG.md`.
2. Create the tag: `git tag v0.1.0 && git push origin v0.1.0`.
3. Wait for the "Release" workflow. It creates a draft release with the archive. Check the draft and publish it.

## Project layout

| Part | What it does |
|---|---|
| `Recall.Core` | Configuration, exclusion rules, scanner, text extraction, OCR, SQLite index, search, embeddings. |
| `Recall.Agent` | Background process of the user. Indexes folders, watches changes, computes vectors, answers queries. |
| `Recall.CmdPal` | Command Palette extension. A thin client: it sends a query to the agent and shows the results. |
| `Recall.Journal`, `Recall.UsnService` | Optional NTFS change-journal service. |
| `tools/npu-embed-server` | Source of the embedding server for the AMD NPU (built separately). |

### Agent

The agent runs under your account. At start it opens the index and the named pipe, turns on watching for folders marked `watched` and starts the first pass over the root folders.

The agent reads and hashes a file only if it is new or changed (name, size, write time). Identical content in different places is extracted once. It collects changes in watched folders with a delay of about two seconds.

Vectors are computed in a separate background loop, so they never block OCR or new files. After the first pass the loop wakes every 20 minutes (configurable), loads the model, embeds the new data and unloads the model.

### Index

SQLite with FTS5 and a trigram tokenizer, so parts of words are found. Result order: exact phrases, then files that contain all fragments (bm25), then file name matches. Vectors live in the `embeddings` table next to it.

### Command Palette extension

The extension sends a query to the agent over the pipe `RecallSearch.<SID>` and shows the hits: name, highlighted snippet, icon and thumbnail. Actions: open the file, show in folder, copy the path and the text. The extension trusts only the agent in `%LOCALAPPDATA%\Programs\RecallSearch`.

## Measurements and plans

- [Roadmap](roadmap.md)
- [First plan measurements](benchmarks/2026-11-plan1.md)
- [Embeddings on the CPU](benchmarks/2026-11-plan2-embeddings.md)
- [Gemma on the NPU against E5](benchmarks/2026-11-gemma-vs-e5.md)
- [Fast first pass](benchmarks/2026-11-fast-scan.md)
- [Search on large indexes](benchmarks/2026-11-search-at-scale.md)
- [Research: running models on the NPU](research/2026-11-npu-runtime.md)
- [Specification](superpowers/specs/2026-11-03-recall-search-design.md)
