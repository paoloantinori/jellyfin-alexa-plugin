#!/usr/bin/env bash
set -euo pipefail

# Read current version from Directory.Build.props and bump the last segment
CURRENT=$(grep -oP '(?<=<Version>)[^<]+' Directory.Build.props)
IFS='.' read -r major minor build rev <<< "$CURRENT"
VERSION="${major}.${minor}.${build}.$((rev + 1))"

# Determine targetAbi from the Jellyfin NuGet package version referenced in the csproj
JELLYFIN_VERSION=$(grep -oP '(?<=Jellyfin.Controller" Version=")[^"]+' Jellyfin.Plugin.AlexaSkill/Jellyfin.Plugin.AlexaSkill.csproj)
# Extract major.minor.build (e.g., "10.11.8" -> "10.11.0.0")
IFS='.' read -r j_major j_minor j_build _ <<< "$JELLYFIN_VERSION"
TARGET_ABI="${j_major}.${j_minor}.0.0"

echo "Releasing ${VERSION} for Jellyfin ${TARGET_ABI}"

# Update Directory.Build.props
cat > Directory.Build.props <<EOF
<Project>
    <PropertyGroup>
        <Version>${VERSION}</Version>
        <AssemblyVersion>${VERSION}</AssemblyVersion>
        <FileVersion>${VERSION}</FileVersion>
    </PropertyGroup>
</Project>
EOF

# Add new version entry to manifest.json using python
python3 -c "
import json, datetime

with open('manifest.json', 'r') as f:
    manifest = json.load(f)

checksum = '$(cat /dev/null)'  # placeholder — compute after build

new_version = {
    'version': '${VERSION}',
    'checksum': checksum,
    'sourceUrl': 'https://github.com/paoloantinori/jellyfin-alexa-plugin/releases/download/${VERSION}/AlexaSkill_${VERSION}.zip',
    'changelog': 'See for more details: https://github.com/paoloantinori/jellyfin-alexa-plugin/releases',
    'targetAbi': '${TARGET_ABI}',
    'timestamp': datetime.datetime.now(datetime.timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ')
}

manifest[0]['versions'].append(new_version)

with open('manifest.json', 'w') as f:
    json.dump(manifest, f, indent=4)
    f.write('\n')
"

git add Directory.Build.props manifest.json
git commit -m "Bump version to ${VERSION} for release"
git tag "${VERSION}"
git push origin main
git push origin "${VERSION}"

echo "Done. Released ${VERSION} for Jellyfin ${TARGET_ABI}."
