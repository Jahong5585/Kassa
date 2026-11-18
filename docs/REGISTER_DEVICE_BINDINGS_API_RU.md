# Привязка устройства к существующей кассе

Этот контракт заменяет ручной ввод общего 64-символьного ключа кассы в новых Android- и Windows-клиентах. Старый заголовок `X-Register-Key` остаётся допустимым на переходный период. Новый токен относится к одной установке приложения, отзывается независимо и не меняет общий ключ или идентификатор существующей кассы.

## Модель безопасности

- владелец сначала создаёт операторскую сессию на стабильный `deviceId` установки;
- сервер привязывает этот `deviceId` к выбранной существующей кассе и один раз возвращает непрозрачный `registerDeviceToken`;
- PostgreSQL хранит только SHA-256 digest токена;
- клиент хранит токен в Android Keystore/EncryptedSharedPreferences или Windows DPAPI;
- выход сотрудника отзывает только операторскую сессию, но не привязку устройства;
- каждая кассовая операция повторно проверяет OIDC host, операторскую сессию, организацию, магазин, устройство, активность кассы, назначение кассира и роль `cashier`;
- `staff_id` продажи и смены берётся из проверенной операторской сессии.

Секреты в примерах заменены на `<скрыто>`.

## 1. Получить кассы магазина

```http
GET /v1/stores/{storeId}/registers?offset=0
Authorization: Bearer <oidc_access_token>
X-Operator-Session: <owner_operator_session>
X-Active-Role: admin
```

`200 OK`

```json
[
  {
    "registerId": "10000000-0000-0000-0000-000000000001",
    "name": "Касса №1",
    "active": true,
    "version": 4,
    "cashierIds": [
      "20000000-0000-0000-0000-000000000001",
      "20000000-0000-0000-0000-000000000002"
    ]
  }
]
```

## 2. Привязать текущее устройство

Только владелец выбранного магазина с активной ролью `admin`. `deviceId` не принимается из тела: сервер берёт его из проверенной операторской сессии.

```http
POST /v1/stores/{storeId}/registers/{registerId}/device-bindings
Authorization: Bearer <oidc_access_token>
X-Operator-Session: <owner_operator_session>
X-Active-Role: admin
Content-Type: application/json

{
  "deviceName": "Касса у входа — Windows",
  "reason": "Подключение рабочего компьютера"
}
```

`200 OK`, с `Cache-Control: no-store`:

```json
{
  "bindingId": "30000000-0000-0000-0000-000000000001",
  "storeId": "40000000-0000-0000-0000-000000000001",
  "registerId": "10000000-0000-0000-0000-000000000001",
  "deviceId": "50000000-0000-0000-0000-000000000001",
  "deviceName": "Касса у входа — Windows",
  "registerDeviceToken": "<скрыто>",
  "version": 1,
  "createdAt": "2026-11-19T08:00:00Z"
}
```

Токен больше ни одним маршрутом не возвращается. Повторная привязка того же устройства перевыпускает токен и отзывает предыдущую привязку. Перенос устройства на другую кассу при открытой смене возвращает `409 register_device_binding_shift_open`.

## 3. Вход сотрудника и восстановление кассы

```http
POST /v1/stores/{storeId}/operator-sessions
Authorization: Bearer <oidc_access_token владельца>
Content-Type: application/json

{
  "operatorId": "20000000-0000-0000-0000-000000000001",
  "password": "<скрыто>",
  "deviceId": "50000000-0000-0000-0000-000000000001"
}
```

Если сотрудник активен, имеет роль `cashier` и назначен на привязанную кассу, `200 OK` содержит её ID:

```json
{
  "operatorSessionToken": "<скрыто>",
  "sessionId": "60000000-0000-0000-0000-000000000001",
  "organizationId": "70000000-0000-0000-0000-000000000001",
  "storeId": "40000000-0000-0000-0000-000000000001",
  "operator": {
    "operatorId": "20000000-0000-0000-0000-000000000001",
    "firstName": "Тест",
    "lastName": "Кассир",
    "displayName": "Тест Кассир",
    "roles": ["cashier", "receiver"],
    "isOwner": false,
    "passwordRequired": true,
    "active": true
  },
  "idleExpiresAt": "2026-11-19T08:30:00Z",
  "absoluteExpiresAt": "2026-11-19T20:00:00Z",
  "registerId": "10000000-0000-0000-0000-000000000001"
}
```

Сервер возвращает только фактически назначенные роли. Если сотрудник не назначен на кассу, вход остаётся успешным, но `registerId` равен `null`, а кассовая операция получает `403 register_assignment_required`.

После входа клиент может восстановить список разрешённых касс:

```http
GET /v1/operator-sessions/current/registers
Authorization: Bearer <oidc_access_token>
X-Operator-Session: <operator_session_token>
X-Active-Role: cashier
```

```json
{
  "items": [
    {
      "registerId": "10000000-0000-0000-0000-000000000001",
      "name": "Касса №1",
      "active": true,
      "version": 4,
      "assigned": true,
      "boundToCurrentDevice": true,
      "bindingId": "30000000-0000-0000-0000-000000000001"
    }
  ]
}
```

## 4. Кассовые операции

Новый клиент:

```http
POST /v1/shifts
Authorization: Bearer <oidc_access_token>
X-Operator-Session: <operator_session_token>
X-Active-Role: cashier
X-Register-Id: 10000000-0000-0000-0000-000000000001
X-Register-Device-Token: <скрыто>
Content-Type: application/json

{
  "shiftId": "80000000-0000-0000-0000-000000000001",
  "openingCash": 100000
}
```

Переходный старый клиент отправляет те же заголовки, но вместо `X-Register-Device-Token` использует `X-Register-Key`. Одновременно передавать оба секрета не требуется. OpenAPI описывает эти варианты как альтернативные security requirements.

Перед сменой сотрудника текущий кассир закрывает смену:

```http
POST /v1/shifts/{shiftId}/close
Authorization: Bearer <oidc_access_token>
X-Operator-Session: <current_operator_session>
X-Active-Role: cashier
X-Register-Id: <register_uuid>
X-Register-Device-Token: <скрыто>
Content-Type: application/json

{ "countedCash": 125000 }
```

Затем клиент вызывает `DELETE /v1/operator-sessions/current`, показывает список сотрудников и создаёт новую операторскую сессию. Привязка кассы остаётся на устройстве. Следующий сотрудник открывает собственную смену на той же кассе.

## 5. Назначения и отзыв

Несколько кассиров назначаются существующим маршрутом:

```http
PUT /v1/stores/{storeId}/registers/{registerId}/cashiers
Authorization: Bearer <oidc_access_token>
X-Operator-Session: <owner_operator_session>
X-Active-Role: admin
Content-Type: application/json

{
  "expectedVersion": 4,
  "cashierIds": [
    "20000000-0000-0000-0000-000000000001",
    "20000000-0000-0000-0000-000000000002"
  ],
  "reason": "Назначение кассиров"
}
```

Список привязок без секретов:

```http
GET /v1/stores/{storeId}/registers/{registerId}/device-bindings?offset=0&limit=50
Authorization: Bearer <oidc_access_token>
X-Operator-Session: <owner_operator_session>
X-Active-Role: admin
```

Отзыв:

```http
POST /v1/stores/{storeId}/registers/{registerId}/device-bindings/{bindingId}/revoke
Authorization: Bearer <oidc_access_token>
X-Operator-Session: <owner_operator_session>
X-Active-Role: admin
Content-Type: application/json

{
  "expectedVersion": 1,
  "reason": "Устройство заменено"
}
```

Успех: `204 No Content`. Старый токен сразу перестаёт приниматься. Отключение кассы также отзывает все её привязки. Удаление кассира из назначения применяется на следующем запросе и очищает сохранённый `registerId` активной операторской сессии.

## Ошибки

Все ошибки используют ProblemDetails с `code` и `traceId`.

| HTTP | code | Значение |
| --- | --- | --- |
| 400 | `invalid_device_name`, `validation_failed`, `active_role_invalid` | Ошибка тела или роли |
| 401 | `oidc_session_expired`, `operator_session_invalid`, `operator_session_expired` | Истёк OIDC/операторский сеанс |
| 403 | `owner_access_required`, `active_role_required`, `active_role_denied` | Недостаточно прав |
| 403 | `register_assignment_required` | Сотрудник не назначен на кассу |
| 403 | `register_device_binding_invalid` | Токен отозван, подменён или относится к другому устройству |
| 404 | `register_not_found` | Касса не относится к магазину владельца |
| 409 | `register_disabled` | Касса отключена |
| 409 | `operator_register_mismatch` | Сеанс уже связан с другой кассой |
| 409 | `register_device_binding_shift_open` | Перед переносом устройства нужно закрыть смену |
| 409 | `register_device_binding_version_conflict` | Привязка уже изменена или отозвана |

## Изменения клиентов

### Android

1. Сохранять стабильный UUID установки как `deviceId` в защищённом хранилище.
2. После входа владельца получить кассы, дать выбрать существующую и вызвать `device-bindings`.
3. Сохранить `registerId`, `bindingId` и `registerDeviceToken` через Android Keystore/EncryptedSharedPreferences; не писать токен в crash report/telemetry.
4. После входа сотрудника использовать возвращённый `registerId` и сверить `GET /operator-sessions/current/registers`.
5. Для кассовых запросов отправлять `X-Register-Device-Token`; при `register_device_binding_invalid` очистить локальную привязку и предложить вход владельца.

### Windows

1. Сохранить существующий DPAPI installation UUID и передавать его как `deviceId`.
2. Добавить выбор из `GET /stores/{storeId}/registers` и вызов `device-bindings` вместо формы ручного ключа.
3. Хранить новый токен через DPAPI отдельно от legacy register key.
4. Сначала использовать `X-Register-Device-Token`; если обновлённый клиент ещё не получил привязку, временно разрешён сохранённый `X-Register-Key`.
5. Не удалять привязку при `DELETE /operator-sessions/current`; это только смена сотрудника.

## Развёртывание и откат

1. Сделать полную резервную копию PostgreSQL и проверить восстановление в отдельной БД.
2. На восстановленной копии применить `db/039_register_device_bindings.sql`, запустить сборку и интеграционные тесты.
3. Опубликовать API с `Auth:RequireOperatorSession=false`, не меняя старые register keys.
4. Проверить установленный Windows-клиент через legacy `X-Register-Key`.
5. Выпустить Android/Windows с токеном привязки, выполнить привязку тестового устройства к существующей кассе и смену двух сотрудников.
6. После обновления поддерживаемых клиентов включить `Auth:RequireOperatorSession=true`. Legacy register key можно отключать только отдельным будущим feature flag после подтверждённой миграции всех устройств.

Откат приложения: вернуть предыдущий образ API. Миграция добавляет только новую таблицу и не изменяет существующие кассы, ключи, назначения, смены, продажи или аудит. Если новая версия окончательно снята и активных клиентов с device token нет, применить `db/rollback/039_register_device_bindings.down.sql`. Сначала откатывается 039, затем при необходимости 038; удаление 039 отзывает только новые привязки и не затрагивает legacy ключи.
