# Вход Flutter-приложения в «Кассу»

Проверенная конфигурация OIDC для Android/Flutter от 11 ноября 2026 года.

## Адреса и идентификаторы

| Параметр | Значение |
| --- | --- |
| API base URL | `https://kassa.qr-space.uz` |
| OIDC issuer | `https://kassa.qr-space.uz/auth/realms/kasa` |
| Discovery | `https://kassa.qr-space.uz/auth/realms/kasa/.well-known/openid-configuration` |
| Authorization endpoint | `https://kassa.qr-space.uz/auth/realms/kasa/protocol/openid-connect/auth` |
| Token endpoint | `https://kassa.qr-space.uz/auth/realms/kasa/protocol/openid-connect/token` |
| Logout endpoint | `https://kassa.qr-space.uz/auth/realms/kasa/protocol/openid-connect/logout` |
| Client ID | `kasa-flutter` |
| Основной Android redirect URI | `uz.qrspace.kassa:/oauth2redirect` |
| Дополнительный redirect URI | `kassa://oauth/callback` |

## Обязательные настройки клиента

- Flow: Authorization Code.
- PKCE: обязательно, `S256`.
- Клиент публичный: Client Secret в APK не используется и не должен добавляться.
- Direct Access Grant / password grant отключён.
- Рекомендуемые scopes: `openid profile email`.
- Access token имеет audience `kasa-api` и срок 300 секунд. API отклоняет токены со сроком больше 600 секунд.
- Для повторного входа используйте системный браузер или Custom Tabs, не WebView и не форму пароля внутри приложения.

Пример параметров для `flutter_appauth`:

```dart
const issuer = 'https://kassa.qr-space.uz/auth/realms/kasa';
const clientId = 'kasa-flutter';
const redirectUrl = 'uz.qrspace.kassa:/oauth2redirect';
const scopes = <String>['openid', 'profile', 'email'];
```

Android manifest должен принимать схему redirect URI, соответствующую `uz.qrspace.kassa`. Точное значение в приложении и в OIDC-запросе должно совпадать с зарегистрированным URI.

## Вызов API

Каждый защищённый запрос передаёт:

```http
Authorization: Bearer <access_token>
```

Кассовые маршруты дополнительно требуют:

```http
X-Register-Id: <register UUID>
X-Register-Key: <64 hex characters>
```

Ключ кассы хранится в Android Keystore/EncryptedSharedPreferences и никогда не записывается в логи. Административные маршруты не требуют кассовых заголовков, но сервер проверяет роль, назначение на магазин и MFA.

## Тестовые учётные записи

- кассир: `test-cashier`;
- администратор: `test-admin`;
- обе учётные записи назначены только тестовому магазину `20000000-0000-0000-0000-000000000011`;
- кассиру назначена активная тестовая касса `20000000-0000-0000-0000-000000000021`;
- у администратора настроен TOTP; вход одним паролем отклоняется.

Пароли, TOTP-секрет и ключ тестовой кассы находятся вне Git в `D:\KassaSecrets\test-accounts.env`. Не копируйте их в исходники, задачи, сообщения или логи сборки.

## Проверки сервера

- вход кассира и доступ к кассовому API — успешно;
- вход администратора без TOTP — отклонён;
- вход администратора с TOTP и доступ к административному API — успешно;
- кассир не имеет доступа к административному маршруту;
- тестовый администратор не имеет доступа к основному магазину;
- старт Authorization Code + PKCE через `kasa-flutter` возвращает страницу входа;
- password grant для `kasa-flutter` отклоняется как `unauthorized_client`.
