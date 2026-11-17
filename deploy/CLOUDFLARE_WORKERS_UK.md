# Cloudflare Workers і серверний API

Цей варіант розміщує Next.js frontend на Workers через OpenNext. ASP.NET API, PostgreSQL і MinIO працюють на власному сервері або VPS. Для повного серверного варіанта використовуйте `TAILSCALE_VPS_UK.md`.

## Налаштування зі скриншота

Помилка `Could not detect a directory containing static files` виникала під час запуску Wrangler у корені репозиторію, де конфігурація Worker була відсутня. Next.js знаходиться в `frontend`. Додано OpenNext build та два узгоджені Wrangler config.

Для наявного проєкту `movie-discovery` у Workers Builds задайте такі значення.

| Поле | Значення |
|---|---|
| Git branch | `main` |
| Root directory | `/` |
| Build command | залишити порожнім |
| Deploy command | `npx wrangler deploy` |
| Build variable NODE_VERSION | `22` |

Кореневий Wrangler config виконає `npm ci` у frontend та OpenNext build. Потім завантажить `.open-next/worker.js` і зібрані assets. Після оновлення GitHub натисніть Retry build для останнього commit.

Для окремого нового Workers-проєкту також можна вибрати root `frontend`, build command `npm run build:cloudflare`, deploy command `npm run cf-deploy`. Обидва config використовують Worker name `movie-discovery`; під час створення іншого Worker узгодьте його назву з config.

## Підключення API

Спочатку підготуйте серверний стек за `TAILSCALE_VPS_UK.md`, відновіть дані й отримайте фактичний HTTPS Funnel URL. Перевірте `/api/health` на ньому.

Створіть випадковий shared secret, наприклад через `openssl rand -hex 32`. Додайте його в приватний `deploy/funnel.env` сервера.

```dotenv
API_PROXY_SHARED_SECRET=ВАШ_ВИПАДКОВИЙ_СЕКРЕТ
```

Застосуйте значення до web на сервері.

```bash
docker compose --env-file deploy/funnel.env -f docker-compose.funnel.yml up -d --no-build web
```

Це потребує web image, зібраного з поточного GitHub commit. Попередні офлайн-образи є знімками попереднього етапу. Для поточного коду можна виконати `docker compose --env-file deploy/funnel.env -f docker-compose.funnel.yml build api web bot` перед запуском.

У Cloudflare відкрийте Worker → Settings → Variables and Secrets та додайте runtime-параметри.

| Параметр | Тип | Значення |
|---|---|---|
| API_SERVER_URL | Text | `https://ФАКТИЧНИЙ_СЕРВЕР.ts.net` |
| API_PROXY_SHARED_SECRET | Secret | той самий секрет, що у VPS web |

API_SERVER_URL містить лише origin без `/api`. DEPLOYMENT_TARGET задається Wrangler config. API_SERVER_URL має вказувати на серверний Funnel URL, а адреса самого Worker використовується браузерами. Зміни runtime variables з панелі зберігаються завдяки keep_vars.

Worker підписує IP клієнта HMAC-SHA256 перед зверненням до Funnel. Web на VPS перевіряє підпис, метод, URL та часову мітку, потім передає IP до API. Це зберігає окремі IP-бакети авторизації при додатковому Worker перед Funnel. Ключ використовується тільки на серверних сторонах.

Для реєстрації й відновлення пароля через Workers задайте на API `FRONT_PUBLIC_URL=https://ВАШ_WORKER.workers.dev`. Для двох незалежних сайтів із незалежними даними використовуйте окремі серверні стеки й резервні копії. Один API має одну канонічну адресу листів.

## Перевірка

Після deployment відкрийте адресу Worker. Перевірте `/api/health`, пошук, постери, вхід, вихід, списки та реальну доставку листів. Cookie передаються через same-origin `/api/*`. Перевірте кілька Set-Cookie заголовків і Secure-cookie на HTTPS. Зовнішню перевірку IP-лімітів описано в TAILSCALE_VPS_UK.md.

До заповнення API_SERVER_URL і shared secret frontend може бути зібрано й опубліковано, а `/api/*` відповідатиме `502 api_not_configured`. Повна робота даних потребує доступного серверного API.

Локальна збірка Workers виконується з `frontend`.

```bash
npm ci
npm run build:cloudflare
npm run cf-preview
```

Для preview скопіюйте `.dev.vars.example` у `.dev.vars` та вкажіть тестовий HTTPS origin і ключ. Приватний файл виключено з Git. Звичайні `npm run build`, `npm start` і Docker standalone залишаються доступними.

OpenNext збирається в Linux CI або Linux build container, що також відповідає середовищу Cloudflare Builds. Дотримуйтесь повідомлень OpenNext щодо підтримки Windows.

Для локального Linux build через Docker з кореня репозиторію використовуйте наведений варіант. Він експортує Worker artifacts у приватну робочу директорію.

```bash
docker build -f frontend/Dockerfile.cloudflare --output type=local,dest=.agent-work/cloudflare-build frontend
```

Перевірка від 17.11.2026 пройшла для Next.js 16.4.6, OpenNext 1.20.17, Wrangler 4.164.0 та Node 22. Підтверджено root `wrangler deploy --dry-run`, виконання у локальному workerd із HTTPS origin, підписаний client IP, пошук і сторінки в браузері для обох варіантів. Віддалене публічне розгортання й реальний origin потребують параметрів вибраного сервера.

Окремий ESLint запуск має нуль помилок і 39 попереджень. Нове правило React Compiler про синхронні state updates в існуючих effects лишене як попередження під час міграції; React Compiler у застосунку не ввімкнено. Помилки доступу до refs під час render виправлено.

Джерела — [Cloudflare OpenNext](https://developers.cloudflare.com/workers/framework-guides/web-apps/opennext/), [OpenNext setup](https://opennext.js.org/cloudflare/get-started), [Worker process.env](https://developers.cloudflare.com/workers/configuration/compatibility-flags/#enable-auto-populating-processenv).
