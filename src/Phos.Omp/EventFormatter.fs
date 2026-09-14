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

    /// Consumes one event frame, returning the updated state and any outbox
    /// envelopes to deliver. `tool_execution_start` status messages are
    /// intentionally skipped in v1 (documented scope reduction).
    let onEvent (ctx: FormatterContext) (state: StreamState) (frame: JsonObject) : StreamState * OutboxEnvelope list =
        let mutable st = state
        let mutable envelopes: OutboxEnvelope list = []

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
                let text = st.Accumulated

                if not (String.IsNullOrWhiteSpace text) then
                    let entities = Markdown.parse text
                    let chunks = EntitySend.chunkForSend text entities

                    envelopes <- chunks |> List.mapi (fun i c -> envelope ctx i c.Text c.Entities)

                st <- initialState
        | _ -> ()

        st, envelopes
