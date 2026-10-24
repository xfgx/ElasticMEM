**Исследовательский preview. Это НЕ готовый Windows VMM.** Windows-гость не загружается, Edge/Chrome/Telegram в своём VMM не запускаются, `physical_ram_saved_bytes = null`.

### Что внутри
- `ElasticMem.exe`: приложение. Backend UI запускается через `ОТКРЫТЬ-BACKEND.cmd`.
- `vmm-runner\ElasticMem.VmmRunner.exe`: проверки WHP. Сначала выполните `--probe`.
- `backend\VMM-032.md`: порядок проверки (команды 01–06) и протокол Gate 0.
- `backend\probe-ci.json`, `backend\user-store-selftest-ci.json`: результаты, снятые на GitHub Windows runner при сборке. Там нет nested virtualization, поэтому WHP недоступен.

### Как проверить
1. Используйте отдельную (disposable) Windows x64, лучше Windows 11.
2. Распакуйте архив.
3. Выполните `vmm-runner\ElasticMem.VmmRunner.exe --probe > 01-probe.json`.
4. Дальше следуйте `backend\VMM-032.md`.

Программа ничего не устанавливает, не включает и не меняет: Secure Boot, HVCI, Defender и BCD остаются как есть. Драйвера `.sys` в пакете нет.

Проверьте SHA256 архива по файлу `.sha256`.
