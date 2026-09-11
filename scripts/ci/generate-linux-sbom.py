#!/usr/bin/env python3
"""Generate an IEM-SBOM-1 document for the Linux release payload.

Invariants:
  200. SBOM_IS_GENERATED_FROM_THE_RELEASE_BEING_DISTRIBUTED
  201. SBOM_ACCURATELY_REPRESENTS_RELEASE_COMPONENTS

The document mirrors the Windows publish pipeline's `IEM-SBOM-1` shape so the same
verifier semantics apply: project components carry a file SHA-256, NuGet components
carry the locked contentHash, and every distributed binary carries its own SHA-256.
"""

from __future__ import annotations

import argparse
import datetime as _dt
import hashlib
import json
import os
import subprocess
import sys

REPO_PROJECTS = [
    "IEM.Core", "IEM.Storage", "IEM.Presentation", "IEM.Evidence",
    "IEM.Verification", "IEM.Linux", "IEM.Service.Runtime", "IEM.Legal",
    "IEM.Service.Linux", "IEM.App.Linux",
]


def sha256_file(path: str) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def project_components(repo_root: str, version: str) -> list[dict]:
    components = []
    for name in REPO_PROJECTS:
        proj = os.path.join(repo_root, "src", name, f"{name}.csproj")
        if not os.path.isfile(proj):
            continue
        components.append({
            "Name": name,
            "Version": version,
            "PackageType": "project",
            "Supplier": "IEM Project",
            "License": "MIT",
            "IntegrityAlgorithm": "SHA256",
            "IntegrityValue": sha256_file(proj),
            "IntegritySource": "File SHA-256",
        })
    return components


def nuget_components(repo_root: str) -> list[dict]:
    seen: set[str] = set()
    components: list[dict] = []
    src = os.path.join(repo_root, "src")
    for base, dirs, files in os.walk(src):
        # `bin/` and `obj/` carry copies of packages.lock.json from whatever was last
        # built in this tree - including other RIDs and other operating systems. Those
        # are not inputs to the Linux payload, and honouring them makes the SBOM depend
        # on uncommitted build detritus: a tree with a stale Windows arm64 build under
        # src/IEM.Service/bin emits a component the release does not contain, and a
        # clean clone emits a different document for the same commit (invariant 201).
        dirs[:] = [d for d in dirs if d not in ("bin", "obj")]
        if "packages.lock.json" not in files:
            continue
        with open(os.path.join(base, "packages.lock.json"), encoding="utf-8") as handle:
            lock = json.load(handle)
        for framework in lock.get("dependencies", {}).values():
            for pkg_name, details in framework.items():
                if str(details.get("type", "")).lower() == "project":
                    continue
                content_hash = details.get("contentHash")
                resolved = details.get("resolved")
                if not content_hash or not resolved:
                    continue
                key = f"{pkg_name}:{resolved}"
                if key in seen:
                    continue
                seen.add(key)
                components.append({
                    "Name": pkg_name,
                    "Version": resolved,
                    "PackageType": "nuget",
                    "Supplier": "NuGet",
                    "License": "Various",
                    "IntegrityAlgorithm": "SHA512",
                    "IntegrityValue": content_hash,
                    "IntegritySource": "NuGet contentHash",
                })
    components.sort(key=lambda c: (c["Name"], c["Version"]))
    return components


def payload_components(payload_root: str) -> list[dict]:
    components = []
    for base, _dirs, files in os.walk(payload_root):
        for file_name in sorted(files):
            full = os.path.join(base, file_name)
            if os.path.islink(full):
                continue
            rel = os.path.relpath(full, payload_root)
            components.append({
                "Name": rel.replace(os.sep, "/"),
                "Version": "",
                "PackageType": "payload-file",
                "Supplier": "Internet Evidence Monitor",
                "License": "MIT",
                "IntegrityAlgorithm": "SHA256",
                "IntegrityValue": sha256_file(full),
                "IntegritySource": "File SHA-256",
                "SizeBytes": os.path.getsize(full),
            })
    components.sort(key=lambda c: c["Name"])
    return components


def resolve_git_commit(repo_root: str) -> str:
    """Resolve the commit this payload was built from.

    Resolution order, most explicit first:

      1. IEM_GIT_COMMIT - the build environment stating the revision outright.
         Required whenever the payload is built somewhere `git` cannot answer:
         the release containers bind-mount a git *worktree*, whose `.git` is a
         file pointing at a host path that does not exist inside the container,
         so git fails there no matter how the repo is mounted.
      2. GITHUB_SHA - set by GitHub Actions.
      3. Asking git directly, for a maintainer cutting a build from a normal
         checkout.

    An SBOM recording "unknown" cannot tie the distributed bytes back to a
    revision, which defeats invariant 200 for exactly the builds nobody else can
    reproduce. Falling back that far is therefore worth a warning, not silence.

    `safe.directory` is set because the container runs as root over a bind mount
    owned by another uid, where git otherwise refuses the repository outright.
    """
    for var in ("IEM_GIT_COMMIT", "GITHUB_SHA"):
        value = os.environ.get(var)
        if value:
            return value.strip()
    try:
        result = subprocess.run(
            ["git", "-c", "safe.directory=*", "-C", repo_root, "rev-parse", "HEAD"],
            capture_output=True, text=True, timeout=15, check=False,
        )
    except (OSError, subprocess.SubprocessError):
        result = None
    commit = result.stdout.strip() if result is not None else ""
    if result is None or result.returncode != 0 or not commit:
        print(
            "WARNING: no commit could be resolved for this SBOM; set IEM_GIT_COMMIT to "
            "record one. The release is not traceable to a revision without it.",
            file=sys.stderr,
        )
        return "unknown"
    return commit


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", required=True)
    parser.add_argument("--payload-root", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--rid", required=True)
    parser.add_argument("--git-commit", default=None)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    git_commit = args.git_commit or resolve_git_commit(args.repo_root)

    components = (
        project_components(args.repo_root, args.version)
        + nuget_components(args.repo_root)
        + payload_components(args.payload_root)
    )

    document = {
        "SbomFormat": "IEM-SBOM-1",
        "DocumentNamespace":
            f"https://github.com/zoxknez/InternetMonitoring/sbom/{args.version}/{args.rid}",
        "Release": {
            "ProductVersion": args.version,
            "GitCommit": git_commit,
            "BuildTimestampUtc": _dt.datetime.now(_dt.timezone.utc).isoformat(),
            "ReleaseChannel": "Preview",
            "RuntimeIdentifiers": [args.rid],
            "ReleaseManifestVersion": 1,
        },
        "ComponentCount": len(components),
        "Components": components,
    }

    os.makedirs(os.path.dirname(os.path.abspath(args.output)), exist_ok=True)
    with open(args.output, "w", encoding="utf-8") as handle:
        json.dump(document, handle, indent=2, ensure_ascii=False)
        handle.write("\n")

    print(f"Wrote {args.output} ({len(components)} components)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
