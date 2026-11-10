// Статичні довідники для фільтрів та бейджів. Локалізовані пари uk/en.
import type { AudioLangCode, CategoryCode, ProviderKind } from "./types";

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

// FR-19: країна виробника (ISO-2) — основні варіанти для /api/search?country=.
export const COUNTRY_OPTIONS: { value: string; uk: string; en: string }[] = [
  { value: "US", uk: "США", en: "USA" },
  { value: "GB", uk: "Велика Британія", en: "United Kingdom" },
  { value: "JP", uk: "Японія", en: "Japan" },
  { value: "KR", uk: "Південна Корея", en: "South Korea" },
  { value: "CN", uk: "Китай", en: "China" },
  { value: "UA", uk: "Україна", en: "Ukraine" },
  { value: "FR", uk: "Франція", en: "France" },
  { value: "DE", uk: "Німеччина", en: "Germany" },
  { value: "IT", uk: "Італія", en: "Italy" },
  { value: "ES", uk: "Іспанія", en: "Spain" },
  { value: "IN", uk: "Індія", en: "India" },
  { value: "CA", uk: "Канада", en: "Canada" },
  { value: "AU", uk: "Австралія", en: "Australia" },
];

export const REGIONS: string[] = ["UA", "PL", "US", "GB", "DE"];

// Мови оригіналу (ISO 639-1) — для фільтра language.
export const LANGUAGES: { value: string; uk: string; en: string }[] = [
  { value: "uk", uk: "українська", en: "Ukrainian" },
  { value: "en", uk: "англійська", en: "English" },
  { value: "ja", uk: "японська", en: "Japanese" },
  { value: "ko", uk: "корейська", en: "Korean" },
  { value: "fr", uk: "французька", en: "French" },
  { value: "de", uk: "німецька", en: "German" },
  { value: "es", uk: "іспанська", en: "Spanish" },
  { value: "it", uk: "італійська", en: "Italian" },
  { value: "pl", uk: "польська", en: "Polish" },
  // російської в списках мов НЕМА — свідоме рішення власника
  { value: "zh", uk: "китайська", en: "Chinese" },
  { value: "hi", uk: "гінді", en: "Hindi" },
];

export const TITLE_TYPES: { value: "movie" | "series" | "show"; key: string }[] = [
  { value: "movie", key: "filters.movie" },
  { value: "series", key: "filters.series" },
  { value: "show", key: "filters.show" },
];

// FR-19: віртуальні категорії (unified-схема titles) — значення = бекенд-коди
// /api/search?category=. tvshow — це телешоу (reality/talk/game-show),
// cartoon — уся анімація крім аніме, dorama — KR/CN/JP-драми.
export const CATEGORY_OPTIONS: { value: CategoryCode; key: string }[] = [
  { value: "movie", key: "filters.movie" },
  { value: "series", key: "filters.series" },
  { value: "tvshow", key: "filters.show" },
  { value: "cartoon", key: "filters.cartoon" },
  { value: "dorama", key: "filters.dorama" },
  { value: "anime", key: "filters.anime" },
  { value: "manga", key: "filters.manga" },
];

// FR-19: мова аудіо/озвучки в наявних провайдерах (watch_links.audio_lang).
// «Російська» тут — КОНТЕНТ-фільтр озвучок, не мова інтерфейсу (uk/en).
export const AUDIO_LANG_OPTIONS: { value: AudioLangCode; key: string }[] = [
  { value: "ukr", key: "filters.audioLang.ukr" },
  { value: "russ", key: "filters.audioLang.russ" },
  { value: "eng", key: "filters.audioLang.eng" },
  { value: "jpn", key: "filters.audioLang.jpn" },
  { value: "kor", key: "filters.audioLang.kor" },
  { value: "zho", key: "filters.audioLang.zho" },
  { value: "original", key: "filters.audioLang.original" },
  { value: "multi", key: "filters.audioLang.multi" },
];

// FR-19: мінімальна якість watch_links.quality (max height, px).
export const QUALITY_OPTIONS: { value: number; key: string }[] = [
  { value: 480, key: "quality.480" },
  { value: 720, key: "quality.720" },
  { value: 1080, key: "quality.1080" },
  { value: 2160, key: "quality.2160" },
];

export const SORTS: {
  value: "relevance" | "rating" | "imdb" | "votes" | "year" | "popularity";
  key: string;
}[] = [
  { value: "relevance", key: "filters.sort.relevance" },
  { value: "rating", key: "filters.sort.rating" },
  { value: "imdb", key: "filters.sort.imdb" },
  { value: "votes", key: "filters.sort.votes" },
  { value: "year", key: "filters.sort.year" },
  { value: "popularity", key: "filters.sort.popularity" },
];

export const PROVIDER_KINDS: { value: ProviderKind; key: string }[] = [
  { value: "stream", key: "providers.stream" },
  { value: "subscription", key: "watch.subscription" },
  { value: "rent", key: "providers.rent" },
  { value: "buy", key: "providers.buy" },
  { value: "free", key: "providers.free" },
];

// Кольори бейджів провайдерів за kind (сторінка тайтла).
export const PROVIDER_KIND_STYLE: Record<ProviderKind, string> = {
  stream: "bg-emerald-500/15 text-emerald-300 border-emerald-500/30",
  subscription: "bg-emerald-500/15 text-emerald-300 border-emerald-500/30",
  rent: "bg-amber-500/15 text-amber-300 border-amber-500/30",
  buy: "bg-violet-500/15 text-violet-300 border-violet-500/30",
  free: "bg-sky-500/15 text-sky-300 border-sky-500/30",
  ads: "bg-orange-500/15 text-orange-300 border-orange-500/30",
};

// Кольори маленьких бейджів watchProviders у кутку картки (FR-14):
// темна підкладка + яскравий текст — читабельно поверх постера.
export const WATCH_KIND_BADGE_STYLE: Record<ProviderKind, string> = {
  stream: "border-line bg-black/60 text-white/90",
  subscription: "border-emerald-400/40 bg-black/60 text-emerald-200",
  rent: "border-amber-400/40 bg-black/60 text-amber-200",
  buy: "border-violet-400/40 bg-black/60 text-violet-200",
  free: "border-sky-400/40 bg-black/60 text-sky-200",
  ads: "border-orange-400/40 bg-black/60 text-orange-200",
};

// Формати аніме (tv/movie/ova/ona/special) та манги (manga/manhwa/one_shot/novel)
// — значення зі схеми БД (titles.format), локалізовані підписи uk/en.
export const ANIME_FORMATS: { value: string; uk: string; en: string }[] = [
  { value: "tv", uk: "ТВ-серіал", en: "TV series" },
  { value: "movie", uk: "Фільм", en: "Movie" },
  { value: "ova", uk: "OVA", en: "OVA" },
  { value: "ona", uk: "ONA", en: "ONA" },
  { value: "special", uk: "Спешиал", en: "Special" },
  { value: "manga", uk: "Манга", en: "Manga" },
  { value: "manhwa", uk: "Манхва", en: "Manhwa" },
  { value: "one_shot", uk: "Ван-шот", en: "One-shot" },
  { value: "novel", uk: "Ранобе", en: "Novel" },
];

// Підмножина для чіпів фільтра формату на сторінці /manga.
export const ANIME_MEDIA_FORMATS = ANIME_FORMATS.filter((f) =>
  ["tv", "movie", "ova", "ona", "special"].includes(f.value),
);

export const MANGA_FORMATS = ANIME_FORMATS.filter((f) =>
  ["manga", "manhwa", "one_shot", "novel"].includes(f.value),
);

// Локалізований підпис формату; невідоме значення показуємо «як є».
export function formatLabel(
  value: string | null | undefined,
  lang: "uk" | "en",
): string | null {
  if (!value) return null;
  const f = ANIME_FORMATS.find((x) => x.value === value);
  return f ? (lang === "uk" ? f.uk : f.en) : value;
}

const TMDB_IMG = "https://image.tmdb.org";

// UX-02 cheap-guard: YouTube-thumbnail (i.ytimg.com / img.youtube.com) — це прев'ю ВІДЕО,
// а не постер тайтла; shikimori missing_original.jpg — службова заглушка джерела. Такі
// URL могли залишились у БД/кешах — показувати їх як постер заборонено, тому posterUrlFull
// повертає null (метадані відсутні) → PosterImage показує чесну заглушку з назвою.
const FAKE_POSTER_MARKERS = ["i.ytimg.com", "img.youtube.com", "missing_original.jpg"];

// posterUrl з API може прийти повним URL або TMDB-шляхом /xxx.jpg.
export function posterUrlFull(posterUrl?: string | null, size = "w500"): string | null {
  if (!posterUrl) return null;
  if (posterUrl.startsWith("http")) {
    if (FAKE_POSTER_MARKERS.some((m) => posterUrl.includes(m))) return null; // фейковий «постер»
    return posterUrl;
  }
  if (posterUrl.startsWith("/")) return `${TMDB_IMG}/t/p/${size}${posterUrl}`;
  return null;
}
