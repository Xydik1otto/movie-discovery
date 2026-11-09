-- Фікстурні тайтли для e2e/md_audit_fixes.py (ізольований стек docker-compose.test.yml).
-- Застосування: docker exec moviediscovery-test-db psql -U movie -d moviediscovery_test -f -  < e2e/seed_fixture.sql
-- Або: docker exec -i moviediscovery-test-db psql -U movie -d moviediscovery_test < e2e/seed_fixture.sql
--
-- CR-05: id ЗАКРІПЛЕНІ ЯВНО (1..6) — раннер не залежить від історії sequence
-- старого volume. На чистій БД: A.id=1, B.id=2 (tmdb_id=1), C=3, D=4, E=5, F=6.
-- TRUNCATE робить сід ІДЕМПОТЕНТНИМ: повторне застосування на будь-якому стані
-- fixture-БД дає той самий детермінований набір (перевіряти \d не треба — CASCADE).
TRUNCATE lists, user_collections, user_collection_items, user_profiles,
         bot_identities, interaction_log, profiles, titles RESTART IDENTITY CASCADE;
INSERT INTO titles (id, type, title_en, year, countries, genres, keywords, actors, popularity, rating_imdb, created_at, updated_at) OVERRIDING SYSTEM VALUE VALUES
 (1,'movie','Collision Target A',2010,'{US}','{action,science fiction}','{heist}','{Bob}',90,7.5,now(),now()),
 (2,'movie','Collision Twin B',2011,'{US}','{drama}','{slow}','{Zed}',80,7.0,now(),now()),
 (3,'movie','Space Heist C',2012,'{US}','{science fiction,adventure}','{heist,space}','{Alice}',70,6.8,now(),now()),
 (4,'movie','Drama Quiet D',2015,'{UK}','{drama}','{slow}','{Carol}',60,6.0,now(),now()),
 (5,'anime','Void Blade E',2019,'{JP}','{action,fantasy}','{sword}','{Dave}',50,7.2,now(),now()),
 (6,'manga','Ink Scroll F',2020,'{JP}','{fantasy}','{ink}','{Eve}',40,0,now(),now());

-- Продовження sequence ПІСЛЯ явних id — щоб наступні INSERT не колізійнули.
SELECT setval(pg_get_serial_sequence('titles','id'), (SELECT max(id) FROM titles));

-- Колізія просторів (MD-03): B.tmdb_id = A.id — GET ids=<A> сіє A, POST tmdbIds=<A> сіє B.
UPDATE titles SET tmdb_id = 1 WHERE id = 2;

-- Гостьовий профіль (designated guest, GUEST_PROFILE_ID=1) — TRUNCATE зняв
-- API-сід при старті, тому відтворюємо тут (created_at NOT NULL — обов'язково).
INSERT INTO profiles (id, name, is_default, created_at) OVERRIDING SYSTEM VALUE VALUES (1, 'Guest', true, now());
SELECT setval(pg_get_serial_sequence('profiles','id'), (SELECT max(id) FROM profiles));

SELECT id, title_en, tmdb_id FROM titles ORDER BY id;
