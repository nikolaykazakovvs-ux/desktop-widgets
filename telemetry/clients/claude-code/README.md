# Claude Code: подключение Моста виджетов (widgets-bridge)

1. Установите Bridge: `telemetry\packaging\Install-Bridge.ps1 -SourceRoot <repo>`.
2. Просмотр команды: `.\register-claude.ps1 -PrintOnly`.
3. Регистрация в явном каталоге: `.\register-claude.ps1 -ClaudeConfigDir <abs-dir>` (резервная копия `.claude.json.bak-*`).
   Для реального профиля нужен `-ConfirmRealHome`.
4. Перезапустите сессию Claude Code, чтобы подхватить новую версию.

Шаблон: `mcp.json.example`. Статус: проверено только в изолированном каталоге; в рабочий профиль не установлено.

Основной путь — автоматический: `telemetry\packaging\Register-Clients.ps1` (или `Install-Bridge.ps1 -RegisterClients`) идемпотентно прописывает запись `widgets-bridge` в `~\.claude.json`; этот помощник — ручной вариант через `claude mcp add-json`.
