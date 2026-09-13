module Phos.Core.InboxStateMachine

open System
open Phos.Core.DomainTypes
open FsToolkit.ErrorHandling

/// Lifecycle status of an inbox command.
type Status =
    | Pending
    | Claimed
    | Running
    | Completed
    | Failed
    | DeadLetter
    | NeedsReview

/// The durable state of an inbox command.
type Command =
    { Id: int64
      Origin: Origin
      Status: Status
      Attempts: int
      MaxAttempts: int
      LeaseUntil: DateTimeOffset option
      HeartbeatAt: DateTimeOffset option }

/// Events that drive the state machine.
type Event =
    | Claim of DateTimeOffset * DateTimeOffset
    | Start
    | Complete
    | Fail
    | LeaseExpired of DateTimeOffset
    | Retry
    | DeadLetter
    | HostDied
    | ReviewedRetry

/// Error returned by `apply` for an invalid transition or an exhausted retry.
type Error =
    | InvalidTransition of Status * Event
    | MaxAttemptsReached

/// Applies `event` to `command`, returning the new state or an error for an
/// invalid transition.
let apply (command: Command) (event: Event) : Result<Command, Error> =
    match command.Status, event with
    | Pending, Claim(now, leaseUntil) ->
        Ok
            { command with
                Status = Claimed
                Attempts = command.Attempts + 1
                LeaseUntil = Some leaseUntil
                HeartbeatAt = Some now }
    | Claimed, Start -> Ok { command with Status = Running }
    | Running, Complete -> Ok { command with Status = Completed }
    | Running, Fail ->
        if command.Attempts >= command.MaxAttempts then
            Ok
                { command with
                    Status = Status.DeadLetter }
        else
            Ok { command with Status = Failed }
    | (Claimed | Running), LeaseExpired _ ->
        Ok
            { command with
                Status = Pending
                LeaseUntil = None
                HeartbeatAt = None }
    | Failed, Retry ->
        result {
            do! Result.requireTrue MaxAttemptsReached (command.Attempts < command.MaxAttempts)

            return
                { command with
                    Status = Pending
                    LeaseUntil = None
                    HeartbeatAt = None }
        }
    | Failed, Event.DeadLetter ->
        Ok
            { command with
                Status = Status.DeadLetter }
    | (Claimed | Running), HostDied -> Ok { command with Status = NeedsReview }
    | NeedsReview, ReviewedRetry ->
        Ok
            { command with
                Status = Pending
                LeaseUntil = None
                HeartbeatAt = None }
    | NeedsReview, Event.DeadLetter ->
        Ok
            { command with
                Status = Status.DeadLetter }
    | _ -> Error(InvalidTransition(command.Status, event))

/// A command can be claimed only while it is `Pending`.
let canClaim (command: Command) : bool = command.Status = Pending

/// Whether the command's lease has expired at `now`.
let isLeaseExpired (now: DateTimeOffset) (command: Command) : bool =
    match command.LeaseUntil with
    | Some until -> now >= until
    | None -> false

/// Whether a failed command has exhausted its attempts and should be dead-lettered.
let shouldDeadLetter (command: Command) : bool =
    command.Status = Failed && command.Attempts >= command.MaxAttempts
