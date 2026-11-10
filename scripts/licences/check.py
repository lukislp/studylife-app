#!/usr/bin/env python3
"""Licence inventory, policy gate and THIRD-PARTY-NOTICES.md for the app bundle.

    python3 scripts/licences/check.py            # restore, check the policy, verify the notices file
    python3 scripts/licences/check.py --update   # same, then rewrite THIRD-PARTY-NOTICES.md
    python3 scripts/licences/check.py --assets obj/a.json obj/b.json   # skip the restore

The package set comes from the build system: `dotnet restore -p:TargetFramework=<tfm>` writes
obj/project.assets.json, which lists the whole resolved NuGet graph. Each package's licence is read
from its .nuspec in the NuGet cache (an SPDX expression, or a licence file shipped in the package).
The rules live in scripts/licences/policy.toml - this script holds no package names.

Exit status 1 on a policy violation, an unreadable licence, a missing licence text, a csproj that
does not ship the notices file, or a stale notices file. Tests: test_check.py.
"""

from __future__ import annotations

import argparse
import difflib
import fnmatch
import json
import re
import subprocess
import sys
import tomllib
import xml.etree.ElementTree as ET
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
UNKNOWN = "UNKNOWN"
FILE_LICENSE = "LicenseRef-package-file"


# --- policy ----------------------------------------------------------------------------------


def load_policy(path: Path) -> dict:
    policy = tomllib.loads(path.read_text(encoding="utf-8"))
    for key in ("bundle", "licenses"):
        if key not in policy:
            raise ValueError(f"policy: missing [{key}]")
    policy.setdefault("exception", [])
    policy.setdefault("component", [])
    for exc in policy["exception"]:
        for key in ("package", "license", "reason", "source"):
            if not exc.get(key):
                raise ValueError(f"policy: exception {exc.get('package', '?')} needs '{key}'")
        if is_denied(exc["license"], policy["licenses"]["denied"]):
            raise ValueError(f"policy: {exc['package']}: a denied licence cannot be an exception")
    return policy


def tokenize(expression: str) -> list[str]:
    return re.findall(r"\(|\)|[A-Za-z0-9.+:\-]+", expression)


def license_ids(expression: str) -> list[str]:
    """Every SPDX id in an expression (operators and WITH exceptions dropped)."""
    ids, tokens, i = [], tokenize(expression), 0
    while i < len(tokens):
        tok = tokens[i]
        if tok.upper() == "WITH":
            i += 2  # skip the exception id
            continue
        if tok not in ("(", ")") and tok.upper() not in ("AND", "OR"):
            ids.append(tok)
        i += 1
    return ids


def is_denied(expression: str, denied: list[str]) -> bool:
    return any(
        fnmatch.fnmatchcase(i.lower(), pat.lower()) for i in license_ids(expression) for pat in denied
    )


def evaluate(expression: str, ok) -> bool:
    """Evaluate an SPDX expression: OR = any alternative, AND = all, `ok(id)` judges one id."""
    tokens = tokenize(expression)
    pos = 0

    def peek():
        return tokens[pos] if pos < len(tokens) else None

    def take():
        nonlocal pos
        pos += 1
        return tokens[pos - 1]

    def parse_or():
        result = parse_and()
        while peek() and peek().upper() == "OR":
            take()
            right = parse_and()
            result = result or right
        return result

    def parse_and():
        result = parse_atom()
        while peek() and peek().upper() == "AND":
            take()
            right = parse_atom()
            result = result and right
        return result

    def parse_atom():
        tok = peek()
        if tok is None:
            raise ValueError(f"bad licence expression: {expression!r}")
        if tok == "(":
            take()
            result = parse_or()
            if peek() != ")":
                raise ValueError(f"bad licence expression: {expression!r}")
            take()
            return result
        if tok in (")",) or tok.upper() in ("AND", "OR", "WITH"):
            raise ValueError(f"bad licence expression: {expression!r}")
        take()
        if peek() and peek().upper() == "WITH":
            take()
            take()
        return ok(tok)

    result = parse_or()
    if pos != len(tokens):
        raise ValueError(f"bad licence expression: {expression!r}")
    return result


def classify(pkg: dict, policy: dict) -> tuple[str, str | None]:
    """Returns ("ok" | "exception" | "denied" | "unknown" | "not-allowed", detail)."""
    spdx = pkg["license"]
    lic = policy["licenses"]
    if spdx == UNKNOWN:
        return "unknown", pkg.get("license_note")
    if is_denied(spdx, lic["denied"]):
        return "denied", None
    allowed = {a.lower() for a in lic["allowed"]}
    if evaluate(spdx, lambda i: i.lower() in allowed):
        return "ok", None
    for exc in policy["exception"]:
        if exc["package"].lower() == pkg["id"].lower() and exc["license"] == spdx:
            return "exception", exc["reason"]
    return "not-allowed", None


# --- inventory from the build system ---------------------------------------------------------


def restore(csproj: Path, tfm: str) -> Path:
    subprocess.run(
        ["dotnet", "restore", str(csproj), f"-p:TargetFramework={tfm}", "--nologo", "-v", "q"],
        check=True,
    )
    return csproj.parent / "obj" / "project.assets.json"


def read_assets(path: Path) -> tuple[list[dict], list[Path]]:
    data = json.loads(path.read_text(encoding="utf-8-sig"))
    folders = [Path(f) for f in data.get("packageFolders", {})]
    packages = []
    for key, lib in data.get("libraries", {}).items():
        if lib.get("type") != "package":
            continue
        name, _, version = key.partition("/")
        packages.append({"id": name, "version": version, "path": lib["path"]})
    return packages, folders


def find_package_dir(pkg: dict, folders: list[Path]) -> Path:
    for folder in folders:
        candidate = folder / pkg["path"]
        if candidate.is_dir():
            return candidate
    raise FileNotFoundError(f"{pkg['id']} {pkg['version']}: not in {[str(f) for f in folders]}")


def _local(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def parse_nuspec(text: str) -> dict:
    meta = {}
    root = ET.fromstring(text)
    for md in root:
        if _local(md.tag) != "metadata":
            continue
        for el in md:
            name = _local(el.tag)
            if name == "license":
                meta["license_type"] = el.get("type")
                meta["license_value"] = (el.text or "").strip()
            elif name in ("licenseUrl", "projectUrl", "copyright", "authors"):
                meta[name] = (el.text or "").strip()
    return meta


def resolve_license(meta: dict, package_dir: Path) -> tuple[str, str | None, str | None]:
    """(spdx or UNKNOWN, note, embedded licence file text)."""
    ltype, value = meta.get("license_type"), meta.get("license_value", "")
    if ltype == "expression" and value:
        return value, None, None
    if ltype == "file" and value:
        file = package_dir / value
        if file.is_file():
            return FILE_LICENSE, value, file.read_text(encoding="utf-8", errors="replace").strip()
        return UNKNOWN, f"licence file {value} missing from the package", None
    if meta.get("licenseUrl"):
        return UNKNOWN, f"only a licenseUrl: {meta['licenseUrl']}", None
    return UNKNOWN, "no licence information in the nuspec", None


def build_inventory(assets: dict[str, Path]) -> list[dict]:
    """assets: framework -> project.assets.json. One entry per (id, version)."""
    merged: dict[tuple[str, str], dict] = {}
    for tfm, path in assets.items():
        packages, folders = read_assets(path)
        for pkg in packages:
            key = (pkg["id"].lower(), pkg["version"])
            if key in merged:
                merged[key]["frameworks"].add(tfm)
                continue
            pdir = find_package_dir(pkg, folders)
            nuspec = next(pdir.glob("*.nuspec"), None)
            if nuspec is None:
                raise FileNotFoundError(f"{pkg['id']} {pkg['version']}: no .nuspec in {pdir}")
            meta = parse_nuspec(nuspec.read_text(encoding="utf-8"))
            spdx, note, file_text = resolve_license(meta, pdir)
            merged[key] = {
                "id": pkg["id"],
                "version": pkg["version"],
                "license": spdx,
                "license_note": note,
                "license_file_text": file_text,
                "project_url": meta.get("projectUrl", ""),
                "copyright": meta.get("copyright", ""),
                "frameworks": {tfm},
            }
    return sorted(merged.values(), key=lambda p: (p["id"].lower(), p["version"]))


# --- checks ----------------------------------------------------------------------------------


def check_inventory(inventory: list[dict], policy: dict, texts_dir: Path) -> list[str]:
    problems = []
    for pkg in inventory:
        status, detail = classify(pkg, policy)
        label = f"{pkg['id']} {pkg['version']} ({pkg['license']})"
        if status == "unknown":
            problems.append(f"{label}: licence could not be verified - {detail}")
        elif status == "denied":
            problems.append(f"{label}: denied licence family")
        elif status == "not-allowed":
            problems.append(f"{label}: not in the allowed list and no exception in the policy")
        elif status == "ok":
            for lid in license_ids(pkg["license"]):
                if lid.lower() in {a.lower() for a in policy["licenses"]["allowed"]} and not (
                    texts_dir / f"{lid}.txt"
                ).is_file():
                    problems.append(f"{label}: no licence text texts/{lid}.txt")
    for comp in policy["component"]:
        if is_denied(comp["license"], policy["licenses"]["denied"]):
            problems.append(f"component {comp['name']}: denied licence family")
        if not (texts_dir / f"{comp['text']}.txt").is_file():
            problems.append(f"component {comp['name']}: no licence text texts/{comp['text']}.txt")
    return problems


def check_asset_declared(csproj_text: str, logical_name: str) -> list[str]:
    for m in re.finditer(r"<MauiAsset\b[^>]*>", csproj_text):
        if f'LogicalName="{logical_name}"' in m.group(0):
            return []
    return [f"csproj does not ship the notices file (no MauiAsset with LogicalName=\"{logical_name}\")"]


# --- notices file ----------------------------------------------------------------------------


def render_notices(inventory: list[dict], policy: dict, texts_dir: Path) -> str:
    short = {"net10.0-ios": "iOS", "net10.0-maccatalyst": "Mac Catalyst"}
    frameworks = " and ".join(short.get(f, f) for f in policy["bundle"]["frameworks"])
    out = [
        "# Third-party notices",
        "",
        "<!-- Generated by scripts/licences/check.py --update from the NuGet graph and",
        "     scripts/licences/policy.toml. Do not edit by hand; CI fails when this file is stale. -->",
        "",
        f"This file lists the third-party software in the StudyLife app bundle for {frameworks}.",
        "The app's own code, and the StudyLife client and shared libraries compiled into it",
        "(github.com/lukislp/studylife), are copyright Lukas Koerber under AGPL-3.0 (see LICENSE).",
        "No third-party package in this list is under a GPL, AGPL or LGPL licence.",
        "",
        "## NuGet packages",
        "",
        "| Package | Version | Licence | Project | Used in |",
        "| --- | --- | --- | --- | --- |",
    ]
    for p in inventory:
        used = ", ".join(sorted(short.get(f, f) for f in p["frameworks"]))
        url = p["project_url"] or "-"
        out.append(f"| {p['id']} | {p['version']} | {p['license']} | {url} | {used} |")

    by_license: dict[str, list[dict]] = {}
    for p in inventory:
        status, _ = classify(p, policy)
        if status == "ok":
            for lid in license_ids(p["license"]):
                by_license.setdefault(lid, []).append(p)

    for lid in sorted(by_license):
        out += ["", f"## {lid}", "", "Applies to:", ""]
        for p in by_license[lid]:
            cr = f" - {p['copyright']}" if p["copyright"] else ""
            out.append(f"- {p['id']} {p['version']}{cr}")
        out += ["", "```text", (texts_dir / f"{lid}.txt").read_text(encoding="utf-8").strip(), "```"]

    for exc in policy["exception"]:
        matches = [p for p in inventory if p["id"].lower() == exc["package"].lower()]
        if not matches:
            continue
        out += ["", f"## {exc['license']}: {exc['package']}", ""]
        for p in matches:
            cr = f" - {p['copyright']}" if p["copyright"] else ""
            out.append(f"- {p['id']} {p['version']}{cr}")
        out += [
            "",
            exc["reason"],
            "",
            f"Source code of this component: {exc['source']}",
            f"Licence text: https://spdx.org/licenses/{exc['license']}.html",
        ]

    for p in inventory:
        if p["license_file_text"]:
            out += ["", f"## Licence file shipped with {p['id']} {p['version']}", "", "```text",
                    p["license_file_text"], "```"]

    out += ["", "## Other components", ""]
    for comp in policy["component"]:
        out += [f"### {comp['name']}", "", f"Licence: {comp['license']}", f"Source: {comp['url']}", ""]
        out += [f"{line}" for line in comp.get("copyright", [])]
        out += ["", "```text", (texts_dir / f"{comp['text']}.txt").read_text(encoding="utf-8").strip(), "```", ""]
    return "\n".join(out).rstrip() + "\n"


# --- command line ----------------------------------------------------------------------------


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--policy", type=Path, default=HERE / "policy.toml")
    ap.add_argument("--update", action="store_true", help="rewrite the notices file")
    ap.add_argument("--assets", nargs="+", type=Path, help="project.assets.json files instead of a restore; "
                    "paired with the policy's frameworks in order")
    args = ap.parse_args(argv)

    policy = load_policy(args.policy)
    frameworks = policy["bundle"]["frameworks"]
    csproj = ROOT / policy["bundle"]["project"]
    notices_path = ROOT / policy["bundle"]["notices"]
    texts_dir = HERE / "texts"

    if args.assets:
        if len(args.assets) != len(frameworks):
            print(f"need {len(frameworks)} --assets files, one per framework {frameworks}", file=sys.stderr)
            return 2
        assets = dict(zip(frameworks, args.assets))
    else:
        assets = {}
        for tfm in frameworks:
            path = restore(csproj, tfm)
            # The next restore overwrites the file, so keep this one's content.
            copy = path.with_name(f"project.assets.{tfm}.json")
            copy.write_bytes(path.read_bytes())
            assets[tfm] = copy

    inventory = build_inventory(assets)
    problems = check_inventory(inventory, policy, texts_dir)
    problems += check_asset_declared(csproj.read_text(encoding="utf-8"), policy["bundle"]["asset_logical_name"])
    if problems:
        print("Licence policy violations:", file=sys.stderr)
        for p in problems:
            print(f"  - {p}", file=sys.stderr)
        return 1

    notices = render_notices(inventory, policy, texts_dir)
    if args.update:
        notices_path.write_bytes(notices.encode("utf-8"))
        print(f"wrote {notices_path.relative_to(ROOT)} ({len(inventory)} packages)")
        return 0
    current = notices_path.read_bytes().decode("utf-8").replace("\r\n", "\n") if notices_path.exists() else ""
    if current != notices:
        print(f"{notices_path.name} is stale. Run: python3 scripts/licences/check.py --update", file=sys.stderr)
        diff = difflib.unified_diff(current.splitlines(), notices.splitlines(), "committed", "generated", lineterm="", n=0)
        for line in list(diff)[:60]:
            print(line, file=sys.stderr)
        return 1
    print(f"{len(inventory)} packages, policy satisfied, {notices_path.name} is current")
    return 0


if __name__ == "__main__":
    sys.exit(main())
