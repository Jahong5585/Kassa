# MyGit MCP Plugin — для ChatGPT и Codex

**Статус:** исходный код опубликован, но сервер пока НЕ установлен, НЕ запущен и НЕ подключён. Для APK понадобится отдельно установленный Forgejo Runner.

## Функции

**Режим просмотра (по умолчанию):** список репозиториев, чтение кода и каталогов, ветки, история коммитов, статусы сборок Forgejo Actions.

**Режим разработки (включается администратором):** создание приватных репозиториев, веток `ai/*`, создание и редактирование текстовых файлов только в `ai/*`, запуск утверждённого workflow `build-apk.yml`. Нет удаления репозиториев или прямой записи в `main`. Для обновления существующего файла требуется его актуальный SHA.

## Требования

- Запущенный MyGit/Forgejo (обычно `http://127.0.0.1:3020`).
- Python **3.11 или новее**.
- Отдельный токен Forgejo с минимальными правами: Settings → Applications → Generate New Token.
- Токен храните только в файле `.env` на своём компьютере. Не отправляйте его в ChatGPT и не публикуйте в GitHub.

## Установка на Windows 10/11

Распакуйте проект и откройте PowerShell в `MyGit\mcp-plugin`.

```powershell
py -3 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
Copy-Item .env.example .env
notepad .env
```

В `.env` замените `PASTE_A_SCOPED_FORGEJO_PERSONAL_ACCESS_TOKEN_HERE` на свой токен Forgejo. Оставьте `MYGIT_WRITE_ENABLED=false`.

Запуск:
```powershell
.\.venv\Scripts\python.exe server.py
```

Локальный MCP endpoint: **http://127.0.0.1:3030/mcp**.

## Установка на Linux

```bash
cd MyGit/mcp-plugin
python3 -m venv .venv
.venv/bin/python -m pip install -r requirements.txt
cp .env.example .env
nano .env
chmod 600 .env
.venv/bin/python server.py
```

## Проверка

```powershell
.\.venv\Scripts\python.exe -m unittest discover -s tests -v
```

Тесты не обращаются к рабочему Forgejo и не изменяют проекты. Для тестирования MCP-протокола используйте совместимый MCP Inspector.

## Подключение к ChatGPT

1. Установите Forgejo и запустите MCP-мост.
2. Для локального сервера используйте **Secure MCP Tunnel**. Простая ссылка `localhost` из браузера ChatGPT не работает: сервер находится на вашем компьютере, а ChatGPT подключается удалённо. Туннель ещё не создан и не входит в исходный комплект.
3. Откройте ChatGPT **в веб-версии** и перейдите в `Plugins → + → Add custom MCP server`.
4. Укажите имя `MyGit`, выберите `Tunnel` и настройте полученный `tunnel_id` (либо надёжно защищённый HTTPS MCP endpoint с OAuth).
5. Проверьте список инструментов и подключите сервис, если возможность доступна вашему аккаунту.
6. Пробный запрос: «@MyGit покажи мои репозитории».

По документации OpenAI Pro может использовать read/fetch MCP; полноценная запись через custom MCP доступна Business и Enterprise/Edu. Конкретные разрешения зависят от аккаунта.

## Codex / локальные клиенты MCP

Если клиент поддерживает MCP stdio, добавьте в `.env`:
```env
MYGIT_TRANSPORT=stdio
```
И укажите своему MCP-клиенту команду `python` из виртуального окружения, путь к `server.py` и рабочий каталог. Для локального stdio-транспорта HTTPS и туннель не нужны.

## Как включить изменения

В `.env` установите:
```env
MYGIT_WRITE_ENABLED=true
MYGIT_ALLOWED_REPOS=localowner/Kassa,localowner/AnotherRepo
```
Это имена **локальных** репозиториев Forgejo, не GitHub. Используйте отдельный токен с минимальными правами. После перезапуска MCP перечень инструментов изменится: при работе с ChatGPT обновите доступные действия (Refresh) и включите только необходимые.

**Правила безопасности:** запись возможна только в allowlist-репозиториях и в ветки `ai/*`; в `main` плагин не пишет; нельзя через MCP редактировать `.env`, приватные ключи, `.forgejo/workflows`, `.github/workflows`. Проверяйте diff перед слиянием AI-ветки.

## Сборка Flutter APK

Сам MCP-сервис APK не собирает. Нужно отдельно установить **Forgejo Runner**, Flutter, JDK, Android SDK и добавить workflow `.forgejo/workflows/build-apk.yml`. Пример workflow уже есть в исходном комплекте MyGit (папка `examples/flutter`). MCP может запросить запуск **только** `build-apk.yml` из ветки `ai/*`, если режим записи включён. Никогда не давайте Runner доступ к критическим данным продакшн-сервера.

## Безопасность подключения

- MCP-процесс слушает **только 127.0.0.1:3030**; не открывайте этот порт прямо в Интернет.
- Secure MCP Tunnel либо аудированный HTTPS/OAuth-шлюз обязателен для облачного ChatGPT. Forgejo PAT защищает REST-вызовы к Forgejo, но не сам публичный MCP endpoint.
- Не храните настоящий токен в Git и не открывайте незашифрованные резервные копии.
- Права просмотра приватных репозиториев определяются выданным Forgejo токеном.

Официальная документация:
- https://help.openai.com/en/articles/12584461-connecting-custom-mcp-servers-in-chatgpt
- https://py.sdk.modelcontextprotocol.io/
- https://forgejo.org/docs/latest/user/api/usage/
- https://forgejo.org/docs/v15.0/user/actions/reference/
