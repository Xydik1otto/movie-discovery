"use client";

import type { ReactNode } from "react";

// Чіп-філтр: обирається/знімається кліком.
export function Chip({
  selected,
  onClick,
  children,
  small = false,
}: {
  selected?: boolean;
  onClick?: () => void;
  children: ReactNode;
  small?: boolean;
}) {
  const pad = small ? "px-2.5 py-1 text-xs" : "px-3 py-1.5 text-sm";
  return (
    <button
      type="button"
      aria-pressed={selected}
      onClick={onClick}
      className={`rounded-full border transition-colors ${pad} ${
        selected
          ? "border-accent bg-accent/15 font-semibold text-accent-2"
          : "border-line bg-panel text-mute hover:border-mute hover:text-ink"
      }`}
    >
      {children}
    </button>
  );
}

export function ChipGroup({ children }: { children: ReactNode }) {
  return <div className="flex flex-wrap gap-1.5">{children}</div>;
}
