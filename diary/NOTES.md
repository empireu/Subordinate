# Notes

## Environment
- Repository: D:\repos\Subordinate
- Branch: cross-platform
- Windows 11 Pro, x64.
- .NET SDK used by the repository: 11.0.100-preview.3.26207.106.
- WSL 2.6.3. Before setup the only distro is docker-desktop, which must not be touched.
- Git Bash: C:\Program Files\Git\bin\bash.exe.
- PowerShell 7 is pwsh on PATH.

## Owner decisions
- Git is allowed for local operations only in this workspace.
- If the WSL install asks for elevation or a reboot, end the turn and report.

## WSL setup
- Distro: subordinate-linux, Ubuntu 24.04, user tester.
- bash: GNU bash 5.2.21(1)-release (x86_64-pc-linux-gnu).
- dotnet: 11.0.100-preview.3.26207.106.
- pwsh: 7.6.10.

## Baselines
- Windows baseline: build succeeded with 0 warnings and 0 errors. Tests: 223 passed, 0 failed, 0 skipped. The deploy guard passed.
- Windows smoke baseline: help, init, config, doctor, classify and failed-run all passed.
- Linux baseline: build succeeded. Tests: 223 total, 151 passed, 72 failed, 0 skipped.
- The 72 Linux failures fall into two expected groups. Six ResolvesShellPaths cases expect Windows drive paths. The other 66 are classification, corpus, regression, resume and lifecycle tests that hit the bash workdir rejection or the hardcoded D:\work\repo workdir.
- Bash workdir rejection: ShellClassifier.Parse calls ShellPaths.ToBashPath, which returns null for every POSIX workdir, so every bash command becomes unbounded or deferred. This explains the SessionResumeTests and SessionLifecycleRegressionTests failures with SessionHarness.
- Hardcoded Windows workdir: ClassificationCorpusTests and ClassificationRegressionTests use D:\work\repo. On Linux Path.GetFullPath resolves it against the test output directory, so relative paths look outside the workdir. The Windows PowerShell corpus rows fail for the same reason.
- No unexpected failures. WSL was stopped after the run and docker-desktop is still the default distro.

## Phase 2
- Platform abstraction added with the Windows backend. Windows build 0 warnings, 224 tests passed, 6 smoke checks passed, deploy guard passed.
- Sequencing deviation: the ConvertsWindowsPathToGitBashForm test now calls WindowsPathSemantics.ToBashPath in Phase 2 so the build stays green. Phase 3 only adds the Windows gating.
- The Windows prompt and tool description strings are unchanged.

## Phase 3
- Windows result after the Linux path work: build 0 warnings, 251 passed, 4 skipped, 255 total. The skipped tests are the Linux-only ones.
- First Linux run: 2 failures, the two backslash corpus rows. The ShellSyntaxTree parser normalizes backslashes in path values before ShellPaths.Resolve sees them, so the paths looked inside the workdir.
- Fix: ShellClassifier.TryGetPathCandidates now checks the raw argument on Linux and returns an Unknown path when it contains a backslash. Windows behavior is unchanged. This restores the fail-closed rule from section 5.2.
- Final Linux result: build succeeded, 229 passed, 0 failed, 7 skipped, 236 total. WSL stopped after the run.

## Phase 4
- Linux shell discovery implemented with the execute-bit check in ShellProbe.FindOnPath. Windows result: build 0 warnings, 251 passed, 12 skipped, 263 total.
- Final Linux result: build succeeded, 237 passed, 0 failed, 7 skipped, 244 total. WSL stopped.

## Phase 5
- Before the project change, Windows evaluation: RuntimeIdentifiers=win-x64, SubordinateDeployDirectory=C:\Users\alioth\.subordinate\bin\.
- After the change, the Windows evaluation is unchanged: RuntimeIdentifiers=win-x64, SubordinateDeployDirectory=C:\Users\alioth\.subordinate\bin\.
- Windows verification: build 0 warnings, 251 passed, 12 skipped, all 6 smoke checks passed, deploy guard passed.
- Linux verification: build succeeded, 237 passed, 0 failed, 7 skipped, 244 total. Deployed to /home/tester/.subordinate/bin/. The deployed binary is executable and help prints ok:true. WSL stopped.

## Phase 6 (in progress)
- LinuxDetachedProcessLauncher starts the worker with redirected pipes, closes the parent ends, disposes the Process and returns the Id. DetachCurrentProcess calls setsid and keeps a PosixSignalRegistration for SIGHUP in a static field.
- DetachedProcess.Select now returns the Linux launcher on Linux.
- smoke.sh accepts --live-config and has check 7 (detach) and check 8 (live).
- Check 7 runs the start pipeline under timeout 30 via bash -c, expects under 15 seconds, checks the pid is alive, sid equals pid, tty is ?, resume exits 5, stop exits 0 and wait reports stopped.
- Check 8 copies the live config to a second home with shells set to {}, runs the readonly ls prompt and the approval round trip, and checks hello.txt.
- Windows verification: build 0 warnings, 251 passed, 12 skipped, 6 smoke passes, detach and live skipped as expected, deploy guard passed.
- First Linux smoke run: all checks passed except detach. The final wait reported run_failed instead of stopped.
- Cause: the stop request cancelled the model call, but the provider wrapped the cancellation as a SubordinateException with agent_error, so the runner's SubordinateException catch set failed.
- Fix: SessionRunner.RunAsync now checks stopSource.IsCancellationRequested in the SubordinateException catch and reports Stopped. The change is platform neutral.
- Windows re-verification after the stop fix: build 0 warnings, 251 passed, 12 skipped, 6 smoke passes, deploy guard passed.
- The first stop fix did not take effect. AgentRunner swallows the cancellation as FinishError and returns CompletedWithError, so the SessionRunner SubordinateException catch never runs.
- Second fix: SessionRunner.RunCoreAsync returns Stopped when FinishError is set and the run token is cancelled.
- Windows re-verification after the second stop fix: build 0 warnings, 251 passed, 12 skipped, 6 smoke passes, deploy guard passed.
- Final Linux result after the second fix: build succeeded, 237 passed, 0 failed, 7 skipped, 244 total. SMOKE PASS detach. Live skipped without --live-config. WSL stopped.
- The optional Phase 6 live run was skipped. The mandatory live run is Phase 8.
- Phase 6 commit: 9a46ea7 Adds detached worker launch on Linux.

## Phase 7
- Sensitive patterns added: process environment (/proc/<pid>/environ), password store, keyring and system password file (/etc/shadow and /etc/gshadow).
- The ps refine reads the raw clause elements after the program word. The parser reports ps aux and ps eww as two command words, so they are absent from the argument list.
- LinuxRealPaths resolves the longest existing prefix with libc realpath and appends the remaining segments. IsInside on Linux requires the real path to be inside the real root.
- Subordinate.Core.csproj now sets AllowUnsafeBlocks for the LibraryImport source generator.
- The first Linux run failed during the build with a file lock on Subordinate.Core.deps.json. WSL was stopped, the retry passed and the failure did not reproduce.
- Windows verification: build 0 warnings, 258 passed, 17 skipped, 275 total, 6 smoke passes, deploy guard passed.
- Linux verification: build succeeded, 249 passed, 0 failed, 7 skipped, 256 total. All 7 smoke checks passed and live was skipped. WSL stopped.
- Limit for the README: links created by an earlier part of the same command are not visible at classification time. The classifier is a deterministic floor, not a sandbox.

## Phase 8
- README.md now covers Windows and Linux, the platform column in the shell table, the Linux path and symlink rules, and the WSL test scripts.
- LINUX_TESTING.md is written for the external testers.
- The live smoke check initially failed because pending.json is indented and the smoke script matched only compact "id":"..." text. The pattern now accepts whitespace around the colon.
- Final Windows verification: build 0 warnings, 258 passed, 17 skipped, 275 total, 6 smoke passes, deploy guard passed.
- Final Linux verification: build succeeded, 249 passed, 0 failed, 7 skipped, 256 total. All 8 smoke checks passed, including the live approval round trip. WSL stopped and docker-desktop remains the default.
- Open issues: none.

## Commits
- Phase 0: cc03455 Adds Linux support plan, work diary and a build script that skips deployment
- Phase 1: 1f02022 Adds scripts for testing on Linux through WSL
- Phase 2: ce5fa9b Adds platform abstraction with a Windows backend
- Phase 3: 47d88f5 Adds Linux path handling
- Phase 4: d1aa5e6 Adds Linux shell discovery
- Phase 5: 3a0e6f6 Adds Linux publish and deploy target
- Phase 6: 9a46ea7 Adds detached worker launch on Linux
- Phase 7: 16c6dec Hardens classification for Linux

## Decisions
- None yet.

## Surprises
- None yet.
