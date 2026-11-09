"""Read-only bounded public API audit; no login, mutations, or imports."""
import concurrent.futures, json, pathlib, time, urllib.request, urllib.parse, urllib.error

OUT = pathlib.Path(__file__).parent
BASE = 'http://localhost:3001'
cases = []
def add(name, path='/api/search', **params):
    if path == '/api/search': params = {'pageSize': 12, 'lang': 'en', **params}
    cases.append((name, path + ('?' + urllib.parse.urlencode(params) if params else '')))

add('health','/api/health')
for kind in ['movie','series','show','anime','manga']:
    add('type_'+kind,type=kind,sort='popularity')
for cat in ['movie','series','tvshow','cartoon','dorama','anime','manga']:
    add('category_'+cat,category=cat,sort='popularity')
for name,q in [('inception','Inception'),('inception_uk','Початок'),('imdb_id','tt1375666'),('naruto','Naruto'),('naruto_uk','Наруто'),('titan_en','Attack on Titan'),('titan_romaji','Shingeki no Kyojin'),('titan_jp','進撃の巨人'),('matrix','The Matrix'),('office','The Office'),('actor','Leonardo DiCaprio'),('director','Christopher Nolan'),('empty_hit','zzzzNoSuchTitle947183')]:
    add('q_'+name,q=q)
for fmt in ['tv','movie','ova','ona','special','impossible_format']:
    add('anime_format_'+fmt,type='anime',format=fmt,sort='popularity')
for fmt in ['manga','novel','one_shot']:
    add('manga_format_'+fmt,type='manga',format=fmt,sort='popularity')
for typ in ['anime','manga']:
    for sort in ['popularity','rating','year']:
        add(typ+'_sort_'+sort,type=typ,sort=sort)
    for status in ['airing','finished']:
        add(typ+'_status_'+status,type=typ,status=status)
for sort in ['popularity','relevance','rating','imdb','votes','year']:
    add('inception_sort_'+sort,q='Inception',sort=sort)
for page in [1,2,3]: add('anime_page_'+str(page),type='anime',page=page,pageSize=24,sort='popularity')
for name,params in [
    ('genre_uk',{'genres':'фантастика','type':'movie'}),
    ('year_range',{'yearFrom':2010,'yearTo':2010,'type':'movie'}),
    ('rating_imdb',{'minRating':9,'ratingSource':'imdb','sort':'imdb'}),
    ('country_ua',{'country':'UA'}),('country_jp_anime',{'type':'anime','country':'JP'}),
    ('runtime_series',{'type':'series','minRuntime':20,'maxRuntime':30}),
    ('actor_filter',{'actor':'Leonardo DiCaprio'}),('director_filter',{'director':'Christopher Nolan'}),
    ('language_ja',{'type':'anime','language':'ja'}),('seasons',{'type':'series','seasonsFrom':2}),
    ('trailer',{'hasTrailer':'true'}),('watchable',{'watchable':'true'}),
    ('audio_ukr',{'audioLang':'ukr'}),('quality',{'minQuality':1080}),
    ('bad_type',{'type':'invalid'}),('bad_genre',{'genres':'noSuchGenre'}),
    ('bad_year_range',{'yearFrom':2025,'yearTo':1900}),('bad_page',{'page':-1}),
    ('large_page',{'page':2147483647}),('wildcard_percent',{'q':'%'}),
]: add(name,**params)
for path in ['/api/anime/catalog','/api/manga/catalog','/api/collections/presets','/api/discover','/api/collections','/api/auth/me','/api/title/1218055','/api/title/528','/api/titles/1218055/watch','/api/title/528/similar','/api/similar/by-seeds']:
    params={'ids':'528','limit':5} if path.endswith('by-seeds') else {'limit':5} if path.endswith('/similar') else {}
    add(path,path,**params)

def probe(case):
    name,path=case; t=time.monotonic(); row={'name':name,'path':path}
    try:
        with urllib.request.urlopen(BASE+path,timeout=24) as r:
            data=r.read();row.update(status=r.status,ms=round((time.monotonic()-t)*1000),body=json.loads(data))
    except urllib.error.HTTPError as e:
        data=e.read().decode('utf-8',errors='replace')
        try: body=json.loads(data)
        except Exception: body=data[:500]
        row.update(status=e.code,ms=round((time.monotonic()-t)*1000),body=body)
    except Exception as e: row.update(status=None,ms=round((time.monotonic()-t)*1000),error=str(e))
    return row

if __name__=='__main__':
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        rows=list(pool.map(probe,cases))
    (OUT/'live-matrix.json').write_text(json.dumps(rows,ensure_ascii=False,indent=2),encoding='utf-8')
    for r in rows:
        b=r.get('body',{});b=b if isinstance(b,dict) else {}
        print(json.dumps({'name':r['name'],'status':r['status'],'ms':r['ms'],'total':b.get('total'),'count':len(b.get('items',[])),'error':r.get('error')},ensure_ascii=True))
