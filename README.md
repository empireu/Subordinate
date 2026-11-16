# Subordinate

Subordinate is a Windows and Linux CLI that lets a supervising agent (Claude Code) start and resume headless coding subagents. Every shell command the subagent proposes is classified deterministically and checked against a permission policy that is fixed when the session starts. Calls the policy does not cover are not executed. They become pending items with stable ids, and the supervisor approves or denies them by id on resume.

## Build

```
dotnet build Subordinate.slnx
dotnet test tests/Subordinate.Tests
```

Every build also publishes a self-contained copy and deploys it to `bin` under the Subordinate home. On Windows that is `%SUBORDINATE_HOME%\bin`, or `%USERPROFILE%\.subordinate\bin` when the variable is not set, and the program is `subordinate.exe`. On Linux that is `$SUBORDINATE_HOME/bin`, or `~/.subordinate/bin` when the variable is not set, and the program is `subordinate`. The copy needs no installed .NET runtime and does not break when the repo is cleaned. If a session is running from that copy, its files are locked. The deploy then logs a warning and keeps the old copy, and the next build retries.

## Configuration

`subordinate init` writes a template to `%SUBORDINATE_HOME%\config.json`, or `~/.subordinate/config.json` when `SUBORDINATE_HOME` is not set. Sessions live next to it under `sessions/`.

```json
{
  "providers": {
    "mistral": { "protocol": "mistral-chat-completions", "endpoint": "https://api.mistral.ai/v1", "api_key_env": "MISTRAL_API_KEY" }
  },
  "models": {
    "default": { "provider": "mistral", "model": "REPLACE-WITH-MODEL-ID", "temperature": 0.2 }
  },
  "default_model": "default",
  "shells": { "bash": null, "powershell": null, "pwsh": null },
  "defaults": { "shell": "bash", "max_turns": 100, "max_minutes": 60, "shell_timeout_seconds": 120, "keep_runs": 10 }
}
```

`protocol` defaults to `mistral-chat-completions`, the only protocol registered. Add more entries under `models` to have several aliases and pick one with `start --model ALIAS`. The API key is read from the environment variable named by `api_key_env` and is never written to disk or output. Every variable named as an `api_key_env`, and every variable whose name looks like a secret, is removed from the environment of the commands the subagent runs. `subordinate doctor` checks the configuration, the key and the shells.

### System prompt

The subagent's system prompt has three parts:

1. **Base prompt:** `system-prompt.md` in the same folder as `config.json`. `subordinate init` writes the built-in default there for you to edit. Without the file, the built-in default is used.
2. **Session block:** generated from the workdir, the shell and the policy.
3. **Supervisor instructions:** whatever is passed to `start` with `--system-prompt TEXT` or `--system-prompt-file F`. This section is left out when nothing is passed.

The composed prompt is saved as the first message of the session's transcript. Editing `system-prompt.md` later only affects new sessions.

Supported shells:

| Token | Shell | Platform | Requirement |
| --- | --- | --- | --- |
| `bash` | Git Bash (msys) on Windows, bash on Linux | Windows, Linux | 5.2 or 5.3. On Windows the WSL `bash.exe` in System32 is never used. |
| `powershell` | Windows PowerShell | Windows | 5.1 |
| `pwsh` | PowerShell 7 | Windows, Linux | 7.6.4 or later 7.6.x, the range the parser supports. Other versions fail closed. |

## Linux

Subordinate runs on Linux with the same CLI, output contract and policy. `bash` must be 5.2 or 5.3, and `pwsh` follows the same 7.6.4 to 7.6.x rule as on Windows. `powershell` is Windows only; use `--shell bash` or `--shell pwsh` on Linux.

Paths are case sensitive, and a path that contains a backslash is rejected by the classifier because bash treats a backslash as part of a file name while pwsh cmdlets treat it as a separator. Write POSIX paths with forward slashes.

On Linux the containment check also resolves symbolic links. A link inside the workdir that points outside it is treated as outside, so reading or writing through it needs `read-outside` or `write-outside`. The check resolves the longest existing prefix of each path. Links created by an earlier part of the same command are not visible at classification time. The classifier is a deterministic floor, not a sandbox.

## Output contract

Every command prints exactly one line of JSON on stdout first, then a human summary. The JSON is ASCII, with non-ASCII characters escaped, and its keys are in a fixed order:

```
ok, command, session, workdir, state, run_id, run_dir, result_file, text_file, pending_file, pending_count, resumable, error{code, exit_code, message}, then command-specific keys
```

Each run writes into `sessions/<session>/runs/<run_id>/`:

| File | Content |
| --- | --- |
| `status.json` | Live progress: state, pid, elapsed time, run counters, session totals, turns remaining, last activity. |
| `result.json` | Final state of the run, run counters, session totals, limit details, pending count, files written, final answer flag. |
| `result.md` | The subagent's final answer, or why the run stopped. |
| `pending.json` / `pending.md` | Open pending items. Both carry the same ids. |
| `log.md` | A readable log of tool calls and their first output lines. |

The run counters are under `run`; the session totals are under `session_totals`. Both objects use the same counter names.

`usage.jsonl` in the Subordinate home is append-only. Every finished run adds one JSON line with its model, state, duration and counters. It is not deleted with the session, so `usage` can still report sessions that are gone.

## Exit codes

Exit code 0 means the command did what was asked, whatever state the session is in. `finished`, `awaiting_approval` and `limit_reached` all exit 0; read `state` in the JSON.

| Code | Token | Meaning |
| --- | --- | --- |
| 0 | | Success. |
| 1 | `internal_error` | Unexpected failure. |
| 2 | `invalid_arguments` | Bad command line. |
| 3 | `unknown_session` | No session with that name. |
| 4 | `session_exists` | `start` with a name already used for this workdir. |
| 5 | `session_busy` | The session is running in another process. |
| 6 | `bad_policy` | Invalid policy tokens. |
| 7 | `workdir_missing` | The workdir does not exist. |
| 8 | `config_error` | Missing or invalid configuration. |
| 9 | `bad_model` | Unknown model alias, missing API key, or the alias now points to another model. |
| 10 | `bad_approval` | Unknown pending id, already decided, or decided twice. |
| 11 | `state_conflict` | The pending list, the transcript and the interrupted batch disagree. The resume is refused. |
| 12 | `shell_unavailable` | The shell is missing, unsupported, or changed since the session started. |
| 13 | `prompt_error` | The prompt is missing, empty or unreadable. |
| 14 | `ambiguous_session` | The name exists in several workdirs. Pass `--workdir`. |
| 15 | `nothing_to_resume` | `resume` with no decisions and no message on a finished or waiting session. |
| 20 | `run_failed` | A `--wait` run ended in the `failed` state. |

## Commands

```
start    --name N --policy P [--policy P2] (--prompt TEXT | --prompt-file F | --prompt-file -)
         [--system-prompt TEXT | --system-prompt-file F] [--workdir D] [--shell bash|powershell|pwsh] [--model ALIAS]
         [--max-turns N] [--max-minutes M] [--shell-timeout S] [--wait]
resume   --name N [--workdir D] [--approve ID,ID] [--approve-session ID] [--deny ID[:reason]]
         [--message TEXT | --message-file F] [--max-turns N] [--max-minutes M] [--wait]
status   --name N [--workdir D]
wait     --name N[,N...] [--workdir D] [--timeout-seconds S]
result   --name N [--workdir D] [--output FILE]
close    --name N [--workdir D]
stop     --name N [--workdir D]
list     [--workdir D]
delete   --name N [--workdir D]
prune    [--older-than-days N] [--dry-run]
usage    [--since TIME] [--session N] [--model ALIAS] [--workdir D]
classify --command TEXT [--shell bash] [--workdir D] [--policy P]
doctor   [--model ALIAS]
init
```

A session is identified by the pair (workdir, name). `--workdir` defaults to the current directory. Commands other than `start` also find a session by name alone when the name is unique.

`start` and `resume` return as soon as a detached worker process has taken the session. Follow the run with `status` or block on it with `wait`. Pass `--wait` to run in the foreground instead.

When `wait` or a `--wait` run ends, the summary includes the subagent's final answer. When the session is waiting for approval, it includes the subagent's last message instead. Text past 20000 characters is cut, with a pointer to `result.md` for the rest.

`wait --name a,b,c` waits for several sessions at once. Its JSON has a `sessions` array with one entry per name, and the top-level `state` is the common state when they agree or `mixed` when they do not. The summary includes each session's final answer or last message.

`result --output FILE` writes the final answer to `FILE` without printing it twice. This is the easiest way to collect answers from a swarm of sessions.

`close` drops every pending item without running the model and marks the session `finished`. Use it when the answer is already good enough and the blocked calls no longer matter. It does not invent a final answer. When the subagent already gave one, `result.md` keeps it and `final_answer` stays true. Otherwise `final_answer` is false and `result.md` records that the session was closed. `stop` is different: it asks a running session to stop and keeps it resumable.

`usage` reads `usage.jsonl` and reports totals plus a breakdown by model alias and workdir. `--since` accepts an ISO 8601 time such as `2026-11-01` or `2026-11-01T12:00:00Z`, or a relative duration such as `30m`, `24h`, `7d` or `2w`. `--session`, `--model` and `--workdir` filter the log. The log survives `delete` and `prune`.

`doctor` reports every configured model alias, its model id, provider, reasoning effort and whether its API key is available. Pass `--model ALIAS` to check one alias.

`resume` without decisions continues an interrupted or limit-stopped run. Approving a call runs it exactly once, through the same gate, and gives its real output to the subagent. Denials are sticky: if the subagent retries the same call, it is refused without asking again.

## Policy

The policy is a set of tokens given to `start`. Separate tokens with commas, or repeat `--policy`. It cannot be changed on resume; widen access one call at a time by approving pending ids.

| Token | Grants |
| --- | --- |
| `readonly` | `read` |
| `build` | `read`, `exec` |
| `edit` | `read`, `write`, `exec` |
| `read` | Reading inside the workdir. |
| `read-outside` | Reading outside the workdir. |
| `write` | Creating, changing and deleting files inside the workdir. |
| `write-outside` | Writing outside the workdir. |
| `exec` | Running project code, build tools, tests and interpreters. |
| `network` | Network access. |
| `cmd:<command words>` | Everything except the once-only facts below, for one command key such as `cmd:npm run lint`. |

Classification produces these facts, and a call runs only when the policy covers all of them:

- `read`, `read-outside`, `write`, `write-outside`, `exec`, `network`: grantable by policy.
- `unclassified`: the command is not in the command database. Only a `cmd:` token or a session approval covers it.
- `unbounded`: the classifier cannot bound what the call does. Examples are loops, variables, `eval`, background jobs, unresolvable paths, and options before a subcommand.
- `sensitive`: the command mentions a credential store, such as `~/.ssh`, `.env`, `.npmrc`, `~/.claude.json`, cloud credentials or key files.
- `dangerous`: privilege changes, process control, destructive git operations, piping downloads into a shell, and similar.

`unbounded`, `sensitive` and `dangerous` are once-only: they can never be granted for the session. Approvals involving paths outside the workdir are also proposed for one call only. Every pending item carries `needs_human: true` when it involves `write-outside`, `network`, `sensitive` or `dangerous`. A supervising agent should escalate those to the user instead of approving them itself.

`find -exec` is classified through the command it runs, so it is bounded when that command is bounded. `xargs` takes its file arguments from standard input, so a nested command that reads or writes files needs `read-outside` or `write-outside`; `xargs echo` stays a plain read.

Use `classify` to see how a command would be judged without running anything:

```
subordinate classify --command "rm -rf /tmp/x" --policy edit
```

## Worked example

This is an illustrative session. The shapes and messages match the code, and the ids are real derivations from the call ids shown.

Start a session that may only read:

```
> subordinate start --name cleanup --policy readonly --prompt "Delete build.log, then report the git status."
{"ok":true,"command":"start","session":"cleanup","workdir":"D:\\repos\\app","state":"running","run_id":"0001-20261112T101500Z",...}
Session "cleanup" is running in the background (pid 18324).
Follow it with: subordinate status --name "cleanup"
Block until it stops with: subordinate wait --name "cleanup"
```

Wait for it. The subagent ran `git status`, which the policy covers. It also asked for `rm build.log`, which needs `write`, so that call was deferred:

```
> subordinate wait --name cleanup
{"ok":true,"command":"wait","session":"cleanup",...,"state":"awaiting_approval",...,"pending_file":"...\\pending.json","pending_count":1,"resumable":true,"error":null}
Session "cleanup": awaiting_approval. 1 call(s) need a decision:
  9933b50559  shell      rm build.log  [missing: write; scope: session]
Decide with: subordinate resume --name "cleanup" --approve <id> --deny <id>:<reason>
```

`pending.json` lists the item:

```json
{"schema":1,"session":"cleanup","workdir":"D:\\repos\\app","count":1,"items":[
  {"id":"9933b50559","status":"pending","tool":"shell","summary":"rm build.log","command":"rm build.log","shell":"bash",
   "working_directory":"D:\\repos\\app","path":null,"capabilities":["read","write"],"missing":["write"],
   "reasons":["rm modifies files","the policy does not grant write"],"proposed_scope":"session","needs_human":false,
   "call_id":"call_2","run_id":"0001-20261112T101500Z","created_at":"2026-11-12T10:15:07Z"}]}
```

The subagent was told `PENDING APPROVAL 9933b50559: this call was not executed. ...` as the result of that call. It continued with the rest of the task, then ended its turn.

Approve it:

```
> subordinate resume --name cleanup --approve 9933b50559 --wait
{"ok":true,"command":"resume","session":"cleanup",...,"state":"finished",...,"pending_count":0,...}
Session "cleanup": finished. The final answer follows and is also in result.md.
Run directory: ...\runs\0002-20261112T101900Z

Final answer:
Deleted build.log. git status shows a clean working tree.
```

On resume, Subordinate ran `rm build.log` once, through the gate, and sent the subagent a `[Supervisor update]` message with the real output. The subagent then finished. `--approve-session 9933b50559` would also have allowed later `rm` calls in this session. `--deny 9933b50559:keep the log` would have told the subagent `DENIED 9933b50559 ... Reason: keep the log.` and refused any identical retry.

## Persistence and crash safety

- `session.json` is written atomically. `transcript.jsonl` is append-only and committed by a count and byte length stored in `session.json`, so a torn write is discarded.
- While a batch of tool calls runs, the state of each call is saved before it executes: not started, started, succeeded, failed or deferred. If the worker dies mid-batch, `resume` restores the batch. Finished calls keep their results, calls that never started run now, and a call that had started is reported as interrupted instead of being run twice. The model is not asked again until the batch is complete.
- A lock file held with exclusive access prevents two processes from running the same session (`session_busy`).
- Resume is refused (`state_conflict`) when the pending list, the transcript and the interrupted batch disagree.

## Testing through WSL

The Linux tests run in a dedicated WSL distro that is created for this work. Nothing in the build script, the projects or the test suite starts WSL. The scripts are:

- `pwsh -NoProfile -File scripts/wsl/Install-TestDistro.ps1` creates or repairs the distro. It takes several minutes and only needs to run once.
- `pwsh -NoProfile -File scripts/wsl/Invoke-LinuxTests.ps1` syncs the source, builds, runs the tests and stops the distro. Add `-Smoke` to run the smoke checks and `-Live` to include one live model run.
- `pwsh -NoProfile -File scripts/wsl/Stop-TestDistro.ps1` stops the distro and gives the memory back.

The distro is stopped after every run because WSL 2 uses a lot of memory. Removing it is the owner's decision: `wsl --unregister subordinate-linux`. Do not run that command unless you created the distro yourself.

`scripts/test-linux.sh` and `scripts/smoke.sh` also run on any Linux machine. See `LINUX_TESTING.md`.
