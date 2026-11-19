# plan.md — Multi Copilot Buddy (MCB)

Полный функционал мульти-аккаунт менеджера для CopilotBuddy.
Мульти-аккаунт функциональность портируется из **WowManager** (`C:\Program Files\WowManager`) в экосистему **CopilotBuddy 1.6.7.6**.

Дата: 2026-11-19
Статус: в реализации (Этап 0: ядро готово, 2026-11-19; см. MultiCopilotBuddy/README.md)

---

## 1. Анализ текущего состояния

### 1.1 CopilotBuddy 1.6.7.6 (целевая платформа)

HonorBuddy, портированный на WotLK 3.3.5a (Likon69). .NET 10, WPF + MahApps.Metro.

| Компонент | Что есть |
|---|---|
| `CopilotBuddy.exe/.dll` | Бот-движок: один бот-коннект **на один процесс WoW** (`Searching for WoW process...` → attach по PID). Публичный API: Bots, Routines, Quest Behaviors, Plugins |
| `Settings\` | Настройки **per-character**: `CharacterSettings_<имя>.xml`, `LevelbotSettings_<имя>.xml`, `PartyBotSettings_<имя>.xml` и т.д. |
| `CBRelog\HBRelog.exe` | Форк HBRelog — внешний релоггер (запуск/релог аккаунтов), но он **не интегрирован** в CopilotBuddy и не знает о его настройках |
| `System.ServiceModel.NetNamedPipe` | Внутренний IPC (named pipes) — можно использовать как канал сессий |
| Клиент WoW | `C:\Games\WoW 3.3.5a\...` — обычный 3.3.5a (build 12340), x86 |

Ключевое ограничение: CopilotBuddy сейчас — **одна сессия** = один процесс WoW = один персонаж. Мультиаккаунтности нет: нет хранилища аккаунтов, нет управления N клиентами, нет рорстера, нет автозапуска по расписанию.

### 1.2 WowManager → **открытый исходник: github.com/Zaifat/wow-autologin** (обновлено 2026-11-19)

Установленный `C:\Program Files\WowManager` (Manager_WOW.exe, PyInstaller) — это сборка
открытого проекта **wow-autologin** (MIT, @Zaifat). Весь исходник доступен и скопирован
в `MultiCopilotBuddy/thirdparty/wow-autologin/`:

- `launcher.py` (~300 KB, Python) — полный исходник менеджера: хранилище аккаунтов,
  `LAUNCH_COOLDOWN_SEC`, внешние лоадеры (Win32/UIA), WTF-бэкапы, генерация Config.lua,
  harvest-оркестрация.
- `awesome_wotlk_src/` — **исходник патч-DLL** (C++, CMake, Microsoft Detours):
  `AwesomeWotlkPatch` (патчит IAT Wow.exe → LoadLibrary "AwesomeWotlkLib.dll",
  маркер-байты в `Patch.h`) + `AwesomeWotlkLib` (Entry/Hooks/CommandLine/CharListJson/UnitAPI).
  **Проблема «DLL закрыта в exe» из старой редакции плана снята.**
- `addon/WowManager/Core.lua` (~74 KB) — полный исходник аддона (collect, social sync,
  LFG, harvest, релог-протокол, окно рорстера, `/wm`).

### 1.3 CopilotBuddy upstream — полный исходник доступен

`github.com/Likon69/CopilotBuddy` (master, 962 коммита) — **весь C#-исходник, включая
UI/** (WPF-окна), `CopilotBuddy.csproj` (`dotnet build -c Release`, .NET 10, WPF, x86).
Копия: `MultiCopilotBuddy/thirdparty/copilotbuddy-master.zip`, распакована в scratch.
Это разблокирует пункт Этапа 0 «вкладка Multi Account в CopilotBuddy»: окно MCB
добавляется в исходник и бот пересобирается целиком. На этой машине нет SDK —
вариант: portable-SDK zip (без установки) или сборка на другой машине/CI.

**Протокол логина (проверено по исходникам):**
менеджер пишет `autologin.json` рядом с Wow.exe — `{"login", "password", "realmlist",
"realmname", "character", "totp_secret"?}` (+ плоские ключи `cvar_<Name>`, anti-AFK);
DLL читает файл **и сразу удаляет** его (там пароль открытым текстом); TOTP пересчитывается
в момент появления диалога — код не протухает. Параметры читаются и из argv Wow.exe
(argv приоритетнее файла). Правило живучести: «выживший autologin.json без запущенного
клиента = запуск умер рано». Переключение персонажа без перезапуска — Lua-глобал
`WowManagerSwitchCharacter(name)`; релог — `WowManagerDB.__relog = {account, char, at}` + `Quit()`.

**Локальная машина: `C:\Games\WoW 3.3.5a\Wow.exe` УЖЕ пропатчен** (маркер найден),
`AwesomeWotlkLib.dll` уже лежит рядом с exe. Значит DLL-часть Этапа 2 на этой машине
уже развёрнута — MCB нужна только запись autologin.json + запуск.

**Менеджер (desktop app):**
- Хранилище аккаунтов: аккаунт (логин/пароль открытым текстом — в порте исправим на DPAPI) → сгруппированные персонажи; учётные данные копируются вниз на персонажа; «скрытые» персонажи (пользователь удалил из списка — они не возвращаются); порядок = порядок первых появлений; один запуск клиента на аккаунт.
- Запуск клиента: `--launch "<значение>"` в CLI; cooldown запусков; поддержка **внешних лоадеров** (Win32-автоматизация: найти окно лоадера, вбить путь к Wow.exe в path-edit, нажать кнопку запуска тремя способами по очереди) — критично для серверов с лоадером (WoW Circle и т.п.).
- Патч-DLL: `deploy_patch` / `is_wow_patched` / `PATCH_MARKER`; общается с менеджером по localhost (сообщение `LAUNCH` стартует игру), репортит **рорстер аккаунта при первом логине**.
- Чтение данных напрямую из `WTF\Account\...\SavedVariables\WowManagerDB.lua` с диска — без запуска клиента.
- UI: таблица рорстера с настраиваемыми колонками (`DEFAULT_COLUMNS`/`ROSTER_COLS`; статические поля: класс/аккаунт; игровые поля; `cur:<имя валюты>`), hover-карточки, портреты по раса/пол из файлов клиента, табы с сохранением ширин, агрегаты по аккаунту.

**Патч-DLL (в клиенте):**
- Экспортирует глобал `WowManagerSwitchCharacter(name)` — переключение персонажа **без перезапуска** клиента (если тот же аккаунт + тот же реалм); при выходе — сама управляет экраном выбора персонажа.
- Различает логаут, инициированный игроком (`!`-маркер) и сервисный; истекает запрос через минуту.

**Lua-аддон (портится почти как есть):**
- Сбор данных персонажа в SavedVariables: имя, реалм, уровень, класс/раса/пол/фракция, зона, гильдия, голд, XP/rested, ачивпоинты, спек+таланты, средний ilvl, GearScore (свой пересчёт как fallback), все валюты, рейдовые локи, профессии, место в сумках, дневалы+время ресета, аренные команды и игры за неделю, выполненные квесты в логе, played time. Отложенный дособор ~16 с после логина (голд/валюты приходят позже).
- Релог через `WowManagerDB.__relog = {account, char, at}` + `Quit()`; менеджер замечает закрытие Wow.exe и перезапускает нужного персонажа/аккаунт.
- Окно «Менеджер персонажей» (рорстер + детали + кнопка «Зайти», быстрый путь без перезапуска vs с перезапуском), миникарт-кнопка с меню перезахода (Персонажи / Аккаунты).
- **Синхронизация друзей/игнора** по реалм+фракция: общий список в account-wide SavedVariables, tombstones (`gone`), баны имён, 5-секундное подтверждение удаления, cap 50, отказ-счётчик, opt-out per character.
- **Поиск группы**: парсер чат-объявлений (ICC/RS/TOC/VOA/ULD/NAXX/OS/ONY/EOE, 10/25, героик, роли Т/Х/Д, парсер ГС из текста «гс 5.5», «от 5700к»), фильтры, «только где хватает моего ГС», звук на новое объявление, клик → шепот «+ класс спек ГС».
- **Harvest mode**: менеджер запускает аккаунт со списком персонажей; аддон сам собирает данные с каждого, внутриклиентово переключается на следующего, по завершении делает Quit — менеджер переходит к следующему аккаунту.
- Слэш-команды `/wm`, `/wowmanager`.

---

## 2. Цель и целевая архитектура

**Цель:** CopilotBuddy, управляющий произвольным числом аккаунтов/персонажей: рорстер, автозапуск и релог N клиентов, запуск бота на выбранных персонажах (по расписанию или постоянно), сбор данных по всему рорстеру — с UX-функционалом WowManager.

### 2.1 Компоненты

```
MultiCopilotBuddy/
├─ src/
│  ├─ MCB.Manager/          # WPF-приложение (встраивается в CopilotBuddy как новое окно/вкладка
│  │                        #  или отдельный exe поверх того же рантайма)
│  │   ├─ Accounts/         # БД аккаунтов и персонажей
│  │   ├─ Sessions/         # Менеджер процессов WoW + сессий бота
│  │   ├─ Launcher/         # Запуск клиента, поддержка лоадеров, cooldown
│  │   ├─ Patching/         # Деплой патч-DLL, проверка маркера
│  │   ├─ Roster/           # Парсинг SavedVariables, колонки, hover-карточки
│  │   ├─ Harvest/          # Сбор данных по всем аккаунтам
│  │   └─ UI/               # Рорстер, вкладки, портреты, настройки
│  ├─ MCB.Core/             # Общие контракты: модели, IPC-протокол, настройки
│  ├─ MCB.Patch/            # Нативная DLL (C/C++): WowManagerSwitchCharacter,
│  │                        #  репорт рорстера, localhost-канал с менеджером
│  └─ MCB.Addon/            # Lua-аддон (порт Core.lua, переименование в MCBuddy*)
└─ tests/
```

### 2.2 Ключевые решения

| Вопрос | Решение |
|---|---|
| Отдельное приложение или модуль CopilotBuddy? | Модуль CopilotBuddy (новая вкладка «Multi Account»), но ядро (`MCB.Core`, `Sessions`) — без зависимости от UI, чтобы можно было запустить headless. Бот-движок остаётся один, но учится **владеть несколькими сессиями** |
| Несколько копий бота или один многопроцессный? | Фаза 1 — один CopilotBuddy, N сессий (по процессу WoW на сессию, каждая сессия = свой ObjectManager/рenievе). Фаза 2 (fallback) — запуск N копий CopilotBuddy с `--session:<id>` и общим менеджером, если рефакторинг глобального состояния окажется слишком дорогим |
| Пароли | DPAPI (`ProtectedData.CurrentUser`), в отличие от WowManager (там открытый текст) |
| Хранилище | JSON-файл `Settings\MultiAccount.json` (совместимость по духу с текущими XML; SQLite уже в зависимостях — при росте переехать) |
| Lua-аддон | Порт 1:1 логики из `Core.lua` с переименованием `WowManager*` → `MCBuddy*` (несовместимые SavedVariables, чтобы не драться с WowManager, если оба стоят) |
| Патч-DLL | WowManager-овская DLL закрыта в exe; пишем свою с тем же протоколом (см. §4.3) — C, MinGW/MSVC, x86 |

---

## 3. Функциональные требования (порт из WowManager)

### FR-1. База аккаунтов и персонажей
- Аккаунт: логин, пароль (зашифрован), сервер/реалм по умолчанию, путь к Wow.exe (для серверов с разными папками клиента).
- Персонаж: имя, реалм, аккаунт-владелец, класс (цвет), флаг «скрыт».
- Автопополнение: персонажи, замеченные в SavedVariables или репорте патча, добавляются к своему аккаунту; существующим дозаполняются недостающие поля; удалённые пользователем не возвращаются (tombstones).
- Редактирование вручную: добавить/скрыть/перенести/переупорядочить (drag-n-drop).

### FR-2. Запуск клиентов
- «Запустить персонажа» / «Запустить аккаунт»: один клиент на аккаунт; если клиент аккаунта уже жив — быстрая внутриклиентовая смена персонажа (патч), иначе полный запуск.
- Cooldown запусков (порт `LAUNCH_COOLDOWN_SEC`), сериализация запусков (не два лоадера одновременно).
- Поддержка внешних лоадеров: профиль лоадера (имя окна, title, поле пути, текст кнопки), Win32-автоматизация `FindWindow`/`SendMessage`/UIA, таймауты, fallback-методы нажатия (порт `orchestrate_loader`).
- CLI: `CopilotBuddy.exe --launch "<account>/<char>"` — запуск сразу в бота.

### FR-3. Патч-DLL и IPC
- Деплой в папку клиента + проверка маркера версии (`PATCH_MARKER`); обновление при несовпадении.
- Канал с менеджером: localhost TCP/именованный пайп; сообщения:
  - `HELLO(pid, accountGuess)` — клиент запущен;
  - `ROSTER(account, [characters…])` — репорт рорстера при первом логине;
  - `LAUNCH(account, character)` — менеджер → DLL: следующий персонаж после релога;
  - `SWITCH(character)` — менеджер → DLL: внутриклиентовая смена.
- Экспорт глобала в Lua: `MCBuddySwitchCharacter(name)`; семантика `!`-маркера (игрок ушёл сам) и minute-expiry — сохранить как в оригинале.

### FR-4. Релог и живучесть
- Релог по выбору пользователя и автоматический: crash WoW, disconnect, детект «клиент умер, а пайп жив» (порт правила «reads it, so one surviving with no client running means the launch died»).
- Протокол `__relog`-переменной: аддон пишет `{account, char, at}` и делает `Quit()`; менеджер, увидев закрытие процесса, перезапускает цель. `at`-таймаут для устаревших запросов.
- Двухпутевой релог из аддона (как в оригинале): тот же аккаунт+реалм → `Logout()` + смена патчем; иначе → запись `__relog` + `Quit()`.
- CBRelog: не трогаем, помечаем как legacy/альтернативу (в FAQ плана), вся его работа уходит в `Sessions`.

### FR-5. Сессии ботов
- Каждая сессия: процесс WoW + привязка персонажа + выбор бота (Grind/Party/Quest…), профиля и рутин (настройки per-character уже есть — переиспользуем `CharacterSettings_<name>.xml`).
- Состояния сессии: Idle / Launching / CharSelect / InGame / Botting / Crashed / Disabled; живой статус-лист.
- Расписание (фаза 2): «персонаж X ботит с 20:00 до 02:00», очередь персонажей на аккаунт.

### FR-6. Рорстер и данные персонажей
- Парсер SavedVariables (`WTF\Account\<acc>\SavedVariables\MCBuddyDB.lua`) прямо с диска — без запуска клиента.
- Все поля из §1.2 (collect) как колонки/данные; настраиваемые колонки, включая виртуальные `cur:<валюта>` и агрегаты по аккаунту.
- Hover-карточки с тем же набором строк, что строит менеджер WowManager.
- Портреты: извлечение из файлов клиента по раса/пол (+мужской портрет как fallback).

### FR-7. Harvest mode
- «Собрать данные по всем»: для каждого аккаунта → запуск с передачей списка персонажей (`Config.harvestChars/harvest/harvestWait` в Config.lua), аддон проходит список, менеджер ловит Quit и идёт дальше. Прогресс и отмена.

### FR-8. Внутриигровой аддон (порт Core.lua)
- Окно рорстера в клиенте, миникарт-кнопка + меню перезахода, детали персонажа.
- Синхронизация друзей/игнора (полный алгоритм tombstones/bans/confirm/cap/fail-counter, opt-out per char, вкладка «Удалённые»).
- Поиск группы: парсер объявлений, фильтры по рейдам, «хватает ли мой ГС», звук, шепот-шаблон. Включается флагом из менеджера (`Config.lfg`).
- `/wm`-команды → `/mcb`.

---

## 4. Контракты данных

### 4.1 `Settings\MultiAccount.json` (новое)
```json
{
  "accounts": [
    {
      "login": "user", "password": "<dpapi-blob>", "wowPath": "C:\\Games\\WoW 3.3.5a\\...",
      "realm": "WoW Circle 3.3.5a x1", "order": 0,
      "characters": [
        { "name": "Pipyau", "realm": "...", "class": "PALADIN", "hidden": false, "order": 0 }
      ]
    }
  ],
  "launch": { "cooldownSec": 20, "loader": null },
  "roster": { "columns": [...], "columnWidths": {...} },
  "lfg": { "enabled": false },
  "social": { "syncFriends": true }
}
```

### 4.2 `addon\MCBuddy\Config.lua` (генерируется менеджером на каждый запуск, как в WowManager)
```lua
MCBuddyConfig = {
  currentAccount = "...",            -- под каким аккаунтом запущен клиент
  characters = { {name=,realm=,account=,isAccount=,color=,lvl=,gs=,gold=,info={...}} },  -- рорстер для меню/окна
  hoverCard = true, showMinimap = true, syncFriends = true, lfg = false,
  harvest = false, harvestChars = { "A", "B" }, harvestWait = 12,
}
```

### 4.3 SavedVariables (аддон → менеджер)
- `MCBuddyDB[realm.char]` — записи collect (поля см. §1.2; хранить совместимо по именам: `name, realm, level, class, race, sex, faction, zone, subzone, guild, gold, xp, xpMax, rested, achPoints, spec, talents, ilvl, gs, currencies, honor, arena, locks, profs, bagFree, dailyDone, dailyMax, dailyResetAt, arenaTeams, arenaGames, questsDone, questsTotal, played, updated`).
- `MCBuddyDB.__relog`, `MCBuddyDB.__social` — служебные, структуры 1:1 из Core.lua.

### 4.4 IPC-протокол патча
JSON over localhost TCP (порт из диапазона, пишется в конфиг патча); сообщения `HELLO | ROSTER | LAUNCH | SWITCH | PING`. Обратно совместимой с WowManager DLL быть не обязана — аддон взаимодействует только через глобал Lua.

---

## 5. Этапы реализации

### Этап 0 — Каркас (1–2 недели) — В РАБОТЕ (ядро готово 2026-11-19)
- [x] Структура `src/MCB.*` — создана в `MultiCopilotBuddy/` (CI отложен: SDK на машине нет; сборка через Roslyn-тулсет, рецепты в `MultiCopilotBuddy/README.md`)
- [x] Модели `Account/Character/Session` + JSON-хранилище с DPAPI (`MCB.Core`; selftest 17/17 PASS: DPAPI roundtrip, пароль не попадает в JSON открытым текстом, tombstones, RosterSync, атомарная запись)
- [x] Каркас WPF-окна «Multi Account» (`MCB.Manager`, standalone exe, UI кодом без XAML)
- [ ] Встроить окно как вкладку CopilotBuddy (MahApps) — нужна пересборка CopilotBuddy из исходников upstream
- **DoD:** аккаунты добавляются и сохраняются — ✅ (CLI + selftest; GUI-окно готово к запуску)

### Этап 1 — Запуск и релог (2–3 недели) — УПРОЩЁН (wow-autologin) — ГОТОВ 2026-11-19
- [x] `Launcher` (`MCB.Core/Launch`): `AutologinFile` пишет `autologin.json` (login/password/realmlist/realmname/character/totp_secret; UTF-8 без BOM — парсер DLL ждёт `{` первым байтом); DLL сама вбивает учётку, реалм, персонажа, 2FA. Win32-ввод логина не нужен
- [x] `WowLauncher`: cooldown (порт `LAUNCH_COOLDOWN_SEC`), «один клиент на аккаунт» (по живым PID сессий, имя образа против переиспользования PID), сериализация запусков (lock), чистка переживший autologin.json (правило «запуск умер»), WowPath = exe или папка
- [x] `Sessions` (`MCB.Core/Sessions/SessionManager`): Pulse-опрос, детект краша/закрытия, автоперезапуск с лимитом (5 подряд, сброс при живом клиенте), статусы в `MultiAccount.json`
- [x] CLI: `launch <account> [character]`, `switch <account> <character>` (смена перезапуском), `stop`, `sessions`, `watch [--interval]` (мониторинг+автоперезапуск); `set-totp`, `set-realm`
- [x] selftest: +18 offline-тестов лоунчера/сессий (итого 35/35 PASS)
- [x] Поддержка внешних лоадеров (`MCB.Core/Launch/LoaderAutomation`, порт `_orchestrate_loader` из launcher.py): ожидание окна 30 с, поиск кнопки по тексту + edit с `.exe` (контролы могут создаваться лениво — опрос 10 с), WM_SETTEXT пути, клик тремя способами (BM_CLICK → LBUTTONDOWN/UP → SPACE); `WowLauncher` умеет запускать через лоадер и ловить появившийся Wow.exe (PID по пути модуля); CLI `set-loader` / `clear-loader`
- [x] Интеграция с CopilotBuddy (`MCB.Core/Launch/BotAttacher`): `CopilotBuddy.exe /pid=<wow pid> /autostart /loadprofile=… /botname=… /customclass=…` (рецепт HBRelog); SessionManager аттачит бота автоматически (BotName + AutoAttach), перезапускает при смерти бота (не чаще раза в 30 с), `stop` убивает бота первым; CLI `set-session`
- [x] selftest: +7 тестов лоадера/аттача (итого 42/42 PASS)
- **DoD:** «запустил 2 аккаунта — оба сами вошли в мир, бот приаттачен, при краше перезапускаются» — код готов и оттестирован offline; проверить в бою на реальных аккаунтах (`set-session` + `launch` + `watch`)

### Этап 2 — Быстрая смена персонажа (1–2 недели) — УПРОЩЁН (DLL уже есть и открыта) — ГОТОВ 2026-11-19
- [x] Патч-DLL: **писать свою не нужно** — `AwesomeWotlkLib.dll` (MIT, исходник в thirdparty); на этой машине Wow.exe уже пропатчен, DLL развёрнута
- [x] Архитектура моста (по исходникам wow-autologin установлено: `WowManagerSwitchCharacter` — Lua-CFunction, зовётся только ИЗ ИГРЫ аддоном, localhost-сокета в DLL нет) → мост = **плагин CopilotBuddy**:
  - [x] `Plugins/MCBBridge/MCBBridge.cs` (исходник `src/MCB.Bridge/`, скомпилирован против реального CopilotBuddy.dll — 0 ошибок; бот перекомпилирует при старте): слушает `127.0.0.1:<порт сессии>`, команды из очереди исполняет на пульсе плагина (Lua — на главном потоке); `PING` → `PONG <имя>|<реалм>|<switch есть>`, `SWITCH <имя>` → `WowManagerSwitchCharacter('<имя>') Logout()` (TTL 60 с, семантика wow-autologin)
  - [x] `BotAttacher`: назначение порта (47201+, уникальный на сессию) + запись `Plugins/MCBBridge/sessions.json` (порт + PID WoW, живые сессии); плагин выбирает запись по PID приаттаченного WoW
  - [x] `BotBridgeClient` (MCB → плагин): PING/SWITCH, таймауты, разбор PONG
  - [x] CLI: `ping <account>`, `switch-fast <account> <char>` (мост, при недоступности — автоматический fallback на перезапуск)
- [ ] IPC `HELLO/ROSTER/LAUNCH` — вычеркнуто (не нужно: Lua-глобал + мост покрывают всё)
- [ ] Первое включение плагина пользователем: в UI бота включить «MCBBridge» (плагины отключены по умолчанию, включение сохраняется в CharacterSettings)
- **DoD:** «кнопка Зайти для персонажа того же аккаунта меняет его без перезапуска» — код готов (`switch-fast`), проверен offline (мок-сервер моста); боевой прогон — при живых сессиях

### Этап 3 — Lua-аддон и рорстер (2–3 недели) — ГОТОВО 2026-11-19
- [x] Порт `Core.lua` → `MCBuddy` (`MultiCopilotBuddy/addon/MCBuddy/`, автотрансформация `WowManager*`→`MCBuddy*`, 1703 строки; DLL-глобал `WowManagerSwitchCharacter` оставлен контрактом через локальный алиас `MCBSwitch`; слэши `/mcb`, `/mcbuddy`; отдельные SavedVariables `MCBuddyDB`/`MCBuddyCharDB` — не конфликтуют с WowManager). **Деплой в клиент: `C:\Games\WoW 3.3.5a\Interface\AddOns\MCBuddy\`**
- [x] Генератор `Config.lua` на запуск (`MCB.Core/Addon/AddonConfigWriter`): рорстер+цвета классов в меню/окно аддона, флаги lfg/syncFriends/harvest; вызывается из `WowLauncher` при каждом запуске
- [x] Парсер SavedVariables с диска (`MCB.Core/Roster/SavedVariablesParser` — Lua-таблицы: вложенность, escape-строки, числа, булевы, комментарии; проверен на реальных файлах клиента GearScore/AtlasLoot)
- [x] `RosterReader` (collect-поля 1:1 с Core.lua: level/gold(медь)/gs/ilvl/spec/currencies/played/...; `cur:<валюта>` колонки) + `RosterScanner` (обход `WTF\Account\*\SavedVariables\{MCBuddyDB,WowManagerDB}.lua` + per-char `played`; tombstone-aware запись в `MultiAccount.json`) — **без запуска клиента**
- [x] CLI: `scan-roster [--wtf <путь>]`, `roster` (таблица), `parse-lua <файл>` (диагностика)
- [x] Hover-карточки/портреты — отложено до GUI-интеграции (UI Этапа 0/5); аддонная hover-карточка уже в порту (`hoverCard` флаг)
- [x] selftest: +14 тестов (парсер, рорстер, скан, Config.lua) — итого **63/63 PASS**
- **DoD:** «в менеджере видна таблица всех персонажей с голдом/ГС/валютами без запуска клиентов» — цепочка готова и оттестирована (парсер читает реальные SavedVariables); на этой машине в WTF ещё нет `MCBuddyDB.lua` — таблица наполнится после первого входа с аддоном MCBuddy (`launch` + логин + `scan-roster`)

### Этап 4 — Harvest + внутриигровой UI (1–2 недели) — ГОТОВО 2026-11-19
- [x] Внутриигровой UI — покрыт портом аддона (Этап 3): минимап-кнопка, окно рорстера, окно друзей/игнора, LFG-окно, `/mcb` — всё из Core.lua; флаги (showMinimap/syncFriends/lfg/hoverCard) генерируются в Config.lua на каждом запуске
- [x] Harvest mode (`MCB.Core/Harvest/HarvestOrchestrator`): по всем/выбранным аккаунтам последовательно, с прогрессом; семантика wow-autologin 1:1 — аддон сам обходит `harvestChars` (collect → смена без перезапуска через DLL-глобал → `Quit()`), оркестратор: закрывает живого клиента (KillRunning в конфиге), пишет Config.lua (`harvest=true` + список через `LaunchCharacter(harvestChars:)`), ждёт выхода (таймаут PerAccountTimeoutMin, по таймауту — KillAliveClient), сканирует SavedVariables, сохраняет рорстер. CLI: `harvest [acc...] [--chars A,B] [--all] [--timeout 15]`
- [x] Дашборд (`MCB.Manager/DashboardWindow`, кнопка «Дашборд» в главном окне): таблица всех персонажей (аккаунт/имя/класс/ур./голд/ГС/ilvl/зона/обновлено), автообновление 10 с, кнопки Обновить/Сканировать рорстер (фон)/Harvest (подтверждение+фон+прогресс)/переключатель языка
- [x] Локализация ru/en (`MCB.Core/L10n/Strings`, язык в `Ui.Language`, переживает Save/Load) — хром окон и дашборд; диалоги и CLI — русский (полный перевод — Этап 6)
- [x] selftest: +6 (ResolveHarvestChars явный/дефолт с tombstone, Config.lua от harvest-запуска, ru/en словарь, persistence настроек) — итого **69/69 PASS**
- **DoD:** «собрать данные по 5 аккаунтам одной кнопкой; в игре работают /mcb, меню перезахода, синк друзей» — код готов (CLI `harvest` без аргументов = все аккаунты; в GUI — кнопка Harvest); боевой прогон — при настроенных аккаунтах (в реальном конфиге аккаунтов пока нет)

### Этап 5 — Мульти-сессии бота (3–5 недель, самый рискованный) — ГОТОВО 2026-11-19 (fallback-архитектура)
- [x] Аудит глобального состояния CopilotBuddy — `MultiCopilotBuddy/docs/stage5-audit.md`. Вывод: стейт бота per-process (`StyxWoW`/`ObjectManager` статик), `ProcessMutex` — per-WoW-PID (параллельные боты на разных клиентах разрешены), логи per-PID → **рефакторинг per-session контекста не нужен**, N процессов бота = N изолированных сессий (уже так работает `BotAttacher` + `SessionManager`)
- [x] N одновременных сессий: `SessionManager.Pulse()` ведёт все сессии параллельно (краш-детект, автоперезапуск, автозаттах бота per-session); UI: окно «Сессии» (`MCB.Manager/SessionsWindow` — статусы, PID клиента/бота, рестарты, порт моста, кнопки Запустить/Остановить/Остановить всё, автообновление 5 с)
- [x] Настройки per-character/per-session: `set-session --bot --profile --routine` (Этап 1), на сессию — порт моста, копия бота
- [x] Fallback-изоляция: опция `Launch.BotCopyPerSession` (`MultiAccount.json`) — по копии папки бота на сессию (`<bot>\MCBBots\<id12>\`, без Logs/Cache/WTF), `BotCopyManager` + запись sessions.json в папку конкретного бота. По умолчанию выключено (аудит: обычно не нужно)
- [x] selftest: +4 (копия бота без рекурсии и без Logs/Cache, парсер расписаний, ротация с tombstone) — итого **82/82 PASS**
- **DoD:** «3 персонажа ботят одновременно, каждый со своими настройками, менеджер показывает статусы» — архитектура готова и оттестирована offline; боевой прогон при живых клиентах

### Этап 6 — Полировка — ГОТОВО 2026-11-19 (остатки — см. ниже)
- [x] Расписание ботов: `ScheduleOptions` per-account (окно HH:mm-HH:mm через полночь, дни Mo/Tu/…/all), `SessionManager` глушит клиента вне окна и не рестартит; CLI `set-schedule`/`clear-schedule`
- [x] Очередь персонажей: `SessionModel.RotationHours` — авто-ротация на следующего видимого персонажа каждые N часов (только через мост, без перезапуска; мост недоступен — ждёт); CLI `next-char <acc>` (мост → fallback на перезапуск)
- [x] Миграция из WowManager: `MCB.Core/Migration/WowManagerImporter` + CLI `import-wowmanager <characters.json> [--wow <путь>]` — flat-формат characters.json, классы по id, пароли/TOTP: plaintext и `enc:v1:` (DPAPI без энтропии, CryptUnprotectData) → перешифровка в DPAPI MCB (своя энтропия); WowManager не модифицируется; tombstone уважается при последующем scan-roster
- [x] Локализация ru/en: `MCB.Core/L10n/Strings` + `Ui.Language` — хром окон, дашборд, сессии; диалоги и CLI остались русскими (перенесено из Этапа 4, полного покрытия не требуется для DoD)
- [x] Документация: README (все команды, архитектура, csc-рецепт), docs/stage5-audit.md, docs/plan.md (копия плана); проект выложен на GitHub (private, slowpokehaze/MultiCopilotBuddy)
- [ ] Остатки (вне DoD): GUI-редактор расписания/ротации (есть в CLI), полный перевод диалогов, автообновление MCB

---

## 6. Риски

| Риск | Митигация |
|---|---|
| Глобальное состояние бот-движка не расщепляется на N сессий | Этап 5 начинается с аудита; заранее предусмотрен fallback с N копиями процесса |
| ~~Оригинальная DLL WowManager недоступна для порта~~ **Снято**: DLL открыта (MIT, `awesome_wotlk_src/`) и уже развёрнута на этой машине | — |
| Пароль в autologin.json лежит открытым текстом (так работает DLL) | Файл удаляется DLL сразу после чтения; MCB удаляет переживший файл (правило «запуск умер»); из MultiAccount.json пароль отдаётся только на момент запуска |
| Оригинальная патч-версия vs обновление Wow.exe | Маркер ищется при каждом запуске (порт `is_wow_patched`); при несовпадении — прогон `AwesomeWotlkPatch` (исходник в thirdparty) |
| Кодировки (кириллица в именах персонажей) | Порт `foldCase` как есть (locale-независимое сравнение), UTF-8 во всех файлах |
| Конфликт с WowManager, если оба стоят | Другие имена аддона/SavedVariables/миникарт-кнопки |
| Сервер кикает при «переключении» сразу после логина | Сохранить обработку `PLAYER_CAMPING/PLAYER_QUITING` и `!`-маркер из оригинала |
| **Гонка autologin.json при мульти-запуске** (файл один, DLL читает его лениво на логин-скрине) | **Найдено в бою 2026-11-19**: `AutologinFile.WaitConsumed` — `LaunchCharacter` после старта клиента ждёт до 60 с, пока DLL удалит файл, и только потом разрешает следующий запуск. Рестарты `SessionManager` сериализуются тем же механизмом |
| **CopilotBuddy.exe требует администратора** (манифест requireAdministrator) | **Найдено в бою 2026-11-19**: прямой старт из не-админской сессии давал ERROR_CANCELLED без диагностики. Фикс: fallback `runas` (UAC-запрос) + определение PID по диффу процессов; CLI `attach <acc>` печатает diagnose (`LastAttachError`) |
| **Бот стартует раньше входа в мир** (аттач по грейс-периоду попадал на логин-скрин) | **Найдено в бою 2026-11-19**: аттач привязан к детекту «в мире» — `WowLauncher.GetInWorldPids()` (один netstat на пульс): на логине/выборе персонажа у клиента ESTABLISHED только к auth-порту 3724, в мире — к world-порту (напр. 11294). Состояния: Running (клиент жив) → InGame (в мире) → Botting (бот работает) |
| **CopilotBuddy сам логаутит персонажа** («выход как будто нажали выбор персонажа») | **Найдено в бою 2026-11-19**: штатный `InactivityDetector` бота (`Settings\StyxSettings.xml`: LogoutForInactivity=True, таймер 10 мин) — Party Bot без лидера стоит на месте → бот делает `Lua Logout()`. Исправление: LogoutForInactivity=False. Единственное место в коде бота с Logout() |
| **UAC-спам при неудачных аттачах** (бот при клиенте на экране выбора персонажа пишет «No available WoW» и закрывается) | Backoff в `SessionManager`: бот, продержавшийся < 5 мин, растит `AttachFailCount`; пауза между повторами 30 с → 1 → 2 → 4 → 8 → 10 мин (макс). Сброс — бот жив > 5 мин |

---

## 7. Что переиспользуем напрямую

- **Lua-аддон `Core.lua`** (thirdparty/wow-autologin) — алгоритмы переносятся почти дословно (переименование + `WowManagerSwitchCharacter` → `MCBuddySwitchCharacter`).
- **AwesomeWotlkLib.dll + AwesomeWotlkPatch** (MIT) — не портируем, используем как есть: autologin-протокол (autologin.json), `WowManagerSwitchCharacter`, 2FA.
- **launcher.py** — источник алгоритмов для Launcher/Sessions/Harvest (cooldown, лоадеры, WTF-бэкап, Config.lua генерация, «выживший autologin.json = мёртвый запуск»).
- **Настройки CopilotBuddy per-character** — как есть.
- **Логика парсера ГС/LFG, social sync, harvest** — перенос семантики 1:1, включая константы (TTL 600, SOCIAL_CAP 50, SOCIAL_CONFIRM 5).
- **CBRelog** — остаётся как автономный инструмент; в MCB не встраивается (дублирует Этап 1).

## 8. Что не входит в скоуп

- Портирование Python-UI WowManager (Tk) — UI строится заново на WPF/MahApps в стиле CopilotBuddy.
- Поддержка клиентов отличных от 3.3.5a.
- Обфускация/анти-детект патча (вне этического/технического скоупа этого плана).
