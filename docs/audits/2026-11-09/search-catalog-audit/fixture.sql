-- Apply ONLY to ownership-verified disposable codex-search-audit PostgreSQL.
INSERT INTO titles(id,type,title_en,year,country,countries,genres,keywords,actors,director,directors,runtime_min,episode_runtime_min,seasons,status,original_language,rating_imdb,rating_tmdb,votes_imdb,popularity,format,score_anilist,score_mal,created_at,updated_at)
OVERRIDING SYSTEM VALUE VALUES
(101,'movie','Audit Alpha',2010,'US','{US}','{action,science fiction}','{heist}','{Alice Actor}','Dana Director','{Dana Director}',100,NULL,NULL,'released','en',8.5,8,2000,1000,NULL,NULL,NULL,now(),now()),
(102,'series','Audit Series',2012,'GB','{GB}','{drama}','{court}','{Bob Actor}','Other Director','{Other Director}',NULL,25,3,'ended','en',9,8.7,5000,900,NULL,NULL,NULL,now(),now()),
(103,'show','Audit Talk',2020,'UA','{UA}','{talk-show}','{}','{}',NULL,NULL,NULL,45,2,'returning','uk',7,NULL,100,800,NULL,NULL,NULL,now(),now()),
(104,'anime','Audit Anime TV',2022,'JP','{JP}','{action,fantasy}','{sword}','{}',NULL,NULL,24,24,NULL,'airing','ja',NULL,NULL,NULL,700,'tv',2,2,now(),now()),
(105,'anime','Audit Anime Film',2021,'JP','{JP}','{drama}','{}','{}',NULL,NULL,110,NULL,NULL,'finished','ja',NULL,NULL,NULL,600,'movie',9.8,9.8,now(),now()),
(106,'manga','Audit Manga',2019,'JP','{JP}','{fantasy}','{}','{}',NULL,NULL,NULL,NULL,NULL,'finished','ja',NULL,NULL,NULL,500,'manga',3,3,now(),now()),
(107,'manga','Audit Novel',2018,'JP','{JP}','{drama}','{}','{}',NULL,NULL,NULL,NULL,NULL,'finished','ja',NULL,NULL,NULL,400,'novel',9.7,9.7,now(),now()),
(108,'movie','Audit Cartoon',2005,'US','{US}','{animation,comedy}','{}','{}',NULL,NULL,90,NULL,NULL,'released','en',7.5,NULL,1000,300,NULL,NULL,NULL,now(),now()),
(109,'series','Audit Dorama Country',2015,'KR','{}','{drama}','{}','{}',NULL,NULL,NULL,55,1,'ended','ko',8,NULL,500,200,NULL,NULL,NULL,now(),now()),
(110,'series','Audit Dorama Array',2015,NULL,'{KR}','{drama}','{}','{}',NULL,NULL,NULL,55,1,'ended','ko',8,NULL,500,190,NULL,NULL,NULL,now(),now()),
(111,'movie','Audit Cross Links',2017,'US','{US}','{thriller}','{}','{}',NULL,NULL,95,NULL,NULL,'released','en',6,NULL,800,180,NULL,NULL,NULL,now(),now()),
(112,'movie','Alpha Audit Remake',2023,'US','{US}','{action}','{}','{}',NULL,NULL,101,NULL,NULL,'released','en',5,NULL,600,1100,NULL,NULL,NULL,now(),now());
UPDATE titles SET trailer_yt_id='fixture-trailer',poster_path='/fixture.jpg' WHERE id=101;
UPDATE titles SET title_uk='Аудит Альфа' WHERE id=101;
INSERT INTO watch_links(title_id,provider,kind,region,url,is_available,checked_at,audio_lang,quality,dub) VALUES
(101,'fixture-UA','free','UA','https://example.com/alpha',true,now(),'ukr',1080,'ukr'),
(111,'fixture-low','free','UA','https://example.com/low',true,now(),'ukr',480,'ukr'),
(111,'fixture-high','free','US','https://example.com/high',true,now(),'eng',1080,'eng');
INSERT INTO providers(title_id,provider,kind,region,url,source,updated_at) VALUES
(101,'fixture-provider','stream','UA','https://example.com/alpha','fixture',now());
