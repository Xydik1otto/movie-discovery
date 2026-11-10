"use client";

// Панель «Де подивитись» (UX-04, хвиля 2) — повний власник зони watch у title-сторінці:
//   • перемикач МОВИ ПЕРЕГЛЯДУ (uk/ru/оригінал/невідома) НЕЗАЛЕЖНИЙ від мови UI;
//   • групи чесні: «Українською (дубляж/аудіо/субтитри)» — теги лише з реальних даних;
//   • direct-лінки окремо від «Знайти на сервісу» (find — з іконкою пошуку);
//   • tri-state доступності: unknown (неперевірений каталог Plex) ≠ «Безкоштовно»;
//   • ціни чесні (rent/buy — якщо ціна є в даних), платні за перемикачем,
//     дефолт — безкоштовні (рішення власника 09.11 збережено);
//   • empty states окремі: «нема у вашому регіоні (UA)» vs «нема цією мовою — інші доступні».
import { useMemo, useState } from "react";
import { useI18n } from "../i18n/I18nContext";
import { PROVIDER_KIND_STYLE } from "../lib/constants";
import {
  groupWatchByLang,
  parseLangCsv,
  watchAvailabilityStatus,
  watchLangGroupOf,
  watchUrlKind,
  qualityLabel,
  type ViewLangGroupKey,
} from "../lib/watch";
import type { ProviderKind, WatchProvider } from "../lib/types";

const GROUP_LABEL_KEYS: Record<ViewLangGroupKey, string> = {
  uk: "watch.lang.uk",
  ru: "watch.lang.ru",
  original: "watch.lang.original",
  unknown: "watch.lang.unknown",
};

const TAG_KEYS: Record<string, string> = {
  dub: "watch.tag.dub",
  audio: "watch.tag.audio",
  sub: "watch.tag.sub",
};

function providerDisplayName(t: (k: string) => string, provider: string): string {
  const key = `provider.${provider}`;
  const label = t(key);
  return label && label !== key ? label : provider;
}

export default function WatchPanel({
  providers,
  region,
}: {
  providers: WatchProvider[];
  region: string;
}) {
  const { t } = useI18n();
  // Рішення власника (09.11): за замовчуванням лише безкоштовні (free|ads),
  // платні — за перемикачем (стан локальний панелі).
  const [showPaid, setShowPaid] = useState(false);
  const isFreeish = (p: WatchProvider) => p.kind === "free" || p.kind === "ads";
  const hasPaid = providers.some((p) => !isFreeish(p));
  const visible = showPaid ? providers : providers.filter(isFreeish);

  const groups = useMemo(() => groupWatchByLang(visible), [visible]);
  const hasLangData = useMemo(() => groups.some((g) => g.key !== "unknown"), [groups]);
  // Початковий вибір мови: uk → ru → оригінал → невідома (перша непорожня група).
  const defaultGroup = groups[0]?.key ?? "unknown";
  const [selected, setSelected] = useState<ViewLangGroupKey | null>(null);
  const active = selected && groups.some((g) => g.key === selected) ? selected : defaultGroup;
  const activeGroup = groups.find((g) => g.key === active);

  // ── Empty state 1: у регіоні взагалі немає даних ──
  if (providers.length === 0) {
    return (
      <section className="rounded-2xl border border-line bg-panel p-4">
        <h2 className="mb-2 text-base font-bold">{t("title.watch.where")}</h2>
        <p className="text-sm text-mute">{t("watch.empty.region", { region })}</p>
      </section>
    );
  }

  const unknownCatalog = (p: WatchProvider) =>
    watchAvailabilityStatus(p) === "unknown";
  const unknownCount = visible.filter(unknownCatalog).length;

  return (
    <section className="rounded-2xl border border-line bg-panel p-4">
      <h2 className="mb-3 text-base font-bold">{t("title.watch.where")}</h2>

      {/* Перемикач мови перегляду — НЕЗАЛЕЖНИЙ від мови UI. Показуємо лише коли
          є мовні дані (інакше всі рядки чесно «невідома» і перемикач безглуздий). */}
      {hasLangData && (
        <div className="mb-3 flex flex-wrap gap-1.5" role="tablist" aria-label={t("watch.lang.switcher")}>
          {groups.map((g) => {
            const selectedStyle =
              g.key === active
                ? "border-accent/70 bg-accent/15 text-accent-2"
                : "border-line bg-panel-2 text-mute hover:border-accent/40";
            return (
              <button
                key={g.key}
                type="button"
                role="tab"
                aria-selected={g.key === active}
                onClick={() => setSelected(g.key)}
                className={`rounded-full border px-3 py-1 text-xs font-semibold transition-colors ${selectedStyle}`}
              >
                {t(GROUP_LABEL_KEYS[g.key])}
                <span className="ml-1 opacity-60">{g.items.length}</span>
              </button>
            );
          })}
        </div>
      )}

      {/* Empty state 2: у вибраній мові нічого, але в інших мовах є */}
      {!activeGroup && hasLangData && (
        <div className="rounded-xl border border-line bg-panel-2 p-3">
          <p className="text-sm text-mute">{t("watch.empty.lang")}</p>
          <p className="mt-1 text-xs text-mute">{t("watch.empty.langHint")}</p>
          <div className="mt-2 flex flex-wrap gap-1.5">
            {groups
              .filter((g) => g.key !== active)
              .map((g) => (
                <button
                  key={g.key}
                  type="button"
                  onClick={() => setSelected(g.key)}
                  className="rounded-full border border-line bg-panel px-2.5 py-1 text-xs font-semibold text-accent-2 hover:border-accent/60"
                >
                  {t(GROUP_LABEL_KEYS[g.key])} ({g.items.length})
                </button>
              ))}
          </div>
        </div>
      )}

      {activeGroup && (
        <div className="space-y-2">
          {/* Теги групи з реальних даних: дубляж/аудіо/субтитри — без вигадок */}
          {activeGroup.tags.length > 0 && (
            <div className="flex flex-wrap items-center gap-1.5 text-[11px] text-mute">
              <span className="text-xs font-semibold uppercase tracking-wide text-mute">
                {t(GROUP_LABEL_KEYS[activeGroup.key])}
              </span>
              <span className="opacity-50">·</span>
              {activeGroup.tags.map((tag) => (
                <span
                  key={tag}
                  className="rounded-md border border-line bg-panel-2 px-1.5 py-0.5"
                >
                  {t(TAG_KEYS[tag])}
                </span>
              ))}
            </div>
          )}
          <ul className="space-y-1.5">
            {activeGroup.items.map((p, i) => (
              <WatchRow key={`${p.provider}-${p.kind}-${p.url ?? "nourl"}-${p.audioLangs ?? "nolang"}-${i}`} watch={p} t={t} />
            ))}
          </ul>
        </div>
      )}

      {/* Неклікабельні/unknown-рядки не ховаємо — чесний стан видно у рядку */}
      {unknownCount > 0 && (
        <p className="mt-2 text-[11px] text-mute">{t("watch.unknownNote", { count: unknownCount })}</p>
      )}

      {hasPaid && (
        <button
          type="button"
          onClick={() => setShowPaid((v) => !v)}
          className="mt-3 rounded-full border border-line bg-panel-2 px-3 py-1 text-xs font-semibold text-mute transition-colors hover:border-accent/60 hover:text-accent-2"
        >
          {showPaid ? t("watch.hidePaid") : t("watch.showPaid")}
        </button>
      )}
    </section>
  );
}

function WatchRow({ watch: p, t }: { watch: WatchProvider; t: (k: string, vars?: Record<string, string | number>) => string }) {
  const style = PROVIDER_KIND_STYLE[p.kind as ProviderKind] ?? "bg-panel-2 text-mute border-line";
  const name = providerDisplayName((k) => t(k), p.provider);
  const kindKey = p.kind === "free" ? "watch.kind.free" : `watch.kind.${p.kind}`;
  const kindLabel = ["free", "ads", "stream", "rent", "buy"].includes(p.kind) ? t(kindKey) : p.kind;
  const quality = qualityLabel(p.quality);
  const avail = watchAvailabilityStatus(p);
  const isFind = watchUrlKind(p) === "find";

  // Чесні підписи стану (unknown ≠ «Безкоштовно»-бейдж):
  const statusLabel =
    avail === "unknown"
      ? p.source === "plex"
        ? t("watch.unknown.plexCatalog")
        : t("watch.status.unknown")
      : avail === "unavailable"
        ? t("watch.status.unavailable")
        : null;

  // Ціна чесна: лише якщо є в даних (rent/buy); free/ads/stream ціну не показуємо.
  const price = (p.kind === "rent" || p.kind === "buy") && p.price ? p.price : null;

  const inner = (
    <>
      {isFind ? (
        <span aria-hidden="true" className="text-mute">🔍</span>
      ) : null}
      <span className="font-semibold">{name}</span>
      <span className="opacity-60">·</span>
      <span className="opacity-80">{kindLabel}</span>
      {price && (
        <>
          <span className="opacity-60">·</span>
          <span className="font-bold">{price}</span>
        </>
      )}
      {quality && (
        <>
          <span className="opacity-60">·</span>
          <span className="font-bold">{quality}</span>
        </>
      )}
      {/* Мовні набори конкретного оффера (не прикрашаємо: показуємо лише відомі) */}
      {parseLangCsv(p.audioLangs).length > 0 && (
        <>
          <span className="opacity-60">·</span>
          <span className="opacity-80">{t("watch.tag.audioShort")}: {parseLangCsv(p.audioLangs).map((l) => t(`watch.lang.code.${l}`)).filter((x) => !x.startsWith("watch.")).join(", ")}</span>
        </>
      )}
      {statusLabel && (
        <>
          <span className="opacity-60">·</span>
          <span className="italic opacity-80">{statusLabel}</span>
        </>
      )}
      {isFind && (
        <>
          <span className="opacity-60">·</span>
          <span className="opacity-70">{t("watch.findOn", { provider: name })}</span>
        </>
      )}
    </>
  );
  const cls = `inline-flex flex-wrap items-center gap-1.5 rounded-full border px-3 py-1 text-xs ${style}`;

  // find-лінк теж клікабельний (корисний вихід на сервіс), але чесно підписаний
  // «Знайти на сервісу» — НЕ як пряме переглядання; url:null → неклікабельний.
  if (p.url && p.url !== "#") {
    return (
      <li>
        <a href={p.url} target="_blank" rel="noopener noreferrer" className={`${cls} hover:brightness-125`}>
          {inner}
        </a>
      </li>
    );
  }
  return (
    <li>
      <span className={`${cls} opacity-90`}>{inner}</span>
    </li>
  );
}

// Реекспорт для зручності інтеграції в page.tsx (pag-2): один import.
export { watchLangGroupOf };
