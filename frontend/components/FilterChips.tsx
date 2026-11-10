"use client";

import type { ReactNode } from "react";

// Чіп-філтр: обирається/знімається кліком. count — опційний лічильник із
// /api/filters (дрібним приглушеним числом праворуч від підпису).
export function Chip({
  selected,
  onClick,
  children,
  small = false,
  count,
}: {
  selected?: boolean;
  onClick?: () => void;
  children: ReactNode;
  small?: boolean;
  count?: number | null;
}) {
  const pad = small ? "px-2.5 py-1 text-xs" : "px-3 py-1.5 text-sm";
  return (
    <button
      type="button"
      aria-pressed={selected}
      onClick={onClick}
      // shrink-0 + whitespace-nowrap: чіп не стискається і не переноситься на
      // 2 рядки — у скрол-рядах (тими) усі чіпи однакової висоти, довші — скролляться.
      className={`shrink-0 whitespace-nowrap rounded-full border transition-colors ${pad} ${
        selected
          ? "border-accent bg-accent/15 font-semibold text-accent-2"
          : "border-line bg-panel text-mute hover:border-mute hover:text-ink"
      }`}
    >
      {children}
      {count != null && count > 0 ? (
        <span className="ml-1.5 text-[10px] font-normal opacity-60">{count}</span>
      ) : null}
    </button>
  );
}

export function ChipGroup({ children }: { children: ReactNode }) {
  return <div className="flex flex-wrap gap-1.5">{children}</div>;
}

// Активний фільтр: чіп з підписом і кнопкою-«х» для зняття (панель фільтрів).
export function ActiveFilterChip({
  label,
  onClear,
}: {
  label: string;
  onClear: () => void;
}) {
  return (
    <span className="inline-flex shrink-0 items-center gap-1 rounded-full border border-accent/40 bg-accent/10 py-1 pl-2.5 pr-1 text-xs font-medium text-accent-2">
      {label}
      <button
        type="button"
        onClick={onClear}
        aria-label={`${label} — прибрати фільтр`}
        className="rounded-full px-1 leading-none transition-colors hover:bg-accent/25"
      >
        ×
      </button>
    </span>
  );
}
