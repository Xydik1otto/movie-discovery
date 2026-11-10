"use client";

// 10.11: підтвердження email за посиланням із листа.
// ?userId=&token= → авто-виклик POST /api/auth/confirm-email ОДИН раз на
// маунті (useRef-гвард — StrictMode-подвійний маунт не повторює POST).
// Стани: verifying → success («Пошту підтверджено», кнопка «Увійти») →
// invalid (400 invalid_token → «Посилання недійсне або застаріле» + resend
// через форму email) → error (мережа/5xx → «Спробувати ще»).

import { Suspense, useCallback, useEffect, useRef, useState } from "react";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { useI18n } from "@/i18n/I18nContext";
import { ApiError } from "@/lib/api";
import { confirmEmail } from "@/lib/auth";
import { EMAIL_RE, ResendEmail } from "@/components/AuthForm";

type ConfirmState = "verifying" | "success" | "invalid" | "error";

// Контракт: 400 = invalid_token (невідомий userId / прострочений токен).
// Інші статуси — мережа/збій сервера, не «недійсне посилання».
function isInvalidToken(e: unknown): boolean {
  return e instanceof ApiError && e.status === 400;
}

function ConfirmEmailInner() {
  const { t } = useI18n();
  const params = useSearchParams();
  const userId = params.get("userId") ?? "";
  const token = params.get("token") ?? "";
  // resend-форма для invalid-стану: у посиланні email немає, тому його
  // вводить користувач (авто-підстановка з ?email=, якщо бекенд додасть).
  const [resendEmail, setResendEmail] = useState(params.get("email") ?? "");
  const [state, setState] = useState<ConfirmState>(
    userId && token ? "verifying" : "invalid",
  );
  // Авто-виклик один раз: повторний монторинг (StrictMode) не повторює POST.
  const firedRef = useRef(false);

  const run = useCallback(async () => {
    if (!userId || !token) {
      setState("invalid");
      return;
    }
    setState("verifying");
    try {
      await confirmEmail(userId, token);
      setState("success");
    } catch (e) {
      setState(isInvalidToken(e) ? "invalid" : "error");
    }
  }, [userId, token]);

  useEffect(() => {
    if (firedRef.current) return;
    firedRef.current = true;
    void run();
  }, [run]);

  const resendValid = EMAIL_RE.test(resendEmail.trim());

  return (
    <div className="mx-auto max-w-sm">
      <h1 className="text-2xl font-extrabold tracking-tight sm:text-3xl">
        {t("auth.confirmTitle")}
      </h1>

      {state === "verifying" && (
        <p role="status" className="mt-4 text-sm text-mute">
          {t("auth.confirmVerifying")}
        </p>
      )}

      {state === "success" && (
        <div className="mt-4">
          <div className="rounded-xl border border-emerald-500/30 bg-emerald-500/10 px-4 py-3">
            <p className="text-sm font-bold text-emerald-300">
              {t("auth.confirmSuccess")}
            </p>
          </div>
          <Link
            href="/login"
            className="mt-4 block w-full rounded-xl bg-accent px-4 py-2.5 text-center text-sm font-bold text-white transition-all hover:bg-accent-2 active:scale-[0.98]"
          >
            {t("auth.login")}
          </Link>
        </div>
      )}

      {state === "invalid" && (
        <div className="mt-4">
          <div
            role="alert"
            className="rounded-xl border border-red-500/30 bg-red-500/10 px-4 py-3"
          >
            <p className="text-sm font-bold text-red-300">{t("auth.invalidLink")}</p>
            <p className="mt-1 text-xs text-red-300/80">{t("auth.confirmResendHint")}</p>
          </div>
          <div className="mt-4">
            <label
              htmlFor="confirm-resend-email"
              className="mb-1 block text-xs font-semibold text-mute"
            >
              {t("auth.email")}
            </label>
            <input
              id="confirm-resend-email"
              type="email"
              autoComplete="email"
              value={resendEmail}
              onChange={(e) => setResendEmail(e.target.value)}
              className="w-full rounded-xl border border-line bg-bg px-4 py-2.5 text-sm text-ink placeholder:text-mute/60 focus:border-accent focus:outline-none focus:ring-2 focus:ring-accent/30"
            />
            <div className="mt-3">
              <ResendEmail email={resendEmail.trim()} canSend={resendValid} />
            </div>
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
      )}

      {state === "error" && (
        <div className="mt-4">
          <div
            role="alert"
            className="rounded-xl border border-amber-500/30 bg-amber-500/10 px-4 py-3"
          >
            <p className="text-sm text-amber-300">{t("auth.err.generic")}</p>
          </div>
          <button
            type="button"
            onClick={() => void run()}
            className="mt-4 w-full rounded-xl border border-accent/50 bg-accent/10 px-4 py-2.5 text-sm font-bold text-accent-2 transition-colors hover:bg-accent/20"
          >
            {t("auth.retry")}
          </button>
        </div>
      )}
    </div>
  );
}

// useSearchParams вимагає Suspense-межі для статичного prerender (Next 15).
export default function ConfirmEmailPage() {
  return (
    <Suspense fallback={<div className="py-10 text-center text-sm text-mute">…</div>}>
      <ConfirmEmailInner />
    </Suspense>
  );
}
