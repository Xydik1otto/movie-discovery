# -*- coding: utf-8 -*-
"""MD-01..MD-05/07 регресія на ізольованому fixture-стеку (docker-compose.test.yml).

Запуск: python e2e/md_audit_fixes.py [BASE]   (BASE за замовчуванням http://localhost:5300)
Передумова: стек піднятий, titles сідовані (e2e/seed_fixture.sql), БД порожня від юзерів.
Скрипт створює власні акаунти (унікальні email), не чіпає чужі дані, exit != 0 при FAIL.
"""
import json, os, sys, time, http.cookiejar, urllib.request, urllib.error

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://localhost:5300"

ok, fail = [], []

def check(name, cond, detail=""):
    (ok if cond else fail).append(name)
    print(("PASS " if cond else "FAIL ") + name + ((" | " + str(detail)[:200]) if detail else ""))

def new_client():
    """Новий «клієнт» = власний cookiejar (анонім або окрема сесія юзера)."""
    cj = http.cookiejar.CookieJar()
    return urllib.request.build_opener(urllib.request.HTTPCookieProcessor(cj))

def call(client, method, path, body=None, headers=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(BASE + path, data=data, method=method,
        headers={"Content-Type": "application/json", **(headers or {})})
    t0 = time.time()
    try:
        with client.open(req, timeout=60) as r:
            raw = r.read().decode()
            print(f"  [{time.time()-t0:5.2f}s] {method} {path} -> {r.status}")
            return r.status, (json.loads(raw) if raw.strip() else {})
    except urllib.error.HTTPError as e:
        raw = e.read().decode()
        print(f"  [{time.time()-t0:5.2f}s] {method} {path} -> {e.code}")
        try: return e.code, (json.loads(raw) if raw.strip() else {})
        except Exception: return e.code, {}

anon = new_client()
stamp = int(time.time())
pw = "Strong1Pass"

# --- 0. Health (MD-07): чесний статус ---
s, r = call(anon, "GET", "/api/health")
check("MD-07 health 200 + db=true + honest status", s == 200 and r.get("db") is True and r.get("status") in ("ok", "degraded"), r)
s, r = call(anon, "GET", "/api/health/ready")
check("MD-07 ready 200 ok (fixture db up)", s == 200 and r.get("status") == "ok", r)

# --- Реєстрація A і B (окремі клієнти-сесії). Register 3/год/IP, login 5/хв/IP (MD-07):
# для повторних прогонів задай MD_E2E_EMAIL_A/MD_E2E_EMAIL_B (пароль Strong1Pass) —
# тоді пропускаємо register і входимо через login (1 запит на клієнта). ---
def register(tag):
    env = os.environ.get(f"MD_E2E_EMAIL_{tag.upper()}")
    c = new_client()
    if env:
        s, r = call(c, "POST", "/api/auth/login", {"email": env, "password": pw})
        check(f"login {tag} ({env}) 200", s == 200, (s, r))
        return c, env
    email = f"mdfix_{tag}_{stamp}@test.ua"
    s, r = call(c, "POST", "/api/auth/register", {"email": email, "username": f"mdfix_{tag}_{stamp}", "password": pw})
    if s == 200:
        check(f"register {tag} 200", True)
        return c, email
    # 429 (rate limit) або 400 (exist) → login під акаунт попереднього прогону
    for prev in (f"mdfix_{tag}_{stamp - 47}@test.ua",
                 f"mdfix_{tag}_1794225948@test.ua",
                 f"mdfix_{tag}_1794225901@test.ua"):
        s2, r2 = call(c, "POST", "/api/auth/login", {"email": prev, "password": pw})
        if s2 == 200:
            check(f"register {tag} (fallback login {prev})", True, s)
            return c, prev
    check(f"register {tag} (fallback login failed)", False, (s, r))
    return c, email

A, emailA = register("a")
B, emailB = register("b")

def callA(method, path, body=None, headers=None): return call(A, method, path, body, headers)
def callB(method, path, body=None, headers=None): return call(B, method, path, body, headers)

s, r = callA("GET", "/api/auth/me")
check("me A returns user", s == 200 and r.get("email") == emailA, (s, r))

# --- MD-01: ownership ---
s, r = callA("GET", "/api/profiles")
check("MD-01 profiles GET (auth) → own profile", s == 200 and isinstance(r, list) and len(r) == 1, (s, r))
profileA = r[0]["id"] if s == 200 and r else None
check("MD-01 own profile id resolved", profileA is not None, r)

s, r = call(anon, "GET", "/api/profiles")
check("MD-01 anonymous profiles GET → 401", s == 401, (s, r))

s, r = call(anon, "GET", "/api/lists", headers={"X-Profile-Probe": "1"})
# анонім GET /api/lists → designated guest profile (200) АБО чесна відмова; головне — не чужі дані
check("MD-01 anonymous lists GET not leaking account data",
      s == 404 or (s == 200 and r.get("profileId") not in (None,) and isinstance(r.get("items"), list)), (s, r))
guest_profile_id = r.get("profileId") if s == 200 else None

# A додає тайтл 4 (Space Heist C) у watched (повторний прогін → 409 already_in_list = теж ок)
s, r = call(A, "POST", "/api/lists/watched", {"titleId": 4, "rating": 7})
check("MD-01 A adds own entry (201/409 idempotent)", s in (200, 201, 409), (s, r))
s, r = callA("GET", "/api/lists?kind=watched")
items = r.get("items", [])
itemA = next((i for i in items if i.get("id") == 4 or i.get("itemId")), None)
itemA_id = itemA.get("itemId") if itemA else None
check("MD-01 A sees own entry with itemId", itemA_id is not None, r)

# Анонім читає профіль A за відомим id → 403 (не 200 з даними)
s, r = call(anon, "GET", f"/api/lists?profileId={profileA}&kind=watched")
check("MD-01 anonymous read A by profileId → 403", s == 403, (s, r))

# Анонім змінює/видаляє запис A за itemId → 404 (не розкриває існування)
s, r = call(anon, "PATCH", f"/api/lists/{itemA_id}", {"rating": 1})
check("MD-01 anonymous PATCH A item → 404", s == 404, (s, r))
s, r = call(anon, "DELETE", f"/api/lists/{itemA_id}")
check("MD-01 anonymous DELETE A item → 404", s == 404, (s, r))

# B (інший акаунт) не бачить/не міняє записи A
s, r = callB("GET", "/api/lists?kind=watched")
check("MD-01 B does not see A entries", s == 200 and all((i.get("id") != 4) for i in r.get("items", [])), r)
s, r = callB("PATCH", f"/api/lists/{itemA_id}", {"rating": 2})
check("MD-01 B PATCH A item → 404", s == 404, (s, r))
s, r = callB("DELETE", f"/api/lists/{itemA_id}")
check("MD-01 B DELETE A item → 404", s == 404, (s, r))

# Валідні власні операції A досі працюють (регресія)
s, r = callA("PATCH", f"/api/lists/{itemA_id}", {"rating": 9})
check("MD-01 A own PATCH still works", s == 200 and r.get("rating") == 9, (s, r))

# Анонімна валідна гостьова операція (legacy surface, designated guest profile)
s, r = call(anon, "POST", "/api/lists/watched", {"titleId": 5})
check("MD-01 anonymous guest add → 201/409 on guest profile", s == 409 or (s in (200, 201) and (guest_profile_id is None or r.get("profileId") == guest_profile_id)), (s, r))
s, r = call(anon, "POST", "/api/lists/watched", {"titleId": 5, "profileId": profileA})
check("MD-01 anonymous add into A profile → 403", s == 403, (s, r))

# --- MD-02: онбординг-контракт ---
s, r = callA("POST", "/api/onboarding/answers",
                {"picks": ["action", "science fiction"], "mood": ["comedy"], "era": ["1990s"],
                 "favoriteTitles": [4, 5, 6], "skipped": False})
check("MD-02 valid answers → 200", s == 200 and r.get("onboardingDone") is True, (s, r))
s, r = callA("GET", "/api/auth/me")
check("MD-02 me.onboardingDone=true", s == 200 and r.get("onboardingDone") is True, r)
s, r = callA("POST", "/api/onboarding/answers", {})
check("MD-02 missing arrays → no 500 (400 or null-safe 200)", s in (200, 400), (s, r))
s, r = callA("POST", "/api/onboarding/answers",
                {"picks": ["action"], "mood": [], "era": [], "favoriteTitles": [4, 5, 6, 7], "skipped": False})
check("MD-02 favoriteTitles > 3 → 400", s == 400, (s, r))

# --- MD-03: простори ідентифікаторів (колізія A.Id == B.TmdbId = 2) ---
s, r = call(anon, "GET", "/api/similar/by-seeds?ids=2&limit=5")
items3 = r.get("items", [])
check("MD-03 GET ids=2 (internal A) → 200", s == 200 and len(items3) > 0, (s, r))
ids3 = [i.get("id") for i in items3]
check("MD-03 GET ids=2 does NOT resolve collision twin B (id=3)",
      s == 200 and 3 not in ids3, ids3)
s, r = call(anon, "GET", "/api/similar/by-seeds?ids=999999")
check("MD-03 GET unknown id → 404 titles_not_found + missing", s == 404 and r.get("error") == "titles_not_found" and 999999 in r.get("missing", []), (s, r))
s, r = call(anon, "GET", "/api/similar/by-seeds?ids=")
check("MD-03 GET empty ids → 400", s == 400, (s, r))
s, r = call(anon, "GET", "/api/similar/by-seeds?ids=1,2,3,4,5,6,7,8,9,10,11")
check("MD-03 GET 11 ids → 400", s == 400, (s, r))
s, r = call(anon, "POST", "/api/similar/by-seeds", {"tmdbIds": [2], "limit": 5})
itemsP = r.get("items", [])
check("MD-03 POST tmdbIds=2 (legacy typed) → 200", s == 200 and len(itemsP) > 0, (s, r))
# POST резолвить по tmdb → сід = B (драма), тому серед результатів має бути Drama Quiet D (id=5),
# а сід-жанр A (action/sci-fi) не тягне сюди B.
idsP = [i.get("id") for i in itemsP]
check("MD-03 POST tmdbIds=2 seeds B (drama) → Drama Quiet D present", 5 in idsP, idsP)
s, r = call(anon, "GET", "/api/similar/by-seeds?ids=2,3&limit=10")
check("MD-03 GET mixed internal seeds 2,3 → 200", s == 200, (s, r))

# --- MD-04 (backend частина): міграція гостьових items, ідемпотентність ---
mig = [{"kind": "watchlist", "titleId": 6, "rating": None}, {"kind": "watched", "titleId": 5, "rating": 8}]
s, r = callA("POST", "/api/lists/migrate-guest", {"items": mig})
check("MD-04 migrate-guest moved+skipped=2", s == 200 and (r.get("moved", 0) + r.get("skipped", 0)) == 2, (s, r))
s, r = callA("POST", "/api/lists/migrate-guest", {"items": mig})
check("MD-04 migrate-guest idempotent (moved=0, skipped=2)", s == 200 and r.get("moved") == 0 and r.get("skipped") == 2, (s, r))
s, r = callB("POST", "/api/lists/migrate-guest", {"items": mig})
check("MD-04 B migrates same snapshot into B (isolated)", s == 200 and (r.get("moved", 0) + r.get("skipped", 0)) == 2, (s, r))

# --- MD-05 (backend): bot identity ---
BH = {"X-Bot-Key": "test-bot-key", "X-Telegram-User-Id": "777001"}
s, r = call(anon, "POST", "/api/lists/watched", {"titleId": 6}, headers=BH)
check("MD-05 bot add → 201/409", s in (200, 201, 409), (s, r))
s, r = call(anon, "GET", "/api/lists?kind=watched", headers=BH)
check("MD-05 bot GET sees own entry", s == 200 and any(i.get("id") == 6 for i in r.get("items", [])), r)
s, r = call(anon, "GET", "/api/lists?kind=watched", headers={"X-Bot-Key": "test-bot-key", "X-Telegram-User-Id": "777002"})
check("MD-05 другой TG user does not see 777001 entries", s == 200 and not any(i.get("id") == 6 for i in r.get("items", [])), r)
s, r = call(anon, "GET", "/api/lists?kind=watched", headers={"X-Bot-Key": "wrong-key", "X-Telegram-User-Id": "777001"})
check("MD-05 wrong bot key → 403", s == 403, (s, r))
s, r = call(anon, "GET", "/api/lists?kind=watched", headers={"X-Bot-Key": "test-bot-key"})
check("MD-05 bot key without TG id → 400", s == 400, (s, r))

print(f"\n== {len(ok)} passed, {len(fail)} failed ==")
if fail:
    print("FAILED:", ", ".join(fail))
sys.exit(1 if fail else 0)
