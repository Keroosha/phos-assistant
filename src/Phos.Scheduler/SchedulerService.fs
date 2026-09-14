namespace Phos.Scheduler

open System
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Phos.Storage

[<assembly: InternalsVisibleTo("Phos.Tests")>]
[<assembly: InternalsVisibleTo("Phos.IntegrationTests")>]
do ()

/// Scheduler runtime knobs (bound by the App from the `Scheduler` config section).
type SchedulerOptions =
    { TickSeconds: int
      PendingTtlHours: int
      MaxFailedTicks: int }

/// Pure delay policy for the tick loop: after `MaxFailedTicks` consecutive
/// failed scans the loop backs off to twice the tick period. Kept as a free
/// function so the backoff rule is directly testable.
module internal SchedulerDelay =
    let delaySeconds (options: SchedulerOptions) (failures: int) : int =
        if failures >= options.MaxFailedTicks then
            options.TickSeconds * 2
        else
            options.TickSeconds

/// Background loop: expires stale pending jobs, claims due occurrences and
/// wakes the OMP worker for each enqueued command.
///
/// `MaxFailedTicks` guards the *scan* (DB trouble): after N consecutive failed
/// scans the error log stays at `Error` and the loop backs off. Jobs are NOT
/// auto-paused — a DB outage must not pause every user's jobs, they stay
/// durable and are retried. `PauseDueToErrors` remains available for explicit
/// manual use.
type SchedulerService(jobs: IScheduleJobRepository, wake: unit -> unit, options: SchedulerOptions, logger: ILogger) as this
    =
    inherit BackgroundService()

    let now () = DateTimeOffset.UtcNow
    let mutable consecutiveFailures = 0

    /// One scan: expire stale pending drafts, then claim due occurrences until
    /// none remain, waking after each claim. Returns the number of claims.
    member internal _.RunOnce(at: DateTimeOffset) : Task<int> =
        task {
            try
                let! _ = jobs.ExpirePending at (at.AddHours(float -options.PendingTtlHours))
                let mutable claimed = 0
                let mutable running = true

                while running do
                    match! jobs.ClaimDueOccurrence at with
                    | Some _ ->
                        wake ()
                        claimed <- claimed + 1
                    | None -> running <- false

                consecutiveFailures <- 0
                return claimed
            with ex ->
                consecutiveFailures <- consecutiveFailures + 1
                logger.LogError(ex, "scheduler scan failed ({N} consecutive)", consecutiveFailures)
                return 0
        }

    /// Test seam: the current consecutive-failure count (drives the backoff).
    member internal _.ConsecutiveFailures = consecutiveFailures

    override _.ExecuteAsync(stoppingToken: CancellationToken) : Task =
        task {
            while not stoppingToken.IsCancellationRequested do
                let! _ = this.RunOnce(now ())
                let delay = SchedulerDelay.delaySeconds options consecutiveFailures
                do! Task.Delay(TimeSpan.FromSeconds(float delay), stoppingToken)
        }
