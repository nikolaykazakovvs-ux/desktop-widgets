# Публикатор подписочных квот: этап 3

Публикатор встроен в штатный интерактивный LimitsWidget. `Collector.CollectOne` передаёт каждый завершённый результат отдельно, до ожидания остальных провайдеров. Медленный источник задерживает общий UI batch как раньше, но не публикацию уже полученных квот. Фоновый общий writer пишет только `quotas-snapshot.json`; сборщик и UI не выполняют файловую публикацию. Ошибка публикатора не останавливает сбор и карточку.

## Жизненный цикл и интервалы

Публикатор создаётся вместе с интерактивным окном, продолжает heartbeat при свёрнутой карточке и освобождается в `Closed`. `--dump` и `--render` его не создают. Период запросов провайдеров сохранён: по умолчанию 300 секунд, нижняя граница 60 секунд. Heartbeat ровно 10 секунд передаёт только уже замороженную проекцию; запросы провайдеров, credentials и OS queries внутри него отсутствуют. Только background writer добавляет фактический `published_at` перед записью.

Результаты провайдеров хранятся как отдельные JSON проекции с allowlisted полями. Один короткий lock упорядочивает изменение словаря, захват immutable снимка и Submit; heartbeat не может позже затереть уже принятую новую версию. Shared writer использует один latest-value slot, единый именованный mutex, приватный каталог и замену файла; его реализация общая с hardware publisher. Подробности shared writer принадлежат этапу 2 и его проверкам.

## Время и исходные факты

`LimitBar.Quota` хранит typed pool/window identity, исходную долю и reset отдельно от подписей/отрисовки. UI может визуально показывать восстановившуюся полосу после reset, но telemetry сохраняет исходный процент. `measured_at` означает исходное наблюдение; `last_attempt_at` — завершение текущей попытки чтения; у provider/snapshot measurement — максимум измерений окон. Heartbeat ни одно из них не меняет.

`ProviderUtil.MarkFresh` использует фактическое завершение адаптера, а не начало общего batch. Визуальные строки не используются как идентификаторы. Состояние сохраняет typed metadata для последующего stale fallback. Старый state без этих полей не восстанавливается через парсинг UI-подписей: telemetry получает unknown. Успешный HTTP/CLI ответ сам по себе не подтверждает новую квоту, если адаптер возвращает кэш.

Точные пути свежести проверены 19 октября чтением vendor code, встроенного в установленные `claude.exe` и `kimi.exe`, без запуска CLI/API и без чтения credentials. Это подтверждает семантику проверенного текущего источника, а не произвольного будущего ответа.

| Путь | Доказательство наблюдения | Telemetry |
|---|---|---|
| Codex app-server rateLimits/read | [официальный AccountProcessor](https://github.com/openai/codex/blob/main/codex-rs/app-server/src/request_processors/account_processor.rs) вызывает BackendClient get_rate_limits; успешный штатный запрос | фактическое завершение адаптера |
| Antigravity /usage | [официальная документация](https://www.antigravity.google/docs/cli/commands/usage) подтверждает обновление backend quota при команде | фактическое завершение адаптера |
| Kimi локальный `/oauth/usage` | route → IOAuthService.getManagedUsage → toolkit.getManagedUsage → await fetchManagedUsage → await res.json; кэша квот в этой цепочке нет | фактическое завершение адаптера |
| Kimi прямой `/usages` | HTTP GET с успешным разбором ответа | фактическое завершение адаптера |
| Claude `get_usage` | upstream R() может вернуть статус ok из Ujo snapshot cache, а Mcn() теряет source/age и не передаёт timestamp | unknown, даже если ключ `cache_status` отсутствует |
| Claude человекочитаемый `/usage` | строки процентов/reset не доказывают исходное время запроса | unknown |
| Claude desktop history | исходный sample.t | сохранить этот timestamp |
| State cache с typed metadata | исходное сохранённое окно | сохранить исходный timestamp и процент, обновить только last_attempt_at |
| Legacy state/UI bars | нет typed provenance | unknown, проценты null |

В проверенном Claude bundle нет поля `cache_status`. Поэтому нельзя считать доказанно новым ответ без этого ключа; само слово fresh/ok тоже не возвращает потерянный timestamp. Для нового upstream протокола с документированным source-observed timestamp понадобится явное обновление адаптера, которое сохранит именно это время, без подстановки now. В текущем Kimi bundle сервер не кэширует квоту, поэтому получение ответа — доказанный новый запрос; его process heartbeat и token expiry остаются отдельно и никогда не используются как время квоты.

Локаторы проверенного vendor code: Claude bundle — функции `R`, `DKn`, `Mcn`, `Ujo` (ветка cached status ok и проекция get_usage); Kimi bundle — route `/oauth/usage`, `IOAuthService.getManagedUsage`, `OAuthToolkit.getManagedUsage`, `fetchManagedUsage`, `parseManagedUsagePayload`. SHA-256 проверенных бинарных артефактов: Claude `EB95BB65955F8B1702E800815F9A2C0388A5DE0F196C354C7EE1DD5CB9A2BA23`, Kimi `E3042A13F5B0152DBC28C5AF9B8F24D0A95D7E4FBD272CB24CF959C93F20A4DB`. Byte locators: Claude R/Мcn block около 228957000–228960500; Kimi fetchManagedUsage около 94332000, toolkit около 94403793, OAuthService около 103518607, route около 109767764. Только код программы, никаких account/state/credentials файлов. Общедоступный официальный исходник старого [MoonshotAI/kimi-cli](https://github.com/MoonshotAI/kimi-cli) не подменяет установленный новый runtime. Поиск официальной документации Claude не дал публичного контракта provenance для внутреннего get_usage, поэтому его нельзя дополнять выдуманными timestamp полями. Ни heartbeat, ни bridge не запускают дополнительный refresh.

## Реестр идентичностей

| Источник | Pool ID | Модели/продукты | Окна |
|---|---|---|---|
| Codex основной | `subscription.general` | `codex.general` | 5 часов, 7 дней/сутки по исходному duration |
| Codex известный reserve | `subscription.reserve` | `codex.reserve` | исходные известные duration |
| Claude общий | `subscription.general` | `claude.opus`, `claude.sonnet`, `claude.haiku` | 5 часов, 7 дней |
| Claude scoped | `subscription.opus`, `.sonnet`, `.cowork`, `.oauth_apps` | соответствующий registry ID | 7 дней |
| Antigravity Gemini | `subscription.gemini` | `gemini.general` | 5 часов, сутки, 7 дней |
| Antigravity Claude/GPT | `subscription.claude_gpt` | `claude.general`, `gpt.general` | 5 часов, сутки, 7 дней |
| Kimi Code | `subscription.code` | `kimi.code` | 5 часов, legacy 7 дней |
| Kimi подписка | `subscription.shared` | `kimi.code`, `kimi.work`, `kimi.claw` | календарный месяц, duration null |

`model_ids` обозначает применимость одного пула, проценты по моделям не складываются. Доля Kimi Code внутри месячного общего расхода не экспортируется как независимый дополнительный бюджет. Неизвестные произвольные provider groups, model slugs и scoped labels остаются только в существующем UI. Их появление понижает полноту telemetry, но не создаёт публичный ID из сырой строки. Gemini как отдельный новый адаптер не добавлен: текущий сборщик представляет его через Antigravity pool. Отключённые/не настроенные провайдеры не получают фиктивных нулей.

Экспорт не включает план, account/profile/email, raw output, credentials, исходные строки ошибок, команды, пути, UI state, денежные суммы, API pricing, credits или бюджеты. Существующие UI функции кредитов и входа сохранены. Top-level reason/status ограничены контрактом 1.0.0; source partial/error не повышается из-за heartbeat. Вычисление `reset_time_passed_unverified`, freshness и hints остаётся bridge этапа 4.

## Запуск и безопасные фикстуры

Для обычного интерактивного запуска каталог разрешается общим writer: `--telemetry-dir` → `DESKTOP_WIDGETS_TELEMETRY_DIR` → `%LOCALAPPDATA%\DesktopWidgets\telemetry`. Допустим только абсолютный путь на локальный фиксированный диск. Нестандартный каталог не включает fixture mode.

Для интерактивных фикстур нужны одновременно `--telemetry-fixture`, `--telemetry-dir`, `--fixtures`, явные изолированные `--config` и `--state`. Отсутствующая обязательная fixture возвращает `fixture_missing` до входа в live-код. Альтернативные fixture файлы Claude/Kimi допустимы, но ни один отсутствующий вариант не вызывает live fallback. Headless fixture режим тоже fail-closed, но не публикует telemetry. Не использовать установленную копию для проверок.

## Конечная проверка

PowerShell из корня checkout; исполнитель — Codex. Путь результата отдельный на D:, никакая установленная копия не запускается и не останавливается. Harness компилирует реальные классы виджета и shared writer C# 5, запускает только fake providers/синтетические ответы и проверяет JSON Python reference этапа 1. Ожидаемое время около 15–25 секунд, включая настоящий 10-секундный heartbeat.

```powershell
& .\telemetry\publisher-tests\quotas\run.ps1 -OutDir 'D:\projects\Виджеты\_work\quotas-publisher-stage3'
```

Покрытие: fast provider до завершения slow provider; completion-time вместо batch-start; cache+error с исходным временем; legacy unknown; Kimi month/code и Antigravity common pool; одновременные Accept и heartbeat без потери provider; heartbeat не меняет измерения и счётчики fake Collect; визуальный reset не изменяет факт; отсутствие canary/credit полей; missing fixtures до live paths. Каждый emitted case валидируется схемой и semantic layer. Reset-pending дополнительно вычисляется Python bridge reference.

Штатная сборка: PowerShell из корня checkout; исполнитель — Codex. Скрипт сборки не запускает приложение и не останавливает установленные копии.

```powershell
& .\windows\limits-widget\build.ps1
```

Финальный последовательный запуск Root после уточнения upstream provenance и общего writer прошёл: 22 C# assertions и 10 emitted quota snapshots успешно прошли JSON Schema + semantic validation reference этапа 1. Harness собирает все реальные исходники виджета и shared writer через native csc с `/langversion:5`; отдельная штатная сборка также пройдена. Независимое ревью и границы приёмки описаны в [результатах этапов 2–3](ai-telemetry-stage23-results.md). Запуск выполнен из уже работающего PowerShell 7 с действующей политикой RemoteSigned. Дочерний Windows PowerShell 5 блокировал запуск скрипта; политики не менялись. Live provider refresh, установка, автозапуск, текущие credentials и соседняя установленная копия не проверялись и не изменялись.
