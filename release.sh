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

# Build release zip
dotnet build Jellyfin.Plugin.AlexaSkill/Jellyfin.Plugin.AlexaSkill.csproj --configuration Release -warnaserror
ZIP_DIR="/tmp/AlexaSkill_${VERSION}"
rm -rf "$ZIP_DIR" "/tmp/AlexaSkill_${VERSION}.zip"
mkdir -p "$ZIP_DIR"

# Copy DLLs from build output and known dependency paths
BUILD_OUT="Jellyfin.Plugin.AlexaSkill/bin/Release/net9.0"
for dll in Alexa.NET.dll Alexa.NET.Management.dll Alexa.NET.ProactiveEvents.dll \
           Alexa.NET.Profile.dll Alexa.NET.Reminders.dll Amazon.Lambda.Core.dll \
           Amazon.Lambda.Serialization.Json.dll Refit.dll; do
    cp "$BUILD_OUT/$dll" "$ZIP_DIR/"
done
cp "$BUILD_OUT/Jellyfin.Plugin.AlexaSkill.dll" "$ZIP_DIR/"
cp "$BUILD_OUT/icon.png" "$ZIP_DIR/" 2>/dev/null || true

cd /tmp && zip -j "AlexaSkill_${VERSION}.zip" "AlexaSkill_${VERSION}/" && cd -

# Compute checksum from the built zip
CHECKSUM=$(md5sum "/tmp/AlexaSkill_${VERSION}.zip" | cut -d' ' -f1)
echo "Checksum: ${CHECKSUM}"

# Add new version entry to manifest.json
python3 -c "
import json, datetime

with open('manifest.json', 'r') as f:
    manifest = json.load(f)

new_version = {
    'version': '${VERSION}',
    'checksum': '${CHECKSUM}',
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

# Create GitHub release and upload zip
gh release create "${VERSION}" "/tmp/AlexaSkill_${VERSION}.zip" \
    --repo paoloantinori/jellyfin-alexa-plugin \
    --title "${VERSION}" \
    --notes "Release ${VERSION}"

echo "Done. Released ${VERSION} for Jellyfin ${TARGET_ABI}."
