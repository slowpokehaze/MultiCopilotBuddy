# Multi Copilot Buddy (MCB)

Мульти-аккаунт менеджер для CopilotBuddy 1.6.7.6 (WotLK 3.3.5a).
Порт функционала WowManager — см. `..\plan.md`.

## Структура

```
src/
  MCB.Core/     # Модели Account/Character/Session, JSON-хранилище, DPAPI, roster-sync
  MCB.Cli/      # Консольная утилита: selftest + управление аккаунтами без UI
  MCB.Manager/  # (Этап 0, в работе) WPF-вкладка «Multi Account»
  MCB.Patch/    # (Этап 2) Не нужен: AwesomeWotlkLib.dll (MIT) уже открыта и развёрнута
  MCB.Addon/    # (Этап 3) Lua-аддон MCBuddy (порт Core.lua)
thirdparty/
  wow-autologin/          # ПОЛНЫЙ открытый исходник WowManager (MIT, Zaifat):
                          # launcher.py, awesome_wotlk_src (патч-DLL, C++), addon/WowManager
  copilotbuddy-master.zip # Полный исходник CopilotBuddy (включая UI/) — для встраивания вкладки
tests/
```

## Хранилище

`Settings\MultiAccount.json` (camelCase, UTF-8). Пароли — DPAPI
(`CryptProtectData`, CurrentUser) в base64, в файле только шифроблоб.

## Статус (Этап 0)

Готово, проверено selftest (17/17 PASS, 2026-11-19):

- `MCB.Core`: модели `AccountModel/CharacterModel/SessionModel`, JSON-хранилище
  `Settings\MultiAccount.json` (camelCase, атомарная запись), DPAPI через
  P/Invoke crypt32 (CurrentUser + своя энтропия), tombstone-семантика скрытых
  персонажей, `RosterSync` (автопополнение, порядок первых появлений).
- `MCB.Cli`: `selftest` + команды add-account / set-password / add-char /
  hide-char / move-char / list.
- `MCB.Manager`: каркас окна «Multi Account» (WPF, UI кодом — без XAML,
  т.к. на машине нет SDK/markup-компилятора). Standalone exe.
- Готовые сборки лежат в `build/` (MCB.Cli.dll, MCB.Manager.dll + runtimeconfig).

Не готово: встраивание окна как вкладки CopilotBuddy (нужна пересборка
CopilotBuddy из исходников upstream) и CI. git на машине отсутствует —
репозиторий пока без VCS.

## Запуск (без SDK)

```
C:\Program Files (x86)\dotnet\dotnet.exe build\MCB.Cli.dll selftest
C:\Program Files (x86)\dotnet\dotnet.exe build\MCB.Manager.dll
```

Конфиг: аргумент `--config <path>`, переменная `MCB_CONFIG`, иначе
`..\Settings\MultiAccount.json` от папки бота.

## Сборка (на этой машине)

SDK нет — только runtime `C:\Program Files (x86)\dotnet` (10.0.15 x86).
Компиляция ведётся Roslyn-тулсетом (`microsoft.net.compilers.toolset`,
`tasks/netcore/bincore/csc.dll`) по рецепту из `plan.md` §Этап 0:

```
dotnet csc.dll /noconfig /target:exe /langversion:12 /reference:<net10 shared framework DLLs> src\...\*.cs
```

`*.csproj` включены для сборки на машине с SDK (`dotnet build`).

## CLI

```
dotnet MCB.Cli.dll selftest                  # 49 offline-тестов (DPAPI, хранилище, launcher, sessions, bridge)
dotnet MCB.Cli.dll add-account <login> <wowPath> [realm]
dotnet MCB.Cli.dll set-password <login>      # пароль скрытым вводом (DPAPI)
dotnet MCB.Cli.dll set-totp <login>          # TOTP-секрет 2FA (DPAPI)
dotnet MCB.Cli.dll set-realm <login> <realmlist> [realmname]
dotnet MCB.Cli.dll add-char <login> <name> [realm] [class]
dotnet MCB.Cli.dll hide-char <login> <name>  # tombstone: не вернётся при автопополнении
dotnet MCB.Cli.dll list
dotnet MCB.Cli.dll launch <account> [char]   # autologin.json + запуск Wow.exe (DLL логинит сама)
dotnet MCB.Cli.dll switch <account> <char>   # смена персонажа через перезапуск
dotnet MCB.Cli.dll stop <account>
dotnet MCB.Cli.dll sessions
dotnet MCB.Cli.dll watch [--interval 5]      # мониторинг сессий + автоперезапуск (Ctrl+C — стоп)
dotnet MCB.Cli.dll set-loader <loaderExe> <windowTitle> <buttonText>  # запуск через внешний лоадер
dotnet MCB.Cli.dll clear-loader              # убрать лоадер (прямой запуск)
dotnet MCB.Cli.dll set-session <account> [--bot <имя>] [--profile <путь>]
                             [--routine <рутина>] [--no-attach|--attach]
dotnet MCB.Cli.dll ping <account>            # статус через MCBBridge (имя|реалм|switch)
dotnet MCB.Cli.dll switch-fast <account> <char>  # смена без перезапуска (мост), fallback — перезапуск
dotnet MCB.Cli.dll scan-roster [--wtf <путь>]    # рорстер из SavedVariables без запуска клиента
dotnet MCB.Cli.dll roster                    # таблица персонажей из конфига
dotnet MCB.Cli.dll parse-lua <файл.lua>      # диагностика: разбор SavedVariables
dotnet MCB.Cli.dll harvest [acc...] [--chars A,B] [--all] [--timeout 15]
                                             # собрать голд по аккаунтам (аддон обходит список сам)
dotnet MCB.Cli.dll stop-all                  # остановить все живые сессии
dotnet MCB.Cli.dll set-schedule <acc> <HH:mm-HH:mm> [days]  # окно ботинга (Mo,Tu,.../all)
dotnet MCB.Cli.dll clear-schedule <acc>      # убрать расписание
dotnet MCB.Cli.dll next-char <acc>           # очередь: следующий персонаж через мост
dotnet MCB.Cli.dll import-wowmanager <characters.json> [--wow <путь>]  # миграция из WowManager
```

## Статус (Этапы 0–6)

Готово (2026-11-19), selftest 82/82 PASS:

- `MCB.Core/Launch/AutologinFile` — протокол autologin.json (wow-autologin):
  UTF-8 без BOM, ключи login/password/realmlist/realmname/character/totp_secret,
  удаление переживший файлов.
- `MCB.Core/Launch/WowLauncher` — cooldown, «один клиент на аккаунт»,
  сериализация запусков, WowPath (exe или папка), чистка autologin.json при
  отказах, смена персонажа перезапуском, запуск через внешний лоадер.
- `MCB.Core/Launch/LoaderAutomation` — Win32-автоматизация лоадера
  (порт _orchestrate_loader): окно → кнопка по тексту → WM_SETTEXT → клик
  тремя способами.
- `MCB.Core/Launch/BotAttacher` — аттач CopilotBuddy (/pid /autostart
  /loadprofile /botname /customclass), автодетект CopilotBuddy.exe.
- `MCB.Core/Sessions/SessionManager` — Pulse-мониторинг, детект краша,
  автоперезапуск клиента, автозапуск/перезапуск бота (лимит по времени).
- `MCB.Core/Bridge/BotBridgeClient` + `Plugins/MCBBridge/MCBBridge.cs` —
  быстрый путь Этапа 2: мост MCB↔CopilotBuddy (localhost TCP), смена
  персонажа без перезапуска через `WowManagerSwitchCharacter` + `Logout()`.
  Порт на сессию (47201+), реестр `Plugins/MCBBridge/sessions.json` пишется
  при каждом аттаче. Плагин включается в UI бота один раз.
- `addon/MCBuddy/` — порт аддона WowManager (Core.lua, 1703 строк):
  рорстер в игре, синк друзей, LFG, harvest, `/mcb`. Деплой в
  `Interface\AddOns\MCBuddy\` выполнен. SavedVariables: `MCBuddyDB`.
- `MCB.Core/Roster/` — парсер Lua-SavedVariables (`SavedVariablesParser`),
  collect-поля 1:1 (`RosterReader`), обход WTF-дерева без запуска клиента
  (`RosterScanner`); `MCB.Core/Addon/AddonConfigWriter` — генерация
  `Config.lua` при каждом запуске (рорстер, цвета, флаги).
- `MCB.Core/Harvest/HarvestOrchestrator` — harvest-режим Этапа 4:
  последовательный обход аккаунтов (запуск с `harvest=true` + список
  персонажей → ожидание выхода клиента → скан рорстера). CLI `harvest`,
  в GUI — кнопка Harvest на дашборде.
- `MCB.Manager/DashboardWindow` — дашборд: таблица персонажей
  (голд/ГС/ilvl/зона), скан рорстера, harvest, переключатель ru/en
  (`MCB.Core/L10n/Strings`, язык в `Ui.Language`).
- `MCB.Manager/SessionsWindow` — список сессий со статусами (PID клиента/бота,
  рестарты, мост), запуск/стоп, автообновление.
- `MCB.Core/Launch/BotCopyManager` — опциональная копия папки бота на сессию
  (`Launch.BotCopyPerSession`); аудит мульти-сессий — `docs/stage5-audit.md`.
- `MCB.Core/Migration/WowManagerImporter` — импорт из WowManager
  (characters.json; пароли plain/`enc:v1:` DPAPI → перешифровка в DPAPI MCB).
- Расписание (`AccountModel.Schedule`) и очередь персонажей
  (`SessionModel.RotationHours`) обрабатываются `SessionManager`.
- Сборка без SDK — Roslyn-тулсет (csc) через dotnet runtime 10 x86; рецепты
  скриптов сборки (списки исходников, ссылки на DLL) — в `docs/plan.md` §9
  и в истории: `build.ps1`/`build-manager.ps1` в temp (`mcb-build`).

Реальный запуск (требует аккаунт в конфиге): `launch <account> [char]`.
Wow.exe должен быть пропатчен (на этой машине — уже) и рядом должен лежать
`AwesomeWotlkLib.dll`.

Файл настроек по умолчанию: `..\..\Settings\MultiAccount.json` относительно exe;
можно переопределить переменной `MCB_CONFIG` или аргументом `--config <path>`.
