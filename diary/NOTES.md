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

## Decisions
- None yet.

## Surprises
- None yet.
