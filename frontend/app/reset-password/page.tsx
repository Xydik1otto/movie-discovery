"use client";

// 10.11: встановлення нового пароля за посиланням із листа.
// ?userId=&token= → два поля пароля (з клієнтським збігом) →
// POST /api/auth/reset-password → 200 → редірект /login?reset=1
// («Пароль змінено — увійдіть новим»). 400 {error, errors?}:
// invalid_token → «Посилання недійсне або застаріле»; помилки
// Identity-політики (errors map) — списком, чесно і повністю.

import { Suspense, useState } from "react";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { useI18n } from "@/i18n/I18nContext";
import { ApiError, ApiUnavailableError } from "@/lib/api";
import { fieldErrorsFromBody, resetPassword } from "@/lib/auth";

// errors map → плоский список повідомлень (звичні значення — рядок або масив).
function errorListFromBody(body: unknown): string[] {
  const fe = fieldErrorsFromBody(body);
  return [
    ...(fe.general ?? []),
    ...(fe.email ?? []),
    ...(fe.username ?? []),
    ...(fe.password ?? []),
  ];
}

function ResetPasswordInner() {
  const { t } = useI18n();
  const router = useRouter();
  const params = useSearchParams();
  const userId = params.get("userId") ?? "";
  const token = params.get("token") ?? "";

  const [password, setPassword] = useState("");
  const [password2, setPassword2] = useState("");
  const [showPassword, setShowPassword] = useState(false);
  const [showPassword2, setShowPassword2] = useState(false);
  const [busy, setBusy] = useState(false);
  const [clientErrors, setClientErrors] = useState<Record<string, string>>({});
  const [policyErrors, setPolicyErrors] = useState<string[]>([]);
  const [invalid, setInvalid] = useState(!userId || !token);
  const [generalError, setGeneralError] = useState<string | null>(null);

  function validate(): boolean {
    const errs: Record<string, string> = {};
    if (!password) errs.password = t("auth.err.required");
    else if (password.length < 8) errs.password = t("auth.err.passwordShort");
    else if (!/\d/.test(password)) errs.password = t("auth.err.passwordDigit");
    else if (!/[A-ZА-ЯІЇЄҐ]/.test(password)) errs.password = t("auth.err.passwordUpper");
    if (password2 !== password) errs.password2 = t("auth.passwordsMismatch");
    setClientErrors(errs);
    return Object.keys(errs).length === 0;
  }

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    setGeneralError(null);
    setPolicyErrors([]);
    if (!validate()) return;
    setBusy(true);
    try {
      await resetPassword(userId, token, password);
      // Успіх → /login з прапорцем ?reset=1 («Пароль змінено — увійдіть новим»).
      router.replace("/login?reset=1");
    } catch (err) {
      if (err instanceof ApiUnavailableError) {
        setGeneralError(t("auth.err.unavailable"));
        return;
      }
      if (err instanceof ApiError && err.status === 400) {
        const body = err.body as { error?: unknown } | null;
        if (body?.error === "invalid_token") {
          // Прострочений/невідомий токен — чесно про посилання, resend пароля
          // не показуємо (правильний шлях — новий запит на /forgot-password).
          setInvalid(true);
          return;
        }
        // Помилки Identity-політики — показати СПИСКОМ, не першою випадковою.
        const list = errorListFromBody(err.body);
        if (list.length) setPolicyErrors(list);
        else setGeneralError(t("auth.err.generic"));
        return;
      }
      setGeneralError(t("auth.err.generic"));
    } finally {
      setBusy(false);
    }
  }

  const fieldError = (field: "password" | "password2"): string | null =>
    clientErrors[field] ?? null;

  const inputCls = (field: "password" | "password2") =>
    `w-full rounded-xl border bg-bg px-4 py-2.5 text-sm text-ink placeholder:text-mute/60 focus:outline-none focus:ring-2 ${
      fieldError(field)
        ? "border-red-500/60 focus:border-red-500 focus:ring-red-500/30"
        : "border-line focus:border-accent focus:ring-accent/30"
    }`;

  const eyeBtnCls =
    "absolute right-1.5 top-1/2 flex h-8 w-8 -translate-y-1/2 items-center justify-center rounded-lg text-base text-mute transition-colors hover:text-ink focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/30";

  const passwordsDiffer = password !== password2;
  const liveMismatch = password2.length > 0 && password2 !== password;

  return (
    <div className="mx-auto max-w-sm">
      <h1 className="text-2xl font-extrabold tracking-tight sm:text-3xl">
        {t("auth.resetTitle")}
      </h1>
      <p className="mt-2 text-sm text-mute">{t("auth.resetSubtitle")}</p>

      {invalid ? (
        <div className="mt-4">
          <div
            role="alert"
            className="rounded-xl border border-red-500/30 bg-red-500/10 px-4 py-3"
          >
            <p className="text-sm font-bold text-red-300">{t("auth.invalidLink")}</p>
          </div>
          <p className="mt-4 text-center text-sm text-mute">
            <Link
              href="/forgot-password"
              className="underline-offset-2 hover:text-ink hover:underline"
            >
              {t("auth.forgotPassword")}
            </Link>
          </p>
        </div>
      ) : (
        <form onSubmit={onSubmit} className="mt-6 space-y-4" noValidate>
          <div>
            <label
              htmlFor="reset-password"
              className="mb-1 block text-xs font-semibold text-mute"
            >
              {t("auth.password")}
            </label>
            <div className="relative">
              <input
                id="reset-password"
                type={showPassword ? "text" : "password"}
                autoComplete="new-password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                className={`${inputCls("password")} pr-11`}
              />
              <button
                type="button"
                onClick={() => setShowPassword((v) => !v)}
                onMouseDown={(e) => e.preventDefault()}
                aria-label={showPassword ? t("auth.hidePassword") : t("auth.showPassword")}
                aria-pressed={showPassword}
                title={showPassword ? t("auth.hidePassword") : t("auth.showPassword")}
                className={eyeBtnCls}
              >
                {showPassword ? "🙈" : "👁️"}
              </button>
            </div>
            {fieldError("password") ? (
              <p className="mt-1 text-xs text-red-400">{fieldError("password")}</p>
            ) : (
              <p className="mt-1 text-xs text-mute/70">{t("auth.passwordHint")}</p>
            )}
          </div>

          <div>
            <label
              htmlFor="reset-password2"
              className="mb-1 block text-xs font-semibold text-mute"
            >
              {t("auth.password2")}
            </label>
            <div className="relative">
              <input
                id="reset-password2"
                type={showPassword2 ? "text" : "password"}
                autoComplete="new-password"
                value={password2}
                onChange={(e) => setPassword2(e.target.value)}
                className={`${inputCls("password2")} pr-11`}
              />
              <button
                type="button"
                onClick={() => setShowPassword2((v) => !v)}
                onMouseDown={(e) => e.preventDefault()}
                aria-label={showPassword2 ? t("auth.hidePassword") : t("auth.showPassword")}
                aria-pressed={showPassword2}
                title={showPassword2 ? t("auth.hidePassword") : t("auth.showPassword")}
                className={eyeBtnCls}
              >
                {showPassword2 ? "🙈" : "👁️"}
              </button>
            </div>
            {(liveMismatch || fieldError("password2")) && (
              <p className="mt-1 text-xs text-red-400">
                {fieldError("password2") ?? t("auth.passwordsMismatch")}
              </p>
            )}
          </div>

          {/* Помилки Identity-політики — списком (10.11). */}
          {policyErrors.length > 0 && (
            <div
              role="alert"
              className="rounded-xl border border-red-500/30 bg-red-500/10 px-4 py-3"
            >
              <ul className="list-disc space-y-1 pl-5 text-sm text-red-300">
                {policyErrors.map((msg, i) => (
                  <li key={i}>{msg}</li>
                ))}
              </ul>
            </div>
          )}

          {generalError && (
            <p
              role="alert"
              className="rounded-xl border border-red-500/30 bg-red-500/10 px-3 py-2 text-sm text-red-300"
            >
              {generalError}
            </p>
          )}

          <button
            type="submit"
            disabled={busy || passwordsDiffer}
            className="w-full rounded-xl bg-accent px-4 py-2.5 text-sm font-bold text-white transition-all enabled:hover:bg-accent-2 enabled:active:scale-[0.98] disabled:opacity-60"
          >
            {busy ? t("common.loading") : t("auth.resetSubmit")}
          </button>
        </form>
      )}
    </div>
  );
}

// useSearchParams вимагає Suspense-межі для статичного prerender (Next 15).
export default function ResetPasswordPage() {
  return (
    <Suspense fallback={<div className="py-10 text-center text-sm text-mute">…</div>}>
      <ResetPasswordInner />
    </Suspense>
  );
}
