namespace Phos.Telegram

open System
open System.Threading.Tasks
open Phos.Core.DomainTypes
open Phos.Core.Whitelist
open Phos.Storage

/// Outcome of handling one incoming update.
type HandleResult =
    | Accepted
    | Duplicate
    | Denied of DenialReason
    | AdmitFailed

/// Outcome of admitting a command into the inbox.
type AdmitOutcome =
    | Admitted of commandId: int64
    | Failed

/// A message to place in the outbox, ready for delivery.
type OutboxEnvelope =
    { CommandId: int64
      ChunkIndex: int
      ChatId: ChatId
      RandomId: int64
      Payload: string }

/// Processes a single incoming update: dedupe, whitelist, then classify.
///
/// Order matters: dedupe first (a duplicate is never re-admitted), then the
/// whitelist is checked BEFORE any admission so a denied user never reaches the
/// inbox. `/start` upserts the user and replies a greeting via the outbox,
/// `/ping` replies `pong`, and any other text/voice update becomes a
/// `CommandEnvelope` admitted through the injected `admit` function. The handler
/// never waits on an LLM/OMP turn.
type UpdateHandler
    (
        whitelist: Whitelist,
        inbox: ICommandInbox,
        users: IUserRepository,
        dedupe: UpdateDedupe,
        admit: CommandEnvelope -> Task<AdmitOutcome>,
        enqueueOutbox: OutboxEnvelope -> Task<unit>,
        voice: IVoiceProcessor
    ) =

    let greetingText = "Привет! Я phos, твой ассистент в Telegram."

    let workspacePath (UserId id) = sprintf "/var/lib/phos/workspace/%d" id

    /// Chunks a reply and enqueues each chunk to the outbox with a fresh,
    /// stable random_id.
    let enqueueReply (commandId: int64) (chat: Chat) (text: string) : Task<unit> =
        task {
            let chunks = EntitySend.chunkForSend text []

            let mutable index = 0

            for chunk in chunks do
                let envelope =
                    { CommandId = commandId
                      ChunkIndex = index
                      ChatId = chat.Id
                      RandomId = Random.Shared.NextInt64()
                      Payload = chunk.Text }

                do! enqueueOutbox envelope
                index <- index + 1
        }

    member _.HandleAsync(update: IncomingUpdate) : Task<HandleResult> =
        task {
            if not (dedupe.TryAdd update.UpdateId) then
                return Duplicate
            else
                match authorize whitelist update.From update.Chat with
                | Deny reason -> return Denied reason
                | Allow role ->
                    match update.Text with
                    | Some text when text.StartsWith "/start" ->
                        let record =
                            { Id = update.From.Id
                              Username = update.From.Username
                              Role = role
                              WorkspacePath = workspacePath update.From.Id
                              Timezone = None }

                        do! users.Upsert record
                        do! enqueueReply update.UpdateId update.Chat greetingText
                        return Accepted
                    | Some text when text.StartsWith "/ping" ->
                        do! enqueueReply update.UpdateId update.Chat "pong"
                        return Accepted
                    | _ ->
                        match update.Voice with
                        | Some v ->
                            let! result = voice.ProcessAsync v

                            match result with
                            | Ok text ->
                                let envelope =
                                    { Origin = Telegram
                                      ExternalKey = Some(sprintf "tg:%d" update.UpdateId)
                                      UserId = update.From.Id
                                      ChatId = update.Chat.Id
                                      Payload = text
                                      Priority = 0 }

                                let! outcome = admit envelope

                                match outcome with
                                | Admitted _ -> return Accepted
                                | Failed -> return AdmitFailed
                            | Error msg ->
                                do! enqueueReply update.UpdateId update.Chat ("⚠️ " + msg)
                                return Accepted
                        | None ->
                            let envelope =
                                { Origin = Telegram
                                  ExternalKey = Some(sprintf "tg:%d" update.UpdateId)
                                  UserId = update.From.Id
                                  ChatId = update.Chat.Id
                                  Payload = update.Text |> Option.defaultValue ""
                                  Priority = 0 }

                            let! outcome = admit envelope

                            match outcome with
                            | Admitted _ -> return Accepted
                            | Failed -> return AdmitFailed
        }
