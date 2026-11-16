# Log

## 2026-11-16

- Phase 0: read LINUX_PLAN.md, AGENTS.md and README.md and skimmed the section 4 files.
- Phase 0: git status is clean apart from untracked LINUX_PLAN.md and the log matches the commit convention.
- Phase 0: created the cross-platform branch.
- Phase 0: wrote the diary and scripts/Invoke-WindowsBuild.ps1.
- Phase 0: Windows baseline passed. Build 0 warnings and 0 errors. Tests 223 passed, 0 failed, 0 skipped. Deploy guard passed.
- Phase 0: committed cc03455 Adds Linux support plan, work diary and a build script that skips deployment.
- Phase 1: started.
- Phase 1: added .gitattributes, scripts/test-linux.sh, scripts/smoke.sh, scripts/wsl/setup-root.sh, scripts/wsl/setup-user.sh, scripts/wsl/Stop-TestDistro.ps1, scripts/wsl/Install-TestDistro.ps1 and scripts/wsl/Invoke-LinuxTests.ps1.
- Phase 1: added -Smoke to scripts/Invoke-WindowsBuild.ps1. All shell scripts are LF and pass bash -n.
- Phase 1: Windows smoke baseline passed. Build 0 warnings, 223 tests passed, all 6 smoke checks passed, deploy guard passed.
- Phase 1: Install-TestDistro.ps1 completed with exit 0. bash 5.2.21, dotnet 11.0.100-preview.3.26207.106, pwsh 7.6.10.
- Phase 1: Linux baseline build succeeded. Tests: 223 total, 151 passed, 72 failed, 0 skipped. Failures are the expected Windows path and bash workdir cases. WSL stopped, docker-desktop remains the default.
