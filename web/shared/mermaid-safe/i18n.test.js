import { afterEach, describe, expect, it } from "vitest";
import en from "../i18n/en.json";
import ko from "../i18n/ko.json";
import { SUPPORTED_LANGUAGES, currentLanguage, setLanguage, t } from "../i18n/index.js";

afterEach(() => setLanguage("en"));

describe("shared i18n catalogue", () => {
  it("ships the same keys in every language", () => {
    // Break caught: a key added on one side only. The other language silently falls back to
    // English, so nothing looks broken until someone spots one stray English label.
    expect(Object.keys(ko).sort()).toEqual(Object.keys(en).sort());
    expect(Object.keys(en).length).toBeGreaterThan(0);
  });

  it("never ships a blank translation", () => {
    for (const [language, catalog] of Object.entries({ en, ko })) {
      for (const [key, value] of Object.entries(catalog)) {
        expect(value.trim(), `${language}:${key}`).not.toBe("");
      }
    }
  });

  it("returns the chosen language and falls back to English for the rest", () => {
    expect(setLanguage("ko")).toBe("ko");
    expect(t("menu.settings.language.system")).toBe(ko["menu.settings.language.system"]);

    // A language we do not ship must land on English, not on empty text.
    expect(setLanguage("ja")).toBe("en");
    expect(t("menu.settings.language.system")).toBe(en["menu.settings.language.system"]);
  });

  it("tolerates casing and padding the host might send", () => {
    expect(setLanguage("  KO ")).toBe("ko");
    expect(currentLanguage()).toBe("ko");
  });

  it("shows the key when there is no translation at all", () => {
    expect(t("no.such.key")).toBe("no.such.key");
    expect(t("")).toBe("");
  });

  it("fills numbered placeholders", () => {
    setLanguage("en");
    expect(t("outline.line", 12)).toBe("Line 12");
    // A placeholder with no argument stays visible rather than becoming "undefined".
    expect(t("outline.line")).toBe(en["outline.line"]);
  });

  it("agrees with the C# side on which languages ship", () => {
    expect(SUPPORTED_LANGUAGES.sort()).toEqual(["en", "ko"]);
  });
});
