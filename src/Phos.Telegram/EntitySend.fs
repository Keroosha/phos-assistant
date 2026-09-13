namespace Phos.Telegram

open Phos.Core.Chunker

/// Entity-aware chunking and entity rebasing for outgoing messages.
module EntitySend =

    /// Live-draft per-peer rate limits documented by Telegram. These apply to
    /// "typing" drafts only; the authoritative throttle for actual sends is the
    /// `FLOOD_WAIT` / `SLOWMODE_WAIT` error handled by `OutboxDelivery`.
    type LiveDraftLimits = { Per5Seconds: int; Per30Seconds: int }

    /// Default live-draft limits: 20 messages per 5 seconds, 40 per 30 seconds.
    let defaultLiveDraftLimits: LiveDraftLimits =
        { Per5Seconds = 20; Per30Seconds = 40 }

    /// Splits `text` into chunks of at most 4096 UTF-16 code units, rebasing the
    /// given entities into each chunk. Wraps `Chunker.chunk`; each returned
    /// `Chunk` carries its rebased entities and fence state.
    let chunkForSend (text: string) (entities: TelegramEntity list) : Chunk list =
        let coreEntities =
            entities
            |> List.map (fun e ->
                { Offset = e.Offset
                  Length = e.Length
                  Kind = e.Kind })

        Phos.Core.Chunker.chunk 4096 text coreEntities

    /// Rebases a chunk's entities into `TelegramEntity` values ready for sending.
    let toTelegramEntities (chunk: Chunk) : TelegramEntity list =
        chunk.Entities
        |> List.map (fun e ->
            { Offset = e.Offset
              Length = e.Length
              Kind = e.Kind })
