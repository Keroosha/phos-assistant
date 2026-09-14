namespace Phos.Omp

open System
open System.Runtime.CompilerServices
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

[<assembly: InternalsVisibleTo("Phos.Tests")>]
do ()

/// Coarse classification of an outbound stdout frame. The RPC client only needs
/// to distinguish a handful of categories; the rest are treated as opaque
/// session events forwarded to subscribers.
[<RequireQualifiedAccess>]
type FrameKind =
    | Ready
    | Response
    | RpcChunk
    | HostToolCall
    | HostUriRequest
    | Event
    | Unknown

/// A typed command failure reported by the RPC server (`success: false`).
type RpcError =
    { Command: string
      Code: string option
      Message: string }

/// Definition of a host-owned tool registered via `set_host_tools`.
type RpcHostToolDefinition =
    { Name: string
      Label: string
      Description: string
      Parameters: JsonObject }

/// Definition of a host-owned URL scheme registered via `set_host_uri_schemes`.
type RpcHostUriScheme =
    { Scheme: string
      Description: string
      Writable: bool
      Immutable: bool }

/// Small JSON accessors over `System.Text.Json.Nodes`. These never throw on a
/// missing/mistyped field; a parse failure surfaces as a `Result`/`option`.
module Json =

    let tryGet (name: string) (obj: JsonObject) : JsonNode option =
        let mutable node: JsonNode | null = null

        if obj.TryGetPropertyValue(name, &node) then
            match node with
            | null -> None
            | n -> Some n
        else
            None

    let getString (name: string) (obj: JsonObject) : string option =
        match tryGet name obj with
        | Some(:? JsonValue as v) ->
            try
                Some(v.GetValue<string>())
            with _ ->
                None
        | _ -> None

    let getInt (name: string) (obj: JsonObject) : int option =
        match tryGet name obj with
        | Some(:? JsonValue as v) ->
            try
                Some(v.GetValue<int>())
            with _ ->
                try
                    Some(int (v.GetValue<int64>()))
                with _ ->
                    None
        | _ -> None

    let getInt64 (name: string) (obj: JsonObject) : int64 option =
        match tryGet name obj with
        | Some(:? JsonValue as v) ->
            try
                Some(v.GetValue<int64>())
            with _ ->
                try
                    Some(int64 (v.GetValue<int>()))
                with _ ->
                    None
        | _ -> None

    let getBool (name: string) (obj: JsonObject) : bool option =
        match tryGet name obj with
        | Some(:? JsonValue as v) ->
            try
                Some(v.GetValue<bool>())
            with _ ->
                None
        | _ -> None

    let getObject (name: string) (obj: JsonObject) : JsonObject option =
        match tryGet name obj with
        | Some(:? JsonObject as o) -> Some o
        | _ -> None

    let getArray (name: string) (obj: JsonObject) : JsonArray option =
        match tryGet name obj with
        | Some(:? JsonArray as a) -> Some a
        | _ -> None

/// Protocol-level helpers shared across the RPC client, process and host
/// bridges. Kept in a module so they are unambiguously accessible from other
/// files in the same namespace.
module RpcProtocol =

    /// Parses a single newline-delimited JSONL frame into a `JsonObject`.
    let tryParseFrame (line: string) : Result<JsonObject, string> =
        if String.IsNullOrWhiteSpace line then
            Error "empty frame"
        else
            try
                match JsonNode.Parse(line) with
                | :? JsonObject as obj -> Ok obj
                | _ -> Error "frame is not a JSON object"
            with :? JsonException as ex ->
                Error(sprintf "invalid JSON frame: %s" ex.Message)

    /// Classifies a parsed frame by its `type` discriminator.
    let classify (frame: JsonObject) : FrameKind =
        match Json.getString "type" frame with
        | Some "ready" -> FrameKind.Ready
        | Some "response" -> FrameKind.Response
        | Some "rpc_chunk" -> FrameKind.RpcChunk
        | Some "host_tool_call" -> FrameKind.HostToolCall
        | Some "host_uri_request" -> FrameKind.HostUriRequest
        | Some _ -> FrameKind.Event
        | None -> FrameKind.Unknown

    /// Builds the JSON object for a `RpcHostToolDefinition`.
    let hostToolDefinitionToJson (tool: RpcHostToolDefinition) : JsonObject =
        let obj = JsonObject()
        obj["name"] <- tool.Name
        obj["label"] <- tool.Label
        obj["description"] <- tool.Description
        obj["parameters"] <- tool.Parameters.DeepClone()
        obj

    /// Builds the JSON object for a `RpcHostUriScheme`.
    let hostUriSchemeToJson (scheme: RpcHostUriScheme) : JsonObject =
        let obj = JsonObject()
        obj["scheme"] <- scheme.Scheme
        obj["description"] <- scheme.Description
        obj["writable"] <- scheme.Writable
        obj["immutable"] <- scheme.Immutable
        obj

/// Reassembles a lossless protocol-v2 object split across `rpc_chunk` frames.
///
/// Validation rules (from `omp://rpc.md`): a sequence is keyed by `chunkId` and
/// must arrive uninterrupted; `index` must be strictly sequential from 0;
/// `count` and `byteLength` must match the decoded bytes; the reassembled total
/// must stay under the advertised ceiling; the concatenated bytes must decode as
/// strict UTF-8 and parse as a single JSON object.
type RpcChunkReassembler(maxReassembledBytes: int64) =

    let mutable activeChunkId: string option = None
    let mutable expectedCount = 0
    let mutable received = 0
    let mutable totalBytes = 0L
    let chunks = ResizeArray<byte[]>()

    let reset () =
        activeChunkId <- None
        expectedCount <- 0
        received <- 0
        totalBytes <- 0L
        chunks.Clear()

    let strictUtf8 (bytes: byte[]) : Result<string, string> =
        try
            Ok((UTF8Encoding(false, true)).GetString(bytes))
        with :? DecoderFallbackException as ex ->
            Error(sprintf "invalid UTF-8 in rpc_chunk reassembly: %s" ex.Message)

    let parseReassembled (bytes: byte[]) : Result<JsonObject, string> =
        match strictUtf8 bytes with
        | Error e -> Error e
        | Ok text -> RpcProtocol.tryParseFrame text

    /// Adds one chunk. Returns `Ok None` while the sequence is incomplete,
    /// `Ok (Some obj)` when the final chunk completes a valid object, and
    /// `Error` on any validation failure (interleave, out-of-order, size,
    /// byteLength, UTF-8 or JSON parse).
    member _.TryAdd(frame: JsonObject) : Result<JsonObject option, string> =
        let cid = Json.getString "chunkId" frame
        let idx = Json.getInt "index" frame
        let cnt = Json.getInt "count" frame
        let byteLen = Json.getInt64 "byteLength" frame
        let data = Json.getString "data" frame

        match cid, idx, cnt, byteLen, data with
        | Some cid, Some idx, Some cnt, Some byteLen, Some data ->
            if idx < 0 || cnt <= 0 || idx >= cnt then
                Error(sprintf "invalid rpc_chunk index/count: %d/%d" idx cnt)
            else
                match activeChunkId with
                | Some active when active <> cid ->
                    Error(sprintf "interleaved rpc_chunk sequence: %s then %s" active cid)
                | Some _ ->
                    if idx <> received then
                        Error(sprintf "out-of-order rpc_chunk: expected index %d, got %d" received idx)
                    else
                        let bytes = Convert.FromBase64String data

                        if bytes.Length <> int byteLen then
                            Error(sprintf "rpc_chunk byteLength mismatch: declared %d, actual %d" byteLen bytes.Length)
                        else
                            totalBytes <- totalBytes + byteLen

                            if totalBytes > maxReassembledBytes then
                                Error(
                                    sprintf "rpc_chunk reassembly exceeds limit: %d > %d" totalBytes maxReassembledBytes
                                )
                            else
                                chunks.Add bytes
                                received <- received + 1

                                if received = cnt then
                                    let all = chunks |> Seq.toArray |> Array.concat
                                    let result = parseReassembled all
                                    reset ()
                                    result |> Result.map Some
                                else
                                    Ok None
                | None ->
                    activeChunkId <- Some cid
                    expectedCount <- cnt

                    if idx <> 0 then
                        Error(sprintf "out-of-order rpc_chunk: expected index 0, got %d" idx)
                    else
                        let bytes = Convert.FromBase64String data

                        if bytes.Length <> int byteLen then
                            Error(sprintf "rpc_chunk byteLength mismatch: declared %d, actual %d" byteLen bytes.Length)
                        else
                            totalBytes <- byteLen

                            if totalBytes > maxReassembledBytes then
                                Error(
                                    sprintf "rpc_chunk reassembly exceeds limit: %d > %d" totalBytes maxReassembledBytes
                                )
                            else
                                chunks.Add bytes
                                received <- 1

                                if received = cnt then
                                    let all = chunks |> Seq.toArray |> Array.concat
                                    let result = parseReassembled all
                                    reset ()
                                    result |> Result.map Some
                                else
                                    Ok None
        | _ -> Error "rpc_chunk frame missing chunkId/index/count/byteLength/data"
