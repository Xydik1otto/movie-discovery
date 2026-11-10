"use client";

import { useI18n } from "../i18n/I18nContext";
import type { Lang } from "../i18n/I18nContext";

export default function LangSwitcher() {
  const { lang, setLang, t } = useI18n();

  const btn = (l: Lang, label: string) => (
    <button
      key={l}
      type="button"
      aria-pressed={lang === l}
      aria-label={t("lang.switch")}
      onClick={() => setLang(l)}
      className={`rounded-md px-2.5 py-1 text-xs font-semibold transition-colors ${
        lang === l
          ? "bg-accent text-white"
          : "text-mute hover:bg-panel-2 hover:text-ink"
      }`}
    >
      {label}
    </button>
  );

  return (
    <div
      role="group"
      aria-label={t("lang.switch")}
      className="flex shrink-0 items-center gap-0.5 rounded-lg border border-line bg-panel p-0.5"
    >
      {btn("uk", "UA")}
      {btn("en", "EN")}
      {btn("ru", "RU")}
    </div>
  );
}
