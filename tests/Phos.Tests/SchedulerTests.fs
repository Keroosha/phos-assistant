module Phos.Tests.SchedulerTests

open System
open System.Threading.Tasks
open Xunit
open FsUnit.Xunit
open Microsoft.Extensions.Logging.Abstractions
open Phos.Scheduler
open Phos.Storage
open Phos.Core.DomainTypes
open Phos.Core.ScheduleJobs
open Phos.Core.SchedulePolicy

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

let private options: SchedulerOptions =
    { TickSeconds = 15
      PendingTtlHours = 24
      MaxFailedTicks = 3 }

let private mkJob (id: int64) : ScheduleJob =
    { Id = id
      UserId = UserId 1L
      ChatId = ChatId 1L
      Prompt = "prompt"
      CronExpr = None
      IntervalSeconds = Some 60
      AfterSeconds = None
      Timezone = "UTC"
      Catchup = SkipMissed
      Status = ScheduleStatus.Active
      NextRun = Some(DateTimeOffset.UtcNow)
      LastRunAt = None
      LastError = None
      CreatedAt = DateTimeOffset.UtcNow
      UpdatedAt = DateTimeOffset.UtcNow }

let private mkClaim (commandId: int64) : ScheduleRunClaim =
    { Job = mkJob commandId
      ScheduledFor = DateTimeOffset.UtcNow
      CommandId = commandId }

/// Fake `IScheduleJobRepository`. Only `ExpirePending` and `ClaimDueOccurrence`
/// are exercised; the remaining members throw if called.
type FakeScheduleJobRepository() =
    let claims = ResizeArray<ScheduleRunClaim>()
    let expireCalls = ResizeArray<DateTimeOffset * DateTimeOffset>()
    let mutable throwOnClaim = 0

    member _.QueueClaim(c: ScheduleRunClaim) = claims.Add c
    member _.ExpireArgs = List.ofSeq expireCalls
    member _.ThrowOnClaim(n: int) = throwOnClaim <- n

    interface IScheduleJobRepository with
        member _.Insert (_: ScheduleJobDraft) (_: string option) : Task<ScheduleJob> =
            Task.FromResult(Unchecked.defaultof<ScheduleJob>)

        member _.FindByToolCallId(_: string) : Task<ScheduleJob option> = Task.FromResult None
        member _.GetById(_: int64) : Task<ScheduleJob option> = Task.FromResult None
        member _.ListForUser(_: UserId) : Task<ScheduleJob list> = Task.FromResult []
        member _.CountActiveForUser(_: UserId) : Task<int> = Task.FromResult 0
        member _.Confirm(_: int64) : Task<unit> = Task.FromResult(())
        member _.Cancel(_: int64) : Task<unit> = Task.FromResult(())
        member _.Pause(_: int64) : Task<unit> = Task.FromResult(())
        member _.Resume(_: int64) : Task<unit> = Task.FromResult(())
        member _.Remove(_: int64) : Task<unit> = Task.FromResult(())
        member _.RunNow(_: int64) : Task<unit> = Task.FromResult(())

        member _.ExpirePending (now: DateTimeOffset) (olderThan: DateTimeOffset) : Task<int> =
            expireCalls.Add(now, olderThan)
            Task.FromResult 0

        member _.PauseDueToErrors (_: int64) (_: string) : Task<unit> = Task.FromResult(())

        member _.ClaimDueOccurrence(_: DateTimeOffset) : Task<ScheduleRunClaim option> =
            if throwOnClaim > 0 then
                throwOnClaim <- throwOnClaim - 1
                Task.FromException<ScheduleRunClaim option>(exn "boom")
            elif claims.Count > 0 then
                let c = claims.[0]
                claims.RemoveAt 0
                Task.FromResult(Some c)
            else
                Task.FromResult None

// ---------------------------------------------------------------------------
// RunOnce
// ---------------------------------------------------------------------------

[<Fact>]
let ``run once expires stale pending and claims due occurrences`` () =
    task {
        let at = DateTimeOffset.UtcNow
        let repo = FakeScheduleJobRepository()
        repo.QueueClaim(mkClaim 1L)
        repo.QueueClaim(mkClaim 2L)
        let mutable wakes = 0

        let svc =
            new SchedulerService(repo, (fun () -> wakes <- wakes + 1), options, NullLogger.Instance)

        let! n = svc.RunOnce at
        n |> should equal 2
        wakes |> should equal 2
        repo.ExpireArgs |> should haveLength 1
        let expNow, older = repo.ExpireArgs |> List.head
        expNow |> should equal at
        older |> should equal (at.AddHours -24.0)
    }

[<Fact>]
let ``run once stops on no claims`` () =
    task {
        let at = DateTimeOffset.UtcNow
        let repo = FakeScheduleJobRepository()
        let mutable wakes = 0

        let svc =
            new SchedulerService(repo, (fun () -> wakes <- wakes + 1), options, NullLogger.Instance)

        let! n = svc.RunOnce at
        n |> should equal 0
        wakes |> should equal 0
    }

[<Fact>]
let ``run once survives exception and continues`` () =
    task {
        let at = DateTimeOffset.UtcNow
        let repo = FakeScheduleJobRepository()
        repo.QueueClaim(mkClaim 1L)
        repo.ThrowOnClaim 1
        let mutable wakes = 0

        let svc =
            new SchedulerService(repo, (fun () -> wakes <- wakes + 1), options, NullLogger.Instance)

        let! n1 = svc.RunOnce at
        n1 |> should equal 0
        wakes |> should equal 0
        svc.ConsecutiveFailures |> should equal 1
        // The throw is one-shot: the next scan succeeds and claims the queued item.
        let! n2 = svc.RunOnce at
        n2 |> should equal 1
        wakes |> should equal 1
        svc.ConsecutiveFailures |> should equal 0
    }

[<Fact>]
let ``run once resets failure counter after success`` () =
    task {
        let at = DateTimeOffset.UtcNow
        let repo = FakeScheduleJobRepository()
        repo.ThrowOnClaim 2
        let mutable wakes = 0

        let svc =
            new SchedulerService(repo, (fun () -> wakes <- wakes + 1), options, NullLogger.Instance)

        let! _ = svc.RunOnce at
        let! _ = svc.RunOnce at
        svc.ConsecutiveFailures |> should equal 2
        let! n = svc.RunOnce at
        n |> should equal 0
        svc.ConsecutiveFailures |> should equal 0
    }

// ---------------------------------------------------------------------------
// Delay backoff
// ---------------------------------------------------------------------------

[<Fact>]
let ``delaySeconds backs off after max failed ticks`` () =
    SchedulerDelay.delaySeconds options 0 |> should equal 15
    SchedulerDelay.delaySeconds options 2 |> should equal 15
    SchedulerDelay.delaySeconds options 3 |> should equal 30
    SchedulerDelay.delaySeconds options 5 |> should equal 30
