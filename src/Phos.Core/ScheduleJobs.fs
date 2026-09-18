module Phos.Core.ScheduleJobs

open System
open System.Globalization
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
      /// Absolute one-time occurrence, normalized to UTC.
      RunAt: DateTimeOffset option
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
      /// Absolute one-time occurrence, normalized to UTC.
      RunAt: DateTimeOffset option
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

let private localRunAtFormats =
    [| "yyyy-MM-dd'T'HH':'mm"
       "yyyy-MM-dd'T'HH':'mm':'ss"
       "yyyy-MM-dd'T'HH':'mm':'ss.FFFFFFF" |]

let private offsetRunAtFormats =
    [| "yyyy-MM-dd'T'HH':'mmzzz"
       "yyyy-MM-dd'T'HH':'mm':'sszzz"
       "yyyy-MM-dd'T'HH':'mm':'ss.FFFFFFFzzz" |]

let private hasExplicitOffset (value: string) =
    let value = value.Trim()
    let separator = value.IndexOf('T')

    if separator < 0 then
        false
    else
        value.EndsWith("Z", StringComparison.OrdinalIgnoreCase)
        || value.LastIndexOf('+') > separator
        || value.LastIndexOf('-') > separator

let private invalidLocalRunAt (value: string) (timezone: string) =
    Error(sprintf "поле run_at: локальное время '%s' не существует в таймзоне '%s'" value timezone)

let private isDateOnly (value: string) =
    let mutable parsed = DateTime.MinValue
    DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, &parsed)

/// Parses an ISO-8601 one-time occurrence in a timezone.
///
/// A value without an offset is interpreted as a local wall-clock time. The
/// existing `resolveLocal` policy rejects spring-forward gaps and chooses the
/// standard-time offset for fall-back ambiguity. Values with an explicit `Z` or
/// offset are normalized to UTC, while the selected timezone remains available
/// for display. Date-only input is rejected so the caller can ask for a time.
let parseRunAt (timezone: string) (value: string) : Result<DateTimeOffset, string> =
    match tryResolveTimezone timezone with
    | None -> Error(sprintf "поле timezone: неизвестная таймзона '%s'" timezone)
    | Some tz ->
        let value = value.Trim()

        if String.IsNullOrWhiteSpace value then
            Error "поле run_at не может быть пустым"
        elif isDateOnly value then
            Error "поле run_at должно содержать точное время (например, 2026-10-02T09:00), а не только дату"
        elif hasExplicitOffset value then
            let normalized =
                if value.EndsWith("Z", StringComparison.OrdinalIgnoreCase) then
                    value.Substring(0, value.Length - 1) + "+00:00"
                else
                    value

            let mutable parsed = DateTimeOffset.MinValue

            if
                DateTimeOffset.TryParseExact(
                    normalized,
                    offsetRunAtFormats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    &parsed
                )
            then
                Ok(parsed.ToUniversalTime())
            else
                Error "поле run_at должно быть датой и временем ISO-8601, например 2026-10-02T09:00"
        else
            let mutable local = DateTime.MinValue

            if
                DateTime.TryParseExact(
                    value,
                    localRunAtFormats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    &local
                )
            then
                match resolveLocal tz local with
                | Some resolved -> Ok(resolved.ToUniversalTime())
                | None -> invalidLocalRunAt value timezone
            else
                Error "поле run_at должно быть датой и временем ISO-8601, например 2026-10-02T09:00"

/// Alias that makes the parsing intent explicit at call sites.
let tryParseRunAt = parseRunAt

let private validateTimezone (draft: ScheduleJobDraft) : Result<unit, string> =
    match tryResolveTimezone draft.Timezone with
    | None -> Error(sprintf "поле timezone: неизвестная таймзона '%s'" draft.Timezone)
    | Some _ -> Ok()

/// Validates a draft against the quota. Pure; no IO.
///
/// Rules: prompt non-blank and within `MaxPromptLength`; exactly one of
/// `RunAt`/`CronExpr`/`IntervalSeconds`/`AfterSeconds`; `AfterSeconds >= 1`;
/// the chosen schedule expression is well-formed and within the minimum
/// interval; and the timezone is a known IANA zone.
let validate (quota: ScheduleQuota) (draft: ScheduleJobDraft) : Result<ScheduleJobDraft, string> =
    if String.IsNullOrWhiteSpace draft.Prompt then
        Error "поле prompt не может быть пустым"
    elif draft.Prompt.Length > quota.MaxPromptLength then
        Error(sprintf "поле prompt: длина %d превышает максимум %d" draft.Prompt.Length quota.MaxPromptLength)
    else
        match draft.RunAt, draft.CronExpr, draft.IntervalSeconds, draft.AfterSeconds with
        | Some _, None, None, None ->
            match validateTimezone draft with
            | Error e -> Error e
            | Ok() -> Ok { draft with RunAt = draft.RunAt |> Option.map (fun value -> value.ToUniversalTime()) }
        | None, Some c, None, None ->
            match validateTimezone draft with
            | Error e -> Error e
            | Ok() ->
                try
                    CronExpression.Parse(c) |> ignore
                    Ok draft
                with _ ->
                    Error(sprintf "поле cron_expr: некорректное cron-выражение '%s'" c)
        | None, None, Some s, None ->
            match validateTimezone draft with
            | Error e -> Error e
            | Ok() ->
                if s < quota.MinIntervalSeconds then
                    Error(sprintf "поле interval_seconds: %d меньше минимального %d" s quota.MinIntervalSeconds)
                else
                    Ok draft
        | None, None, None, Some a ->
            match validateTimezone draft with
            | Error e -> Error e
            | Ok() ->
                if a < 1 then
                    Error "after_seconds должен быть >= 1"
                else
                    Ok draft
        | _ -> Error "укажите ровно один из run_at, cron_expr, interval_seconds или after_seconds"

/// Validates a draft and rejects an absolute one-time occurrence that is not
/// strictly in the future relative to `now`.
let validateAt
    (quota: ScheduleQuota)
    (now: DateTimeOffset)
    (draft: ScheduleJobDraft)
    : Result<ScheduleJobDraft, string> =
    match validate quota draft with
    | Error e -> Error e
    | Ok valid ->
        match valid.RunAt with
        | Some runAt when runAt <= now ->
            Error "поле run_at должно указывать время в будущем"
        | _ -> Ok valid


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
/// (`RunAt` or `AfterSeconds`) uses its absolute occurrence or fires exactly
/// `AfterSeconds` seconds from `now`.
let nextRunAfter (draft: ScheduleJobDraft) (now: DateTimeOffset) : DateTimeOffset option =
    match draft.RunAt with
    | Some runAt -> Some(runAt.ToUniversalTime())
    | None ->
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
