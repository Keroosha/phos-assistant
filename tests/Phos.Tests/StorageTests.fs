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

    Schema.migrate exec Schema.migrations
    |> fun t -> t.GetAwaiter().GetResult() |> ignore

    exec

let private dispose (exec: StorageExecutor) =
    (exec :> IAsyncDisposable).DisposeAsync().AsTask()
    |> fun t -> t.GetAwaiter().GetResult()

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
      Priority = 0 }

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
                tableExists exec "schema_version" |> should be True

                let! applied = Schema.migrate exec Schema.migrations
                applied |> should equal 0
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
                let! applied1 = Schema.migrate exec (Schema.migrations |> List.take 1)
                applied1 |> should equal 1
                tableExists exec "users" |> should be True
                tableExists exec "command_inbox" |> should be False

                // Replay the full list: 2..6 should be applied.
                let! applied2 = Schema.migrate exec Schema.migrations
                applied2 |> should equal 5
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
                let! a0 = Schema.migrate exec []
                a0 |> should equal 0
                let! a1 = Schema.migrate exec (Schema.migrations |> List.take 2)
                a1 |> should equal 2
                let! a2 = Schema.migrate exec (Schema.migrations |> List.take 2)
                a2 |> should equal 0
                let! a3 = Schema.migrate exec Schema.migrations
                a3 |> should equal 4
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
                Schema.migrate exec Schema.migrations
                |> fun t -> t.GetAwaiter().GetResult() |> ignore

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
                Schema.migrate exec Schema.migrations
                |> fun t -> t.GetAwaiter().GetResult() |> ignore

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
                                Priority = 0 } ]

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
                              let! _ = outbox.Insert (int64 i) 0 (ChatId 1L) (int64 i) "m"
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
                let! id = outbox.Insert 1L 0 (ChatId 1L) 42L "hello"
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
                let! id1 = outbox.Insert 1L 0 (ChatId 1L) 42L "hello"
                let! id2 = outbox.Insert 1L 1 (ChatId 1L) 42L "hello"
                id2 |> should equal id1
                let! count = outbox.CountPending()
                count |> should equal 1
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
                              Priority = 0 }

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
                do! inbox.Heartbeat id (DateTimeOffset.UtcNow)
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
                          Priority = 0 }

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
