"use client";

import { useI18n } from "../i18n/I18nContext";

export default function Pagination({
  page,
  total,
  pageSize,
  onChange,
  capped = false,
  maxPage,
  onNextHover,
}: {
  page: number;
  total: number;
  pageSize: number;
  onChange: (p: number) => void;
  /** UX-01: total обрізаний лімітом підрахунку — межа показується як ~N. */
  capped?: boolean;
  /** Жорстка стеля глибини пагінації (синхронна з бекендом) — глибші сторінки недосяжні. */
  maxPage?: number;
  /** UX-медіа: hover/фокус/тап на «Наступна» → префетч наступної сторінки (опційно).
   *  Без текстів і без обов'язкових реквізитів — старі місця виклику не змінюються. */
  onNextHover?: () => void;
}) {
  const { t } = useI18n();
  const totalPages = Math.max(1, Math.ceil(total / pageSize));
  if (totalPages <= 1) return null;
  const lastPage = maxPage != null ? Math.min(totalPages, maxPage) : totalPages;
  if (lastPage <= 1) return null;
  const nextDisabled = page >= lastPage;
  // Префетч лише коли «Наступна» активна: disabled-кнопка сторінку все одно не дає.
  const prefetchNext = nextDisabled || !onNextHover ? undefined : () => onNextHover();

  return (
    <nav className="mt-6 flex items-center justify-center gap-3" aria-label="Pagination">
      <button
        type="button"
        disabled={page <= 1}
        onClick={() => onChange(page - 1)}
        className="rounded-lg border border-line bg-panel px-4 py-2 text-sm text-ink transition-colors enabled:hover:border-accent disabled:opacity-40"
      >
        ← {t("common.prev")}
      </button>
      <span className="text-sm text-mute">
        {capped
          ? t("common.pageOfCapped", { p: page, t: totalPages })
          : t("common.pageOf", { p: page, t: totalPages })}
      </span>
      <button
        type="button"
        disabled={nextDisabled}
        onClick={() => onChange(page + 1)}
        onMouseEnter={prefetchNext}
        onFocus={prefetchNext}
        onTouchStart={prefetchNext}
        className="rounded-lg border border-line bg-panel px-4 py-2 text-sm text-ink transition-colors enabled:hover:border-accent disabled:opacity-40"
      >
        {t("common.next")} →
      </button>
    </nav>
  );
}
