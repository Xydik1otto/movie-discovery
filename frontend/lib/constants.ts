// Статичні довідники для фільтрів та бейджів. Локалізовані трійки uk/en/ru
// (рішення власника 10.11: російська — третя мова інтерфейсу).
import type { AudioLangCode, CategoryCode, ProviderKind } from "./types";
import type { Lang } from "../i18n/I18nContext";

// Форма довідникового елемента з локалізованими назвами всіх мов UI.
export type LocalizedOption = { value: string; uk: string; en: string; ru: string };

export const GENRES: LocalizedOption[] = [
  { value: "драма", uk: "драма", en: "drama", ru: "драма" },
  { value: "комедія", uk: "комедія", en: "comedy", ru: "комедия" },
  { value: "бойовик", uk: "бойовик", en: "action", ru: "боевик" },
  { value: "триллер", uk: "триллер", en: "thriller", ru: "триллер" },
  { value: "детектив", uk: "детектив", en: "mystery", ru: "детектив" },
  { value: "кримінал", uk: "кримінал", en: "crime", ru: "криминал" },
  { value: "фантастика", uk: "фантастика", en: "sci-fi", ru: "фантастика" },
  { value: "фентезі", uk: "фентезі", en: "fantasy", ru: "фэнтези" },
  { value: "жахи", uk: "жахи", en: "horror", ru: "ужасы" },
  { value: "пригоди", uk: "пригоди", en: "adventure", ru: "приключения" },
  { value: "анімація", uk: "анімація", en: "animation", ru: "анимация" },
  { value: "документальний", uk: "документальний", en: "documentary", ru: "документальный" },
  { value: "мелодрама", uk: "мелодрама", en: "romance", ru: "мелодрама" },
  { value: "історичний", uk: "історичний", en: "history", ru: "исторический" },
  { value: "музика", uk: "музика", en: "music", ru: "музыка" },
  { value: "військовий", uk: "військовий", en: "war", ru: "военный" },
];

// FR-19: країна виробника (ISO-2) — основні варіанти для /api/search?country=.
export const COUNTRY_OPTIONS: LocalizedOption[] = [
  { value: "US", uk: "США", en: "USA", ru: "США" },
  { value: "GB", uk: "Велика Британія", en: "United Kingdom", ru: "Великобритания" },
  { value: "JP", uk: "Японія", en: "Japan", ru: "Япония" },
  { value: "KR", uk: "Південна Корея", en: "South Korea", ru: "Южная Корея" },
  { value: "CN", uk: "Китай", en: "China", ru: "Китай" },
  { value: "UA", uk: "Україна", en: "Ukraine", ru: "Украина" },
  { value: "FR", uk: "Франція", en: "France", ru: "Франция" },
  { value: "DE", uk: "Німеччина", en: "Germany", ru: "Германия" },
  { value: "IT", uk: "Італія", en: "Italy", ru: "Италия" },
  { value: "ES", uk: "Іспанія", en: "Spain", ru: "Испания" },
  { value: "IN", uk: "Індія", en: "India", ru: "Индия" },
  { value: "CA", uk: "Канада", en: "Canada", ru: "Канада" },
  { value: "AU", uk: "Австралія", en: "Australia", ru: "Австралия" },
];

export const REGIONS: string[] = ["UA", "PL", "US", "GB", "DE"];

// Мови оригіналу (ISO 639-1) — для фільтра language.
export const LANGUAGES: LocalizedOption[] = [
  { value: "uk", uk: "українська", en: "Ukrainian", ru: "украинский" },
  { value: "en", uk: "англійська", en: "English", ru: "английский" },
  { value: "ja", uk: "японська", en: "Japanese", ru: "японский" },
  { value: "ko", uk: "корейська", en: "Korean", ru: "корейский" },
  { value: "fr", uk: "французька", en: "French", ru: "французский" },
  { value: "de", uk: "німецька", en: "German", ru: "немецкий" },
  { value: "es", uk: "іспанська", en: "Spanish", ru: "испанский" },
  { value: "it", uk: "італійська", en: "Italian", ru: "итальянский" },
  { value: "pl", uk: "польська", en: "Polish", ru: "польский" },
  // російської в списках мов НЕМА — свідоме рішення власника
  { value: "zh", uk: "китайська", en: "Chinese", ru: "китайский" },
  { value: "hi", uk: "гінді", en: "Hindi", ru: "хинди" },
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
// — значення зі схеми БД (titles.format), локалізовані підписи uk/en/ru.
export const ANIME_FORMATS: LocalizedOption[] = [
  { value: "tv", uk: "ТВ-серіал", en: "TV series", ru: "ТВ-сериал" },
  { value: "movie", uk: "Фільм", en: "Movie", ru: "Фильм" },
  { value: "ova", uk: "OVA", en: "OVA", ru: "OVA" },
  { value: "ona", uk: "ONA", en: "ONA", ru: "ONA" },
  { value: "special", uk: "Спешиал", en: "Special", ru: "Спешл" },
  { value: "manga", uk: "Манга", en: "Manga", ru: "Манга" },
  { value: "manhwa", uk: "Манхва", en: "Manhwa", ru: "Манхва" },
  { value: "one_shot", uk: "Ван-шот", en: "One-shot", ru: "Ван-шот" },
  { value: "novel", uk: "Ранобе", en: "Novel", ru: "Ранобэ" },
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
  lang: Lang,
): string | null {
  if (!value) return null;
  const f = ANIME_FORMATS.find((x) => x.value === value);
  return f ? f[lang] : value;
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
