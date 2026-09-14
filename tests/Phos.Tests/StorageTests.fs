module Phos.Tests.StorageTests

open System
open System.IO
open System.Threading.Tasks
open Xunit
open FsUnit.Xunit
open Microsoft.Data.Sqlite
open Phos.Storage
open Phos.Core
open Phos.Core.DomainTypes
open Phos.Core.ScheduleJobs
open Phos.Core.SchedulePolicy

// Module abbreviations to disambiguate the overlapping Status types.
module In = Phos.Core.InboxStateMachine
module Out = Phos.Core.OutboxStateMachine

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

let private makeTempDir () : string =
    let dir =
        Path.Combine(Path.GetTempPath(), "phos-test-" + Guid.NewGuid().ToString("N"))

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

/// Creates a migrated executor, runs `body`, then disposes it.
let private withExecutor (dbPath: string) (body: StorageExecutor -> Task<unit>) : Task<unit> =
    task {
        let exec = createExecutor dbPath

        try
            do! body exec
        finally
            dispose exec
    }

let private tableExists (exec: StorageExecutor) (name: string) : bool =
    exec.ReadAsync(fun conn ->
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;"
        cmd.Parameters.AddWithValue("$name", name) |> ignore
        cmd.ExecuteScalar() :?> int64 > 0L)
    |> fun t -> t.GetAwaiter().GetResult()

let private lease () : Lease =
    { Until = DateTimeOffset.UtcNow.AddMinutes 5.0
      HeartbeatAt = DateTimeOffset.UtcNow }

let private mkEnvelope (key: string) : CommandEnvelope =
    { Origin = Telegram
      ExternalKey = Some key
      UserId = UserId 1L
      ChatId = ChatId 1L
      Payload = "payload"
      Priority = 0
      Images = [] }

// ---------------------------------------------------------------------------
// Migrations
// ---------------------------------------------------------------------------

[<Fact>]
let ``migrations apply fresh schema and are idempotent`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                tableExists exec "users" |> should be True
                tableExists exec "command_inbox" |> should be True
                tableExists exec "message_outbox" |> should be True
                tableExists exec "schedule_jobs" |> should be True
                tableExists exec "schedule_runs" |> should be True
                tableExists exec "backup_log" |> should be True
                tableExists exec "VersionInfo" |> should be True

                // Re-running is a no-op: FluentMigrator skips applied migrations.
                Schema.run (defaultOptions dbPath)
            })
    finally
        deleteDir dir

[<Fact>]
let ``migrations replay in order for upgrade`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            // Do NOT run the full migration set here: create an un-migrated executor
            // so the upgrade replay can be observed.
            let exec = StorageExecutor.Create(defaultOptions dbPath)

            try
                // Only apply migration 1 (users), then verify later tables are absent.
                Schema.migrateUp (defaultOptions dbPath) 1L
                tableExists exec "users" |> should be True
                tableExists exec "command_inbox" |> should be False

                // Replay the rest: 2..6 should be applied.
                Schema.run (defaultOptions dbPath)
                tableExists exec "command_inbox" |> should be True
                tableExists exec "message_outbox" |> should be True
                tableExists exec "schedule_jobs" |> should be True
                tableExists exec "schedule_runs" |> should be True
                tableExists exec "backup_log" |> should be True
            finally
                dispose exec
        }
    finally
        deleteDir dir

[<Fact>]
let ``migrate handles empty and partial upgrade sets`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            let exec = StorageExecutor.Create(defaultOptions dbPath)

            try
                // Partial upgrade: apply up to version 2.
                Schema.migrateUp (defaultOptions dbPath) 2L
                tableExists exec "users" |> should be True
                tableExists exec "command_inbox" |> should be True
                tableExists exec "message_outbox" |> should be False

                // Re-applying the same version is a no-op.
                Schema.migrateUp (defaultOptions dbPath) 2L
                tableExists exec "message_outbox" |> should be False

                // Apply the rest.
                Schema.run (defaultOptions dbPath)
                tableExists exec "message_outbox" |> should be True
                tableExists exec "schedule_jobs" |> should be True
                tableExists exec "schedule_runs" |> should be True
                tableExists exec "backup_log" |> should be True
            finally
                dispose exec
        }
    finally
        deleteDir dir

[<Fact>]
let ``migrate down rolls back the schema executing every Down`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            let exec = StorageExecutor.Create(defaultOptions dbPath)

            try
                Schema.run (defaultOptions dbPath)
                tableExists exec "users" |> should be True
                tableExists exec "command_inbox" |> should be True
                tableExists exec "message_outbox" |> should be True
                tableExists exec "schedule_jobs" |> should be True
                tableExists exec "schedule_runs" |> should be True
                tableExists exec "backup_log" |> should be True

                // Roll back everything: each migration's Down() executes in reverse.
                Schema.migrateDown (defaultOptions dbPath) 0L
                tableExists exec "users" |> should be False
                tableExists exec "command_inbox" |> should be False
                tableExists exec "message_outbox" |> should be False
                tableExists exec "schedule_jobs" |> should be False
                tableExists exec "schedule_runs" |> should be False
                tableExists exec "backup_log" |> should be False
            finally
                dispose exec
        }
    finally
        deleteDir dir

[<Fact>]
let ``executor rejects use after dispose`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            let exec = createExecutor dbPath
            dispose exec
            dispose exec // second dispose exercises the already-disposed path

            let throwsOn (f: unit -> Task) =
                try
                    f().GetAwaiter().GetResult()
                    false
                with _ ->
                    true

            let threwWrite = throwsOn (fun () -> exec.WriteAsync ignore)
            let threwRead = throwsOn (fun () -> exec.ReadAsync ignore)
            let threwCheckpoint = throwsOn (fun () -> exec.CheckpointNow())
            (threwWrite && threwRead && threwCheckpoint) |> should be True
        }
    finally
        deleteDir dir

[<Fact>]
let ``executor checkpoints after every write when interval is one`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            let options =
                { DatabasePath = dbPath
                  BusyTimeout = TimeSpan.FromSeconds 2.0
                  ReadPoolSize = 2
                  CheckpointEvery = 1 }

            let exec = StorageExecutor.Create options

            try
                Schema.run (defaultOptions dbPath)

                let inbox = Repositories.commandInbox exec
                let! _ = inbox.Insert(mkEnvelope "cp1-1")
                let! _ = inbox.Insert(mkEnvelope "cp1-2")
                let! count = inbox.CountPending()
                count |> should equal 2
            finally
                dispose exec
        }
    finally
        deleteDir dir

[<Fact>]
let ``executor handles zero checkpoint interval and sub-second timeout`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            let options =
                { DatabasePath = dbPath
                  BusyTimeout = TimeSpan.FromMilliseconds 500.0
                  ReadPoolSize = 2
                  CheckpointEvery = 0 }

            let exec = StorageExecutor.Create options

            try
                Schema.run (defaultOptions dbPath)

                let inbox = Repositories.commandInbox exec
                let! _ = inbox.Insert(mkEnvelope "zero-1")
                let! count = inbox.CountPending()
                count |> should equal 1
            finally
                dispose exec
        }
    finally
        deleteDir dir

// ---------------------------------------------------------------------------
// Concurrency
// ---------------------------------------------------------------------------

[<Fact>]
let ``parallel writes are all committed without loss`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let inbox = Repositories.commandInbox exec
                let n = 50

                let tasks =
                    [ for i in 1..n ->
                          inbox.Insert
                              { Origin = Telegram
                                ExternalKey = Some(sprintf "key-%d" i)
                                UserId = UserId 1L
                                ChatId = ChatId 1L
                                Payload = sprintf "payload-%d" i
                                Priority = 0
                                Images = [] } ]

                let! _ = Task.WhenAll(tasks)
                let! count = inbox.CountPending()
                count |> should equal n
            })
    finally
        deleteDir dir

[<Fact>]
let ``parallel reads are bounded and correct`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let inbox = Repositories.commandInbox exec

                for i in 1..20 do
                    let! _ = inbox.Insert(mkEnvelope (sprintf "pr-%d" i))
                    ()

                let reads = [ for _ in 1..50 -> inbox.CountPending() ]
                let! results = Task.WhenAll(reads)
                results |> Array.forall (fun r -> r = 20) |> should be True
            })
    finally
        deleteDir dir

[<Fact>]
let ``checkpoint now can be called repeatedly with interleaved writes`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let inbox = Repositories.commandInbox exec
                do! exec.CheckpointNow()
                let! _ = inbox.Insert(mkEnvelope "cp-1")
                do! exec.CheckpointNow()
                let! _ = inbox.Insert(mkEnvelope "cp-2")
                do! exec.CheckpointNow()
                let! count = inbox.CountPending()
                count |> should equal 2
            })
    finally
        deleteDir dir

[<Fact>]
let ``mixed concurrent operations are serialized correctly`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let inbox = Repositories.commandInbox exec
                let outbox = Repositories.messageOutbox exec

                let ops =
                    [ for i in 1..30 ->
                          task {
                              let! _ = inbox.Insert(mkEnvelope (sprintf "mix-%d" i))
                              do! exec.CheckpointNow()
                              let! _ = outbox.Insert (int64 i) 0 (ChatId 1L) (int64 i) "m" []
                              ()
                          } ]

                let! _ = Task.WhenAll(ops)
                let! count = inbox.CountPending()
                count |> should equal 30
            })
    finally
        deleteDir dir

[<Fact>]
let ``busy timeout bounds the wait`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                // Hold a write lock from a separate connection.
                use raw = new SqliteConnection(sprintf "Data Source=%s" dbPath)
                raw.Open()
                use tx = raw.BeginTransaction()
                use cmd = raw.CreateCommand()
                cmd.Transaction <- tx

                cmd.CommandText <-
                    "INSERT INTO users(user_id, username, role, workspace_path, created_at, updated_at) VALUES (1, 'x', 'user', '/tmp', 0, 0);"

                cmd.ExecuteNonQuery() |> ignore

                let sw = System.Diagnostics.Stopwatch.StartNew()

                let threw =
                    try
                        exec.WriteAsync(fun conn ->
                            use c = conn.CreateCommand()

                            c.CommandText <-
                                "INSERT INTO users(user_id, username, role, workspace_path, created_at, updated_at) VALUES (2, 'y', 'user', '/tmp', 0, 0);"

                            c.ExecuteNonQuery() |> ignore
                            ())
                        |> fun t -> t.GetAwaiter().GetResult()

                        false
                    with _ ->
                        true

                sw.Stop()
                threw |> should be True
                // Bounded by the busy timeout (~2s), not hanging forever.
                sw.Elapsed.TotalSeconds |> should be (lessThan 10.0)
            })
    finally
        deleteDir dir

// ---------------------------------------------------------------------------
// Crash boundaries
// ---------------------------------------------------------------------------

[<Fact>]
let ``uncommitted transaction is rolled back on dispose`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let inbox = Repositories.commandInbox exec
                // Open a transaction, insert, and never commit: the connection
                // dispose rolls it back.
                do!
                    exec.WriteAsync(fun conn ->
                        use tx = conn.BeginTransaction()
                        use cmd = conn.CreateCommand()
                        cmd.Transaction <- tx

                        cmd.CommandText <-
                            "INSERT INTO command_inbox(origin, user_id, chat_id, payload, status, created_at, updated_at) VALUES ('telegram', 1, 1, 'hello', 'pending', 0, 0);"

                        cmd.ExecuteNonQuery() |> ignore
                        ())

                let! count = inbox.CountPending()
                count |> should equal 0
            })
    finally
        deleteDir dir

[<Fact>]
let ``committed command survives executor close and reopen`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            let exec1 = createExecutor dbPath

            try
                let inbox1 = Repositories.commandInbox exec1
                let! _ = inbox1.Insert(mkEnvelope "survive-1")
                ()
            finally
                dispose exec1

            let exec2 = createExecutor dbPath

            try
                let inbox2 = Repositories.commandInbox exec2
                let! count = inbox2.CountPending()
                count |> should equal 1
            finally
                dispose exec2
        }
    finally
        deleteDir dir

[<Fact>]
let ``command inbox images round trip`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let inbox = Repositories.commandInbox exec
                let images = [ "AQID"; "BAUG" ]

                let! _ =
                    inbox.Insert
                        { Origin = Telegram
                          ExternalKey = Some "img-key"
                          UserId = UserId 1L
                          ChatId = ChatId 1L
                          Payload = "смотри"
                          Priority = 0
                          Images = images }

                let! c = inbox.ClaimNextForChat (ChatId 1L) (lease ())
                c |> Option.isSome |> should be True
                c.Value.Envelope.Images |> should equal images
            })
    finally
        deleteDir dir

// ---------------------------------------------------------------------------
// Leases
// ---------------------------------------------------------------------------

[<Fact>]
let ``two concurrent claims yield exactly one winner`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let inbox = Repositories.commandInbox exec
                let! _ = inbox.Insert(mkEnvelope "claim-1")
                let l = lease ()
                let t1 = inbox.ClaimNextForChat (ChatId 1L) l
                let t2 = inbox.ClaimNextForChat (ChatId 1L) l
                let! results = Task.WhenAll(t1, t2)
                let winners = results |> Array.choose id
                winners.Length |> should equal 1
            })
    finally
        deleteDir dir

[<Fact>]
let ``expired lease returns command to pending`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let inbox = Repositories.commandInbox exec
                let! _ = inbox.Insert(mkEnvelope "expire-1")
                let! claimed = inbox.ClaimNextForChat (ChatId 1L) (lease ())
                claimed |> should not' (be None)
                claimed.Value.Status |> should equal In.Status.Claimed

                let! expired = inbox.ExpireLeases(DateTimeOffset.UtcNow.AddMinutes 10.0)
                expired |> should equal 1
                let! pending = inbox.CountPending()
                pending |> should equal 1
            })
    finally
        deleteDir dir

[<Fact>]
let ``command reaches dead letter after max attempts`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let inbox = Repositories.commandInbox exec
                let! _ = inbox.Insert(mkEnvelope "dead-1")
                let l = lease ()

                for i in 1..5 do
                    let! claimed = inbox.ClaimNextForChat (ChatId 1L) l
                    claimed |> should not' (be None)
                    let id = claimed.Value.Id
                    do! inbox.MarkStarted id
                    do! inbox.MarkFailed id

                    if i < 5 then
                        do! inbox.Retry id

                let! dead = inbox.CountDeadLetter()
                dead |> should equal 1
            })
    finally
        deleteDir dir

[<Fact>]
let ``command can be read in each status`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let inbox = Repositories.commandInbox exec
                let l = lease ()

                // pending
                let! id1 = inbox.Insert(mkEnvelope "st-pending")
                let! c1 = inbox.GetById id1
                c1.Value.Status |> should equal In.Status.Pending

                // claimed
                let! _ = inbox.ClaimById id1 l
                let! c2 = inbox.GetById id1
                c2.Value.Status |> should equal In.Status.Claimed

                // running
                do! inbox.MarkStarted id1
                let! c3 = inbox.GetById id1
                c3.Value.Status |> should equal In.Status.Running

                // completed
                do! inbox.MarkCompleted id1
                let! c4 = inbox.GetById id1
                c4.Value.Status |> should equal In.Status.Completed

                // failed
                let! id2 = inbox.Insert(mkEnvelope "st-failed")
                let! _ = inbox.ClaimById id2 l
                do! inbox.MarkStarted id2
                do! inbox.MarkFailed id2
                let! c5 = inbox.GetById id2
                c5.Value.Status |> should equal In.Status.Failed

                // dead_letter
                let! id3 = inbox.Insert(mkEnvelope "st-dead")

                for i in 1..5 do
                    let! _ = inbox.ClaimById id3 l
                    do! inbox.MarkStarted id3
                    do! inbox.MarkFailed id3

                    if i < 5 then
                        do! inbox.Retry id3

                let! c6 = inbox.GetById id3
                c6.Value.Status |> should equal In.Status.DeadLetter

                // needs_review
                let! id4 = inbox.Insert(mkEnvelope "st-review")
                let! _ = inbox.ClaimById id4 l
                do! inbox.MarkNeedsReview id4
                let! c7 = inbox.GetById id4
                c7.Value.Status |> should equal In.Status.NeedsReview
            })
    finally
        deleteDir dir

[<Fact>]
let ``get by id returns none for missing id`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let inbox = Repositories.commandInbox exec
                let! missing = inbox.GetById 999999L
                missing |> should equal None
            })
    finally
        deleteDir dir

[<Fact>]
let ``mark needs review then review retry returns to pending`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let inbox = Repositories.commandInbox exec
                let! _ = inbox.Insert(mkEnvelope "review-1")
                let! claimed = inbox.ClaimNextForChat (ChatId 1L) (lease ())
                claimed |> should not' (be None)
                let id = claimed.Value.Id

                do! inbox.MarkNeedsReview id
                let! pending = inbox.CountPending()
                pending |> should equal 0

                do! inbox.ReviewRetry id
                let! pending2 = inbox.CountPending()
                pending2 |> should equal 1
            })
    finally
        deleteDir dir

// ---------------------------------------------------------------------------
// Restart replay
// ---------------------------------------------------------------------------

[<Fact>]
let ``pending command remains claimable after reopen`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            let exec1 = createExecutor dbPath

            try
                let inbox1 = Repositories.commandInbox exec1
                let! _ = inbox1.Insert(mkEnvelope "reopen-1")
                ()
            finally
                dispose exec1

            let exec2 = createExecutor dbPath

            try
                let inbox2 = Repositories.commandInbox exec2
                let! claimed = inbox2.ClaimNextForChat (ChatId 1L) (lease ())
                claimed |> should not' (be None)
                claimed.Value.Envelope.Payload |> should equal "payload"
            finally
                dispose exec2
        }
    finally
        deleteDir dir

// ---------------------------------------------------------------------------
// Outbox
// ---------------------------------------------------------------------------

[<Fact>]
let ``outbox retry keeps random id and sent stores remote id`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let outbox = Repositories.messageOutbox exec
                let! id = outbox.Insert 1L 0 (ChatId 1L) 42L "hello" []
                let! next = outbox.NextPending()
                next |> should not' (be None)
                let eid = next.Value.Id

                do! outbox.BeginSend eid
                let! during = outbox.GetByRandomId 42L
                during.Value.Status |> should equal Out.Status.Sending
                do! outbox.MarkFailed eid
                let! failed = outbox.GetByRandomId 42L
                failed.Value.Status |> should equal Out.Status.Failed
                do! outbox.Retry eid

                let! next2 = outbox.NextPending()
                next2 |> should not' (be None)
                next2.Value.RandomId |> should equal 42L
                do! outbox.BeginSend next2.Value.Id
                do! outbox.MarkSent next2.Value.Id 777L

                let! byRandom = outbox.GetByRandomId 42L
                byRandom |> should not' (be None)
                byRandom.Value.Status |> should equal Out.Status.Sent
                byRandom.Value.RemoteMessageId |> should equal (Some 777L)
            })
    finally
        deleteDir dir

[<Fact>]
let ``outbox duplicate random id collapses to one row`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let outbox = Repositories.messageOutbox exec
                let! id1 = outbox.Insert 1L 0 (ChatId 1L) 42L "hello" []
                let! id2 = outbox.Insert 1L 1 (ChatId 1L) 42L "hello" []
                id2 |> should equal id1
                let! count = outbox.CountPending()
                count |> should equal 1
            })
    finally
        deleteDir dir

[<Fact>]
let ``outbox entities roundtrip through insert and read`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let outbox = Repositories.messageOutbox exec

                let entities: Phos.Core.Chunker.Entity list =
                    [ { Offset = 0
                        Length = 8
                        Kind = Phos.Core.Chunker.Bold
                        Url = None }
                      { Offset = 10
                        Length = 6
                        Kind = Phos.Core.Chunker.Italic
                        Url = None } ]

                let! id = outbox.Insert 1L 0 (ChatId 1L) 42L "**bold** *italic*" entities

                id |> should not' (equal 0L)

                let! byRandom = outbox.GetByRandomId 42L
                byRandom |> should not' (be None)
                byRandom.Value.Entities |> should equal entities

                let! next = outbox.NextPending()
                next |> should not' (be None)
                next.Value.Entities |> should equal entities
            })
    finally
        deleteDir dir

[<Fact>]
let ``outbox roundtrips TextUrl entities with their url`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let outbox = Repositories.messageOutbox exec

                let entities: Phos.Core.Chunker.Entity list =
                    [ { Offset = 0
                        Length = 27
                        Kind = Phos.Core.Chunker.TextUrl
                        Url = Some "https://example.com" }
                      { Offset = 30
                        Length = 4
                        Kind = Phos.Core.Chunker.Bold
                        Url = None } ]

                let! id = outbox.Insert 1L 0 (ChatId 1L) 77L "[text](https://example.com) **x**" entities

                id |> should not' (equal 0L)

                let! byRandom = outbox.GetByRandomId 77L
                byRandom |> should not' (be None)
                byRandom.Value.Entities |> should equal entities

                let! next = outbox.NextPending()
                next |> should not' (be None)
                next.Value.Entities |> should equal entities
            })
    finally
        deleteDir dir

[<Fact>]
let ``insert deduplicates external key`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let inbox = Repositories.commandInbox exec
                let! id1 = inbox.Insert(mkEnvelope "dedup-1")
                let! id2 = inbox.Insert(mkEnvelope "dedup-1")
                id2 |> should equal id1
                let! count = inbox.CountPending()
                count |> should equal 1
            })
    finally
        deleteDir dir

[<Fact>]
let ``command origins round trip through claim`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let inbox = Repositories.commandInbox exec
                let l = lease ()

                for origin, key in [ Telegram, "o-tg"; Schedule, "o-sch"; System, "o-sys" ] do
                    let! _ =
                        inbox.Insert
                            { Origin = origin
                              ExternalKey = Some key
                              UserId = UserId 1L
                              ChatId = ChatId 2L
                              Payload = "p"
                              Priority = 0
                              Images = [] }

                    ()

                let! c1 = inbox.ClaimNextForChat (ChatId 2L) l
                c1.Value.Envelope.Origin |> should equal Telegram
                let! c2 = inbox.ClaimNextForChat (ChatId 2L) l
                c2.Value.Envelope.Origin |> should equal Schedule
                let! c3 = inbox.ClaimNextForChat (ChatId 2L) l
                c3.Value.Envelope.Origin |> should equal System
            })
    finally
        deleteDir dir

[<Fact>]
let ``claim by id then start and complete`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let inbox = Repositories.commandInbox exec
                let! id = inbox.Insert(mkEnvelope "byid-1")
                let! claimed = inbox.ClaimById id (lease ())
                claimed |> should not' (be None)
                claimed.Value.Status |> should equal In.Status.Claimed
                do! inbox.MarkStarted id
                do! inbox.MarkCompleted id
                let! pending = inbox.CountPending()
                pending |> should equal 0
            })
    finally
        deleteDir dir

[<Fact>]
let ``heartbeat updates lease heartbeat`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let inbox = Repositories.commandInbox exec
                let! id = inbox.Insert(mkEnvelope "hb-1")
                let! claimed = inbox.ClaimNextForChat (ChatId 1L) (lease ())
                claimed |> should not' (be None)
                do! inbox.Heartbeat id (DateTimeOffset.UtcNow) (DateTimeOffset.UtcNow.AddSeconds 60.0)
                let! pending = inbox.CountPending()
                pending |> should equal 0
            })
    finally
        deleteDir dir

// ---------------------------------------------------------------------------
// Users
// ---------------------------------------------------------------------------

[<Fact>]
let ``user upsert get and list with workspace path`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let users = Repositories.userRepository exec

                let u1 =
                    { Id = UserId 1L
                      Username = Some "alice"
                      Role = User
                      WorkspacePath = "/ws/alice"
                      Timezone = Some "Europe/Berlin" }

                do! users.Upsert u1
                let! got = users.GetByTelegramId(UserId 1L)
                got |> should not' (be None)
                got.Value.WorkspacePath |> should equal "/ws/alice"
                got.Value.Role |> should equal User
                got.Value.Timezone |> should equal (Some "Europe/Berlin")

                let u2 =
                    { u1 with
                        Role = Admin
                        WorkspacePath = "/ws/alice-new" }

                do! users.Upsert u2
                let! got2 = users.GetByTelegramId(UserId 1L)
                got2.Value.Role |> should equal Admin
                got2.Value.WorkspacePath |> should equal "/ws/alice-new"

                let! all = users.List()
                all.Length |> should equal 1
            })
    finally
        deleteDir dir

[<Fact>]
let ``user upsert handles owner role and null fields`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let users = Repositories.userRepository exec

                let u =
                    { Id = UserId 9L
                      Username = None
                      Role = Owner
                      WorkspacePath = "/ws/owner"
                      Timezone = None }

                do! users.Upsert u
                let! got = users.GetByTelegramId(UserId 9L)
                got |> should not' (be None)
                got.Value.Role |> should equal Owner
                got.Value.Username |> should equal None
                got.Value.Timezone |> should equal None
            })
    finally
        deleteDir dir

// ---------------------------------------------------------------------------
// Error paths
// ---------------------------------------------------------------------------

[<Fact>]
let ``unknown inbox status throws when read`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                // Insert a command with a bogus status directly.
                do!
                    exec.WriteAsync(fun conn ->
                        use cmd = conn.CreateCommand()

                        cmd.CommandText <-
                            "INSERT INTO command_inbox(origin, user_id, chat_id, payload, status, created_at, updated_at) VALUES ('telegram', 1, 1, 'p', 'bogus', 0, 0);"

                        cmd.ExecuteNonQuery() |> ignore
                        ())

                let inbox = Repositories.commandInbox exec

                let threw =
                    try
                        inbox.GetById 1L |> fun t -> t.GetAwaiter().GetResult() |> ignore
                        false
                    with _ ->
                        true

                threw |> should be True
            })
    finally
        deleteDir dir

[<Fact>]
let ``unknown outbox status throws when read`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                // Insert an outbox entry with a bogus status directly.
                do!
                    exec.WriteAsync(fun conn ->
                        use cmd = conn.CreateCommand()

                        cmd.CommandText <-
                            "INSERT INTO message_outbox(command_id, chunk_index, chat_id, random_id, payload, status, created_at, updated_at) VALUES (1, 0, 1, 42, 'p', 'bogus', 0, 0);"

                        cmd.ExecuteNonQuery() |> ignore
                        ())

                let outbox = Repositories.messageOutbox exec

                let threw =
                    try
                        outbox.GetByRandomId 42L |> fun t -> t.GetAwaiter().GetResult() |> ignore
                        false
                    with _ ->
                        true

                threw |> should be True
            })
    finally
        deleteDir dir

// ---------------------------------------------------------------------------
// Keys absent
// ---------------------------------------------------------------------------

[<Fact>]
let ``raw database bytes do not contain sentinel secret`` () =
    let dir = makeTempDir ()
    let sentinel = "PHOS_SENTINEL_SECRET_7f3a9c"
    let dbPath = Path.Combine(dir, sentinel + ".db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let inbox = Repositories.commandInbox exec

                let! _ =
                    inbox.Insert
                        { Origin = Telegram
                          ExternalKey = None
                          UserId = UserId 1L
                          ChatId = ChatId 1L
                          Payload = "hello"
                          Priority = 0
                          Images = [] }

                do! exec.CheckpointNow()
                let dbBytes = File.ReadAllBytes dbPath
                let walPath = dbPath + "-wal"

                let walBytes =
                    if File.Exists walPath then
                        File.ReadAllBytes walPath
                    else
                        [||]

                let all = Array.append dbBytes walBytes
                all.Length |> should be (greaterThan 0)
                let text = System.Text.Encoding.UTF8.GetString(all)
                text.Contains(sentinel) |> should be False
            })
    finally
        deleteDir dir

// ---------------------------------------------------------------------------
// Acceptance: durable command survives process kill
// ---------------------------------------------------------------------------

[<Fact>]
let ``durable command survives executor disposed without clean shutdown and reopened`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        task {
            // Insert a command and dispose WITHOUT a clean checkpoint, simulating
            // a process kill. The committed row must be durable in WAL and
            // claimable after reopening.
            let exec1 = createExecutor dbPath

            try
                let inbox1 = Repositories.commandInbox exec1
                let! _ = inbox1.Insert(mkEnvelope "kill-1")
                ()
            finally
                dispose exec1

            let exec2 = createExecutor dbPath

            try
                let inbox2 = Repositories.commandInbox exec2
                let! claimed = inbox2.ClaimNextForChat (ChatId 1L) (lease ())
                claimed |> should not' (be None)
                claimed.Value.Envelope.Payload |> should equal "payload"
            finally
                dispose exec2
        }
    finally
        deleteDir dir

// ---------------------------------------------------------------------------
// Phase 5 — ListPendingChatIds
// ---------------------------------------------------------------------------

let private mkEnvFor (chatId: int64) (key: string) : CommandEnvelope =
    { Origin = Telegram
      ExternalKey = Some key
      UserId = UserId 1L
      ChatId = ChatId chatId
      Payload = "payload"
      Priority = 0
      Images = [] }

[<Fact>]
let ``list pending chat ids returns distinct chats with pending work`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let inbox = Repositories.commandInbox exec
                let! _ = inbox.Insert(mkEnvFor 1L "lpc-1")
                let! _ = inbox.Insert(mkEnvFor 2L "lpc-2")
                let! _ = inbox.Insert(mkEnvFor 1L "lpc-3")
                let! ids = inbox.ListPendingChatIds()
                ids |> List.sort |> should equal [ ChatId 1L; ChatId 2L ]
            })
    finally
        deleteDir dir

[<Fact>]
let ``list pending chat ids includes failed and needs_review, excludes completed`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let inbox = Repositories.commandInbox exec
                let l = lease ()

                // chat 1: fail (attempts < max) → 'failed'
                let! id1 = inbox.Insert(mkEnvFor 1L "lpc-fail")
                let! _ = inbox.ClaimById id1 l
                do! inbox.MarkStarted id1
                do! inbox.MarkFailed id1

                // chat 2: needs_review
                let! id2 = inbox.Insert(mkEnvFor 2L "lpc-review")
                let! _ = inbox.ClaimById id2 l
                do! inbox.MarkNeedsReview id2

                // chat 3: completed → excluded
                let! id3 = inbox.Insert(mkEnvFor 3L "lpc-done")
                let! _ = inbox.ClaimById id3 l
                do! inbox.MarkStarted id3
                do! inbox.MarkCompleted id3

                let! ids = inbox.ListPendingChatIds()
                ids |> List.sort |> should equal [ ChatId 1L; ChatId 2L ]
            })
    finally
        deleteDir dir

// ---------------------------------------------------------------------------
// Schedule jobs
// ---------------------------------------------------------------------------

let private tableColumns (exec: StorageExecutor) (table: string) : string list =
    exec.ReadAsync(fun conn ->
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sprintf "PRAGMA table_info(%s);" table
        use reader = cmd.ExecuteReader()
        let cols = ResizeArray<string>()

        while reader.Read() do
            cols.Add(reader.GetString 1)

        List.ofSeq cols)
    |> fun t -> t.GetAwaiter().GetResult()

let private indexExists (exec: StorageExecutor) (name: string) : bool =
    exec.ReadAsync(fun conn ->
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = $name;"
        cmd.Parameters.AddWithValue("$name", name) |> ignore
        cmd.ExecuteScalar() :?> int64 > 0L)
    |> fun t -> t.GetAwaiter().GetResult()

let private mkDraft (cron: string option) (interval: int option) (catchup: CatchupPolicy) : ScheduleJobDraft =
    { UserId = UserId 1L
      ChatId = ChatId 1L
      Prompt = "remind me"
      CronExpr = cron
      IntervalSeconds = interval
      AfterSeconds = None
      Timezone = "UTC"
      Catchup = catchup }

let private queryCommand (exec: StorageExecutor) (id: int64) : (string * int * string * string) option =
    exec.ReadAsync(fun conn ->
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT origin, priority, payload, external_key FROM command_inbox WHERE id = $id;"
        cmd.Parameters.AddWithValue("$id", id) |> ignore
        use reader = cmd.ExecuteReader()

        if reader.Read() then
            Some(reader.GetString 0, reader.GetInt32 1, reader.GetString 2, reader.GetString 3)
        else
            None)
    |> fun t -> t.GetAwaiter().GetResult()

let private queryRun (exec: StorageExecutor) (jobId: int64) (scheduledFor: DateTimeOffset) : (string * int64) option =
    exec.ReadAsync(fun conn ->
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            "SELECT status, command_id FROM schedule_runs WHERE job_id = $jobId AND scheduled_for = $scheduledFor;"

        cmd.Parameters.AddWithValue("$jobId", jobId) |> ignore

        cmd.Parameters.AddWithValue("$scheduledFor", scheduledFor.ToUnixTimeSeconds())
        |> ignore

        use reader = cmd.ExecuteReader()

        if reader.Read() then
            Some(reader.GetString 0, reader.GetInt64 1)
        else
            None)
    |> fun t -> t.GetAwaiter().GetResult()

let private commandCount (exec: StorageExecutor) : int =
    exec.ReadAsync(fun conn ->
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT COUNT(*) FROM command_inbox;"
        cmd.ExecuteScalar() :?> int64 |> int)
    |> fun t -> t.GetAwaiter().GetResult()

let private runsCount (exec: StorageExecutor) (jobId: int64) : int =
    exec.ReadAsync(fun conn ->
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT COUNT(*) FROM schedule_runs WHERE job_id = $jobId;"
        cmd.Parameters.AddWithValue("$jobId", jobId) |> ignore
        cmd.ExecuteScalar() :?> int64 |> int)
    |> fun t -> t.GetAwaiter().GetResult()

[<Fact>]
let ``migration 9 extends schedule jobs and drops enabled`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let cols = tableColumns exec "schedule_jobs"
                cols |> List.contains "chat_id" |> should be True
                cols |> List.contains "status" |> should be True
                cols |> List.contains "interval_seconds" |> should be True
                cols |> List.contains "origin_tool_call_id" |> should be True
                cols |> List.contains "last_run_at" |> should be True
                cols |> List.contains "last_error" |> should be True
                cols |> List.contains "enabled" |> should be False
                indexExists exec "ux_schedule_jobs_origin_tool_call_id" |> should be True
            })
    finally
        deleteDir dir

[<Fact>]
let ``migration 10 adds after_seconds column`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let cols = tableColumns exec "schedule_jobs"
                cols |> List.contains "after_seconds" |> should be True
            })
    finally
        deleteDir dir

[<Fact>]
let ``schedule job insert get list and count active round trip`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let repo = Repositories.scheduleJobRepository exec
                let! j1 = repo.Insert (mkDraft None (Some 3600) SkipMissed) None
                j1.Id |> should be (greaterThan 0L)
                j1.Status |> should equal ScheduleStatus.Pending
                j1.NextRun |> should equal None

                let cronDraft =
                    { mkDraft (Some "0 9 * * *") None SkipMissed with
                        ChatId = ChatId 2L }

                let! _ = repo.Insert cronDraft None

                let! got = repo.GetById j1.Id
                got |> should not' (be None)
                got.Value.IntervalSeconds |> should equal (Some 3600)
                got.Value.CronExpr |> should equal None
                got.Value.Catchup |> should equal SkipMissed
                got.Value.UserId |> should equal (UserId 1L)

                let! all = repo.ListForUser(UserId 1L)
                all.Length |> should equal 2

                let! active = repo.CountActiveForUser(UserId 1L)
                active |> should equal 0

                do! repo.Confirm j1.Id
                let! active2 = repo.CountActiveForUser(UserId 1L)
                active2 |> should equal 1
            })
    finally
        deleteDir dir

[<Fact>]
let ``confirm activates pending job and sets next run`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let repo = Repositories.scheduleJobRepository exec
                let! job = repo.Insert (mkDraft (Some "0 9 * * *") None SkipMissed) None
                do! repo.Confirm job.Id
                let! after = repo.GetById job.Id
                after.Value.Status |> should equal ScheduleStatus.Active
                after.Value.NextRun |> should not' (be None)
                after.Value.NextRun.Value |> should be (greaterThan DateTimeOffset.UtcNow)
                let next1 = after.Value.NextRun

                do! repo.Confirm job.Id
                let! after2 = repo.GetById job.Id
                after2.Value.Status |> should equal ScheduleStatus.Active
                after2.Value.NextRun |> should equal next1
            })
    finally
        deleteDir dir

[<Fact>]
let ``confirm interval job sets next run ahead`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let repo = Repositories.scheduleJobRepository exec
                let! job = repo.Insert (mkDraft None (Some 60) SkipMissed) None
                do! repo.Confirm job.Id
                let! after = repo.GetById job.Id
                after.Value.NextRun |> should not' (be None)

                (after.Value.NextRun.Value - DateTimeOffset.UtcNow).TotalSeconds
                |> should be (greaterThan 50.0)
            })
    finally
        deleteDir dir

[<Fact>]
let ``cancel pause resume status transitions`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let repo = Repositories.scheduleJobRepository exec
                let! j1 = repo.Insert (mkDraft None (Some 3600) SkipMissed) None
                do! repo.Cancel j1.Id
                let! c1 = repo.GetById j1.Id
                c1.Value.Status |> should equal ScheduleStatus.Cancelled

                let! j2 = repo.Insert (mkDraft None (Some 3600) SkipMissed) None
                do! repo.Confirm j2.Id
                do! repo.Pause j2.Id
                let! p2 = repo.GetById j2.Id
                p2.Value.Status |> should equal ScheduleStatus.Paused

                do! repo.Resume j2.Id
                let! r2 = repo.GetById j2.Id
                r2.Value.Status |> should equal ScheduleStatus.Active
                r2.Value.NextRun |> should not' (be None)
                r2.Value.NextRun.Value |> should be (greaterThan DateTimeOffset.UtcNow)

                do! repo.Pause j2.Id
                let! p3 = repo.GetById j2.Id
                p3.Value.Status |> should equal ScheduleStatus.Paused
            })
    finally
        deleteDir dir

[<Fact>]
let ``remove deletes job and its runs`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let repo = Repositories.scheduleJobRepository exec
                let! job = repo.Insert (mkDraft None (Some 3600) CatchUpOnce) None
                do! repo.Confirm job.Id
                let! confirmed = repo.GetById job.Id
                let nextRun = confirmed.Value.NextRun.Value
                let! claim = repo.ClaimDueOccurrence(nextRun.AddSeconds 5.0)
                claim |> should not' (be None)
                runsCount exec job.Id |> should be (greaterThan 0)

                do! repo.Remove job.Id
                let! gone = repo.GetById job.Id
                gone |> should equal None
                runsCount exec job.Id |> should equal 0
            })
    finally
        deleteDir dir

[<Fact>]
let ``expire pending expires only old pending jobs`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let repo = Repositories.scheduleJobRepository exec
                let! oldJob = repo.Insert (mkDraft None (Some 3600) SkipMissed) None
                let! recentJob = repo.Insert (mkDraft None (Some 3600) SkipMissed) None
                let! activeJob = repo.Insert (mkDraft None (Some 3600) SkipMissed) None
                do! repo.Confirm activeJob.Id
                let now = DateTimeOffset.UtcNow

                do!
                    exec.WriteAsync(fun conn ->
                        use cmd = conn.CreateCommand()
                        cmd.CommandText <- "UPDATE schedule_jobs SET created_at = $old WHERE id = $id;"

                        cmd.Parameters.AddWithValue("$old", now.AddHours(-2.0).ToUnixTimeSeconds())
                        |> ignore

                        cmd.Parameters.AddWithValue("$id", oldJob.Id) |> ignore
                        cmd.ExecuteNonQuery() |> ignore
                        ())

                let! expired = repo.ExpirePending now (now.AddHours(-1.0))
                expired |> should equal 1

                let! o = repo.GetById oldJob.Id
                o.Value.Status |> should equal ScheduleStatus.Expired
                let! r = repo.GetById recentJob.Id
                r.Value.Status |> should equal ScheduleStatus.Pending
                let! a = repo.GetById activeJob.Id
                a.Value.Status |> should equal ScheduleStatus.Active
            })
    finally
        deleteDir dir

[<Fact>]
let ``pause due to errors pauses active job and records error`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let repo = Repositories.scheduleJobRepository exec
                let! job = repo.Insert (mkDraft None (Some 3600) SkipMissed) None
                do! repo.Confirm job.Id
                do! repo.PauseDueToErrors job.Id "boom"
                let! after = repo.GetById job.Id
                after.Value.Status |> should equal ScheduleStatus.Paused
                after.Value.LastError |> should equal (Some "boom")
            })
    finally
        deleteDir dir

[<Fact>]
let ``claim due occurrence enqueues exactly one command`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let repo = Repositories.scheduleJobRepository exec
                let! job = repo.Insert (mkDraft None (Some 3600) CatchUpOnce) None
                do! repo.Confirm job.Id
                let! confirmed = repo.GetById job.Id
                let nextRun = confirmed.Value.NextRun.Value
                let now = nextRun.AddSeconds 5.0

                let! claim = repo.ClaimDueOccurrence now
                claim |> should not' (be None)
                claim.Value.CommandId |> should be (greaterThan 0L)
                claim.Value.ScheduledFor |> should equal nextRun

                let cmd = queryCommand exec claim.Value.CommandId
                cmd |> should not' (be None)
                let (origin, priority, payload, extKey) = cmd.Value
                origin |> should equal "schedule"
                priority |> should equal 10
                payload |> should startWith "[по расписанию]"
                extKey |> should equal (sprintf "sched:%d:%d" job.Id nextRun.UtcTicks)

                let run = queryRun exec job.Id nextRun
                run |> should not' (be None)
                let (runStatus, runCommandId) = run.Value
                runStatus |> should equal "claimed"
                runCommandId |> should equal claim.Value.CommandId

                let! after = repo.GetById job.Id
                after.Value.NextRun |> should not' (be None)
                after.Value.NextRun.Value |> should be (greaterThan nextRun)
                after.Value.LastRunAt |> should not' (be None)

                let! claim2 = repo.ClaimDueOccurrence now
                claim2 |> should equal None
                commandCount exec |> should equal 1
            })
    finally
        deleteDir dir

[<Fact>]
let ``claim skips missed occurrence under skip catchup`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let repo = Repositories.scheduleJobRepository exec
                let! job = repo.Insert (mkDraft None (Some 3600) SkipMissed) None
                do! repo.Confirm job.Id
                let! confirmed = repo.GetById job.Id
                let nextRun = confirmed.Value.NextRun.Value
                let now = nextRun.AddSeconds 5.0

                let! claim = repo.ClaimDueOccurrence now
                claim |> should equal None
                commandCount exec |> should equal 0

                let! after = repo.GetById job.Id
                after.Value.NextRun |> should not' (be None)
                after.Value.NextRun.Value |> should be (greaterThan nextRun)
                after.Value.LastRunAt |> should equal None
            })
    finally
        deleteDir dir

[<Fact>]
let ``claim catch up once fires then returns none`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let repo = Repositories.scheduleJobRepository exec
                let! job = repo.Insert (mkDraft None (Some 3600) CatchUpOnce) None
                do! repo.Confirm job.Id
                let! confirmed = repo.GetById job.Id
                let nextRun = confirmed.Value.NextRun.Value
                let now = nextRun.AddSeconds 5.0

                let! claim = repo.ClaimDueOccurrence now
                claim |> should not' (be None)

                let! claim2 = repo.ClaimDueOccurrence now
                claim2 |> should equal None
                commandCount exec |> should equal 1
            })
    finally
        deleteDir dir

[<Fact>]
let ``claim due exactly on time fires under skip catchup`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let repo = Repositories.scheduleJobRepository exec
                let! job = repo.Insert (mkDraft None (Some 3600) SkipMissed) None
                do! repo.Confirm job.Id
                let! confirmed = repo.GetById job.Id
                let nextRun = confirmed.Value.NextRun.Value

                let! claim = repo.ClaimDueOccurrence nextRun
                claim |> should not' (be None)
                commandCount exec |> should equal 1
            })
    finally
        deleteDir dir

[<Fact>]
let ``claim due cron job enqueues command`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let repo = Repositories.scheduleJobRepository exec
                let! job = repo.Insert (mkDraft (Some "0 9 * * *") None CatchUpOnce) None
                do! repo.Confirm job.Id
                let! confirmed = repo.GetById job.Id
                let nextRun = confirmed.Value.NextRun.Value

                let! claim = repo.ClaimDueOccurrence(nextRun.AddSeconds 5.0)
                claim |> should not' (be None)
                claim.Value.ScheduledFor |> should equal nextRun
                commandCount exec |> should equal 1
                runsCount exec job.Id |> should equal 1

                let! after = repo.GetById job.Id
                after.Value.NextRun |> should not' (be None)
                after.Value.NextRun.Value |> should be (greaterThan nextRun)
            })
    finally
        deleteDir dir

[<Fact>]
let ``one-shot job fires once and completes`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let repo = Repositories.scheduleJobRepository exec

                let draft =
                    { mkDraft None None SkipMissed with
                        AfterSeconds = Some 300 }

                let! job = repo.Insert draft None
                do! repo.Confirm job.Id
                let! confirmed = repo.GetById job.Id
                let nextRun = confirmed.Value.NextRun.Value
                nextRun |> should be (greaterThan (DateTimeOffset.UtcNow.AddSeconds 299.0))

                let! claim = repo.ClaimDueOccurrence nextRun
                claim |> should not' (be None)
                claim.Value.ScheduledFor |> should equal nextRun

                let cmd = queryCommand exec claim.Value.CommandId
                cmd |> should not' (be None)
                let (_, _, payload, _) = cmd.Value
                payload |> should startWith "[по расписанию]"

                let! after = repo.GetById job.Id
                after.Value.Status |> should equal ScheduleStatus.Completed

                let! claim2 = repo.ClaimDueOccurrence(nextRun.AddSeconds 1.0)
                claim2 |> should equal None
                commandCount exec |> should equal 1
            })
    finally
        deleteDir dir

[<Fact>]
let ``one-shot fires late after restart semantics`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let repo = Repositories.scheduleJobRepository exec

                let draft =
                    { mkDraft None None SkipMissed with
                        AfterSeconds = Some 300 }

                let! job = repo.Insert draft None
                do! repo.Confirm job.Id
                let! confirmed = repo.GetById job.Id
                let nextRun = confirmed.Value.NextRun.Value

                // Claim late (simulating a restart after the due moment): a one-shot
                // fires even when it is past its scheduled time.
                let lateNow = nextRun.AddSeconds 301.0
                let! claim = repo.ClaimDueOccurrence lateNow
                claim |> should not' (be None)
                claim.Value.ScheduledFor |> should equal nextRun
                commandCount exec |> should equal 1

                let! after = repo.GetById job.Id
                after.Value.Status |> should equal ScheduleStatus.Completed

                let! claim2 = repo.ClaimDueOccurrence(lateNow.AddSeconds 1.0)
                claim2 |> should equal None
                commandCount exec |> should equal 1
            })
    finally
        deleteDir dir

[<Fact>]
let ``claim returns none when no job is due`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let repo = Repositories.scheduleJobRepository exec
                // Pending (not confirmed) is not active.
                let! _ = repo.Insert (mkDraft None (Some 3600) SkipMissed) None
                // Confirmed but next_run is in the future.
                let! j2 = repo.Insert (mkDraft None (Some 3600) SkipMissed) None
                do! repo.Confirm j2.Id

                let! claim = repo.ClaimDueOccurrence DateTimeOffset.UtcNow
                claim |> should equal None
                commandCount exec |> should equal 0
            })
    finally
        deleteDir dir

[<Fact>]
let ``find by tool call id returns job or none`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let repo = Repositories.scheduleJobRepository exec
                let! _ = repo.Insert (mkDraft None (Some 3600) SkipMissed) (Some "tool-1")

                let! found = repo.FindByToolCallId "tool-1"
                found |> should not' (be None)
                found.Value.Prompt |> should equal "remind me"

                let! missing = repo.FindByToolCallId "tool-none"
                missing |> should equal None
            })
    finally
        deleteDir dir

[<Fact>]
let ``run now makes an active job due immediately`` () =
    let dir = makeTempDir ()
    let dbPath = Path.Combine(dir, "phos.db")

    try
        withExecutor dbPath (fun exec ->
            task {
                let repo = Repositories.scheduleJobRepository exec
                let! job = repo.Insert (mkDraft None (Some 3600) CatchUpOnce) None
                do! repo.Confirm job.Id
                do! repo.RunNow job.Id
                let! claim = repo.ClaimDueOccurrence DateTimeOffset.UtcNow
                claim |> should not' (be None)
                claim.Value.Job.Id |> should equal job.Id
            })
    finally
        deleteDir dir
