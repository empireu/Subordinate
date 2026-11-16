#!/usr/bin/env bash
set -u

binary=""

while [ "$#" -gt 0 ]; do
    case "$1" in
        --binary)
            if [ "$#" -lt 2 ]; then
                printf 'Missing value for --binary.\n' >&2
                exit 2
            fi
            binary="$2"
            shift 2
            ;;
        *)
            printf 'Unknown argument: %s\n' "$1" >&2
            exit 2
            ;;
    esac
done

if [ -z "$binary" ]; then
    printf 'Usage: %s --binary <path>\n' "$0" >&2
    exit 2
fi

failures=0

pass() {
    printf 'SMOKE PASS %s\n' "$1"
}

fail() {
    failures=$((failures + 1))
    printf 'SMOKE FAIL %s: %s\n' "$1" "$2"
}

is_linux=0
if [ "$(uname -s)" = "Linux" ]; then
    is_linux=1
fi

temp_directory=$(mktemp -d)

if [ -z "$temp_directory" ]; then
    printf 'Could not create a temporary directory.\n' >&2
    exit 2
fi

home_directory="$temp_directory/home"
work_directory="$temp_directory/work"
mkdir -p "$home_directory" "$work_directory"

listener_pid=""
listener_output=""

cleanup() {
    if [ -n "$listener_pid" ]; then
        kill "$listener_pid" 2>/dev/null || true
    fi
    if [ -n "$listener_output" ]; then
        rm -f "$listener_output"
    fi
    rm -rf "$temp_directory"
}
trap cleanup EXIT

if [ "$is_linux" -eq 1 ]; then
    export SUBORDINATE_HOME="$home_directory"
    workdir="$work_directory"
else
    export MSYS2_ARG_CONV_EXCL='*'
    converted_home=$(cygpath -w "$home_directory")
    converted_work=$(cygpath -w "$work_directory")
    export SUBORDINATE_HOME="$converted_home"
    workdir="$converted_work"
fi

output=$("$binary" help 2>&1)
status=$?
first_line=$(printf '%s\n' "$output" | head -n 1)

if [ "$status" -eq 0 ]; then
    case "$first_line" in
        '{"ok":true'*)
            pass help
            ;;
        *)
            fail help "first line: $first_line"
            ;;
    esac
else
    fail help "exit $status"
fi

output=$("$binary" init 2>&1)
status=$?

if [ "$status" -eq 0 ] && [ -f "$home_directory/config.json" ]; then
    pass init
else
    fail init "exit $status"
fi

hang_port=""

if [ "$is_linux" -eq 1 ] && command -v python3 >/dev/null 2>&1; then
    listener_output=$(mktemp)
    python3 -c 'import socket, time; listener = socket.socket(); listener.bind(("127.0.0.1", 0)); listener.listen(16); print(listener.getsockname()[1], flush=True); time.sleep(900)' > "$listener_output" 2>/dev/null &
    listener_pid=$!

    attempts=0
    while [ "$attempts" -lt 50 ]; do
        if [ -s "$listener_output" ]; then
            break
        fi
        sleep 0.1
        attempts=$((attempts + 1))
    done

    if [ -s "$listener_output" ]; then
        hang_port=$(head -n 1 "$listener_output")
    fi
fi

if [ -n "$hang_port" ]; then
    cat > "$home_directory/config.json" <<EOF
{
  "providers": {
    "dead": { "endpoint": "http://127.0.0.1:9/v1", "api_key": "smoke" },
    "hang": { "endpoint": "http://127.0.0.1:$hang_port/v1", "api_key": "smoke", "network_timeout_seconds": 120 }
  },
  "models": {
    "dead": { "provider": "dead", "model": "smoke-model" },
    "hang": { "provider": "hang", "model": "smoke-model" }
  },
  "default_model": "dead"
}
EOF
else
    cat > "$home_directory/config.json" <<EOF
{
  "providers": {
    "dead": { "endpoint": "http://127.0.0.1:9/v1", "api_key": "smoke" }
  },
  "models": {
    "dead": { "provider": "dead", "model": "smoke-model" }
  },
  "default_model": "dead"
}
EOF
fi

if [ -f "$home_directory/config.json" ]; then
    pass config
else
    fail config "config.json was not written"
fi

output=$("$binary" doctor 2>&1)
status=$?

if [ "$status" -eq 0 ] && printf '%s\n' "$output" | grep -Eq '^ok +shell:bash:'; then
    pass doctor
else
    fail doctor "exit $status"
fi

output=$("$binary" classify --command "ls" --policy readonly 2>&1)
status=$?

if [ "$status" -eq 0 ] && printf '%s' "$output" | grep -q '"verdict":"allow"'; then
    output=$("$binary" classify --command "rm -rf /tmp/smoke-x" --policy edit 2>&1)
    status=$?

    if [ "$status" -eq 0 ] && printf '%s' "$output" | grep -q '"verdict":"defer"' && printf '%s' "$output" | grep -Eq '"missing":\[[^]]*"write-outside"'; then
        pass classify
    else
        fail classify "edit rm: exit $status"
    fi
else
    fail classify "readonly ls: exit $status"
fi

output=$("$binary" start --name smoke-dead --policy readonly --prompt "Say hi." --workdir "$workdir" --model dead 2>&1)
status=$?

if [ "$status" -ne 0 ]; then
    fail failed-run "start exit $status"
else
    output=$("$binary" wait --name smoke-dead --workdir "$workdir" --timeout-seconds 60 2>&1)
    status=$?

    if [ "$status" -eq 20 ] && printf '%s' "$output" | grep -q '"state":"failed"'; then
        pass failed-run
    else
        fail failed-run "wait exit $status"
    fi
fi

if [ "$failures" -ne 0 ]; then
    exit 1
fi

exit 0
