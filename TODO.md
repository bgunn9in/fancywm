# TODO: Master + Satellites Algorithmic Layout for FancyWM

Репозиторий: `https://github.com/bgunn9in/fancywm`

Цель: реализовать алгоритмический layout **Master + Satellites** для FancyWM.
Основной сценарий — UWQHD-монитор, но реализация не должна зависеть от конкретного разрешения `3440×1440`.

---

## Статусы

- [ ] Не начато
- [~] В работе
- [x] Завершено и проверено
- [!] Заблокировано — причина должна быть описана рядом

Пункт считается завершённым только после:

1. реализации рабочего кода;
2. успешной сборки затронутых проектов;
3. прохождения соответствующих тестов;
4. отсутствия незакрытых `TODO`, `FIXME` или `NotImplementedException` в достижимом feature-path.

---

# 1. Зафиксированные требования

## 1.1. Canonical layout

### Пустой рабочий стол

```text
Root
└── no tiled windows
```

### Только master

```text
Root: SplitPanelNode(Horizontal)
└── Master Window — занимает весь WorkArea
```

### Master и satellite

Master слева:

```text
Root: SplitPanelNode(Horizontal)
├── Master Window
└── Satellite Panel
    ├── Satellite 1
    ├── Satellite 2
    └── Satellite 3
```

Master справа:

```text
Root: SplitPanelNode(Horizontal)
├── Satellite Panel
│   ├── Satellite 1
│   ├── Satellite 2
│   └── Satellite 3
└── Master Window
```

## 1.2. Значения по умолчанию

```text
Enabled:                       false
Master ratio:                  60%
Master side:                   Left
Satellite orientation:         Vertical
Maximum satellite windows:     3
Total tiled capacity:          4
Overflow policy:               MoveToExistingDesktop
Follow overflow window:        false
Display scope:                 PrimaryDisplay
Max auto-created desktops:     1
```

## 1.3. Основные правила

- Первое tiled-окно становится master.
- При единственном master он занимает весь `WorkArea`.
- Второе окно становится первым satellite и создаёт разделение 60/40.
- Следующие окна добавляются в конец satellite panel.
- Satellite panel может быть:
  - `Vertical` — окна сверху вниз;
  - `Horizontal` — окна слева направо.
- Master можно перемещать слева направо и справа налево.
- Satellite можно переставлять внутри satellite layout.
- Satellite можно сделать master.
- При promotion старый master занимает **точный слот** выбранного satellite.
- При превышении лимита новое окно отправляется на другой виртуальный рабочий стол.
- Если подходящего рабочего стола нет, окно остаётся floating на исходном рабочем столе.
- Обычное поведение FancyWM не должно меняться при выключенной функции.

## 1.4. Ограничения реализации

- Не хардкодить координаты или разрешение `3440×1440`.
- Использовать `IDisplay.WorkArea`.
- Не использовать `AutoSplitCount` как `MaxSatellites`.
- Не использовать `LayoutFunctionNode`/`RatioLayout` как основной механизм.
- Основное дерево должно строиться из `SplitPanelNode` и существующего `Flex`.
- Не разрушать пользовательские ручные layouts на других рабочих столах.
- Не изменять submodules без доказанной необходимости.
- Не вызывать `IVirtualDesktop.MoveWindow()` под backend/window/floating locks.
- Не сериализовать HWND, `IWindow`, runtime-роли и ссылки на виртуальные рабочие столы.

---

# 2. Этап 0 — подготовка и baseline

- [x] Выполнить `git status --short`.
- [x] Проверить незакоммиченные изменения пользователя.
- [x] Не применять разрушительные команды:
  - `git reset --hard`;
  - `git clean -fd`;
  - принудительный checkout поверх пользовательских изменений.
- [x] Выполнить:

```powershell
git submodule update --init --recursive
```

- [x] Убедиться, что submodules доступны:
  - `ModernWpf`;
  - `winman`;
  - `winman-windows`.
- [x] Создать ветку `feature/master-satellite-layout` после безопасной фиксации сохранённого рабочего дерева.
- [x] Зафиксировать текущий commit и состояние submodules.
- [x] Выполнить baseline Debug build.
- [x] Выполнить baseline Release build, если проект уже собирается в Release.
- [x] Запустить:
  - `FancyWM.Tests`;
  - `FancyWM.Layouts.Tests`.
- [x] Зафиксировать существующие до изменений ошибки сборки и тестов.
- [x] Создать или обновить:
  - `docs/master-satellite-layout.md`;
  - `IMPLEMENTATION_STATUS.md`;
  - данный `TODO.md`.
- [x] Записать в документацию canonical invariant.
- [x] Записать в документацию acceptance criteria.
- [x] Убедиться, что `git diff` после baseline содержит только ожидаемые документы.

---

# 3. Этап 1 — модели и настройки

## 3.1. Enum-типы

- [x] Добавить `MasterSide`:

```csharp
public enum MasterSide
{
    Left,
    Right,
}
```

- [x] Добавить `SatelliteLayoutOrientation`:

```csharp
public enum SatelliteLayoutOrientation
{
    Vertical,
    Horizontal,
}
```

- [x] Добавить `MasterSatelliteOverflowPolicy`:

```csharp
public enum MasterSatelliteOverflowPolicy
{
    FloatOnCurrentDesktop,
    MoveToExistingDesktop,
    MoveToExistingOrCreateDesktop,
}
```

- [x] Добавить `AlgorithmicLayoutDisplayScope`:

```csharp
public enum AlgorithmicLayoutDisplayScope
{
    PrimaryDisplay,
    UltrawideDisplays,
    AllDisplays,
}
```

## 3.2. Модель настроек

- [x] Добавить `MasterSatelliteLayoutSettings`.
- [x] Задать defaults:

```csharp
public record MasterSatelliteLayoutSettings
{
    public bool Enabled { get; init; } = false;
    public double MasterRatio { get; init; } = 0.60;
    public MasterSide DefaultMasterSide { get; init; } = MasterSide.Left;
    public SatelliteLayoutOrientation DefaultSatelliteOrientation { get; init; }
        = SatelliteLayoutOrientation.Vertical;
    public int MaxSatellites { get; init; } = 3;
    public MasterSatelliteOverflowPolicy OverflowPolicy { get; init; }
        = MasterSatelliteOverflowPolicy.MoveToExistingDesktop;
    public AlgorithmicLayoutDisplayScope DisplayScope { get; init; }
        = AlgorithmicLayoutDisplayScope.PrimaryDisplay;
    public int MaxAutoCreatedDesktops { get; init; } = 1;
    public bool FollowOverflowWindow { get; init; } = false;
}
```

- [x] Добавить nested property в `FancyWM/Models/Settings.cs`.
- [x] Добавить необходимые свойства в `ITilingServiceSettings`.
- [x] Проверить, нужно ли передавать всю вложенную модель либо отдельные read-only свойства.
- [x] Реализовать нормализацию значений:
  - `MasterRatio`: `0.50–0.80`;
  - `MaxSatellites`: `1–9`;
  - `MaxAutoCreatedDesktops`: `0–9`.
- [x] Не считать невалидный пользовательский JSON поводом для падения приложения.
- [x] Сохранять enums как строки через существующий `JsonStringEnumConverter`.
- [x] Обеспечить загрузку старого `settings.json` без новой секции.
- [x] Не требовать отдельной миграции старого файла.
- [x] Проверить сохранение комментариев в JSON.

## 3.3. SettingsViewModel

- [x] Добавить properties в `FancyWM/ViewModels/SettingsViewModel.cs`:
  - `MasterSatelliteEnabled`;
  - `MasterRatio`;
  - `DefaultMasterSide`;
  - `DefaultSatelliteOrientation`;
  - `MaxSatellites`;
  - `OverflowPolicy`;
  - `AlgorithmicLayoutDisplayScope`;
  - `MaxAutoCreatedDesktops`;
  - `FollowOverflowWindow`.
- [x] Добавить backing fields.
- [x] Загружать значения при получении новой модели настроек.
- [x] Сохранять новую вложенную модель в `SaveChanges()`.
- [x] Исключить бесконечный цикл save/notify.
- [x] Обновить derived properties, необходимые для UI.
- [x] Добавить команду/метод применения UWQHD preset.
- [x] Проверить, что изменение одного поля не сбрасывает остальные поля nested settings.

## 3.4. Тесты настроек

- [x] Тест: старый JSON без `MasterSatelliteLayout` загружается с defaults.
- [x] Тест: сериализация и десериализация всех enum.
- [x] Тест: round-trip всех значений.
- [x] Тест: clamp слишком маленького `MasterRatio`.
- [x] Тест: clamp слишком большого `MasterRatio`.
- [x] Тест: clamp `MaxSatellites`.
- [x] Тест: clamp `MaxAutoCreatedDesktops`.
- [x] Тест: комментарии старого JSON не теряются.
- [x] Тест: unknown properties старого/нового JSON не вызывают сбой.

---

# 4. Этап 2 — чистый MasterSatelliteLayoutEngine

## 4.1. Доменные классы

- [x] Создать каталог, если нужен:

```text
FancyWM/AlgorithmicLayouts/
```

- [x] Добавить `MasterSatelliteRuntimeState`.
- [x] Добавить `MasterSatelliteLayoutSnapshot`.
- [x] Добавить `MasterSatelliteInvariantResult`.
- [x] Добавить `MasterSatellitePlacementResult`.
- [x] Добавить `MasterSatelliteOperationResult`.
- [x] Добавить `LayoutStateKey`, идентифицирующий:

```text
(IVirtualDesktop, IDisplay)
```

- [x] Runtime-state не должен попадать в `settings.json`.
- [x] Runtime-state должен содержать:
  - активность режима;
  - сторону master;
  - requested master ratio;
  - effective master ratio;
  - ориентацию satellite;
  - текущий master;
  - упорядоченный список satellite;
  - revision/generation;
  - признак layout recovery.

## 4.2. Engine

- [x] Создать `MasterSatelliteLayoutEngine`.
- [x] Engine не должен зависеть от:
  - WPF;
  - toast;
  - `MainWindow`;
  - `TaskbarIcon`;
  - конкретной Windows-реализации virtual desktops.
- [x] Engine должен работать с:
  - `DesktopTree`;
  - `SplitPanelNode`;
  - `WindowNode`;
  - `PanelNode`;
  - `WorkArea`;
  - настройками и runtime-state.

## 4.3. Построение layout

- [x] Реализовать пустой layout.
- [x] Реализовать layout с одним master.
- [x] При одном master использовать весь `WorkArea`.
- [x] Не оставлять пустую satellite panel.
- [x] Не добавлять `PlaceholderNode`.
- [x] Реализовать master слева.
- [x] Реализовать master справа.
- [x] Реализовать вертикальные satellite.
- [x] Реализовать горизонтальные satellite.
- [x] Реализовать равномерное первичное распределение satellite.
- [x] Сохранять visual order.
- [x] Обеспечить отсутствие duplicate `WindowNode`.

## 4.4. Master ratio

- [x] Применять `MasterRatio` только при наличии хотя бы одного satellite.
- [x] Считать ratio относительно полезной ширины root после padding/spacing.
- [x] Учитывать `MinSize` master.
- [x] Учитывать суммарный `MinSize` satellite panel.
- [x] Рассчитать допустимый диапазон ratio.
- [x] Clamp requested ratio к допустимому диапазону.
- [x] Сохранять отдельно:
  - requested ratio;
  - effective ratio.
- [x] Если layout физически невозможен, вернуть ошибку без частичного изменения дерева.
- [x] Поддержать reset к default.
- [x] Поддержать сохранение runtime ratio после ручного resize.
- [x] Проверить округление до целых пикселей.
- [x] Не допускать накопления ошибки после многократного relayout.

## 4.5. Promotion

- [x] Реализовать promotion satellite в master.
- [x] Использовать swap ссылок узлов, а не unregister/register.
- [x] Старый master должен попасть в точный слот выбранного satellite.
- [x] Пример:

```text
Master A
Satellites [B, C, D]
Promote C
Result:
Master C
Satellites [B, A, D]
```

- [x] Перед mutation выполнить preflight на клоне.
- [x] Проверить `MinSize` старого master в satellite-слоте.
- [x] При невозможности отменить операцию атомарно.
- [x] Promotion текущего master должен быть безопасным no-op.
- [x] Сохранять сторону master.
- [x] Сохранять master ratio.
- [x] Сохранять ориентацию satellite.

## 4.6. Reorder

- [x] Реализовать перестановку satellite по индексу.
- [x] Реализовать move previous/next.
- [x] Не разрешать выход индекса за границы.
- [x] Переносить вместе с окном его индивидуальный flex-размер.
- [x] Не менять master при обычном reorder.
- [x] Сохранять полный набор окон.

## 4.7. Смена стороны master

- [x] Реализовать `SetMasterSide(Left)`.
- [x] Реализовать `SetMasterSide(Right)`.
- [x] Реализовать `SwapMasterSide()`.
- [x] Сохранять master ratio.
- [x] Сохранять satellite order.
- [x] Не пересоздавать окна.
- [x] Использовать безопасную перестановку root children.
- [x] Повторная установка текущей стороны — no-op.

## 4.8. Смена ориентации satellite

- [x] Добавить безопасный API в `SplitPanelNode` для смены orientation.
- [x] При смене orientation переинициализировать `Flex`.
- [x] Не оставлять ограничения предыдущей оси.
- [x] Сохранять порядок окон.
- [x] После смены распределять satellite поровну.
- [x] Выполнять preflight на min-size.
- [x] При невозможности отменять операцию атомарно.
- [x] Не выполнять overflow уже существующих окон из-за ручной смены orientation.
- [x] Возвращать понятную причину отказа.

## 4.9. Закрытие и удаление

- [x] Удаление satellite сохраняет остальные окна.
- [x] Последний satellite удаляет satellite panel.
- [x] После удаления последнего satellite master занимает весь `WorkArea`.
- [x] Удаление/float master продвигает первый satellite.
- [x] После promotion нового master список satellite сохраняет порядок.
- [x] Удаление последнего окна создаёт пустой layout.
- [x] Не позволять обычному auto-collapse разрушить canonical satellite panel.

## 4.10. Invariant

- [x] Реализовать `ValidateInvariant()`.
- [x] Проверять:
  - root orientation;
  - количество root children;
  - наличие ровно одного master;
  - тип satellite panel;
  - отсутствие nested panels;
  - отсутствие stack;
  - отсутствие placeholder;
  - лимит satellite;
  - отсутствие duplicate windows;
  - соответствие runtime-state дереву.
- [x] Реализовать `Normalize()`.
- [x] В Debug использовать assert с подробным описанием.
- [x] В Release пытаться восстановить layout.
- [x] Если recovery невозможен, безопасно отключать algorithmic mode только для данного desktop/display.
- [x] Не уничтожать окна при recovery.
- [x] Логировать before/after tree snapshot.

---

# 5. Этап 3 — интеграция с TilingWorkspace

- [x] Добавить API для получения дерева конкретного desktop.
- [x] Не раскрывать наружу лишние mutable структуры.
- [x] Добавить безопасную замену root.
- [x] Сохранять `m_originalPositions` при перестройке дерева.
- [x] Сохранять focused window.
- [x] Добавить регистрацию окна как master.
- [x] Добавить регистрацию окна как satellite с заданным индексом.
- [x] Добавить регистрацию окна в зарезервированный slot.
- [x] Добавить удаление окна без обычного collapse canonical panel.
- [x] После unregister запускать normalization в algorithmic mode.
- [x] Не использовать `ResolveParentWithWidthConstraint()` для canonical placement.
- [x] Не применять `AutoSplitCount` в algorithmic mode.
- [x] Не добавлять placeholders в algorithmic mode.
- [x] Добавить preflight размещения нового окна.
- [x] Добавить capacity query.
- [x] Добавить возможность определить:
  - пустой layout;
  - canonical layout;
  - ручной layout;
  - повреждённый canonical layout.
- [x] Добавить clone-based transaction helper либо эквивалент.
- [x] Не оставлять частично изменённое дерево после исключения.
- [x] После каждой mutation выполнять `ValidateInvariant`.
- [x] Добавить логирование layout revision.

---

# 6. Этап 4 — локальная интеграция TilingService

## 6.1. Runtime-state

- [x] Хранить состояние отдельно для каждого virtual desktop данного display.
- [x] Инициализировать state при `DesktopAdded`.
- [x] Удалять state при `DesktopRemoved`.
- [x] Обновлять `WorkArea` при изменениях display.
- [x] Обрабатывать смену DPI/scaling.
- [x] Обрабатывать смену текущего desktop.
- [x] Не смешивать состояния разных мониторов.

## 6.2. Включение режима

- [x] Реализовать включение на текущем desktop/display.
- [x] При наличии окон выбрать master:
  1. focused tiled window;
  2. иначе первое окно в визуальном порядке.
- [x] Остальные окна сохранить в текущем визуальном порядке.
- [x] При превышении capacity существующие лишние окна обрабатывать с конца списка.
- [x] Не разрушать дерево до успешного planning overflow.
- [x] Применить defaults из настроек.
- [x] Проверить min-size до commit.
- [x] Показать событие `LayoutEnabled`.

## 6.3. Выключение режима

- [x] Реализовать выключение режима.
- [x] Оставить текущее canonical дерево как обычный FancyWM layout.
- [x] Не пытаться восстанавливать старый ручной layout.
- [x] Удалить runtime-state, не трогая окна.
- [x] Сохранить рабочее дерево валидным для обычного FancyWM.
- [x] Показать событие `LayoutDisabled`.

## 6.4. Новые окна

- [x] Перехватить placement новых окон.
- [x] До регистрации определить:
  - master;
  - satellite;
  - overflow;
  - floating fallback.
- [x] Первое окно регистрировать master.
- [x] Второе и последующие — satellite.
- [x] Не позволять новому окну кратковременно стать пятым tiled-окном.
- [x] Проверять min-size.
- [x] Проверять capacity с учётом reservations.
- [x] При локальном успехе устанавливать focus.
- [x] После размещения выполнять normalization и invariant validation.

## 6.5. Закрытие, float и unfloat

- [x] При закрытии master продвигать первый satellite.
- [x] При float master продвигать первый satellite.
- [x] При закрытии satellite сохранять canonical panel.
- [x] При одном satellite не схлопывать panel.
- [x] При отсутствии satellite растягивать master на весь `WorkArea`.
- [x] При unfloat:
  - создать master, если layout пуст;
  - добавить satellite, если есть slot;
  - выполнить overflow, если layout полон;
  - оставить floating, если overflow невозможен.
- [x] Floating окна не должны считаться занятыми slots.
- [x] Excluded окна не должны считаться slots.
- [x] Transient/dialog окна должны продолжать обрабатываться текущим `CanManage`.
- [x] Pinned окна не включать в canonical layout.

## 6.6. Generic failure handling

- [x] Если arrange падает из-за constraints, сначала определить, относится ли дерево к algorithmic mode.
- [x] Не float случайное существующее окно, если проблема вызвана новым окном и можно выполнить overflow.
- [x] Новое окно считать первым кандидатом на overflow/floating.
- [x] Не нарушать canonical layout при fallback.
- [x] Дедуплицировать сообщения для одного HWND.

---

# 7. Этап 5 — команды и горячие клавиши

## 7.1. BindableAction

- [x] Добавить:

```text
ToggleMasterSatelliteLayout
PromoteFocusedWindowToMaster
SwapMasterSide
ToggleSatelliteOrientation
ResetMasterRatio
RebalanceMasterSatelliteLayout
```

- [x] Назначить default sequence bindings, если свободны:
  - `A` — toggle layout;
  - `M` — promote;
  - `B` — swap master side.
- [x] `ResetMasterRatio` оставить без default binding, если возникает конфликт.
- [x] `RebalanceMasterSatelliteLayout` оставить без default binding, если возникает конфликт.

## 7.2. ITilingService

- [x] Добавить методы `Can...` там, где это оправдано.
- [x] Добавить:
  - toggle layout;
  - promotion;
  - swap side;
  - toggle orientation;
  - reset ratio;
  - rebalance.
- [x] Реализовать методы в `TilingService`.
- [x] Делегировать методы в `MultiDisplayTilingService`.

## 7.3. ExecuteAction

- [x] Обновить `MainWindow.ExecuteAction`.
- [x] Добавить friendly action names.
- [x] Обработать ошибки через существующий механизм toast.
- [x] Не использовать success как failure.
- [x] Не воспроизводить beep при успешном overflow.

## 7.4. Поведение существующих команд

При активном Master + Satellites:

- [x] `CreateVerticalPanel` → satellite orientation `Vertical`.
- [x] `CreateHorizontalPanel` → satellite orientation `Horizontal`.
- [x] `CreateStackPanel` → отказ с пояснением.
- [x] `PullWindowUp` на satellite → promotion.
- [x] `MoveLeft/MoveRight` на master → установка стороны master.
- [x] Move по активной оси на satellite → reorder.
- [x] Move по активной оси через непосредственно соседний master → promotion с сохранением точного satellite-slot старого master.
- [x] Swap master/satellite → promotion.
- [x] Resize width master → изменение master ratio.
- [x] `ToggleFloatingMode` → корректное удаление из canonical layout.
- [x] Вне algorithmic mode все команды работают как раньше.

## 7.5. Keybinding UI и ресурсы

- [x] Добавить новые действия в keybinding groups.
- [x] Обновить `KeybindingList`.
- [x] Обновить context hints.
- [x] Обновить neutral `Strings.resx`.
- [x] Добавить caption и description для каждой команды.
- [x] Не редактировать вручную все локализации на первом этапе.
- [x] Убедиться, что Debug assert о пропущенных enum отсутствует.
- [ ] Проверить direct hotkeys.

---

# 8. Этап 6 — AlgorithmicLayoutCoordinator

## 8.1. Общая архитектура

- [x] Создать один coordinator на весь `IWorkspace`.
- [x] Передавать один и тот же coordinator:
  - single-display `TilingService`;
  - всем сервисам внутри `MultiDisplayTilingService`.
- [x] Coordinator должен работать на WPF Dispatcher thread.
- [x] Не создавать отдельный coordinator для каждого display.
- [x] Зарегистрировать/отменить регистрацию `TilingService` при добавлении/удалении display.

## 8.2. Ответственность coordinator

- [x] Runtime states.
- [x] Capacity snapshots.
- [x] Pending transfers.
- [x] Slot reservations.
- [x] Destination search.
- [x] Transfer correlation.
- [x] Защита от transfer loops.
- [x] Различение:
  - нового окна;
  - автоматического overflow;
  - ручного перемещения пользователем.
- [x] Обработка desktop removal.
- [x] Обработка display removal.
- [x] Информационные события.
- [x] Cleanup зависших intents.

## 8.3. PendingWindowTransfer

- [x] Добавить модель:

```csharp
internal sealed record PendingWindowTransfer
{
    public required Guid CorrelationId { get; init; }
    public required IntPtr WindowHandle { get; init; }
    public required IVirtualDesktop SourceDesktop { get; init; }
    public required IVirtualDesktop TargetDesktop { get; init; }
    public required IDisplay TargetDisplay { get; init; }
    public required ReservedRole TargetRole { get; init; }
    public int? TargetSatelliteIndex { get; init; }
}
```

- [x] Добавить timestamps/deadline для cleanup зависших transfer.
- [x] Добавить состояние transfer:
  - planned;
  - reserved;
  - moving;
  - destination observed;
  - committed;
  - failed;
  - cancelled.
- [x] Сделать операции идемпотентными.
- [x] Ключевать intent по HWND и correlation ID.
- [x] Не переиспользовать intent после закрытия окна.

## 8.4. Reservations

- [x] Добавить reservation master slot.
- [x] Добавить reservation satellite slot.
- [x] Учитывать reservations при capacity query.
- [x] Не разрешать двум окнам резервировать один slot.
- [x] Освобождать reservation при success.
- [x] Освобождать reservation при failure.
- [x] Освобождать reservation при timeout.
- [x] Освобождать reservation при desktop removal.
- [x] Освобождать reservation при window close.

---

# 9. Этап 7 — overflow на виртуальные рабочие столы

## 9.1. Поиск destination

- [x] Начинать со следующего desktop по индексу.
- [x] Обходить остальные desktops циклически ровно один раз.
- [x] Не рассматривать source desktop.
- [x] Проверять `IsAlive`.
- [x] Проверять тот же display.
- [x] Проверять capacity с reservations.
- [x] Проверять min-size.
- [x] Разрешать target:
  - с активным canonical layout;
  - пустой на данном display.
- [x] Не разрушать ручной FancyWM layout.
- [x] Пропускать target с повреждённым layout, если recovery не гарантирован.
- [x] На пустом target резервировать master.
- [x] На непустом target резервировать последний satellite slot.
- [x] Не переключать пользователя на target по умолчанию.
- [x] Возвращать понятный diagnostic reason при отсутствии target.

## 9.2. Transfer flow

- [x] Реализовать последовательность:

```text
Plan
→ Reserve
→ Add PendingWindowTransfer
→ Release all locks
→ IVirtualDesktop.MoveWindow(window)
→ source WindowRemoved
→ destination WindowAdded
→ consume intent
→ register exact reserved slot
→ commit
→ release reservation
→ event
```

- [x] Не вызывать `MoveWindow()` под:
  - `m_backendLock`;
  - `m_windowSetLock`;
  - `m_floatingSetLock`;
  - `m_newWindowSetLock`;
  - coordinator global mutation lock.
- [x] Обработать порядок `WindowRemoved → WindowAdded`.
- [x] Обработать порядок `WindowAdded → WindowRemoved`.
- [x] Обработать duplicate `WindowAdded`.
- [x] Обработать duplicate `WindowRemoved`.
- [x] Не допустить двойную регистрацию.
- [x] Не допустить временную регистрацию в source tree после transfer.
- [x] Не допустить transfer loop.

## 9.3. Успешный overflow

- [x] На пустом destination окно становится master.
- [x] На частично заполненном destination окно становится последним satellite.
- [x] Применять defaults target desktop/display.
- [x] Сохранять display.
- [x] Не менять текущий desktop, если `FollowOverflowWindow=false`.
- [x] При `FollowOverflowWindow=true` переключаться только после commit.
- [x] Показать informational event:
  - title окна;
  - target desktop name/index;
  - причина overflow;
  - capacity source layout.

## 9.4. Failure и rollback

- [x] Обработать исключение `MoveWindow`.
- [x] Обработать исчезновение target desktop.
- [x] Обработать закрытие окна.
- [x] Обработать смену settings во время transfer.
- [x] Обработать удаление target display.
- [x] Освободить reservation.
- [x] Удалить pending intent.
- [x] По возможности вернуть окно на source desktop.
- [x] Оставить окно floating на source desktop.
- [x] Не пытаться бесконечно повторять transfer.
- [x] Показать failure event.
- [x] Не центрировать окно дважды.
- [x] Не выдавать несколько одинаковых toast.

## 9.5. Ручное перемещение пользователем

- [x] Определять manual desktop move отдельно от overflow.
- [x] При ручном переносе в заполненный desktop:
  - не отправлять окно автоматически дальше по цепочке;
  - оставить его floating на выбранном пользователем desktop;
  - показать одно сообщение.
- [x] Не создавать transfer loop между двумя полными desktops.
- [x] Не восстанавливать окно автоматически на старый desktop против решения пользователя.

---

# 10. Этап 8 — автоматическое создание desktops

- [x] Реализовать только для policy `MoveToExistingOrCreateDesktop`.
- [x] Проверять `CanManageVirtualDesktops`.
- [x] Не вызывать `CreateDesktop()` при policy `MoveToExistingDesktop`.
- [x] Ограничить число автоматически созданных desktops.
- [x] Значение по умолчанию — `1`.
- [x] Считать только desktops, созданные feature/coordinator в текущем сеансе.
- [x] Не удалять автоматически созданный desktop без отдельного требования.
- [x] После создания дождаться/обработать `DesktopAdded`.
- [x] Зарегистрировать desktop во всех нужных `TilingService`.
- [x] Только после готовности layout выполнять transfer.
- [x] При ошибке создания оставить окно floating.
- [x] Не переключаться на новый desktop при `FollowOverflowWindow=false`.
- [x] Присвоение имени desktop считать необязательной дополнительной возможностью.
- [x] Не создавать несколько desktops для одного окна из-за duplicate events.

---

# 11. Этап 9 — multi-monitor

- [x] Состояние ключуется парой `(desktop, display)`.
- [x] Overflow сохраняет исходный display.
- [x] На другом display того же desktop capacity считается отдельно.
- [x] `PrimaryDisplay` scope применяет layout только к основному монитору.
- [x] `AllDisplays` применяет layout ко всем.
- [x] `UltrawideDisplays` определяется по aspect ratio.
- [x] Не использовать конкретное разрешение для определения ultrawide.
- [x] Предлагаемый порог aspect ratio задокументировать.
- [x] При отключённом scope на display сохранять обычное поведение FancyWM.
- [x] При hot-plug monitor:
  - создать/удалить states;
  - отменить связанные reservations;
  - корректно завершить pending transfers.
- [x] Проверить независимость master side/orientation/ratio на разных displays.
- [x] Проверить active display routing в `MultiDisplayTilingService`.
- [x] Команды должны применяться к display focused window.

---

# 12. Этап 10 — mouse drag-and-drop

## 12.1. Безопасность

- [x] В algorithmic mode не использовать generic arbitrary nesting для canonical окон.
- [x] Не разрешать перенос canonical satellite panel как обычной панели.
- [x] Не разрешать создание nested panels в satellite panel.
- [x] Не разрешать stack.
- [x] Любой drop выполнять атомарно.
- [x] При ошибке возвращать окно в исходный slot.
- [x] После drop выполнять invariant validation.

## 12.2. Сценарии

- [x] Drag satellite на другой satellite → reorder.
- [x] Drag satellite выше/левее соседа → вставка до.
- [x] Drag satellite ниже/правее соседа → вставка после.
- [x] Drag satellite на master → promotion.
- [x] Drag master на satellite → swap/promotion.
- [x] Drag master через центральную границу → смена стороны.
- [x] Drag master наружу layout не должен разрушать дерево.
- [x] Unsupported drop → no-op + пояснение.
- [x] Drag floating window в свободный slot → tile.
- [x] Drag floating window в полный layout → overflow либо floating по policy.

## 12.3. Preview

- [x] Preview reorder.
- [x] Preview promotion.
- [x] Preview master side change.
- [x] Preview invalid drop.
- [x] Preview должен отображать предполагаемый результат, а не текущее дерево.
- [x] Preview не должен мутировать production tree.
- [x] При невозможном min-size preview должен показывать отказ.
- [x] Не оставлять stale preview после отмены drag.

---

# 13. Этап 11 — Settings UI

## 13.1. Страница

- [x] Создать:

```text
FancyWM/Pages/Settings/LayoutsPage.xaml
FancyWM/Pages/Settings/LayoutsPage.xaml.cs
```

- [x] Добавить страницу в `SettingsWindow.xaml`.
- [x] Название раздела: `Layouts` либо `Algorithmic layouts`.
- [x] Использовать существующий `SettingsViewModel`.
- [x] Использовать существующие ModernWpf styles.
- [x] Не добавлять WebView или тяжёлый UI framework.

## 13.2. Элементы управления

- [x] Enable toggle.
- [x] Display scope:
  - Primary display;
  - Ultrawide displays;
  - All displays.
- [x] Master side:
  - Left;
  - Right.
- [x] Satellite orientation:
  - Vertical;
  - Horizontal.
- [x] Master ratio slider:
  - min 50%;
  - max 80%;
  - отображение текущего процента.
- [x] Maximum satellite windows:
  - min 1;
  - max 9;
  - default 3.
- [x] Overflow policy selector.
- [x] Maximum automatically created desktops.
- [x] Follow overflow window toggle.
- [x] UWQHD preset button.
- [x] Пояснение floating fallback.

## 13.3. Preview

- [x] Реализовать lightweight WPF preview через `Grid`/`Border`.
- [x] Не использовать bitmap.
- [x] Отображать master слева/справа.
- [x] Отображать ratio.
- [x] Отображать vertical/horizontal satellites.
- [x] Отображать количество satellite до разумного визуального лимита.
- [x] Обновлять preview без перезапуска приложения.
- [x] Preview не должен участвовать в реальном layout engine.

## 13.4. Зависимые controls

- [x] При `FloatOnCurrentDesktop` скрыть/disable:
  - max auto-created desktops;
  - follow target desktop, если он не имеет смысла.
- [x] При `MoveToExistingDesktop` скрыть max auto-created desktops.
- [x] При `MoveToExistingOrCreateDesktop` показать max auto-created desktops.
- [x] При выключенной функции оставить настройки видимыми, но визуально disabled либо доступными как defaults.
- [ ] Проверить keyboard navigation.
- [x] Добавить accessibility labels/tooltips.
- [ ] Проверить DPI:
  - 100%;
  - 125%;
  - 150%;
  - 200%.
- [ ] Проверить отсутствие Debug binding errors.

## 13.5. UWQHD preset

- [x] Кнопка устанавливает:
  - enabled — не менять автоматически либо явно задокументировать;
  - ratio 60%;
  - master side Left;
  - satellite orientation Vertical;
  - max satellites 3;
  - overflow MoveToExistingDesktop;
  - follow false.
- [x] Применение preset должно сохраняться через обычный `SettingsViewModel`.
- [x] Не перезаписывать unrelated settings.

---

# 14. Этап 12 — события, toast и логирование

## 14.1. События

- [x] Добавить `AlgorithmicLayoutEvent`.
- [x] Виды событий:
  - `LayoutEnabled`;
  - `LayoutDisabled`;
  - `WindowMovedToDesktop`;
  - `WindowLeftFloating`;
  - `OperationRejected`;
  - `LayoutRecovered`;
  - `TransferFailed`;
  - `TransferCancelled`.
- [x] Не использовать `PlacementFailed` для успешного overflow.
- [x] Передавать:
  - HWND/cached title;
  - source desktop;
  - target desktop;
  - display;
  - correlation ID;
  - reason;
  - user-facing message key.

## 14.2. Toast

- [x] Успешный overflow:

```text
Terminal moved to Desktop 2

Desktop 1 reached its 4-window limit.
```

- [x] Floating fallback:

```text
Visual Studio left floating

Master + Satellites is full on all available desktops.
Capacity: 4 windows per desktop.
```

- [x] Promotion rejected:
  - объяснить, что старый master не помещается в satellite slot.
- [x] Orientation rejected:
  - объяснить min-size conflict.
- [x] Не использовать failure sound при success.
- [x] Уважать существующую настройку `SoundOnFailure`.
- [x] Дедуплицировать toast для одного transfer.
- [x] Не показывать внутренние exception messages пользователю.

## 14.3. Serilog

- [x] Логировать correlation ID.
- [x] Логировать source/target desktop.
- [x] Логировать reservation.
- [x] Логировать capacity snapshot.
- [x] Логировать invariant failures.
- [x] Логировать recovery.
- [x] Не спамить Information на каждом layout tick.
- [x] Использовать Debug/Verbose для частых внутренних операций.
- [x] Исключения не должны silently swallow без fallback и записи в лог.

---

# 15. Этап 13 — тесты layout engine

- [x] Пустой layout валиден.
- [x] Один master занимает весь `WorkArea`.
- [x] Второе окно создаёт разделение 60/40.
- [x] Master слева.
- [x] Master справа.
- [x] Один vertical satellite.
- [x] Три vertical satellite.
- [x] Один horizontal satellite.
- [x] Три horizontal satellite.
- [x] Порядок satellite сохраняется.
- [x] Promotion S1.
- [x] Promotion S2.
- [x] Promotion S3.
- [x] Promotion сохраняет точный slot.
- [x] Promotion текущего master — no-op.
- [x] Promotion отклоняется при min-size conflict.
- [x] Reorder first → last.
- [x] Reorder last → first.
- [x] Reorder middle.
- [x] Master side swap сохраняет ratio.
- [x] Master side swap сохраняет satellite order.
- [x] Orientation switch сохраняет order.
- [x] Orientation switch reset flex.
- [x] Невозможная orientation отклоняется атомарно.
- [x] Requested ratio сохраняется отдельно от effective.
- [x] Ratio clamp по master min-size.
- [x] Ratio clamp по satellite min-size.
- [x] Полностью невозможный layout возвращает failure.
- [x] Удаление satellite.
- [x] Удаление последнего satellite.
- [x] Удаление master продвигает S1.
- [x] Удаление последнего окна.
- [x] `AutoCollapsePanels=true` не ломает canonical tree.
- [x] `ValidateInvariant()` на корректном дереве.
- [x] `ValidateInvariant()` обнаруживает nested panel.
- [x] `ValidateInvariant()` обнаруживает stack.
- [x] `ValidateInvariant()` обнаруживает placeholder.
- [x] `ValidateInvariant()` обнаруживает duplicate.
- [x] `Normalize()` восстанавливает поддерживаемое повреждение.
- [x] Recovery failure не удаляет окна.

---

# 16. Этап 14 — тесты TilingService и coordinator

## 16.1. Capacity

- [x] `MaxSatellites=1` → capacity 2.
- [x] `MaxSatellites=3` → capacity 4.
- [x] `MaxSatellites=9` → capacity 10.
- [x] Floating окна не занимают capacity.
- [x] Excluded окна не занимают capacity.
- [x] Dialog/transient окна не занимают capacity.
- [x] Pinned окна не занимают capacity.
- [x] Reservation занимает capacity до commit/cancel.

## 16.2. Новые окна

- [x] Первое окно → master.
- [x] Второе → satellite.
- [x] Четвёртое при default → третий satellite.
- [x] Пятое не регистрируется локально.
- [x] Пятое планирует overflow до local mutation.
- [x] Min-size failure нового satellite запускает overflow.
- [x] При невозможном overflow окно floating.

## 16.3. Overflow

- [x] Target desktop пуст → окно становится master.
- [x] Target частично заполнен → окно становится satellite.
- [x] Полный target пропускается.
- [x] Target с reservation последнего slot пропускается.
- [x] Target с ручным layout пропускается.
- [x] Dead target пропускается.
- [x] Source desktop повторно не выбирается.
- [x] Поиск начинается со следующего desktop.
- [x] Циклический обход выполняется один раз.
- [x] Нет destination → floating на source.
- [x] VDM unavailable → floating.
- [x] `MoveWindow` exception → rollback/floating.
- [x] Target removed mid-transfer.
- [x] Window closed mid-transfer.
- [x] Display removed mid-transfer.
- [x] Settings changed mid-transfer.
- [x] Duplicate `WindowAdded`.
- [x] Duplicate `WindowRemoved`.
- [x] `WindowAdded` приходит раньше `WindowRemoved`.
- [x] `WindowRemoved` приходит раньше `WindowAdded`.
- [x] Два simultaneous overflow не используют один slot.
- [x] Три simultaneous overflow корректно распределяются.
- [x] Reservation освобождается после success.
- [x] Reservation освобождается после failure.
- [x] Timeout очищает reservation.
- [x] Нет transfer loop.
- [x] Manual move в полный desktop оставляет floating именно там.

## 16.4. Auto-create desktop

- [x] Policy `MoveToExistingDesktop` не создаёт desktop.
- [x] Policy `MoveToExistingOrCreateDesktop` создаёт при отсутствии target.
- [x] `CanManageVirtualDesktops=false` → floating.
- [x] Достигнут `MaxAutoCreatedDesktops` → floating.
- [x] Duplicate event не создаёт второй desktop.
- [x] Новый desktop зарегистрирован до transfer.
- [x] Follow=false не переключает desktop.
- [x] Follow=true переключает только после commit.

## 16.5. Multi-monitor

- [x] Состояния двух displays независимы.
- [x] Capacity считается отдельно.
- [x] Overflow сохраняет display.
- [x] PrimaryDisplay scope.
- [x] UltrawideDisplays scope.
- [x] AllDisplays scope.
- [x] Active display command routing.
- [x] Display hot-plug cleanup.
- [x] Display removal отменяет pending reservations.

---

# 17. Этап 15 — integration/UI tests и ручные проверки

## 17.1. Основной сценарий UWQHD

- [x] Запустить на `3440×1440`.
- [ ] Окно A занимает весь экран.
- [ ] Окно B создаёт 60/40.
- [ ] Окна B/C/D располагаются вертикально.
- [x] Переключение делает B/C/D горизонтальными.
- [ ] Master переносится вправо.
- [ ] Master возвращается влево.
- [ ] Promotion C даёт:
  - C master;
  - A в прежнем slot C.
- [ ] Satellite reorder работает клавиатурой.
- [ ] Satellite reorder работает мышью.
- [x] Пятое окно отправляется на Desktop 2.
- [ ] Текущий desktop не переключается.
- [ ] При полном Desktop 2 окно остаётся floating.
- [ ] Toast содержит правильный desktop и capacity.

## 17.2. Другие разрешения

- [ ] `2560×1440`.
- [ ] `1920×1080`.
- [ ] `3840×1600`.
- [ ] Portrait monitor.
- [ ] Проверить отсутствие зависимости от UWQHD pixel size.
- [ ] Проверить разумное поведение horizontal satellites на узком display.

## 17.3. DPI

- [ ] 100%.
- [ ] 125%.
- [ ] 150%.
- [ ] 200%.
- [ ] Смена DPI во время работы.
- [ ] Перетаскивание между мониторами с разным DPI.
- [ ] Preview и реальные окна совпадают.

## 17.4. Жизненный цикл окон

- [ ] Открытие нескольких окон одного процесса.
- [ ] Быстрое массовое открытие окон.
- [ ] Закрытие master.
- [ ] Закрытие нескольких satellite подряд.
- [ ] Minimize/restore master.
- [ ] Minimize/restore satellite.
- [ ] Maximize/restore.
- [ ] Float/unfloat.
- [ ] Приложение с большим `MinSize`.
- [ ] Нересайзабельное окно.
- [ ] Диалоговое окно.
- [ ] UWP/WinUI окно.
- [ ] Elevated приложение при обычном FancyWM.
- [ ] Приложение, меняющее собственный размер.

## 17.5. Настройки

- [ ] Настройки применяются без перезапуска.
- [ ] Изменение master ratio.
- [ ] Изменение max satellites.
- [ ] Уменьшение max satellites обрабатывает лишние окна с конца.
- [ ] Увеличение max satellites не выполняет неожиданный backfill.
- [ ] Изменение overflow policy.
- [ ] UWQHD preset.
- [ ] Перезапуск FancyWM сохраняет defaults.
- [x] Runtime-роли не сериализуются.
- [x] Старый `settings.json` загружается.

---

# 18. Этап 16 — уменьшение MaxSatellites

- [x] При уменьшении лимита определить лишние satellite с конца.
- [x] Не менять первые satellite.
- [x] Не менять master.
- [x] Для каждого лишнего окна выполнить overflow planning.
- [x] Сначала зарезервировать все доступные destinations либо применять последовательную безопасную стратегию.
- [x] Не мутировать source layout до гарантированного результата для текущего окна.
- [x] При отсутствии destination оставить лишнее окно floating на source.
- [x] Показать сводное либо дедуплицированное сообщение.
- [x] Не выполнять автоматический backfill при увеличении лимита.
- [x] Возможный future backfill задокументировать как не реализованный, если он не входит в scope.

---

# 19. Этап 17 — Rebalance

- [x] Определить точное назначение `RebalanceMasterSatelliteLayout`.
- [x] Предлагаемое поведение:
  - восстановить canonical tree;
  - применить текущую сторону master;
  - применить текущую orientation;
  - применить requested/default master ratio;
  - равномерно распределить satellite;
  - сохранить master и visual order.
- [x] Не возвращать окна с других desktops.
- [x] Не включать floating окна автоматически.
- [x] Не менять master без необходимости.
- [x] Выполнять операцию атомарно.
- [x] Добавить тесты recovery/rebalance.
- [x] Показать toast только при реальном изменении или recovery.

---

# 20. Этап 18 — совместимость и регрессии

- [x] При `Enabled=false` все существующие tests проходят без изменений.
- [x] Обычные horizontal/vertical/stack panels работают как раньше.
- [x] `AutoSplitCount` работает как раньше вне algorithmic mode.
- [x] `AutoCollapsePanels` работает как раньше вне algorithmic mode.
- [ ] Existing virtual desktop hotkeys работают.
- [ ] Existing multi-display hotkeys работают.
- [ ] Existing direct keybindings работают.
- [ ] Existing drag-and-drop работает вне algorithmic mode.
- [ ] Existing floating behavior работает.
- [ ] Existing exclusion rules работают.
- [x] Existing startup settings не повреждены.
- [x] Existing settings comments сохраняются.
- [x] Нет случайных изменений public API submodules.
- [x] Нет изменений SHA submodules без необходимости.
- [x] Нет нового постоянного background polling с высокой частотой.
- [ ] Idle CPU usage не ухудшился заметно.
- [x] Нет утечек event subscriptions.
- [x] Coordinator корректно `Dispose()`.

---

# 21. Этап 19 — сборка и качество кода

- [x] Debug build всего решения.
- [x] Release build всего решения.
- [x] `FancyWM.Tests`.
- [x] `FancyWM.Layouts.Tests`.
- [x] Проверить warnings.
- [x] Не добавлять глобальное подавление warnings ради новой функции.
- [x] Nullable warnings обработаны корректно.
- [~] Нет `async void`, кроме UI/event handlers, где это оправдано.
- [~] Нет fire-and-forget задач без обработки исключений.
- [~] Нет blocking wait на Dispatcher thread.
- [x] Locks используются в согласованном порядке.
- [x] `MoveWindow()` не вызывается под locks.
- [x] Все reservations очищаются через гарантированный путь.
- [x] Все event subscriptions отписываются.
- [~] Нет unreachable `NotImplementedException`.
- [x] Нет временных debug hacks.
- [x] Нет hardcoded пользовательских путей.
- [x] Нет hardcoded desktop indexes, кроме отображения/тестов.
- [x] Нет зависимости от языка Windows.
- [x] Нет зависимости от title окна для идентификации.
- [x] Логирование не раскрывает чувствительные данные сверх уже принятого поведения проекта.

---

# 22. Этап 20 — документация

- [x] Обновить `docs/master-satellite-layout.md`.
- [x] Описать:
  - назначение;
  - canonical layout;
  - настройки;
  - горячие клавиши;
  - overflow;
  - floating fallback;
  - multi-monitor;
  - min-size limitations;
  - поведение при выключении.
- [x] Добавить диаграммы master left/right.
- [x] Добавить примеры vertical/horizontal satellite.
- [x] Описать promotion.
- [x] Описать, что старый master занимает slot выбранного satellite.
- [x] Описать отсутствие automatic backfill.
- [x] Описать manual move behavior.
- [x] Обновить README кратким упоминанием функции.
- [x] Обновить changelog.
- [x] Обновить `IMPLEMENTATION_STATUS.md`.
- [x] Перечислить известные ограничения.
- [x] Указать команды сборки и тестов.
- [x] Не заявлять о поддержке сценария, который не проверен.

---

# 23. Acceptance criteria

Функция готова, когда одновременно выполняются все условия:

- [x] На пустом desktop окно A становится master и занимает весь `WorkArea`.
- [x] Окно B создаёт layout 60/40.
- [x] Окна B/C/D располагаются в satellite panel.
- [x] Default satellite orientation — Vertical.
- [x] Orientation можно переключить на Horizontal без изменения порядка.
- [x] Master можно переместить слева направо и обратно.
- [x] Ширина master сохраняется при смене стороны.
- [x] Satellite можно переставлять внутри satellite layout.
- [x] Satellite C можно сделать master.
- [x] После promotion C старый master A занимает прежний slot C.
- [x] При default limit пятое окно не появляется как пятый tile.
- [x] Пятое окно перемещается на следующий подходящий desktop.
- [x] На пустом target desktop оно становится master.
- [x] При отсутствии target оно остаётся floating на source desktop.
- [ ] Пользователь получает корректное сообщение.
- [x] Успешный overflow не воспроизводит failure sound.
- [x] Закрытие master продвигает первый satellite.
- [x] При одном master он снова занимает весь `WorkArea`.
- [x] `AutoCollapsePanels` не ломает canonical tree.
- [x] `AutoSplitCount` не управляет количеством satellite.
- [x] Multi-monitor state разделён по `(desktop, display)`.
- [x] Старый `settings.json` загружается.
- [x] При выключенной функции FancyWM работает как до изменений.
- [x] Debug и Release builds успешны.
- [x] Все существующие и новые тесты проходят.
- [x] Нет случайных изменений submodules.
- [x] Документация соответствует фактической реализации.

---

# 24. Рекомендуемый порядок реализации

1. [x] Baseline и документация.
2. [x] Settings/domain model.
3. [x] Pure `MasterSatelliteLayoutEngine`.
4. [x] Unit tests engine.
5. [x] `TilingWorkspace` integration.
6. [x] Local `TilingService` placement.
7. [x] Commands и keybindings.
8. [x] Coordinator и reservations.
9. [x] Existing-desktop overflow.
10. [x] Auto-create desktop.
11. [x] Multi-monitor.
12. [x] Settings UI.
13. [~] Mouse interaction.
14. [x] Full regression suite.
15. [ ] Manual UWQHD verification.
16. [x] Documentation and final cleanup.

---

# 25. Финальный отчёт Codex

Перед завершением Codex должен вывести:

- [x] Текущую ветку и commit.
- [x] Полный список изменённых файлов.
- [x] Краткое описание архитектуры.
- [x] Какие сценарии реализованы.
- [x] Какие сценарии не реализованы.
- [x] Результат Debug build.
- [x] Результат Release build.
- [x] Результат `FancyWM.Tests`.
- [x] Результат `FancyWM.Layouts.Tests`.
- [x] Baseline failures, если они были.
- [x] Новые известные ограничения.
- [x] Остаток unchecked пунктов этого файла.
- [x] `git status --short`.
- [x] Проверку отсутствия неожиданных submodule changes.
