# Pause indexing while the user is gaming

**Goal:** When a full screen game, a presentation or a configured process is running, the agent stops indexing, releases OCR engines and keeps changes queued. It resumes after a short quiet time. Searches over the pipe keep working.

## Design

- `Recall.Core/Activity/IActivityMonitor` : `string? BusyReason()`, null when the user is not busy. Pure, cheap, polled.
- `WindowsActivityMonitor` : `SHQueryUserNotificationState` (busy, D3D full screen, presentation mode, full screen UWP app) plus a process name list from `RecallConfig.PauseProcesses`. Classification and name matching are separate pure functions, so they are tested without Windows state. Game Mode has no public query API, so it is not used; full screen detection covers games.
- `ActivityGate : IPauseGate` : polls the monitor. Busy pauses at once. Idle resumes only after `ResumeDelay` of continuous idle. `WaitWhilePausedAsync(ct)` blocks while paused. `Changed` event reports transitions.
- `IndexRunner` takes an optional `IPauseGate`. It waits before each new root, before each new file and before each embedded image OCR. Files already in a worker finish.
- `ChangeQueue` takes an optional `Func<bool> isPaused`. While paused it keeps paths and does not flush. `Resume()` re-arms the timer.
- `ReleasableOcr : IOcr` creates the inner OCR lazily and drops it on `Release()` (after in-flight calls end).
- `AgentHost` wires it behind `AgentOptions.Activity` (null means never pause, so existing tests are unaffected). `Program` passes the Windows monitor when `RecallConfig.PauseWhenBusy` is true. Transitions go to `recall.log`. The status op is not extended because `Protocol.cs` belongs to another session.

## Tasks (test first)

1. `ActivityGateTests`: pause on busy, resume after delay, flap does not resume, waiter released.
2. `WindowsActivityMonitorTests`: state classification, process matching.
3. `IndexRunnerPauseTests`: paused run indexes nothing, resumes and finishes.
4. `ChangeQueueTests`: no flush while paused, flush after `Resume()`.
5. `ReleasableOcrTests`: lazy create, release recreates, release waits for in-flight.
6. `AgentHostTests`: busy monitor blocks indexing and search still works, idle monitor lets it finish.
7. Config fields, roadmap update.
