"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useI18n } from "../i18n/I18nContext";
import { useAuth } from "../lib/auth";
import LangSwitcher from "./LangSwitcher";

const NAV = [
  { href: "/", key: "nav.home" },
  { href: "/discover", key: "nav.discover" },
  { href: "/anime", key: "nav.anime" },
  { href: "/manga", key: "nav.manga" },
  { href: "/similar", key: "nav.similar" },
  { href: "/lists", key: "nav.lists" },
  { href: "/import", key: "nav.import" },
  { href: "/settings", key: "nav.settings" },
];

export default function Header() {
  const { t } = useI18n();
  const { user, ready, logoutFailed, logout } = useAuth();
  const pathname = usePathname();

  return (
    <header className="sticky top-0 z-40 border-b border-line bg-bg/85 backdrop-blur-md">
      <div className="mx-auto flex max-w-7xl items-center gap-3 px-4 py-3">
        <Link href="/" className="flex shrink-0 items-center gap-2">
          <span className="text-xl" aria-hidden>
            🎬
          </span>
          <span className="text-base font-bold tracking-tight text-ink sm:text-lg">
            {t("brand.name")}
          </span>
        </Link>

        <nav
          aria-label="main"
          className="no-scrollbar -mx-1 flex flex-1 items-center gap-1 overflow-x-auto px-1"
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
  );
}
