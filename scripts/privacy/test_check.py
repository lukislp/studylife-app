"""Tests for scripts/privacy/check.py - run with `python3 -m unittest discover -s scripts/privacy`.

Pure file logic against small repositories built in a temp directory, plus one test that the real
repository passes its own gate.
"""

import plistlib
import tempfile
import unittest
from pathlib import Path

import check

POLICY = check.load_policy(check.HERE / "policy.toml")


def manifest(*apis, tracking=False, domains=(), collected=()):
    return plistlib.dumps({
        "NSPrivacyTracking": tracking,
        "NSPrivacyTrackingDomains": list(domains),
        "NSPrivacyCollectedDataTypes": list(collected),
        "NSPrivacyAccessedAPITypes": [
            {"NSPrivacyAccessedAPIType": f"NSPrivacyAccessedAPICategory{name}", "NSPrivacyAccessedAPITypeReasons": list(r)}
            for name, r in apis
        ],
    })


BASELINE = (("FileTimestamp", ["C617.1"]), ("SystemBootTime", ["35F9.1"]), ("DiskSpace", ["E174.1"]))


class Repo(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.root = Path(self._tmp.name)

    def put(self, rel, content):
        path = self.root / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content if isinstance(content, bytes) else content.encode("utf-8"))
        return path


class Scan(unittest.TestCase):
    def hits(self, text, language="csharp"):
        return {(c, a) for c, a, _ in check.scan_text(text, language, POLICY)}

    def test_csharp_apis(self):
        self.assertIn(("UserDefaults", "Preferences.Default"), self.hits("var x = Preferences.Default.Get(\"a\", 1);"))
        self.assertIn(("UserDefaults", "NSUserDefaults"), self.hits("NSUserDefaults.StandardUserDefaults"))
        self.assertIn(("FileTimestamp", "GetLastWriteTime"), self.hits("File.GetLastWriteTime(p)"))
        self.assertIn(("DiskSpace", "DriveInfo"), self.hits("new DriveInfo(\"/\")"))
        self.assertIn(("SystemBootTime", "SystemUptime"), self.hits("NSProcessInfo.ProcessInfo.SystemUptime"))

    def test_swift_apis(self):
        self.assertIn(("UserDefaults", "UserDefaults"), self.hits("let d = UserDefaults.standard", "swift"))
        self.assertIn(("FileTimestamp", "stat("), self.hits("var s = stat(path, &buf)", "swift"))
        self.assertIn(("SystemBootTime", "systemUptime"), self.hits("ProcessInfo.processInfo.systemUptime", "swift"))
        self.assertIn(("ActiveKeyboards", "activeInputModes"), self.hits("UITextInputMode.activeInputModes", "swift"))

    def test_plain_code_is_clean(self):
        self.assertEqual(self.hits("let now = Date()\nFileManager.default.containerURL(forSecurityApplicationGroupIdentifier: g)", "swift"), set())
        self.assertEqual(self.hits("File.Exists(p); File.ReadAllText(p); var stat = 1; var status = 2;"), set())

    def test_whole_words_only(self):
        # "stat(" must be a call of stat, not the tail of another name
        self.assertEqual(self.hits("let s = restat(x); mystat(x)", "swift"), set())
        self.assertEqual(self.hits("MyPreferences.Default", "csharp"), set())

    def test_comments_do_not_count_but_strings_keep_slashes(self):
        text = "// NSUserDefaults is mentioned here\n/* Preferences.Default\n too */\nvar url = \"http://x\"; var y = 1;"
        self.assertEqual(self.hits(text), set())
        self.assertIn(("UserDefaults", "NSUserDefaults"), self.hits("var url = \"http://x\"; NSUserDefaults.Foo"))

    def test_line_numbers_survive_comment_stripping(self):
        hits = check.scan_text("/* a\n b */\nvar x = NSUserDefaults.Standard;", "csharp", POLICY)
        self.assertEqual([line for _, _, line in hits], [3])

    def test_app_group_defaults_need_1c8f(self):
        hits = check.scan_text('let d = UserDefaults(suiteName: "group.app")', "swift", POLICY)
        self.assertTrue(any(api.startswith("1C8F.1:") for _, api, _ in hits))
        hits = check.scan_text('Preferences.Default.Get("k", 1, sharedName: "g")', "csharp", POLICY)
        self.assertTrue(any(api.startswith("1C8F.1:") for _, api, _ in hits))


class Manifests(Repo):
    def check(self, content):
        path = self.put("m.xcprivacy", content)
        return check.check_manifest(path, POLICY, "m.xcprivacy")

    def test_valid(self):
        self.assertEqual(self.check(manifest(*BASELINE, ("UserDefaults", ["CA92.1"]))), [])

    def test_not_a_plist(self):
        self.assertIn("not a valid property list", self.check("<plist><dict>")[0])
        self.assertIn("not a valid property list", self.check("hello")[0])

    def test_tracking_is_refused(self):
        problems = self.check(manifest(tracking=True, domains=["t.example"]))
        self.assertTrue(any("NSPrivacyTracking must be" in p for p in problems))
        self.assertTrue(any("NSPrivacyTrackingDomains" in p for p in problems))

    def test_collected_data_is_refused(self):
        problems = self.check(manifest(collected=[{"NSPrivacyCollectedDataType": "x"}]))
        self.assertTrue(any("NSPrivacyCollectedDataTypes" in p for p in problems))

    def test_missing_keys_are_refused(self):
        problems = self.check(plistlib.dumps({"NSPrivacyAccessedAPITypes": []}))
        self.assertEqual(len(problems), 3)

    def test_unknown_category_and_reason(self):
        raw = plistlib.dumps({
            "NSPrivacyTracking": False, "NSPrivacyTrackingDomains": [], "NSPrivacyCollectedDataTypes": [],
            "NSPrivacyAccessedAPITypes": [
                {"NSPrivacyAccessedAPIType": "NSPrivacyAccessedAPICategoryMadeUp", "NSPrivacyAccessedAPITypeReasons": ["AAAA.1"]},
                {"NSPrivacyAccessedAPIType": "NSPrivacyAccessedAPICategoryUserDefaults", "NSPrivacyAccessedAPITypeReasons": ["XXXX.9"]},
                {"NSPrivacyAccessedAPIType": "NSPrivacyAccessedAPICategoryDiskSpace", "NSPrivacyAccessedAPITypeReasons": []},
            ],
        })
        problems = self.check(raw)
        self.assertTrue(any("unknown API category" in p for p in problems))
        self.assertTrue(any("not an approved reason" in p for p in problems))
        self.assertTrue(any("declares no reason" in p for p in problems))

    def test_duplicate_category(self):
        problems = self.check(manifest(("UserDefaults", ["CA92.1"]), ("UserDefaults", ["1C8F.1"])))
        self.assertTrue(any("declared twice" in p for p in problems))


class Bundles(Repo):
    def bundle(self, manifest_rel="app.xcprivacy", baseline=True, sources=("src/*.cs", "src/*.swift")):
        return {"name": "b", "manifest": manifest_rel, "baseline": baseline, "sources": list(sources)}

    def test_declared_use_passes(self):
        self.put("app.xcprivacy", manifest(*BASELINE, ("UserDefaults", ["CA92.1"])))
        self.put("src/a.cs", "var v = Preferences.Default.Get(\"k\", 1);")
        problems, used = check.check_bundle(self.root, self.bundle(), POLICY)
        self.assertEqual(problems, [])
        self.assertIn("UserDefaults", used)

    def test_undeclared_category_fails(self):
        self.put("app.xcprivacy", manifest(*BASELINE))
        self.put("src/a.cs", "NSUserDefaults.StandardUserDefaults.SetString(\"a\", \"b\");")
        problems, _ = check.check_bundle(self.root, self.bundle(), POLICY)
        self.assertEqual(len(problems), 1)
        self.assertIn("UserDefaults", problems[0])
        self.assertIn("src/a.cs:1", problems[0])

    def test_baseline_is_required(self):
        self.put("app.xcprivacy", manifest(("DiskSpace", ["E174.1"])))
        problems, _ = check.check_bundle(self.root, self.bundle(), POLICY)
        self.assertEqual(len(problems), 2)
        self.assertTrue(all("baseline" in p or ".NET runtime" in p for p in problems))

    def test_baseline_needs_its_reason(self):
        self.put("app.xcprivacy", manifest(("FileTimestamp", ["DDA9.1"]), ("SystemBootTime", ["35F9.1"]), ("DiskSpace", ["E174.1"])))
        problems, _ = check.check_bundle(self.root, self.bundle(), POLICY)
        self.assertEqual(len(problems), 1)
        self.assertIn("C617.1", problems[0])

    def test_app_group_defaults_need_the_group_reason(self):
        self.put("app.xcprivacy", manifest(*BASELINE, ("UserDefaults", ["CA92.1"])))
        self.put("src/W.swift", 'let d = UserDefaults(suiteName: "group.x")')
        problems, _ = check.check_bundle(self.root, self.bundle(), POLICY)
        self.assertEqual(len(problems), 1)
        self.assertIn("1C8F.1", problems[0])
        self.put("app.xcprivacy", manifest(*BASELINE, ("UserDefaults", ["CA92.1", "1C8F.1"])))
        self.assertEqual(check.check_bundle(self.root, self.bundle(), POLICY)[0], [])

    def test_bundle_without_manifest_may_not_use_an_api(self):
        self.put("src/W.swift", "let d = UserDefaults.standard")
        problems, _ = check.check_bundle(self.root, self.bundle("", baseline=False), POLICY)
        self.assertEqual(len(problems), 1)
        self.assertIn("has no privacy manifest", problems[0])

    def test_bundle_without_manifest_and_clean_sources_passes(self):
        self.put("src/W.swift", "let now = Date()")
        self.assertEqual(check.check_bundle(self.root, self.bundle("", baseline=False), POLICY)[0], [])

    def test_missing_manifest_file(self):
        problems, _ = check.check_bundle(self.root, self.bundle(), POLICY)
        self.assertIn("does not exist", problems[0])

    def test_excluded_sources_are_ignored(self):
        self.put("app.xcprivacy", manifest(*BASELINE))
        self.put("src/obj/x.cs", "NSUserDefaults.Foo")
        bundle = self.bundle(sources=("src/**/*.cs",))
        bundle["exclude"] = ["**/obj/**"]
        self.assertEqual(check.check_bundle(self.root, bundle, POLICY)[0], [])


class Purpose(Repo):
    def setup_purpose(self, plist_keys, strings_by_lang):
        info = {"CFBundleName": "x"}
        info.update({k: "Beschreibung" for k in plist_keys})
        self.put("Info.plist", plistlib.dumps(info))
        for lang, entries in strings_by_lang.items():
            body = "".join(f'"{k}" = "{v}";\n' for k, v in entries.items())
            self.put(f"l/{lang}.lproj/InfoPlist.strings", body)
        return {"name": "t", "plist": "Info.plist", "lproj": "l/*.lproj/InfoPlist.strings", "required": ["NSMicrophoneUsageDescription"]}

    def test_complete(self):
        p = self.setup_purpose(["NSMicrophoneUsageDescription"], {"de": {"NSMicrophoneUsageDescription": "a"}, "en": {"NSMicrophoneUsageDescription": "b"}})
        self.assertEqual(check.check_purpose(self.root, p), [])

    def test_missing_in_plist(self):
        p = self.setup_purpose([], {"de": {"NSMicrophoneUsageDescription": "a"}})
        self.assertIn("missing or empty", check.check_purpose(self.root, p)[0])

    def test_missing_in_one_language(self):
        p = self.setup_purpose(["NSMicrophoneUsageDescription", "NSLocalNetworkUsageDescription"], {
            "de": {"NSMicrophoneUsageDescription": "a", "NSLocalNetworkUsageDescription": "a"},
            "fr": {"NSMicrophoneUsageDescription": "a"},
        })
        problems = check.check_purpose(self.root, p)
        self.assertEqual(len(problems), 1)
        self.assertIn("fr.lproj", problems[0])
        self.assertIn("NSLocalNetworkUsageDescription", problems[0])

    def test_empty_translation(self):
        p = self.setup_purpose(["NSMicrophoneUsageDescription"], {"de": {"NSMicrophoneUsageDescription": " "}})
        self.assertIn("is empty", check.check_purpose(self.root, p)[0])

    def test_languages_must_carry_the_same_keys(self):
        p = self.setup_purpose(["NSMicrophoneUsageDescription"], {
            "de": {"NSMicrophoneUsageDescription": "a", "Fokus": "x"},
            "en": {"NSMicrophoneUsageDescription": "a"},
        })
        self.assertIn("exists in other languages", check.check_purpose(self.root, p)[0])

    def test_no_strings_files(self):
        p = self.setup_purpose(["NSMicrophoneUsageDescription"], {})
        self.assertIn("no InfoPlist.strings", check.check_purpose(self.root, p)[0])

    def test_parse_strings(self):
        text = '"a" = "x \\"q\\" y";\n/* c */\n"b" = "z";\r\n'
        self.assertEqual(check.parse_strings(text), {"a": 'x \\"q\\" y', "b": "z"})


class BuiltBundle(Repo):
    def test_built_bundle(self):
        for lang in ("de", "en"):
            self.put(f"src/StudyLife.App/Platforms/iOS/{lang}.lproj/InfoPlist.strings", "")
        policy = {**POLICY, "purpose": [{**POLICY["purpose"][0], "lproj": "src/StudyLife.App/Platforms/iOS/*.lproj/InfoPlist.strings"}]}
        self.put("out/PrivacyInfo.xcprivacy", manifest(*BASELINE))
        info = {k: "x" for k in policy["purpose"][0]["required"]}
        self.put("out/Info.plist", plistlib.dumps(info, fmt=plistlib.FMT_BINARY))
        for lang in ("de", "en"):
            self.put(f"out/{lang}.lproj/InfoPlist.strings", "")
        # the first [[purpose]] of the real policy is "ios"
        ok = check.check_built_bundle(self.root, policy, "ios", self.root / "out/Info.plist", self.root / "out")
        self.assertEqual(ok, [])

    def test_missing_pieces_are_reported(self):
        self.put("src/StudyLife.App/Platforms/iOS/de.lproj/InfoPlist.strings", "")
        policy = {**POLICY, "purpose": [{**POLICY["purpose"][0], "lproj": "src/StudyLife.App/Platforms/iOS/*.lproj/InfoPlist.strings"}]}
        self.put("out/Info.plist", plistlib.dumps({}))
        problems = check.check_built_bundle(self.root, policy, "ios", self.root / "out/Info.plist", self.root / "out")
        self.assertTrue(any("manifest is not in the bundle" in p for p in problems))
        self.assertTrue(any("missing from the built Info.plist" in p for p in problems))
        self.assertTrue(any("de.lproj" in p for p in problems))


class Policy(unittest.TestCase):
    def test_every_hint_and_baseline_reason_is_approved(self):
        for name, reason in POLICY["baseline"].items():
            self.assertIn(reason, POLICY["categories"][name]["reasons"])

    def test_all_five_apple_categories_are_known(self):
        self.assertEqual(
            {c["key"] for c in POLICY["categories"].values()},
            {f"NSPrivacyAccessedAPICategory{n}" for n in ("FileTimestamp", "SystemBootTime", "DiskSpace", "ActiveKeyboards", "UserDefaults")})

    def test_the_repository_passes_its_own_gate(self):
        self.assertEqual(check.run(check.ROOT, POLICY), 0)


if __name__ == "__main__":
    unittest.main()
