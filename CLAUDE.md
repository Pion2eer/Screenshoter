# CLAUDE.md — Скриншотер

Лёгкий скриншотер для Windows 10/11. Его задача: сделать несколько скринов с подписями,
стрелками и рамками, а потом вставить их все одним Ctrl+V в чат ИИ (Claude, ChatGPT).
Пользователь говорит по-русски, поэтому интерфейс, комментарии и ответы пишутся по-русски.

Главные правила проекта лежат в `.specify/memory/constitution.md`. Прочитай её перед любым
изменением. Ниже краткая выжимка.

## Структура

| Файл | Что делает |
|---|---|
| `src/Program.cs` | Точка входа: включает DPI-awareness, защищает от второго экземпляра, запускает `TrayApp` |
| `src/TrayApp.cs` | Иконка и меню в трее, запуск снимка, автозапуск, отключение системных «Ножниц» |
| `src/KeyboardHook.cs` | `WH_KEYBOARD_LL` в отдельном потоке: Win+Shift+S / PrtSc, отслеживание Ctrl+V |
| `src/Overlay.cs` | Затемнённый оверлей: выделение области, инструменты, отрисовка, итоговый PNG |
| `src/Batch.cs` | Пачка PNG в `%TEMP%\Screenshoter`, буфер обмена (`CF_HDROP` или склейка), сброс пачки |
| `src/Settings.cs` | `%APPDATA%\Screenshoter\settings.ini` |
| `src/Toast.cs` | Уведомление в углу экрана, не забирает фокус |
| `src/Native.cs` | Все P/Invoke |
| `src/app.ico` | Иконка exe и трея (встраивается как ресурс `Screenshoter.app.ico`) |
| `build.bat` | Сборка встроенным в Windows `csc.exe` |

## Жёсткие ограничения

- Только .NET Framework 4.8 + WinForms, никаких NuGet-пакетов и зависимостей.
- Синтаксис **C# 5**, потому что так собирает встроенный `csc.exe`: нельзя `$""`, `?.`,
  `nameof`, `=>`-члены, `out var`, инициализаторы автосвойств.
- Только API, которые есть в .NET Framework 4.8. Нельзя компилировать против библиотек Mono:
  однажды так получилась перегрузка `TrimEnd()`, которой нет на Windows.
- Файлы `.cs` хранятся в UTF-8 с BOM и с окончаниями строк CRLF.
- Обработчик хука клавиатуры не блокируется: работу он отправляет в UI-поток через `BeginInvoke`.
- Экран и итоговый PNG рисуются одной и той же функцией `DrawShape` / `DrawAllShapes`.
- В буфер обмена не кладём текст, только файлы или картинку.
- `Screenshoter.exe` не коммитим (он в `.gitignore`).

## Сборка в облаке / Linux

```bash
# эталонные сборки Microsoft (один раз)
curl -sSL -o ref.nupkg https://api.nuget.org/v3-flatcontainer/microsoft.netframework.referenceassemblies.net48/1.0.3/microsoft.netframework.referenceassemblies.net48.1.0.3.nupkg
unzip -q ref.nupkg -d ref && R=ref/build/.NETFramework/v4.8

mcs -langversion:5 -nostdlib -noconfig -target:winexe -codepage:utf8 -optimize+ \
  -r:$R/mscorlib.dll -r:$R/System.dll -r:$R/System.Drawing.dll -r:$R/System.Windows.Forms.dll \
  -win32icon:src/app.ico -resource:src/app.ico,Screenshoter.app.ico \
  -out:Screenshoter.exe src/*.cs
```

На Windows достаточно запустить `build.bat`.

## Проверка

- Сборка выше должна проходить без ошибок.
- Отрисовку проверяем так: в mono + `xvfb-run` создаём `OverlayForm` через reflection,
  заполняем `sel` и `shapes`, вызываем `Finish()` и смотрим на `Result`.
- Хук, буфер обмена, DPI и фокус вне Windows проверить нельзя. Об этом надо прямо сказать
  пользователю и попросить его проверить у себя.
- Если меняется поведение, которое видит пользователь, обнови `README.md`.

## Spec-kit

Проект инициализирован через `specify init --integration claude --script ps`. Команды
`/speckit-specify`, `/speckit-plan`, `/speckit-tasks`, `/speckit-implement` и другие лежат
в `.claude/skills/`. Скрипты в `.specify/scripts/powershell/` требуют PowerShell (`pwsh`).
