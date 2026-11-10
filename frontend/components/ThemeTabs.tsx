"use client";

// Верхньорівневі тими каталогу: Фільми / Серіали / Анімація (фільми) /
// Аніме (багатосерійні). value → category=movie|series|cartoon|anime
// (аніме = багатосерійна анімація, cartoon = повнометражна анімація).
// Вибір тими живе в URL (?category=). Лічильники — з /api/filters counts,
// коли ендпоінт досяжний; інакше чіпи рендеряться без чисел (деградація).
// «Усі» (value=null) скидає категорію. current поза списком тем (наприклад
// "manga" на /manga) означає «жодна тима не активна» — чіпи не підсвічені.

import { Chip } from "./FilterChips";
import { useI18n } from "../i18n/I18nContext";
import type { FiltersCounts } from "../lib/types";

export type ThemeValue = "movie" | "series" | "cartoon" | "anime";

const THEMES: {
  value: ThemeValue;
  key: string;
  countKey: keyof FiltersCounts;
}[] = [
  { value: "movie", key: "themes.movies", countKey: "movies" },
  { value: "series", key: "themes.series", countKey: "series" },
  { value: "cartoon", key: "themes.cartoon", countKey: "animation_movies" },
  { value: "anime", key: "themes.anime", countKey: "animation_series" },
];

export default function ThemeTabs({
  current,
  counts,
  onSelect,
}: {
  /** Активна тима: код категорії або null («Усі»). Значення поза списком = нічого не активне. */
  current: string | null;
  /** Лічильники з /api/filters; undefined/null/ендпоінт недоступний → без чисел. */
  counts: FiltersCounts | null | undefined;
  onSelect: (value: ThemeValue | null) => void;
}) {
  const { t, lang } = useI18n();
  const locale = lang === "ru" ? "ru-RU" : lang === "en" ? "en-US" : "uk-UA";
  const fmt = (n: number | null | undefined): string | null =>
    n == null || n < 0 ? null : n.toLocaleString(locale);

  return (
    <div className="no-scrollbar -mx-1 mb-4 flex items-center gap-1.5 overflow-x-auto px-1">
      <Chip selected={current === null} onClick={() => onSelect(null)}>
        {t("themes.all")}
      </Chip>
      {THEMES.map((th) => {
        const c = counts ? fmt(counts[th.countKey]) : null;
        return (
          <Chip
            key={th.value}
            selected={current === th.value}
            onClick={() => onSelect(th.value)}
          >
            {t(th.key)}
            {c ? (
              <span className="ml-1.5 whitespace-nowrap text-[10px] font-normal opacity-70">
                {c}
              </span>
            ) : null}
          </Chip>
        );
      })}
    </div>
  );
}
