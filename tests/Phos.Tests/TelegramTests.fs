module Phos.Tests.TelegramTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Xunit
open FsUnit.Xunit
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
open Phos.Core.DomainTypes
open Phos.Core.Whitelist
open Phos.Core.Chunker
open Phos.Storage
open Phos.Telegram

module Out = Phos.Core.OutboxStateMachine

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

let private makeTempDir () : string =
    let dir =
        Path.Combine(Path.GetTempPath(), "phos-tg-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(dir) |> ignore
    dir

let private deleteDir (dir: string) =
    try
        if Directory.Exists dir then
            Directory.Delete(dir, true)
    with _ ->
        ()

let private defaultOptions (dbPath: string) : StorageOptions =
    { DatabasePath = dbPath
      BusyTimeout = TimeSpan.FromSeconds 2.0
      ReadPoolSize = 4
      CheckpointEvery = 10 }

let private createExecutor (dbPath: string) : StorageExecutor =
    let exec = StorageExecutor.Create(defaultOptions dbPath)
    Schema.run (defaultOptions dbPath)
    exec

let private dispose (exec: StorageExecutor) = exec.Dispose()

let private mkRepos (exec: StorageExecutor) =
    (CommandInbox(exec) :> ICommandInbox, MessageOutbox(exec) :> IMessageOutbox, UserRepository(exec) :> IUserRepository)

let private testChat: Chat = { Id = ChatId 1L; Kind = Private }

let private testUser: User =
    { Id = UserId 1L
      Username = Some "tester"
      Role = User }

let private mkUpdate
    (id: int64)
    (chat: Chat)
    (from: User)
    (text: string option)
    (voice: VoiceRef option)
    : IncomingUpdate =
    { UpdateId = id
      Chat = chat
      From = from
      Text = text
      Voice = voice }

/// Fake transport that records `SendTarget` calls and can be made to fail or
/// flood-wait a fixed number of times before succeeding.
type FakeTransport() =
    let sendCalls = ResizeArray<SendTarget>()
    let mutable remoteId = 1L
    let mutable voiceBytes = Array.empty
    let mutable failCount = 0
    let mutable floodCount = 0
    let mutable slowmodeCount = 0
    let mutable floodSeconds = 0

    member _.SendCalls = List.ofSeq sendCalls

    member _.RemoteId
        with set (v: int64) = remoteId <- v

    member _.VoiceBytes
        with set (v: byte[]) = voiceBytes <- v

    member _.FailNext(n: int) = failCount <- n

    member _.FloodNext(n: int) = floodCount <- n

    member _.FloodSeconds
        with set (v: int) = floodSeconds <- v

    member _.SlowmodeNext(n: int) = slowmodeCount <- n

    interface ITelegramTransport with
        member _.Login() =
            task { return { BotId = 1L; Username = Some "bot" } }

        member _.SendMessage(target: SendTarget) =
            task {
                if failCount > 0 then
                    failCount <- failCount - 1
                    return Error(Other "boom")
                elif floodCount > 0 then
                    floodCount <- floodCount - 1
                    return Error(FloodWait floodSeconds)
                elif slowmodeCount > 0 then
                    slowmodeCount <- slowmodeCount - 1
                    return Error(SlowModeWait 0)
                else
                    sendCalls.Add target
                    return Ok { RemoteMessageId = remoteId }
            }

        member _.EditMessage _ _ _ _ = Task.FromResult(())
        member _.DownloadVoice _ = task { return voiceBytes }

/// Capturing ILogger that records (level, eventId, message) for each log call.
type CapturingLogger() =
    let entries = ResizeArray<LogLevel * EventId * string>()

    member _.Entries = List.ofSeq entries

    interface ILogger with
        member _.Log<'TState>
            (
                logLevel: LogLevel,
                eventId: EventId,
                state: 'TState,
                ex: Exception | null,
                formatter: Func<'TState, Exception | null, string>
            ) : unit =
            entries.Add(logLevel, eventId, formatter.Invoke(state, ex))

        member _.IsEnabled(_: LogLevel) : bool = true

        member _.BeginScope<'TState when 'TState: not null>(_: 'TState) : IDisposable | null = null

// ---------------------------------------------------------------------------
// Login contract
// ---------------------------------------------------------------------------

// The WTelegramClient config callback must return `null` for unknown keys
// (phone/code/2FA are never configured for a bot login), so this test asserts
// that `configProvider` returns null for them. `configProvider` is annotated
// `string | null`, so the null comparison is nullness-safe.
[<Fact>]
let ``login config provider only answers the four bot keys`` () =
    let config =
        { ApiId = 1
          ApiHash = "hash"
          BotToken = "token"
          SessionPath = "/tmp/phos.session" }

    Transport.configProvider config "api_id" |> should equal "1"
    Transport.configProvider config "api_hash" |> should equal "hash"
    Transport.configProvider config "bot_token" |> should equal "token"

    Transport.configProvider config "session_pathname"
    |> should equal "/tmp/phos.session"

    Transport.configProvider config "phone" |> isNull |> should be True
    Transport.configProvider config "code" |> isNull |> should be True
    Transport.configProvider config "password" |> isNull |> should be True
    Transport.configProvider config "2fa" |> isNull |> should be True

// ---------------------------------------------------------------------------
// Update handler
// ---------------------------------------------------------------------------

[<Fact>]
let ``allowlisted start is accepted and upserts the user`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            let exec = createExecutor dbPath

            try
                let inbox, _, users = mkRepos exec
                let dedupe = UpdateDedupe(1000)
                let admitCalls = ResizeArray<CommandEnvelope>()
                let outboxCalls = ResizeArray<OutboxEnvelope>()

                let admit (env: CommandEnvelope) =
                    task {
                        admitCalls.Add env
                        return Admitted 1L
                    }

                let enqueue (env: OutboxEnvelope) = task { outboxCalls.Add env }

                let whitelist =
                    Phos.Core.Whitelist.create (Map.ofList [ (UserId 1L, User) ]) Set.empty

                let handler = UpdateHandler(whitelist, inbox, users, dedupe, admit, enqueue)

                let update = mkUpdate 100L testChat testUser (Some "/start") None
                let! result = handler.HandleAsync update
                result |> should equal Accepted

                let! user = users.GetByTelegramId(UserId 1L)
                user |> Option.isSome |> should be True
                user.Value.WorkspacePath |> should startWith "/var/lib/phos/workspace/"
                user.Value.Username |> should equal (Some "tester")

                admitCalls.Count |> should equal 0
                outboxCalls.Count |> should equal 1
                outboxCalls.[0].Payload |> should not' (be Empty)
            finally
                dispose exec
        }
    finally
        deleteDir dir

[<Fact>]
let ``start is denied before admission for a non-whitelisted user`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            let exec = createExecutor dbPath

            try
                let inbox, _, users = mkRepos exec
                let dedupe = UpdateDedupe(1000)
                let admitCalls = ResizeArray<CommandEnvelope>()
                let outboxCalls = ResizeArray<OutboxEnvelope>()

                let admit (env: CommandEnvelope) =
                    task {
                        admitCalls.Add env
                        return Admitted 1L
                    }

                let enqueue (env: OutboxEnvelope) = task { outboxCalls.Add env }

                let whitelist =
                    Phos.Core.Whitelist.create (Map.ofList [ (UserId 1L, User) ]) Set.empty

                let handler = UpdateHandler(whitelist, inbox, users, dedupe, admit, enqueue)

                let stranger =
                    { Id = UserId 999L
                      Username = Some "stranger"
                      Role = User }

                let update = mkUpdate 101L testChat stranger (Some "/start") None
                let! result = handler.HandleAsync update
                result |> should equal (Denied NotWhitelisted)

                let! user = users.GetByTelegramId(UserId 999L)
                user |> Option.isNone |> should be True
                admitCalls.Count |> should equal 0
                outboxCalls.Count |> should equal 0
            finally
                dispose exec
        }
    finally
        deleteDir dir

[<Fact>]
let ``non-whitelisted text is denied and admit is not called`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            let exec = createExecutor dbPath

            try
                let inbox, _, users = mkRepos exec
                let dedupe = UpdateDedupe(1000)
                let admitCalls = ResizeArray<CommandEnvelope>()
                let enqueue (env: OutboxEnvelope) = task { () }

                let admit (env: CommandEnvelope) =
                    task {
                        admitCalls.Add env
                        return Admitted 1L
                    }

                let whitelist =
                    Phos.Core.Whitelist.create (Map.ofList [ (UserId 1L, User) ]) Set.empty

                let handler = UpdateHandler(whitelist, inbox, users, dedupe, admit, enqueue)

                let stranger =
                    { Id = UserId 999L
                      Username = Some "stranger"
                      Role = User }

                let update = mkUpdate 102L testChat stranger (Some "hello") None
                let! result = handler.HandleAsync update
                result |> should equal (Denied NotWhitelisted)
                admitCalls.Count |> should equal 0
            finally
                dispose exec
        }
    finally
        deleteDir dir

[<Fact>]
let ``ping is accepted and pong is delivered with the same random id`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            let exec = createExecutor dbPath

            try
                let inbox, outbox, users = mkRepos exec
                let dedupe = UpdateDedupe(1000)

                let admit _ = task { return Admitted 1L }

                let enqueue (env: OutboxEnvelope) =
                    task {
                        let! _ = outbox.Insert env.CommandId env.ChunkIndex env.ChatId env.RandomId env.Payload

                        return ()
                    }

                let whitelist =
                    Phos.Core.Whitelist.create (Map.ofList [ (UserId 1L, User) ]) Set.empty

                let handler = UpdateHandler(whitelist, inbox, users, dedupe, admit, enqueue)

                let update = mkUpdate 200L testChat testUser (Some "/ping") None
                let! result = handler.HandleAsync update
                result |> should equal Accepted

                let! entry = outbox.NextPending()
                entry |> Option.isSome |> should be True
                let e = entry.Value

                let transport = FakeTransport()
                let delivery = OutboxDelivery(outbox, transport, NullLogger.Instance)
                let! processed = delivery.DeliverOnceAsync(CancellationToken.None)
                processed |> should be True

                transport.SendCalls |> should haveLength 1
                transport.SendCalls.[0].RandomId |> should equal e.RandomId
                transport.SendCalls.[0].Text |> should equal "pong"

                let! sent = outbox.GetByRandomId e.RandomId
                sent |> Option.isSome |> should be True
                sent.Value.Status |> should equal Out.Sent
            finally
                dispose exec
        }
    finally
        deleteDir dir

[<Fact>]
let ``duplicate update id is rejected and admitted once`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            let exec = createExecutor dbPath

            try
                let inbox, _, users = mkRepos exec
                let dedupe = UpdateDedupe(1000)
                let admitCalls = ResizeArray<CommandEnvelope>()
                let enqueue (env: OutboxEnvelope) = task { () }

                let admit (env: CommandEnvelope) =
                    task {
                        admitCalls.Add env
                        return Admitted 1L
                    }

                let whitelist =
                    Phos.Core.Whitelist.create (Map.ofList [ (UserId 1L, User) ]) Set.empty

                let handler = UpdateHandler(whitelist, inbox, users, dedupe, admit, enqueue)

                let update = mkUpdate 300L testChat testUser (Some "hello") None
                let! first = handler.HandleAsync update
                first |> should equal Accepted
                let! second = handler.HandleAsync update
                second |> should equal Duplicate
                admitCalls.Count |> should equal 1
            finally
                dispose exec
        }
    finally
        deleteDir dir

[<Fact>]
let ``voice update is admitted with a voice marker and voice bytes download`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            let exec = createExecutor dbPath

            try
                let inbox, _, users = mkRepos exec
                let dedupe = UpdateDedupe(1000)
                let admitCalls = ResizeArray<CommandEnvelope>()
                let enqueue (env: OutboxEnvelope) = task { () }

                let admit (env: CommandEnvelope) =
                    task {
                        admitCalls.Add env
                        return Admitted 1L
                    }

                let whitelist =
                    Phos.Core.Whitelist.create (Map.ofList [ (UserId 1L, User) ]) Set.empty

                let handler = UpdateHandler(whitelist, inbox, users, dedupe, admit, enqueue)

                let voiceRef =
                    { ChatId = testChat.Id
                      MessageId = 500L
                      FileReference = [| 1uy; 2uy |]
                      AccessHash = 123L }

                let update = mkUpdate 400L testChat testUser None (Some voiceRef)
                let! result = handler.HandleAsync update
                result |> should equal Accepted
                admitCalls.Count |> should equal 1
                admitCalls.[0].Payload |> should equal "voice:500"

                let transport = FakeTransport()
                transport.VoiceBytes <- [| 9uy; 8uy; 7uy |]
                let! bytes = (transport :> ITelegramTransport).DownloadVoice voiceRef
                bytes |> should equal [| 9uy; 8uy; 7uy |]
            finally
                dispose exec
        }
    finally
        deleteDir dir

[<Fact>]
let ``admit failure yields AdmitFailed`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            let exec = createExecutor dbPath

            try
                let inbox, _, users = mkRepos exec
                let dedupe = UpdateDedupe(1000)
                let enqueue (env: OutboxEnvelope) = task { () }
                let admit _ = task { return Failed }

                let whitelist =
                    Phos.Core.Whitelist.create (Map.ofList [ (UserId 1L, User) ]) Set.empty

                let handler = UpdateHandler(whitelist, inbox, users, dedupe, admit, enqueue)

                let update = mkUpdate 500L testChat testUser (Some "hello") None
                let! result = handler.HandleAsync update
                result |> should equal AdmitFailed
            finally
                dispose exec
        }
    finally
        deleteDir dir

// ---------------------------------------------------------------------------
// Outbox delivery
// ---------------------------------------------------------------------------

[<Fact>]
let ``outbox retry after failure keeps the same random id`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            let exec = createExecutor dbPath

            try
                let _, outbox, _ = mkRepos exec
                let! _ = outbox.Insert 1L 0 testChat.Id 777L "hello"

                let transport = FakeTransport()
                transport.FailNext 1
                let delivery = OutboxDelivery(outbox, transport, NullLogger.Instance)
                let! _ = delivery.DeliverOnceAsync(CancellationToken.None)
                let! _ = delivery.DeliverOnceAsync(CancellationToken.None)

                transport.SendCalls |> should haveLength 1
                transport.SendCalls.[0].RandomId |> should equal 777L

                let! sent = outbox.GetByRandomId 777L
                sent |> Option.isSome |> should be True
                sent.Value.Status |> should equal Out.Sent
            finally
                dispose exec
        }
    finally
        deleteDir dir

[<Fact>]
let ``flood wait retries later with the same random id`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            let exec = createExecutor dbPath

            try
                let _, outbox, _ = mkRepos exec
                let! _ = outbox.Insert 1L 0 testChat.Id 888L "hello"

                let transport = FakeTransport()
                transport.FloodNext 1
                let delivery = OutboxDelivery(outbox, transport, NullLogger.Instance)
                let! _ = delivery.DeliverOnceAsync(CancellationToken.None)
                let! _ = delivery.DeliverOnceAsync(CancellationToken.None)

                transport.SendCalls |> should haveLength 1
                transport.SendCalls.[0].RandomId |> should equal 888L

                let! sent = outbox.GetByRandomId 888L
                sent |> Option.isSome |> should be True
                sent.Value.Status |> should equal Out.Sent
            finally
                dispose exec
        }
    finally
        deleteDir dir

[<Fact>]
let ``flood wait logs a warning event 2 with the wait seconds`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            let exec = createExecutor dbPath

            try
                let _, outbox, _ = mkRepos exec
                let! _ = outbox.Insert 1L 0 testChat.Id 888L "hello"
                let! entry = outbox.NextPending()
                let e = entry.Value

                let transport = FakeTransport()
                transport.FloodSeconds <- 1
                transport.FloodNext 1
                let logger = CapturingLogger()
                let delivery = OutboxDelivery(outbox, transport, logger)
                let! _ = delivery.DeliverOnceAsync(CancellationToken.None)

                logger.Entries |> should haveLength 1
                let level, eventId, message = logger.Entries.[0]
                level |> should equal LogLevel.Warning
                eventId.Id |> should equal 2
                eventId.Name |> should equal "FloodWait"
                message.Contains("Flood/Slowmode wait 1s") |> should be True
                message.Contains(string e.Id) |> should be True
                message.Contains(string e.RandomId) |> should be True
            finally
                dispose exec
        }
    finally
        deleteDir dir

[<Fact>]
let ``slowmode wait also retries later with the same random id`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            let exec = createExecutor dbPath

            try
                let _, outbox, _ = mkRepos exec
                let! _ = outbox.Insert 1L 0 testChat.Id 889L "hello"

                let transport = FakeTransport()
                transport.SlowmodeNext 1
                let delivery = OutboxDelivery(outbox, transport, NullLogger.Instance)
                let! _ = delivery.DeliverOnceAsync(CancellationToken.None)
                let! _ = delivery.DeliverOnceAsync(CancellationToken.None)

                transport.SendCalls |> should haveLength 1
                transport.SendCalls.[0].RandomId |> should equal 889L

                let! sent = outbox.GetByRandomId 889L
                sent |> Option.isSome |> should be True
                sent.Value.Status |> should equal Out.Sent
            finally
                dispose exec
        }
    finally
        deleteDir dir

[<Fact>]
let ``deliver once returns false when outbox is empty`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            let exec = createExecutor dbPath

            try
                let _, outbox, _ = mkRepos exec
                let transport = FakeTransport()
                let delivery = OutboxDelivery(outbox, transport, NullLogger.Instance)
                let! processed = delivery.DeliverOnceAsync(CancellationToken.None)
                processed |> should be False
            finally
                dispose exec
        }
    finally
        deleteDir dir

[<Fact>]
let ``run async delivers a pending entry until cancelled`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            let exec = createExecutor dbPath

            try
                let _, outbox, _ = mkRepos exec
                let! _ = outbox.Insert 1L 0 testChat.Id 999L "hello"

                let transport = FakeTransport()
                let delivery = OutboxDelivery(outbox, transport, NullLogger.Instance)

                use cts = new CancellationTokenSource()
                let runTask = delivery.RunAsync(cts.Token)

                let sw = Diagnostics.Stopwatch.StartNew()

                while transport.SendCalls.IsEmpty && sw.ElapsedMilliseconds < 5000L do
                    do! Task.Delay(10)

                transport.SendCalls |> should haveLength 1
                transport.SendCalls.[0].RandomId |> should equal 999L
                cts.Cancel()

                try
                    do! runTask
                with _ ->
                    ()
            finally
                dispose exec
        }
    finally
        deleteDir dir

[<Fact>]
let ``run async with no pending entries stops on cancellation`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            let exec = createExecutor dbPath

            try
                let _, outbox, _ = mkRepos exec
                let transport = FakeTransport()
                let delivery = OutboxDelivery(outbox, transport, NullLogger.Instance)

                use cts = new CancellationTokenSource()
                let runTask = delivery.RunAsync(cts.Token)
                do! Task.Delay 50
                cts.Cancel()

                try
                    do! runTask
                with _ ->
                    ()

                transport.SendCalls |> should haveLength 0
            finally
                dispose exec
        }
    finally
        deleteDir dir

[<Fact>]
let ``run async exits immediately when already cancelled`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            let exec = createExecutor dbPath

            try
                let _, outbox, _ = mkRepos exec
                let transport = FakeTransport()
                let delivery = OutboxDelivery(outbox, transport, NullLogger.Instance)

                use cts = new CancellationTokenSource()
                cts.Cancel()
                let runTask = delivery.RunAsync(cts.Token)
                do! runTask
                transport.SendCalls |> should haveLength 0
            finally
                dispose exec
        }
    finally
        deleteDir dir

// ---------------------------------------------------------------------------
// Chunking / entities
// ---------------------------------------------------------------------------

[<Fact>]
let ``chunkForSend splits long text and rebases entities`` () =
    let text = String.replicate 5000 "x"

    let entities =
        [ { Offset = 10
            Length = 20
            Kind = Bold } ]

    let chunks = EntitySend.chunkForSend text entities

    chunks |> List.forall (fun c -> c.Text.Length <= 4096) |> should be True
    let joined = chunks |> List.map (fun c -> c.Text) |> String.concat ""
    joined |> should equal text

    let chunkWithEntity = chunks |> List.find (fun c -> not (List.isEmpty c.Entities))
    chunkWithEntity.Entities |> should haveLength 1
    chunkWithEntity.Entities.[0].Kind |> should equal Bold

[<Fact>]
let ``toTelegramEntities rebases chunk entities`` () =
    let chunk =
        { Text = "hello"
          Entities =
            [ { Offset = 1
                Length = 3
                Kind = Italic } ]
          FenceOpened = false
          FenceClosed = false }

    let ents = EntitySend.toTelegramEntities chunk
    ents |> should haveLength 1
    ents.[0].Offset |> should equal 1
    ents.[0].Length |> should equal 3
    ents.[0].Kind |> should equal Italic

// ---------------------------------------------------------------------------
// Dedupe / peer cache
// ---------------------------------------------------------------------------

[<Fact>]
let ``update dedupe tracks ids and evicts when full`` () =
    let d = UpdateDedupe(2)
    d.TryAdd 1L |> should be True
    d.TryAdd 1L |> should be False
    d.TryAdd 2L |> should be True
    d.TryAdd 2L |> should be False
    d.TryAdd 3L |> should be True
    d.TryAdd 1L |> should be True
    d.Count |> should equal 2

[<Fact>]
let ``update dedupe falls back to default capacity for non-positive input`` () =
    let d = UpdateDedupe(-5)
    d.TryAdd 1L |> should be True
    d.TryAdd 1L |> should be False
    d.Count |> should equal 1

[<Fact>]
let ``peer cache caches and retrieves input peers`` () =
    let cache = PeerCache()
    cache.CacheUser(5L, 100L)
    cache.CacheChannel(6L, 200L)
    cache.CacheChat(7L)
    cache.Get(ChatId 5L) |> Option.isSome |> should be True
    cache.Get(ChatId 6L) |> Option.isSome |> should be True
    cache.Get(ChatId 7L) |> Option.isSome |> should be True
    cache.Get(ChatId 8L) |> Option.isNone |> should be True

// ---------------------------------------------------------------------------
// Update model mapper
// ---------------------------------------------------------------------------

[<Fact>]
let ``update model maps a text message`` () =
    let peer = TL.PeerUser()
    peer.user_id <- 1L

    let from = TL.PeerUser()
    from.user_id <- 1L

    let m = TL.Message()
    m.id <- 42
    m.peer_id <- peer
    m.from_id <- from
    m.message <- "hello"

    let mapped = UpdateModel.tryMapMessage m
    mapped |> Option.isSome |> should be True
    mapped.Value.Text |> should equal (Some "hello")
    mapped.Value.Chat.Id |> should equal (ChatId 1L)
    mapped.Value.Chat.Kind |> should equal Private
    mapped.Value.From.Id |> should equal (UserId 1L)

[<Fact>]
let ``update model derives sender from peer when from_id is missing in private chat`` () =
    // Telegram omits from_id for private-chat messages: peer_id is the sender.
    let peer = TL.PeerUser()
    peer.user_id <- 393314434L

    let m = TL.Message()
    m.id <- 50
    m.peer_id <- peer
    m.from_id <- null
    m.message <- "/ping"

    let mapped = UpdateModel.tryMapMessage m
    mapped |> Option.isSome |> should be True
    mapped.Value.Text |> should equal (Some "/ping")
    mapped.Value.Chat.Kind |> should equal Private
    mapped.Value.Chat.Id |> should equal (ChatId 393314434L)
    mapped.Value.From.Id |> should equal (UserId 393314434L)

[<Fact>]
let ``update model prefers from_id over peer in private chat`` () =
    let peer = TL.PeerUser()
    peer.user_id <- 1L

    let from = TL.PeerUser()
    from.user_id <- 2L

    let m = TL.Message()
    m.id <- 51
    m.peer_id <- peer
    m.from_id <- from
    m.message <- "forwarded"

    let mapped = UpdateModel.tryMapMessage m
    mapped |> Option.isSome |> should be True
    mapped.Value.From.Id |> should equal (UserId 2L)

[<Fact>]
let ``update model drops group message without from_id`` () =
    let peer = TL.PeerChat()
    peer.chat_id <- 7L

    let m = TL.Message()
    m.id <- 52
    m.peer_id <- peer
    m.from_id <- null
    m.message <- "anon"

    let mapped = UpdateModel.tryMapMessage m
    mapped |> Option.isNone |> should be True

[<Fact>]
let ``update model maps a voice message`` () =
    let peer = TL.PeerUser()
    peer.user_id <- 1L

    let from = TL.PeerUser()
    from.user_id <- 1L

    let doc = TL.Document()
    doc.mime_type <- "audio/ogg"
    doc.file_reference <- [| 1uy |]
    doc.access_hash <- 99L

    let media = TL.MessageMediaDocument()
    media.document <- doc

    let m = TL.Message()
    m.id <- 43
    m.peer_id <- peer
    m.from_id <- from
    m.media <- media

    let mapped = UpdateModel.tryMapMessage m
    mapped |> Option.isSome |> should be True
    mapped.Value.Text |> should equal None

    let voice = mapped.Value.Voice
    voice |> Option.isSome |> should be True
    voice.Value.MessageId |> should equal 43L
    voice.Value.AccessHash |> should equal 99L
    voice.Value.FileReference |> should equal [| 1uy |]

[<Fact>]
let ``update model ignores service messages`` () =
    let svc = TL.MessageService()
    let mapped = UpdateModel.tryMapMessage svc
    mapped |> Option.isNone |> should be True

// ---------------------------------------------------------------------------
// Transport module (pure helpers)
// ---------------------------------------------------------------------------

[<Fact>]
let ``toTLMessageEntity maps every entity kind`` () =
    let mk kind =
        let e = Transport.toTLMessageEntity { Offset = 1; Length = 2; Kind = kind }
        e.offset |> should equal 1
        e.length |> should equal 2
        e

    (mk Bold) :? TL.MessageEntityBold |> should be True
    (mk Italic) :? TL.MessageEntityItalic |> should be True
    (mk Code) :? TL.MessageEntityCode |> should be True
    (mk Pre) :? TL.MessageEntityPre |> should be True
    (mk TextUrl) :? TL.MessageEntityTextUrl |> should be True
    (mk Mention) :? TL.MessageEntityMention |> should be True
    (mk Hashtag) :? TL.MessageEntityHashtag |> should be True
    (mk Unknown) :? TL.MessageEntityUnknown |> should be True

[<Fact>]
let ``extractRemoteMessageId reads id from sent message updates`` () =
    let sent = TL.UpdateShortSentMessage()
    sent.id <- 55
    Transport.extractRemoteMessageId sent |> should equal 55L

    let short = TL.UpdateShortMessage()
    short.id <- 66
    Transport.extractRemoteMessageId short |> should equal 66L

    let other = TL.Updates()
    Transport.extractRemoteMessageId other |> should equal 0L

[<Fact>]
let ``mapRpcError recognizes flood and slowmode waits and falls back to Other`` () =
    Transport.mapRpcError (Exception "FLOOD_WAIT_1") |> should equal (FloodWait 1)
    Transport.mapRpcError (Exception "FLOOD_WAIT5") |> should equal (FloodWait 5)

    Transport.mapRpcError (Exception "SLOWMODE_WAIT_0")
    |> should equal (SlowModeWait 0)

    Transport.mapRpcError (Exception "boom") |> should equal (Other "boom")

[<Fact>]
let ``buildSendRequest sets peer, message, random id and entities`` () =
    let peer = TL.InputPeerUser(1L, 2L) :> TL.InputPeer

    let target =
        { ChatId = ChatId 1L
          RandomId = 42L
          Text = "hi"
          Entities = [ { Offset = 0; Length = 2; Kind = Bold } ] }

    let req = Transport.buildSendRequest peer target
    req.peer |> should not' (be null)
    req.message |> should equal "hi"
    req.random_id |> should equal 42L
    req.entities |> should haveLength 1
    req.entities.[0].offset |> should equal 0

    let noEntities =
        { ChatId = ChatId 1L
          RandomId = 43L
          Text = "hi"
          Entities = [] }

    let req2 = Transport.buildSendRequest peer noEntities
    req2.message |> should equal "hi"
    req2.random_id |> should equal 43L

[<Fact>]
let ``buildEditRequest sets peer, id, message and entities`` () =
    let peer = TL.InputPeerUser(1L, 2L) :> TL.InputPeer

    let req =
        Transport.buildEditRequest
            peer
            55L
            "new text"
            [ { Offset = 0
                Length = 8
                Kind = Italic } ]

    req.peer |> should not' (be null)
    req.id |> should equal 55
    req.message |> should equal "new text"
    req.entities |> should haveLength 1
    req.entities.[0].length |> should equal 8

    let req2 = Transport.buildEditRequest peer 56L "more" []
    req2.message |> should equal "more"

[<Fact>]
let ``resolvePeer returns a cached peer or throws`` () =
    let cache = PeerCache()
    cache.CacheUser(5L, 100L)
    let peer = Transport.resolvePeer cache (ChatId 5L)
    peer |> should not' (be null)

    (fun () -> Transport.resolvePeer cache (ChatId 999L) |> ignore)
    |> should throw typeof<Exception>

[<Fact>]
let ``cachePeers populates user and channel peers from updates`` () =
    let updates = TL.Updates()
    updates.users <- System.Collections.Generic.Dictionary<_, _>()
    updates.chats <- System.Collections.Generic.Dictionary<_, _>()

    let u = TL.User()
    u.id <- 5L
    u.access_hash <- 100L
    updates.users.[5L] <- u

    let ch = TL.Channel()
    ch.id <- 6L
    ch.access_hash <- 200L
    updates.chats.[6L] <- ch

    let cache = PeerCache()
    Transport.cachePeers cache updates
    cache.Get(ChatId 5L) |> Option.isSome |> should be True
    cache.Get(ChatId 6L) |> Option.isSome |> should be True

[<Fact>]
let ``cachePeers ignores null users and non-channel chats`` () =
    let updates = TL.Updates()
    updates.users <- System.Collections.Generic.Dictionary<_, _>()
    updates.chats <- System.Collections.Generic.Dictionary<_, _>()

    // A null user entry must be skipped.
    updates.users.[5L] <- null

    // A basic group (not a Channel) must be skipped.
    let chat = TL.Chat()
    chat.id <- 6L
    updates.chats.[6L] <- chat

    let cache = PeerCache()
    Transport.cachePeers cache updates
    cache.Get(ChatId 5L) |> Option.isNone |> should be True
    cache.Get(ChatId 6L) |> Option.isNone |> should be True

[<Fact>]
let ``ensureSessionDir creates the session directory`` () =
    let dir = makeTempDir ()

    try
        let sessionPath = Path.Combine(dir, "sub", "bot.session")
        Transport.ensureSessionDir sessionPath
        Directory.Exists(Path.Combine(dir, "sub")) |> should be True
    finally
        deleteDir dir

[<Fact>]
let ``ensureSessionDir handles null and dirless paths`` () =
    Transport.ensureSessionDir null
    Transport.ensureSessionDir "bot.session"

[<Fact>]
let ``secureSessionFile sets the mode without throwing`` () =
    let dir = makeTempDir ()

    try
        let path = Path.Combine(dir, "bot.session")
        File.WriteAllText(path, "")
        Transport.secureSessionFile path
        File.Exists path |> should be True
        Transport.secureSessionFile (Path.Combine(dir, "missing.session"))
    finally
        deleteDir dir

[<Fact>]
let ``extractBotInfo builds BotInfo from a user`` () =
    let u = TL.User()
    u.id <- 7L
    u.username <- "phos_bot"
    let info = Transport.extractBotInfo 7L u
    info.BotId |> should equal 7L
    info.Username |> should equal (Some "phos_bot")

[<Fact>]
let ``tryVoiceDocument extracts a voice document from a message`` () =
    let peer = TL.PeerUser()
    peer.user_id <- 1L

    let from = TL.PeerUser()
    from.user_id <- 1L

    let doc = TL.Document()
    doc.id <- 1L

    let media = TL.MessageMediaDocument()
    media.document <- doc

    let m = TL.Message()
    m.id <- 10
    m.peer_id <- peer
    m.from_id <- from
    m.media <- media

    Transport.tryVoiceDocument (Some(m :> TL.MessageBase))
    |> Option.isSome
    |> should be True

    let m2 = TL.Message()
    m2.id <- 11
    m2.peer_id <- peer
    m2.from_id <- from

    Transport.tryVoiceDocument (Some(m2 :> TL.MessageBase))
    |> Option.isNone
    |> should be True

    let photoMedia = TL.MessageMediaPhoto()

    let m3 = TL.Message()
    m3.id <- 12
    m3.peer_id <- peer
    m3.from_id <- from
    m3.media <- photoMedia

    Transport.tryVoiceDocument (Some(m3 :> TL.MessageBase))
    |> Option.isNone
    |> should be True

    Transport.tryVoiceDocument (Some(TL.MessageService() :> TL.MessageBase))
    |> Option.isNone
    |> should be True

    Transport.tryVoiceDocument None |> Option.isNone |> should be True

// ---------------------------------------------------------------------------
// Update model additional branches
// ---------------------------------------------------------------------------

[<Fact>]
let ``update model maps a group chat and empty text`` () =
    let peer = TL.PeerChat()
    peer.chat_id <- 7L

    let from = TL.PeerUser()
    from.user_id <- 1L

    let m = TL.Message()
    m.id <- 50
    m.peer_id <- peer
    m.from_id <- from
    m.message <- ""

    let mapped = UpdateModel.tryMapMessage m
    mapped |> Option.isSome |> should be True
    mapped.Value.Chat.Kind |> should equal Group
    mapped.Value.Chat.Id |> should equal (ChatId 7L)
    mapped.Value.Text |> should equal None

[<Fact>]
let ``update model maps a whole update batch`` () =
    let updates = TL.Updates()
    updates.updates <- [||]

    let peer = TL.PeerUser()
    peer.user_id <- 1L

    let from = TL.PeerUser()
    from.user_id <- 1L

    let m = TL.Message()
    m.id <- 60
    m.peer_id <- peer
    m.from_id <- from
    m.message <- "batch"

    let un = TL.UpdateNewMessage()
    un.message <- m
    updates.updates <- [| un :> TL.Update |]

    let mapped = UpdateModel.mapUpdates updates
    mapped |> should haveLength 1
    mapped.[0].Text |> should equal (Some "batch")

[<Fact>]
let ``update model maps an UpdateNewMessage directly`` () =
    let peer = TL.PeerUser()
    peer.user_id <- 1L

    let from = TL.PeerUser()
    from.user_id <- 1L

    let m = TL.Message()
    m.id <- 61
    m.peer_id <- peer
    m.from_id <- from
    m.message <- "direct"

    let un = TL.UpdateNewMessage()
    un.message <- m

    let mapped = UpdateModel.tryMapUpdateNewMessage un
    mapped |> Option.isSome |> should be True
    mapped.Value.Text |> should equal (Some "direct")

[<Fact>]
let ``update model does not treat a non-voice document as voice`` () =
    let peer = TL.PeerUser()
    peer.user_id <- 1L

    let from = TL.PeerUser()
    from.user_id <- 1L

    let doc = TL.Document()
    doc.mime_type <- "application/pdf"
    doc.access_hash <- 1L

    let media = TL.MessageMediaDocument()
    media.document <- doc

    let m = TL.Message()
    m.id <- 70
    m.peer_id <- peer
    m.from_id <- from
    m.media <- media

    let mapped = UpdateModel.tryMapMessage m
    mapped |> Option.isSome |> should be True
    mapped.Value.Voice |> Option.isNone |> should be True

[<Fact>]
let ``update model maps a channel chat`` () =
    let peer = TL.PeerChannel()
    peer.channel_id <- 8L

    let from = TL.PeerUser()
    from.user_id <- 1L

    let m = TL.Message()
    m.id <- 80
    m.peer_id <- peer
    m.from_id <- from
    m.message <- "channel post"

    let mapped = UpdateModel.tryMapMessage m
    mapped |> Option.isSome |> should be True
    mapped.Value.Chat.Kind |> should equal Channel
    mapped.Value.Chat.Id |> should equal (ChatId 8L)

[<Fact>]
let ``update model ignores non-user senders in group chats`` () =
    let peer = TL.PeerChat()
    peer.chat_id <- 7L

    let from = TL.PeerChannel()
    from.channel_id <- 9L

    let m = TL.Message()
    m.id <- 81
    m.peer_id <- peer
    m.from_id <- (from :> TL.Peer)
    m.message <- "post"

    let mapped = UpdateModel.tryMapMessage m
    mapped |> Option.isNone |> should be True

[<Fact>]
let ``update model maps an opus voice message`` () =
    let peer = TL.PeerUser()
    peer.user_id <- 1L

    let from = TL.PeerUser()
    from.user_id <- 1L

    let doc = TL.Document()
    doc.mime_type <- "audio/opus"
    doc.access_hash <- 2L

    let media = TL.MessageMediaDocument()
    media.document <- doc

    let m = TL.Message()
    m.id <- 82
    m.peer_id <- peer
    m.from_id <- from
    m.media <- media

    let mapped = UpdateModel.tryMapMessage m
    mapped |> Option.isSome |> should be True
    mapped.Value.Voice |> Option.isSome |> should be True

[<Fact>]
let ``update model treats a null mime type as non-voice`` () =
    let peer = TL.PeerUser()
    peer.user_id <- 1L

    let from = TL.PeerUser()
    from.user_id <- 1L

    let doc = TL.Document()
    // `mime_type` is an external WTelegramClient property; the interop type is
    // unannotated so a literal null is accepted without a nullness warning.
    doc.mime_type <- null
    doc.access_hash <- 3L

    let media = TL.MessageMediaDocument()
    media.document <- doc

    let m = TL.Message()
    m.id <- 90
    m.peer_id <- peer
    m.from_id <- from
    m.media <- media

    let mapped = UpdateModel.tryMapMessage m
    mapped |> Option.isSome |> should be True
    mapped.Value.Voice |> Option.isNone |> should be True

[<Fact>]
let ``update model maps a null message text to None`` () =
    let peer = TL.PeerUser()
    peer.user_id <- 1L

    let from = TL.PeerUser()
    from.user_id <- 1L

    let m = TL.Message()
    m.id <- 91
    m.peer_id <- peer
    m.from_id <- from
    // `message` is an external WTelegramClient property; the interop type is
    // unannotated so a literal null is accepted without a nullness warning.
    m.message <- null

    let mapped = UpdateModel.tryMapMessage m
    mapped |> Option.isSome |> should be True
    mapped.Value.Text |> should equal None

[<Fact>]
let ``extractBotInfo handles a user without a username`` () =
    let u = TL.User()
    u.id <- 8L
    let info = Transport.extractBotInfo 8L u
    info.BotId |> should equal 8L
    info.Username |> should equal None

// ---------------------------------------------------------------------------
// Update handler additional branches
// ---------------------------------------------------------------------------

[<Fact>]
let ``group chat with an allowed chat id is processed`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            let exec = createExecutor dbPath

            try
                let inbox, _, users = mkRepos exec
                let dedupe = UpdateDedupe(1000)
                let admitCalls = ResizeArray<CommandEnvelope>()
                let enqueue (env: OutboxEnvelope) = task { () }

                let admit (env: CommandEnvelope) =
                    task {
                        admitCalls.Add env
                        return Admitted 1L
                    }

                let whitelist =
                    Phos.Core.Whitelist.create (Map.ofList [ (UserId 1L, User) ]) (Set.ofList [ ChatId 10L ])

                let handler = UpdateHandler(whitelist, inbox, users, dedupe, admit, enqueue)

                let groupChat = { Id = ChatId 10L; Kind = Group }
                let update = mkUpdate 900L groupChat testUser (Some "hello") None
                let! result = handler.HandleAsync update
                result |> should equal Accepted
                admitCalls.Count |> should equal 1
            finally
                dispose exec
        }
    finally
        deleteDir dir

[<Fact>]
let ``voice update with text still admits the voice marker`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            let exec = createExecutor dbPath

            try
                let inbox, _, users = mkRepos exec
                let dedupe = UpdateDedupe(1000)
                let admitCalls = ResizeArray<CommandEnvelope>()
                let enqueue (env: OutboxEnvelope) = task { () }

                let admit (env: CommandEnvelope) =
                    task {
                        admitCalls.Add env
                        return Admitted 1L
                    }

                let whitelist =
                    Phos.Core.Whitelist.create (Map.ofList [ (UserId 1L, User) ]) Set.empty

                let handler = UpdateHandler(whitelist, inbox, users, dedupe, admit, enqueue)

                let voiceRef =
                    { ChatId = testChat.Id
                      MessageId = 501L
                      FileReference = [| 1uy |]
                      AccessHash = 5L }

                let update = mkUpdate 901L testChat testUser (Some "caption") (Some voiceRef)
                let! result = handler.HandleAsync update
                result |> should equal Accepted
                admitCalls.Count |> should equal 1
                admitCalls.[0].Payload |> should equal "voice:501"
            finally
                dispose exec
        }
    finally
        deleteDir dir

[<Fact>]
let ``empty update admits an empty command payload`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            let exec = createExecutor dbPath

            try
                let inbox, _, users = mkRepos exec
                let dedupe = UpdateDedupe(1000)
                let admitCalls = ResizeArray<CommandEnvelope>()
                let enqueue (env: OutboxEnvelope) = task { () }

                let admit (env: CommandEnvelope) =
                    task {
                        admitCalls.Add env
                        return Admitted 1L
                    }

                let whitelist =
                    Phos.Core.Whitelist.create (Map.ofList [ (UserId 1L, User) ]) Set.empty

                let handler = UpdateHandler(whitelist, inbox, users, dedupe, admit, enqueue)

                let update = mkUpdate 902L testChat testUser None None
                let! result = handler.HandleAsync update
                result |> should equal Accepted
                admitCalls.Count |> should equal 1
                admitCalls.[0].Payload |> should equal ""
            finally
                dispose exec
        }
    finally
        deleteDir dir

[<Fact>]
let ``unknown slash command is admitted as a command`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            let exec = createExecutor dbPath

            try
                let inbox, _, users = mkRepos exec
                let dedupe = UpdateDedupe(1000)
                let admitCalls = ResizeArray<CommandEnvelope>()
                let enqueue (env: OutboxEnvelope) = task { () }

                let admit (env: CommandEnvelope) =
                    task {
                        admitCalls.Add env
                        return Admitted 1L
                    }

                let whitelist =
                    Phos.Core.Whitelist.create (Map.ofList [ (UserId 1L, User) ]) Set.empty

                let handler = UpdateHandler(whitelist, inbox, users, dedupe, admit, enqueue)

                let update = mkUpdate 903L testChat testUser (Some "/help") None
                let! result = handler.HandleAsync update
                result |> should equal Accepted
                admitCalls.Count |> should equal 1
                admitCalls.[0].Payload |> should equal "/help"
            finally
                dispose exec
        }
    finally
        deleteDir dir

[<Fact>]
let ``voice update in an allowed group chat is admitted`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            let exec = createExecutor dbPath

            try
                let inbox, _, users = mkRepos exec
                let dedupe = UpdateDedupe(1000)
                let admitCalls = ResizeArray<CommandEnvelope>()
                let enqueue (env: OutboxEnvelope) = task { () }

                let admit (env: CommandEnvelope) =
                    task {
                        admitCalls.Add env
                        return Admitted 1L
                    }

                let whitelist =
                    Phos.Core.Whitelist.create (Map.ofList [ (UserId 1L, User) ]) (Set.ofList [ ChatId 20L ])

                let handler = UpdateHandler(whitelist, inbox, users, dedupe, admit, enqueue)

                let groupChat = { Id = ChatId 20L; Kind = Group }

                let voiceRef =
                    { ChatId = groupChat.Id
                      MessageId = 502L
                      FileReference = [| 2uy |]
                      AccessHash = 6L }

                let update = mkUpdate 904L groupChat testUser None (Some voiceRef)
                let! result = handler.HandleAsync update
                result |> should equal Accepted
                admitCalls.Count |> should equal 1
                admitCalls.[0].Payload |> should equal "voice:502"
            finally
                dispose exec
        }
    finally
        deleteDir dir

[<Fact>]
let ``group chat not in whitelist is denied with ChatNotAllowed`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            let exec = createExecutor dbPath

            try
                let inbox, _, users = mkRepos exec
                let dedupe = UpdateDedupe(1000)
                let admitCalls = ResizeArray<CommandEnvelope>()
                let enqueue (env: OutboxEnvelope) = task { () }

                let admit (env: CommandEnvelope) =
                    task {
                        admitCalls.Add env
                        return Admitted 1L
                    }

                let whitelist =
                    Phos.Core.Whitelist.create (Map.ofList [ (UserId 1L, User) ]) Set.empty

                let handler = UpdateHandler(whitelist, inbox, users, dedupe, admit, enqueue)

                let groupChat = { Id = ChatId 30L; Kind = Group }
                let update = mkUpdate 905L groupChat testUser (Some "hello") None
                let! result = handler.HandleAsync update
                result |> should equal (Denied ChatNotAllowed)
                admitCalls.Count |> should equal 0
            finally
                dispose exec
        }
    finally
        deleteDir dir

[<Fact>]
let ``start in a channel with an allowed chat id upserts the user`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            let exec = createExecutor dbPath

            try
                let inbox, _, users = mkRepos exec
                let dedupe = UpdateDedupe(1000)
                let admitCalls = ResizeArray<CommandEnvelope>()
                let outboxCalls = ResizeArray<OutboxEnvelope>()

                let admit (env: CommandEnvelope) =
                    task {
                        admitCalls.Add env
                        return Admitted 1L
                    }

                let enqueue (env: OutboxEnvelope) = task { outboxCalls.Add env }

                let whitelist =
                    Phos.Core.Whitelist.create (Map.ofList [ (UserId 1L, User) ]) (Set.ofList [ ChatId 40L ])

                let handler = UpdateHandler(whitelist, inbox, users, dedupe, admit, enqueue)

                let channel = { Id = ChatId 40L; Kind = Channel }
                let update = mkUpdate 906L channel testUser (Some "/start") None
                let! result = handler.HandleAsync update
                result |> should equal Accepted

                let! user = users.GetByTelegramId(UserId 1L)
                user |> Option.isSome |> should be True
                admitCalls.Count |> should equal 0
                outboxCalls.Count |> should equal 1
            finally
                dispose exec
        }
    finally
        deleteDir dir

[<Fact>]
let ``ping with trailing text still replies pong`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            let exec = createExecutor dbPath

            try
                let inbox, _, users = mkRepos exec
                let dedupe = UpdateDedupe(1000)
                let admitCalls = ResizeArray<CommandEnvelope>()
                let enqueue (env: OutboxEnvelope) = task { () }

                let admit (env: CommandEnvelope) =
                    task {
                        admitCalls.Add env
                        return Admitted 1L
                    }

                let whitelist =
                    Phos.Core.Whitelist.create (Map.ofList [ (UserId 1L, User) ]) Set.empty

                let handler = UpdateHandler(whitelist, inbox, users, dedupe, admit, enqueue)

                let update = mkUpdate 907L testChat testUser (Some "/ping please") None
                let! result = handler.HandleAsync update
                result |> should equal Accepted
                admitCalls.Count |> should equal 0
            finally
                dispose exec
        }
    finally
        deleteDir dir

[<Fact>]
let ``ping in an allowed group chat is accepted`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            let exec = createExecutor dbPath

            try
                let inbox, _, users = mkRepos exec
                let dedupe = UpdateDedupe(1000)
                let admitCalls = ResizeArray<CommandEnvelope>()
                let enqueue (env: OutboxEnvelope) = task { () }

                let admit (env: CommandEnvelope) =
                    task {
                        admitCalls.Add env
                        return Admitted 1L
                    }

                let whitelist =
                    Phos.Core.Whitelist.create (Map.ofList [ (UserId 1L, User) ]) (Set.ofList [ ChatId 50L ])

                let handler = UpdateHandler(whitelist, inbox, users, dedupe, admit, enqueue)

                let groupChat = { Id = ChatId 50L; Kind = Group }
                let update = mkUpdate 908L groupChat testUser (Some "/ping") None
                let! result = handler.HandleAsync update
                result |> should equal Accepted
                admitCalls.Count |> should equal 0
            finally
                dispose exec
        }
    finally
        deleteDir dir

[<Fact>]
let ``empty text string is admitted as an empty command`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            let exec = createExecutor dbPath

            try
                let inbox, _, users = mkRepos exec
                let dedupe = UpdateDedupe(1000)
                let admitCalls = ResizeArray<CommandEnvelope>()
                let enqueue (env: OutboxEnvelope) = task { () }

                let admit (env: CommandEnvelope) =
                    task {
                        admitCalls.Add env
                        return Admitted 1L
                    }

                let whitelist =
                    Phos.Core.Whitelist.create (Map.ofList [ (UserId 1L, User) ]) Set.empty

                let handler = UpdateHandler(whitelist, inbox, users, dedupe, admit, enqueue)

                let update = mkUpdate 909L testChat testUser (Some "") None
                let! result = handler.HandleAsync update
                result |> should equal Accepted
                admitCalls.Count |> should equal 1
                admitCalls.[0].Payload |> should equal ""
            finally
                dispose exec
        }
    finally
        deleteDir dir

[<Fact>]
let ``start with a user that has no username still upserts`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            let exec = createExecutor dbPath

            try
                let inbox, _, users = mkRepos exec
                let dedupe = UpdateDedupe(1000)
                let enqueue (env: OutboxEnvelope) = task { () }
                let admit _ = task { return Admitted 1L }

                let whitelist =
                    Phos.Core.Whitelist.create (Map.ofList [ (UserId 1L, User) ]) Set.empty

                let handler = UpdateHandler(whitelist, inbox, users, dedupe, admit, enqueue)

                let noUsername =
                    { Id = UserId 1L
                      Username = None
                      Role = User }

                let update = mkUpdate 910L testChat noUsername (Some "/start") None
                let! result = handler.HandleAsync update
                result |> should equal Accepted

                let! user = users.GetByTelegramId(UserId 1L)
                user |> Option.isSome |> should be True
                user.Value.Username |> should equal None
            finally
                dispose exec
        }
    finally
        deleteDir dir

[<Fact>]
let ``ping with a voice still replies pong`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            let exec = createExecutor dbPath

            try
                let inbox, _, users = mkRepos exec
                let dedupe = UpdateDedupe(1000)
                let admitCalls = ResizeArray<CommandEnvelope>()
                let enqueue (env: OutboxEnvelope) = task { () }

                let admit (env: CommandEnvelope) =
                    task {
                        admitCalls.Add env
                        return Admitted 1L
                    }

                let whitelist =
                    Phos.Core.Whitelist.create (Map.ofList [ (UserId 1L, User) ]) Set.empty

                let handler = UpdateHandler(whitelist, inbox, users, dedupe, admit, enqueue)

                let voiceRef =
                    { ChatId = testChat.Id
                      MessageId = 503L
                      FileReference = [| 3uy |]
                      AccessHash = 7L }

                let update = mkUpdate 911L testChat testUser (Some "/ping") (Some voiceRef)
                let! result = handler.HandleAsync update
                result |> should equal Accepted
                admitCalls.Count |> should equal 0
            finally
                dispose exec
        }
    finally
        deleteDir dir

[<Fact>]
let ``admit failure returns AdmitFailed and enqueues nothing`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            let exec = createExecutor dbPath

            try
                let inbox, _, users = mkRepos exec
                let dedupe = UpdateDedupe(1000)
                let admitCalls = ResizeArray<CommandEnvelope>()
                let outboxCalls = ResizeArray<OutboxEnvelope>()

                let admit (env: CommandEnvelope) =
                    task {
                        admitCalls.Add env
                        return Failed
                    }

                let enqueue (env: OutboxEnvelope) = task { outboxCalls.Add env }

                let whitelist =
                    Phos.Core.Whitelist.create (Map.ofList [ (UserId 1L, User) ]) Set.empty

                let handler = UpdateHandler(whitelist, inbox, users, dedupe, admit, enqueue)

                let update = mkUpdate 920L testChat testUser (Some "hello") None
                let! result = handler.HandleAsync update
                result |> should equal AdmitFailed
                admitCalls.Count |> should equal 1
                outboxCalls.Count |> should equal 0
            finally
                dispose exec
        }
    finally
        deleteDir dir

[<Fact>]
let ``update without text or voice is admitted with an empty payload`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            let exec = createExecutor dbPath

            try
                let inbox, _, users = mkRepos exec
                let dedupe = UpdateDedupe(1000)
                let admitCalls = ResizeArray<CommandEnvelope>()
                let enqueue (env: OutboxEnvelope) = task { () }

                let admit (env: CommandEnvelope) =
                    task {
                        admitCalls.Add env
                        return Admitted 1L
                    }

                let whitelist =
                    Phos.Core.Whitelist.create (Map.ofList [ (UserId 1L, User) ]) Set.empty

                let handler = UpdateHandler(whitelist, inbox, users, dedupe, admit, enqueue)

                let update = mkUpdate 921L testChat testUser None None
                let! result = handler.HandleAsync update
                result |> should equal Accepted
                admitCalls.Count |> should equal 1
                admitCalls.[0].Payload |> should equal ""
            finally
                dispose exec
        }
    finally
        deleteDir dir

[<Fact>]
let ``dedupe evicts the least recently used id beyond capacity`` () =
    let dedupe = UpdateDedupe(2)
    dedupe.TryAdd 1L |> should be True
    dedupe.TryAdd 2L |> should be True
    dedupe.TryAdd 3L |> should be True
    dedupe.TryAdd 1L |> should be True
    dedupe.Count |> should equal 2
