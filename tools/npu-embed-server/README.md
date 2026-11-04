# Сервер эмбеддингов для NPU

`windows_embed_server.exe` держит модель EmbeddingGemma 2 на NPU AMD загруженной и отвечает на строки из stdin. Агент Recall Search запускает его как дочерний процесс на время индексации и закрывает, когда работа закончена или стоит на паузе. Закрытие stdin завершает процесс и освобождает NPU и память.

Модель и порт Taconite для Windows не входят в этот репозиторий. Здесь только пример на Rust, который собирается вместе с ними.

## Сборка

Нужны: порт `embeddinggemma2-windows-taconite`, заголовки XRT и Visual Studio Build Tools. Пути задаются переменными окружения, в скрипте ничего не зашито.

```cmd
set TACONITE_DIR=<папка embeddinggemma2-windows-taconite>
set XRT_INCLUDE=<папка заголовков XRT, runtime_src\core\include>
set VCVARS=<путь к vcvars64.bat>
set TARGET_DIR=<необязательно: куда cargo кладёт результат>
tools\npu-embed-server\build.cmd
```

Скрипт копирует `windows_embed_server.rs` в `examples` крейта `taconite-embeddinggemma2` и собирает пример. Результат: `<TARGET_DIR>\release\examples\windows_embed_server.exe`.

## Включение в Recall Search

В `%LOCALAPPDATA%\RecallSearch\config.json`:

```json
{
  "embeddingModel": "embeddinggemma2-npu",
  "embeddingDevice": "Npu",
  "npuServerPath": "<полный путь к windows_embed_server.exe>",
  "npuBundlePath": "<папка модели embeddinggemma2-model>"
}
```

Если пути неверны или сервер не запускается, индексация и поиск работают как раньше, только по тексту. Порог похожести для этой модели 0.65 (грубая калибровка, см. `docs/benchmarks/2026-11-gemma-vs-e5.md`). Его можно изменить в настройках расширения.

## Протокол

Одна строка в каждую сторону, UTF-8.

- stderr, один раз после загрузки: `READY dim=768 model_load_ms=<n>`.
- Запрос: `<prompt>\t<text>`. Prompt: `SearchQuery` для запросов, `Document` для текста документов, `-` без prompt. В тексте `\\`, `\n`, `\t`, `\r` означают обратную косую, перевод строки, табуляцию и возврат каретки.
- Ответ: `V <f32> <f32> ...` (768 чисел) или `ERR\t<сообщение>`.

Клиент: `src/Recall.Core/Embed/NpuEmbedder.cs`, `NpuProtocol.cs`, `ProcessLineChannel.cs`.

## Что не сделано

Картинки передаются по одной: запрос с приглашением `@image` и путём к файлу (JPEG или PNG), ответ тот же `V ...`. Группы по 3 картинки (`windows_folder_pipe`) не используются, это возможное ускорение.
