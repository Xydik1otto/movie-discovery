"use client";

// 10.11: відновлення пароля. Email-форма → POST /api/auth/forgot-password →
// ЗАВЖДИ 200 → чесний текст «якщо акаунт існує — лист надіслано»
// (існування акаунта не розкриваємо — так само і для невідомих email).

import { useState } from "react";
import Link from "next/link";
import { useI18n } from "@/i18n/I18nContext";
import { ApiError, ApiUnavailableError } from "@/lib/api";
import { forgotPassword } from "@/lib/auth";
import { EMAIL_RE } from "@/components/AuthForm";

export default function ForgotPasswordPage() {
  const { t } = useI18n();
  const [email, setEmail] = useState("");
  const [busy, setBusy] = useState(false);
  const [sent, setSent] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    // Клієнтська валідація — чесні повідомлення до запиту.
    if (!email.trim()) {
      setError(t("auth.err.required"));
      return;
    }
    if (!EMAIL_RE.test(email.trim())) {
      setError(t("auth.err.email"));
      return;
    }
    setBusy(true);
    try {
      await forgotPassword(email.trim());
      // 200 (завжди за контрактом) — чесний текст без розкриття акаунтів.
      setSent(true);
    } catch (err) {
      setError(
        err instanceof ApiUnavailableError
          ? t("auth.err.unavailable")
          : err instanceof ApiError && err.status === 429
            ? t("auth.resendTooFrequent")
            : t("auth.err.generic"),
      );
    } finally {
      setBusy(false);
    }
  }

  const inputCls =
    "w-full rounded-xl border border-line bg-bg px-4 py-2.5 text-sm text-ink placeholder:text-mute/60 focus:border-accent focus:outline-none focus:ring-2 focus:ring-accent/30";

  return (
    <div className="mx-auto max-w-sm">
      <h1 className="text-2xl font-extrabold tracking-tight sm:text-3xl">
        {t("auth.forgotTitle")}
      </h1>

      {sent ? (
        <>
          <div
            role="status"
            className="mt-4 rounded-xl border border-emerald-500/30 bg-emerald-500/10 px-4 py-3"
          >
            <p className="text-sm text-emerald-300">{t("auth.forgotSent")}</p>
          </div>
          <p className="mt-4 text-center text-sm text-mute">
            <Link
              href="/login"
              className="underline-offset-2 hover:text-ink hover:underline"
            >
              {t("auth.toLogin")}
            </Link>
          </p>
        </>
      ) : (
        <>
          <p className="mt-2 text-sm text-mute">{t("auth.forgotSubtitle")}</p>
          <form onSubmit={onSubmit} className="mt-6 space-y-4" noValidate>
            <div>
              <label
                htmlFor="forgot-email"
                className="mb-1 block text-xs font-semibold text-mute"
              >
                {t("auth.email")}
              </label>
              <input
                id="forgot-email"
                type="email"
                autoComplete="email"
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                className={inputCls}
              />
              {error && <p className="mt-1 text-xs text-red-400">{error}</p>}
            </div>
            <button
              type="submit"
              disabled={busy}
              className="w-full rounded-xl bg-accent px-4 py-2.5 text-sm font-bold text-white transition-all enabled:hover:bg-accent-2 enabled:active:scale-[0.98] disabled:opacity-60"
            >
              {busy ? t("common.loading") : t("auth.forgotSubmit")}
            </button>
          </form>
          <p className="mt-4 text-center text-sm text-mute">
            <Link
              href="/login"
              className="underline-offset-2 hover:text-ink hover:underline"
            >
              {t("auth.toLogin")}
            </Link>
          </p>
        </>
      )}
    </div>
  );
}
