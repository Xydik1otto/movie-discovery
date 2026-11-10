"use client";

import { useEffect, useState } from "react";
import { useI18n, type Lang } from "@/i18n/I18nContext";
import { API_URL } from "@/lib/api";
import { REGIONS } from "@/lib/constants";

export default function SettingsPage() {
  const { t, lang, setLang } = useI18n();
  const [region, setRegion] = useState("UA");
  const [saved, setSaved] = useState(false);

  useEffect(() => {
    try {
      const r = window.localStorage.getItem("region");
      if (r) setRegion(r);
    } catch {
      // default UA
    }
  }, []);

  function changeRegion(r: string) {
    setRegion(r);
    try {
      window.localStorage.setItem("region", r);
      setSaved(true);
      window.setTimeout(() => setSaved(false), 1500);
    } catch {
      // ignore
    }
  }

  return (
    <div className="max-w-xl">
      <h1 className="mb-5 text-2xl font-extrabold tracking-tight sm:text-3xl">
        {t("nav.settings")}
      </h1>

      <section className="mb-4 rounded-2xl border border-line bg-panel p-4">
        <h2 className="mb-3 text-base font-bold">{t("settings.language")}</h2>
        <div className="flex gap-2">
          {(
            [
              ["uk", "Українська"],
              ["en", "English"],
              ["ru", "Русский"],
            ] as [Lang, string][]
          ).map(([code, label]) => (
            <button
              key={code}
              type="button"
              aria-pressed={lang === code}
              onClick={() => setLang(code)}
              className={`flex-1 rounded-xl border px-4 py-3 text-sm font-semibold transition-colors ${
                lang === code
                  ? "border-accent bg-accent/15 text-accent-2"
                  : "border-line bg-panel-2 text-mute hover:text-ink"
              }`}
            >
              {label}
            </button>
          ))}
        </div>
      </section>

      <section className="rounded-2xl border border-line bg-panel p-4">
        <h2 className="mb-1 text-base font-bold">{t("settings.region")}</h2>
        <p className="mb-3 text-xs text-mute">{t("settings.regionHint")}</p>
        <select
          value={region}
          onChange={(e) => changeRegion(e.target.value)}
          className="w-full rounded-xl border border-line bg-bg px-3 py-2.5 text-sm focus:border-accent focus:outline-none"
        >
          {REGIONS.map((r) => (
            <option key={r} value={r}>
              {r}
            </option>
          ))}
        </select>
        {saved && (
          <p className="mt-2 text-xs text-emerald-400">✓ {t("title.added")}</p>
        )}
      </section>

      <p className="mt-4 text-xs text-mute opacity-60">API: {API_URL}</p>
    </div>
  );
}
