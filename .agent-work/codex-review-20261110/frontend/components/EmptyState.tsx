"use client";

import { useI18n } from "../i18n/I18nContext";

export default function EmptyState({
  title,
  hint,
  icon = "🍿",
}: {
  title?: string;
  hint?: string;
  icon?: string;
}) {
  const { t } = useI18n();
  return (
    <div className="flex flex-col items-center justify-center rounded-2xl border border-line bg-panel px-6 py-14 text-center">
      <div className="mb-3 text-4xl" aria-hidden>
        {icon}
      </div>
      <p className="max-w-md text-sm text-mute">{title ?? t("lists.empty")}</p>
      {hint && <p className="mt-2 max-w-md text-xs text-mute opacity-70">{hint}</p>}
    </div>
  );
}
