# Testing Subordinate on Linux

This guide is for Linux testers. It covers the prerequisites, building and deploying, the automated checks, and trying a real session. Windows is not covered here.

## Prerequisites

- Linux on x64 or arm64. Other architectures are not tested.
- bash 5.2 or 5.3. Check with `bash --version`. Other versions fail closed.
- The .NET 11 SDK version 11.0.100-preview.3.26207.106. Install that SDK and put `dotnet` on PATH.
- Optional: PowerShell 7.6.4 or later, below 7.7, for the `pwsh` shell. Set `SUBORDINATE_TEST_PWSH` to its path if it is not on PATH.

## Build and deploy

From the repository root:

```
dotnet build Subordinate.slnx
dotnet test tests/Subordinate.Tests
```

The build publishes a self-contained copy to `$SUBORDINATE_HOME/bin`, or `~/.subordinate/bin` when `SUBORDINATE_HOME` is not set. The program is `~/.subordinate/bin/subordinate`. It needs no installed .NET runtime. If a session is running from that copy, the deploy logs a warning and keeps the old copy. Stop the session and build again.

The test script runs the same build and tests in one step:

```
bash scripts/test-linux.sh
```

It uses `~/.dotnet/dotnet` when it exists and `~/.local/pwsh/pwsh` when it exists, and prints the kernel, bash and dotnet versions. It exits with the test exit code.

## Smoke checks

The smoke script uses a temporary home, so it never touches your real configuration:

```
bash scripts/smoke.sh --binary ~/.subordinate/bin/subordinate
```

It prints one `SMOKE PASS`, `SMOKE FAIL` or `SMOKE SKIP` line per check and exits 1 if anything failed. The live check is skipped unless you pass `--live-config <path>` and the API key variable named in that config is set.

## Configure a model

For the rest of this guide, put the deploy directory on PATH for the current shell only:

```
export PATH="$HOME/.subordinate/bin:$PATH"
```

Then create the configuration:

```
subordinate init
```

This writes `~/.subordinate/config.json`, or `$SUBORDINATE_HOME/config.json` when the variable is set. Edit the provider endpoint, the model id and the `api_key_env` variable name. Export the key in your shell. Never put the key in the file.

Check the configuration, the key and the shells:

```
subordinate doctor
```

`doctor` prints one JSON line and a human summary. It reports every model alias and whether its key is available.

## Try a real session

Start with a read-only session in a scratch directory:

```
mkdir -p ~/subordinate-test
cd ~/subordinate-test
subordinate start --name first --policy readonly --prompt "List the files in this directory and report what you see." --wait
```

The command prints one JSON line and a summary. A finished session exits 0. When the session is waiting for approval, read the id from `pending.json` and run:

```
subordinate resume --name first --approve <id> --wait
```

To try the supervisor flow with Claude Code, give it the commands from the README. Start a session, wait for `awaiting_approval`, approve or deny the pending ids, and collect the final answer with:

```
subordinate result --name first
```

## What to send back

If something misbehaves, send:

- the output of `uname -a`
- the first line of `bash --version`
- the full output of `subordinate doctor`
- the full output of `bash scripts/smoke.sh --binary ~/.subordinate/bin/subordinate`
- the exact command lines that failed, with the JSON line and the human summary
- whether the session was started by Claude Code or by hand

Do not send API keys, configuration files with keys, or transcripts that contain secrets.
