module Phos.Core.SchedulePolicy

open System
open Cronos

/// How missed occurrences are handled.
type CatchupPolicy =
    | SkipMissed
    | CatchUpOnce

/// Schedule policy: catch-up behaviour and the maximum number of missed
/// occurrences that may be caught up.
type Policy =
    { Catchup: CatchupPolicy
      MaxCatchUp: int }

/// A concrete scheduled occurrence.
type Occurrence = { ScheduledFor: DateTimeOffset }

/// Deterministic key that uniquely identifies one occurrence of a job.
let occurrenceKey (jobId: int64) (scheduledFor: DateTimeOffset) : string =
    sprintf "%d:%d" jobId scheduledFor.UtcTicks

/// Decides whether a scheduled occurrence is due at `now`.
///
/// - `scheduled > now` (future) -> false.
/// - `lastRun >= scheduled` (already ran) -> false.
/// - missed (`scheduled < now` and no `lastRun` at/after it):
///   `SkipMissed` -> false; `CatchUpOnce` -> true (the caller must record the run
///   via `lastRun` so it fires at most once).
/// - exactly on time (`scheduled = now`) -> true.
let isDue (policy: Policy) (now: DateTimeOffset) (lastRun: DateTimeOffset option) (scheduled: DateTimeOffset) : bool =
    if scheduled > now then
        false
    elif
        (match lastRun with
         | Some lr -> lr >= scheduled
         | None -> false)
    then
        false
    elif scheduled < now then
        match policy.Catchup with
        | SkipMissed -> false
        | CatchUpOnce -> true
    else
        true

/// Resolves a local wall-clock `DateTime` to a `DateTimeOffset` in `tz`.
///
/// - Invalid local time (spring-forward gap) -> `None`.
/// - Ambiguous local time (fall-back) -> deterministic fixed choice: the standard
///   (non-DST) offset, i.e. the smaller UTC offset (winter time), matching
///   `TimeZoneInfo.GetUtcOffset` for ambiguous times.
let resolveLocal (tz: TimeZoneInfo) (local: DateTime) : DateTimeOffset option =
    let local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified)

    if tz.IsInvalidTime local then
        None
    else
        let offset =
            if tz.IsAmbiguousTime local then
                tz.GetAmbiguousTimeOffsets local |> Array.minBy (fun o -> o.TotalMinutes)
            else
                tz.GetUtcOffset local

        Some(DateTimeOffset(local, offset))

/// Returns the next `count` occurrences of `cron` in `tz` strictly after `after`.
let nextOccurrences
    (cron: CronExpression)
    (tz: TimeZoneInfo)
    (after: DateTimeOffset)
    (count: int)
    : DateTimeOffset list =
    let mutable result = []
    let mutable remaining = count
    let mutable current = cron.GetNextOccurrence(after, tz, false)

    while remaining > 0 && current.HasValue do
        result <- current.Value :: result
        current <- cron.GetNextOccurrence(current.Value, tz, false)
        remaining <- remaining - 1

    List.rev result
