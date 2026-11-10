"use client";

import { useEffect, useRef, useState } from "react";
import { useI18n } from "../i18n/I18nContext";

type Phase = "loading" | "success" | "error";

// UX-02: спільний постер-слот з 4 станами (одна правда про постери на всьому фронті).
//
// 1. metadata-missing (src=null/'') — локальна SVG-заглушка з назвою тайтла; жодного
//    запиту в мережу, слот заповнюється повністю (h-full w-full) — зона НЕ росте.
// 2. loading — <img> рендериться, поки не завантажився — під ним пульсує нейтральний фон.
// 3. error — onError рівно ОДИН ретрай з cache-buster (багато CDN віддають тимчасові
//    збої/сталі кеші); після другого збою — остаточна чесна «Постер недоступний».
//    Нескінченних повторів немає.
// 4. success — постер видно (fade-in над заглушкою фону).
//
// Слот задає БАТЬКІВСКИЙ контейнер (TitleCard/CollectionsPanel: aspect-[2/3];
// мініатюри: фіксовані h-…/w-…) — компонент завжди заповнює його повністю,
// тож поява/зникнення постера не змінює розміри сітки.
// ytimg-прев'ю (та missing_original) НЕ вважаються постером: posterUrlFull у
// lib/constants.ts повертає для них null → потрапляють у metadata-missing.
export default function PosterImage({
  src,
  title,
  alt,
  eager = false,
  compact = false,
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
    // Рівно 1 ретрай з cache-buster, далі — чесний error назавжди (до зміни src).
    if (!retriedRef.current) {
      retriedRef.current = true;
      setBuster(Date.now());
      return;
    }
    setPhase("error");
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
          <rect x="3" y="3" width="18" height="18" rx="2" />
          <path d="M7 3v18M17 3v18M3 8h4M3 16h4M17 8h4M17 16h4" />
        </svg>
        <span
          className={`line-clamp-3 font-semibold text-ink/80 ${compact ? "text-[9px] leading-tight" : "text-xs"}`}
        >
          {title}
        </span>
      </div>
    );
  }

  // error: обидві спроби (оригінал + 1 ретрай з cache-buster) дали збій → чесна заглушка.
  if (phase === "error") {
    return (
      <div
        className={`flex h-full w-full flex-col items-center justify-center gap-1.5 bg-panel-2 p-2 text-center ${className}`}
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
          <rect x="3" y="3" width="18" height="18" rx="2" />
          <path d="M9 9l6 6M15 9l-6 6" />
        </svg>
        <span
          className={`line-clamp-2 font-medium text-mute ${compact ? "text-[9px] leading-tight" : "text-xs"}`}
        >
          {t("poster.unavailable")}
        </span>
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
