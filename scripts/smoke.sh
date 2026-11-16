#!/usr/bin/env bash
set -u

binary=""
live_config=""

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
        --live-config)
            if [ "$#" -lt 2 ]; then
                printf 'Missing value for --live-config.\n' >&2
                exit 2
            fi
            live_config="$2"
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
worker_pid=""
live_root=""

cleanup() {
    if [ -n "$listener_pid" ]; then
        kill "$listener_pid" 2>/dev/null || true
    fi
    if [ -n "$worker_pid" ]; then
        kill "$worker_pid" 2>/dev/null || true
    fi
    if [ -n "$listener_output" ]; then
        rm -f "$listener_output"
    fi
    if [ -n "$live_root" ]; then
        rm -rf "$live_root"
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

if [ "$is_linux" -eq 0 ]; then
    printf 'SMOKE SKIP detach: runs on Linux only.\n'
elif [ -z "$hang_port" ]; then
    printf 'SMOKE SKIP detach: python3 is not available or the listener did not start.\n'
else
    started_at=$SECONDS
    output=$(timeout 30 bash -c '"$@" | cat' _ "$binary" start --name smoke-hang --policy readonly --prompt "Say hi." --workdir "$workdir" --model hang 2>&1)
    status=$?
    elapsed=$((SECONDS - started_at))

    if [ "$status" -ne 0 ]; then
        fail detach "start pipeline exit $status"
    elif [ "$elapsed" -ge 15 ]; then
        fail detach "start pipeline took $elapsed seconds"
    else
        pid=$(printf '%s' "$output" | grep -o '"pid":[0-9]*' | head -n 1 | grep -o '[0-9]*')
        worker_pid="$pid"

        if [ -z "$pid" ] || ! kill -0 "$pid" 2>/dev/null; then
            fail detach "worker pid $pid is not alive"
        else
            sid=$(ps -o sid= -p "$pid" | tr -d ' ')
            tty=$(ps -o tty= -p "$pid" | tr -d ' ')

            if [ "$sid" != "$pid" ] || [ "$tty" != "?" ]; then
                fail detach "sid=$sid tty=$tty"
            else
                output=$("$binary" resume --name smoke-hang --workdir "$workdir" --message x 2>&1)
                status=$?

                if [ "$status" -ne 5 ]; then
                    fail detach "resume exit $status"
                else
                    output=$("$binary" stop --name smoke-hang --workdir "$workdir" 2>&1)
                    status=$?

                    if [ "$status" -ne 0 ]; then
                        fail detach "stop exit $status"
                    else
                        output=$("$binary" wait --name smoke-hang --workdir "$workdir" --timeout-seconds 60 2>&1)
                        status=$?

                        if [ "$status" -eq 0 ] && printf '%s' "$output" | grep -q '"state":"stopped"'; then
                            worker_pid=""
                            pass detach
                        else
                            fail detach "wait exit $status"
                        fi
                    fi
                fi
            fi
        fi
    fi
fi

if [ -z "$live_config" ]; then
    printf 'SMOKE SKIP live: no --live-config was given.\n'
elif [ "$is_linux" -eq 0 ]; then
    printf 'SMOKE SKIP live: runs on Linux only.\n'
else
    live_key=""

    if command -v python3 >/dev/null 2>&1; then
        live_key=$(python3 -c 'import json, sys; config = json.load(open(sys.argv[1])); model = config["models"][config["default_model"]]; print(config["providers"][model["provider"]].get("api_key_env", ""))' "$live_config" 2>/dev/null)
    fi

    if ! command -v python3 >/dev/null 2>&1; then
        printf 'SMOKE SKIP live: python3 is not available to read the live config.\n'
    elif [ -z "$live_key" ]; then
        printf 'SMOKE SKIP live: the live config does not name an API key variable.\n'
    elif [ -z "$(printenv "$live_key" 2>/dev/null)" ]; then
        printf 'SMOKE SKIP live: the API key variable is not set.\n'
    else
        live_root=$(mktemp -d)
        live_home="$live_root/home"
        live_work="$live_root/work"
        mkdir -p "$live_home" "$live_work"

        python3 -c 'import json, sys; config = json.load(open(sys.argv[1])); config["shells"] = {}; json.dump(config, open(sys.argv[2], "w"))' "$live_config" "$live_home/config.json" 2>/dev/null

        if [ ! -f "$live_home/config.json" ]; then
            fail live "the configuration could not be copied"
        else
            printf 'hello\n' > "$live_work/a.txt"
            export SUBORDINATE_HOME="$live_home"

            output=$("$binary" start --name smoke-live --policy readonly --prompt "Use the shell tool to run ls, then report the file names you saw." --wait --max-turns 6 --workdir "$live_work" 2>&1)
            status=$?

            if [ "$status" -ne 0 ] || ! printf '%s' "$output" | grep -q '"state":"finished"' || ! printf '%s' "$output" | grep -q 'a.txt'; then
                fail live "readonly run did not finish with a.txt"
            else
                output=$("$binary" start --name smoke-approve --policy readonly --prompt "Create a file named hello.txt that contains the word hi. Then report what you did." --wait --max-turns 6 --workdir "$live_work" 2>&1)
                status=$?
                rounds=0

                while [ "$status" -eq 0 ] && printf '%s' "$output" | grep -q '"state":"awaiting_approval"' && [ "$rounds" -lt 3 ]; do
                    run_dir=$(printf '%s' "$output" | grep -o '"run_dir":"[^"]*"' | head -n 1 | sed 's/^"run_dir":"//; s/"$//')
                    pending_id=$(grep -o '"id"[[:space:]]*:[[:space:]]*"[0-9a-f]\{10\}"' "$run_dir/pending.json" 2>/dev/null | head -n 1 | grep -o '[0-9a-f]\{10\}')

                    if [ -z "$pending_id" ]; then
                        break
                    fi

                    output=$("$binary" resume --name smoke-approve --approve "$pending_id" --wait --max-turns 6 --workdir "$live_work" 2>&1)
                    status=$?
                    rounds=$((rounds + 1))
                done

                if [ -f "$live_work/hello.txt" ]; then
                    pass live
                else
                    run_dir=$(printf '%s' "$output" | grep -o '"run_dir":"[^"]*"' | head -n 1 | sed 's/^"run_dir":"//; s/"$//')
                    last_state=$(printf '%s' "$output" | grep -o '"state":"[^"]*"' | head -n 1 | sed 's/^"state":"//; s/"$//')
                    detail="last state: ${last_state:-unknown}"

                    if [ -n "$run_dir" ] && [ -f "$run_dir/result.md" ]; then
                        detail="$detail; result: $(head -c 300 "$run_dir/result.md" | tr '\n' ' ')"
                    fi

                    if [ -n "$run_dir" ] && [ -f "$run_dir/pending.json" ]; then
                        detail="$detail; pending: $(head -c 300 "$run_dir/pending.json" | tr '\n' ' ')"
                    else
                        detail="$detail; pending.json missing"
                    fi

                    fail live "hello.txt was not created; $detail"
                fi
            fi
        fi
    fi
fi

if [ "$failures" -ne 0 ]; then
    exit 1
fi

exit 0
