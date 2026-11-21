# Этап A — Разведка init-последовательности CopilotBuddy (для MCB.Agent)

Дата: 2026-11-21. Источник: `thirdparty/copilotbuddy-master.zip` (распакован, версия 1.6.7.6).
Статус: готово. DoD выполнен: ниже — письменный список шагов init «как у живого бота»
и список того, что глушится/вырезается в headless.

---

## 1. Точка входа и полный порядок инициализации

Entry point: `CopilotBuddy.UI.App` (`UI/App.xaml.cs`, `StartupUri` → `UI/MainWindow.xaml`).
`App.OnStartup` — только UI-обвязка (глобальные обработчики исключений + `Globalization.ApplyLanguage`),
движку не нужен. Вся реальная инициализация — в `MainWindow.Window_Loaded` → `Task.Run`:

```
1. InitializeLogging()                     — LogFilePath = Logs\yyyy-MM-dd_HHmmss_<procId>.log,
                                             подписка Logging.OnLogMessage (в агенте — зеркало в пайп)
2. FindAndAttachToWoW():
   2.1. /pid=N из аргументов (иначе — перебор Wow/WoW/Luacct по build 12340)
   2.2. проверка логина: Memory.Read<byte>(GlobalOffsets.InGame) != 0
   2.3. ProcessMutex.Create(pid, out createdNew) — мьютекс владения WoW-PID
   2.4. new Memory(pid)            — GreenMagic: Process.EnterDebugMode(), чтение памяти
   2.5. повторная проверка InGame
   2.6. ObjectManager.Initialize(memory):
          - WowProcess = по PID, EnableRaisingEvents
          - проверка build (FilePrivatePart == 12340)
          - HookEndscene()  ← FASM-инъекция ExecutorRand в EndScene d3d9 (требует админ-прав)
          - ObjectManager.Update() — первый проход объектов
3. TreeRoot.Initialize()                   — только StartMinimizeGuard (фоновый поток,
                                             разворачивает минимизированный WoW пока бот бежит)
4. Лог персонажа (level/race/class/zone)
5. LoadSettings():
   - CharacterSettings.Initialize()        — Settings\CharacterSettings_<Me.Name>.xml
   - UISettings.Instance.Load()            — UI-only, в агенте можно пропустить
   - StyxSettings.Instance.Load()          — StyxSettings.xml (UseFrameLock и пр.)
   - LevelbotSettings.Instance.Load()
6. RoutineManager.Init()                   — компиляция Routines\ (Roslyn через SourceCompiler),
                                             выбор рутины по классу (см. §2 про RoutineSelectionForm)
7. BotManager.Instance.LoadBots(<BaseDirectory>\Bots)   — компиляция/загрузка внешних ботов;
                                             built-in боты (Party Bot, Grind, Questing) грузятся
                                             в ctor BotManager из Assembly.GetExecutingAssembly()
                                             = CopilotBuddy.dll — в агенте работает как есть
8. PluginManager.Initialize(CharacterSettings.Instance.EnabledPlugins) — компиляция Plugins\,
                                             включение перечисленных плагинов
9. ApplyAutoStartConfig():                 — из CommandLine:
   /loadprofile=<path> → ProfileManager.LoadNew(path)
   /botname=<фрагмент> → BotManager.Instance.SetCurrent(bot)   (substring match по bot.Name,
                                регистронезависимо; имя это display-name: «Party Bot», «Grind»…)
   (любой из двух аргументов) → StartBot() → TreeRoot.Start()
```

Командная строка живого бота, которую агент должен повторить:
`/pid=<wowPid> /loadprofile=<abs> /botname=Party Bot /customclass=<RoutineName>`.

## 2. Скрытые UI-зависимости на путях инициализации

| Место | Зависимость | Действие в headless |
|---|---|---|
| `App`/`MainWindow` целиком | WPF `Application`, MetroWindow | **Не создаём.** Всё перечисленное выше вызывается напрямую, UI-классы не инстанцируются |
| `StyxWoW.Overlay` (lazy, `StyxWoW.cs:184`) | `System.Windows.Application.Current.Dispatcher` → **NRE, если Application.Current == null**; дальше WidgetOverlayWindow/ToastOverlayWindow поверх WoW | Плагин, его трогающий — `BuddyControlPanel` (включён в CharacterSettings!). Агент фильтрует список EnabledPlugins: BCP не включать (флаг конфига, дефолт — вырезать) |
| `RoutineManager.SelectRoutineForCurrentClass` (`RoutineManager.cs:151`) | `RoutineSelectionForm` (WinForms-диалог) при **двух и более рутинах на класс** | Агент всегда передаёт `/customclass=<RoutineName>` (FEAT-37 уже поддержан в `Init()`) — диалог не показывается |
| `MainWindow.InitializeLogging` | RichTextBox/WPF Dispatcher | Не вызываем; вместо него подписка `Logging.OnLogMessage` → `LOG` в пайп + файл лога |
| `HotkeysManager.Initialize/Register F7` | WinForms-хоткеи на окно WoW | Пропускаем (только «поднять окно бота» — в headless не нужно) |
| `MainWindow` minimize-guard-подписки, `TreeRoot.OnStatusTextChanged` | Dispatcher | Подписываемся напрямую без Dispatcher (событие можно читать из любого потока) |
| `TreeRoot.Shutdown(closeWow:true)` | `Application.Current?.Shutdown` | Null-safe — безопасно |
| `UISettings.Instance` | геометрия окна | Пропускаем Load/Save |
| `Memory(proc.Id)` ctor | `Process.EnterDebugMode()` | Не UI, но **требует SeDebugPrivilege** → агент обязан быть elevated (как и план) |
| `ExecutorRand` | EndScene-detour (FASM, x86) | Не UI. Но тики исполняются на рендер-потоке WoW → минимизированный клиент = риск (уже в плане, Этап E) |
| `SourceCompiler` | Roslyn, temp в `%TEMP%`, имена файлов с `DateTime.Now.Ticks` + `Guid.NewGuid()` | Параллельно-безопасно; агент дополнительно ставит `TMP` per-агента (см. §5) |

Прочие WinForms-зависимости (`Logging.cs` — `System.Windows.Forms.Application.StartupPath`,
`RoutineManager.cs`, `BotBase.ConfigurationForm`) — пассивные: требуют сборки
`System.Windows.Forms.dll` в резолве, но не создают окон/месседжбоксов.

## 3. Лицензии / trial / single-instance

- **Лицензионных и trial-чеков нет.** В `CommandLine.cs` прямо указано:
  «HbUser/HbPass removed — no auth in CopilotBuddy». Grep по `trial|license` по исходникам —
  только Lua-офсеты WoW (`IsTrialAccount`) и LGPL-заголовки TreeSharp.
- **Single-instance глобальный отсутствует.** Единственный мьютекс — `Styx.ProcessMutex`,
  **per-PID**: имя `Local\<hash(MachineName ^ pid ^ TZ) ^ ("CopilotBuddy" + 2922027367)>`
  (`ProcessMutex.cs:25-28`). Класс `internal` → агент либо дублирует формулу, либо держит
  свой мьютекс того же имени (достаточно своего: важно только что два владельца не сошлись).
  Защита от двойного аттача агент + живой CopilotBuddy.exe — работает из коробки.
- Хэш строки в .NET (Framework/Core) — рандомизированный per-process! Формулу нельзя
  «вычислить» снаружи — но она и не нужна: мьютекс живёт только внутри процесса-владельца.

## 4. Карта зависимостей DLL (что должно быть рядом с CopilotBuddy.dll)

Из `CopilotBuddy.csproj` + папка установки:

| Категория | Сборки | Обязательна агенту? |
|---|---|---|
| Native (P/Invoke) | `Navigation.dll` (pathing), `fasmdll_managed.dll` (FASM для инъекций) | **Да** |
| Data/DB | `System.Data.SQLite.dll` (Data.bin с SEE), `data.bin`, `Spells.bin`, `item_loot.db`, `CreatureSpawns.db` | **Да** |
| Roslyn-компиляция рутин/ботов/плагинов | `Microsoft.CodeAnalysis{,.CSharp,.Scripting,.CSharp.Scripting}.dll`, `Microsoft.Extensions.ObjectPool.dll` | **Да** (Runtime-компиляция) |
| WinForms/WPF-фреймворк | `Microsoft.WindowsDesktop.App` (UseWPF+UseWindowsForms в csproj) — в self-contained установке свои | **Да** как резолв-цель: CopilotBuddy.dll типы (`BotBase.ConfigurationWindow`, `Logging`) тянут WinForms/WPF типы. Сами окна не создаются |
| MahApps / XAML | `MahApps.Metro.dll`, `Microsoft.Xaml.Behaviors.dll`, `ControlzEx.dll` | Только для UI-классов; в резолве быть должны (типы в сигнатурах BotBase), исполнение не трогает |
| WCF (плагины) | `System.ServiceModel.Primitives.dll`, `System.ServiceModel.NetNamedPipe.dll` | Да, если MCBBridge/плагины используют (план: не трогаем) |
| Локализация | `Languages\<culture>\CopilotBuddy.resources.dll` | Опционально |

**Вывод:** агент размещаем в папке бота рядом с `CopilotBuddy.dll` (или `MCBBots\<id>`
при BotCopyPerSession) — весь резолв и `Logging.ApplicationPath` (WinForms
`Application.StartupPath` = папка exe агента) сходятся в одну точку. Агент также
выставляет `Directory.SetCurrentDirectory(botDir)`.

## 5. Глушить/вырезать/настроить в headless (чек-лист для Этапа B)

1. **Не создавать** `App`/`MainWindow`; свой `Main` повторяет шаги §1 (2.1–9).
2. **EnabledPlugins**: загрузить из CharacterSettings, **вырезать `BuddyControlPanel`**
   (и всё, что трогает `StyxWoW.Overlay`) — иначе NRE на `Application.Current.Dispatcher`.
   MCBBridge, LeaderPlugin, AutoEquip2 и пр. — оставить как настроено.
3. **`/customclass=<routine>` всегда** — обход `RoutineSelectionForm`.
4. **TMP per-agent**: перед компиляциями выставить `TMP/TEMP` на уникальную папку
   (`%TEMP%\MCBAgent_<pid>`) — страховка к кейсу stage5-audit §1 (параллельный старт,
   TempFileCollection в общем `%TEMP%`).
5. **Minimize-guard** (`TreeRoot.Initialize`) — оставить: разворачивание минимизированного
   WoW это существующее поведение движка (бот не может тикать свёрнутый клиент).
6. **ProcessMutex** — агент создаёт мьютекс по той же схеме (свой класс, имя любое
   стабильное per-PID, напр. `Local\MCBAgent_WoW_<pid>`); двойной аттач закрыт.
7. **Лог**: подписка `Logging.OnLogMessage` (потокобезопасно, приходит батчами) → `LOG`
   в пайп; `Logging.LogFilePath` в `Logs\` папки бота — как у живого бота.
8. **Мониторинг целевого PID**: `ObjectManager.WoWProcess.EnableRaisingEvents = true`
   уже включён — агенту достаточно подписаться/поллить `HasExited` → `TreeRoot.Stop()` →
   self-exit (зомби-фикс №2 из плана).
9. **CWD/AppContext**: агент.exe рядом с CopilotBuddy.dll; `AppDomain.CurrentDomain.BaseDirectory`
   = папка агента = папка бота. `BotCopyPerSession` — без изменений.

## 6. Что подтвердилось из рисков плана

- Вариант A реален: **нет ни одной жёсткой UI-зависимости на критическом пути init** —
  все три найденных (Overlay, RoutineSelectionForm, Logging-UI) обходятся фильтром
  плагинов, `/customclass` и собственной подпиской на лог.
- Fallback на вариант C не нужен.
- Битность: движок x86 (PlatformTarget x86, инъекция 32-битная) → агент строго x86.
