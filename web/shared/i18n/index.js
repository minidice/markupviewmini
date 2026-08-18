import en from "./en.json";
import ko from "./ko.json";

/*
 * The same catalogues the C# side embeds. Importing the JSON directly is what keeps the two
 * halves of the app from drifting - there is no second copy of the translations to update.
 */
const CATALOGS = { en, ko };
const FALLBACK = "en";

export const SUPPORTED_LANGUAGES = Object.keys(CATALOGS);

let current = FALLBACK;

/** Reduces a code to the shape we accept: "" (unknown) or a lower-case two-letter code. */
function sanitize(code) {
  const trimmed = typeof code === "string" ? code.trim() : "";
  return /^[a-z]{2}$/iu.test(trimmed) ? trimmed.toLowerCase() : "";
}

/** Switches the language, ignoring anything we do not ship. Returns the language in force. */
export function setLanguage(code) {
  const wanted = sanitize(code);
  current = Object.hasOwn(CATALOGS, wanted) ? wanted : FALLBACK;
  return current;
}

export function currentLanguage() {
  return current;
}

/**
 * Looks up display text, falling back from the current language to English and finally to the
 * key itself - never to an empty string, which would read as a missing feature rather than a
 * missing translation. `{0}`-style placeholders are filled from the extra arguments.
 */
export function t(key, ...args) {
  if (typeof key !== "string" || key === "") return "";
  const text = CATALOGS[current]?.[key] ?? CATALOGS[FALLBACK][key] ?? key;
  return args.length === 0
    ? text
    : text.replace(/\{(\d+)\}/gu, (match, index) => {
      const value = args[Number(index)];
      return value === undefined ? match : String(value);
    });
}
