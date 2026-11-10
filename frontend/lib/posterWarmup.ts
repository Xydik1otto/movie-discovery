// Прогрівання постерів (UX-медіа, hover-префетч «Наступна»): new Image().src по
// posterUrl перших N тайтлів наступної сторінки — браузер кещує зображення, тож
// після кліку постери малюються без очікування мережі. Пул із concurrency ≤6,
// сесійний дедуп по URL (кожен URL гріється рівно один раз за сесію).
//
// URL будує той самий posterUrlFull, що й PosterImage у TitleCard — інакше
// прогрів запросив би іншу адресу, ніж <img>, і був би марним. YouTube-thumbnail
// і missing_original відсікає сам posterUrlFull (FAKE_POSTER_MARKERS).
import { posterUrlFull } from "./constants";

const warmed = new Set<string>();

export function warmPosterImages(
  items: ReadonlyArray<unknown>,
  limit = 20,
  concurrency = 6,
): void {
  if (typeof window === "undefined" || typeof Image === "undefined") return;

  const urls: string[] = [];
  for (const it of items) {
    if (urls.length >= limit) break;
    const raw = (it as { posterUrl?: unknown } | null | undefined)?.posterUrl;
    if (typeof raw !== "string" || !raw) continue;
    const full = posterUrlFull(raw);
    if (!full || warmed.has(full)) continue;
    warmed.add(full); // маркуємо до запиту: повторний прогрів того самого URL не потрібен
    urls.push(full);
  }
  if (urls.length === 0) return;

  let started = 0;
  let active = 0;
  const pump = () => {
    while (started < urls.length && active < concurrency) {
      const url = urls[started++];
      active++;
      const img = new Image();
      img.decoding = "async";
      const done = () => {
        active--;
        pump(); // звільнений слот забирає наступний URL
      };
      img.onload = done;
      img.onerror = done;
      img.src = url;
    }
  };
  pump();
}
