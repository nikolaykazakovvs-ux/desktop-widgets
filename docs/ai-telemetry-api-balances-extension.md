# AI Telemetry: расширение `api_limits` (балансы API)

Статус (07.11.2026): **reader и узел Bridge реализованы и проверены офлайн; установки в клиенты и приёмки нет; producer живёт в отдельном проекте «Балансы API».** Решение оператора 07.11.2026: виджет «Балансы API» — отдельный проект; его публикатор пишет `api-balances-snapshot.json` по этому расширению, а Bridge в этом репозитории только читает. Файл-источник в этом репозитории не создаётся; без него Bridge отдаёт резерв `not_available` (см. [контракт](ai-telemetry-contract.md), раздел «Ответ и независимые состояния»).

Схема: `telemetry/schema/extensions/api-limits-v1.1.schema.json` (JSON Schema 2020-12, `$id` `https://schemas.desktop-widgets.invalid/telemetry/1.0.0/extensions/api-limits-v1.1.schema.json`, объекты `additionalProperties: false`). Корень схемы описывает файл producer, `$defs/bridge_api_limits` — узел Bridge. `bridge-output.schema.json` ссылается на узел относительным `$ref` `extensions/api-limits-v1.1.schema.json#/$defs/bridge_api_limits`; он разрешается по `$id` стандартным реестром (в `contract.py` и в тестах реестр строится только по `$id`, без обходных ключей), Bridge загружает схему из того же каталога. Принимаемые значения `schemaVersion` и `schema_extension` включают `1.1.0`, `1.0.0` и их `-draft`-варианты: ответ с draft-версией producer принимается и версия передаётся в `schema_extension`; потребитель, не допускающий draft (по умолчанию так у `sigma-core`), переведёт такой бюджет в ошибку.

## 1. Что не меняется (Stage 1)

`schemaVersion 1.0.0` и `policy_version 0.1.0` ответа Bridge не менялись: `api_limits` — точка расширения, форма и правила вычисления узла версионируются полем `schema_extension` (правило для потребителей — в [контракте](ai-telemetry-contract.md), раздел о версиях). Узел `api_limits` допускает две формы: резерв `{status: not_available, reason: monitor_not_implemented}` (когда файла-источника нет) либо узел расширения из раздела 4. Отсутствие снимка не означает нулевой расход или свободный бюджет. Входные схемы hardware и quotas расширение не затрагивает; эталон `contract.py` выводит только резерв, а узел расширения проверяет по схеме и часам.

## 2. Состояние активации

| Шаг | Состояние на 07.11.2026 |
|---|---|
| Схема расширения и ссылка из `bridge-output.schema.json`; тесты схем | реализовано, проверено офлайн (`telemetry/tests/test_contract.py`, класс `ApiLimitsExtensionTests`) |
| Reader и вычисление узла в Bridge (`telemetry/bridge/reader.mjs`, `core.mjs`), допуск имени файла в общем writer | реализовано, проверено офлайн (блоки A1-API в `telemetry/bridge/test.mjs`, `WriterTests.cs`) |
| Адаптер S.I.G.M.A. (`telemetry/integration/sigma-adapter`) | упрощённый потребитель Bridge CLI для контракта 1.0.0: ресурсы и квоты читает, узел `api_limits` игнорирует (это проверяет его тест); бюджеты API читает адаптер в `sigma-core`, который шире этой копии |
| Producer `api-balances-snapshot.json` | вне этого репозитория: проект «Балансы API» |
| Установка Bridge в клиенты, согласование порогов с потребителями, приёмка | не выполнено |

`schemaVersion` ответа Bridge и `policy_version` не повышаются: вместо этого форма узла и его вычисления версионируются `schema_extension`, а потребитель контракта 1.0.0 принимает обе формы узла или игнорирует его (адаптер S.I.G.M.A. в этом репозитории игнорирует). Повышение `policy_version` потребуется, если данные `api_limits` попадут в `orchestration_hints`.

## 3. Producer-файл `api-balances-snapshot.json`

Поля верхнего уровня (все обязательны): `schemaVersion` (допустимые значения — в разделе «Схема» выше), `kind: "api-balances-snapshot"`, `source_id: "api-widget"`, `mode: fixture|live`, `measured_at` (nullable), `published_at`, `last_attempt_at` (nullable), `status` (`ok|partial|unavailable|unknown|error`), `reason`, `poll_interval_ms` (60000–86400000), `heartbeat_interval_ms` (ровно 10000), `pools[]` (до 64), `connections[]` (до 256), `entries[]` (до 512), `providers[]` (до 64; сводка по провайдеру: статус, возраст, валюты). Времена — UTC `…Z`, как в `common.schema.json`.

Идентичность:

| Поле | Правило |
|---|---|
| `provider_id` | стабильный slug (`deepseek`, `openrouter`, …) |
| `account_id` | `acc_` + 16–64 символа `[a-z0-9]`: непрозрачный стабильный hash/slug; не email, не имя, не название организации, не фрагмент ключа |
| `connection_id` | `conn_…`: одно подключение (ключ) виджета; не секрет и не ссылка на хранилище |
| `shared_pool_id` | `pool_…`: один provider-side баланс. Несколько ключей одного account/org/project, делящих баланс, получают один и тот же pool |

Правило пула: агрегирование идёт **по `shared_pool_id`**; подключения одного пула никогда не суммируются, баланс пула учитывается один раз. Это согласуется с описанием виджета в проекте «Балансы API»: подключения с общим балансом аккаунта не суммируются. Баланс разных пулов и валют тоже не суммируется: итоговых полей в схеме нет.

Entry (`entries[]`): `connection_id` (строка либо `null` для записи уровня пула), `shared_pool_id`, `metric_kind`, `currency`, `credit_unit`, `amount`, `scale`, `minor_units`, `value_origin`, `status`, `reason`, `measured_at`, `last_attempt_at` обязательны (неизвестное — `null`); `degradation_status` (`none|stale_after_error|stale_cached|credentials_unconfigured|corrupt_payload`) необязателен. `status` записи — `ok|unknown|unavailable|error|not_supported`. Подключение (`connections[]`) может нести `1password_ref_masked` — санитизированную ссылку вида `1p_masked:<часть>/<часть>/***` без `op://`, идентификатора хранилища и секрета, а также `rate_limits` (целые счётчики или `null`); значения секретов в файл не попадают.

- `metric_kind`: `account_balance`, `period_spend`, `key_allowance`, `plan_credits`. Расход (OpenAI/Anthropic с начала месяца UTC), лимит ключа и кредиты плана не переименовываются в баланс.
- `currency`: ISO 4217, три заглавные буквы, **или** литерал `credits`. При `credits` обязателен `credit_unit` (id единицы, например токены или кредиты Tavily); иначе `credit_unit` равен `null`. Кредиты не конвертируются в деньги.
- `value_origin`: `measured` (ответ billing API провайдера), `calculated` (арифметика локального ledger), `estimated`.
- `status: ok` требует `amount`, `scale`, `minor_units`, `measured_at`, `reason: none`; иначе все три значения `null`, `reason` не `none`. Неизвестное не равно нулю. `period_spend` и `key_allowance` со статусом `ok` неотрицательны.

### Деньги: каноническая форма

Каноническая — строка `amount` по `^-?(0|[1-9][0-9]{0,17})(\.[0-9]{1,9})?$`. Обоснование: точный десятичный текст без потерь, без IEEE-float (float в JSON отвергается типом `string`), не зависит от валютного exponent и от разрядности парсера. `scale` (0–9) обязан равняться числу знаков после точки в `amount`, а `minor_units` (целое, |x| ≤ 2^53−1) равен `amount` без точки; это redundant integer cross-check для потребителей, у которых нет decimal-типа. Проверка равенства `amount` ↔ `scale` ↔ `minor_units` семантическая (JSON Schema её не выражает) и выполняется валидатором reader (`core.mjs`). Значение, не помещающееся в 2^53−1 минимальных единиц, отвергается как `invalid_input`, а не округляется. Отрицательные суммы допустимы для `account_balance` (долг/овердрафт у провайдера), но не для `period_spend` и `key_allowance`.

### Коды ошибок

Закрытый enum `error_code` (источник истины — `$defs/error_code` схемы): `none, not_configured, credentials_unconfigured, auth_failed, insufficient_permission, not_eligible, rate_limited, provider_unavailable, network_unreachable, timeout, invalid_response, unsupported_currency, clock_invalid, source_unavailable, source_stale, source_partial, publisher_silent, invalid_input, access_denied, transient_io_error, conflicting_shared_pool_balance, stale_after_error, corrupt_snapshot_nul_bytes, invalid_json, internal_error`. Сырой текст провайдера, URL, заголовки, тела ответов в файл не попадают.

## 4. Узел Bridge `api_limits` (v1.1, `#/$defs/bridge_api_limits`)

Верхний уровень (все поля обязательны): `schema_extension` (`api-limits/<schemaVersion источника>`; для изолированного узла-ошибки `api-limits/1.1.0`), `status` (`ok|partial|unavailable|unknown|error`), `reason`, `read_status`, `liveness` (`alive|silent|unknown`), `freshness` (`fresh|stale|unknown`), `pools[]`. `read_status` схема допускает `ok|missing|invalid_input|clock_invalid|transient_io_error|access_denied|unavailable|error`; reader выдаёт коды чтения `ok`, `missing`, `invalid_input`, `clock_invalid`, `transient_io_error`, `access_denied`.

Pool: `shared_pool_id`, `provider_id`, `account_id`, `connection_ids[]` (уникальные), `entries[]`. Entry повторяет поля producer (в том числе `connection_id: null` у записи уровня пула), но `shared_pool_id` не повторяется, и добавлены **Bridge-derived** поля `freshness` (`fresh|stale|unknown`), `liveness` (`alive|silent|unknown`), `age_ms` (целая часть разности времени ответа и `measured_at`, `null` без измерения; при `measured_at: null` — `freshness: unknown`); при наличии у источника копируется `degradation_status`. Producer эти поля не публикует.

Пороги (код `deriveApiLimits`, `telemetry/bridge/core.mjs`): liveness — возраст `published_at` ≤ 30000 мс; свежесть записи — возраст её `measured_at` ≤ `max(180000, 3 × poll_interval_ms)` мс у `ok`-записи. Свежесть и статус узла агрегируются по записям: любая `stale`-запись даёт `stale`; `status: ok` источника понижается до `partial` с причиной `publisher_silent`, `source_stale` или `source_partial` (есть запись не `ok`). Свежий heartbeat не обновляет `measured_at`. При ошибке чтения с кэшем узел получает `status: error`, как у hardware и quotas, а пулы остаются из кэша. Полные правила — в [контракте](ai-telemetry-contract.md).

Семантические правила reader (вне JSON Schema, `validateApiBalances`):

- `measured_at` источника равен самому позднему `measured_at` его записей; порядок времён источника `measured_at ≤ last_attempt_at ≤ published_at`; источник со `status: ok` содержит хотя бы одну `ok`-запись;
- уникальные `shared_pool_id` и `connection_id`; у каждого пула есть подключение; подключение ссылается на существующий пул и несёт его `provider_id`; запись с непустым `connection_id` ссылается на подключение **своего** `shared_pool_id`;
- порядок времён записи: `measured_at ≤ last_attempt_at ≤ last_attempt_at источника`; известное наблюдение требует известную попытку;
- записи уровня пула (`connection_id: null` либо метрика не `key_allowance`) с одинаковым `(shared_pool_id, metric_kind, currency, credit_unit)` не складываются: при равном `measured_at` значения обязаны совпадать, иначе `conflicting_shared_pool_balance`; при разных `measured_at` узел Bridge берёт запись с самым поздним `measured_at`;
- `scale` и `minor_units` соответствуют `amount`; знак и единицы (`credits` — `credit_unit`) — частично тем же правилом схемы, которое срабатывает первым;
- отсутствие образцов секретов, URL, e-mail, путей и NUL в строках;
- готовый узел в `bridge-output.json` проверяется схемой, часами и внутренними инвариантами (см. [контракт](ai-telemetry-contract.md), «Границы проверки ответа»).

## 5. Отделение от `subscription_quotas`

`subscription_quotas` описывает окна подписок в процентах (`used_percent`, `remaining_percent`, `resets_at`). `api_limits` описывает денежные/кредитные суммы провайдерских API-аккаунтов. Поля, валюты, кредиты и стоимость не переносятся между узлами; квота плана, например Z.ai Coding Plan, остаётся квотой и не становится API-балансом. Узел не даёт рекомендаций по запуску исполнителей, scheduler, routing или команд.

## 6. Запреты

- Не читать UI-состояние виджета (`state.json`, выбранные вкладки, фильтры, экспорт скриншотов): источник — только явно публикуемый producer-файл.
- Не обращаться к 1Password, DPAPI, хранилищам учётных данных и любым хранилищам секретов; Bridge не вызывает API провайдеров и не имеет доступа к ключам.
- В файл и в Bridge-вывод не попадают raw keys/tokens, email, имена, названия организаций, URL запросов, тексты ошибок провайдера.
- Для `mode: fixture` данные синтетические; живой аккаунт в fixture не используется.

## 7. Проверка

- Схема: `Draft202012Validator.check_schema`; `telemetry/tests/test_contract.py`, класс `ApiLimitsExtensionTests`: все `$ref` всех схем разрешаются стандартным реестром по `$id`; `$id` расширения равен цели ссылки из `bridge-output`; ответ с узлом расширения валиден, а недопустимые узлы (лишнее поле, неизвестный `read_status`, пул без подключений, `amount` числом или с пробелом, отрицательный `key_allowance`, неверная причина резерва) отвергаются без ошибок разрешения ссылок; снимок producer: положительные формы (запись уровня пула, `credits` с `credit_unit`, отрицательный `account_balance`) и отказы (число вместо строки, `credits` без `credit_unit`, `credit_unit` у денежной валюты, отрицательные `period_spend`/`key_allowance`, пробел и перевод строки в `amount`, `ok` без суммы, не-`ok` с суммой или без причины, лишнее поле, `ok` без пулов, `heartbeat_interval_ms` не 10000, слишком малый `poll_interval_ms`, URL вместо идентификатора).
- Reader, вычисление и инварианты узла: блоки A1-API в `telemetry/bridge/test.mjs` (производные значения, дедупликация без суммирования, отказы с точным сообщением правила, агрегирование свежести и статуса по записям, принадлежность подключения пулу, порядок времён записей, пул без подключения для reader/CLI/MCP, ошибка чтения с кэшем на одном экземпляре reader, проверка готового узла) и тест ACL (повторная проверка владельца и DACL после замены файла).
- Допуск имени `api-balances-snapshot.json` в общем writer проверяет `telemetry/publisher-tests/shared/WriterTests.cs`.
- Адаптер S.I.G.M.A. в этом репозитории узел `api_limits` не интерпретирует; это фиксирует `test_api_limits_node_is_ignored_not_interpreted` в `telemetry/integration/sigma-adapter/test_bridge_telemetry_adapter.py`. Проверки бюджетов API на стороне S.I.G.M.A. находятся в `sigma-core`.

Все проверки офлайн, на синтетических данных; живой producer и установленный Bridge не участвовали.
