"use client";

// Next 404 (невідомий маршрут). Локалізована: рендериться всередині RootLayout,
// тож I18nProvider (як для всіх сторінок без динамічного сегмента) доступний і
// useI18n читає мову з localStorage/контексту.
import Link from "next/link";
import { useI18n } from "@/i18n/I18nContext";

export default function NotFound() {
  const { t } = useI18n();
  return (
    <div className="flex flex-col items-center justify-center rounded-2xl border border-line bg-panel px-6 py-14 text-center">
      <div className="mb-3 text-5xl" aria-hidden>
        🎬
      </div>
      <h1 className="text-xl font-extrabold tracking-tight sm:text-2xl">
        {t("notFound.title")}
      </h1>
      <p className="mt-2 max-w-md text-sm text-mute">{t("notFound.text")}</p>
      <Link
        href="/"
        className="mt-5 rounded-xl bg-accent px-5 py-2.5 text-sm font-bold text-white transition-colors hover:bg-accent-2"
      >
        {t("onboarding.done.home")}
      </Link>
    </div>
  );
}
