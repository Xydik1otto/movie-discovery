# -*- coding: utf-8 -*-
"""MD-09: заміри latency на read-only запитах до ЖИВОГО стеку (:5200) і fixture (:5300).

Read-only: GET search/detail/similar/health. Нічого не змінює. Результат у docs/audits/2026-11-09/perf.md.
Замір на fixture (порожня БД, 6 тайтлів) — тільки smoke latency, НЕ доказ NFR-1 (це фіксуватиме звіт).
"""
import json, statistics, sys, time, urllib.request

TARGETS = {
    "live5200": "http://localhost:5200",
    "fixture5300": "http://localhost:5300",
}

def timed(base, path, n=5):
    xs = []
    for _ in range(n):
        t0 = time.time()
        try:
            with urllib.request.urlopen(base + path, timeout=60) as r:
                r.read()
            xs.append((time.time() - t0) * 1000)
        except Exception as e:
            return {"error": str(e)}
    return {"n": len(xs), "median_ms": round(statistics.median(xs), 1),
            "p95_ms": round(sorted(xs)[int(0.95 * (len(xs) - 1))], 1) if len(xs) > 1 else xs[0],
            "min_ms": round(min(xs), 1), "max_ms": round(max(xs), 1)}

ROUTES = [
    ("search_inception", "/api/search?q=Inception&page=1&pageSize=24&lang=uk"),
    ("search_filtered", "/api/search?q=Inception&yearFrom=2000&yearTo=2020&minRating=5&page=1&pageSize=24&lang=uk"),
    ("health", "/api/health"),
]

rows = {}
for name, base in TARGETS.items():
    rows[name] = {}
    # health перший — warmup + перевірка живості
    h = timed(base, "/api/health", n=2)
    if "error" in h:
        rows[name]["UNAVAILABLE"] = h["error"]
        continue
    titles = None
    try:
        with urllib.request.urlopen(base + "/api/health", timeout=10) as r:
            titles = json.loads(r.read()).get("titlesCount")
    except Exception:
        pass
    rows[name]["titlesCount"] = titles
    for label, path in ROUTES:
        rows[name][label] = timed(base, path, n=5)
    # detail + similar — перший результат search_inception
    try:
        with urllib.request.urlopen(base + ROUTES[0][1], timeout=60) as r:
            first = json.loads(r.read())["items"][0]
        rows[name]["title_detail"] = timed(base, f"/api/title/{first['id']}?lang=uk", n=5)
        rows[name]["similar_by_title"] = timed(base, f"/api/title/{first['id']}/similar?limit=20&lang=uk", n=5)
    except Exception as e:
        rows[name]["title_detail"] = {"error": str(e)}

out = json.dumps(rows, indent=2, ensure_ascii=False)
print(out)
with open("docs/audits/2026-11-09/perf-probe.json", "w", encoding="utf-8") as f:
    f.write(out)
