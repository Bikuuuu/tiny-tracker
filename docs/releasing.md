# Releasing

1. **Check main.** CI is green, and the [manual checklist](manual-test-checklist.md), up to its After publishing part, passed on a real PC with a Setup.exe of this version. To get one, run Actions > Release > Run workflow with the version. A manual run builds, tests and keeps Setup.exe with the run for 7 days; it publishes nothing.
2. **Tag the commit on main** with plain numbers, and push the tag:
   ```powershell
   git tag v1.2.3
   git push origin v1.2.3
   ```
3. **Watch Actions > Release.** It runs CI, builds Setup.exe with the tag's version and runs the install test. Then it waits for you: open the run, choose **Review deployments**, tick `release` and approve. It publishes "Tiny Tracker 1.2.3" with Setup.exe under two names (`TinyTracker-Setup-1.2.3-x64.exe` and `TinyTracker-Setup-x64.exe`, for the README's download link), marked latest, with notes that list the merged pull requests.
4. **Check the release.**
   - It's marked Immutable, and the workflow's `verify` job passed.
   - Both files' SHA-256 are listed and the same, and `gh release verify-asset v1.2.3 TinyTracker-Setup-1.2.3-x64.exe` passes.
   - The README's download link (`releases/latest/download/TinyTracker-Setup-x64.exe`) downloads this version.
   - Edit the notes if needed. They stay editable; the file and the tag don't.
   - The checklist's [After publishing](manual-test-checklist.md#after-publishing) part passes: the previous version updates itself to this one.
5. **Submit Setup.exe** to [Microsoft's file submission portal](https://www.microsoft.com/wdsi/filesubmission) as a software developer, so SmartScreen and Defender learn it sooner.

If the workflow fails before it publishes, delete the draft release and the tag, fix the problem, and tag again. A published release's tag can't be used again, so a broken release is fixed with a new version.

If it published but the `verify` job failed:
- **Not immutable:** immutable releases were off. Delete the release and the tag, turn immutable releases on in the repository's settings, and tag again. A release that wasn't immutable doesn't lock its tag.
- **The file didn't verify:** rerun the `verify` job; it only checks, so it can run again. If it still fails, mark the release as a pre-release so it isn't the latest, and release a new version.
