// Бейдж провайдера («Де подивитись») з кольором за kind.
import { PROVIDER_KIND_STYLE } from "../lib/constants";
import type { ProviderRef } from "../lib/types";

export default function ProviderBadge({ provider }: { provider: ProviderRef }) {
  const style = PROVIDER_KIND_STYLE[provider.kind] ?? "bg-panel-2 text-mute border-line";
  const inner = (
    <>
      <span className="font-semibold">{provider.provider}</span>
      <span className="opacity-60">·</span>
      <span className="opacity-80">{provider.kind}</span>
    </>
  );
  const cls = `inline-flex items-center gap-1.5 rounded-full border px-3 py-1 text-xs ${style}`;
  if (provider.url && provider.url !== "#") {
    return (
      <a
        href={provider.url}
        target="_blank"
        rel="noreferrer noopener"
        className={`${cls} hover:brightness-125`}
      >
        {inner}
      </a>
    );
  }
  return <span className={cls}>{inner}</span>;
}
