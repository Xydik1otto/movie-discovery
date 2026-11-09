-- Фікстурні тайтли для e2e/md_audit_fixes.py (ізольований стек docker-compose.test.yml).
-- Застосування: docker exec moviediscovery-test-db psql -U movie -d moviediscovery_test -f -  < e2e/seed_fixture.sql
-- Або: docker exec -i moviediscovery-test-db psql -U movie -d moviediscovery_test < e2e/seed_fixture.sql
-- Колізія MD-03: A (idsequent) має internal Id == B.tmdb_id (перевіряти після INSERT: id другого рядка).
INSERT INTO titles (type, title_en, year, countries, genres, keywords, actors, popularity, rating_imdb, created_at, updated_at) VALUES
 ('movie','Collision Target A',2010,'{US}','{action,science fiction}','{heist}','{Bob}',90,7.5,now(),now()),
 ('movie','Collision Twin B',2011,'{US}','{drama}','{slow}','{Zed}',80,7.0,now(),now()),
 ('movie','Space Heist C',2012,'{US}','{science fiction,adventure}','{heist,space}','{Alice}',70,6.8,now(),now()),
 ('movie','Drama Quiet D',2015,'{UK}','{drama}','{slow}','{Carol}',60,6.0,now(),now()),
 ('anime','Void Blade E',2019,'{JP}','{action,fantasy}','{sword}','{Dave}',50,7.2,now(),now()),
 ('manga','Ink Scroll F',2020,'{JP}','{fantasy}','{ink}','{Eve}',40,0,now(),now());

-- B.TmdbId = A.Id (колізія просторів: GET ids=<A> сіє A, POST tmdbIds=<A> сіє B).
UPDATE titles SET tmdb_id = (SELECT min(id) FROM titles WHERE title_en = 'Collision Target A')
 WHERE title_en = 'Collision Twin B';

SELECT id, title_en, tmdb_id FROM titles ORDER BY id;
