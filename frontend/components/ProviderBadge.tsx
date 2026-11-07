// Бейдж провайдера «Де подивитись» (FR-14): читабельна назва + підпис kind,
// колір за kind. Провайдер-посилання — target=_blank rel=noopener noreferrer.
import { useI18n } from "../i18n/I18nContext";
import { PROVIDER_KIND_STYLE } from "../lib/constants";
import { providerNameKey, watchKindLabelKey } from "../lib/watch";
import type { WatchProvider } from "../lib/types";

export default function ProviderBadge({ watch }: { watch: WatchProvider }) {
  const { t } = useI18n();
  const style = PROVIDER_KIND_STYLE[watch.kind] ?? "bg-panel-2 text-mute border-line";
  const nameKey = providerNameKey(watch.provider);
  const name = nameKey ? t(nameKey) : watch.provider;
  const kindKey = watchKindLabelKey(watch.kind);
  const kindLabel = kindKey ? t(kindKey) : watch.kind;
  const inner = (
    <>
      <span className="font-semibold">{name}</span>
      <span className="opacity-60">·</span>
      <span className="opacity-80">{kindLabel}</span>
    </>
  );
  const cls = `inline-flex items-center gap-1.5 rounded-full border px-3 py-1 text-xs ${style}`;
  if (watch.url && watch.url !== "#") {
    return (
      <a
        href={watch.url}
        target="_blank"
        rel="noopener noreferrer"
        className={`${cls} hover:brightness-125`}
      >
        {inner}
      </a>
    );
  }
  return <span className={cls}>{inner}</span>;
}
