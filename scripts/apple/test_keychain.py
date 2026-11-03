"""Tests for `scripts/apple/keychain.sh check` - run with `python3 -m unittest discover -s scripts/apple`.

Only the secret check is exercised: it is plain bash and runs on any runner. All values are
dummies; nothing is decoded or imported.
"""

import os
import shutil
import subprocess
import unittest
from pathlib import Path

KEYCHAIN = Path(__file__).resolve().parent / "keychain.sh"
BASH = shutil.which("bash")

MACOS_SECRETS = {
    "APPLE_TEAM_ID": "ABCDE12345",
    "APPLE_DISTRIBUTION_P12_BASE64": "ZHVtbXk=",
    "APPLE_DISTRIBUTION_P12_PASSWORD": "dummy",
    "APPLE_MAC_INSTALLER_P12_BASE64": "ZHVtbXk=",
    "APPLE_MAC_INSTALLER_P12_PASSWORD": "dummy",
    "APPLE_PROFILE_MAC_BASE64": "ZHVtbXk=",
    "ASC_API_KEY_P8_BASE64": "ZHVtbXk=",
    "ASC_API_KEY_ID": "ABCDE12345",
    "ASC_API_ISSUER_ID": "00000000-0000-0000-0000-000000000000",
}
DEVELOPER_ID = {
    "APPLE_DEVELOPER_ID_P12_BASE64": "ZHVtbXk=",
    "APPLE_DEVELOPER_ID_P12_PASSWORD": "dummy",
}


@unittest.skipIf(BASH is None, "bash not available")
class KeychainCheckTest(unittest.TestCase):
    def run_check(self, platform="macos", **env):
        clean = {k: v for k, v in os.environ.items()
                 if not k.startswith(("APPLE_", "ASC_", "REQUIRE_DEVELOPER_ID"))}
        clean.update(env)
        return subprocess.run([BASH, str(KEYCHAIN), "check", platform], env=clean,
                              capture_output=True, text=True, timeout=30)

    def test_macos_without_developer_id_is_optional(self):
        result = self.run_check(**MACOS_SECRETS)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("No Developer ID secrets", result.stdout)

    def test_macos_with_developer_id(self):
        result = self.run_check(**MACOS_SECRETS, **DEVELOPER_ID)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("will be signed and notarized", result.stdout)

    def test_only_one_developer_id_secret_fails(self):
        for name in DEVELOPER_ID:
            with self.subTest(name=name):
                result = self.run_check(**MACOS_SECRETS, **{name: DEVELOPER_ID[name]})
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("set both or neither", result.stderr)

    def test_zip_only_requires_developer_id(self):
        result = self.run_check(**MACOS_SECRETS, REQUIRE_DEVELOPER_ID="true")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("APPLE_DEVELOPER_ID_P12_BASE64", result.stderr)
        self.assertIn("APPLE_DEVELOPER_ID_P12_PASSWORD", result.stderr)
        self.assertIn("::error::", result.stderr)

    def test_zip_only_with_developer_id(self):
        result = self.run_check(**MACOS_SECRETS, **DEVELOPER_ID, REQUIRE_DEVELOPER_ID="true")
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_require_false_keeps_it_optional(self):
        result = self.run_check(**MACOS_SECRETS, REQUIRE_DEVELOPER_ID="false")
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_missing_common_secret_is_named(self):
        env = dict(MACOS_SECRETS)
        del env["ASC_API_KEY_ID"]
        result = self.run_check(**env)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("ASC_API_KEY_ID", result.stderr)


if __name__ == "__main__":
    unittest.main()
