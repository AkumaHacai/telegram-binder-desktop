# Telegram Binder

[English](README.md) · [Русский](README.ru.md) · [简体中文](README.zh.md)

پاسخ‌های پرکاربرد را ذخیره کنید و با دستور کوتاهی مثل `/reply` در تلگرام دسکتاپ وارد کنید. برنامه دستور را در پیش‌نویس با پاسخ جایگزین می‌کند و ارسال پیام با خودتان است.

## دانلود

| نسخهٔ ویندوز | دانلود |
| --- | --- |
| x64 (بیشتر رایانه‌های Intel و AMD) | [TelegramBinder-win-x64.exe](https://github.com/AkumaHacai/telegram-binder-desktop/releases/latest/download/TelegramBinder-win-x64.exe) |
| x86 (ویندوز ۳۲ بیتی) | [TelegramBinder-win-x86.exe](https://github.com/AkumaHacai/telegram-binder-desktop/releases/latest/download/TelegramBinder-win-x86.exe) |
| ARM64 (ویندوز روی Arm) | [TelegramBinder-win-arm64.exe](https://github.com/AkumaHacai/telegram-binder-desktop/releases/latest/download/TelegramBinder-win-arm64.exe) |

فایل `.exe` را در کنار تلگرام دسکتاپ اجرا کنید. فایل دانلودی شامل ‎.NET است و برای اجرا به [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) نیاز دارد. رابط کاربری نسخهٔ منتشرشده به زبان روسی است.

## روش استفاده

1. در Binder دستور و متن پاسخ را وارد کنید و روی **Создать команду** (ساخت دستور) بزنید. برای هر دستور می‌توانید چند پاسخ ذخیره کنید.
2. در گفت‌وگوی تلگرام دستور را بنویسید و کلید فاصله را بزنید. هر سه پیشوند `/`، `.` و `!` قابل استفاده‌اند.
3. اگر فهرست پیشنهادها یا پاسخ‌ها باز شد، با کلیدهای جهت‌دار انتخاب کنید و Tab یا Enter را بزنید. می‌توانید روی گزینه هم کلیک کنید. Esc فهرست را می‌بندد.

در کادر پیام تلگرام Ctrl+Shift+B را بزنید تا کل پیش‌نویس در پاسخی که در Binder ویرایش می‌کنید کپی شود. چسباندن ایموجی سفارشی به تلگرام بستگی دارد. فهرست‌های HTML در نسخه‌های پشتیبان وب به صورت متن همراه با نشانه‌های فهرست چسبانده می‌شوند.

با بستن پنجره، Binder در ناحیهٔ اعلان ویندوز می‌ماند. می‌توانید پاسخ‌ها را با پوشه‌ها و موارد دلخواه مرتب کنید. **Импорт JSON** (وارد کردن JSON) پشتیبان‌های v1 و v2 را می‌خواند و پس از تأیید، جایگزین الگوهای فعلی می‌کند. **Экспорт JSON** (خروجی JSON) یک نسخهٔ پشتیبان ذخیره می‌کند. الگوها در `%LOCALAPPDATA%\TelegramBinderDesktop\templates.json` ذخیره می‌شوند.

## ساخت از روی کد منبع

در ویندوز ‎.NET 10 SDK را نصب کنید و این دستور را در پوشهٔ اصلی مخزن اجرا کنید.

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o artifacts/win-x64
```

برای معماری‌های دیگر از `win-x86` یا `win-arm64` استفاده کنید. برنامه را با `--self-test` اجرا کنید تا تطبیق دستورها و قالب‌های کلیپ‌بورد بررسی شوند.

[اطلاعیه‌های نرم‌افزارهای شخص ثالث](THIRD-PARTY-NOTICES.txt)
