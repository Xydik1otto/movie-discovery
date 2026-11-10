"use client";

import { useEffect, useState } from "react";
import { useParams, useRouter } from "next/navigation";
import { guardedTitle, guardedTitleSimilar } from "@/lib/fallback";
import { addListItem } from "@/lib/listActions";
import { getWatch, getTitleSeries } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { formatLabel, posterUrlFull } from "@/lib/constants";
import {
  dubGroupLabelKey,
  groupWatchByDub,
  hasDubMetadata,
  qualityLabel,
} from "@/lib/watch";
import type { ListKind, SeriesResponse, SimilarItem, TitleDetail, WatchProvider } from "@/lib/types";
import { useI18n } from "@/i18n/I18nContext";
import TitleCard from "@/components/TitleCard";
import ApiBanner from "@/components/ApiBanner";
import EmptyState from "@/components/EmptyState";
import LoadingSkeleton from "@/components/LoadingSkeleton";
import RatingBadge from "@/components/RatingBadge";
import ProviderBadge from "@/components/ProviderBadge";
import SeriesBlock from "@/components/SeriesBlock";
import WatchPanel from "@/components/WatchPanel";
import TrailerSection from "@/components/TrailerSection";
import PosterImage from "@/components/PosterImage";

export default function TitleDetailPage() {
  const { t, lang } = useI18n();
  const { user } = useAuth();
  const router = useRouter();
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
  // Повна форма провайдерів з GET /api/titles/{id}/watch (FR-14).
  // null = ендпоінт недоступний → fallback на компактні detail.providers.
  const [watchList, setWatchList] = useState<WatchProvider[] | null>(null);
  // Рішення власника (09.11): платні варіанти приховані за замовчуванням;
  // перемикач «Показати платні» розкриває їх (стан лише клієнтський).
  const [showPaid, setShowPaid] = useState(false);

  // FR: франшиза/сага з GET /api/titles/{id}/series — підтягуємо ПІСЛЯ основного
  // detail (не блокує рендер); помилка/відсутність серії — блок просто не з'явиться.
  const [series, setSeries] = useState<SeriesResponse | null>(null);

  const [added, setAdded] = useState<ListKind | null>(null);
  const [busyKind, setBusyKind] = useState<ListKind | null>(null);
  const [actionError, setActionError] = useState(false);
  const [watchedRating, setWatchedRating] = useState("");
  // UX-03: битий backdrop (URL truthy, але onError) → hero-блок ПРИБИРАЄМО
  // повністю (компактний лейаут), а не лишаємо порожню зону 320px. Хуки —
  // ДО early-returns (rules-of-hooks); скидання — на зміну URL бекдропа.
  const [backdropFailed, setBackdropFailed] = useState(false);
  useEffect(() => {
    setBackdropFailed(false);
  }, [detail?.backdropUrl]);

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

  // Повні провайдери перегляду (FR-14): /watch повертає рядки з region/isAvailable —
  // фільтруємо за вибраним регіоном на клієнті ("*" = усі регіони).
  useEffect(() => {
    setWatchList(null);
    setShowPaid(false); // новий тайтл — знову безкоштовний вигляд за замовчуванням
    if (!detail || unavailable || !Number.isFinite(id)) return;
    let alive = true;
    getWatch(id)
      .then((r) => {
        if (alive) {
          // UX-04 tri-state: ховаємо лише ПІДТВЕРДЖЕНО недоступні (unavailable);
          // unknown (неперевірений каталог/не заявлено) лишається з чесним станом.
          setWatchList((r.providers ?? []).filter((p) => p.availabilityStatus !== "unavailable"));
        }
      })
      .catch(() => {
        // ендпоінт недоступний/немає даних → лишається fallback з detail.providers
      });
    return () => {
      alive = false;
    };
  }, [id, detail, unavailable]);

  useEffect(() => {
    if (detail) document.title = `${detail.title} · КіноНавігатор`;
  }, [detail]);

  // FR: серія тайтла — тільки коли основний detail завантажився (неблокуюче).
  // Локаль: lang у запиті — назва серії локалізується на бекенді (uk → ru → en).
  useEffect(() => {
    setSeries(null);
    if (!detail || unavailable || !Number.isFinite(id)) return;
    let alive = true;
    getTitleSeries(id, lang)
      .then((r) => {
        if (alive) setSeries(r);
      })
      .catch(() => {
        // ендпоінт недоступний/тайтл поза серією → блок не рендеримо
      });
    return () => {
      alive = false;
    };
  }, [id, lang, detail, unavailable]);

  async function add(kind: ListKind) {
    if (!detail) return;
    setBusyKind(kind);
    setActionError(false);
    try {
      const rating =
        kind !== "watchlist" && watchedRating ? Number(watchedRating) : undefined;
      // MD-04: єдиний хелпер — залогінений → API, гість → localStorage.
      // Внутрішній id летить завжди; tmdbId — коли є (аніме/манга без нього ок).
      await addListItem(
        !!user,
        kind,
        { id: detail.id, tmdbId: detail.tmdbId ?? null },
        rating ?? null,
        {
          title: detail.title,
          type: detail.type,
          posterUrl: detail.posterUrl ?? null,
          year: detail.year ?? null,
        },
      );
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

  // FR-14: спершу повні провайдери з /watch (уніфіковані: watch_links + providers,
  // kind free|stream|rent|buy|ads), інакше компактні з detail (легасі/демо); регіон
  // фільтруємо на клієнті. Рішення власника (09.11): ЗА ЗАМОВЧУВАННЯМ лише
  // безкоштовні варіанти (ads — теж безкоштовний перегляд), платні (підписка/
  // оренда/купівля) — за перемикачем «Показати платні».
  const watchPool: WatchProvider[] = watchList ?? detail.providers ?? [];
  const regionFiltered = watchPool.filter(
    (p) => !p.region || p.region === "*" || p.region === region,
  );
  const isFreeish = (p: WatchProvider) => p.kind === "free" || p.kind === "ads";
  const hasPaid = regionFiltered.some((p) => !isFreeish(p));
  const providers = showPaid ? regionFiltered : regionFiltered.filter(isFreeish);
  const isAnime = detail.type === "anime";
  const isManga = detail.type === "manga";
  // FR: бейдж «У серії» на картках «Схожих» — лише коли група осмислена (≥2 тайтли).
  const showSeriesBadges = !!series?.seriesKey && series.items.length >= 2;
  const seriesIds = new Set(series?.items.map((i) => i.id) ?? []);
  const doneKind: ListKind = isManga ? "read" : "watched";
  const poster = posterUrlFull(detail.posterUrl);
  const backdrop = detail.backdropUrl
    ? posterUrlFull(detail.backdropUrl, "w1280")
    : null;
  const showHero = !!backdrop && !backdropFailed;
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

  // UX-03: постер спільний для обох лейаутів (ширина підсталяється).
  // PosterImage: metadata-missing → чесна заглушка з назвою; onError → 1 ретрай →
  // «Постер недоступний» (UX-02); слот завжди aspect-[2/3], зона не роздувається.
  const posterBlock = (widthCls: string) => (
    <div className={`${widthCls} shrink-0 overflow-hidden rounded-xl border border-line shadow-xl`}>
      <PosterImage src={poster} title={detail.title} className="w-full" eager />
    </div>
  );

  // UX-03: заголовок+рейтинги+бейджі — спільний блок (pad — лише над hero).
  const headingBlock = (pad: boolean) => (
    <div className={`min-w-0 flex-1 ${pad ? "pb-1" : ""}`}>
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
  );

  return (
    <div>
      {unavailable && <ApiBanner />}
      {error && (
        <div className="mb-4 text-sm text-amber-300">{t("common.error")}</div>
      )}

      {/* Заставка (UX-03): високий hero + негативні відступи ЛИШЕ коли backdrop
          є І завантажився; без нього або при onError — компактний layout впритул,
          без порожнього блоку 320px. */}
      {showHero ? (
        <>
          <div className="relative -mx-4 mb-4 h-56 overflow-hidden sm:-mx-6 sm:h-72 lg:h-80">
            <img
              src={backdrop}
              alt=""
              aria-hidden
              onError={() => setBackdropFailed(true)}
              className="h-full w-full object-cover"
            />
            <div className="absolute inset-0 bg-gradient-to-t from-bg via-bg/40 to-transparent" />
          </div>
          <div className="-mt-28 sm:-mt-36">
            <div className="flex flex-col gap-5 sm:flex-row sm:items-end">
              {posterBlock("w-36 sm:w-48")}
              {headingBlock(true)}
            </div>
          </div>
        </>
      ) : (
        /* Компактний header: постер + заголовок у один ряд (на 390px — теж
           горизонтально: постер w-28 вміщується поруч, без гор. скролу). */
        <div className="flex items-start gap-4 sm:gap-5">
          {posterBlock("w-28 sm:w-40 lg:w-48")}
          {headingBlock(false)}
        </div>
      )}

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

        {/* FR-21/MD-03: «Схожі» → /similar?seed={id} (внутрішній id, авто-прогін).
            Показуємо для ВСІХ тайтлів — gate по tmdbId ховав кнопку в аніме/манги. */}
        <button
          type="button"
          onClick={() => router.push(`/similar?seed=${detail.id}`)}
          className="rounded-xl border border-accent/50 bg-accent/10 px-4 py-2.5 text-sm font-bold text-accent-2 transition-colors hover:bg-accent/20"
        >
          ✦ {t("similar.cardBtn")}
        </button>

        <div className="flex items-center gap-2">
          <button
            type="button"
            disabled={busyKind != null || added != null}
            onClick={() => add(doneKind)}
            title={
              added != null && added !== doneKind ? t("title.removeFirst") : undefined
            }
            className={`rounded-xl border px-4 py-2.5 text-sm font-bold transition-all enabled:active:scale-95 disabled:opacity-60 ${
              added === doneKind
                ? "border-emerald-500/40 bg-emerald-500/15 text-emerald-300"
                : "border-line bg-panel text-ink enabled:hover:border-accent"
            }`}
          >
            {/* Чесні стани: «✓» — лише коли дію ВИКОНАНО (added === doneKind).
                Оманлива ✓-мітка на disabled кнопці (дефект хвилі-1) знята; коли
                тайтл щойно додано в інший список — тултіп із причиною. */}
            {added === doneKind
              ? `✓ ${t("title.added")}`
              : isManga
                ? t("title.readAdd")
                : t("title.watchedAdd")}
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

          {/* Трейлер (UX-05): окрема кнопка «Переглянути трейлер» + вибір мови
              чипами; iframe ЛИШЕ після кліку (без autoplay); чесні стани
              «українською не знайдено» / gone → безпечне зовнішнє посилання. */}
          <TrailerSection titleId={detail.id} initialTrailers={detail.trailers} />
        </div>

        {/* Де подивитись (UX-04) — WatchPanel: групи мов ПЕРЕГЛЯДУ (uk/ru/оригінал/
            невідома) незалежно від мови UI, tri-state доступності (unknown ≠
            «Безкоштовно»), direct vs «Знайти на сервісу», ціни чесні.
            Рішення власника (09.11): за замовчуванням лише безкоштовні,
            перемикач розкриває платні (підписка/оренда/купівля). */}
        {(regionFiltered.length > 0 || !!detail.keywords?.length) && (
          <aside className="space-y-5">
            {regionFiltered.length > 0 && <WatchPanel providers={regionFiltered} region={region} />}

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
        )}
      </div>

      {/* FR: фільми з тієї ж лінійки — порядок перегляду саги (сховається сам,
          коли тайтл поза серією або група < 2 тайтлів). */}
      <SeriesBlock series={series} />

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
                footer={
                  showSeriesBadges && seriesIds.has(item.id) ? (
                    <span className="mt-1 inline-flex items-center gap-1 px-0.5 text-[11px] font-bold text-accent-2">
                      🔖 {t("series.badge")}
                    </span>
                  ) : undefined
                }
              />
            ))}
          </div>
        )}
      </section>
    </div>
  );
}
