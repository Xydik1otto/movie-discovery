"use client";

import { useI18n } from "../i18n/I18nContext";

export default function ApiBanner() {
  const { t } = useI18n();
  return (
    <div
      role="status"
      className="mb-4 flex items-center gap-2 rounded-xl border border-amber-500/30 bg-amber-500/10 px-4 py-2.5 text-sm text-amber-300"
    >
      <span aria-hidden>⚠️</span>
      <span>{t("api.unavailable")}</span>
    </div>
  );
}
