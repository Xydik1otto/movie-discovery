"use client";

import { Suspense, useEffect } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { useI18n } from "@/i18n/I18nContext";
import { useAuth } from "@/lib/auth";
import AuthForm from "@/components/AuthForm";

// FR-16: сторінка входу. Вже залогінений — одразу на ціль/додому.
// useSearchParams вимагає Suspense-межі для статичного prerender (Next 15).
function LoginInner() {
  const { t } = useI18n();
  const { user, ready } = useAuth();
  const router = useRouter();
  const params = useSearchParams();
  const next = params.get("next");

  useEffect(() => {
    if (ready && user) router.replace(next && next.startsWith("/") ? next : "/");
  }, [ready, user, next, router]);

  // needLogin=1 → /discover пояснює, чому користувач тут.
  const needLogin = params.get("needLogin") === "1";
  // ?reset=1 → редірект із /reset-password: пароль змінено, чесне підтвердження.
  const resetDone = params.get("reset") === "1";

  return (
    <div>
      {resetDone && (
        <p
          role="status"
          className="mx-auto mb-4 max-w-sm rounded-xl border border-emerald-500/30 bg-emerald-500/10 px-4 py-2.5 text-sm text-emerald-300"
        >
          {t("auth.resetSuccess")}
        </p>
      )}
      {needLogin && (
        <p
          role="status"
          className="mx-auto mb-4 max-w-sm rounded-xl border border-accent/30 bg-accent/10 px-4 py-2.5 text-sm text-accent-2"
        >
          {t("auth.needLogin")}
        </p>
      )}
      <AuthForm mode="login" />
    </div>
  );
}

export default function LoginPage() {
  return (
    <Suspense
      fallback={<div className="py-10 text-center text-sm text-mute">…</div>}
    >
      <LoginInner />
    </Suspense>
  );
}
