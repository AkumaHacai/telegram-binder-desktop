# Telegram Binder

[English](README.md) · [简体中文](README.zh.md) · [فارسی](README.fa.md)

Сохраняй готовые ответы и вставляй их в Telegram Desktop короткими командами, например `/ответ`. Биндер заменяет команду в черновике. Сообщение отправляешь ты.

## Скачать

Выбери файл для своей версии Windows.

| Windows | Скачать |
| --- | --- |
| x64 (большинство ПК с Intel и AMD) | [TelegramBinder-win-x64.exe](https://github.com/AkumaHacai/telegram-binder-desktop/releases/latest/download/TelegramBinder-win-x64.exe) |
| x86 (32-битная Windows) | [TelegramBinder-win-x86.exe](https://github.com/AkumaHacai/telegram-binder-desktop/releases/latest/download/TelegramBinder-win-x86.exe) |
| ARM64 (Windows на Arm) | [TelegramBinder-win-arm64.exe](https://github.com/AkumaHacai/telegram-binder-desktop/releases/latest/download/TelegramBinder-win-arm64.exe) |

Запусти `.exe` вместе с Telegram Desktop. .NET уже входит в сборку. Для запуска нужен [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/).

## Как пользоваться

1. Нажми **+ Команда**, задай команду и текст ответа, затем нажми **Создать команду**. У одной команды может быть несколько вариантов.
2. В чате Telegram введи команду и нажми пробел. Префиксы `/`, `.` и `!` взаимозаменяемы.
3. Если открыт список подсказок или вариантов, выбери строку стрелками и нажми Tab или Enter. Можно щёлкнуть по строке. Esc закрывает список.

Нажми Ctrl+Shift+B в поле сообщения Telegram, чтобы скопировать весь черновик в текущий вариант ответа. Вставка кастомных эмодзи зависит от Telegram. HTML-списки из резервных копий веб-версии вставляются как текст с маркерами.

При закрытии окна биндер остаётся в трее. Фильтр рядом с поиском открывает избранное и папки. В настройках можно управлять папками и менять язык интерфейса. Доступны русский, английский, упрощённый китайский и фарси. Кнопка **?** открывает справку.

**Импорт JSON** принимает резервные копии v1 и v2 и заменяет текущую библиотеку после подтверждения. **Экспорт JSON** сохраняет копию. Шаблоны лежат в `%LOCALAPPDATA%\TelegramBinderDesktop\templates.json`.

## Сборка из исходников

Установи .NET 10 SDK и выполни команду в корне репозитория.

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o artifacts/win-x64
```

Для других сборок укажи `win-x86` или `win-arm64`. Аргумент `--self-test` запускает проверку команд и форматов буфера обмена.

[Уведомления о сторонних компонентах](THIRD-PARTY-NOTICES.txt)
