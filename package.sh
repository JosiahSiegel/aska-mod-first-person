#!/usr/bin/env bash
# Build and package the mod for distribution.
# Produces:
#   dist/AskaFirstPerson.dll           — standalone DLL (for Nexus Mods)
#   dist/AskaFirstPerson-VERSION.zip   — Thunderstore package (for r2modman)
#
# Usage:
#   ./package.sh          # uses version from FirstPersonPlugin.cs
#   ./package.sh 1.2.3    # override version
#
# Works on Linux (GitHub Actions ubuntu) and Windows (Git Bash / MSYS2).

set -euo pipefail

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

# Colored output when running in a terminal that supports it.
if [[ -t 1 ]] && [[ "${TERM:-dumb}" != "dumb" ]]; then
    RED=$'\033[1;31m'
    YELLOW=$'\033[1;33m'
    GREEN=$'\033[1;32m'
    CYAN=$'\033[1;36m'
    RESET=$'\033[0m'
else
    RED="" YELLOW="" GREEN="" CYAN="" RESET=""
fi

info()  { printf '%s[INFO]%s  %s\n' "$CYAN"  "$RESET" "$*"; }
warn()  { printf '%s[WARN]%s  %s\n' "$YELLOW" "$RESET" "$*"; }
err()   { printf '%s[ERROR]%s %s\n' "$RED"    "$RESET" "$*"; }
ok()    { printf '%s[OK]%s    %s\n' "$GREEN"  "$RESET" "$*"; }

die() { err "$@"; exit 1; }

# Cleanup on any unexpected exit.
cleanup() {
    local code=$?
    if [[ $code -ne 0 ]]; then
        err "Script failed with exit code $code."
    fi
}
trap cleanup EXIT

# Resolve the directory this script lives in (works in Git Bash and Linux).
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# ---------------------------------------------------------------------------
# Version detection
# ---------------------------------------------------------------------------

PLUGIN_CS="FirstPersonPlugin.cs"
MANIFEST_JSON="thunderstore/manifest.json"

[[ -f "$PLUGIN_CS" ]] || die "Cannot find $PLUGIN_CS in $SCRIPT_DIR"

# Extract version from FirstPersonPlugin.cs.
extract_cs_version() {
    # Use sed for portability (grep -P is not available everywhere).
    sed -n 's/.*PluginVersion\s*=\s*"\([^"]*\)".*/\1/p' "$PLUGIN_CS" | head -1
}

CS_VERSION="$(extract_cs_version)"
[[ -n "$CS_VERSION" ]] || die "Could not parse PluginVersion from $PLUGIN_CS"

VERSION="${1:-$CS_VERSION}"
info "Target version: $VERSION  (source C# reports: $CS_VERSION)"

# Compare with manifest.json version_number.
if [[ -f "$MANIFEST_JSON" ]]; then
    # Parse version_number without requiring jq (works on minimal CI images).
    MANIFEST_VERSION="$(sed -n 's/.*"version_number"\s*:\s*"\([^"]*\)".*/\1/p' "$MANIFEST_JSON" | head -1)"
    if [[ -n "$MANIFEST_VERSION" ]]; then
        if [[ "$CS_VERSION" != "$MANIFEST_VERSION" ]]; then
            warn "Version mismatch: $PLUGIN_CS has $CS_VERSION but $MANIFEST_JSON has $MANIFEST_VERSION"
            warn "Update one of them before publishing."
        else
            ok "Versions match across $PLUGIN_CS and $MANIFEST_JSON ($CS_VERSION)"
        fi
    else
        warn "Could not parse version_number from $MANIFEST_JSON"
    fi
else
    warn "$MANIFEST_JSON not found -- skipping version-match check"
fi

# ---------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------

info "Building AskaFirstPerson v${VERSION} ..."
dotnet build -c Release || die "dotnet build failed"

DLL="bin/Release/net6.0/AskaFirstPerson.dll"
[[ -f "$DLL" ]] || die "Build output not found at $DLL"
ok "Build succeeded: $DLL"

# ---------------------------------------------------------------------------
# Package
# ---------------------------------------------------------------------------

info "Packaging ..."
rm -rf dist
mkdir -p dist/thunderstore/BepInEx/plugins/AskaFirstPerson

# --- Standalone DLL (Nexus Mods) ---
cp "$DLL" dist/AskaFirstPerson.dll

# --- Thunderstore layout ---
cp "$DLL" dist/thunderstore/BepInEx/plugins/AskaFirstPerson/

if [[ -f "$MANIFEST_JSON" ]]; then
    cp "$MANIFEST_JSON" dist/thunderstore/
else
    die "$MANIFEST_JSON is required for Thunderstore packaging"
fi

if [[ -f thunderstore/README.md ]]; then
    cp thunderstore/README.md dist/thunderstore/
else
    die "thunderstore/README.md is required for Thunderstore packaging"
fi

if [[ -f thunderstore/icon.png ]]; then
    cp thunderstore/icon.png dist/thunderstore/
else
    warn "thunderstore/icon.png not found -- Thunderstore requires a 256x256 PNG icon"
    warn "The zip will be created without an icon; upload will be rejected by Thunderstore."
fi

# --- Create zip ---
ZIP_NAME="AskaFirstPerson-${VERSION}.zip"
ZIP_PATH="dist/${ZIP_NAME}"

create_zip() {
    if command -v zip &>/dev/null; then
        info "Creating zip with 'zip' ..."
        (cd dist/thunderstore && zip -r "../${ZIP_NAME}" .)
    elif command -v powershell.exe &>/dev/null || command -v powershell &>/dev/null; then
        info "Creating zip with PowerShell Compress-Archive ..."
        local ps
        ps="$(command -v powershell.exe 2>/dev/null || command -v powershell 2>/dev/null)"
        # Convert to Windows-style paths for PowerShell on MSYS2/Git Bash.
        local src dst
        if [[ "$(uname -s)" == MINGW* ]] || [[ "$(uname -s)" == MSYS* ]]; then
            src="$(cygpath -w "dist/thunderstore")"
            dst="$(cygpath -w "$ZIP_PATH")"
        else
            src="$(pwd)/dist/thunderstore"
            dst="$(pwd)/${ZIP_PATH}"
        fi
        "$ps" -NoProfile -Command "Compress-Archive -Path '${src}\\*' -DestinationPath '${dst}' -Force" \
            || die "PowerShell Compress-Archive failed"
    else
        die "Neither 'zip' nor 'powershell' found. Install one of them to create the package."
    fi
}

create_zip
[[ -f "$ZIP_PATH" ]] || die "Zip was not created at $ZIP_PATH"
ok "Created $ZIP_PATH"

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------

DLL_SIZE="$(wc -c < dist/AskaFirstPerson.dll | tr -d '[:space:]')"
ZIP_SIZE="$(wc -c < "$ZIP_PATH" | tr -d '[:space:]')"

echo ""
echo "============================================================"
ok "Packaging complete for AskaFirstPerson v${VERSION}"
echo "============================================================"
echo ""
echo "  Artifacts:"
echo "    dist/AskaFirstPerson.dll       ($DLL_SIZE bytes)"
echo "    dist/${ZIP_NAME}  ($ZIP_SIZE bytes)"
echo ""
echo "  Next steps:"
echo ""
echo "  1. ${CYAN}GitHub Release${RESET}"
echo "       git tag v${VERSION}"
echo "       git push origin v${VERSION}"
echo "       Upload both files to the GitHub release."
echo ""
echo "  2. ${CYAN}Thunderstore${RESET}"
echo "       Upload ${ZIP_NAME} at:"
echo "       https://thunderstore.io/c/aska/create/"
echo ""
echo "  3. ${CYAN}Nexus Mods${RESET}"
echo "       Upload dist/AskaFirstPerson.dll at:"
echo "       https://www.nexusmods.com/aska/mods/"
echo ""
