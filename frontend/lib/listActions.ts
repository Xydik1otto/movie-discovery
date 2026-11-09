// MD-04: єдина точка дій зі списками. Залогінений → API (cookie-профіль,
// credentials: include в request()); гість → localStorage (lib/guestLists.ts).
// Використовується сторінкою тайтла і сторінкою списків, тож поведінка
// «додати/оцінити» однакова всюди і не залежить від прихованого profileId.

import { addToList, rateListItem } from "./api";
import { addGuestItem, rateGuestItem, type GuestEntryMeta } from "./guestLists";
import type { ListKind } from "./types";

export interface ListRef {
  id: number;
  tmdbId?: number | null;
}

// Куди ляг запис: "server" (акаунт) або "guest" (лише цей браузер).
export type ListTarget = "server" | "guest";

// CR-04: відмова localStorage (quota/приватний режим) — ЧЕСНА помилка,
// а не «успіх» без запису. UI ловить через catch і показує банер.
export class GuestStorageError extends Error {
  constructor() {
    super("guest_storage_failed");
    this.name = "GuestStorageError";
  }
}

// Ідемпотентний upsert: POST /api/lists/{kind} {titleId, rating?} або
// addGuestItem за (kind, titleId). rating опційний (watchlist без оцінки).
export async function addListItem(
  authenticated: boolean,
  kind: ListKind,
  ref: ListRef,
  rating?: number | null,
  meta?: GuestEntryMeta | null,
): Promise<ListTarget> {
  if (authenticated) {
    await addToList(ref, kind, rating ?? null);
    return "server";
  }
  // CR-04: addGuestItem повертає false, коли запис НЕ зберігся (quota/privacy) —
  // ігнорувати не можна: «додано» без збереження = false success.
  if (!addGuestItem(kind, { id: ref.id, tmdbId: ref.tmdbId }, rating ?? null, meta ?? null)) {
    throw new GuestStorageError();
  }
  return "guest";
}

// Оцінка 1–10: той самий ідемпотентний upsert на сервері / у localStorage.
export async function rateListEntry(
  authenticated: boolean,
  kind: ListKind,
  ref: ListRef,
  rating: number,
): Promise<ListTarget> {
  if (authenticated) {
    await rateListItem(kind, ref, rating);
    return "server";
  }
  if (!rateGuestItem(kind, ref.id, rating)) {
    throw new GuestStorageError();
  }
  return "guest";
}
