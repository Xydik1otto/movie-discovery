// FR-21: парсер reasons-кодів by-seeds у локалізовані бейджі.
// Коди за контрактом: shared_genres:<жанр> / shared_actor:<ім'я> / decade:<YYYY0>s.
// Бекенд може ще слати готові речення (старий формат, «Спільні жанри: …») —
// тоді код не розпізнається і рядок іде як нейтральний (raw) бейдж.

import type { Lang } from "../i18n/I18nContext";

export type ReasonKind = "genre" | "actor" | "decade" | "raw";

export interface ReasonBadge {
  kind: ReasonKind;
  label: string;
}

const CODE_RE = /^(shared_genres|shared_actor|decade):(.+)$/i;

export function parseReasons(reasons: string[], lang: Lang): ReasonBadge[] {
  const out: ReasonBadge[] = [];
  for (const raw of reasons) {
    const s = raw.trim();
    if (!s) continue;
    const m = CODE_RE.exec(s);
    if (!m) {
      out.push({ kind: "raw", label: s });
      continue;
    }
    const code = m[1].toLowerCase();
    const value = m[2].trim();
    if (code === "shared_genres") {
      out.push({ kind: "genre", label: value });
    } else if (code === "shared_actor") {
      out.push({ kind: "actor", label: value });
    } else {
      // decade:<1990s> (допускаємо і «1995») → «1990-ті» / «1990s».
      const y = /(\d{4})/.exec(value);
      const dec = y ? String(Math.floor(Number(y[1]) / 10) * 10) : value.replace(/s$/i, "");
      out.push({ kind: "decade", label: lang === "uk" ? `${dec}-ті` : `${dec}s` });
    }
  }
  return out.slice(0, 4);
}
