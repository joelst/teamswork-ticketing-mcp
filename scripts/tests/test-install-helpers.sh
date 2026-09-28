#!/bin/sh
# Tests install.sh's account lookup without a terminal or the network: loads its functions from the script itself,
# stubs curl and the Azure CLI, and exits 1 on any failure. POSIX sh, like the installer (CI runs it under dash).
# The functions are loaded with eval, so shellcheck can't see that they read REGION and SECRETS_PATH.
# shellcheck disable=SC2034
set -u
here=$(cd "$(dirname "$0")" && pwd)
installer="$here/../install.sh"
for fn in json_escape secret_get people_with_email env_setting find_person; do
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

# A made-up instance in the live shape: a namesake pair sharing an email (no match), an escaped quote in a name, a
# name with brackets and braces ahead of Pat (which mustn't end the list or split a person), and Pat listed twice
# (one match, whatever the case of the ID). Outside the assignee list, which alone counts: a string value reading
# "assignees" before the real key, an SLA escalation contact with Pat's email but another ID (which mustn't make Pat
# look ambiguous), and a people-picker default for someone not on the list.
cat >"$tmp/instance.json" <<'EOF'
{"item":{"id":"i","customFieldsLeft":[{"id":"f","title":"assignees","defaultValue":[{"id":"44444444-4444-4444-4444-444444444444","name":"Lee Outside","email":"lee@contoso.com"}]}],"assignees":{"type":"teamsOwner","peoples":[{"id":"55555555-5555-5555-5555-555555555555","name":"Bracket [Team] {Lead}","email":"brackets@contoso.com"},{"id":"1111aaaa-1111-1111-1111-111111111111","name":"Pat \"PJ\" Lee","email":"Pat.Lee@contoso.com"},{"id":"22222222-2222-2222-2222-222222222222","name":"Sam Roe","email":"sam@contoso.com"},{"id":"33333333-3333-3333-3333-333333333333","name":"Sam Roe","email":"SAM@contoso.com"},{"email":"pat.lee@contoso.com","id":"1111AAAA-1111-1111-1111-111111111111","name":"Pat \"PJ\" Lee"}]},"sla":{"frt":{"escalation":{"enabled":false,"escalationAssignee":{"id":"99999999-9999-9999-9999-999999999999","name":"Pat Lee (old)","email":"pat.lee@contoso.com"}}}}}}
EOF

# Stubs, defined after the functions they replace would be looked up at call time.
have() { return 1; }  # no Azure CLI
curl() { cat >"$tmp/curl-config"; [ -f "$tmp/fail" ] && return 22; cat "$tmp/instance.json"; }

out=$(find_person 'pat.lee@CONTOSO.com' 'abc123')
check 'an email is matched in the assignee list, case aside' "$(printf '%s\n' "$out" | sed -n 1p)" '1111aaaa-1111-1111-1111-111111111111'
check 'its name comes back JSON-escaped' "$(printf '%s\n' "$out" | sed -n 2p)" 'Pat \"PJ\" Lee'
check 'and where it came from' "$(printf '%s\n' "$out" | sed -n 3p)" "the help desk's assignee list"
check 'the key goes on stdin to the US endpoint' "$(cat "$tmp/curl-config")" 'url = "https://teamswork.azure-api.net/ticketing/v1/instance?key=abc123&timezone=0"'

check 'a contact outside the assignee list with the same email is ignored' "$(find_person 'pat.lee@contoso.com' 'abc123' | sed -n 1p)" '1111aaaa-1111-1111-1111-111111111111'
check 'brackets and braces in a name are text, not structure' "$(find_person 'brackets@contoso.com' 'abc123' | sed -n 2p)" 'Bracket [Team] {Lead}'
check 'so the people after that name are still read' "$(people_with_email 'sam@contoso.com' <"$tmp/instance.json" | wc -l | tr -d ' ')" '2'
check 'someone only outside the assignee list is no match' "$(find_person 'lee@contoso.com' 'abc123')" ''
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
# .NET reads environment variables in any case and with either separator, so the lookup must too. A name with a colon
# can't be set from sh, so env_setting is run in a child given it.
TICKETING__BASEURL='https://elsewhere.example/v1'; export TICKETING__BASEURL; rm -f "$tmp/curl-config"
check 'so does one in upper case' "$(find_person 'pat.lee@contoso.com' 'abc123')$([ -f "$tmp/curl-config" ] && echo requested)" ''
unset TICKETING__BASEURL
env_setting_fn=$(sed -n '/^env_setting() {/,/^}/p' "$installer")
if [ -r "/proc/$$/environ" ]; then
    check 'a colon-separated name is read too' "$(env 'Ticketing:BaseUrl=https://elsewhere.example/v1' sh -c "$env_setting_fn
env_setting Ticketing:BaseUrl")" 'https://elsewhere.example/v1'
else
    echo 'skip a colon-separated name (no /proc here, and no shell can set one)'
fi
check 'and a lower-case one' "$(env 'ticketing__baseurl=https://elsewhere.example/v1' sh -c "$env_setting_fn
env_setting Ticketing:BaseUrl")" 'https://elsewhere.example/v1'
check 'an empty variable is set, to nothing, as .NET keeps it' "$(env 'Ticketing__BaseUrl=' sh -c "$env_setting_fn
v=\$(env_setting Ticketing:BaseUrl) && echo \"set:[\$v]\"")" 'set:[]'
check 'an unset variable is not set' "$(env -u Ticketing__BaseUrl sh -c "$env_setting_fn
env_setting Ticketing:BaseUrl || echo unset")" 'unset'
# An empty region variable overrides the file and --region, and the server then uses its default: so must the lookup.
printf '{\n  "Ticketing:Region": "EU"\n}\n' >"$SECRETS_PATH"
Ticketing__Region=''; export Ticketing__Region; find_person 'pat.lee@contoso.com' 'abc123' >/dev/null
check 'an empty region variable over a file region means US, as for the server' "$(cat "$tmp/curl-config")" 'url = "https://teamswork.azure-api.net/ticketing/v1/instance?key=abc123&timezone=0"'
REGION=EU; find_person 'pat.lee@contoso.com' 'abc123' >/dev/null
check 'and over --region too' "$(cat "$tmp/curl-config")" 'url = "https://teamswork.azure-api.net/ticketing/v1/instance?key=abc123&timezone=0"'
REGION=""; unset Ticketing__Region
find_person 'pat.lee@contoso.com' 'abc123' >/dev/null
check 'without the variable, the file region is used' "$(cat "$tmp/curl-config")" 'url = "https://ticketing-apim-eu.azure-api.net/ticketing/v1/instance?key=abc123&timezone=0"'
: >"$SECRETS_PATH"
Ticketing__Region=EU; export Ticketing__Region; find_person 'pat.lee@contoso.com' 'abc123' >/dev/null
check 'a region set in the environment picks the endpoint, as it does for the server' "$(cat "$tmp/curl-config")" 'url = "https://ticketing-apim-eu.azure-api.net/ticketing/v1/instance?key=abc123&timezone=0"'
unset Ticketing__Region
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
