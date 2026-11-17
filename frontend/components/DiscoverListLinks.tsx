"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { useI18n } from "@/i18n/I18nContext";
import { getListCounts } from "@/lib/api";
import { loadGuestLists } from "@/lib/guestLists";
import type { ListKind } from "@/lib/types";

const VIEWING_STATUSES = [
  "watchlist",
  "watching",
  "watched",
  "on_hold",
  "dropped",
] as const satisfies readonly ListKind[];

type ListCounts = Partial<Record<ListKind, number>>;

export default function DiscoverListLinks({
  userId,
  revision,
}: {
  userId: string | null;
  revision: number;
}) {
  const { t } = useI18n();
  const [result, setResult] = useState<{ userId: string | null; counts: ListCounts } | null>(null);

  useEffect(() => {
    if (userId === null) {
      const counts: ListCounts = {};
      for (const { kind } of loadGuestLists()) {
        counts[kind] = (counts[kind] ?? 0) + 1;
      }
      setResult({ userId, counts });
      return;
    }

    const controller = new AbortController();
    getListCounts(controller.signal)
      .then((counts) => {
        if (!controller.signal.aborted) setResult({ userId, counts });
      })
      .catch(() => {
        if (!controller.signal.aborted) setResult(null);
      });
    return () => controller.abort();
  }, [userId, revision]);

  const counts = result?.userId === userId ? result.counts : null;

  return (
    <nav aria-label={t("nav.lists")} className="mb-5 flex flex-wrap gap-2">
      {VIEWING_STATUSES.map((kind) => (
        <Link
          key={kind}
          href={`/lists?kind=${kind}`}
          prefetch={false}
          className="rounded-full border border-line bg-panel px-3 py-1.5 text-sm text-ink transition-colors hover:border-accent hover:text-accent focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent"
        >
          {t(`discover.lists.${kind}`)}
          {counts !== null && (
            <span className="ml-1.5 tabular-nums text-mute">({counts[kind] ?? 0})</span>
          )}
        </Link>
      ))}
    </nav>
  );
}
