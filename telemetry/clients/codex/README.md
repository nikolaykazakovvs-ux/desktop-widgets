# Codex: подключение Моста виджетов (widgets-bridge)

1. Установите Bridge: `telemetry\packaging\Install-Bridge.ps1 -SourceRoot <repo>`.
2. Напечатать фрагмент: `.\register-codex.ps1` и вручную добавить в `config.toml` Codex.
3. Или записать в явный каталог: `.\register-codex.ps1 -CodexHome <abs-dir>` (создаётся резервная копия `config.toml.bak-*`).
   Реальный `%USERPROFILE%\.codex` скрипт не трогает без `-ConfirmRealHome`.
4. Перезапустите сессию Codex: лаунчер указывает на текущую версию на момент старта процесса.

Шаблон: `config.toml.example`. Статус: проверено только в изолированном каталоге.

Основной путь — автоматический: `telemetry\packaging\Register-Clients.ps1` (или `Install-Bridge.ps1 -RegisterClients`) идемпотентно прописывает блок `[mcp_servers.widgets-bridge]` в `config.toml`; этот помощник — ручной вариант.
