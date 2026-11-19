# Tailscale Funnel і універсальний VPS

Підготовлено 17 листопада 2026 року. Профіль запуску — `docker-compose.funnel.yml`. Він запускає PostgreSQL, MinIO, API, web і бота. Tailscale встановлюється на Linux-хості й передає публічний HTTPS-трафік на `http://127.0.0.1:3001`.

## 1. Виберіть VPS

Для початкового публічного запуску цього каталогу орієнтиром є Ubuntu 24.04 LTS, Linux amd64, 4 vCPU, 8 ГБ RAM і від 80 ГБ SSD. Це попередня оцінка для наявного каталогу, пошуку й помірного навантаження. Поточне idle-споживання шести локальних контейнерів сумарно близько 1 ГБ; індексація, імпорти й паралельні запити потребують запасу.

Провайдер може бути будь-який, що дає SSH-доступ, Docker і вихідний доступ до інтернету. Увімкніть вхід за власним SSH-ключем. У firewall провайдера залиште SSH доступним зі своєї адреси. Web, API, PostgreSQL та MinIO у цьому профілі прив’язані до loopback. Публічний вхід забезпечує Funnel.

Для даних бажано використати окремий підключений диск. Перед копіюванням виконайте `bash scripts/server-preflight.sh /mnt/ВАШ_ДИСК/movie-discovery`. Перевірте `df`, `findmnt`, постійне монтування й місце для DockerRootDir. Архіви релізу та резервні копії також розмістіть на диску з достатнім місцем. Інструкції відновлення БД і MinIO є у `deploy/SERVER_TRANSFER_UK.md`.

## 2. Підготуйте Docker і Tailscale

Встановіть Docker Engine і Compose plugin через офіційний apt-репозиторій Docker. Потрібна версія Compose із підтримкою `!override`, щонайменше 2.24.4. Перевірте `docker compose version`.

Команди Docker нижче виконуються користувачем із доступом до Docker або через sudo. Операції з приватними ключами й каталогами даних потребують відповідних файлових прав.

[Офіційна інструкція Docker для Ubuntu](https://docs.docker.com/engine/install/ubuntu/)

На VPS встановіть Tailscale наведеною командою з офіційної сторінки. Вона встановлює системний сервіс.

```bash
curl -fsSL https://tailscale.com/install.sh -o /tmp/install-tailscale.sh
sudo sh /tmp/install-tailscale.sh
sudo systemctl enable --now tailscaled
tailscale version
```

[Встановлення Tailscale](https://tailscale.com/download/linux)

## 3. Зареєструйте постійний вузол

Створіть обліковий запис на https://login.tailscale.com/start. Для особистого некомерційного використання є Personal plan. Відкрийте саме кінцевий VPS через SSH і виконайте команду.

```bash
sudo tailscale up --hostname=movie-discovery
```

Відкрийте показане посилання у своєму браузері, увійдіть і підтвердьте додавання VPS. У панелі Machines має з’явитися `movie-discovery`. Для безперервного серверного доступу перевірте строк дії ключа вузла й налаштування key expiry. У DNS увімкніть MagicDNS та HTTPS certificates, якщо вони ще вимкнені.

Отримайте адресу вузла наведеною командою. Вона ще не відкриває сайт для публіки.

```bash
tailscale status --json | python3 -c 'import json,sys; print("https://" + json.load(sys.stdin)["Self"]["DNSName"].rstrip("."))'
```

Приклад результату — `https://movie-discovery.tail123456.ts.net`. Використовуйте фактичний результат. Адреса пов’язана з назвою вузла й DNS-іменем tailnet. Збереження вузла, його стану та цих назв забезпечує ту саму адресу після звичайного перезапуску. Стан native Tailscale зберігається у `/var/lib/tailscale`. Резервуйте його як секрет. Перенесення вже зареєстрованої ідентичності на іншу VM виконуйте із зупиненим старим вузлом.

[Налаштування сервера](https://tailscale.com/docs/how-to/set-up-servers), [Funnel](https://tailscale.com/docs/features/tailscale-funnel), [тарифи](https://tailscale.com/pricing)

## 4. Заповніть конфігурацію застосунку

Приватний комплект містить `funnel.env` із перенесеними інтеграціями та новими окремими ADMIN_KEY, BOT_API_KEY, POSTGRES_PASSWORD і MINIO_MINIO_PASSWORD. Скопіюйте його в `deploy/funnel.env` із правами `0600`.

Для ручного налаштування використовуйте `deploy/funnel.env.example` разом із конфігурацією вихідного застосунку. Вкажіть фактичні PG_DATA_DIR, MINIO_DATA_DIR, APP_KEYS_DIR, CATALOG_CACHE_DIR, BACKUP_DIR та отриманий FRONT_PUBLIC_URL. `CATALOG_CACHE_DIR` зберігає стиснені файли IMDb між оновленнями.

Коли розгортаєте поточний код із GitHub, задайте власний унікальний RELEASE_TAG і зберіть образи через `docker compose --env-file deploy/funnel.env -f docker-compose.funnel.yml build api web bot minio`. Теги та офлайн-образи попередніх комплектів відповідають збереженим знімкам цих комплектів.

```dotenv
FRONT_PUBLIC_URL=https://movie-discovery.tail123456.ts.net
```

Новий пароль PostgreSQL застосовується під час першої ініціалізації порожнього цільового сховища. Використовуйте ті самі значення в API та БД, які передає Compose. Логічний dump відновлює дані й власників таблиць. `globals.sql` із вихідним паролем ролі зберігається для відновлення й перегляду. У вже ініціалізованій БД зміна POSTGRES_PASSWORD в env потребує узгодженої зміни пароля ролі.

APP_NETWORK_SUBNET задає окрему Docker-підмережу та відповідну довіру API до proxy headers. Перевірте, що вона не перетинається з маршрутами VPS або наявними Docker-мережами. За потреби змініть цей параметр перед створенням мережі.

Налаштуйте SMTP_HOST, SMTP_PORT, SMTP_USER, SMTP_PASSWORD та MAIL_FROM для підтвердження адрес і скидання пароля. При MAIL_ENABLED=true перевірте реальну доставку листів перед публічним запуском. Якщо свідомо вибрано MAIL_ENABLED=false, застосунок автоматично підтверджує нові адреси, а листи відновлення пароля залишаються в серверних логах.

```bash
python3 scripts/check-public-config.py --env-file deploy/funnel.env
```

Перевірка показує назви відсутніх параметрів без значень секретів. На іншому ПК можна використати `--skip-storage-check` для перевірки структури конфігурації без доступу до серверних дисків.

## 5. Відновіть дані й запустіть сайт локально на VPS

Початковий комплект є онлайн-копією. Під час остаточного перенесення зупиніть вихідні процеси запису й створіть новий знімок через `scripts/export-server-state.py --final`, як описано в SERVER_TRANSFER_UK.md. Початкова перевірена БД містила 29 таблиць, 2 864 858 тайтлів і 14 вебкористувачів.

Виконайте `sha256sum -c SHA256SUMS` і `docker load -i images.tar`. Відновіть PostgreSQL у нову порожню директорію, MinIO — в окрему порожню директорію, а ключі DataProtection — в APP_KEYS_DIR. MinIO використовує UID/GID 10001. У командах відновлення з SERVER_TRANSFER_UK.md використовуйте `--env-file deploy/funnel.env -f docker-compose.funnel.yml`.

```bash
docker compose --env-file deploy/funnel.env -f docker-compose.funnel.yml up -d --no-build --pull never db
```

Дочекайтеся готовності БД, потім відновіть фінальний dump. Після відновлення постерів і ключів запустіть застосунок.

```bash
docker compose --env-file deploy/funnel.env -f docker-compose.funnel.yml up -d --no-build --pull never minio api web
curl --fail http://127.0.0.1:3001/api/health
```

API у Production завершує запуск при помилці міграції або seed. Перевірте завершення міграцій, таблиці, пошук і доступ до постерів. Запити з браузера використовують same-origin `/api/*`. Для HTTPS FRONT_PUBLIC_URL авторизаційна cookie має Secure.

## 6. Отримайте публічне посилання

Коли локальна перевірка пройшла, виконайте на VPS команду.

```bash
sudo tailscale funnel --bg http://127.0.0.1:3001
```

Якщо CLI покаже посилання на дозвіл Funnel, відкрийте його, підтвердьте налаштування й повторіть команду. Tailscale налаштує публічний HTTPS-доступ і покаже адресу сайту. Перевірте конфігурацію.

```bash
sudo tailscale funnel status
```

Режим `--bg` зберігає маршрут і відновлює його після перезапуску Tailscale або VPS. Перевірте фактичний перезапуск після першого налаштування. Відвідувачі відкривають адресу звичайним браузером.

[Параметри й автозапуск Funnel](https://tailscale.com/docs/reference/tailscale-cli/funnel)

## 7. Приймання публічного запуску

Відкрийте сайт з іншої мережі, наприклад через мобільний інтернет. Перевірте вхід, HTTPS-cookie, реєстрацію, лист підтвердження, скидання пароля, пошук, власні списки, постери та мобільну сторінку. Бот отримує той самий FRONT_PUBLIC_URL.

Для перевірки IP-лімітів на тестовому запуску надішліть з однієї мережі шість POST-запитів із тілом `{}` на `/api/auth/login` за одну хвилину, щоразу змінюючи підставлений X-Forwarded-For. Очікується п’ять відповідей 400 і потім 429. З другої мережі в цей час очікується 400. Це перевіряє окремі клієнтські IP-бакети та очищення заголовка на Funnel. Дайте тестовій квоті першої мережі відновитися перед звичайним входом.

Оглянутий Tailscale HTTP proxy замінює X-Forwarded-For адресою клієнта, Next передає її до API. Цей ланцюжок спирається на loopback-only web і узгоджену довіру всередині Docker. Версію на VPS та реальний маршрут ще потрібно перевірити.

Після зупинки вихідного polling запустіть єдиний серверний екземпляр Telegram-бота.

```bash
docker compose --env-file deploy/funnel.env -f docker-compose.funnel.yml up -d --no-build --pull never bot
```

Перевірте `/tunnel`, повторний запуск застосунку, перезавантаження VPS, резервне копіювання й пробне відновлення. Фінальну резервну копію тримайте також поза VPS.

Для резервної копії вже працюючого VPS вкажіть фактичний env-файл. Утиліта знаходить каталог DataProtection за bind mount контейнера API, тому підтримує вибране окреме сховище ключів.

```bash
sudo python3 scripts/export-server-state.py /mnt/BACKUP_DISK/movie-discovery/NEW_BACKUP --env-file deploy/funnel.env
```

Це онлайн-копія. Для фінального перемикання після зупинки процесів запису додайте `--final`. Окремо збережіть секретний стан `/var/lib/tailscale` або повний зашифрований snapshot VPS, щоб відновлювати ту саму ідентичність Tailscale.

Після приймання можна під’єднати [автоматичні оновлення через GitHub Actions](runner/README_UK.md). Приклад конфігурації оновлювача використовує профіль `funnel`.

## Межі підготовки

Живий вузол Tailscale та реальна адреса ще не створені, хмарний сервер ще не наданий. Авторизація в Tailscale, SMTP-доставка, зовнішні IP-ліміти й перезапуск VPS залишаються кроками приймання на вибраному сервері. Funnel наразі у beta й має обмеження пропускної здатності. Публічну місткість потрібно перевірити на очікуваному навантаженні.
