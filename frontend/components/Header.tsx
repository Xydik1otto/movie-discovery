"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useCallback, useEffect, useRef, useState } from "react";
import { useI18n } from "../i18n/I18nContext";
import { useAuth } from "../lib/auth";
import LangSwitcher from "./LangSwitcher";

type Dock = "top" | "left" | "right";

// Докінг верхньої панелі: top (дефолт) | left | right (вертикальний сайдбар).
// Вибір зберігається в localStorage 'md.header.dock'; мобайл <768px — завжди top.
const DOCK_KEY = "md.header.dock";
const DOCK_ORDER: Dock[] = ["top", "left", "right"];
const ZONE_PX = 160; // зона дропу: ≤160px від лівого/правого краю екрана
const GHOST_W = 240; // розмір напівпрозорого примірника під час драга
const GHOST_H = 48;
const DRAG_THRESHOLD = 4; // px — звичайний клік/подвійний клік не рахується драгом

const NAV = [
  { href: "/", key: "nav.home", icon: "🏠" },
  { href: "/discover", key: "nav.discover", icon: "✨" },
  { href: "/anime", key: "nav.anime", icon: "🎌" },
  { href: "/manga", key: "nav.manga", icon: "📚" },
  { href: "/similar", key: "nav.similar", icon: "🔮" },
  { href: "/lists", key: "nav.lists", icon: "📋" },
  { href: "/import", key: "nav.import", icon: "⬇️" },
  { href: "/settings", key: "nav.settings", icon: "⚙️" },
] as const;

export default function Header() {
  const { t } = useI18n();
  const { user, ready, logoutFailed, logout } = useAuth();
  const pathname = usePathname();

  const [saved, setSaved] = useState<Dock>("top");
  const [mobile, setMobile] = useState(false);
  const [dragging, setDragging] = useState(false);
  const [zone, setZone] = useState<"left" | "right" | null>(null);
  const [expanded, setExpanded] = useState<"lang" | "auth" | null>(null);

  // На мобайлі панель завжди зверху; збережений док лишається для десктопа.
  const dock: Dock = mobile ? "top" : saved;
  const vertical = dock !== "top";

  const rootRef = useRef<HTMLElement | null>(null);
  const ghostRef = useRef<HTMLDivElement | null>(null);
  const ghostPos = useRef<{ x: number; y: number } | null>(null);
  const dragRef = useRef<{ id: number; x0: number; y0: number; moved: boolean } | null>(null);

  // (c) відновлення доку на маунті (допустимий один flash-top, inline script не потрібен).
  useEffect(() => {
    try {
      const raw = window.localStorage.getItem(DOCK_KEY);
      if (raw === "top" || raw === "left" || raw === "right") setSaved(raw);
    } catch {
      // localStorage may be unavailable — лишається дефолт top
    }
  }, []);

  const applyDock = useCallback((next: Dock) => {
    setSaved(next);
    try {
      window.localStorage.setItem(DOCK_KEY, next);
    } catch {
      // ignore
    }
  }, []);

  // (d) мобайл <768px: драг вимкнено, панель завжди top.
  useEffect(() => {
    const mql = window.matchMedia("(max-width: 767px)");
    const apply = () => {
      setMobile(mql.matches);
      if (mql.matches) setExpanded(null);
    };
    apply();
    mql.addEventListener("change", apply);
    return () => mql.removeEventListener("change", apply);
  }, []);

  // Контент сторінки відсувається padding'ом, коли панель вертикальна
  // (data-атрибут на body → CSS у globals.css).
  useEffect(() => {
    document.body.dataset.headerDock = dock;
  }, [dock]);

  // Зміна маршруту закриває розгорнуті поверхні.
  useEffect(() => {
    setExpanded(null);
  }, [pathname]);

  // Клік поза панеллю або Escape закриває розгорнуту поверхню.
  useEffect(() => {
    if (!expanded) return;
    const onDoc = (ev: PointerEvent) => {
      if (rootRef.current && !rootRef.current.contains(ev.target as Node)) {
        setExpanded(null);
      }
    };
    const onKey = (ev: KeyboardEvent) => {
      if (ev.key === "Escape") setExpanded(null);
    };
    document.addEventListener("pointerdown", onDoc);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("pointerdown", onDoc);
      document.removeEventListener("keydown", onKey);
    };
  }, [expanded]);

  // Під час драга — заборонити виділення тексту й показувати grabbing-курсор.
  useEffect(() => {
    if (!dragging) return;
    const { userSelect, cursor } = document.body.style;
    document.body.style.userSelect = "none";
    document.body.style.cursor = "grabbing";
    return () => {
      document.body.style.userSelect = userSelect;
      document.body.style.cursor = cursor;
    };
  }, [dragging]);

  const zoneFor = useCallback((x: number): "left" | "right" | null => {
    const w = window.innerWidth;
    if (x <= ZONE_PX) return "left";
    if (x >= w - ZONE_PX) return "right";
    return null;
  }, []);

  const moveGhost = (x: number, y: number) => {
    ghostPos.current = { x, y };
    const g = ghostRef.current;
    if (g) g.style.transform = `translate(${x - GHOST_W / 2}px, ${y - GHOST_H / 2}px)`;
  };

  // Перший рендер примірника після початку драга: застосувати збережену позицію.
  useEffect(() => {
    if (dragging && ghostPos.current) {
      moveGhost(ghostPos.current.x, ghostPos.current.y);
    }
  }, [dragging]);

  // (a) драг грипа: pointerdown/move/up + setPointerCapture.
  const onGripPointerDown = (e: React.PointerEvent<HTMLDivElement>) => {
    if (mobile || e.button !== 0) return;
    try {
      e.currentTarget.setPointerCapture(e.pointerId);
    } catch {
      // ignore — драг просто не почнеться
    }
    dragRef.current = { id: e.pointerId, x0: e.clientX, y0: e.clientY, moved: false };
  };

  const onGripPointerMove = (e: React.PointerEvent<HTMLDivElement>) => {
    const ds = dragRef.current;
    if (!ds || ds.id !== e.pointerId) return;
    if (!ds.moved && Math.hypot(e.clientX - ds.x0, e.clientY - ds.y0) < DRAG_THRESHOLD) return;
    if (!ds.moved) {
      ds.moved = true;
      setDragging(true);
    }
    moveGhost(e.clientX, e.clientY);
    setZone(zoneFor(e.clientX));
  };

  const endDrag = (e: React.PointerEvent<HTMLDivElement>, commit: boolean) => {
    const ds = dragRef.current;
    if (!ds || ds.id !== e.pointerId) return;
    dragRef.current = null;
    try {
      if (e.currentTarget.hasPointerCapture(e.pointerId)) {
        e.currentTarget.releasePointerCapture(e.pointerId);
      }
    } catch {
      // ignore
    }
    if (commit && ds.moved) {
      // Відпуск у зоні → dock зліва/справа; зверху/за межами зон → dock зверху.
      applyDock(zoneFor(e.clientX) ?? "top");
      setExpanded(null);
    }
    setDragging(false);
    setZone(null);
  };

  // (e) клавіатурна альтернатива: подвійний клік/Enter циклічно top→left→right.
  const cycleDock = useCallback(() => {
    applyDock(DOCK_ORDER[(DOCK_ORDER.indexOf(saved) + 1) % DOCK_ORDER.length]);
  }, [applyDock, saved]);

  const grip = (
    <div
      role="button"
      tabIndex={0}
      aria-label={t("header.dockHint")}
      title={t("header.dockHint")}
      onPointerDown={onGripPointerDown}
      onPointerMove={onGripPointerMove}
      onPointerUp={(e) => endDrag(e, true)}
      onPointerCancel={(e) => endDrag(e, false)}
      onDoubleClick={cycleDock}
      onKeyDown={(e) => {
        if (e.key === "Enter" || e.key === " ") {
          e.preventDefault();
          cycleDock();
        }
      }}
      className={`hidden shrink-0 cursor-grab touch-none select-none items-center justify-center rounded-lg text-lg text-mute transition-colors hover:bg-panel-2 hover:text-ink active:cursor-grabbing md:flex ${
        dragging ? "text-accent-2" : ""
      }`}
    >
      <span aria-hidden>⠿</span>
    </div>
  );

  const brand = vertical ? (
    <Link
      href="/"
      aria-label={t("brand.name")}
      title={t("brand.name")}
      className="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl text-2xl transition-colors hover:bg-panel-2"
    >
      <span aria-hidden>🎬</span>
    </Link>
  ) : (
    <Link href="/" className="flex shrink-0 items-center gap-2">
      <span className="text-xl" aria-hidden>
        🎬
      </span>
      <span className="text-base font-bold tracking-tight text-ink sm:text-lg">
        {t("brand.name")}
      </span>
    </Link>
  );

  // Мобайл: nav ховаємо — на 390px flex-1 стискає його до ~8px і активний чіп
  // виглядає як обрізаний рожевий півмісяць між лого і «Увійти».
  const navTop = (
    <nav
      aria-label="main"
      className="no-scrollbar -mx-1 hidden flex-1 items-center gap-1 overflow-x-auto px-1 md:flex"
    >
      {NAV.map((item) => {
        const active =
          item.href === "/" ? pathname === "/" : pathname.startsWith(item.href);
        return (
          <Link
            key={item.href}
            href={item.href}
            className={`shrink-0 rounded-full px-3 py-1.5 text-sm transition-colors ${
              active
                ? "bg-accent/15 font-semibold text-accent-2"
                : "text-mute hover:bg-panel-2 hover:text-ink"
            }`}
          >
            {t(item.key)}
          </Link>
        );
      })}
    </nav>
  );

  const navSide = (
    <nav
      aria-label="main"
      className="no-scrollbar flex w-full flex-1 flex-col items-stretch gap-1 overflow-y-auto px-1"
    >
      {NAV.map((item) => {
        const active =
          item.href === "/" ? pathname === "/" : pathname.startsWith(item.href);
        return (
          <Link
            key={item.href}
            href={item.href}
            title={t(item.key)}
            className={`flex flex-col items-center gap-0.5 rounded-lg px-1 py-1.5 text-[10px] leading-tight transition-colors ${
              active
                ? "bg-accent/15 font-semibold text-accent-2"
                : "text-mute hover:bg-panel-2 hover:text-ink"
            }`}
          >
            <span className="text-lg" aria-hidden>
              {item.icon}
            </span>
            <span className="w-full truncate text-center">{t(item.key)}</span>
          </Link>
        );
      })}
    </nav>
  );

  // Вертикальний док: широкі елементи (мова, авторизація) згортаються в іконку,
  // що розгортає поверхню над контентом.
  const sideCluster = (
    <div className="flex shrink-0 flex-col items-center gap-1">
      {ready && (
        <button
          type="button"
          title={user ? user.username : t("auth.login")}
          aria-label={user ? user.username : t("auth.login")}
          aria-expanded={expanded === "auth"}
          onClick={() => setExpanded(expanded === "auth" ? null : "auth")}
          className={`flex h-9 w-9 items-center justify-center rounded-lg text-lg transition-colors hover:bg-panel-2 ${
            expanded === "auth" ? "bg-panel-2 text-ink" : "text-mute"
          }`}
        >
          <span aria-hidden>👤</span>
        </button>
      )}
      <button
        type="button"
        title={t("lang.switch")}
        aria-label={t("lang.switch")}
        aria-expanded={expanded === "lang"}
        onClick={() => setExpanded(expanded === "lang" ? null : "lang")}
        className={`flex h-9 w-9 items-center justify-center rounded-lg text-base transition-colors hover:bg-panel-2 ${
          expanded === "lang" ? "bg-panel-2 text-ink" : "text-mute"
        }`}
      >
        <span aria-hidden>🌐</span>
      </button>

      {expanded === "lang" && (
        <div
          className={`absolute top-24 z-50 rounded-xl border border-line bg-panel p-2 shadow-2xl ${
            dock === "left" ? "left-full ml-2" : "right-full mr-2"
          }`}
        >
          <LangSwitcher />
        </div>
      )}
      {expanded === "auth" && ready && (
        <div
          className={`absolute top-36 z-50 w-48 rounded-xl border border-line bg-panel p-3 shadow-2xl ${
            dock === "left" ? "left-full ml-2" : "right-full mr-2"
          }`}
        >
          {user ? (
            <div className="flex flex-col gap-2">
              <span
                className="truncate text-xs font-semibold text-ink"
                title={user.username}
              >
                {user.username}
              </span>
              {logoutFailed && (
                <span role="alert" className="text-[11px] leading-tight text-amber-300">
                  {t("auth.logoutUnavailable")}
                </span>
              )}
              {user.onboardingDone === false && (
                <Link
                  href="/onboarding"
                  className="rounded-full border border-accent/40 bg-accent/10 px-3 py-1.5 text-center text-xs font-semibold text-accent-2"
                >
                  {t("onboarding.title")}
                </Link>
              )}
              <button
                type="button"
                onClick={() => void logout()}
                className="rounded-full border border-line bg-panel-2 px-3 py-1.5 text-xs text-mute transition-colors hover:text-ink"
              >
                {t("auth.logout")}
              </button>
            </div>
          ) : (
            <Link
              href="/login"
              className="block rounded-full bg-accent px-3.5 py-1.5 text-center text-xs font-bold text-white transition-colors hover:bg-accent-2"
            >
              {t("auth.login")}
            </Link>
          )}
        </div>
      )}
    </div>
  );

  return (
    <>
      {vertical ? (
        <header
          ref={rootRef}
          className={`fixed top-0 z-40 flex h-full w-[68px] flex-col items-center gap-1 border-line bg-bg/90 py-3 backdrop-blur-md transition-[width,transform,opacity] duration-200 ease-[ease] ${
            dock === "left" ? "left-0 border-r" : "right-0 border-l"
          } ${dragging ? "opacity-50" : ""}`}
        >
          {grip}
          {brand}
          {navSide}
          {sideCluster}
        </header>
      ) : (
        <header
          ref={rootRef}
          className={`sticky top-0 z-40 border-b border-line bg-bg/85 backdrop-blur-md transition-[width,transform,opacity] duration-200 ease-[ease] ${
            dragging ? "opacity-50" : ""
          }`}
        >
          <div className="mx-auto flex max-w-7xl items-center gap-3 px-4 py-3">
            {grip}
            {brand}
            {navTop}
            {/* FR-16: стан користувача. Поки /me не відповідав — нічого (без блимання). */}
            {ready &&
              (user ? (
                <div className="flex shrink-0 items-center gap-1.5">
                  {/* MD-07: сервер не підтвердив logout — чесне повідомлення, користувач лишається залогіненим. */}
                  {logoutFailed && (
                    <span
                      role="alert"
                      className="hidden max-w-56 text-[11px] leading-tight text-amber-300 md:inline"
                    >
                      {t("auth.logoutUnavailable")}
                    </span>
                  )}
                  {user.onboardingDone === false && (
                    <Link
                      href="/onboarding"
                      className="hidden shrink-0 rounded-full border border-accent/40 bg-accent/10 px-3 py-1.5 text-xs font-semibold text-accent-2 md:inline-block"
                    >
                      {t("onboarding.title")}
                    </Link>
                  )}
                  <span
                    className="hidden max-w-32 truncate text-xs text-mute sm:inline"
                    title={user.username}
                  >
                    {user.username}
                  </span>
                  <button
                    type="button"
                    onClick={() => void logout()}
                    className="shrink-0 rounded-full border border-line bg-panel px-3 py-1.5 text-xs text-mute transition-colors hover:text-ink"
                  >
                    {t("auth.logout")}
                  </button>
                </div>
              ) : (
                <Link
                  href="/login"
                  className="shrink-0 rounded-full bg-accent px-3.5 py-1.5 text-xs font-bold text-white transition-colors hover:bg-accent-2"
                >
                  {t("auth.login")}
                </Link>
              ))}
            <LangSwitcher />
          </div>
        </header>
      )}

      {/* (a) напівпрозорий примірник панелі слідує за курсором під час драга */}
      {dragging && (
        <div
          ref={ghostRef}
          aria-hidden
          className="pointer-events-none fixed left-0 top-0 z-[60] flex items-center gap-2 rounded-xl border border-accent/50 bg-panel/80 px-3 opacity-70 shadow-2xl backdrop-blur-md"
          style={{ width: GHOST_W, height: GHOST_H, transform: "translate(-9999px,-9999px)" }}
        >
          <span className="text-xl" aria-hidden>
            🎬
          </span>
          <span className="truncate text-sm font-bold text-ink">{t("brand.name")}</span>
          <span className="ml-auto shrink-0 text-mute">
            {zone === "left" ? "⇤" : zone === "right" ? "⇥" : ""}
          </span>
        </div>
      )}

      {/* (a) підсвітка зон дропу зліва/справа (≤160px від краю) */}
      {dragging && (
        <>
          <div
            aria-hidden
            className={`pointer-events-none fixed left-0 top-0 z-30 transition-opacity duration-200 ${
              zone === "left" ? "opacity-100" : "opacity-0"
            }`}
            style={{ width: ZONE_PX, height: "100%" }}
          >
            <div className="h-full border-r-2 border-dashed border-accent bg-accent/10" />
          </div>
          <div
            aria-hidden
            className={`pointer-events-none fixed right-0 top-0 z-30 transition-opacity duration-200 ${
              zone === "right" ? "opacity-100" : "opacity-0"
            }`}
            style={{ width: ZONE_PX, height: "100%" }}
          >
            <div className="h-full border-l-2 border-dashed border-accent bg-accent/10" />
          </div>
        </>
      )}
    </>
  );
}
