"use client";

import { useEffect, useMemo, useState, useSyncExternalStore } from "react";
import { useI18n } from "../i18n/I18nContext";
import { getWatch } from "../lib/api";
import { PROVIDER_KIND_STYLE, REGIONS } from "../lib/constants";
import {
  groupWatchByLang,
  parseLangCsv,
  qualityLabel,
  watchAvailabilityStatus,
  watchUrlKind,
} from "../lib/watch";
import type { ViewLangGroupKey } from "../lib/watch";
import type { WatchProvider, WatchSearchLink } from "../lib/types";

const POLL_DELAYS = [800, 1500, 2500, 4000, 6500];
const LEGACY_LANGUAGES: Record<string, string> = { ukr: "uk", russ: "ru", eng: "en", jpn: "ja", kor: "ko", zho: "zh" };
const COUNTRY_CODES = new Intl.DisplayNames(["en"], { type: "region", fallback: "none" });

const GROUP_LABEL_KEYS: Record<ViewLangGroupKey, string> = {
  uk: "watch.lang.uk",
  ru: "watch.lang.ru",
  original: "watch.lang.original",
  unknown: "watch.lang.unknown",
};

function offerLanguages(provider: WatchProvider) {
  return [
    ...parseLangCsv(provider.audioLangs),
    ...parseLangCsv(provider.subLangs),
    LEGACY_LANGUAGES[provider.audioLang ?? ""] ?? provider.audioLang ?? "",
  ].filter((code) => /^[a-z]{2}$/.test(code));
}

function countryCode(value: string | null | undefined) {
  const code = value?.trim().toUpperCase();
  if (!code || !/^[A-Z]{2}$/.test(code)) return null;
  return COUNTRY_CODES.of(code) ? code : null;
}

function readRegion() {
  try {
    return countryCode(localStorage.getItem("region")) ?? "UA";
  } catch {
    return "UA";
  }
}

function subscribeRegion(onChange: () => void) {
  window.addEventListener("storage", onChange);
  return () => window.removeEventListener("storage", onChange);
}

export default function WatchPanel({
  titleId,
  initialProviders = [],
  online,
}: {
  titleId: number;
  initialProviders?: WatchProvider[];
  online: boolean;
}) {
  const { lang, t } = useI18n();
  const savedRegion = useSyncExternalStore(subscribeRegion, readRegion, () => null);
  const [selectedRegion, setSelectedRegion] = useState<string | null>(null);
  const requestRegion = selectedRegion ?? savedRegion;
  const region = requestRegion ?? "UA";
  const [options, setOptions] = useState<WatchProvider[] | null>(null);
  const [searchLinks, setSearchLinks] = useState<WatchSearchLink[]>([]);
  const [completedRegion, setCompletedRegion] = useState<string | null>(null);
  const [showPaid, setShowPaid] = useState(false);
  const [language, setLanguage] = useState("");
  const [selected, setSelected] = useState<ViewLangGroupKey | null>(null);

  useEffect(() => {
    if (!online || !requestRegion) return;

    const controller = new AbortController();
    const signal = AbortSignal.any([controller.signal, AbortSignal.timeout(20_000)]);
    let timer: ReturnType<typeof setTimeout> | undefined;
    let attempt = 0;

    async function refresh() {
      try {
        const result = await getWatch(titleId, signal, requestRegion ?? undefined);
        signal.throwIfAborted();
        setOptions(result.providers.filter((provider) => watchAvailabilityStatus(provider) !== "unavailable"));
        setSearchLinks(result.searchLinks ?? []);
        const delay = POLL_DELAYS[attempt++];
        if (result.discoveryPending && delay !== undefined) {
          timer = setTimeout(refresh, delay);
          return;
        }
      } catch {
        // Keep the last successful response, or the title page's cached offers.
      }
      if (!controller.signal.aborted) setCompletedRegion(requestRegion);
    }

    void refresh();
    return () => {
      controller.abort();
      if (timer !== undefined) clearTimeout(timer);
    };
  }, [online, requestRegion, titleId]);

  const regionNames = useMemo(() => new Intl.DisplayNames([lang], { type: "region" }), [lang]);
  const languageNames = useMemo(() => new Intl.DisplayNames([lang], { type: "language" }), [lang]);
  const pool = options ?? initialProviders;
  const countries = [...new Set([...REGIONS, region,
    ...[...pool, ...searchLinks].map((p) => countryCode(p.region)).filter((code) => code !== null)])]
    .sort((a, b) => regionNames.of(a)!.localeCompare(regionNames.of(b)!, lang));
  const providers = pool.filter((p) => !p.region || p.region === "*" || p.region.toUpperCase() === region);
  const regionalLinks = searchLinks.filter((link) => link.region === region || link.region === "*");
  const catalogs = regionalLinks.filter((link) => !link.source);
  const discovered = regionalLinks.filter((link) => link.source === "brave-web");
  const pending = online && completedRegion !== requestRegion;
  const isFree = (p: WatchProvider) => p.kind === "free" || p.kind === "ads";
  const hasPaid = providers.some((p) => !isFree(p));
  const byPrice = showPaid ? providers : providers.filter(isFree);
  const languages = [...new Set(byPrice.flatMap(offerLanguages))]
    .sort((a, b) => languageNames.of(a)!.localeCompare(languageNames.of(b)!, lang));
  const visible = byPrice.filter((p) => !language || offerLanguages(p).includes(language));
  const groups = groupWatchByLang(visible);
  const activeGroup = groups.find((group) => group.key === selected) ?? groups[0];
  const hasJustWatch = activeGroup?.items.some((p) => p.source === "tmdb-providers" || p.source === "justwatch");

  function selectCountry(code: string) {
    setSelectedRegion(code);
    setCompletedRegion(null);
    setLanguage("");
    try {
      localStorage.setItem("region", code);
    } catch {
      // The selected country still applies to the current page.
    }
  }

  const selectStyle = "min-w-0 rounded-lg border border-line bg-panel-2 px-2 py-1.5 text-sm text-ink";
  return (
    <section className="rounded-2xl border border-line bg-panel p-4">
      <h2 className="mb-3 text-base font-bold">{t("title.watch.where")}</h2>
      <div className="mb-3 flex flex-wrap gap-3">
        <label className="flex min-w-0 flex-col gap-1 text-xs text-mute">
          {t("watch.region.select")}
          <select className={selectStyle} value={region} onChange={(event) => selectCountry(event.target.value)}>
            {countries.map((code) => <option key={code} value={code}>{regionNames.of(code)}</option>)}
          </select>
        </label>
        {(languages.length > 0 || language) && (
          <label className="flex min-w-0 flex-col gap-1 text-xs text-mute">
            {t("watch.language.select")}
            <select className={selectStyle} value={language} onChange={(event) => setLanguage(event.target.value)}>
              <option value="">{t("watch.language.any")}</option>
              {[...new Set([...languages, ...(language ? [language] : [])])].map((code) => (
                <option key={code} value={code}>{languageNames.of(code)}</option>
              ))}
            </select>
          </label>
        )}
      </div>

      {pending && <p role="status" className="mb-2 text-sm text-mute">{t("watch.discovery.pending")}</p>}
      {!pending && providers.length === 0 && (
        <p className="text-sm text-mute">{t("watch.empty.region", { region: regionNames.of(region) ?? region })}</p>
      )}
      {providers.length > 0 && byPrice.length === 0 && (
        <p className="text-sm text-mute">{t("watch.empty.free")}</p>
      )}
      {byPrice.length > 0 && !activeGroup && <p className="text-sm text-mute">{t("watch.empty.lang")}</p>}

      {groups.length > 1 && (
        <div className="mb-3 flex flex-wrap gap-1.5" role="group" aria-label={t("watch.lang.switcher")}>
          {groups.map((group) => (
            <button
              key={group.key}
              type="button"
              aria-pressed={group.key === activeGroup?.key}
              onClick={() => setSelected(group.key)}
              className={`rounded-full border px-3 py-1 text-xs font-semibold transition-colors ${group.key === activeGroup?.key
                ? "border-accent/70 bg-accent/15 text-accent-2"
                : "border-line bg-panel-2 text-mute hover:border-accent/40"}`}
            >
              {t(GROUP_LABEL_KEYS[group.key])} <span className="opacity-60">{group.items.length}</span>
            </button>
          ))}
        </div>
      )}

      {activeGroup && (
        <ul className="space-y-1.5">
          {activeGroup.items.map((provider, index) => (
            <WatchRow key={`${provider.provider}-${provider.kind}-${provider.url}-${index}`} watch={provider} languageNames={languageNames} t={t} />
          ))}
        </ul>
      )}
      {hasPaid && (
        <button
          type="button"
          onClick={() => setShowPaid((value) => !value)}
          aria-pressed={showPaid}
          className="mt-3 rounded-full border border-line bg-panel-2 px-3 py-1 text-xs font-semibold text-mute transition-colors hover:border-accent/60 hover:text-accent-2"
        >
          {showPaid ? t("watch.hidePaid") : t("watch.showPaid")}
        </button>
      )}
      {hasJustWatch && <p className="mt-2 text-xs text-mute">{t("watch.source.justwatch")}</p>}
      {discovered.length > 0 && (
        <details className="mt-3 border-t border-line pt-2" open={providers.length === 0}>
          <summary className="cursor-pointer text-sm text-accent-2">{t("watch.web.links")}</summary>
          <p className="my-2 text-xs text-mute">{t("watch.web.hint")}</p>
          <ul className="flex flex-wrap gap-2">
            {discovered.map((link) => (
              <li key={link.url}>
                <a className="rounded-lg border border-line px-2.5 py-1 text-xs hover:border-accent/60"
                  href={link.url} target="_blank" rel="noopener noreferrer">{link.provider}</a>
              </li>
            ))}
          </ul>
        </details>
      )}
      {catalogs.length > 0 && (
        <details className="mt-3 border-t border-line pt-2">
          <summary className="cursor-pointer text-sm text-accent-2">{t("watch.catalog.search")}</summary>
          <p className="my-2 text-xs text-mute">{t("watch.catalog.hint")}</p>
          <ul className="flex flex-wrap gap-2">
            {catalogs.map((link) => (
              <li key={link.url}>
                <a className="rounded-lg border border-line px-2.5 py-1 text-xs hover:border-accent/60"
                  href={link.url} target="_blank" rel="noopener noreferrer">{link.provider}</a>
              </li>
            ))}
          </ul>
        </details>
      )}
    </section>
  );
}

function WatchRow({ watch: provider, languageNames, t }: {
  watch: WatchProvider;
  languageNames: Intl.DisplayNames;
  t: (k: string, vars?: Record<string, string | number>) => string;
}) {
  const nameKey = `provider.${provider.provider}`;
  const translated = t(nameKey);
  const name = translated === nameKey ? provider.provider : translated;
  const kind = provider.kind === "subscription" ? "stream" : provider.kind;
  const kindKey = `watch.kind.${kind}`;
  const kindLabel = t(kindKey);
  const quality = qualityLabel(provider.quality);
  const price = (kind === "rent" || kind === "buy") ? provider.price : null;
  const isFind = watchUrlKind(provider) === "find";
  const isCatalog = provider.source === "tmdb-providers";
  const url = /^https?:\/\//i.test(provider.url ?? "") ? provider.url : null;
  const labels = (csv: string | null | undefined) => parseLangCsv(csv).map((code) =>
    /^[a-z]{2}$/.test(code) ? languageNames.of(code) : t(`watch.lang.code.${code}`)).join(", ");
  const audio = labels(provider.audioLangs) || (provider.audioLang ? t(`watch.audio.${provider.audioLang}`) : "");
  const subtitles = labels(provider.subLangs);
  const style = PROVIDER_KIND_STYLE[provider.kind] ?? "bg-panel-2 text-mute border-line";
  const className = `inline-flex flex-wrap items-center gap-x-2 gap-y-1 rounded-xl border px-3 py-1.5 text-xs ${style}`;
  const entry = (
    <>
      <span className="font-semibold">{name}</span>
      <span>{kindLabel === kindKey ? kind : kindLabel}</span>
      {price && <span className="font-bold">{price}</span>}
      {quality && <span className="font-bold">{quality}</span>}
      {provider.dub && <span>{t("watch.tag.dub")}</span>}
      {audio && !audio.startsWith("watch.") && <span>{t("watch.tag.audioShort")} {audio}</span>}
      {subtitles && <span>{t("watch.tag.sub")} {subtitles}</span>}
      {isCatalog && <span className="italic">{t("watch.catalog.listing")}</span>}
      {!isCatalog && watchAvailabilityStatus(provider) === "unknown" && <span className="italic">{t("watch.status.unknown")}</span>}
      {isFind && !isCatalog && url && <span>{t("watch.findOn", { provider: name })}</span>}
    </>
  );

  return (
    <li>
      {url ? <a href={url} target="_blank" rel="noopener noreferrer" className={`${className} hover:brightness-125`}>{entry}</a>
        : <span className={className}>{entry}</span>}
    </li>
  );
}
