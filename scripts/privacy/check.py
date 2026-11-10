#!/usr/bin/env python3
"""Privacy gate of the app bundles: privacy manifests, required reason APIs, purpose strings.

    python3 scripts/privacy/check.py                      # check the sources (CI, ubuntu, no workloads)
    python3 scripts/privacy/check.py --inventory          # print which bundle uses which API, then check
    python3 scripts/privacy/check.py --bundle ios INFO_PLIST RESOURCES_DIR
                                                          # check a built bundle (used by the Apple build scripts)

Pure file checks, Python standard library only. The rules live in scripts/privacy/policy.toml
(category-to-API table, bundles, purpose strings); this script holds no API names.

Fails (exit status 1) when
  * a privacy manifest is not a valid property list, declares tracking, tracking domains or
    collected data types, or uses a category/reason Apple does not define,
  * a bundle's sources use a required reason API category its manifest does not declare (or the
    bundle has no manifest at all), or lack a reason the code needs (App Group defaults: 1C8F.1),
  * a bundle with the .NET baseline does not declare it,
  * a purpose string key is missing from an Info.plist or from any InfoPlist.strings file.

Tests: test_check.py.
"""

from __future__ import annotations

import argparse
import fnmatch
import plistlib
import re
import sys
import tomllib
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
LANGUAGES = {"csharp": {".cs"}, "swift": {".swift"}}

# Comments are dropped before scanning (a comment may name an API without using it); strings are
# matched first so that "//" inside a string literal is not taken for a comment.
_COMMENTS = re.compile(r'"(?:\\.|[^"\\\n])*"|//[^\n]*|/\*.*?\*/', re.DOTALL)


# --- policy ----------------------------------------------------------------------------------


def load_policy(path: Path) -> dict:
    policy = tomllib.loads(path.read_text(encoding="utf-8"))
    for key in ("categories", "bundle", "purpose", "baseline"):
        if key not in policy:
            raise ValueError(f"policy: missing [{key}]")
    for name, cat in policy["categories"].items():
        for key in ("key", "reasons", "swift", "csharp"):
            if key not in cat:
                raise ValueError(f"policy: category {name} needs '{key}'")
        for hint in cat.get("hint", []):
            if hint["reason"] not in cat["reasons"]:
                raise ValueError(f"policy: category {name}: hint reason {hint['reason']} is not approved")
    for name, reason in policy["baseline"].items():
        if name not in policy["categories"] or reason not in policy["categories"][name]["reasons"]:
            raise ValueError(f"policy: baseline {name}={reason} is not a known category/reason")
    for b in policy["bundle"]:
        for key in ("name", "manifest", "sources"):
            if key not in b:
                raise ValueError(f"policy: bundle {b.get('name', '?')} needs '{key}'")
    return policy


# --- privacy manifests -----------------------------------------------------------------------


def read_plist(path: Path) -> dict:
    with path.open("rb") as f:
        data = plistlib.load(f)
    if not isinstance(data, dict):
        raise ValueError("top level is not a dictionary")
    return data


def declared(manifest: dict, policy: dict) -> dict[str, set[str]]:
    """Category name -> declared reason codes."""
    by_key = {cat["key"]: name for name, cat in policy["categories"].items()}
    result: dict[str, set[str]] = {}
    for entry in manifest.get("NSPrivacyAccessedAPITypes", []):
        name = by_key.get(entry.get("NSPrivacyAccessedAPIType"))
        if name:
            result.setdefault(name, set()).update(entry.get("NSPrivacyAccessedAPITypeReasons", []))
    return result


def check_manifest(path: Path, policy: dict, label: str | None = None) -> list[str]:
    """Problems of one privacy manifest file, independent of any bundle."""
    shown = label or str(path)
    try:
        manifest = read_plist(path)
    except Exception as exc:  # plistlib raises several types for malformed input
        return [f"{shown}: not a valid property list ({exc})"]
    problems = []
    if manifest.get("NSPrivacyTracking") is not False:
        problems.append(f"{shown}: NSPrivacyTracking must be present and false")
    if manifest.get("NSPrivacyTrackingDomains") != []:
        problems.append(f"{shown}: NSPrivacyTrackingDomains must be present and empty")
    if manifest.get("NSPrivacyCollectedDataTypes") != []:
        problems.append(f"{shown}: NSPrivacyCollectedDataTypes must be present and empty (the app collects nothing)")
    known = {cat["key"]: cat for cat in policy["categories"].values()}
    seen = set()
    for entry in manifest.get("NSPrivacyAccessedAPITypes", []):
        key = entry.get("NSPrivacyAccessedAPIType")
        if key not in known:
            problems.append(f"{shown}: unknown API category {key!r}")
            continue
        if key in seen:
            problems.append(f"{shown}: category {key} is declared twice")
        seen.add(key)
        reasons = entry.get("NSPrivacyAccessedAPITypeReasons")
        if not reasons:
            problems.append(f"{shown}: category {key} declares no reason")
            continue
        for reason in reasons:
            if reason not in known[key]["reasons"]:
                problems.append(f"{shown}: reason {reason!r} is not an approved reason of {key}")
    return problems


# --- required reason API scan ----------------------------------------------------------------


def strip_comments(text: str) -> str:
    return _COMMENTS.sub(lambda m: m.group(0) if m.group(0).startswith('"') else _blank(m.group(0)), text)


def _blank(comment: str) -> str:
    """Keep the line structure, so reported line numbers stay right."""
    return re.sub(r"[^\n]", " ", comment)


def symbol_regex(symbol: str) -> re.Pattern:
    body = re.escape(symbol)
    tail = "" if symbol.endswith("(") else r"\b"
    return re.compile(r"(?<!\w)" + body + tail)


def compile_table(policy: dict) -> dict[str, dict[str, list[tuple[str, re.Pattern]]]]:
    table: dict[str, dict[str, list[tuple[str, re.Pattern]]]] = {}
    for name, cat in policy["categories"].items():
        for language in LANGUAGES:
            table.setdefault(language, {})[name] = [(s, symbol_regex(s)) for s in cat[language]]
    return table


def scan_text(text: str, language: str, policy: dict, table=None) -> list[tuple[str, str, int]]:
    """(category, api name or hint, line) for every required reason API use in a source text."""
    table = table or compile_table(policy)
    clean = strip_comments(text)
    hits = []
    for name, symbols in table[language].items():
        for symbol, rx in symbols:
            for m in rx.finditer(clean):
                hits.append((name, symbol, clean.count("\n", 0, m.start()) + 1))
        for hint in policy["categories"][name].get("hint", []):
            for m in re.finditer(hint["pattern"], clean):
                hits.append((name, f"{hint['reason']}:{hint['pattern']}", clean.count("\n", 0, m.start()) + 1))
    return hits


def bundle_files(root: Path, bundle: dict) -> list[Path]:
    files: set[Path] = set()
    for pattern in bundle["sources"]:
        files.update(p for p in root.glob(pattern) if p.is_file())
    exclude = bundle.get("exclude", [])
    kept = [
        p for p in files
        if not any(fnmatch.fnmatchcase(p.relative_to(root).as_posix(), e) for e in exclude)
    ]
    return sorted(kept)


def scan_bundle(root: Path, bundle: dict, policy: dict) -> dict[str, list[tuple[Path, str, int]]]:
    """Category -> [(file, api, line)] over the sources of one bundle."""
    table = compile_table(policy)
    used: dict[str, list[tuple[Path, str, int]]] = {}
    for path in bundle_files(root, bundle):
        language = next((lang for lang, exts in LANGUAGES.items() if path.suffix in exts), None)
        if language is None:
            continue
        text = path.read_text(encoding="utf-8", errors="replace")
        for category, api, line in scan_text(text, language, policy, table):
            used.setdefault(category, []).append((path.relative_to(root), api, line))
    return used


def check_bundle(root: Path, bundle: dict, policy: dict) -> tuple[list[str], dict]:
    problems: list[str] = []
    used = scan_bundle(root, bundle, policy)
    name = bundle["name"]
    manifest_path = bundle["manifest"]
    if not manifest_path:
        for category, hits in sorted(used.items()):
            where = ", ".join(f"{p.as_posix()}:{line} ({api})" for p, api, line in hits[:3])
            problems.append(
                f"bundle {name} has no privacy manifest but its sources use {category}: {where}. "
                "Add a PrivacyInfo.xcprivacy to that target and set it in scripts/privacy/policy.toml.")
        if bundle.get("baseline"):
            problems.append(f"bundle {name} needs a manifest for the .NET baseline")
        return problems, used
    path = root / manifest_path
    if not path.is_file():
        return [f"bundle {name}: manifest {manifest_path} does not exist"], used
    problems.extend(check_manifest(path, policy, manifest_path))
    try:
        have = declared(read_plist(path), policy)
    except Exception:
        return problems, used  # already reported by check_manifest
    if bundle.get("baseline"):
        for category, reason in policy["baseline"].items():
            if reason not in have.get(category, set()):
                problems.append(
                    f"bundle {name}: manifest lacks {category} / {reason}, "
                    "which the .NET runtime needs (Microsoft's MAUI privacy manifest guidance)")
    for category, hits in sorted(used.items()):
        where = ", ".join(f"{p.as_posix()}:{line} ({api})" for p, api, line in hits[:3])
        if category not in have:
            problems.append(f"bundle {name}: sources use {category} but the manifest does not declare it: {where}")
            continue
        for hit_path, api, line in hits:
            if ":" in api and api.split(":", 1)[0] in policy["categories"][category]["reasons"]:
                reason = api.split(":", 1)[0]
                if reason not in have[category]:
                    problems.append(
                        f"bundle {name}: {hit_path.as_posix()}:{line} needs {category} reason {reason}, "
                        f"the manifest declares {sorted(have[category])}")
    return problems, used


# --- purpose strings -------------------------------------------------------------------------

_STRING_LINE = re.compile(r'^\s*"((?:\\.|[^"\\])*)"\s*=\s*"((?:\\.|[^"\\])*)"\s*;\s*$')


def parse_strings(text: str) -> dict[str, str]:
    """Keys and values of an InfoPlist.strings file (one `"key" = "value";` per line)."""
    result = {}
    for line in text.splitlines():
        m = _STRING_LINE.match(line)
        if m:
            result[m.group(1)] = m.group(2)
    return result


def usage_keys(info: dict) -> set[str]:
    return {k for k in info if k.endswith("UsageDescription")}


def check_purpose(root: Path, purpose: dict) -> list[str]:
    name, plist_path = purpose["name"], root / purpose["plist"]
    if not plist_path.is_file():
        return [f"purpose {name}: {purpose['plist']} does not exist"]
    try:
        info = read_plist(plist_path)
    except Exception as exc:
        return [f"{purpose['plist']}: not a valid property list ({exc})"]
    problems = []
    for key in purpose["required"]:
        if not str(info.get(key, "")).strip():
            problems.append(f"{purpose['plist']}: purpose string {key} is missing or empty")
    expected = usage_keys(info) | set(purpose["required"])
    files = sorted(root.glob(purpose["lproj"]))
    if not files:
        problems.append(f"purpose {name}: no InfoPlist.strings files match {purpose['lproj']}")
    all_keys: dict[Path, set[str]] = {}
    for f in files:
        strings = parse_strings(f.read_text(encoding="utf-8"))
        all_keys[f] = set(strings)
        for key in sorted(expected - set(strings)):
            problems.append(f"{f.relative_to(root).as_posix()}: purpose string {key} is missing")
        for key in sorted(k for k in expected if k in strings and not strings[k].strip()):
            problems.append(f"{f.relative_to(root).as_posix()}: purpose string {key} is empty")
    if all_keys:
        union = set().union(*all_keys.values())
        for f, keys in all_keys.items():
            for key in sorted(union - keys - expected):
                problems.append(f"{f.relative_to(root).as_posix()}: key {key} exists in other languages but not here")
    return problems


# --- a built bundle --------------------------------------------------------------------------


def check_built_bundle(root: Path, policy: dict, purpose_name: str, info_plist: Path, resources: Path) -> list[str]:
    """What the Apple build scripts verify on the finished .app: the manifest and the localised
    purpose strings really reached the bundle (the sources are checked by the normal run)."""
    purpose = next((p for p in policy["purpose"] if p["name"] == purpose_name), None)
    if purpose is None:
        return [f"no [[purpose]] named {purpose_name} in policy.toml"]
    problems = []
    manifest = resources / "PrivacyInfo.xcprivacy"
    if not manifest.is_file():
        problems.append(f"{manifest}: the privacy manifest is not in the bundle")
    else:
        problems.extend(check_manifest(manifest, policy))
    try:
        info = read_plist(info_plist)
    except Exception as exc:
        return problems + [f"{info_plist}: not readable ({exc})"]
    for key in purpose["required"]:
        if not str(info.get(key, "")).strip():
            problems.append(f"{info_plist}: purpose string {key} is missing from the built Info.plist")
    for source in sorted(root.glob(purpose["lproj"])):
        language = source.parent.name
        built = resources / language / "InfoPlist.strings"
        if not built.is_file():
            problems.append(f"{built}: the localised purpose strings of {language} are not in the bundle")
    return problems


# --- command line ----------------------------------------------------------------------------


def run(root: Path, policy: dict, inventory: bool = False) -> int:
    problems: list[str] = []
    seen_manifests: set[str] = set()
    for bundle in policy["bundle"]:
        found, used = check_bundle(root, bundle, policy)
        problems.extend(found)
        if inventory:
            declared_cats = {}
            if bundle["manifest"] and (root / bundle["manifest"]).is_file():
                try:
                    declared_cats = declared(read_plist(root / bundle["manifest"]), policy)
                except Exception:
                    pass
            print(f"bundle {bundle['name']}: manifest {bundle['manifest'] or '(none)'}")
            for category in sorted(set(used) | set(declared_cats)):
                reasons = ",".join(sorted(declared_cats.get(category, []))) or "-"
                evidence = "; ".join(sorted({f"{p.as_posix()}:{line} {api}" for p, api, line in used.get(category, [])})[:4])
                print(f"  {category:<16} declared: {reasons:<10} sources: {evidence or '(only the .NET baseline)'}")
        if bundle["manifest"]:
            seen_manifests.add(bundle["manifest"])
    for purpose in policy["purpose"]:
        problems.extend(check_purpose(root, purpose))
    # Every manifest in the repository must be known to the policy, so none escapes the checks.
    for path in sorted(root.rglob("*.xcprivacy")):
        rel = path.relative_to(root).as_posix()
        if "/obj/" in f"/{rel}" or "/bin/" in f"/{rel}" or rel.startswith("studylife/"):
            continue
        if rel not in seen_manifests:
            problems.append(f"{rel}: privacy manifest that no [[bundle]] in policy.toml refers to")
    for p in problems:
        print(f"::error::{p}")
    if problems:
        print(f"{len(problems)} problem(s)")
        return 1
    print("OK: privacy manifests, required reason APIs and purpose strings are consistent.")
    return 0


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--policy", type=Path, default=HERE / "policy.toml")
    ap.add_argument("--root", type=Path, default=ROOT)
    ap.add_argument("--inventory", action="store_true", help="print the API use per bundle")
    ap.add_argument("--bundle", nargs=3, metavar=("PURPOSE", "INFO_PLIST", "RESOURCES_DIR"),
                    help="check a built bundle instead of the sources")
    args = ap.parse_args(argv)
    policy = load_policy(args.policy)
    if args.bundle:
        purpose, info, resources = args.bundle
        problems = check_built_bundle(args.root, policy, purpose, Path(info), Path(resources))
        for p in problems:
            print(f"::error::{p}")
        if not problems:
            print(f"OK: the built bundle carries the privacy manifest and the {purpose} purpose strings.")
        return 1 if problems else 0
    return run(args.root, policy, args.inventory)


if __name__ == "__main__":
    sys.exit(main())
