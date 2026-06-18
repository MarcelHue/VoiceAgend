namespace VoiceAgend.App.Services;

/// <summary>
/// Statische ISO-639-1 → Anzeigename-Map für die von Whisper unterstützten Sprachen.
/// Bewusst als C#-Datei statt in strings.*.json: ~99 Einträge × 2 Sprachen würden die
/// Locale-JSONs aufblähen und unterliegen dort der Anführungszeichen-Regel. de/en/Auto
/// werden weiterhin über die normalen Locale-Keys lokalisiert; diese Map liefert die
/// Namen der zusätzlich aktivierbaren Sprachen.
/// </summary>
public static class LanguageNames
{
    // (Deutscher Name, Englischer Name) je ISO-Code.
    private static readonly Dictionary<string, (string De, string En)> Map = new()
    {
        ["en"] = ("Englisch", "English"),
        ["zh"] = ("Chinesisch", "Chinese"),
        ["de"] = ("Deutsch", "German"),
        ["es"] = ("Spanisch", "Spanish"),
        ["ru"] = ("Russisch", "Russian"),
        ["ko"] = ("Koreanisch", "Korean"),
        ["fr"] = ("Französisch", "French"),
        ["ja"] = ("Japanisch", "Japanese"),
        ["pt"] = ("Portugiesisch", "Portuguese"),
        ["tr"] = ("Türkisch", "Turkish"),
        ["pl"] = ("Polnisch", "Polish"),
        ["ca"] = ("Katalanisch", "Catalan"),
        ["nl"] = ("Niederländisch", "Dutch"),
        ["ar"] = ("Arabisch", "Arabic"),
        ["sv"] = ("Schwedisch", "Swedish"),
        ["it"] = ("Italienisch", "Italian"),
        ["id"] = ("Indonesisch", "Indonesian"),
        ["hi"] = ("Hindi", "Hindi"),
        ["fi"] = ("Finnisch", "Finnish"),
        ["vi"] = ("Vietnamesisch", "Vietnamese"),
        ["he"] = ("Hebräisch", "Hebrew"),
        ["uk"] = ("Ukrainisch", "Ukrainian"),
        ["el"] = ("Griechisch", "Greek"),
        ["ms"] = ("Malaiisch", "Malay"),
        ["cs"] = ("Tschechisch", "Czech"),
        ["ro"] = ("Rumänisch", "Romanian"),
        ["da"] = ("Dänisch", "Danish"),
        ["hu"] = ("Ungarisch", "Hungarian"),
        ["ta"] = ("Tamil", "Tamil"),
        ["no"] = ("Norwegisch", "Norwegian"),
        ["th"] = ("Thailändisch", "Thai"),
        ["ur"] = ("Urdu", "Urdu"),
        ["hr"] = ("Kroatisch", "Croatian"),
        ["bg"] = ("Bulgarisch", "Bulgarian"),
        ["lt"] = ("Litauisch", "Lithuanian"),
        ["la"] = ("Latein", "Latin"),
        ["mi"] = ("Maori", "Maori"),
        ["ml"] = ("Malayalam", "Malayalam"),
        ["cy"] = ("Walisisch", "Welsh"),
        ["sk"] = ("Slowakisch", "Slovak"),
        ["te"] = ("Telugu", "Telugu"),
        ["fa"] = ("Persisch", "Persian"),
        ["lv"] = ("Lettisch", "Latvian"),
        ["bn"] = ("Bengalisch", "Bengali"),
        ["sr"] = ("Serbisch", "Serbian"),
        ["az"] = ("Aserbaidschanisch", "Azerbaijani"),
        ["sl"] = ("Slowenisch", "Slovenian"),
        ["kn"] = ("Kannada", "Kannada"),
        ["et"] = ("Estnisch", "Estonian"),
        ["mk"] = ("Mazedonisch", "Macedonian"),
        ["br"] = ("Bretonisch", "Breton"),
        ["eu"] = ("Baskisch", "Basque"),
        ["is"] = ("Isländisch", "Icelandic"),
        ["hy"] = ("Armenisch", "Armenian"),
        ["ne"] = ("Nepalesisch", "Nepali"),
        ["mn"] = ("Mongolisch", "Mongolian"),
        ["bs"] = ("Bosnisch", "Bosnian"),
        ["kk"] = ("Kasachisch", "Kazakh"),
        ["sq"] = ("Albanisch", "Albanian"),
        ["sw"] = ("Suaheli", "Swahili"),
        ["gl"] = ("Galicisch", "Galician"),
        ["mr"] = ("Marathi", "Marathi"),
        ["pa"] = ("Pandschabi", "Punjabi"),
        ["si"] = ("Singhalesisch", "Sinhala"),
        ["km"] = ("Khmer", "Khmer"),
        ["sn"] = ("Shona", "Shona"),
        ["yo"] = ("Yoruba", "Yoruba"),
        ["so"] = ("Somali", "Somali"),
        ["af"] = ("Afrikaans", "Afrikaans"),
        ["oc"] = ("Okzitanisch", "Occitan"),
        ["ka"] = ("Georgisch", "Georgian"),
        ["be"] = ("Belarussisch", "Belarusian"),
        ["tg"] = ("Tadschikisch", "Tajik"),
        ["sd"] = ("Sindhi", "Sindhi"),
        ["gu"] = ("Gujarati", "Gujarati"),
        ["am"] = ("Amharisch", "Amharic"),
        ["yi"] = ("Jiddisch", "Yiddish"),
        ["lo"] = ("Laotisch", "Lao"),
        ["uz"] = ("Usbekisch", "Uzbek"),
        ["fo"] = ("Färöisch", "Faroese"),
        ["ht"] = ("Haitianisch", "Haitian Creole"),
        ["ps"] = ("Paschtu", "Pashto"),
        ["tk"] = ("Turkmenisch", "Turkmen"),
        ["nn"] = ("Nynorsk", "Nynorsk"),
        ["mt"] = ("Maltesisch", "Maltese"),
        ["sa"] = ("Sanskrit", "Sanskrit"),
        ["lb"] = ("Luxemburgisch", "Luxembourgish"),
        ["my"] = ("Birmanisch", "Burmese"),
        ["bo"] = ("Tibetisch", "Tibetan"),
        ["tl"] = ("Tagalog", "Tagalog"),
        ["mg"] = ("Madagassisch", "Malagasy"),
        ["as"] = ("Assamesisch", "Assamese"),
        ["tt"] = ("Tatarisch", "Tatar"),
        ["haw"] = ("Hawaiianisch", "Hawaiian"),
        ["ln"] = ("Lingala", "Lingala"),
        ["ha"] = ("Hausa", "Hausa"),
        ["ba"] = ("Baschkirisch", "Bashkir"),
        ["jw"] = ("Javanisch", "Javanese"),
        ["su"] = ("Sundanesisch", "Sundanese"),
        ["yue"] = ("Kantonesisch", "Cantonese"),
    };

    /// <summary>
    /// Anzeigename für einen ISO-Code in der gewünschten UI-Sprache ("de" oder "en").
    /// Unbekannte Codes werden als Großbuchstaben zurückgegeben.
    /// </summary>
    public static string Display(string code, string uiLang)
    {
        if (string.IsNullOrEmpty(code)) return code;
        if (Map.TryGetValue(code.ToLowerInvariant(), out var n))
            return uiLang == "en" ? n.En : n.De;
        return code.ToUpperInvariant();
    }
}
