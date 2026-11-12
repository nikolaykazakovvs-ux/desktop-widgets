# AI Telemetry Bridge: установка

Статус: **реализовано; проверено в изолированном каталоге; в рабочие клиенты не установлено.** Отдельной приёмки установки нет.

## Требования

- Windows 11, PowerShell 7 (`pwsh`), Node.js 20+ (проверено на 24).
- Исходники репозитория с `telemetry\bridge` (вместе с `node_modules`: `npm ci --ignore-scripts`; сеть нужна один раз, ключ `--offline` работает только при заполненном кэше npm), `telemetry\schema`, `telemetry\fixtures`.

## Установка

```powershell
pwsh telemetry\packaging\Install-Bridge.ps1 -SourceRoot <repo> [-InstallRoot <abs>]
```

По умолчанию `%LOCALAPPDATA%\DesktopWidgets\bridge`. Лаунчеры и модули исполняются от имени клиента оператора, поэтому `InstallRoot` должен быть закрыт для других учётных записей:

- новый каталог создаётся с защищённой DACL без наследования: полный доступ только у текущего пользователя, SYSTEM и Administrators (наследуется версиями внутри);
- существующий каталог принимается, только если его владелец и все разрешающие ACE принадлежат этим трём субъектам; иначе установка и откат завершаются ошибкой, ничего не меняя. DACL существующего каталога скрипты не переписывают и родительские каталоги не трогают: выбирайте корень в профиле пользователя, а не в общей папке диска, где наследуются права группы «Прошедшие проверку»;
- `Test-BridgeHealth.ps1` повторяет эту проверку.

Раскладка:

```
<InstallRoot>\versions\<версия>-<12 hex sha256>\{bridge,schema,fixtures,manifest.json}
<InstallRoot>\current.json          {current, previous, installed_at}
<InstallRoot>\bridge-server.cmd     лаунчер MCP (stdio)
<InstallRoot>\bridge-cli.cmd        лаунчер CLI
```

Версия собирается в `staging\<guid>` и переименовывается в `versions\<id>`; существующий каталог не перезаписывается (повторная установка того же содержимого идемпотентна после проверки manifest). `current.json` и лаунчеры переключаются атомарно (temp + replace).

## Регистрация в клиентах

Скрипты по умолчанию ничего не пишут в рабочие профили; перед записью делают резервную копию конфигурации (`*.bak-<время>`).

- **Codex**: `telemetry\clients\codex\register-codex.ps1` печатает TOML-фрагмент `[mcp_servers.ai_telemetry_bridge]`; запись только с `-CodexHome <abs>` (реальный `~\.codex` — только с `-ConfirmRealHome`). Шаблон: `config.toml.example`.
- **Claude Code**: `telemetry\clients\claude-code\register-claude.ps1 -ClaudeConfigDir <abs>` выполняет `claude mcp add-json --scope user ai_telemetry_bridge '<json>'` с `CLAUDE_CONFIG_DIR` только для дочернего процесса; `-PrintOnly` печатает команду. Шаблон: `mcp.json.example`.
- **Antigravity**: CLI `agy.exe mcp add [flags] <name> <commandOrUrl> [args...]` (флаги до имени). Для Bridge: `agy.exe mcp add ai_telemetry_bridge "<InstallRoot>\bridge-server.cmd"`. Помощник регистрации не написан; команда не выполнялась (проверен только `--help`).

Инструмент MCP: `get_telemetry_and_quotas`.

## Диагностика

```powershell
pwsh telemetry\packaging\Test-BridgeHealth.ps1 [-InstallRoot <abs>]
```

Проверяет владельца и DACL `InstallRoot`, формат идентификаторов в `current.json`, хеши manifest (в manifest входят все файлы версии, в том числе `bridge\node_modules`: изменённый, добавленный или пропавший файл зависимости даёт отказ; не входит только сам `manifest.json`), Node >= 20 и запускает `bridge-cli.cmd --fixture <версия>\fixtures\normal --as-of 2026-01-01T12:00:00Z` (ожидается `mode=fixture`, `schemaVersion=1.0.0`). Вывод — JSON, код 0/1. Код 2 у CLI означает ошибку конфигурации.

## Обновление при активном MCP

`Update-Bridge.ps1 -SourceRoot <новый источник>` (= Install). Новая версия получает новый каталог, предыдущая остаётся как `previous`. Уже запущенные MCP-процессы продолжают читать файлы своей (неизменяемой) версии; новый лаунчер указывает на новую. Чтобы клиент перешёл на новую версию, перезапустите его сессию. Старый каталог остаётся, пока не будет удалён вручную или при Uninstall.

## Откат

```powershell
pwsh telemetry\packaging\Install-Bridge.ps1 -Rollback -InstallRoot <abs>
```

Проверяет manifest предыдущей версии и переключает `current`/лаунчеры; `previous` становится прежней текущей.

## Удаление и deferred cleanup

```powershell
pwsh telemetry\packaging\Uninstall-Bridge.ps1 [-InstallRoot <abs>] [-KeepVersion <id>]
```

Удаляет лаунчеры, `current.json` и только настоящие версии Bridge: каталоги `versions\<semver>-<12 hex>` с `manifest.json`, чей `id` равен имени. Посторонние каталоги и файлы в `versions\` остаются и перечисляются в отчёте как `foreign`; если в корне нет ни `current.json`, ни лаунчеров, ни настоящей версии, Uninstall ничего не удаляет. Каталог, занятый процессом (IOException / UnauthorizedAccess), не форсируется и не затрагивается через остановку процессов: в отчёте он помечен `deferred`; повторите команду после закрытия клиента. Отказ для корня диска, относительного пути и путей с reparse point.

Важно: Windows не блокирует файлы JS-модулей Node после загрузки. В изолированном тесте автора (receipt жизненного цикла в репозиторий не включён) Uninstall при живом процессе `server.mjs` **удалил** каталог его версии без `deferred`. Защита от этого — не механизм блокировки, а порядок действий: закройте клиенты (Codex, Claude Code, Antigravity) до деинсталляции; `-KeepVersion <id>` оставляет нужную версию. Удаление каталога под работающим процессом не останавливает процесс, но его ленивые импорты и перечитывание схем могут завершиться ошибкой.

Если что-то частично удалилось, версия может не пройти проверку manifest; тогда выполните Uninstall повторно.

## Проверка самих скриптов упаковки

```powershell
pwsh telemetry\packaging\Test-InstallSafety.ps1 -WorkRoot <abs, каталог не должен существовать>
```

Изолированный прогон (около минуты; в `%LOCALAPPDATA%` и профили клиентов не пишет, каталог `WorkRoot` удаляется): защищённая DACL нового `InstallRoot`, отказ для каталога с правами «Все», обнаружение изменённого и добавленного файла в `node_modules`, отказ при недопустимых идентификаторах в `current.json`, Uninstall оставляет посторонние каталоги и не трогает чужой корень. Код возврата 0/1.

## Резервные копии конфигураций клиентов

`register-codex.ps1` и `register-claude.ps1` копируют существующий `config.toml` / `.claude.json` в `*.bak-<yyyyMMddHHmmss>` перед изменением. Удаление записи клиента при деинсталляции Bridge выполняется вручную (`claude mcp remove`, правка `config.toml`, `agy.exe mcp remove`).
