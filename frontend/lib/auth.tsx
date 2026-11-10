"use client";

// FR-16: auth-стейт застосунку. Один GET /api/auth/me на завантаження;
// недоступність API — м'який фолбек (анонім, apiUp=false), без аварій.
// Cookie ходять через credentials: "include" у lib/api.ts request().

import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from "react";
import {
  API_URL,
  ApiError,
  ApiUnavailableError,
  getMe,
  loginUser,
  logoutUser,
  registerUser,
} from "./api";
import type { AuthFieldErrors, AuthUser } from "./types";

// --- 10.11: підтвердження пошти, resend, forgot/reset password ---
// lib/api.ts поза зоною цього агента, тому request-хелпер тут локальний —
// з тією самою семантикою (credentials: include, JSON, ApiError із тілом).

async function authRequest<T>(path: string, init?: RequestInit): Promise<T> {
  let res: Response;
  try {
    res = await fetch(`${API_URL}${path}`, {
      ...init,
      credentials: "include",
      headers: { "Content-Type": "application/json", ...(init?.headers ?? {}) },
      cache: "no-store",
      signal: AbortSignal.timeout(25_000),
    });
  } catch {
    throw new ApiUnavailableError();
  }
  if (!res.ok) {
    let body: unknown = null;
    try {
      const text = await res.text();
      body = text ? JSON.parse(text) : null;
    } catch {
      body = null;
    }
    throw new ApiError(res.status, `${res.status} ${res.statusText}`, body);
  }
  const text = await res.text();
  return (text ? JSON.parse(text) : null) as T;
}

// POST /api/auth/confirm-email {userId, token} → 200 {confirmed:true} | 400 invalid_token.
export async function confirmEmail(
  userId: string,
  token: string,
): Promise<{ confirmed?: boolean }> {
  const r = await authRequest<{ confirmed?: boolean } | null>("/api/auth/confirm-email", {
    method: "POST",
    body: JSON.stringify({ userId, token }),
  });
  return r ?? {};
}

// POST /api/auth/resend-confirmation {email} → завжди 200; 429 — зловживання
// (UI показує чесне «Занадто часто», а не мовчить).
export async function resendConfirmation(email: string): Promise<void> {
  await authRequest<void>("/api/auth/resend-confirmation", {
    method: "POST",
    body: JSON.stringify({ email }),
  });
}

// POST /api/auth/forgot-password {email} → завжди 200 — чесний текст
// «якщо акаунт існує — лист надіслано» (існування акаунта не розкриваємо).
export async function forgotPassword(email: string): Promise<void> {
  await authRequest<void>("/api/auth/forgot-password", {
    method: "POST",
    body: JSON.stringify({ email }),
  });
}

// POST /api/auth/reset-password {userId, token, newPassword} → 200 |
// 400 {error, errors?} — errors (Identity-політика) показуємо списком у UI.
export async function resetPassword(
  userId: string,
  token: string,
  newPassword: string,
): Promise<void> {
  await authRequest<void>("/api/auth/reset-password", {
    method: "POST",
    body: JSON.stringify({ userId, token, newPassword }),
  });
}

export type AuthErrorKind =
  | "credentials"
  | "lockout"
  | "unavailable"
  | "rate_limited"
  // 403 {error:"email_not_confirmed"} — логін непідтвердженого акаунта (контракт 10.11).
  | "email_not_confirmed"
  | "generic";

export interface AuthFailure {
  kind: AuthErrorKind;
  fieldErrors: AuthFieldErrors;
  // Codex-додаток: 429 register-лімітера — чесне «через ~N хв» (backend дає retryAfterMinutes).
  retryAfterMinutes?: number | null;
}

// Результат реєстрації: ok + прапорець «потрібне підтвердження пошти»
// ({emailConfirmationRequired:true} — новий бекенд) або failure.
export interface RegisterOutcome {
  ok: boolean;
  emailConfirmationRequired?: boolean;
  failure?: AuthFailure;
}

interface AuthContextValue {
  user: AuthUser | null;
  ready: boolean;
  apiUp: boolean;
  // MD-07/MD-08: сервер не підтвердив logout (напр., API недоступний) —
  // користувач лишається залогіненим локально, UI показує чесне повідомлення.
  logoutFailed: boolean;
  refresh: () => Promise<void>;
  login: (
    email: string,
    password: string,
    rememberMe?: boolean,
  ) => Promise<AuthFailure | null>;
  register: (
    email: string,
    username: string,
    password: string,
  ) => Promise<RegisterOutcome>;
  logout: () => Promise<void>;
}

const AuthContext = createContext<AuthContextValue | null>(null);

// {errors: {Email: ["..."], password: "..."}} → нормалізовані fieldErrors.
// Експорт: reset-password показує помилки Identity-політики списком (10.11).
export function fieldErrorsFromBody(body: unknown): AuthFieldErrors {
  const out: AuthFieldErrors = {};
  if (!body || typeof body !== "object") return out;
  const raw = (body as { errors?: unknown }).errors ?? body;
  if (!raw || typeof raw !== "object") return out;
  for (const [key, value] of Object.entries(raw as Record<string, unknown>)) {
    const k = key.toLowerCase();
    const list = Array.isArray(value)
      ? value.map(String)
      : typeof value === "string"
        ? [value]
        : [];
    if (!list.length) continue;
    if (k.includes("email")) out.email = [...(out.email ?? []), ...list];
    else if (k.includes("username")) out.username = [...(out.username ?? []), ...list];
    else if (k.includes("password")) out.password = [...(out.password ?? []), ...list];
    else out.general = [...(out.general ?? []), ...list];
  }
  return out;
}

function failureFrom(e: unknown): AuthFailure {
  if (e instanceof Error && e.name === "ApiUnavailableError") {
    return { kind: "unavailable", fieldErrors: {} };
  }
  if (e instanceof ApiError) {
    if (e.status === 401) return { kind: "credentials", fieldErrors: {} };
    if (e.status === 423) return { kind: "lockout", fieldErrors: {} };
    // 403 email_not_confirmed — окремий чесний стан: панель «Підтвердіть пошту»
    // з кнопкою resend, а не загальне «Не вдалося».
    if (
      e.status === 403 &&
      (e.body as { error?: unknown } | null)?.error === "email_not_confirmed"
    ) {
      return { kind: "email_not_confirmed", fieldErrors: {} };
    }
    // 429 — ліміт бекенд-лімітера (register 3/год, login 5/хв). Бекенд повертає
    // розпізнаване {error:"rate_limited", retryAfterMinutes}; чесне повідомлення
    // замість загального «Не вдалося» (юзер інакше стукає марно).
    if (e.status === 429) {
      const body = e.body as { retryAfterMinutes?: unknown } | null;
      const minutes =
        typeof body?.retryAfterMinutes === "number" && body.retryAfterMinutes > 0
          ? body.retryAfterMinutes
          : null;
      return { kind: "rate_limited", fieldErrors: {}, retryAfterMinutes: minutes };
    }
    if (e.status === 400) return { kind: "generic", fieldErrors: fieldErrorsFromBody(e.body) };
  }
  return { kind: "generic", fieldErrors: {} };
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<AuthUser | null>(null);
  const [ready, setReady] = useState(false);
  const [apiUp, setApiUp] = useState(true);
  // MD-07/MD-08: честність logout — сервер не підтвердив вихід → повідомлення.
  const [logoutFailed, setLogoutFailed] = useState(false);

  const refresh = useCallback(async () => {
    try {
      const u = await getMe();
      setUser(u);
      setApiUp(true);
    } catch {
      // API недоступний — анонімний режим, без різких повідомлень.
      setUser(null);
      setApiUp(false);
    } finally {
      setReady(true);
    }
  }, []);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  const login = useCallback(
    async (email: string, password: string, rememberMe = true): Promise<AuthFailure | null> => {
      try {
        const r = await loginUser(email, password, rememberMe);
        if (r.user) setUser(r.user);
        else await refresh();
        setApiUp(true);
        return null;
      } catch (e) {
        return failureFrom(e);
      }
    },
    [refresh],
  );

  // Реєстрація: новий бекенд повертає {emailConfirmationRequired:true} —
  // автологін тоді НЕ пробуємо (логін непідтвердженого дає 403
  // email_not_confirmed), UI показує екран «Перевірте пошту».
  // Старий бекенд (без поля) — як і було: пробуємо увійти тими самими даними.
  const register = useCallback(
    async (
      email: string,
      username: string,
      password: string,
    ): Promise<RegisterOutcome> => {
      try {
        // registerUser у lib/api.ts (поза зоною) типізує лише userId —
        // нове опційне поле читаємо приведенням типу (несумісності немає).
        const r = (await registerUser(email, username, password)) as {
          emailConfirmationRequired?: boolean;
        };
        const needsConfirm = r.emailConfirmationRequired === true;
        if (!needsConfirm) {
          try {
            await loginUser(email, password);
          } catch {
            // Автологін необов'язковий — бекенд міг сам залогінити при register.
          }
        }
        await refresh();
        return { ok: true, emailConfirmationRequired: needsConfirm };
      } catch (e) {
        return { ok: false, failure: failureFrom(e) };
      }
    },
    [refresh],
  );

  const logout = useCallback(async () => {
    setLogoutFailed(false);
    try {
      await logoutUser();
      // Сервер підтвердив вихід (200/204) — локально теж розлогінюємось.
      setUser(null);
    } catch (e) {
      if (e instanceof ApiError && e.status === 401) {
        // Сесія вже закрита на сервері — локальний вихід коректний.
        setUser(null);
        return;
      }
      // MD-07: сервер НЕ підтвердив вихід (API недоступний / 5xx) —
      // НЕ удаваємо завершення серверної сесії: користувач лишається
      // залогіненим локально, UI показує чесне повідомлення.
      setLogoutFailed(true);
    }
  }, []);

  const value = useMemo(
    () => ({ user, ready, apiUp, logoutFailed, refresh, login, register, logout }),
    [user, ready, apiUp, logoutFailed, refresh, login, register, logout],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth must be used inside <AuthProvider>");
  return ctx;
}
