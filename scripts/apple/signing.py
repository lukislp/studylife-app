#!/usr/bin/env python3
"""Provisioning profile checks and entitlement generation for the Apple release jobs.

Used by scripts/apple/release-ios.sh and scripts/apple/release-maccatalyst.sh. Standard
library only, so the unit tests (scripts/apple/test_signing.py) run on any runner.

A provisioning profile is a CMS (PKCS#7) envelope around an XML plist. Reading it here does
not verify Apple's CMS signature - codesign and App Store Connect do that - it only extracts
the plist to check that the right profile was supplied for the right bundle.

Commands:

  profile <file>
      Print a JSON summary (name, team, bundle id, kind, expiry, entitlements).

  check-profile <file> --bundle-id ID --team TEAM --kind app-store|developer-id
                --platform ios|maccatalyst
      Fail with a readable message unless the profile matches.

  entitlements --declared PLIST --out PLIST [--profile FILE]
      Write the entitlements to sign with: the declared ones from the repo plus the identity
      keys the profile grants (application identifier, team, get-task-allow,
      beta-reports-active). Every declared, profile-gated entitlement must be allowed by the
      profile, otherwise the command fails and names the capability to enable. Without
      --profile (ad-hoc test builds) only the declared entitlements are written.

  find-profile <dir> --bundle-id ID --team TEAM
      Print the path of the profile in <dir> whose application identifier is TEAM.ID.
"""

from __future__ import annotations

import argparse
import datetime as dt
import fnmatch
import json
import plistlib
import sys
from pathlib import Path

# Keys copied from the profile into the signing entitlements (what Xcode adds on export).
IDENTITY_KEYS = (
    "application-identifier",
    "com.apple.application-identifier",
    "com.apple.developer.team-identifier",
    "get-task-allow",
    "com.apple.security.get-task-allow",
    "beta-reports-active",
)

# macOS sandbox and hardened-runtime entitlements are not granted through a profile.
# App groups are, even though they share the prefix.
UNGATED_PREFIX = "com.apple.security."
GATED_EXCEPTIONS = ("com.apple.security.application-groups",)


class SigningError(Exception):
    pass


def load_plist_bytes(data: bytes) -> dict:
    if data.startswith(b"\xef\xbb\xbf"):
        data = data[3:]
    return plistlib.loads(data)


def read_profile(path: Path) -> dict:
    data = path.read_bytes()
    start = data.find(b"<?xml")
    end = data.find(b"</plist>")
    if start < 0 or end < 0:
        raise SigningError(f"{path}: not a provisioning profile (no embedded plist found)")
    try:
        return load_plist_bytes(data[start : end + len(b"</plist>")])
    except Exception as exc:  # plistlib raises several types
        raise SigningError(f"{path}: embedded plist does not parse: {exc}") from exc


def app_identifier(profile: dict) -> str:
    ents = profile.get("Entitlements", {})
    return ents.get("application-identifier") or ents.get("com.apple.application-identifier") or ""


def platform_family(profile: dict) -> str:
    """iOS/watchOS profiles use 'application-identifier', macOS (incl. Mac Catalyst) the
    'com.apple.application-identifier' key."""
    ents = profile.get("Entitlements", {})
    if "com.apple.application-identifier" in ents:
        return "maccatalyst"
    if "application-identifier" in ents:
        return "ios"
    return "unknown"


def profile_kind(profile: dict) -> str:
    ents = profile.get("Entitlements", {})
    debuggable = ents.get("get-task-allow") or ents.get("com.apple.security.get-task-allow")
    if profile.get("ProvisionsAllDevices"):
        return "developer-id" if platform_family(profile) == "maccatalyst" else "enterprise"
    if profile.get("ProvisionedDevices"):
        return "development" if debuggable else "ad-hoc"
    if debuggable:
        return "development"
    return "app-store"


def summary(profile: dict) -> dict:
    team_ids = profile.get("TeamIdentifier") or []
    appid = app_identifier(profile)
    team = team_ids[0] if team_ids else ""
    bundle = appid[len(team) + 1 :] if team and appid.startswith(team + ".") else appid
    expires = profile.get("ExpirationDate")
    return {
        "name": profile.get("Name", ""),
        "uuid": profile.get("UUID", ""),
        "team": team,
        "app_identifier": appid,
        "bundle_id": bundle,
        "platform": platform_family(profile),
        "kind": profile_kind(profile),
        "expires": expires.isoformat() if isinstance(expires, dt.datetime) else "",
        "entitlements": sorted(profile.get("Entitlements", {}).keys()),
    }


def check_profile(profile: dict, path: str, bundle_id: str, team: str, kind: str, platform: str,
                  now: dt.datetime | None = None) -> list[str]:
    """Return warnings; raise SigningError on a mismatch."""
    info = summary(profile)
    problems = []
    if info["team"] != team:
        problems.append(f"team is {info['team'] or 'missing'}, expected {team}")
    if info["app_identifier"] != f"{team}.{bundle_id}":
        problems.append(f"app identifier is {info['app_identifier'] or 'missing'}, expected {team}.{bundle_id}")
    if info["platform"] != platform:
        problems.append(f"platform is {info['platform']}, expected {platform}")
    if info["kind"] != kind:
        problems.append(f"profile type is {info['kind']}, expected {kind}")
    expires = profile.get("ExpirationDate")
    now = now or dt.datetime.now(dt.timezone.utc).replace(tzinfo=None)
    warnings = []
    if isinstance(expires, dt.datetime):
        exp = expires.replace(tzinfo=None)
        if exp <= now:
            problems.append(f"expired on {exp:%Y-%m-%d}")
        elif exp - now < dt.timedelta(days=30):
            warnings.append(f"{path} ({info['name']}) expires on {exp:%Y-%m-%d} - renew it soon")
    if problems:
        raise SigningError(f"{path} ({info['name'] or 'unnamed'}): " + "; ".join(problems))
    return warnings


def _is_gated(key: str) -> bool:
    return not key.startswith(UNGATED_PREFIX) or key in GATED_EXCEPTIONS


def _allowed(declared, granted) -> bool:
    if isinstance(declared, bool):
        return granted is True or (declared is False)
    if isinstance(declared, str):
        if isinstance(granted, list):
            return any(fnmatch.fnmatchcase(declared, g) for g in granted if isinstance(g, str))
        return isinstance(granted, str) and fnmatch.fnmatchcase(declared, granted)
    if isinstance(declared, list):
        return all(_allowed(item, granted) for item in declared)
    return declared == granted


def build_entitlements(declared: dict, profile: dict | None) -> dict:
    result = dict(declared)
    if profile is None:
        return result
    granted = profile.get("Entitlements", {})
    missing = []
    for key, value in declared.items():
        if not _is_gated(key):
            continue
        if key not in granted:
            missing.append(f"{key} (not in the profile - enable the capability on the App ID and regenerate the profile)")
        elif not _allowed(value, granted[key]):
            missing.append(f"{key}={value!r} (profile allows {granted[key]!r})")
    if missing:
        raise SigningError(f"profile '{profile.get('Name', '')}' does not grant: " + "; ".join(missing))
    for key in IDENTITY_KEYS:
        if key in granted:
            result[key] = granted[key]
    return result


def find_profile(directory: Path, bundle_id: str, team: str) -> Path:
    wanted = f"{team}.{bundle_id}"
    matches = []
    for path in sorted(directory.iterdir()):
        if path.suffix not in (".mobileprovision", ".provisionprofile"):
            continue
        if app_identifier(read_profile(path)) == wanted:
            matches.append(path)
    if not matches:
        raise SigningError(f"no profile for {wanted} in {directory}")
    if len(matches) > 1:
        raise SigningError(f"more than one profile for {wanted}: {', '.join(map(str, matches))}")
    return matches[0]


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    sub = parser.add_subparsers(dest="command", required=True)

    p = sub.add_parser("profile")
    p.add_argument("file", type=Path)

    c = sub.add_parser("check-profile")
    c.add_argument("file", type=Path)
    c.add_argument("--bundle-id", required=True)
    c.add_argument("--team", required=True)
    c.add_argument("--kind", required=True, choices=["app-store", "developer-id"])
    c.add_argument("--platform", required=True, choices=["ios", "maccatalyst"])

    e = sub.add_parser("entitlements")
    e.add_argument("--declared", required=True, type=Path)
    e.add_argument("--out", required=True, type=Path)
    e.add_argument("--profile", type=Path)

    f = sub.add_parser("find-profile")
    f.add_argument("directory", type=Path)
    f.add_argument("--bundle-id", required=True)
    f.add_argument("--team", required=True)

    args = parser.parse_args(argv)
    try:
        if args.command == "profile":
            print(json.dumps(summary(read_profile(args.file)), indent=2))
        elif args.command == "check-profile":
            for warning in check_profile(read_profile(args.file), str(args.file), args.bundle_id,
                                         args.team, args.kind, args.platform):
                print(f"::warning::{warning}")
        elif args.command == "entitlements":
            declared = load_plist_bytes(args.declared.read_bytes())
            profile = read_profile(args.profile) if args.profile else None
            args.out.write_bytes(plistlib.dumps(build_entitlements(declared, profile)))
        elif args.command == "find-profile":
            print(find_profile(args.directory, args.bundle_id, args.team))
    except SigningError as exc:
        print(f"::error::{exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
