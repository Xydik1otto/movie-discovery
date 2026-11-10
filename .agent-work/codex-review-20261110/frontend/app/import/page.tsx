"use client";

// «Імпорт переглянутого»: три джерела (MyAnimeList / AniList / Shikimori),
// нікнейм → POST /api/import/jobs (202 {jobId}) → опитування GET /api/import/jobs/{id}
// кожні 1.5 с з прогресом і підсумком. Демо-підмінки НЕМАЄ: без API імпорт не працює.

import { useEffect, useRef, useState } from "react";
import Link from "next/link";
import { useI18n } from "@/i18n/I18nContext";
import {
  ApiUnavailableError,
  getImportJob,
  getImportSources,
  startImportJob,
} from "@/lib/api";
import { isUnavailable } from "@/lib/fallback";
import type { ImportJob, ImportJobStatus, ImportSource } from "@/lib/types";
import ApiBanner from "@/components/ApiBanner";
import LoadingSkeleton from "@/components/LoadingSkeleton";

// Джерела за PLAN — статичний перелік; API /api/import/sources повертає те саме.
// Локальний перелік потрібен, щоб форма була видно й без бекенду (але старт тоді
// вимкнено — імпорт без API не емулюємо).
const LOCAL_SOURCES: ImportSource[] = [
  { id: "mal", name: "MyAnimeList", hint: "" },
  { id: "anilist", name: "AniList", hint: "" },
  { id: "shikimori", name: "Shikimori", hint: "" },
];

const POLL_INTERVAL_MS = 1500;

function statusKey(status: ImportJobStatus): string {
  return status === "queued"
    ? "import.statusQueued"
    : status === "running"
      ? "import.statusRunning"
      : status === "done"
        ? "import.statusDone"
        : "import.statusFailed";
}

// Осмислена помилка джоби: приватний профіль / незнайдений нікнейм.
function failureMessage(error: string | null | undefined, t: (k: string) => string): string {
  const e = (error ?? "").toLowerCase();
  if (e.includes("privat")) return t("import.private");
  if (e.includes("not found") || e.includes("404") || e.includes("не знайд")) {
    return t("import.notFound");
  }
  return error || t("common.error");
}

export default function ImportPage() {
  const { t } = useI18n();
  const [sources, setSources] = useState<ImportSource[]>(LOCAL_SOURCES);
  const [sourcesLoading, setSourcesLoading] = useState(true);
  const [unavailable, setUnavailable] = useState(false);

  const [selected, setSelected] = useState<string>("mal");
  const [username, setUsername] = useState("");
  const [job, setJob] = useState<ImportJob | null>(null);
  const [starting, setStarting] = useState(false);
  const [startError, setStartError] = useState<string | null>(null);
  const [pollError, setPollError] = useState(false);
  const pollRef = useRef<{ alive: boolean } | null>(null);

  useEffect(() => {
    let alive = true;
    getImportSources()
      .then((r) => {
        if (!alive) return;
        if (r.length > 0) setSources(r);
        setUnavailable(false);
      })
      .catch((e) => {
        if (alive && isUnavailable(e)) setUnavailable(true);
      })
      .finally(() => {
        if (alive) setSourcesLoading(false);
      });
    return () => {
      alive = false;
    };
  }, []);

  // Опитування джоби: setTimeout-ланцюжок, зупинка на done/failed/unmount.
  function poll(jobId: number) {
    const state = { alive: true };
    pollRef.current = state;
    let misses = 0;

    const tick = async () => {
      if (!state.alive) return;
      try {
        const j = await getImportJob(jobId);
        misses = 0;
        setPollError(false);
        setJob(j);
        if (j.status === "done" || j.status === "failed") {
          state.alive = false;
          return;
        }
      } catch (e) {
        if (!state.alive) return;
        // Мережева відмова під час опитування: кілька спроб — тоді зупинка.
        if (e instanceof ApiUnavailableError || isUnavailable(e)) {
          misses += 1;
          if (misses >= 4) {
            setPollError(true);
            state.alive = false;
            return;
          }
        } else {
          setPollError(true);
          state.alive = false;
          return;
        }
      }
      setTimeout(tick, POLL_INTERVAL_MS);
    };
    setTimeout(tick, POLL_INTERVAL_MS);
  }

  useEffect(() => {
    return () => {
      if (pollRef.current) pollRef.current.alive = false;
    };
  }, []);

  async function start() {
    const name = username.trim();
    if (!name || starting) return;
    setStarting(true);
    setStartError(null);
    setPollError(false);
    setJob(null);
    try {
      const { jobId } = await startImportJob(selected, name);
      poll(jobId);
    } catch (e) {
      // Без API джобу не створити — ніяких демо-підмінок.
      const down = isUnavailable(e) || e instanceof ApiUnavailableError;
      if (down) setUnavailable(true);
      setStartError(down ? t("api.unavailable") : t("common.error"));
    } finally {
      setStarting(false);
    }
  }

  const busy = starting || (job != null && (job.status === "queued" || job.status === "running"));
  const done = job?.status === "done";
  const failed = job?.status === "failed";
  const stats = job?.stats ?? null;
  const found =
    stats && (stats.animeTotal != null || stats.mangaTotal != null)
      ? (stats.animeTotal ?? 0) + (stats.mangaTotal ?? 0)
      : null;
  const selectedSource = sources.find((s) => s.id === selected) ?? sources[0];
  const hintFor = (id: string): string => {
    const key = `import.hint.${id}`;
    const localized = t(key);
    return localized !== key ? localized : (sources.find((s) => s.id === id)?.hint ?? "");
  };

  return (
    <div>
      <section className="mb-6">
        <h1 className="text-2xl font-extrabold tracking-tight sm:text-3xl">
          {t("import.title")}
        </h1>
        <p className="mt-1 max-w-2xl text-sm text-mute">{t("import.subtitle")}</p>
      </section>

      <p className="mb-5 rounded-2xl border border-line bg-panel px-4 py-3 text-sm leading-relaxed text-mute">
        {t("import.explain")}
      </p>

      {unavailable && <ApiBanner />}

      {sourcesLoading ? (
        <LoadingSkeleton count={3} />
      ) : (
        <>
          {/* Три картки джерел */}
          <div className="mb-5 grid gap-3 sm:grid-cols-3">
            {sources.map((s) => {
              const active = selected === s.id;
              return (
                <button
                  key={s.id}
                  type="button"
                  aria-pressed={active}
                  onClick={() => setSelected(s.id)}
                  className={`rounded-2xl border p-4 text-left transition-all ${
                    active
                      ? "border-accent bg-accent/10 shadow-lg shadow-accent/10"
                      : "border-line bg-panel hover:border-accent/50"
                  }`}
                >
                  <div className="flex items-center justify-between gap-2">
                    <span className="text-base font-bold text-ink">{s.name}</span>
                    {active && <span className="text-accent-2" aria-hidden>✓</span>}
                  </div>
                  <p className="mt-1 text-xs text-mute">{hintFor(s.id)}</p>
                </button>
              );
            })}
          </div>

          {/* Форма імпорту */}
          <div className="mb-6 rounded-2xl border border-line bg-panel p-4">
            <div className="flex flex-col gap-3 sm:flex-row sm:items-end">
              <label className="block min-w-0 flex-1">
                <span className="mb-1 block text-xs text-mute">
                  {t("import.source")}: {selectedSource?.name} · {t("import.username")}
                </span>
                <input
                  type="text"
                  value={username}
                  onChange={(e) => setUsername(e.target.value)}
                  onKeyDown={(e) => e.key === "Enter" && start()}
                  placeholder={hintFor(selected) || t("import.usernamePlaceholder")}
                  disabled={busy}
                  className="w-full rounded-xl border border-line bg-bg px-4 py-2.5 text-sm text-ink placeholder:text-mute/60 focus:border-accent focus:outline-none focus:ring-2 focus:ring-accent/30 disabled:opacity-50"
                />
              </label>
              <button
                type="button"
                onClick={start}
                disabled={busy || !username.trim() || unavailable}
                className="shrink-0 rounded-xl bg-accent px-5 py-2.5 text-sm font-bold text-white transition-all enabled:hover:bg-accent-2 enabled:active:scale-95 disabled:opacity-50"
              >
                {t("import.start")}
              </button>
            </div>
            {startError && (
              <p className="mt-3 text-sm text-amber-300">{startError}</p>
            )}
          </div>

          {/* Прогрес / підсумок */}
          {job && (
            <div
              className={`rounded-2xl border p-4 ${
                failed
                  ? "border-red-500/40 bg-red-500/10"
                  : done
                    ? "border-emerald-500/40 bg-emerald-500/10"
                    : "border-line bg-panel"
              }`}
            >
              <div className="flex items-center justify-between gap-2">
                <h2 className="text-base font-bold">
                  {done ? t("import.summary") : t("import.progress")}
                </h2>
                <span
                  className={`rounded-full px-3 py-1 text-xs font-semibold ${
                    failed
                      ? "bg-red-500/20 text-red-300"
                      : done
                        ? "bg-emerald-500/20 text-emerald-300"
                        : "bg-accent/15 text-accent-2"
                  }`}
                >
                  {t(statusKey(job.status))}
                </span>
              </div>

              {(job.status === "queued" || job.status === "running") && (
                <div className="mt-3 h-2 w-full overflow-hidden rounded-full bg-panel-2">
                  <div className="h-full w-1/3 animate-pulse rounded-full bg-accent" />
                </div>
              )}

              {pollError && !done && !failed && (
                <p className="mt-3 text-sm text-amber-300">{t("api.unavailable")}</p>
              )}

              {stats && (done || job.status === "running" || job.status === "queued") && (
                <div className="mt-3 grid grid-cols-2 gap-2 sm:grid-cols-4">
                  {found != null && (
                    <div className="rounded-xl border border-line bg-panel-2 px-3 py-2">
                      <div className="text-lg font-bold text-ink">{found}</div>
                      <div className="text-xs text-mute">{t("import.stats.found")}</div>
                    </div>
                  )}
                  {stats.created != null && (
                    <div className="rounded-xl border border-line bg-panel-2 px-3 py-2">
                      <div className="text-lg font-bold text-ink">{stats.created}</div>
                      <div className="text-xs text-mute">{t("import.stats.created")}</div>
                    </div>
                  )}
                  {stats.animeWatched != null && (
                    <div className="rounded-xl border border-line bg-panel-2 px-3 py-2">
                      <div className="text-lg font-bold text-ink">{stats.animeWatched}</div>
                      <div className="text-xs text-mute">{t("import.stats.watched")}</div>
                    </div>
                  )}
                  {stats.mangaRead != null && (
                    <div className="rounded-xl border border-line bg-panel-2 px-3 py-2">
                      <div className="text-lg font-bold text-ink">{stats.mangaRead}</div>
                      <div className="text-xs text-mute">{t("import.stats.read")}</div>
                    </div>
                  )}
                </div>
              )}

              {failed && (
                <p className="mt-3 text-sm text-red-300">
                  {failureMessage(job.error, t)}
                </p>
              )}

              {done && (
                <div className="mt-3">
                  <Link
                    href="/lists"
                    className="inline-block rounded-xl bg-emerald-500/20 px-4 py-2 text-sm font-bold text-emerald-300 transition-colors hover:bg-emerald-500/30"
                  >
                    {t("import.goToLists")} →
                  </Link>
                </div>
              )}
            </div>
          )}
        </>
      )}
    </div>
  );
}
