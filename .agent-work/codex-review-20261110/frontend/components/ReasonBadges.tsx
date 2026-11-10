"use client";

import { useI18n } from "../i18n/I18nContext";
import { parseReasons } from "../lib/reasons";

const KIND_STYLE: Record<string, string> = {
  // FR-21: жанр — жовтий, актор — синій, декада — сіра.
  genre: "border-amber-400/40 bg-amber-500/15 text-amber-200",
  actor: "border-sky-400/40 bg-sky-500/15 text-sky-200",
  decade: "border-line bg-panel-2 text-mute",
  raw: "border-line bg-panel-2 text-mute",
};

// Маленькі бейджі-пояснення «чому схоже» на картках результатів.
// Коди бекенду локалізуємо (i18n-шаблони); незнані рядки — нейтральний бейдж.
export default function ReasonBadges({ reasons }: { reasons?: string[] }) {
  const { t, lang } = useI18n();
  if (!reasons?.length) return null;
  const badges = parseReasons(reasons, lang);
  if (!badges.length) return null;
  return (
    <div className="mt-1.5 flex flex-wrap gap-1">
      {badges.map((b, i) => (
        <span
          key={`${b.kind}-${i}`}
          className={`rounded-md border px-1.5 py-0.5 text-[10px] font-medium leading-tight ${KIND_STYLE[b.kind] ?? KIND_STYLE.raw}`}
        >
          {b.kind === "genre" && t("similar.reason.genre", { value: b.label })}
          {b.kind === "actor" && t("similar.reason.actor", { value: b.label })}
          {b.kind === "decade" && t("similar.reason.decade", { decade: b.label })}
          {b.kind === "raw" && b.label}
        </span>
      ))}
    </div>
  );
}
