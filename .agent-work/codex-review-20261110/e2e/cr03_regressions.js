// -*- coding: utf-8 -*-
// CR-03 фінал — браузерна регресія міграції гостьових списків по ФАКТИЧНОМУ UI
// (frontend/app/lists/page.tsx doMigrate + production guestLists через localStorage).
// E2E_BASE=http://localhost:3201 (тест-стек). 6 сценаріїв:
//  S1 legacy {moved,skipped} без entries: знімок НЕ стирається, маркер НЕ ставиться, помилка показана
//  S2 same-key rating edit під час in-flight: оновлений запис лишається
//  S3 remove+readd того самого ключа під час in-flight: новий запис лишається
//  S4 новий інший key під час in-flight: підтверджений видалено, новий лишився
//  S5 звичайні confirmed видаляються, rejected лишається, маркер ставиться
//  S6 retry після S5: idempotent, rejected не стирається
const { chromium } = require("playwright");
const fs = require("fs");
const path = require("path");

const BASE = process.env.E2E_BASE || "http://localhost:3201";
const SHOTS = process.env.CR03_SHOTS || path.join(__dirname, "shots");
fs.mkdirSync(SHOTS, { recursive: true });

let pass = 0, fail = 0;
function check(name, cond, detail = "") {
  if (cond) { pass++; console.log(`PASS  ${name}`); }
  else { fail++; console.log(`FAIL  ${name}  ${detail}`); }
}

const KEY = "md_guest_lists_v1";
const entry = (kind, titleId, rating, addedAt, tmdbId = null) => ({
  kind, titleId, tmdbId, rating, addedAt, meta: null,
});
function snap(items) {
  return JSON.stringify({ version: 1, items });
}

(async () => {
  const browser = await chromium.launch();
  const ctx = await browser.newContext({ baseURL: BASE, viewport: { width: 1280, height: 800 } });
  const page = await ctx.newPage();
  const errors = [];
  page.on("pageerror", (e) => errors.push(String(e)));

  // ── логін: ЛІМІТИРНИЙ порядок — fallback-логін ПЕРШИМ (1 спроба), register
  // лише якщо логін не вдався. Login 5/хв/IP рахує УСІ спроби — тому перед
  // спробою пауза ≥60с після будь-яких попередніх auth-запитів цієї машини. ──
  await page.goto("/register");
  const email = `cr03-${Date.now()}@t.ua`;
  const uname = `cr03U${Date.now() % 100000}`;
  const pwd = "Cr03Pass!1";
  const me = async () => page.evaluate(async () => {
    const r = await fetch("/api/auth/me", { credentials: "include" });
    if (r.status !== 200) return null;
    const j = await r.json();
    const u = j.user ?? j; // бекенд віддає і {user:{id}}, і плоский об'єкт
    return u?.id ?? null;
  });
  const tryLogin = async (em, pw) => page.evaluate(async ({ em, pw }) => {
    const r = await fetch("/api/auth/login", {
      method: "POST", headers: { "Content-Type": "application/json" }, credentials: "include",
      body: JSON.stringify({ email: em, password: pw }),
    });
    return r.status;
  }, { em, pw });
  let who = await me();
  if (!who && process.env.CR03_FALLBACK_EMAIL) {
    await page.waitForTimeout(65000);
    const s = await tryLogin(process.env.CR03_FALLBACK_EMAIL, "Strong1Pass"); // пароль з md_audit_fixes.py (репо)
    console.log(`NOTE  fallback login ${process.env.CR03_FALLBACK_EMAIL} -> ${s}`);
    who = await me();
  }
  if (!who) {
    const regStatus = await page.evaluate(async ({ email, pwd, uname }) => {
      const r = await fetch("/api/auth/register", {
        method: "POST", headers: { "Content-Type": "application/json" }, credentials: "include",
        body: JSON.stringify({ email, password: pwd, username: uname }),
      });
      return r.status;
    }, { email, pwd, uname });
    console.log(`NOTE  register ${email} -> ${regStatus}`);
    who = await me();
    if (!who) {
      // Пароль e2e-debug@t.ua з репо-файлу browser_e2e.js.
      await page.waitForTimeout(65000);
      const s = await tryLogin("e2e-debug@t.ua", "Passw0rdE2E");
      console.log(`NOTE  fallback login e2e-debug@t.ua -> ${s}`);
      who = await me();
    }
  }
  check("S0 logged in before migration scenarios", who !== null, `who=${who}`);

  let routeConfig = null; // { delayMs, body }
  await page.route("**/api/lists/migrate-guest", async (route) => {
    const cfg = routeConfig;
    if (!cfg) return route.fallback();
    if (cfg.delayMs) await new Promise((r) => setTimeout(r, cfg.delayMs));
    return route.fulfill({ status: cfg.status || 200, contentType: "application/json", body: JSON.stringify(cfg.body) });
  });

  const openLists = async (initSnapshot) => {
    // CR-03: маркер попереднього сценарію глушив би банер для ідентичного
    // знімка (S3 == S2 за значеннями) — чистимо md_guest_migrated:* перед кожним.
    await ctx.addInitScript(([k, v]) => {
      for (const key of Object.keys(localStorage)) {
        if (key.startsWith("md_guest_migrated")) localStorage.removeItem(key);
      }
      localStorage.setItem(k, v);
    }, [KEY, initSnapshot]);
    await page.goto("/lists");
    await page.waitForTimeout(400);
  };
  const storeNow = () => page.evaluate((k) => JSON.parse(localStorage.getItem(k) || "null"), KEY);
  const migratedKeys = () => page.evaluate(() => Object.keys(localStorage).filter((k) => k.startsWith("md_guest_migrated")));
  const clickMigrate = async () => {
    await page.locator("button", { hasText: "Перенести" }).first().click();
  };

  // ── S1: legacy-відповідь БЕЗ per-entry → знімок цілий, маркера немає, помилка ──
  const s1 = snap([entry("watchlist", 1, null, "T1"), entry("watchlist", 999999999, null, "T2")]);
  await openLists(s1);
  routeConfig = { delayMs: 0, body: { moved: 0, skipped: 2 } }; // legacy: rejected змішані у skipped
  await clickMigrate();
  await page.waitForTimeout(500);
  const s1store = await storeNow();
  const s1mk = await migratedKeys();
  check("S1 legacy response: snapshot NOT wiped (2 entries intact)",
    s1store && s1store.items.length === 2, JSON.stringify(s1store));
  check("S1 legacy response: NO completion marker", s1mk.length === 0, JSON.stringify(s1mk));
  check("S1 incompatible-API error shown",
    (await page.locator("p", { hasText: "не повертає детальний статус" }).count()) > 0);
  await page.screenshot({ path: path.join(SHOTS, "cr03_s1_legacy.png") });

  // ── S1b: БУКВАЛЬНИЙ незалежний repro рев'ювера (module-probes.json
  // legacyRejectedMigration): snapshot лише з неіснуючим тайтлом 999999999,
  // legacy-відповідь {moved:0,skipped:1} → ДО фіксу remaining=[], migrated=true.
  // Після фіксу: знімок недоторканий, migrated НЕ встановлено, маркера немає.
  const s1b = snap([entry("watchlist", 999999999, null, "T2")]);
  await openLists(s1b);
  routeConfig = { delayMs: 0, body: { moved: 0, skipped: 1 } };
  await clickMigrate();
  await page.waitForTimeout(500);
  const s1bStore = await storeNow();
  check("S1b reviewer repro {moved:0,skipped:1}: entry 999999999 RETAINED",
    s1bStore && s1bStore.items.length === 1 && s1bStore.items[0].titleId === 999999999,
    JSON.stringify(s1bStore));
  check("S1b reviewer repro: migrated NOT set", (await page.locator("p[role=status]").count()) === 0);
  check("S1b reviewer repro: NO completion marker", (await migratedKeys()).length === 0,
    JSON.stringify(await migratedKeys()));
  await page.screenshot({ path: path.join(SHOTS, "cr03_s1b_reviewer_repro.png") });

  // ── S2: same-key rating edit під час in-flight → оновлений лишається ──
  const s2 = snap([entry("watched", 1, 7, "T1")]);
  await openLists(s2);
  routeConfig = {
    delayMs: 2500,
    body: { moved: 1, skipped: 0, entries: [{ titleId: 1, kind: "watched", status: "moved" }] },
  };
  await clickMigrate();
  await page.waitForTimeout(600); // всередині in-flight
  await page.evaluate(([k]) => {
    const st = JSON.parse(localStorage.getItem(k));
    st.items[0].rating = 9; // «інша вкладка» міняє рейтинг, addedAt той самий
    localStorage.setItem(k, JSON.stringify(st));
  }, [KEY]);
  await page.waitForTimeout(3000); // дочекатись ack
  const s2store = await storeNow();
  check("S2 same-key rating edit during flight: rating 9 preserved",
    s2store.items.length === 1 && s2store.items[0].rating === 9, JSON.stringify(s2store));
  check("S2 submitted snapshot fully confirmed → marker set", (await migratedKeys()).length > 0);
  await page.screenshot({ path: path.join(SHOTS, "cr03_s2_edit.png") });

  // ── S3: remove+readd того самого ключа під час in-flight → новий лишається ──
  const s3 = snap([entry("watched", 1, 7, "T1")]);
  await openLists(s3);
  routeConfig = {
    delayMs: 2500,
    body: { moved: 1, skipped: 0, entries: [{ titleId: 1, kind: "watched", status: "moved" }] },
  };
  await clickMigrate();
  await page.waitForTimeout(600);
  await page.evaluate(([k]) => {
    const st = JSON.parse(localStorage.getItem(k));
    st.items = [{ kind: "watched", titleId: 1, tmdbId: null, rating: 7, addedAt: "T2-readded", meta: null }];
    localStorage.setItem(k, JSON.stringify(st)); // remove+readd: новий addedAt
  }, [KEY]);
  await page.waitForTimeout(3000);
  const s3store = await storeNow();
  check("S3 remove+readd same key: re-added entry preserved (addedAt T2-readded)",
    s3store.items.length === 1 && s3store.items[0].addedAt === "T2-readded", JSON.stringify(s3store));
  await page.screenshot({ path: path.join(SHOTS, "cr03_s3_readd.png") });

  // ── S4: новий інший key під час in-flight → confirmed видалено, новий лишився ──
  const s4 = snap([entry("watched", 1, 7, "T1")]);
  await openLists(s4);
  routeConfig = {
    delayMs: 2500,
    body: { moved: 1, skipped: 0, entries: [{ titleId: 1, kind: "watched", status: "moved" }] },
  };
  await clickMigrate();
  await page.waitForTimeout(600);
  await page.evaluate(([k]) => {
    const st = JSON.parse(localStorage.getItem(k));
    st.items = [{ kind: "watchlist", titleId: 5, tmdbId: null, rating: null, addedAt: "T5", meta: null }];
    localStorage.setItem(k, JSON.stringify(st)); // новий запис іншого ключа від «іншої вкладки»
  }, [KEY]);
  await page.waitForTimeout(3000);
  const s4store = await storeNow();
  check("S4 new other-key entry preserved, confirmed one removed",
    s4store.items.length === 1 && s4store.items[0].titleId === 5, JSON.stringify(s4store));
  await page.screenshot({ path: path.join(SHOTS, "cr03_s4_newkey.png") });

  // ── S5: частковий результат — confirmed видаляються, rejected лишається ──
  // Семантика продукту: rejected у знімку → НЕ повне підтвердження → маркер НЕ
  // ставиться (retry має лишитись доступним), повідомлення «успіху» НЕ показується.
  const s5 = snap([
    entry("watchlist", 1, null, "T1"),
    entry("watched", 5, 8, "T2"),
    entry("watchlist", 999999999, null, "T3"),
  ]);
  await openLists(s5);
  routeConfig = {
    delayMs: 0,
    body: {
      moved: 2, skipped: 0,
      entries: [
        { titleId: 1, kind: "watchlist", status: "moved" },
        { titleId: 5, kind: "watched", status: "moved" },
        { titleId: 999999999, kind: "watchlist", status: "unknown_title" },
      ],
    },
  };
  await clickMigrate();
  await page.waitForTimeout(800);
  const s5store = await storeNow();
  check("S5 confirmed entries removed, rejected 999999999 remains",
    s5store.items.length === 1 && s5store.items[0].titleId === 999999999, JSON.stringify(s5store));
  check("S5 partial: NO marker (retry stays available for rejected snapshot)",
    (await migratedKeys()).length === 0, JSON.stringify(await migratedKeys()));
  check("S5 partial: NO success message", (await page.locator("p[role=status]").count()) === 0);
  check("S5 partial: rejected count reported (1)",
    (await page.locator("p", { hasText: "1 запис" }).count()) > 0);
  await page.screenshot({ path: path.join(SHOTS, "cr03_s5_rejected.png") });

  // ── S6: retry після S5 — idempotent, rejected НЕ стирається ──
  // Знімок лишився [999999999] → банер знову активний (новий хеш ≠ маркер).
  routeConfig = {
    delayMs: 0,
    body: {
      moved: 0, skipped: 0,
      entries: [{ titleId: 999999999, kind: "watchlist", status: "unknown_title" }],
    },
  };
  await clickMigrate();
  await page.waitForTimeout(800);
  const s6store = await storeNow();
  check("S6 retry idempotent: rejected entry still present after second run",
    s6store.items.length === 1 && s6store.items[0].titleId === 999999999, JSON.stringify(s6store));
  check("S6 rejected count reported (1)",
    (await page.locator("p", { hasText: "1 запис" }).count()) > 0);
  await page.screenshot({ path: path.join(SHOTS, "cr03_s6_retry.png") });

  check("S7 no page errors during all scenarios", errors.length === 0, errors.join(" | "));

  await browser.close();
  console.log(`\n== CR-03 browser regression: PASS=${pass} FAIL=${fail} ==`);
  const OUT = process.env.CR03_OUT || path.join(__dirname, "..", "docs", "audits", "2026-11-09", "cr03-final");
  fs.mkdirSync(OUT, { recursive: true });
  fs.writeFileSync(
    path.join(OUT, "browser-regressions.json"),
    JSON.stringify({ scenarios: 7, pass, fail, base: BASE }, null, 2),
  );
  process.exit(fail ? 1 : 0);
})().catch((e) => { console.error("FATAL", e); process.exit(2); });
