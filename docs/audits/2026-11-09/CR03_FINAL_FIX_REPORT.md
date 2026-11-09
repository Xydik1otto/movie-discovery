# CR-03 Final Fix Report — два P1-залишки (2026-11-09, канал верифікації)

Вхід: `CODEX_CR_REVIEW.md` + `codex-cr-review/module-probes.json` (незалежний рев'ю
після прийняття f336082). CR-01/02/04/05/06 підтверджені — не переглядались.
Виправлені ОБИДВА P1-залишки CR-03 у фронтенді. Коміт: **babd123**.

## Залишок 1 (P1): стирання знімка без per-entry доказів

**Файл:** `frontend/app/lists/page.tsx` (doMigrate), `frontend/lib/guestLists.ts`.

**Дефект (repro рев'ювера, module-probes.json → `legacyRejectedMigration`):**
snapshot `{watchlist, titleId=999999999}` + legacy-відповідь `{moved:0,skipped:1}`
(старий бекенд змішує rejected у skipped) → старий код бачив
`entries.length===0 && moved+skipped===snap.length`, ставив completion-маркер,
викликав `clearGuestSnapshot()` → `remaining=[], migrated=true` — дані користувача
втрачено, хоча сервер підтвердження не давав.

**Фікс:**
- Legacy-гілку ВИДАЛЕНО. При `entries.length===0` (немає per-entry статусів):
  чесна помилка несумісної версії API (`migrate.incompatible`, uk/en) —
  знімок НЕ чиститься, маркер НЕ ставиться, retry збережений.
- `clearGuestSnapshot()` (full-wipe API) видалено з модуля — точкове видалення
  тепер тільки через `removeGuestEntries` за ідентичністю.
- Маркер ставиться лише в гілці повного підтвердження `confirmed.length===snap.length`.

## Залишок 2 (P1): видалення по ключу без звірки значення

**Файл:** `frontend/lib/guestLists.ts` (removeGuestEntries), `page.tsx` (dropConfirmed).

**Дефект (module-probes.json → `concurrentRating`):** submitted `watched/id1/rating7`
→ інша вкладка змінює на `rating9` (той самий ключ) → ack старого знімка видаляв
запис по `(kind,titleId)` → новий `rating9` знищено.

**Фікс:** `removeGuestEntries(confirmed: GuestEntryIdentity[])` видаляє запис лише
якщо він ТОЧНО ідентичний надісланому підтвердженому знімку — токен ідентичності
`kind:titleId:rating:addedAt`. Зміна рейтингу або remove+readd того самого ключа
створює новий addedAt/значення → не збігається → запис ЛИШАЄТЬСЯ для наступної
явної міграції. `page.tsx` передає повні submitted-записи (мапа `kind:titleId` →
запис знімка), а не лише пари ключів.

## Регресії (продакшн-функції й UI, не копії логіки)

1. **Модульна проба** — `docs/audits/2026-11-09/cr03-final/cr03_module_probe.js`:
   transpile ФАКТИЧНОГО `frontend/lib/guestLists.ts` через typescript + стаб
   localStorage; **7/7 PASS** (`module-probe.json`): same-key rating edit 7→9
   збережений; remove+readd (новий addedAt) збережений; ідентичний confirmed
   видаляється; інший ключ не чіпається; mixed; rejected-виклик no-op;
   `clearGuestSnapshot` більше не існує в API.
2. **Браузерна регресія** — `e2e/cr03_regressions.js` по реальному UI
   (page.tsx + guestLists у браузері, route-intercept migrate-guest): **18/18 PASS**
   (`cr03-final/browser-regressions.json`, скріншоти `cr03-final/shots/`):
   - S1 legacy `{moved:0,skipped:2}` без entries → знімок цілий (2 записи),
     маркера немає, помилка «не повертає детальний статус» показана;
   - **S1b — БУКВАЛЬНИЙ repro рев'ювера**: snapshot лише `999999999` +
     `{moved:0,skipped:1}` → запис ЗАЛИШИВСЯ, `migrated` НЕ встановлено,
     маркера немає (до фіксу: `remaining=[], migrated=true`);
   - S2 same-key rating edit 7→9 під час in-flight (delay 2.5s) → rating 9
     збережений, маркер надісланого знімка ставиться;
   - S3 remove+readd під час in-flight → новий запис (addedAt T2) збережений;
   - S4 новий інший key під час in-flight → підтверджений видалено, новий залишився;
   - S5 частковий результат → confirmed видалені, rejected 999999999 лишився,
     маркера НЕМАЄ (retry доступний), «успіх» не показується, кількість rejected=1;
   - S6 retry після S5 → idempotent, rejected не стертий;
   - S7 жодної pageerror у всіх сценаріях.
3. **Холодна API-матриця** (fresh volume + `e2e/seed_fixture.sql`, раннер
   `e2e/md_audit_fixes.py` з name-резолвом): **46/46 PASS**.
4. **Базовий браузерний E2E** `e2e/browser_e2e.js`: **14/14 PASS** (регресії
   CR-02/03/04/06 не зламані фіксами).

## Гейти (точні результати)

| Гейт | Результат |
|---|---|
| backend dotnet test | **481/481** Passed, 0 Failed (6 s) |
| bot dotnet test | **158/158** Passed, 0 Failed (95 ms) |
| eslint (frontend) | 0 errors, 2 warnings (відома пара у `public/sw.js`) |
| tsc --noEmit | чисто |
| next build | OK (17 сторінок) |
| cold API matrix (5300/3201/5434) | **46/46** |
| browser E2E (browser_e2e.js) | **14/14** |
| CR-03 browser regression (cr03_regressions.js) | **18/18** |
| CR-03 module probe (реальний guestLists.ts) | **7/7** |

## Відтворюваність незалежних repro

- `legacyRejectedMigration` ({moved:0,skipped:1}, titleId 999999999): **більше не
  відтворюється** — S1b (браузер, реальний UI) + чесна помилка несумісності.
- `concurrentRating` (submitted 7 → інша вкладка 9 → ack): **більше не
  відтворюється** — M1 (модульна проба на реальному guestLists.ts) + S2 (браузер).

## Cleanup

- Тест-стек `moviediscovery-test` знятий `down -v` (0 контейнерів, volume видалено).
- Prod `moviediscovery-{db,api,web,bot,tunnel}` та `scb-*` — не чіпались.
- `.env`, `data/pg`, чужі зміни — недоторкані. Push/deploy/production-міграції — не виконувались.
- Штатні облікові дані тестів — тільки з файлів репозиторію (md_audit_fixes.py,
  browser_e2e.js); нотатка для повторних прогонів: `CR03_FALLBACK_EMAIL=<mdfix_b_...>`
  і пауза ≥60с перед auth (login 5/хв/IP рахує всі спроби).

## Статус

Обидва незалежні repro рев'ювера перестали відтворюватися на виправленому коді
(модульна + браузерна верифікація вище). CR-03 фінально закривається після
повторного незалежного приймання — рішення за власником/рев'ювером.
