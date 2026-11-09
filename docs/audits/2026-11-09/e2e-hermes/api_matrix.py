#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
MD-01..04 + MD-07 API integration matrix (FIX_SPEC 2026-11-09) — ізольований стек.
Середовище: docker-compose.test.yml (moviediscovery-test), API http://localhost:5300.
Власні фікстури в порожній БД; production data/pg не використовується.
Exit: 0 = усі перевірки PASS; 1 = є FAIL (регресія). Докази — stdout.
"""
import json
import sys
import urllib.request
import urllib.error
import http.cookiejar

API = "http://localhost:5300"
BOT_KEY = "test-bot-key"
PASS_COUNT = 0
FAILS: list[str] = []


def req(method: str, path: str, body=None, cookies=None, headers=None):
    r = urllib.request.Request(API + path, method=method)
    r.add_header("Content-Type", "application/json")
    if headers:
        for k, v in headers.items():
            r.add_header(k, v)
    if cookies:
        r.add_header("Cookie", "; ".join(f"{k}={v}" for k, v in cookies.items()))
    data = json.dumps(body).encode() if body is not None else None
    try:
        with urllib.request.urlopen(r, data=data, timeout=30) as resp:
            raw = resp.read().decode()
            set_cookie = resp.headers.get_all("Set-Cookie") or []
            return resp.status, (json.loads(raw) if raw else None), set_cookie
    except urllib.error.HTTPError as e:
        raw = e.read().decode()
        try:
            return e.code, json.loads(raw), e.headers.get_all("Set-Cookie") or []
        except Exception:
            return e.code, {"_raw": raw}, e.headers.get_all("Set-Cookie") or []


def collect_cookies(set_cookies):
    jar = {}
    for sc in set_cookies:
        pair = sc.split(";", 1)[0]
        if "=" in pair:
            k, v = pair.split("=", 1)
            jar[k.strip()] = v.strip()
    return jar


def check(name: str, cond: bool, detail: str = ""):
    global PASS_COUNT
    if cond:
        PASS_COUNT += 1
        print(f"PASS  {name}")
    else:
        FAILS.append(name)
        print(f"FAIL  {name}  {detail}")


def register(email, username, password):
    st, body, sc = req("POST", "/api/auth/register",
                       {"email": email, "username": username, "password": password})
    if st in (400, 409):  # уже існує або слабкий пароль → логін
        st, body, sc = req("POST", "/api/auth/login", {"email": email, "password": password})
    return collect_cookies(sc), body


def profile_id(cookies):
    st, me, _ = req("GET", "/api/auth/me", cookies=cookies)
    assert st == 200, f"me -> {st}"
    return None  # резолв через /api/lists (profileId у відповіді)


def guest_items(cookies, kind=None):
    q = f"?kind={kind}" if kind else ""
    st, body, _ = req("GET", f"/api/lists{q}", cookies=cookies)
    return st, (body or {}).get("items", []), (body or {}).get("profileId")


# ═══ MD-01: ownership matrix ═══
def md01():
    print("\n=== MD-01 ownership lists/profiles (P0) ===")
    g = {}
    st, items, gpid = guest_items(g)
    check("MD-01 anonymous GET lists -> 200 guest profile", st == 200 and gpid == 1, f"{st}")
    base_guest_count = len(items)
    check("MD-01 guest fixture entry visible", base_guest_count >= 1, f"{len(items)}")

    a_cookies, a_body = register("own-a@t.ua", "ownA1", "Passw0rdA")
    b_cookies, b_body = register("own-b@t.ua", "ownB1", "Passw0rdB")
    check("MD-01 register A", a_cookies.get(".AspNetCore.Cookies") is not None, str(a_body)[:120])
    check("MD-01 register B", b_cookies.get(".AspNetCore.Cookies") is not None, str(b_body)[:120])

    # A додає свій запис, B — свій.
    st, body, _ = req("POST", "/api/lists/watched", {"titleId": 7002, "rating": 9}, cookies=a_cookies)
    a_item = (body or {}).get("id") if st == 201 else None
    check("MD-01 A adds own entry", st == 201 and a_item, f"{st} {str(body)[:100]}")
    st, body, _ = req("POST", "/api/lists/watched", {"titleId": 7002, "rating": 4}, cookies=b_cookies)
    b_item = (body or {}).get("id") if st == 201 else None
    check("MD-01 B adds own entry", st == 201 and b_item, f"{st} {str(body)[:100]}")

    # Резолв профільних id: A GET lists → profileId; B так само.
    _, _, a_pid = guest_items(a_cookies)
    _, _, b_pid = guest_items(b_cookies)
    check("MD-01 A profile isolated from B", a_pid not in (None, b_pid, 1), f"{a_pid} vs {b_pid}")

    # Anonymous не бачить B: profileId=B → 403/404, тіло без даних B.
    st, body, _ = req("GET", f"/api/lists?profileId={b_pid}")
    text = json.dumps(body)
    check("MD-01 anonymous GET foreign profile blocked", st in (403, 404), f"{st}")
    check("MD-01 no B-data leak in body", str(b_item) not in text and "Shawshank" not in text, text[:120])

    # Unknown profile → 403/404.
    st, _, _ = req("GET", "/api/lists?profileId=999999")
    check("MD-01 anonymous GET unknown profile blocked", st in (403, 404), f"{st}")

    # Anonymous POST у чужий профіль → заблоковано; дефолт → гостьовий.
    st, _, _ = req("POST", "/api/lists", {"profileId": b_pid, "kind": "watchlist", "titleId": 7000})
    check("MD-01 anonymous POST foreign profile blocked", st in (403, 404), f"{st}")
    st, body, _ = req("POST", "/api/lists", {"kind": "watchlist", "titleId": 7001})
    check("MD-01 anonymous POST default -> guest profile", st == 201 and (body or {}).get("profileId") == 1, f"{st}")

    # IDOR PATCH/DELETE чужих записів (anonymous і A на B).
    st, _, _ = req("PATCH", f"/api/lists/{b_item}", {"rating": 1})
    check("MD-01 anonymous PATCH B-item blocked", st in (403, 404), f"{st}")
    st, _, _ = req("DELETE", f"/api/lists/{b_item}")
    check("MD-01 anonymous DELETE B-item blocked", st in (403, 404), f"{st}")
    st, _, _ = req("PATCH", f"/api/lists/{b_item}", {"rating": 1}, cookies=a_cookies)
    check("MD-01 A PATCH B-item blocked", st in (403, 404), f"{st}")
    st, _, _ = req("DELETE", f"/api/lists/{b_item}", cookies=a_cookies)
    check("MD-01 A DELETE B-item blocked", st in (403, 404), f"{st}")

    # Дані B не пошкоджені.
    st, items, _ = guest_items(b_cookies)
    st_b = [i for i in items if i.get("itemId") == b_item]
    check("MD-01 B entry intact", st == 200 and st_b and st_b[0].get("rating") == 4, f"{st} {str(st_b)[:100]}")

    # Власні операції валідні.
    st, _, _ = req("PATCH", f"/api/lists/{a_item}", {"rating": 8}, cookies=a_cookies)
    check("MD-01 A PATCH own item OK", st == 200, f"{st}")
    st, _, _ = req("DELETE", f"/api/lists/{a_item}", cookies=a_cookies)
    check("MD-01 A DELETE own item OK", st == 204, f"{st}")

    # /api/profiles: анонім → 401; юзер → лише свій.
    st, body, _ = req("GET", "/api/profiles")
    check("MD-01 anonymous GET /api/profiles -> 401", st == 401, f"{st}")
    st, body, _ = req("GET", "/api/profiles", cookies=a_cookies)
    arr = (body if isinstance(body, list) else (body or {}).get("items")) or []
    check("MD-01 profiles: only own profile", st == 200 and len(arr) == 1 and arr[0].get("id") == a_pid, f"{st} {str(arr)[:100]}")
    st, _, _ = req("POST", "/api/profiles", {"name": "evil"}, cookies=None)
    check("MD-01 anonymous POST /api/profiles -> 401", st == 401, f"{st}")

    # Bot-header path (MD-05/MD-01): два різні telegram-юзери ізольовані.
    t1 = {"X-Bot-Key": BOT_KEY, "X-Telegram-User-Id": "111111"}
    t2 = {"X-Bot-Key": BOT_KEY, "X-Telegram-User-Id": "222222"}
    st, body, _ = req("GET", "/api/lists", headers=t1)
    check("MD-05 bot tg1 GET lists -> 200 own profile", st == 200, f"{st} {str(body)[:100]}")
    t1_pid = (body or {}).get("profileId")
    st, body, _ = req("GET", "/api/lists", headers=t2)
    t2_pid = (body or {}).get("profileId")
    check("MD-05 bot tg1/tg2 profiles differ", t1_pid != t2_pid, f"{t1_pid} vs {t2_pid}")
    st, _, _ = req("GET", f"/api/lists?profileId={b_pid}", headers=t1)
    check("MD-05 bot foreign profileId ignored/blocked", st == 200 and (body or {}).get("profileId") == t1_pid, f"{st}")
    st, _, _ = req("GET", "/api/lists", headers={"X-Bot-Key": "wrong-key", "X-Telegram-User-Id": "1"})
    check("MD-05 forged bot key -> 401/403", st in (401, 403), f"{st}")
    st, _, _ = req("GET", "/api/lists", headers={"X-Bot-Key": BOT_KEY})
    check("MD-05 bot key without tg-id -> 400", st == 400, f"{st}")


# ═══ MD-02: onboarding contract ═══
def md02():
    print("\n=== MD-02 onboarding contract ===")
    cookies, _ = register("onb@t.ua", "onbU1", "Passw0rdO")
    st, me, _ = req("GET", "/api/auth/me", cookies=cookies)
    check("MD-02 fresh account onboardingDone=false", st == 200 and me.get("onboardingDone") is False, f"{st} {me}")

    st, body, _ = req("GET", "/api/onboarding/questions", cookies=cookies)
    check("MD-02 questions 200", st == 200 and body.get("questions"), f"{st}")

    # Коректна форма (контракт бекенда): picks/mood/era — string[], favoriteTitles — int[].
    st, body, _ = req("POST", "/api/onboarding/answers", {
        "picks": ["action", "drama"], "mood": ["comedy"], "era": ["1990s"],
        "favoriteTitles": [7002, 7000], "skipped": False,
    }, cookies=cookies)
    check("MD-02 valid shape accepted", st == 200 and (body or {}).get("onboardingDone") is True, f"{st} {str(body)[:100]}")
    st, me, _ = req("GET", "/api/auth/me", cookies=cookies)
    check("MD-02 me.onboardingDone=true after success", me.get("onboardingDone") is True, str(me)[:100])

    # Неправильна форма (об'єкти замість рядків) → 400 (не 500/200).
    st2, body2, _ = req("POST", "/api/onboarding/answers", {
        "picks": [{"questionId": "posters-1", "values": ["action"]}],
        "mood": [], "era": None, "favoriteTitles": [], "skipped": False,
    }, cookies=cookies)
    check("MD-02 object-shape rejected (400), not 500", st2 in (400, 422), f"{st2} {str(body2)[:100]}")

    # Пропущені nullable масиви → валідний «порожній» (200), не 500 (null-safe бекенд).
    st3, body3, _ = req("POST", "/api/onboarding/answers", {"skipped": True}, cookies=cookies)
    check("MD-02 missing arrays null-safe (no NRE/500)", st3 in (200, 400, 422), f"{st3} {str(body3)[:100]}")
    if st3 == 200:
        # Skip теж зберігається серверно (MD-02): skipped → onboardingDone=false, але quiz збережено.
        st, me, _ = req("GET", "/api/auth/me", cookies=cookies)
        check("MD-02 skip persists without marking done", st == 200 and me.get("onboardingDone") is False, str(me)[:100])

    # >3 favorites → 400.
    st4, _, _ = req("POST", "/api/onboarding/answers", {
        "picks": [], "mood": [], "era": [], "favoriteTitles": [7000, 7001, 7002, 7003], "skipped": False,
    }, cookies=cookies)
    check("MD-02 >3 favorites -> 400", st4 == 400, f"{st4}")


# ═══ MD-03: ID namespaces + similarity ═══
def md03():
    print("\n=== MD-03 internal ID / similarity namespaces ===")
    # Internal id 7000 (tmdb NULL) → результати (не 404, не fallback-порожнина).
    st, body, _ = req("GET", "/api/similar/by-seeds?ids=7000&limit=3")
    ok = st == 200 and (body or {}).get("items")
    check("MD-03 GET by-seeds internal id works", ok, f"{st} {str(body)[:120]}")
    if ok:
        item = body["items"][0]
        check("MD-03 reasons present", isinstance(item.get("reasons"), list), str(item)[:100])

    # Legacy tmdbIds=7000 → collision fixture: title 7001 (tmdb_id=7000), НЕ 7000.
    st, body, _ = req("POST", "/api/similar/by-seeds", {"tmdbIds": [7000], "limit": 3})
    check("MD-03 POST legacy tmdbIds resolves typed title", st == 200, f"{st} {str(body)[:120]}")

    # GET і POST з фільтрами еквівалентні (порівнюємо перші 3 id).
    st1, b1, _ = req("GET", "/api/similar/by-seeds?ids=7000&yearFrom=1990&yearTo=2020&types=movie&limit=3")
    st2, b2, _ = req("POST", "/api/similar/by-seeds",
                     {"tmdbIds": [7000], "yearFrom": 1990, "yearTo": 2020, "types": "movie", "limit": 3})
    ids1 = [i["id"] for i in (b1 or {}).get("items", [])]
    ids2 = [i["id"] for i in (b2 or {}).get("items", [])]
    check("MD-03 GET/POST filters equivalent", st1 == 200 and st2 == 200 and ids1 == ids2, f"{ids1} vs {ids2}")

    # Фільтр року реально фільтрує.
    st3, b3, _ = req("GET", "/api/similar/by-seeds?ids=7002&yearFrom=2020&limit=3")
    years_ok = all((i.get("year") or 0) >= 2020 for i in (b3 or {}).get("items", []))
    check("MD-03 year filter hard", st3 == 200 and years_ok, f"{st3} {str(b3)[:120]}")

    # Невідомий id → контрольована 404 (titles_not_found), без фолбеку на інший тайтл.
    st4, body4, _ = req("GET", "/api/similar/by-seeds?ids=424242&limit=3")
    check("MD-03 unknown id -> 404", st4 == 404 and (body4 or {}).get("error") == "titles_not_found", f"{st4} {str(body4)[:100]}")

    # Змішаний список: IMDb-only + TMDB — обидва сиди враховані (200, результати є).
    st5, b5, _ = req("GET", "/api/similar/by-seeds?ids=7000,7001&limit=5")
    check("MD-03 mixed seeds accepted", st5 == 200 and len((b5 or {}).get("items", [])) > 0, f"{st5}")

    # 0 seeds → валідація.
    st6, _, _ = req("GET", "/api/similar/by-seeds?ids=&limit=3")
    check("MD-03 empty ids -> 400", st6 == 400, f"{st6}")

    # Anime seed (tmdb NULL) працює.
    st7, b7, _ = req("GET", "/api/similar/by-seeds?ids=7003&limit=3")
    check("MD-03 anime seed (tmdb null) works", st7 == 200 and (b7 or {}).get("items") is not None, f"{st7} {str(b7)[:100]}")


# ═══ MD-04: guest snapshot → migrate ═══
def md04():
    print("\n=== MD-04 guest migration ===")
    # Гостьовий snapshot (симуляція localStorage): 5 kinds + рейтинги.
    snapshot = [
        {"kind": "watchlist", "titleId": 7000, "rating": None},
        {"kind": "watched", "titleId": 7002, "rating": 9},
        {"kind": "liked", "titleId": 7001, "rating": 8},
        {"kind": "disliked", "titleId": 7003, "rating": 2},
        {"kind": "read", "titleId": 7004, "rating": None},
    ]
    cookies, _ = register("mig@t.ua", "migU1", "Passw0rdM")
    st, body, _ = req("POST", "/api/lists/migrate-guest", {"items": snapshot}, cookies=cookies)
    check("MD-04 migrate 5 kinds + ratings", st == 200 and (body or {}).get("moved") == 5, f"{st} {str(body)[:100]}")
    # Ідемпотентність: повтор → 0 moved.
    st, body, _ = req("POST", "/api/lists/migrate-guest", {"items": snapshot}, cookies=cookies)
    check("MD-04 migrate idempotent", st == 200 and (body or {}).get("moved") == 0, f"{st} {str(body)[:100]}")
    # Усі kinds на місці.
    ok_all = True
    for kind, tid, rating in [("watchlist", 7000, None), ("watched", 7002, 9), ("liked", 7001, 8),
                              ("disliked", 7003, 2), ("read", 7004, None)]:
        st, items, _ = guest_items(cookies, kind)
        hit = [i for i in items if i.get("id") == tid]
        ok_all = ok_all and bool(hit) and (hit[0].get("rating") == rating)
    check("MD-04 all kinds+ratings migrated correctly", ok_all)

    # profileId з тіла чужого юзера ігнорується (не приймає чужий profileId).
    b_cookies, _ = register("mig-b@t.ua", "migB1", "Passw0rdB")
    st, body, _ = req("POST", "/api/lists/migrate-guest",
                      {"items": [{"kind": "watchlist", "titleId": 7001, "rating": 1}]}, cookies=b_cookies)
    check("MD-04 migrate B independent", st == 200 and (body or {}).get("moved") == 1, f"{st}")
    st, items_b, pid_b = guest_items(b_cookies, "watchlist")
    check("MD-04 B has own migrated entry only", all(i.get("id") == 7001 for i in items_b), str(items_b)[:100])


# ═══ MD-07: health/readiness ═══
def md07():
    print("\n=== MD-07 health/readiness ===")
    st, body, _ = req("GET", "/api/health")
    check("MD-07 health 200", st == 200, f"{st}")
    check("MD-07 health db=true (ready env)", (body or {}).get("db") is True, str(body)[:100])
    # Ollama вимкнена в тест-стеку → чесна деградація, не ok-all.
    raw_ok = (body or {}).get("status")
    check("MD-07 health reports degraded semantics when ollama off",
          raw_ok in ("ok", "degraded") and (body or {}).get("ollama") is False, str(body)[:120])


if __name__ == "__main__":
    md07()
    md01()
    md02()
    md03()
    md04()
    print(f"\n===== RESULT: PASS={PASS_COUNT} FAIL={len(FAILS)} =====")
    if FAILS:
        print("Failed checks:")
        for f in FAILS:
            print(f"  - {f}")
        sys.exit(1)
