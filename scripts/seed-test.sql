-- DEV-SEED для смоук-тестів без TMDB-ключа.
-- УВАГА: назви/описи тут правдоподібні, але не канонічні — перший справжній
-- sync (POST /api/admin/sync/popular з TMDB_API_KEY) перезапише їх upsert-ом.
-- Ідемпотентність: спочатку видаляємо, потім вставляємо.
-- poster_path: тільки реально перевірені на image.tmdb.org шляхи (200); решта NULL -> градієнт-плейсхолдер у UI.
-- Запуск: docker exec -i moviediscovery-db psql -U movie -d moviediscovery < scripts/seed-test.sql

DELETE FROM lists WHERE title_id IN (SELECT id FROM titles WHERE tmdb_id IN (272,155,680,603,11,1396));
DELETE FROM providers WHERE title_id IN (SELECT id FROM titles WHERE tmdb_id IN (272,155,680,603,11,1396));
DELETE FROM titles WHERE tmdb_id IN (272,155,680,603,11,1396);

INSERT INTO titles
  (tmdb_id, type, title_uk, title_en, overview_uk, overview_en, year, countries, genres, keywords, actors, director, runtime_min, episode_runtime_min, seasons, status, original_language, rating_tmdb, rating_imdb, votes_imdb, popularity, poster_path, trailer_yt_id, created_at, updated_at)
VALUES
  (272, 'movie', 'Початок', 'Inception',
   'Злодій, який краде секрети з підсвідомості, отримує завдання посадити ідею у розум керівника корпорації. Багатошарова sci-fi пригода про сни, вкладені в сни.',
   'A thief who steals corporate secrets through dream-sharing technology is given the inverse task of planting an idea into the mind of a C.E.O.',
   2010, '{US,GB}', '{science fiction,thriller,action}', '{dreams,heist,mind-bending}', '{Leonardo DiCaprio,Joseph Gordon-Levitt,Elliot Page,Tom Hardy}', 'Christopher Nolan',
   148, NULL, NULL, 'released', 'en', 8.4, 8.8, 36000, 95.5, NULL, 'YoHD9XEInc0', now(), now()),

  (155, 'movie', 'Темний лицар', 'The Dark Knight',
   'Бетмен зіштовхується з анархістом Джокером, який занурює Ґотем у хаос. Темний кримінальний трилер про межі моральності.',
   'Batman raises the stakes in his war on crime with the help of Lt. Jim Gordon and District Attorney Harvey Dent — until the Joker unleashes chaos.',
   2008, '{US}', '{action,crime,drama,thriller}', '{superhero,chaos,anarchy,justice}', '{Christian Bale,Heath Ledger,Aaron Eckhart,Michael Caine}', 'Christopher Nolan',
   152, NULL, NULL, 'released', 'en', 8.5, 9.0, 34000, 110.2, NULL, 'EXeTwQWrcwY', now(), now()),

  (680, 'movie', 'Кримінальне чтиво', 'Pulp Fiction',
   'Кілька переплетених історій лос-анджелеських гангстерів, боксера і пари грабіжників ресторанів. Нелінійний культовий кримінал.',
   'A burger-loving hit man, his philosophical partner, a drug-addled gangster''s moll and a washed-up boxer converge in three stories of violence and redemption.',
   1994, '{US}', '{crime,drama,thriller}', '{nonlinear,gangster,dialogue}', '{John Travolta,Samuel L. Jackson,Uma Thurman,Bruce Willis}', 'Quentin Tarantino',
   154, NULL, NULL, 'released', 'en', 8.5, 8.9, 27000, 88.1, NULL, 's7EdQ4FqbhY', now(), now()),

  (603, 'movie', 'Матриця', 'The Matrix',
   'Хакер Нео дізнається, що реальність — симуляція, і приєднується до повстання машин. Культовий sci-fi бойовик.',
   'When a beautiful stranger leads computer hacker Neo to a forbidding underworld, he discovers the shocking truth: the life he knows is an elaborate deception.',
   1999, '{US,AU}', '{science fiction,action,thriller}', '{simulation,dystopia,chosen-one}', '{Keanu Reeves,Laurence Fishburne,Carrie-Anne Moss,Hugo Weaving}', 'Lana Wachowski',
   136, NULL, NULL, 'released', 'en', 8.2, 8.7, 25000, 90.7, NULL, 'vKQi3bBA1y8', now(), now()),

  (11, 'movie', 'Зоряні війни: Нова надія', 'Star Wars',
   'Фермер Люк приєднується до повстанців проти Галактичної імперії. Класична космічна опера.',
   'Luke Skywalker joins forces with a Jedi Knight, a cocky pilot and two droids to save the galaxy from the Empire''s battle station.',
   1977, '{US}', '{science fiction,action,adventure}', '{space,hero,journey}', '{Mark Hamill,Harrison Ford,Carrie Fisher,Alec Guinness}', 'George Lucas',
   121, NULL, NULL, 'released', 'en', 8.2, 8.6, 19000, 70.3, NULL, 'vZ734NWnAHA', now(), now()),

  (1396, 'series', 'Пустити в продаж', 'Breaking Bad',
   'Вчитель хімії з діагнозом починає варити метамфетамин, щоб забезпечити сім''ю, і поступово перетворюється на наркобарона. Кримінальна драма про трансформацію.',
   'A chemistry teacher diagnosed with terminal cancer teams up with a former student to secure his family''s future by cooking methamphetamine.',
   2008, '{US}', '{crime,drama,thriller}', '{chemistry,transformation,antihero}', '{Bryan Cranston,Aaron Paul,Anna Gunn,Giancarlo Esposito}', NULL,
   NULL, 47, 5, 'ended', 'en', 8.9, 9.5, 14000, 120.0, '/ggFHVNu6YYI5L9pCfOacjizRGt.jpg', 'HhesaQXLuRY', now(), now());

INSERT INTO providers (title_id, provider, kind, url, source, region, updated_at)
SELECT id, 'Megogo', 'rent', 'https://megogo.net/ua/search?q=' || title_en, 'seed', 'UA', now() FROM titles WHERE tmdb_id IN (272,603);
INSERT INTO providers (title_id, provider, kind, url, source, region, updated_at)
SELECT id, 'Netflix', 'stream', NULL, 'seed', 'UA', now() FROM titles WHERE tmdb_id = 155;
