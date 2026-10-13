# ElasticMem 0.14.0 — проверка на Windows

Это готовый **win-x64 self-contained** исследовательский билд. Python, VS и установка .NET не нужны.
D3D12 GPU_UPLOAD требует поддержки ОС/драйвера/дискретной GPU; ReBAR сам по себе не гарантия.

## Быстрый запуск

1. Распакуйте ZIP в отдельную папку. Старый ElasticMem сначала закройте; агент не запускайте.
2. Запустите `ElasticMem.exe`. Если SmartScreen предупреждает о неподписанном EXE — это ожидаемо:
   цифровой подписи нет; решение о запуске за вами. Проверяйте SHA256 из BUILD.json/sidecar.
3. Откройте «Лаборатория»: окружение, самопроверка, Г0, Г1, Г2, Г3 доступны отдельно.
4. `CHECK-WINDOWS.cmd` запускает безопасную базовую серию и создаёт ZIP логов в `%TEMP%`.
5. Для VRAM запустите `CHECK-VRAM.cmd`, выберите существующую папку на SSD, закройте игры.
6. «Проверка интерфейса» снимет все вкладки в обеих темах и сохранит layout-audit. Повторите
   при нужном DPI (100/125/150/200%). PNG требует ручного просмотра; audit не доказывает красоту.
7. «Экспорт логов ZIP»: **Нет** в вопросе о чувствительных файлах — отчёты/сырые счётчики без
   дампов, трасс и screenshots. **Да** — полный пакет, может содержать пароли и переписку из
   чужой памяти, имена процессов, пути. Просмотрите архив до отправки. Ничего не загружается автоматически.

Логи: `%LOCALAPPDATA%\ElasticMem\research-runs`. Ошибки/unsupported/cancelled сохраняются, не считаются успехом.
Старые проверки «Игры и приложения» сохраняют TXT в «Папка отчётов»; журнал агента —
`%LOCALAPPDATA%\ElasticMem\agent.log`. Их можно прислать отдельными файлами.
Экспорт лаборатории создаёт ZIP и соседний `.sha256`; EXPORT.json хеширует именно экспортированные байты.
Логи с редактированием/без входов — не самодостаточный пакет воспроизведения, это явно отмечено.
Даже logs-only пакет может содержать имена процессов, сообщения ошибок и пути вне USERPROFILE.
Это не обещание анонимности: просмотрите архив перед отправкой. Хеши исходных прогонов проверяются
до экспорта, EXPORT hashes относятся к экспортированным (возможно отредактированным) байтам отдельно.

## Что означают проверки

- **Г0 наблюдение:** WS/private commit/page-fault счётчики, не полная трасса. PageFaultCount включает
  soft faults; нельзя назвать его SSD-трафиком. Никаких trimming в этом режиме.
- **Г0 модель:** импорт `timestamp_ns,pid,page,op` CSV и metadata по контракту `research/ceiling`.
  Fixture приложена в `examples`. `map` — начало резидентного baseline lifetime; все read/write нужны.
  Только внутренние gaps; сценарный optimum `gap−e−r`, не universal/demand-paging bound. Полный сборщик
  чужих Windows-обращений **не реализован**; A/D polling не может его заменить.
- **Г1:** x64 words в private 4-KiB страницах, candidate pointers по известным readable committed ranges.
  Raw/normalized дедуп и Brotli сравниваются при полном учёте ссылок, bitmaps, maps, header. Создаётся
  реальный `cold-store.emd`, декодируется и побайтово сверяется со снимками. Это офлайн холодный формат,
  не live combining. Снимки ограниченные/неатомарные, чтение может изменить WS. Не выбирайте процессы игр
  с античитом. Нет эскалации привилегий; denied/PPL ошибки сохраняются. WOW64/ARM64 цели отвергаются: только native x64; схема 8-byte word candidate не определяет типы объектов.
- **Г2:** настоящее компактное хранилище собственных schema-controlled записей, handles/type IDs,
  escapes для больших refcounts; случайные операции проверены. **Не** патч CPython/V8 и не внешний heap.
- **Г3:** реальные OWN-буферы GPU_UPLOAD → DMA READBACK → RAM. Payload уникален по offset/generation;
  отрицательные проверки должны обнаружить пропущенную/неверную копию. 4 KiB/64 KiB/1 MiB/16 MiB,
  warmup 10 + N samples, p50/p95/p99, raw CSV. Запись command list включена в handler_us и исключена из
  submit-only total_us. После GPU timeout незавершённые ресурсы удерживаются до выхода процесса; следующий GPU тест
  заблокирован до перезапуска. Закрытие приложения ждёт завершения/отмены прогонов.
  Не перенос pagefile и не transparent paging; миграция при запуске игры отсутствует.
  Disk comparator — unbuffered file I/O, не полный NVMe-pagefile fault. Кеш SSD/очереди/питание/нагрузка GPU
  не контролируются; не сравнивайте отдельные медианы как end-to-end roundtrip.

## CLI без диалогов

```bat
ElasticMem.exe --research selftest
ElasticMem.exe --research environment
ElasticMem.exe --research g0-sample --pids 1234,5678 --seconds 60
ElasticMem.exe --research g0-model --trace examples\trace.csv --metadata examples\metadata.json
ElasticMem.exe --research g1 --pids 1234,5678 --limit 16 --allow-memory-read
ElasticMem.exe --research g2 --records 100000
ElasticMem.exe --research gpu-probe
ElasticMem.exe --research g3 --scratch C:\Temp --pool 32 --iterations 100 --negative
ElasticMem.exe --research export --out C:\Temp\elasticmem-logs.zip
```

Версия/коммит/EXE hash сохраняются каждым прогоном. BUILD.json — provenance сборки, не цифровая подпись.
Сборка и Core tests проведены на Linux; фактический запуск WinForms, P/Invoke и D3D12 **ещё не проверен на Windows**.

Старая сетевая отправка/встроенный credential удалены. Если ранее встроенный credential ещё действителен,
его владелец должен отозвать/заменить его на стороне сервиса; удаление из новой сборки не отменяет старый ключ.
