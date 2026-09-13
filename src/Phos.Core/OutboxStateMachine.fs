module Phos.Core.OutboxStateMachine

/// Lifecycle status of an outbox entry.
type Status =
    | Pending
    | Sending
    | Sent
    | Failed

/// A message to be delivered, with a stable `RandomId` that is never changed on
/// retry (so a retry reuses the same MTProto random_id and does not create a
/// duplicate message).
type Entry =
    { Id: int64
      CommandId: int64
      ChunkIndex: int
      RandomId: int64
      Status: Status
      Attempts: int
      MaxAttempts: int
      RemoteMessageId: int64 option }

/// Events that drive the outbox state machine.
type Event =
    | BeginSend
    | Sent of int64
    | Fail

/// An entry can be retried if it failed and has not exhausted its attempts.
let canRetry (entry: Entry) : bool =
    entry.Status = Failed && entry.Attempts < entry.MaxAttempts

/// Applies `event` to `entry`, returning the new state or an error for an
/// invalid transition.
let apply (entry: Entry) (event: Event) : Result<Entry, string> =
    match entry.Status, event with
    | Pending, BeginSend -> Ok { entry with Status = Sending }
    | Sending, Event.Sent remoteId ->
        Ok
            { entry with
                Status = Status.Sent
                RemoteMessageId = Some remoteId }
    | Sending, Fail ->
        Ok
            { entry with
                Status = Failed
                Attempts = entry.Attempts + 1 }
    | Failed, BeginSend ->
        if canRetry entry then
            Ok { entry with Status = Sending }
        else
            Error "cannot retry: max attempts reached"
    | _ -> Error(sprintf "invalid transition: %A -> %A" entry.Status event)

/// An entry can be sent if it is `Pending` or `Failed` and still retryable.
let canSend (entry: Entry) : bool =
    entry.Status = Pending || (entry.Status = Failed && canRetry entry)
