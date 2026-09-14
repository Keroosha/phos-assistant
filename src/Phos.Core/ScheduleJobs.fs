module Phos.Core.ScheduleJobs

open System
open Phos.Core.DomainTypes
open Phos.Core.SchedulePolicy
open Cronos

/// Lifecycle of a schedule job.
[<RequireQualifiedAccess>]
type ScheduleStatus =
    | Pending
    | Active
    | Paused
    | Cancelled
    | Expired
    | Completed

/// A persisted schedule job (the Core-side model; storage lives in Phos.Storage).
type ScheduleJob =
    { Id: int64
      UserId: UserId
      ChatId: ChatId
      Prompt: string
      CronExpr: string option
      IntervalSeconds: int option
      AfterSeconds: int option
      Timezone: string
      Catchup: CatchupPolicy
      Status: ScheduleStatus
      NextRun: DateTimeOffset option
      LastRunAt: DateTimeOffset option
      LastError: string option
      CreatedAt: DateTimeOffset
      UpdatedAt: DateTimeOffset }

/// Input for creating a schedule job (before an id/status/timestamps exist).
type ScheduleJobDraft =
    { UserId: UserId
      ChatId: ChatId
      Prompt: string
      CronExpr: string option
      IntervalSeconds: int option
      AfterSeconds: int option
      Timezone: string
      Catchup: CatchupPolicy }

/// Per-user scheduling quotas.
type ScheduleQuota =
    { MaxJobsPerUser: int
      MinIntervalSeconds: int
      MaxPromptLength: int }

let private tryResolveTimezone (tzName: string) : TimeZoneInfo option =
    try
        Some(TimeZoneInfo.FindSystemTimeZoneById tzName)
    with _ ->
        None

/// Validates a draft against the quota. Pure; no IO.
///
/// Rules: prompt non-blank and within `MaxPromptLength`; exactly one of
/// `CronExpr`/`IntervalSeconds`/`AfterSeconds`; `AfterSeconds >= 1`; the chosen
/// schedule expression is well-formed and within the minimum interval; the
/// timezone is a known IANA zone.
let validate (quota: ScheduleQuota) (draft: ScheduleJobDraft) : Result<ScheduleJobDraft, string> =
    if String.IsNullOrWhiteSpace draft.Prompt then
        Error "поле prompt не может быть пустым"
    elif draft.Prompt.Length > quota.MaxPromptLength then
        Error(sprintf "поле prompt: длина %d превышает максимум %d" draft.Prompt.Length quota.MaxPromptLength)
    else
        match draft.CronExpr, draft.IntervalSeconds, draft.AfterSeconds with
        | Some _, Some _, _
        | Some _, _, Some _
        | _, Some _, Some _ -> Error "укажите ровно один из cron_expr, interval_seconds или after_seconds"
        | None, None, None -> Error "укажите ровно один из cron_expr, interval_seconds или after_seconds"
        | Some c, None, None ->
            match tryResolveTimezone draft.Timezone with
            | None -> Error(sprintf "поле timezone: неизвестная таймзона '%s'" draft.Timezone)
            | Some _ ->
                try
                    CronExpression.Parse(c) |> ignore
                    Ok draft
                with _ ->
                    Error(sprintf "поле cron_expr: некорректное cron-выражение '%s'" c)
        | None, Some s, None ->
            match tryResolveTimezone draft.Timezone with
            | None -> Error(sprintf "поле timezone: неизвестная таймзона '%s'" draft.Timezone)
            | Some _ ->
                if s < quota.MinIntervalSeconds then
                    Error(sprintf "поле interval_seconds: %d меньше минимального %d" s quota.MinIntervalSeconds)
                else
                    Ok draft
        | None, None, Some a ->
            if a < 1 then
                Error "after_seconds должен быть >= 1"
            else
                Ok draft

/// Computes the next `count` occurrences strictly after `after`.
///
/// For a cron schedule this re-parses the expression each call (acceptable: pure)
/// and delegates DST handling to `SchedulePolicy.nextOccurrences`. For an interval
/// schedule it returns equally-spaced steps of `interval` seconds. `count` is
/// clamped to 1..100.
let nextOccurrences
    (cron: string option)
    (interval: int option)
    (timezone: string)
    (after: DateTimeOffset)
    (count: int)
    : DateTimeOffset list =
    let count = max 1 (min 100 count)

    match cron, interval with
    | Some c, None ->
        let tz = TimeZoneInfo.FindSystemTimeZoneById timezone
        let parsed = CronExpression.Parse(c)
        SchedulePolicy.nextOccurrences parsed tz after count
    | None, Some s -> [ 1..count ] |> List.map (fun k -> after.AddSeconds(float (k * s)))
    | _ -> []

/// Computes the single next run for a draft strictly after `now`.
///
/// Cron/interval schedules delegate to `nextOccurrences`; a one-shot
/// (`AfterSeconds`) fires exactly `AfterSeconds` seconds from `now`.
let nextRunAfter (draft: ScheduleJobDraft) (now: DateTimeOffset) : DateTimeOffset option =
    match draft.CronExpr, draft.IntervalSeconds with
    | Some _, _
    | _, Some _ ->
        nextOccurrences draft.CronExpr draft.IntervalSeconds draft.Timezone now 1
        |> List.tryHead
    | None, None -> draft.AfterSeconds |> Option.map (fun a -> now.AddSeconds(float a))

/// Renders occurrences one per line: `"2026-09-14 09:00 UTC (11:00 Europe/Berlin)"`.
/// The UTC label is fixed; the local time is shown in the job's timezone.
/// An empty list renders as "".
let formatOccurrences (timezone: string) (occurrences: DateTimeOffset list) : string =
    let tz = TimeZoneInfo.FindSystemTimeZoneById timezone

    occurrences
    |> List.map (fun dto ->
        let local = TimeZoneInfo.ConvertTimeFromUtc(dto.UtcDateTime, tz)

        sprintf "%s UTC (%s %s)" (dto.UtcDateTime.ToString("yyyy-MM-dd HH:mm")) (local.ToString("HH:mm")) timezone)
    |> String.concat "\n"

let confirmStatus (status: ScheduleStatus) : Result<ScheduleStatus, string> =
    match status with
    | ScheduleStatus.Pending -> Ok ScheduleStatus.Active
    | _ -> Error "задание не в статусе pending"

let cancelStatus (status: ScheduleStatus) : Result<ScheduleStatus, string> =
    match status with
    | ScheduleStatus.Pending -> Ok ScheduleStatus.Cancelled
    | _ -> Error "отменить можно только pending-задание"

let pauseStatus (status: ScheduleStatus) : Result<ScheduleStatus, string> =
    match status with
    | ScheduleStatus.Active -> Ok ScheduleStatus.Paused
    | _ -> Error "пауза возможна только для active-задания"

let resumeStatus (status: ScheduleStatus) : Result<ScheduleStatus, string> =
    match status with
    | ScheduleStatus.Paused -> Ok ScheduleStatus.Active
    | _ -> Error "возобновить можно только paused-задание"

let expireStatus (status: ScheduleStatus) : Result<ScheduleStatus, string> =
    match status with
    | ScheduleStatus.Pending -> Ok ScheduleStatus.Expired
    | _ -> Error "истечь может только pending-задание"

let statusToString (status: ScheduleStatus) : string =
    match status with
    | ScheduleStatus.Pending -> "pending"
    | ScheduleStatus.Active -> "active"
    | ScheduleStatus.Paused -> "paused"
    | ScheduleStatus.Cancelled -> "cancelled"
    | ScheduleStatus.Expired -> "expired"
    | ScheduleStatus.Completed -> "completed"

/// Case-insensitive parse of a status string; `None` for unknown values.
let statusOfString (s: string) : ScheduleStatus option =
    match s.ToLowerInvariant() with
    | "pending" -> Some ScheduleStatus.Pending
    | "active" -> Some ScheduleStatus.Active
    | "paused" -> Some ScheduleStatus.Paused
    | "cancelled" -> Some ScheduleStatus.Cancelled
    | "expired" -> Some ScheduleStatus.Expired
    | "completed" -> Some ScheduleStatus.Completed
    | _ -> None

/// Builds the `SchedulePolicy` that drives a job: catch-up per job, at most one
/// missed occurrence.
let duePolicy (catchup: CatchupPolicy) : Policy = { Catchup = catchup; MaxCatchUp = 1 }
