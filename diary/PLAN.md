Current: Phase 4, step 5.

# Plan

## Phase 0: Preparation
- [x] Read LINUX_PLAN.md, AGENTS.md and README.md, skim the section 4 files
- [x] Confirm the tree is clean apart from LINUX_PLAN.md
- [x] Confirm the commit message convention
- [x] Create the cross-platform branch
- [x] Create the diary files
- [x] Write scripts/Invoke-WindowsBuild.ps1 without -Smoke
- [x] Run the Windows baseline build and tests
- [x] Commit Adds Linux support plan, work diary and a build script that skips deployment

## Phase 1: Test tooling and the Linux baseline
- [x] Add .gitattributes with *.sh text eol=lf
- [x] Write scripts/test-linux.sh and scripts/smoke.sh checks 1 to 6
- [x] Write scripts/wsl/setup-root.sh, setup-user.sh, Install-TestDistro.ps1, Invoke-LinuxTests.ps1 and Stop-TestDistro.ps1
- [x] Add -Smoke to Invoke-WindowsBuild.ps1 and verify LF line endings
- [x] Run the Windows smoke baseline
- [x] Run Install-TestDistro.ps1 and record the versions
- [x] Run the Linux baseline tests and record the failures
- [x] Confirm WSL stopped
- [x] Commit Adds scripts for testing on Linux through WSL

## Phase 2: Platform abstraction with the Windows backend
- [x] Add PlatformFacts.cs and the SessionIdentityIsStable Windows test
- [x] Create Platform/ with the interfaces, PlatformServices, UnsupportedPlatform, WindowsPlatform, WindowsPathSemantics, WindowsShellEnvironment and ShellProbe
- [x] Turn ShellPaths and ShellLocator into facades and update the call sites
- [x] Add IDetachedProcessLauncher and WindowsDetachedProcessLauncher
- [x] Add the unsupported platform check to Program.Main and the empty-home check to ResolveHome
- [x] Verify on Windows
- [x] Commit Adds platform abstraction with a Windows backend

## Phase 3: Linux paths
- [x] Add LinuxPathSemantics, LinuxPlatform and the LinuxShellEnvironment stub
- [x] Add TestPaths and LinuxPathSemanticsTests
- [x] Split the existing tests and add the Linux corpora
- [x] Add the Linux system prompt test
- [x] Verify on Windows and Linux
- [x] Commit Adds Linux path handling

## Phase 4: Linux shells
- [x] Implement LinuxShellEnvironment.Locate and ProbeVersion
- [x] Make Doctor use SupportedShells
- [x] Add the Linux shell tests
- [x] Verify on Windows and Linux
- [ ] Commit Adds Linux shell discovery

## Phase 5: Linux build and deploy
- [ ] Record the Windows project evaluation in NOTES.md
- [ ] Add the Linux property group and DeployToSubordinateHomeLinux target
- [ ] Verify on Windows and Linux
- [ ] Commit Adds Linux publish and deploy target

## Phase 6: Detached worker on Linux
- [ ] Add LinuxDetachedProcessLauncher and wire the selector
- [ ] Add smoke checks 7 and 8
- [ ] Verify on Windows and Linux
- [ ] Optional live run
- [ ] Commit Adds detached worker launch on Linux

## Phase 7: Linux hardening
- [ ] Add the sensitive patterns and tests
- [ ] Add the ps rule and corpus rows
- [ ] Add LinuxRealPaths and the symlink check with tests
- [ ] Verify on Windows and Linux
- [ ] Commit Hardens classification for Linux

## Phase 8: Documentation and final verification
- [ ] Update README.md
- [ ] Write LINUX_TESTING.md
- [ ] Final verification on Windows and Linux with -Live
- [ ] Update the diary
- [ ] Commit Documents Linux support
- [ ] Report to the owner
