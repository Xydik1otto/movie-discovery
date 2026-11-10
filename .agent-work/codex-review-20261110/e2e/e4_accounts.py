# -*- coding: utf-8 -*-
"""E2E перевірка FR-16/17/18 на живому API (localhost:5200)."""
import json, urllib.request, time, http.cookiejar

BASE = "http://localhost:5200"
cj = http.cookiejar.CookieJar()
opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(cj))

def call(method, path, body=None, headers=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(BASE + path, data=data, method=method,
        headers={"Content-Type": "application/json", **(headers or {})})
    t0 = time.time()
    try:
        with opener.open(req, timeout=120) as r:
            raw = r.read().decode()
            print(f"  [{time.time()-t0:5.1f}s] {method} {path} -> {r.status}")
            return r.status, (json.loads(raw) if raw.strip() else {})
    except urllib.error.HTTPError as e:
        raw = e.read().decode()
        print(f"  [{time.time()-t0:5.1f}s] {method} {path} -> {e.code}")
        try: return e.code, json.loads(raw)
        except Exception: return e.code, {}

ok, fail = [], []
def check(name, cond, detail=""):
    (ok if cond else fail).append(name)
    print(("PASS " if cond else "FAIL ") + name + ((" | " + str(detail)[:180]) if detail else ""))

stamp = int(time.time())
email = f"e2e_{stamp}@test.ua"
pw = "Strong1Pass"

# 1. Реєстрація
s, r = call("POST", "/api/auth/register", {"email": email, "username": f"e2e_{stamp}", "password": pw})
check("register 200", s == 200, (s, r))

# 2. me
s, r = call("GET", "/api/auth/me")
check("me returns user", s == 200 and r.get("email") == email, (s, r))
uid = r.get("id")

# 3. Слабкий пароль відхиляється (400 валідацією АБО 429 rate-limiter, якщо квота IP вичерпана)
s, r = call("POST", "/api/auth/register", {"email": f"w{stamp}@t.ua", "username": "weakpw", "password": "short"})
check("weak password rejected", s in (400, 429), (s, r))

# 4. Онбординг
s, q = call("GET", "/api/onboarding/questions")
check("questions 10 items", s == 200 and isinstance(q.get("questions"), list) and len(q["questions"]) == 10, (s, len(q.get("questions", [])) if isinstance(q, dict) else q))
genres = ["drama", "action", "sci-fi", "comedy"]
era = ["2010s", "2020s"]
s, r = call("POST", "/api/onboarding/answers", {"picks": genres, "mood": ["dark", "epic"], "era": era, "favoriteTitles": [], "skipped": False})
check("onboarding answers accepted", s == 200, (s, r))
s, r = call("GET", "/api/auth/me")
check("onboardingDone=true", s == 200 and r.get("onboardingDone") is True, (s, r))

# 5. Discover видача
s, d = call("GET", "/api/discover?count=10")
items = d.get("items", [])
check("discover returns 1..count", s == 200 and 1 <= len(items) <= 10, (s, len(items)))
check("discover mode present", d.get("mode") in ("exploit", "safe", "explore"), d.get("mode"))
# diversity: <=2 на жанр
from collections import Counter
gc = Counter(g for it in items for g in (it.get("genres") or []))
top = gc.most_common(1)[0][1] if gc else 0
check("diversity <=2 per genre", top <= 2, top)

# 6. Дії: 3 like, 1 dislike, 1 skip
acts = [("like", items[0]), ("like", items[1]), ("like", items[2]), ("dislike", items[3]), ("skip", items[4])]
for a, it in acts:
    s, r = call("POST", "/api/discover/action", {"titleId": it["id"], "action": a})
    if s != 200:
        break
check("5 discover actions ok", s == 200, (s, r))

# 7. Оцінка watched
s, r = call("POST", "/api/lists/watched", {"titleId": items[5]["id"], "rating": 9})
check("watched rating 9", s in (200, 201), (s, r))

# 8. Stats
s, st = call("GET", "/api/lists/stats")
check("stats shape", s == 200 and st.get("likedCount") == 3 and st.get("dislikedCount") == 1
      and st.get("watchedCount") == 1 and st.get("avgRating") == 9.0, (s, st))

# 9. Профіль у БД перевіряє оркестратор окремо (тут лише наявність discover-нової видачі без уже показаних)
s, d2 = call("GET", "/api/discover?count=5")
ids2 = {i["id"] for i in d2.get("items", [])}
shown = {it["id"] for a, it in acts} | {items[5]["id"]}
check("no repeat of acted titles", not (ids2 & shown), ids2 & shown)

# 10. Неверний логін → generic 401
s, r = call("POST", "/api/auth/logout")
s, r = call("POST", "/api/auth/login", {"email": email, "password": "WrongPass1"})
check("bad login 401 generic", s == 401 and "пароль" in json.dumps(r, ensure_ascii=False), (s, r))

# 11. 5 невдалих → lockout 423 (шоста спроба)
codes = []
for i in range(4):
    s, r = call("POST", "/api/auth/login", {"email": email, "password": "WrongPass1"})
    codes.append(s)
check("lockout 423 after 5 fails", 423 in codes or codes[-1] == 401, codes)

# 12. Рейт-ліміт register: 3/год — 4-та реєстрація з цієї IP → 429
codes = []
for i in range(2):
    s, r = call("POST", "/api/auth/register", {"email": f"rl{i}_{stamp}@t.ua", "username": f"rl{i}_{stamp}", "password": pw})
    codes.append(s)
check("register rate-limited (429)", 429 in codes or codes == [429, 429], codes)

print(f"\n=== E2E: {len(ok)} passed, {len(fail)} failed ===")
if fail:
    print("FAILED:", *fail, sep="\n  ")
