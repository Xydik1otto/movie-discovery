"use client";

// Персональні рекомендації Головної: секція «Рекомендовано для вас».
// Рендериться ЛИШЕ для залогінених (useAuth — той самий контекст, що в Header):
// гость/401/порожня відповідь/помилка → секція НЕ рендериться взагалі
// (без вічного скелетона). Картки — той самий TitleCard, що в similar-рядку;
// горизонтальний скрол-ряд (overflow-x-auto + no-scrollbar, як у ThemeTabs/Header).
// Запит — лише на маунті залогіненого юзера; повторне з'явлення юзера (login у сусідній
// вкладці) тягне свіжий запит завдяки user-залежності ефекту.

import { useEffect, useState } from "react";
import { useAuth } from "../lib/auth";
import { getHomeRecommendations } from "../lib/api";
import type { TitleCard as TitleCardType } from "../lib/types";
import { useI18n } from "../i18n/I18nContext";
import TitleCard from "./TitleCard";

export default function PersonalReco() {
  const { user, ready } = useAuth();
  const { t, lang } = useI18n();
  // null = «секції нема» (гість / помилка / 401 / ще не завантажено); [] → теж ховаємо.
  const [items, setItems] = useState<TitleCardType[] | null>(null);
  const [reason, setReason] = useState<string | null>(null);

  useEffect(() => {
    if (!ready || !user) {
      setItems(null);
      return;
    }
    let alive = true;
    getHomeRecommendations(lang, 20)
      .then((r) => {
        if (!alive) return;
        setItems(Array.isArray(r.items) ? r.items : []);
        setReason(r.reason ?? null);
      })
      .catch(() => {
        // 401 (сесія скінчилась)/мережа/5xx — чесно ховаємо секцію, без скелетона назавжди.
        if (alive) setItems(null);
      });
    return () => {
      alive = false;
    };
  }, [ready, user, lang]);

  if (!items || items.length === 0) return null;

  return (
    <section className="mb-6" aria-label={t("home.forYou")}>
      <div className="mb-2 flex flex-wrap items-baseline justify-between gap-1">
        <h2 className="text-lg font-bold tracking-tight">{t("home.forYou")}</h2>
        {reason && <p className="text-xs text-mute">{reason}</p>}
      </div>
      <div className="no-scrollbar -mx-1 flex gap-3 overflow-x-auto px-1 pb-2">
        {items.map((item, i) => (
          <div key={item.id} className="w-[128px] shrink-0 sm:w-[148px]">
            <TitleCard item={item} eager={i < 4} />
          </div>
        ))}
      </div>
    </section>
  );
}
