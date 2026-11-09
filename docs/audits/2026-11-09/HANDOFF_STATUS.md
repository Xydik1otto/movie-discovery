# Статус передачі Hermes

- Board: `movie-discovery`, display name `Movie Discovery`.
- Hermes task: `t_cf58bb8d`.
- Назва: MovieDiscovery: fix audit MD-01–10, ownership, onboarding, similarity and migration.
- Assignee: `default`, поточний Hermes provider/model без override (status показав custom / z-ai/glm-5.3).
- Workspace: `dir:G:/Projects/movie-discovery`; completion contract: `local-only`.
- Idempotency key: `movie-discovery-audit-fixes-20261109`.
- Перевірений статус: `ready`, started_at/session_id/result = null. Завдання прийняте в durable чергу; початок реалізації не підтверджений.
- Виконано тільки dispatch --dry-run: task eligible, це НЕ фактичний запуск worker. Наявний gateway повідомляє running. Його конфігурацію, дозволи та глобальну активну board не змінювали.
- ТЗ й докази лежать у цьому каталозі; body task містить HERMES_HANDOFF.md з точними посиланнями та повноваженнями.
- Код продукту аудитором не змінювався; нові docs/audits/ артефакти збережені без коміту. Немає push/deploy.

## Реєстр ресурсів аудиту перед фінальним cleanup

| Ресурс | Власник | Старт / ідентифікатор | Стан перед cleanup | Дія |
|---|---|---|---|---|
| Backend audit subagent | Codex audit | backend_audit / gpt-6-luna | Finished; 419 tests | Немає активного процесу |
| Frontend audit subagent | Codex audit | frontend_audit / gpt-6-luna | Finished; lint/tsc/build | Немає активного процесу |
| Bot tests | backend subagent | dotnet test | 111 tests, exited | Немає активного процесу |
| Next dev :3101 | Codex audit | exec session 61993 | Зупинений Ctrl-C для відновлення після .next collision | Немає |
| Перша спроба next start :3101 | Codex audit | exec session 37091 | Зупинений Ctrl-C | Немає |
| Послідовний production build | Codex audit | exec session 27398 | Exit 0 | Зберегти артефакти |
| Остаточний next start :3101 | Codex audit | exec session 62205; старт близько 13:05 Europe/Kiev | Running перед cleanup | Ctrl-C, перевірити port/process descendants |
| HTTP smoke Python | Codex audit | exec 12231 і наступний повтор | Exited; перший stdout encoding error, повтор збережений | Немає |
| Browser tabs 1/2 | Codex audit | in-app browser | Тимчасові, не marked deliverable/handoff | Автоматично закриваються наприкінці turn |
| Hermes kanban board/card | Передана користувачем робота | movie-discovery / t_cf58bb8d | Durable ready queue; НЕ disposable | Зберегти |
| .next/bin/obj | Існували до аудиту, оновлені збіркою | frontend/backend/bot | Build outputs | Не видаляти pre-existing directories |
| docs/audits/2026-11-09 | Постійний результат аудиту | 6 документів/доказів | Saved | Зберегти |
| moviediscovery-web/api/bot/tunnel/db | Зовнішній запуск, не audit-owned | 6cc7ee8372ae / 5f8d1ea23331 / 406c2ea0a0fa / d5d40cd87764 / ae0addb940cc | Running, DB healthy | Не чіпати |
| Наявні Hermes desktop/gateway та інші процеси | Користувач / невстановлений | Існували до передачі | Running | Не чіпати |

Фактичні результати останньої фази: ENVIRONMENT_CLEANUP.md. Жодні тестові Docker/БД/тунелі аудитором не створювалися.
