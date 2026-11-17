# Перенесення MovieDiscovery на сервер

Для поточного варіанта з Tailscale Funnel використовуйте `deploy/TAILSCALE_VPS_UK.md` та `docker-compose.funnel.yml`. Нижче збережено деталі відновлення даних і попередній варіант Cloudflare.

Підготовлено 17 листопада 2026 року. Вкажіть SSH-користувача, адресу й порт власного сервера. Для варіанта Cloudflare потрібно заповнити домен і токен тунелю.

## Комплект

Приватна директорія `data/deployment-backups/20261117-server-transfer` містить SQL dump, постери, конфігурацію з ключами авторизації, образи, вихідний код, контрольні суми та результат тестового відновлення. Доступ до директорії обмежено поточним користувачем Windows і SYSTEM. `config-private.tar.gz` та `globals.sql` містять секрети.

Зібрані образи мають платформу Linux amd64. Перед завантаженням перевірте `uname -m` на сервері. Для іншої архітектури потрібна відповідна збірка.

Початкова копія знята з працюючого застосунку. Перед остаточним перемиканням виконайте фінальний експорт із зупиненими процесами запису. Під час підготовки локальний сайт продовжує працювати.

## Диски сервера

Запустіть із каталогу розпакованого вихідного коду наведений скрипт. Він показує диски, файлові системи, вільне місце, Docker storage і поточні контейнери.

```bash
bash scripts/server-preflight.sh /mnt/CHOSEN_DATA_DISK/movie-discovery /mnt/CHOSEN_BACKUP_DISK/movie-discovery
```

Виберіть реальні точки монтування після перегляду `df` та `lsblk`. PostgreSQL займає близько 2,4 ГБ, MinIO — 2,1 ГБ. Для PostgreSQL врахуйте індекси, тимчасове місце для їх перебудови, до 4 ГБ WAL і подальше зростання. Для даних цього проєкту орієнтиром є щонайменше 20 ГБ вільного місця. Резервні копії та архів образів також займають місце.

`PG_DATA_DIR`, `MINIO_DATA_DIR` і `APP_KEYS_DIR` задаються окремо. Їх можна розмістити на різних дисках. Порівняйте `findmnt -T` для кожного шляху й `/`, щоб перевірити, чи вибране сховище розташоване поза системним розділом. Перевірте постійне монтування через `/etc/fstab` і `findmnt --verify` до запуску. Compose має `create_host_path: false`, тому порожня випадкова директорія не створиться автоматично.

Окремо перевірте диск для DockerRootDir. Образи, шари контейнерів і build cache зберігаються там, навіть коли БД винесена на інший диск. Серверний Compose обмежує журнали кожного контейнера трьома файлами по 10 МБ. Остаточний план звільнення системного диска залежить від фактичного серверного огляду.

## Зареєстрований Cloudflare Tunnel

1. Додайте власний домен до Cloudflare та завершіть активацію DNS-зони.
2. У Cloudflare відкрийте Networking → Tunnels і створіть тунель `movie-discovery`.
3. Збережіть токен конектора у файлі `/etc/movie-discovery/cloudflare-token` на сервері. Директорія має бути доступна адміністратору. Файл повинен читатися користувачем контейнера cloudflared. Наприклад, директорія `0700` root і файл `0644` усередині неї дозволяють читання через Docker secret без доступу інших користувачів хоста до директорії.
4. Додайте Published application route для свого домену або піддомену. Service URL — `http://web:3000`.
5. У `deploy/server.env` вкажіть `FRONT_PUBLIC_URL=https://ВАШ_ДОМЕН` і шлях `CLOUDFLARE_TUNNEL_TOKEN_FILE`.

Конектор читає токен через Docker secret. Серверний Compose запускає `cloudflared tunnel run`. Бот отримує той самий `FRONT_PUBLIC_URL`, перевіряє сайт і повертає стабільне посилання. Для цього режиму бот працює без Docker socket і спільного журналу quick tunnel.

Офіційні джерела — [створення тунелю й публічний маршрут](https://developers.cloudflare.com/tunnel/get-started/) та [token-file](https://developers.cloudflare.com/tunnel/reference/run-parameters/#token-file).

## Фінальний знімок на вихідному ПК

Завершіть імпорти, потім у корені проєкту виконайте наведені команди. PostgreSQL залишається працювати для узгодженого логічного dump. Експортер перевіряє, що контейнери запису зупинено, а постери читає через окремий read-only контейнер.

```powershell
docker compose stop bot tunnel web api minio
python scripts/export-server-state.py data/deployment-backups/FINAL_TRANSFER --final
```

Фінальний експорт містить усі таблиці БД, їх лічильники у `database-counts.json`, постери, `.env`, ключі DataProtection та ролі PostgreSQL. Ключі потрібні для перевірки cookie після перенесення. Браузер зберігає cookie за доменом, тому після першої зміни домену користувачам може знадобитися ввійти повторно.

Передайте архіви релізу та фінальний знімок через `scp`, указавши SSH-порт власного сервера, у директорію з достатнім вільним місцем. Перевірте `sha256sum -c SHA256SUMS` окремо для обох комплектів. Остаточне відновлення використовує `moviediscovery.dump`, `minio.tar`, `config-private.tar.gz` з FINAL_TRANSFER.

## Відновлення у нове серверне сховище

Виконуйте з адміністративним доступом до вибраних директорій. Задайте фактичні абсолютні шляхи замість прикладів. PostgreSQL і MinIO відновлюються у нові порожні директорії. Якщо на сервері вже є MovieDiscovery, спочатку збережіть його конфігурацію, образи та резервну копію даних, а потім узгодьте цільовий екземпляр.

```bash
sha256sum -c SHA256SUMS
docker load -i images.tar
mkdir -p /opt/movie-discovery
tar -xzf source.tar.gz -C /opt/movie-discovery
mkdir -m 700 private-config
tar -xzf config-private.tar.gz -C private-config
cd /opt/movie-discovery
```

Скопіюйте приватний `.env` у `deploy/server.env` із правами `0600`. Додайте параметри з `deploy/server.env.example`, замінивши всі `REPLACE` на реальні значення. Паролі PostgreSQL і MinIO та BOT_API_KEY мають відповідати експортованій конфігурації.

```bash
docker compose --env-file deploy/server.env -f docker-compose.server.yml config --quiet
```

Створіть вибрані директорії PG_DATA_DIR, MINIO_DATA_DIR та APP_KEYS_DIR. Перевірте їх порожнечу і файлові системи. Розпакуйте `minio.tar` у MINIO_DATA_DIR, а вміст `private-config/data/keys/` скопіюйте в APP_KEYS_DIR. UID/GID MinIO у підготовленому образі — `10001`. Надайте йому володіння MINIO_DATA_DIR. API читає ключі з APP_KEYS_DIR. Власність PG_DATA_DIR встановлює PostgreSQL entrypoint. Після перенесення каталогів з іншого сервера збережіть коректні UID і права.

```bash
docker compose --env-file deploy/server.env -f docker-compose.server.yml up -d --no-build --pull never db
docker exec moviediscovery-db sh -c 'pg_isready -U "$POSTGRES_USER" -d "$POSTGRES_DB"'
```

Дочекайтеся готовності PostgreSQL. Відновіть повний dump із фінального знімка, вказавши абсолютний шлях до нього.

```bash
docker exec -i moviediscovery-db sh -c 'exec pg_restore -U "$POSTGRES_USER" -d "$POSTGRES_DB" --exit-on-error --single-transaction' < /ABSOLUTE/FINAL_TRANSFER/moviediscovery.dump
```

`globals.sql` зберігає кластерні ролі. Основний користувач уже створюється entrypoint із параметрів POSTGRES_USER/POSTGRES_PASSWORD. Перегляньте додаткові ролі перед застосуванням globals, щоб врахувати наявні ролі цільового кластера.

```bash
docker compose --env-file deploy/server.env -f docker-compose.server.yml up -d --no-build --pull never minio api web
curl --fail http://127.0.0.1:3001/api/health
```

Перевірте журнали API і завершення міграцій, кількість рядків у всіх 29 таблицях, роботу пошуку, власних списків і постерів. Порівнюйте з фінальним знімком. У початковій перевіреній копії було 2 864 858 тайтлів і 14 вебкористувачів. Для cookie, листів і посилань використовується новий FRONT_PUBLIC_URL.

## Перемикання

Після локальної перевірки серверного web запустіть зареєстрований тунель і перевірте публічну адресу.

```bash
docker compose --env-file deploy/server.env -f docker-compose.server.yml up -d --no-build --pull never tunnel
```

Перевірте HTTPS, вхід, пошук, п’ять статусів, додавання тайтлів, постери та відображення на мобільному екрані. Локальний Telegram polling має залишатися зупиненим. Потім запустіть єдиний серверний екземпляр бота.

```bash
docker compose --env-file deploy/server.env -f docker-compose.server.yml up -d --no-build --pull never bot
```

Перевірте `/tunnel`, персональні списки в боті та відсутність конфлікту Telegram polling. Дочекайтеся завершення приймання перед очищенням тимчасового середовища. Збережіть фінальну резервну копію, образи й звіти.

## Відкат

За помилки до приймання зупиніть тільки нові сервіси цього проєкту. Збережіть серверну БД, якщо вона вже приймала записи. Поверніть маршрут Cloudflare на попередню робочу ціль і відновіть запуск вихідного застосунку. Одночасно запускайте один екземпляр Telegram polling для цього токена. Вихідна БД, постери й попередні образи залишаються точкою відновлення. Перенесення нових записів назад потребує окремої перевірки перед відкатом після відкриття запису користувачам.
