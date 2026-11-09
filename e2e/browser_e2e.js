#!/usr/bin/env node
/**
 * Browser E2E (MD-10 + CR-02/03/04/06, FIX_SPEC 2026-11-09) — ізольований стек http://localhost:3201.
 * Запуск: node browser_e2e.js  (потрібен `npm i playwright` у каталозі скрипта + chromium)
 * Exit 0 = PASS; 1 = FAIL. Скріншоти → ./shots/.
 */
const { chromium } = require("playwright");
const fs = require("fs");
const path = require("path");

const BASE = process.env.E2E_BASE || "http://localhost:3201";
const SHOTS = path.join(__dirname, "shots");
fs.mkdirSync(SHOTS, { recursive: true });

let pass = 0;
const fails = [];
function check(name, cond, detail = "") {
  if (cond) { pass++; console.log(`PASS  ${name}`); }
  else { fails.push(name); console.log(`FAIL  ${name}  ${detail}`); }
}

// Резолв внутрішнього id фіксстури за назвою через живий API (CR-05: не хардкод).
async function resolveId(page, name) {
  return page.evaluate(async (q) => {
    const r = await fetch(`/api/search?q=${encodeURIComponent(q)}&pageSize=10&lang=en`);
    if (!r.ok) return null;
    const j = await r.json();
    const hit = (j.items || []).find((i) => i.title === q || i.originalTitle === q);
    return hit ? hit.id : null;
  }, name);
}

(async () => {
  const browser = await chromium.launch();

  // ═══ Блок 1: базові MD-03 сценарії ═══
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  const page = await ctx.newPage();
  const errors = [];
  page.on("pageerror", (e) => errors.push(String(e)));

  await page.goto(BASE + "/", { waitUntil: "domcontentloaded", timeout: 30000 });
  check("home loads", (await page.title()).length > 0, await page.title());

  const cId = await resolveId(page, "Space Heist C");
  check("CR-05 fixture id resolved by name (not hardcoded)", cId !== null, String(cId));

  // MD-03: /similar?seed=<C_ID> (внутрішній id) → результати рендеряться
  await page.goto(BASE + `/similar?seed=${cId}`, { waitUntil: "domcontentloaded" });
  await page.waitForTimeout(4000);
  const similarCards = await page.locator("a[href*='/title/']").count();
  check("MD-03 similar renders results for internal id", similarCards > 0, `cards=${similarCards}`);
  await page.screenshot({ path: path.join(SHOTS, "similar-internal-id.png") });

  // MD-03: головна → вкладка «Схоже на…» → швидкий режим → результати
  await page.goto(BASE + "/", { waitUntil: "domcontentloaded" });
  const similarTab = page.locator("button", { hasText: /Схоже на|Similar/i }).last();
  if (await similarTab.count()) {
    await similarTab.click();
    await page.waitForTimeout(500);
    const input = page.locator("input").first();
    await input.fill("Space");
    await page.waitForTimeout(1500);
    const opt = page.locator("text=Space Heist C").first();
    if (await opt.count()) {
      await opt.click();
      await page.waitForTimeout(800);
      const go = page.locator("button", { hasText: /Знайти схоже|Find similar/i }).first();
      if (await go.count()) await go.click();
      await page.waitForTimeout(3500);
      const results = await page.locator("a[href*='/title/']").count();
      check("MD-03 home tab quick similar works (no error banner)", results > 0, `results=${results}`);
      await page.screenshot({ path: path.join(SHOTS, "home-similar-fixed.png") });
    } else {
      check("MD-03 home tab quick similar works (no error banner)", false, "suggestion not found");
    }
  } else {
    check("MD-03 home tab quick similar works (no error banner)", false, "tab not found");
  }

  // ═══ CR-02: fallback regression — GET 405 → ЖОДНОГО POST, чесна помилка ═══
  const cr02 = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  const p2 = await cr02.newPage();
  const posts = [];
  p2.on("request", (r) => {
    if (r.method() === "POST" && r.url().includes("/api/similar/by-seeds")) posts.push(r.url());
  });
  await p2.route("**/api/similar/by-seeds*", (route) => {
    if (route.request().method() === "GET") {
      route.fulfill({ status: 405, contentType: "application/json", body: JSON.stringify({}) });
    } else {
      route.continue(); // POST має НІКОЛИ не статись — фіксуємо факт
    }
  });
  await p2.goto(BASE + `/similar?seed=${cId}`, { waitUntil: "domcontentloaded" });
  await p2.waitForTimeout(4000);
  check("CR-02 no silent fallback POST after GET 405", posts.length === 0, `posts=${posts.length}`);
  const errShown = (await p2.locator("text=Не вдалося знайти схоже").count())
    + (await p2.locator("text=Помилка").count());
  check("CR-02 honest error shown (no fake results)", errShown > 0, "no error banner");
  await p2.screenshot({ path: path.join(SHOTS, "cr02-no-fallback.png") });

  // ═══ CR-04: відмова localStorage → чесна помилка, НЕ «додано» ═══
  const cr04 = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  await cr04.addInitScript(() => {
    const orig = window.localStorage.setItem.bind(window.localStorage);
    Object.defineProperty(window.localStorage, "setItem", {
      value: (k, v) => {
        if (k === "md_guest_lists_v1") throw new DOMException("quota", "QuotaExceededError");
        return orig(k, v);
      },
    });
  });
  const p4 = await cr04.newPage();
  await p4.goto(BASE + `/title/${cId}`, { waitUntil: "domcontentloaded" });
  await p4.waitForTimeout(2500);
  const addBtn = p4.locator("button", { hasText: /Хочу подивитись|Watchlist/i }).first();
  if (await addBtn.count()) {
    await addBtn.click();
    await p4.waitForTimeout(1500);
    const stored = await p4.evaluate(() => window.localStorage.getItem("md_guest_lists_v1"));
    check("CR-04 guest quota failure -> nothing stored", stored === null, String(stored).slice(0, 80));
    const errVisible = (await p4.locator("text=Помилка").count()) > 0;
    // «успіх» = кнопка switchнулась на «✓ Додано»; перевіряємо саме її текст
    const btnLabel = (await addBtn.innerText()).trim();
    const falseSuccess = btnLabel.includes("✓");
    check("CR-04 error banner shown, no false success", errVisible && !falseSuccess, `err=${errVisible} btn="${btnLabel}"`);
    await p4.screenshot({ path: path.join(SHOTS, "cr04-storage-error.png") });
  } else {
    check("CR-04 guest quota failure -> nothing stored", false, "add button not found");
  }

  // ═══ CR-03: міграція з missing title → rejected лишається у знімку ═══
  const cr03 = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  // Гостьовий знімок ДО логіну: 2 валідні + 1 неіснуючий тайтл.
  await cr03.addInitScript((cid) => {
    const snap = {
      version: 1,
      items: [
        { kind: "watchlist", titleId: cid, tmdbId: null, rating: null, addedAt: new Date().toISOString(), meta: null },
        { kind: "watched", titleId: 5, tmdbId: null, rating: 8, addedAt: new Date().toISOString(), meta: null },
        { kind: "watchlist", titleId: 999999999, tmdbId: null, rating: null, addedAt: new Date().toISOString(), meta: null },
      ],
    };
    window.localStorage.setItem("md_guest_lists_v1", JSON.stringify(snap));
  }, cId);
  const p3 = await cr03.newPage();
  // Логін через same-origin API (cookie в контексті браузера).
  const loginEmail = process.env.MD_E2E_LOGIN || "e2e-debug@t.ua";
  const loginPass = process.env.MD_E2E_PASS || "Passw0rdE2E";
  await p3.goto(BASE + "/login", { waitUntil: "domcontentloaded" });
  const loginStatus = await p3.evaluate(async ({ email, password }) => {
    const r = await fetch("/api/auth/login", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      credentials: "include",
      body: JSON.stringify({ email, password }),
    });
    return r.status;
  }, { email: loginEmail, password: loginPass });
  console.log(`NOTE  CR-03 login ${loginEmail} -> ${loginStatus}`);
  await p3.goto(BASE + "/lists", { waitUntil: "domcontentloaded" });
  // чекаємо на банер міграції до 15с (auth/me + guestSnapshot)
  let migBtn = null;
  for (let i = 0; i < 15; i++) {
    await p3.waitForTimeout(1000);
    migBtn = p3.locator("button", { hasText: /Перенести|Migrate/i }).first();
    if (await migBtn.count()) break;
  }
  if (migBtn && (await migBtn.count())) {
    await migBtn.click();
    await p3.waitForTimeout(4000);
    const snap = await p3.evaluate(() => {
      try { return JSON.parse(window.localStorage.getItem("md_guest_lists_v1") || "null"); } catch { return null; }
    });
    const keptRejected = (snap?.items || []).some((i) => i.titleId === 999999999);
    const confirmedGone = !(snap?.items || []).some((i) => i.titleId === cId);
    check("CR-03 rejected entry PRESERVED in snapshot", keptRejected, JSON.stringify(snap).slice(0, 150));
    check("CR-03 confirmed entries removed from snapshot", confirmedGone, JSON.stringify(snap).slice(0, 150));
    const rejectedShown = (await p3.locator("text=Не вдалося перенести").count()) > 0;
    check("CR-03 rejected reason shown in UI", rejectedShown, "banner missing");
    await p3.screenshot({ path: path.join(SHOTS, "cr03-migration-rejected.png") });
  } else {
    check("CR-03 rejected entry PRESERVED in snapshot", false, "migrate button not found (login/rate-limit?)");
    check("CR-03 confirmed entries removed from snapshot", false, "migrate button not found");
  }

  // ═══ CR-06: SW activate чистить лише свій префікс mvdc-* ═══
  const cr06 = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  const p6 = await cr06.newPage();
  await p6.goto(BASE + "/", { waitUntil: "domcontentloaded" });
  await p6.waitForTimeout(2500);
  const swResult = await p6.evaluate(async () => {
    try {
      // Чужий кеш origin + застарілий кеш свого префікса.
      const foreign = await caches.open("other-app-v1");
      await foreign.put("/probe", new Response("x"));
      const stale = await caches.open("mvdc-v0");
      await stale.put("/probe", new Response("x"));
      const reg = await navigator.serviceWorker.getRegistration();
      if (!reg) return { error: "no registration" };
      // Симуляція РЕАЛЬНОГО оновлення: той самий scope, новий scriptURL (query) —
      // інакше Chromium перевикористовує активного воркера і install/activate не йдуть.
      await reg.unregister();
      const reg2 = await navigator.serviceWorker.register(`/sw.js?update=${Date.now()}`);
      const worker = reg2.waiting || reg2.installing || reg2.active;
      if (!worker) return { error: "no worker" };
      await new Promise((resolve) => {
        if (worker.state === "activated") return resolve();
        worker.addEventListener("statechange", () => {
          if (worker.state === "activated") resolve();
        });
        setTimeout(resolve, 15000);
      });
      const keys = await caches.keys();
      return { state: worker.state, keys, foreignAlive: keys.includes("other-app-v1"), staleGone: !keys.includes("mvdc-v0") };
    } catch (e) {
      return { error: String(e) };
    }
  });
  if (swResult.error) {
    check("CR-06 SW activate cleans only mvdc-* prefix", false, swResult.error);
  } else {
    check("CR-06 SW activate cleans only mvdc-* prefix", swResult.foreignAlive && swResult.staleGone, JSON.stringify(swResult.keys));
  }

  // ═══ MD-08 mobile + page errors ═══
  const mob = await browser.newContext({ viewport: { width: 390, height: 844 } });
  const mp = await mob.newPage();
  await mp.goto(BASE + "/", { waitUntil: "domcontentloaded" });
  await mp.waitForTimeout(1500);
  const overflow = await mp.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth
  );
  check("MD-08 mobile 390px no horizontal overflow", overflow <= 2, `overflowPx=${overflow}`);
  await mp.screenshot({ path: path.join(SHOTS, "mobile-390.png") });

  check("no uncaught page errors", errors.length === 0, errors.slice(0, 3).join(" | "));

  await browser.close();
  console.log(`\n===== BROWSER E2E: PASS=${pass} FAIL=${fails.length} =====`);
  if (fails.length) { fails.forEach((f) => console.log("  - " + f)); process.exit(1); }
})().catch((e) => { console.error("RUNNER ERROR", e); process.exit(1); });
