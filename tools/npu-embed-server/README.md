# Embedding server for the NPU

`windows_embed_server.exe` keeps the EmbeddingGemma 2 model loaded on the AMD NPU. It answers lines from stdin. The Recall Search agent starts it as a child process for the time of indexing. The agent closes it when the work is done or paused. Closing stdin ends the process and frees the NPU and memory.

The model and the Taconite port for Windows are not part of this repository. This repository holds only a Rust example that builds together with them.

## Build

You need the `embeddinggemma2-windows-taconite` port, the XRT headers and Visual Studio Build Tools. Environment variables set the paths. The script hardcodes nothing.

```cmd
set TACONITE_DIR=<folder of embeddinggemma2-windows-taconite>
set XRT_INCLUDE=<папка заголовков XRT, runtime_src\core\include>
set VCVARS=<path to vcvars64.bat>
set TARGET_DIR=<optional: where cargo puts the result>
tools\npu-embed-server\build.cmd
```

The script copies `windows_embed_server.rs` into `examples` of the `taconite-embeddinggemma2` crate and builds the example. The result is `<TARGET_DIR>\release\examples\windows_embed_server.exe`.

## Enable in Recall Search

In `%LOCALAPPDATA%\RecallSearch\config.json`:

```json
{
  "embeddingModel": "embeddinggemma2-npu",
  "embeddingDevice": "Npu",
  "npuServerPath": "<full path to windows_embed_server.exe>",
  "npuBundlePath": "<folder of the embeddinggemma2-model bundle>"
}
```

If the paths are wrong or the server does not start, indexing and search work as before, on text only. The similarity threshold for this model is 0.65 (a rough calibration, see `docs/benchmarks/2026-11-gemma-vs-e5.md`). You can change it in the extension settings.

## Protocol

One line in each direction, UTF-8.

- stderr, once after loading: `READY dim=768 model_load_ms=<n>`.
- Request: `<prompt>\t<text>`. Prompt is `SearchQuery` for queries, `Document` for document text, and `-` for no prompt. In the text, `\\`, `\n`, `\t` and `\r` mean backslash, line feed, tab and carriage return.
- Response: `V <f32> <f32> ...` (768 numbers) or `ERR\t<message>`.

Client: `src/Recall.Core/Embed/NpuEmbedder.cs`, `NpuProtocol.cs`, `ProcessLineChannel.cs`.

## Not done

Images go one at a time. The request has the prompt `@image` and a file path (JPEG or PNG). The response is the same `V ...`. Groups of 3 images (`windows_folder_pipe`) are not used. They are a possible speedup.
