# SYS0.30 source update — native binary NOT built/tested

Capabilities negotiation, bounded empty-store resize (up to1,048,576 page keys,32MiB session max,64MiB global unchanged), generation retention and failure preservation. See backend/SYS-TYPES-030.md. Old reports below do not qualify this revision. No own Windows-VMM/transparent app paging.

# 0.28 SYS paging foundation — UNQUALIFIED DRIVER SOURCE

New source changes invalidate the old native smoke report as evidence for this revision. No new SYS is built or executed here. Existing `driver/verification` refers only to the previous revision.

Added compressed-only PUT receipt, monotonic per-open generation and conditional GET/DELETE. PUT rejects RAW or payload+node allocation >=4096 without replacing prior data. Reset does not reset generations. Core CRC validates restored bytes. Legacy ABI1 remains; node overhead is now24 bytes. Session16MiB/global64MiB/max4096 pages are unchanged: NOT enough for a whole browser VM. Paged-pool allocation is not a physical-RAM-saved metric.

Custom VMM adapter uses only new compressed-only/generation IOCTLs. Old installed SYS rejects them: it MUST NOT silently fall back to raw/Windows Memory Compression. Automatic windows-bootable VMM, vCPU/DMA barrier and cold-page policy remain missing.

Windows test: build with `driver/scripts/Build-Windows.ps1` in VS2022 Developer PowerShell + WDK10.0.26100, sign using approved existing driver trust, install ONLY in disposable authorized VM; do not disable security. Then `ElasticMem.DriverClient.exe pager-smoke pager-smoke.json` or use Backend VM → SYS owned-buffer test in app0.28. It checks synthetic own VirtualAlloc pages, SYS compressed storage, native decommit and full restore. NOT Edge/Chrome/Telegram paging. If policy/trust blocks the driver, stop.

Read `backend/SYS-028.md` before testing. Prior full driver instructions below refer to legacy operations.

# ElasticMemStore — исследовательский драйвер Windows x64

**Статус: KMDF kernel-хранилище + клиент; нативный smoke прошёл на Windows Server 2025 x64. Не production, не прозрачный RAM-оптимизатор; Windows 11 пока не проверена.**

Подробности и исходные доказательства: `verification/REPORT.md`. 4042 проверки RAW/XPRESS, штатные unload/reload и restricted-token ACL прошли. Driver Verifier, HVCI, HLK, независимая обычная учётная запись и длительный stress не проверены.

## Что он делает

- Штатный non-PnP KMDF control device `\\.\ElasticMemStore`, ABI v1.
- PUT/GET/DELETE/RESET/STATS/CONFIG, ровно 4096 байт на страницу, ключи 0…4095.
- XPRESS через документированные `RtlCompressBuffer`/`RtlDecompressBuffer`; если нет уменьшения или codec не принимает блок — исходные байты. Не zstd в ядре.
- CRC32 после восстановления; ошибка возвращается без выдачи испорченных байтов. CRC — детектор случайного повреждения, не криптографическая защита от другого kernel-драйвера.
- Caller buffer не удерживается. При отказе overwrite предыдущие данные остаются. RESET/закрытие очищают байты перед освобождением.
- Сессия принадлежит FILE_OBJECT: отдельные CreateFile изолированы; DuplicateHandle разделяет ту же сессию.
- Предел 8 сессий, 4096 страниц/сессию, 16 МиБ запрошенных allocations/сессию, 64 МиБ tracked allocations устройства. Таблица указателей входит в quota; workspace и временный новый overwrite входят в device limit. Поэтому raw-сессия в x64 вмещает 4076, не 4096 заполненных страниц.
- Только SYSTEM и elevated Administrators; METHOD_BUFFERED, нет указателей, PID/адресов чужого процесса, DMA, hooks или произвольной kernel-памяти.
- PASSIVE_LEVEL, один device wait-lock для create/cleanup/IOCTL; sequential queue, атомарный краткий request. Отмена до начала операции; уже начатое обновление может завершиться успешно. Не async-pager.
- Paged pool: ОС может вытеснять его; страницы **не гарантированно физически резидентны**.

## Чего он НЕ делает

Не подменяет Windows Memory Manager, не перехватывает page faults/записи в Chrome или Telegram, не отключает pagefile/Memory Compression, не инжектирует код, не обходит античит, подпись, Secure Boot или HVCI. Это kernel backend для **явно интегрированного** клиента, который владеет страницами и освобождает исходные буферы. Перенос копии чужой страницы в этот драйвер сам по себе ничего не освобождает.

`session_allocation_bytes/global_allocation_bytes` — суммы размеров запросов allocator, НЕ фактическая физическая RAM. Они не включают округления pool, KMDF/file/request contexts, SystemBuffer, образ драйвера и весь процесс клиента. Нельзя выдавать разницу payload за экономию RAM рабочего стола.

Хранилище volatile. Закрытие последнего дескриптора сессии/сбой/перезагрузка уничтожают данные. Не хранить единственную копию важных данных.

## Сборка и загрузка

1. **Отдельная Windows 11 VM**, снимок до установки. Не основной ПК. Драйвер может вызвать BSOD; статические/portable тесты этого не исключают.
2. VS 2022 Desktop C++, SDK и WDK 10.0.26100, KMDF 1.33; .NET 8 SDK.
3. Developer PowerShell: `./driver/scripts/Build-Windows.ps1`.
4. Подписать SYS своим согласованным процессом разработки/поставки драйверов. Private key не включать в архив и не отправлять в чат. Production требует принятого Windows signing pipeline.
5. Установка требует уже **валидной доверенной подписи**. Скрипт ничего не меняет в security policy. Если текущая политика VM не допускает подпись — остановиться; не отключать защиту ради этого файла.
6. Elevated PowerShell: `./driver/scripts/Install-SignedDriver.ps1 -Path ./driver/out/Release/ElasticMemStore.sys -ResearchVM`.
7. `./driver/out/client/ElasticMem.DriverClient.exe smoke kernel-smoke.json` либо приложение: Лаборатория → «Драйвер · проверка kernel-хранилища (только VM)». Приложение должно быть elevated. Автоустановки/auto-elevation нет.
8. Закрыть клиенты, `./driver/scripts/Uninstall-Driver.ps1`.

**Старые `kernel-cross/ElasticMemStore.sys` и `kernel-cross-UNSIGNED/ElasticMemStore.sys` отозваны как исследовательские артефакты: НЕ устанавливать даже после подписи.** LLD-образ не загрузился; попытка исправить его вызвала BSOD в VM. Новый `driver-native-UNSIGNED/ElasticMemStore.sys` получен Microsoft link.exe и проверен в VM с локальной тестовой подписью. В поставке он unsigned: требуется собственная доверенная подпись и отдельная VM. MSBuild-сборка и HVCI по-прежнему не проверены.

## Обязательные проверки VM перед любой поставкой

- WDK Debug/Release x64, Code Analysis; signing/Code Integrity/HVCI logs. Использовать актуальные официальные правила проверки драйверов.
- Smoke: raw/XPRESS, все байты, overwrite, random negative control, isolation, quotas, malformed ABI/lengths, parallel client calls, reset/handle close.
- Не-elevated клиент получает access denied; сессия не открывается.
- Повторный smoke (например, 20 запусков); отдельно несколько процессов, abrupt client exit, CancelIoEx и гонка close/request с собственными fixtures.
- В VM включить Driver Verifier **только для ElasticMemStore.sys** по стандартной процедуре Microsoft: pool, IRQL, deadlock, I/O/security checks. Снимок и доступ к recovery обязательны; не проверять все системные драйверы.
- Проверить штатный stop/unload/reload при закрытых handles и отказ/безопасное поведение при активном клиенте; reboot, shutdown. Для open handles нормальный stop может быть недоступен; принудительный unload недопустим.
- RAM/commit считать для полного client+kernel пути, отдельно CPU и p50/p95/p99, при неизменённой системной политике. XPRESS/raw kernel A/B пока НЕ реализован как измерительный стенд; smoke не измеряет пользу.

## Portable проверки

`ZIG=/path/to/zig driver/tests/run.sh /path/to/output` — C store + test codec (не XPRESS) с UBSan.
Без ZIG требуется gcc и runtime библиотек AddressSanitizer/UBSan.
`dotnet run --project tests/ElasticMem.Transport.Tests -c Release` — owned-process IPC, raw/zstd, UTF8/BOM, deadlines, escaped-base64 frame regression (old cap12000, fixture key62=12138), malformed replies, log privacy.

Официальные API: https://learn.microsoft.com/en-us/windows-hardware/drivers/wdf/using-control-device-objects ; https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/nf-ntifs-rtlcompressbuffer

### Воспроизведение Linux cross-build
`python3 driver/scripts/prepare-cross.py /path/to/toolchains` загружает pinned Microsoft NuGet пакеты (~266МБ) и создаёт case aliases. Скачайте Zig0.14.1 с официального сайта и проверьте опубликованный checksum. Затем задайте `ZIG`, `WDK_ROOT`, `SDK_ROOT`, выполните `driver/scripts/cross-build.sh /path/to/output`. Скрипт теперь создаёт только `driver.obj` и `em_store.obj`, не SYS. На Windows выполните `Native-Relink.ps1 -ObjectDirectory <objects> -LinkerPath <official-link.exe> -WdkRoot <WDK-root> -OutputDirectory <output>`; используется KMDF1.33/WDK10.0.26100.0. Это исследовательский альтернативный путь, не замена WDK/MSBuild/Windows qualification.

### Клиент приложения 0.25.0

Опциональная кнопка «VM: копии приложений → kernel RAW/XPRESS» сравнивает одни скопированные байты Edge/Chrome/Telegram в двух режимах, проверяет full-byte GET и RESET counters, пишет безопасные `kernel-samples.csv`/сводку. Это добавляет kernel allocations, **не освобождает оригиналы приложения**. Новый путь клиента не был выполнен на Windows; ранее passed относится к unchanged SYS и synthetic 0.24 client. Полная установка/локальная VM подпись: `../windows-check/ПОЛНАЯ-ИНСТРУКЦИЯ.md`. Новый `Sign-ResearchDriver.ps1` сам ещё не runtime-tested; применяется только к exact verified unsigned image, не меняет boot policy.
