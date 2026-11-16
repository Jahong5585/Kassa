# Касса Минимаркет — совместимость сервера с Windows Desktop 0.8.5

Дата проверки: 16 ноября 2026 года. Ветка: `feature/desktop-0.8.5-server-compat`.

Документ фиксирует выполненные серверные изменения, проверенный HTTP-контракт, результаты тестов и безопасный порядок выпуска. Значения токенов, паролей и ключей касс ниже являются только заполнителями.

## 1. Установленная версия рабочего API

- `GET https://kassa.qr-space.uz/health/live` отвечает `200 OK`.
- `GET https://kassa.qr-space.uz/openapi/v1.json` без OIDC Bearer отвечает `401`, поэтому публично сравнить рабочую схему OpenAPI невозможно.
- Проверен образ работающего контейнера API и соответствующая ему локальная копия исходников. Рабочий сервер собран из более ранней версии, в которой отсутствует `OperatorService` и маршруты операторского входа.
- Исправления из этой ветки на рабочий сервер не развёртывались. До выпуска Desktop 0.8.5 не сможет пройти новый сценарий на `https://kassa.qr-space.uz`.
- Приложенный исходный код Desktop 0.8.5 получен из `build/source.zip.part.00` ветки `origin/codex/desktop-completion-0.8.5` и сверён с `OperatorAuthorization.cs`, `EmployeeWorkspace.cs`, `Program.cs`, `ManagementWorkspace.cs` и `web/app.js`. Отдельный файл `Kassa_Project_0.8.5_HANDOFF.md` в доступных вложениях и ветках не найден.

## 2. Выполненные изменения

1. Сохранён новый двухуровневый вход: OIDC-владелец выбирает магазин и фактического оператора, после чего сервер выдаёт короткоживущий непрозрачный операторский токен.
2. Проверены JSON-поля, HTTP-методы и заголовки, которые отправляет Desktop 0.8.5.
3. Исправлена совместимость старого маршрута `GET /v1/stores/{storeId}/staff`: локальные сотрудники с `subject IS NULL` теперь возвращаются с безопасной подписью из `display_name`. Искусственный OIDC subject в базу не записывается.
4. Исправлено добавление старого назначения сотрудника к магазину. После миграции 038 уникальность `staff.subject` обеспечивается частичным индексом, поэтому старый `ON CONFLICT (subject)` вызывал PostgreSQL `42P10`. Используется совместимый `ON CONFLICT DO NOTHING`.
5. В production-compose добавлен управляемый параметр `KASA_REQUIRE_OPERATOR_SESSION`, по умолчанию `false`. Это позволяет сначала выпустить совместимый API и только после обновления клиентов включить обязательную операторскую сессию.
6. Удалены устаревшие тестовые ожидания, требовавшие повышенный OIDC ACR от обычного администратора магазина. Защита `/v1/platform/*`, MFA, FIDO2/WebAuthn и аппаратного ключа платформенных пользователей не изменена.
7. Расширен PostgreSQL integration-сценарий: сотрудник с ролями `cashier` и `receiver`, назначенная и неназначенная касса, открытие смены, выход, повторный вход, отзыв сессий после изменения ролей и отказ повторного использования отозванного токена.

Новая миграция не потребовалась: необходимые таблицы, индексы и обратимый переход уже находятся в `db/038_store_operator_auth.sql`; откат — `db/rollback/038_store_operator_auth.down.sql`.

## 3. Проверенный API-контракт

Во всех примерах:

```text
Authorization: Bearer <oidc_access_token>
```

### 3.1 Список магазинов владельца

```http
GET /v1/me/stores HTTP/1.1
Host: kassa.qr-space.uz
Authorization: Bearer <oidc_access_token>
Accept: application/json
```

```json
{
  "items": [
    {
      "organizationId": "<organization_uuid>",
      "organizationName": "Организация",
      "storeId": "<store_uuid>",
      "storeName": "Магазин",
      "timeZone": "Asia/Tashkent",
      "isOwner": true,
      "active": true
    }
  ]
}
```

### 3.2 Операторы выбранного магазина

```http
GET /v1/stores/<store_uuid>/operators HTTP/1.1
Host: kassa.qr-space.uz
Authorization: Bearer <oidc_access_token>
Accept: application/json
```

Ответ содержит владельца и только активных сотрудников выбранного магазина. `subject`, хеш пароля, число ошибок входа и внутренние токены не возвращаются.

### 3.3 Вход владельца

```http
POST /v1/stores/<store_uuid>/operator-sessions HTTP/1.1
Host: kassa.qr-space.uz
Authorization: Bearer <oidc_access_token>
Content-Type: application/json

{
  "operatorId": "<owner_staff_uuid>",
  "password": null,
  "deviceId": "<desktop_installation_uuid>"
}
```

Владелец входит без второго локального пароля и получает роли `admin`, `cashier`, `receiver`.

### 3.4 Вход сотрудника

```http
POST /v1/stores/<store_uuid>/operator-sessions HTTP/1.1
Host: kassa.qr-space.uz
Authorization: Bearer <owner_oidc_access_token>
Content-Type: application/json

{
  "operatorId": "<employee_uuid>",
  "password": "<employee_password>",
  "deviceId": "<desktop_installation_uuid>"
}
```

Успешный ответ содержит `sessionId`, одноразово возвращаемый `operatorSessionToken`, `organizationId`, `storeId`, объект `operator`, `idleExpiresAt` и `absoluteExpiresAt`. Сервер хранит только SHA-256 digest токена.

### 3.5 Создание сотрудника

```http
POST /v1/stores/<store_uuid>/employees HTTP/1.1
Host: kassa.qr-space.uz
Authorization: Bearer <oidc_access_token>
X-Operator-Session: <owner_operator_session_token>
X-Active-Role: admin
Idempotency-Key: <request_uuid>
Content-Type: application/json

{
  "employeeId": "<new_employee_uuid>",
  "firstName": "Саида",
  "lastName": "Каримова",
  "roles": ["cashier", "receiver"],
  "password": "<new_employee_password>",
  "reason": "Создание сотрудника магазина"
}
```

Повтор с тем же ключом и тем же телом возвращает прежний результат. Другое тело с тем же ключом получает `409 idempotency_conflict`.

### 3.6 Изменения сотрудника

Desktop 0.8.5 использует следующие операции с `expectedVersion`:

```text
GET  /v1/stores/{storeId}/employees?offset=0&limit=50
PUT  /v1/stores/{storeId}/employees/{employeeId}
PUT  /v1/stores/{storeId}/employees/{employeeId}/roles
POST /v1/stores/{storeId}/employees/{employeeId}/reset-password
POST /v1/stores/{storeId}/employees/{employeeId}/disable
POST /v1/stores/{storeId}/employees/{employeeId}/enable
```

Пример смены ролей:

```http
PUT /v1/stores/<store_uuid>/employees/<employee_uuid>/roles HTTP/1.1
Authorization: Bearer <oidc_access_token>
X-Operator-Session: <owner_operator_session_token>
X-Active-Role: admin
Content-Type: application/json

{
  "expectedVersion": 1,
  "roles": ["cashier", "receiver"],
  "reason": "Изменение обязанностей сотрудника"
}
```

Смена ролей, сброс пароля и отключение немедленно отзывают прежние операторские сессии сотрудника.

### 3.7 Касса и открытие смены

Сначала владелец создаёт кассу и назначает кассиров. Register Key возвращается только при создании или ротации.

```http
PUT /v1/stores/<store_uuid>/registers/<register_uuid>/cashiers HTTP/1.1
Authorization: Bearer <oidc_access_token>
X-Operator-Session: <owner_operator_session_token>
X-Active-Role: admin
Content-Type: application/json

{
  "expectedVersion": 1,
  "cashierIds": ["<employee_uuid>"],
  "reason": "Назначение кассира"
}
```

Операционный запрос сотрудника:

```http
POST /v1/stores/<store_uuid>/shifts/open HTTP/1.1
Authorization: Bearer <oidc_access_token>
X-Operator-Session: <employee_operator_session_token>
X-Active-Role: cashier
X-Register-Id: <register_uuid>
X-Register-Key: <register_key>
Content-Type: application/json

{
  "openingCash": 0
}
```

Фактический `staff_id` для операции и аудита берётся только из проверенной операторской сессии. Неназначенная касса получает `403 register_access_denied`.

### 3.8 Выход и проверка отзыва

```http
DELETE /v1/operator-sessions/current HTTP/1.1
Authorization: Bearer <oidc_access_token>
X-Operator-Session: <operator_session_token>
X-Active-Role: cashier
```

Повторное использование этого токена отклоняется как `operator_session_invalid`.

## 4. Проверки

В локальной изолированной среде с PostgreSQL 17 пройдены:

- сборка API Release: 0 предупреждений, 0 ошибок;
- unit-тесты: 3 из 3;
- полный старый серверный smoke-набор, включая Android/web-совместимые маршруты;
- новый integration/security-набор операторской авторизации;
- применение и обратный откат миграции 038 на базе с существующей схемой;
- владелец → магазин → сотрудник с двумя ролями → касса → назначение → вход сотрудника → роль cashier → открытие смены;
- неверный пароль и `Retry-After`, чужой магазин, неназначенная роль, неназначенная касса, истёкшая/отозванная сессия;
- фактический сотрудник в аудите, Argon2id с индивидуальной солью, отсутствие тестового пароля в логах и аудите;
- Desktop 0.8.5: Release-сборка .NET Framework без предупреждений и ошибок;
- browser/module-тесты Desktop 0.8.5. Они используют подставные ответы и учитываются только как проверка интерфейса, не как серверный E2E.

Нативный Desktop с рабочим OIDC нельзя считать сквозно проверенным до развёртывания этой ветки на тестовом или рабочем API. Автоматизация не вводила учётные данные и не обходила интерактивную аутентификацию.

Команды локальной проверки:

```powershell
D:\CodexTools\dotnet\dotnet.exe test .\tests\Kasa.Api.Tests\Kasa.Api.Tests.csproj -c Release --no-restore
pwsh -NoProfile -File .\tests\run_operator_auth.ps1
pwsh -NoProfile -File .\tests\test_operator_migration_rollback.ps1
& 'C:\Program Files\Git\bin\bash.exe' .\tests\run.sh
```

## 5. Выпуск

1. Создать резервную копию:

   ```powershell
   pwsh -NoProfile -File .\deploy\Manage-Kassa.ps1 -Action backup -SecretsFile '<protected-env-file>'
   ```

2. Проверить восстановление `kasa.dump` в отдельном PostgreSQL 17 и контрольные количества организаций, магазинов, сотрудников, ролей, касс, смен и продаж.
3. В защищённом env-файле задать `KASA_REQUIRE_OPERATOR_SESSION=false`.
4. Развернуть сервер, включая миграцию 038:

   ```powershell
   pwsh -NoProfile -File .\deploy\Manage-Kassa.ps1 -Action restart -SecretsFile '<protected-env-file>'
   ```

5. Проверить старый клиент и bootstrap/operator API новым клиентом.
6. Выпустить Desktop 0.8.5 и пройти сценарий на тестовом магазине.
7. Изменить только защищённый env-файл: `KASA_REQUIRE_OPERATOR_SESSION=true`; повторить `restart`.
8. Выполнить health, OpenAPI, OIDC, operator-session, register и shift smoke-проверки.

Нельзя включать обязательную операторскую сессию, пока все поддерживаемые Android-, web- и Windows-клиенты не отправляют `X-Operator-Session` и `X-Active-Role` на бизнес-маршрутах.

## 6. Откат

1. Вернуть `KASA_REQUIRE_OPERATOR_SESSION=false` и перезапустить API.
2. Вернуть предыдущий проверенный образ/коммит API.
3. Миграцию 038 предпочтительно оставить: она аддитивна, сохраняет старые данные и совместимые маршруты.
4. Если откат схемы обязателен и новые операторские данные не нужно сохранять, после новой резервной копии выполнить `db/rollback/038_store_operator_auth.down.sql` в транзакции.
5. Если новые данные нужно сохранить, восстановить проверенную резервную копию вместо принудительного down-скрипта.

## 7. Открытые производственные условия

- Исправленная ветка ещё не развёрнута на `kassa.qr-space.uz`.
- Рабочий OpenAPI защищён Bearer-токеном и не был прочитан анонимно.
- Реальный OIDC-вход и нативный E2E Desktop 0.8.5 нужно выполнить после тестового развёртывания; browser mock-тесты его не заменяют.
- Все секреты должны оставаться только в защищённом env-файле/DPAPI. Не добавлять Bearer, Client Secret, пароли, operator token или Register Key в Git, логи и отчёты.
