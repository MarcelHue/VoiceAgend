"""Statische Whisper-Sprachliste + Normalisierung von HuggingFace-Sprach-Tags.

Speaches/faster-whisper bietet keinen Endpoint, der die unterstützten Sprachen
eines Modells liefert. Standard-Whisper-Modelle können aber alle dieselben ~99
Sprachen. Diese Liste dient als Fallback, wenn HuggingFace keine brauchbaren
`language`-Tags im Model-Card liefert (häufig bei reinen CTranslate2-Repos).

Quelle der Codes: openai-whisper `whisper.tokenizer.LANGUAGES` (ISO 639-1, mit
einigen Whisper-spezifischen Zusätzen wie `yue` für Kantonesisch).
"""

# ISO-639-1-Codes (plus `yue`) der von Whisper large-v3 unterstützten Sprachen.
WHISPER_LANGUAGES: list[str] = [
    "en", "zh", "de", "es", "ru", "ko", "fr", "ja", "pt", "tr", "pl", "ca",
    "nl", "ar", "sv", "it", "id", "hi", "fi", "vi", "he", "uk", "el", "ms",
    "cs", "ro", "da", "hu", "ta", "no", "th", "ur", "hr", "bg", "lt", "la",
    "mi", "ml", "cy", "sk", "te", "fa", "lv", "bn", "sr", "az", "sl", "kn",
    "et", "mk", "br", "eu", "is", "hy", "ne", "mn", "bs", "kk", "sq", "sw",
    "gl", "mr", "pa", "si", "km", "sn", "yo", "so", "af", "oc", "ka", "be",
    "tg", "sd", "gu", "am", "yi", "lo", "uz", "fo", "ht", "ps", "tk", "nn",
    "mt", "sa", "lb", "my", "bo", "tl", "mg", "as", "tt", "haw", "ln", "ha",
    "ba", "jw", "su", "yue",
]

# Set für schnelle Membership-Checks.
_KNOWN = set(WHISPER_LANGUAGES)

# HuggingFace verwendet teils ISO-639-3 / abweichende Codes in `language`-Tags.
# Hier auf die Whisper-Codes mappen, damit z.B. "deu" → "de" funktioniert.
_ALIASES: dict[str, str] = {
    "deu": "de", "ger": "de",
    "eng": "en",
    "fra": "fr", "fre": "fr",
    "spa": "es",
    "rus": "ru",
    "zho": "zh", "chi": "zh", "cmn": "zh",
    "jpn": "ja",
    "kor": "ko",
    "por": "pt",
    "ita": "it",
    "nld": "nl", "dut": "nl",
    "ara": "ar",
    "tur": "tr",
    "pol": "pl",
    "ukr": "uk",
    "ces": "cs", "cze": "cs",
    "ron": "ro", "rum": "ro",
    "ell": "el", "gre": "el",
    "heb": "he",
    "hin": "hi",
    "ind": "id",
    "vie": "vi",
    "swe": "sv",
    "fin": "fi",
    "dan": "da",
    "nor": "no", "nob": "no",
    "hun": "hu",
}


def normalize_lang_codes(raw: object) -> list[str]:
    """Filtert eine beliebige Tag-/Sprachliste auf bekannte Whisper-Codes.

    Akzeptiert str (einzelner Code) oder iterierbare Sammlungen. Lowercased,
    löst Aliase auf, verwirft Nicht-Sprach-Tags und Dubletten, behält die
    Reihenfolge stabil.
    """
    if raw is None:
        return []
    items: list[str]
    if isinstance(raw, str):
        items = [raw]
    elif isinstance(raw, (list, tuple, set)):
        items = [str(x) for x in raw]
    else:
        return []

    out: list[str] = []
    seen: set[str] = set()
    for item in items:
        code = item.strip().lower()
        # HF-Tags sehen teils aus wie "language:de" — Präfix abschneiden.
        if ":" in code:
            code = code.split(":", 1)[1]
        code = _ALIASES.get(code, code)
        if code in _KNOWN and code not in seen:
            seen.add(code)
            out.append(code)
    return out
