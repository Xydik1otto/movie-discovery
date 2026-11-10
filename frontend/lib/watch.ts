// Довідник провайдерів перегляду (FR-14). Мапить машинні значення з API
// на ключі i18n; невідомі значення показуємо «як є» (напр., назва від TMDB).
import type { WatchProvider } from "./types";

// Провайдер → ключ назви в словнику.
const PROVIDER_NAME_KEYS: Record<string, string> = {
  sweettv: "provider.sweettv",
  archive: "provider.archive",
  "tmdb-streaming": "provider.tmdb-streaming",
  // FR-20: YouTube як джерело перегляду.
  youtube: "provider.youtube",
};

// kind → ключ підпису в словнику. Ключі watch.kind.* — бейджі панелі «Де подивитись»
// (рішення власника 09.11: усі варіанти з бейджем kind); "stream" — уніфікована форма
// (subscription → stream на бекенді), "subscription" — легасі-рядки detail.providers.
const KIND_LABEL_KEYS: Record<string, string> = {
  free: "watch.kind.free",
  ads: "watch.kind.ads",
  stream: "watch.kind.stream",
  subscription: "watch.kind.stream",
  rent: "watch.kind.rent",
  buy: "watch.kind.buy",
};

// kind → ключ короткого підпису для бейджа в кутку картки.
const KIND_BADGE_KEYS: Record<string, string> = {
  free: "watch.badge.free",
  subscription: "watch.badge.subscription",
  rent: "watch.badge.rent",
  buy: "watch.badge.buy",
};

export function providerNameKey(provider: string): string | null {
  return PROVIDER_NAME_KEYS[provider] ?? null;
}

export function watchKindLabelKey(kind: string): string | null {
  return KIND_LABEL_KEYS[kind] ?? null;
}

export function watchBadgeLabelKey(kind: string): string | null {
  return KIND_BADGE_KEYS[kind] ?? null;
}

// Рішення власника (07.11.2026): показуємо ЛИШЕ безкоштовні варіанти перегляду.
// Платні (передплата/оренда/купівля) офіційні сайти не показуємо взагалі —
// ні бейджі на картках, ні блок «Де подивитись»: усі дивляться безкоштовне.
export function freeOnly(list: WatchProvider[]): WatchProvider[] {
  return list.filter((w) => w.kind === "free");
}

// Перший провайдер на кожен kind — щоб у кутку картки не було дублікатів.
export function dedupeWatchKinds(list: WatchProvider[]): WatchProvider[] {
  const seen = new Set<string>();
  const out: WatchProvider[] = [];
  for (const w of list) {
    if (seen.has(w.kind)) continue;
    seen.add(w.kind);
    out.push(w);
  }
  return out;
}

// --- UX-04 (хвиля 2): мови перегляду і групи панелі «Де подивитись» ---

// csv «en,ru» → список кодів; порожнє → [].
export function parseLangCsv(csv: string | null | undefined): string[] {
  if (!csv) return [];
  return csv
    .split(",")
    .map((s) => s.trim())
    .filter((s) => s.length > 0);
}

// Мовна ГРУПА рядка (дзеркало WatchLanguages.GroupOf на бекенді):
// пріоритет uk → ru → original (конкретна не-uk/ru мова теж «оригінал/іноземна»)
// → unknown. Субтитри рахуються: en-аудіо + uk-суб → uk-група (субтитри).
export type ViewLangGroupKey = "uk" | "ru" | "original" | "unknown";

export function watchLangGroupOf(p: WatchProvider): ViewLangGroupKey {
  const audio = parseLangCsv(p.audioLangs);
  const subs = parseLangCsv(p.subLangs);
  const legacy = (p.audioLang ?? "").toLowerCase();
  if (audio.includes("uk") || subs.includes("uk") || legacy === "ukr") return "uk";
  if (audio.includes("ru") || subs.includes("ru") || legacy === "russ") return "ru";
  if (p.dub === "original" || legacy === "original") return "original";
  if (audio.includes("original") || audio.includes("multi") || legacy === "multi") return "original";
  if (audio.length > 0) return "original"; // en/pl/ja… — оригінал/іноземною (чесніше, ніж «невідома»)
  return "unknown";
}

// Вид перегляду в групі (чесні теги з даних): дубляж (dub), аудіо, субтитри.
export type WatchTag = "dub" | "audio" | "sub";

export function watchGroupTags(items: WatchProvider[]): WatchTag[] {
  const tags = new Set<WatchTag>();
  for (const p of items) {
    if (p.dub) tags.add("dub");
    if (parseLangCsv(p.audioLangs).length > 0 || (p.audioLang && !p.dub)) tags.add("audio");
    if (parseLangCsv(p.subLangs).length > 0) tags.add("sub");
  }
  const order: WatchTag[] = ["dub", "audio", "sub"];
  return order.filter((t) => tags.has(t));
}

// Групи в ПОРЯДКУ пріоритету (uk першими — основна мова аудиторії, далі ru,
// оригінал, невідома); порожні групи НЕ повертаються.
export interface WatchLangGroup {
  key: ViewLangGroupKey;
  items: WatchProvider[];
  tags: WatchTag[];
}

export const WATCH_LANG_GROUP_ORDER: ViewLangGroupKey[] = ["uk", "ru", "original", "unknown"];

export function groupWatchByLang(list: WatchProvider[]): WatchLangGroup[] {
  const byKey = new Map<ViewLangGroupKey, WatchProvider[]>();
  for (const p of list) {
    const key = watchLangGroupOf(p);
    const bucket = byKey.get(key);
    if (bucket) bucket.push(p);
    else byKey.set(key, [p]);
  }
  return WATCH_LANG_GROUP_ORDER.filter((k) => byKey.has(k)).map((key) => ({
    key,
    items: byKey.get(key)!,
    tags: watchGroupTags(byKey.get(key)!),
  }));
}

// Чи взагалі є мовні дані хоч у одного рядка (інакше перемикач мови не показуємо —
// всі рядки «невідома» і панель виглядає як простий список).
export function hasLangMetadata(list: WatchProvider[]): boolean {
  return list.some((p) => watchLangGroupOf(p) !== "unknown");
}

// urlKind: direct (deep-link) | find («Знайти на сервісу»). Легасі-рядки без
// urlKind: url є → direct (так працювало і до хвилі-2), url:null → find.
export function watchUrlKind(p: WatchProvider): "direct" | "find" {
  if (p.urlKind === "find") return "find";
  if (p.urlKind === "direct") return "direct";
  return p.url ? "direct" : "find";
}

// Чесний статус доступності: unknown ≠ «Безкоштовно»-бейдж.
export function watchAvailabilityStatus(p: WatchProvider): "available" | "unavailable" | "unknown" {
  if (p.availabilityStatus === "available" || p.availabilityStatus === "unavailable" || p.availabilityStatus === "unknown") {
    return p.availabilityStatus;
  }
  // легасі-рядки (компактна форма detail.providers): isAvailable з бекенда
  if (p.isAvailable === true) return "available";
  if (p.isAvailable === false) return "unavailable";
  return "unknown";
}

// --- FR-19: групування watch-посилань за озвучкою (dub + audioLang) ---

// dub → ключ підпису групи («Українська озвучка» тощо).
const DUB_LABEL_KEYS: Record<string, string> = {
  ukr: "watch.dub.ukr",
  russ: "watch.dub.russ",
  eng: "watch.dub.eng",
  original: "watch.dub.original",
  multi: "watch.dub.multi",
};

// audio_lang → ключ підпису групи, коли dub невідомий (напр., «Японська»).
const AUDIO_LANG_LABEL_KEYS: Record<string, string> = {
  ukr: "watch.audio.ukr",
  russ: "watch.audio.russ",
  eng: "watch.audio.eng",
  jpn: "watch.audio.jpn",
  kor: "watch.audio.kor",
  zho: "watch.audio.zho",
  original: "watch.audio.original",
  multi: "watch.audio.multi",
};

export function dubLabelKey(dub: string): string | null {
  return DUB_LABEL_KEYS[dub] ?? null;
}

export function audioLangLabelKey(audioLang: string): string | null {
  return AUDIO_LANG_LABEL_KEYS[audioLang] ?? null;
}

// Група = один «варіант перегляду»: озвучка (dub, за потреби audioLang)
// + максимальна якість серед її посилань.
export interface WatchDubGroup {
  // Сигнатура групи: dub має пріоритет, audioLang — коли dub невідомий.
  key: string;
  dub: string | null;
  audioLang: string | null;
  maxQuality: number | null;
  items: WatchProvider[];
}

// Чи взагалі є метадані озвучки/якості: якщо ні — watch-блок показує
// звичайний плоский список провайдерів «як зараз».
export function hasDubMetadata(list: WatchProvider[]): boolean {
  return list.some(
    (p) => p.dub || p.audioLang || (p.quality != null && p.quality > 0),
  );
}

// 480/720/1080/2160 → «480p»; інакше null.
export function qualityLabel(quality: number | null | undefined): string | null {
  return quality != null && quality > 0 ? `${quality}p` : null;
}

// Стійке групування за першою появою: order стабільний, як у відповіді API.
export function groupWatchByDub(list: WatchProvider[]): WatchDubGroup[] {
  const groups: WatchDubGroup[] = [];
  const byKey = new Map<string, WatchDubGroup>();
  for (const p of list) {
    const dub = p.dub || null;
    const audioLang = p.audioLang || null;
    const sig = dub ?? audioLang ?? "~";
    let g = byKey.get(sig);
    if (!g) {
      g = { key: sig, dub, audioLang, maxQuality: null, items: [] };
      byKey.set(sig, g);
      groups.push(g);
    }
    if (p.quality != null && p.quality > 0 && (g.maxQuality == null || p.quality > g.maxQuality)) {
      g.maxQuality = p.quality;
    }
    g.items.push(p);
  }
  return groups;
}

// Ключ підпису групи: dub → audioLang → null (невідомо → «Дивитись»).
export function dubGroupLabelKey(g: {
  dub: string | null;
  audioLang: string | null;
}): string | null {
  if (g.dub) return DUB_LABEL_KEYS[g.dub] ?? null;
  if (g.audioLang) return AUDIO_LANG_LABEL_KEYS[g.audioLang] ?? null;
  return null;
}
