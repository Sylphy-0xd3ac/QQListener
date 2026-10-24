#!/usr/bin/env bash
set -euo pipefail

: "${RELEASE_TAG:?RELEASE_TAG is required}"
: "${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is required}"
: "${GH_TOKEN:?GH_TOKEN is required}"

if [[ ${GITHUB_EVENT_NAME:-} != push || ${GITHUB_REF_TYPE:-} != tag ]]; then
    printf '%s\n' 'Release publishing requires a pushed version tag.' >&2
    exit 1
fi

version_component='(0|[1-9][0-9]*)'
prerelease_identifier='(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
semver_pattern="^${version_component}\.${version_component}\.${version_component}(-${prerelease_identifier}(\.${prerelease_identifier})*)?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$"
legacy_pattern="^${version_component}\.${version_component}\.${version_component}\.${version_component}$"
release_title="QQListener $RELEASE_TAG"
if [[ $RELEASE_TAG =~ $legacy_pattern ]]; then
    release_title="QQListener ${RELEASE_TAG%.0}"
elif [[ ! $RELEASE_TAG =~ $semver_pattern ]]; then
    printf '%s\n' 'Release tags must use canonical SemVer or a legacy four-part version.' >&2
    exit 1
fi
release_flags=(--prerelease=false --latest)
version_without_metadata="${RELEASE_TAG%%+*}"
if [[ $version_without_metadata == *-* ]]; then
    release_flags=(--prerelease --latest=false)
fi

package_directory="${1:-release}"
for asset in QQListener.cipx checksums.md; do
    if [[ ! -s "$package_directory/$asset" ]]; then
        printf 'Missing release asset: %s\n' "$asset" >&2
        exit 1
    fi
done

checksum_pattern='<!--[[:space:]]*CLASSISLAND_PKG_MD5[[:space:]]+\{[^}]*"QQListener\.cipx"[[:space:]]*:[[:space:]]*"[0-9A-Fa-f]{32}"[^}]*\}[[:space:]]*-->'
if ! grep -Eq "$checksum_pattern" "$package_directory/checksums.md"; then
    printf '%s\n' 'The plugin checksum marker is missing or malformed.' >&2
    exit 1
fi

notes_file="$package_directory/release-notes.md"
awk -v heading="## [$RELEASE_TAG]" '
    { sub(/\r$/, "") }
    $0 == heading || index($0, heading " ") == 1 { copying = 1; found = 1; next }
    copying && /^## \[/ { exit }
    copying { print }
    END { if (!found) exit 1 }
' CHANGELOG.md > "$notes_file"
if ! grep -q '[^[:space:]]' "$notes_file"; then
    printf '%s\n' 'The tagged version has no release notes in CHANGELOG.md.' >&2
    exit 1
fi

printf '\n' >> "$notes_file"
cat "$package_directory/checksums.md" >> "$notes_file"

if release_state="$(gh release view "$RELEASE_TAG" --repo "$GITHUB_REPOSITORY" --json isDraft --jq '.isDraft')"; then
    if [[ $release_state == false ]]; then
        printf 'Release %s is already published; leaving it unchanged.\n' "$RELEASE_TAG"
        exit 0
    fi
    if [[ $release_state != true ]]; then
        printf '%s\n' 'Cannot determine whether the existing release is a draft.' >&2
        exit 1
    fi
    gh release edit "$RELEASE_TAG" --repo "$GITHUB_REPOSITORY" --draft \
        --title "$release_title" --notes-file "$notes_file" "${release_flags[@]}"
else
    gh release create "$RELEASE_TAG" --repo "$GITHUB_REPOSITORY" --draft --verify-tag \
        --title "$release_title" --notes-file "$notes_file" "${release_flags[@]}"
fi

gh release upload "$RELEASE_TAG" "$package_directory/QQListener.cipx" "$package_directory/checksums.md" \
    --repo "$GITHUB_REPOSITORY" --clobber
gh release edit "$RELEASE_TAG" --repo "$GITHUB_REPOSITORY" --draft=false "${release_flags[@]}"
