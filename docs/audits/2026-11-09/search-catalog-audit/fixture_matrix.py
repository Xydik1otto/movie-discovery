import urllib.request,urllib.error,urllib.parse,json,pathlib,time,http.cookiejar,sys
BASE='http://localhost:3201'; OUT=pathlib.Path(__file__).parent
rows=[]
def call(path,params=None,body=None,client=None):
    url=BASE+path+('?' + urllib.parse.urlencode(params) if params else '')
    req=urllib.request.Request(url,json.dumps(body).encode() if body is not None else None,headers={'Content-Type':'application/json'})
    t=time.monotonic()
    try:
        with (client or urllib.request).open(req,timeout=15) if client else urllib.request.urlopen(req,timeout=15) as r:
            return r.status,json.load(r),round((time.monotonic()-t)*1000)
    except urllib.error.HTTPError as e:
        try:b=json.loads(e.read())
        except Exception:b={}
        return e.code,b,round((time.monotonic()-t)*1000)
def test(name,params,expected=None,first=None,status=200):
    params={'q':'Audit','pageSize':50,'lang':'en',**params}
    s,b,ms=call('/api/search',params);items=b.get('items',[]);ids=[x['id'] for x in items]
    ok=s==status and (expected is None or set(ids)==set(expected)) and (first is None or (ids and ids[0]==first))
    rows.append({'name':name,'pass':bool(ok),'status':s,'ms':ms,'expected':expected,'expectedFirst':first,'ids':ids,'total':b.get('total')})
for k,ids in [('movie',[101,108,111,112]),('series',[102,109,110]),('show',[103]),('anime',[104,105]),('manga',[106,107])]:test('type_'+k,{'type':k},ids)
test('anime_format_movie',{'type':'anime','format':'movie'},[105])
test('anime_format_tv',{'type':'anime','format':'tv'},[104])
test('manga_format_novel',{'type':'manga','format':'novel'},[107])
test('anime_score_sort',{'type':'anime','sort':'rating'},first=105)
test('manga_score_sort',{'type':'manga','sort':'rating'},first=107)
for n,p,ids in [
 ('uk_title',{'q':'Аудит Альфа'},[101]),('actor',{'actor':'Alice Actor'},[101]),('director',{'director':'Dana Director'},[101]),
 ('keyword',{'keyword':'heist'},[101]),('genre_uk',{'genres':'фантастика'},[101]),('exclude_genre',{'type':'anime','excludeGenres':'fantasy'},[105]),
 ('country_ua',{'country':'UA'},[103]),('country_array',{'country':'KR'},[109,110]),('language',{'language':'ja'},[104,105,106,107]),
 ('rating',{'minRating':8.4,'ratingSource':'imdb'},[101,102]),('year',{'yearFrom':2010,'yearTo':2012},[101,102]),
 ('runtime_series',{'type':'series','minRuntime':20,'maxRuntime':30},[102]),('runtime_category_series',{'category':'series','minRuntime':20,'maxRuntime':30},[102]),
 ('status_anime',{'type':'anime','status':'airing'},[104]),('votes',{'minVotes':2000},[101,102]),('seasons',{'seasonsFrom':2},[102,103]),
 ('trailer',{'hasTrailer':'true'},[101]),('providers',{'hasProviders':'true'},[101]),('watchable',{'watchable':'true'},[101,111]),
 ('audio_quality_same_link',{'audioLang':'ukr','minQuality':1080},[101]),('category_dorama',{'category':'dorama'},[109,110]),
 ('category_cartoon',{'category':'cartoon'},[108]),('category_tvshow',{'category':'tvshow'},[103]),
]:test(n,p,ids)
test('invalid_type',{'type':'nonsense'},status=400)
test('invalid_genres',{'genres':'nonsense'},status=400)
test('invalid_reversed_year',{'yearFrom':2025,'yearTo':1900},status=400)
test('page_overflow',{'page':2147483647},status=400)
s,b,ms=call('/api/title/109',{'lang':'en'});rows.append({'name':'country_in_detail','pass':'KR' in b.get('countries',[]),'status':s,'countries':b.get('countries')})
s,b,ms=call('/api/title/102',{'lang':'en'});rows.append({'name':'episode_runtime_in_detail','pass':b.get('runtimeMin')==25,'status':s,'runtimeMin':b.get('runtimeMin')})
# All authenticated writes are confined to this disposable DB.
c=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
stamp=int(time.time());s,b,ms=call('/api/auth/register',body={'email':f'catalog{stamp}@test.ua','username':f'catalog{stamp}','password':'Strong1Pass'},client=c)
rows.append({'name':'fixture_register','pass':s==200,'status':s})
for count in [1,10,50]:
    s,b,ms=call('/api/discover',{'count':count},client=c);items=b.get('items',[])
    rows.append({'name':f'discover_{count}','pass':s==200 and len(items)<=count and len({i['id'] for i in items})==len(items),'status':s,'count':len(items),'ids':[i['id'] for i in items]})
s,b,ms=call('/api/discover',{'count':5},client=c);before=[i['id'] for i in b.get('items',[])]
if before:
    sid=before[0];act=call('/api/discover/action',body={'titleId':sid,'action':'skip'},client=c)
    s,b,ms=call('/api/discover',{'count':50},client=c);after=[i['id'] for i in b.get('items',[])]
    rows.append({'name':'discover_skip_not_repeated','pass':sid not in after,'skipped':sid,'afterIds':after,'actionStatus':act[0]})
for path in ['/api/collections/presets','/api/similar/by-seeds?ids=101,104&limit=10','/api/title/105/similar?limit=5']:
    s,b,ms=call(path);rows.append({'name':path,'pass':s==200,'status':s,'body':b})
(OUT/'fixture-matrix.json').write_text(json.dumps(rows,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'passed':sum(x['pass'] for x in rows),'failed':[x for x in rows if not x['pass']]},ensure_ascii=True,indent=2))
sys.exit(1 if any(not x['pass'] for x in rows) else 0)
