"use client";

import { Suspense, useEffect, useRef, useState } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { useI18n } from "@/i18n/I18nContext";
import { getTitle } from "@/lib/api";
import { guardedBySeedsEx, guardedByText } from "@/lib/fallback";
import type {
  SimilarItem,
  SimilarSeedsFilters,
  TitleCard as TitleCardType,
} from "@/lib/types";
import SeedsBuilder from "@/components/SeedsBuilder";
import QuickSimilar from "@/components/QuickSimilar";
import StudioPanel from "@/components/StudioPanel";
import CollectionsPanel from "@/components/CollectionsPanel";
import TitleCard from "@/components/TitleCard";
import ApiBanner from "@/components/ApiBanner";
import EmptyState from "@/components/EmptyState";
import LoadingSkeleton from "@/components/LoadingSkeleton";
import { usePosterBackfill } from "@/lib/usePosterBackfill";

interface HistoryEntry {
  label: string;
  items: SimilarItem[];
  unavailable: boolean;
}

// Стабільне порожнє значення для usePosterBackfill: літерал [] у місці виклику
// створював би новий масив на кожен рендер (ре-прогін ефекту хука).
const NO_ITEMS: SimilarItem[] = [];

// Знімок для ланцюга (FR-21.5): «назад» повертає попередні еталони й
// результати без повторного запиту — simple stack у стані сторінки.
interface ChainEntry {
  mode: Mode;
  label: string;
  items: SimilarItem[];
  unavailable: boolean;
  picked: TitleCardType | null;
  seeds: TitleCardType[];
  seedFilters: SimilarSeedsFilters;
  studioSeeds: TitleCardType[];
  studioFilters: SimilarSeedsFilters;
}

type Mode = "quick" | "seeds" | "studio" | "collections";

const STUDIO_DEBOUNCE_MS = 500;
const CHAIN_LIMIT = 10;

// --- Режим «Еталони»: URL-стан (?types=&genres=&mood=&yearFrom=&yearTo=&seedIds=) ---
// Перезавантаження/шерінг відновлюють добірку: фільтри — з query одразу,
// еталони — резолв по внутрішніх id через singular /api/title/{id}.
const SEED_PARAM_KEYS = ["types", "genres", "mood", "yearFrom", "yearTo", "seedIds"] as const;
const MAX_URL_SEEDS = 10;
// Валідні значення hard-фільтра типів бекенду (movie|series|show|anime|manga) —
// сміття з поширених посилань тихо відкидаємо, щоб не ловити 400.
const SEED_TYPE_VALUES = new Set(["movie", "series", "show", "anime", "manga"]);
// Ключі пресетів настрою (бекенд: MoodPresets).
const MOOD_KEYS = new Set([
  "cozy",
  "tense",
  "funny",
  "sad",
  "epic",
  "romantic",
  "family",
  "mindbender",
]);

function seedFiltersFromParams(p: URLSearchParams): SimilarSeedsFilters {
  const csv = (k: string) =>
    (p.get(k) ?? "")
      .split(",")
      .map((s) => s.trim())
      .filter(Boolean);
  const num = (k: string) => {
    const raw = p.get(k);
    if (!raw) return null;
    const n = Number(raw);
    return Number.isFinite(n) ? n : null;
  };
  const moodRaw = p.get("mood");
  return {
    types: csv("types").filter((v) => SEED_TYPE_VALUES.has(v)),
    genres: csv("genres"),
    mood: moodRaw && MOOD_KEYS.has(moodRaw) ? moodRaw : null,
    yearFrom: num("yearFrom"),
    yearTo: num("yearTo"),
  };
}

function seedIdsFromParams(p: URLSearchParams): number[] {
  return (p.get("seedIds") ?? "")
    .split(",")
    .map((s) => Number(s.trim()))
    .filter((n) => Number.isInteger(n) && n > 0)
    .slice(0, MAX_URL_SEEDS);
}

// Канонічний вигляд seeds-параметрів стану (детермінований порядок ключів) —
// порівнюється з поточним URL, щоб не робити зайвих router.replace.
function seedQsFromState(seeds: TitleCardType[], f: SimilarSeedsFilters): string {
  const sp = new URLSearchParams();
  if (f.types?.length) sp.set("types", f.types.join(","));
  if (f.genres?.length) sp.set("genres", f.genres.join(","));
  if (f.mood) sp.set("mood", f.mood);
  if (f.yearFrom != null) sp.set("yearFrom", String(f.yearFrom));
  if (f.yearTo != null) sp.set("yearTo", String(f.yearTo));
  if (seeds.length) sp.set("seedIds", seeds.map((s) => s.id).join(","));
  return sp.toString();
}

function seedQsFromParams(p: URLSearchParams): string {
  const sp = new URLSearchParams();
  for (const k of SEED_PARAM_KEYS) {
    const v = p.get(k);
    if (v) sp.set(k, v);
  }
  return sp.toString();
}

// Чи є взагалі вхід для пошуку: еталони опційні — досить жанрів або настрою.
function hasSeedInput(s: TitleCardType[], f: SimilarSeedsFilters): boolean {
  return s.length >= 1 || (f.genres?.length ?? 0) >= 1 || Boolean(f.mood);
}

// Підпис добірки без еталонів (історія/лейбл результатів).
function seedFiltersLabel(
  f: SimilarSeedsFilters,
  t: (k: string, vars?: Record<string, string | number>) => string,
): string {
  const parts: string[] = [];
  if (f.mood) parts.push(`🎭 ${t(`mood.${f.mood}`)}`);
  if (f.genres?.length) parts.push(f.genres.join(", "));
  if (f.types?.length) parts.push(f.types.join(", "));
  return parts.join(" · ") || "…";
}

// Чотирирежимна сторінка «Схоже на...» (FR-21):
//  — «Швидко» (дефолт): один тайтл з дропдауна → одразу результати;
//  — «Еталони»: старий мультисід-конструктор (1–10 seed'ів) + пошук за текстом;
//  — «Студія»: ті самі референси + рамки (роки/тип/жанри), живе оновлення 500 мс;
//  — «Збірники»: пресети, генератор добірок, свої збірки.
// Кнопка «Схожі» на кожній картці результату = ланцюг: підміняє еталони цим
// тайтлом і шукає знову; ← повертає попередні еталони (stack у стані).
function SimilarInner() {
  const { t, lang } = useI18n();
  const router = useRouter();
  const searchParams = useSearchParams();
  const seedParam = searchParams.get("seed");

  const [mode, setMode] = useState<Mode>("quick");

  const [seeds, setSeeds] = useState<TitleCardType[]>([]);
  const [picked, setPicked] = useState<TitleCardType | null>(null);
  const [busy, setBusy] = useState(false);
  const [items, setItems] = useState<SimilarItem[] | null>(null);
  const [unavailable, setUnavailable] = useState(false);
  const [error, setError] = useState(false);
  const [history, setHistory] = useState<HistoryEntry[]>([]);
  const [currentLabel, setCurrentLabel] = useState("");
  const [chainStack, setChainStack] = useState<ChainEntry[]>([]);

  // Режим «Еталони»: вхідні фільтри (типи/жанри/настрій/роки). Початкове
  // значення — з URL: Suspense-межа сторінки бейлаутить серверний рендер
  // (fallback у HTML), тож перший клієнтський рендер уже бачить реальний
  // query — hydration-місматчу нема (той самий патерн, що на головній).
  const [seedFilters, setSeedFilters] = useState<SimilarSeedsFilters>(() =>
    seedFiltersFromParams(searchParams),
  );

  const [studioSeeds, setStudioSeeds] = useState<TitleCardType[]>([]);
  const [studioFilters, setStudioFilters] = useState<SimilarSeedsFilters>({ genres: [] });

  const [textForm, setTextForm] = useState("");
  const [textBusy, setTextBusy] = useState(false);
  const [textError, setTextError] = useState(false);

  // Захист від гонок: відповіді застарілих запитів (після нового запиту,
  // скидання чіпа чи перемикання режиму) не застосовуються.
  const seqRef = useRef(0);
  // Ключ останнього студійного прогону — щоб не перезапускати пошук при
  // перемиканні табів і коректно пропускати відновлений ланцюгом стан.
  const lastStudioRunRef = useRef("");
  // Відновлення еталонів з ?seedIds= триває → sync-ефект не пише URL
  // (інакше порожній seeds затер би seedIds до завершення фетчів).
  const seedRestoringRef = useRef(false);

  // UX-медіа: батч-підгрузка постерів результатів («Швидко»/«Еталони»/«Студія»).
  // Мердж — прямо в useState-масив items (лише картки без posterUrl;
  // resolved id більше не колектяться, misses не повторюються — циклів нема).
  usePosterBackfill(items ?? NO_ITEMS, (map) => {
    setItems((prev) =>
      prev
        ? prev.map((it) => {
            if (it.posterUrl) return it;
            const hit = map.get(it.id);
            return hit ? { ...it, posterUrl: hit.posterUrl ?? null } : it;
          })
        : prev,
    );
  });

  function pushHistory(label: string, data: SimilarItem[], un: boolean) {
    setHistory((prev) => [{ label, items: data, unavailable: un }, ...prev].slice(0, 8));
  }

  function clearResults() {
    setItems(null);
    setUnavailable(false);
    setError(false);
  }

  async function runBySeedsEx(
    list: TitleCardType[],
    label: string,
    filters: SimilarSeedsFilters = {},
  ) {
    // FR-21: сиди — внутрішні id (є в усіх тайтлів; tmdb_id є лише у частини каталогу).
    const seedIds = list.map((s) => s.id);
    // Еталони опційні: 0 ids валідно, коли є жанри або настрій (бекенд-контракт).
    if (!seedIds.length && !filters.mood && !(filters.genres?.length ?? 0)) {
      setError(true);
      return;
    }
    const seq = ++seqRef.current;
    setBusy(true);
    setError(false);
    setUnavailable(false);
    try {
      const r = await guardedBySeedsEx(seedIds, lang, filters);
      if (seq !== seqRef.current) return;
      setItems(r.data);
      setUnavailable(r.unavailable);
      setCurrentLabel(label);
      pushHistory(label, r.data, r.unavailable);
    } catch {
      if (seq !== seqRef.current) return;
      setError(true);
      setItems(null);
    } finally {
      if (seq === seqRef.current) setBusy(false);
    }
  }

  // Режим «Еталони»: еталони опційні — досить жанрів або настрою; фільтри
  // (типи/жанри/настрій/роки) завжди летять у запит разом з еталонами.
  async function find() {
    if (!hasSeedInput(seeds, seedFilters)) return;
    const label = seeds.length
      ? seeds.map((s) => s.title).join(" + ")
      : seedFiltersLabel(seedFilters, t);
    await runBySeedsEx(seeds, label, seedFilters);
  }

  // Режим «Швидко»: вибір тайтла в дропдауні = одразу пошук за одним сидом.
  function quickPick(x: TitleCardType) {
    setPicked(x);
    void runBySeedsEx([x], x.title);
  }

  function quickClear() {
    seqRef.current += 1; // скидаємо відповідь, що ще летить
    setBusy(false);
    setPicked(null);
    clearResults();
  }

  function switchMode(next: Mode) {
    if (next === mode) return;
    seqRef.current += 1; // відповідь, що летить, не має «догнати» інший режим
    setBusy(false);
    setTextBusy(false);
    setMode(next);
    // Студія без референсів — показуємо її порожній стан, а не чужі результати.
    if (next === "studio" && studioSeeds.length === 0) clearResults();
    // ?seed={tmdbId} — deep-link швидкого режиму: зійшов, прибираємо з URL,
    // інакше перезавантаження щоразу скидає користувача назад у «Швидко».
    if (next !== "quick" && searchParams.get("seed")) {
      const sp = new URLSearchParams(searchParams.toString());
      sp.delete("seed");
      const rest = sp.toString();
      router.replace(rest ? `/similar?${rest}` : "/similar", { scroll: false });
    }
  }

  async function findByText() {
    const text = textForm.trim();
    if (text.length < 3) return;
    const seq = ++seqRef.current;
    setTextBusy(true);
    setTextError(false);
    try {
      const r = await guardedByText(text, lang);
      if (seq !== seqRef.current) return;
      const label = `✍️ ${text.slice(0, 60)}${text.length > 60 ? "…" : ""}`;
      setItems(r.data);
      setUnavailable(r.unavailable);
      setCurrentLabel(label);
      pushHistory(label, r.data, r.unavailable);
    } catch {
      if (seq !== seqRef.current) return;
      setTextError(true);
    } finally {
      if (seq === seqRef.current) setTextBusy(false);
    }
  }

  // ?seed={tmdbId} (FR-21.2): авто-прогін — розгортаємо швидкий режим.
  // Сам резолв чіпа й запуск пошуку робить QuickSimilar (useSearchParams).
  useEffect(() => {
    if (seedParam) setMode("quick");
  }, [seedParam]);

  // Відновлення режиму «Еталони» з URL (?seedIds= — внутрішні id): фільтри вже
  // прийшли з URL в ініціалізаторі useState, еталони резолвимо singular-фетчем
  // /api/title/{id}?lang= по кожному id; невідомі id тихо відкидаються.
  // Один раз після монтування. Якщо користувач встиг щось додати, поки йшли
  // фетчі — його стан перемагає (не перетираємо).
  useEffect(() => {
    const ids = seedIdsFromParams(searchParams);
    if (!ids.length) return;
    seedRestoringRef.current = true;
    let alive = true;
    void Promise.allSettled(ids.map((id) => getTitle(id, lang))).then((results) => {
      if (!alive) return;
      const restored: TitleCardType[] = [];
      const seen = new Set<number>();
      for (const r of results) {
        const card = r.status === "fulfilled" ? r.value : null;
        if (card && !seen.has(card.id)) {
          seen.add(card.id);
          restored.push(card);
        }
      }
      seedRestoringRef.current = false;
      setSeeds((prev) => (prev.length ? prev : restored));
    });
    return () => {
      alive = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // URL-дзеркало режиму «Еталони»: кожна зміна еталонів/фільтрів оновлює query
  // (router.replace без скролу, історію не засмічує). Інші параметри (?seed=)
  // зберігаються; під час відновлення еталонів запис заблоковано.
  const seedQsTarget = seedQsFromState(seeds, seedFilters);
  const seedQsCurrent = seedQsFromParams(searchParams);
  useEffect(() => {
    if (seedRestoringRef.current) return;
    if (seedQsTarget === seedQsCurrent) return;
    const sp = new URLSearchParams(searchParams.toString());
    for (const k of SEED_PARAM_KEYS) sp.delete(k);
    for (const [k, v] of new URLSearchParams(seedQsTarget)) sp.set(k, v);
    const rest = sp.toString();
    router.replace(rest ? `/similar?${rest}` : "/similar", { scroll: false });
  }, [seedQsTarget, seedQsCurrent, searchParams, router]);

  // Студія: живе оновлення з дебаунсом 500 мс, без кнопки (FR-21.3).
  useEffect(() => {
    const ids = studioSeeds.map((s) => s.id); // FR-21: внутрішні id
    if (!ids.length) return;
    const f = studioFilters;
    const key = `${ids.join(",")}|${f.yearFrom ?? ""}|${f.yearTo ?? ""}|${(f.types ?? []).join(",")}|${(f.genres ?? []).join(",")}|${f.mood ?? ""}|${lang}`;
    if (key === lastStudioRunRef.current) return; // стан уже відпрацьований
    const timer = setTimeout(() => {
      lastStudioRunRef.current = key;
      void runBySeedsEx(
        studioSeeds,
        studioSeeds.map((s) => s.title).join(" + "),
        f,
      );
    }, STUDIO_DEBOUNCE_MS);
    return () => clearTimeout(timer);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [studioSeeds, studioFilters, lang]);

  // --- Ланцюг (FR-21.5) ---
  function makeSnapshot(): ChainEntry {
    return {
      mode,
      label: currentLabel,
      items: items ?? [],
      unavailable,
      picked,
      seeds,
      seedFilters,
      studioSeeds,
      studioFilters,
    };
  }

  function chainFrom(item: TitleCardType) {
    // MD-03: ланцюг по ВНУТРІШНЬОМУ id — аніме/манга (tmdbId=null) більше
    // не відсікаються мовчки.
    setChainStack((prev) => [makeSnapshot(), ...prev].slice(0, CHAIN_LIMIT));
    if (mode === "studio") {
      // підміняємо еталони — живий ефект перезапустить пошук (дебаунс 500 мс)
      setStudioSeeds([item]);
      return;
    }
    if (mode === "seeds") {
      // Ланцюг у «Еталонах»: підміняємо еталони, вхідні фільтри (жанри/настрій/
      // роки/типи) зберігаємо — добірка продовжується в тому ж контексті.
      setSeeds([item]);
      void runBySeedsEx([item], item.title, seedFilters);
      return;
    }
    // «Швидко» і «Збірники» → швидкий режим з одним сидом
    if (mode !== "quick") setMode("quick");
    quickPick(item);
  }

  function chainBack() {
    const head = chainStack[0];
    if (!head) return;
    setChainStack(chainStack.slice(1));
    seqRef.current += 1; // відповідь, що летить, не перекриє відновлений стан
    setBusy(false);
    setTextBusy(false);
    setMode(head.mode);
    setPicked(head.picked);
    setSeeds(head.seeds);
    setSeedFilters(head.seedFilters);
    setStudioSeeds(head.studioSeeds);
    setStudioFilters(head.studioFilters);
    setItems(head.items.length ? head.items : null);
    setUnavailable(head.unavailable);
    setError(false);
    setCurrentLabel(head.label);
  }

  const chainBar =
    chainStack.length > 0 ? (
      <button
        type="button"
        onClick={chainBack}
        className="mb-3 inline-flex items-center gap-1.5 rounded-xl border border-line bg-panel px-3 py-1.5 text-xs font-semibold text-mute transition-colors hover:border-accent/60 hover:text-ink"
      >
        <span aria-hidden>←</span>
        {t("similar.chainBack")}
      </button>
    ) : null;

  // Спільний блок результатів (грід карток з ланцюгом) для режимів пошуку.
  function resultsBlock(loading: boolean, emptyTitle: string) {
    return (
      <div className="min-w-0">
        {unavailable && <ApiBanner />}
        {chainBar}
        {error ? (
          <div
            role="alert"
            className="flex items-center gap-2 rounded-xl border border-amber-500/30 bg-amber-500/10 px-4 py-2.5 text-sm text-amber-300"
          >
            <span aria-hidden>⚠️</span>
            <span>{t("similar.similarError")}</span>
          </div>
        ) : loading ? (
          <LoadingSkeleton count={10} />
        ) : items ? (
          items.length === 0 ? (
            <EmptyState title={t("search.noResults")} />
          ) : (
            <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 xl:grid-cols-4">
              {items.map((item) => (
                <TitleCard
                  key={`${item.id}-${item.tmdbId}`}
                  item={item}
                  score={item.score}
                  onSimilar={chainFrom}
                />
              ))}
            </div>
          )
        ) : (
          <EmptyState icon="🔍" title={emptyTitle} />
        )}
      </div>
    );
  }

  const subtitle =
    mode === "quick"
      ? t("similar.quickHint")
      : mode === "seeds"
        ? t("similar.seedsHint")
        : mode === "studio"
          ? t("studio.hint")
          : t("collections.subtitle");

  const emptyTitle =
    mode === "quick"
      ? t("similar.quickHint")
      : mode === "studio"
        ? t("studio.hint")
        : t("similar.seedsHint");

  const tabs: { id: Mode; label: string }[] = [
    { id: "quick", label: t("similar.quickMode") },
    { id: "seeds", label: t("similar.seedsMode") },
    { id: "studio", label: t("similar.studioMode") },
    { id: "collections", label: t("similar.collectionsMode") },
  ];

  const tabCls = (active: boolean) =>
    `rounded-lg px-4 py-2 text-sm font-bold transition-colors ${
      active ? "bg-accent text-white" : "text-mute hover:text-ink"
    }`;

  return (
    <div>
      <h1 className="mb-1 text-2xl font-extrabold tracking-tight sm:text-3xl">
        {t("similar.seedsTitle")}
      </h1>
      <p className="mb-5 max-w-2xl text-sm text-mute">{subtitle}</p>

      {/* Перемикач режимів: Швидко | Еталони | Студія | Збірники (FR-21.6) */}
      <div
        className="mb-5 inline-flex flex-wrap rounded-xl border border-line bg-panel p-1"
        role="tablist"
      >
        {tabs.map((tab) => (
          <button
            key={tab.id}
            type="button"
            role="tab"
            aria-selected={mode === tab.id}
            onClick={() => switchMode(tab.id)}
            className={tabCls(mode === tab.id)}
          >
            {tab.label}
          </button>
        ))}
      </div>

      {mode === "quick" && (
        <div className="space-y-5">
          <QuickSimilar picked={picked} onPick={quickPick} onClear={quickClear} />
          {resultsBlock(busy, emptyTitle)}
        </div>
      )}

      {mode === "seeds" && (
        <div className="grid gap-5 lg:grid-cols-[420px_1fr]">
          <div className="space-y-4">
            <SeedsBuilder
              seeds={seeds}
              filters={seedFilters}
              onAdd={(x) => setSeeds((prev) => [...prev, x])}
              onRemove={(id) => setSeeds((prev) => prev.filter((s) => s.id !== id))}
              onClear={() => setSeeds([])}
              onFind={find}
              onFilters={setSeedFilters}
              busy={busy}
            />

            {/* Пошук за вільним текстом */}
            <div className="rounded-2xl border border-line bg-panel p-4 sm:p-5">
              <h2 className="mb-1 text-base font-bold text-ink">{t("similar.byText")}</h2>
              <textarea
                value={textForm}
                onChange={(e) => setTextForm(e.target.value)}
                rows={3}
                placeholder={t("similar.byTextPlaceholder")}
                className="w-full resize-none rounded-xl border border-line bg-bg px-4 py-2.5 text-sm text-ink placeholder:text-mute/60 focus:border-accent focus:outline-none focus:ring-2 focus:ring-accent/30"
              />
              <button
                type="button"
                disabled={textBusy || textForm.trim().length < 3}
                onClick={findByText}
                className="mt-3 w-full rounded-xl border border-accent/50 bg-accent/10 px-4 py-2.5 text-sm font-bold text-accent-2 transition-colors enabled:hover:bg-accent/20 disabled:opacity-50"
              >
                {textBusy ? t("common.loading") : t("similar.textFind")}
              </button>
              {textError && (
                <p className="mt-2 text-sm text-amber-300">{t("common.error")}</p>
              )}
            </div>

            {/* Історія добірок */}
            {history.length > 0 && (
              <div className="rounded-2xl border border-line bg-panel p-4 sm:p-5">
                <h2 className="mb-2 text-base font-bold text-ink">{t("similar.history")}</h2>
                <ul className="space-y-1">
                  {history.map((h, i) => (
                    <li key={i}>
                      <button
                        type="button"
                        onClick={() => {
                          setItems(h.items);
                          setUnavailable(h.unavailable);
                        }}
                        className="w-full truncate rounded-lg px-2 py-1.5 text-left text-sm text-mute transition-colors hover:bg-panel-2 hover:text-ink"
                        title={h.label}
                      >
                        {h.label}
                      </button>
                    </li>
                  ))}
                </ul>
              </div>
            )}
          </div>

          {resultsBlock(busy || textBusy, emptyTitle)}
        </div>
      )}

      {mode === "studio" && (
        <div className="grid gap-5 lg:grid-cols-[420px_1fr]">
          <StudioPanel
            seeds={studioSeeds}
            filters={studioFilters}
            onAdd={(x) => setStudioSeeds((prev) => [...prev, x])}
            onRemove={(id) => setStudioSeeds((prev) => prev.filter((s) => s.id !== id))}
            onClear={() => setStudioSeeds([])}
            onFilters={setStudioFilters}
          />
          {resultsBlock(busy, emptyTitle)}
        </div>
      )}

      {mode === "collections" && <CollectionsPanel onChain={chainFrom} />}
    </div>
  );
}

// useSearchParams вимагає Suspense-межі для статичного prerender (Next 15).
export default function SimilarPage() {
  return (
    <Suspense fallback={<div className="py-10 text-center text-sm text-mute">…</div>}>
      <SimilarInner />
    </Suspense>
  );
}
