# ElasticMem 0.30 — тип1 наш SYS, тип2 Hyper-V

## Главное: это ещё не собственный гипервизор

Ваш компьютер без Hyper-V **не обязан устанавливать Hyper-V для SYS page-store**. SYS API использует KMDF/Rtl XPRESS, а не Hyper-V/PowerShell Direct/WHP. Однако SYS пока **не запускает Windows-VM**. Для этого нужны собственный VMM/CPU/device/firmware runtime, безопасные vCPU/DMA barriers, обработка exits и paging policy. Их законченной реализации в0.30 нет. Сжатие Chrome/Telegram, выполняющихся в своей Windows-VM через SYS, и физическая экономия RAM не доказаны.

Не устанавливайте экспериментальный kernel driver на свой единственный рабочий компьютер. Нужна отдельная одноразовая Windows x64 тестовая среда — можно на другом разрешённом гипервизоре/компьютере, Hyper-V для page-store не требуется. Если такой среды нет, ограничьтесь просмотром/сборкой исходников. Нельзя отключать Secure Boot/HVCI/подпись ради запуска.

## Профили каждого типа

Вкладка **VM · SYS / Hyper-V** имеет каталог до32 профилей:
- **Тип1 — наш SYS (VMM не готов)**. Кнопка сохранения/проверки вызывает только `ElasticMemStore` IOCTL. Нет Hyper-V imports/cmdlets/WHP и нет автоматического fallback. `driver_protocol_reachable=true` означает доступность page-store, НЕ работающую VM. `blocked_vm_runtime_missing` — ожидаемый честный результат даже при доступном SYS.
- **Тип2 — Hyper-V**. Сохраняется прежний сценарий создания VM, PowerShell Direct, Windows guest Compression/DM и RemoteApp с его prerequisites. На компьютере без Hyper-V этот вариант недоступен; программа не превращает его автоматически в тип1.

Выберите **Тип нового → Новый профиль выбранного типа**. Затем используйте список **Профили VM**. Тип хранится у каждого профиля, не является общим переключателем всех VM. Переименование не меняет тип. Тип уже сохранённого профиля не конвертируется скрыто: создайте отдельный профиль. Перенос/миграция работающей VM между гипервизорами не реализован.

Новый профиль типа1 — **проект будущей VM**, без фиктивного runtime VM GUID и без автозапуска. Кнопки Hyper-V управления для него выключены; попытка создания Windows-VM возвращает отсутствие VMM без обращения к Hyper-V. Создание профиля нельзя выдавать за создание VM.

Старый одиночный профиль0.29 импортируется как тип2, потому что он действительно был Hyper-V. Он не переименовывается в SYS. Каталог лежит в защищённом ProgramData/ElasticMemBackendTools/<user SID>.catalog.json. Guest credentials остаются scoped по реальному guest VM GUID; миграции паролей между VM/типами нет. При изменении active profile другим процессом операция отвергается, а не отправляется другой VM.

## Реальные изменения в исходниках SYS0.30

Добавлены:
1. `PAGER_CAPABILITIES` — page size, feature bits, default/max slots, sessions/quota/global limit. Новый клиент отвергает старый/несовместимый SYS, не подменяет его Hyper-V или RAW.
2. `CONFIG_STORE` — изменение ёмкости таблицы/квоты только пустого store. Новая таблица выделяется **до** освобождения старой; при allocation/global quota failure старый store остаётся целым.
3. Capacity1..1,048,576 page keys; default4096. Это до4GiB **пространства ключей логических4KiB страниц**, НЕ выделенные4GiB RAM и НЕ работающая Windows-VM. Metadata таблицы учитывается в квоте. Максимальная session quota32MiB; global tracked-allocation limit64MiB и max8 handles остаются. Лимит учитывает запрошенные размеры allocation, не полное фактическое потребление pool/physical RAM: rounding и allocator headers отдельно не измерены. Это bounded research store, не полноценный backing store большой VM.
4. Resize/reset не сбрасывают поколения. Старые generation receipts не могут восстановить/удалить новые данные. При непустом store resize запрещён.
5. Существующие compressed-only PUT/GET_VERSIONED/DELETE_VERSIONED и CRC проверки сохранены. Legacy RAW API остаётся для старого smoke-теста, но SYS VMM adapter не использует RAW fallback.

В приложении можно выбрать **SYS slots / Session quota MiB** для owned-buffer проверки. После сохранения они хранятся в профиле; изменение UI до сохранения влияет только на запущенный fixture. Значения применяются к временному file-handle session, не меняют глобальную защиту и не запускают VMM. Квота должна вмещать таблицу (`slots*8` на x64) и payload. Не устанавливайте огромную ёмкость ради «экономии»: одна metadata таблица тоже расходует память.

## Сборка и проверка

**Новый SYS0.30 не собран и не исполнен в Windows здесь.** Предыдущие `driver/verification` и старые SYS0.24/0.28 не квалифицируют эту ревизию. Нативная работа GUI/профилей/Hyper-V также не проверена.

В исходниках: VS2022 Desktop C++ + SDK/WDK10.0.26100 + .NET8 SDK, Developer PowerShell:
```powershell
.\driver\scripts\Build-Windows.ps1 -Configuration Release
```
После штатной сборки подпишите и загрузите драйвер **только утверждённым вашей средой способом в disposable Windows**. `Install-SignedDriver.ps1` требует `-Path` и `-ResearchVM`, проверяет доверенную подпись; не меняет enforcement. При отказе остановитесь. LLD/relink обходы не используйте.

От администратора в тестовой Windows:
```powershell
.\driver\out\client\ElasticMem.DriverClient.exe capabilities caps.json
.\driver\out\client\ElasticMem.DriverClient.exe pager-smoke pager-smoke.json
.\driver\out\client\ElasticMem.DriverClient.exe smoke legacy-smoke.json
```
`pager-smoke` использует новый configure-store, high-key test, отказ resize непустого store и64 собственные native страницы: compress in SYS → receipt → decommit → versioned restore → full-byte comparison. Не чужие процессы, не Windows guest. GUI SYS-кнопка запускает этот же fixture. На старом SYS остановится до decommit из-за отсутствующего capabilities/configure API.

Логи: SYS fixture — `%LOCALAPPDATA%\ElasticMem\sys-pager-reports`, статус выбранного профиля — backend journal/export. Сырые страницы, credentials, VM диски/ключи не экспортируются; metadata профилей и лимиты могут присутствовать. Просмотрите логи перед отправкой.

## Что проверено локально

Portable C store ASan/UBSan с **тестовым codec, не XPRESS**: resize/failure preservation/quota/high key/generation/reset/wipe плюс прежние randomized tests. Portable .NET model tests: типы, catalog persistence/selection, guards против Hyper-V fallback, старые backend/creation/VMM lifecycle тесты. PowerShell parser — только синтаксис. Windows app/client compilation — не нативный запуск.

Windows11/HVCI/Driver Verifier/долгий stress и реальная физическая экономия RAM не доказаны. Следующий крупный незавершённый этап — собственный VMM, а не новая надпись в selector.
