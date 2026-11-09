-- Fixture-сід для ізольованого тестового стеку (MD-10).
-- Виконується ПІСЛЯ старту api-test (EF-міграції застосовані) через:
--   docker exec -i moviediscovery-test-db psql -U movie -d moviediscovery_test < seed_fixtures.sql
-- Детерміновані id; колізійна фікстура MD-03: title 7000 (id, tmdb_id=NULL)
-- і title 7001 з tmdb_id=7000 → ids=7000 резолвить 7000, legacy tmdbIds=7000 → 7001.
TRUNCATE list_entries, user_profiles, bot_identities, profiles, titles RESTART IDENTITY CASCADE;

INSERT INTO profiles (id, name, is_default) VALUES
  (1, 'Guest', true);

INSERT INTO titles (id, type, title_en, title_uk, year, genres, popularity, runtime_min) VALUES
  (7000, 'movie', 'Inception Test', 'Початок (тест)', 2010, '{drama,science fiction}', 50.0, 148),
  (7001, 'movie', 'Collision Fixture', 'Колізія', 1999, '{drama}', 40.0, 120),
  (7002, 'movie', 'Shawshank Test', 'Втеча (тест)', 1994, '{drama,crime}', 60.0, 142),
  (7003, 'anime', 'Anime Fixture', 'Аніме (тест)', 2015, '{action,adventure}', 30.0, 24),
  (7004, 'manga', 'Manga Fixture', 'Манга (тест)', 2012, '{adventure}', 20.0, NULL);

INSERT INTO list_entries (id, profile_id, kind, title_id, created_at) VALUES
  (900, 1, 'watchlist', 7000, now());
SELECT setval('list_entries_id_seq', 1000, false);
