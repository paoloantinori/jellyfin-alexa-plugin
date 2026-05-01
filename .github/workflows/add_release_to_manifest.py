from datetime import datetime, timezone
import hashlib
import json
import sys

import yaml


def main():
    if len(sys.argv) != 3:
        print("ERROR: Wrong arguments!\nUsage: add_release_to_manifest.py <version> <file>")
        sys.exit(1)

    version = sys.argv[1]
    zip_path = sys.argv[2]

    with open("manifest.json", "r") as f:
        manifest = json.load(f)

    with open("build.yaml", "r") as f:
        build = yaml.safe_load(f)

    with open(zip_path, "rb") as f:
        checksum = hashlib.md5()
        while chunk := f.read(4096):
            checksum.update(chunk)

    new_version_info = {
        "version": version,
        "checksum": checksum.hexdigest(),
        "sourceUrl": f"https://github.com/{build['owner']}/{build.get('repository', 'jellyfin-alexa-plugin')}/releases/download/{version}/AlexaSkill_{version}.zip",
        "changelog": build.get("changelog", ""),
        "targetAbi": build["targetAbi"],
        "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S")
    }

    manifest[0]["versions"].append(new_version_info)

    with open("manifest.json", "w") as f:
        json.dump(manifest, f, indent=4)
        f.write("\n")


if __name__ == "__main__":
    main()
