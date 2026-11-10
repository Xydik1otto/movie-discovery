# Browser E2E runner (MD-10, FIX_SPEC 2026-11-09)

Playwright-раннер браузерних сценаріїв на ізольованому fixture-стеку
(`docker-compose.test.yml`, web :3201). Закриває прогалину з
`HERMES_IMPLEMENTATION_REPORT.md` («Playwright-runner не додано»).

## Встановлення (ОДИН раз, поза репозиторієм — щоб не чіпати package.json фронту)

```bash
mkdir -p "$TMPDIR/pw-e2e" && cd "$TMPDIR/pw-e2e"
npm init -y && npm i playwright --no-audit --no-fund
npx playwright install chromium
cp <repo>/e2e/browser_e2e.js .
```

## Запуск

```bash
docker compose -f docker-compose.test.yml -p moviediscovery-test up -d --build
docker exec -i moviediscovery-test-db psql -U movie -d moviediscovery_test < e2e/seed_fixture.sql
E2E_BASE=http://localhost:3201 node browser_e2e.js
```

Exit 0 = усі PASS. Скріншоти пишуться у `./shots/`.
Еталонні скріншоти успішного прогону: `docs/audits/2026-11-09/e2e-hermes/shots/`.

## Що покриває (6 чеків)

1. Головна завантажується без критичних JS-помилок.
2. **MD-03**: `/similar?seed=4` — внутрішній id (tmdb_id=NULL) рендерить результати.
3. **MD-03**: вкладка «Схоже на…» на головній → швидкий пошук → підказка →
   «Знайти схоже» → результати (репро-кейс аудиту: «ввів фільм — і все»).
4. **MD-02**: реєстрація → онбординг → «Пропустити» завершується лише після
   серверного збереження; при register rate-limit 429 (3/год/IP) — fallback
   login у тест-акаунт (обхід відомої пастки, чесно логується).
5. **MD-08**: мобільний viewport 390px — без горизонтального overflow.
6. Жодних uncaught page errors.

## Виявлені пастки середовища (див. також seed_fixtures.sql)

* Cookie тест-стеку: `md_auth` (+ чанкінг `.C1/.C2`).
* Rate-ліміти: register 3/год/IP, login тоже має вікно — не лупити поспіль.
* Username унікальний — у повторних прогонах брати суфікс з timestamp.
