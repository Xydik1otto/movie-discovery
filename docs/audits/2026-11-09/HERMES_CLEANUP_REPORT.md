# Фінальний Environment Cleanup Report (Hermes, t_cf58bb8d)

Дата: 09.11.2026. Принцип: зупинено/видалено лише task-owned ресурси; користувацькі сервіси і дані не чіпались.

## Task-owned ресурси, створені під час виконання

| Ресурс | Ідентифікатор | Стан | Дія |
|---|---|---|---|
| Fixture-стек `moviediscovery-test` | контейнери `moviediscovery-test-db` (:5434→5432), `moviediscovery-test-api` (:5300→5200), `moviediscovery-test-web` (:3201→3000) | **ЗАЛИШИЛИСЯ ЗАПУЩЕНИМИ** — docker-lifecycle команди заблоковані політикою approvals у безлюдному (unattended) режимі | Однорядкове прибирання (власник, виконати вручну): `docker compose -f docker-compose.test.yml -p moviediscovery-test down -v --remove-orphans` (з G:/Projects/movie-discovery). Volume fixture-БД видаляється цією ж командою; тестові акаунти/тайтли зникнуть разом із volume. |
| Fixture-DB volume | `moviediscovery-test` project volume | не видалено (див. вище) | входить у `down -v` |
| Тимчасові тестові файли | `backend/tests/.../TmpProbeTests.cs`, `TmpProbe2Tests.cs`, `DebugWafTests.cs`, `bot/*.new.cs` | видалено під час виконання/субагентами | — |
| Логи збірок | `C:/Users/bigpi/AppData/Local/Temp/mdfixtest-*.log` (bash /tmp) | залишені, підлягають автоочищенню temp | можна видалити вручну |
| Фонові dotnet-процеси гейту | test runner (pid закінчився) | завершились самі | — |

## Збережені (НЕ my-owned, не чіпались)

- Зовнішній стек: `moviediscovery-db` (:5433), `moviediscovery-api` (:5200), `moviediscovery-web` (:3001), `moviediscovery-bot`, `moviediscovery-tunnel` — працюють на старих образах, запуск/власник зовнішні.
- Аудиторський frontend :3101 — не використовувався, не зупинявся (власник — оркестратор аудиту).
- `.env`, `data/pg`, наявні логи, Hermes desktop/gateway процеси — без змін.
- Arteфакти аудиту `docs/audits/2026-11-09/*` — збережені й закомічені (локальний checkpoint, без push).

## Результати в робочій копії

- Локальний checkpoint-коміт містить: кодові виправлення MD-01–10, тести, fixture-стек, e2e-скрипти, оновлені PLAN/TZ/README/.env.example/bot README, всі артефакти аудиту. Без push/deploy.
