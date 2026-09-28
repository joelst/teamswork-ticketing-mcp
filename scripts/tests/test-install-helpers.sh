#!/bin/sh
# Tests install.sh's account lookup without a terminal or the network: loads its functions from the script itself,
# stubs curl and the Azure CLI, and exits 1 on any failure. POSIX sh, like the installer (CI runs it under dash).
# The functions are loaded with eval, so shellcheck can't see that they read REGION and SECRETS_PATH.
# shellcheck disable=SC2034
set -u
here=$(cd "$(dirname "$0")" && pwd)
installer="$here/../install.sh"
for fn in json_escape secrets_text secret_get secret_has people_with_email env_setting vendor_endpoint find_person; do
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
# Two spellings with different values: .NET folds them into one key and which it reads last isn't defined, so the
# server's value can't be known (2). The same value twice is fine. Each in a child that really has both in its
# environment, as a colon name can only get there that way.
if [ -r "/proc/$$/environ" ]; then
    check 'a colon name and an underscore name with different values are unknowable' "$(env 'Ticketing:BaseUrl=https://elsewhere.example/v1' 'Ticketing__BaseUrl=https://teamswork.azure-api.net/ticketing/v1' sh -c "$env_setting_fn
env_setting Ticketing:BaseUrl; echo rc=\$?")" 'rc=2'
    check 'with the same value they are that value' "$(env 'Ticketing:BaseUrl=https://a.example' 'Ticketing__BaseUrl=https://a.example' sh -c "$env_setting_fn
env_setting Ticketing:BaseUrl; echo; echo rc=\$?")" 'https://a.example
rc=0'
    check 'a newline in a colon-named value is unknowable' "$(env 'Ticketing:Region=EU
not-a-region' sh -c "$env_setting_fn
env_setting Ticketing:Region; echo rc=\$?")" 'rc=2'
else
    echo 'skip conflicts with a colon-separated name (no /proc here)'
fi
check 'two cases of one name with different values are unknowable' "$(env 'Ticketing__BaseUrl=https://a.example' 'TICKETING__BASEURL=https://b.example' sh -c "$env_setting_fn
env_setting Ticketing:BaseUrl; echo rc=\$?")" 'rc=2'
check 'a value with a newline is unknowable, not cut at the line' "$(env 'Ticketing__Region=EU
not-a-region' sh -c "$env_setting_fn
env_setting Ticketing:Region; echo rc=\$?")" 'rc=2'
check "a line of another variable's value that looks like the setting is not the setting" "$(env -u Ticketing__BaseUrl 'X=a
Ticketing__BaseUrl=https://elsewhere.example/v1' sh -c "$env_setting_fn
env_setting Ticketing:BaseUrl; echo rc=\$?")" 'rc=1'
# End to end, an unknowable setting sends nothing, even with --region or the file giving a vendor endpoint.
Ticketing__Region='EU'; TICKETING__REGION='AUS'; export Ticketing__Region TICKETING__REGION; REGION=EU; rm -f "$tmp/curl-config"
check 'conflicting region variables get no request, even with --region' "$(find_person 'pat.lee@contoso.com' 'abc123' 2>/dev/null)$([ -f "$tmp/curl-config" ] && echo requested)" ''
unset Ticketing__Region TICKETING__REGION; REGION=""
# .NET refuses a secrets file with one key twice (in any case), so the server wouldn't start: no request either.
printf '{\n  "Ticketing:Region": "EU",\n  "ticketing:region": "AUS"\n}\n' >"$SECRETS_PATH"; rm -f "$tmp/curl-config"
check 'a key the file has twice gets no request' "$(find_person 'pat.lee@contoso.com' 'abc123' 2>/dev/null)$([ -f "$tmp/curl-config" ] && echo requested)" ''
# dotnet user-secrets starts the file with a byte order mark; every read sees past it, even to a key on that line.
bom=$(printf '\357\273\277')
printf '%s{\n  "Ticketing:BaseUrl": ""\n}\n' "$bom" >"$SECRETS_PATH"; rm -f "$tmp/curl-config"
check 'an empty base URL in a file with a byte order mark gets no request' "$(find_person 'pat.lee@contoso.com' 'abc123' 2>/dev/null)$([ -f "$tmp/curl-config" ] && echo requested)" ''
printf '%s"Ticketing:BaseUrl": "https://elsewhere.example/v1",\n' "$bom" >"$SECRETS_PATH"
check 'a key right after the mark is found' "$(secret_has 'Ticketing:BaseUrl' && secret_get 'Ticketing:BaseUrl')" 'https://elsewhere.example/v1'
printf '%s"Ticketing:Region": "EU",\n  "ticketing:region": "AUS"\n' "$bom" >"$SECRETS_PATH"; rm -f "$tmp/curl-config"
check 'and counts toward a key the file has twice' "$(find_person 'pat.lee@contoso.com' 'abc123' 2>/dev/null)$([ -f "$tmp/curl-config" ] && echo requested)" ''
rm -f "$SECRETS_PATH"; rm -f "$tmp/curl-config"; find_person 'pat.lee@contoso.com' 'abc123' >/dev/null 2>&1
check 'no secrets file at all still looks up US' "$(cat "$tmp/curl-config" 2>/dev/null)" 'url = "https://teamswork.azure-api.net/ticketing/v1/instance?key=abc123&timezone=0"'
: >"$SECRETS_PATH"
# An empty region variable overrides the file and --region, and the server then uses its default: so must the lookup.
printf '{\n  "Ticketing:Region": "EU"\n}\n' >"$SECRETS_PATH"
Ticketing__Region=''; export Ticketing__Region; rm -f "$tmp/curl-config"; find_person 'pat.lee@contoso.com' 'abc123' >/dev/null
check 'an empty region variable over a file region means US, as for the server' "$(cat "$tmp/curl-config" 2>/dev/null)" 'url = "https://teamswork.azure-api.net/ticketing/v1/instance?key=abc123&timezone=0"'
REGION=EU; rm -f "$tmp/curl-config"; find_person 'pat.lee@contoso.com' 'abc123' >/dev/null
check 'and over --region too' "$(cat "$tmp/curl-config" 2>/dev/null)" 'url = "https://teamswork.azure-api.net/ticketing/v1/instance?key=abc123&timezone=0"'
REGION=""; unset Ticketing__Region
rm -f "$tmp/curl-config"; find_person 'pat.lee@contoso.com' 'abc123' >/dev/null
check 'without the variable, the file region is used' "$(cat "$tmp/curl-config" 2>/dev/null)" 'url = "https://ticketing-apim-eu.azure-api.net/ticketing/v1/instance?key=abc123&timezone=0"'
# The server trims the region and treats a blank one as unset (US), so the lookup must too.
# The stub's record is cleared first, so a request that never happens fails these rather than reading an older one.
Ticketing__Region='   '; export Ticketing__Region; rm -f "$tmp/curl-config"; find_person 'pat.lee@contoso.com' 'abc123' >/dev/null
check 'a blank region means US, as for the server' "$(cat "$tmp/curl-config" 2>/dev/null)" 'url = "https://teamswork.azure-api.net/ticketing/v1/instance?key=abc123&timezone=0"'
unset Ticketing__Region
printf '{\n  "Ticketing:Region": " eu "\n}\n' >"$SECRETS_PATH"; rm -f "$tmp/curl-config"; find_person 'pat.lee@contoso.com' 'abc123' >/dev/null
check 'a region with spaces around it is that region' "$(cat "$tmp/curl-config" 2>/dev/null)" 'url = "https://ticketing-apim-eu.azure-api.net/ticketing/v1/instance?key=abc123&timezone=0"'
: >"$SECRETS_PATH"

# vendor_endpoint follows the server's startup rules (TicketingOptions and Program.cs), so the key goes only where the
# server itself would send it, and only to the vendor.
us='https://teamswork.azure-api.net/ticketing/v1'; eu='https://ticketing-apim-eu.azure-api.net/ticketing/v1'; aus='https://ticketing-apim-aus.azure-api.net/ticketing/v1'
check 'nothing set is US' "$(vendor_endpoint '' '' '')" "$us"
check 'a region picks its endpoint' "$(vendor_endpoint 'eu' '' '')" "$eu"
check 'the built-in US base URL, set, is a placeholder a region replaces' "$(vendor_endpoint 'EU' set "$us")" "$eu"
check 'in any case and with a trailing slash' "$(vendor_endpoint 'AUS' set 'HTTPS://teamswork.azure-api.net/ticketing/v1/')" "$aus"
check 'the US base URL alone is US' "$(vendor_endpoint '' set "$us/")" "$us"
check 'a vendor endpoint set as the base URL is used' "$(vendor_endpoint '' set "$eu")" "$eu"
check 'and agrees with its own region' "$(vendor_endpoint ' eu ' set "$eu")" "$eu"
check 'a region and a base URL naming different endpoints stop the server: no request' "$(vendor_endpoint 'EU' set "$aus")" ''
check 'a custom base URL: no request' "$(vendor_endpoint '' set 'https://elsewhere.example/v1')" ''
check 'nor with a region' "$(vendor_endpoint 'EU' set 'https://elsewhere.example/v1')" ''
check 'an empty base URL: no request' "$(vendor_endpoint '' set '')" ''
check 'an unknown region: no request' "$(vendor_endpoint 'XX' '' '')" ''
check 'the server trims a region only at the ends, so E U is unknown: no request' "$(vendor_endpoint 'E U' '' '')" ''
printf '{\n  "Ticketing:BaseUrl": "%s",\n  "Ticketing:Region": "EU"\n}\n' "$us" >"$SECRETS_PATH"; rm -f "$tmp/curl-config"
check 'end to end, the built-in URL in the file with a region looks up the regional list' "$(find_person 'pat.lee@contoso.com' 'abc123' | sed -n 1p)$(cat "$tmp/curl-config" 2>/dev/null)" '1111aaaa-1111-1111-1111-111111111111url = "https://ticketing-apim-eu.azure-api.net/ticketing/v1/instance?key=abc123&timezone=0"'
# An empty base URL in the file is set and empty, as .NET keeps it, and the server refuses it: no request.
printf '{\n  "Ticketing:BaseUrl": ""\n}\n' >"$SECRETS_PATH"; rm -f "$tmp/curl-config"
check 'an empty base URL in the file gets no request' "$(find_person 'pat.lee@contoso.com' 'abc123')$([ -f "$tmp/curl-config" ] && echo requested)" ''
: >"$SECRETS_PATH"

# Only item.assignees.peoples counts: an assignees list nested under a custom field, or one outside item, doesn't.
cp "$tmp/instance.json" "$tmp/instance-full.json"
printf '%s\n' '{"before":{"assignees":{"peoples":[{"id":"88888888-8888-8888-8888-888888888888","name":"Root","email":"root@contoso.com"}]}},"item":{"id":"i","customFieldsLeft":[{"id":"f","extension":{"assignees":{"type":"x","peoples":[{"id":"99999999-0000-0000-0000-000000000000","name":"Nested","email":"pat.lee@contoso.com"},{"id":"99999999-1111-0000-0000-000000000000","name":"Only Nested","email":"nested@contoso.com"}]}}}],"assignees":{"type":"teamsOwner","peoples":[{"id":"1111aaaa-1111-1111-1111-111111111111","name":"Pat","email":"pat.lee@contoso.com"}]}}}' >"$tmp/instance.json"
check 'a nested list of the same name is not the assignee list' "$(find_person 'pat.lee@contoso.com' 'abc123' | sed -n 1p)" '1111aaaa-1111-1111-1111-111111111111'
check 'so someone only in it is no match' "$(find_person 'nested@contoso.com' 'abc123')" ''
check 'nor is someone in a list outside item' "$(find_person 'root@contoso.com' 'abc123')" ''
cp "$tmp/instance-full.json" "$tmp/instance.json"

# No assignee list, or a null one, finds no one, rather than the search running on into a later people array.
cp "$tmp/instance.json" "$tmp/instance-full.json"
later='"customFieldsRight":[{"id":"g","defaultValue":[{"id":"66666666-6666-6666-6666-666666666666","name":"Later","email":"later@contoso.com"}]}],"x":{"peoples":[{"id":"77777777-7777-7777-7777-777777777777","name":"Stray","email":"stray@contoso.com"}]}'
for shape in '"assignees":null' '"assignees":{"type":"teamsOwner","peoples":null}' '"assignees":{"type":"teamsOwner"}' '"noAssignees":1'; do
    printf '{"item":{"id":"i",%s,%s}}\n' "$shape" "$later" >"$tmp/instance.json"
    check "no match with $shape" "$(find_person 'later@contoso.com' 'abc123')$(find_person 'stray@contoso.com' 'abc123')" ''
done
cp "$tmp/instance-full.json" "$tmp/instance.json"
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
