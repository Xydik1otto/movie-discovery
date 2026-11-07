// Статичні довідники для фільтрів та бейджів. Локалізовані пари uk/en.
import type { ProviderKind } from "./types";

export const GENRES: { value: string; uk: string; en: string }[] = [
  { value: "драма", uk: "драма", en: "drama" },
  { value: "комедія", uk: "комедія", en: "comedy" },
  { value: "бойовик", uk: "бойовик", en: "action" },
  { value: "триллер", uk: "триллер", en: "thriller" },
  { value: "детектив", uk: "детектив", en: "mystery" },
  { value: "кримінал", uk: "кримінал", en: "crime" },
  { value: "фантастика", uk: "фантастика", en: "sci-fi" },
  { value: "фентезі", uk: "фентезі", en: "fantasy" },
  { value: "жахи", uk: "жахи", en: "horror" },
  { value: "пригоди", uk: "пригоди", en: "adventure" },
  { value: "анімація", uk: "анімація", en: "animation" },
  { value: "документальний", uk: "документальний", en: "documentary" },
  { value: "мелодрама", uk: "мелодрама", en: "romance" },
  { value: "історичний", uk: "історичний", en: "history" },
  { value: "музика", uk: "музика", en: "music" },
  { value: "військовий", uk: "військовий", en: "war" },
];

export const COUNTRIES: { value: string; uk: string; en: string }[] = [
  { value: "UA", uk: "Україна", en: "Ukraine" },
  { value: "US", uk: "США", en: "USA" },
  { value: "GB", uk: "Велика Британія", en: "United Kingdom" },
  { value: "FR", uk: "Франція", en: "France" },
  { value: "DE", uk: "Німеччина", en: "Germany" },
  { value: "ES", uk: "Іспанія", en: "Spain" },
  { value: "IT", uk: "Італія", en: "Italy" },
  { value: "PL", uk: "Польща", en: "Poland" },
  { value: "SE", uk: "Швеція", en: "Sweden" },
  { value: "DK", uk: "Данія", en: "Denmark" },
  { value: "IS", uk: "Ісландія", en: "Iceland" },
  { value: "JP", uk: "Японія", en: "Japan" },
  { value: "KR", uk: "Південна Корея", en: "South Korea" },
  { value: "IN", uk: "Індія", en: "India" },
  { value: "CA", uk: "Канада", en: "Canada" },
];

export const REGIONS: string[] = ["UA", "PL", "US", "GB", "DE"];

export const TITLE_TYPES: { value: "movie" | "series" | "show"; key: string }[] = [
  { value: "movie", key: "filters.movie" },
  { value: "series", key: "filters.series" },
  { value: "show", key: "filters.show" },
];

export const SORTS: {
  value: "relevance" | "rating" | "year" | "popularity";
  key: string;
}[] = [
  { value: "relevance", key: "filters.sort.relevance" },
  { value: "rating", key: "filters.sort.rating" },
  { value: "year", key: "filters.sort.year" },
  { value: "popularity", key: "filters.sort.popularity" },
];

export const PROVIDER_KINDS: { value: ProviderKind; key: string }[] = [
  { value: "stream", key: "providers.stream" },
  { value: "rent", key: "providers.rent" },
  { value: "buy", key: "providers.buy" },
  { value: "free", key: "providers.free" },
];

// Кольори бейджів провайдерів за kind.
export const PROVIDER_KIND_STYLE: Record<ProviderKind, string> = {
  stream: "bg-emerald-500/15 text-emerald-300 border-emerald-500/30",
  rent: "bg-amber-500/15 text-amber-300 border-amber-500/30",
  buy: "bg-violet-500/15 text-violet-300 border-violet-500/30",
  free: "bg-sky-500/15 text-sky-300 border-sky-500/30",
};

const TMDB_IMG = "https://image.tmdb.org";

// posterUrl з API може прийти повним URL або TMDB-шляхом /xxx.jpg.
export function posterUrlFull(posterUrl?: string | null, size = "w500"): string | null {
  if (!posterUrl) return null;
  if (posterUrl.startsWith("http")) return posterUrl;
  if (posterUrl.startsWith("/")) return `${TMDB_IMG}/t/p/${size}${posterUrl}`;
  return null;
}
