"use client";

import { useEffect } from "react";

// Реєстрація service worker'а (PWA). Тільки у браузері, тільки production-актив.
export default function ServiceWorkerRegister() {
  useEffect(() => {
    if (typeof window === "undefined") return;
    if (!("serviceWorker" in navigator)) return;
    const on = () => {
      navigator.serviceWorker.register("/sw.js").catch(() => {
        /* без SW сайт працює звичайно */
      });
    };
    if (document.readyState === "complete") on();
    else window.addEventListener("load", on, { once: true });
    return () => window.removeEventListener("load", on);
  }, []);
  return null;
}
