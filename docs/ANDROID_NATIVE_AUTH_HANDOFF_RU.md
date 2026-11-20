# Android native auth: контракт, внедрение и проверка

Дата проверки: 20 ноября 2026 года. Ветка: `codex/android-native-auth`.

## Результат

Android может выполнять первый вход, обновление токенов, выход, смену сотрудника и повторное подтверждение владельца внутри приложения. Keycloak Password Grant остаётся выключенным. Desktop и веб продолжают использовать прежний OIDC Authorization Code + PKCE и прежние маршруты, заголовки и ответы.

Мобильные учётные данные привязаны к существующему `staff_id`. Пароли хранятся только как Argon2id-хеши с индивидуальной солью. Access/refresh/operator/device токены хранятся только как SHA-256 digest. Пароли и токены не записываются в аудит и application logs.

Сроки:

- mobile access token: 10 минут;
- mobile refresh token: 30 дней, с ротацией при каждом обновлении;
- подтверждение роли владельца: 30 минут без успешного owner/admin-действия;
- успешная работа владельца в роли admin сдвигает срок ещё на 30 минут;
- refresh, восстановление сессии и фоновые запросы не сдвигают срок;
- операторская сессия: 15 минут бездействия для admin, 30 минут для cashier/receiver, максимум 12 часов.

Все запросы выполняются только по HTTPS. Значения в примерах являются тестовыми заполнителями.

## Подключение существующего владельца

Первичная установка мобильного логина выполняется один раз после подтверждённого OIDC-входа владельца. Это не включает Password Grant и не изменяет настройки существующих OIDC-клиентов.

```http
POST /v1/mobile-auth/credentials
Authorization: Bearer <oidc_access_token>
Content-Type: application/json

{
  "login": "owner.example",
  "password": "<новый-мобильный-пароль>",
  "deviceId": "11111111-1111-1111-1111-111111111111"
}
```

```json
{
  "staffId": "22222222-2222-2222-2222-222222222222",
  "login": "owner.example",
  "mobileEnabled": true,
  "version": 1
}
```

Только активный OIDC-владелец может создать собственные mobile credentials. Повторная настройка меняет пароль и отзывает прежние mobile-сессии.

## Вход

```http
POST /v1/mobile-auth/login
Content-Type: application/json

{
  "login": "owner.example",
  "password": "<пароль>",
  "deviceId": "11111111-1111-1111-1111-111111111111"
}
```

```json
{
  "tokenType": "Bearer",
  "accessToken": "<opaque-access-token>",
  "accessTokenExpiresAt": "2026-11-20T13:20:00Z",
  "refreshToken": "<opaque-refresh-token>",
  "refreshTokenExpiresAt": "2026-12-20T13:10:00Z",
  "sessionId": "33333333-3333-3333-3333-333333333333",
  "deviceId": "11111111-1111-1111-1111-111111111111",
  "operator": {
    "staffId": "22222222-2222-2222-2222-222222222222",
    "displayName": "Тестовый владелец",
    "isOwner": true
  },
  "ownerRoleExpiresAt": "2026-11-20T13:40:00Z"
}
```

После входа:

```http
GET /v1/me/stores?offset=0
Authorization: Bearer <mobile-access-token>
```

`GET /v1/me/roles?storeId=<uuid>` также является мобильным bootstrap-запросом. Он не требует операторской сессии и возвращает только фактические роли выбранного сотрудника в этом магазине.

## Операторская сессия магазина

После выбора магазина Android создаёт операторскую сессию:

```http
POST /v1/mobile-auth/operator-sessions
Authorization: Bearer <mobile-access-token>
Content-Type: application/json

{ "storeId": "44444444-4444-4444-4444-444444444444" }
```

Ответ совместим с существующим `OperatorSessionCreated`: содержит `operatorSessionToken`, `sessionId`, `organizationId`, `storeId`, `operator`, сроки действия и `registerId`, если это устройство уже привязано к активной назначенной кассе.

Дальнейшие бизнес-запросы:

```http
Authorization: Bearer <mobile-access-token>
X-Operator-Session: <operator-session-token>
X-Active-Role: cashier | receiver | admin
```

Кассовые запросы нового Android-клиента:

```http
X-Register-Id: <register-uuid>
X-Register-Device-Token: <device-binding-token>
```

Legacy Desktop продолжает использовать без изменений:

```http
X-Register-Id: <register-uuid>
X-Register-Key: <legacy-register-key>
```

## Обновление и восстановление после перезапуска

```http
POST /v1/mobile-auth/refresh
Content-Type: application/json

{ "refreshToken": "<текущий-refresh-token>" }
```

Ответ имеет тот же формат, что и login. Клиент обязан атомарно заменить оба токена. Старые access и refresh токены после успешной ротации больше не принимаются. Refresh-токен хранится только в Android Keystore/EncryptedSharedPreferences и никогда не помещается в журнал.

Для проверки восстановленной сессии:

```http
GET /v1/mobile-auth/current
Authorization: Bearer <mobile-access-token>
```

## Смена сотрудника

```http
POST /v1/mobile-auth/switch
Authorization: Bearer <access-token-предыдущего-сотрудника>
Content-Type: application/json

{
  "login": "cashier.example",
  "password": "<пароль-кассира>"
}
```

Успешная смена атомарно отзывает mobile- и operator-сессии предыдущего сотрудника на устройстве. `deviceId` и серверная привязка кассы сохраняются. Затем клиент вызывает `/v1/mobile-auth/operator-sessions`. Сервер возвращает `registerId` только если касса активна, привязка устройства не отозвана и новый сотрудник назначен кассиром этой кассы.

Открытая смена должна быть закрыта текущим кассиром до смены сотрудника. Следующий кассир открывает собственную смену на той же кассе.

## Повторное подтверждение владельца

Каждое успешно авторизованное owner/admin-действие мобильного владельца продлевает доступ к роли на 30 минут. Поэтому непрерывная активная работа может продолжаться дольше 30 минут без повторного ввода пароля. Если owner/admin-действий не было 30 минут, следующее такое действие получает `403 owner_reauthentication_required`. Кассовая/приёмочная роль не получает дополнительные права владельца и не продлевает owner/admin-доступ.

```http
POST /v1/mobile-auth/owner/reauthenticate
Authorization: Bearer <mobile-access-token>
Content-Type: application/json

{
  "storeId": "44444444-4444-4444-4444-444444444444",
  "login": "owner.example",
  "password": "<пароль>"
}
```

```json
{ "ownerRoleExpiresAt": "2026-11-20T14:10:00Z" }
```

Срок проверяется сервером и является авторитетным: клиент не должен блокировать роль только по первоначальному `ownerRoleExpiresAt`, потому что сервер сдвигает его после успешной owner/admin-работы. Актуальный срок возвращает `GET /v1/mobile-auth/current`. Сам этот GET, refresh токена и фоновые запросы срок не сдвигают.

## Выход

```http
DELETE /v1/mobile-auth/current
Authorization: Bearer <mobile-access-token>
```

Ответ: `204 No Content`. Mobile- и operator-сессии отзываются. Привязка устройства к кассе сохраняется до отдельного отзыва владельцем.

## Подключение сотрудника

Владелец создаёт сотрудника обычным employees API либо назначает mobile login существующему локальному сотруднику:

```http
PUT /v1/stores/{storeId}/employees/{employeeId}/mobile-credentials
Authorization: Bearer <owner-access-token>
X-Operator-Session: <owner-operator-token>
X-Active-Role: admin
Content-Type: application/json

{
  "expectedVersion": 1,
  "login": "cashier.example",
  "newPassword": null,
  "reason": "Подключение Android"
}
```

`newPassword: null` сохраняет текущий Argon2id-пароль сотрудника. Для нового пароля передаётся строка длиной 8–128 символов. Операция увеличивает version и отзывает старые mobile/operator-сессии сотрудника.

## Ошибки

Единый формат:

```json
{
  "type": "https://kassa.qr-space.uz/problems/mobile-credentials-invalid",
  "title": "mobile_credentials_invalid",
  "status": 401,
  "code": "mobile_credentials_invalid",
  "traceId": "<request-id>"
}
```

Основные коды:

- `400 validation_failed`, `active_role_invalid`;
- `401 mobile_credentials_invalid`, `mobile_session_invalid`, `mobile_access_revoked`, `mobile_access_token_expired`, `mobile_refresh_token_invalid`, `mobile_refresh_token_expired`, `operator_session_invalid`, `operator_session_expired`;
- `403 oidc_identity_confirmation_required`, `store_access_denied`, `owner_access_required`, `owner_reauthentication_required`, `active_role_required`, `active_role_denied`, `register_assignment_required`, `register_device_binding_invalid`;
- `404 store_not_found`, `employee_not_found`, `register_not_found`;
- `409 mobile_login_conflict`, `employee_version_conflict`, `operator_store_mismatch`, `register_disabled`;
- `429 mobile_login_rate_limited` с `Retry-After`;
- `503 password_service_unavailable`, `database_temporarily_unavailable`.

Клиент не должен автоматически повторять login/reauthenticate после `429`.

## Изменения Android

1. Добавить экраны login/password, employee switch и owner reauthentication.
2. Создать стабильный installation `deviceId`; не менять его при смене сотрудника.
3. Хранить refresh token и register device token в защищённом хранилище; access/operator токены — только в памяти или защищённом краткоживущем хранилище.
4. После login/refresh атомарно заменять токены. При `401` выполнить один refresh, но не повторять ввод пароля автоматически.
5. После выбора магазина создавать mobile operator session.
6. Перед каждым бизнес-запросом передавать фактически выбранную роль. Не вычислять права на клиенте.
7. При `owner_reauthentication_required` показывать диалог логина/пароля владельца.
8. При смене кассира сначала закрыть его открытую смену, затем вызвать switch и создать новую operator session.

## Изменения Desktop и веба

Обязательных изменений нет. OIDC Authorization Code + PKCE, legacy routes, `X-Operator-Session`, `X-Active-Role` и `X-Register-Key` сохранены. MFA/FIDO2/USB-политики платформенных разработчиков и серверных администраторов не менялись. Правило 30 минут применяется только к mobile bearer владельца.

## Миграция

- применение: `db/040_mobile_auth.sql`;
- обратный откат: `db/rollback/040_mobile_auth.down.sql`;
- миграция добавляет mobile-поля в `employee_credentials` и новые таблицы сессий, rate-limit и аудита;
- существующие staff/store/register/shift/sale данные не удаляются и не изменяются.

Проверенный откат: `040 -> 039 -> 038`, после чего восстанавливается legacy staff schema.

## Развёртывание

1. Взять проверенный custom-format backup PostgreSQL и SHA-256.
2. Восстановить backup в отдельный PostgreSQL 17 и выполнить контрольные запросы.
3. Собрать API из чистой ветки: `dotnet restore --locked-mode`, затем `dotnet publish -c Release --no-restore`.
4. Сохранить тег/идентификатор текущего API-образа для rollback.
5. Применить `db/040_mobile_auth.sql` с `ON_ERROR_STOP=1`.
6. Развернуть только API; оставить `Auth:RequireOperatorSession=false` на переходный период Desktop.
7. Проверить локальный и публичный `/health/live`, авторизованный `/v1/me/stores`, OIDC Desktop и mobile login.
8. Выполнить smoke: магазин, привязка кассы, два сотрудника, две смены, продажа, refresh, owner reauthentication и revoke.

## Откат

1. Остановить выдачу новых mobile credentials.
2. Вернуть предыдущий API-образ и проверить health/Desktop OIDC.
3. Если mobile-данные требуется сохранить, оставить additive-схему 040: старый API её игнорирует.
4. Если схема должна быть удалена, сначала сделать ещё один backup, затем выполнить `db/rollback/040_mobile_auth.down.sql`. Это удалит только mobile sessions/credentials mapping/audit, но не сотрудников, кассы, смены и продажи.
5. При сомнении восстановить проверенный pre-migration backup вместо down migration.

## Проверки

- Release build с `-warnaserror`: успешно, 0 предупреждений;
- unit tests: 3/3;
- прежний Desktop/OIDC operator suite: успешно;
- legacy bearer-only API при `Auth:RequireOperatorSession=false`: успешно;
- Android native auth integration: login, refresh rotation, restart recovery, two-employee switch, register binding, two shifts, owner 30-minute expiry/reauthentication, disable/revoke, foreign-store denial — успешно;
- секреты в application logs/audit и plaintext в БД: не обнаружены;
- миграции 040/039/038: последовательный откат успешно.

Проверки выполнены в изолированном локальном стенде ASP.NET Core + PostgreSQL 17. Реальная установленная Desktop-касса и физический Android-телефон должны быть проверены после развёртывания на доступном тестовом сервере с учётными данными, которые не передаются в чат или отчёт.
