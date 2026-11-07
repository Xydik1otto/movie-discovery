"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useI18n } from "../i18n/I18nContext";
import LangSwitcher from "./LangSwitcher";

const NAV = [
  { href: "/", key: "nav.home" },
  { href: "/similar", key: "nav.similar" },
  { href: "/lists", key: "nav.lists" },
  { href: "/settings", key: "nav.settings" },
];

export default function Header() {
  const { t } = useI18n();
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

        <LangSwitcher />
      </div>
    </header>
  );
}
