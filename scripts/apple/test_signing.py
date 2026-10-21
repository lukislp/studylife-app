"""Unit tests for scripts/apple/signing.py - run with `python3 -m unittest discover -s scripts/apple`.

The profiles here are synthetic: a plist wrapped in junk bytes, the same shape as the CMS
envelope of a real profile as far as signing.py is concerned.
"""

import datetime as dt
import plistlib
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import signing  # noqa: E402

TEAM = "ABCDE12345"
FUTURE = dt.datetime(2099, 1, 1)
NOW = dt.datetime(2026, 10, 21)
REPO_ENTITLEMENTS = Path(__file__).resolve().parent / "entitlements"


def ios_profile(bundle="app.studylife.mobile", **extra_ents):
    ents = {
        "application-identifier": f"{TEAM}.{bundle}",
        "com.apple.developer.team-identifier": TEAM,
        "get-task-allow": False,
        "beta-reports-active": True,
        "keychain-access-groups": [f"{TEAM}.*"],
        "aps-environment": "production",
        "com.apple.developer.healthkit": True,
        "com.apple.developer.healthkit.access": [],
        "com.apple.security.application-groups": ["group.app.studylife.mobile"],
    }
    ents.update(extra_ents)
    return {"Name": "StudyLife iOS App Store", "UUID": "u-1", "TeamIdentifier": [TEAM],
            "ExpirationDate": FUTURE, "Platform": ["iOS"], "Entitlements": ents}


def mac_profile(bundle="app.studylife.mobile", developer_id=False):
    ents = {
        "com.apple.application-identifier": f"{TEAM}.{bundle}",
        "com.apple.developer.team-identifier": TEAM,
        "keychain-access-groups": [f"{TEAM}.*"],
    }
    profile = {"Name": "StudyLife Mac", "TeamIdentifier": [TEAM], "ExpirationDate": FUTURE,
               "Entitlements": ents}
    if developer_id:
        profile["ProvisionsAllDevices"] = True
    return profile


def write_profile(directory: Path, name: str, profile: dict) -> Path:
    path = directory / name
    path.write_bytes(b"\x30\x82\x01\x02junk-before" + plistlib.dumps(profile) + b"junk-after\x00")
    return path


class ReadProfile(unittest.TestCase):
    def test_extracts_plist_from_envelope(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = write_profile(Path(tmp), "a.mobileprovision", ios_profile())
            info = signing.summary(signing.read_profile(path))
        self.assertEqual(info["bundle_id"], "app.studylife.mobile")
        self.assertEqual(info["team"], TEAM)
        self.assertEqual(info["platform"], "ios")
        self.assertEqual(info["kind"], "app-store")

    def test_rejects_non_profile(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "x.mobileprovision"
            path.write_bytes(b"not a profile")
            with self.assertRaises(signing.SigningError):
                signing.read_profile(path)


class Kinds(unittest.TestCase):
    def test_development_profile(self):
        p = ios_profile(**{"get-task-allow": True})
        p["ProvisionedDevices"] = ["00008140-001605D11E6A801C"]
        self.assertEqual(signing.profile_kind(p), "development")

    def test_ad_hoc_profile(self):
        p = ios_profile()
        p["ProvisionedDevices"] = ["00008140-001605D11E6A801C"]
        self.assertEqual(signing.profile_kind(p), "ad-hoc")

    def test_mac_kinds(self):
        self.assertEqual(signing.profile_kind(mac_profile()), "app-store")
        self.assertEqual(signing.profile_kind(mac_profile(developer_id=True)), "developer-id")
        self.assertEqual(signing.platform_family(mac_profile()), "maccatalyst")


class CheckProfile(unittest.TestCase):
    def check(self, profile, bundle="app.studylife.mobile", kind="app-store", platform="ios"):
        return signing.check_profile(profile, "p", bundle, TEAM, kind, platform, now=NOW)

    def test_matching_profile_passes(self):
        self.assertEqual(self.check(ios_profile()), [])

    def test_wrong_bundle(self):
        with self.assertRaisesRegex(signing.SigningError, "app identifier"):
            self.check(ios_profile("app.studylife.mobile.widgets"))

    def test_wrong_team(self):
        p = ios_profile()
        p["TeamIdentifier"] = ["ZZZZZ99999"]
        with self.assertRaisesRegex(signing.SigningError, "team is ZZZZZ99999"):
            self.check(p)

    def test_ios_profile_given_for_mac(self):
        with self.assertRaisesRegex(signing.SigningError, "platform is ios"):
            self.check(ios_profile(), platform="maccatalyst")

    def test_development_profile_rejected_for_testflight(self):
        p = ios_profile(**{"get-task-allow": True})
        with self.assertRaisesRegex(signing.SigningError, "profile type is development"):
            self.check(p)

    def test_expired(self):
        p = ios_profile()
        p["ExpirationDate"] = dt.datetime(2026, 1, 1)
        with self.assertRaisesRegex(signing.SigningError, "expired"):
            self.check(p)

    def test_expiring_soon_warns(self):
        p = ios_profile()
        p["ExpirationDate"] = NOW + dt.timedelta(days=10)
        self.assertEqual(len(self.check(p)), 1)


class Entitlements(unittest.TestCase):
    def test_merges_identity_keys(self):
        declared = {"com.apple.security.application-groups": ["group.app.studylife.mobile"]}
        ents = signing.build_entitlements(declared, ios_profile("app.studylife.mobile.widgets"))
        self.assertEqual(ents["application-identifier"], f"{TEAM}.app.studylife.mobile.widgets")
        self.assertIs(ents["get-task-allow"], False)
        self.assertIs(ents["beta-reports-active"], True)
        # Wildcard grants from the profile are never copied into the signature.
        self.assertNotIn("keychain-access-groups", ents)
        self.assertNotIn("aps-environment", ents)

    def test_missing_capability_is_named(self):
        profile = ios_profile()
        del profile["Entitlements"]["com.apple.developer.healthkit"]
        with self.assertRaisesRegex(signing.SigningError, "com.apple.developer.healthkit"):
            signing.build_entitlements({"com.apple.developer.healthkit": True}, profile)

    def test_wrong_app_group(self):
        with self.assertRaisesRegex(signing.SigningError, "group.other"):
            signing.build_entitlements(
                {"com.apple.security.application-groups": ["group.other"]}, ios_profile())

    def test_development_push_rejected(self):
        with self.assertRaisesRegex(signing.SigningError, "aps-environment"):
            signing.build_entitlements({"aps-environment": "development"}, ios_profile())

    def test_sandbox_keys_need_no_profile_grant(self):
        declared = {"com.apple.security.app-sandbox": True, "com.apple.security.network.client": True}
        ents = signing.build_entitlements(declared, mac_profile())
        self.assertIs(ents["com.apple.security.app-sandbox"], True)
        self.assertEqual(ents["com.apple.application-identifier"], f"{TEAM}.app.studylife.mobile")

    def test_without_profile_only_declared(self):
        declared = {"com.apple.security.cs.allow-jit": True}
        self.assertEqual(signing.build_entitlements(declared, None), declared)


class RepoEntitlementFiles(unittest.TestCase):
    """The committed entitlement files must be satisfiable by profiles made as the README says."""

    def load(self, name):
        return signing.load_plist_bytes((REPO_ENTITLEMENTS / name).read_bytes())

    def test_ios_app(self):
        signing.build_entitlements(self.load("ios-app.plist"), ios_profile())

    def test_extensions(self):
        signing.build_entitlements(self.load("ios-extension.plist"),
                                   ios_profile("app.studylife.mobile.watchkitapp"))

    def test_maccatalyst_app_store(self):
        path = Path(__file__).resolve().parents[2] / "src/StudyLife.App/Platforms/MacCatalyst/Entitlements.plist"
        ents = signing.build_entitlements(signing.load_plist_bytes(path.read_bytes()), mac_profile())
        self.assertIs(ents["com.apple.security.app-sandbox"], True)

    def test_developer_id_not_sandboxed(self):
        ents = self.load("maccatalyst-developer-id.plist")
        self.assertNotIn("com.apple.security.app-sandbox", ents)
        self.assertIs(ents.get("com.apple.security.cs.allow-jit"), True)


class FindProfile(unittest.TestCase):
    def test_finds_by_bundle(self):
        with tempfile.TemporaryDirectory() as tmp:
            d = Path(tmp)
            write_profile(d, "app.mobileprovision", ios_profile())
            widgets = write_profile(d, "widgets.mobileprovision", ios_profile("app.studylife.mobile.widgets"))
            self.assertEqual(signing.find_profile(d, "app.studylife.mobile.widgets", TEAM), widgets)
            with self.assertRaisesRegex(signing.SigningError, "no profile"):
                signing.find_profile(d, "app.studylife.mobile.share", TEAM)

    def test_cli_exit_codes(self):
        with tempfile.TemporaryDirectory() as tmp:
            d = Path(tmp)
            path = write_profile(d, "app.mobileprovision", ios_profile())
            ok = signing.main(["check-profile", str(path), "--bundle-id", "app.studylife.mobile",
                               "--team", TEAM, "--kind", "app-store", "--platform", "ios"])
            bad = signing.main(["check-profile", str(path), "--bundle-id", "app.studylife.mobile",
                                "--team", TEAM, "--kind", "app-store", "--platform", "maccatalyst"])
            self.assertEqual((ok, bad), (0, 1))


if __name__ == "__main__":
    unittest.main()
