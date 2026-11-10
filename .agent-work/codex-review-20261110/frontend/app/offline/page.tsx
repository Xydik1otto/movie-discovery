import Link from "next/link";

export const metadata = { title: "Офлайн" };

// Офлайн-заглушка для PWA: показується, коли немає мережі до API/сторінки.
export default function OfflinePage() {
  return (
    <div className="flex min-h-[60vh] flex-col items-center justify-center gap-4 text-center">
      <span className="text-5xl" aria-hidden>
        📡
      </span>
      <h1 className="text-2xl font-bold">Немає з&apos;єднання</h1>
      <p className="max-w-md text-mute">
        Додаток офлайн. Перевір інтернет-з&apos;єднання — дані про фільми потребують доступу до
        сервера.
      </p>
      <Link
        href="/"
        className="rounded-xl bg-accent px-5 py-2.5 text-sm font-semibold text-white transition-opacity hover:opacity-90"
      >
        Спробувати знову
      </Link>
    </div>
  );
}
