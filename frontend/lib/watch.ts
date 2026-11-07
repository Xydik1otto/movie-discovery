// Довідник провайдерів перегляду (FR-14). Мапить машинні значення з API
// на ключі i18n; невідомі значення показуємо «як є» (напр., назва від TMDB).
import type { WatchProvider } from "./types";

// Провайдер → ключ назви в словнику.
const PROVIDER_NAME_KEYS: Record<string, string> = {
  sweettv: "provider.sweettv",
  archive: "provider.archive",
  "tmdb-streaming": "provider.tmdb-streaming",
};

// kind → ключ підпису в словнику. "stream" — легасі-форма detail.providers.
const KIND_LABEL_KEYS: Record<string, string> = {
  free: "watch.free",
  subscription: "watch.subscription",
  rent: "watch.rent",
  buy: "watch.buy",
  stream: "providers.stream",
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
