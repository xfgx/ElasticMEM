# ElasticMem: воспроизводимый этап Г0 / Г3

Это **исследовательский стенд**, не новый механизм вытеснения Windows. Ничего не меняет
в настройках MMAgent, pagefile, античита или драйверов. Г1/Г2 отложены до данных Г0.
Потерянный `/data/expdef` не восстановлен: исторические числа D/E — **заявлено ранее,
сейчас не воспроизведено**. Числа нельзя подтверждать повторной реализацией «по памяти».

## Что есть

- `run.py`: чистый git-коммит, аннотированный тег `run/<id>`, копии входов, SHA256,
  сырые артефакты, manifest и отчёт с коммитом. Ошибка/unsupported тоже сохраняются.
- `verify.py`: проверка файлов и соответствия тега коммиту. Это проверка целостности,
  **не** доказательство корректности измерений и не криптографическая подпись.
- `g0.py`: future-aware оптимизация заданного gap-score, не строгий upper bound demand paging. Требует полную event-трассу
  с lifecycle или явно помеченную синтетику. Выборочные наблюдения отвергает.
- `page_idle.py`: Linux capability probe и выборочный сбор accessed-битов. НЕ полная трасса.
- `windows/vram_bench.cpp`: исходник Г3; CPU-запись в GPU_UPLOAD, GPU DMA в READBACK,
  fence + материализация RAM; сравнение с unbuffered file I/O. Не драйвер и не pagefile.
- `tests/`: детерминированные проверки парсера, арифметики, brute-force выбора интервалов,
  бюджета, хешей и обработки выходов Г3. Синтетика не считается экспериментом на приложениях.

## Протокол сохранности

Git внутри `/data` **не спасает от сброса всей среды**. После каждого прогона нужно выгрузить
архив на машину пользователя/в долговременное хранилище. Не считать эту папку единственной копией.

```bash
cd ElasticMem
python3 -m unittest discover -s research/ceiling/tests -v
# После любых правок, ДО прогона:
git add . && git commit -m "Описание изменения методики"
python3 research/ceiling/run.py --id example-001 g0 \
  --trace research/ceiling/examples/trace.csv \
  --metadata research/ceiling/examples/metadata.json \
  --eviction-us 10 --recovery-us 100 --max-fault-us 100 \
  --mean-budgets-us 0,0.1,1,10,100
python3 research/ceiling/verify.py research/ceiling/runs/example-001 --check-git
# Git история + теги; затем сохранить весь run рядом:
git bundle create elasticmem-history.bundle --all
```

`inputs/`, `runs/` и Windows build outputs исключены из git. Дампы нужны отдельно,
с размером, SHA256, происхождением, инвентарём регионов и временем сбора.
`inventory` хеширует сохранившиеся дампы, но НЕ превращает их в Windows-дампы или трассу.
Формат журнала прогона: `commit`, `git_tag`, статус, параметры, версия ОС/Python,
список входов и полный список хешей артефактов. Manifest нельзя подписать сам собой;
для защиты от злонамеренной замены нужна внешняя подпись/независимое хранилище.

## Г0: точная постановка и границы

Полную трассу нужно получить **на нужных нагрузках** из проверенного инструмента динамического
инструментирования/гипервизора, фиксирующего каждое обращение. EPT A/D-биты, page_idle и DAMON
показывают активность в окнах; сами по себе полной трассы не дают. EPT trapping каждого обращения
не равен A/D polling и вносит большой overhead. Сборщик полной Windows-трассы здесь **не реализован**.
Не подменять Chrome/Steam Windows Linux-объектами ради готовых цифр.

### Вход точной трассы

CSV, события отсортированы, адрес страницы выровнен на page_size:

```csv
timestamp_ns,pid,page,op
0,123,0x1000,map
100000,123,0x1000,read
200000,123,0x1000,write
300000,123,0x1000,unmap
```

`page` — виртуальный адрес начала страницы, `pid` — положительное целое. `map` — начало
**полностью резидентного и инициализированного** baseline-lifetime, не просто VirtualAlloc reserve.
Для уже живых страниц нужен начальный map. Повторное использование адреса требует unmap/map;
трасса должна различать lifetimes процесса/PID. Одна операция, пересекающая страницы, разворачивается
в событие для каждой затронутой страницы; строки CSV — page touches, не CPU instructions.
Моделируются все read/write, а не только cache misses. Разделяемая физическая память не дедуплицируется.
Metadata JSON: `schema=elasticmem.events.v1`, `trace_kind=exact|synthetic`, `source`, `scope`,
`limitations` (массив), `page_size`, `start_ns`, `end_ns`, `clock`, `access_definition`,
`process_identity`, `event_loss_count=0`. PID в CSV — уникальный ID экземпляра процесса
(при повторном использовании OS PID назначить другой ID и сохранить соответствие в provenance). Метка exact — утверждение сборщика,
а не факт, доказанный анализатором; полноту и perturbation нужно проверять отдельно.

### Что считает модель

Baseline — время жизни всех mapped страниц × размер. Метрика — **средние байты за окно**, не peak RAM
и не физический RSS всей машины. Для каждого gap сохраняется `max(0, gap - eviction - recovery)`;
по умолчанию **только между двумя обращениями** (`--boundary-mode interior`).
Для sensitivity `--boundary-mode all` добавляет gap от map и хвост до unmap/end; у хвоста
recovery не нужен. Конечные хвосты имеют
horizon bias: будущий доступ после end неизвестен. Повторить с разными окнами/warmup и отдельно
анализировать внутренние gaps, прежде чем принимать решения.

Средний бюджет X — сумма последовательных recovery-us / количество наблюдаемых page touches.
При одинаковой цене recovery выбираются самые длинные gaps; max-fault-us ограничивает разовый
модельный промах. Это **не** измеренное замедление приложения, не p99 задержки и не ограничение
instantaneous ёмкости. В строках PID распределён единый глобальный бюджет, а не независимые бюджеты.
Eviction и recovery обязаны иметь положительную стоимость. Цены по умолчанию условные; заменять их измерениями, отдельно учитывать декомпрессию, копирование,
метаданные и постановку на очередь. Числа, полученные с условными ценами, остаются сценарным расчётом.

`unbudgeted_gap_score_optimum_fraction` — все подходящие gaps без бюджетного ограничения.
Это оптимум **заданной функции gap−e−r**, а не upper bound реального demand paging.
Например, demand recovery после следующего touch может освобождать RAM дольше внутри gap,
чем эта функция приписывает; реальная временная ось при stalls также изменится.
Модель не учитывает конкуренцию каналов, очередь, RAM-резерв, сжатый backing store в RAM,
служебные структуры, dirty writeback, реальные зависимости после задержки, page sharing и nonpageable RAM.
**Это не потолок любого прозрачного механизма. Нельзя закрывать ветку по порогу 15–20% только по ней.**

### Linux выборочные наблюдения

```bash
python3 research/ceiling/run.py --id host-probe probe
# На отдельном Linux-стенде с page_idle и CAP_SYS_ADMIN, осознанно:
python3 research/ceiling/run.py --id idle-001 idle --pids 1234 5678 \
  --interval 0.1 --samples 100 --max-pages 8192
```

Сохраняются maps каждого окна, PFN/адрес, accessed, неизвестные/сменившие mapping строки.
Только rw private mappings, ограниченное число страниц, присутствующие PFN. Доля unknown —
обязательная часть интерпретации; ноль accessed не означает гарантию отсутствия будущих обращений.
Не запускать параллельно DAMON: accessed-биты общие, возможна интерференция с reclaim.
В текущем sandbox доступность надо проверять probe, а не обходить ограничение привилегий.

## Г3: Windows

Требуется Windows, Python 3.10+, git, VS C++ x64 tools, современный Windows SDK
(рекомендуется 10.0.26100), дискретная GPU и `GPUUploadHeapSupported` в OPTIONS16.
ReBAR сам по себе не гарантирует поддержку драйвером. Build manifest фиксирует commit, source/EXE SHA256, compiler/SDK и флаги; runner
отвергает binary другой ревизии или без manifest. Это заявленная provenance, не signed attestation.
Нет fallback на обычный UPLOAD/WARP/UMA:
такой fallback измерял бы другой путь.

```powershell
# x64 Developer PowerShell:
.\research\ceiling\windows\build.ps1
python research/ceiling/run.py --id g3-pc-001 g3 `
  --exe research/ceiling/windows/vram_bench.exe `
  --scratch D:\elasticmem-new-scratch.tmp --iterations 200 --pool-mib 32 --adapter-index 0
python research/ceiling/verify.py research/ceiling/runs/g3-pc-001 --check-git
```

Scratch — **новое имя файла на нужном физическом SSD**. CREATE_NEW исключает перезапись;
DELETE_ON_CLOSE удаляет тестовый файл; NVMe тип диска нужно зафиксировать отдельно.
В тесте 4 KiB/64 KiB/1 MiB/16 MiB, 10 warmup + N измерений; byte-for-byte проверка восстановления
после каждой копии; содержимое отличается по offset и номеру итерации.
`windows/test-negative.ps1 -ScratchDirectory D:\` проверяет пропуск DMA и неправильный offset
для 4 KiB/64 KiB/1 MiB/16 MiB — каждый обязан вызвать именно byte mismatch. Эти тесты здесь
не запускались, требуют Windows. GPU_UPLOAD никогда не читается CPU; запись завершается sfence.
GPU restore включает submit, fence wait, Map и копирование в обычную RAM; запись команд
и выделение ресурсов вне замера. Disk restore — unbuffered ReadFile в aligned RAM плюс
такое же копирование в materialized RAM. Полный page fault с scheduler/декомпрессией тут не измеряется.
Build log/info и hardware inventory включаются в sealed run manifest. Scratch path
связывается с локальным drive/partition/disk; провал inventory даёт incomplete-provenance
с ненулевым кодом возврата, не принятый аппаратный результат. BusType сохраняется;
даже NVMe file I/O не равен цене pagefile fault.
`summary.json` содержит p50/p95/p99, raw `samples.csv` — все итерации включая warmup.
Грубая GB/s из median не заменяет tail latency одиночной страницы.

Пул по умолчанию 32 MiB, максимум 256 MiB и не больше четверти текущего свободного DXGI бюджета.
Это осторожный микротест, **не** доказательство доступности 8–24 GB. Нагрузка игр,
очереди DMA, состояние питания/драйвера, thermal и interference пока не контролируются;
фиксированный порядок режимов тоже ограничение. Нужны повторения, случайный порядок,
loaded GPU, многогигабайтная capacity проверка и реальная цена page fault перед интеграцией.
Резервирование VRAM, миграция при запуске игры и интеграция с pagefile **не реализованы**.

Источники API/ограничений:
- https://microsoft.github.io/DirectX-Specs/d3d/D3D12GPUUploadHeaps.html
- https://gpuopen.com/learn/using-d3d12-heap-type-gpu-upload/
- https://docs.kernel.org/mm/damon/design.html

## Порядок следующих работ

1. Внешний архив текущего кода/тегов/сырых результатов, проверка восстановления из него.
2. Полноценный сбор на Windows нагрузках + calibrated recovery distributions и оценка overhead.
3. Г0: process/page-time, sensitivity по окнам, а затем capacity/queue simulation с реальными RAM резервами.
4. Г3: компиляция, проверка D3D12 debug layer отдельно от timing, реальный idle/loaded GPU микротест.
5. Только по результатам: Г1 нормализованный дедуп против raw и штатного combining (точность восстановления,
   ложные указатели и карту регионов считать полностью); Г2 на контролируемых кучах с ABI/GC-инвариантами.

Аппаратный MXT/CXL тракт на данном игровом ПК — граница, не программный план.

## Автоматическая выгрузка

```bash
python3 research/ceiling/package.py --runs research/ceiling/runs/example-001 \
  --output ../ElasticMem-evidence.zip
# По желанию сохранить существующие старые дампы (не подтверждает D/E):
# --preserved-files ../mem_python.bin ../mem_sqlite.bin ../mem_control.bin
```

Архив содержит source.zip, history.bundle, sealed runs, PACKAGE.json с hashes и RESTORE.md;
отдельный `.sha256` удостоверяет весь архив. Скачивание/внешнее сохранение обязательно.
Перед выдачей архива проверяется восстановление Git-тегов и повтор анализа retained synthetic input.
