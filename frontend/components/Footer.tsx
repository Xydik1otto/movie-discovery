"use client";

import { useI18n } from "../i18n/I18nContext";

export default function Footer() {
  const { t } = useI18n();
  return (
    <footer className="mt-10 border-t border-line py-6">
      <div className="mx-auto flex max-w-7xl flex-col gap-1 px-4 text-xs text-mute">
        <p>{t("footer.attribution")}</p>
        <p className="opacity-60">
          © {new Date().getFullYear()} {t("brand.name")}
        </p>
      </div>
    </footer>
  );
}
