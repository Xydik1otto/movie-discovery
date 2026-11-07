"use client";

import { useEffect, useState } from "react";
import { useParams } from "next/navigation";
import { addToList, guardedTitle, guardedTitleSimilar } from "@/lib/fallback";
import { getProviders } from "@/lib/api";
import { formatLabel, posterUrlFull } from "@/lib/constants";
import type { ListKind, ProviderRef, SimilarItem, TitleDetail } from "@/lib/types";
import { useI18n } from "@/i18n/I18nContext";
import TitleCard from "@/components/TitleCard";
import ApiBanner from "@/components/ApiBanner";
import EmptyState from "@/components/EmptyState";
import LoadingSkeleton from "@/components/LoadingSkeleton";
import RatingBadge from "@/components/RatingBadge";
import ProviderBadge from "@/components/ProviderBadge";

export default function TitleDetailPage() {
  const { t, lang } = useI18n();
  const routeParams = useParams<{ id: string }>();
  const id = Number(routeParams?.id);

  const [detail, setDetail] = useState<TitleDetail | null>(null);
  const [unavailable, setUnavailable] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(false);

  const [similar, setSimilar] = useState<SimilarItem[]>([]);
  const [similarLoading, setSimilarLoading] = useState(true);
  const [similarUnavailable, setSimilarUnavailable] = useState(false);

  const [region, setRegion] = useState("UA");
  const [regionProviders, setRegionProviders] = useState<ProviderRef[] | null>(null);

  const [added, setAdded] = useState<ListKind | null>(null);
  const [busyKind, setBusyKind] = useState<ListKind | null>(null);
  const [actionError, setActionError] = useState(false);
  const [watchedRating, setWatchedRating] = useState("");

  useEffect(() => {
    try {
      const r = window.localStorage.getItem("region");
      if (r) setRegion(r);
    } catch {
      // default UA
    }
  }, []);

  useEffect(() => {
    if (!Number.isFinite(id)) return;
    let alive = true;
    setLoading(true);
    setError(false);
    setDetail(null);
    guardedTitle(id, lang)
      .then((r) => {
        if (!alive) return;
        setDetail(r.data);
        setUnavailable(r.unavailable);
      })
      .catch(() => {
        if (alive) setError(true);
      })
      .finally(() => {
        if (alive) setLoading(false);
      });
    return () => {
      alive = false;
    };
  }, [id, lang]);

  useEffect(() => {
    if (!Number.isFinite(id)) return;
    let alive = true;
    setSimilarLoading(true);
    guardedTitleSimilar(id, lang)
      .then((r) => {
        if (!alive) return;
        setSimilar(r.data);
        setSimilarUnavailable(r.unavailable);
      })
      .catch(() => {
        if (alive) setSimilar([]);
      })
      .finally(() => {
        if (alive) setSimilarLoading(false);
      });
    return () => {
      alive = false;
    };
  }, [id, lang]);

  // Регіон ≠ UA → перезапит провайдерів окремим ендпоінтом.
  useEffect(() => {
    if (!detail || unavailable || region === "UA") {
      setRegionProviders(null);
      return;
    }
    let alive = true;
    getProviders(id, region)
      .then((r) => alive && setRegionProviders(r.providers ?? []))
      .catch(() => alive && setRegionProviders(null));
    return () => {
      alive = false;
    };
  }, [detail, unavailable, region, id]);

  useEffect(() => {
    if (detail) document.title = `${detail.title} · КіноНавігатор`;
  }, [detail]);

  async function add(kind: ListKind) {
    if (!detail) return;
    setBusyKind(kind);
    setActionError(false);
    try {
      const rating =
        kind !== "watchlist" && watchedRating ? Number(watchedRating) : undefined;
      // Для аніме/манги з імпорту tmdbId може бути null — тоді addToList
      // сам надсилає внутрішній id тайтла як titleId.
      await addToList(detail, kind, rating ?? null);
      setAdded(kind);
    } catch (e) {
      void e;
      setActionError(true);
    } finally {
      setBusyKind(null);
    }
  }

  if (!Number.isFinite(id)) {
    return <EmptyState icon="🤷" title={t("common.error")} />;
  }

  if (loading) {
    return (
      <div>
        <LoadingSkeleton count={6} />
      </div>
    );
  }

  if (error && !(unavailable && detail)) {
    return <EmptyState icon="⚠️" title={t("common.error")} />;
  }

  if (!detail) {
    return (
      <div>
        {unavailable && <ApiBanner />}
        <EmptyState icon="🔍" title={t("search.noResults")} />
      </div>
    );
  }

  const providers = regionProviders ?? detail.providers ?? [];
  const isAnime = detail.type === "anime";
  const isManga = detail.type === "manga";
  const doneKind: ListKind = isManga ? "read" : "watched";
  const poster = posterUrlFull(detail.posterUrl);
  const backdrop = detail.backdropUrl
    ? posterUrlFull(detail.backdropUrl, "w1280")
    : null;
  const statusKey =
    detail.status === "released"
      ? "title.status.released"
      : detail.status === "returning"
        ? "title.status.returning"
        : detail.status === "ended"
          ? "title.status.ended"
          : detail.status === "canceled"
            ? "title.status.canceled"
            : detail.status === "airing"
              ? "anime.airing"
              : detail.status === "finished"
                ? "anime.finished"
                : null;

  const metaRow = (label: string, value?: React.ReactNode) =>
    value ? (
      <div className="flex gap-2 text-sm">
        <span className="shrink-0 text-mute">{label}:</span>
        <span className="min-w-0 text-ink">{value}</span>
      </div>
    ) : null;

  return (
    <div>
      {unavailable && <ApiBanner />}
      {error && (
        <div className="mb-4 text-sm text-amber-300">{t("common.error")}</div>
      )}

      {/* Заставка */}
      <div className="relative -mx-4 mb-4 h-56 overflow-hidden sm:-mx-6 sm:h-72 lg:h-80">
        {backdrop ? (
          <img
            src={backdrop}
            alt=""
            aria-hidden
            className="h-full w-full object-cover"
          />
        ) : (
          <div className="h-full w-full bg-gradient-to-br from-panel-2 via-[#232a3d] to-[#3a1a2b]" />
        )}
        <div className="absolute inset-0 bg-gradient-to-t from-bg via-bg/40 to-transparent" />
      </div>

      <div className="-mt-28 sm:-mt-36">
        <div className="flex flex-col gap-5 sm:flex-row sm:items-end">
          {/* Постер */}
          <div className="w-36 shrink-0 overflow-hidden rounded-xl border border-line shadow-xl sm:w-48">
            <div className="aspect-[2/3] bg-panel-2">
              {poster ? (
                <img
                  src={poster}
                  alt={detail.title}
                  className="h-full w-full object-cover"
                />
              ) : (
                <div className="flex h-full w-full flex-col items-center justify-center gap-2 bg-gradient-to-br from-panel-2 via-[#232a3d] to-[#3a1a2b] p-3 text-center">
                  <span className="text-3xl opacity-60" aria-hidden>
                    🎬
                  </span>
                  <span className="line-clamp-3 text-xs font-semibold text-ink/80">
                    {detail.title}
                  </span>
                </div>
              )}
            </div>
          </div>

          {/* Заголовок + рейтинги */}
          <div className="min-w-0 flex-1 pb-1">
            <h1 className="text-2xl font-extrabold tracking-tight sm:text-4xl">
              {detail.title}
            </h1>
            {detail.originalTitle && detail.originalTitle !== detail.title && (
              <p className="mt-1 text-sm text-mute">
                {t("title.originalTitle")}: {detail.originalTitle}
              </p>
            )}
            <div className="mt-3 flex flex-wrap items-center gap-2">
              <RatingBadge source="TMDB" value={detail.ratingTmdb} size="lg" />
              <RatingBadge source="IMDb" value={detail.ratingImdb} size="lg" />
              <RatingBadge
                source="MAL"
                value={detail.scoreMal}
                size="lg"
                title={t("badges.mal")}
              />
              <RatingBadge
                source="AL"
                value={detail.scoreAnilist}
                size="lg"
                title={t("badges.al")}
              />
              <RatingBadge
                source="Shiki"
                value={detail.scoreShikimori}
                size="lg"
                title={t("badges.shiki")}
              />
              <span className="rounded-md border border-line bg-panel px-2.5 py-1 text-sm text-mute">
                {detail.year ?? "—"}
              </span>
              <span className="rounded-md border border-line bg-panel px-2.5 py-1 text-sm text-mute">
                {detail.type === "movie"
                  ? t("filters.movie")
                  : detail.type === "series"
                    ? t("filters.series")
                    : detail.type === "anime"
                      ? t("filters.anime")
                      : detail.type === "manga"
                        ? t("filters.manga")
                        : t("filters.show")}
              </span>
            </div>
          </div>
        </div>
      </div>

      {/* Кнопки списків */}
      <div className="mt-5 flex flex-wrap items-center gap-2">
        <button
          type="button"
          disabled={busyKind != null || added != null}
          onClick={() => add("watchlist")}
          className={`rounded-xl px-4 py-2.5 text-sm font-bold transition-all enabled:active:scale-95 disabled:opacity-60 ${
            added === "watchlist"
              ? "bg-emerald-500/20 text-emerald-300"
              : "bg-accent text-white enabled:hover:bg-accent-2"
          }`}
        >
          {added === "watchlist" ? `✓ ${t("title.added")}` : `＋ ${t("title.watchlist")}`}
        </button>

        <div className="flex items-center gap-2">
          <button
            type="button"
            disabled={busyKind != null || added != null}
            onClick={() => add(doneKind)}
            className={`rounded-xl border px-4 py-2.5 text-sm font-bold transition-all enabled:active:scale-95 disabled:opacity-60 ${
              added === doneKind
                ? "border-emerald-500/40 bg-emerald-500/15 text-emerald-300"
                : "border-line bg-panel text-ink enabled:hover:border-accent"
            }`}
          >
            {added === doneKind
              ? `✓ ${t("title.added")}`
              : `✓ ${isManga ? t("title.readAdd") : t("title.watchedAdd")}`}
          </button>
          <select
            value={watchedRating}
            onChange={(e) => setWatchedRating(e.target.value)}
            aria-label={t("lists.rating")}
            className="rounded-xl border border-line bg-panel px-2 py-2.5 text-sm text-mute focus:border-accent focus:outline-none"
          >
            <option value="">{t("lists.rating")}</option>
            {Array.from({ length: 10 }, (_, i) => (
              <option key={i + 1} value={i + 1}>
                {i + 1}
              </option>
            ))}
          </select>
        </div>

        {actionError && (
          <span className="text-sm text-amber-300">{t("common.error")}</span>
        )}
        {busyKind && <span className="text-sm text-mute">{t("common.loading")}</span>}
      </div>

      {/* Основне */}
      <div className="mt-6 grid gap-6 lg:grid-cols-[1fr_360px]">
        <div className="min-w-0 space-y-5">
          <section>
            <p className="whitespace-pre-line text-sm leading-relaxed text-ink/90">
              {detail.overviewFull || detail.overview || "—"}
            </p>
          </section>

          {!!detail.genres?.length && (
            <div className="flex flex-wrap gap-1.5">
              {detail.genres.map((g) => (
                <span
                  key={g}
                  className="rounded-full border border-line bg-panel px-3 py-1 text-xs text-ink/80"
                >
                  {g}
                </span>
              ))}
            </div>
          )}

          <section className="grid gap-2 rounded-2xl border border-line bg-panel p-4 sm:grid-cols-2">
            {metaRow(t("title.director"), detail.director)}
            {metaRow(
              t("title.actors"),
              detail.actors?.length ? detail.actors.join(", ") : null,
            )}
            {metaRow(
              t("title.countries"),
              detail.countries?.length ? detail.countries.join(", ") : null,
            )}
            {metaRow(
              detail.runtimeMin != null && detail.runtimeMin > 0
                ? t("title.runtime")
                : t("title.seasons"),
              detail.runtimeMin != null && detail.runtimeMin > 0
                ? t("title.runtimeMin", { n: detail.runtimeMin })
                : detail.seasons != null
                  ? String(detail.seasons)
                  : null,
            )}
            {isAnime && metaRow(t("anime.format"), formatLabel(detail.format, lang))}
            {isAnime && metaRow(t("anime.episodes"), detail.episodes ?? null)}
            {isAnime &&
              metaRow(
                t("anime.studios"),
                detail.studios?.length ? detail.studios.join(", ") : null,
              )}
            {isManga && metaRow(t("manga.volumes"), detail.volumes ?? null)}
            {isManga && metaRow(t("manga.chapters"), detail.chapters ?? null)}
            {isManga &&
              metaRow(
                t("manga.authors"),
                detail.authors?.length ? detail.authors.join(", ") : null,
              )}
            {metaRow(t("title.status"), statusKey ? t(statusKey) : null)}
            {metaRow(t("title.year"), detail.year ?? null)}
          </section>

          {/* Трейлер */}
          {detail.trailerYtId ? (
            <section>
              <h2 className="mb-3 text-lg font-bold">{t("title.trailer")}</h2>
              <div className="aspect-video overflow-hidden rounded-2xl border border-line">
                <iframe
                  src={`https://www.youtube.com/embed/${detail.trailerYtId}`}
                  title={t("title.trailer")}
                  allow="accelerometer; autoplay; clipboard-write; encrypted-media; gyroscope; picture-in-picture"
                  allowFullScreen
                  loading="lazy"
                  className="h-full w-full"
                />
              </div>
            </section>
          ) : (
            <p className="text-sm text-mute">{t("title.noTrailer")}</p>
          )}
        </div>

        {/* Де подивитись */}
        <aside className="space-y-5">
          <section className="rounded-2xl border border-line bg-panel p-4">
            <h2 className="mb-3 text-base font-bold">{t("title.providers")}</h2>
            {providers.length === 0 ? (
              <p className="text-sm text-mute">{t("providers.none")}</p>
            ) : (
              <div className="flex flex-wrap gap-2">
                {providers.map((p, i) => (
                  <ProviderBadge key={`${p.provider}-${p.kind}-${i}`} provider={p} />
                ))}
              </div>
            )}
          </section>

          {!!detail.keywords?.length && (
            <section className="rounded-2xl border border-line bg-panel p-4">
              <h2 className="mb-2 text-base font-bold">{t("title.keywords")}</h2>
              <div className="flex flex-wrap gap-1.5">
                {detail.keywords.map((k) => (
                  <span
                    key={k}
                    className="rounded-md bg-panel-2 px-2 py-1 text-xs text-mute"
                  >
                    {k}
                  </span>
                ))}
              </div>
            </section>
          )}
        </aside>
      </div>

      {/* Схожі */}
      <section className="mt-8">
        <h2 className="mb-3 text-lg font-bold">{t("title.similar")}</h2>
        {similarUnavailable && <ApiBanner />}
        {similarLoading ? (
          <LoadingSkeleton count={5} />
        ) : similar.length === 0 ? (
          <EmptyState title={t("search.noResults")} />
        ) : (
          <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5">
            {similar.map((item) => (
              <TitleCard
                key={item.id}
                item={item}
                score={item.score}
                reasonsLabel={t("title.why")}
              />
            ))}
          </div>
        )}
      </section>
    </div>
  );
}
