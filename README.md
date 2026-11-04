<div align="center">

# Recall Search

**Find a file by what you remember: a word on a screenshot, a phrase from a PDF, the meaning of a picture.**

Text and semantic search for Windows. It runs locally, inside PowerToys Command Palette.

[![Release](https://img.shields.io/github/v/release/onezuppi/recall-search?style=flat-square)](https://github.com/onezuppi/recall-search/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/onezuppi/recall-search/total?style=flat-square)](https://github.com/onezuppi/recall-search/releases)
[![CI](https://img.shields.io/github/actions/workflow/status/onezuppi/recall-search/ci.yml?style=flat-square&label=build)](https://github.com/onezuppi/recall-search/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue?style=flat-square)](LICENSE)
![Windows 10/11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?style=flat-square)

</div>

Recall Search indexes the folders you choose and finds the text you remember. It reads text from images and screenshots (Windows OCR), PDFs, Office documents and plain text files. Optional semantic search finds a terminal screenshot for the query "terminal window", even when the picture contains no such words.

Your files, their text and the index never leave your computer. The project has no telemetry and no cloud services.

## Features

- **Fragment search.** Remember part of a word or a phrase, at least three characters.
- **Images and screenshots.** OCR in Russian and English. Pictures inside DOCX, XLSX and PPTX are read too.
- **Documents.** PDF, DOCX, XLSX, PPTX, TXT, MD.
- **Semantic search** (optional). A model on the CPU or on an AMD NPU. It finds both text and images.
- **Filters:** `in:`, `type:`, `after:`, `before:` and exact phrases in quotes.
- **Folder watching.** New files reach the index within a couple of seconds.
- **Smart exclusions.** Built-in rules, `.gitignore`, `.recallignore`. Secrets (`.pem`, `.key`, `.env`, `.kdbx` and others) are never read.
- **Stays out of your way.** The agent runs at low priority and pauses indexing during a full-screen game or a presentation.

## Installation

You need Windows 10 or 11 and [PowerToys](https://github.com/microsoft/PowerToys) with Command Palette. You do not need .NET or the SDK.

1. Turn on Developer Mode: Windows Settings, System, For developers, Developer Mode. The extension is not signed yet, so Windows requires this.
2. Download `RecallSearch-<version>-win-x64.zip` from [Releases](https://github.com/onezuppi/recall-search/releases/latest) and extract it.
3. Open PowerShell in the extracted folder and run:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

4. Open Command Palette, run the `Reload` command, then type "Recall Search".

The installer needs no administrator rights. It puts the agent in `%LOCALAPPDATA%\Programs\RecallSearch`, registers the extension and adds the agent to the autostart of the current user. Pass `-NoAutostart` to skip the autostart. To update, extract the new archive and run `install.ps1` again. Your index and settings stay.

The first pass over a large collection takes a long time, especially with semantic search. You can see the progress in the extension settings, card "Indexing progress". Text search works while indexing continues.

To uninstall:

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
```

The index and settings stay in `%LOCALAPPDATA%\RecallSearch`. Add `-RemoveData` to delete them too.

> Check the checksum of the archive. It is in the `.sha256` file next to it.

## How to search

| Query | What it finds |
|---|---|
| `invoice 4521` | Files that contain both fragments. |
| `"payment under the contract"` | The exact phrase. |
| `contract in:Downloads` | Only paths that contain `Downloads`. |
| `waybill type:pdf` | Only PDFs. Extensions and groups (`images`, `office`, `text`) work. |
| `password after:2026-05` | Files changed since 1 May 2026. |
| `type:png "confirmation code" after:2026-05-14` | Filters and phrases combine. |

For the full syntax, exclusion rules and all settings, see the [user guide](docs/guide.md).

## Semantic search

Off by default. Turn it on in the extension settings, card "Semantic search device":

- `cpu`: the multilingual-e5-small model (about 123 MB, downloaded once from Hugging Face). It runs on any computer.
- `npu`: EmbeddingGemma 2 on an AMD NPU. It is faster and it embeds images, but it needs a separate server and a model bundle ([instructions](tools/npu-embed-server/README.md)).

Details and measurements: [user guide](docs/guide.md#semantic-search).

## Privacy and security

- Nothing leaves your computer. The only network request is the download of the `cpu` model from Hugging Face, and only if you select it.
- The agent and the extension talk over the named pipe `RecallSearch.<SID>`. Only your account can use it. The client checks that the pipe belongs to `Recall.Agent.exe` from the install folder.
- The index holds the text of your documents. It lives in your user profile under file system permissions. The file itself is not encrypted.

To report a vulnerability, follow [SECURITY.md](SECURITY.md).

## Building from source

```powershell
git clone https://github.com/onezuppi/recall-search.git
cd recall-search
dotnet test
.\scripts\build-release.ps1
```

More for developers: [docs/development.md](docs/development.md). How to contribute: [CONTRIBUTING.md](CONTRIBUTING.md). History of changes: [CHANGELOG.md](CHANGELOG.md). Plans: [roadmap](docs/roadmap.md).

## License

[MIT](LICENSE)
