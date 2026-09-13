# Pinned WinMan Windows performance patch

PERF-001, PERF-002, PERF-022 and PERF-016 target only
`adb9f55b84b567db9f6e0ee8df4c33d11a12d91f`. The submodule gitlink remains
unchanged. All six changed dependency sources are delivered by the single
cumulative `PERF-001-recent-timer.patch` and normalized Git blob checks in
`manifest.json`; the historical patch filename is retained and the file is kept
LF for its SHA256 checksum.
The patch attribute permits the required space on empty unified-diff context
lines; the patch bytes and their manifest checksum are preserved.

From the repository root, before building:

```powershell
pwsh -NoProfile -File scripts/performance/Apply-DependencyPatches.ps1 -Mode Check
pwsh -NoProfile -File scripts/performance/Apply-DependencyPatches.ps1 -Mode Apply
```

Check reports clean applicable, already applied, or a conflict. Apply checks the
pinned commit, patch checksum and every affected file before mutation. Repeating
Apply is an idempotent verification; mixed/unknown local changes stop the script.
Neither command changes the index, gitlink or any unrelated dependency file.
Both CI workflows and the performance validation gate run this bootstrap.
An old checkout with only the earlier PERF-001 file pair applied is intentionally
a mixed state after this cumulative patch update; preserve it and apply the new
patch from a clean pinned dependency rather than overwriting unknown work.

```powershell
pwsh -NoProfile -File scripts/performance/Test-DependencyPatches.ps1 -SnapshotId YOUR-CANDIDATE
```

This archives the pinned dependencies into a new artifact directory, checks and
applies the patch, checks repeated application, and builds the actual archived
Release dependency project. It never resets or checks out the live submodule.
The archive's empty Directory.Build.props isolates Git-version metadata; the
production package, SDK, framework and source generator versions are unchanged.

The existing recent-window observer owns its list and native timer state under
one stable lock. A coalesced message requests changes on the window's owner
thread. The 10 ms cadence, inclusive 500 ms retention and final expired-window
check are preserved. The 200 ms timer also retries failed native timer updates.
Shutdown wakes the message thread and releases timers and the receiver window
there; late messages cannot restart observation. No second scheduler is added.

`Win32VirtualDesktop.IsAlive` uses the concrete manager's locked current-list
membership query without materializing the public `Desktops` snapshot on every
read. The public snapshot API is unchanged. Membership retains the existing
wrapper equality: removing a wrapper and later adding a new wrapper with the same
GUID does not revive the old object. FancyWM's 250 ms full reconciliation and its
ordinary/M+S eventless-move behavior are unchanged.

Win32DisplayManager.Displays keeps its sorted provider snapshot current under the
existing display-list lock and refreshes it when the caller culture changes. Each
read still returns a fresh concrete list, preserving isolation, stable equal-key
ordering and current-culture ordering while removing the repeated LINQ sorting
pipeline.
The HWND must belong to the thread calling
[SetTimer](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-settimer).
[KillTimer](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-killtimer)
needs the original HWND/ID and does not remove queued WM_TIMER messages.
These contracts motivate owner-thread cleanup and the stale-message guard.
Controlled adapter tests cover scheduling, but actual HWND delivery, workspace
shutdown, delayed visibility and process WPR measurements remain E2E_PENDING.

PERF-016 isolates completed EventLoop callbacks in a non-inlined method so Debug
stack locals release their owners before the next idle wait. Queue, order,
exception notification and shutdown behavior are preserved. Native root evidence
and the candidate protocol are archived under FWM-WORKER-RETENTION-20260912.
