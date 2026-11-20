# Серверный выпуск для Desktop 0.8.16

Дата развёртывания: 20 ноября 2026 года.

## Выпуск

- Ветка: `codex/desktop-0.8.16-receiving-updates`.
- Код Android native auth, сохранённый в базе ветки: `6be2c7a`.
- Код приёмки и обновлений Desktop: `ebcbd34`.
- Миграции: существующая `040_mobile_auth.sql` и новая `041_receiving_catalog.sql`.
- Production API image: `sha256:93b7d825565b94d6a6d2d51eefa890bc73b758dcde2a6d6a4d50afa12d5f0e33`.
- Предыдущий API сохранён как `kasa-production-api:rollback-20261120-225746`, image ID `sha256:25b85a2111e50dd6b62d197b810a706b81e4950e2d670becf82fcc693a3b21f3`.
- Резервная копия PostgreSQL: `D:\KassaBackups\20261120-225748\kasa.dump`.
- SHA-256 резервной копии: `AF859B05E4877041EE0E887C50F6C5CFD449DA8CDF3596DA535854B0F44C6735`.

Номер 040 уже занимала более новая миграция мобильной авторизации, поэтому миграция из пакета перенесена вручную под номером 041. Применённые миграции не изменялись.

## API приёмки

Все маршруты используют `Authorization: Bearer <oidc_access_token>`. Обновлённый клиент также передаёт `X-Operator-Session` и фактически выбранную `X-Active-Role: receiver | admin`. На переходном этапе `Auth:RequireOperatorSession=false`, поэтому установленные старые Desktop продолжают работать. Проверки организации, магазина и роли выполняются сервером.

### Общая карточка

`GET /v1/stores/{storeId}/receiving/catalog/shared-barcodes/{barcode}`

Успешный ответ не содержит цены, себестоимости, остатков, `organizationId`, `storeId` или чужого `productId`:

```json
{
  "barcode": "4780000000001",
  "name": "Тестовый товар",
  "unit": "шт",
  "requiresExpiry": true,
  "category": "Продукты",
  "brand": "Пример",
  "shelfLifeDays": 30
}
```

`GET /v1/stores/{storeId}/receiving/catalog/products/{id}` возвращает карточку товара текущего магазина.

### Создание товара

`POST /v1/stores/{storeId}/receiving/catalog/products`

```json
{
  "productId": "11111111-1111-1111-1111-111111111111",
  "product": {
    "name": "Тестовый товар",
    "unit": "шт",
    "requiresExpiry": true,
    "category": "Продукты",
    "brand": "Пример",
    "shelfLifeDays": 30,
    "barcodes": [{ "code": "4780000000001", "quantityFactor": 1 }]
  }
}
```

`POST /v1/stores/{storeId}/receiving/catalog/priced-products` атомарно создаёт товар и первоначальную цену:

```json
{
  "productId": "11111111-1111-1111-1111-111111111111",
  "initialSalePrice": 8000,
  "product": {
    "name": "Тестовый товар",
    "unit": "шт",
    "requiresExpiry": true,
    "category": "Продукты",
    "brand": "Пример",
    "shelfLifeDays": 30,
    "barcodes": [{ "code": "4780000000001", "quantityFactor": 1 }]
  }
}
```

`productId` является ключом идемпотентности: повтор того же запроса после потери ответа возвращает прежний результат и не создаёт дубль; другое тело с тем же идентификатором даёт `409`.

### Черновик накладной

`POST /v1/stores/{storeId}/receipts/with-proposed-costs`

```json
{
  "receiptId": "22222222-2222-2222-2222-222222222222",
  "supplierRef": "supplier-demo",
  "invoiceNumber": "TEST-001",
  "invoiceDate": "2026-11-20",
  "items": [{
    "productId": "11111111-1111-1111-1111-111111111111",
    "lotId": "33333333-3333-3333-3333-333333333333",
    "manufacturedOn": "2026-11-20",
    "expiryDate": "2026-12-20",
    "quantity": 10,
    "proposedUnitCost": 5500
  }]
}
```

`PUT /v1/stores/{storeId}/receipts/{id}/with-proposed-costs` принимает `expectedVersion` и новый массив `items`. Значение `proposedUnitCost: 0` сохраняется как нулевая себестоимость, а не как отсутствие значения. Сервер проверяет дату изготовления, срок хранения и дату окончания годности. Перед проведением накладной администратор обязан подтвердить себестоимость через действующий API `/receipts/{id}/costs`.

Типовые ответы ошибок: `400 validation_failed`, `401 oidc_session_expired`, `403 active_role_denied`, `404 product_not_found`, `409 product_creation_conflict` или конфликт версии. Тело ошибки имеет единый `ProblemDetails` с `status`, `code` и `traceId`.

## Обновления Desktop

- `GET /desktop-updates/latest.json` — публичный строгий манифест.
- `GET /desktop-updates/{file}` — только файл, точно указанный текущим валидным манифестом.

Каталог выпусков подключён к API read-only. Сервер проверяет точный набор полей манифеста, стабильный канал, версию, безопасное имя файла, размер, SHA-256, закреплённый thumbprint издателя, UTC-время публикации и отсутствие reparse point. Скрипт `ops/Publish-DesktopUpdate.ps1` проверяет Authenticode, сертификат издателя, размер и SHA-256, затем атомарно заменяет `latest.json`.

Подписанный установщик 0.8.16 не предоставлен. Поэтому рабочее обновление не опубликовано, а `GET /desktop-updates/latest.json` сейчас намеренно возвращает `404 desktop_update_unavailable`. `TEST_UNSIGNED` не публиковался, проверка подписи клиента не отключалась. Для активации требуется подписать `KassaSetup-0.8.16.exe` вне API-сервера действующим закрытым ключом издателя и запустить скрипт публикации с закреплённым thumbprint. Закрытый ключ на API переносить нельзя.

## Выполненные проверки

- Release build с предупреждениями как ошибками: успешно, 0 предупреждений и ошибок.
- Unit tests: 11/11.
- Полный API/PostgreSQL/security suite: успешно.
- Приёмка: цена 8000, предложенная себестоимость 5500 и ноль, даты изготовления/годности, повтор без дубля, обязательное подтверждение стоимости, защита без Bearer — успешно.
- Регрессия operator auth, Android mobile auth, device bindings и старого Bearer-only API при feature flag false — успешно.
- Резервная копия production восстановлена в отдельный PostgreSQL 17: 5 организаций и 6 магазинов; миграции 040/041 применились, откат 041 и повторное применение прошли.
- Production: локальный и публичный `/health/live` — `200`; защищённый новый маршрут без Bearer — `401`; `Auth:RequireOperatorSession=false`; схема 040/041 присутствует.
- Production OIDC Authorization Code + PKCE с MFA: `/v1/me/stores?offset=0` — `200`, тестовый владелец получил свой магазин с `isOwner=true`.

Проверка через интерфейс установленного Desktop 0.8.16 не выполнена: в доступном окружении нет приложения/исходников Desktop 0.8.16 с тестовым UI. Поэтому фактические поиск и добавление товара через UI, повторное открытие накладной и сохранение настроек после обновления остаются клиентским приёмочным шагом. Серверная часть этих сценариев проверена интеграционно на отдельной PostgreSQL; тестовые товары в реальные магазины не добавлялись.

## Развёртывание и откат

Развёртывание выполняется после резервной копии и проверки её восстановления: явно применить 040, затем 041, подготовить read-only каталог выпусков и пересобрать API из указанной ветки. После запуска проверить локальный и публичный health, схему и `Auth:RequireOperatorSession=false`.

Для быстрого отката приложения вернуть тег `kasa-production-api:rollback-20261120-225746` и пересоздать API/gateway. Миграции аддитивны и старый API их игнорирует, поэтому безопаснее оставить схему на месте. Если требуется полный откат схемы, сначала остановить запись, сделать новый дамп, выполнить `041_receiving_catalog.down.sql`, затем `040_mobile_auth.down.sql`. При необходимости полного восстановления использовать указанный проверенный дамп.
