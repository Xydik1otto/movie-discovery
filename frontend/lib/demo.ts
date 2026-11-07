// Демо-дані: 8 фіктивних тайтлів з плейсхолдер-постерами.
// Використовуються, коли API недоступний — щоб UI жив без бекенду.

import type {
  DiscoverResponse,
  ListItem,
  ListKind,
  ListStats,
  OnboardingQuestion,
  SimilarItem,
  TitleCard,
  TitleDetail,
} from "./types";
import type { Lang } from "../i18n/I18nContext";
import { GENRES } from "./constants";

// FR-14: демо-провайдери перегляду (provider: sweettv|archive|tmdb-streaming).
// Компактна форма → TitleCard.watchProviders; повна (+region/isAvailable) → detail.providers.
type DemoWatch = { provider: string; kind: "free" | "subscription" | "rent" | "buy"; url: string };

const DEMO_WATCH: Record<number, DemoWatch[]> = {
  9001: [
    { provider: "sweettv", kind: "subscription", url: "https://sweet.tv/" },
    { provider: "archive", kind: "free", url: "https://archive.org/" },
  ],
  9002: [{ provider: "tmdb-streaming", kind: "subscription", url: "https://www.themoviedb.org/" }],
  9003: [{ provider: "archive", kind: "free", url: "https://archive.org/" }],
  9004: [
    { provider: "sweettv", kind: "subscription", url: "https://sweet.tv/" },
    { provider: "tmdb-streaming", kind: "rent", url: "https://www.themoviedb.org/" },
  ],
  9005: [{ provider: "tmdb-streaming", kind: "buy", url: "https://www.themoviedb.org/" }],
  9007: [{ provider: "tmdb-streaming", kind: "subscription", url: "https://www.themoviedb.org/" }],
  9008: [
    { provider: "archive", kind: "free", url: "https://archive.org/" },
    { provider: "sweettv", kind: "rent", url: "https://sweet.tv/" },
  ],
  // 9006 — свідомо без провайдерів: блок «Де подивитись» не рендериться.
};

interface DemoTitle {
  id: number;
  tmdbId: number;
  type: "movie" | "series";
  uk: { title: string; overview: string; genres: string[] };
  en: { title: string; overview: string; genres: string[] };
  originalTitle: string;
  year: number;
  countries: string[];
  ratingTmdb: number;
  ratingImdb: number;
  runtimeMin: number | null;
  seasons: number | null;
  director: string;
  actors: string[];
  keywords: string[];
}

const DEMO: DemoTitle[] = [
  {
    id: 9001, tmdbId: 45001, type: "movie",
    uk: {
      title: "Тінь над Дніпром",
      overview: "Детектив київського управління розслідує серію зникнень на Оболоні. Кожна нитка веде глибше у минуле міста.",
      genres: ["триллер", "детектив", "кримінал"],
    },
    en: {
      title: "Shadow Over the Dnipro",
      overview: "A Kyiv detective investigates a string of disappearances. Every thread pulls him deeper into the city's past.",
      genres: ["thriller", "mystery", "crime"],
    },
    originalTitle: "Shadow Over the Dnipro",
    year: 2024, countries: ["UA"], ratingTmdb: 7.4, ratingImdb: 7.1,
    runtimeMin: 118, seasons: null,
    director: "Олена Гладченко",
    actors: ["Максим Радів", "Тетяна Пелюх", "Андрій Мостренко"],
    keywords: ["детектив", "Київ", "нуар", "розслідування"],
  },
  {
    id: 9002, tmdbId: 45002, type: "series",
    uk: {
      title: "Нічні мисливці",
      overview: "Підрозділ особливих розслідувань виходить на слід мережі, яка працює лише вночі. Третій сезон — нові правила гри.",
      genres: ["триллер", "кримінал", "драма"],
    },
    en: {
      title: "Night Hunters",
      overview: "A special investigations unit tracks a network that only operates after dark. Season three changes the rules.",
      genres: ["thriller", "crime", "drama"],
    },
    originalTitle: "Night Hunters",
    year: 2022, countries: ["US"], ratingTmdb: 8.1, ratingImdb: 8.4,
    runtimeMin: 52, seasons: 3,
    director: "Карла Дженнінгс",
    actors: ["Дана Роуз", "Ілля Ковтун", "Марк Еверс"],
    keywords: ["поліція", "нуар", "серійний злочинець"],
  },
  {
    id: 9003, tmdbId: 45003, type: "movie",
    uk: {
      title: "Крига",
      overview: "Двоє рятівників заблукали в льодовиковій пустці. Щоб повернутись, треба довіряти одне одному більше, ніж власним голосам.",
      genres: ["драма", "пригоди"],
    },
    en: {
      title: "The Frost",
      overview: "Two rescuers are lost in the ice desert. Getting back means trusting each other more than their own voices.",
      genres: ["drama", "adventure"],
    },
    originalTitle: "The Frost",
    year: 2021, countries: ["IS", "SE"], ratingTmdb: 7.0, ratingImdb: 6.8,
    runtimeMin: 104, seasons: null,
    director: "Йонас Бйорнссон",
    actors: ["Сігрун Хафліадоттір", "Олена Верес"],
    keywords: ["виживання", "лід", "гори"],
  },
  {
    id: 9004, tmdbId: 45004, type: "series",
    uk: {
      title: "Голоси міста",
      overview: "Комунальний подкаст-студія Харкова стає несподіваним центром громади. Тепер у неї слухачі, кредити і дуже багато проблем.",
      genres: ["драма", "комедія"],
    },
    en: {
      title: "City Voices",
      overview: "A scrappy podcast studio becomes the unlikely heart of its neighborhood. Now it has listeners, debt and trouble.",
      genres: ["drama", "comedy"],
    },
    originalTitle: "City Voices",
    year: 2023, countries: ["UA"], ratingTmdb: 7.8, ratingImdb: 7.6,
    runtimeMin: 38, seasons: 2,
    director: "Ігор Шевчук",
    actors: ["Соломія Кравчик", "Денис Береговий", "Поліна Лисенко"],
    keywords: ["місто", "дружба", "медіа"],
  },
  {
    id: 9005, tmdbId: 45005, type: "movie",
    uk: {
      title: "Останній кадр",
      overview: "Кінооператор на межі відставки отримує останнє завдання: зняти портрет міста, якого вже не існує.",
      genres: ["драма", "мелодрама"],
    },
    en: {
      title: "The Last Frame",
      overview: "A cinematographer on the edge of retirement gets one last job: to film a city that no longer exists.",
      genres: ["drama", "romance"],
    },
    originalTitle: "The Last Frame",
    year: 2019, countries: ["FR"], ratingTmdb: 7.9, ratingImdb: 7.7,
    runtimeMin: 127, seasons: null,
    director: "Клер Моро",
    actors: ["Жан-Люк Паре", "Мілена Стоян"],
    keywords: ["кіно", "спогади", "Париж"],
  },
  {
    id: 9006, tmdbId: 45006, type: "movie",
    uk: {
      title: "Порох і лід",
      overview: "Колишній гірськолижник вимушений повернутись на трасу, яку так і не встиг завершити п'ять років тому.",
      genres: ["бойовик", "триллер"],
    },
    en: {
      title: "Powder and Ice",
      overview: "A former alpine racer is forced back onto the run he never finished five years ago.",
      genres: ["action", "thriller"],
    },
    originalTitle: "Powder and Ice",
    year: 2025, countries: ["GB"], ratingTmdb: 6.9, ratingImdb: 6.5,
    runtimeMin: 131, seasons: null,
    director: "Деклан Мерфі",
    actors: ["Рорі Колдвелл", "Інга Леміш"],
    keywords: ["спорт", "помста", "гори"],
  },
  {
    id: 9007, tmdbId: 45007, type: "series",
    uk: {
      title: "Тиха орбіта",
      overview: "Екіпаж дослідної станції на межі сонячної системи ловить сигнал, який не мав існувати. Четвертий сезон — контакт.",
      genres: ["фантастика", "драма"],
    },
    en: {
      title: "The Quiet Orbit",
      overview: "The crew of a research station at the edge of the solar system picks up a signal that should not exist. Season four: contact.",
      genres: ["sci-fi", "drama"],
    },
    originalTitle: "The Quiet Orbit",
    year: 2020, countries: ["US", "CA"], ratingTmdb: 8.3, ratingImdb: 8.6,
    runtimeMin: 48, seasons: 4,
    director: "Саманта Ортіс",
    actors: ["Ніколь Ейрс", "Тарас Гнатюк", "Джошуа Рід"],
    keywords: ["космос", "перший контакт", "станція"],
  },
  {
    id: 9008, tmdbId: 45008, type: "movie",
    uk: {
      title: "Серце степу",
      overview: "1920-ті. Молодий картограф їде мапувати степ і знаходить там цілий світ, якого не було на жодній мапі.",
      genres: ["історичний", "драма", "пригоди"],
    },
    en: {
      title: "Heart of the Steppe",
      overview: "1920s. A young cartographer sets out to map the steppe and finds a whole world missing from every map.",
      genres: ["history", "drama", "adventure"],
    },
    originalTitle: "Heart of the Steppe",
    year: 2018, countries: ["UA", "PL"], ratingTmdb: 7.6, ratingImdb: 7.4,
    runtimeMin: 142, seasons: null,
    director: "Марта Зарицька",
    actors: ["Богдан Сич", "Анна Вронська"],
    keywords: ["історія", "степ", "мандрівка"],
  },
];

export function demoCards(lang: Lang): TitleCard[] {
  return DEMO.map((d) => demoToCard(d, lang));
}

function demoToCard(d: DemoTitle, lang: Lang): TitleCard {
  const loc = d[lang];
  return {
    id: d.id,
    tmdbId: d.tmdbId,
    type: d.type,
    title: loc.title,
    originalTitle: d.originalTitle,
    year: d.year,
    genres: loc.genres,
    countries: d.countries,
    ratingTmdb: d.ratingTmdb,
    ratingImdb: d.ratingImdb,
    posterUrl: null, // → плейсхолдер-градієнт у TitleCard
    runtimeMin: d.runtimeMin,
    seasons: d.seasons,
    overview: loc.overview,
    watchProviders: DEMO_WATCH[d.id]?.map((w) => ({
      provider: w.provider,
      kind: w.kind,
      url: w.url,
    })),
  };
}

export function demoDetail(id: number, lang: Lang): TitleDetail | null {
  const d = DEMO.find((x) => x.id === id || x.tmdbId === id);
  if (!d) return null;
  const base = demoToCard(d, lang);
  return {
    ...base,
    overviewFull: d[lang].overview,
    actors: d.actors,
    director: d.director,
    keywords: d.keywords,
    backdropUrl: null,
    // "aqz-KE-bpKQ" — Big Buck Bunny (Blender Foundation), вільний для вбудовування.
    trailerYtId: d.id === 9003 ? "aqz-KE-bpKQ" : null,
    status: d.type === "series" ? "returning" : "released",
    originalLanguage: d.countries[0] ? d.countries[0].toLowerCase() : "en",
    providers: demoProviders(d.id),
  };
}

function demoProviders(id: number): TitleDetail["providers"] {
  // Повна форма за контрактом FR-14: {provider, kind, region, url, isAvailable}.
  return (DEMO_WATCH[id] ?? []).map((w) => ({
    provider: w.provider,
    kind: w.kind,
    url: w.url,
    region: "UA",
    isAvailable: true,
  }));
}

// Демо-пошук: фільтрація пулу за текстом/жанрами/роком/рейтингом.
export function demoSearch(
  opts: {
    q?: string;
    type?: string | null;
    genres?: string[];
    yearFrom?: number | null;
    yearTo?: number | null;
    minRating?: number | null;
    maxRating?: number | null;
    minRuntime?: number | null;
    maxRuntime?: number | null;
    country?: string | null;
    watchable?: boolean | null;
  },
  lang: Lang,
  page = 1,
  pageSize = 24,
): { items: TitleCard[]; total: number; page: number; pageSize: number } {
  let items = DEMO.map((d) => demoToCard(d, lang));
  const q = opts.q?.trim().toLowerCase();
  if (q) {
    items = items.filter((i) =>
      [i.title, i.originalTitle ?? "", (i.genres ?? []).join(" ")]
        .join(" ")
        .toLowerCase()
        .includes(q),
    );
  }
  if (opts.type) items = items.filter((i) => i.type === opts.type);
  if (opts.genres?.length) {
    items = items.filter((i) =>
      opts.genres!.some((g) => (i.genres ?? []).some((ig) => ig.toLowerCase().includes(g.toLowerCase()))),
    );
  }
  if (opts.yearFrom != null) items = items.filter((i) => (i.year ?? 0) >= opts.yearFrom!);
  if (opts.yearTo != null) items = items.filter((i) => (i.year ?? 0) <= opts.yearTo!);
  if (opts.minRating != null) {
    items = items.filter((i) => Math.max(i.ratingTmdb ?? 0, i.ratingImdb ?? 0) >= opts.minRating!);
  }
  if (opts.maxRating != null) {
    items = items.filter(
      (i) => Math.max(i.ratingTmdb ?? 0, i.ratingImdb ?? 0) <= opts.maxRating!,
    );
  }
  // Серіали без повнометражного runtime не виключаємо з фільтра тривалості.
  if (opts.minRuntime != null) {
    items = items.filter((i) => i.runtimeMin == null || i.runtimeMin >= opts.minRuntime!);
  }
  if (opts.maxRuntime != null) {
    items = items.filter((i) => i.runtimeMin == null || i.runtimeMin <= opts.maxRuntime!);
  }
  if (opts.country) items = items.filter((i) => (i.countries ?? []).includes(opts.country!));
  // FR-14: лише тайтли з доступними провайдерами перегляду.
  if (opts.watchable) items = items.filter((i) => (i.watchProviders?.length ?? 0) > 0);
  const total = items.length;
  const start = (page - 1) * pageSize;
  return { items: items.slice(start, start + pageSize), total, page, pageSize };
}

// Демо-схожість: детермінований «score» на основі спільних жанрів та епохи.
export function demoSimilar(seedIds: number[], lang: Lang, limit = 20): SimilarItem[] {
  const seeds = DEMO.filter((d) => seedIds.includes(d.tmdbId) || seedIds.includes(d.id));
  const seedGenres = new Set(seeds.flatMap((s) => s[lang].genres));
  const genreLabel = lang === "uk" ? "спільні жанри" : "shared genres";
  const yearLabel = lang === "uk" ? "близький рік" : "close year";
  return DEMO.filter((d) => !seedIds.includes(d.tmdbId) && !seedIds.includes(d.id))
    .map((d) => {
      const sharedGenres = d[lang].genres.filter((g) => seedGenres.has(g));
      const yearGap = Math.min(...seeds.map((s) => Math.abs(s.year - d.year)));
      const score = Math.min(
        0.95,
        0.35 + sharedGenres.length * 0.18 + (yearGap <= 5 ? 0.12 : 0) + (d.id % 7) * 0.02,
      );
      const reasons: string[] = [];
      if (sharedGenres.length) reasons.push(`${genreLabel}: ${sharedGenres.join(", ")}`);
      if (yearGap <= 5) reasons.push(yearLabel);
      return { ...demoToCard(d, lang), score, reasons };
    })
    .sort((a, b) => (b.score ?? 0) - (a.score ?? 0))
    .slice(0, limit);
}

// Демо-списки, щоб сторінка /lists жила без бекенду.
// «read» — манга; демо-пул манги не має, тому порожньо (без підмінки).
export function demoLists(kind: ListKind, lang: Lang): ListItem[] {
  if (kind === "watchlist") {
    return [9003, 9005, 9006].map((id) => ({ ...demoDetail(id, lang)!, rating: null }));
  }
  if (kind === "read" || kind === "disliked") return [];
  if (kind === "liked") {
    return [9002, 9007].map((id) => ({ ...demoDetail(id, lang)!, rating: null }));
  }
  return [
    { ...demoDetail(9001, lang)!, rating: 9 },
    { ...demoDetail(9002, lang)!, rating: 8 },
    { ...demoDetail(9007, lang)!, rating: 10 },
  ];
}

// --- FR-17/18: демо-статистика, discover-колода, онбординг-квіз ---

export function demoListStats(lang: Lang): ListStats {
  const watched = demoLists("watched", lang);
  const liked = demoLists("liked", lang);
  const ratings = watched.map((w) => w.rating).filter((r): r is number => r != null);
  const genreCount = new Map<string, number>();
  for (const w of watched) {
    for (const g of w.genres ?? []) {
      genreCount.set(g, (genreCount.get(g) ?? 0) + 1);
    }
  }
  const topGenres = [...genreCount.entries()]
    .sort((a, b) => b[1] - a[1])
    .slice(0, 5)
    .map(([genre]) => genre);
  return {
    watchedCount: watched.length,
    likedCount: liked.length,
    dislikedCount: 0,
    avgRating: ratings.length
      ? Math.round((ratings.reduce((a, b) => a + b, 0) / ratings.length) * 10) / 10
      : null,
    topGenres,
  };
}

// Демо-колода discover: усі демо-тайтли, окрім уже розіграних (excluded).
export function demoDiscover(lang: Lang, exclude: number[] = [], count = 10): DiscoverResponse {
  const items = DEMO.filter(
    (d) => !exclude.includes(d.id) && !exclude.includes(d.tmdbId),
  )
    .slice(0, count)
    .map((d) => demoToCard(d, lang));
  // Демо не знає профілю — чесний «safe» (популярні високооцінені).
  return { items, mode: "safe" };
}

// Статичні демо-питання онбордингу: 3 екрани постерів по жанрах + настрій + ера.
export function demoOnboardingQuestions(lang: Lang): OnboardingQuestion[] {
  const label = (g: { value: string; uk: string; en: string }) => (lang === "uk" ? g.uk : g.en);
  const posterGroups = [GENRES.slice(0, 6), GENRES.slice(6, 11), GENRES.slice(11, 16)];
  const posters: OnboardingQuestion[] = posterGroups.map((g, i) => ({
    id: `posters-${i + 1}`,
    type: "posters",
    title: null,
    options: g.map((x) => ({ id: x.value, label: label(x), value: x.value })),
  }));
  const moods: [string, string, string][] = [
    ["fun", "Щось весале й легке", "Something fun and light"],
    ["tense", "Напружене, на нервах", "Tense, on edge"],
    ["cozy", "Затишне, під плед", "Cozy, blanket-level"],
    ["sad", "Сумне, але чесне", "Sad but honest"],
    ["epic", "Епічне, велике", "Epic, large-scale"],
    ["mind", "Загадкове, для розуму", "Mysterious, brainy"],
  ];
  const eras: [string, string, string][] = [
    ["1960s", "1960-ті", "1960s"],
    ["1980s", "1980-ті", "1980s"],
    ["1990s", "1990-ті", "1990s"],
    ["2000s", "2000-ті", "2000s"],
    ["2010s", "2010-ті", "2010s"],
    ["2020s", "2020-ті", "2020s"],
  ];
  return [
    ...posters,
    {
      id: "mood",
      type: "mood",
      title: null,
      options: moods.map(([id, uk, en]) => ({ id, label: lang === "uk" ? uk : en, value: id })),
    },
    {
      id: "era",
      type: "era",
      title: null,
      options: eras.map(([id, uk, en]) => ({ id, label: lang === "uk" ? uk : en, value: id })),
    },
  ];
}
