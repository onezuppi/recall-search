# Choosing a runtime for embeddings on the NPU, 2026-11-04

Decision: for the AMD NPU we stay on Taconite (EmbeddingGemma 2 through `windows_embed_server`). We do not adopt FastFlowLM (FLM) as the base.
The main path for other users does not change. It is ONNX Runtime on CPU (E5-small). The NPU is an optional provider.

Measurement machine: AMD Ryzen AI 9 HX 370 (NPU2), NPU driver 32.0.20102.4480, Windows 11.

## Why not FLM

| Limitation | What we found | Source |
|---|---|---|
| Only one embedding model | The FastFlowLM organization on Hugging Face has 64 models. The only embedding model is `Embedding-Gemma-300M-NPU2`. `flm list` on the machine also shows one: `embed-gemma:300m`. User uploads in the NPU2 format repeat the same LLMs and the same Gemma. | HF API, `flm list` |
| No image embeddings | FLM handles images only through chat models (Qwen-VL, MiniCPM-V, Gemma 4). They answer with text. There are no vectors in the same space as text. | `flm list`, release v1.0.11 |
| We cannot add E5 or our own models | There is no public guide for adding models to FLM. The kernels are closed (free to use and to distribute unchanged). Dev builds of FastFlow-SDK for Windows exist. We found no conversion docs. | README ROCm/FastFlowLM, ROCm/FastFlow-SDK |
| Embeddings do not run alone | `flm serve embed-gemma:300m` answers "Unsupported model family or non-llm". Embeddings work only as an add-on to a chat model with the flag `--embed 1`. So we also need an LLM (Llama 3.2 1B, about 1.2 GB). | checked on the machine |
| Installation | The MSI is unsigned and installs machine-wide. It needs administrator rights (exit code 1603 without them). | checked on the machine |

We did not test FLM to the end (speed and Russian quality of `embed-gemma:300m` are not measured). We will not test it. The decision rests on the limitations above. We removed the FLM models from the machine.

## Why Taconite

- It is the only path on this machine that gives text and images in one space (Gemma 2, 768 dimensions, bundle `embeddinggemma2-model`, 1.07 GB).
- It already works. The agent has the `TaconiteNpuProvider` provider, the server is `tools/npu-embed-server`, and paths come from the config.
- Measurements (docs/benchmarks/2026-11-plan2-embeddings.md): about 167 ms per text fragment, about 6 fragments per second, about 0.85-1.5 s per image, about 2 GB of memory. CPU with E5-small: about 22 fragments per second. The models differ. We have no direct "NPU versus CPU" comparison on one model.
- Accuracy according to the author's card: cosine with the reference at least 0.999.

## Taconite limitations

- AMD NPU2 only (Strix, Strix Halo, Krackan). It does not load on Phoenix and Hawk Point.
- It is an experimental Windows port. It builds with MSVC and a local `xrt_coreutil.lib` made from the exports of the driver's `xrt_coreutil.dll`. The author gives no Windows support. A driver update can break it.
- The user needs an AMD driver, a ready `windows_embed_server.exe`, Python with Pillow for images, and a 1 GB bundle. So the provider is optional and off by default.

## Other options we found (not chosen)

| Option | Note |
|---|---|
| `amd/embeddinggemma-300m_npu_rai_1.8.0_npu_4K` | Gemma-300M from AMD for Ryzen AI Software (Quark, ONNX Runtime). It needs the SDK. The card lists the language as "English". The license is contradictory. |
| `amd/NPU-Nomic-embed-text-v1.5-ryzen-strix-cpp` | A C++ example for an English model, with instructions for Phoenix and Hawk Point. |
| `peonist-ai/halogen-npu-*` (Qwen3-Embedding, EmbeddingGemma 2) | Strix Halo only, and only inside the halogen-flash-server container. |
| `ItsOkayNow/bge-XDNA1-NPUE-PACKS`, `all-MiniLM-L6-v2-XDNA1-NPUE-PACKS` | For XDNA1. They do not work on the HX 370. |
| `seminse/multilingual-e5-small-openvino-npu-static` | E5-small for the Intel NPU through OpenVINO. The author's measurement on Core Ultra 9: iGPU 19.4 ms, NPU 29.1 ms, CPU 49 ms per text. We did not test it. |
| IRON (`amd/IRON`) | The official way to port your own model (section "Add a model" in CONTRIBUTING.md). Linux. It has an `Encoder` class. There is no ready BERT. |

## What remains

- Intel NPU: an OpenVINO provider with E5-small. Measure it on our corpus.
- Gemma 2 against E5-small on the same queries, and a similarity threshold for Gemma.
- Our own E5 export for the AMD NPU (Ryzen AI Software with Quark, or IRON). Do it only if a measurement shows a gain.
