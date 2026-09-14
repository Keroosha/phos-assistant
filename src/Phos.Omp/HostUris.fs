namespace Phos.Omp

open System
open System.Threading.Tasks
open System.Text.Json.Nodes
open Microsoft.Extensions.Logging
open Phos.Core.DomainTypes
open Phos.Telegram

/// Resolves `host_uri_request` frames for the registered `tg://` scheme. Reads
/// of `tg://message/<chatId>/<messageId>` download the voice bytes and return
/// them base64-encoded as `application/octet-stream`; writes are rejected.
type HostUriResolver(transport: ITelegramTransport, logger: ILogger) =

    let uriResult (id: string) (content: string) : JsonObject =
        let obj = JsonObject()
        obj["type"] <- "host_uri_result"
        obj["id"] <- id
        obj["content"] <- content
        obj["contentType"] <- "application/octet-stream"
        obj

    let uriError (id: string) (error: string) : JsonObject =
        let obj = JsonObject()
        obj["type"] <- "host_uri_result"
        obj["id"] <- id
        obj["isError"] <- true
        obj["error"] <- error
        obj

    let tryParseTgUri (url: string) : Result<int64 * int64, string> =
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
            Error "unsupported host URI scheme (only tg://message/<chatId>/<messageId>)"

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
                else

                    match tryParseTgUri url with
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
                            return Some(uriResult id b64)
                        with ex ->
                            logger.LogWarning(ex, "host URI download failed for {Url}", url)
                            return Some(uriError id ex.Message)
        }
