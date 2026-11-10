"use client";

import { useEffect, useRef, useState } from "react";
import { useI18n } from "../i18n/I18nContext";

type Phase = "loading" | "success" | "error";

/**
 * Форма слота — задається БАТЬКІВСЬКИМ контейнером (спільна правило для всіх місць):
 *  - "poster" (дефолт): 2:3 слот (TitleCard/CollectionsPanel/QuickSimilar/SeriesBlock);
 *  - "backdrop": 16:9 слот (hero сторінки тайтла) — інша іконка заглушки; стани спільні.
 * pag-2 інтегрує backdrop-варіант у title-page hero: onError → hero прибирається.
 */
export type PosterVariant = "poster" | "backdrop";

// UX-02: спільний постер-слот з 4 станами (одна правда про постери на всьому фронті).
//
// 1. metadata-missing (src=null/'') — локальна SVG-заглушка з назвою тайтла; жодного
//    запиту в мережу, слот заповнюється повністю (h-full w-full) — зона НЕ росте.
// 2. loading — <img> рендериться, поки не завантажився — під ним пульсує нейтральний фон.
// 3. error — onError рівно ОДИН автоматичний ретрай з cache-buster (багато CDN віддають
//    тимчасові збої/сталі кеші); після другого збою — остаточна чесна «Постер недоступний»
//    З КНОПКОЮ «Спробувати ще»: явний ручний ретрай перезапускає завантаження (той самий
//    URL під НОВИМ cache-buster), без unmount і без зміни src-пропса. Нескінченних
//    АВТО-повторів немає.
// 4. success — постер/фон видно (fade-in над заглушкою фону).
//
// Слот задає БАТЬКІВСЬКИЙ контейнер (TitleCard/CollectionsPanel: aspect-[2/3]; hero: 16:9
// контейнер pag-2; мініатюри: фіксовані h-…/w-…) — компонент завжди заповнює його повністю,
// тож поява/зникнення зображення не змінює розміри сітки.
// ytimg-прев'ю (та missing_original) НЕ вважаються постером: posterUrlFull у
// lib/constants.ts повертає для них null → потрапляють у metadata-missing.
export default function PosterImage({
  src,
  title,
  alt,
  eager = false,
  compact = false,
  variant = "poster",
  imgClassName = "",
  className = "",
}: {
  src: string | null | undefined;
  /** Назва тайтла — для metadata-missing заглушки й alt за замовчуванням. */
  title: string;
  /** Alt зображення (default: назва тайтла). */
  alt?: string;
  /** loading="eager" для першого екрана (TitleCard), інакше lazy. */
  eager?: boolean;
  /** Міні-слоти (thumbnail-и): менші іконка/текст заглушки. */
  compact?: boolean;
  /** Форма слота: "poster" (2:3) або "backdrop" (16:9, hero). */
  variant?: PosterVariant;
  /** Додаткові класи на <img> (hover-ефекти карток), крім базового fill. */
  imgClassName?: string;
  /** Додаткові класи на кореневий слот. */
  className?: string;
}) {
  const { t } = useI18n();
  const [phase, setPhase] = useState<Phase>(() => (src ? "loading" : "success"));
  const [buster, setBuster] = useState(0);
  const retriedRef = useRef(false);

  // Зміна тайтла/URL (ре-рендер списків, повторний enrichment) → стан з нуля,
  // лічильник ретраїв теж скидається (стара помилка не «переїжджає» на новий постер).
  useEffect(() => {
    retriedRef.current = false;
    setBuster(0);
    setPhase(src ? "loading" : "success");
  }, [src]);

  function handleError() {
    // Рівно 1 автоматичний ретрай з cache-buster, далі — чесний error (з ручним retry).
    if (!retriedRef.current) {
      retriedRef.current = true;
      setBuster(Date.now());
      return;
    }
    setPhase("error");
  }

  // Явний ручний ретрай зі стану error: та сама адреса під НОВИМ cache-buster —
  // <img> отримує новий src і браузер робить свіжий запит (unmount не потрібен).
  function retryManually() {
    retriedRef.current = false;
    setBuster(Date.now());
    setPhase("loading");
  }

  // metadata-missing: метаданих немає → SVG-заглушка з назвою; слот заповнений, зона не росте.
  if (!src) {
    return (
      <div
        className={`flex h-full w-full flex-col items-center justify-center gap-1.5 bg-gradient-to-br from-panel-2 via-[#232a3d] to-[#3a1a2b] p-2 text-center ${className}`}
      >
        <svg
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          strokeWidth="1.5"
          strokeLinecap="round"
          strokeLinejoin="round"
          aria-hidden
          className={compact ? "h-4 w-4 shrink-0 text-ink/50" : "h-8 w-8 shrink-0 text-ink/60"}
        >
          {variant === "backdrop" ? (
            <>
              {/* 16:9 кадр-плівка */}
              <rect x="2" y="6" width="20" height="12" rx="2" />
              <path d="M6 6v12M18 6v12M2 10h4M2 14h4M18 10h4M18 14h4" />
            </>
          ) : (
            <>
              {/* 2:3 постер */}
              <rect x="5" y="2" width="14" height="20" rx="2" />
              <path d="M9 2v20M15 2v20M5 7h4M5 17h4M15 7h4M15 17h4" />
            </>
          )}
        </svg>
        <span
          className={`line-clamp-3 font-semibold text-ink/80 ${compact ? "text-[9px] leading-tight" : "text-xs"}`}
        >
          {title}
        </span>
      </div>
    );
  }

  // error: авто-ретрай не допоміг → чесна заглушка + явна кнопка «Спробувати ще».
  if (phase === "error") {
    return (
      <div
        className={`flex h-full w-full flex-col items-center justify-center gap-2 bg-panel-2 p-2 text-center ${className}`}
      >
        <svg
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          strokeWidth="1.5"
          strokeLinecap="round"
          strokeLinejoin="round"
          aria-hidden
          className={compact ? "h-4 w-4 shrink-0 text-mute/70" : "h-8 w-8 shrink-0 text-mute"}
        >
          {variant === "backdrop" ? (
            <>
              {/* 16:9 кадр із хрестиком */}
              <rect x="2" y="6" width="20" height="12" rx="2" />
              <path d="M9.5 9.5l5 5M14.5 9.5l-5 5" />
            </>
          ) : (
            <>
              <rect x="3" y="3" width="18" height="18" rx="2" />
              <path d="M9 9l6 6M15 9l-6 6" />
            </>
          )}
        </svg>
        {!compact && (
          <span className="line-clamp-2 text-xs font-medium text-mute">
            {t("poster.unavailable")}
          </span>
        )}
        <button
          type="button"
          onClick={retryManually}
          className={`rounded-md border border-line px-2 py-0.5 font-semibold text-mute transition-colors hover:border-accent/60 hover:text-ink ${
            compact ? "text-[9px]" : "text-[11px]"
          }`}
        >
          ⟳ {t("poster.retry")}
        </button>
      </div>
    );
  }

  const bustedUrl = buster > 0 ? `${src}${src.includes("?") ? "&" : "?"}r=${buster}` : src;

  return (
    <div className={`relative h-full w-full ${className}`}>
      {phase === "loading" && (
        <div className="absolute inset-0 animate-pulse bg-panel-2" aria-hidden />
      )}
      <div
        className={`absolute inset-0 transition-opacity duration-300 ${
          phase === "success" ? "opacity-100" : "opacity-0"
        }`}
      >
        <img
          src={bustedUrl}
          alt={alt ?? title}
          loading={eager ? "eager" : "lazy"}
          decoding="async"
          draggable={false}
          onError={handleError}
          onLoad={() => setPhase("success")}
          className={`h-full w-full object-cover ${imgClassName}`}
        />
      </div>
    </div>
  );
}
