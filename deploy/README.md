# Развёртывание на Windows

Рабочая конфигурация использует Docker Compose и хранит состояние в именованных томах. Исходники и скрипты находятся в `D:\CodexProjects\Kassa`, секреты — в `D:\KassaSecrets`, резервные копии — в `D:\KassaBackups`. Файлы секретов не входят в Git.

## Управление

```powershell
cd D:\CodexProjects\Kassa
.\deploy\Manage-Kassa.ps1 -Action start
.\deploy\Manage-Kassa.ps1 -Action status
.\deploy\Manage-Kassa.ps1 -Action logs
.\deploy\Manage-Kassa.ps1 -Action backup
.\deploy\Manage-Kassa.ps1 -Action restart
.\deploy\Manage-Kassa.ps1 -Action stop
```

`start` и `restart` публикуют API локальным .NET 10 SDK из `D:\CodexTools\dotnet`, собирают runtime-образ и запускают стек. Если Docker Desktop ещё не готов, скрипт запускает `D:\Docker\Desktop\Docker Desktop.exe` и ожидает демон до пяти минут.

Задачу входа в Windows устанавливает `deploy\Install-KassaAutostart.ps1`. Контейнеры имеют `restart: unless-stopped`, а служба Cloudflared должна иметь состояния `Running` и `Automatic`.

## Сеть и OIDC

- API и Keycloak: `https://kassa.qr-space.uz/`
- локальный origin Cloudflare Tunnel: `http://localhost:3500`
- Path в опубликованном приложении: пустой
- PostgreSQL: только `127.0.0.1:5432`
- OIDC issuer: `https://kassa.qr-space.uz/auth/realms/kasa`
- audience API: `kasa-api`
- срок access token: 300 секунд; API отклоняет токены дольше 600 секунд

Первый вход `kasa-admin` выполняется на `https://kassa.qr-space.uz/auth/realms/kasa/account/`. Начальный пароль берётся из `KASA_ADMIN_INITIAL_PASSWORD` в `D:\KassaSecrets\.env`; при входе обязательны смена пароля и настройка TOTP. Аналогичные обязательные действия заданы для `master-admin` консоли Keycloak, пароль находится в `KEYCLOAK_BOOTSTRAP_ADMIN_PASSWORD`.

## Проверка

```powershell
Invoke-WebRequest -UseBasicParsing https://kassa.qr-space.uz/health/live
.\deploy\Manage-Kassa.ps1 -Action status
Get-NetTCPConnection -State Listen -LocalPort 3500,5432
```

Защищённую проверку выполняйте OIDC-токеном: без `Authorization` маршрут `/openapi/v1.json` возвращает `401`, с токеном аудитории `kasa-api` — `200`. Не вставляйте токены или значения `.env` в журнал и командную историю.
