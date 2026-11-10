# Касса Минимаркет. Справочник HTTPS API

Срез сохранённого бэкенда `kasa-backend.zip` версии 28 от 8 ноября 2026 года. Справочник содержит 136 бизнес-операций и два служебных маршрута. Состав маршрутов и типы запросов сверены с `src/Kasa.Api/Program.cs` и `src/Kasa.Api/Contracts.cs` из архива.

## Подключение

| Что передать | Правило |
| --- | --- |
| `Authorization: Bearer <access_token>` | Для бизнес-операций и OpenAPI. Токен выдаёт настроенный провайдер. |
| `X-Register-Id`, `X-Register-Key` | Дополнительно для кассовых маршрутов. Касса, кассир и точка проверяются сервером. |
| `Content-Type: application/json` | Для JSON-тел. Поля именуются в `camelCase`. Сервер отклоняет неизвестные поля. |
| `X-File-Name` и фактический `Content-Type` | Для бинарного `PUT` вложения накладной. Допустимы PDF, JPEG и PNG до 2 МиБ, максимум три вложения на накладную. |

Базовый адрес задаётся при развёртывании. Пример ниже использует переменную `KASA_BASE` с HTTPS-адресом вашего стенда и токен из переменной окружения `KASA_TOKEN`.

```bash
curl --fail-with-body \
  -H "Authorization: Bearer ${KASA_TOKEN}" \
  "${KASA_BASE}/openapi/v1.json" -o kasa-openapi.json
```

`GET /openapi/v1.json` возвращает схемы тела, ответа и параметры запущенной версии сервера. Его можно импортировать в Swagger UI или Postman. `GET /health/live` доступен без токена и подтверждает, что приложение отвечает.

Кассир работает с зарегистрированной кассой своей точки. Для административных маршрутов нужны роль администратора, назначение на точку и уровень MFA, заданный через `Auth__AdminAcr`. Приёмщик может создавать и редактировать собственные черновики, передавать их на проверку и читать разрешённые складские данные. Проведение, закупочная стоимость, выгрузки и скачивание оригиналов накладных доступны администратору.

UUID в путях передаются без фигурных скобок. Календарные даты передаются как `YYYY-MM-DD`. Отчёты с `from` и `to` включают обе даты в часовом поясе точки. Для пагинации передавайте `nextOffset` как `offset` либо `nextCursor` как `after` с теми же фильтрами. Используйте тип `decimal` для сумм и количеств на клиенте.

### Ошибки и повторы

| HTTP | Что делать клиенту |
| --- | --- |
| 400 | Проверить тело, обязательные поля и формат параметров. |
| 401 | Получить действующий токен и повторить запрос. |
| 403 | Проверить роль, назначение на кассу и MFA. |
| 404 | Проверить ID и область доступа. Вне области объект может выглядеть как отсутствующий. |
| 409 | Прочитать `code` и сверить состояние документа, версию, цену или остаток перед повтором. |
| 410 | Срок готового файла фоновой выгрузки истёк. |
| 413 / 415 | Проверить размер или формат вложения. |
| 429 / 503 | Повторить с задержкой. Для команды с клиентским ID сохранить исходный ID и тело. |

При прикладной ошибке API возвращает Problem Details. Для диагностики используйте `code`, `traceId` и заголовок `X-Request-Id`. Ошибка строки импорта может дополнительно содержать `row`. После потери ответа на продажу сначала вызовите `GET /v1/sales/{id}` на исходной кассе с исходным `operationId`. При необходимости повторите тот же запрос. Новый идентификатор означает новую операцию.

## Основные сценарии

### Продажа и возврат

1. Откройте смену через `POST /v1/shifts` с `shiftId` и фактическим `openingCash`.
2. Найдите товар через `GET /v1/catalog/barcodes/{barcode}`. Для продажи используйте выбранную партию и актуальную `priceVersion`.
3. Отправьте `POST /v1/sales` с токеном и заголовками кассы. При наличной продаже укажите `method: "cash"` и сумму, которую передал покупатель, в `tendered`. При ручном безнале передайте `method: "manualCashless"` и `tendered: null`.
4. Для возврата запросите `GET /v1/sales/{sourceRegisterId}/{id}/refund-options`, затем отправьте `POST /v1/refunds` с исходным чеком и строками. По коду `refund_approval_required` запросите подтверждение админа и повторите исходное тело возврата.
5. Для закрытия смены отправьте фактический пересчёт `countedCash` через `POST /v1/shifts/{id}/close`.

Пример тела учебной продажи ручным безналом

```json
{
  "operationId": "caf8837e-c82d-4606-a5c7-3b7fc1cbb1e6",
  "shiftId": "b296c6a6-6d0e-4066-83cc-e50412c1bf5b",
  "method": "manualCashless",
  "tendered": null,
  "items": [
    {
      "productId": "00000000-0000-0000-0000-000000000041",
      "lotId": "00000000-0000-0000-0000-000000000051",
      "priceVersion": 1,
      "quantity": 1
    }
  ]
}
```

### Приёмка

1. Приёмщик создаёт черновик через `POST /v1/stores/{storeId}/receipts`. Тело содержит `receiptId`, `supplierRef`, `invoiceNumber`, `invoiceDate`, `items` и при наличии `purchaseOrderId`.
2. Загрузите оригинал через `PUT /v1/stores/{storeId}/receipts/{id}/files/{fileId}`. Тело содержит байты файла. Дождитесь `scanStatus: clean`. Для ожидающей проверки доступен `POST …/files/{fileId}/scan`.
3. Передайте документ на проверку через `POST …/submit` с `expectedVersion`. Админ может проверить закупочную стоимость через `GET` и `PUT …/costs`, вернуть в черновик с причиной или провести через `POST …/post` с актуальной версией.
4. `POST …/cancel` отменяет непроведённую приёмку с указанием версии и причины. Приёмщик может отменить свой черновик. Для отправленного документа нужен админ.

В версии 28 обязательность вложения включается параметром `Purchasing__RequireReceiptAttachment=true`. Прикладное исправление из документа передачи включает этот режим по умолчанию.

### Длинная выгрузка складских движений

Отправьте `POST /v1/reports/stores/{storeId}/stock-movements/exports` с `operationId`, `from`, `to` и при необходимости `productId`, `lotId` и `kind`. В ответе `202 Accepted` будет `Location` для проверки состояния. По статусу `ready` добавьте `/file` к этому пути. Скачивание проверяет автора, назначение на точку и MFA. Возможные статусы — `queued`, `ready`, `failed`, `expired`. Лимит — 10 000 строк и 8 МиБ, доступ к файлу — 24 часа с готовности.

### Диагностика кассы

`POST /v1/register/diagnostics` принимает `eventId`, `shiftId`, `code` и `operationId`. Допустимы `saleOutcomeUnknown`, `printFailed`, `syncFailed`. Для `syncFailed` ID операции можно пропустить. При ошибке печати нужна сохранённая продажа этой смены. Одинаковый повтор события безопасен. Админ видит события в `GET /v1/reports/stores/{storeId}/exceptions`.

## Изменения API после переноса исправлений

Следующие изменения входят в план продолжения и относятся к версии после 28.

- Новый административный `POST /v1/stores/{storeId}/registers/{registerId}/shifts/{id}/recover-close` с `countedCash` и `reason`. Цель — закрыть смену при отозванном доступе кассира или устройства.
- Наличный возврат при недостатке денег в выдающей смене возвращает `409 insufficient_cash_balance`.
- `PUT` карточки товара сохраняет действующий `requiresExpiry`, если поле пропущено или равно `null`. В версии 28 передавайте текущее значение явно.
- Отправка и проведение приёмки требуют проверенное вложение по умолчанию. Точный повтор уже сохранённого файла должен возвращать его метаданные после отправки документа.
- Токен с временем действия свыше 10 минут, без `iat` или с недопустимым будущим временем выдачи должен возвращать `401`. Интеграционную проверку сессий выполняют с рабочим OIDC.

## Каталог маршрутов версии 28

В столбце «Вход» перечислены параметры запроса, кроме параметров пути. Звёздочка означает обязательный параметр запроса на уровне привязки. Валидация на уровне бизнес-логики может добавлять условия. Имена типов в этом столбце соответствуют JSON-телам из следующего раздела. Для схем ответов и примеров фильтров используйте `/openapi/v1.json` и `README.md` из архива.


### Служебные маршруты

| Метод и маршрут | Операция OpenAPI | Вход | Успех |
| --- | --- | --- | --- |
| `GET /health/live` | — | — | 200 JSON |
| `GET /openapi/v1.json` | — | — | 200 OpenAPI JSON |


### Касса и смены

| Метод и маршрут | Операция OpenAPI | Вход | Успех |
| --- | --- | --- | --- |
| `POST /v1/shifts` | `OpenShift` | `OpenShift` | 200 JSON |
| `POST /v1/shifts/{id}/close` | `CloseShift` | `CloseShift` | 200 JSON |
| `GET /v1/shifts/{id}` | `ReadCashierShift` | — | 200 JSON |
| `GET /v1/catalog/barcodes/{barcode}` | `FindProductByBarcode` | — | 200 JSON |
| `GET /v1/catalog/search` | `SearchCashierProducts` | `q` *, `offset` | 200 JSON |
| `GET /v1/catalog/products/{id}` | `ReadCashierProduct` | — | 200 JSON |
| `POST /v1/shifts/{shiftId}/cash-movements` | `SaveCashMovement` | `CreateCashMovement` | 200 JSON |
| `GET /v1/shifts/{shiftId}/cash-movements` | `ListCashierMovements` | `offset` | 200 JSON |
| `GET /v1/shifts/{shiftId}/carts` | `ListParkedCarts` | `includeInactive`, `offset` | 200 JSON |
| `GET /v1/shifts/{shiftId}/carts/{id}` | `ReviewCart` | — | 200 JSON |
| `PUT /v1/shifts/{shiftId}/carts/{id}` | `SaveCart` | `SaveParkedCart` | 200 JSON |
| `POST /v1/shifts/{shiftId}/carts/{id}/cancel` | `CancelCart` | `CancelParkedCart` | 200 JSON |
| `GET /v1/sales/history` | `CashierSalesHistory` | `receiptId`, `shiftId`, `date`, `amount`, `offset` | 200 JSON |
| `POST /v1/sales` | `CommitSale` | `CreateSale` | 200 JSON |
| `GET /v1/sales/{id}` | `ReconcileSale` | — | 200 JSON |
| `POST /v1/register/diagnostics` | `ReportRegisterDiagnostic` | `ReportRegisterDiagnostic` | 200 JSON |
| `GET /v1/sales/{sourceRegisterId}/{id}/refund-options` | `ReadRefundOptions` | — | 200 JSON |
| `POST /v1/refunds` | `CommitRefund` | `CreateRefund` | 200 JSON |
| `GET /v1/refunds/{id}` | `ReconcileRefund` | — | 200 JSON |
| `GET /v1/expiry/cashier` | `ListCashierExpiry` | `filter`, `offset` | 200 JSON |
| `POST /v1/expiry/cashier/shelf-tasks` | `MarkShelfRemoval` | `CreateShelfTask` | 200 JSON |
| `GET /v1/stores/{storeId}/sales/history` | `AdminSalesHistory` | `registerId`, `cashierId`, `receiptId`, `shiftId`, `date`, `amount`, `offset` | 200 JSON |
| `GET /v1/stores/{storeId}/sales/{registerId}/{id}` | `ReadAdminSale` | — | 200 JSON |
| `GET /v1/stores/{storeId}/sales/{sourceRegisterId}/{id}/refund-options` | `ReadAdminRefundOptions` | — | 200 JSON |
| `POST /v1/stores/{storeId}/refund-approvals` | `ApproveRefund` | `ApproveRefund` | 200 JSON |


### Доступ и аудит

| Метод и маршрут | Операция OpenAPI | Вход | Успех |
| --- | --- | --- | --- |
| `GET /v1/stores/{storeId}/audit` | `ReadStoreAudit` | `from` *, `to` *, `action`, `after`, `limit` | 200 JSON |
| `GET /v1/stores/{storeId}/staff` | `ListStoreStaff` | `offset` | 200 JSON |
| `POST /v1/stores/{storeId}/staff` | `ConnectStoreStaff` | `ConnectStoreStaff` | 200 JSON |
| `POST /v1/stores/{storeId}/staff/{id}/revoke` | `RevokeStoreStaff` | — | 200 JSON |
| `GET /v1/stores/{storeId}/registers` | `ListRegisterDevices` | `offset` | 200 JSON |
| `GET /v1/stores/{storeId}/registers/{id}` | `ReadRegisterDevice` | — | 200 JSON |
| `POST /v1/stores/{storeId}/registers` | `RegisterDevice` | `RegisterNewDevice` | 200 JSON |
| `POST /v1/stores/{storeId}/registers/{id}/rotate-key` | `RotateRegisterKey` | `RotateDeviceKey` | 200 JSON |
| `POST /v1/stores/{storeId}/registers/{id}/revoke` | `RevokeRegisterDevice` | `RevokeDevice` | 200 JSON |
| `GET /v1/stores/{storeId}/registers/{registerId}/shifts/{shiftId}/cash-movements` | `ListAdminMovements` | `offset` | 200 JSON |


### Приёмка и документы

| Метод и маршрут | Операция OpenAPI | Вход | Успех |
| --- | --- | --- | --- |
| `GET /v1/stores/{storeId}/receiving/catalog/barcodes/{barcode}` | `ReadReceivingProduct` | — | 200 JSON |
| `POST /v1/stores/{storeId}/purchase-orders` | `CreatePurchaseOrder` | `CreatePurchaseOrder` | 200 JSON |
| `GET /v1/stores/{storeId}/purchase-orders` | `ListPurchaseOrders` | `openOnly`, `offset` | 200 JSON |
| `GET /v1/stores/{storeId}/purchase-orders/{id}` | `ReadPurchaseOrder` | — | 200 JSON |
| `POST /v1/stores/{storeId}/receipts` | `CreateReceipt` | `CreateReceipt` | 200 JSON |
| `GET /v1/stores/{storeId}/receipts` | `ListReceipts` | `status`, `offset` | 200 JSON |
| `GET /v1/stores/{storeId}/receipts/{id}` | `ReadReceipt` | — | 200 JSON |
| `PUT /v1/stores/{storeId}/receipts/{id}` | `EditReceipt` | `EditReceipt` | 200 JSON |
| `POST /v1/stores/{storeId}/receipts/{id}/submit` | `SubmitReceipt` | `ReceiptTransition` | 200 JSON |
| `POST /v1/stores/{storeId}/receipts/{id}/return-to-draft` | `ReturnReceiptToDraft` | `ReturnReceipt` | 200 JSON |
| `POST /v1/stores/{storeId}/receipts/{id}/post` | `PostReceipt` | `ReceiptTransition` | 200 JSON |
| `POST /v1/stores/{storeId}/receipts/{id}/cancel` | `CancelReceipt` | `CancelReceipt` | 200 JSON |
| `GET /v1/stores/{storeId}/receipts/{id}/costs` | `ReadReceiptCosts` | — | 200 JSON |
| `PUT /v1/stores/{storeId}/receipts/{id}/costs` | `SaveReceiptCosts` | `WriteReceiptCosts` | 200 JSON |
| `GET /v1/stores/{storeId}/receipts/{id}/files` | `ListInvoiceFiles` | — | 200 JSON |
| `PUT /v1/stores/{storeId}/receipts/{id}/files/{fileId}` | `UploadInvoiceFile` | `X-File-Name` *, байты PDF, JPEG или PNG | 200 JSON |
| `POST /v1/stores/{storeId}/receipts/{id}/files/{fileId}/scan` | `ScanInvoiceFile` | — | 200 JSON |
| `GET /v1/stores/{storeId}/receipts/{id}/files/{fileId}` | `DownloadInvoiceFile` | — | 200 файл |
| `DELETE /v1/stores/{storeId}/receipts/{id}/files/{fileId}` | `DeleteInvoiceFile` | — | 204 |


### Каталог и цены

| Метод и маршрут | Операция OpenAPI | Вход | Успех |
| --- | --- | --- | --- |
| `GET /v1/stores/{storeId}/catalog/products` | `ListStoreProducts` | `q`, `includeArchived`, `offset` | 200 JSON |
| `GET /v1/stores/{storeId}/catalog/products/{id}` | `ReadStoreProduct` | — | 200 JSON |
| `PUT /v1/stores/{storeId}/catalog/products/{id}` | `SaveStoreProduct` | `WriteProductCard` | 200 JSON |
| `PUT /v1/stores/{storeId}/catalog/products/{id}/price` | `SaveStoreProductPrice` | `WriteProductPrice` | 200 JSON |
| `GET /v1/stores/{storeId}/catalog/products/{id}/price-history` | `ListProductPrices` | `offset` | 200 JSON |
| `POST /v1/stores/{storeId}/catalog/import` | `ImportStoreCatalog` | `ImportCatalog` | 200 JSON |
| `POST /v1/stores/{storeId}/receiving/catalog/proposals` | `ProposeProduct` | `ProposeProduct` | 200 JSON |
| `GET /v1/stores/{storeId}/receiving/catalog/proposals` | `ListProductProposals` | `status`, `offset` | 200 JSON |
| `POST /v1/stores/{storeId}/catalog/proposals/{id}/approve` | `ApproveProductProposal` | `ReviewProductProposal` | 200 JSON |
| `POST /v1/stores/{storeId}/catalog/proposals/{id}/reject` | `RejectProductProposal` | `ReviewProductProposal` | 200 JSON |


### Поставщики и закупки

| Метод и маршрут | Операция OpenAPI | Вход | Успех |
| --- | --- | --- | --- |
| `GET /v1/stores/{storeId}/suppliers` | `ListSuppliers` | `activeOnly`, `offset` | 200 JSON |
| `GET /v1/stores/{storeId}/suppliers/{code}` | `ReadSupplier` | — | 200 JSON |
| `PUT /v1/stores/{storeId}/suppliers/{code}` | `WriteSupplier` | `WriteSupplier` | 200 JSON |
| `GET /v1/stores/{storeId}/procurement/replenishment-policies/{code}/{sku}` | `ReadReplenishmentPolicy` | — | 200 JSON |
| `PUT /v1/stores/{storeId}/procurement/replenishment-policies/{code}/{sku}` | `SaveReplenishmentPolicy` | `WriteReplenishmentPolicy` | 200 JSON |
| `GET /v1/stores/{storeId}/procurement/recommendations/{code}/{sku}/snapshot` | `ReadReplenishmentSnapshot` | — | 200 JSON |
| `GET /v1/stores/{storeId}/procurement/recommendations/{code}/{sku}` | `RecommendFromPolicy` | — | 200 JSON |
| `POST /v1/stores/{storeId}/procurement/recommendations/{code}/{sku}` | `RecommendPurchase` | `ReplenishmentRequest` | 200 JSON |
| `POST /v1/stores/{storeId}/procurement/recommendations/{code}/{sku}/explain` | `ExplainPurchaseRecommendation` | `language` | 200 `ReplenishmentExplanation` |
| `GET /v1/stores/{storeId}/procurement/products/{productId}/stockout-days/{day}` | `ReadStockoutDay` | — | 200 JSON |
| `PUT /v1/stores/{storeId}/procurement/products/{productId}/stockout-days/{day}` | `SaveStockoutDay` | `WriteStockoutDay` | 200 JSON |
| `GET /v1/stores/{storeId}/procurement/suppliers/{code}/prices` | `ListSupplierPrices` | `asOf`, `offset` | 200 JSON |
| `GET /v1/stores/{storeId}/procurement/suppliers/{code}/prices/{sku}` | `SupplierPriceHistory` | `offset` | 200 JSON |
| `PUT /v1/stores/{storeId}/procurement/suppliers/{code}/prices/{sku}` | `SaveSupplierPrice` | `WriteSupplierPrice` | 200 JSON |
| `GET /v1/stores/{storeId}/procurement/orders` | `ListPurchaseDrafts` | `stage`, `offset` | 200 JSON |
| `GET /v1/stores/{storeId}/procurement/orders/{id}` | `GetPurchaseDraft` | — | 200 JSON |
| `PUT /v1/stores/{storeId}/procurement/orders/{id}` | `SavePurchaseDraft` | `SavePurchaseDraft` | 200 JSON |
| `POST /v1/stores/{storeId}/procurement/orders/{id}/approve` | `ApprovePurchaseDraft` | `ApprovePurchaseOrder` | 200 JSON |
| `POST /v1/stores/{storeId}/procurement/orders/{id}/mark-sent` | `MarkPurchaseSent` | `DispatchPurchaseOrder` | 200 JSON |
| `POST /v1/stores/{storeId}/procurement/returns` | `ReturnToSupplier` | `CreateSupplierReturn` | 200 JSON |
| `GET /v1/stores/{storeId}/procurement/returns/{id}` | `GetSupplierReturn` | — | 200 JSON |
| `GET /v1/stores/{storeId}/procurement/suppliers/{code}/balance` | `ReadSupplierBalance` | — | 200 JSON |
| `GET /v1/stores/{storeId}/procurement/suppliers/{code}/payments` | `ListSupplierPayments` | `offset` | 200 JSON |
| `POST /v1/stores/{storeId}/procurement/suppliers/{code}/payments` | `SaveSupplierPayment` | `RecordSupplierPayment` | 200 JSON |
| `GET /v1/stores/{storeId}/procurement/suppliers/{code}/invoices` | `ListSupplierInvoices` | `offset` | 200 JSON |
| `GET /v1/stores/{storeId}/procurement/suppliers/{code}/invoices/{id}` | `ReadSupplierInvoice` | — | 200 JSON |
| `PUT /v1/stores/{storeId}/procurement/suppliers/{code}/invoices/{id}/due-date` | `SetSupplierInvoiceDueDate` | `SetSupplierInvoiceDueDate` | 200 JSON |
| `GET /v1/stores/{storeId}/procurement/suppliers/{code}/invoices/{id}/allocations` | `ListSupplierInvoiceAllocations` | `offset` | 200 JSON |
| `GET /v1/stores/{storeId}/procurement/suppliers/{code}/payments/{id}/allocation-balance` | `ReadSupplierPaymentAllocationBalance` | — | 200 JSON |
| `POST /v1/stores/{storeId}/procurement/suppliers/{code}/payment-allocations` | `AllocateSupplierPayment` | `AllocateSupplierPayment` | 200 JSON |
| `GET /v1/stores/{storeId}/procurement/suppliers/{code}/payment-allocations/{id}` | `ReadSupplierPaymentAllocation` | — | 200 JSON |
| `POST /v1/stores/{storeId}/procurement/suppliers/{code}/payment-allocations/{id}/reverse` | `ReverseSupplierPaymentAllocation` | `ReverseSupplierPaymentAllocation` | 200 JSON |
| `GET /v1/stores/{storeId}/procurement/suppliers/{code}/invoices/{id}/credit-transfers` | `ListInvoiceCreditTransfers` | `offset` | 200 JSON |
| `POST /v1/stores/{storeId}/procurement/suppliers/{code}/credit-transfers` | `TransferSupplierCredit` | `TransferSupplierCredit` | 200 JSON |
| `GET /v1/stores/{storeId}/procurement/suppliers/{code}/credit-transfers/{id}` | `ReadSupplierCreditTransfer` | — | 200 JSON |
| `POST /v1/stores/{storeId}/procurement/suppliers/{code}/credit-transfers/{id}/reverse` | `ReverseSupplierCreditTransfer` | `ReverseSupplierCreditTransfer` | 200 JSON |

`ExplainPurchaseRecommendation` принимает JWT администратора назначенной точки и пустое тело. Дополнительно требуется разрешение `Ai:ProcurementAccess:<organizationId>:<storeId>:<staffId>`. При отсутствии разрешения API возвращает `403 procurement_ai_access_denied`. Параметр `language` принимает `ru` (по умолчанию) или `uz` (латиница). Маршрут считает рекомендацию по сохранённым настройкам выбранного магазина. Отображайте `explanation` как обычный текст. Дата и объём заказа относятся к версии расчёта с указанными `calculatedAt` и `policyVersion`.

```json
{
  "explanation": "До ближайшей поставки может возникнуть дефицит. При расчёте учтён запас, срок годности которого истечёт до поставки.",
  "calculatedAt": "2026-11-10T10:15:00Z",
  "policyVersion": 1,
  "orderDate": "2026-11-10",
  "suggestedQuantity": 100
}
```

Для этого маршрута действует лимит в три запроса на пользователя в минуту (`429`). Неподдерживаемый язык возвращает `400 unsupported_explanation_language`. Если ИИ выключен, квота провайдера исчерпана, вышло время ожидания или ответ недоступен, API возвращает `503` с кодом `procurement_ai_disabled`, `procurement_ai_quota`, `procurement_ai_timeout` или `procurement_ai_unavailable` соответственно. Клиент в этих случаях может отобразить обычный результат расчёта.


### Склад и инвентаризация

| Метод и маршрут | Операция OpenAPI | Вход | Успех |
| --- | --- | --- | --- |
| `POST /v1/stores/{storeId}/stock/opening-balances/preview` | `PreviewOpeningStock` | `ImportOpeningStock` | 200 JSON |
| `POST /v1/stores/{storeId}/stock/opening-balances` | `PostOpeningStock` | `ImportOpeningStock` | 200 JSON |
| `GET /v1/stores/{storeId}/stock/opening-balances/{id}` | `ReadOpeningStock` | — | 200 JSON |
| `GET /v1/stores/{storeId}/stock/opening-balances/{id}/csv` | `ExportOpeningStock` | — | 200 CSV |
| `GET /v1/stores/{storeId}/stock/lots` | `ListStockLots` | `productId`, `offset` | 200 JSON |
| `GET /v1/stores/{storeId}/stock/expiry` | `ListStoreExpiry` | `filter`, `offset` | 200 JSON |
| `GET /v1/stores/{storeId}/stock/shelf-tasks` | `ListShelfTasks` | `openOnly`, `offset` | 200 JSON |
| `POST /v1/stores/{storeId}/stock/shelf-tasks/{id}/resolve` | `ResolveShelfTask` | `ResolveShelfTask` | 200 JSON |
| `POST /v1/stores/{storeId}/stock/operations` | `ApplyStockOperation` | `CreateStockOperation` | 200 JSON |
| `GET /v1/stores/{storeId}/stock/operations` | `ListStockOperations` | `offset` | 200 JSON |
| `GET /v1/stores/{storeId}/stock/operations/{id}` | `ReadStockOperation` | — | 200 JSON |
| `POST /v1/stores/{storeId}/stock/inventories` | `CreateInventory` | `CreateInventory` | 200 JSON |
| `GET /v1/stores/{storeId}/stock/inventories` | `ListInventories` | `status`, `offset` | 200 JSON |
| `GET /v1/stores/{storeId}/stock/inventories/{id}` | `ReadInventory` | `blind` | 200 JSON |
| `POST /v1/stores/{storeId}/stock/inventories/{id}/refresh` | `RefreshInventoryScope` | — | 200 JSON |
| `POST /v1/stores/{storeId}/stock/inventories/{id}/count` | `SaveInventoryCount` | `CountInventory` | 200 JSON |
| `POST /v1/stores/{storeId}/stock/inventories/{id}/post` | `PostInventory` | `ReceiptTransition` | 200 JSON |


### Отчёты и выгрузки

| Метод и маршрут | Операция OpenAPI | Вход | Успех |
| --- | --- | --- | --- |
| `GET /v1/reports/stores/{storeId}/registers/{registerId}/shifts/{id}` | `ReadAdminShift` | — | 200 JSON |
| `GET /v1/reports/stores/{storeId}/sales` | `ReadSalesPeriod` | `from` *, `to` *, `registerId`, `cashierId` | 200 JSON |
| `GET /v1/reports/stores/{storeId}/sales/csv` | `DownloadSalesPeriodCsv` | `from` *, `to` *, `registerId`, `cashierId` | 200 CSV |
| `GET /v1/reports/stores/{storeId}/profit` | `ReadProfitReport` | `from` *, `to` * | 200 JSON |
| `GET /v1/reports/stores/{storeId}/profit/csv` | `DownloadProfitReportCsv` | `from` *, `to` * | 200 CSV |
| `GET /v1/reports/stores/{storeId}/stock-movements` | `ReadStockMovementReport` | `from` *, `to` *, `productId`, `lotId`, `kind`, `after`, `limit` | 200 JSON |
| `GET /v1/reports/stores/{storeId}/stock-movements/csv` | `DownloadStockMovementCsv` | `from` *, `to` *, `productId`, `lotId`, `kind` | 200 CSV |
| `POST /v1/reports/stores/{storeId}/stock-movements/exports` | `QueueStockMovementExport` | `CreateStockMovementExport` | 202 JSON |
| `GET /v1/reports/stores/{storeId}/stock-movements/exports/{id}` | `ReadStockMovementExport` | — | 200 JSON |
| `GET /v1/reports/stores/{storeId}/stock-movements/exports/{id}/file` | `DownloadStockMovementExport` | — | 200 CSV |
| `GET /v1/reports/stores/{storeId}/stock-losses` | `ReadStockLossReport` | `from` *, `to` *, `productId`, `after`, `limit` | 200 JSON |
| `GET /v1/reports/stores/{storeId}/stock-losses/csv` | `DownloadStockLossReportCsv` | `from` *, `to` *, `productId` | 200 CSV |
| `GET /v1/reports/stores/{storeId}/stock-expiry` | `ReadStockExpiryReport` | `filter`, `offset` | 200 JSON |
| `GET /v1/reports/stores/{storeId}/stock-expiry/csv` | `DownloadStockExpiryCsv` | `filter` | 200 CSV |
| `GET /v1/reports/stores/{storeId}/stock-valuation` | `ReadStockValuation` | `productId`, `offset` | 200 JSON |
| `GET /v1/reports/stores/{storeId}/stock-valuation/csv` | `DownloadStockValuationCsv` | `productId` | 200 CSV |
| `POST /v1/reports/stores/{storeId}/stock-valuation/snapshots` | `CaptureStockSnapshot` | `CaptureStockSnapshot` | 200 JSON |
| `GET /v1/reports/stores/{storeId}/stock-valuation/snapshots` | `ReadStockSnapshot` | `date` *, `productId`, `offset` | 200 JSON |
| `GET /v1/reports/stores/{storeId}/supplier-settlements` | `ReadSupplierSettlementReport` | `supplierCode`, `offset` | 200 JSON |
| `GET /v1/reports/stores/{storeId}/exceptions` | `ReadStoreExceptionReport` | `code`, `offset`, `limit` | 200 JSON |

## Поля JSON-запросов

Типы ниже приведены в нотации C#. `Guid` передаётся строкой UUID, `DateOnly` строкой даты, `decimal` числом. Знак `?` допускает `null` в типе. Звёздочкой отмечены поля с явной проверкой наличия при разборе JSON. Остальные обязательные значения и ограничения определяются бизнес-валидацией.

| Тип | Поля и типы |
| --- | --- |
| `AllocateSupplierPayment` | `operationId` — `Guid`, `paymentId` — `Guid`, `items` — `SupplierPaymentAllocationLine[]` |
| `ApprovePurchaseOrder` | `expectedRevision` — `int` |
| `ApproveRefund` | `registerId` — `Guid`, `amount` * — `decimal`, `refund` — `CreateRefund` |
| `CancelParkedCart` | `expectedVersion` — `int`, `reason` — `string` |
| `CancelReceipt` | `expectedVersion` — `int`, `reason` — `string` |
| `CaptureStockSnapshot` | `operationId` — `Guid` |
| `CloseShift` | `countedCash` * — `decimal` |
| `ConnectStoreStaff` | `subject` — `string`, `role` — `string`, `registerId` — `Guid?`, по умолчанию `null` |
| `CountInventory` | `expectedVersion` — `int`, `items` — `CountInventoryLine[]` |
| `CountInventoryLine` | `productId` — `Guid`, `lotId` — `Guid`, `lotRevision` — `long`, `stockQuantity` * — `decimal`, `quarantineQuantity` * — `decimal` |
| `CreateCashMovement` | `operationId` — `Guid`, `direction` — `string`, `amount` — `decimal`, `reason` — `string` |
| `CreateInventory` | `inventoryId` — `Guid`, `reason` — `string`, `lots` — `StockLotKey[]?`, по умолчанию `null`, `scope` — `InventoryScope?`, по умолчанию `null` |
| `CreatePurchaseOrder` | `orderId` — `Guid`, `supplierRef` — `string`, `items` — `OrderItem[]` |
| `CreateReceipt` | `receiptId` — `Guid`, `supplierRef` — `string`, `invoiceNumber` — `string`, `invoiceDate` — `DateOnly`, `items` — `ReceiptItem[]`, `purchaseOrderId` — `Guid?`, по умолчанию `null` |
| `CreateRefund` | `operationId` — `Guid`, `shiftId` — `Guid`, `sourceRegisterId` — `Guid`, `saleId` — `Guid`, `reason` — `string`, `items` — `RefundItem[]` |
| `CreateSale` | `operationId` — `Guid`, `shiftId` — `Guid`, `method` — `string`, `tendered` — `decimal?`, `items` — `SaleItem[]`, `discount` — `ManualSaleDiscount?`, по умолчанию `null`, `cartId` — `Guid?`, по умолчанию `null`, `cartVersion` — `int?`, по умолчанию `null` |
| `CreateShelfTask` | `taskId` — `Guid`, `productId` — `Guid`, `lotId` — `Guid`, `quantity` — `decimal` |
| `CreateStockMovementExport` | `operationId` — `Guid`, `from` — `DateOnly`, `to` — `DateOnly`, `productId` — `Guid?`, `lotId` — `Guid?`, `kind` — `string?` |
| `CreateStockOperation` | `operationId` — `Guid`, `productId` — `Guid`, `lotId` — `Guid`, `kind` — `string`, `quantity` — `decimal`, `reason` — `string` |
| `CreateSupplierReturn` | `returnId` — `Guid`, `receiptId` — `Guid`, `reason` — `string`, `items` — `SupplierReturnItem[]` |
| `DispatchPurchaseOrder` | `expectedRevision` — `int`, `reference` — `string` |
| `EditReceipt` | `expectedVersion` — `int`, `items` — `ReceiptItem[]` |
| `ImportCatalog` | `preview` * — `bool`, `csv` — `string` |
| `ImportOpeningStock` | `operationId` — `Guid`, `sourceReference` — `string`, `reason` — `string`, `items` — `OpeningStockItem[]?`, по умолчанию `null`, `csv` — `string?`, по умолчанию `null` |
| `InventoryScope` | `kind` — `string`, `productId` — `Guid?`, по умолчанию `null`, `category` — `string?`, по умолчанию `null` |
| `ManualSaleDiscount` | `kind` — `string`, `value` — `decimal`, `reason` — `string` |
| `OpenShift` | `shiftId` — `Guid`, `openingCash` * — `decimal` |
| `OpeningStockItem` | `productId` — `Guid`, `lotId` — `Guid`, `expiryDate` — `DateOnly?`, `stockQuantity` * — `decimal`, `quarantineQuantity` * — `decimal`, `totalCost` — `decimal?` |
| `OrderItem` | `productId` — `Guid`, `quantity` — `decimal` |
| `OrderOffer` | `supplierSku` — `string`, `priceVersion` — `int`, `packages` — `int` |
| `ProductBarcode` | `code` — `string`, `quantityFactor` — `decimal` |
| `ProductProposalBody` | `name` — `string`, `unit` — `string`, `requiresExpiry` — `bool`, `sku` — `string?`, `category` — `string?`, `barcodes` — `ProductBarcode[]` |
| `ProposeProduct` | `proposalId` — `Guid`, `product` — `ProductProposalBody` |
| `ReceiptCostItem` | `productId` — `Guid`, `lotId` — `Guid`, `unitPrice` * — `decimal`, `discount` — `decimal`, `includedTax` — `decimal`, `allocatedCharges` — `decimal` |
| `ReceiptItem` | `productId` — `Guid`, `lotId` — `Guid`, `expiryDate` — `DateOnly?`, `quantity` — `decimal` |
| `ReceiptTransition` | `expectedVersion` — `int` |
| `RecordSupplierPayment` | `operationId` — `Guid`, `amount` — `decimal`, `paymentDate` — `DateOnly`, `reference` — `string` |
| `RefundItem` | `productId` — `Guid`, `lotId` — `Guid`, `quantity` — `decimal`, `destination` — `string` |
| `RegisterNewDevice` | `registerId` — `Guid`, `name` — `string`, `cashierId` — `Guid`, `registerKey` — `string` |
| `ReplenishmentRequest` | `historyDays` — `int`, по умолчанию `28`, `minimumHistoryDays` — `int`, по умолчанию `7`, `coverageDays` — `int`, по умолчанию `5`, `safetyStock` — `decimal`, по умолчанию `0`, `minimumPackages` — `int`, по умолчанию `1`, `leadTimeDays` — `int?`, по умолчанию `null`, `orderWeekdays` — `int[]?`, по умолчанию `null`, `manualDailyDemand` — `decimal?`, по умолчанию `null`, `orderCutoffTime` — `TimeOnly?`, по умолчанию `null` |
| `ReportRegisterDiagnostic` | `eventId` — `Guid`, `shiftId` — `Guid`, `code` — `string`, `operationId` — `Guid?` |
| `ResolveShelfTask` | `resolution` — `string` |
| `ReturnReceipt` | `expectedVersion` — `int`, `reason` — `string` |
| `ReverseSupplierCreditTransfer` | `operationId` — `Guid`, `reason` — `string` |
| `ReverseSupplierPaymentAllocation` | `operationId` — `Guid`, `reason` — `string` |
| `ReviewProductProposal` | `reason` — `string?` |
| `RevokeDevice` | `expectedVersion` — `int` |
| `RotateDeviceKey` | `expectedVersion` — `int`, `registerKey` — `string` |
| `SaleItem` | `productId` — `Guid`, `lotId` — `Guid`, `priceVersion` — `int`, `quantity` — `decimal` |
| `SaveParkedCart` | `expectedVersion` * — `int`, `items` — `SaleItem[]` |
| `SavePurchaseDraft` | `expectedRevision` * — `int`, `supplierCode` — `string`, `expectedDeliveryDate` — `DateOnly`, `items` — `OrderOffer[]` |
| `SetSupplierInvoiceDueDate` | `expectedVersion` * — `int`, `dueDate` * — `DateOnly?` |
| `StockLotKey` | `productId` — `Guid`, `lotId` — `Guid` |
| `SupplierPaymentAllocationLine` | `receiptId` — `Guid`, `amount` — `decimal` |
| `SupplierReturnItem` | `productId` — `Guid`, `lotId` — `Guid`, `quantity` — `decimal`, `bucket` — `string` |
| `TransferSupplierCredit` | `operationId` — `Guid`, `sourceReceiptId` — `Guid`, `targetReceiptId` — `Guid`, `amount` — `decimal`, `reason` — `string` |
| `WriteProductCard` | `expectedVersion` * — `int`, `name` — `string`, `unit` — `string`, `requiresExpiry` — `bool`, `sku` — `string?`, `category` — `string?`, `brand` — `string?`, `supplierRef` — `string?`, `taxProfile` — `string?`, `barcodes` — `ProductBarcode[]` |
| `WriteProductPrice` | `expectedVersion` — `int`, `unitPrice` * — `decimal?`, `active` * — `bool`, `reason` — `string` |
| `WriteReceiptCosts` | `expectedVersion` — `int`, `items` — `ReceiptCostItem[]` |
| `WriteReplenishmentPolicy` | `expectedVersion` * — `int`, `settings` — `ReplenishmentRequest` |
| `WriteStockoutDay` | `expectedVersion` * — `int`, `confirmed` * — `bool`, `reason` — `string` |
| `WriteSupplier` | `expectedVersion` — `int`, `name` — `string`, `taxId` — `string?`, `phone` — `string?`, `email` — `string?`, `paymentTerms` — `string?`, `leadTimeDays` — `int`, `active` * — `bool` |
| `WriteSupplierPrice` | `expectedVersion` * — `int`, `productId` — `Guid`, `packageUnit` — `string`, `unitsPerPackage` — `decimal`, `packagePrice` — `decimal`, `validFrom` — `DateOnly`, `validThrough` — `DateOnly?` |
