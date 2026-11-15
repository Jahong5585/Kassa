# Отчёт по двухуровневому входу владельца и сотрудников

Дата фиксации состояния: 15 ноября 2026 года  
Ветка: `feature/store-operator-auth`  
Коммит реализации: `0266366 feat: add store operator authentication`

Основной сценарий реализован в исходном коде и протестирован в изолированном окружении. В production изменения не развёртывались, миграция production PostgreSQL не применялась, feature flag не включался. Известные незакрытые пункты приведены в конце отчёта.

## Результат

API теперь разделяет две личности:

- OIDC-владелец является подтверждённым владельцем и хостом сессии;
- выбранный владелец или сотрудник является фактическим оператором бизнес-действий;
- сервер создаёт непрозрачную короткоживущую операторскую сессию;
- фактический `staff_id` для прав и аудита берётся только из проверенной операторской сессии;
- магазинный `admin` остаётся обычной ролью и не требует повышенного OIDC ACR;
- MFA, FIDO2, USB-проверки и защита platform API не ослаблены.

## Выполненные изменения

### PostgreSQL

Добавлена последовательная миграция `db/038_store_operator_auth.sql` и обратная миграция `db/rollback/038_store_operator_auth.down.sql`.

Миграция:

- расширяет `staff` полями имени, типа личности, версии и временных меток;
- разрешает `subject IS NULL` только локальным сотрудникам;
- создаёт частичный уникальный индекс по непустому `subject`;
- создаёт `staff_store_roles` и переносит роли в область конкретного магазина;
- выполняет backfill из `staff_stores` и `staff_roles`;
- сохраняет `staff_roles` и добавляет совместимые синхронизирующие триггеры;
- создаёт `employee_credentials` для Argon2id-хешей и блокировок;
- создаёт `operator_sessions`, где хранится только SHA-256 digest токена;
- создаёт таблицы ограничения попыток входа, идемпотентности и операторского аудита;
- не удаляет владельцев, сотрудников, роли, кассы, смены или продажи.

### Пароли и сессии

- Пароли хешируются Argon2id с индивидуальной 16-байтовой солью.
- Параметры хеширования: 64 MiB памяти, 3 итерации, параллелизм 1, digest 32 байта.
- Пароли принимаются длиной от 8 до 128 символов и не обрезаются через `Trim`.
- Операторский токен генерируется криптографическим RNG и возвращается только при создании сессии.
- В базе сохраняется только SHA-256 digest операторского токена.
- Idle timeout: 15 минут для оператора с ролью `admin`, 30 минут для остальных.
- Absolute timeout: 12 часов.
- Смена ролей, сброс пароля, отключение сотрудника и отзыв кассы отзывают связанные сессии.

### Авторизация

- Добавлена центральная проверка `X-Operator-Session` для бизнес-маршрутов.
- Проверяются Bearer, issuer/subject владельца, магазин, сотрудник, активность, сроки, отзыв и актуальные роли.
- `X-Active-Role` принимается только если роль назначена оператору в выбранном магазине.
- Подмена магазина возвращает ошибку без раскрытия данных другой организации.
- Кассовые операции дополнительно продолжают проверять `X-Register-Id` и `X-Register-Key`.
- Feature flag `Auth:RequireOperatorSession` позволяет сначала развернуть сервер в режиме совместимости.

### Сотрудники

- Реализованы создание, список, изменение профиля и ролей, сброс пароля, отключение и повторное включение.
- Управление разрешено только OIDC-владельцу с владельческой операторской сессией и активной ролью `admin`.
- Обычный сотрудник с ролью `admin`, но без `owner`, не может управлять сотрудниками.
- Создание сотрудника транзакционное и поддерживает `Idempotency-Key`.
- Роль `owner` локальному сотруднику запрещена.
- Используется optimistic concurrency через `expectedVersion`.

### Кассы

- Существующие маршруты касс сохранены и адаптированы к операторской сессии.
- Добавлено назначение нескольких кассиров на кассу.
- Владельца можно назначить кассиром.
- Register Key возвращается только при создании или ротации; сервер хранит только digest.

### Ошибки, аудит и OpenAPI

- Ошибки возвращаются в едином ProblemDetails с `type`, `title`, `status`, `code` и `traceId`.
- Ответ rate limit содержит `Retry-After`.
- Добавлен аудит входов, сессий и административных изменений без паролей и токенов.
- OpenAPI описывает Bearer, `X-Operator-Session`, `X-Active-Role`, кассовые заголовки, схемы, ограничения, роли и ошибки.
- Bootstrap-маршруты явно отделены от бизнес-маршрутов.

## Общие параметры запросов

Production URL:

```powershell
$Api = 'https://kassa.qr-space.uz'
$StoreId = '<store_uuid>'
$EmployeeId = '<employee_uuid>'
$DeviceId = '<device_uuid>'
$RegisterId = '<register_uuid>'
```

Только OIDC Bearer для bootstrap:

```powershell
$BootstrapHeaders = @{
    Authorization = 'Bearer <oidc_access_token>'
}
```

Владелец в роли администратора:

```powershell
$OwnerAdminHeaders = @{
    Authorization = 'Bearer <oidc_access_token>'
    'X-Operator-Session' = '<operator_session_token>'
    'X-Active-Role' = 'admin'
}
```

Кассир с подключённой кассой:

```powershell
$CashierHeaders = @{
    Authorization = 'Bearer <oidc_access_token>'
    'X-Operator-Session' = '<operator_session_token>'
    'X-Active-Role' = 'cashier'
    'X-Register-Id' = $RegisterId
    'X-Register-Key' = '<register_key>'
}
```

Все значения в угловых скобках являются плейсхолдерами. Реальные пароли, Bearer-токены, операторские токены и Register Key нельзя сохранять в Git, командной истории или отчётах.

## Bootstrap API

### Получить магазины текущего OIDC-владельца

`GET /v1/me/stores`

```powershell
$Stores = Invoke-RestMethod `
    -Method Get `
    -Uri "$Api/v1/me/stores" `
    -Headers $BootstrapHeaders
```

Возвращаются только магазины текущего OIDC subject. Если доступа нет, ответ содержит пустой `items`.

### Получить операторов выбранного магазина

`GET /v1/stores/{storeId}/operators`

```powershell
$Operators = Invoke-RestMethod `
    -Method Get `
    -Uri "$Api/v1/stores/$StoreId/operators" `
    -Headers $BootstrapHeaders
```

Маршрут доступен только OIDC-владельцу выбранного магазина. Subject, хеши, счётчики неудачных входов и токены в ответ не попадают.

### Создать сессию владельца

`POST /v1/stores/{storeId}/operator-sessions`

```powershell
$OwnerLoginBody = @{
    operatorId = '<owner_staff_uuid>'
    password = $null
    deviceId = $DeviceId
} | ConvertTo-Json

$OwnerSession = Invoke-RestMethod `
    -Method Post `
    -Uri "$Api/v1/stores/$StoreId/operator-sessions" `
    -Headers $BootstrapHeaders `
    -ContentType 'application/json' `
    -Body $OwnerLoginBody
```

Владелец не вводит локальный пароль и получает роли `admin`, `cashier`, `receiver`.

### Создать сессию сотрудника

`POST /v1/stores/{storeId}/operator-sessions`

```powershell
$EmployeeLoginBody = @{
    operatorId = $EmployeeId
    password = '<employee_password>'
    deviceId = $DeviceId
} | ConvertTo-Json

$EmployeeSession = Invoke-RestMethod `
    -Method Post `
    -Uri "$Api/v1/stores/$StoreId/operator-sessions" `
    -Headers $BootstrapHeaders `
    -ContentType 'application/json' `
    -Body $EmployeeLoginBody
```

При неверных данных возвращается общий `operator_credentials_invalid`. При временной блокировке возвращается `429 operator_login_rate_limited` и заголовок `Retry-After`.

## API операторской сессии

### Получить текущую сессию

`GET /v1/operator-sessions/current`

```powershell
$CurrentSession = Invoke-RestMethod `
    -Method Get `
    -Uri "$Api/v1/operator-sessions/current" `
    -Headers $OwnerAdminHeaders
```

Ответ содержит session ID, магазин, фактического оператора, актуальные роли, сроки и `registerId`, если касса уже привязана.

### Завершить текущую сессию

`DELETE /v1/operator-sessions/current`

```powershell
Invoke-RestMethod `
    -Method Delete `
    -Uri "$Api/v1/operator-sessions/current" `
    -Headers $OwnerAdminHeaders
```

После ответа `204 No Content` токен больше не принимается.

### Сменить собственный пароль сотрудника

`POST /v1/operator-sessions/current/change-password`

```powershell
$ChangePasswordBody = @{
    currentPassword = '<current_employee_password>'
    newPassword = '<new_employee_password>'
} | ConvertTo-Json

Invoke-RestMethod `
    -Method Post `
    -Uri "$Api/v1/operator-sessions/current/change-password" `
    -Headers $CashierHeaders `
    -ContentType 'application/json' `
    -Body $ChangePasswordBody
```

Текущая сессия сохраняется, остальные сессии сотрудника отзываются.

## API управления сотрудниками

Для всех запросов этого раздела требуются Bearer владельца, владельческая операторская сессия и `X-Active-Role: admin`.

### Получить сотрудников

`GET /v1/stores/{storeId}/employees?offset=0&limit=50`

```powershell
$Employees = Invoke-RestMethod `
    -Method Get `
    -Uri "$Api/v1/stores/$StoreId/employees?offset=0&limit=50" `
    -Headers $OwnerAdminHeaders
```

Владелец не включается в список редактируемых сотрудников.

### Создать сотрудника

`POST /v1/stores/{storeId}/employees`

```powershell
$CreateEmployeeHeaders = @{
    Authorization = 'Bearer <oidc_access_token>'
    'X-Operator-Session' = '<operator_session_token>'
    'X-Active-Role' = 'admin'
    'Idempotency-Key' = ([guid]::NewGuid().ToString())
}

$CreateEmployeeBody = @{
    employeeId = $EmployeeId
    firstName = 'Саида'
    lastName = 'Каримова'
    roles = @('cashier', 'receiver')
    password = '<employee_password>'
    reason = 'Создание сотрудника магазина'
} | ConvertTo-Json

$Employee = Invoke-RestMethod `
    -Method Post `
    -Uri "$Api/v1/stores/$StoreId/employees" `
    -Headers $CreateEmployeeHeaders `
    -ContentType 'application/json' `
    -Body $CreateEmployeeBody
```

Разрешены от одной до трёх уникальных ролей из `admin`, `cashier`, `receiver`. Повтор того же тела с тем же ключом возвращает прежний результат; другое тело с тем же ключом возвращает `409 idempotency_conflict`.

### Изменить имя и фамилию

`PUT /v1/stores/{storeId}/employees/{employeeId}`

```powershell
$UpdateProfileBody = @{
    expectedVersion = 1
    firstName = 'Новое имя'
    lastName = 'Новая фамилия'
    reason = 'Исправление имени сотрудника'
} | ConvertTo-Json

$UpdatedEmployee = Invoke-RestMethod `
    -Method Put `
    -Uri "$Api/v1/stores/$StoreId/employees/$EmployeeId" `
    -Headers $OwnerAdminHeaders `
    -ContentType 'application/json' `
    -Body $UpdateProfileBody
```

### Изменить роли

`PUT /v1/stores/{storeId}/employees/{employeeId}/roles`

```powershell
$UpdateRolesBody = @{
    expectedVersion = 1
    roles = @('cashier', 'receiver')
    reason = 'Изменение обязанностей сотрудника'
} | ConvertTo-Json

$UpdatedEmployee = Invoke-RestMethod `
    -Method Put `
    -Uri "$Api/v1/stores/$StoreId/employees/$EmployeeId/roles" `
    -Headers $OwnerAdminHeaders `
    -ContentType 'application/json' `
    -Body $UpdateRolesBody
```

После изменения ролей версия увеличивается, а действующие сессии сотрудника отзываются.

### Сбросить пароль сотрудника

`POST /v1/stores/{storeId}/employees/{employeeId}/reset-password`

```powershell
$ResetPasswordBody = @{
    expectedVersion = 2
    newPassword = '<new_employee_password>'
    reason = 'Сброс пароля по обращению сотрудника'
} | ConvertTo-Json

$UpdatedEmployee = Invoke-RestMethod `
    -Method Post `
    -Uri "$Api/v1/stores/$StoreId/employees/$EmployeeId/reset-password" `
    -Headers $OwnerAdminHeaders `
    -ContentType 'application/json' `
    -Body $ResetPasswordBody
```

Блокировка и счётчик ошибок снимаются, все сессии сотрудника отзываются, пароль в ответ не возвращается.

### Отключить сотрудника

`POST /v1/stores/{storeId}/employees/{employeeId}/disable`

```powershell
$DisableBody = @{
    expectedVersion = 3
    reason = 'Сотрудник больше не работает'
} | ConvertTo-Json

$DisabledEmployee = Invoke-RestMethod `
    -Method Post `
    -Uri "$Api/v1/stores/$StoreId/employees/$EmployeeId/disable" `
    -Headers $OwnerAdminHeaders `
    -ContentType 'application/json' `
    -Body $DisableBody
```

Новые входы запрещаются, активные сессии отзываются, исторические продажи, смены и аудит сохраняются.

### Повторно включить сотрудника

`POST /v1/stores/{storeId}/employees/{employeeId}/enable`

```powershell
$EnableBody = @{
    expectedVersion = 4
    reason = 'Восстановление доступа сотрудника'
} | ConvertTo-Json

$EnabledEmployee = Invoke-RestMethod `
    -Method Post `
    -Uri "$Api/v1/stores/$StoreId/employees/$EmployeeId/enable" `
    -Headers $OwnerAdminHeaders `
    -ContentType 'application/json' `
    -Body $EnableBody
```

Старые операторские сессии не восстанавливаются.

## API кассовых устройств

Сохранены маршруты:

- `GET /v1/stores/{storeId}/registers`
- `POST /v1/stores/{storeId}/registers`
- `GET /v1/stores/{storeId}/registers/{registerId}`
- `POST /v1/stores/{storeId}/registers/{registerId}/rotate-key`
- `POST /v1/stores/{storeId}/registers/{registerId}/revoke`

Пример получения касс:

```powershell
$Registers = Invoke-RestMethod `
    -Method Get `
    -Uri "$Api/v1/stores/$StoreId/registers?offset=0" `
    -Headers $OwnerAdminHeaders
```

Пример создания кассы:

```powershell
$CreateRegisterBody = @{
    registerId = $RegisterId
    name = 'Касса 1'
    cashierId = '<owner_staff_uuid>'
    registerKey = '<new_register_key>'
} | ConvertTo-Json

$Register = Invoke-RestMethod `
    -Method Post `
    -Uri "$Api/v1/stores/$StoreId/registers" `
    -Headers $OwnerAdminHeaders `
    -ContentType 'application/json' `
    -Body $CreateRegisterBody
```

Пример получения одной кассы:

```powershell
$Register = Invoke-RestMethod `
    -Method Get `
    -Uri "$Api/v1/stores/$StoreId/registers/$RegisterId" `
    -Headers $OwnerAdminHeaders
```

Пример ротации ключа:

```powershell
$RotateKeyBody = @{
    expectedVersion = 1
    registerKey = '<new_register_key>'
} | ConvertTo-Json

$Register = Invoke-RestMethod `
    -Method Post `
    -Uri "$Api/v1/stores/$StoreId/registers/$RegisterId/rotate-key" `
    -Headers $OwnerAdminHeaders `
    -ContentType 'application/json' `
    -Body $RotateKeyBody
```

Пример отзыва кассы:

```powershell
$RevokeRegisterBody = @{
    expectedVersion = 2
} | ConvertTo-Json

$Register = Invoke-RestMethod `
    -Method Post `
    -Uri "$Api/v1/stores/$StoreId/registers/$RegisterId/revoke" `
    -Headers $OwnerAdminHeaders `
    -ContentType 'application/json' `
    -Body $RevokeRegisterBody
```

### Назначить нескольких кассиров

`PUT /v1/stores/{storeId}/registers/{registerId}/cashiers`

```powershell
$CashiersBody = @{
    expectedVersion = 1
    cashierIds = @($EmployeeId, '<owner_staff_uuid>')
    reason = 'Назначение кассиров'
} | ConvertTo-Json

$Register = Invoke-RestMethod `
    -Method Put `
    -Uri "$Api/v1/stores/$StoreId/registers/$RegisterId/cashiers" `
    -Headers $OwnerAdminHeaders `
    -ContentType 'application/json' `
    -Body $CashiersBody
```

### Пример кассовой бизнес-операции

`POST /v1/shifts`

```powershell
$OpenShiftBody = @{
    shiftId = ([guid]::NewGuid().ToString())
    openingCash = 1000
} | ConvertTo-Json

$Shift = Invoke-RestMethod `
    -Method Post `
    -Uri "$Api/v1/shifts" `
    -Headers $CashierHeaders `
    -ContentType 'application/json' `
    -Body $OpenShiftBody
```

В аудит смены записывается сотрудник из проверенной операторской сессии, а не переданное клиентом имя или ID.

## OpenAPI

Актуальная схема после развёртывания соответствующей версии API:

```powershell
$OpenApi = Invoke-RestMethod `
    -Method Get `
    -Uri "$Api/openapi/v1.json"
```

До production-развёртывания URL продолжает показывать схему текущей production-версии, а не изменения этой ветки.

## Формат ошибок

Пример безопасного ответа:

```json
{
  "type": "https://kassa.qr-space.uz/problems/operator-credentials-invalid",
  "title": "Ошибка входа",
  "status": 401,
  "code": "operator_credentials_invalid",
  "traceId": "<trace_id>"
}
```

Коды, используемые реализацией нового сценария:

- `400`: `validation_failed`, `invalid_employee_name`, `invalid_employee_roles`, `invalid_employee_password`, `active_role_invalid`;
- `401`: `oidc_session_expired`, `operator_credentials_invalid`, `operator_session_invalid`, `operator_session_expired`;
- `403`: `store_access_denied`, `owner_access_required`, `operator_session_required`, `active_role_required`, `active_role_denied`, `register_access_denied`;
- `404`: `store_not_found`, `employee_not_found`, `register_not_found`;
- `409`: `employee_version_conflict`, `employee_already_exists`, `idempotency_conflict`, `operator_store_mismatch`;
- `429`: `operator_login_rate_limited` с `Retry-After`;
- `503`: `password_service_unavailable`.

## Аудит

Поддержаны события:

- `oidc.owner_login_succeeded`;
- `operator.login_succeeded`;
- `operator.login_failed`;
- `operator.session_revoked`;
- `operator.logout`;
- `employee.created`;
- `employee.profile_updated`;
- `employee.roles_updated`;
- `employee.password_reset`;
- `employee.disabled`;
- `employee.enabled`;
- `register.cashiers_updated`.

Пароли, хеши, Bearer-токены, операторские токены, Register Key и полный Authorization header в аудит и application logs не записываются.

## Проверки

Выполнены и прошли:

- unit-тесты Argon2id и индивидуальной соли — 3 из 3;
- изолированный integration/security suite для PostgreSQL и API;
- проверка bootstrap-изоляции нескольких магазинов и организаций;
- вход владельца без пароля и сотрудника с паролем;
- одна и несколько ролей, запрет `owner`;
- идемпотентность создания сотрудника;
- подмена роли и магазина;
- отзыв сессий после изменения ролей, пароля и отключения;
- истечение сессии;
- rate limit и `Retry-After`;
- назначение кассиров и аудит фактического сотрудника;
- отсутствие тестового пароля в логах и аудите;
- применение и откат миграции 038;
- сборка API и проверка отсутствия случайно добавленных секретов.

Команды повторной проверки:

```powershell
dotnet test tests/Kasa.Api.Tests/Kasa.Api.Tests.csproj -c Release
powershell -File tests/run_operator_auth.ps1
powershell -File tests/test_operator_migration_rollback.ps1
```

Полный legacy-набор `bash tests/run.sh` должен быть дополнительно выполнен на Linux-хосте с Bash, Docker, Python 3 и .NET 10.

## Порядок развёртывания

1. Создать custom-format backup PostgreSQL и сохранить checksum.
2. Восстановить backup в отдельную PostgreSQL 17 и проверить контрольные запросы.
3. Применить `db/038_store_operator_auth.sql`.
4. Развернуть API с `Auth:RequireOperatorSession=false`.
5. Выполнить smoke-тест старого Windows-клиента.
6. Выпустить новый клиент и проверить вход владельца и сотрудника на тестовом магазине.
7. Включить `Auth:RequireOperatorSession=true`.
8. Выполнить bootstrap, role, register, shift и checkout smoke-тесты.

Для отката сначала выключается feature flag и возвращается предыдущая совместимая версия API. Additive-миграцию рекомендуется оставить. Down-скрипт допускается только после новой резервной копии и если данные новых локальных сотрудников и операторских сессий не требуется сохранять.

## Незакрытые production-шаги

- production backup и проверка восстановления;
- применение миграции production PostgreSQL;
- развёртывание новой версии API;
- smoke-тест старого и нового Windows-клиента;
- включение `Auth:RequireOperatorSession=true`;
- production smoke-тест и фиксация процедуры отката.

Перед включением feature flag также требуется закрыть три расхождения с полным техническим заданием:

- добавить отдельное событие `oidc.owner_login_failed` там, где доступен безопасный контекст владельца и магазина;
- добавить явный `409 employee_disabled` для подходящих административных конфликтов, не раскрывая состояние сотрудника при входе;
- сопоставить недоступность внешнего OIDC/identity provider с `503 identity_service_unavailable` там, где запрос уже принят API.
