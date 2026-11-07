"use client";

// FR-18: онбординг після реєстрації. Кроки: 3 екрани постер-мультивибірів
// (жанри, статичні групи з /api/onboarding/questions), настрій, ера,
// 3 улюблених тайтли через пошук-автокомпліт. «Пропустити» — у будь-який
// момент (skipped=true). Прогрес-бар. API недоступний → демо-квіз.

import { useCallback, useEffect, useRef, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useI18n } from "@/i18n/I18nContext";
import { useAuth } from "@/lib/auth";
import {
  ApiError,
  guardedOnboardingQuestions,
  guardedSearch,
  postOnboardingAnswers,
} from "@/lib/fallback";
import type { OnboardingQuestion, TitleCard } from "@/lib/types";
import { posterUrlFull } from "@/lib/constants";

const MAX_FAVORITES = 3;

export default function OnboardingPage() {
  const { t, lang } = useI18n();
  const { user, ready, apiUp, refresh } = useAuth();
  const router = useRouter();

  const [questions, setQuestions] = useState<OnboardingQuestion[]>([]);
  const [loading, setLoading] = useState(true);
  const [unavailable, setUnavailable] = useState(false);
  const [step, setStep] = useState(0);
  const [picks, setPicks] = useState<Record<string, string[]>>({});
  const [favorites, setFavorites] = useState<TitleCard[]>([]);
  const [submitting, setSubmitting] = useState(false);
  const [done, setDone] = useState(false);

  // Автокомпліт улюблених тайтлів
  const [query, setQuery] = useState("");
  const [options, setOptions] = useState<TitleCard[]>([]);
  const [open, setOpen] = useState(false);
  const boxRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    // Анонім на живому API → на вхід.
    if (ready && apiUp && !user) {
      router.replace("/login?needLogin=1&next=/onboarding");
      return;
    }
    if (!ready) return;
    if (apiUp && !user) return;
    let alive = true;
    setLoading(true);
    guardedOnboardingQuestions(lang)
      .then((r) => {
        if (!alive) return;
        setQuestions(r.data);
        setUnavailable(r.unavailable);
      })
      .catch(() => {
        if (alive) setUnavailable(true);
      })
      .finally(() => {
        if (alive) setLoading(false);
      });
    return () => {
      alive = false;
    };
  }, [ready, apiUp, user, lang, router]);

  // Debounced пошук для кроку «3 улюблених тайтли».
  useEffect(() => {
    const q = query.trim();
    if (q.length < 2) {
      setOptions([]);
      return;
    }
    const timer = setTimeout(async () => {
      try {
        const r = await guardedSearch({ q, pageSize: 8, page: 1 }, lang);
        setOptions(r.data.items);
        setOpen(true);
      } catch {
        // помилки автокомпліту не блокуємо
      }
    }, 350);
    return () => clearTimeout(timer);
  }, [query, lang]);

  useEffect(() => {
    function onDoc(e: MouseEvent) {
      if (boxRef.current && !boxRef.current.contains(e.target as Node)) setOpen(false);
    }
    document.addEventListener("mousedown", onDoc);
    return () => document.removeEventListener("mousedown", onDoc);
  }, []);

  const totalSteps = questions.length + 1; // + крок улюблених тайтлів
  const currentQ = step < questions.length ? questions[step] : null;

  const togglePick = useCallback((qid: string, oid: string) => {
    setPicks((prev) => {
      const cur = prev[qid] ?? [];
      return { ...prev, [qid]: cur.includes(oid) ? cur.filter((x) => x !== oid) : [...cur, oid] };
    });
  }, []);

  const submit = useCallback(
    async (skipped: boolean) => {
      setSubmitting(true);
      try {
        if (!unavailable) {
          await postOnboardingAnswers({
            picks: Object.entries(picks).map(([questionId, values]) => ({
              questionId,
              values,
            })),
            mood: picks["mood"] ?? [],
            era: picks["era"]?.[0] ?? null,
            favoriteTitles: favorites.map((f) => ({
              titleId: f.id,
              tmdbId: f.tmdbId ?? null,
              title: f.title,
            })),
            skipped,
          });
          await refresh();
        }
        setDone(true);
      } catch (e) {
        if (e instanceof ApiError && e.status === 401) {
          router.replace("/login?needLogin=1&next=/onboarding");
          return;
        }
        // Інші збої не блокують: квіз можна «пропустити» пізніше з Header.
        setDone(true);
      } finally {
        setSubmitting(false);
      }
    },
    [picks, favorites, unavailable, refresh, router],
  );

  if (done) {
    return (
      <div className="mx-auto max-w-md rounded-3xl border border-line bg-panel px-6 py-12 text-center">
        <div className="mb-3 text-5xl" aria-hidden>
          🎉
        </div>
        <h1 className="text-2xl font-extrabold">{t("onboarding.done.title")}</h1>
        <p className="mt-2 text-sm text-mute">{t("onboarding.done.text")}</p>
        <div className="mt-6 flex flex-col items-center gap-2">
          <Link
            href="/discover"
            className="rounded-xl bg-accent px-6 py-2.5 text-sm font-bold text-white transition-colors hover:bg-accent-2"
          >
            {t("onboarding.done.discover")}
          </Link>
          <Link
            href="/"
            className="rounded-xl border border-line px-6 py-2.5 text-sm text-mute transition-colors hover:text-ink"
          >
            {t("onboarding.done.home")}
          </Link>
        </div>
      </div>
    );
  }

  if (loading || !ready) {
    return (
      <div className="mx-auto max-w-2xl py-10 text-center text-sm text-mute">
        {t("common.loading")}
      </div>
    );
  }

  if (apiUp && !user) {
    return (
      <div className="mx-auto max-w-2xl py-10 text-center text-sm text-mute">
        {t("common.loading")}
      </div>
    );
  }

  const progress = totalSteps > 0 ? Math.min(100, ((step + 1) / totalSteps) * 100) : 0;

  const addFavorite = (item: TitleCard) => {
    if (favorites.length >= MAX_FAVORITES) return;
    if (favorites.some((f) => f.id === item.id)) return;
    setFavorites((prev) => [...prev, item]);
    setQuery("");
    setOptions([]);
    setOpen(false);
  };

  return (
    <div className="mx-auto max-w-2xl">
      {/* Заголовок + прогрес-бар */}
      <div className="mb-6">
        <div className="flex items-baseline justify-between gap-3">
          <h1 className="text-2xl font-extrabold tracking-tight sm:text-3xl">
            {t("onboarding.title")}
          </h1>
          <button
            type="button"
            onClick={() => void submit(true)}
            disabled={submitting}
            className="text-sm text-mute underline-offset-2 transition-colors hover:text-ink hover:underline disabled:opacity-50"
          >
            {t("onboarding.skip")}
          </button>
        </div>
        <p className="mt-1 text-sm text-mute">{t("onboarding.subtitle")}</p>
        <div className="mt-4 h-1.5 overflow-hidden rounded-full bg-panel-2">
          <div
            className="h-full rounded-full bg-accent transition-all duration-300"
            style={{ width: `${progress}%` }}
          />
        </div>
        <p className="mt-1.5 text-xs text-mute">
          {t("onboarding.step", { n: step + 1, t: totalSteps })}
        </p>
      </div>

      {unavailable && (
        <p className="mb-4 rounded-xl border border-amber-500/30 bg-amber-500/10 px-4 py-2.5 text-sm text-amber-300">
          {t("api.unavailable")}
        </p>
      )}

      {/* Крок-питання (постери/настрій/ера) */}
      {currentQ && (
        <section className="rounded-3xl border border-line bg-panel p-5 sm:p-6">
          <h2 className="text-lg font-bold">
            {currentQ.type === "posters"
              ? t("onboarding.q.posters")
              : currentQ.type === "mood"
                ? t("onboarding.q.mood")
                : currentQ.type === "era"
                  ? t("onboarding.q.era")
                  : (currentQ.title ?? "")}
          </h2>

          <div className="mt-4 grid grid-cols-2 gap-2.5 sm:grid-cols-3">
            {(currentQ.options ?? []).map((o) => {
              const selected = (picks[currentQ.id] ?? []).includes(o.id);
              const poster = posterUrlFull(o.posterUrl, "w300");
              return (
                <button
                  key={o.id}
                  type="button"
                  aria-pressed={selected}
                  onClick={() => togglePick(currentQ.id, o.id)}
                  className={`overflow-hidden rounded-2xl border text-left transition-all active:scale-[0.97] ${
                    selected
                      ? "border-accent ring-2 ring-accent/40"
                      : "border-line hover:border-accent/50"
                  }`}
                >
                  <div className="relative aspect-[4/3] bg-panel-2">
                    {poster ? (
                      <img
                        src={poster}
                        alt=""
                        className="h-full w-full object-cover"
                        loading="lazy"
                      />
                    ) : (
                      <div className="flex h-full w-full items-center justify-center bg-gradient-to-br from-panel-2 via-[#232a3d] to-[#3a1a2b] text-2xl">
                        {currentQ.type === "mood" ? "🎭" : currentQ.type === "era" ? "🕰️" : "🎞️"}
                      </div>
                    )}
                    {selected && (
                      <span className="absolute right-2 top-2 flex h-6 w-6 items-center justify-center rounded-full bg-accent text-xs font-bold text-white">
                        ✓
                      </span>
                    )}
                  </div>
                  <div className="px-3 py-2 text-sm font-semibold text-ink">
                    {o.label ?? o.value ?? o.id}
                  </div>
                </button>
              );
            })}
          </div>
        </section>
      )}

      {/* Крок «3 улюблених тайтли» */}
      {!currentQ && (
        <section className="rounded-3xl border border-line bg-panel p-5 sm:p-6">
          <h2 className="text-lg font-bold">{t("onboarding.q.titles")}</h2>
          <p className="mt-1 text-xs text-mute">{t("onboarding.titlesHint")}</p>
          <p className="mt-1 text-xs text-mute">
            {t("onboarding.selected", { n: favorites.length })}
          </p>

          <div ref={boxRef} className="relative mt-4">
            <input
              type="text"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              onFocus={() => options.length > 0 && setOpen(true)}
              onKeyDown={(e) => {
                if (e.key === "Escape") setOpen(false);
                if (e.key === "Enter" && options.length > 0 && open) {
                  e.preventDefault();
                  addFavorite(options[0]);
                }
              }}
              placeholder={t("onboarding.searchPlaceholder")}
              disabled={favorites.length >= MAX_FAVORITES}
              className="w-full rounded-xl border border-line bg-bg px-4 py-2.5 text-sm text-ink placeholder:text-mute/60 focus:border-accent focus:outline-none focus:ring-2 focus:ring-accent/30 disabled:opacity-50"
            />
            {open && options.length > 0 && (
              <ul className="absolute z-30 mt-1 max-h-72 w-full overflow-y-auto rounded-xl border border-line bg-panel shadow-xl">
                {options.map((o) => {
                  const picked = favorites.some((f) => f.id === o.id);
                  return (
                    <li key={o.id}>
                      <button
                        type="button"
                        disabled={picked}
                        onMouseDown={(e) => {
                          e.preventDefault();
                          addFavorite(o);
                        }}
                        className={`flex w-full items-center gap-2 px-3 py-2 text-left text-sm transition-colors ${
                          picked
                            ? "cursor-default text-mute opacity-50"
                            : "text-ink hover:bg-panel-2"
                        }`}
                      >
                        <span aria-hidden>🎞️</span>
                        <span className="min-w-0 flex-1 truncate">
                          {o.title}
                          {o.year != null ? ` (${o.year})` : ""}
                        </span>
                        {picked && <span aria-hidden>✓</span>}
                      </button>
                    </li>
                  );
                })}
              </ul>
            )}
          </div>

          {favorites.length > 0 && (
            <div className="mt-3 flex flex-wrap gap-1.5">
              {favorites.map((f) => (
                <span
                  key={f.id}
                  className="inline-flex items-center gap-1.5 rounded-full border border-accent/40 bg-accent/10 py-1 pl-3 pr-1.5 text-xs font-medium text-accent-2"
                >
                  {f.title}
                  {f.year != null ? ` (${f.year})` : ""}
                  <button
                    type="button"
                    aria-label={`${t("lists.remove")}: ${f.title}`}
                    onClick={() => setFavorites((prev) => prev.filter((x) => x.id !== f.id))}
                    className="rounded-full px-1 text-accent-2/70 hover:bg-accent/20 hover:text-accent-2"
                  >
                    ✕
                  </button>
                </span>
              ))}
            </div>
          )}
        </section>
      )}

      {/* Навігація кроків */}
      <div className="mt-5 flex items-center justify-between gap-3">
        <button
          type="button"
          onClick={() => setStep((s) => Math.max(0, s - 1))}
          disabled={step === 0 || submitting}
          className="rounded-xl border border-line bg-panel px-4 py-2.5 text-sm text-mute transition-colors hover:text-ink disabled:opacity-40"
        >
          {t("onboarding.back")}
        </button>
        <button
          type="button"
          onClick={() => {
            if (step < totalSteps - 1) setStep((s) => s + 1);
            else void submit(false);
          }}
          disabled={submitting}
          className="rounded-xl bg-accent px-6 py-2.5 text-sm font-bold text-white transition-all enabled:hover:bg-accent-2 enabled:active:scale-95 disabled:opacity-60"
        >
          {step < totalSteps - 1
            ? t("onboarding.next")
            : submitting
              ? t("common.loading")
              : t("onboarding.finish")}
        </button>
      </div>
    </div>
  );
}
