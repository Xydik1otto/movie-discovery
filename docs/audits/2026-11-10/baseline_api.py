"""Bounded read-only acceptance baseline. No auth, imports, enrichment or DB writes."""
import concurrent.futures
import json
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

BASE = 'http://localhost:3001'
QUERIES = [
    ('film-page1', '/api/search?type=movie&page=1&pageSize=20&minVotes=10000&sort=rating'),
    ('film-page2', '/api/search?type=movie&page=2&pageSize=20&minVotes=10000&sort=rating'),
    ('series', '/api/search?type=series&pageSize=20'),
    ('show', '/api/search?type=show&pageSize=20'),
    ('anime', '/api/search?type=anime&pageSize=20'),
    ('manga', '/api/search?type=manga&pageSize=20'),
    ('anime-film', '/api/search?type=anime&category=anime&format=movie&pageSize=20'),
    ('uk-audio', '/api/search?audioLang=ukr&pageSize=20'),
    ('ru-audio', '/api/search?audioLang=russ&pageSize=20'),
    ('no-match', '/api/search?q=zzzxqvnonexistentcodex20261110&pageSize=20'),
    ('mars', '/api/search?q=Mission%20to%20Mars&pageSize=20&lang=uk'),
    ('mars-detail', '/api/title/517682?lang=uk'),
    ('mars-watch', '/api/titles/517682/watch'),
    ('mars-similar', '/api/title/517682/similar?limit=20&lang=uk'),
    ('anime-detail', '/api/title/528?lang=uk'),
    ('anime-watch', '/api/titles/528/watch'),
    ('health', '/api/health'),
]

def probe(entry):
    name, path = entry
    start = time.monotonic()
    try:
        with urllib.request.urlopen(BASE + path, timeout=25) as response:
            data = json.load(response)
            result = dict(name=name, path=path, status=response.status,
                          seconds=round(time.monotonic()-start, 3), body=data)
    except Exception as error:
        result = dict(name=name, path=path, error=str(error),
                      seconds=round(time.monotonic()-start, 3))
    print(json.dumps({k:v for k,v in result.items() if k != 'body'}, ensure_ascii=True), flush=True)
    return result

if __name__ == '__main__':
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        results = list(pool.map(probe, QUERIES))
    Path(__file__).with_name('BASELINE_API.json').write_text(
        json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')
    print('Saved BASELINE_API.json', flush=True)
