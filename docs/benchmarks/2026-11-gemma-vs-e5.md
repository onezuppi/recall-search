# EmbeddingGemma 2 (NPU) and E5-small (CPU), 2026-11-05

The measurement comes from one machine (AMD Ryzen AI 9 HX 370) with the `tools/Recall.EmbedEval` tool. The corpus is small: 13 Markdown files of this repository, 100 chunks. The developer wrote the queries. They are in `tools/Recall.EmbedEval/queries.json`: 15 queries with a known file (Russian and English) and 3 nonsense queries. This is a rough estimate, not a general result.

Run:

```cmd
Recall.EmbedEval --model multilingual-e5-small --corpus <repository>
Recall.EmbedEval --model embeddinggemma2-npu --corpus <repository> --server <windows_embed_server.exe> --bundle <model folder>
```

## Quality

| Metric | E5-small, CPU (int8) | EmbeddingGemma 2, NPU |
|---|---|---|
| Right file at rank 1 | 8 of 15 | 12 of 15 |
| Right file in the top three | 11 of 15 | 15 of 15 |
| MRR | 0.657 | 0.878 |
| Score of the right file: min / median | 0.804 / 0.859 | 0.675 / 0.762 |
| Best score of a nonsense query | 0.787 - 0.830 | 0.586 - 0.618 |

- **Separation.** With E5, nonsense queries (up to 0.830) score higher than some right ones (from 0.804). A threshold cannot remove the noise without losing right files. With Gemma, the weakest right result (0.675) is above the best noise (0.618). A threshold of 0.65 separates them on these 18 queries.
- **Languages.** Of 8 Russian queries, Gemma puts the right file at rank 1 in 6, and E5 in 4. With E5, four Russian queries gave ranks 3, 4, 5, and 8.
- **Threshold.** `MinSimilarity` for Gemma is set to 0.65. This value was tuned on the same 18 queries. It needs a check on other data.

## Speed

| Metric | E5-small, CPU | EmbeddingGemma 2, NPU |
|---|---|---|
| Indexing | 56 ms per chunk (17.7 per second) | 321 ms per chunk (3.1 per second) |
| Query with ranking | 9 ms | 172 ms |

Speed in this measurement is worse than in the previous one (167 ms per chunk for Gemma, 27 ms for E5). Here, indexing ran in one process right after the build. A running agent and other processes loaded the machine. This is not a repeat measurement under the same conditions. Do not compare these numbers with the earlier ones.

## Conclusion

Gemma searches noticeably better, especially between Russian and English. The cost: 5-6 times slower indexing and about 2 GB of memory for the NPU process. A query time of 172 ms is acceptable for search. Gemma needs an AMD NPU and a local Taconite build. So E5 stays the default model for other machines.

## Images (Gemma 2 on the NPU), 2026-11-05

The server accepts an `@image` request with a path to a JPEG or PNG. It returns the image vector in the same space as text. Measurement: `Recall.EmbedEval --model embeddinggemma2-npu --images <screenshots folder>`. The right answer comes from the file name: ShareX names a screenshot `приложение_случайные-буквы.jpg`, and a query about a terminal is right for the screenshots `WindowsTerminal`, `Termius`, and `OpenCode`. The tool does not open or show the images. The sample has 152 images from 19 applications (up to 8 per application, plus `chrome`, `browser`, and `msedge` as outsiders). The queries are in `tools/Recall.EmbedEval/image-queries.json`: 11 queries with a known answer (Russian and English) and 3 nonsense queries.

| Metric | Value |
|---|---|
| Time per image | 1190 ms |
| Right class at rank 1 | 7 of 11 |
| In the top three | 9 of 11 |
| MRR | 0.746 |
| Score of right images: median / 90th percentile | 0.626 / 0.683 |
| Score of wrong images: median / 90th percentile | 0.591 / 0.644 |
| Best result of nonsense queries | 0.583 - 0.624 |

- **Threshold.** The best right image of each query scores from 0.653. Nonsense queries give up to 0.624. The image threshold is set to 0.64. It keeps the best images of a class and cuts the noise. The other right images (median 0.626) fall under the threshold. Only OCR text finds them.
- **What this means.** Queries about the look of a window ("терминал", "документ Word") work fairly well. But for the queries "файловый менеджер" and "терминал", the best wrong result scored higher than the right one. This is a search by overall look, not by a caption. OCR still finds the exact text on a screenshot.
- **Cost.** About 1.2 seconds of NPU time per image. 4500 screenshots in the ShareX folder take about an hour and a half of background work. The `embedImages` parameter in `config.json` turns this mode off.
- **Groups of 3 (2026-11-04).** The agent sends a page of images in one `@images` request. The server decodes them in parallel and runs them through the NPU three at a time (`embed_rgb_pipelined`). On 36 ShareX screenshots, 2 rounds: one at a time took 960 and 1040 ms per image, groups took 784 and 848 ms. That is about 19% faster. The vectors are identical (the response hash matched in all modes). The port decisions, buffer reuse and zero padding of the inputs, were built into the server from the `perf/host-pipeline` branch.
- **Limits.** The sample is small, the queries are mine, and the right answers come from the application name. The server does not read WEBP, BMP, GIF, TIFF, and HEIC. For these, only OCR remains.
