"use client";

// UX-05: секція трейлера на сторінці тайтла.
// - ВИДИМА кнопка «Переглянути трейлер»; iframe з'являється ЛИШЕ після кліку
//   і БЕЗ autoplay (негативний acceptance g).
// - Явний вибір мов чипами — лише наявні мови кандидатів; вибір міняє фактичний ролик.
// - Початковий вибір: мова UI → збережена перевага (localStorage md_trailer_lang) →
//   unknown; БЕЗ автопідміни uk→en — якщо uk нема, чесне повідомлення + свідомий
//   вибір іншої мови (acceptance a/c).
// - gone/nonembeddable (напр., швидкий шлях titles.trailer_yt_id із перевіреним
//   статусом) → чесне повідомлення + безпечне зовнішнє посилання, не мертвий iframe (f).
// - Дані: GET /api/titles/{id}/trailers (кеш бекенда 24 год; on-demand search —
//   тільки коли кандидатів нема). Початкове значення — detail.trailers з батьківської
//   сторінки, щоб не робити зайвий запит, коли кандидати вже завантажені.

import { useEffect, useMemo, useState } from "react";
import { getTrailers } from "../lib/api";
import type { TrailerInfo } from "../lib/types";
import { useI18n } from "../i18n/I18nContext";

const LANG_PREF_KEY = "md_trailer_lang";

/** Збережена перевага мови трейлера (пишеться лише свідомим кліком по чипу). */
function readSavedLang(): string | null {
  try {
    const v = window.localStorage.getItem(LANG_PREF_KEY);
    return v && v.trim() ? v.trim() : null;
  } catch {
    return null;
  }
}

function writeSavedLang(lang: string) {
  try {
    window.localStorage.setItem(LANG_PREF_KEY, lang);
  } catch {
    /* приватний режим — перевага просто не збережеться */
  }
}

function langLabel(code: string | null, t: (key: string) => string): string {
  if (code === null) return t("trailer.unknownLang");
  const known = ["uk", "ru", "en", "multi"];
  return known.includes(code) ? t(`trailer.lang.${code}`) : code.toUpperCase();
}

export default function TrailerSection({
  titleId,
  initialTrailers,
}: {
  titleId: number | string;
  /** Кандидати вже завантажені батьківською сторінкою (detail.trailers) — фетч не потрібен. */
  initialTrailers?: TrailerInfo[] | null;
}) {
  const { t, lang } = useI18n();
  const [trailers, setTrailers] = useState<TrailerInfo[] | null>(
    initialTrailers && initialTrailers.length > 0 ? initialTrailers : null,
  );
  const [error, setError] = useState(false);
  // undefined = ще не обрано; null = обрано «Невідома мова»; рядок = конкретна мова.
  const [selectedLang, setSelectedLang] = useState<string | null | undefined>(undefined);
  const [playing, setPlaying] = useState(false);

  useEffect(() => {
    if (trailers !== null) return; // кандидати вже є (detail або попередній фетч)
    let alive = true;
    getTrailers(titleId)
      .then((res) => {
        if (alive) setTrailers(res.trailers ?? []);
      })
      .catch(() => {
        if (alive) setError(true);
      });
    return () => {
      alive = false;
    };
  }, [titleId, trailers]);

  /** Програвані: verifiedStatus ok або ще не верифіковано (бекенд не віддає синх-не-ok). */
  const playable = useMemo(
    () => (trailers ?? []).filter((tr) => !tr.verifiedStatus || tr.verifiedStatus === "ok"),
    [trailers],
  );

  /** Недоступні (gone/nonembeddable/private) — лише швидкий шлях їх віддає; чесне пояснення. */
  const stale = useMemo(
    () => (trailers ?? []).filter((tr) => !!tr.verifiedStatus && tr.verifiedStatus !== "ok"),
    [trailers],
  );

  /** Наявні мови (null = невідома) в порядку відповіді бекенда (official → uk → ru → en → …). */
  const langOptions = useMemo(() => {
    const seen: (string | null)[] = [];
    for (const tr of playable) {
      const code = tr.lang ?? null;
      if (!seen.includes(code)) seen.push(code);
    }
    return seen;
  }, [playable]);

  /** Автовибір: мова UI → збережена перевага → unknown; жодної автопідміни uk→en. */
  useEffect(() => {
    if (langOptions.length === 0) return;
    const ui = lang as string;
    const saved = readSavedLang();
    const pick =
      langOptions.find((l) => l === ui) ??
      langOptions.find((l) => l !== null && l === saved) ??
      langOptions.find((l) => l === null) ??
      undefined;
    setSelectedLang(pick ?? null);
  }, [langOptions, lang]);

  const selected =
    selectedLang === undefined
      ? null
      : playable.find((tr) => (tr.lang ?? null) === selectedLang) ?? null;
  const uiLangMissing = playable.length > 0 && !langOptions.some((l) => l === lang);

  const chooseLang = (code: string | null) => {
    setSelectedLang(code);
    if (code !== null) writeSavedLang(code); // зберігаємо лише конкретну мову, unknown не запам'ятовуємо
  };

  // ── Стани ──
  if (error) {
    return (
      <section>
        <h2 className="mb-3 text-lg font-bold">{t("title.trailer")}</h2>
        <p className="text-sm text-mute">{t("trailer.empty")}</p>
      </section>
    );
  }

  if (trailers === null) {
    return (
      <section>
        <h2 className="mb-3 text-lg font-bold">{t("title.trailer")}</h2>
        <p className="text-sm text-mute">{t("trailer.loading")}</p>
      </section>
    );
  }

  if (playable.length === 0 && stale.length === 0) {
    return (
      <section>
        <h2 className="mb-3 text-lg font-bold">{t("title.trailer")}</h2>
        <p className="text-sm text-mute">{t("trailer.empty")}</p>
      </section>
    );
  }

  return (
    <section>
      <h2 className="mb-3 text-lg font-bold">{t("title.trailer")}</h2>

      {/* Недоступні (gone/nonembeddable) — повідомлення + безпечне зовнішнє посилання (UX-05 f) */}
      {stale.length > 0 && (
        <div className="mb-3 space-y-1 rounded-2xl border border-line bg-panel p-3 text-sm">
          {stale.map((tr) => (
            <div key={tr.ytId} className="flex flex-wrap items-center gap-x-3 gap-y-1">
              <span className="text-mute">
                {tr.verifiedStatus === "nonembeddable" ? t("trailer.nonembeddable") : t("trailer.gone")}
              </span>
              <a
                href={`https://www.youtube.com/watch?v=${encodeURIComponent(tr.ytId)}`}
                target="_blank"
                rel="noopener noreferrer"
                className="font-semibold text-[color:var(--accent,#7dd3fc)] hover:underline"
              >
                {t("trailer.openExternal")} ↗
              </a>
            </div>
          ))}
        </div>
      )}

      {playable.length > 0 && (
        <>
          {/* Чипи мов — лише наявні; показуємо також єдину не-UI мову (свідомий вибір, UX-05 c);
              ★ = офіційний канал/джерело */}
          {(langOptions.length > 1 || uiLangMissing) && (
            <div className="mb-3 flex flex-wrap items-center gap-2" role="group" aria-label={t("trailer.chooseLang")}>
              {langOptions.map((code) => {
                const isActive = selectedLang !== undefined && (selectedLang ?? null) === code;
                const isOfficial = playable.some((tr) => (tr.lang ?? null) === code && tr.official);
                return (
                  <button
                    key={code ?? "unknown"}
                    type="button"
                    onClick={() => chooseLang(code)}
                    aria-pressed={isActive}
                    className={`rounded-full border px-3 py-1 text-xs font-semibold transition-colors ${
                      isActive
                        ? "border-transparent bg-[color:var(--accent,#7dd3fc)] text-black"
                        : "border-line text-ink/80 hover:border-ink/40"
                    }`}
                  >
                    {langLabel(code, t)}
                    {isOfficial && <span aria-hidden> ★</span>}
                  </button>
                );
              })}
            </div>
          )}

          {/* Чесне повідомлення: потрібної (UI) мови нема — свідомий вибір іншої (UX-05 c) */}
          {uiLangMissing && !playing && (
            <p className="mb-3 text-sm text-mute">
              {t("trailer.notFoundUi")}
              {langOptions.length > 1 ? ` — ${t("trailer.chooseLang").toLowerCase()}` : ""}
            </p>
          )}

          {/* iframe ЛИШЕ після кліку по кнопці (g); без autoplay (g); вибір мови міняє ролик (b) */}
          {playing && selected ? (
            <div className="aspect-video overflow-hidden rounded-2xl border border-line">
              <iframe
                src={`https://www.youtube.com/embed/${encodeURIComponent(selected.ytId)}`}
                title={`${t("title.trailer")}${selected.lang ? ` — ${langLabel(selected.lang, t)}` : ""}`}
                allow="accelerometer; clipboard-write; encrypted-media; gyroscope; picture-in-picture"
                allowFullScreen
                className="h-full w-full"
              />
            </div>
          ) : (
            <button
              type="button"
              onClick={() => setPlaying(true)}
              disabled={!selected}
              title={selected ? t("trailer.watch") : t("trailer.chooseLang")}
              className="flex items-center gap-2 rounded-full bg-[color:var(--accent,#7dd3fc)] px-5 py-2.5 text-sm font-bold text-black transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-40"
            >
              <span aria-hidden>▶</span> {t("trailer.watch")}
              {selected?.lang != null && <span className="font-medium opacity-80">· {langLabel(selected.lang, t)}</span>}
            </button>
          )}
        </>
      )}
    </section>
  );
}
