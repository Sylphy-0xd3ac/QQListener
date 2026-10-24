#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
temporary_root="$(mktemp -d)"
trap 'rm -r -- "$temporary_root"' EXIT
mkdir -p "$temporary_root/bin"

cat > "$temporary_root/bin/gh" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$GH_CALLS_LOG"
case "$1 $2" in
    'release view')
        case "$TEST_RELEASE_STATE" in
            published) printf '%s\n' false ;;
            draft) printf '%s\n' true ;;
            *) exit 1 ;;
        esac
        ;;
    'release upload')
        if [[ $TEST_RELEASE_STATE == upload_failure ]]; then exit 1; fi
        ;;
    'release create'|'release edit') ;;
    *) exit 1 ;;
esac
MOCK
chmod +x "$temporary_root/bin/gh"

run_case() {
    local scenario="$1"
    local expected_status="$2"
    local working_directory="$temporary_root/$scenario"
    mkdir -p "$working_directory/release"
    printf '%s\n' package > "$working_directory/release/QQListener.cipx"
    printf '%s\n' '<!-- CLASSISLAND_PKG_MD5 {"QQListener.cipx":"00000000000000000000000000000000"} -->' > "$working_directory/release/checksums.md"
    printf '# Changelog\r\n\r\n## [2.2.0.0] - 2026-10-23\r\n\r\n- Current release.\r\n\r\n## [2.1.0.0]\r\n\r\n- Previous release.\r\n' > "$working_directory/CHANGELOG.md"
    : > "$working_directory/calls.log"
    local tag=2.2.0.0
    local event=push
    local ref_type=tag
    case "$scenario" in
        invalid_tag) tag='2.2.0.0;echo injected' ;;
        leading_zero) tag='2.3.0-beta.01' ;;
        invalid_semver) tag='2.3.0-' ;;
        beta|stable|stable_metadata)
            case "$scenario" in
                beta) tag=2.3.0-beta.1 ;;
                stable) tag=2.3.0 ;;
                stable_metadata) tag=2.3.0+build-test ;;
            esac
            printf '## [%s]\n\n- Current release.\n\n## [2.2.0.0]\n- Previous release.\n' "$tag" > "$working_directory/CHANGELOG.md"
            ;;
        branch_push) ref_type=branch ;;
        pull_request) event=pull_request ;;
        missing_asset) : > "$working_directory/release/checksums.md" ;;
        invalid_checksum) printf '%s\n' invalid > "$working_directory/release/checksums.md" ;;
        missing_notes) tag=2.3.0.0 ;;
        empty_notes) printf '## [2.2.0.0]\n\n## [2.1.0.0]\n- Old.\n' > "$working_directory/CHANGELOG.md" ;;
    esac
    local status
    if (
        cd "$working_directory"
        PATH="$temporary_root/bin:$PATH" GH_TOKEN=test-token GITHUB_REPOSITORY=test/QQListener \
            GITHUB_EVENT_NAME="$event" GITHUB_REF_TYPE="$ref_type" RELEASE_TAG="$tag" \
            GH_CALLS_LOG="$working_directory/calls.log" TEST_RELEASE_STATE="$scenario" \
            bash "$repository_root/Tools/PublishRelease.sh" release
    ) > "$working_directory/output.log" 2>&1; then status=0; else status=$?; fi
    if [[ $status -ne $expected_status ]]; then
        cat "$working_directory/output.log" >&2
        printf 'Unexpected exit status for %s: %s\n' "$scenario" "$status" >&2
        exit 1
    fi
    case "$scenario" in
        new|draft|upload_failure|published|beta|stable|stable_metadata)
            grep -q 'Current release.' "$working_directory/release/release-notes.md"
            grep -q 'CLASSISLAND_PKG_MD5' "$working_directory/release/release-notes.md"
            if grep -q 'Previous release.' "$working_directory/release/release-notes.md"; then exit 1; fi
            ;;
        *) [[ ! -s "$working_directory/calls.log" ]] ;;
    esac
    case "$scenario" in
        new)
            grep -q '^release create .*--draft --verify-tag' "$working_directory/calls.log"
            grep -q '^release upload .*QQListener.cipx release/checksums.md' "$working_directory/calls.log"
            grep -q '^release edit .*--draft=false --prerelease=false --latest' "$working_directory/calls.log"
            ;;
        beta)
            grep -q '^release create .*--prerelease --latest=false' "$working_directory/calls.log"
            grep -q '^release edit .*--draft=false --prerelease --latest=false' "$working_directory/calls.log"
            if grep -Eq -- '--latest( |$)' "$working_directory/calls.log"; then exit 1; fi
            ;;
        stable|stable_metadata)
            grep -q '^release edit .*--draft=false --prerelease=false --latest$' "$working_directory/calls.log"
            grep -q -- "--title QQListener $tag --notes-file" "$working_directory/calls.log"
            ;;
        draft)
            if grep -q '^release create ' "$working_directory/calls.log"; then exit 1; fi
            grep -q '^release edit .*--draft=false --prerelease=false --latest' "$working_directory/calls.log"
            ;;
        published) [[ $(wc -l < "$working_directory/calls.log") -eq 1 ]] ;;
        upload_failure)
            if grep -q -- '--draft=false' "$working_directory/calls.log"; then exit 1; fi
            ;;
    esac
    printf 'Release automation: %s OK\n' "$scenario"
}

run_case new 0
run_case draft 0
run_case published 0
run_case upload_failure 1
run_case invalid_tag 1
run_case branch_push 1
run_case pull_request 1
run_case missing_asset 1
run_case invalid_checksum 1
run_case missing_notes 1
run_case empty_notes 1

run_case beta 0
run_case stable 0
run_case stable_metadata 0
run_case leading_zero 1
run_case invalid_semver 1
