# AI Telemetry Bridge: совместимость и измерительный прототип

Срез: 19 октября 2026 года, Windows x64. Область — только этап 1 принятого плана: схемы, fixture, транспорт и измерения. Подключение к рабочему ведущему, реальные producers, пилот и поставка относятся к последующим этапам. Installed paths ниже обезличены через стандартные переменные Windows или опущены; это проверенные расположения на тестовой машине, не требование для других установок.

## Среда и выбор SDK

| Компонент | Проверено локально | Роль |
|---|---|---|
| Node.js | 24.19.0; `%LOCALAPPDATA%/Microsoft/WinGet/Packages/OpenJS.NodeJS.LTS_Microsoft.Winget.Source_8wekyb3d8bbwe/node-v24.19.0-win-x64/node.exe` | Установленный runtime; новый runtime не устанавливался |
| MCP server/client | `@modelcontextprotocol/server` и `@modelcontextprotocol/client` 2.3.1, engines Node >=20; package registry и установленные package manifests | Официальный актуальный split TypeScript SDK; JavaScript ES modules без compiler/build step |
| Схема аргументов | zod 4.6.7, установленная зависимость и pinned manifest | Строгий пустой объект; пути в tool arguments запрещены |
| JSON Schema validator | Встроенный в SDK Ajv с поддержкой draft 2020-12 | Канонический bundled outputSchema, структурная валидация без network dereference |
| Codex CLI | 0.163.0; `codex.exe` (путь установки опущен) | app-server и ephemeral native client probe |
| Claude Code | 2.1.292; `%USERPROFILE%/.local/bin/claude.exe` | Scoped MCP management/health probe без model inference |
| Antigravity CLI | `%LOCALAPPDATA%/agy/bin/agy.exe`; `--help` и `mcp --help` подтверждены; версия **NO_DATA** | Команды MCP существуют; версия отсутствует в inspected help/Windows file metadata |

Версии относятся к этому срезу, а не к будущей установке. `package.json` и `package-lock.json` позволяют повторить зависимости; `npm ci --ignore-scripts` выполнен отдельно от виджетов. Официальный SDK требует Node >=20 и разделяет client/server; stdio запускает и останавливает клиент. Основание: [официальный quickstart server](https://ts.sdk.modelcontextprotocol.io/v2/get-started/first-server.html), [официальный quickstart client](https://ts.sdk.modelcontextprotocol.io/v2/get-started/first-client.html).

Отдельный Node runtime bridge не меняет требование существующих widget publishers C# 5/.NET Framework. Установка новой .NET SDK не требуется. Production упаковка Node и обновление зависимостей пока не выбраны: lockfile прототипа не равен release package.

## Матрица доказательств

| Поверхность | Что поддерживает документация/установленный CLI | Что проверяет прототип | Что ещё не доказано |
|---|---|---|---|
| Официальный SDK client | stdio, initialize, listTools, callTool и close | Автоматический fixture test с полным output, строгим inputSchema и canonical outputSchema | Live snapshots, реальная политика hints, модельный выбор |
| Установленный Codex CLI | `app-server`, stdio, `-c` overrides; installed executable генерирует собственную protocol schema | **PASS**: изолированный CODEX_HOME; initialize, ephemeral thread, mcpServerStatus/list только telemetry_fixture, mcpServer/tool/call без turn/start; app-server и fixture process завершены | Осталась диагностическая папка с EBUSY; причина не установлена. Работа в текущем desktop чате и model invocation не проверены |
| Codex desktop | Документация описывает общий MCP config с CLI и STDIO | Текущий host действительно предоставляет локальное shell execution; prototype не регистрируется в этой сессии | Применение конфигурации к уже открытому чату, самостоятельный model tool call и решения ведущего |
| Claude Code | Установленный help: `mcp add-json`, `get`, scoped remove; session flags `--mcp-config` и `--strict-mcp-config` | **PASS health-check**: изолированный CLAUDE_CONFIG_DIR; add/get/remove конкретного synthetic сервера без prompt | get не доказывает tools/call; модельный вызов не запускался |
| Antigravity CLI | Installed help содержит `mcp add/remove/list/enable/disable`; [официальная MCP документация](https://www.antigravity.google/docs/mcp?tab=cli) | Только help; конфигурации не менялись, сессии не запускались | Версия, конкретный stdio initialize/tools/call, контекст root; полноценная совместимость не заявлена |
| Облачный чат/worker | Локальный executable не доказывает доступ облака к локальному stdio | Не проверялось | Нужна отдельная проверка точного облачного host/разрешений; переносить вывод с Windows CLI нельзя |

[OpenAI MCP documentation](https://learn.chatgpt.com/docs/extend/mcp) описывает локальную конфигурацию и общий config desktop/CLI. [App-server reference](https://learn.chatgpt.com/docs/app-server) описывает отдельные native methods discovery/call; installed generated schema проверяет их exact parameters. Эти методы позволяют проверить реальный native клиент без запроса модели. Успешный такой вызов доказывает путь клиента до synthetic инструмента, но не автоматическое использование ведущим в обычной работе.

[Claude MCP documentation](https://code.claude.com/docs/en/mcp) различает локальный stdio, управление серверами и рабочую сессию. [Settings documentation](https://code.claude.com/docs/en/settings) подтверждает relocation через CLAUDE_CONFIG_DIR; probe использует пустой временный профиль, не переносит credentials. Полный доступ в обычной сессии и самостоятельный вызов модели остаются отдельной приёмкой этапа 5.

## Windows-файлы и границы измерения

В Node reader используется readFile/read-only open, но точные share flags C# FileShare.Read|Delete через этот API не задаются. В исходнике libuv из exact tag Node v24.19.0 обычный open допускает FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE. Это sharing compatibility, а не запрос разрешения записи и не ACL. Поэтому более широкий sharing Node нельзя описывать как точные C# flags. Проверенный source: [Node v24.19.0, libuv Windows fs.c](https://github.com/nodejs/node/blob/v24.19.0/deps/uv/src/win/fs.c).

Для будущего Bridge основной контракт остаётся: read-only access, immutable published files, ограниченный срок открытого handle и совместимая замена; published target не правится на месте. Производитель сохраняет exclusive temp + close before replace и ограниченные повторы. Synthetic Node rename здесь не заменяет проверку реального C# ReplaceFile/первоначального создания, ошибок доступа/коллизий, ACL и reader/writer interop. Если этап 4 требует точного FileShare.Read|Delete, потребуется отдельный Windows reader adapter либо иная среда; текущий spike такую гарантию не даёт.

Измеритель разделяет read+parse baseline, структурно validated read, warm stdio call, cold Node CLI и synthetic fsync+rename. Он не пересчитывает hints и не читает live источники. Методика, defaults, deadlines и CPU/RSS scope находятся в README прототипа Bridge (в этом репозитории пока не опубликован). Одна миллисекунда остаётся исследовательской целью чтения файла, а не всей цепочки.

## Фактические проверки 19 октября

Root выполнил финальные контрактные тесты (36 PASS) и SDK spike tests (6 PASS) на расширенном контракте. Финальный Node test receipt (сырые замеры хранятся вне публичного репозитория). Независимый SDK client действительно завершил initialize, получил точный bundled outputSchema и вызвал synthetic инструмент; regression подтверждает отказ mixed fixture/live и длительности выше JavaScript safe integer, а исчезновение fixture после startup возвращает обезличенную ошибку. В первоначальном измерительном запуске согласована версия протокола **2025-11-25**; SDK 2.3.1 сам согласовал её, вручную версия не задавалась. Поддержка SDK более нового протокола не означает, что именно он проверен этим handshake.

Native Codex первая попытка в 01:53:47 Europe/Kyiv подтвердила app-server initialize, ephemeral thread, discovery и полный tool output без model turn. Проверка cleanup тогда завершилась неуспешно (`owned_appserver_closed=false`); исходный receipt сохранён (сырые замеры хранятся вне публичного репозитория). Во второй попытке после разделения exit/close временная папка дала EBUSY при удалении; финальный JSON тогда не был выведен из-за ошибки finalizer. Адресные чтения процессов в 01:54:50 и 01:56:24 не обнаружили процессов с signature этого synthetic сервера. Это не устанавливает причину filesystem lock и не делает первые попытки успешными.

Финальная native проверка в 02:03:33 Europe/Kyiv на полном контракте подтвердила initialize/discovery/tools-call/полный output и завершение собственного app-server (`exited=true`, `closed=true`). Fixture process наблюдался до cleanup с native CreationDate; после cleanup точечный query показал `present=false`, поэтому `fixture_child_exit_verified=true`. Исходный финальный receipt (сырые замеры хранятся вне публичного репозитория) сохранён без переписывания: исторический процесс завершился с exit 1 только из-за EBUSY удаления диагностической папки. Root принял функциональную совместимость и cleanup этих процессов отдельно от сохранённой папки. Причина блокировки папки остаётся NO_DATA; следов работы production Bridge это не создаёт.

В той же проверке `thread/archive` собственного ephemeral thread вернул -32600. Этот вызов удалён из будущего probe; проверка не повторялась ради удаления папки. Будущий report разделяет functional pass, подтверждённое завершение процессов и benign retained diagnostic directory после ограниченных EBUSY/EPERM retries. Unknown/present процесс сохраняет failure. Изменённая классификация выхода после успешной проверки процессов проверена чтением кода; дополнительного native запуска для косметики cleanup не было. Дополнительное точечное чтение Root в 02:04:23 обнаружило 0 потомков исходных app-server/fixture PID; причина EBUSY этим не установлена.

Claude Code isolated probe в 01:54:51 Europe/Kyiv завершился с exit 0: конфигурация распознана, `mcp get telemetry_fixture` подтвердил Connected, собственные management processes завершились, временная запись удалена. Исходный health-check receipt (сырые замеры хранятся вне публичного репозитория). Native tools/call и модельный вызов Claude не проверялись. Это ровно уровень health-check установленного клиента.

Повторный Claude health-check на финальном контракте (сырые замеры хранятся вне публичного репозитория) в 02:04:23 также завершился с exit 0 и подтвердил Connected, завершение management processes и удаление временной записи. Модель не вызывалась.

Первоначальный baseline до дополнения контракта температурами, GPU load, shared GPU memory и network quality: короткий последовательный benchmark в 01:55:10 Europe/Kyiv, payload 5567 bytes, wall 8,30 секунды. Сохранён в baseline-before-contract-completeness.json (сырые замеры хранятся вне публичного репозитория); он не является измерением финального расширенного контракта.

| Операция | n | p50, ms | p95, ms |
|---|---:|---:|---:|
| Read + JSON.parse | 50 | 0,495 | 0,679 |
| Read + structural schema validation | 50 | 0,886 | 1,261 |
| Warm official SDK MCP | 50 | 2,567 | 3,676 |
| Cold CLI, включая import/compile/validation | 10 | 647,929 | 914,387 |
| Synthetic create/write/fsync/close/rename | 50 | 4,200 | 5,724 |

За 50 warm MCP calls собственный server учёл 78 ms user CPU и 0 ms system CPU; RSS изменился с 98,89 до 104,39 MiB, heap в конце 18,30 MiB. Это выборочные показания и короткий workload; верхняя граница или пиковая память ими не доказаны. CPU runner за весь набор 266 ms user + 219 ms system. Cold child CPU/RSS измерены отдельно по каждому завершению; p95 из десяти процессов грубый. Инструмент на постоянном MCP устраняет повторную цену cold startup; один процесс на клиента имеет заметную память около 104 MiB в этом опыте. Десять лишних MCP процессов ради измерения не запускались.

Root принял экспериментальные бюджеты этапа 1 для одного fixture клиента на сопоставимом Windows host: validated read p95 ≤2 ms, warm MCP p95 ≤10 ms, cold CLI p95 ≤1500 ms, steady sampled RSS одного bridge ≤128 MiB, synthetic publication p95 ≤10 ms и mean own CPU публикации ≤5 ms/operation. Это исследовательские границы для прототипа, а не production SLA или разрешение запуска тяжёлой работы; гарантия для десяти клиентов не выводится. Окончательный production budget должен включать реальное вычисление hints, reader/writer interop и приёмку производителей. Пороговые интервалы freshness и heartbeat не выводятся из скорости synthetic чтения.

## Финальное измерение полного контракта

Неизменённый JSON receipt (сырые замеры хранятся вне публичного репозитория): 02:04:51 Europe/Kyiv, Windows x64/Node 24.19.0, входной fixture 12409 bytes с форматированием, compact JSON payload 7515 bytes, wall 8,66 секунды. Все операции последовательны; n=50 для тёплых операций и публикации, n=10 для новых CLI processes. Согласованный MCP protocol снова 2025-11-25.

| Операция | p50, ms | p95, ms | Проверка принятого экспериментального бюджета |
|---|---:|---:|---|
| Read + JSON.parse | 0,639 | 1,055 | Цель 1 ms для p95 на этом запуске не достигнута; обещания не было |
| Read + structural schema validation | 1,067 | 1,838 | PASS ≤2 ms |
| Warm official SDK MCP | 2,554 | 3,432 | PASS ≤10 ms |
| Cold CLI, включая import/compile/validation | 728,027 | 902,074 | PASS ≤1500 ms |
| Synthetic create/write/fsync/close/rename | 4,812 | 6,773 | PASS исследовательского бюджета ≤10 ms |

Steady sampled RSS MCP server: 112,355 → **112,535 MiB**, PASS начального бюджета ≤128 MiB; heap в конце 22,216 MiB. За 50 warm calls собственный server учёл 125 ms user CPU и 0 ms system CPU. Synthetic publication отдельно учла 31 ms user + 78 ms system CPU, то есть 109 ms на 50 последовательных операций: **mean own CPU 2,18 ms/operation, PASS ≤5 ms**. Эти значения относятся к измерительному процессу Node, не к будущему C# publisher. У публикации RSS был 80,453 →81,219 MiB; изменения GC между фазами не являются размером standalone publisher. Полный runner учёл 375 ms user +187 ms system CPU.

Все шесть принятых исследовательских ограничений пройдены на одном fixture клиенте. Publication budgets оставляют запас относительно предлагаемых 1 Hz публикации hardware и 0,1 Hz heartbeat quotas: 10 ms wall — 1% интервала в 1 секунду; 5 ms CPU/operation — 0,5% одного CPU-equivalent при одной операции в секунду. Для интервала 10 секунд эти доли в десять раз меньше. Это обоснование инженерного запаса, а не результат timed 1 Hz/0,1 Hz пилота или гарантия реальных C# publishers.

Для production остаются реальное слияние источников и hints, полнота semantic validation reader, датчики, sharing/replacement/ACL и принятие числа долговременных MCP процессов. Память около 113 MiB на клиента и cold CLI около 0,9 s p95 — измеренные издержки выбора Node/SDK; runtime для поставки пока не утверждён.

## Статус этапа 1

Реализованы и проверены на синтетических данных pinned официальный SDK, CLI/stdio server, canonical bundled outputSchema, SDK client tests, bounded benchmark и native probes. Функциональный native Codex call и cleanup собственных app-server/fixture процессов подтверждены; Claude подтверждён до health-check. Финальное измерение полного контракта прошло принятые экспериментальные бюджеты; исходный baseline и неуспешная первая попытка сохранены отдельно. Автоподключения, provider refresh, изменение глобального config, рестарты, установка в рабочую сессию и публикация отсутствуют. Принятие всего этапа 1 фиксируется ведущим по контракту, независимому review и этим evidence; это не внедрение Bridge в обычную работу.
