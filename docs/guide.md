# User guide

Details about search, exclusion rules, configuration and the optional services. For installation, see the [README](../README.md).

## How to search

Open Command Palette and type a query. Use at least three characters.

| Query | What it finds |
|---|---|
| `invoice 4521` | Files that contain both fragments. |
| `"payment under the contract"` | Files with this exact phrase. |
| `contract in:Downloads` | Only files whose path contains `Downloads`. |
| `waybill type:pdf` | Only PDFs. An extension (`pdf`, `.png`) or a group (`images`, `office`, `text`) works. |
| `password after:2026-05` | Files changed since 1 May 2026. |
| `report before:2026` | Files changed before 1 January 2026. |
| `type:png "confirmation code" after:2026-05-14` | Filters and phrases combine. |

Rules:

- `after:` and `before:` accept `2026`, `2026-05` and `2026-05-14`. The search uses the start of the period.
- Put a filter value with a space in quotes: `in:"Telegram Desktop"`.
- Words without quotes are searched as separate fragments, all at once. Quotes make an exact phrase.
- Case does not matter.

## What is indexed

| Group | Extensions |
|---|---|
| `images` | jpg, jpeg, png, webp, bmp, gif, tif, tiff, heic |
| `pdf` | pdf |
| `office` | docx, xlsx, pptx |
| `text` | txt, md |

HEIC needs the Windows HEIF extension. Source code, archives, video and audio are not indexed.

## Exclusion rules

The rules apply in order. An excluded folder is not walked at all.

1. **System locations, always.** Windows, Program Files, ProgramData, AppData, temporary folders, the recycle bin, `System Volume Information` and the profiles of other users.
2. **Links.** Reparse points (junctions and symlinks) are not followed.
3. **Cloud files.** OneDrive files that are not on the disk are not opened or downloaded. They enter the index when they become local.
4. **Built-in folder names.** `node_modules`, `.git`, `bin`, `obj`, `dist`, `build`, `target`, `vendor`, `.venv`, `venv`, `__pycache__`, `.idea`, `.vs`, `.vscode`, `.cache` and others. The full list is in [BuiltInRules.cs](../src/Recall.Core/Ignore/BuiltInRules.cs).
5. **`.gitignore`.** Every `.gitignore` is honored wherever it exists, with git rules: nested files, negations and folder patterns.
6. **`.recallignore`.** Put this file in any folder. The syntax is the same as `.gitignore`. The rules apply to that folder and everything below it.
7. **Global patterns.** The `globalIgnorePatterns` field in `%LOCALAPPDATA%\RecallSearch\config.json`. The syntax is the same as `.gitignore`. A full path also works.
8. **Secrets, always.** Files `.pem`, `.key`, `.pfx`, `.p12`, `.mafile`, `.kdbx`, `.ppk`, `.env`, `.env.*` and `id_rsa*` are never read and never enter the index. You cannot turn this off.
9. **Size.** Text files above 2 MB and images above 50 MB are skipped. The values `maxTextBytes` and `maxImageBytes` in the configuration set the limits. The 50 MB limit also applies to PDF and Office files.

An example of `globalIgnorePatterns`:

```json
"globalIgnorePatterns": [
  "build-output*/",
  "D:/archive/old-scans/"
]
```

Restart the agent after you edit the configuration.

## Configuration

The agent creates `%LOCALAPPDATA%\RecallSearch\config.json` on the first start. Folders come from the known folders of Windows, so no path is hard-coded. The defaults are:

- watched: Desktop, Downloads, Screenshots, and the ShareX and Telegram Desktop folders if they exist;
- not watched (walked only at start): Documents, Pictures, Camera Roll, Videos\Captures.

Fields of the file:

| Field | Meaning |
|---|---|
| `roots` | List of folders: `path`, `watched`, `name`. |
| `enabledGroups` | Enabled type groups: `images`, `pdf`, `office`, `text`. |
| `globalIgnorePatterns` | Global exclusion patterns. |
| `maxTextBytes`, `maxImageBytes` | Size limits in bytes. |
| `ocrLanguages` | OCR languages. The Windows language packs must be installed. |

## Privacy and security

- Nothing leaves your computer. The only network request is the download of the `cpu` model, and only if you select it.
- The agent and the extension talk over the named pipe `RecallSearch.<user SID>`. Only your account can use it. Network logons are denied explicitly.
- The pipe is created with the `FirstPipeInstance` flag. If a foreign process already holds the name, the agent does not start.
- The client verifies the server: the pipe must belong to the `Recall.Agent.exe` process from the install folder, running under your account. The extension rejects a replaced pipe.
- A query has a size limit. A reply holds only paths, text snippets and metadata.
- Secrets from the list in "Exclusion rules" are never read or shown.
- The index holds the text of your documents. It lives in your user profile under file system permissions. The file itself is not encrypted.

## Where things are

| What | Where |
|---|---|
| Configuration | `%LOCALAPPDATA%\RecallSearch\config.json` |
| Index | `%LOCALAPPDATA%\RecallSearch\index.db` (with `-wal` and `-shm` files next to it) |
| Log | `%LOCALAPPDATA%\RecallSearch\recall.log` |
| Installed agent | `%LOCALAPPDATA%\Programs\RecallSearch` |

The log rolls over to `recall.log.1` when it reaches 5 MB.

To clear the index, stop the agent and delete `index.db`, `index.db-wal` and `index.db-shm`. The agent rebuilds the index at the next start.

## Run without installing

The agent can make one pass and exit. This is handy for checks and measurements:

```powershell
Recall.Agent.exe --once --db C:\temp\test.db --root D:\docs --workers 4
```

- `--once` - one pass, then exit.
- `--root PATH` - a root folder instead of the configured ones (repeat the option for more folders).
- `--db PATH` - another index file. With this option the agent does not take the single-instance guard, so the installed agent keeps running.
- `--workers N` - how many files to process at the same time.

The global exclusion patterns come from `config.json` in this mode too.

> The scripts in the next two sections are in the `scripts` folder of the repository. The release archive does not include them. Clone the repository to use them. The change-journal service also needs the .NET SDK 10.

## Defender exclusion (optional)

The agent opens every new or changed file, and Defender scans those reads. To keep Defender from loading the CPU, add the agent process to its exclusions. Run in PowerShell as administrator:

```powershell
.\scripts\defender-exclusion.ps1
```

The script excludes only the process `Recall.Agent.exe`. It does not touch folders. To remove the exclusion, pass `-Remove`. The script starts nothing by itself and does not raise its rights.

## Change-journal service (optional, once, as administrator)

Without it, the agent skips unchanged folders by the write time of the directory and walks everything once a week. The service adds exact information about files overwritten in place while the agent was off, so the weekly full walk is no longer needed. Install it in PowerShell as administrator:

```powershell
.\scripts\install-usn-service.ps1
```

The service runs as SYSTEM, reads the NTFS journal and stores nothing. Its known limits: for a root that every user can read (for example `C:\`) it returns the names of changed files without checking the folder rights of the caller, and any local user can make it fail or stop answering. Install it only on a personal computer with one user, and index roots inside your own profile. Without the service the agent falls back to a normal scan. To remove it, pass `-Remove`. Restart the agent after the installation.

## Semantic search

Semantic search finds files by meaning, not by letters: the query "terminal window" finds a screenshot of a terminal. It works for text and for images. It is off by default.

Turn it on in the extension settings, card "Semantic search device":

| Value | What it is |
|---|---|
| `off` | Text search only. |
| `cpu` | The multilingual-e5-small model (about 123 MB). It runs on any processor. The default similarity threshold is 0.84. |
| `npu` | EmbeddingGemma 2 on an AMD NPU. It needs a separate server and a model bundle, see [tools/npu-embed-server](../tools/npu-embed-server/README.md). The default threshold is 0.65. It embeds images too. |

Restart the agent after you change the device. The agent downloads the `cpu` model from Hugging Face into `%LOCALAPPDATA%\RecallSearch\models` and verifies its SHA-256. This is the only network request in the project, and it happens only if you select `cpu`.

How it works:

- The model loads while vectors are computed and while a query is answered. It unloads after 60 seconds of idle time.
- New vectors are computed in the background every 20 minutes when there is new data. You can change the interval in the settings.
- The first pass over a large collection takes hours. The progress is visible in the card "Indexing progress".
- The similarity threshold is in the settings, card "Semantic search threshold". The higher the number, the fewer and more reliable the semantic results.
- If the model is not ready, search works with text only.

Speed and memory figures: [embedding measurements](benchmarks/2026-11-plan2-embeddings.md) and [Gemma against E5](benchmarks/2026-11-gemma-vs-e5.md).
