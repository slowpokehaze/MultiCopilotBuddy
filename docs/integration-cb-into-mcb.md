# Интеграция CopilotBuddy в MultiCopilotBuddy (.NET)

Дата: 2026-11-21. Статус: план.
Контекст: `../plan.md` (этапы 0–6 готовы), `stage5-audit.md` (движок бота per-process,
сессии = N процессов), риски из `../plan.md` §6 (бой 2026-11-19/20).

---

## 1. Что болит сейчас (зачем интегрировать)

MCB управляет CopilotBuddy **снаружи**: `BotAttacher` стартует полноценный
`CopilotBuddy.exe` (WPF-UI + requireAdministrator) с аргументами `/pid /autostart`.
Отсюда все боевые шишки:

| Боль | Корень |
|---|---|
| **Зомби-боты**: CopilotBuddy переживает смерть своего клиента, сессия не аттачит нового | Бот elevated, MCB из не-админской сессии не может его убить; бот не выходит при потере целевого PID |
| **UAC-спам** при неудачных аттачах + backoff-эвристики (30 с → 1 → 2 → 4 → 8 → 10 мин) | Каждый CopilotBuddy.exe — отдельный UAC-запрос; статус бота MCB узнаёт только по «жив > 5 мин» |
| **Аттач по грейс-периоду попадал на логин-скрин** → netstat-детект «в мире», который ловит и выбор персонажа | MCB не получает от бота честного статуса, догадывается по сетевым соединениям |
| **watch не перезапустил клиентов** после массового килла (сессии Crashed → Idle) | Статус и решение о рестарте размазаны между двумя процессами и файлом настроек |
| ~50–100 МБ RAM + WPF-UI на каждую сессию | Полный экземпляр бота на сессию, UI не нужен |

Вывод: пока бот — чужой чёрный ящик с UI, MCB будет вечно коноёбиться с
эвристиками. Лечится тем, что движок бота становится **частью MCB**.

## 2. Варианты и выбор

| Вариант | Суть | Вердикт |
|---|---|---|
| **A. MCB.Agent — headless-хост движка** (рекомендуется) | Маленький net10.0 x86 exe, референсит `CopilotBuddy.dll` + зависимости, крутит Styx-движок **без UI**, аттачится к WoW по PID, живёт под управлением MCB по пайпу | Чинит зомби, UAC, статусы, RAM. Не требует пересборки CopilotBuddy — референсим установленные DLL |
| B. Встроить MCB как вкладку в CopilotBuddy (старый пункт Этапа 0) | Пересборка бота из upstream-исходников с окном MCB | Направление обратное («MCB в CB»), нужен SDK/портативный SDK, ничего не даёт против зомби. Отложить |
| C. Дожать MCBBridge до полного канала управления | Плагин шлёт статус/принимает start-stop, CB.exe остаётся | Дешёвый промежуточный, но бот всё ещё чужой elevated-процесс: зомби и UAC остаются |

План ниже — вариант A, с пунктами C как быстрыми победами, если A затянется.

## 3. Целевая архитектура

```
MCB.Manager / MCB.Cli (UI/CLI, не elevated — как сейчас)
   │  spawn + named pipe (протокол ниже)
   ├─ MCB.Agent #1 ── CopilotBuddy.dll (Styx-движок, без UI) ── Wow.exe #1
   ├─ MCB.Agent #2 ── ... ── Wow.exe #2
   └─ MCB.Agent #N
        все агенты — в одном Windows Job Object (KILL_ON_JOB_CLOSE)
```

Ключевые решения:

- **Job Object** — все агенты одного MCB в job с kill-on-close: умер MCB → умерли
  все агенты. Зомби-боты умирают архитектурно, а не обходом через taskkill/UAC.
- **Один UAC на старт MCB**: MCB.Manager при запуске сам просит elevation
  (manifest `requireAdministrator` на менеджере — или elevate-хелпер только для
  агентов: `runas MCB.Agent.exe`, UAC один раз при первом старте сессии).
  Дальше MCB Elevated может kill-ать своих детей напрямую — access denied уходит.
- **Статусы из первых рук**: агент сам шлёт `ATTACHED / IN_WORLD / BOT_STARTING /
  BOT_RUNNING / CLIENT_DEAD / ERROR <msg>` — netstat-детект и backoff-эвристики
  (`WowLauncher.GetInWorldPids`, `AttachFailCount`) остаются только как fallback.
- **Бот сам выходит при смерти клиента** (честный фикс из plan.md §6): агент
  мониторит целевой PID и делает self-exit — дважды защищает зомби-кейс.
- **MCBBridge не трогаем** — быстрый switch продолжает работать как есть,
  `sessions.json` агент пишет так же (порт + PID), договорились по контракту.
- **Папка бота**: агент работает из папки оригинала (или `MCBBots\<id>` при
  `BotCopyPerSession`) — `Logging.ApplicationPath`, Routines/Bots/mmaps
  резолвятся как у живого бота; agent.exe кладём рядом с CopilotBuddy.dll.

## 4. Протокол MCB ↔ Agent (named pipe, JSON-строки по \n)

Регистр-контракт:

```
→ AGENT { "wowPid": 1234, "bot": "Party Bot", "profile": "...", "routine": "Singular wotlk",
          "bridgePort": 47201, "botDir": "..." }   # старт: аттач + бот
← LOG   { "level": "...", "msg": "..." }            # зеркало строк лога бота
← STATE { "status": "ATTACHED|IN_WORLD|BOT_RUNNING|CLIENT_DEAD|ERROR", "detail": "..." }
→ STOP                                                     # мягко: бот stop → exit
→ PING  → ← PONG
→ POLL  → ← (все накопленные LOG/STATE)                    # вычитать очередь статусов
```

Реализация (Этапы B/C) немного проще реестра: аргументы агенту уходят командной
строкой (`/pid= /botname= /loadprofile= /customclass= --pipe= --botdir=`),
`AGENT`-сообщение не нужно. Канал строгого request-response (как
`BotBridgeClient`): клиент всегда пишет первым (PING/POLL/STOP), сервер только
отвечает — иначе обе стороны могут одновременно зависнуть в Write (найдено в
selftest). Статусы: `ATTACHED / IN_WORLD / BOT_RUNNING / BOT_STOPPED /
CLIENT_DEAD / ERROR`.

Смерть пайпа = агент умер (или наоборот) — SessionManager.Pulse различает по
живости Wow-PID.

## 5. Этапы

### Этап A — Разведка init-последовательности (1–2 дня) ✅ 2026-11-21
- [x] Распаковать `thirdparty/copilotbuddy-master.zip`, вытащить `Main()`/App.xaml.cs
      CopilotBuddy.exe: полный порядок инициализации до старта бота
      (Logging.ApplicationPath, StyxWoW init, ObjectManager, ProcessMutex,
      PluginManager.RefreshPlugins, RoutineManager, BotManager).
- [x] Карта скрытых UI-зависимостей: всё, что трогает WPF `Application.Current`,
      `ProcessSelectorWindow`, MessageBox/DiagDialog на путях инициализации.
- [x] Проверить лицензионные/trial-чеки и single-instance глобалы (не per-PID).
- [x] Карта зависимостей DLL рядом с CopilotBuddy.dll (что обязательно грузить:
      Navigation.dll, Microsoft.CodeAnalysis.*, MahApps — только если UI).
- **DoD:** ✅ `stageA-init-recon.md` — шаги init + чек-лист заглушек headless.

### Этап B — Каркас MCB.Agent (2–4 дня) — код готов, живой прогон DoD не сделан
- [x] `src/MCB.Agent/`: net10.0 **x86** exe (runtime машины x86 10.0.15; сборка
      через csc-тулсет по рецепту `tools/build-manager.ps1` → `tools/build-agent.ps1`,
      деплой MCB.Agent.dll в корень бота рядом с CopilotBuddy.dll).
- [x] Main: парс аргументов → init из списка Этапа A → `attach(wowPid)` →
      старт бота (`BotManager.Start`) → пайп-сервер. Init 1:1 с
      `MainWindow.Window_Loaded` (см. `stageA-init-recon.md` §1), заглушки — §5.
- [x] Мониторинг целевого Wow-PID: клиент умер → бот Stop → self-exit (зомби-фикс).
- [x] Job Object: `MCB.Core/Launch/WindowsJob.cs` (KILL_ON_JOB_CLOSE) — менеджер
      добавляет агента при старте; при не-elevated MCB (UAC-fallback) assignment
      может не пройти — лог warn, закрывается Этапом D.
- **DoD:** ⏳ руками запущенный агент ботит одного персонажа — прогнать по живому
  клиенту (`dotnet MCB.Agent.dll /pid=<pid> /botname=Party Bot /customclass=...`).

### Этап C — Протокол и оркестрация (2–3 дня) — код готов, selftest зелёный по агентным тестам
- [x] Named-pipe сервер в агенте (`src/MCB.Agent/AgentPipeServer.cs`) +
      `AgentClient` в MCB.Core (`src/MCB.Core/Launch/AgentClient.cs`,
      по образцу `BotBridgeClient`). Грабли, найденные в selftest:
      1) dispose клиента с незавершённым async-read НЕ закрывает соединение —
      чтение только по `PeekNamedPipe` + синхронные Read;
      2) сервер не должен писать без запроса — обе стороны зависали в Write
      одновременно; протокол переведён на строгий request-response
      (клиент шлёт PING/POLL/STOP, сервер отвечает).
- [x] `STATE`-машина статусов; SessionManager ест статусы агента (Poll в Pulse),
      netstat-детект и грейс-периоды — fallback. Флаг `Launch.UseAgent` (по
      умолчанию false до стабилизации), старый путь BotAttacher остаётся.
- [x] `AgentLauncher` в MCB.Core/Launch: замена `BotAttacher` при UseAgent
      (elevated MCB — прямой старт, иначе runas-UAC; pid по диффу хостов).
- [x] selftest: тесты аргументов, STATE-маппинга, живого пайпа (мок-агент:
      Poll/PING/STOP, реконнект) — `dotnet MCB.Cli.dll selftest`.
- **DoD:** ⏳ `launch`/`watch`/`stop` через агента по живым сессиям (Этап E).

### Этап D — Элевация и зомби-политика (1–2 дня)
- [ ] Решение по UAC: manifest requireAdministrator на MCB.Manager (просто) или
      elevate-хелпер только для агентов (UAC один раз при первом старте сессии).
- [ ] SessionManager: агент мёртв + клиент жив → рестарт агента (лимит/бэкофф
      как сейчас); клиент мёртв + агент жив → невозможен (self-exit) — assert в лог.
- [ ] Убрать UAC-обходы: runas-fallback, дифф процессов, taskkill-обходы — остаются
      только в legacy-пути (UseAgent=false).
- **DoD:** «kill MCB → все агенты и их боты умерли; перезапуск MCB → сессии
  поднялись сами, один UAC за весь запуск».

### Этап E — Боевой прогон (по живым аккаунтам)
- [ ] 5 аккаунтов WoW Circle: цикл входа ~20 с (как 2026-11-20), агенты ботят,
      `switch-fast` через мост, краш-тест: kill одного Wow.exe → его сессия
      восстановлена, остальные не дёрнулись.
- [ ] Кириллическое имя персонажа (регресс `\u`-escape) — прогнать через агента.
- [ ] Свёрнутый клиент (maxFPSBk) — проверить, что агент не зависит от рендера
      так, как зависела DLL-машина входа (агент живёт вне WoW, но рутруte бота — нет).
- **DoD:** суммарный: «N сессий, ноль зомби, ноль UAC-спама, watch чинит всё сам».

## 6. Риски

| Риск | Митигация |
|---|---|
| Init-последовательность тянет WPF-UI (диалоги, ProcessSelector) | ✅ Закрыт Этапом A: три зависимости найдены и обходятся (BCP-фильтр, `/customclass`, своя подписка на лог) |
| Скрытый single-instance/лицензия в CopilotBuddy.dll | ✅ Этап A: лицензий нет; мьютекс per-PID (формула в `stageA-init-recon.md` §3) |
| Битность: движок x86, сборка агента x64 → BadImageFormatException | Agent строго x86 (`-platform:x86` в build-agent.ps1), референсим те же DLL, что CopilotBuddy.exe |
| Roslyn-компиляция routines внутри агента (Microsoft.CodeAnalysis) | ✅ DLL в папке бота подхватываются; TMP per-agent (`%TEMP%` → `<bot>\Temp\agent_<pid>`, stageA §5.4) |
| Папка бота как CWD: относительные пути из чужого CWD | Агент сам ставит CWD/AppContext.BaseDirectory = папка бота; `BotCopyPerSession` как сейчас (агент.dll копируется вместе с ботом) |
| Двойной аттач к одному клиенту (агент + живой CopilotBuddy.exe) | `Styx.ProcessMutex` per-PID у бота + свой `Local\MCBAgent_WoW_<pid>` у агента |
| Регресс быстрого switch (мост) | MCBBridge не меняется; sessions.json пишет менеджер как раньше; кейс в Этапе E |
| Несколько рутин на класс в headless | `RoutineSelectionForm` — агент всегда передаёт `/customclass=` (FEAT-37); если имя рутины в сессии пустое/битое — бот не стартует (ERROR вместо зависшего диалога) |
| BuddyControlPanel тянет `StyxWoW.Overlay` → NRE (`Application.Current == null`) | Агент вырезает BCP из EnabledPlugins (env `MCB_AGENT_DISABLE_PLUGINS`, дефолт «BuddyControlPanel») |
| Плагины не компилируются в headless (Roslyn-референсы = AppDomain) | ✅ Бой 21.11: агент предзагружает System.Drawing.Common / Forms / Net.Primitives / WebClient / WCF до init (`PreloadFrameworkAssemblies`); иначе MCBBridge (IPAddress), AutoEquip2/BCP (Font/Icon) не собирались |
| Не-elevated менеджер ↔ elevated агент (UAC-fallback) | Пайп агента не принимает запись с medium-IL (Access denied), kill/STOP тоже не проходят — менеджер ДОЛЖЕН быть elevated (Этап D). В не-elevated режиме менеджер только стартует агентов, статусы — по процессу dotnet |
| Kill-обход при не-elevated MCB | Этап D: manifest requireAdministrator на менеджере → дети наследуют elevation, UAC один раз |

## 7. Что не трогаем

- WowLauncher / autologin.json / лоадеры — вход в мир остаётся как есть
  (Enter-фикс DLL, паузы 5+5 с).
- Lua-аддон MCBuddy, harvest, рорстер — без изменений.
- CBRelog — legacy, не трогаем.
- Старый путь BotAttacher → CopilotBuddy.exe — остаётся флагом `UseAgent=false`
  как запасной до снятия с вооружения.

## 8. Как включить (после Этапа B/C)

```powershell
# 1. Собрать и задеплоить агента (MCB.Agent.dll + runtimeconfig → корень бота)
powershell -NoProfile -ExecutionPolicy Bypass -File tools\build-agent.ps1

# 2. Пересобрать менеджер/CLI (Core изменился)
powershell -NoProfile -ExecutionPolicy Bypass -File tools\build-manager.ps1
Copy-Item tools\out\MCB.Manager.dll build\
powershell -NoProfile -ExecutionPolicy Bypass -File tools\build-cli.ps1

# 3. Включить агентный режим (Settings\MultiAccount.json):
#    "launch": { "useAgent": true, ... }
# 4. Запускать MCB с правами администратора — тогда агенты стартуют без UAC
#    (пока MCB не elevated — один UAC-запрос на старт каждого агента, Этап D).
```

Пайп сессии: `MCBAgent_<session.Id>` — детерминирован, нигде не настраивается.
