// Причини схожості (1-3 локалізовані рядки від API).

export default function ReasonsList({
  reasons,
  label,
}: {
  reasons?: string[];
  label?: string;
}) {
  if (!reasons?.length) return null;
  return (
    <div className="mt-2">
      {label && (
        <div className="mb-1 text-[11px] font-semibold uppercase tracking-wide text-mute">
          {label}
        </div>
      )}
      <ul className="space-y-1">
        {reasons.slice(0, 3).map((r, i) => (
          <li
            key={i}
            className="flex items-start gap-1.5 text-xs leading-snug text-mute"
          >
            <span className="mt-0.5 shrink-0 text-accent" aria-hidden>
              ✦
            </span>
            <span>{r}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}
