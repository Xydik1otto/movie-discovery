using System.Text;

namespace MovieDiscovery.Infrastructure.Watchability;

/// <summary>
/// Нормалізація мов озвучки/субтитрів (UX-04, хвиля 2): усі джерела (JustWatch ISO-коди,
/// sweet.tv-слаги /ua-uk/movies/filters/audiotrack/{slug}, Megogo-лейбли «Переклад»,
/// легасі-коди watch_links ukr/russ/eng) → канонічні ISO-639-1 2-літерні коди
/// (uk, ru, en, pl, …), csv у детермінованому порядку (алфавіт).
/// ПРАВИЛО ЧЕСНОСТІ: невідома мова = ЗАВЖДИ null; ніколи не uk/ru «за замовчуванням»,
/// ніколи не вигадуємо мову, якої джерело не заявило. Чисті функції — покриті тестами.
/// </summary>
public static class WatchLanguages
{
    /// <summary>Синоніми → ISO-639-1. Ключі нижнім регістром; sweet-слаги, укр/рос/англ-лейбли, легасі-коди.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        // українська
        ["uk"] = "uk", ["ua"] = "uk", ["ukr"] = "uk", ["ukraine"] = "uk", ["ukrainian"] = "uk",
        ["ukrajinska"] = "uk", ["українська"] = "uk", ["украинская"] = "uk", ["укр"] = "uk",
        // російська
        ["ru"] = "ru", ["rus"] = "ru", ["russ"] = "ru", ["russian"] = "ru",
        ["russkiy"] = "ru", ["російська"] = "ru", ["русская"] = "ru", ["рос"] = "ru",
        // англійська
        ["en"] = "en", ["eng"] = "en", ["english"] = "en", ["angliyska"] = "en",
        ["англійська"] = "en", ["английский"] = "en",
        // інші, зустрічні в UA-джерелах
        ["pl"] = "pl", ["pol"] = "pl", ["polish"] = "pl", ["polska"] = "pl", ["польська"] = "pl", ["польский"] = "pl",
        ["de"] = "de", ["ger"] = "de", ["german"] = "de", ["nimetska"] = "de", ["німецька"] = "de", ["немецкий"] = "de",
        ["fr"] = "fr", ["fre"] = "fr", ["french"] = "fr", ["francuzka"] = "fr", ["французька"] = "fr", ["французский"] = "fr",
        ["es"] = "es", ["spa"] = "es", ["spanish"] = "es", ["ispanka"] = "es", ["іспанська"] = "es", ["испанский"] = "es",
        ["it"] = "it", ["ita"] = "it", ["italian"] = "it", ["italiyska"] = "it", ["італійська"] = "it", ["итальянский"] = "it",
        ["hu"] = "hu", ["hun"] = "hu", ["hungarian"] = "hu", ["ugorska"] = "hu", ["угорська"] = "hu", ["венгерский"] = "hu",
        ["cs"] = "cs", ["cze"] = "cs", ["czech"] = "cs", ["cheska"] = "cs", ["чеська"] = "cs", ["чешский"] = "cs",
        ["sk"] = "sk", ["slo"] = "sk", ["slovak"] = "sk", ["slovalska"] = "sk", ["словацька"] = "sk",
        ["tr"] = "tr", ["tur"] = "tr", ["turkish"] = "tr", ["turecka"] = "tr", ["турецька"] = "tr",
        ["ja"] = "ja", ["jpn"] = "ja", ["japanese"] = "ja", ["iaponska"] = "ja", ["японська"] = "ja", ["японский"] = "ja",
        ["ko"] = "ko", ["kor"] = "ko", ["korean"] = "ko", ["korejska"] = "ko", ["корейська"] = "ko",
        ["zh"] = "zh", ["zho"] = "zh", ["chi"] = "zh", ["chinese"] = "zh", ["kytaiska"] = "zh", ["китайська"] = "zh",
        ["ar"] = "ar", ["ara"] = "ar", ["arabic"] = "ar", ["arabska"] = "ar", ["арабська"] = "ar",
        ["he"] = "he", ["heb"] = "he", ["hebrew"] = "he", ["ievreiska"] = "he", ["іврит"] = "he",
        ["hi"] = "hi", ["hin"] = "hi", ["hindi"] = "hi",
        ["th"] = "th", ["tha"] = "th", ["thai"] = "th",
        ["id"] = "id", ["ind"] = "id", ["indonesian"] = "id",
        ["nl"] = "nl", ["dut"] = "nl", ["dutch"] = "nl", ["niderlandska"] = "nl", ["нідерландська"] = "nl",
        ["pt"] = "pt", ["por"] = "pt", ["portuguese"] = "pt", ["portugalska"] = "pt", ["португальська"] = "pt",
        ["ro"] = "ro", ["rum"] = "ro", ["romanian"] = "ro", ["rumunska"] = "ro", ["румунська"] = "ro",
        ["bg"] = "bg", ["bul"] = "bg", ["bulgarian"] = "bg", ["bolharska"] = "bg", ["болгарська"] = "bg",
        ["et"] = "et", ["est"] = "et", ["estonian"] = "et", ["estonska"] = "et", ["естонська"] = "et",
        ["lv"] = "lv", ["lav"] = "lv", ["latvian"] = "lv", ["latiska"] = "lv", ["латиська"] = "lv",
        ["lt"] = "lt", ["lit"] = "lt", ["lithuanian"] = "lt", ["lytovska"] = "lt", ["литовська"] = "lt",
        ["da"] = "da", ["dan"] = "da", ["danish"] = "da",
        ["sv"] = "sv", ["swe"] = "sv", ["swedish"] = "sv",
        ["fi"] = "fi", ["fin"] = "fi", ["finnish"] = "fi", ["finska"] = "fi", ["фінська"] = "fi",
        ["no"] = "no", ["nor"] = "no", ["norwegian"] = "no",
        ["el"] = "el", ["gre"] = "el", ["greek"] = "el", ["gretska"] = "el", ["грецька"] = "el",
        ["uk_original"] = "original", ["original"] = "original", ["multi"] = "multi",
    };

    /// <summary>Мовні групи фронт-панелі (UX-04): uk | ru | original | unknown.</summary>
    public const string GroupUk = "uk";
    public const string GroupRu = "ru";
    public const string GroupOriginal = "original";
    public const string GroupUnknown = "unknown";

    /// <summary>Нормалізація мовного csv ДО КЛЮЧА (єДИНА нормалізація для upsert-ключа,
    /// dedup-ключа і міграційного UNIQUE-індексу): токени → ISO-639-1 (lowercase), distinct,
    /// СОРТУВАННЯ — «ru,uk» і «uk,ru» дають ОДНУ сигнатуру «ru,uk»; невідомі токени
    /// відкидаються; все-невідоме/порожнє → порожня сигнатура «» (NULL у БД). Гарантує,
    /// що повторний sync з тим самим набором мов попаде в той самий рядок.</summary>
    public static string CanonicalKey(string? langsCsv)
    {
        if (string.IsNullOrWhiteSpace(langsCsv)) return string.Empty;
        var normalized = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var token in langsCsv.Split(','))
        {
            var n = Normalize(token);
            if (n is not null) normalized.Add(n);
        }
        return normalized.Count == 0 ? string.Empty : string.Join(",", normalized);
    }

    /// <summary>Один токен → ISO-639-1 (або original/multi); невідоме/порожнє → null.
    /// Приймає 2-літерні ISO-коди як є (lowercase latin), інакше — словник синонімів.
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var token = raw.Trim().ToLowerInvariant();
        if (Aliases.TryGetValue(token, out var mapped)) return mapped;
        // невідомий 2-літерний latin-код (ISO-подібний) — берегось вигадок: лише з довідника вище.
        // Тому НЕ повертаємо token як є — чесний null.
        return null;
    }

    /// <summary>
    /// Набір токенів → нормалізований csv («uk,ru»): нормалізація, distinct, сортування.
    /// Немає жодної відомої мови → null (невідома ≠ порожній список).
    /// </summary>
    public static string? NormalizeSet(IEnumerable<string?>? tokens)
    {
        if (tokens is null) return null;
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var t in tokens)
        {
            var n = Normalize(t);
            if (n is not null) set.Add(n);
        }
        return set.Count == 0 ? null : string.Join(",", set);
    }

    /// <summary>csv → список кодів (без нормалізації; для відображення/групування).</summary>
    public static List<string> ParseCsv(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return [];
        return csv.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }

    /// <summary>Сигнатура для дедупу/UNIQUE: null (невідома) → порожній рядок (детермінований ключ).
    /// Для КЛЮЧІВ (upsert/dedup/міграційний індекс) використовуйте CanonicalKey — нормалізована форма.</summary>
    public static string Signature(string? langs) => string.IsNullOrWhiteSpace(langs) ? string.Empty : langs.Trim();

    /// <summary>Об'єднання двох нормалізованих csv-наборів (null ігноруємо); обидва null → null.</summary>
    public static string? Union(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a)) return EmptyToNull(b);
        if (string.IsNullOrWhiteSpace(b)) return EmptyToNull(a);
        return NormalizeSet(ParseCsv(a).Concat(ParseCsv(b)));
    }

    private static string? EmptyToNull(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>
    /// Мовна ГРУПА для фронт-групування (uk|ru|original|unknown): пріоритет uk, далі ru,
    /// далі оригінал (original/multi або конкретна не-uk/ru мова озвучки).
    /// Субтитри теж рахуються: en-аудіо + uk-суб → «українською (субтитри)».
    /// </summary>
    public static string GroupOf(string? audioLangs, string? subLangs, string? dub)
    {
        var audio = ParseCsv(audioLangs);
        var subs = ParseCsv(subLangs);
        if (audio.Contains(GroupUk) || subs.Contains(GroupUk)) return GroupUk;
        if (audio.Contains(GroupRu) || subs.Contains(GroupRu)) return GroupRu;
        if (dub == "original" || audio.Contains("original") || audio.Contains("multi")) return GroupOriginal;
        if (audio.Count > 0) return GroupOriginal; // en/pl/ja… — оригінал/іноземною (чесніше, ніж «невідома»)
        return GroupUnknown;
    }
}
