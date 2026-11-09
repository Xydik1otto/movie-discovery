// MD-04 / PLAN FR-16: гостьові списки у versioned localStorage.
// Гість не має акаунта і не ходить на /api/lists (старий шлях із дефолтним
// profileId=1 читав чужі/спільні дані — видалено). Усі доступи try/catch:
// приватний режим браузера не має ламати сторінку.

import type { ListKind, TitleType } from "./types";

export const GUEST_LISTS_KEY = "md_guest_lists_v1";

export const GUEST_KINDS: ListKind[] = [
  "watchlist",
  "watched",
  "read",
  "liked",
  "disliked",
];

// Метадані для рендера картки на /lists без серверних запитів (гість).
// У POST /api/lists/migrate-guest НЕ летять — бекенд приймає kind/titleId/rating.
export interface GuestEntryMeta {
  title: string;
  type: TitleType;
  posterUrl?: string | null;
  year?: number | null;
}

export interface GuestEntry {
  kind: ListKind;
  titleId: number;
  tmdbId: number | null;
  rating: number | null;
  addedAt: string;
  meta?: GuestEntryMeta | null;
}

interface GuestStore {
  version: 1;
  items: GuestEntry[];
}

const KIND_SET = new Set<string>(GUEST_KINDS);

function readStore(): GuestStore {
  try {
    const raw = window.localStorage.getItem(GUEST_LISTS_KEY);
    if (!raw) return { version: 1, items: [] };
    const parsed = JSON.parse(raw) as Partial<GuestStore> | null;
    if (!parsed || parsed.version !== 1 || !Array.isArray(parsed.items)) {
      return { version: 1, items: [] };
    }
    // Толеруємо лише валідні записи — биті/чужі структури відкидаємо, не падаємо.
    const items = parsed.items.filter(
      (it): it is GuestEntry =>
        !!it &&
        typeof it === "object" &&
        typeof (it as GuestEntry).kind === "string" &&
        KIND_SET.has((it as GuestEntry).kind) &&
        typeof (it as GuestEntry).titleId === "number" &&
        Number.isFinite((it as GuestEntry).titleId),
    );
    return { version: 1, items };
  } catch {
    return { version: 1, items: [] };
  }
}

function writeStore(store: GuestStore): boolean {
  try {
    window.localStorage.setItem(GUEST_LISTS_KEY, JSON.stringify(store));
    return true;
  } catch {
    // Переповнення/приватний режим — запис не вдався; UI показує чесне попередження.
    return false;
  }
}

// Чи доступний localStorage узагалі (проба запису/читання/видалення).
export function storageAvailable(): boolean {
  try {
    const probe = "__md_storage_probe__";
    window.localStorage.setItem(probe, "1");
    window.localStorage.removeItem(probe);
    return true;
  } catch {
    return false;
  }
}

// Усі записи або один kind (kind === "read" використовується лише для манги —
// правило ListKinds застосовує сторінка тайтла, тут приймаємо як є).
export function loadGuestLists(kind?: ListKind): GuestEntry[] {
  const items = readStore().items;
  return kind ? items.filter((i) => i.kind === kind) : items;
}

// Ідемпотентний upsert за (kind, titleId); оновлення rating — тим самим add.
export function addGuestItem(
  kind: ListKind,
  ref: { id: number; tmdbId?: number | null },
  rating?: number | null,
  meta?: GuestEntryMeta | null,
): boolean {
  const store = readStore();
  const existing = store.items.find((i) => i.kind === kind && i.titleId === ref.id);
  if (existing) {
    if (rating != null) existing.rating = rating;
    if (meta) existing.meta = meta;
  } else {
    store.items.push({
      kind,
      titleId: ref.id,
      tmdbId: ref.tmdbId ?? null,
      rating: rating ?? null,
      addedAt: new Date().toISOString(),
      meta: meta ?? null,
    });
  }
  return writeStore(store);
}

export function removeGuestItem(kind: ListKind, titleId: number): boolean {
  const store = readStore();
  const items = store.items.filter((i) => !(i.kind === kind && i.titleId === titleId));
  if (items.length === store.items.length) return true; // нема чого видаляти — уже «видалено»
  return writeStore({ version: 1, items });
}

export function rateGuestItem(kind: ListKind, titleId: number, rating: number): boolean {
  const store = readStore();
  const item = store.items.find((i) => i.kind === kind && i.titleId === titleId);
  if (!item) return addGuestItem(kind, { id: titleId }, rating);
  item.rating = rating;
  return writeStore(store);
}

// Повний знімок (усі kind) для міграції. Читається ЛИШЕ з локального сховища —
// ніколи з сервера (сервер після login віддає вже акаунтні списки).
export function guestSnapshot(): GuestEntry[] {
  return loadGuestLists();
}

// MD-04: маркер завершення — `md_guest_migrated:{userId}:{hash}`.
// Scoped за акаунтом+знімком: інший користувач не успадковує чужий маркер,
// а зміна знімка (нові гостьові дії після часткового збою) пропонує міграцію знову.
export function migrationMarkerKey(userId: string | number, snapshot: GuestEntry[]): string {
  const canonical = JSON.stringify(
    snapshot
      .map((i) => `${i.kind}:${i.titleId}:${i.rating ?? ""}`)
      .sort(),
  );
  // djb2 — достатньо, щоб розрізняти різні знімки (не криптографія).
  let h = 5381;
  for (let i = 0; i < canonical.length; i++) {
    h = ((h << 5) + h + canonical.charCodeAt(i)) >>> 0;
  }
  return `md_guest_migrated:${userId}:${h.toString(16)}`;
}

export function hasMigrationMarker(userId: string | number, snapshot: GuestEntry[]): boolean {
  try {
    return window.localStorage.getItem(migrationMarkerKey(userId, snapshot)) === "1";
  } catch {
    return false;
  }
}

export function setMigrationMarker(userId: string | number, snapshot: GuestEntry[]): void {
  try {
    window.localStorage.setItem(migrationMarkerKey(userId, snapshot), "1");
  } catch {
    // без storage маркер не переживе перезавантаження — банер просто з'явиться знову
  }
}

// CR-03 фінал: full-wipe знімка ВИДАЛЕНО з API (clearGuestSnapshot) — він стирав
// усі гостьові дані разом з rejected/новими записами інших вкладок. Видалення
// тепер лише точкове через removeGuestEntries за ідентичністю запису.

// CR-03 фінал: прибрати зі знімка ЛИШЕ підтверджені записи, ТОЧНО ідентичні
// надісланому snapshot (kind + titleId + rating + addedAt). Зміна рейтингу або
// remove+readd того самого ключа іншою вкладкою під час in-flight міграції
// створюють НОВИЙ запис — він не збігається з підтвердженим і лишається для
// наступної явної міграції. Ключ alone (kind,titleId) недостатній: сервер
// підтвердив лише надіслане значення, а не новіше.
export interface GuestEntryIdentity {
  kind: string;
  titleId: number;
  rating: number | null;
  addedAt: string;
}

// Токен ідентичності запису: усі поля значенням; addedAt розрізняє повторно
// додані записи того самого ключа (новий add = нова мітка часу).
function entryToken(e: { kind: string; titleId: number; rating: number | null; addedAt: string }): string {
  return `${e.kind}:${e.titleId}:${e.rating ?? ""}:${e.addedAt}`;
}

export function removeGuestEntries(confirmed: readonly GuestEntryIdentity[]): boolean {
  if (confirmed.length === 0) return true;
  const drop = new Set(confirmed.map(entryToken));
  const store = readStore();
  const items = store.items.filter((i) => !drop.has(entryToken(i)));
  if (items.length === store.items.length) return true; // ніщо не збігається — лишити як є
  return writeStore({ version: store.version, items });
}
