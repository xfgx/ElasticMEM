# ElasticMem 0.27 — backend для Windows: установка и проверка (preview)

## Сначала прочитайте границы готовности

**Это не готовое выполнение требования «всё сжатие через наш SYS».** В выпуске есть Windows-панель оркестрации Hyper-V/RemoteApp и исходный модуль SYS/WHP memory-provider для собственного VMM. Но provider ещё **не подключён к VMM, который загружает Windows**; отсутствуют реализованные CPU/I/O barrier, обработчик memory-access exits, выбор холодных страниц и полная VM-интеграция. Новый SYS не пересобирался/не испытывался на Windows. Нет подтверждённого сжатия Edge/Chrome/Telegram через него.

Рабочий management-путь панели использует **Memory Compression гостевой Windows + Dynamic Memory Hyper-V**, не наш SYS. Это открыто обозначено в интерфейсе и логах. Windows решает, какие страницы сжимать; нет гарантии уменьшения памяти конкретного PID. Локальная VM сама расходует память хоста и может ухудшить итог.

Программа собрана под Windows x64; новые backend-функции на настоящем Hyper-V/RDS здесь не запускались. Проверены portable контракты/lifecycle модели и PowerShell-синтаксис. Смотрите QA.json. Не устанавливайте старые LLD-SYS.

## 1. Что требуется на хосте

- Обычный рабочий стол Windows сохраняется. Host — Windows Pro/Enterprise/Server с установленным Hyper-V и разрешённой аппаратной виртуализацией. Если host сам VM, нужен nested virtualization от её владельца; программа её не обходит.
- Администратор для локального Hyper-V / PowerShell Direct. Запуск elevated — вручную, без скрытого UAC/auto-elevation.
- Достаточный запас RAM/диска. Min/startup/max по умолчанию 2/4/8 GiB — это не универсальный рецепт экономии. Не выделяйте больше возможностей host.
- Windows PowerShell5.1 и согласованный execution/signing policy. Программа НЕ устанавливает Bypass/Unrestricted. Если `.ps1` блокируются, согласуйте подпись/исполнение с администратором; не отключайте защиту.
- Secure Boot, HVCI, Defender, signature enforcement и host pagefile не отключаются ради backend.

## 2. Подготовка гостевой Windows

Нужна **уже созданная и зарегистрированная Hyper-V VM Windows Server** с checkpoint и PowerShell Direct. Автоматическое создание Windows/domain/RDS-инфраструктуры в этом preview не реализовано.

Для бесшовных окон требуется **поддерживаемый RDS RemoteApp deployment**: Connection Broker, один Session Host в collection, нужные domain prerequisites, настроенные Windows/RDS лицензии, разрешённый RDP-пользователь и доверенный сертификат сервера под выбранный hostname. Настройте через Server Manager → Remote Desktop Services по политике вашей среды. Программа не подменяет лицензии, не сбрасывает grace period и не делает registry-patch для неподдерживаемого RemoteApp-hosting Windows11.

RDS deployment здесь single-server/single-session-host. Multi-host farm/redirection на другую VM не поддерживается: иначе показанные метрики не относились бы к выполняющим приложения backend.

Установите официальные подписанные x64 приложения **в госте system-wide**:
- Edge: Program Files (x86) или Program Files → Microsoft\Edge\Application\msedge.exe.
- Chrome: Program Files или Program Files (x86) → Google\Chrome\Application\chrome.exe.
- Telegram: Program Files или Program Files (x86) → Telegram Desktop\Telegram.exe.

Персональная установка Telegram в LocalAppData другого пользователя автоматически не публикуется. Неподходящий путь/подпись даст partial/rejected, не «готово».

Хостовые уже запущенные процессы не перемещаются в VM. Открываются новые гостевые процессы и профили. Войдите в приложения внутри гостя обычным способом; программа не копирует пароли, токены и профили с хоста.

## 3. Установка программы

1. Закройте старые ElasticMem и агенты. Скачайте новый ZIP и SHA256.txt, проверьте hash через `Get-FileHash ... -Algorithm SHA256`.
2. Распакуйте **целиком в новую папку**, не смешивайте версии. .NET8 runtime включён; Python/Visual Studio для пользователя не нужны.
3. В elevated Windows PowerShell, если разрешено политикой:
   ```powershell
   .\backend\windows\Preflight.ps1
   ```
   Это read-only проверка. Missing module/admin — отказ, не успешная настройка.
4. Запустите `ElasticMem.exe` от администратора. Откройте **Backend VM**. Публичный updater этим исследовательским выпуском не обновлён.

## 4. Настройка из панели

1. **VM** — точное имя зарегистрированной Hyper-V VM, без wildcard. **RemoteApp host** — FQDN/IP этого гостя, доступный с host. **Collection** — существующая RDS collection.
2. Укажите min/startup/max MiB и buffer%. Нажмите **1 · Сохранить / привязать VM**. Проверяется и сохраняется GUID; при подмене/пересоздании VM операции отвергаются.
3. **2 · Учётная запись гостя…** — введите администратора ВНУТРИ гостевой Windows в нативном Get-Credential prompt. Пароль не нужно отправлять в чат. Windows DPAPI хранит credential для текущего host-пользователя/ПК в private ACL каталоге. Это НЕ RDP-пароль; RDP-вход отдельный.
4. При выключенной VM: **3 · Dynamic Memory**. Затем **Запустить VM**. Кнопка не меняет startup/min параметры работающей VM и не force-restart её.
5. **4 · Включить сжатие Windows в госте** — явное согласие на `Enable-MMAgent -MemoryCompression` только внутри этой VM. Если API/политика отвергли настройку, операция останется failed, не будет заменена trim.
6. **5 · Опубликовать приложения RemoteApp** — проверяет доступную collection, единственный guest Session Host, подписи/издателей и system-wide пути. Публикует `ElasticMem-Edge`, `ElasticMem-Chrome`, `ElasticMem-Telegram`. Чужой конфликтующий alias не перезаписывается.
7. **Обновить процессы / память**. Нужно видеть работающую VM и `rdp_target_matches_vm=true`. DNS/IP сверяется с IPv4 гостя. Если связь не доказана, launch блокируется.
8. **Открыть edge/chrome/telegram на хосте** создаёт RemoteApp RDP-файл без пароля и запускает штатный mstsc. Нужен доверенный сертификат/NLA и обычный RDP-вход. Не понижайте authentication и не принимайте неожиданную машину.

На host появляются окна RemoteApp, но не новый основной рабочий стол. Рабочий стол гостя отдельно не подменяет host desktop. Clipboard/drive/printer/smartcard redirection по умолчанию выключены.

Запуск mstsc — только запуск клиента. Факт старта приложения подтверждается **новым гостевым PID/session в списке**, а не только появлением окна клиента. Если RDP deployment/cert/license/пользователь не настроены, эта часть не заработает сама.

## 5. Управление процессами и фоновый режим

Список показывает только allowlisted гостевые Edge/Chrome/Telegram: PID, session, start-time, WS и private commit. Он не включает host PIDs.

Выбрать строку → **Завершить выбранный ГОСТЕВОЙ PID** → подтвердить возможную потерю несохранённых данных. Проверяются VM identity, allowlist, interactive session, время старта и стабильный process handle. Нет массового kill дерева; Session0/переиспользованный PID отвергается.

`ElasticMem.exe --backend-agent` открывает Backend VM и сворачивается в существующий tray. Это **агент интерактивного пользователя**, а не SYSTEM-service: RemoteApp требует вошедшего пользователя. Опрос примерно каждые15с; не выполняется во время других тестов/trim-агента. Открытие из tray возвращает панель. Выход из агента **не завершает гостевые приложения и VM**.

Флажок Auto-start запускает только уже привязанную VM при старте backend-агента. Он не создаёт ОС, не настраивает RDS/domain и не переносит процессы автоматически.

## 6. Автозапуск при входе пользователя

Сначала проверьте панель вручную. Затем, если это разрешено:
```powershell
.\backend\windows\Install-Agent.ps1 -ProgramDirectory 'C:\path\ElasticMem' -ConfirmInstall
```

Installer копирует EXE в защищённый ProgramFiles, регистрирует user-logon task с Highest по вашему явному согласию; пароль в task не сохраняется. Существующая папка/task не перезаписываются. Запустите защищённый EXE `--backend-agent` или выйдите/войдите в Windows.

Используйте **тот же Windows-аккаунт**, для которого сохранён DPAPI credential. Если запускать installer под другой admin-учётной записью, задача/credential относятся к ней, не к обычному пользователю. Нет автоматического переноса credential между аккаунтами.

Удаление:
```powershell
.\backend\windows\Uninstall-Agent.ps1
```
Закройте ElasticMem; ProgramFiles и private credentials удаляйте отдельно после проверки. Скрипт не удаляет VM/RDS aliases и не откатывает memory settings; для полного лабораторного отката восстановите checkpoint.

## 7. Как проверять память

Панель показывает VM MemoryAssigned/MemoryDemand, host available physical memory, guest compression enabled, гостевые process WS/private commit. `compressed_store_process_ws_bytes` — только WS видимого системного процесса, не compressed payload; может быть null.

1. Снимите **Точку сравнения** при running VM.
2. Выполните повторяемую нагрузку с теми же приложениями/профилями/вкладками.
3. Подождите решений Windows и Hyper-V; обновляйте.
4. **ZIP логов без credentials**; повторите несколько прогонов.

Уменьшение MemoryAssigned — наблюдаемое уменьшение assignment VM. Оно НЕ доказывает причинную экономию от compression, влияние на скорость или уникальную физическую RAM каждого PID. При смене GUID/guest boot/state baseline сбрасывается. `physical_ram_saved_bytes` и causal effect остаются null. Выключение VM нельзя выдавать за успешное сжатие.

Фоновый опрос, mstsc, гостевая Windows и ElasticMem тоже имеют overhead. Нельзя суммировать process WS или игнорировать это при сравнении.

## 8. Логи и ошибки

ZIP исключает guest credential CLI XML, пароли, RDP-файлы, сырую память, command lines и чаты. Содержит VM GUID, guest name/OS/IP, PID/session/start ticks и метрики: просмотрите metadata перед отправкой. Исходный журнал — `%LOCALAPPDATA%\ElasticMem\backend-private\events.jsonl`, отчёты — `backend-reports`.

`timeout_outcome_unknown`: worker прерван, действие могло уже начаться в госте. Сначала обновите статус; не делайте force power-off/повторную мутацию вслепую. При `failed` передайте ZIP, не credential.clixml и не memory dump.

Нативное исполнение новой версии на Windows/Hyper-V/RDS/WHP ещё не проверено. `backend/tests/parse.ps1` проверяет только синтаксис. Полноценного SYS/VMM paging-пути в этом preview **нет**; его интеграция остаётся отдельным этапом, а не скрытым переключателем этой панели.
