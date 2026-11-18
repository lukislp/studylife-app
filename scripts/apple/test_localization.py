"""Tests for the native surfaces' localisation - run with `python3 -m unittest discover -s scripts/apple`.

The Swift sources use English text as the key (`Text("Today")`, `String(localized: "Break")`), and the
string catalogs (`Localizable.xcstrings`) carry the translations. These checks need no Xcode:

* every catalog is valid, English is the source language and every entry has a German translation
  with the same placeholders as its key,
* every `String(localized: "...")` literal in a target's sources, and every title, description and
  answer of an App Intent, has an entry in that target's catalog,
* the main app's `de.lproj/Localizable.strings` (the App Intents that run in the app process) says the
  same as the widget catalog, which holds the same intents for the extension,
* no German text is left in the views.
"""

import json
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
NATIVE = ROOT / "native"

WIDGETS = NATIVE / "ios-liveactivity" / "Widget" / "Localizable.xcstrings"
INTENT_FILE = NATIVE / "ios-liveactivity" / "TimerControlIntent.swift"
TARGETS = {
    "widgets": (
        WIDGETS,
        [INTENT_FILE] + sorted((NATIVE / "ios-liveactivity" / "Widget").glob("*.swift")),
    ),
    "share extension": (
        NATIVE / "ios-share" / "Localizable.xcstrings",
        sorted((NATIVE / "ios-share").glob("*.swift")),
    ),
    "watch app": (
        NATIVE / "watchos-app" / "Localizable.xcstrings",
        sorted((NATIVE / "watchos-app").glob("*.swift")),
    ),
    "watch widgets": (
        NATIVE / "watchos-app" / "Widgets" / "Localizable.xcstrings",
        sorted((NATIVE / "watchos-app" / "Widgets").glob("*.swift")),
    ),
}
APP_STRINGS = ROOT / "src" / "StudyLife.App" / "Platforms" / "iOS" / "de.lproj" / "Localizable.strings"

PLACEHOLDER = re.compile(r"%(?:\d+\$)?(?:lld|@|d|f)")
BACKSLASH = chr(92)
SWIFT_LITERAL = r'"((?:' + BACKSLASH * 2 + r'.|[^"' + BACKSLASH * 2 + r'])*)"'
LOCALIZED_CALL = re.compile(r"String\(localized: " + SWIFT_LITERAL)
# Titles, descriptions and spoken answers of an App Intent are localised through their literal.
INTENT_LITERAL = re.compile(
    r"(?:LocalizedStringResource = |IntentDescription\(|dialog: |IntentDialog =\s*)" + SWIFT_LITERAL
)
STRINGS_LINE = re.compile("^" + SWIFT_LITERAL + r"\s*=\s*" + SWIFT_LITERAL + r";\s*$", re.M)
GERMAN = re.compile("[äöüÄÖÜß]")


def load_catalog(path: Path) -> dict:
    data = json.loads(path.read_text(encoding="utf-8"))
    assert data["sourceLanguage"] == "en", f"{path}: English must be the source language"
    return data["strings"]


def normalise(text: str) -> str:
    return PLACEHOLDER.sub("%@", text)


def swift_key(literal: str) -> str:
    """A Swift string literal's key as the catalog spells it: each interpolation becomes a placeholder."""
    out, i = [], 0
    while i < len(literal):
        if literal.startswith(BACKSLASH + "(", i):
            depth, j = 1, i + 2
            while j < len(literal) and depth:
                depth += {"(": 1, ")": -1}.get(literal[j], 0)
                j += 1
            out.append("%@")
            i = j
        elif literal.startswith(BACKSLASH + "n", i):
            out.append("\n")
            i += 2
        else:
            out.append(literal[i])
            i += 1
    return "".join(out)


def localized_literals(path: Path) -> list[str]:
    text = path.read_text(encoding="utf-8")
    patterns = [LOCALIZED_CALL] + ([INTENT_LITERAL] if path == INTENT_FILE else [])
    return [swift_key(m.group(1)) for pattern in patterns for m in pattern.finditer(text)]


def unescape(text: str) -> str:
    return text.replace(BACKSLASH + '"', '"').replace(BACKSLASH + "n", "\n").replace(BACKSLASH * 2, BACKSLASH)


def strings_file(path: Path) -> dict[str, str]:
    text = path.read_text(encoding="utf-8")
    return {unescape(m.group(1)): unescape(m.group(2)) for m in STRINGS_LINE.finditer(text)}


class CatalogTests(unittest.TestCase):
    def test_every_entry_has_a_german_translation_with_the_same_placeholders(self):
        for name, (catalog, _) in TARGETS.items():
            for key, entry in load_catalog(catalog).items():
                with self.subTest(target=name, key=key):
                    unit = entry["localizations"]["de"]["stringUnit"]
                    self.assertEqual("translated", unit["state"])
                    self.assertTrue(unit["value"].strip())
                    self.assertEqual(PLACEHOLDER.findall(key), PLACEHOLDER.findall(unit["value"]))

    def test_every_localized_literal_in_the_sources_has_a_catalog_entry(self):
        for name, (catalog, sources) in TARGETS.items():
            keys = {normalise(k) for k in load_catalog(catalog)}
            found = 0
            for source in sources:
                for literal in localized_literals(source):
                    found += 1
                    with self.subTest(target=name, source=source.name, key=literal):
                        self.assertIn(normalise(literal), keys)
            self.assertGreater(found, 0, f"{name}: the scan found no localised literal at all")

    def test_no_german_text_is_left_in_the_views(self):
        for _, (_, sources) in TARGETS.items():
            for source in sources:
                # The Siri trigger phrases in the intents file are German and English on purpose.
                if source == INTENT_FILE:
                    continue
                for number, line in enumerate(source.read_text(encoding="utf-8").splitlines(), 1):
                    if GERMAN.search(line.split("//")[0]):
                        self.fail(f"{source.relative_to(ROOT)}:{number}: German text in code: {line.strip()}")


class AppIntentStringsTests(unittest.TestCase):
    def test_the_app_translations_match_the_widget_catalog(self):
        catalog = load_catalog(WIDGETS)
        app = strings_file(APP_STRINGS)
        self.assertTrue(app, "de.lproj/Localizable.strings has no entries")
        for key, value in app.items():
            with self.subTest(key=key):
                self.assertIn(key, catalog)
                self.assertEqual(catalog[key]["localizations"]["de"]["stringUnit"]["value"], value)

    def test_every_intent_string_is_translated_in_the_app(self):
        translated = {normalise(k) for k in strings_file(APP_STRINGS)}
        for literal in localized_literals(INTENT_FILE):
            with self.subTest(key=literal):
                self.assertIn(normalise(literal), translated)


if __name__ == "__main__":
    unittest.main()
