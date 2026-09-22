from datetime import datetime, timezone
import hashlib
import json
import sys

import yaml


# One release serves BOTH shipping lines since 1.0 (JF-307 dual-target): the
# net9.0 zip claims targetAbi from build.yaml (10.11.0.0 today), the net10.0
# zip claims 12.0.0.0. ORDER IS LOAD-BEARING (review round 1, verified against
# Jellyfin's InstallationManager at v10.11.8 and v12.1: compatible versions are
# filtered by targetAbi <= server, then OrderByDescending(VersionNumber) - a
# STABLE sort - and the caller takes the first): among EQUAL version numbers
# the FIRST-LISTED entry wins, so the net10 entry MUST be appended first. A
# 12.x server then resolves the net10 zip; a 10.11 server has the 12.0.0.0
# entry filtered out and falls to the 10.11 entry. The net9.0 DLL on 12.x
# throws MissingMethodException on changed APIs (live-verified 2026-09-21).
# CONSEQUENCE (review finding 3): equal-version entries never supersede each
# other server-side (updates need Version > installed), so a repaired release
# REQUIRES a version bump - a re-run of the same version only fixes fresh
# installs.
ABI_LINES = [
    {"targetAbi": "12.0.0.0", "zip_suffix": ".net10"},
    {"targetAbi": None, "zip_suffix": ""},  # None = build.yaml's targetAbi (the net9 line)
]


def md5_of(path):
    checksum = hashlib.md5()
    with open(path, "rb") as f:
        while chunk := f.read(4096):
            checksum.update(chunk)
    return checksum.hexdigest()


def main():
    if len(sys.argv) != 3:
        print("ERROR: Wrong arguments!\nUsage: add_release_to_manifest.py <version> <zip-basename-without-suffix>")
        print("Example: add_release_to_manifest.py 1.0.0.0 AlexaSkill_1.0.0.0")
        sys.exit(1)

    version = sys.argv[1]
    zip_basename = sys.argv[2]

    with open("manifest.json", "r") as f:
        manifest = json.load(f)

    with open("build.yaml", "r") as f:
        build = yaml.safe_load(f)

    # Drop EVERY entry carrying this version first (review finding 4: a
    # validate_versions-forced placeholder with a near-miss targetAbi must not
    # survive ahead of the real entries - equal versions resolve first-listed).
    versions = manifest[0]["versions"]
    manifest[0]["versions"] = [v for v in versions if v["version"] != version]
    versions = manifest[0]["versions"]

    repo = f"https://github.com/{build['owner']}/{build.get('repository', 'jellyfin-alexa-plugin')}/releases/download/{version}"

    for line in ABI_LINES:
        zip_path = f"{zip_basename}{line['zip_suffix']}.zip"
        new_version_info = {
            "version": version,
            "checksum": md5_of(zip_path),
            "sourceUrl": f"{repo}/{zip_path.split('/')[-1]}",
            "changelog": build.get("changelog", ""),
            "targetAbi": line["targetAbi"] or build["targetAbi"],
            "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S"),
        }
        versions.append(new_version_info)

    with open("manifest.json", "w") as f:
        json.dump(manifest, f, indent=4)
        f.write("\n")


if __name__ == "__main__":
    main()
