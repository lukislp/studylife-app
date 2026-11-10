"""Tests for scripts/licences/check.py - run with `python3 -m unittest discover -s scripts/licences`.

Pure logic only (no dotnet, no NuGet cache): expression handling, the policy verdicts, nuspec
parsing and the notices rendering, against small policies built in the test.
"""

import json
import tempfile
import unittest
from pathlib import Path

import check

DENIED = ["GPL-*", "AGPL-*", "LGPL-*"]


def make_policy(allowed=("MIT",), exceptions=()):
    return {
        "bundle": {"frameworks": ["net10.0-ios"], "asset_logical_name": "N.md"},
        "licenses": {"allowed": list(allowed), "denied": DENIED},
        "exception": list(exceptions),
        "component": [],
    }


def pkg(spdx, name="Pkg", **kw):
    return {"id": name, "version": "1.0.0", "license": spdx, "license_note": None,
            "license_file_text": None, "project_url": "", "copyright": "", "frameworks": {"net10.0-ios"}, **kw}


class Expressions(unittest.TestCase):
    def test_ids(self):
        self.assertEqual(
            check.license_ids("(MIT OR Apache-2.0) AND GPL-2.0-only WITH Classpath-exception-2.0"),
            ["MIT", "Apache-2.0", "GPL-2.0-only"])

    def test_denied_families(self):
        for spdx in ("GPL-3.0-only", "AGPL-3.0-or-later", "LGPL-2.1-only", "MIT OR GPL-2.0-only", "gpl-3.0"):
            self.assertTrue(check.is_denied(spdx, DENIED), spdx)
        for spdx in ("MIT", "MPL-2.0", "BSD-2-Clause"):
            self.assertFalse(check.is_denied(spdx, DENIED), spdx)

    def test_evaluate_or_and(self):
        def ok(i):
            return i == "MIT"
        self.assertTrue(check.evaluate("MIT", ok))
        self.assertTrue(check.evaluate("Apache-2.0 OR MIT", ok))
        self.assertFalse(check.evaluate("Apache-2.0 AND MIT", ok))
        self.assertTrue(check.evaluate("(Apache-2.0 OR MIT) AND MIT", ok))

    def test_bad_expression(self):
        for bad in ("MIT OR", "(MIT", "OR MIT", ""):
            with self.assertRaises(ValueError, msg=bad):
                check.evaluate(bad, lambda i: True)


class Verdicts(unittest.TestCase):
    def test_allowed(self):
        self.assertEqual(check.classify(pkg("MIT"), make_policy())[0], "ok")

    def test_unknown_always_fails(self):
        self.assertEqual(check.classify(pkg(check.UNKNOWN), make_policy())[0], "unknown")

    def test_denied_beats_allowed(self):
        self.assertEqual(check.classify(pkg("GPL-3.0-only"), make_policy(allowed=("GPL-3.0-only",)))[0], "denied")

    def test_policy_rejects_a_denied_exception(self):
        with tempfile.TemporaryDirectory() as d:
            f = Path(d) / "p.toml"
            f.write_text('[bundle]\n[licenses]\nallowed=[]\ndenied=["GPL-*"]\n'
                         '[[exception]]\npackage="Pkg"\nlicense="GPL-3.0-only"\nreason="r"\nsource="s"\n')
            with self.assertRaises(ValueError):
                check.load_policy(f)

    def test_exception_matches_package_and_licence_exactly(self):
        exc = {"package": "Toolbelt", "license": "MPL-2.0", "reason": "file-level copyleft", "source": "s"}
        policy = make_policy(exceptions=[exc])
        self.assertEqual(check.classify(pkg("MPL-2.0", "toolbelt"), policy)[0], "exception")
        self.assertEqual(check.classify(pkg("MPL-2.0", "Other"), policy)[0], "not-allowed")
        self.assertEqual(check.classify(pkg("MPL-1.1", "Toolbelt"), policy)[0], "not-allowed")

    def test_inventory_problems(self):
        with tempfile.TemporaryDirectory() as d:
            texts = Path(d)
            (texts / "MIT.txt").write_text("text")
            inv = [pkg("MIT", "A"), pkg(check.UNKNOWN, "B", license_note="only a licenseUrl"),
                   pkg("GPL-2.0-only", "C"), pkg("ISC", "D")]
            problems = check.check_inventory(inv, make_policy(), texts)
        self.assertEqual(len(problems), 3)
        self.assertTrue(any("B 1.0.0" in p and "could not be verified" in p for p in problems))
        self.assertTrue(any("C 1.0.0" in p and "denied" in p for p in problems))
        self.assertTrue(any("D 1.0.0" in p and "not in the allowed" in p for p in problems))

    def test_missing_licence_text(self):
        with tempfile.TemporaryDirectory() as d:
            problems = check.check_inventory([pkg("MIT")], make_policy(), Path(d))
        self.assertEqual(len(problems), 1)
        self.assertIn("texts/MIT.txt", problems[0])


class NuspecAndAssets(unittest.TestCase):
    NS = 'xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"'

    def nuspec(self, body):
        return check.parse_nuspec(f"<package {self.NS}><metadata>{body}</metadata></package>")

    def test_expression(self):
        meta = self.nuspec('<license type="expression">MIT</license><projectUrl>u</projectUrl>'
                           "<copyright>c</copyright>")
        self.assertEqual(check.resolve_license(meta, Path("."))[0], "MIT")
        self.assertEqual((meta["projectUrl"], meta["copyright"]), ("u", "c"))

    def test_license_url_only_is_unknown(self):
        meta = self.nuspec("<licenseUrl>https://example.invalid/LICENSE</licenseUrl>")
        spdx, note, _ = check.resolve_license(meta, Path("."))
        self.assertEqual(spdx, check.UNKNOWN)
        self.assertIn("licenseUrl", note)

    def test_nothing_is_unknown(self):
        self.assertEqual(check.resolve_license(self.nuspec("<id>x</id>"), Path("."))[0], check.UNKNOWN)

    def test_license_file(self):
        with tempfile.TemporaryDirectory() as d:
            (Path(d) / "LICENSE.txt").write_text("custom terms\n")
            meta = self.nuspec('<license type="file">LICENSE.txt</license>')
            spdx, _, text = check.resolve_license(meta, Path(d))
            self.assertEqual((spdx, text), (check.FILE_LICENSE, "custom terms"))
            missing = self.nuspec('<license type="file">NOPE.txt</license>')
            self.assertEqual(check.resolve_license(missing, Path(d))[0], check.UNKNOWN)

    def test_inventory_from_assets(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            pdir = root / "cache" / "foo" / "1.2.3"
            pdir.mkdir(parents=True)
            (pdir / "foo.nuspec").write_text(
                f'<package {self.NS}><metadata><license type="expression">MIT</license></metadata></package>')
            assets = root / "a.json"
            assets.write_text(json.dumps({
                "packageFolders": {str(root / "cache"): {}},
                "libraries": {"Foo/1.2.3": {"type": "package", "path": "foo/1.2.3"},
                              "Proj/1.0.0": {"type": "project", "path": "x"}}}))
            inv = check.build_inventory({"net10.0-ios": assets, "net10.0-maccatalyst": assets})
        self.assertEqual([(p["id"], p["version"], p["license"]) for p in inv], [("Foo", "1.2.3", "MIT")])
        self.assertEqual(inv[0]["frameworks"], {"net10.0-ios", "net10.0-maccatalyst"})


class SdkPackages(unittest.TestCase):
    def test_version_follows_the_sdk_and_is_not_recorded(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            files = []
            for version in ("10.0.12", "10.0.15"):
                pdir = root / "cache" / "tool" / version
                pdir.mkdir(parents=True)
                (pdir / "tool.nuspec").write_text(
                    f'<package {NuspecAndAssets.NS}><metadata><license type="expression">MIT</license>'
                    "</metadata></package>")
                assets = root / f"{version}.json"
                assets.write_text(json.dumps({
                    "packageFolders": {str(root / "cache"): {}},
                    "libraries": {f"Tool/{version}": {"type": "package", "path": f"tool/{version}"}}}))
                files.append(assets)
            inv = check.build_inventory({"a": files[0], "b": files[1]}, frozenset({"tool"}))
        self.assertEqual([(p["id"], p["version"]) for p in inv], [("Tool", check.SDK_VERSION)])


class Rendering(unittest.TestCase):
    def test_notices_are_deterministic_and_complete(self):
        exc = {"package": "Tool", "license": "MPL-2.0", "reason": "file-level copyleft", "source": "https://src"}
        policy = make_policy(exceptions=[exc])
        with tempfile.TemporaryDirectory() as d:
            texts = Path(d)
            (texts / "MIT.txt").write_text("MIT TEXT")
            inv = [pkg("MPL-2.0", "Tool"), pkg("MIT", "A", copyright="(c) A")]
            out = check.render_notices(inv, policy, texts)
            self.assertEqual(out, check.render_notices(inv, policy, texts))
        self.assertIn("| A | 1.0.0 | MIT |", out)
        self.assertIn("MIT TEXT", out)
        self.assertIn("(c) A", out)
        self.assertIn("Source code of this component: https://src", out)

    def test_asset_declared(self):
        good = '<MauiAsset Include="..\\N.md" Link="Resources\\Raw\\N.md" LogicalName="N.md" />'
        self.assertEqual(check.check_asset_declared(good, "N.md"), [])
        self.assertEqual(len(check.check_asset_declared("<ItemGroup/>", "N.md")), 1)


class RealPolicy(unittest.TestCase):
    def test_committed_policy_loads_and_every_text_exists(self):
        policy = check.load_policy(check.HERE / "policy.toml")
        for lid in policy["licenses"]["allowed"]:
            self.assertTrue((check.HERE / "texts" / f"{lid}.txt").is_file(), lid)
        for comp in policy["component"]:
            self.assertTrue((check.HERE / "texts" / f"{comp['text']}.txt").is_file(), comp["name"])


if __name__ == "__main__":
    unittest.main()
