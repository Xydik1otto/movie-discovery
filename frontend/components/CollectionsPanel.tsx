"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { useI18n } from "../i18n/I18nContext";
import { useAuth } from "../lib/auth";
import {
  ApiError,
  createCollection,
  deleteCollection,
  getCollectionPresets,
  getCollections,
} from "../lib/api";
import { guardedBySeedsEx, guardedSearch, isUnavailable } from "../lib/fallback";
import { posterUrlFull } from "../lib/constants";
import type {
  CollectionSummary,
  PresetCollection,
  SimilarItem,
  TitleCard as TitleCardType,
} from "../lib/types";
import TitleCard from "./TitleCard";
import ApiBanner from "./ApiBanner";
import EmptyState from "./EmptyState";
import LoadingSkeleton from "./LoadingSkeleton";
import PosterImage from "./PosterImage";

const MAX_CHIPS = 3;

// Локалізовані назви серверних пресетів; незнаний ключ показуємо як є.
const PRESET_KEY: Record<string, string> = {
  mood: "collections.preset.mood",
  evening: "collections.preset.evening",
  duration: "collections.preset.duration",
};

// Бекенд у items може слати як картки тайтлів (пресети), так і голі titleId
// (форма POST) — для гріду придатні лише картки, решту м'яко відкидаємо.
function cardsOf(items?: (TitleCardType | number)[] | null): TitleCardType[] {
  if (!items) return [];
  const out: TitleCardType[] = [];
  for (const x of items) {
    if (x && typeof x === "object" && "title" in x) out.push(x as TitleCardType);
  }
  return out;
}

// FR-21 «Збірники»: (a) серверні пресети (12 мініатюр, клік → грід результатів),
// (b) генератор 1–3 тайтли + назва → POST /api/collections → прев'ю добірки,
// (c) «Мої добірки» (auth; 401 → підказка «увійди») з видаленням.
export default function CollectionsPanel({
  onChain,
}: {
  onChain: (t: TitleCardType) => void;
}) {
  const { t, lang } = useI18n();
  const { user, ready } = useAuth();

  // --- пресети ---
  const [presets, setPresets] = useState<PresetCollection[]>([]);
  const [presetsLoading, setPresetsLoading] = useState(true);
  const [presetsUnavailable, setPresetsUnavailable] = useState(false);
  const [presetsError, setPresetsError] = useState(false);
  const [openPreset, setOpenPreset] = useState<string | null>(null);

  // --- мої добірки ---
  const [mine, setMine] = useState<CollectionSummary[] | null>(null);
  const [mineLoading, setMineLoading] = useState(true);
  const [mineNeedLogin, setMineNeedLogin] = useState(false);
  const [mineUnavailable, setMineUnavailable] = useState(false);
  const [mineError, setMineError] = useState(false);
  const [deletingId, setDeletingId] = useState<number | null>(null);

  // --- генератор ---
  const [query, setQuery] = useState("");
  const [options, setOptions] = useState<TitleCardType[]>([]);
  const [open, setOpen] = useState(false);
  const boxRef = useRef<HTMLDivElement>(null);
  const [chips, setChips] = useState<TitleCardType[]>([]);
  const [name, setName] = useState("");
  const [creating, setCreating] = useState(false);
  const [createDone, setCreateDone] = useState(false);
  const [createErr, setCreateErr] = useState<"auth" | "error" | null>(null);

  // --- прев'ю добірки (результати) ---
  const [preview, setPreview] = useState<SimilarItem[] | null>(null);
  const [previewLabel, setPreviewLabel] = useState("");
  const [previewBusy, setPreviewBusy] = useState(false);
  const [previewUnavailable, setPreviewUnavailable] = useState(false);
  const [previewError, setPreviewError] = useState(false);

  const loadPresets = useCallback(async () => {
    setPresetsLoading(true);
    setPresetsError(false);
    setPresetsUnavailable(false);
    try {
      setPresets(await getCollectionPresets(lang));
    } catch (e) {
      if (isUnavailable(e)) setPresetsUnavailable(true);
      else setPresetsError(true);
    } finally {
      setPresetsLoading(false);
    }
  }, [lang]);

  const loadMine = useCallback(async () => {
    setMineLoading(true);
    setMineNeedLogin(false);
    setMineError(false);
    setMineUnavailable(false);
    if (!user) {
      // анонімний профіль → 401 гарантовано; не дергаємо бекенд, одразу підказка
      setMine(null);
      setMineNeedLogin(true);
      setMineLoading(false);
      return;
    }
    try {
      setMine(await getCollections());
    } catch (e) {
      if (e instanceof ApiError && e.status === 401) setMineNeedLogin(true);
      else if (isUnavailable(e)) setMineUnavailable(true);
      else setMineError(true);
    } finally {
      setMineLoading(false);
    }
  }, [user]);

  useEffect(() => {
    void loadPresets();
  }, [loadPresets]);

  useEffect(() => {
    if (ready) void loadMine();
  }, [ready, loadMine]);

  // Автокомпліт генератора (як у SeedsBuilder/StudioPanel).
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
        // автокомпліт не блокує панель
      }
    }, 350);
    return () => clearTimeout(timer);
  }, [query, lang]);

  const addChip = (item: TitleCardType) => {
    if (chips.length >= MAX_CHIPS) return;
    if (chips.some((c) => c.id === item.id)) return;
    setChips((prev) => [...prev, item]);
    setQuery("");
    setOptions([]);
    setOpen(false);
  };

  async function runPreview(list: TitleCardType[], label: string) {
    const ids = list.map((c) => c.id);
    setPreviewLabel(label);
    if (!ids.length) {
      setPreview([]);
      setPreviewUnavailable(false);
      setPreviewError(false);
      return;
    }
    setPreviewBusy(true);
    setPreviewError(false);
    setPreviewUnavailable(false);
    try {
      const r = await guardedBySeedsEx(ids, lang);
      setPreview(r.data);
      setPreviewUnavailable(r.unavailable);
    } catch {
      setPreview(null);
      setPreviewError(true);
    } finally {
      setPreviewBusy(false);
    }
  }

  async function create() {
    if (chips.length < 1 || chips.length > MAX_CHIPS || !name.trim() || creating) return;
    setCreating(true);
    setCreateErr(null);
    setCreateDone(false);
    try {
      // POST /api/collections {name, items:[titleId...]} — внутрішні id тайтлів.
      await createCollection(name.trim(), chips.map((c) => c.id));
      setCreateDone(true);
      setName("");
      void loadMine();
    } catch (e) {
      if (e instanceof ApiError && e.status === 401) setCreateErr("auth");
      else setCreateErr("error");
    } finally {
      setCreating(false);
    }
    // Прев'ю — незалежно від результату POST: гість теж бачить добірку,
    // просто без збереження (graceful до 401).
    await runPreview(chips, chips.map((c) => c.title).join(" + "));
  }

  async function remove(id: number) {
    setDeletingId(id);
    try {
      await deleteCollection(id);
      setMine((prev) => (prev ? prev.filter((c) => c.id !== id) : prev));
    } catch {
      setMineError(true);
    } finally {
      setDeletingId(null);
    }
  }

  function openSaved(c: CollectionSummary) {
    const cards = cardsOf(c.items);
    if (!cards.length) return; // items — голі id без карток: прев'ю неможливе без додаткових запитів
    setPreview(cards);
    setPreviewLabel(c.name);
    setPreviewBusy(false);
    setPreviewError(false);
    setPreviewUnavailable(false);
  }

  const canCreate = chips.length >= 1 && chips.length <= MAX_CHIPS && !!name.trim() && !creating;
  const inputCls =
    "w-full rounded-xl border border-line bg-bg px-4 py-2.5 text-sm text-ink placeholder:text-mute/60 focus:border-accent focus:outline-none focus:ring-2 focus:ring-accent/30";

  const openPresetData = openPreset
    ? presets.find((p) => p.key === openPreset) ?? null
    : null;
  const openPresetLabel = openPreset
    ? PRESET_KEY[openPreset]
      ? t(PRESET_KEY[openPreset])
      : openPreset
    : "";

  return (
    <div className="space-y-6">
      {/* (a) Пресети */}
      <section>
        <h2 className="mb-3 text-lg font-bold text-ink">{t("collections.presets")}</h2>
        {presetsUnavailable && <ApiBanner />}
        {presetsLoading ? (
          <LoadingSkeleton count={3} />
        ) : presets.length === 0 ? (
          <p className="text-sm text-mute">
            {presetsError ? t("common.error") : t("collections.presetEmpty")}
          </p>
        ) : (
          <div className="grid gap-3 sm:grid-cols-3">
            {presets.map((p) => {
              const cards = cardsOf(p.items);
              const isOpen = openPreset === p.key;
              // Бекенд шле ключі з префіксом ("preset.mood") — довідник локалізованих
              // назв зберігає голі імена; зрізаємо префікс, незнане лишаємо як є.
              const bareKey = p.key.replace(/^preset\./, "");
              const presetLabel = PRESET_KEY[bareKey]
                ? t(PRESET_KEY[bareKey])
                : p.key;
              return (
                <button
                  key={p.key}
                  type="button"
                  aria-expanded={isOpen}
                  onClick={() => setOpenPreset(isOpen ? null : p.key)}
                  className={`rounded-2xl border p-3 text-left transition-colors ${
                    isOpen
                      ? "border-accent bg-accent/5"
                      : "border-line bg-panel hover:border-accent/60"
                  }`}
                >
                  <div className="flex items-center justify-between gap-2">
                    <span className="text-sm font-bold text-ink">{presetLabel}</span>
                    <span className="text-xs text-mute">{cards.length}</span>
                  </div>
                  <div className="mt-2 grid grid-cols-6 gap-1">
                    {cards.slice(0, 12).map((c) => {
                      const thumb = posterUrlFull(c.posterUrl, "w92");
                      return thumb ? (
                        // UX-02: thumbnail з чесними станами (loading/404-1 ретрай/«Постер недоступний»)
                        <span
                          key={c.id}
                          className="block aspect-[2/3] w-full overflow-hidden rounded bg-panel-2"
                        >
                          <PosterImage src={thumb} title={c.title} compact />
                        </span>
                      ) : (
                        <span
                          key={c.id}
                          aria-hidden
                          className="flex aspect-[2/3] items-center justify-center rounded bg-panel-2 text-xs"
                        >
                          🎞️
                        </span>
                      );
                    })}
                  </div>
                </button>
              );
            })}
          </div>
        )}

        {/* Грід розгорнутого пресета */}
        {openPresetData && (
          <div className="mt-4">
            <h3 className="mb-2 text-sm font-bold text-ink">{openPresetLabel}</h3>
            {cardsOf(openPresetData.items).length === 0 ? (
              <EmptyState title={t("search.noResults")} />
            ) : (
              <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 xl:grid-cols-4">
                {cardsOf(openPresetData.items).map((c) => (
                  <TitleCard key={c.id} item={c} onSimilar={onChain} />
                ))}
              </div>
            )}
          </div>
        )}
      </section>

      {/* (b) Генератор */}
      <section className="rounded-2xl border border-line bg-panel p-4 sm:p-5">
        <h2 className="text-lg font-bold text-ink">{t("collections.generator")}</h2>
        <p className="mb-3 text-xs leading-relaxed text-mute">{t("collections.chipsHint")}</p>

        <div ref={boxRef} className="relative">
          <input
            type="text"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            onFocus={() => options.length > 0 && setOpen(true)}
            onKeyDown={(e) => {
              if (e.key === "Escape") setOpen(false);
              if (e.key === "Enter" && options.length > 0 && open) {
                e.preventDefault();
                addChip(options[0]);
              }
            }}
            placeholder={t("similar.searchSeed")}
            disabled={chips.length >= MAX_CHIPS}
            className={`${inputCls} disabled:opacity-50`}
          />
          {open && options.length > 0 && (
            <ul className="absolute z-30 mt-1 max-h-72 w-full overflow-y-auto rounded-xl border border-line bg-panel shadow-xl">
              {options.map((o) => {
                const picked = chips.some((c) => c.id === o.id);
                return (
                  <li key={o.id}>
                    <button
                      type="button"
                      disabled={picked}
                      onMouseDown={(e) => {
                        e.preventDefault();
                        addChip(o);
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

        {chips.length > 0 && (
          <div className="mt-3 flex flex-wrap gap-1.5">
            {chips.map((c) => (
              <span
                key={c.id}
                className="inline-flex items-center gap-1.5 rounded-full border border-accent/40 bg-accent/10 py-1 pl-3 pr-1.5 text-xs font-medium text-accent-2"
              >
                {c.title}
                {c.year != null ? ` (${c.year})` : ""}
                <button
                  type="button"
                  aria-label={`${t("similar.clear")}: ${c.title}`}
                  onClick={() => setChips((prev) => prev.filter((x) => x.id !== c.id))}
                  className="rounded-full px-1 text-accent-2/70 hover:bg-accent/20 hover:text-accent-2"
                >
                  ✕
                </button>
              </span>
            ))}
          </div>
        )}

        <label className="mt-3 block">
          <span className="mb-1 block text-xs font-semibold text-mute">
            {t("collections.nameLabel")}
          </span>
          <input
            type="text"
            value={name}
            onChange={(e) => setName(e.target.value)}
            maxLength={80}
            placeholder={t("collections.namePlaceholder")}
            className={inputCls}
          />
        </label>

        <div className="mt-3 flex flex-wrap items-center gap-3">
          <button
            type="button"
            disabled={!canCreate}
            onClick={() => void create()}
            className="rounded-xl bg-accent px-5 py-2.5 text-sm font-bold text-white transition-all enabled:hover:bg-accent-2 enabled:active:scale-95 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {creating ? t("common.loading") : t("collections.create")}
          </button>
          {createDone && (
            <span className="text-sm text-emerald-300">{t("collections.created")}</span>
          )}
          {createErr === "auth" && (
            <span className="text-sm text-amber-300">{t("collections.loginHint")}</span>
          )}
          {createErr === "error" && (
            <span className="text-sm text-amber-300">{t("common.error")}</span>
          )}
        </div>
      </section>

      {/* Прев'ю добірки (після генерації або кліку по збереженій) */}
      {(previewBusy || preview != null) && (
        <section>
          <h3 className="mb-2 text-base font-bold text-ink">
            {t("collections.preview")}
            {previewLabel ? `: ${previewLabel}` : ""}
          </h3>
          {previewUnavailable && <ApiBanner />}
          {previewError && <p className="text-sm text-amber-300">{t("similar.similarError")}</p>}
          {previewBusy ? (
            <LoadingSkeleton count={10} />
          ) : preview && preview.length === 0 ? (
            <EmptyState title={t("search.noResults")} />
          ) : preview ? (
            <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 xl:grid-cols-4">
              {preview.map((item) => (
                <TitleCard
                  key={`${item.id}-${item.tmdbId}`}
                  item={item}
                  score={item.score}
                  onSimilar={onChain}
                />
              ))}
            </div>
          ) : null}
        </section>
      )}

      {/* (c) Мої добірки */}
      <section>
        <h2 className="mb-2 text-lg font-bold text-ink">{t("collections.mine")}</h2>
        {mineNeedLogin && (
          <p
            role="status"
            className="rounded-xl border border-accent/30 bg-accent/10 px-4 py-2.5 text-sm text-accent-2"
          >
            {t("collections.loginHint")}
          </p>
        )}
        {mineUnavailable && <ApiBanner />}
        {mineError && <p className="text-sm text-amber-300">{t("common.error")}</p>}
        {mineLoading && !mineNeedLogin && <LoadingSkeleton count={2} />}
        {mine && mine.length === 0 && (
          <p className="text-sm text-mute">{t("collections.mineEmpty")}</p>
        )}
        {mine && mine.length > 0 && (
          <ul className="space-y-1.5">
            {mine.map((c) => {
              const canOpen = cardsOf(c.items).length > 0;
              return (
                <li
                  key={c.id}
                  className="flex items-center gap-2 rounded-xl border border-line bg-panel px-3 py-2"
                >
                  {canOpen ? (
                    <button
                      type="button"
                      onClick={() => openSaved(c)}
                      className="min-w-0 flex-1 truncate text-left text-sm font-medium text-ink transition-colors hover:text-accent-2"
                      title={c.name}
                    >
                      {c.name}
                    </button>
                  ) : (
                    <span className="min-w-0 flex-1 truncate text-sm font-medium text-ink">
                      {c.name}
                    </span>
                  )}
                  {typeof c.itemCount === "number" && (
                    <span className="shrink-0 text-xs text-mute">
                      {t("collections.itemsCount", { n: c.itemCount })}
                    </span>
                  )}
                  <button
                    type="button"
                    onClick={() => void remove(c.id)}
                    disabled={deletingId === c.id}
                    aria-label={`${t("lists.remove")}: ${c.name}`}
                    className="shrink-0 rounded-full px-1.5 text-mute transition-colors hover:bg-panel-2 hover:text-ink disabled:opacity-40"
                  >
                    ✕
                  </button>
                </li>
              );
            })}
          </ul>
        )}
      </section>
    </div>
  );
}
