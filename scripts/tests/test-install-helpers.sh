#!/bin/sh
# Tests install.sh's account lookup without a terminal or the network: loads its functions from the script itself,
# stubs curl and the Azure CLI, and exits 1 on any failure. POSIX sh, like the installer (CI runs it under dash).
# The functions are loaded with eval, so shellcheck can't see that they read REGION and SECRETS_PATH.
# shellcheck disable=SC2034
set -u
here=$(cd "$(dirname "$0")" && pwd)
installer="$here/../install.sh"
for fn in json_escape secret_get people_with_email find_person; do
    body=$(sed -n "/^$fn() {/,/^}/p" "$installer")
    [ -n "$body" ] || { echo "install.sh has no function $fn"; exit 1; }
    eval "$body"
done

failures=0
check() { if [ "$2" = "$3" ]; then echo "ok   $1"; else echo "FAIL $1: expected [$3], got [$2]"; failures=$((failures + 1)); fi; }

tmp=$(mktemp -d); trap 'rm -rf "$tmp"' EXIT
SECRETS_PATH="$tmp/secrets.json"; : >"$SECRETS_PATH"
REGION=""
unset Ticketing__BaseUrl 2>/dev/null || true

# A made-up instance in the live shape: the same person as an assignee and as the SLA escalation contact (one match,
# by ID), a namesake pair sharing an email (no match), and an escaped quote in a name.
cat >"$tmp/instance.json" <<'EOF'
{"item":{"id":"i","assignees":{"type":"teamsOwner","peoples":[{"id":"11111111-1111-1111-1111-111111111111","name":"Pat \"PJ\" Lee","email":"Pat.Lee@contoso.com"},{"id":"22222222-2222-2222-2222-222222222222","name":"Sam Roe","email":"sam@contoso.com"},{"id":"33333333-3333-3333-3333-333333333333","name":"Sam Roe","email":"SAM@contoso.com"}]},"sla":{"frt":{"escalation":{"enabled":false,"escalationAssignee":{"id":"11111111-1111-1111-1111-111111111111","name":"Pat \"PJ\" Lee","email":"pat.lee@contoso.com"}}}}}}
EOF

# Stubs, defined after the functions they replace would be looked up at call time.
have() { return 1; }  # no Azure CLI
curl() { cat >"$tmp/curl-config"; [ -f "$tmp/fail" ] && return 22; cat "$tmp/instance.json"; }

out=$(find_person 'pat.lee@CONTOSO.com' 'abc123')
check 'an email is matched in the assignee list, case aside' "$(printf '%s\n' "$out" | sed -n 1p)" '11111111-1111-1111-1111-111111111111'
check 'its name comes back JSON-escaped' "$(printf '%s\n' "$out" | sed -n 2p)" 'Pat \"PJ\" Lee'
check 'and where it came from' "$(printf '%s\n' "$out" | sed -n 3p)" "the help desk's assignee list"
check 'the key goes on stdin to the US endpoint' "$(cat "$tmp/curl-config")" 'url = "https://teamswork.azure-api.net/ticketing/v1/instance?key=abc123&timezone=0"'

check 'two people with one email is no match' "$(find_person 'sam@contoso.com' 'abc123')" ''
check 'nobody with the email is no match' "$(find_person 'nobody@contoso.com' 'abc123')" ''

REGION=EU; find_person 'pat.lee@contoso.com' 'abc123' >/dev/null
check 'a region picks its endpoint' "$(cat "$tmp/curl-config")" 'url = "https://ticketing-apim-eu.azure-api.net/ticketing/v1/instance?key=abc123&timezone=0"'
REGION=XX; rm -f "$tmp/curl-config"
check 'an unknown region makes no request' "$(find_person 'pat.lee@contoso.com' 'abc123')$([ -f "$tmp/curl-config" ] && echo requested)" ''
REGION=""

Ticketing__BaseUrl='https://elsewhere.example/v1'; export Ticketing__BaseUrl; rm -f "$tmp/curl-config"
check 'a custom base URL gets no request, so the key stays put' "$(find_person 'pat.lee@contoso.com' 'abc123')$([ -f "$tmp/curl-config" ] && echo requested)" ''
unset Ticketing__BaseUrl
printf '{\n  "Ticketing:BaseUrl": "https://elsewhere.example/v1"\n}\n' >"$SECRETS_PATH"; rm -f "$tmp/curl-config"
check 'so does one in the secrets file' "$(find_person 'pat.lee@contoso.com' 'abc123')$([ -f "$tmp/curl-config" ] && echo requested)" ''
: >"$SECRETS_PATH"

rm -f "$tmp/curl-config"
check 'a key needing URL escaping makes no request' "$(find_person 'pat.lee@contoso.com' 'a&b=c')$([ -f "$tmp/curl-config" ] && echo requested)" ''

touch "$tmp/fail"
check 'a failed request is no match' "$(find_person 'pat.lee@contoso.com' 'abc123' 2>/dev/null </dev/null)" ''
rm -f "$tmp/fail"

if [ "$failures" -ne 0 ]; then echo "$failures failed"; exit 1; fi
echo 'all passed'
