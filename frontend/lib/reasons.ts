// FR-21: парсер reasons-кодів by-seeds у локалізовані бейджі.
// Коди за контрактом: shared_genres:<жанр> / shared_actor:<ім'я> / decade:<YYYY0>s.
// Бекенд може ще слати готові речення (старий формат, «Спільні жанри: …») —
// тоді код не розпізнається і рядок іде як нейтральний (raw) бейдж.

import { GENRES } from "./constants";
import type { Lang } from "../i18n/I18nContext";

export type ReasonKind = "genre" | "actor" | "decade" | "raw";

export interface ReasonBadge {
  kind: ReasonKind;
  label: string;
}

const CODE_RE = /^(shared_genres|shared_actor|decade):(.+)$/i;

// MD-08: у кодах reasons бекенд шле англійські жанрові ключі TMDB
// («спільний жанр: adventure» у uk UI — неправильно). Мапимо через довідник
// GENRES (uk-значення + en-назви) і докладаємо ключі, яких у довіднику нема.
// Невідомі ключі лишаємо «як є» (не вигадуємо переклад).
const GENRE_UK: Record<string, string> = (() => {
  const m: Record<string, string> = {};
  for (const g of GENRES) {
    m[g.value.toLowerCase()] = g.uk;
    m[g.en.toLowerCase()] = g.uk;
  }
  // TMDB-ключі, яких нема у фільтровому довіднику GENRES
  m["science fiction"] = "фантастика";
  m["sci-fi"] = "фантастика";
  m["family"] = "сімейний";
  m["western"] = "вестерн";
  m["tv movie"] = "телефільм";
  return m;
})();

function localizeGenre(value: string, lang: Lang): string {
  if (lang !== "uk") return value; // en-інтерфейс: англійські ключі доречні
  return GENRE_UK[value.toLowerCase()] ?? value;
}

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
      out.push({ kind: "genre", label: localizeGenre(value, lang) });
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
