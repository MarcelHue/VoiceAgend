// ISO-639-1 → Anzeigename (de/en). Portiert aus client/.../Services/LanguageNames.cs.
window.VA_LANGS = {
  en: ["Englisch", "English"], zh: ["Chinesisch", "Chinese"], de: ["Deutsch", "German"],
  es: ["Spanisch", "Spanish"], ru: ["Russisch", "Russian"], ko: ["Koreanisch", "Korean"],
  fr: ["Französisch", "French"], ja: ["Japanisch", "Japanese"], pt: ["Portugiesisch", "Portuguese"],
  tr: ["Türkisch", "Turkish"], pl: ["Polnisch", "Polish"], ca: ["Katalanisch", "Catalan"],
  nl: ["Niederländisch", "Dutch"], ar: ["Arabisch", "Arabic"], sv: ["Schwedisch", "Swedish"],
  it: ["Italienisch", "Italian"], id: ["Indonesisch", "Indonesian"], hi: ["Hindi", "Hindi"],
  fi: ["Finnisch", "Finnish"], vi: ["Vietnamesisch", "Vietnamese"], he: ["Hebräisch", "Hebrew"],
  uk: ["Ukrainisch", "Ukrainian"], el: ["Griechisch", "Greek"], ms: ["Malaiisch", "Malay"],
  cs: ["Tschechisch", "Czech"], ro: ["Rumänisch", "Romanian"], da: ["Dänisch", "Danish"],
  hu: ["Ungarisch", "Hungarian"], ta: ["Tamil", "Tamil"], no: ["Norwegisch", "Norwegian"],
  th: ["Thailändisch", "Thai"], ur: ["Urdu", "Urdu"], hr: ["Kroatisch", "Croatian"],
  bg: ["Bulgarisch", "Bulgarian"], lt: ["Litauisch", "Lithuanian"], la: ["Latein", "Latin"],
  mi: ["Maori", "Maori"], ml: ["Malayalam", "Malayalam"], cy: ["Walisisch", "Welsh"],
  sk: ["Slowakisch", "Slovak"], te: ["Telugu", "Telugu"], fa: ["Persisch", "Persian"],
  lv: ["Lettisch", "Latvian"], bn: ["Bengalisch", "Bengali"], sr: ["Serbisch", "Serbian"],
  az: ["Aserbaidschanisch", "Azerbaijani"], sl: ["Slowenisch", "Slovenian"], kn: ["Kannada", "Kannada"],
  et: ["Estnisch", "Estonian"], mk: ["Mazedonisch", "Macedonian"], br: ["Bretonisch", "Breton"],
  eu: ["Baskisch", "Basque"], is: ["Isländisch", "Icelandic"], hy: ["Armenisch", "Armenian"],
  ne: ["Nepalesisch", "Nepali"], mn: ["Mongolisch", "Mongolian"], bs: ["Bosnisch", "Bosnian"],
  kk: ["Kasachisch", "Kazakh"], sq: ["Albanisch", "Albanian"], sw: ["Suaheli", "Swahili"],
  gl: ["Galicisch", "Galician"], mr: ["Marathi", "Marathi"], pa: ["Pandschabi", "Punjabi"],
  si: ["Singhalesisch", "Sinhala"], km: ["Khmer", "Khmer"], sn: ["Shona", "Shona"],
  yo: ["Yoruba", "Yoruba"], so: ["Somali", "Somali"], af: ["Afrikaans", "Afrikaans"],
  oc: ["Okzitanisch", "Occitan"], ka: ["Georgisch", "Georgian"], be: ["Belarussisch", "Belarusian"],
  tg: ["Tadschikisch", "Tajik"], sd: ["Sindhi", "Sindhi"], gu: ["Gujarati", "Gujarati"],
  am: ["Amharisch", "Amharic"], yi: ["Jiddisch", "Yiddish"], lo: ["Laotisch", "Lao"],
  uz: ["Usbekisch", "Uzbek"], fo: ["Färöisch", "Faroese"], ht: ["Haitianisch", "Haitian Creole"],
  ps: ["Paschtu", "Pashto"], tk: ["Turkmenisch", "Turkmen"], nn: ["Nynorsk", "Nynorsk"],
  mt: ["Maltesisch", "Maltese"], sa: ["Sanskrit", "Sanskrit"], lb: ["Luxemburgisch", "Luxembourgish"],
  my: ["Birmanisch", "Burmese"], bo: ["Tibetisch", "Tibetan"], tl: ["Tagalog", "Tagalog"],
  mg: ["Madagassisch", "Malagasy"], as: ["Assamesisch", "Assamese"], tt: ["Tatarisch", "Tatar"],
  haw: ["Hawaiianisch", "Hawaiian"], ln: ["Lingala", "Lingala"], ha: ["Hausa", "Hausa"],
  ba: ["Baschkirisch", "Bashkir"], jw: ["Javanisch", "Javanese"], su: ["Sundanesisch", "Sundanese"],
  yue: ["Kantonesisch", "Cantonese"]
};

window.vaLangName = function (code, uiLang) {
  if (!code) return uiLang === "en" ? "Auto" : "Auto-erkennen";
  const n = window.VA_LANGS[code.toLowerCase()];
  if (!n) return code.toUpperCase();
  return uiLang === "en" ? n[1] : n[0];
};
