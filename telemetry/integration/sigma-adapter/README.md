# S.I.G.M.A. consuming adapter для AI Telemetry Bridge

Статус: реализовано и проверено на фикстурах; в sigma-core не внедрено. Это упрощённая версия для контракта `schemaVersion 1.0.0`: она **не читает узел `api_limits`** (ни резерв `not_available`, ни расширение v1.1) и принимает только `schemaVersion == "1.0.0"`. Адаптер, работающий в `sigma-core`, шире: он понимает `api_limits` и версии `1.1.0`; актуальным считается он, а не эта копия.

Адаптер только наблюдает. Планирование, выбор модели и исполнителя, надзор за job/process остаются в S.I.G.M.A. (см. `docs/bridge-sigma-boundaries.md`). Bridge не опрашивает источники и не выбирает исполнителей; сборщики ресурсов/квот в sigma-core копировать нельзя.

## Что заменить в sigma-core

| Было | Стало |
|---|---|
| Прямое чтение widget `state.json` | `run_bridge_cli(...)` / `load_bridge_output(...)` |
| Вызовы `nvidia-smi` для VRAM/GPU | `host.values["vram_*"]`, `gpu_usage_percent`, `host.gpu_memory_kind` |
| Собственный парсинг квот по названиям карточек | `BridgeObservation.quota(provider, pool_id, window_id)` по стабильной тройке |
| Собственные пороги давления памяти | `host.pressure[...]` из `orchestration_hints.resource_pressure` |

Адаптер: stdlib-only, один файл `bridge_telemetry_adapter.py`. Это образец потребителя ресурсов и квот; его не следует копировать поверх адаптера `sigma-core`: узел `api_limits` будет потерян. Бюджеты API читает только адаптер `sigma-core`.

## Вызов Bridge

```python
import sys, bridge_telemetry_adapter as A
obs = A.run_bridge_cli(["node", r"<repo>/telemetry/bridge/cli.mjs"], timeout_s=5)
# или Windows-обёртка: [r"<repo>\telemetry\bridge\bridge-cli.cmd"]
# тесты: [..., "--fixture", r"<abs>/bridge-output-source"]
```

- `shell=False`; exit 0 = stdout чистый JSON; exit 2 = configuration error (результат `error`); stderr/traceback в результат не попадают.
- Live-каталог telemetry: `--telemetry-dir` -> `DESKTOP_WIDGETS_TELEMETRY_DIR` -> `%LOCALAPPDATA%\DesktopWidgets\telemetry`.
- `--fixture <abs>` только для тестов; `obs.is_fixture` = `True` при `mode == "fixture"`. Решения на фикстурных данных в production не принимать.
- Также: `load_bridge_output(path)`, `from_dict(obj)`.

## Результат

`BridgeObservation.result_status`: `ok` | `unsupported_contract` | `invalid_input` | `error`. При не-`ok` поля `host`/`quotas` пустые, причина в `error`. Все dataclass frozen, `to_dict()` для сериализации.

Строгая загрузка JSON: дубликаты ключей и NaN/Infinity отвергаются. Поддерживается только `schemaVersion == "1.0.0"` и `policy_version == "0.1.0"`; иначе `unsupported_contract` без догадок. Ответ Bridge с узлом `api_limits` расширения читается (ресурсы и квоты разбираются), но сам узел игнорируется; это проверяет `test_api_limits_node_is_ignored_not_interpreted`.

## Mapping полей

| Источник в bridge-output | Поле адаптера |
|---|---|
| `subscription_quotas.windows[]` (provider_id, pool_id, window_id) | `QuotaObservation` с ключом-тройкой; display titles не используются |
| window freshness / reset_time_passed_unverified / remaining_percent | `availability`: `available` (fresh, reset не прошёл, процент не None), `reset_pending`, `stale`, иначе `unavailable` |
| `subscription_quotas` / `hardware_state` : status, reason, liveness, freshness, read_status | `quota_node`, `host_node` |
| `mode` | `mode`, `is_fixture` |
| `hardware_state.source.metrics.<id>.value` при `hardware_state.metrics.<id>.freshness == "fresh"` | `host.values[id]`: cpu_usage_percent, ram_total_bytes, ram_available_bytes, commit_headroom_bytes, commit_limit_bytes, vram_total_bytes, vram_available_bytes, gpu_usage_percent |
| иначе (stale/unknown/нет source) | `None`, причина в `host.unknown_reasons[id]` |
| `hardware_state.source.gpu_memory_kind` | `host.gpu_memory_kind` |
| `orchestration_hints.resource_pressure.{physical_memory,commit_memory,vram,cpu}.level` | `host.pressure` (+ `pressure_reasons`) |
| `orchestration_hints.data_quality` | `host.data_quality` |

Provider ids контракта: `codex`, `claude`, `antigravity`, `gemini`, `kimi`. Alias-таблица `PROVIDER_ALIASES` (`claude_code`->`claude`, `gemini_cli`->`gemini`, `kimi_code`->`kimi`) применяется только в `quota()`/`quotas_for()`; в данных всегда contract id.

## Честное unknown

- Неизвестное значение = `None`, никогда не 0; процент квоты не выдумывается и не переносится из stale-окна.
- `reset_pending`: время сброса прошло, но обновление не подтверждено; процента нет.
- `liveness == "silent"` у узла означает, что publisher молчит: `host.values` будут `None`, давление `unknown`. Решение принимает S.I.G.M.A. (например, не запускать тяжёлую работу), адаптер его не принимает.
- Уровень `unknown` давления не приравнивать к `normal`.

## Проверка

Из корня репозитория:

```
python -m unittest discover -s telemetry/integration/sigma-adapter -v
```

Фикстуры: `telemetry/fixtures/*/bridge-output.json`. Контракт: `telemetry/schema/bridge-output.schema.json`.

## Остаётся в S.I.G.M.A.

Планирование и очереди, выбор моделей и исполнителей, надзор и управление job/process, политика реакции на `pressure`/`availability`. Адаптер ничего из этого не затрагивает.
