"use client";

// FR-16: спільна форма входу/реєстрації. Валідація пароля на клієнті
// (≥8 + цифра + велика), помилки бекенду — по полях (400 {errors:{...}}).

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { useI18n } from "../i18n/I18nContext";
import { useAuth, type AuthFailure } from "../lib/auth";

type Mode = "login" | "register";

const EMAIL_RE = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

export default function AuthForm({ mode }: { mode: Mode }) {
  const { t } = useI18n();
  const { login, register } = useAuth();
  const router = useRouter();

  const [email, setEmail] = useState("");
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  // Показ/приховання пароля (око) і «Запам'ятати мене» (галочка ввімкнена за замовчуванням).
  const [showPassword, setShowPassword] = useState(false);
  const [rememberMe, setRememberMe] = useState(true);
  const [busy, setBusy] = useState(false);
  // Помилки клієнтської валідації та бекенду — окремо, бекендові сильніші.
  const [clientErrors, setClientErrors] = useState<Record<string, string>>({});
  const [serverErrors, setServerErrors] = useState<AuthFailure | null>(null);

  function validate(): boolean {
    const errs: Record<string, string> = {};
    if (!email.trim()) errs.email = t("auth.err.required");
    else if (!EMAIL_RE.test(email.trim())) errs.email = t("auth.err.email");
    if (mode === "register" && !username.trim())
      errs.username = t("auth.err.required");
    if (!password) errs.password = t("auth.err.required");
    else if (mode === "register") {
      if (password.length < 8) errs.password = t("auth.err.passwordShort");
      else if (!/\d/.test(password)) errs.password = t("auth.err.passwordDigit");
      else if (!/[A-ZА-ЯІЇЄҐ]/.test(password))
        errs.password = t("auth.err.passwordUpper");
    }
    setClientErrors(errs);
    return Object.keys(errs).length === 0;
  }

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    setServerErrors(null);
    if (!validate()) return;
    setBusy(true);
    try {
      const failure =
        mode === "login"
          ? await login(email.trim(), password, rememberMe)
          : await register(email.trim(), username.trim(), password);
      if (failure) {
        setServerErrors(failure);
        return;
      }
      // Успіх → на ціль (?next=…) або додому/онбординг.
      const params = new URLSearchParams(window.location.search);
      const next = params.get("next");
      const fallback = mode === "register" ? "/onboarding" : "/";
      router.replace(next && next.startsWith("/") ? next : fallback);
    } finally {
      setBusy(false);
    }
  }

  const fieldError = (field: "email" | "username" | "password"): string | null => {
    const fromServer =
      serverErrors?.fieldErrors?.[
        field === "email" ? "email" : field === "username" ? "username" : "password"
      ];
    if (fromServer?.length) return fromServer[0];
    return clientErrors[field] ?? null;
  };

  const generalError = (() => {
    if (serverErrors?.fieldErrors.general?.length)
      return serverErrors.fieldErrors.general[0];
    if (!serverErrors) return null;
    switch (serverErrors.kind) {
      case "credentials":
        return t("auth.err.credentials");
      case "lockout":
        return t("auth.err.lockout");
      case "unavailable":
        return t("auth.err.unavailable");
      default:
        // 400 без розпізнаних полів — загальна форма.
        return serverErrors.fieldErrors.email?.length ||
          serverErrors.fieldErrors.username?.length ||
          serverErrors.fieldErrors.password?.length
          ? null
          : t("auth.err.generic");
    }
  })();

  const inputCls = (field: "email" | "username" | "password") =>
    `w-full rounded-xl border bg-bg px-4 py-2.5 text-sm text-ink placeholder:text-mute/60 focus:outline-none focus:ring-2 ${
      fieldError(field)
        ? "border-red-500/60 focus:border-red-500 focus:ring-red-500/30"
        : "border-line focus:border-accent focus:ring-accent/30"
    }`;

  return (
    <div className="mx-auto max-w-sm">
      <h1 className="text-2xl font-extrabold tracking-tight sm:text-3xl">
        {t(mode === "login" ? "auth.loginTitle" : "auth.registerTitle")}
      </h1>
      <p className="mt-2 text-sm text-mute">
        {t(mode === "login" ? "auth.loginSubtitle" : "auth.registerSubtitle")}
      </p>

      <form onSubmit={onSubmit} className="mt-6 space-y-4" noValidate>
        <div>
          <label htmlFor="auth-email" className="mb-1 block text-xs font-semibold text-mute">
            {t("auth.email")}
          </label>
          <input
            id="auth-email"
            type="email"
            autoComplete="email"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            className={inputCls("email")}
          />
          {fieldError("email") && (
            <p className="mt-1 text-xs text-red-400">{fieldError("email")}</p>
          )}
        </div>

        {mode === "register" && (
          <div>
            <label
              htmlFor="auth-username"
              className="mb-1 block text-xs font-semibold text-mute"
            >
              {t("auth.username")}
            </label>
            <input
              id="auth-username"
              type="text"
              autoComplete="username"
              value={username}
              onChange={(e) => setUsername(e.target.value)}
              className={inputCls("username")}
            />
            {fieldError("username") && (
              <p className="mt-1 text-xs text-red-400">{fieldError("username")}</p>
            )}
          </div>
        )}

        <div>
          <label
            htmlFor="auth-password"
            className="mb-1 block text-xs font-semibold text-mute"
          >
            {t("auth.password")}
          </label>
          {/* Поле пароля з кнопкою показу/приховання: текст не підповзає під
              кнопку (pr-11), кнопка всередині поля (absolute right). */}
          <div className="relative">
            <input
              id="auth-password"
              type={showPassword ? "text" : "password"}
              autoComplete={mode === "login" ? "current-password" : "new-password"}
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
              className="absolute right-1.5 top-1/2 flex h-8 w-8 -translate-y-1/2 items-center justify-center rounded-lg text-base text-mute transition-colors hover:text-ink focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/30"
            >
              {showPassword ? "🙈" : "👁️"}
            </button>
          </div>
          {fieldError("password") ? (
            <p className="mt-1 text-xs text-red-400">{fieldError("password")}</p>
          ) : (
            mode === "register" && (
              <p className="mt-1 text-xs text-mute/70">{t("auth.passwordHint")}</p>
            )
          )}
        </div>

        {mode === "login" && (
          // «Запам'ятати мене»: за замовчуванням ввімкнено — сесія персистентна.
          <label className="flex cursor-pointer select-none items-center gap-2 text-sm text-mute">
            <input
              type="checkbox"
              checked={rememberMe}
              onChange={(e) => setRememberMe(e.target.checked)}
              className="h-4 w-4 accent-[var(--color-accent)]"
            />
            {t("auth.rememberMe")}
          </label>
        )}

        {generalError && (
          <p role="alert" className="rounded-xl border border-red-500/30 bg-red-500/10 px-3 py-2 text-sm text-red-300">
            {generalError}
          </p>
        )}

        <button
          type="submit"
          disabled={busy}
          className="w-full rounded-xl bg-accent px-4 py-2.5 text-sm font-bold text-white transition-all enabled:hover:bg-accent-2 enabled:active:scale-[0.98] disabled:opacity-60"
        >
          {busy
            ? t("common.loading")
            : t(mode === "login" ? "auth.loginSubmit" : "auth.registerSubmit")}
        </button>
      </form>

      <p className="mt-4 text-center text-sm text-mute">
        <Link
          href={mode === "login" ? "/register" : "/login"}
          className="underline-offset-2 hover:text-ink hover:underline"
        >
          {t(mode === "login" ? "auth.toRegister" : "auth.toLogin")}
        </Link>
      </p>
    </div>
  );
}
