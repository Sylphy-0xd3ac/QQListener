#!/usr/bin/env bash
set -euo pipefail
source_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
fixture=$(mktemp -d)
trap 'rm -rf "$fixture"' EXIT
mkdir -p "$fixture/bin" "$fixture/Assets/Updates" "$fixture/Assets/PMHQ/win-x64" "$fixture/Distribution/pmhq" "$fixture/release"
openssl ecparam -name prime256v1 -genkey -noout -out "$fixture/key.pem" 2>/dev/null
openssl pkey -in "$fixture/key.pem" -pubout -out "$fixture/Assets/Updates/runtime-public.pem" 2>/dev/null
printf core > "$fixture/release/QQListener.Core.zip"
printf bundled > "$fixture/release/QQListener.cipx"
printf loader > "$fixture/release/QQListener.Download.cipx"
printf checksums > "$fixture/release/checksums.md"
printf patch > "$fixture/Assets/PMHQ/win-x64/pmhq-v9.0.6-local-pipe.2.patch"
core_hash=$(sha256sum "$fixture/release/QQListener.Core.zip" | cut -d' ' -f1)
patch_hash=$(sha256sum "$fixture/Assets/PMHQ/win-x64/pmhq-v9.0.6-local-pipe.2.patch" | cut -d' ' -f1)
jq -n --arg hash "$core_hash" '{Version:"2.3.0-beta.2",LoaderAbi:1,BuildNumber:1,Package:{Url:("https://github.com/Sylphy-0xd3ac/QQListener/releases/download/runtime-builds/QQListener.Core-"+$hash+".zip"),Sha256:$hash,Size:4}}' > "$fixture/release/core-release.json"
jq --arg hash "$patch_hash" '.Patch.Sha256=$hash | .Patch.Size=5 | .Patch.Url="https://github.com/Sylphy-0xd3ac/QQListener/releases/download/runtime-builds/test.patch"' "$source_root/Distribution/pmhq/9.0.6-local-pipe.2.json" > "$fixture/Distribution/pmhq/test.json"
cat > "$fixture/bin/gh" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$RUNTIME_TEST_ROOT/calls.log"
case "$1 $2" in
  'release view')
    if [[ "$*" == *'--json assets'* ]]; then
      if [[ -f "$RUNTIME_TEST_ROOT/old.json" ]]; then printf '1\n'; else printf '0\n'; fi
    fi
    ;;
  'release download')
    filename=; target=
    while (( $# )); do
      if [[ "$1" == --pattern ]]; then filename=$2; shift 2
      elif [[ "$1" == --dir ]]; then target=$2; shift 2
      else shift; fi
    done
    if [[ "$filename" == catalog.json ]]; then cp "$RUNTIME_TEST_ROOT/old.json" "$target/$filename"
    else cp "$RUNTIME_TEST_ROOT/old.sig" "$target/$filename"; fi
    ;;
  'release upload')
    for asset in "$@"; do
      if [[ -f "$asset" ]]; then cp "$asset" "$RUNTIME_TEST_ROOT/published/$(basename "$asset")"; fi
    done
    ;;
  *) exit 1 ;;
esac
MOCK
chmod +x "$fixture/bin/gh"
mkdir "$fixture/published"
export RUNTIME_TEST_ROOT="$fixture"
export PATH="$fixture/bin:$PATH"
export GITHUB_REPOSITORY=Sylphy-0xd3ac/QQListener
export GITHUB_REF=refs/heads/main
export GITHUB_SHA=test
export RUNTIME_SIGNING_KEY="$(cat "$fixture/key.pem")"
cd "$fixture"
bash "$source_root/Tools/PublishRuntime.sh" release
openssl dgst -sha256 -verify Assets/Updates/runtime-public.pem -signature published/catalog.json.sig published/catalog.json
test -f published/QQListener.Download.cipx
test -f "published/QQListener.Core-$core_hash.zip"
test "$(jq '.Core[0].BuildNumber' published/catalog.json)" == 1
# An older workflow must not replace a newer signed catalog.
jq '.Core[0].BuildNumber=2' published/catalog.json > old.json
openssl dgst -sha256 -sign key.pem -out old.sig old.json
: > calls.log
bash "$source_root/Tools/PublishRuntime.sh" release
if grep -q 'release upload' calls.log; then echo 'Older build uploaded assets' >&2; exit 1; fi
# A wrong ref or corrupt previous signature must fail before publishing.
if GITHUB_REF=refs/heads/feature bash "$source_root/Tools/PublishRuntime.sh" release 2>/dev/null; then exit 1; fi
printf corrupt > old.sig
: > calls.log
if bash "$source_root/Tools/PublishRuntime.sh" release >/dev/null 2>&1; then exit 1; fi
if grep -q 'release upload' calls.log; then echo 'Invalid catalog was replaced' >&2; exit 1; fi
printf 'Runtime publishing: signature, two variants, stale build and corrupt catalog checks OK\n'
