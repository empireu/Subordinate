# Plan: Linux support for Subordinate

Branch: `cross-platform`. Read this whole document once before you change anything, then work through the phases in order. Section 3 explains how to keep your place across sessions and context compactions.

## Quick reference

| Item | Value |
| --- | --- |
| Branch | `cross-platform` |
| Diary | `diary/` in the repository root: `PLAN.md`, `TODO.md`, `NOTES.md`, `LOG.md` |
| WSL distro | `subordinate-linux`, user `tester`, created only for these tests |
| Linux source copy | `/home/tester/subordinate` (synced from the Windows checkout on every run) |
| Windows build | `pwsh -NoProfile -File scripts/Invoke-WindowsBuild.ps1` (never deploys; the only allowed way to build on Windows) |
| Windows build and tests | `pwsh -NoProfile -File scripts/Invoke-WindowsBuild.ps1 -Test` |
| Windows build, tests and smoke | `pwsh -NoProfile -File scripts/Invoke-WindowsBuild.ps1 -Test -Smoke` |
| Linux tests | `pwsh -NoProfile -File scripts/wsl/Invoke-LinuxTests.ps1 [-Smoke] [-Live]` |
| Stop WSL | `pwsh -NoProfile -File scripts/wsl/Stop-TestDistro.ps1` (Invoke-LinuxTests calls it for you) |
| Logs | Always in the Windows temp directory, never in the repository. Every script prints the paths of its logs. |

## 1. Goal

Subordinate runs only on Windows today. Make it run on Linux as well, with the same CLI, the same output contract and the same safety guarantees. Windows behavior must not change.

Two areas are Windows specific:

1. **Paths.** Authored paths are resolved into Windows paths (`/c/x` becomes `C:\x`, every other `/x` is treated as foreign), containment checks ignore case and use `\`, the session identity lowercases the workdir, and Git Bash is required to have a drive-letter workdir.
2. **Console and process handling.** The background worker is started with `CreateProcessW` from kernel32 (no console window, new process group, breakaway from the job object, no inherited handles), and the shells are found in Windows locations.

You will introduce a platform abstraction with a Windows backend (today's code, moved without behavior changes) and a Linux backend. When you are done, the owner gives the branch to friends who run Linux for real testing, so the result also needs a Linux deploy, test and smoke scripts, and a short testing guide.

Decisions already made by the owner. Do not revisit them:

- Linux is the only new platform. Other operating systems fail with a clear message.
- On Linux, bash is accepted only at version 5.2 or 5.3, the same rule as on Windows. Other versions fail closed.
- Linux supports the `bash` and `pwsh` shells (pwsh 7.6.4 up to, but not including, 7.7). `powershell` (Windows PowerShell 5.1) stays Windows only.
- Linux testing uses a dedicated WSL distro created for this work. It is started only by an explicit script and shut down after every run, because WSL 2 uses a lot of memory. Nothing in the Windows build script, the projects or the test suite starts WSL.
- `LINUX_PLAN.md` and `diary/` are committed on the branch.
- No build may ever deploy to `%USERPROFILE%\.subordinate\bin`. The owner works with the copy there, and replacing it interferes with their work. Every Windows build goes through `scripts/Invoke-WindowsBuild.ps1`, which disables deployment and checks that the directory was not touched (section 6.2).

## 2. Ground rules

### 2.1 Code style and working rules

These rules combine `AGENTS.md` with the owner's personal rules. Your harness may not load the personal rules, so this section is the complete reference. The rules apply to every line you write, in C#, PowerShell, bash and Markdown.

Formatting:

- Always use braces for control flow. The body always goes on its own line, never on the line of the condition. In bash, put `then`/`do` on the condition line and the body on the next lines.
- ASCII only: no em dashes, arrows, typographic quotes or other unicode, in code, comments, scripts and documents.
- Full, descriptive names. No abbreviations.
- Match the formatting of the surrounding code. Keep each file's line endings: some files are CRLF (`Program.cs`, `README.md`, `AGENTS.md`, `Subordinate.csproj`), others are LF (`ShellPaths.cs`, most tests). Use LF for new files. Shell scripts must be LF.
- Keep diffs minimal. When you move code into a backend, move it verbatim. Do not reformat, rename or restructure unrelated code.

Comments:

- Do not write comments unless a line is genuinely load-bearing and a comment meaningfully reduces the risk of breaking it.
- A comment is short and stays on one line. It is made of simple sentences that need only commas and a final period, with no colons, dashes or semicolons inside to separate parts. Never break a sentence across lines.
- Attach each comment to the class, method or statement it explains. A comment about the line directly below it ends with a colon.
- Describe only what the code does. Never mention this plan, the phases, design history or comparisons with the old code.
- Never write decorative separators or banners, such as lines of `=`, `-` or `*`.
- Document a parameter only when its purpose is not obvious from its name.
- Do not write TODO comments.

Correctness:

- Think about failure modes before writing code. Fatal errors must never crash the process. Report them as a `SubordinateException` with a friendly message and the right `ErrorCode`, and never leak secrets such as API keys or environment values.
- Do not write inefficient code in hot paths. Classification runs on every tool call, so do not add expensive work there. The symlink check in Phase 7 touches only the paths a command names. If a simple approach might not scale with data that can grow, ask the owner before writing it.
- Flag edge cases that can only show up in real use in NOTES.md and in your final report. Do not ship them silently.
- Follow existing patterns and reuse existing helpers before inventing new ones.

Working:

- Verify that every change builds and passes the tests (section 6) before you move on.
- If your harness has dedicated tools for reading, searching and editing files, use them instead of shell commands. Never modify files through inline scripts, `sed` or shell redirection when an edit tool can do it. Use the shell for builds, tests, scripts and git.
- Send build, test and script output to log files outside the repository, and read the results with targeted searches. The scripts in this plan do this for you. Never read a log from top to bottom or in fixed-size chunks.
- Re-read a file only when it changed on disk, or when you need a part of it you have not seen since your last edit.
- Do not spawn sub-agents.

### 2.2 Git (an exception to AGENTS.md, for this task only)

`AGENTS.md` forbids git. For this task the owner explicitly allows these commands and no others:

- `git status`, `git diff`, `git log --oneline -n <count>`
- `git switch -c cross-platform` (once, in Phase 0)
- `git add <explicit paths>` (never `git add -A` or `git add .`)
- `git commit -m "<subject>"`

Never push, pull, fetch, merge, rebase, reset, stash, amend, switch to another branch, change git config, or bypass hooks.

Commit at the end of every phase, after its verification passes, with the subject given in that phase. The convention, from the existing history (`Adds test for line endings`, `Fixes line ending issue with the file tool`, `Fixes gate, session lifecycle, and CLI issues`): one line, present tense, third person (`Adds`, `Fixes`, `Moves`), capitalized, no trailing period, no body, no trailers such as Co-Authored-By. Before each commit, run `git status` and confirm that only the intended files are staged: no logs, no build output, nothing containing a secret. Include the updated `diary/` files in every commit.

### 2.3 Protecting the owner's environment

The owner requires that their environment is left exactly as it was. That means:

- Never change Windows environment variables persistently: no `setx`, no `[Environment]::SetEnvironmentVariable(..., "User")` or `"Machine"`, no edits to PowerShell profiles, `.bashrc` or PATH. Setting `$env:NAME` inside a script is fine, because it only lives for that process.
- Never install anything on Windows (no winget, choco, msi or global dotnet tools). All Linux tooling goes inside the dedicated distro.
- Never edit `.wslconfig`, git config, NuGet config or anything under `~/.claude`.
- Never touch the `docker-desktop` WSL distro. Never change the default WSL distro; if `wsl --install` changes it, restore it. Never run `wsl --unregister`. Never run `wsl --shutdown` while another distro is running.
- Never deploy to `%USERPROFILE%\.subordinate\bin` (or `%SUBORDINATE_HOME%\bin`). A plain `dotnet build` of this repository deploys there and replaces the owner's working copy of the tool. On Windows, never run `dotnet build`, `dotnet test`, `dotnet run`, `dotnet publish` or `msbuild` yourself, and do not build from an IDE. Build only through `scripts/Invoke-WindowsBuild.ps1`. The single exception is `dotnet msbuild <project> -getProperty:<name>`, which only evaluates the project and builds nothing. If that script's deploy guard ever reports a change, stop and tell the owner.
- Never forward `SUBORDINATE_HOME` or `USERPROFILE` into WSL. The Linux build deploys only inside the distro, to `/home/tester/.subordinate/bin`.
- Never run `subordinate` commands against the real `%USERPROFILE%\.subordinate` home. The smoke script uses a temporary `SUBORDINATE_HOME`.
- Never print, log or write an API key. The live test forwards the key variable into WSL for that one process only (section 6.7).
- Stop WSL after every Linux test run.

### 2.4 The machine and your harness

- Windows 11 Pro. The repository is at `D:\repos\Subordinate`.
- PowerShell 7 (`pwsh`) is on PATH. Git Bash is at `C:\Program Files\Git\bin\bash.exe`. The .NET SDKs are `11.0.100-preview.3.26207.106` (used by this repository) and `9.0.310`. WSL is 2.6.3, and its only distro is `docker-desktop`, which you must not touch. Docker Desktop is installed but not needed.
- Your harness may run commands in a shell other than PowerShell. To work in any shell, the commands in this plan call scripts as `pwsh -NoProfile -File <script> <arguments>`. When this plan shows a PowerShell expression such as `$env:TEMP`, run it through `pwsh -NoProfile -Command "..."` if your shell is not PowerShell.
- Some steps take many minutes (the WSL setup, the first Linux build). If your harness limits how long a command may run, use its background or long-running mode if it has one. Otherwise raise the time limit for that command. Never interrupt a script halfway through the WSL setup without writing it down in the diary.

## 3. The diary

Your context may be compacted during this work, and you may be resumed in a new session. The `diary/` folder in the repository root is your memory. Keep it current and you will never lose your place.

Create these files in Phase 0:

- `diary/PLAN.md`: the phase and step list from section 7, with `[ ]` and `[x]` markers and a `Current:` line at the top naming the phase and step you are on. Update it after every step.
- `diary/TODO.md`: the next one to five concrete actions, and a `Questions for the owner` list. Rewrite it freely.
- `diary/NOTES.md`: facts you learned. Tool versions, baseline test counts, the Linux baseline failures and their causes, decisions with their reasons, deviations from this plan, surprises.
- `diary/LOG.md`: append only. One dated entry per step with what you did, the verification result (test totals, smoke results) and, at the end of a phase, the commit hash and subject.

Rules:

- At the start of every session and after every compaction, read `diary/TODO.md`, `diary/PLAN.md`, `diary/NOTES.md` and the latest entries of `diary/LOG.md`, then the section of this plan for your current phase. Then continue.
- Update the diary before you start anything long (the WSL setup, a Linux test run) and right after every verification result.
- Keep entries short and factual. ASCII only. No secrets, no environment dumps, no pasted logs: summaries and counts only.

## 4. What is Windows specific today

Line numbers are approximate; search for the symbol.

| Location | What it does today | Why it breaks on Linux |
| --- | --- | --- |
| `src/Subordinate.Core/Classification/ShellPaths.cs` | Resolves authored paths into absolute Windows paths: `/c/x` becomes `C:\x`, every other `/x` is `Foreign`, `nul` is a null device, `\` is a separator, containment ignores case. | `/etc/x` is a real local path, `nul` is an ordinary file name, `\` is not a separator, paths are case sensitive. |
| `ShellPaths.ToBashPath`, used in `ShellClassifier.Parse` (~line 80) and `SessionCommands.StartAsync` (~line 61) | Refuses bash when the workdir has no drive letter. | Every Linux workdir is refused, so every bash command classifies as `unbounded`. |
| `src/Subordinate.Core/Sessions/SessionStore.cs` `NormalizeWorkdir` (~128), `PathsFor` (~173) | Uppercases the drive letter; lowercases the workdir in the session identity hash. | Two Linux directories that differ only in case would share one session. |
| `src/Subordinate.Core/Tools/FileTools.cs` `FileToolPaths.Classify` (~54) | The session grant key is the lowercased path. | A grant would cover other files that differ only in case. |
| `SubordinateContext.cs` (~32) `FilesWritten`, `SessionRunner.cs` (~603, ~647), `SessionCommands.cs` usage (~484, ~540, ~604) and list (~754) | Compare or order paths ignoring case. | Should be case sensitive on Linux. |
| `src/Subordinate.Core/Shells/ShellLocator.cs` | Finds Git Bash under Program Files and requires `-pc-msys` or `-pc-cygwin` in `bash --version`; finds `powershell.exe` in System32 and `pwsh.exe` on PATH. | Finds nothing on Linux. |
| `ShellToolHandler.Register` (~23), `SubordinateAgent.BuildSystemPrompt` (~32) | Describe bash as Git Bash on Windows. | Wrong text for Linux. |
| `src/Subordinate/Cli/DetachedProcess.cs` | Starts the worker with `CreateProcessW`. | kernel32 does not exist; the call throws. |
| `src/Subordinate/Subordinate.csproj` | `RuntimeIdentifiers` is `win-x64`; the deploy target runs only on `Windows_NT` and uses `$(USERPROFILE)`. | Nothing is deployed on Linux. |
| `UtilityCommands.Doctor` (~174) | Checks all three shells. | `powershell` can never exist on Linux. |
| Tests | Hardcode `D:\work\repo`, Windows path expectations and case-insensitive workdir identity. | Fail on Linux. |

These already work on Linux. Keep them as they are:

- `SessionLock`: on Linux, .NET turns `FileShare.None` into an advisory `flock`, which works across processes and between two handles in one process. It does not work reliably on `/mnt/<drive>` (drvfs), which is one reason the Linux tests run from a copy in the Linux file system.
- Atomic writes with `File.Move`, the append-only transcript, the JSON envelope, and `Program.Main` writing raw UTF-8 to stdout.
- `ShellCommandExecutor`: `ArgumentList`, `Kill(true)` and environment scrubbing are cross platform.
- `Process.GetProcessById` for liveness checks.

## 5. Target design

### 5.1 Platform selection

Create `src/Subordinate.Core/Platform/`:

```cs
public interface IPlatform
{
    string Name { get; }

    IPathSemantics Paths { get; }

    IShellEnvironment Shells { get; }
}

public static class PlatformServices
{
    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    public static IPlatform Current { get; } = Select();

    private static IPlatform Select()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsPlatform.Instance;
        }

        if (OperatingSystem.IsLinux())
        {
            return LinuxPlatform.Instance;
        }

        return UnsupportedPlatform.Instance;
    }
}
```

- Every member of `UnsupportedPlatform` throws `SubordinateException(ErrorCode.InternalError, "Subordinate runs on Windows and Linux. This operating system is not supported.")`. Until a phase adds the Linux backend, `Select` returns `UnsupportedPlatform` on Linux.
- `Program.Main` checks `PlatformServices.IsSupported` at the top of its `try` block and throws that same exception, so the normal error envelope is printed.
- No static initializer may throw. A `TypeInitializationException` would bypass the friendly error path.
- Backends are `sealed` classes with a `public static readonly ... Instance`. `Subordinate.Core` already has `InternalsVisibleTo Subordinate.Tests`, so tests can call a specific backend directly.
- Mark classes that use OS-only APIs with `[SupportedOSPlatform("linux")]` or `[SupportedOSPlatform("windows")]`, and guard calls into them with `OperatingSystem.IsLinux()` or `IsWindows()`. The build must not gain new warnings (CA1416 included).

### 5.2 Paths

```cs
public interface IPathSemantics
{
    StringComparer Comparer { get; }

    StringComparison Comparison { get; }

    string NormalizeDirectory(string directory);

    string IdentityKey(string normalizedDirectory);

    string GrantKey(string canonicalPath);

    bool SupportsShellWorkingDirectory(ShellKind shell, string directory);

    ResolvedPath Resolve(string value, string? baseDirectory);

    string? Canonicalize(string absolutePath);

    bool IsInside(string path, string root);
}
```

`ShellPaths` stays the static facade that the classifier, file tools and shell tool use. `Resolve`, `Canonicalize` and `IsInside` keep their signatures and delegate to `PlatformServices.Current.Paths`. `IsUrl` and `GlobCoveringPrefix` are platform neutral and stay in `ShellPaths`. `ToBashPath` moves to `WindowsPathSemantics.ToBashPath` (public static, so its test keeps working). Change doc comments that say "absolute Windows path" to "absolute path".

**Windows backend** (`WindowsPathSemantics`): today's code, verbatim.

- `Comparer` is `StringComparer.OrdinalIgnoreCase`; `Comparison` is `StringComparison.OrdinalIgnoreCase`.
- `NormalizeDirectory` is the current body of `SessionStore.NormalizeWorkdir`.
- `IdentityKey` and `GrantKey` return `value.ToLowerInvariant()`.
- `SupportsShellWorkingDirectory` returns `shell != ShellKind.Bash || ToBashPath(directory) != null`.
- `Resolve`, `Canonicalize` and `IsInside` are the current `ShellPaths` bodies.

**Linux backend** (`LinuxPathSemantics`):

- `Comparer` is `StringComparer.Ordinal`; `Comparison` is `StringComparison.Ordinal`.
- `Canonicalize(absolutePath)` is purely lexical: no `Path.GetFullPath`, no file system access. It returns null when the input does not start with `/` or contains `\0`, `\`, `*`, `?`, `"`, `<`, `>` or `|`. Otherwise it splits on `/`, drops empty and `.` segments, lets `..` remove the previous segment (never going above the root), and joins with `/`. The root is `/`. There is no trailing slash.
- `Resolve(value, baseDirectory)` checks these in order, on the trimmed value:
  1. Empty or contains `\0`: `Unknown`.
  2. `/dev/null`, or `$null` compared ignoring case (kept from today): `NullDevice`.
  3. URL prefix (`ShellPaths.IsUrl`): `Foreign`.
  4. Contains `\`: `Unknown`. On Linux a backslash is part of a file name for bash but a separator for pwsh cmdlets, so it fails closed.
  5. Provider prefix, the existing regex `^[A-Za-z][A-Za-z0-9_]+:` (for example `env:` or `HKLM:`): `Foreign`.
  6. Starts with `/`: `Canonicalize(value)`, giving `Local`, or `Unknown` when that returns null.
  7. Starts with `~`: `Unknown`.
  8. Otherwise, when `baseDirectory` is null: `Unknown`. When it is set: `Canonicalize(baseDirectory + "/" + value)`.

  This means `nul` is an ordinary relative file name, `C:x` and `D:/x` are ordinary relative names (one letter before the colon does not match the provider regex), and `/c/x` is simply `/c/x`.
- `IsInside(path, root)`: the same segment-aware check as Windows, using `/` and `Ordinal`, where the root `/` contains every path. Expose it as `public static bool IsInsideLexically(string path, string root)`. In Phase 7, when running on Linux, `IsInside` also requires the symlink-resolved path to be inside the resolved root (section 5.6).
- `NormalizeDirectory(directory)` returns `Canonicalize(Path.GetFullPath(directory))`. When that is null, throw `SubordinateException(ErrorCode.InvalidArguments, $"The directory {directory} cannot be used on Linux because its path contains a backslash or a wildcard character.")`.
- `IdentityKey` and `GrantKey` return their input unchanged.
- `SupportsShellWorkingDirectory` returns true when the directory starts with `/`.

These results become `LinuxPathSemanticsTests`. They are pure string logic, so they use plain `[Theory]` and `[Fact]` and also run on Windows. Calling the Linux backend directly does not start WSL.

| Value | Base | Kind | Path |
| --- | --- | --- | --- |
| `/work/repo/src/a.cs` | null | Local | `/work/repo/src/a.cs` |
| `src/../b.txt` | `/work/repo` | Local | `/work/repo/b.txt` |
| `../other` | `/work/repo` | Local | `/work/other` |
| `./a//b/./c/` | `/work/repo` | Local | `/work/repo/a/b/c` |
| `/` | null | Local | `/` |
| `/..` | null | Local | `/` |
| `//server/share/x` | null | Local | `/server/share/x` |
| `/c/Users/me/x` | null | Local | `/c/Users/me/x` |
| `D:/repos/y` | `/work/repo` | Local | `/work/repo/D:/repos/y` |
| `C:relative` | `/work/repo` | Local | `/work/repo/C:relative` |
| `nul` | `/work/repo` | Local | `/work/repo/nul` |
| `/dev/null` | `/work/repo` | NullDevice | null |
| `$null` | `/work/repo` | NullDevice | null |
| `src\a.cs` | `/work/repo` | Unknown | null |
| `..\..\etc` | `/work/repo` | Unknown | null |
| `~/x` | `/work/repo` | Unknown | null |
| `relative` | null | Unknown | null |
| `/tmp/a*b` | null | Unknown | null |
| `env:SECRET` | `/work/repo` | Foreign | null |
| `https://example.invalid/a` | `/work/repo` | Foreign | null |

| Path | Root | `IsInsideLexically` |
| --- | --- | --- |
| `/work/repo` | `/work/repo` | true |
| `/work/repo/src` | `/work/repo` | true |
| `/work/REPO/src` | `/work/repo` | false |
| `/work/repo2` | `/work/repo` | false |
| `/work` | `/work/repo` | false |
| `/anything` | `/` | true |

### 5.3 Shells

```cs
public interface IShellEnvironment
{
    IReadOnlyList<ShellKind> SupportedShells { get; }

    ShellInfo Locate(ShellKind kind, ShellPathsEntry configured);

    string? ProbeVersion(ShellKind kind, string executablePath);

    string ToolDescription(ShellKind kind);

    string PromptDescription(ShellKind kind);
}
```

`ShellLocator` stays the facade. `Locate` delegates to `PlatformServices.Current.Shells.Locate`, and `VerifyUnchanged` stays in `ShellLocator` but calls `ProbeVersion`. Move the shared helpers (`RunProbe`, `FindOnPath` and the version regexes) into an internal static class `ShellProbe` in `Shells/`.

**Windows backend** (`WindowsShellEnvironment`): the current `LocateBash`, `LocateWindowsPowerShell`, `LocatePwsh`, `ProbeBash` and `ProbePowerShell`, verbatim. `SupportedShells` is `[Bash, WindowsPowerShell, PowerShell7]`. The descriptions are today's strings, byte for byte, because they end up in system prompts:

- Tool: `Git Bash on Windows. Use POSIX syntax; Windows drives are /c/..., /d/... and so on.` / `Windows PowerShell 5.1. Use PowerShell syntax.` / `PowerShell 7.6. Use PowerShell syntax.`
- Prompt: `Git Bash (bash 5.2 on Windows). Write POSIX shell syntax. Windows drives appear as /c/, /d/ and so on.` / `Windows PowerShell 5.1. Write PowerShell syntax.` / `PowerShell 7.6. Write PowerShell syntax.`

**Linux backend** (`LinuxShellEnvironment`):

- `SupportedShells` is `[Bash, PowerShell7]`.
- Bash: use the configured path when set. Otherwise try `/usr/bin/bash`, `/bin/bash`, then `bash` on PATH, removing duplicates with `Ordinal` comparison. Probe with `--norc --noprofile --version`. Accept a candidate only when the output contains a platform triple with `-linux` (for example `x86_64-pc-linux-gnu`) and the version is 5.2.x or 5.3.x. Otherwise add it to the rejected list with a reason. When nothing is accepted, throw `ShellUnavailable` with `bash 5.2 or 5.3 was not found. Set shells.bash in the configuration to the path of a bash 5.2 or 5.3 executable.` followed by the rejected list.
- Windows PowerShell: throw `ShellUnavailable` with `Windows PowerShell 5.1 is only available on Windows. Use --shell bash or --shell pwsh.`
- pwsh: use the configured path, otherwise `pwsh` on PATH, then `/opt/microsoft/powershell/7/pwsh`, then `/usr/bin/pwsh`. Apply the same version rule as Windows (7.6.4 or later, below 7.7). The messages say `pwsh`, not `pwsh.exe`.
- PATH lookups on Linux also require an execute bit: `(File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0`.
- Record the path as found. Do not resolve symlinks.
- Tool descriptions: `bash on Linux. Use POSIX syntax.` / `PowerShell 7.6 on Linux. Use PowerShell syntax and forward slashes in paths.`
- Prompt descriptions: `bash (5.2 or 5.3) on Linux. Write POSIX shell syntax.` / `PowerShell 7.6 on Linux. Write PowerShell syntax and use forward slashes in paths.`
- For `WindowsPowerShell` (which never reaches a session on Linux), return the Windows strings.

`UtilityCommands.Doctor` loops over `PlatformServices.Current.Shells.SupportedShells` instead of its fixed array.

### 5.4 Detached worker launch (console handling)

Requirements, the same on both platforms: `start` and `resume` return immediately; the worker survives the caller exiting and the caller's process group being signalled; and the worker holds none of the caller's stdin, stdout or stderr, so a caller reading `subordinate start` through a pipe sees end of file as soon as `start` exits.

In `src/Subordinate/Cli/`:

```cs
internal interface IDetachedProcessLauncher
{
    int Start(string executable, IReadOnlyList<string> arguments, string workingDirectory);

    void DetachCurrentProcess();
}
```

- `DetachedProcess.cs` becomes a static selector with `public static IDetachedProcessLauncher Current`: `WindowsDetachedProcessLauncher` on Windows, `LinuxDetachedProcessLauncher` on Linux, and before Phase 6 an unsupported launcher on Linux that throws a friendly `SubordinateException`.
- `WindowsDetachedProcessLauncher.cs`: today's `CreateProcessW` code and `AppendArgument`, verbatim. `DetachCurrentProcess` does nothing.
- `LinuxDetachedProcessLauncher.cs`, marked `[SupportedOSPlatform("linux")]`:
  - `Start`: a `ProcessStartInfo` with `FileName = executable`, `WorkingDirectory`, `UseShellExecute = false` and `RedirectStandardInput`, `RedirectStandardOutput` and `RedirectStandardError` all true, plus every argument in `ArgumentList`. Start it, then immediately close `StandardInput`, `StandardOutput` and `StandardError`, dispose the `Process` object and return its `Id`. Do not wait for it.
  - `DetachCurrentProcess`: call `setsid` through `[LibraryImport("libc", EntryPoint = "setsid", SetLastError = true)] private static partial int CreateNewSession();` and ignore the result. Then store `PosixSignalRegistration.Create(PosixSignal.SIGHUP, context => { context.Cancel = true; })` in a static field so it lives as long as the process.
- `SessionCommands.WorkerAsync` calls `DetachedProcess.Current.DetachCurrentProcess()` first.
- `SessionCommands.LaunchAsync` catches `Win32Exception or InvalidOperationException or IOException` around `Start` (Process.Start on Linux can throw any of them) and keeps the existing friendly message.

Why this works on Linux:

- .NET opens every file with close-on-exec, so the child inherits only descriptors 0, 1 and 2. Redirecting them to pipes and closing the parent's ends leaves the worker holding nothing of the caller's.
- `setsid` moves the worker into its own session and process group with no controlling terminal, so Ctrl+C or a tool timeout aimed at the caller's group does not reach it.
- The worker never writes to stdout. .NET ignores SIGPIPE, so a stray write fails with an IOException instead of killing the worker.
- The worker calls `setsid` on itself, so its PID stays the one `Process.Start` returned. `LaunchAsync` relies on that: it waits for `status.json` to report that PID.

### 5.5 Build and deploy

Keep every Windows line of `Subordinate.csproj` unchanged. In particular, do not add Linux runtime identifiers to the Windows `RuntimeIdentifiers`, because Windows restores would then download Linux runtime packs.

After the existing home and deploy `PropertyGroup`, add a group that applies only on Linux:

```xml
<PropertyGroup Condition="$([MSBuild]::IsOSPlatform('Linux'))">
    <SubordinateLinuxRuntime Condition="'$([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)' == 'Arm64'">linux-arm64</SubordinateLinuxRuntime>
    <SubordinateLinuxRuntime Condition="'$(SubordinateLinuxRuntime)' == ''">linux-x64</SubordinateLinuxRuntime>
    <RuntimeIdentifiers>$(SubordinateLinuxRuntime)</RuntimeIdentifiers>
    <SubordinateHomeDirectory Condition="'$(SUBORDINATE_HOME)' == ''">$(HOME)/.subordinate</SubordinateHomeDirectory>
    <SubordinateDeployDirectory>$(SubordinateHomeDirectory)/bin/</SubordinateDeployDirectory>
    <SubordinatePublishDirectory>$(MSBuildProjectDirectory)/obj/deploy/$(Configuration)/</SubordinatePublishDirectory>
</PropertyGroup>
```

Add a target `DeployToSubordinateHomeLinux` that mirrors the Windows target, with the condition `'$(SubordinateDeploying)' != 'true' And $([MSBuild]::IsOSPlatform('Linux'))` and `RuntimeIdentifier=$(SubordinateLinuxRuntime)`. Do not edit the Windows target.

- If MSBuild rejects the `RuntimeInformation` property function, derive the architecture from `$(NETCoreSdkRuntimeIdentifier)` instead (it ends in `-arm64` or `-x64`), and note it in the diary.
- After a Linux build, `~/.subordinate/bin/subordinate` must exist and be executable. If the Copy task loses the execute bit, add `<Exec Command="chmod +x &quot;$(SubordinateDeployDirectory)subordinate&quot;" />` to the Linux target only.
- A running worker keeps the binary busy, so the copy fails with "Text file busy" and keeps the old binary. That matches the Windows behavior (warn and keep the old copy) and is acceptable.

### 5.6 Linux hardening (Phase 7)

Linux opens a few holes that Windows does not have. Close them:

1. **Process environments.** `/proc/<pid>/environ` exposes the environment of the user's other processes, including the worker's API key variable. Add a `SensitivePaths` pattern, `"process environment"`, matching `/proc/<anything without a slash>/environ` (the matched text has backslashes already normalized to slashes).
2. **`ps` with environments.** BSD-style `ps e` prints environments (`ps eww`, `ps auxe`). `ps` is currently in the read-only group of `CommandDatabase`. Give it its own rule with a `Refine` that calls `context.Add(Capability.Sensitive, "ps can print the environment of other processes")` when either of these holds:
   - an argument that does not start with `-` contains `e`
   - an argument that starts with a single `-` contains `e` together with `u` or `x`

   `ps -ef`, `ps aux` and `ps -p 1 -o comm=` stay read only. Format lists such as `-o user,pid` are flagged too; that is a harmless false positive. This rule applies on both platforms because it only makes classification stricter.
3. **Linux credential stores.** Add patterns for `.password-store`, `.local/share/keyrings`, `/etc/shadow` and `/etc/gshadow`.
4. **Symbolic links.** On Linux, git checks out symbolic links as real links, and containment is checked lexically. A link inside the workdir that points outside (for example `docs -> /home/me`) would make `cat docs/.bashrc` or `write_file docs/x` look like workdir access.
   - In `LinuxPathSemantics.IsInside`, when `OperatingSystem.IsLinux()`, a path counts as inside only if it is lexically inside and its real path is inside the root's real path.
   - Real path: call libc `realpath` on the longest existing prefix of the path and append the remaining segments. Use `[LibraryImport("libc", EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr ResolveRealPath(string path, IntPtr resolvedPath);`, read the result with `Marshal.PtrToStringUTF8`, and release it with libc `free` (also via `LibraryImport`). If no prefix resolves, treat the path as outside.
   - Resolve the root too, once per call, because the workdir itself may be reached through a link.
   - Put this in a separate class `LinuxRealPaths`, marked `[SupportedOSPlatform("linux")]`.
   - Record the limit in NOTES and the README: links created by an earlier part of the same command are not visible at classification time. The classifier is a deterministic floor, not a sandbox.

### 5.7 Call-site map

| Call site | Change |
| --- | --- |
| `ShellPaths.Resolve`, `Canonicalize`, `IsInside` | Delegate to `PlatformServices.Current.Paths`. |
| `ShellPaths.ToBashPath` | Moves to `WindowsPathSemantics.ToBashPath`. |
| `ShellClassifier.Parse`, the `ToBashPath(...) == null` check | `!PlatformServices.Current.Paths.SupportsShellWorkingDirectory(ShellKind.Bash, scope.WorkingDirectory)`. Keep the error text. |
| `SessionCommands.StartAsync`, the bash drive check | The same replacement. Keep the message. |
| `SessionStore.NormalizeWorkdir` | Keep the static method; its body delegates to `Paths.NormalizeDirectory`. |
| `SessionStore.PathsFor` | `Paths.IdentityKey(NormalizeWorkdir(workdir)) + "\n" + name.ToLowerInvariant()`. Session names stay case insensitive on every platform. |
| `FileToolPaths.Classify`, `Key` | `Paths.GrantKey(path.Path!)`. |
| `ToolEnvironment.FilesWritten` | `new(PlatformServices.Current.Paths.Comparer)`. |
| `SessionRunner`, both `FilesWritten.Order(...)` | `Paths.Comparer`. |
| `SessionCommands.Usage`: `byWorkdir`, the workdir filter, the workdir ordering | `Paths.Comparer` and `Paths.Comparison`. Model and session comparisons stay as they are. |
| `SessionCommands.List`, the workdir filter | `Paths.Comparison`. |
| `ShellLocator.Locate`, `VerifyUnchanged` | Delegate to `Shells.Locate` and `Shells.ProbeVersion`. |
| `ShellToolHandler.Register`, the shell description | `Shells.ToolDescription(shell)`. |
| `SubordinateAgent.BuildSystemPrompt`, the shell text | `Shells.PromptDescription(shell)`. |
| `UtilityCommands.Doctor`, the shell array | `Shells.SupportedShells`. |
| `SessionCommands.LaunchAsync`, `DetachedProcess.Start` | `DetachedProcess.Current.Start`, with the wider catch. |
| `SessionCommands.WorkerAsync` | Calls `DetachedProcess.Current.DetachCurrentProcess()` first. |
| `Program.Main` | Checks for an unsupported platform. |
| `SubordinateConfig.ResolveHome` | When `Environment.GetFolderPath(SpecialFolder.UserProfile)` is empty, throw `ConfigError` with `The home directory is unknown. Set SUBORDINATE_HOME.` |

Leave these alone: the name comparisons in `SessionResolver`, `PendingLedger` id comparisons, `PolicySet` lowercasing, `CommandDatabase.NormalizeProgram`, and the secret-variable scrubbing in `ShellCommandExecutor`.

## 6. Testing procedure

### 6.1 Test gating

Add `tests/Subordinate.Tests/PlatformFacts.cs` with four attributes, `WindowsFactAttribute`, `WindowsTheoryAttribute`, `LinuxFactAttribute` and `LinuxTheoryAttribute`, all following this pattern:

```cs
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Runs on Windows only.";
        }
    }
}
```

Also add `internal static class TestPaths` with `public static string Workdir => OperatingSystem.IsWindows() ? @"D:\work\repo" : "/work/repo";`.

Rules:

- Plain `[Fact]` and `[Theory]` mean platform neutral; they run everywhere.
- Windows behavior (drive letters, backslash separators, case-insensitive identity, Git Bash, Windows PowerShell) gets the Windows attributes. Linux behavior (POSIX paths, case sensitivity, symlinks, real bash, `setsid`) gets the Linux attributes.
- Pure backend logic, such as the Linux path table, uses plain attributes and calls the backend directly.
- Nothing in any test or build target starts WSL. WSL runs only through `scripts/wsl/*.ps1`.
- Never make a failing test pass by gating it away. Gate a test only when the behavior it asserts is genuinely platform specific, and add the counterpart for the other platform in the same commit.
- Never weaken a security expectation (a corpus row, a sensitive pattern, a containment result) just to make a test pass. If you think an expectation in this plan is wrong, write down why in NOTES.md and ask the owner.

### 6.2 Windows build and tests

Every Windows build goes through `scripts/Invoke-WindowsBuild.ps1`, which you write first, in Phase 0. It is the only allowed way to build on Windows, because it guarantees that nothing is deployed to the owner's `.subordinate\bin`.

```
pwsh -NoProfile -File scripts/Invoke-WindowsBuild.ps1 [-Test] [-Smoke] [-Configuration Debug]
```

What the script does, in order:

1. Uses `Set-StrictMode -Version Latest` and `$ErrorActionPreference = "Stop"`, finds the repository root from its own location, and checks `$LASTEXITCODE` after every native command.
2. Disables deployment twice over. It sets `$env:SubordinateDeploying = "true"` for its own process (MSBuild reads environment variables as properties, so this also reaches nested builds), and it passes `-p:SubordinateDeploying=true` to every `dotnet` command it runs. It never runs `dotnet publish` and never sets the property to anything else.
3. Deploy guard, part one. It works out the deploy directory the same way the project does: `$env:SUBORDINATE_HOME\bin` when that variable is set, otherwise `$env:USERPROFILE\.subordinate\bin`. It records a fingerprint of that directory: the relative path, length and `LastWriteTimeUtc` of every file in it, or an empty fingerprint when it does not exist. It only reads the directory and never writes to it.
4. Runs `dotnet build Subordinate.slnx -c <Configuration> -p:SubordinateDeploying=true` and writes all output to `$env:TEMP\subordinate-build.log`.
5. With `-Test`, runs `dotnet test tests/Subordinate.Tests -c <Configuration> -p:SubordinateDeploying=true` and writes the output to `$env:TEMP\subordinate-test.log`.
6. With `-Smoke`, runs `scripts/smoke.sh` (section 6.6) under Git Bash against the framework-dependent build output. The binary path uses forward slashes: `<repository root>/src/Subordinate/bin/<Configuration>/net11.0/subordinate.exe`. Output goes to `$env:TEMP\subordinate-smoke-windows.log`. The smoke script uses a temporary home, so the real one is never touched.
7. Deploy guard, part two. It fingerprints the deploy directory again. If anything differs, it prints `DEPLOY GUARD FAILED: <directory> changed during the build. Stop and tell the owner.` and exits 1. It does not try to repair anything.
8. Prints only summary lines:
   - from the build log: `: error |: warning |Build succeeded|Build FAILED|Warning\(s\)|Error\(s\)`, the last 30 matches
   - from the test log: `^\s+Failed |Passed!|Failed!`, the last 40 matches
   - from the smoke log: `^SMOKE `
   - the full paths of the three logs
9. Exits nonzero when the build, the tests, the smoke or the guard failed.

To see why a test failed, search the test log for its name with some context after it, for example `Select-String -Path <log> -Pattern 'ResolvesShellPaths' -Context 0,15` in PowerShell, or `grep -A 15 'ResolvesShellPaths' <log>` in bash.

### 6.3 Windows smoke

`pwsh -NoProfile -File scripts/Invoke-WindowsBuild.ps1 -Test -Smoke` runs the smoke checks against the Windows build output, with nothing deployed. On Windows the detach checks are skipped (section 6.6), because the Windows launcher is moved verbatim and is covered by the other checks.

### 6.4 One-time WSL setup

Run `pwsh -NoProfile -File scripts/wsl/Install-TestDistro.ps1` once, in Phase 1. It takes several minutes, so if your shell tool has a time limit, run it in the background and wait for it to finish. It must be idempotent: running it again repairs or skips what already exists.

What the script does, in order:

1. Takes `-DistroName` (default `subordinate-linux`) and `-UserName` (default `tester`). Sets `$env:WSL_UTF8 = "1"` and `[Console]::OutputEncoding = [Text.Encoding]::UTF8` for its own process. Uses `Set-StrictMode -Version Latest` and `$ErrorActionPreference = "Stop"`, and checks `$LASTEXITCODE` after every `wsl.exe` call.
2. Records the current default distro, the line marked `*` in `wsl.exe --list --verbose`.
3. If the distro does not exist, checks that `wsl.exe --list --online` offers `Ubuntu-24.04` and runs `wsl.exe --install Ubuntu-24.04 --name subordinate-linux --no-launch`. If the install asks for elevation or a reboot, or fails, stop and ask the owner.
4. Runs `scripts/wsl/setup-root.sh` as root: `wsl.exe -d subordinate-linux -u root --exec bash /mnt/d/repos/Subordinate/scripts/wsl/setup-root.sh tester` (convert the repository path, section 6.5). Pipe empty input into `wsl.exe` (`$null | wsl.exe ...`) so a stray prompt fails instead of hanging. The script:
   - runs `apt-get update` and `DEBIAN_FRONTEND=noninteractive apt-get install -y ca-certificates curl rsync python3 libicu74`
   - creates the user with `useradd --create-home --shell /bin/bash tester` when it does not exist
   - writes `/etc/wsl.conf` with exactly this content:

     ```
     [boot]
     systemd=false

     [interop]
     appendWindowsPath=false

     [user]
     default=tester
     ```

   Disabling systemd keeps the VM light. Not appending the Windows PATH keeps the distro hermetic, so `dotnet` or `pwsh` never resolve to Windows binaries.
5. Runs `wsl.exe --terminate subordinate-linux` so `wsl.conf` takes effect.
6. Runs `scripts/wsl/setup-user.sh` as `tester`, passing the SDK version: the output of `dotnet --version` run on Windows in the repository root, which only prints the version and builds nothing (currently `11.0.100-preview.3.26207.106`). The script:
   - downloads `https://dot.net/v1/dotnet-install.sh` to `~/dotnet-install.sh` and runs it with `--version <sdk> --install-dir ~/.dotnet`
   - looks up the newest `v7.6.N` release with N of 4 or more at `https://api.github.com/repos/PowerShell/PowerShell/releases?per_page=100`, downloads `powershell-7.6.N-linux-x64.tar.gz`, extracts it into `~/.local/pwsh` and makes `~/.local/pwsh/pwsh` executable. If no release qualifies, it prints a warning and skips pwsh; the pwsh tests then skip themselves.
   - prints `bash --version | head -1`, `~/.dotnet/dotnet --version` and the pwsh version.

   Nothing edits `.bashrc` or any profile. The test scripts set PATH for their own process.
7. Restores the default distro recorded in step 2, if it changed (`wsl.exe --set-default <previous>`).
8. Calls `Stop-TestDistro.ps1`.

Write the versions it reports into `diary/NOTES.md`.

`scripts/wsl/Stop-TestDistro.ps1` runs `wsl.exe --terminate subordinate-linux`. Then, if `wsl.exe --list --running --quiet` prints nothing, it runs `wsl.exe --shutdown` to give the memory back. Otherwise it prints `Other WSL distributions are running; the WSL VM was left up.`

Removing the distro is the owner's decision. Document `wsl --unregister subordinate-linux` in the README and never run it yourself.

### 6.5 Linux test run

`scripts/wsl/Invoke-LinuxTests.ps1`:

- Parameters: `-DistroName`, `-UserName`, `-LogFile` (default `$env:TEMP\subordinate-linux.log`, overwritten on each run), `-Smoke`, `-Live`, `-LiveConfig <Windows path>`, `-KeepRunning`. `-Live` implies `-Smoke` and uses the owner's config, which it only reads: `$env:SUBORDINATE_HOME\config.json` when that variable is set, otherwise `$env:USERPROFILE\.subordinate\config.json`. `-LiveConfig` overrides that path and also implies `-Live`.
- Sets `$env:WSL_UTF8 = "1"` for its own process.
- Converts the repository root (two levels above the script) to its WSL path: `/mnt/` + the lowercase drive letter + the rest with `/` separators. For example `D:\repos\Subordinate` becomes `/mnt/d/repos/Subordinate`.
- Syncs the source into the Linux file system without a shell, so nothing is reinterpreted: `wsl.exe -d subordinate-linux -u tester --exec rsync -a --delete --exclude=bin/ --exclude=obj/ --exclude=.git/ --exclude=.idea/ --exclude=.vs/ --exclude=diary/ /mnt/d/repos/Subordinate/ /home/tester/subordinate/`. Excluded directories are not deleted on the Linux side, so Linux builds stay incremental.
- Runs `bash /home/tester/subordinate/scripts/test-linux.sh` as `tester` and appends all output to the log file.
- With `-Smoke`, runs `bash /home/tester/subordinate/scripts/smoke.sh --binary /home/tester/.subordinate/bin/subordinate` and appends its output.
- With `-Live`, reads the config JSON on Windows, collects every `api_key_env` name, appends `NAME/u` entries to `$env:WSLENV` for this process only, and passes `--live-config <WSL path of the file>` to the smoke script. It never prints a variable's value.
- Unless `-KeepRunning` is given, calls `Stop-TestDistro.ps1`, even when a step failed (use `try`/`finally`).
- Prints only summary lines: `Select-String -Path $LogFile -Pattern '^SMOKE |Passed!|Failed!|^\s+Failed |: error |Build FAILED'`.
- Exits with a nonzero code when any step failed.

`scripts/test-linux.sh` runs on any Linux machine: the WSL distro or a friend's computer.

- `set -euo pipefail`. Finds the repository root from the script's own location.
- When `$HOME/.dotnet/dotnet` exists, exports `DOTNET_ROOT` and prepends it to PATH. When `$HOME/.local/pwsh/pwsh` exists, exports `SUBORDINATE_TEST_PWSH` with that path. Exports `DOTNET_CLI_TELEMETRY_OPTOUT=1` and `DOTNET_NOLOGO=1`. All of this affects the script's own process only.
- Prints `uname -sr`, the first line of `bash --version` and `dotnet --version`.
- Runs `dotnet build Subordinate.slnx` (on Linux this also deploys to `~/.subordinate/bin`), then `dotnet test tests/Subordinate.Tests`, and exits with the test exit code.

Expect the first Linux build to take several minutes (package restore and the Linux runtime pack). Later builds take one or two minutes.

### 6.6 Smoke script

`scripts/smoke.sh --binary <path> [--live-config <path>]`:

- Creates a temporary directory with `home/` and `work/` inside, and exports `SUBORDINATE_HOME` to `home/`. Removes the directory and stops any helper process in an `EXIT` trap.
- Prints exactly one line per check, `SMOKE PASS <name>`, `SMOKE FAIL <name>: <detail>` or `SMOKE SKIP <name>: <reason>`, and exits 1 if anything failed.
- Never prints environment variables.
- Parses the JSON with `grep -o`. Do not depend on `jq`.
- Runs under Git Bash on Windows too. When `uname -s` is not `Linux`: convert every path given to the binary, including `SUBORDINATE_HOME`, with `cygpath -w`; export `MSYS2_ARG_CONV_EXCL='*'` so Git Bash does not rewrite arguments; and skip the Linux-only checks.

Checks:

1. `help`: exits 0 and the first line starts with `{"ok":true`.
2. `init`: exits 0 and creates `config.json`.
3. Config. On Linux, when `python3` exists, first start a listener that accepts connections and never answers: `python3 -c 'import socket, time; listener = socket.socket(); listener.bind(("127.0.0.1", 0)); listener.listen(16); print(listener.getsockname()[1], flush=True); time.sleep(900)'` in the background, and read its port. Then overwrite `config.json` with:
   - provider `dead`: endpoint `http://127.0.0.1:9/v1`, `api_key` `smoke`
   - provider `hang`: endpoint `http://127.0.0.1:<port>/v1`, `api_key` `smoke`, `network_timeout_seconds` 120 (only when the listener runs)
   - models `dead` and `hang` with model id `smoke-model`, and `default_model` `dead`

   The keys are fake and never reach a real service.
4. `doctor`: exits 0 and the summary has a line matching `^ok +shell:bash:`.
5. `classify`: `--command "ls" --policy readonly` gives `"verdict":"allow"`, and `--command "rm -rf /tmp/smoke-x" --policy edit` gives `"verdict":"defer"` with `write-outside` among the missing capabilities.
6. Failed run: `start --name smoke-dead --policy readonly --prompt "Say hi." --workdir <work> --model dead` exits 0. Then `wait --name smoke-dead --workdir <work> --timeout-seconds 60` exits 20 with `"state":"failed"`.
7. Detach, on Linux only, and only when the listener runs (added in Phase 6):
   - `start --name smoke-hang --model hang ...`, piped through `cat` under `timeout 30`, must finish within 15 seconds (measure with `$SECONDS`). This proves the worker does not hold the caller's pipe.
   - Read `"pid":<n>` from the output. `kill -0 <n>` succeeds. `ps -o sid= -p <n>` equals `<n>`, so the worker leads its own session. `ps -o tty= -p <n>` is `?`, so it has no controlling terminal.
   - `resume --name smoke-hang --workdir <work> --message x` exits 5 (`session_busy`), which proves the lock works across processes.
   - `stop --name smoke-hang --workdir <work>` exits 0. Then `wait ... --timeout-seconds 60` reports `"state":"stopped"`. If it reports another state, write it in NOTES.md and treat it as a failure to investigate.
8. Live, only with `--live-config`: see section 6.7.

### 6.7 Live test

Live runs against the owner's model are allowed but cost money, so run them only where a phase says so: optionally once in Phase 6, and once in Phase 8.

```
pwsh -NoProfile -File scripts/wsl/Invoke-LinuxTests.ps1 -Live
```

Inside `smoke.sh`, the live block:

- Skips with `SMOKE SKIP live` when the key variable named in the config is not set.
- Copies the config into a second temporary home. When `python3` can parse it, sets `shells` to an empty object, because a configured Windows shell path cannot exist on Linux. It only ever reads the owner's file.
- Creates `a.txt` in a fresh workdir. Then `start --name smoke-live --policy readonly --prompt "Use the shell tool to run ls, then report the file names you saw." --wait --max-turns 6` must exit 0 with `"state":"finished"`, and the summary must mention `a.txt`.
- `start --name smoke-approve --policy readonly --prompt "Create a file named hello.txt that contains the word hi. Then report what you did." --wait --max-turns 6` must end in `"state":"awaiting_approval"`. Read the first id from `pending.json` (`grep -o '"id":"[0-9a-f]\{10\}"'`) and run `resume --name smoke-approve --approve <id> --wait --max-turns 6`. Repeat for up to three rounds while the state is `awaiting_approval`. The check passes when `hello.txt` exists at the end.

### 6.8 Triage

- **Build errors:** search the log for `: error `, fix, rebuild.
- **Test failures:** collect the names from the `^\s+Failed ` lines, then search for each name with context to read the assertion message.
- **Linux-only failures:** decide whether the code or the test's assumption is wrong. Record each failure, its cause and its fix in NOTES.md.
- **A WSL command hangs:** it is almost certainly waiting for interactive input. Stop it, run `Stop-TestDistro.ps1`, write down what happened and ask the owner.
- **The same problem resists two honest attempts:** write down what you tried in NOTES.md and ask the owner instead of guessing further.

## 7. Phases

Work through the phases in order. Every phase ends with verification, a diary update and a commit. Do not start a phase while the previous one is uncommitted.

### Phase 0: Preparation

1. Read this plan, `AGENTS.md` and `README.md`. Skim the files listed in section 4.
2. Run `git status`. The tree must be clean apart from the untracked `LINUX_PLAN.md`. If anything else is modified, stop and ask the owner.
3. Run `git log --oneline -n 10` and confirm the commit message convention.
4. Run `git switch -c cross-platform`.
5. Create `diary/PLAN.md`, `diary/TODO.md`, `diary/NOTES.md` and `diary/LOG.md` (section 3).
6. Write `scripts/Invoke-WindowsBuild.ps1` (section 6.2) before you build anything. Leave out `-Smoke` for now; Phase 1 adds it together with `smoke.sh`.
7. Windows baseline: `pwsh -NoProfile -File scripts/Invoke-WindowsBuild.ps1 -Test`. The deploy guard must pass. Record the test totals and the build warning count in NOTES.md.
8. Commit `LINUX_PLAN.md`, `diary/` and the build script: `Adds Linux support plan, work diary and a build script that skips deployment`.

### Phase 1: Test tooling and the Linux baseline

1. Add `.gitattributes` with the single line `*.sh text eol=lf`.
2. Write `scripts/test-linux.sh`, `scripts/smoke.sh` (checks 1 to 6 only; 7 and 8 come in Phase 6), `scripts/wsl/setup-root.sh`, `scripts/wsl/setup-user.sh`, `scripts/wsl/Install-TestDistro.ps1`, `scripts/wsl/Invoke-LinuxTests.ps1` and `scripts/wsl/Stop-TestDistro.ps1`.
3. Add the `-Smoke` switch to `scripts/Invoke-WindowsBuild.ps1`. Confirm that every `.sh` file is LF only: in PowerShell, `[IO.File]::ReadAllBytes("<path>") -contains 13` must be `False`; in bash, `grep -c $'\r' <path>` must print `0`.
4. Run the Windows smoke against the unchanged code: `pwsh -NoProfile -File scripts/Invoke-WindowsBuild.ps1 -Test -Smoke`. Checks 1 to 6 must pass. This is the Windows smoke baseline.
5. Run `pwsh -NoProfile -File scripts/wsl/Install-TestDistro.ps1`. Record the versions it reports.
6. Run `pwsh -NoProfile -File scripts/wsl/Invoke-LinuxTests.ps1` without `-Smoke`. The build should succeed and many tests should fail. Record the failing tests, grouped by cause, in NOTES.md. This is the Linux baseline.
7. Confirm that WSL stopped: `wsl.exe --list --running` must not list `subordinate-linux`.
8. Commit: `Adds scripts for testing on Linux through WSL`.

### Phase 2: Platform abstraction with the Windows backend

This is a pure refactor. Windows behavior must not change in any way.

1. Before you move any code, add `tests/Subordinate.Tests/PlatformFacts.cs` with the four attributes from section 6.1. Then add a `[WindowsFact]` named `SessionIdentityIsStable` to `SessionStoreTests`. It asserts that `Path.GetFileName(new SessionStore(<any temp home>).PathsFor(@"D:\Work\Repo", "My Session").Directory)` equals `my-session-08c207a26afc`. Run it on the unchanged code. It must pass. If it does not, stop and ask the owner, because the value was computed from the current formula.
2. Create `src/Subordinate.Core/Platform/` with `IPlatform`, `IPathSemantics`, `IShellEnvironment`, `PlatformServices`, `UnsupportedPlatform`, `WindowsPlatform`, `WindowsPathSemantics` and `WindowsShellEnvironment`. Move the code verbatim. Add `ShellProbe`.
3. Turn `ShellPaths` and `ShellLocator` into facades, and update every call site in section 5.7 except the Linux-only parts.
4. In the CLI, add `IDetachedProcessLauncher`, move the existing code verbatim into `WindowsDetachedProcessLauncher`, turn `DetachedProcess` into the selector, and update `LaunchAsync` and `WorkerAsync`.
5. Add the unsupported platform check to `Program.Main`, and the empty-home check to `ResolveHome`.
6. Verify on Windows with `pwsh -NoProfile -File scripts/Invoke-WindowsBuild.ps1 -Test -Smoke`: the build has no new warnings; the test totals equal the baseline plus the new test, all passing; the smoke passes; the deploy guard passes.
7. Commit: `Adds platform abstraction with a Windows backend`.

### Phase 3: Linux paths

1. Add `LinuxPathSemantics` (lexical only for now) and `LinuxPlatform`. Add `LinuxShellEnvironment` with `SupportedShells` and the descriptions from section 5.3; for now its `Locate` and `ProbeVersion` throw `ShellUnavailable` with `Shell discovery on Linux is not implemented yet.`
2. Add `TestPaths` and `LinuxPathSemanticsTests` (the tables in section 5.2).
3. Split the existing tests:
   - In `PathAndPolicyTests`, `ResolvesShellPaths` and `ContainmentIsSegmentAwareAndCaseInsensitive` become `[WindowsTheory]`. `ConvertsWindowsPathToGitBashForm` becomes `[WindowsFact]` and calls `WindowsPathSemantics.ToBashPath`.
   - In `SessionStoreTests.SameNameInAnotherWorkdirIsADifferentSession`, keep the different-workdir assertion platform neutral. Move the uppercase-workdir assertion into a new `[WindowsFact]`, and add a `[LinuxFact]` asserting that two workdirs differing only in case get different session directories.
   - In `ClassificationCorpusTests`, the workdir becomes `TestPaths.Workdir`. Move the rows `cat D:/elsewhere/x.txt` and `cat D:/work/repo/src/a.cs` into a new `WindowsBashCorpus` (`[WindowsTheory]`). `PowerShellCorpus` becomes `[WindowsTheory]`. Add `LinuxBashCorpus` and `LinuxPwshCorpus` (`[LinuxTheory]`, the latter using `ShellKind.PowerShell7`) with the rows below.
   - In `ClassificationRegressionTests`, the workdir becomes `TestPaths.Workdir`.
   - Add a `[LinuxFact]` to `SystemPromptTests` asserting that the Linux bash prompt mentions `Linux` and does not mention `Git Bash`.
4. Verify on Windows with `pwsh -NoProfile -File scripts/Invoke-WindowsBuild.ps1 -Test -Smoke`: all tests pass, and the totals equal the previous totals plus the new tests. Verify on Linux with `pwsh -NoProfile -File scripts/wsl/Invoke-LinuxTests.ps1`: every classification, session and CLI test passes. Record any remaining failures and their causes.
5. Commit: `Adds Linux path handling`.

`LinuxBashCorpus` (command, policy, verdict, capability, human), with workdir `/work/repo`:

| Command | Policy | Verdict | Capability | Human |
| --- | --- | --- | --- | --- |
| `cat /work/repo/src/a.cs` | readonly | allow | read | |
| `cat /work/elsewhere/x.txt` | readonly | defer | read-outside | |
| `cat /work/REPO/src/a.cs` | readonly | defer | read-outside | |
| `ls /c/Users` | readonly | defer | read-outside | |
| `echo hi > nul` | readonly | defer | write | |
| `echo hi > nul` | edit | allow | write | |
| `cat 'src\a.cs'` | readonly | defer | read-outside | |
| `rm -rf /work/repo/build` | edit | allow | write | |
| `rm -rf /work/repo2` | edit | defer | write-outside | human |
| `cd /work/repo/src && ls` | readonly | allow | read | |

`LinuxPwshCorpus`:

| Command | Policy | Verdict | Capability | Human |
| --- | --- | --- | --- | --- |
| `Get-Content src/a.cs` | readonly | allow | read | |
| `Get-ChildItem src -Recurse` | readonly | allow | read | |
| `Set-Content out.txt 'x'` | edit | allow | write | |
| `Remove-Item -Path ../y -Recurse` | edit | defer | write-outside | human |
| `Get-Content src\a.cs` | readonly | defer | read-outside | |
| `Get-Content env:SECRET` | readonly | defer | sensitive | human |
| `Invoke-WebRequest https://example.invalid` | edit | defer | network | human |

If a row fails, first work out whether the code or the row is wrong. A row that would let a write or read outside the workdir through without approval points to a bug in the code, not in the row.

### Phase 4: Linux shells

1. Implement `LinuxShellEnvironment.Locate` and `ProbeVersion` (section 5.3), with the execute-bit check.
2. Make `Doctor` use `SupportedShells`.
3. Add Linux tests (`[LinuxFact]`):
   - `LocatesSystemBash`: the path exists and the version starts with `5.2.` or `5.3.`.
   - `WindowsPowerShellIsUnavailableOnLinux`: `ShellUnavailable`.
   - `ConfiguredProgramThatIsNotBashIsRejected`: configure `/bin/true` and expect `ShellUnavailable`.
   - `VerifyUnchangedAcceptsTheLocatedBash`.
   - `ExecutorRunsInTheWorkingDirectory`: `ShellCommandExecutor` with the located bash runs `pwd` in a temp directory.
   - `ExecutorRemovesSecretVariables`: set `SUBORDINATE_SMOKE_TOKEN` in-process with `Environment.SetEnvironmentVariable` and restore it in `finally`. `printenv SUBORDINATE_SMOKE_TOKEN` must print nothing.
   - `ExecutorKillsOnTimeout`: `sleep 30` with a 1 second timeout reports `TimedOut` and finishes in under 10 seconds.
   - `PwshRunsWhenAvailable`: when `SUBORDINATE_TEST_PWSH` is not set, return early and write a line through `ITestOutputHelper` saying it was skipped. Otherwise locate pwsh through `ShellPathsEntry.Pwsh`, check the version is 7.6.x, and run `Get-Location`.
4. Verify on Windows and on Linux.
5. Commit: `Adds Linux shell discovery`.

### Phase 5: Linux build and deploy

1. Before you edit the project, record in NOTES.md the output of `dotnet msbuild src/Subordinate/Subordinate.csproj -getProperty:RuntimeIdentifiers -getProperty:SubordinateDeployDirectory`. This only evaluates the project and builds nothing. Then make the `Subordinate.csproj` changes from section 5.5.
2. Verify on Windows: `pwsh -NoProfile -File scripts/Invoke-WindowsBuild.ps1 -Test -Smoke` passes, including the deploy guard. Then check that the Windows evaluation is unchanged (these commands only evaluate the project, they build nothing): `dotnet msbuild src/Subordinate/Subordinate.csproj -getProperty:RuntimeIdentifiers` still prints `win-x64`, and `-getProperty:SubordinateDeployDirectory` prints the same directory as before your change.
3. Verify on Linux: `pwsh -NoProfile -File scripts/wsl/Invoke-LinuxTests.ps1`, then `wsl.exe -d subordinate-linux -u tester --exec bash -c 'test -x ~/.subordinate/bin/subordinate && ~/.subordinate/bin/subordinate help | head -n 1'` prints a JSON line with `"ok":true`. Run `Stop-TestDistro.ps1` afterwards.
4. Commit: `Adds Linux publish and deploy target`.

### Phase 6: Detached worker on Linux

1. Add `LinuxDetachedProcessLauncher` (section 5.4) and wire it into the selector.
2. Add checks 7 and 8 to `smoke.sh`.
3. Verify on Windows: build, tests, Windows smoke. Verify on Linux: `Invoke-LinuxTests.ps1 -Smoke` with every `SMOKE` line `PASS` (or `SKIP` for live).
4. Optional: one live run (section 6.7).
5. Commit: `Adds detached worker launch on Linux`.

### Phase 7: Linux hardening

1. Add the sensitive patterns from section 5.6 with plain `[Fact]` tests in `PathAndPolicyTests`. These must match: `cat /proc/1234/environ`, `cat /proc/self/environ`, `cat ~/.password-store/x.gpg`, `ls ~/.local/share/keyrings`, `cat /etc/shadow`. This must not: `cat /proc/cpuinfo`.
2. Add the `ps` rule, with platform-neutral corpus rows: `ps aux` readonly allow read; `ps -ef` readonly allow read; `ps -p 1 -o comm=` readonly allow read; `ps eww` readonly defer sensitive human; `ps auxe` readonly defer sensitive human.
3. Add `LinuxRealPaths` and the symlink check, with `[LinuxFact]` tests in a temp directory that contains `work/` and `outside/` with the link `work/escape -> ../outside`:
   - `IsInside(work/escape/x, work)` is false.
   - `IsInside(work/src/a.cs, work)` is true.
   - A workdir reached through a linked parent directory still contains its own files.
   - `cat escape/x`, classified with that workdir, needs `read-outside`.
   - `write_file` on `escape/x` needs `write-outside`.
4. Verify on Windows and on Linux.
5. Commit: `Hardens classification for Linux`.

### Phase 8: Documentation and final verification

1. Update `README.md`:
   - The first sentence says Windows and Linux.
   - The build section covers the Linux deploy path (`~/.subordinate/bin/subordinate`).
   - The supported shells table gets a platform column.
   - A short Linux section covers the bash 5.2/5.3 requirement, pwsh, case-sensitive paths, backslash paths failing closed, the symlink check and its limit, and that `powershell` is Windows only.
   - A short section on testing through WSL lists the scripts, explains that WSL is stopped after each run, and gives the owner-only removal command.
2. Write `LINUX_TESTING.md` for the friends who will test:
   - prerequisites: bash 5.2 or 5.3, the .NET 11 SDK version from NOTES.md, optionally pwsh 7.6.4 or later
   - building and deploying
   - running `bash scripts/test-linux.sh` and `bash scripts/smoke.sh --binary ~/.subordinate/bin/subordinate`
   - `subordinate init`, configuring a model, `subordinate doctor`
   - trying a real session driven by Claude Code
   - what to send back: `uname -a`, `bash --version`, the doctor JSON, the smoke output, and the exact command lines that misbehaved
3. Final verification:
   - Windows: build with no new warnings, all tests pass, smoke passes.
   - Linux: `pwsh -NoProfile -File scripts/wsl/Invoke-LinuxTests.ps1 -Live`, with everything passing, including the live approval round trip.
   - WSL is stopped and the default distro is unchanged.
4. Bring the diary up to date: the final state, test totals for both platforms, and open issues.
5. Commit: `Documents Linux support`. Then `git status` must show a clean tree.
6. Report to the owner: what changed, test totals on both platforms, every deviation from this plan, open issues, and how the friends should start (`LINUX_TESTING.md`).

## 8. Done criteria

- Windows: all tests pass, with the baseline count plus the new tests; no new build warnings; the smoke passes; `SessionIdentityIsStable` passes; the prompt strings are unchanged; the deploy guard never fired, and `%USERPROFILE%\.subordinate\bin` was never written.
- Linux: all tests pass, with Windows-only tests skipped; the smoke passes, including the detach checks and one live approval round trip.
- The owner's environment is untouched (section 2.3), and WSL is stopped.
- `README.md` and `LINUX_TESTING.md` are written.
- There is one commit per phase on `cross-platform`, and the working tree is clean.

## 9. Out of scope

- macOS, BSD, and musl-based distributions such as Alpine. They may work, but nothing is tested or promised.
- Changing the bash version policy.
- The `subordinate` skill under `~/.claude/skills`. It hardcodes the Windows path; the owner handles it.
- Command database changes beyond the `ps` rule.
- Case-insensitive command word matching (`NormalizeProgram` lowercases program names).
- Symbolic links and junctions on Windows.
- Pushing, pull requests and merging.

## 10. When to stop and ask the owner

- `git status` shows unexpected changes in Phase 0.
- A WSL step needs interaction, elevation or a reboot, or `wsl --install` fails.
- A step would change the owner's environment in any way listed in section 2.3.
- A Windows test fails after a step that was meant to be a pure refactor, and the cause is not clear.
- A security expectation in this plan looks wrong to you.
- The code differs from what this plan describes in a way that changes the design.

Write the question in `diary/TODO.md` under `Questions for the owner` before you ask, so it survives a compaction.
