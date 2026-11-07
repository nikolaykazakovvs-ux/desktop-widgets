# AI Telemetry Bridge: производитель системных ресурсов

Реализация этапа 2: `windows/resources-widget/src/TelemetryPublisher.cs`, общий файловый writer — `telemetry/publisher-shared/TelemetrySnapshotWriter.cs`. Сборка сохраняет C# 5 / .NET Framework 4.8 и существующую JSON-обёртку, без NuGet и нового сборщика ОС. Этот документ описывает реализацию и воспроизводимые проверки; принятие, установка и публикация не следуют из наличия исходников или успешной компиляции.

На 21.10.2026 этап 2 принят, установлен и проверен в живой работе: [готовность производителей и receipt](../PRODUCER-READINESS.md). p95 всей фоновой операции 6,1417/5,7273 ms на двух контрольных прогонах окончательного исходника; порог 10 ms не менялся.

## Поток данных и время

`Sampler.Loop` передаёт уже собранный `Sample` после `Collect(true)` и перед `Updated`. Передача копирует только разрешённые примитивы в отдельную проекцию: sensor/UI объекты, их имена, процессы, диски, пути, адреса и строки источников в неё не попадают. Дальнейшая мутация исходного `Sample` не меняет снимок. JSON и дисковый ввод-вывод выполняются фоновым writer. Есть один исполняемый снимок и один ожидающий; новый ожидающий заменяет прежний. Очередь не растёт.

Публикация ограничена 1 Гц независимо от 500-миллисекундного опроса. При большем `intervalMs` writer ждёт настоящего нового измерения; отдельного heartbeat ресурсов нет. Скрытая карточка продолжает обычный `Sampler.Loop`; диски и процессы остаются со своим прежним режимом, в telemetry они не экспортируются. Конструктор Sampler с `journalReadOnly=true` не создаёт publisher: штатные `--dump`, `--render` и `--render-journal` не публикуют live snapshot.

| Поля контракта | Исходные данные и ограничения |
| --- | --- |
| CPU usage | Существующий успешный дифференциальный `NtQuerySystemInformation`; неуспех/непрогретая разность — unknown |
| RAM total/available | Уже полученные `GetPerformanceInfo` bytes и время этого чтения |
| Commit total/limit/headroom | Те же операнды и один timestamp; headroom строго limit − total, без оценки размера pagefile |
| GPU load/memory | Текущее успешное NVML/WDDM чтение; неполный запрос WDDM memory — unknown; shared отдельно от dedicated |
| VRAM | Только dedicated memory; shared не становится самостоятельным физическим запасом VRAM |
| CPU/GPU temperature | Время исходного helper snapshot или существующего LHM/WMI cache; собственный NVML/WDDM запрос имеет время реального чтения |
| Температура с неизвестным временем | Значение и measured_at в telemetry — null, UI сохраняет существующее отображение. В частности, чтение HWiNFO/AIDA/Core Temp без раскрытого исходного времени не получает свежую дату автоматически |
| Swap | Unknown: существующий collector не измеряет фактическое использование pagefile; commit − RAM не используется как подмена |
| Трафик | Уже вычисленные скорости NetSensor и время чтения; дополнительных запросов адаптеров нет |
| RTT | Значение существующего NetJournal.Health с исходными lastPingAt/lastGwAt и настроенным probe interval; дополнительного ping нет |
| Reachability | Успешная gateway probe означает только gateway reachable; `scope: gateway` допустим лишь при `status: ok`, а при отсутствующем/stale наблюдении — `unknown + not_measured`, `provider_id: null`. Достоверное старое значение с исходным timestamp сохраняется; доступность провайдеров из probe не выводится |
| Network quality | Исходные дневные счётчики NetJournal, coverage widget_observed_only и независимое преобразование начала/конца местного дня в UTC |

`measured_at` источника — максимум сохранённых исходных timestamps, `last_attempt_at` — завершение существующего Collect, `published_at` задаётся фоновым writer. UTC записывается с шестью знаками дробной секунды. Известное значение без доказанного исходного времени или с временем после завершения Collect экспортируется unknown. Из-за отсутствующего swap нормальный аппаратный снимок обычно имеет source status partial; доступные метрики сохраняют собственный status ok.

Исправлена необходимая для контракта граница NetJournal: queued ping предыдущего местного дня больше не увеличивает счётчики нового дня. Его RTT сохраняет исходное время и может ещё быть свежим сразу после полуночи. Пропускается конкретное старое наблюдение для дневных counters/episodes, весь новый день остаётся доступным. Наблюдение не переотносится задним числом в уже сохранённый старый дневной журнал. Если округлённая/пересекающая полночь legacy coverage превышает прошедшую часть дня, только coverage становится null и quality — partial, без выдуманного исправленного значения. Нулевое число проб не означает нулевой процент потерь: loss в таком случае null. Границы перехода летнего времени преобразуются отдельно, поэтому сутки могут иметь 23/25 часов.

## Общий файловый writer

API `DesktopWidgets.Telemetry.TelemetrySnapshotWriter` принимает `TelemetryWriterOptions` и `Submit(Func<DateTime,string>)`. Callback должен замыкать исключительно собственную неизменяемую проекцию. Он вызывается worker с publication UTC и не запускает collector/provider. `LastError`, `PublishedCount` и `TargetPath` предназначены для ограниченной диагностики; исходные exception messages, пути и секреты в ошибки не включаются.

Имя файла закрыто allowlist: `hardware-snapshot.json` либо `quotas-snapshot.json`. Каталог выбирается один раз: явный параметр → `DESKTOP_WIDGETS_TELEMETRY_DIR` → `%LOCALAPPDATA%\DesktopWidgets\telemetry`. Нужен абсолютный путь на локальном fixed drive. UNC/device paths и reparse points по цепочке каталогов/target отвергаются. Fixture mode требует отдельного явного пути и запрещён в стандартном/настроенном live каталоге и его подкаталогах. Сам нестандартный путь не меняет mode. Тесты создают новые каталоги с GUID на D:.

Для fixture/live guard selected и стандартный/настроенный live roots канонизируются по final path существующего объекта через `GetFinalPathNameByHandleW`. Directory handle открывается с нулевым desired access и share `Read | Write | Delete`. Для несуществующего пути resolver канонизирует ближайшего существующего предка и добавляет несуществующий хвост; при ошибке resolution writer отказывается без строкового fallback. Guard выполняется до создания каталога и повторяется после него. Исходный путь сохраняется для reparse/ACL/lock/replace операций, поэтому canonicalization не разрешает junction или symlink.

Новый каталог получает защищённую ACL без наследуемых широких правил: текущий SID, SYSTEM и Administrators. Существующие каталог, target и lock проверяются: чужой owner или allow ACE для других principals приводят к отказу, права автоматически не расширяются. UAC, сеть, установщик и защищённый sensor helper не изменяются. Обычный пользователь создаёт приватную папку в собственном LocalAppData; reparse/ACL проверяются также перед публикацией. Это защита от непреднамеренно общего пути, а не изоляция от администратора или другого процесса с тем же SID, способного менять собственные ACL.

Worker неблокирующе захватывает global mutex с закрытой ACL и SHA-256 от SID/нормализованного target path. Дополнительно держит фиксированный per-target lock file через исключительный доступ (share 0, ранее `FileShare.None`; сейчас lock создаётся через `CreateOwned`, см. ниже): он исключает два writer даже при DOS short-name alias или разных Windows logon sessions. Hardware и quotas используют разные lock names. Mutex/handle освобождаются на owning worker thread; Dispose вызывается при завершении Sampler. Второй writer получает `writer_already_running`, работа UI/collector продолжается.

Временный файл получает случайное уникальное имя в том же каталоге и создаётся через CreateFileW(CREATE_NEW, OPEN_REPARSE_POINT) с share 0; в текущем writer (после PR #1) вызов идёт через `CreateOwned` с дескриптором, задающим только владельца — SID текущего пользователя, без DACL (файл наследует проверенные ACE приватного каталога). После записи/Flush и проверки ACL по handle он остаётся открытым до SetFileInformationByHandle(FileRenameInfoEx): replace-existing/POSIX, полный проверенный TargetPath, RootDirectory=NULL. Если расширенный класс не поддержан, выполняется классический FileRenameInfo. Target предварительно не удаляется. DACL существующей цели переносится на temp; любое несовпадение применённого DACL с требуемым вызывает отказ до rename. UTF-8 snapshot ограничен 1 MiB. Повторяются только Windows sharing/lock codes 32/33: четыре попытки всего, задержки 1/2/4 ms и monotonic deadline 25 ms перед новой попыткой. Это ограничение расписания повторов, не гарантия времени системного вызова.

При обычном sharing failure сохраняется старый target. Очистка temp выполняется через собственный DELETE-handle, без повторного path-based ACL lookup. После попытки rename сначала проверяется, что handle по-прежнему носит созданное GUID-имя temp; если имя изменилось или неизвестно, ничего не удаляется, worker останавливается с `replace_state_requires_inspection`. Отказ самого удаления останавливает worker с `temp_cleanup_failed`, ограничивая рост одним orphan temp. Sharing failure первой публикации ретраится или остаётся обычным восстанавливаемым отказом, а не автоматически неоднозначным исходом. Target, чужие файлы и дерево каталогов не удаляются. Fixed lock files ограничены двумя именами и не являются data snapshot.

При стандартной гранулярности системного таймера Windows (без запроса 1 мс процессом) `Sleep(1)` может занимать около 15-21 ms, поэтому в 25 ms бюджет обычно укладываются 1-2 повторные попытки, а не все три задержки 1/2/4 ms. Fault-тест `WriterFaultTests` на время проверки запрашивает разрешение таймера 1 мс (`timeBeginPeriod`), чтобы проверять логику повторов, а не системный таймер; production writer не менялся.

Официальные основания: [SetFileInformationByHandle](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfileinformationbyhandle), [FILE_RENAME_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_rename_info), [FileShare и независимость от ACL](https://learn.microsoft.com/dotnet/api/system.io.fileshare), [DirectorySecurity](https://learn.microsoft.com/en-us/dotnet/api/system.security.accesscontrol.directorysecurity?view=netframework-4.8.1), [CreateFileW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew), [GetFinalPathNameByHandleW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfinalpathnamebyhandlew).

## Ограниченные проверки

Синтетический runner компилирует actual shared source и все actual resources-widget sources встроенным csc с langversion 5. Он не создаёт sensor/UI/provider objects для основных assertions. Проверяется immutable projection после Submit, timestamps кэша/helper/RTT, dedicated/shared/unknown memory, арифметика commit, local-day UTC/DST bounds, midnight attribution настоящего NetJournal, нулевые/неизвестные данные, latest-slot under pressure, worker ownership через отдельный процесс, разные файлы, совместимый reader старого immutable handle, exclusive sharing failure/recovery, size bound, private ACL, отказ broad target/dir, fixture/live path guard и directory junction. Снимки проходят schema и semantic oracle этапа 1. Исходный hook/headless guard проверяется ограниченным smoke по коду.

Отдельные assertions различают empty/stale gateway и достоверное старое наблюдение с исходным timestamp; semantic oracle отклоняет `unknown + gateway` для reachability. DOS 8.3 regression использует `GetShortPathNameW` только на новом test-owned каталоге на D: после остановки live writer и проверяет alias в обеих ролях, несуществующего потомка и сохранность live snapshot. Если short-name alias отсутствует, runner печатает `SKIP dos83_alias_live_guard` и не меняет настройки 8.3 или файловую систему.

Для PowerShell в корне репозитория; исполнитель — Codex. Runner не устанавливает зависимости и пишет только новый изолированный каталог под указанным WorkRoot:

```powershell
.\telemetry\publisher-tests\resources\run.ps1
```

Для PowerShell в корне репозитория; исполнитель — Codex. Только компиляция, без запуска assertions/датчиков:

```powershell
.\telemetry\publisher-tests\resources\run.ps1 -CompileOnly
```

Для PowerShell в корне репозитория; исполнитель — Codex. Дополнительно ограниченный запуск настоящего существующего Sampler.Loop примерно на 11 секунд, без UI, ping, LHM HTTP polling и записи network history; mode live, собственный D: testdir:

```powershell
.\telemetry\publisher-tests\resources\run.ps1 -LiveSmoke
```

Real-sampler harness сначала подтверждает, что read-only Sampler не создаёт publisher, затем только внутри теста через reflection привязывает explicit isolated live publisher к тому же Loop. Два последовательных интервала 500/2500 ms, Detailed true → false, проверяют продолжение публикации при скрытой карточке и ограничение частоты. Production CLI не получает тестовой команды или второго daemon. Датчики не запускаются самим synthetic runner без LiveSmoke.

История 19 октября сохранена в [результатах этапов 2–3](ai-telemetry-stage23-results.md); окончательная приёмка 21 октября и receipt — в [PRODUCER-READINESS](../PRODUCER-READINESS.md). Выполнены функциональные, отказные и performance-проверки, точечная установка в существующие рабочие каталоги и проверка живых снимков. Clean-machine installation и готовность отдельного Bridge этим не утверждаются. Target проверяется перед каждой заменой, ACL temp проверяется через эксклюзивный FileStream; дублирующего чтения target ACL перед созданием temp нет.
