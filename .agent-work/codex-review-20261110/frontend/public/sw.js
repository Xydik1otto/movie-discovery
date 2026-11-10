// Service Worker «ретранслятора»: мережа-перша з кеш-фолбеком.
// Кешуємо лише статику (_next/static, іконки) і постери TMDB + навігації для офлайн-фолбеку.
// /api/* НЕ кешуємо (живі дані). POST/інші методи не перехоплюємо.
const CACHE = "mvdc-v1";
// CR-06: очищаємо в activate ЛИШЕ кеші СВОГО префікса (mvdc-*) — чужі кеші
// origin (інші застосунки/PWA на тому ж домені) не чіпаємо.
const CACHE_PREFIX = "mvdc-";
const OFFLINE_URL = "/offline";

self.addEventListener("install", (event) => {
  event.waitUntil(
    (async () => {
      const cache = await caches.open(CACHE);
      try {
        await cache.add(OFFLINE_URL);
      } catch (_) {
        /* офлайн-сторінка з'явиться після першого візиту */
      }
      self.skipWaiting();
    })(),
  );
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    (async () => {
      const keys = await caches.keys();
      // CR-06: видаляємо лише застарілі кеші свого префікса mvdc-*;
      // кеші інших додатків на цьому origin залишаються недоторканими.
      await Promise.all(
        keys
          .filter((k) => k.startsWith(CACHE_PREFIX) && k !== CACHE)
          .map((k) => caches.delete(k)),
      );
      await self.clients.claim();
    })(),
  );
});

self.addEventListener("fetch", (event) => {
  const req = event.request;
  if (req.method !== "GET") return;
  const url = new URL(req.url);
  if (url.pathname.startsWith("/api/")) return; // живі дані — лише мережа

  const isNavigation =
    req.mode === "navigate" || (req.headers.get("accept") || "").includes("text/html");
  const isStatic =
    url.origin === self.location.origin &&
    (url.pathname.startsWith("/_next/static/") ||
      url.pathname.startsWith("/icons/") ||
      url.pathname === "/manifest.webmanifest" ||
      url.pathname === "/favicon.ico");
  const isTmdbImage = url.hostname === "image.tmdb.org";
  if (!isNavigation && !isStatic && !isTmdbImage) return;

  event.respondWith(
    (async () => {
      const cache = await caches.open(CACHE);
      try {
        const fresh = await fetch(req);
        if (fresh && fresh.status === 200) await cache.put(req, fresh.clone());
        return fresh;
      } catch (_) {
        const hit = await cache.match(req);
        if (hit) return hit;
        if (isNavigation) {
          const offline = await cache.match(OFFLINE_URL);
          if (offline) return offline;
        }
        return new Response("", { status: 503, statusText: "Offline" });
      }
    })(),
  );
});
