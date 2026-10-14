# ElasticMemStore — нативная VM-проверка 0.24.0

## Результат

**Passed research smoke, не production qualification.** Настоящий Windows Server 2025 Datacenter Evaluation x64 (build 26100.1742, Desktop Experience), локальная disposable QEMU/TCG VM, 2 vCPU / 2 GiB, без сетевого адаптера. Windows 11 не тестировалась. Дата часов гостя — 2026-10-14; это не независимое подтверждение календарной даты запуска.

Исходный C-код: commit `2ea62910a9a44fe2f8d25d1fca37f18601abbc1c`. Те же Zig/Clang COFF-объекты связаны **официальным Microsoft link.exe 14.44.35207 внутри Windows** с WDK 10.0.26100.0 / KMDF 1.33. Не выполнялись MSBuild и cl.exe. Изменения после этого commit касаются только сборки, упаковки, документации и сохранения доказательств, не логики C/C#.

### Выполнено

- Встроенная локальная тестовая подпись: Authenticode `Valid`, `Signature verified.`. Штатный test-signing включён и перезагрузка выполнена только в собственной VM. Приватный ключ не экспортирован. Trust Root использовался для ручной non-PnP установки; TrustedPublisher не требовался. Не отключались проверки подписи, integrity checks, Secure Boot/HVCI или защиты основного ПК. Совместимость с Secure Boot/HVCI этой проверкой не установлена.
- Служба kernel driver создана demand-start, загрузка успешна.
- **4042 assertions**, по **1000 итераций RAW и XPRESS**. Полные восстановленные байты сверены. Smoke включает изоляцию дескрипторов, quotas, overwrite, malformed IOCTL/ABI/длины, параллельные вызовы, RESET/закрытие сессий. Это конечный набор синтетических fixtures, не исчерпывающее доказательство корректности.
- После теста штатный unload; повторные start/info дали те же пустые исходные stats; затем штатные stop/delete. `driver-info.json` и `driver-reload-info.json` совпадают, pages=0. Никакого принудительного kernel unload.
- Отдельный native ACL probe: elevated Administrator открывает устройство; impersonated restricted token с отключённым Administrators SID и удалёнными privileges получает Win32 **5 / AccessDenied**. Probe завершён, служба остановлена и удалена с exit0. **Это не независимая обычная учётная запись и не отдельный UAC/non-elevated login test.**

### Что означают числа

| 64 синтетические страницы | RAW | XPRESS |
|---|---:|---:|
| Logical bytes | 262144 | 262144 |
| Payload bytes | 262144 | 704 |
| Session requested allocation bytes | 295680 | 34240 |
| Полное восстановление байтов | true | true |

После RESET значения logical/payload равны 0; session requested allocation возвращается к таблице 32768 байт. Это суммы запросов allocator, **не уникальная физическая RAM**; overhead KMDF/pool/IO/client не измерен. Для хорошо сжимаемых fixtures codec XPRESS действительно работал; это не zstd и не A/B пользы для рабочего стола.

## Неудачная LLD-сборка — не устанавливать

Старый Linux LLD SYS без `/dll` отказал при `sc start` с Win32 487. Добавление `/dll` привело в VM к реальному **MEMORY_MANAGEMENT BugCheck 0x1A, subtype 0x101B**, P4 `0xC0000005`. Изменение выравнивания CFG-таблиц не исправило проблему. Точная причина BSOD не доказана; нельзя сводить её к одной гипотезе о CFG. У образов LLD и Microsoft linker отличаются PE/section attributes; те же core COFF objects с Microsoft linker прошли runtime smoke.

Поэтому старые `kernel-cross/ElasticMemStore.sys` и `kernel-cross-UNSIGNED/ElasticMemStore.sys` **отозваны как исследовательские артефакты**, не применять после подписи. `cross-build.sh` теперь создаёт только objects; старый `package-024.py` остановлен fail-closed. Рекомендуемый штатный путь — VS/WDK `Build-Windows.ps1`, но он в этой лаборатории ещё не запускался. Альтернативный `Native-Relink.ps1` воспроизводит проверенную native команду, сам новый wrapper ещё не запускался в VM.

Microsoft link.exe выдал **LNK4078: multiple .data sections with different attributes (40401040)**. Warning не скрыт, warning-free/production claim отсутствует. Native image имеет DLL characteristic, CFG, NX, ASLR, integrity-check flags; это не доказывает HVCI compliance.

## Артефакты и provenance

- Поставляемый **unsigned** native SYS: SHA256 `5362244d0d4d0724312ee3e403303190448fbdaf108b3d2a4a3a4b2cd6cea8a5`, 19456 байт.
- Именно этот pre-sign образ использован для VM-тестового подписанного SYS: SHA256 `a143a233ddd5ec96128b7c1f2ad0bdfea618298fab5a0fee24a8d5ec4fc01acc`, 20928 байт. После экспорта удалён certificate table и восстановлен checksum; исходный unsigned SHA совпал с SHA, записанным до подписи. Тестовый подписанный SYS не включён в поставку.
- DriverClient SHA256 `d4465ff2c2d0d88a91e6879bca0a0cc79380fc6202c3485bab6aa33b34be3693`.
- `provenance.json`: hashes objects и официального VS package. Microsoft tool binaries/WDK libs не перераспространяются этим архивом.
- `kernel-smoke.json`, `transcript.txt` — неизменённые результаты smoke. Строка limitations про отдельный unload/non-admin run описывает scope **самого клиента**, не последующих самостоятельных тестов; фактические unload/reload/ACL описаны выше.
- `signature.json` относится к подписанному VM-образу, **не к unsigned файлу в поставке**. `native-pe.txt` снят в госте.
- `acl-probe.c` — исходник отдельной проверки доступа (например, `cl acl-probe.c /link advapi32.lib`; запустить elevated только с уже установленным trusted VM-драйвером). Этот пример команды cl не выполнялся; probe реально собран Zig Windows GNU.
- `acl-restricted.json` и `serial-markers.txt` — сохранённый вывод отдельного probe/UART. Private keys, пароли, полный RAM dump и диск VM не включены.

## Ограничения / следующий этап

Не проверены Windows 11, HVCI, Driver Verifier, HLK, stock MSBuild/cl.exe, Code Analysis, независимая обычная учётная запись, длительный stress, race close/cancel/request, unload при активных handles, shutdown/reboot с загруженным драйвером. Успешный штатный stop/reload не заменяет эти проверки.

Это volatile **явно интегрированное owned-page IOCTL-хранилище**. Нет прозрачного сжатия Chrome/Telegram, foreign-process access, Memory Manager replacement или измеренной экономии physical RAM. Не хранить единственную копию важных данных. Только disposable VM и собственный разрешённый процесс доверенной подписи; если политика не допускает загрузку — остановиться, не отключать защиты.
