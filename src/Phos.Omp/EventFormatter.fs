namespace Phos.Omp

open System
open System.Text.Json.Nodes
open Phos.Core.DomainTypes
open Phos.Core.Chunker
open Phos.Telegram

/// Per-session stream state for the pure event formatter.
type StreamState = { Accumulated: string }

/// Context needed to build outbox envelopes from a session event.
type FormatterContext = { CommandId: int64; ChatId: ChatId }

/// Typed outcome of a finished turn, as observed by the host on a terminal
/// `agent_end`. The terminal boundary is classified by inspecting the
/// serialized assistant messages of the frame (`stopReason` / `errorMessage`):
///
/// - `Completed`: the turn finished normally; accumulated text (if any) is
///   delivered as the successful answer.
/// - `Aborted`: the user/host aborted the turn (`stopReason: "aborted"`); the
///   partial text is still delivered (existing `/stop` behavior).
/// - `ProviderFailure`: the provider/model failed after OMP's own retry cycle
///   (`stopReason: "error"` with `errorMessage`); the accumulated partial text
///   is suppressed so a failed turn is never delivered as a successful answer.
/// - `NeedsReview`: the outcome is unknown (e.g. the OMP process died
///   mid-turn, possibly after host-tool activity); the command is parked
///   durably for manual review instead of being replayed automatically.
[<RequireQualifiedAccess>]
type TurnOutcome =
    | Completed
    | Aborted
    | ProviderFailure of reason: string
    | NeedsReview

/// Pure, I/O-free mapping from OMP session events to outbox envelopes. Text
/// deltas are accumulated silently; the final assistant text is chunked
/// (4096 UTF-16 units, fence-aware) on a terminal `agent_end`. Acknowledging
/// the command is the caller's job (reaction), not a streamed message.
module EventFormatter =

    let initialState: StreamState = { Accumulated = "" }

    let private envelope (ctx: FormatterContext) (index: int) (text: string) (entities: Entity list) : OutboxEnvelope =
        { CommandId = ctx.CommandId
          ChunkIndex = index
          ChatId = ctx.ChatId
          RandomId = Random.Shared.NextInt64()
          Payload = text
          Entities = entities }

    /// Classifies a terminal `agent_end` by inspecting its serialized
    /// assistant `messages`: the first assistant message with
    /// `stopReason: "error"` (carrying `errorMessage`) is a provider/model
    /// failure after OMP's own retry cycle; `stopReason: "aborted"` is a
    /// user/host abort; anything else (including older runtimes that omit
    /// `stopReason`) is a normal completion. Non-message payload shapes are
    /// tolerated.
    let terminalOutcome (frame: JsonObject) : TurnOutcome =
        let stops: (string * string option) list =
            match Json.getArray "messages" frame with
            | Some msgs ->
                [ for m in msgs do
                      match m with
                      | :? JsonObject as o ->
                          match Json.getString "stopReason" o with
                          | Some r -> yield (r, Json.getString "errorMessage" o)
                          | None -> ()
                      | _ -> () ]
            | None -> []

        let errors =
            stops
            |> List.choose (fun (r, msg) ->
                if r = "error" then
                    Some(defaultArg msg "unknown provider error")
                else
                    None)

        if not (List.isEmpty errors) then
            TurnOutcome.ProviderFailure(String.concat "; " errors)
        elif stops |> List.exists (fun (r, _) -> r = "aborted") then
            TurnOutcome.Aborted
        else
            TurnOutcome.Completed

    /// Consumes one event frame, returning the updated state, any outbox
    /// envelopes to deliver and the terminal turn outcome. The outcome is
    /// `Some` only for a terminal `agent_end` (`isTerminal !== false`):
    /// non-terminal boundaries, OMP's auto-retry frames and intermediate
    /// retry errors never finalize a turn. On `ProviderFailure` the
    /// accumulated partial text is suppressed so a failed turn is never
    /// delivered as a successful answer; `tool_execution_start` status
    /// messages are intentionally skipped in v1 (documented scope reduction).
    let onEvent
        (ctx: FormatterContext)
        (state: StreamState)
        (frame: JsonObject)
        : StreamState * OutboxEnvelope list * TurnOutcome option =
        let mutable st = state
        let mutable envelopes: OutboxEnvelope list = []
        let mutable outcome: TurnOutcome option = None

        match Json.getString "type" frame with
        | Some "message_update" ->
            match Json.getObject "assistantMessageEvent" frame with
            | Some ev ->
                match Json.getString "type" ev with
                | Some "text_delta" ->
                    let delta = Json.getString "delta" ev |> Option.defaultValue ""

                    if delta <> "" then
                        st <-
                            { st with
                                Accumulated = st.Accumulated + delta }
                | _ -> ()
            | None -> ()
        | Some "agent_end" ->
            let isTerminal =
                match Json.getBool "isTerminal" frame with
                | Some b -> b
                | None -> true

            if isTerminal then
                let turnOutcome = terminalOutcome frame

                match turnOutcome with
                | TurnOutcome.ProviderFailure _ ->
                    // Suppress the buffered partial text: the turn failed after
                    // OMP's retry cycle, so it must not be sent as a success.
                    envelopes <- []
                | _ ->
                    let text = st.Accumulated

                    if not (String.IsNullOrWhiteSpace text) then
                        let entities = Markdown.parse text
                        let chunks = EntitySend.chunkForSend text entities

                        envelopes <- chunks |> List.mapi (fun i c -> envelope ctx i c.Text c.Entities)

                st <- initialState
                outcome <- Some turnOutcome
        | _ -> ()

        st, envelopes, outcome
