<div align="center">

# Recall Search

**Найдите файл по тому, что вы помните: слово на скриншоте, фраза из PDF, смысл картинки.**

Поиск по тексту и смыслу для Windows. Работает локально, внутри PowerToys Command Palette.

[![Release](https://img.shields.io/github/v/release/onezuppi/recall-search?style=flat-square)](https://github.com/onezuppi/recall-search/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/onezuppi/recall-search/total?style=flat-square)](https://github.com/onezuppi/recall-search/releases)
[![CI](https://img.shields.io/github/actions/workflow/status/onezuppi/recall-search/ci.yml?style=flat-square&label=build)](https://github.com/onezuppi/recall-search/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue?style=flat-square)](LICENSE)
![Windows 10/11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?style=flat-square)

</div>

Recall Search индексирует выбранные папки и ищет в них текст, который вы помните. Он читает текст с картинок и скриншотов (OCR Windows), из PDF, документов Office и текстовых файлов. По желанию добавляется поиск по смыслу: запрос "окно терминала" находит скриншот терминала, даже если слов на нём нет.

Файлы, текст и индекс не покидают компьютер. В проекте нет телеметрии и облачных сервисов.

## Возможности

- **Поиск по фрагменту.** Достаточно помнить часть слова или фразы, минимум три символа.
- **Картинки и скриншоты.** OCR на русском и английском. Картинки внутри DOCX, XLSX и PPTX тоже читаются.
- **Документы.** PDF, DOCX, XLSX, PPTX, TXT, MD.
- **Поиск по смыслу** (по желанию). Модель на процессоре или на NPU AMD. Находит и текст, и картинки.
- **Фильтры:** `in:`, `type:`, `after:`, `before:` и точные фразы в кавычках.
- **Слежение за папками.** Новые файлы попадают в индекс через пару секунд.
- **Умные исключения.** Встроенные правила, `.gitignore`, `.recallignore`. Секреты (`.pem`, `.key`, `.env`, `.kdbx` и другие) не читаются никогда.
- **Не мешает работе.** Агент работает с пониженным приоритетом и ставит индексацию на паузу в полноэкранной игре и презентации.

## Установка

Нужны Windows 10 или 11 и [PowerToys](https://github.com/microsoft/PowerToys) с Command Palette. .NET и SDK не нужны.

1. Включите режим разработчика: Параметры Windows, Система, Для разработчиков, Режим разработчика. Он нужен, потому что расширение пока не подписано.
2. Скачайте `RecallSearch-<версия>-win-x64.zip` со страницы [Releases](https://github.com/onezuppi/recall-search/releases/latest) и распакуйте.
3. Откройте PowerShell в распакованной папке и выполните:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

4. Откройте Command Palette, выполните команду `Reload`, затем введите "Recall Search".

Установщик работает без прав администратора. Он ставит агента в `%LOCALAPPDATA%\Programs\RecallSearch`, регистрирует расширение и добавляет агента в автозапуск текущего пользователя. Отказаться от автозапуска можно ключом `-NoAutostart`. Обновление: распакуйте новый архив и запустите `install.ps1` ещё раз, индекс и настройки сохранятся.

Первый проход по большим коллекциям идёт долго, особенно с поиском по смыслу. Прогресс виден в настройках расширения, карточка "Indexing progress". Текстовый поиск работает уже в процессе.

Удаление:

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
```

Индекс и настройки остаются в `%LOCALAPPDATA%\RecallSearch`. Чтобы удалить и их, добавьте `-RemoveData`.

> Проверьте контрольную сумму архива: она лежит рядом, в файле `.sha256`.

## Как искать

| Запрос | Что находит |
|---|---|
| `счёт 4521` | Файлы, где есть оба фрагмента. |
| `"оплата по договору"` | Точная фраза. |
| `договор in:Downloads` | Только пути с `Downloads`. |
| `накладная type:pdf` | Только PDF. Работают расширения и группы (`images`, `office`, `text`). |
| `пароль after:2026-05` | Файлы, изменённые с 1 мая 2026. |
| `type:png "код подтверждения" after:2026-05-14` | Фильтры и фразы сочетаются. |

Полное описание, правила исключений и все настройки: [руководство](docs/guide.md).

## Поиск по смыслу

Выключен по умолчанию. Включается в настройках расширения, карточка "Semantic search device":

- `cpu`: модель multilingual-e5-small (около 123 МБ, скачивается один раз с Hugging Face). Работает на любом компьютере.
- `npu`: EmbeddingGemma 2 на NPU AMD. Быстрее и считает картинки, но нужны отдельный сервер и пакет модели ([инструкция](tools/npu-embed-server/README.md)).

Подробности и замеры: [руководство](docs/guide.md#поиск-по-смыслу).

## Приватность и безопасность

- Ничего не уходит с компьютера. Единственный сетевой запрос: скачивание модели `cpu` с Hugging Face, и только если вы её выбрали.
- Агент и расширение общаются по именованному каналу `RecallSearch.<SID>`. Доступ есть только у вашей учётной записи. Клиент проверяет, что канал держит именно `Recall.Agent.exe` из папки установки.
- Индекс содержит текст ваших документов. Он лежит в профиле пользователя под защитой прав файловой системы. Шифрования самого файла нет.

О найденной уязвимости сообщайте по правилам из [SECURITY.md](SECURITY.md).

## Сборка из исходников

```powershell
git clone https://github.com/onezuppi/recall-search.git
cd recall-search
dotnet test
.\scripts\build-release.ps1
```

Подробности для разработчиков: [docs/development.md](docs/development.md). Правила участия: [CONTRIBUTING.md](CONTRIBUTING.md). История изменений: [CHANGELOG.md](CHANGELOG.md). Планы: [дорожная карта](docs/roadmap.md).

## Лицензия

[MIT](LICENSE)
