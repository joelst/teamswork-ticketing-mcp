#!/bin/sh
# Installs the TeamsWork Ticketing MCP server on macOS or Linux and registers it with local MCP clients.
#
# Downloads a release from GitHub, checks it against the release's SHA256SUMS.txt, and puts the executable at a
# fixed per-user path (default ~/.local/share/teamswork-ticketing-mcp), so client configurations keep working across
# upgrades. Run it again to upgrade; running clients keep the old version until they restart.
#
# The server needs the Ticketing API key and the account that ticket changes are attributed to. Both are stored in
# the .NET user-secrets file the server reads (~/.microsoft/usersecrets/teamswork-taas-mcp/secrets.json), never in a
# client config.
#
# The server is then registered, over stdio, with every supported client found on PATH (or the ones named by
# --clients): Claude Code, Codex CLI, GitHub Copilot CLI, and VS Code (GitHub Copilot Chat).
#
#   curl -fsSL https://raw.githubusercontent.com/joelst/teamswork-ticketing-mcp/main/scripts/install.sh | sh
#   curl -fsSL https://raw.githubusercontent.com/joelst/teamswork-ticketing-mcp/main/scripts/install.sh | sh -s -- --clients claude,vscode
#   sh install.sh --uninstall
set -eu

# The release workflow sets this in the copy attached to each release, so that copy installs its own release.
PINNED_TAG=""

REPO=joelst/teamswork-ticketing-mcp
SERVER_NAME=teamswork-ticketing
EXE_NAME=TeamsWork.Ticketing.Mcp
SECRETS_PATH="$HOME/.microsoft/usersecrets/teamswork-taas-mcp/secrets.json"
ALL_CLIENTS="claude codex copilot vscode"

VERSION=""
CLIENTS=""
INSTALL_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/teamswork-ticketing-mcp"
SKIP_SECRETS=0
REGION=""
UNINSTALL=0
REMOVE_SECRETS=0
FAILED=""

usage() {
    cat <<EOF
Usage: install.sh [options]

  --version <tag>       Release to install, for example v0.2.0, or 'latest' (default: the release this copy was
                        attached to, or for the copy on main, the newest release, pre-releases included)
  --clients <list>      Comma-separated: claude, codex, copilot, vscode, all, or none (default: those on PATH)
  --install-dir <dir>   Where to put the executable (default: $INSTALL_DIR)
  --skip-secrets        Don't prompt for the API key and account; keep what the secrets file already has
  --region <name>       Data region of your Ticketing instance: US (the default), EU, or AUS. Saved in the secrets
                        file; leave it out to keep the region already saved there
  --uninstall           Unregister from the clients and delete the server's files (and the folder, if empty)
  --remove-secrets      With --uninstall, also delete $SECRETS_PATH
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --version) VERSION="$2"; shift 2 ;;
        --clients) CLIENTS="$2"; shift 2 ;;
        --install-dir) INSTALL_DIR="$2"; shift 2 ;;
        --skip-secrets) SKIP_SECRETS=1; shift ;;
        --region) REGION="$2"; shift 2 ;;
        --uninstall) UNINSTALL=1; shift ;;
        --remove-secrets) REMOVE_SECRETS=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
done

if [ -n "$REGION" ] && [ "$UNINSTALL" = 0 ]; then
    REGION=$(printf '%s' "$REGION" | tr -d '[:space:]' | tr '[:lower:]' '[:upper:]')
    case "$REGION" in
        US|EU|AUS) ;;
        *) echo "Unknown --region '$REGION'. Use US (the default), EU, or AUS." >&2; exit 2 ;;
    esac
    if [ "$SKIP_SECRETS" = 1 ]; then
        echo "--region is saved in the secrets file, which --skip-secrets leaves alone. Drop --skip-secrets, or set the Ticketing__Region environment variable in each client's MCP config instead." >&2
        exit 2
    fi
fi

# Clients start the server from their own working directory, so they must be given an absolute path.
case "$INSTALL_DIR" in
    /*) ;;
    *) INSTALL_DIR="$(pwd)/${INSTALL_DIR#./}" ;;
esac
EXE_PATH="$INSTALL_DIR/$EXE_NAME"
VERSION_PATH="$INSTALL_DIR/$EXE_NAME.version"

step() { printf '\033[36m==> %s\033[0m\n' "$1"; }
warn() { printf '\033[33mWARNING: %s\033[0m\n' "$1" >&2; }
die() { printf '\033[31mERROR: %s\033[0m\n' "$1" >&2; exit 1; }
have() { command -v "$1" >/dev/null 2>&1; }

client_command() {
    case "$1" in vscode) echo code ;; *) echo "$1" ;; esac
}

is_wsl() { grep -qi microsoft /proc/sys/kernel/osrelease 2>/dev/null; }

# Prints why a client can't be registered from here, or nothing if it can.
client_problem() {
    cmd=$(client_command "$1")
    have "$cmd" || { echo "'$cmd' is not on PATH"; return; }
    # WSL puts Windows programs on PATH. Those run on Windows and would be given a Linux path they can't start;
    # install with scripts/install.ps1 on Windows for them instead.
    if is_wsl; then
        case "$(command -v "$cmd")" in /mnt/*) echo "'$cmd' is the Windows program; use install.ps1 on Windows for it" ;; esac
    fi
}

# Sets TARGETS. Not run in a subshell, so die() ends the script.
resolve_clients() {
    TARGETS=""
    if [ -z "$CLIENTS" ]; then
        for c in $ALL_CLIENTS; do
            problem=$(client_problem "$c")
            if [ -z "$problem" ]; then
                TARGETS="$TARGETS $c"
            elif have "$(client_command "$c")"; then
                echo "Skipping $c: $problem."
            fi
        done
        [ -n "$TARGETS" ] || echo 'No supported MCP client found on PATH; skipping registration.'
        return 0
    fi
    names=$(echo "$CLIENTS" | tr ',' ' ' | tr '[:upper:]' '[:lower:]')
    for n in $names; do
        case "$n" in
            none) TARGETS=""; return 0 ;;
            all) TARGETS="$ALL_CLIENTS"; return 0 ;;
            claude|codex|copilot|vscode) TARGETS="$TARGETS $n" ;;
            *) die "Unknown client: $n. Use claude, codex, copilot, vscode, all, or none." ;;
        esac
    done
}

unregister_client() {
    # Removing a server that isn't registered fails harmlessly, so the output and result are ignored.
    case "$1" in
        claude) claude mcp remove --scope user "$SERVER_NAME" >/dev/null 2>&1 || true ;;
        codex) codex mcp remove "$SERVER_NAME" >/dev/null 2>&1 || true ;;
        copilot) copilot mcp remove "$SERVER_NAME" >/dev/null 2>&1 || true ;;
    esac
}

# Escapes text for use inside a JSON string, including control characters such as a pasted tab, which JSON doesn't
# allow unescaped.
json_escape() {
    printf '%s' "$1" | awk '
        BEGIN { for (i = 1; i < 32; i++) esc[sprintf("%c", i)] = sprintf("\\u%04x", i)
                esc["\t"] = "\\t"; esc["\\"] = "\\\\"; esc["\""] = "\\\"" }
        NR > 1 { printf "\\n" }
        { n = length($0); for (i = 1; i <= n; i++) { c = substr($0, i, 1); printf "%s", (c in esc) ? esc[c] : c } }'
}

# Prints the client's current entry for this server, or nothing if it has none. VS Code has no command for this, so
# its user mcp.json is read instead (the default profile only).
registration() {
    if [ "$1" = vscode ]; then
        if [ "$(uname -s)" = Darwin ]; then f="$HOME/Library/Application Support/Code/User/mcp.json"
        else f="${XDG_CONFIG_HOME:-$HOME/.config}/Code/User/mcp.json"; fi
        if [ -f "$f" ] && grep -qF "\"$SERVER_NAME\"" "$f"; then cat "$f"; fi
        return 0
    fi
    out=$("$1" mcp get "$SERVER_NAME" 2>/dev/null) && printf '%s\n' "$out"
    return 0
}

# Returns non-zero if the client ends up unregistered.
register_client() {
    problem=$(client_problem "$1")
    if [ -n "$problem" ]; then warn "$1: $problem; skipped."; return 0; fi
    # An entry that already runs this executable is kept, so settings added to it in the client's config (such as env,
    # with --skip-secrets) survive upgrades. The path doesn't change between versions, so there's nothing to update.
    existing=$(registration "$1")
    path="$EXE_PATH"
    [ "$1" != vscode ] || path=$(json_escape "$EXE_PATH")
    if [ -n "$existing" ] && printf '%s\n' "$existing" | grep -qF -- "$path"; then
        echo "    already registered with $1; kept its settings"
        return 0
    fi
    unregister_client "$1"
    log=$(mktemp)
    ok=1
    case "$1" in
        claude) claude mcp add --transport stdio --scope user "$SERVER_NAME" -- "$EXE_PATH" --stdio >"$log" 2>&1 || ok=0 ;;
        codex) codex mcp add "$SERVER_NAME" -- "$EXE_PATH" --stdio >"$log" 2>&1 || ok=0 ;;
        copilot) copilot mcp add "$SERVER_NAME" -- "$EXE_PATH" --stdio >"$log" 2>&1 || ok=0 ;;
        vscode)
            # code exits 0 even when it rejects the argument, so success is read from its output.
            # Re-adding replaces the entry.
            json="{\"name\":\"$SERVER_NAME\",\"type\":\"stdio\",\"command\":\"$(json_escape "$EXE_PATH")\",\"args\":[\"--stdio\"]}"
            code --add-mcp "$json" >"$log" 2>&1 || true
            grep -q 'Added MCP servers' "$log" || ok=0
            ;;
    esac
    if [ "$ok" = 1 ]; then
        echo "    registered with $1"
    else
        sed 's/^/    /' "$log"
        if [ -n "$existing" ] && [ "$1" != vscode ]; then
            warn "$1: registration failed (output above), and its previous registration was removed."
        else
            warn "$1: registration failed (output above)."
        fi
    fi
    rm -f "$log"
    [ "$ok" = 1 ]
}

detect_rid() {
    os=$(uname -s)
    arch=$(uname -m)
    case "$os" in
        Darwin)
            # A shell running under Rosetta reports x86_64 on Apple silicon.
            if [ "$arch" = arm64 ] || [ "$(sysctl -n hw.optional.arm64 2>/dev/null || echo 0)" = 1 ]; then
                echo osx-arm64
                return
            fi ;;
        Linux)
            # The published Linux executable is built against glibc, and doesn't start on musl (Alpine and others).
            if [ -e /lib/ld-musl-x86_64.so.1 ] || { have ldd && ldd --version 2>&1 | grep -qi musl; }; then
                die "This system uses musl libc, which the published Linux executable doesn't support. Use the portable release with the .NET 10 runtime: dotnet TeamsWork.Ticketing.Mcp.dll --stdio"
            fi
            if [ "$arch" = x86_64 ]; then echo linux-x64; return; fi ;;
        MINGW*|MSYS*|CYGWIN*)
            die "This installer is for macOS and Linux. On Windows use scripts/install.ps1." ;;
    esac
    die "No executable is published for $os $arch. Use the portable release with the .NET 10 runtime: dotnet TeamsWork.Ticketing.Mcp.dll --stdio"
}

sha256() {
    if have sha256sum; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi
}

# Unauthenticated API calls are limited to 60 an hour per IP address, which shared networks and CI runners hit, so
# use a token when there is one. It goes to curl on stdin, since command lines are visible to other users. The
# repository is public, so if the token is rejected (expired, or scoped to another organization), try without it.
github_api() {
    if [ -n "${GITHUB_TOKEN:-}" ] &&
        printf 'header = "Authorization: Bearer %s"\n' "$GITHUB_TOKEN" |
            curl -fsSL -K - -H 'Accept: application/vnd.github+json' "$1" 2>/dev/null; then
        return 0
    fi
    curl -fsSL -H 'Accept: application/vnd.github+json' "$1"
}

# Sets TAG, ARCHIVE_URL, and SUMS_URL from the GitHub releases API.
find_release() {
    rid="$1"
    api="https://api.github.com/repos/$REPO/releases"
    tag="$VERSION"
    [ -n "$tag" ] || tag="$PINNED_TAG"
    [ "$tag" != latest ] || tag=""
    if [ -n "$tag" ]; then
        case "$tag" in v*) ;; *) tag="v$tag" ;; esac
        json=$(github_api "$api/tags/$tag") ||
            die "Couldn't get release $tag of $REPO. Check the tag on https://github.com/$REPO/releases."
    else
        # Newest first. /releases/latest would skip pre-releases, and 0.x versions are published as pre-releases.
        json=$(github_api "$api?per_page=20") || die "Couldn't list the releases of $REPO."
    fi
    # The API sometimes pretty-prints and sometimes returns everything on one line, so first split at the characters
    # a key can follow (',', '{', '['): each "key": "value" pair then starts a line. Download URLs contain none of them.
    # A quote inside a string value is escaped, so text such as release notes can't start a line with a key.
    # With a token that has push access the list includes drafts, whose files can't be downloaded without it, so
    # drop the files of any release marked as a draft. Each release starts with its "url", .../releases/<id>.
    urls=$(printf '%s\n' "$json" | tr ',' '\n' | tr '{' '\n' | tr '[' '\n' |
        awk '
            function flush() { if (!draft) printf "%s", files; files = ""; draft = 0 }
            /^[[:space:]]*"url"[[:space:]]*:[[:space:]]*"[^"]*\/releases\/[0-9]+"/ { flush() }
            /^[[:space:]]*"draft"[[:space:]]*:[[:space:]]*true/ { draft = 1 }
            /^[[:space:]]*"browser_download_url"/ { files = files $0 "\n" }
            END { flush() }' |
        sed -n 's/^[[:space:]]*"browser_download_url"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
    # The first release whose files include this platform's archive and the checksums. A release is published some
    # minutes before the workflow attaches its files.
    for url in $urls; do
        release_tag=${url%/*}
        release_tag=${release_tag##*/}
        [ "${url##*/}" = "teamswork-ticketing-mcp-${release_tag#v}-$rid.tar.gz" ] || continue
        if printf '%s\n' "$urls" | grep -qxF "${url%/*}/SHA256SUMS.txt"; then
            TAG="$release_tag"
            ARCHIVE_URL="$url"
            SUMS_URL="${url%/*}/SHA256SUMS.txt"
            return 0
        fi
    done
    if [ -n "$tag" ]; then die "Release $tag has no $rid build or SHA256SUMS.txt (yet)."; fi
    die "No release of $REPO has a $rid build yet."
}

install_binary() {
    have curl || die "curl is required to download the server. Install it (for example: apt install curl), then run the installer again."
    rid=$(detect_rid)
    find_release "$rid"
    archive_name="${ARCHIVE_URL##*/}"

    step "Downloading $archive_name ($TAG)"
    temp=$(mktemp -d)
    trap 'rm -rf "$temp"' EXIT
    curl -fsSL -o "$temp/$archive_name" "$ARCHIVE_URL"
    curl -fsSL -o "$temp/SHA256SUMS.txt" "$SUMS_URL"
    expected=$(awk -v f="$archive_name" '$2 == f || $2 == "*" f { print $1; exit }' "$temp/SHA256SUMS.txt")
    [ -n "$expected" ] || die "SHA256SUMS.txt has no entry for $archive_name."
    actual=$(sha256 "$temp/$archive_name")
    [ "$(echo "$expected" | tr '[:upper:]' '[:lower:]')" = "$actual" ] ||
        die "Checksum mismatch for $archive_name (expected $expected, got $actual)."
    echo '    checksum OK'

    tar -xzf "$temp/$archive_name" -C "$temp"
    extracted=$(find "$temp" -type f -name "$EXE_NAME" | head -n1)
    [ -n "$extracted" ] || die "$EXE_NAME not found in $archive_name."
    mkdir -p "$INSTALL_DIR"
    # Copy then rename, so a client that is running the old executable keeps working until it restarts.
    cp "$extracted" "$EXE_PATH.new"
    chmod +x "$EXE_PATH.new"
    mv -f "$EXE_PATH.new" "$EXE_PATH"
    # Only files named after the executable, so a shared --install-dir (such as ~/.local/bin) gets nothing that could
    # clash with other programs' files, and uninstall knows exactly what to delete.
    echo "$TAG" >"$VERSION_PATH"
    if [ "$(uname -s)" = Darwin ]; then xattr -d com.apple.quarantine "$EXE_PATH" 2>/dev/null || true; fi
    echo "    installed $EXE_PATH"
}

# The secrets file as its lines are read everywhere below, or nothing when there is none. dotnet user-secrets starts
# the file with a UTF-8 byte order mark, which grep doesn't count as space, so it is dropped first (in the C locale,
# where sed takes the bytes as they are): every read then sees the same lines, and none misses a key behind the mark.
secrets_text() {
    [ -f "$SECRETS_PATH" ] || return 0
    LC_ALL=C sed "1s/^$(printf '\357\273\277')//" "$SECRETS_PATH"
}

# The secrets file can only be updated safely without a JSON parser when it is a flat object with one
# "key": "string" pair per line, which is how dotnet user-secrets and this script write it. A key with an escape in it
# ("Ticketing:BaseUrl") is refused: .NET decodes it into a setting that every literal key match below would miss.
secrets_file_editable() {
    ! secrets_text |
        grep -vqE '^[[:space:]]*([{}]|\{[[:space:]]*\}|"[^"\\]+"[[:space:]]*:[[:space:]]*"([^"\\]|\\.)*"[[:space:]]*,?)?[[:space:]]*$'
}

# Prints a value from the secrets file still JSON-escaped, so an unchanged value is written back exactly as it was
# (dotnet user-secrets writes non-ASCII characters as \uXXXX escapes).
# Keys match case-insensitively, as .NET configuration keys do.
secret_get() {
    secrets_text | grep -iE "^[[:space:]]*\"$1\"[[:space:]]*:" | head -n1 |
        sed -n 's/^[[:space:]]*"[^"]*"[[:space:]]*:[[:space:]]*"\(.*\)"[[:space:]]*,\{0,1\}[[:space:]]*$/\1/p'
}

# Succeeds when the secrets file has the key, whatever its value: .NET keeps an empty one, so it is set, and empty.
secret_has() {
    secrets_text | grep -qiE "^[[:space:]]*\"$1\"[[:space:]]*:"
}

# Prompts on the terminal, since stdin is the script itself under `curl | sh`. $2 is the current value, JSON-escaped;
# prints the answer JSON-escaped.
ask() {
    prompt="$1"; current="$2"
    while :; do
        if [ -n "$current" ]; then printf '%s [%s]: ' "$prompt" "$current" >/dev/tty; else printf '%s: ' "$prompt" >/dev/tty; fi
        IFS= read -r value </dev/tty || value=""
        value=$(printf '%s' "$value" | tr -d '\r')
        if [ -n "$value" ]; then json_escape "$value"; return; fi
        if [ -n "$current" ]; then printf '%s' "$current"; return; fi
    done
}

# Reads the instance JSON on stdin and prints "id<TAB>name" (still JSON-escaped) for each person in its assignee list
# whose email is $1, compared in lower case. Only item.assignees.peoples counts, as in install.ps1: people elsewhere in
# the response (an SLA escalation contact, a people-picker default) aren't the help desk's list, and one of them with
# the same email would make the real match look ambiguous.
# The list is found by walking the JSON, honouring strings, down the exact path item > assignees > peoples, so a null
# or missing list finds no one and a list of the same name anywhere else doesn't count. Braces and brackets inside
# strings (a display name such as "A [B]") aren't structure: before the list is split into people at "{", those four
# characters inside strings are swapped for control characters, which valid JSON never has raw in a string, and the
# values printed get them back.
people_with_email() {
    tr -d '\n' | awk -v want="$1" '
        function shield(s,   out, c, i, n, quoted, escaped) {
            n = length(s); out = ""; quoted = 0; escaped = 0
            for (i = 1; i <= n; i++) {
                c = substr(s, i, 1)
                if (quoted) {
                    if (escaped) escaped = 0
                    else if (c == "\\") escaped = 1
                    else if (c == "\"") quoted = 0
                    else if (c == "{") c = "\001"
                    else if (c == "}") c = "\002"
                    else if (c == "[") c = "\003"
                    else if (c == "]") c = "\004"
                } else if (c == "\"") quoted = 1
                out = out c
            }
            return out
        }
        function unshield(s) {
            gsub("\001", "{", s); gsub("\002", "}", s); gsub("\003", "[", s); gsub("\004", "]", s)
            return s
        }
        function field(s, k,   re, m) {
            re = "\"" k "\"[ \t]*:[ \t]*\"([^\"\\\\]|\\\\.)*\""
            if (!match(s, re)) return ""
            m = substr(s, RSTART, RLENGTH)
            sub("^\"" k "\"[ \t]*:[ \t]*\"", "", m)
            sub("\"$", "", m)
            return unshield(m)
        }
        # The inside of the item.assignees.peoples array, or "" when there is none (a null or missing list, or one
        # anywhere else, such as under a custom field): walks the JSON once, honouring strings, and keeps the kind of
        # each open container and the key being read in each object on the way down, so the array is taken only at
        # that exact path.
        function peoples(s,   n, i, c, depth, quoted, escaped, from, text, keyed, start) {
            n = length(s); depth = 0; quoted = 0; escaped = 0; keyed = 0; start = 0
            for (i = 1; i <= n; i++) {
                c = substr(s, i, 1)
                if (quoted) {
                    if (escaped) escaped = 0
                    else if (c == "\\") escaped = 1
                    else if (c == "\"") { quoted = 0; text = substr(s, from + 1, i - from - 1); keyed = 1 }
                    continue
                }
                if (c == "\"") { quoted = 1; from = i; continue }
                if (c == " " || c == "\t" || c == "\r") continue
                if (c == ":") { if (keyed && kind[depth] == "{") key[depth] = text; keyed = 0; continue }
                keyed = 0
                if (c == "{" || c == "[") {
                    if (c == "[" && depth == 3 && kind[1] == "{" && kind[2] == "{" && kind[3] == "{" &&
                        key[1] == "item" && key[2] == "assignees" && key[3] == "peoples") start = i + 1
                    depth++; kind[depth] = c; key[depth] = ""
                } else if (c == "}" || c == "]") {
                    if (start && depth == 4 && c == "]") return substr(s, start, i - start)
                    depth--
                } else if (c == ",") key[depth] = ""
            }
            return ""
        }
        {
            # The list, then its people: person objects hold no nested braces, so with braces in strings shielded,
            # splitting at "{" gives one person each.
            n = split(shield(peoples($0)), people, "{")
            for (p = 2; p <= n; p++)
                if (tolower(field(people[p], "email")) == want) {
                    id = field(people[p], "id")
                    if (id != "") printf "%s\t%s\n", id, field(people[p], "name")
                }
        }'
}

# Prints a setting as the server's .NET configuration will see it from the environment: either separator
# (Ticketing__BaseUrl or Ticketing:BaseUrl) in any case. Nothing when it isn't set there. An environment variable wins
# over the secrets file, and the account lookup decides with these where the API key may go, so it must read them as
# the server does. A name with a colon can't be a shell variable, and some shells (dash) drop it from what they pass to
# `env` while others (bash, macOS's sh) pass it on, so on Linux the environment this script started with is read too.
# Without that view (macOS), a colon name that `env` shows makes the setting unknowable (2, below); one a shell dropped
# can't be seen at all there.
# Succeeds, printing the value, when the variable is set, even to nothing: .NET keeps an empty value and it overrides
# the file, so an empty Ticketing__Region means the server's default, US. Returns 1 when it isn't set at all.
# Returns 2, printing nothing, when the server's view can't be known, so the caller must not guess: two spellings set
# to different values (.NET folds them into one key, and which one it reads last isn't defined), or a value with a
# newline, which the server reads whole but a line-based reading would cut short.
# Values are read whole: through the shell for the names it can hold (`env` only lists names, and a name that isn't
# really set, a line of some other variable's value, is skipped), and on Linux from the NUL-separated start-up
# environment with newlines swapped for \001. Each value is marked with a leading "=" so an empty one isn't lost.
env_setting() {
    es_a=$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]')
    es_b=$(printf '%s' "$es_a" | sed 's/:/__/g')
    es_values=$(
        for es_name in $(env | sed -n 's/^\([A-Za-z_][A-Za-z0-9_]*\)=.*/\1/p'); do
            [ "$(printf '%s' "$es_name" | tr '[:upper:]' '[:lower:]')" = "$es_b" ] || continue
            eval "[ -n \"\${$es_name+x}\" ]" || continue
            printf '='; eval "printf '%s' \"\$$es_name\"" | tr '\n' '\001'; printf '\n'
        done
        if [ -r "/proc/$$/environ" ]; then
            tr '\n\0' '\001\n' <"/proc/$$/environ" | awk -v a="$es_a" -v b="$es_b" '
                { k = $0; sub(/=.*/, "", k); k = tolower(k); if (k == a || k == b) { v = $0; sub(/^[^=]*=/, "", v); print "=" v } }'
        else
            # Its value can't be read whole from `env`, so it is marked unknowable. A line of another variable's value
            # that looks like it is marked too, which only means no request.
            env | awk -v a="$es_a" '{ k = $0; sub(/=.*/, "", k); if (tolower(k) == a) print "=\001" }'
        fi
    )
    [ -n "$es_values" ] || return 1
    case "$es_values" in *"$(printf '\001')"*) return 2 ;; esac
    [ -z "$(printf '%s\n' "$es_values" | sort -u | sed -n 2p)" ] || return 2
    es_value=$(printf '%s\n' "$es_values" | head -n1)
    printf '%s' "${es_value#=}"
}

# Prints the endpoint the server will call for region $1 and base URL $3 ($2 is "set" when a base URL is configured,
# even an empty one), when it is one of the vendor's; nothing otherwise, and then no request is made: the API key is
# only ever sent to one of these fixed vendor endpoints, and only to the one the server would use for the settings as
# read here (settings this script can't read as the server does get no request). Follows the server's startup rules
# (TicketingOptions and its validation in Program.cs), as install.ps1's Resolve-VendorEndpoint does: the base URL is
# the built-in US one unless set; a region (trimmed at the ends only, any case, blank meaning unset) replaces that
# built-in value but not one set to anything else; an unknown region, or a region and base URL naming different
# endpoints, stops the server. A custom base URL, even an empty one, is never a vendor endpoint.
vendor_endpoint() {
    ve_us='https://teamswork.azure-api.net/ticketing/v1'
    ve_eu='https://ticketing-apim-eu.azure-api.net/ticketing/v1'
    ve_aus='https://ticketing-apim-aus.azure-api.net/ticketing/v1'
    if [ "$2" = set ]; then ve_url=$(printf '%s' "$3" | sed 's:/*$::' | tr '[:upper:]' '[:lower:]'); else ve_url=$ve_us; fi
    ve_region=$(printf '%s' "$1" | sed 's/^[[:space:]]*//; s/[[:space:]]*$//' | tr '[:lower:]' '[:upper:]')
    if [ -n "$ve_region" ]; then
        case "$ve_region" in
            US) ve_regional=$ve_us ;;
            EU) ve_regional=$ve_eu ;;
            AUS) ve_regional=$ve_aus ;;
            *) return 0 ;;
        esac
        [ "$ve_url" != "$ve_us" ] || ve_url=$ve_regional
        [ "$ve_url" = "$ve_regional" ] || return 0
    fi
    for ve in "$ve_us" "$ve_eu" "$ve_aus"; do
        if [ "$ve_url" = "$ve" ]; then printf '%s' "$ve"; return 0; fi
    done
}

# Prints the Entra object ID and display name (JSON-escaped) and where they came from, one per line, for the email
# $1, so nobody has to know their own GUID: from the help desk's assignee list (read with the API key $2), then from
# the directory through the Azure CLI. Prints nothing unless exactly one person matches. The key goes only to the
# vendor endpoint vendor_endpoint finds, never to a custom base URL (which a planted setting could point elsewhere),
# on curl's stdin rather than its command line, and only when it needs no escaping in a URL.
find_person() {
    fp_email="$1"; fp_key="$2"
    fp_want=$(printf '%s' "$fp_email" | tr '[:upper:]' '[:lower:]')
    # The region and base URL the server will see: the environment first (a variable that is set wins even when
    # empty), then --region, then the secrets file. When that can't be known (conflicting variables, a value with a
    # newline, or a key the file has twice, which the server refuses), no request is made.
    fp_blocked=""
    for fp_setting in 'Ticketing:Region' 'Ticketing:BaseUrl'; do
        fp_n=$(secrets_text | grep -ciE "^[[:space:]]*\"$fp_setting\"[[:space:]]*:") || :
        [ "${fp_n:-0}" -le 1 ] || fp_blocked=$fp_setting
    done
    fp_rc=0; fp_region=$(env_setting 'Ticketing:Region') || fp_rc=$?
    if [ "$fp_rc" = 2 ]; then fp_blocked='Ticketing:Region'
    elif [ "$fp_rc" != 0 ]; then
        fp_region=$REGION
        [ -n "$fp_region" ] || fp_region=$(secret_get 'Ticketing:Region')
    fi
    fp_url_set=""
    fp_rc=0; fp_url=$(env_setting 'Ticketing:BaseUrl') || fp_rc=$?
    if [ "$fp_rc" = 0 ]; then fp_url_set='set'
    elif [ "$fp_rc" = 2 ]; then fp_blocked='Ticketing:BaseUrl'
    elif secret_has 'Ticketing:BaseUrl'; then fp_url=$(secret_get 'Ticketing:BaseUrl'); fp_url_set='set'
    fi
    fp_base=""
    if [ -n "$fp_blocked" ]; then
        echo "    not reading the help desk's assignee list: $fp_blocked is set more than once, or with a line break" >/dev/tty
    else
        fp_base=$(vendor_endpoint "$fp_region" "$fp_url_set" "$fp_url")
    fi
    if [ -n "$fp_base" ] &&
        printf '%s' "$fp_key" | grep -qE '^[A-Za-z0-9._~-]+$'; then
        if fp_json=$(printf 'url = "%s/instance?key=%s&timezone=0"\n' "$fp_base" "$fp_key" | curl -fsS --max-time 20 -K - 2>/dev/null); then
            fp_people=$(printf '%s' "$fp_json" | people_with_email "$fp_want")
            # One person listed twice is still one match (install.ps1 counts the same way); two IDs are ambiguous.
            if [ -n "$fp_people" ] && [ "$(printf '%s\n' "$fp_people" | cut -f1 | tr '[:upper:]' '[:lower:]' | sort -u | wc -l | tr -d ' ')" = 1 ]; then
                printf '%s\n' "$fp_people" | head -n1 | cut -f1
                printf '%s\n' "$fp_people" | head -n1 | cut -f2
                echo "the help desk's assignee list"
                return
            fi
        else
            echo "    couldn't read the help desk's assignee list" >/dev/tty
        fi
    fi
    # Guests, or an account without directory read rights, get an error here; that just means no match.
    if have az && fp_me=$(az ad user show --id "$fp_email" --query '[id, displayName]' -o tsv 2>/dev/null | tr -d '\r') &&
        [ -n "$(printf '%s\n' "$fp_me" | sed -n 1p)" ]; then
        printf '%s\n' "$fp_me" | sed -n 1p
        json_escape "$(printf '%s\n' "$fp_me" | sed -n 2p)"; echo
        echo 'the directory (Azure CLI)'
    fi
}

set_secrets() {
    step "Configuring $SECRETS_PATH"
    # In a subshell: dash exits the whole shell when a redirect on a builtin fails.
    (: </dev/tty) 2>/dev/null || die "No terminal to prompt on. Run again with --skip-secrets and write $SECRETS_PATH yourself."
    if [ -f "$SECRETS_PATH" ] && ! secrets_file_editable; then
        die "$SECRETS_PATH isn't in the one-setting-per-line form this script can update safely. Edit it by hand (see docs/stdio.md), or run again with --skip-secrets."
    fi

    # All values below are held JSON-escaped.
    key=$(secret_get 'Ticketing:ApiKey')
    id=$(secret_get 'Ticketing:ServiceAccount:Id')
    name=$(secret_get 'Ticketing:ServiceAccount:Name')
    email=$(secret_get 'Ticketing:ServiceAccount:Email')

    # The signed-in Azure CLI account, when there is one: the email offered when the file has none, and the ID and name
    # used when its email is the one entered and the lookup finds nothing. Loaded on a reinstall too, since the file's
    # account may be swapped for this one, and `az ad user show` needs directory rights some lack.
    me_id=""; me_name=""; me_email=""
    if have az; then
        # One value per line. Strip CRs, which the Windows az prints when it is reached from WSL.
        if me=$(az ad signed-in-user show --query '[id, displayName, mail || userPrincipalName]' -o tsv 2>/dev/null | tr -d '\r'); then
            me_id=$(json_escape "$(printf '%s\n' "$me" | sed -n 1p)")
            me_name=$(json_escape "$(printf '%s\n' "$me" | sed -n 2p)")
            me_email=$(json_escape "$(printf '%s\n' "$me" | sed -n 3p)")
        fi
    fi

    if [ -n "$key" ]; then key_prompt='Ticketing API key (Enter keeps the current key): '
    else key_prompt='Ticketing API key (Ticketing app > Settings > API): '; fi
    while :; do
        printf '%s' "$key_prompt" >/dev/tty
        # If the prompt is interrupted, restore echo and stop with the usual status for the signal. A trap that only
        # restored echo would return into this loop, which would ask again instead of letting Ctrl+C end the script.
        trap 'stty echo </dev/tty; exit 130' INT
        trap 'stty echo </dev/tty; exit 143' TERM
        stty -echo </dev/tty
        IFS= read -r value </dev/tty || value=""
        stty echo </dev/tty
        trap - INT TERM
        printf '\n' >/dev/tty
        value=$(printf '%s' "$value" | tr -d '\r')
        if [ -n "$value" ]; then key=$(json_escape "$value"); break; fi
        if [ -n "$key" ]; then break; fi
    done

    # The email first, since it's the one detail people know: the ID and name are then looked up for it and shown
    # together, so a default from another account (say, the one signed in to the Azure CLI) is easy to spot. The
    # file's ID and name belong to its email; for any other, they come from the lookup, never the old file.
    echo 'Ticket changes are attributed to this account (use your own):' >/dev/tty
    stored_email=$email
    new_email=$(ask '  Email' "${stored_email:-$me_email}")
    lower() { printf '%s' "$1" | tr '[:upper:]' '[:lower:]'; }
    if [ -n "$stored_email" ] && [ -n "$id" ] && [ "$(lower "$stored_email")" = "$(lower "$new_email")" ]; then
        : # the same account: keep its ID and name as the defaults
    else
        found=$(find_person "$new_email" "$key")
        if [ -z "$found" ] && [ -n "$me_id" ] && [ "$(lower "$me_email")" = "$(lower "$new_email")" ]; then
            found=$(printf '%s\n%s\n%s' "$me_id" "$me_name" 'your Azure CLI sign-in')
        fi
        id=$(printf '%s\n' "$found" | sed -n 1p)
        name=$(printf '%s\n' "$found" | sed -n 2p)
        if [ -n "$id" ]; then
            printf '    found %s <%s> in %s\n' "$name" "$new_email" "$(printf '%s\n' "$found" | sed -n 3p)" >/dev/tty
        else
            printf "    %s isn't in the assignee list or the directory. Your Entra object ID is on your user page in the Entra admin center (Users > your name > Object ID), or run: az ad signed-in-user show --query id -o tsv\n" "$new_email" >/dev/tty
        fi
    fi
    email=$new_email
    name=$(ask '  Display name' "$name")
    id=$(ask '  Entra object ID' "$id")

    # Settings written below, so their old lines aren't kept too.
    written='ApiKey|ServiceAccount:Id|ServiceAccount:Name|ServiceAccount:Email'
    [ -z "$REGION" ] || written="$written|Region"

    mkdir -p "$(dirname "$SECRETS_PATH")"
    new="$SECRETS_PATH.new"
    (
        umask 077
        {
            echo '{'
            printf '  "Ticketing:ApiKey": "%s",\n' "$key"
            printf '  "Ticketing:ServiceAccount:Id": "%s",\n' "$id"
            printf '  "Ticketing:ServiceAccount:Name": "%s",\n' "$name"
            printf '  "Ticketing:ServiceAccount:Email": "%s"' "$email"
            # Validated against a fixed list above, so it needs no escaping.
            [ -z "$REGION" ] || printf ',\n  "Ticketing:Region": "%s"' "$REGION"
            # Keep any other settings already in the file, such as Ticketing:DefaultTimeZoneId.
            if [ -f "$SECRETS_PATH" ]; then
                secrets_text | grep -E '^[[:space:]]*"[^"]+"[[:space:]]*:' |
                    grep -viE "^[[:space:]]*\"Ticketing:($written)\"" |
                    sed 's/^[[:space:]]*//; s/[[:space:]]*,\{0,1\}[[:space:]]*$//' |
                    while IFS= read -r line; do printf ',\n  %s' "$line"; done
            fi
            printf '\n}\n'
        } >"$new"
        # A fresh copy, so it gets this umask rather than the original's mode (dotnet user-secrets may have made the
        # original readable by others).
        if [ -f "$SECRETS_PATH" ]; then rm -f "$SECRETS_PATH.bak"; cat "$SECRETS_PATH" >"$SECRETS_PATH.bak"; fi
    )
    chmod 600 "$new"
    mv -f "$new" "$SECRETS_PATH"
    echo '    saved (the API key is stored only in this file)'
}

# Sends an MCP initialize request over stdio and checks for a response, the same smoke test the release runs.
test_server() {
    step 'Checking that the server starts'
    dir=$(mktemp -d)
    mkfifo "$dir/in"
    "$EXE_PATH" --stdio <"$dir/in" >"$dir/out" 2>"$dir/err" &
    pid=$!
    exec 3>"$dir/in"
    printf '%s\n' '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"installer","version":"1"}}}' >&3
    started=0
    i=0
    while [ $i -lt 40 ]; do
        if grep -q '"serverInfo"' "$dir/out" 2>/dev/null; then started=1; break; fi
        kill -0 "$pid" 2>/dev/null || break
        sleep 0.5
        i=$((i + 1))
    done
    exec 3>&-
    kill "$pid" 2>/dev/null || true
    wait "$pid" 2>/dev/null || true
    if [ "$started" = 1 ]; then
        echo '    OK'
    else
        warn "The server did not start. Its output:"
        cat "$dir/err" >&2
        if grep -q 'libicu' "$dir/err"; then
            # Only releases up to v0.1.2-beta need ICU; later Linux builds run without it.
            warn "This release needs ICU. Install a newer release, or install ICU with your package manager, for example 'sudo apt install libicu-dev' (Debian/Ubuntu) or 'sudo dnf install libicu' (Fedora), then run the installer again."
        fi
    fi
    rm -rf "$dir"
    [ "$started" = 1 ]
}

resolve_clients

if [ "$UNINSTALL" = 1 ]; then
    step 'Unregistering'
    for c in $TARGETS; do
        if [ "$c" = vscode ]; then
            echo "    vscode: run 'MCP: Open User Configuration' and delete the '$SERVER_NAME' entry"
        elif [ -z "$(client_problem "$c")" ]; then
            unregister_client "$c"
            echo "    removed from $c"
        fi
    done
    # Only the installer's own files: --install-dir may be a folder shared with other programs.
    rm -f "$EXE_PATH" "$EXE_PATH.new" "$VERSION_PATH"
    rmdir "$INSTALL_DIR" 2>/dev/null || true
    echo "    removed the server from $INSTALL_DIR"
    if [ "$REMOVE_SECRETS" = 1 ]; then
        rm -f "$SECRETS_PATH" "$SECRETS_PATH.bak"
        echo "    deleted $SECRETS_PATH"
    fi
    exit 0
fi

install_binary
if [ "$SKIP_SECRETS" = 0 ]; then
    set_secrets
elif [ ! -f "$SECRETS_PATH" ]; then
    warn "No secrets file at $SECRETS_PATH; the server needs its settings in environment variables instead, set in each client's MCP config (see docs/stdio.md)."
fi
# Environment variables override the secrets file. Whether one set here reaches the server depends on the client:
# Claude Code passes its environment on, GitHub Copilot CLI and Codex pass only a few variables. The startup check
# below sees them either way, so it can pass where a client would not. Names only: the values may be secrets.
overrides=$(env | sed -n 's/^\([Tt][Ii][Cc][Kk][Ee][Tt][Ii][Nn][Gg]__[^=]*\)=.*/\1/p' | sort | tr '\n' ' ')
NOTICE=""
if [ -n "$overrides" ]; then
    NOTICE="These environment variables are set: ${overrides% }. They override the secrets file in clients that pass their environment to the server, such as Claude Code, but GitHub Copilot CLI and Codex don't pass them. Unset them if the secrets file should be used."
    warn "$NOTICE"
fi
started=1
test_server || started=0

if [ -n "$(echo "$TARGETS" | tr -d ' ')" ]; then
    step 'Registering with MCP clients'
    for c in $TARGETS; do register_client "$c" || FAILED="$FAILED $c"; done
fi

echo
# Repeated here, where it won't have scrolled out of sight.
if [ -n "$NOTICE" ]; then warn "$NOTICE"; fi
# A failing status, so scripted installs can tell. The executable and client registrations stay in place.
if [ "$started" = 0 ]; then
    die "Installed, but the server can't start yet. Fix the settings above (or run the installer again without --skip-secrets), then restart your MCP client."
fi
if [ -n "$FAILED" ]; then
    die "Installed, but registering with$FAILED failed (see above). Fix the problem, then run the installer again with --clients $(printf '%s' "${FAILED# }" | tr ' ' ',')."
fi
# File uploads aren't offered on macOS (the server can't verify the file it opens there).
if [ "$(uname -s)" = Darwin ]; then tools='20 tools'; else tools='20 tools, 21 with file uploads on'; fi
echo "Done. Restart your MCP client and look for '$SERVER_NAME' ($tools)."
echo 'Run the installer again to upgrade; client configurations do not need to change.'
