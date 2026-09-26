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
UNINSTALL=0
REMOVE_SECRETS=0

usage() {
    cat <<EOF
Usage: install.sh [options]

  --version <tag>       Release to install, for example v0.2.0, or 'latest' (default: the release this copy was
                        attached to, or for the copy on main, the newest release, pre-releases included)
  --clients <list>      Comma-separated: claude, codex, copilot, vscode, all, or none (default: those on PATH)
  --install-dir <dir>   Where to put the executable (default: $INSTALL_DIR)
  --skip-secrets        Don't prompt for the API key and account; keep what the secrets file already has
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
        --uninstall) UNINSTALL=1; shift ;;
        --remove-secrets) REMOVE_SECRETS=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
done

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

register_client() {
    problem=$(client_problem "$1")
    if [ -n "$problem" ]; then warn "$1: $problem; skipped."; return; fi
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
        warn "$1: registration failed (output above)."
    fi
    rm -f "$log"
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

# The secrets file can only be updated safely without a JSON parser when it is a flat object with one
# "key": "string" pair per line, which is how dotnet user-secrets and this script write it.
secrets_file_editable() {
    ! grep -vqE '^[[:space:]]*([{}]|\{[[:space:]]*\}|"[^"]+"[[:space:]]*:[[:space:]]*"([^"\\]|\\.)*"[[:space:]]*,?)?[[:space:]]*$' "$SECRETS_PATH"
}

# Prints a value from the secrets file still JSON-escaped, so an unchanged value is written back exactly as it was
# (dotnet user-secrets writes non-ASCII characters as \uXXXX escapes).
# Keys match case-insensitively, as .NET configuration keys do.
secret_get() {
    [ -f "$SECRETS_PATH" ] || return 0
    grep -iE "^[[:space:]]*\"$1\"[[:space:]]*:" "$SECRETS_PATH" | head -n1 |
        sed -n 's/^[[:space:]]*"[^"]*"[[:space:]]*:[[:space:]]*"\(.*\)"[[:space:]]*,\{0,1\}[[:space:]]*$/\1/p'
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

    # Offer the signed-in Azure CLI account as the default identity, when there is one.
    if [ -z "$id" ] && have az; then
        # One value per line. Strip CRs, which the Windows az prints when it is reached from WSL.
        if me=$(az ad signed-in-user show --query '[id, displayName, mail || userPrincipalName]' -o tsv 2>/dev/null | tr -d '\r'); then
            id=$(json_escape "$(printf '%s\n' "$me" | sed -n 1p)")
            [ -n "$name" ] || name=$(json_escape "$(printf '%s\n' "$me" | sed -n 2p)")
            [ -n "$email" ] || email=$(json_escape "$(printf '%s\n' "$me" | sed -n 3p)")
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

    echo 'Ticket changes are attributed to this account (use your own):' >/dev/tty
    id=$(ask '  Entra object ID' "$id")
    name=$(ask '  Display name' "$name")
    email=$(ask '  Email' "$email")

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
            # Keep any other settings already in the file, such as Ticketing:DefaultTimeZoneId.
            if [ -f "$SECRETS_PATH" ]; then
                grep -E '^[[:space:]]*"[^"]+"[[:space:]]*:' "$SECRETS_PATH" |
                    grep -viE '^[[:space:]]*"Ticketing:(ApiKey|ServiceAccount:Id|ServiceAccount:Name|ServiceAccount:Email)"' |
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
    for c in $TARGETS; do register_client "$c"; done
fi

echo
# Repeated here, where it won't have scrolled out of sight.
if [ -n "$NOTICE" ]; then warn "$NOTICE"; fi
if [ "$started" = 0 ]; then
    echo "Installed, but the server can't start yet. Fix the settings above (or run the installer again without"
    echo '--skip-secrets), then restart your MCP client.'
    exit 0
fi
echo "Done. Restart your MCP client and look for '$SERVER_NAME' (12 tools)."
echo 'Run the installer again to upgrade; client configurations do not need to change.'
