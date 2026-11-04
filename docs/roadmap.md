# Ideas for the future

This file lists features that are not in any plan yet. Implemented features do not appear here.

## Accepted decisions

- Base: ONNX Runtime on CPU with the E5-small model. It works on any machine.
- Optional: EmbeddingGemma 2 on the AMD NPU through Taconite. It finds noticeably better results (see `docs/benchmarks/2026-11-gemma-vs-e5.md`). It is slower at indexing and needs a local build of the port.
- FastFlowLM does not fit the requirements (see `docs/research/2026-11-npu-runtime.md`).
- OCR stays for exact search over text in images. Gemma image vectors complement it. They search by overall appearance ("terminal window"), not by captions (see the section about images in `docs/benchmarks/2026-11-gemma-vs-e5.md`).
- Text and images only. Audio and video are not indexed (decision of 2026-11-04).

## GPU and NPU devices for embeddings

The config has an `EmbeddingDevice` field. CPU (E5) and AMD NPU (Gemma 2, text and images) work. Not done:

- GPU through DirectML (CUDA is optional).
- Intel NPU through OpenVINO. We cannot test it on this machine. It needs a computer with an Intel NPU.
- E5 on the AMD NPU (Ryzen AI SDK or porting through IRON).
- The image formats WEBP, BMP, GIF, TIFF and HEIC are not read for vectors. Only OCR covers them.
- The NPU power mode Turbo (`xrt-smi configure --pmode turbo`) gives another 4-12% in the port author's measurements. This is a system setting. The agent does not change it.

Before you enable a new device, check vector compatibility (cosine at least 0.999).

## Search on very large indexes

Done: an in-memory vector cache, FTS search without extra joins, and a fast path for short queries (see `docs/benchmarks/2026-11-search-at-scale.md`). Remaining:

- File names are searched by scanning all paths (about 0.5 µs per file). For a million files we need an index on the lowercase path.
- The word search still sorts every document by bm25 in full (about 5 µs per match).
- The measurements ran on 20 thousand files. They were not checked on hundreds of thousands.
