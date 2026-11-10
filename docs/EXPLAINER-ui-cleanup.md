# Explainer — чистое меню приложения и вынос research из `main`

## Background

ElasticMem — десктопное приложение (.NET 8, WinForms, `net8.0-windows`). Точка входа —
`Program.cs`, окно — `MainForm`, навигация — `AppShell` (левое меню с кнопками-разделами).
Каждый «раздел» — это `TabPage` внутри скрытого `TabControl`: `AppShell` сам рисует кнопки по
`tabs.TabPages` и переключает `SelectedTab`.

Исторически в окне скопилось **13 вкладок**, перемешавших три несовместимых аудитории:

- рабочие пользовательские функции (освободить память, профили приложений, фоновый агент);
- измерительные инструменты (анализ процесса, бенчмарк, «сжатая куча»);
- чистый research, который нативно не доведён или закрыт (WHP/VMM, драйвер `ElasticMemStore.sys`,
  Backend VM с Hyper-V/RDS, realtime-сжатие, `ElasticArray64`, связанные буферы `B = F(A) ⊕ R`).

> **Почему это проблема.** Research-код тащил за собой зависимости в проекте приложения:
> `../ElasticMem.Vmm/*.cs` целиком, исходники клиента драйвера (`driver/client/*.cs`),
> семь встроенных backend-ресурсов (`.md`/`.ps1`) и пакет `Vortice.Direct3D12` — всё это попадало
> в поставляемый `ElasticMem.exe`, хотя обычному пользователю не нужно и местами даже не работает.

## Intuition

Ключевая идея разделения — **один артефакт, одна аудитория**. `main` должен собирать
маленькое понятное приложение; всё экспериментальное живёт в ветке `claude/research`.

Простой пример: раньше, чтобы «сжать куче» нужен был `NtXpressCodec` — ровно **один** файл из
проекта `ElasticMem.Vmm`. Но приложение включало **весь** `ElasticMem.Vmm/*.cs`, то есть и
`WhpSyntheticVm`, `WhpPageBacking`, `WhpHostTrimProbe` — синтетический VMM, который без драйвера
всё равно не запускается. Мы заменили «звёздочку» на один файл:

```
../ElasticMem.Vmm/*.cs   →   ../ElasticMem.Vmm/NtXpressCodec.cs
```

и research-слой просто перестал компилироваться в пользовательский `.exe`, ничего при этом не
сломав в разделе «Сжатая куча».

## Code

**1. Навигация (`AppShell.cs`).** Убраны research-бренд («ElasticMem · Исследования») и кнопка
«Проверить всё (читает память)», которая запускала авто-research. Осталось чистое левое меню:
бренд «ElasticMem · Сжатие памяти», крупные кнопки-разделы, переключатель темы. Публичный
API сузился: конструктор теперь `AppShell(TabControl tabs, CheckBox theme)` без
`Action? automatic`, метод `SetResearchBusy` удалён.

**2. Окно (`MainForm.cs`).** 13 вкладок → 6 верхнеуровневых разделов, измерительные инструменты
сгруппированы во вложенный `TabControl` раздела «Проверка»:

```
Освободить память   (RamSaverTab — одна кнопка, реальный замер RAM до/после)
Игры и приложения   (AppsTab)
Фоновый агент       (IdleTrimAgent — вся логика сохранена)
Проверка            ├─ Сжатая куча (CompressedPageTable + XPRESS)
                    ├─ Анализ памяти (только чтение)
                    └─ Бенчмарк (модельные потоки)
Журнал
Обновление
О программе
```

Удалены поля и обработчики удалённых вкладок (`_researchTab`, `_backendTab`, `_txtAccess`,
`BuildAccessTab`, realtime). Из `ToggleAgent` убран guard по research, из `OnLoaded` и трея —
ветки `BackendPanel`. Логика агента (`ToggleAgent`, `RefreshUi`, `FillProcessCombos`,
`CheckMmAgent`, `RunAnalyze`, `RunBench`) не тронута.

**3. Запуск (`Program.cs`).** Убраны `ResearchCli.TryRun`, режимы `--backend-agent/--backend-ui`
и mutex backend-агента. Путь crash-лога больше не зависит от research-кода:

```
ResearchJobs.Root/diagnostics   →   AppSettings.Dir/diagnostics
```

**4. Проект (`ElasticMem.App.csproj`).** Удалены: `Compile` клиента драйвера и `ElasticMem.Vmm/*.cs`
(оставлен только `NtXpressCodec.cs`), семь backend-`EmbeddedResource`, `windows-check` инструкция
и `Vortice.Direct3D12` (использовался только `GpuResearch`).

**5. Удалённые из приложения файлы** (сохранены в `claude/research`):
`src/ElasticMem.App/Research/` (целиком), `src/ElasticMem.App/Backend/` (целиком),
`RealtimeCompression.cs`, `ElasticHeap.cs`.

## Verification

- **Сборка** (на Linux через `EnableWindowsTargeting`):
  `dotnet build src/ElasticMem.App -c Release /p:EnableWindowsTargeting=true` →
  **Build succeeded, 0 Warning(s), 0 Error(s)**.
- **Тесты ядра**: `dotnet run --project tests/ElasticMem.Tests -c Release -- --bench` — все стратегии
  проходят (OK=да), ядро не затронуто.
- **Ручная проверка (на Windows):** запустить `ElasticMem.exe`; убедиться, что в левом меню ровно
  6 разделов, «Освободить память» открывается первой, «Проверка» содержит три вложенные вкладки,
  тёмная тема переключается, в разделах нет упоминаний research/Backend/VMM. Проверить CLI:
  `--selftest`, `--scan chrome`, `--agent --all --tray`.

## Alternatives

| Подход | Плюсы | Минусы |
|---|---|---|
| **Выбранный:** вынести research из проекта приложения, оставить нижние библиотеки (Vmm/driver/tests) в дереве | Маленькая безопасная правка; `main` собирает чистый `.exe`; ничего не потеряно (ветка research) | Исходники Vmm/driver физически ещё в дереве `main`, хоть и не в поставке |
| Физически удалить проекты `ElasticMem.Vmm`/`driver` и их тесты из `main` | Дерево `main` полностью «рабочее» | Правка `.sln`, тестов и CI; высокий риск сломать сборку одним заходом |

## Suggested people to talk to

Это по сути solo-проект: история правок `MainForm.cs`/`AppShell.cs` принадлежит владельцу
(коммиты `ElasticMem Research` / `Goddamnmagadem`) и AI-ассистентам. Обсуждать изменения имеет
смысл с **владельцем репозитория** — у него контекст по тому, какие вкладки реально использовались
и какие профили приложений важны. Внешних контрибьюторов, затронутых этой правкой, нет.

## Quiz

<details><summary>1. Почему «Сжатая куча» продолжает работать, хотя `ElasticMem.Vmm/*.cs` убран из проекта?</summary>

Потому что ей нужен только `NtXpressCodec`, который мы оставили точечным include. Остальные
файлы Vmm (WHP synthetic VM и пр.) ей не нужны. **Верно:** зависимость была ровно на один класс.
</details>

<details><summary>2. Что станет с crash-логом после удаления research-кода?</summary>

Он пишется в `AppSettings.Dir/diagnostics/crash.log` вместо `ResearchJobs.Root/diagnostics`.
Функционально то же место (`%LOCALAPPDATA%\ElasticMem\...`), но без зависимости от research.
</details>

<details><summary>3. Куда делись realtime-сжатие, Backend VM и драйвер?</summary>

Они не удалены безвозвратно — полный снимок дерева лежит в ветке `claude/research`. Из `main`
убран только их вызов из приложения.
</details>

<details><summary>4. Почему выбран «мягкий» вынос, а не физическое удаление проектов Vmm/driver?</summary>

Чтобы одним PR не трогать `.sln`, тесты и CI и не рисковать сборкой. Поставляемый `.exe` уже
чистый; физическая вырезка библиотек — отдельный последующий шаг.
</details>

<details><summary>5. Изменилась ли логика фонового агента при переписывании меню?</summary>

Нет. `IdleTrimAgent` и все методы (`ToggleAgent`, `RefreshUi`, `CheckMmAgent`…) перенесены как
есть; поменялись только расположение вкладки и текст подсказок.
</details>
