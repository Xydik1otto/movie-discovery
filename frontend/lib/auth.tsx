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
import { ApiError, getMe, loginUser, logoutUser, registerUser } from "./api";
import type { AuthFieldErrors, AuthUser } from "./types";

export type AuthErrorKind = "credentials" | "lockout" | "unavailable" | "generic";

export interface AuthFailure {
  kind: AuthErrorKind;
  fieldErrors: AuthFieldErrors;
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
  ) => Promise<AuthFailure | null>;
  logout: () => Promise<void>;
}

const AuthContext = createContext<AuthContextValue | null>(null);

// {errors: {Email: ["..."], password: "..."}} → нормалізовані fieldErrors.
function fieldErrorsFromBody(body: unknown): AuthFieldErrors {
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

  const register = useCallback(
    async (email: string, username: string, password: string): Promise<AuthFailure | null> => {
      try {
        await registerUser(email, username, password);
        // Реєстрація може не логінити автоматично — пробуємо увійти тими самими
        // даними; якщо вийшло, одразу ведемо на онбординг.
        try {
          await loginUser(email, password);
        } catch {
          // Автологін необов'язковий — бекенд міг сам залогінити при register.
        }
        await refresh();
        return null;
      } catch (e) {
        return failureFrom(e);
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
