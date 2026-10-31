#!/usr/bin/env bash
set -euo pipefail
# A public prerelease serves anonymous clients; Actions artifact downloads require authentication.
repo="Sylphy-0xd3ac/QQListener"
[[ "${GITHUB_REPOSITORY:-}" == "$repo" ]] || { echo 'Runtime publishing is restricted to the distribution repository.' >&2; exit 1; }
[[ "${GITHUB_REF:-}" == 'refs/heads/main' ]] || { echo 'Runtime builds must come from main.' >&2; exit 1; }
payload="${1:?package directory required}"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
umask 077
printf '%s\n' "${RUNTIME_SIGNING_KEY:?Missing RUNTIME_SIGNING_KEY repository secret}" > "$work/key.pem"
openssl pkey -in "$work/key.pem" -pubout -out "$work/public.pem" 2>/dev/null
cmp Assets/Updates/runtime-public.pem "$work/public.pem"
gh release view runtime-builds --repo "$repo" >/dev/null 2>&1 ||
  gh release create runtime-builds --repo "$repo" --target "$GITHUB_SHA" --prerelease --latest=false \
    --title 'QQListener runtime builds' --notes 'Automatic builds from main. The loader verifies the signed catalog before installing updates.'
printf '{"Schema":1,"Pmhq":[],"Core":[]}' > "$work/previous.json"
catalog_count=$(gh release view runtime-builds --repo "$repo" --json assets --jq '[.assets[] | select(.name == "catalog.json")] | length')
if [[ "$catalog_count" != 0 ]]; then
  gh release download runtime-builds --repo "$repo" --pattern catalog.json --dir "$work"
  gh release download runtime-builds --repo "$repo" --pattern catalog.json.sig --dir "$work"
  openssl dgst -sha256 -verify Assets/Updates/runtime-public.pem -signature "$work/catalog.json.sig" "$work/catalog.json"
  mv "$work/catalog.json" "$work/previous.json"
fi
new_build=$(jq -r '.BuildNumber' "$payload/core-release.json")
old_build=$(jq '[.Core[].BuildNumber] | max // 0' "$work/previous.json")
if (( new_build <= old_build )); then echo 'A newer or identical build is already published.'; exit 0; fi
core_name=$(jq -r '.Package.Url | split("/")[-1]' "$payload/core-release.json")
core_hash=$(jq -r '.Package.Sha256' "$payload/core-release.json")
[[ "$(sha256sum "$payload/QQListener.Core.zip" | cut -d' ' -f1)" == "$core_hash" ]]
cp "$payload/QQListener.Core.zip" "$work/$core_name"
gh release upload runtime-builds "$work/$core_name" --repo "$repo" --clobber
for record in Distribution/pmhq/*.json; do
  patch_hash=$(jq -r '.Patch.Sha256' "$record")
  patch_name=$(jq -r '.Patch.Url | split("/")[-1]' "$record")
  version=$(jq -r '.RuntimeVersion' "$record")
  patch="Assets/PMHQ/win-x64/pmhq-v${version}.patch"
  [[ "$(sha256sum "$patch" | cut -d' ' -f1)" == "$patch_hash" ]]
  [[ "$(wc -c < "$patch" | tr -d ' ')" == "$(jq -r '.Patch.Size' "$record")" ]]
  cp "$patch" "$work/$patch_name"
  gh release upload runtime-builds "$work/$patch_name" --repo "$repo" --clobber
done
jq -s '.' Distribution/pmhq/*.json > "$work/pmhq.json"
jq --slurpfile next "$payload/core-release.json" --slurpfile pmhq "$work/pmhq.json" \
  '{Schema:1,Pmhq:$pmhq[0],Core:([$next[0]] + .Core | unique_by(.Package.Sha256) | sort_by(.BuildNumber) | reverse | .[:32])}' \
  "$work/previous.json" > "$work/catalog.json"
openssl dgst -sha256 -sign "$work/key.pem" -out "$work/catalog.json.sig" "$work/catalog.json"
# Publish payloads first. A reader during the two catalog uploads fails verification and uses its cache.
gh release upload runtime-builds "$work/catalog.json" "$work/catalog.json.sig" --repo "$repo" --clobber
gh release upload runtime-builds "$payload/QQListener.cipx" "$payload/QQListener.Download.cipx" "$payload/checksums.md" --repo "$repo" --clobber
