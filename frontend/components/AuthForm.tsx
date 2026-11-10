"use client";

// FR-16: спільна форма входу/реєстрації. Валідація пароля на клієнті
// (≥8 + цифра + велика), помилки бекенду — по полях (400 {errors:{...}}).
// 10.11: register має друге поле «Повторіть пароль» (клієнтський збіг до
// сабміту, кнопка disabled доки не збігаються); успіх реєстрації з
// emailConfirmationRequired → екран «Перевірте пошту» з resend; логін
// 403 email_not_confirmed → панель «Підтвердіть пошту» з resend; лінк
// «Забули пароль?» → /forgot-password.

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { useI18n } from "../i18n/I18nContext";
import { ApiError } from "../lib/api";
import {
  resendConfirmation,
  useAuth,
  type AuthFailure,
  type RegisterOutcome,
} from "../lib/auth";

type Mode = "login" | "register";

export const EMAIL_RE = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

type ResendState = "idle" | "busy" | "sent" | "rate_limited" | "error";

// Кнопка повторної відправки листа підтвердження: чесні стани — 429
// («Занадто часто — спробуйте пізніше»), збій («Не вдалося надіслати»);
// «надіслано» показуємо лише після реального 200. Використовується на екрані
// «Перевірте пошту», у панелі логіна та на сторінці confirm-email.
export function ResendEmail({
  email,
  canSend = true,
}: {
  email: string;
  canSend?: boolean;
}) {
  const { t } = useI18n();
  const [state, setState] = useState<ResendState>("idle");

  async function resend() {
    setState("busy");
    try {
      await resendConfirmation(email);
      setState("sent");
    } catch (e) {
      setState(e instanceof ApiError && e.status === 429 ? "rate_limited" : "error");
    }
  }

  return (
    <div>
      <button
        type="button"
        onClick={resend}
        disabled={state === "busy" || !canSend}
        className="w-full rounded-xl border border-accent/50 bg-accent/10 px-4 py-2.5 text-sm font-bold text-accent-2 transition-colors enabled:hover:bg-accent/20 disabled:opacity-60"
      >
        {state === "busy" ? t("common.loading") : t("auth.resendAgain")}
      </button>
      {state === "sent" && (
        <p role="status" className="mt-2 text-xs text-emerald-300">
          {t("auth.resendSent")}
        </p>
      )}
      {state === "rate_limited" && (
        <p role="alert" className="mt-2 text-xs text-amber-300">
          {t("auth.resendTooFrequent")}
        </p>
      )}
      {state === "error" && (
        <p role="alert" className="mt-2 text-xs text-red-400">
          {t("auth.resendError")}
        </p>
      )}
    </div>
  );
}

export default function AuthForm({ mode }: { mode: Mode }) {
  const { t } = useI18n();
  const { login, register } = useAuth();
  const router = useRouter();

  const [email, setEmail] = useState("");
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  // 10.11: друге поле «Повторіть пароль» (register) — збіг перевіряємо
  // клієнтськи ДО сабміту, кнопка заблокована доки паролі не збігаються.
  const [password2, setPassword2] = useState("");
  // Показ/приховання пароля (око) і «Запам'ятати мене» (галочка ввімкнена за замовчуванням).
  const [showPassword, setShowPassword] = useState(false);
  const [showPassword2, setShowPassword2] = useState(false);
  const [rememberMe, setRememberMe] = useState(true);
  const [busy, setBusy] = useState(false);
  // Екран «Перевірте пошту» після реєстрації з підтвердженням email (10.11).
  const [checkEmailFor, setCheckEmailFor] = useState<string | null>(null);
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
    // Збіг паролів — чесне повідомлення, без «прихованого» відхилення реєстрації.
    if (mode === "register" && password2 !== password)
      errs.password2 = t("auth.passwordsMismatch");
    setClientErrors(errs);
    return Object.keys(errs).length === 0;
  }

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    setServerErrors(null);
    if (!validate()) return;
    setBusy(true);
    try {
      if (mode === "login") {
        const failure = await login(email.trim(), password, rememberMe);
        if (failure) {
          setServerErrors(failure);
          return;
        }
      } else {
        const outcome: RegisterOutcome = await register(
          email.trim(),
          username.trim(),
          password,
        );
        if (!outcome.ok) {
          setServerErrors(outcome.failure ?? null);
          return;
        }
        // Новий бекенд вимагає підтвердження пошти → екран «Перевірте пошту»
        // замість редиректу (онбординг недосяжний до підтвердження).
        if (outcome.emailConfirmationRequired) {
          setCheckEmailFor(email.trim());
          return;
        }
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

  const fieldError = (
    field: "email" | "username" | "password" | "password2",
  ): string | null => {
    if (field === "password2") return clientErrors.password2 ?? null;
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
      case "rate_limited":
        // Codex-додаток: чесне повідомлення про ліміт (register 3/год/IP),
        // з ~N хв якщо бекенд дав retryAfterMinutes; без нього — «пізніше».
        return serverErrors.retryAfterMinutes != null
          ? t("auth.err.rateLimited", { n: serverErrors.retryAfterMinutes })
          : t("auth.err.rateLimitedGeneric");
      case "email_not_confirmed":
        // Панель «Підтвердіть пошту» рендериться окремим блоком — тут null.
        return null;
      default:
        // 400 без розпізнаних полів — загальна форма.
        return serverErrors.fieldErrors.email?.length ||
          serverErrors.fieldErrors.username?.length ||
          serverErrors.fieldErrors.password?.length
          ? null
          : t("auth.err.generic");
    }
  })();

  const inputCls = (field: "email" | "username" | "password" | "password2") =>
    `w-full rounded-xl border bg-bg px-4 py-2.5 text-sm text-ink placeholder:text-mute/60 focus:outline-none focus:ring-2 ${
      fieldError(field)
        ? "border-red-500/60 focus:border-red-500 focus:ring-red-500/30"
        : "border-line focus:border-accent focus:ring-accent/30"
    }`;

  // Екран «Перевірте пошту» — замінює форму після реєстрації з підтвердженням.
  if (mode === "register" && checkEmailFor) {
    return (
      <div className="mx-auto max-w-sm">
        <h1 className="text-2xl font-extrabold tracking-tight sm:text-3xl">
          {t("auth.checkEmail")}
        </h1>
        <p className="mt-2 text-sm text-mute">
          {t("auth.checkEmailText", { email: checkEmailFor })}
        </p>
        <div className="mt-6 rounded-2xl border border-line bg-panel p-4 sm:p-5">
          <ResendEmail email={checkEmailFor} />
        </div>
        <p className="mt-4 text-center text-sm text-mute">
          <Link
            href="/login"
            className="underline-offset-2 hover:text-ink hover:underline"
          >
            {t("auth.toLogin")}
          </Link>
        </p>
      </div>
    );
  }

  // Register: кнопка заблокована, доки паролі не збігаються (10.11).
  const passwordsDiffer = mode === "register" && password !== password2;
  // Живий підказувач під другим полем: різні непорожні паролі — одразу видно.
  const liveMismatch =
    mode === "register" && password2.length > 0 && password2 !== password;

  const eyeBtnCls =
    "absolute right-1.5 top-1/2 flex h-8 w-8 -translate-y-1/2 items-center justify-center rounded-lg text-base text-mute transition-colors hover:text-ink focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/30";

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
              className={eyeBtnCls}
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

        {mode === "register" && (
          <div>
            <label
              htmlFor="auth-password2"
              className="mb-1 block text-xs font-semibold text-mute"
            >
              {t("auth.password2")}
            </label>
            <div className="relative">
              <input
                id="auth-password2"
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
        )}

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

        {/* 403 email_not_confirmed — панель «Підтвердіть пошту» з resend (10.11). */}
        {mode === "login" && serverErrors?.kind === "email_not_confirmed" && (
          <div
            role="alert"
            className="space-y-2 rounded-xl border border-amber-500/30 bg-amber-500/10 px-4 py-3"
          >
            <p className="text-sm font-bold text-amber-200">
              {t("auth.emailNotConfirmed")}
            </p>
            <p className="text-xs text-amber-200/80">
              {t("auth.emailNotConfirmedText", { email: email.trim() })}
            </p>
            <ResendEmail email={email.trim()} />
          </div>
        )}

        {generalError && (
          <p role="alert" className="rounded-xl border border-red-500/30 bg-red-500/10 px-3 py-2 text-sm text-red-300">
            {generalError}
          </p>
        )}

        <button
          type="submit"
          disabled={busy || passwordsDiffer}
          className="w-full rounded-xl bg-accent px-4 py-2.5 text-sm font-bold text-white transition-all enabled:hover:bg-accent-2 enabled:active:scale-[0.98] disabled:opacity-60"
        >
          {busy
            ? t("common.loading")
            : t(mode === "login" ? "auth.loginSubmit" : "auth.registerSubmit")}
        </button>
      </form>

      {mode === "login" && (
        <p className="mt-2 text-center text-sm text-mute">
          <Link
            href="/forgot-password"
            className="underline-offset-2 hover:text-ink hover:underline"
          >
            {t("auth.forgotPassword")}
          </Link>
        </p>
      )}

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
