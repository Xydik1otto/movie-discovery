"use client";

import { useEffect } from "react";
import { useRouter } from "next/navigation";
import { useAuth } from "@/lib/auth";
import AuthForm from "@/components/AuthForm";

// FR-16: реєстрація. Після успіху — на онбординг (квіз смаків).
export default function RegisterPage() {
  const { user, ready } = useAuth();
  const router = useRouter();

  useEffect(() => {
    if (ready && user) {
      router.replace(user.onboardingDone === false ? "/onboarding" : "/");
    }
  }, [ready, user, router]);

  return <AuthForm mode="register" />;
}
