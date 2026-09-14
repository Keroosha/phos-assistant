namespace Phos.Omp

open System
open System.Threading.Tasks
open System.Text.Json.Nodes
open Microsoft.Extensions.Logging
open Phos.Core.DomainTypes
open Phos.Telegram

/// Resolves `host_uri_request` frames for the registered `tg://` scheme. Reads
/// of `tg://message/<chatId>/<messageId>` download the voice bytes and return
/// them base64-encoded as `application/octet-stream`; reads of
/// `tg://history/<chatId>?limit=N&before=<id>` return a chronological JSON
/// array of the chat history; writes are rejected.
type HostUriResolver(transport: ITelegramTransport, logger: ILogger) =

    let uriResult (contentType: string) (id: string) (content: string) : JsonObject =
        let obj = JsonObject()
        obj["type"] <- "host_uri_result"
        obj["id"] <- id
        obj["content"] <- content
        obj["contentType"] <- contentType
        obj

    let uriError (id: string) (error: string) : JsonObject =
        let obj = JsonObject()
        obj["type"] <- "host_uri_result"
        obj["id"] <- id
        obj["isError"] <- true
        obj["error"] <- error
        obj

    /// Parses `tg://message/<chatId>/<messageId>` into a voice-message read.
    let tryParseMessageUri (url: string) : Result<int64 * int64, string> =
        let prefix = "tg://message/"

        if url.StartsWith prefix then
            let rest = url.Substring prefix.Length
            let parts = rest.Split('/')

            match parts with
            | [| chatStr; msgStr |] ->
                match Int64.TryParse chatStr, Int64.TryParse msgStr with
                | (true, chat), (true, msg) -> Ok(chat, msg)
                | _ -> Error "invalid tg://message URI"
            | _ -> Error "invalid tg://message URI"
        else
            Error "unsupported host URI scheme"

    /// Parses the query string of a history URI into a (limit, before) pair.
    let parseHistoryQuery (query: string) : int * int64 =
        let mutable limit = 50
        let mutable before = 0L

        if not (String.IsNullOrWhiteSpace query) then
            for kv in query.Split('&') do
                let pair = kv.Split('=')

                if pair.Length = 2 then
                    match pair.[0] with
                    | "limit" ->
                        match Int32.TryParse pair.[1] with
                        | true, n -> limit <- n
                        | _ -> ()
                    | "before" ->
                        match Int64.TryParse pair.[1] with
                        | true, b -> before <- b
                        | _ -> ()
                    | _ -> ()

        min 100 (max 1 limit), before

    /// Parses `tg://history/<chatId>?limit=N&before=<id>` into a history read.
    /// The query is optional; `limit` defaults to 50 (clamped 1..100) and
    /// `before` defaults to 0.
    let tryParseHistoryUri (url: string) : Result<int64 * int * int64, string> =
        let prefix = "tg://history/"

        if url.StartsWith prefix then
            let rest = url.Substring prefix.Length
            let parts = rest.Split('?')

            match Int64.TryParse parts.[0] with
            | true, chatId ->
                let query = if parts.Length > 1 then parts.[1] else ""
                let limit, before = parseHistoryQuery query
                Ok(chatId, limit, before)
            | _ -> Error "invalid tg://history URI"
        else
            Error "unsupported host URI scheme"

    /// Builds the chronological JSON array body for a history read. Text messages
    /// carry a `text` field; non-text messages carry a `kind` field.
    let buildHistoryJson (entries: HistoryEntry list) : string =
        let arr = JsonArray()

        for e in entries do
            let obj = JsonObject()
            obj["id"] <- e.Id
            obj["fromBot"] <- e.FromBot
            obj["date"] <- e.Date.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ")

            match e.Summary with
            | MessageSummary.Text t -> obj["text"] <- t
            | MessageSummary.Voice -> obj["kind"] <- "voice"
            | MessageSummary.Photo -> obj["kind"] <- "photo"
            | MessageSummary.Other -> obj["kind"] <- "other"

            arr.Add obj

        arr.ToJsonString()

    /// Resolves a frame if it is a `host_uri_request`, returning the
    /// `host_uri_result` frame to send, or `None` for any other frame.
    member _.TryResolve(frame: JsonObject) : Task<JsonObject option> =
        task {
            if RpcProtocol.classify frame <> FrameKind.HostUriRequest then
                return None
            else
                let id = Json.getString "id" frame |> Option.defaultValue ""
                let op = Json.getString "operation" frame |> Option.defaultValue "read"
                let url = Json.getString "url" frame |> Option.defaultValue ""

                if op = "write" then
                    return Some(uriError id "host URI write is not supported")
                elif url.StartsWith "tg://message/" then

                    match tryParseMessageUri url with
                    | Error e -> return Some(uriError id e)
                    | Ok(chatId, messageId) ->
                        try
                            let voiceRef =
                                { ChatId = ChatId chatId
                                  MessageId = messageId
                                  FileReference = [||]
                                  AccessHash = 0L }

                            let! bytes = transport.DownloadVoice voiceRef
                            let b64 = Convert.ToBase64String bytes
                            return Some(uriResult "application/octet-stream" id b64)
                        with ex ->
                            logger.LogWarning(ex, "host URI download failed for {Url}", url)
                            return Some(uriError id ex.Message)
                elif url.StartsWith "tg://history/" then

                    match tryParseHistoryUri url with
                    | Error e -> return Some(uriError id e)
                    | Ok(chatId, limit, before) ->
                        try
                            let! entries = transport.GetHistory (ChatId chatId) before limit
                            return Some(uriResult "application/json" id (buildHistoryJson entries))
                        with ex ->
                            logger.LogWarning(ex, "host URI history failed for {Url}", url)
                            return Some(uriError id ex.Message)
                else
                    return
                        Some(
                            uriError
                                id
                                "unsupported host URI scheme (only tg://message/<chatId>/<messageId> and tg://history/<chatId>?limit=N&before=<id>)"
                        )
        }
