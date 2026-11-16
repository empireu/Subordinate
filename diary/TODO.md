# TODO

Current: Phase 7, step 4 (verify on Windows and Linux).

## State
- Phase 7 code and tests are written.
- Sensitive patterns added: process environment, password store, keyring, system password file.
- The ps rule reads the raw clause elements after the program word because ps aux is parsed as two command words.
- LinuxRealPaths added and LinuxPathSemantics.IsInside checks the real path on Linux.
- Subordinate.Core.csproj has AllowUnsafeBlocks for LibraryImport.
- Windows: build 0 warnings, 258 passed, 17 skipped, 275 total, 6 smoke passes, deploy guard passed.
- Linux verification is next.

## Next actions
1. Run pwsh -NoProfile -File scripts/wsl/Invoke-LinuxTests.ps1 -Smoke.
2. Fix any Linux failures.
3. Update NOTES and LOG with the results.
4. Commit Phase 7 with the subject Hardens classification for Linux.

## Questions for the owner
- None.
