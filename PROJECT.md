# Subordinate

A handoff prompt for Claude Code. Read this whole document before writing anything.

## Mission

Build `Subordinate`, a command line tool that starts and resumes headless AI coding subagents. Claude Code is the primary driver: it starts a subagent with a task and a permission policy, gets control back immediately, and later receives a structured request for approval when the subagent wants to do something the policy does not cover. Claude approves or denies, and the subagent continues where it stopped.

The tool is for delegating work that does not need Claude's full attention: code review, second opinions, mechanical refactors, searching a large codebase. It is not a general purpose agent framework.

The agent loop already exists. `D:\repos\KnowledgeSystem` contains a C# agent harness that has been in use for months. We are copying the parts we need into this repository and adapting them. Do not write a new agent loop.

## Why this exists

The current approach drives `pi` through a PowerShell wrapper at `C:\Users\alioth\.claude\skills\pi\`. It works, but the friction is severe and it is the reason for this project. These are observed failures from real sessions, not hypotheticals.

### Result transport

On 2026-11-12, reading one finished review out of the wrapper's output took four failed attempts:

- `json.load` failed with `Extra data: line 34 column 1`. Claude Code appends `[exited with code 0]` to background task output files, so the wrapper's JSON is never the whole file. Claude had to fall back to `JSONDecoder().raw_decode`.
- Printing the `text` field crashed twice with `UnicodeEncodeError: 'charmap' codec can't encode '\u2248'`. The review contained non-ASCII characters such as the almost-equal sign and em dashes, and the console is cp1252. Needed `PYTHONIOENCODING=utf-8` plus `sys.stdout.reconfigure`.
- The wrapper serializes a plain PowerShell hashtable, so key order is unstable. `text`, the payload, landed mid-object. Any `head -c` truncation loses it.
- Output is CRLF framed and `text` is one giant escaped string, so it is unreadable in a tool result.

**Requirement:** the machine readable result is a file at a known path, UTF-8, with a stable shape. Stdout carries one line of JSON and nothing Claude has to parse out of prose.

### Session identity

The wrapper keeps its own session registry at `~/.claude/pi-runs/sessions/`, separate from pi's own store at `~/.pi/agent/sessions/`. Claude passed the id `4ab69456-53da-219f-95c3-faf4db2e98b0`, which existed in pi's store, and got `Unknown session`. pi itself would have accepted it. A background run was wasted and retried.

There is also no way to list sessions, so Claude carries a raw GUID in context across a long conversation. On 2026-11-11 it kept `0ffc691e-8aad-4add-9630-36503d8b81b4` alive across a long series of consecutive reviews. Context compaction would have lost it.

**Requirement:** sessions are named, resolvable from the command line, and listable. One source of truth.

### Progress and limits

There is no way to ask a running subagent how far along it is. On 2026-11-12 Claude armed a `ScheduleWakeup` with a 1200 second delay purely as a fallback in case the completion notification never arrived, then sat idle.

Limits are guessed per call. Defaults are 540 seconds and 40 turns. That review took 7.5 minutes and 23 turns, right against the deadline. An earlier session had to raise to 900 seconds and 60 turns for one step. `run_in_background` does not extend the deadline; the wrapper kills at the limit either way.

**Requirement:** progress is observable, and a run that stops has a distinct, resumable state rather than looking like an error.

### Wrapper overhead

The wrapper re-reads and re-parses the entire `events.jsonl` at the end of every call. That file was 12 MB and 33,025 lines for the review above, of which 32,777 were streaming deltas. It used about 160 of them. The parse is on the critical path, after the agent has already finished.

**Requirement:** do not re-parse history to produce a result.

### Permissions

There is no shell profile at all, because pi has no per-command allowlist and enabling `bash` would allow anything. So the subagent cannot build, run tests, or `git diff`. Claude verifies everything itself. During the review, pi tried twice to read `~/.gradle` and was blocked by the guardrails extension with "no UI to confirm", which is a dead end: the only recourse is starting a new session with a different profile, losing all context.

**Requirement:** shell access with per-command decisions, and a way to answer a blocked request without losing the session.

### Smaller things

- A 51 KB prompt had to be written to a scratchpad file and passed by path. `--prompt` on a command line does not survive real prompts.
- `files_written` returned relative paths even though the workdir was absolute.
- One error code covers unknown session, bad model alias, missing workdir and busy session, so the driver cannot branch without reading the message.
- Run directories accumulate with no pruning. Fifteen of them, about 40 MB.

## Decisions already made

These were settled in design discussion. Do not relitigate them; if you believe one is wrong, implement the rest and raise it in your report.

1. **Own agent loop, not pi.** We are not wrapping pi and not using its RPC mode. The RPC surface is mostly session management and UI plumbing for a long lived interactive client; a one shot subagent driver needs a fraction of it. We have a C# loop already.
2. **Deterministic classification, not a model call.** Shell commands are classified by a parser and a command database, not by asking a small model whether a command is dangerous. A curated database is testable, free and predictable. See the library section.
3. **The model is for the residual only.** A model call is allowed for commands the database reports as `UNKNOWN`, and for the question no parser can answer: is this command consistent with the task the subagent was given. It must not be on the hot path for ordinary commands.
4. **Policy is a token set, not prose.** The policy is evaluated by code over classification facts. Prose is a human facing summary that compiles to tokens, never the enforcement mechanism.
5. **Pending approvals are addressed by id, never by position.** Positional indices break the moment a list is regenerated, reordered or partially approved.
6. **Blocking is a tool result, not a hard stop.** A blocked call returns a result saying it is pending. The model decides whether to keep working or stop and ask. This is strictly more capable than halting the run at the first wall, and it is a small difference in the gate.
7. **Session identity is the pair (workdir, name).** `"Code Review"` in two repositories is two sessions.
8. **The policy is fixed at start.** It cannot be widened on resume. Otherwise the driver can loosen the rule whenever it is blocked, which is a self approval loop. Individual items are approved with explicit ids.
9. **Artifacts live in a run directory.** Stdout is for a single JSON line and a human summary.
10. **Awaiting approval is a success outcome.** Exit code 0 with an explicit state. A nonzero exit makes Claude Code's notification say "failed", which is what happened on 2026-11-12 and made Claude go read the file anyway.

## The classification library

Use **`ShellSyntaxTree`** from NuGet. Version `0.4.0-beta.24` at time of writing. Apache-2.0, pure C#, hand rolled, zero native dependencies, targets `netstandard2.0` and `net8.0`. Source is `github.com/Aaronontheweb/ShellSyntaxTree`, and its `docs/CONSUMER_GUIDE.md` is the document to read in full before writing the policy layer.

It parses **bash and PowerShell** into a typed AST and is built for exactly this use case: "given a command emitted by an LLM, decide ALLOW / PROMPT / DENY before invoking the shell". Its original consumer is Netclaw's approval policy.

### What it gives you

- `ParsedCommand.Commands` is the authorization projection: every `CommandOccurrence` in source order, including loop bodies, command substitutions and `bash -c` payloads. `ParsedCommand.Syntax` is for display only. Authorize `Commands`; never walk `Syntax` to discover commands.
- `CommandOccurrence` carries the clause, the immediate role, ancestry, analyzed arguments, assignments, working directory and redirects, plus `IsComplete`.
- `VerbChain.CanonicalVerb` and `VerbChain.IsDynamic`. The second is an explicit "I cannot tell what this runs".
- `ShellValueDomain`: `Unknown`, `Exact`, `FiniteSet`, `PathPattern`, `IntegerRange`, `Concatenation`. This is what makes `rm $X` distinguishable from `rm build/`.
- `ShellWorkingDirectoryEffect`: `Unchanged`, `ChangesOnSuccess`, `Unknown`. The parser propagates `cd` and `Set-Location`, so `cd /repo && rm /etc/passwd` knows the `rm` runs under `/repo` and that the target is outside it. This is the feature that makes path policy correct rather than string matching.
- `RedirectAnalysis` with closed alternatives for file, descriptor, heredoc, here-string and unresolved.
- `BashParser` and `PwshParser`, the latter with `PwshDialect.PowerShell7` and `WindowsPowerShell51`. Select the parser from the shell that will actually execute, never by guessing from the command text.

### Safe-fail rules to implement

The consumer guide is explicit that `IsUnparseable = false` is **not** an allow signal. Treat all of the following as deny or prompt, never as a durable automatic grant:

- `ParsedCommand.IsUnparseable` is true.
- Non-empty input produces no command occurrences.
- An occurrence is incomplete or has an unknown role.
- An occurrence has `Verb.IsDynamic` or no statically known command identity.
- A dynamic argument or redirect affects a policy sensitive position.
- A future package version introduces an enum or syntax shape you have not mapped. Handle this by failing closed on the default branch, not by throwing.

`BashInitialStateMode.Unknown` is the safe default. Only select `IsolatedNonInteractive` if the same component that parses also enforces that the command is the complete input to a freshly spawned non-interactive shell with no profile or `BASH_ENV` and no inherited environment carrying a loop bound name. If you cannot guarantee that, leave it at `Unknown` and accept that some loops are unparseable.

### Sensitive paths

Classification describes what a command does, not what it touches. Reading a private key is genuinely read-only, so `cat ~/.ssh/id_rsa` classifies as read-only and anything auto-approving on that basis auto-approves it.

Keep the two axes orthogonal, the way `bash-classify` does it: check every argv token and every redirect target against a credential denylist, leave the classification alone, and floor the risk at high. The denylist should cover SSH and GPG keys, cloud and cluster credentials, `.env`, `.netrc`, `.npmrc`, `.git-credentials`, `.pgpass`, `.my.cnf`, and token stores such as `~/.claude.json` and the gh and glab config files. Settings files such as `~/.aws/config`, `.git/config` and `~/.gitconfig` are routine debugging and should not be on it.

### Caveat

This package is a beta and its changelog is a list of recent security fixes: quoting, ANSI-C decoding, line continuation, `#` inside words. That is active hardening, which is good, but it means holes are still being found. Treat it as a very good deterministic floor, not as a security boundary. Pin the version, and keep the consumer side fail-closed so a parser gap degrades to a prompt rather than an approval.

### Alternatives, if you hit a wall

All of these are subprocess dependencies rather than NuGet packages, so prefer `ShellSyntaxTree` for a C# host. They are worth reading for design even if unused.

- `cmdrisk` (Go): produces exactly `allow` / `requires_approval` / `reject`, eight layer pipeline, configurable allowance rules, JSON output.
- `bash-classify` (Python): 150+ commands in one YAML file each, classified as `READONLY`, `LOCAL_EFFECTS`, `EXTERNAL_EFFECTS`, `DANGEROUS`, `UNKNOWN`, plus a separate risk axis and the sensitive path handling described above. Its `match` mode does command shape rules.
- `yah`, `@themoltnet/shell-command-analyzer`, `cc-toolgate`, `cc-safety-net`, `sh-guard`: same family, varying completeness.

## Architecture

### What to copy

Copy from `D:\repos\KnowledgeSystem` into this repository and adapt. The dependency chain is small:

- `KnowledgeSystem.Agents` depends on `KnowledgeSystem.Ai.Providers`, `KnowledgeSystem.Events` and `KnowledgeSystems.Extensions`.
- All target `net11.0` with `ImplicitUsings` and `Nullable` enabled.
- `KnowledgeSystem.Agents` pulls `Microsoft.ML.Tokenizers` and `Tokenizers.HuggingFace`, used for context token estimation.

Do not copy the plugins (`KnowledgeSystem.Plugins.*`), the Discord integration, the retrieval or vector projects, the EMD parser, or `KnowledgeSystem.Api`. Copy `AGENTS.md` from that repository as well and follow it; its C# style rules are mandatory and are repeated below.

### What you are building on

- `AgentRunner.RunAsync()` loops `ExecuteTurn()` until `IsFinished`.
- `AgentRunner.TurnStatus` already distinguishes `ToolCallsReceived`, `ToolsStepped`, `ToolsFinished`, `CompletedSuccessfully`, `CompletedWithError` and `CompletionHandled`. A turn can end in a distinct state without being an error, which is what awaiting approval needs.
- `AgentRunner.StepTools()` in `KnowledgeSystem.Agents/Orchestration/AgentRunner.cs` is the single choke point for every plain tool call. The `handler.ExecuteAsync(...)` call is the injection point for the gate.
- `AgentToolFrame.RunningFrame` with `Result: null` already models a tool call that has been received but not executed. That is the mid-turn resume hook.
- `AgentToolRegistry<TContext>` is per agent, so a read-only agent is just a different registry.
- `ToolBuilder` and `ToolArgument` give you structured output for free. Use them for any tool that needs a typed verdict, rather than parsing JSON out of a model response.
- `CodeRipgrepToolHandler` in `KnowledgeSystem.Plugins.Wiki/CodeRag/` is the template for a process spawning tool: an `AllowedArgs` dictionary with typed value kinds, then a `ProcessStartInfo`. Read it before writing the bash tool.

### What is missing and must be built

- **Session persistence.** Nothing in `KnowledgeSystem.Agents` serializes `ExecutionContext.ChatMessages`. There is `AgentContext`, `ChatElement` and `ContextCompactor` but no save or load. This is the largest piece of work and it gates `--resume`.
- **Any permission concept.** There is none. The gate, the policy and the pending protocol are new.
- **A bash tool.** Only ripgrep and dotnet-rip spawn processes today.
- **A CLI entry point.** `Harness/Program.cs` in the source repository is a CodeRag test harness with `Check("PASS ...")` assertions, not a driver. Do not copy it as a starting point.

## The gate

Insert the gate at the `handler.ExecuteAsync(...)` call in `StepTools()`. Before executing, ask a gate component for a verdict: allow, deny, or defer. Allow and deny execute or short circuit as usual. Defer produces a pending result and does not execute.

One problem you will hit immediately. `ToolExecutionResult` exposes `Output` and `ErrorMessage` as properties that throw when accessed on the wrong branch:

```csharp
public string Output => IsSuccessful ? field ?? string.Empty : throw new InvalidOperationException(...);
public string? ErrorMessage => !IsSuccessful ? field : throw new InvalidOperationException(...);
```

A pending result is neither a success nor a plain error, so it does not fit this shape. Decide deliberately how to model the third state, and make the choice safe for every existing consumer of the type. Do not paper over it by encoding pending in the error string.

Denials must be sticky. A denied call returns a result that says the request was denied, that it must not be retried, and that the agent should find another way or explain what it needs. Record the denial in the session so a later resume does not ask again. Without this the model re-proposes the same command and burns turns.

## The pending protocol

When the gate defers, record an entry with:

- a stable id, derived from the tool call id rather than a position
- the tool name and the exact argv
- the classification facts, including the capability set and the reason
- the proposed scope: this invocation only, or this command for the rest of the session
- whether it needs a human rather than Claude

Anything that writes outside the workdir or touches the network should be flagged as needing a human, so Claude escalates to the user instead of approving it itself.

Persist the pending list in the session record. A resume must refuse if the recorded list and the session disagree.

The rendered block is for humans and for Claude to relay. The machine readable list is a file. Both carry the same ids.

## Session persistence

Sessions are stored under a directory you choose, one record per session, keyed by name within a workdir. A second start with the same name in the same workdir must fail loudly rather than overwrite.

The record must contain at least: the name, the absolute workdir, the shell and dialect in use, the model, the policy token set, the conversation, the pending list with its decisions, and a creation timestamp.

**Resume is mid-turn and that is the hard case.** When the gate defers, the assistant message containing that tool call is already in the transcript with no result. Resuming with an approval has to restore that state and execute the call, not re-prompt the model. `AgentToolFrame.RunningFrame` models this in memory; making it survive a process boundary is where the real bugs will be. Test it explicitly.

## The bash tool

One tool that runs a shell command. It takes the command, the working directory and a timeout. It returns exit code, stdout and stderr, with output truncated sensibly and the full output written to a file in the run directory when truncated.

Route every command through the gate before execution. Do not add a bypass flag.

Choose the shell explicitly and record it in the session. On this machine the subagent may run under bash (Claude Code's shell) or PowerShell. The parser must match the executor, and a mismatch must fail closed rather than fall back.

## The CLI

**The shape of the command line is your call.** You are the primary consumer of this tool: you will be reading its output, parsing its results and constructing its invocations from inside a coding session. Design the ergonomics for yourself. If an argument name, an output format or a subcommand arrangement is awkward to use from your side, change it. The user will review the result.

The following invariants must hold regardless of the shape you choose.

- **Start:** a subcommand that takes a session name, a workdir, a prompt, and a policy. It returns immediately and the subagent runs in the background.
- **Prompt input:** accept a path to a prompt file or stdin. Real prompts are tens of kilobytes and will not survive a command line argument. A short inline form is fine as well.
- **Resume:** a subcommand that takes a session name and a set of approvals and denials by id. It continues the existing run rather than starting over.
- **List:** a subcommand that shows sessions with their name, workdir, state, and pending count. This must exist. The absence of it is one of the failures being fixed.
- **Status:** a way to ask a running session how far along it is. This must exist.
- **Stdout:** one line of JSON describing the outcome and the run directory, plus a human readable summary. Never prose that has to be scraped to build the next invocation.
- **Artifacts:** `result.json` and `pending.json` in the run directory, UTF-8, stable key order, ASCII-only in the envelope. The text payload may contain any characters but must be valid UTF-8 and must not be escaped into unreadability.
- **Exit codes:** 0 for finished and for awaiting approval, with the state in the JSON. Distinct nonzero codes for genuine failures, documented.
- **Errors:** unknown session, bad policy, missing workdir and busy session must be distinguishable without parsing a message.

## Constraints

- C# following the rules in `AGENTS.md` from the source repository. Always use braces. Never put a statement on the same line as its condition. No unicode in code or comments: no emdashes, arrows or typographic quotes. No comments unless asked. No decorative separators. Do not break one sentence across multiple lines.
- Fatal errors must not crash the driver. Surface them as friendly messages, and never leak sensitive data.
- If a simple approach may not scale, say so and ask before proceeding.
- **Do not use git.** No commits, no branches, no status checks. The user handles version control.
- Target `net11.0`, Windows. Keep the dependency count low.

## Anti-goals

- Do not write a new agent loop.
- Do not wrap pi or use its RPC mode.
- Do not put a model call in the hot path for ordinary shell commands.
- Do not implement the policy as prose that a model interprets.
- Do not address pending items by position.
- Do not hard stop the run at the first blocked call.
- Do not let the policy change on resume.
- Do not make the driver scrape prose to find approvals.
- Do not copy the plugins, the Discord integration, the retrieval stack or the test harness from the source repository.

## Deliverables

1. The solution, building cleanly, with the copied projects adapted rather than duplicated.
2. The gate, the policy layer, the pending protocol, session persistence including mid-turn resume, the bash tool, and the CLI.
3. Tests for the parts that will silently break: the classification policy table against a corpus of commands, the pending id stability across resume, and mid-turn resume with an unexecuted tool call.
4. A short `README.md` documenting the CLI surface you settled on, with one worked example of a start, a deferral, an approval and a resume.

## Report back

When you are done, report:

- the CLI surface you chose and why it is comfortable to drive from a coding session
- every place you deviated from this document, and why
- anything in the copied code that fought the design, especially around `ToolExecutionResult` and mid-turn resume
- what you could not finish, and what you would do next

The user will review the work before it is used.
