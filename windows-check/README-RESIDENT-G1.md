> Актуальная установка и тест Edge/Chrome/Telegram для 0.25.0: `ПОЛНАЯ-ИНСТРУКЦИЯ.md`. Драйвер необязателен; kernel sample только в VM. Ниже — справка существующих исследовательских режимов, не подтверждение нового Windows runtime.

# ElasticMem 0.14 — resident G1 verification

Это исследовательская версия. Windows API, GPU и GUI здесь (Linux) не исполнялись.

## Что нажимать
1. Агент остановлен. Лаборатория → Г1 · резидентные private-страницы → Выбрать процессы.
2. Только 2–4 renderer-процесса Chrome ИЛИ 2–4 steamwebhelper одного семейства; не игры с античитом.
3. 16 МиБ на процесс → Запустить проверку → согласие на чтение памяти.
4. Проверить хеши → Экспорт логов ZIP → НЕТ на дампы/трассы/screenshots.
5. Прислать один ZIP. Отдельно Проверка интерфейса при 125% и ручной скриншот, если подписи снова режутся.

## Новые результаты
- process_name, QueryWorkingSetEx Valid/Shared/ShareCount до чтения и после;
- отдельные счётчики MEM_PRIVATE/MEM_MAPPED/MEM_IMAGE;
- resident private not-shareable input, captured_zero_pages и exclusion counters;
- residency-summary-<PID>.json включается в logs-only export; адреса/page-status.csv остаются в sensitive snapshots/;
- brotli_only_bytes и actual_brotli_only_bytes — одинаковый постраничный Brotli Fastest, но БЕЗ дедупликации;
- actual_raw_dedup_brotli_bytes — обычная дедупликация + Brotli;
- три сериализованных формата проверяются восстановлением каждого байта;
- raw_page_analysis разделяет дубли внутри процессов и дополнительный межпроцессный эффект.

ElasticMem сам себя для Г1 не снимает: буферы наблюдателя загрязняют входные страницы.

Valid=0 — не доказательство отсутствия байтов в RAM: состояние demand-zero/standby/compressed/swapped этим API не разделено. Такие страницы не читаются намеренно и их содержимое неизвестно.
Shared означает shareable; ShareCount (максимум 7) характеризует sharing при Valid=1. Не путать эти поля.
Проверки до/после чтения сужают, но НЕ устраняют гонку. Нет заявления об отсутствии добавленных page faults. Счётчики процесса включают фоновую нагрузку.
Выборка — ограниченный адресный префикс, не полный census, не случайная выборка. Основной вход консервативен и не включает private COW-pages, оставшиеся MEM_IMAGE/MEM_MAPPED.

Порядковая нормализация остаётся исключительно помеченным историческим comparator; семантическая нормализация не реализована и не объявлена проверенной.
Нет live RAM reclaim, kernel pagefile device, отключения MemoryCompression/PageCombining, принудительного combine, использования BAR, миграции VRAM или точной Windows access trace.

## Воспроизводимость
SOURCE-RECOVERY.json честно фиксирует восстановление из ранее выданного исходного ZIP (historical .git отсутствовал).
Новый git начинается с проверенного архива. В source package есть git.bundle, исходники, release tag; BUILD.json связывает бинарник с новым точным коммитом.
SHA256 и manifest подтверждают целостность, а не научную валидность результатов.
