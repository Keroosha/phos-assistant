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
      Payload: string
      Entities: Phos.Core.Chunker.Entity list }

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
        voice: IVoiceProcessor,
        transport: ITelegramTransport
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
                      Payload = chunk.Text
                      Entities = chunk.Entities }

                do! enqueueOutbox envelope
                index <- index + 1
        }

    /// Resolves a short human-readable summary of the message this update replies
    /// to, so the bot can see its own context in the prompt. Any transport
    /// failure degrades to `None` (no prefix) rather than blocking the admit.
    let replyContext (chat: ChatId) (replyToId: int64) : Task<string option> =
        task {
            try
                let! s = transport.GetMessageSummary chat replyToId

                return
                    match s with
                    | Some(MessageSummary.Text t) -> Some t
                    | Some MessageSummary.Voice -> Some "🎤 голосовое сообщение"
                    | Some MessageSummary.Photo -> Some "📷 фото"
                    | Some MessageSummary.Other -> Some "сообщение"
                    | None -> None
            with _ ->
                return None
        }

    /// Builds the admit payload, prefixing the reply-context when the update is
    /// a reply to an earlier message. Existing payload behavior is unchanged when
    /// there is no reply (or the replied message cannot be resolved).
    let replyPayload (update: IncomingUpdate) (basePayload: string) : Task<string> =
        task {
            let! replyText =
                match update.ReplyToMessageId with
                | Some rid -> replyContext update.Chat.Id rid
                | None -> task { return None }

            return
                match replyText with
                | Some ctx -> sprintf "[в ответ на: %s]\n\n%s" ctx basePayload
                | None -> basePayload
        }

    member _.HandleAsync(update: IncomingUpdate) : Task<HandleResult> =
        task {
            if not (dedupe.TryAdd update.UpdateId) then
                return Duplicate
            else
                match authorize whitelist update.From update.Chat with
                | Deny reason -> return Denied reason
                | Allow role ->
                    if update.IsSticker then
                        // A sticker is feedback-only: react with 👀 and never
                        // admit a command. The reaction is best-effort — a
                        // transport failure must never block the handler.
                        try
                            do! transport.SetReaction update.Chat.Id update.MessageId "👀"
                        with _ ->
                            ()

                        return Accepted
                    elif update.Photo.IsSome then
                        let photo = update.Photo.Value

                        let! bytesOpt =
                            task {
                                try
                                    let! b = transport.DownloadPhoto photo
                                    return Some b
                                with _ ->
                                    return None
                            }

                        match bytesOpt with
                        | Some bytes ->
                            let! payload = replyPayload update (update.Text |> Option.defaultValue "")

                            let envelope =
                                { Origin = Telegram
                                  ExternalKey = Some(sprintf "tg:%d" update.UpdateId)
                                  UserId = update.From.Id
                                  ChatId = update.Chat.Id
                                  Payload = payload
                                  Priority = 0
                                  Images = [ Convert.ToBase64String bytes ] }

                            let! outcome = admit envelope

                            match outcome with
                            | Admitted _ -> return Accepted
                            | Failed -> return AdmitFailed
                        | None ->
                            do! enqueueReply update.UpdateId update.Chat "⚠️ не удалось скачать фото"
                            return Accepted
                    else
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
                                    let! payload = replyPayload update text

                                    let envelope =
                                        { Origin = Telegram
                                          ExternalKey = Some(sprintf "tg:%d" update.UpdateId)
                                          UserId = update.From.Id
                                          ChatId = update.Chat.Id
                                          Payload = payload
                                          Priority = 0
                                          Images = [] }

                                    let! outcome = admit envelope

                                    match outcome with
                                    | Admitted _ -> return Accepted
                                    | Failed -> return AdmitFailed
                                | Error msg ->
                                    do! enqueueReply update.UpdateId update.Chat ("⚠️ " + msg)
                                    return Accepted
                            | None ->
                                let! payload = replyPayload update (update.Text |> Option.defaultValue "")

                                let envelope =
                                    { Origin = Telegram
                                      ExternalKey = Some(sprintf "tg:%d" update.UpdateId)
                                      UserId = update.From.Id
                                      ChatId = update.Chat.Id
                                      Payload = payload
                                      Priority = 0
                                      Images = [] }

                                let! outcome = admit envelope

                                match outcome with
                                | Admitted _ -> return Accepted
                                | Failed -> return AdmitFailed
        }
