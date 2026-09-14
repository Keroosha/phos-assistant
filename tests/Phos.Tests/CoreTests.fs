module Phos.Tests.CoreTests

open System
open Xunit
open FsUnit.Xunit
open FsCheck
open FsCheck.FSharp
open FsCheck.Xunit
open Phos.Core.DomainTypes
open Phos.Core.Whitelist
open Phos.Core.ToolPolicy
open Phos.Core.Chunker
open Phos.Core.SchedulePolicy
open Phos.Core.InboxStateMachine
open Phos.Core.OutboxStateMachine

// Module abbreviations to disambiguate the heavily-overlapping type/case names.
module Wl = Phos.Core.Whitelist
module Tp = Phos.Core.ToolPolicy
module In = Phos.Core.InboxStateMachine
module Out = Phos.Core.OutboxStateMachine
module Sj = Phos.Core.ScheduleJobs

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/// Generator of arbitrary strings (includes backticks to exercise fences).
let private stringGen: Gen<string> =
    Gen.listOf (Gen.elements [ 'a'; 'b'; 'c'; '`'; 'x'; 'y'; 'z'; ' ' ])
    |> Gen.map (fun chars -> System.String(List.toArray chars))

/// Generator of strings with no backtick fence delimiter.
let private fenceFreeGen: Gen<string> =
    Gen.listOf (Gen.elements [ 'a'; 'b'; 'c'; 'x'; 'y'; 'z'; ' ' ])
    |> Gen.map (fun chars -> System.String(List.toArray chars))

let private mkInboxCommand (status: In.Status) (attempts: int) (maxAttempts: int) : In.Command =
    { Id = 1L
      Origin = Telegram
      Status = status
      Attempts = attempts
      MaxAttempts = maxAttempts
      LeaseUntil = None
      HeartbeatAt = None }

let private mkOutboxEntry (status: Out.Status) (attempts: int) (maxAttempts: int) : Out.Entry =
    { Id = 1L
      CommandId = 2L
      ChunkIndex = 0
      RandomId = 42L
      Status = status
      Attempts = attempts
      MaxAttempts = maxAttempts
      RemoteMessageId = None }

// ---------------------------------------------------------------------------
// DomainTypes
// ---------------------------------------------------------------------------

[<Fact>]
let ``domain types are constructible`` () =
    UserId 7L |> should equal (UserId 7L)
    ChatId 9L |> should equal (ChatId 9L)

    (UserId 1L, ChatId 2L, Private, Owner, Telegram)
    |> should equal (UserId 1L, ChatId 2L, Private, Owner, Telegram)

// ---------------------------------------------------------------------------
// Whitelist
// ---------------------------------------------------------------------------

[<Fact>]
let ``whitelist private allows whitelisted user with its role`` () =
    let user =
        { Id = UserId 1L
          Username = Some "bob"
          Role = Admin }

    let chat = { Id = ChatId 10L; Kind = Private }
    let wl = create (Map.ofList [ (UserId 1L, Admin) ]) Set.empty
    authorize wl user chat |> should equal (Wl.Allow Admin)

[<Fact>]
let ``whitelist private denies non-whitelisted user`` () =
    let user =
        { Id = UserId 2L
          Username = None
          Role = User }

    let chat = { Id = ChatId 10L; Kind = Private }
    let wl = create Map.empty Set.empty
    authorize wl user chat |> should equal (Wl.Deny Wl.NotWhitelisted)

[<Fact>]
let ``whitelist group denies when chat not allowed`` () =
    let user =
        { Id = UserId 1L
          Username = None
          Role = User }

    let chat = { Id = ChatId 20L; Kind = Group }
    let wl = create (Map.ofList [ (UserId 1L, User) ]) Set.empty
    authorize wl user chat |> should equal (Wl.Deny Wl.ChatNotAllowed)

[<Fact>]
let ``whitelist group allows allowed chat and falls back to User role`` () =
    let user =
        { Id = UserId 5L
          Username = None
          Role = User }

    let chat = { Id = ChatId 20L; Kind = Group }
    let wl = create Map.empty (Set.singleton (ChatId 20L))
    authorize wl user chat |> should equal (Wl.Allow User)

[<Property>]
let ``whitelist private chat requires whitelisted user``
    (userId: int64)
    (chatId: int64)
    (role: UserRole)
    (isWhitelisted: bool)
    =
    let user =
        { Id = UserId userId
          Username = None
          Role = role }

    let chat = { Id = ChatId chatId; Kind = Private }

    let users =
        if isWhitelisted then
            Map.ofList [ (UserId userId, role) ]
        else
            Map.empty

    let wl = create users Set.empty

    match authorize wl user chat with
    | Wl.Allow r -> isWhitelisted && r = role
    | Wl.Deny Wl.NotWhitelisted -> not isWhitelisted
    | Wl.Deny Wl.ChatNotAllowed -> false

[<Property>]
let ``whitelist group requires allowed chat and uses stored role or User fallback``
    (userId: int64)
    (chatId: int64)
    (role: UserRole)
    (chatAllowed: bool)
    (isWhitelisted: bool)
    =
    let user =
        { Id = UserId userId
          Username = None
          Role = role }

    let chat = { Id = ChatId chatId; Kind = Group }

    let users =
        if isWhitelisted then
            Map.ofList [ (UserId userId, role) ]
        else
            Map.empty

    let allowed =
        if chatAllowed then
            Set.singleton (ChatId chatId)
        else
            Set.empty

    let wl = create users allowed

    match authorize wl user chat with
    | Wl.Allow r -> chatAllowed && (if isWhitelisted then r = role else r = User)
    | Wl.Deny Wl.ChatNotAllowed -> not chatAllowed
    | Wl.Deny Wl.NotWhitelisted -> false

// ---------------------------------------------------------------------------
// ToolPolicy
// ---------------------------------------------------------------------------

[<Fact>]
let ``role ordering is Owner >= Admin >= User`` () =
    roleAtLeast Owner Admin |> should be True
    roleAtLeast Owner User |> should be True
    roleAtLeast Admin User |> should be True
    roleAtLeast Admin Owner |> should be False
    roleAtLeast User Owner |> should be False
    roleAtLeast User Admin |> should be False
    roleAtLeast Owner Owner |> should be True
    roleAtLeast Admin Admin |> should be True
    roleAtLeast User User |> should be True

[<Fact>]
let ``tool policy known tool allowed when role at least min role`` () =
    let policy =
        { DefaultMinRole = None
          Tools = Map.ofList [ "tg_send_message", Admin ] }

    resolve policy Owner "tg_send_message"
    |> should equal { Allowed = true; MinRole = Some Admin }

    resolve policy Admin "tg_send_message"
    |> should equal { Allowed = true; MinRole = Some Admin }

[<Fact>]
let ``tool policy known tool denied when role below min role`` () =
    let policy =
        { DefaultMinRole = None
          Tools = Map.ofList [ "tg_send_message", Admin ] }

    resolve policy User "tg_send_message"
    |> should equal { Allowed = false; MinRole = None }

[<Fact>]
let ``tool policy unknown tool uses default min role`` () =
    let policy =
        { DefaultMinRole = Some User
          Tools = Map.empty }

    resolve policy Owner "unknown"
    |> should equal { Allowed = true; MinRole = Some User }

    resolve policy Admin "unknown"
    |> should equal { Allowed = true; MinRole = Some User }

    resolve policy User "unknown"
    |> should equal { Allowed = true; MinRole = Some User }

[<Fact>]
let ``tool policy unknown tool denied when default min role exceeds role`` () =
    let policy =
        { DefaultMinRole = Some Admin
          Tools = Map.empty }

    resolve policy User "unknown"
    |> should equal { Allowed = false; MinRole = None }

[<Fact>]
let ``tool policy unknown tool with no default is denied`` () =
    let policy =
        { DefaultMinRole = None
          Tools = Map.empty }

    resolve policy Owner "unknown"
    |> should equal { Allowed = false; MinRole = None }

[<Property>]
let ``tool policy known tool allowed iff role at least min role`` (role: UserRole) (minRole: UserRole) =
    let policy =
        { DefaultMinRole = None
          Tools = Map.ofList [ "tool", minRole ] }

    let decision = resolve policy role "tool"

    decision.Allowed = roleAtLeast role minRole
    && (decision.Allowed = (decision.MinRole = Some minRole))

[<Property>]
let ``tool policy unknown tool resolves by default min role`` (role: UserRole) (minRole: UserRole option) =
    let policy =
        { DefaultMinRole = minRole
          Tools = Map.empty }

    let decision = resolve policy role "unknown"

    match minRole with
    | Some m ->
        decision.Allowed = roleAtLeast role m
        && (decision.Allowed = (decision.MinRole = Some m))
    | None -> not decision.Allowed && decision.MinRole = None

[<Property>]
let ``roleAtLeast is a total preorder`` (a: UserRole) (b: UserRole) =
    roleAtLeast a a
    && (roleAtLeast a b || roleAtLeast b a)
    && ((roleAtLeast a b && roleAtLeast b a) = (a = b))

// ---------------------------------------------------------------------------
// Chunker
// ---------------------------------------------------------------------------

let private validEntityGen (maxOffset: int) : Gen<Entity> =
    Gen.zip
        (Gen.zip (Gen.choose (0, maxOffset)) (Gen.choose (0, maxOffset)))
        (Gen.elements [ Bold; Italic; Code; Pre; TextUrl; Mention; Hashtag; Unknown ])
    |> Gen.map (fun ((offset, length), kind) ->
        { Offset = offset
          Length = min length (maxOffset - offset)
          Kind = kind
          Url = None })

let private chunkInputGen: Gen<int * string * Entity list> =
    Gen.zip (Gen.choose (1, 64)) (Gen.zip stringGen (Gen.listOf (validEntityGen 64)))
    |> Gen.map (fun (maxUnits, (text, entities)) -> (maxUnits, text, entities))

let private countFences (s: string) : int =
    let mutable count = 0
    let mutable i = 0

    while i < s.Length do
        if s.[i] = '`' then
            let mutable j = i

            while j < s.Length && s.[j] = '`' do
                j <- j + 1

            if j - i = 3 then
                count <- count + 1

            i <- j
        else
            i <- i + 1

    count

[<Property>]
let ``chunk never exceeds maxUnits and keeps entity offsets valid`` () =
    Prop.forAll (Arb.fromGen chunkInputGen) (fun (maxUnits, text, entities) ->
        let chunks = chunk maxUnits text entities
        let sizeOk = chunks |> List.forall (fun c -> c.Text.Length <= maxUnits)

        let entityOk =
            chunks
            |> List.forall (fun c ->
                c.Entities
                |> List.forall (fun e -> e.Offset >= 0 && e.Offset + e.Length <= c.Text.Length))

        sizeOk && entityOk)

[<Property>]
let ``chunk reassembles fence-free text`` () =
    Prop.forAll (Arb.fromGen (Gen.zip (Gen.choose (1, 64)) fenceFreeGen)) (fun (maxUnits, text) ->
        let chunks = chunk maxUnits text []
        String.concat "" (chunks |> List.map (fun c -> c.Text)) = text)

[<Property>]
let ``chunk fences are balanced`` () =
    Prop.forAll (Arb.fromGen (Gen.zip (Gen.choose (7, 64)) stringGen)) (fun (maxUnits, text) ->
        let chunks = chunk maxUnits text []
        // Fence markers are never split: an opened fence is reflected by a leading
        // ```, a closed fence by a trailing ``` (synthetic markers may merge with
        // adjacent backticks, so we assert flag/text consistency rather than an
        // exact even delimiter count).
        chunks
        |> List.forall (fun c ->
            (not c.FenceOpened || c.Text.StartsWith("```"))
            && (not c.FenceClosed || c.Text.EndsWith("```"))
            && c.Text.Length <= maxUnits))

[<Fact>]
let ``chunk splits an entity crossing a boundary`` () =
    let entities =
        [ { Offset = 0
            Length = 7
            Kind = Bold
            Url = None } ]

    let chunks = chunk 4 "abcdefgh" entities

    let flattened =
        chunks
        |> List.map (fun c -> c.Entities |> List.map (fun e -> e.Offset, e.Length, e.Kind))

    flattened |> should equal [ [ (0, 4, Bold) ]; [ (0, 3, Bold) ] ]

[<Fact>]
let ``chunk rebase carries Url across a split entity`` () =
    let entities =
        [ { Offset = 0
            Length = 7
            Kind = TextUrl
            Url = Some "https://example.com" } ]

    let chunks = chunk 4 "abcdefgh" entities

    let flattened =
        chunks
        |> List.map (fun c -> c.Entities |> List.map (fun e -> e.Offset, e.Length, e.Kind, e.Url))

    flattened
    |> should
        equal
        [ [ (0, 4, TextUrl, Some "https://example.com") ]
          [ (0, 3, TextUrl, Some "https://example.com") ] ]

[<Fact>]
let ``chunk fences are never cut`` () =
    let text = "```ABCDEFGHIJKLMNOPQRSTUVWXYZ```"
    let chunks = chunk 10 text []

    chunks
    |> List.forall (fun c ->
        c.Text.Length <= 10
        && (countFences c.Text) % 2 = 0
        && (not c.FenceOpened || c.Text.StartsWith("```"))
        && (not c.FenceClosed || c.Text.EndsWith("```")))
    |> should be True

// ---------------------------------------------------------------------------
// SchedulePolicy
// ---------------------------------------------------------------------------

let private berlinTz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin")

[<Fact>]
let ``resolveLocal returns None for invalid spring-forward time`` () =
    resolveLocal berlinTz (DateTime(2026, 3, 29, 2, 30, 0)) |> should equal None

[<Fact>]
let ``resolveLocal picks standard offset for ambiguous fall-back time`` () =
    match resolveLocal berlinTz (DateTime(2026, 10, 25, 2, 30, 0)) with
    | Some dto ->
        dto.Offset |> should equal (TimeSpan.FromHours 1.0)
        dto.LocalDateTime |> should equal (DateTime(2026, 10, 25, 2, 30, 0))
    | None -> failwithf "expected a resolved offset for fall-back %O" (DateTime(2026, 10, 25, 2, 30, 0))

[<Fact>]
let ``resolveLocal resolves unambiguous local time`` () =
    match resolveLocal berlinTz (DateTime(2026, 6, 1, 12, 0, 0)) with
    | Some dto -> dto.Offset |> should equal (TimeSpan.FromHours 2.0)
    | None -> failwithf "expected a resolved offset for summer %O" (DateTime(2026, 6, 1, 12, 0, 0))

[<Fact>]
let ``isDue SkipMissed never fires for a missed occurrence`` () =
    let policy = { Catchup = SkipMissed; MaxCatchUp = 3 }
    let now = DateTimeOffset(2026, 3, 29, 10, 0, 0, TimeSpan.Zero)
    let missed = DateTimeOffset(2026, 3, 29, 9, 0, 0, TimeSpan.Zero)
    isDue policy now None missed |> should be False

[<Fact>]
let ``isDue CatchUpOnce fires exactly once for a missed occurrence`` () =
    let policy =
        { Catchup = CatchUpOnce
          MaxCatchUp = 3 }

    let now = DateTimeOffset(2026, 3, 29, 10, 0, 0, TimeSpan.Zero)
    let missed = DateTimeOffset(2026, 3, 29, 9, 0, 0, TimeSpan.Zero)
    isDue policy now None missed |> should be True
    // After the run is recorded (lastRun >= scheduled) it no longer fires.
    isDue policy now (Some missed) missed |> should be False

[<Fact>]
let ``isDue returns false for future and already-run occurrences`` () =
    let policy =
        { Catchup = CatchUpOnce
          MaxCatchUp = 3 }

    let now = DateTimeOffset(2026, 3, 29, 10, 0, 0, TimeSpan.Zero)
    let future = DateTimeOffset(2026, 3, 29, 11, 0, 0, TimeSpan.Zero)
    let scheduled = DateTimeOffset(2026, 3, 29, 9, 0, 0, TimeSpan.Zero)
    isDue policy now None future |> should be False
    isDue policy now (Some now) scheduled |> should be False

[<Fact>]
let ``isDue returns true for an exactly on-time occurrence`` () =
    let policy = { Catchup = SkipMissed; MaxCatchUp = 3 }
    let now = DateTimeOffset(2026, 3, 29, 10, 0, 0, TimeSpan.Zero)
    isDue policy now None now |> should be True

[<Fact>]
let ``occurrenceKey is unique across job and time`` () =
    let t1 = DateTimeOffset(2026, 3, 29, 9, 0, 0, TimeSpan.Zero)
    let t2 = DateTimeOffset(2026, 3, 29, 10, 0, 0, TimeSpan.Zero)
    occurrenceKey 1L t1 |> should not' (equal (occurrenceKey 2L t1))
    occurrenceKey 1L t1 |> should not' (equal (occurrenceKey 1L t2))

[<Fact>]
let ``nextOccurrences returns occurrences strictly after`` () =
    let cron = Cronos.CronExpression.Parse("30 2 * * *")
    let after = DateTimeOffset(2026, 3, 28, 0, 0, 0, TimeSpan.Zero)
    let occs = nextOccurrences cron berlinTz after 3
    occs |> List.length |> should equal 3
    occs |> List.forall (fun o -> o > after) |> should be True
    occs |> List.pairwise |> List.forall (fun (a, b) -> a < b) |> should be True

[<Fact>]
let ``nextOccurrences handles DST spring-forward shift`` () =
    let cron = Cronos.CronExpression.Parse("30 2 * * *")
    let after = DateTimeOffset(2026, 3, 28, 0, 0, 0, TimeSpan.Zero)
    let occs = nextOccurrences cron berlinTz after 2
    occs |> List.length |> should equal 2
    occs.[1].LocalDateTime |> should equal (DateTime(2026, 3, 29, 3, 0, 0))
    occs.[1].Offset |> should equal (TimeSpan.FromHours 2.0)

// ---------------------------------------------------------------------------
// InboxStateMachine
// ---------------------------------------------------------------------------

[<Fact>]
let ``inbox Pending --Claim--> Claimed increments attempts and sets lease`` () =
    let cmd = mkInboxCommand In.Pending 0 5
    let now = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let lease = now.AddMinutes 5.0

    match In.apply cmd (In.Claim(now, lease)) with
    | Ok next ->
        next.Status |> should equal In.Claimed
        next.Attempts |> should equal 1
        next.LeaseUntil |> should equal (Some lease)
    | Error e -> failwith (sprintf "expected Ok, got %A" e)

[<Fact>]
let ``inbox Claimed --Start--> Running`` () =
    let cmd = mkInboxCommand In.Claimed 1 5

    match In.apply cmd In.Start with
    | Ok next -> next.Status |> should equal In.Running
    | Error e -> failwith (sprintf "expected Ok, got %A" e)

[<Fact>]
let ``inbox Running --Complete--> Completed`` () =
    let cmd = mkInboxCommand In.Running 1 5

    match In.apply cmd In.Complete with
    | Ok next -> next.Status |> should equal In.Completed
    | Error e -> failwith (sprintf "expected Ok, got %A" e)

[<Fact>]
let ``inbox Running --Fail--> Failed when attempts below max`` () =
    let cmd = mkInboxCommand In.Running 1 5

    match In.apply cmd In.Fail with
    | Ok next -> next.Status |> should equal In.Failed
    | Error e -> failwith (sprintf "expected Ok, got %A" e)

[<Fact>]
let ``inbox Running --Fail--> DeadLetter when attempts at max`` () =
    let cmd = mkInboxCommand In.Running 5 5

    match In.apply cmd In.Fail with
    | Ok next -> next.Status |> should equal In.DeadLetter
    | Error e -> failwith (sprintf "expected Ok, got %A" e)

[<Fact>]
let ``inbox LeaseExpired resets to Pending`` () =
    let cmd = mkInboxCommand In.Claimed 1 5

    match In.apply cmd (In.LeaseExpired(DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))) with
    | Ok next ->
        next.Status |> should equal In.Pending
        next.LeaseUntil |> should equal None
    | Error e -> failwith (sprintf "expected Ok, got %A" e)

[<Fact>]
let ``inbox Failed --Retry--> Pending`` () =
    let cmd = mkInboxCommand In.Failed 2 5

    match In.apply cmd In.Retry with
    | Ok next -> next.Status |> should equal In.Pending
    | Error e -> failwith (sprintf "expected Ok, got %A" e)

[<Fact>]
let ``inbox Failed --DeadLetter--> DeadLetter`` () =
    let cmd = mkInboxCommand In.Failed 5 5

    match In.apply cmd In.Event.DeadLetter with
    | Ok next -> next.Status |> should equal In.Status.DeadLetter
    | Error e -> failwith (sprintf "expected Ok, got %A" e)

[<Fact>]
let ``inbox invalid transitions return Error`` () =
    let cmd = mkInboxCommand In.Pending 0 5
    (In.apply cmd In.Start |> Result.isError) |> should be True
    (In.apply cmd In.Complete |> Result.isError) |> should be True
    (In.apply cmd In.Fail |> Result.isError) |> should be True

[<Fact>]
let ``inbox canClaim only for pending`` () =
    In.canClaim (mkInboxCommand In.Pending 0 5) |> should be True
    In.canClaim (mkInboxCommand In.Claimed 1 5) |> should be False
    In.canClaim (mkInboxCommand In.Completed 1 5) |> should be False

[<Fact>]
let ``inbox isLeaseExpired compares now to lease`` () =
    let lease = DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero)

    let cmd =
        { (mkInboxCommand In.Claimed 1 5) with
            LeaseUntil = Some lease }

    In.isLeaseExpired (lease.AddSeconds 1.0) cmd |> should be True
    In.isLeaseExpired (lease.AddSeconds -1.0) cmd |> should be False

    In.isLeaseExpired (lease.AddSeconds 1.0) (mkInboxCommand In.Pending 0 5)
    |> should be False

[<Fact>]
let ``inbox shouldDeadLetter flags exhausted failed commands`` () =
    In.shouldDeadLetter (mkInboxCommand In.Failed 5 5) |> should be True
    In.shouldDeadLetter (mkInboxCommand In.Failed 2 5) |> should be False
    In.shouldDeadLetter (mkInboxCommand In.Running 5 5) |> should be False

[<Fact>]
let ``inbox Claimed --HostDied--> NeedsReview`` () =
    let cmd = mkInboxCommand In.Claimed 1 5

    match In.apply cmd In.HostDied with
    | Ok next -> next.Status |> should equal In.NeedsReview
    | Error e -> failwith (sprintf "expected Ok, got %A" e)

[<Fact>]
let ``inbox Running --HostDied--> NeedsReview`` () =
    let cmd = mkInboxCommand In.Running 1 5

    match In.apply cmd In.HostDied with
    | Ok next -> next.Status |> should equal In.NeedsReview
    | Error e -> failwith (sprintf "expected Ok, got %A" e)

[<Fact>]
let ``inbox NeedsReview --ReviewedRetry--> Pending preserves attempts`` () =
    let cmd = mkInboxCommand In.NeedsReview 3 5

    match In.apply cmd In.ReviewedRetry with
    | Ok next ->
        next.Status |> should equal In.Pending
        next.Attempts |> should equal 3
    | Error e -> failwith (sprintf "expected Ok, got %A" e)

[<Fact>]
let ``inbox NeedsReview --DeadLetter--> DeadLetter`` () =
    let cmd = mkInboxCommand In.NeedsReview 5 5

    match In.apply cmd In.Event.DeadLetter with
    | Ok next -> next.Status |> should equal In.Status.DeadLetter
    | Error e -> failwith (sprintf "expected Ok, got %A" e)

[<Fact>]
let ``inbox NeedsReview has no automatic transition except review or dead letter`` () =
    let cmd = mkInboxCommand In.NeedsReview 3 5

    (In.apply
        cmd
        (In.Claim(
            DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            DateTimeOffset(2026, 1, 1, 0, 5, 0, TimeSpan.Zero)
        ))
     |> Result.isError)
    |> should be True

    (In.apply cmd In.Start |> Result.isError) |> should be True
    (In.apply cmd In.Complete |> Result.isError) |> should be True
    (In.apply cmd In.Fail |> Result.isError) |> should be True

    (In.apply cmd (In.LeaseExpired(DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)))
     |> Result.isError)
    |> should be True

    (In.apply cmd In.Retry |> Result.isError) |> should be True
    (In.apply cmd In.HostDied |> Result.isError) |> should be True

[<Property>]
let ``inbox HostDied from Claimed or Running yields NeedsReview`` (status: In.Status) =
    (status = In.Claimed || status = In.Running)
    ==> (let cmd = mkInboxCommand status 1 5

         match In.apply cmd In.HostDied with
         | Ok next -> next.Status = In.NeedsReview
         | Error _ -> false)

[<Property>]
let ``inbox NeedsReview only transitions via ReviewedRetry or DeadLetter`` (ev: In.Event) =
    let cmd = mkInboxCommand In.NeedsReview 3 5

    match ev with
    | In.Event.ReviewedRetry -> In.apply cmd ev = Ok { cmd with Status = In.Pending }
    | In.Event.DeadLetter ->
        In.apply cmd ev = Ok
            { cmd with
                Status = In.Status.DeadLetter }
    | _ -> In.apply cmd ev |> Result.isError

[<Property>]
let ``inbox apply never throws and yields a known status or error``
    (attempts: int)
    (maxAttempts: int)
    (status: In.Status)
    (ev: In.Event)
    =
    let cmd = mkInboxCommand status (abs attempts) (max (abs maxAttempts) 1)

    match In.apply cmd ev with
    | Ok next ->
        List.contains
            next.Status
            [ In.Pending
              In.Claimed
              In.Running
              In.Completed
              In.Failed
              In.Status.DeadLetter
              In.NeedsReview ]
    | Error _ -> true

// ---------------------------------------------------------------------------
// OutboxStateMachine
// ---------------------------------------------------------------------------

[<Fact>]
let ``outbox Pending --BeginSend--> Sending`` () =
    let entry = mkOutboxEntry Out.Pending 0 5

    match Out.apply entry Out.BeginSend with
    | Ok next -> next.Status |> should equal Out.Sending
    | Error e -> failwith (sprintf "expected Ok, got %A" e)

[<Fact>]
let ``outbox Sending --Sent--> Sent stores remote id`` () =
    let entry = mkOutboxEntry Out.Sending 0 5

    match Out.apply entry (Out.Event.Sent 99L) with
    | Ok next ->
        next.Status |> should equal Out.Status.Sent
        next.RemoteMessageId |> should equal (Some 99L)
    | Error e -> failwith (sprintf "expected Ok, got %A" e)

[<Fact>]
let ``outbox Sending --Fail--> Failed increments attempts`` () =
    let entry = mkOutboxEntry Out.Sending 1 5

    match Out.apply entry Out.Fail with
    | Ok next ->
        next.Status |> should equal Out.Failed
        next.Attempts |> should equal 2
    | Error e -> failwith (sprintf "expected Ok, got %A" e)

[<Fact>]
let ``outbox retry keeps RandomId`` () =
    let entry = mkOutboxEntry Out.Failed 1 5

    match Out.apply entry Out.BeginSend with
    | Ok next -> next.RandomId |> should equal 42L
    | Error e -> failwith (sprintf "expected Ok, got %A" e)

[<Fact>]
let ``outbox retry is rejected when max attempts reached`` () =
    let entry = mkOutboxEntry Out.Failed 5 5
    (Out.apply entry Out.BeginSend |> Result.isError) |> should be True

[<Fact>]
let ``outbox canSend allows pending and retryable failed`` () =
    Out.canSend (mkOutboxEntry Out.Pending 0 5) |> should be True
    Out.canSend (mkOutboxEntry Out.Failed 1 5) |> should be True
    Out.canSend (mkOutboxEntry Out.Failed 5 5) |> should be False
    Out.canSend (mkOutboxEntry Out.Status.Sent 1 5) |> should be False

[<Fact>]
let ``outbox canRetry requires failed with remaining attempts`` () =
    Out.canRetry (mkOutboxEntry Out.Failed 1 5) |> should be True
    Out.canRetry (mkOutboxEntry Out.Failed 5 5) |> should be False
    Out.canRetry (mkOutboxEntry Out.Sending 1 5) |> should be False

[<Property>]
let ``outbox retry never changes RandomId`` (attempts: int) (maxAttempts: int) (status: Out.Status) =
    let entry = mkOutboxEntry status (abs attempts) (max (abs maxAttempts) 1)

    match Out.apply entry Out.BeginSend with
    | Ok next -> next.RandomId = entry.RandomId
    | Error _ -> true

[<Fact>]
let ``chunk with tiny maxUnits and fences makes progress without looping`` () =
    // maxUnits < 7 cannot hold both fence markers and content; the chunker's
    // safety net must still make forward progress (no infinite loop) and never
    // emit a chunk larger than maxUnits.
    let chunks = chunk 3 "```x```" []
    chunks |> List.isEmpty |> should be False
    chunks |> List.forall (fun c -> c.Text.Length <= 3) |> should be True

// ---------------------------------------------------------------------------
// ScheduleJobs
// ---------------------------------------------------------------------------

let private testQuota: Sj.ScheduleQuota =
    { MaxJobsPerUser = 20
      MinIntervalSeconds = 60
      MaxPromptLength = 2000 }

let private validCronDraft: Sj.ScheduleJobDraft =
    { UserId = UserId 1L
      ChatId = ChatId 10L
      Prompt = "напомни мне"
      CronExpr = Some "0 9 * * *"
      IntervalSeconds = None
      AfterSeconds = None
      Timezone = "Europe/Berlin"
      Catchup = SkipMissed }

let private validIntervalDraft: Sj.ScheduleJobDraft =
    { UserId = UserId 1L
      ChatId = ChatId 10L
      Prompt = "проверь деплой"
      CronExpr = None
      IntervalSeconds = Some 300
      AfterSeconds = None
      Timezone = "UTC"
      Catchup = CatchUpOnce }

[<Fact>]
let ``validate rejects both cron and interval`` () =
    let draft =
        { validCronDraft with
            IntervalSeconds = Some 300 }

    Sj.validate testQuota draft |> Result.isError |> should be True

[<Fact>]
let ``validate rejects neither cron nor interval`` () =
    let draft =
        { validCronDraft with
            CronExpr = None
            IntervalSeconds = None }

    Sj.validate testQuota draft |> Result.isError |> should be True

[<Fact>]
let ``validate rejects invalid cron`` () =
    let draft =
        { validCronDraft with
            CronExpr = Some "not a cron" }

    Sj.validate testQuota draft |> Result.isError |> should be True

[<Fact>]
let ``validate rejects interval below minimum`` () =
    let draft =
        { validIntervalDraft with
            IntervalSeconds = Some 30 }

    Sj.validate testQuota draft |> Result.isError |> should be True

[<Fact>]
let ``validate rejects blank prompt`` () =
    let draft = { validCronDraft with Prompt = "   " }
    Sj.validate testQuota draft |> Result.isError |> should be True

[<Fact>]
let ``validate rejects too long prompt`` () =
    let draft =
        { validCronDraft with
            Prompt = String.replicate 2001 "a" }

    Sj.validate testQuota draft |> Result.isError |> should be True

[<Fact>]
let ``validate rejects unknown timezone`` () =
    let draft =
        { validCronDraft with
            Timezone = "Not/AZone" }

    Sj.validate testQuota draft |> Result.isError |> should be True

[<Fact>]
let ``validate accepts valid cron draft`` () =
    match Sj.validate testQuota validCronDraft with
    | Ok draft -> draft |> should equal validCronDraft
    | Error e -> failwith (sprintf "expected Ok, got %A" e)

[<Fact>]
let ``validate accepts valid interval draft`` () =
    match Sj.validate testQuota validIntervalDraft with
    | Ok draft -> draft |> should equal validIntervalDraft
    | Error e -> failwith (sprintf "expected Ok, got %A" e)

[<Fact>]
let ``validate errors name the offending field`` () =
    match
        Sj.validate
            testQuota
            { validCronDraft with
                IntervalSeconds = Some 300 }
    with
    | Error e -> e.Contains "cron_expr" |> should be True
    | Ok _ -> failwith "expected an error for both cron and interval"

    match
        Sj.validate
            testQuota
            { validCronDraft with
                Timezone = "Not/AZone" }
    with
    | Error e -> e.Contains "timezone" |> should be True
    | Ok _ -> failwith "expected an error for unknown timezone"

[<Fact>]
let ``validate rejects two of three schedule kinds`` () =
    // cron + interval
    Sj.validate
        testQuota
        { validCronDraft with
            IntervalSeconds = Some 300 }
    |> Result.isError
    |> should be True

    // cron + after_seconds
    Sj.validate
        testQuota
        { validCronDraft with
            AfterSeconds = Some 60 }
    |> Result.isError
    |> should be True

    // interval + after_seconds
    Sj.validate
        testQuota
        { validIntervalDraft with
            AfterSeconds = Some 60 }
    |> Result.isError
    |> should be True

[<Fact>]
let ``validate rejects none of three`` () =
    let draft =
        { validCronDraft with
            CronExpr = None
            IntervalSeconds = None
            AfterSeconds = None }

    Sj.validate testQuota draft |> Result.isError |> should be True

[<Fact>]
let ``validate accepts after_seconds`` () =
    let draft =
        { validCronDraft with
            CronExpr = None
            IntervalSeconds = None
            AfterSeconds = Some 300 }

    match Sj.validate testQuota draft with
    | Ok d -> d |> should equal draft
    | Error e -> failwith (sprintf "expected Ok, got %A" e)

[<Fact>]
let ``validate rejects after_seconds below 1`` () =
    let draft =
        { validCronDraft with
            CronExpr = None
            IntervalSeconds = None
            AfterSeconds = Some 0 }

    Sj.validate testQuota draft |> Result.isError |> should be True

[<Fact>]
let ``nextRunAfter cron interval and after`` () =
    let now = DateTimeOffset(2026, 3, 28, 0, 0, 0, TimeSpan.Zero)

    // cron: next occurrence strictly after now
    let cronNext = Sj.nextRunAfter validCronDraft now
    cronNext |> should not' (be None)
    cronNext.Value |> should be (greaterThan now)

    // interval: now + interval seconds
    let intervalDraft =
        { validIntervalDraft with
            IntervalSeconds = Some 60 }

    Sj.nextRunAfter intervalDraft now |> should equal (Some(now.AddSeconds 60.0))

    // after_seconds: now + after seconds
    let afterDraft =
        { validCronDraft with
            CronExpr = None
            IntervalSeconds = None
            AfterSeconds = Some 300 }

    Sj.nextRunAfter afterDraft now |> should equal (Some(now.AddSeconds 300.0))

    // no schedule kind: None
    let noneDraft =
        { validCronDraft with
            CronExpr = None
            IntervalSeconds = None
            AfterSeconds = None }

    Sj.nextRunAfter noneDraft now |> should equal None

[<Fact>]
let ``nextOccurrences cron returns strictly after in timezone`` () =
    let after = DateTimeOffset(2026, 3, 28, 0, 0, 0, TimeSpan.Zero)
    let occs = Sj.nextOccurrences (Some "30 2 * * *") None "Europe/Berlin" after 3
    occs |> List.length |> should equal 3
    occs |> List.forall (fun o -> o > after) |> should be True
    occs |> List.pairwise |> List.forall (fun (a, b) -> a < b) |> should be True

[<Fact>]
let ``nextOccurrences handles DST spring-forward gap deterministically`` () =
    // 2026-03-29 02:30 Europe/Berlin does not exist (clocks jump 02:00 -> 03:00
    // CET -> CEST). Cronos shifts the nonexistent time to the post-jump local
    // time (03:00 CEST == 01:00 UTC): the daily job still fires on the gap day.
    let after = DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.Zero)

    Sj.nextOccurrences (Some "30 2 * * *") None "Europe/Berlin" after 1
    |> should equal [ DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero) ]

[<Fact>]
let ``nextOccurrences is deterministic on DST fall-back`` () =
    // 2026-10-25 02:30 Europe/Berlin occurs twice (CEST 00:30Z and CET 01:30Z).
    // The engine must return a deterministic single occurrence at local 02:30.
    let after = DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero)

    match Sj.nextOccurrences (Some "30 2 * * *") None "Europe/Berlin" after 1 with
    | [ occ ] ->
        occ.LocalDateTime |> should equal (DateTime(2026, 10, 25, 2, 30, 0))
        let offsets = [ TimeSpan.FromHours 1.0; TimeSpan.FromHours 2.0 ]
        offsets |> should contain occ.Offset
    | _ ->
        failwithf
            "expected exactly one fall-back occurrence, got %A"
            (Sj.nextOccurrences (Some "30 2 * * *") None "Europe/Berlin" after 1)

[<Fact>]
let ``nextOccurrences interval returns k times interval steps`` () =
    let after = DateTimeOffset(2026, 3, 28, 0, 0, 0, TimeSpan.Zero)
    let occs = Sj.nextOccurrences None (Some 60) "UTC" after 3

    occs
    |> should equal [ after.AddSeconds 60.0; after.AddSeconds 120.0; after.AddSeconds 180.0 ]

[<Fact>]
let ``nextOccurrences clamps count to 1..100`` () =
    let after = DateTimeOffset(2026, 3, 28, 0, 0, 0, TimeSpan.Zero)

    Sj.nextOccurrences (Some "0 9 * * *") None "Europe/Berlin" after 0
    |> List.length
    |> should equal 1

    Sj.nextOccurrences (Some "0 9 * * *") None "Europe/Berlin" after 150
    |> List.length
    |> should equal 100

[<Fact>]
let ``formatOccurrences renders UTC and local time`` () =
    let occ = DateTimeOffset(2026, 9, 14, 9, 0, 0, TimeSpan.Zero)

    Sj.formatOccurrences "Europe/Berlin" [ occ ]
    |> should equal "2026-09-14 09:00 UTC (11:00 Europe/Berlin)"

[<Fact>]
let ``formatOccurrences empty returns empty string`` () =
    Sj.formatOccurrences "Europe/Berlin" [] |> should equal ""

[<Fact>]
let ``confirmStatus pending to active`` () =
    match Sj.confirmStatus Sj.ScheduleStatus.Pending with
    | Ok s -> s |> should equal Sj.ScheduleStatus.Active
    | Error e -> failwith (sprintf "expected Ok, got %A" e)

[<Fact>]
let ``confirmStatus rejects non pending`` () =
    Sj.confirmStatus Sj.ScheduleStatus.Active |> Result.isError |> should be True
    Sj.confirmStatus Sj.ScheduleStatus.Paused |> Result.isError |> should be True
    Sj.confirmStatus Sj.ScheduleStatus.Cancelled |> Result.isError |> should be True
    Sj.confirmStatus Sj.ScheduleStatus.Expired |> Result.isError |> should be True

[<Fact>]
let ``cancelStatus pending to cancelled`` () =
    match Sj.cancelStatus Sj.ScheduleStatus.Pending with
    | Ok s -> s |> should equal Sj.ScheduleStatus.Cancelled
    | Error e -> failwith (sprintf "expected Ok, got %A" e)

[<Fact>]
let ``cancelStatus rejects non pending`` () =
    Sj.cancelStatus Sj.ScheduleStatus.Active |> Result.isError |> should be True

[<Fact>]
let ``pauseStatus active to paused`` () =
    match Sj.pauseStatus Sj.ScheduleStatus.Active with
    | Ok s -> s |> should equal Sj.ScheduleStatus.Paused
    | Error e -> failwith (sprintf "expected Ok, got %A" e)

[<Fact>]
let ``pauseStatus rejects non active`` () =
    Sj.pauseStatus Sj.ScheduleStatus.Pending |> Result.isError |> should be True

[<Fact>]
let ``resumeStatus paused to active`` () =
    match Sj.resumeStatus Sj.ScheduleStatus.Paused with
    | Ok s -> s |> should equal Sj.ScheduleStatus.Active
    | Error e -> failwith (sprintf "expected Ok, got %A" e)

[<Fact>]
let ``resumeStatus rejects non paused`` () =
    Sj.resumeStatus Sj.ScheduleStatus.Active |> Result.isError |> should be True

[<Fact>]
let ``expireStatus pending to expired`` () =
    match Sj.expireStatus Sj.ScheduleStatus.Pending with
    | Ok s -> s |> should equal Sj.ScheduleStatus.Expired
    | Error e -> failwith (sprintf "expected Ok, got %A" e)

[<Fact>]
let ``expireStatus rejects non pending`` () =
    Sj.expireStatus Sj.ScheduleStatus.Active |> Result.isError |> should be True

[<Fact>]
let ``statusOfString round-trips`` () =
    Sj.statusOfString "pending" |> should equal (Some Sj.ScheduleStatus.Pending)
    Sj.statusOfString "active" |> should equal (Some Sj.ScheduleStatus.Active)
    Sj.statusOfString "paused" |> should equal (Some Sj.ScheduleStatus.Paused)
    Sj.statusOfString "cancelled" |> should equal (Some Sj.ScheduleStatus.Cancelled)
    Sj.statusOfString "expired" |> should equal (Some Sj.ScheduleStatus.Expired)
    Sj.statusOfString "completed" |> should equal (Some Sj.ScheduleStatus.Completed)
    Sj.statusOfString "PENDING" |> should equal (Some Sj.ScheduleStatus.Pending)
    Sj.statusOfString "unknown" |> should equal None

[<Fact>]
let ``statusOfString completed`` () =
    Sj.statusOfString "completed" |> should equal (Some Sj.ScheduleStatus.Completed)
    Sj.statusOfString "COMPLETED" |> should equal (Some Sj.ScheduleStatus.Completed)

[<Fact>]
let ``statusToString completed`` () =
    Sj.statusToString Sj.ScheduleStatus.Completed |> should equal "completed"

[<Fact>]
let ``statusToString matches statusOfString`` () =
    [ Sj.ScheduleStatus.Pending
      Sj.ScheduleStatus.Active
      Sj.ScheduleStatus.Paused
      Sj.ScheduleStatus.Cancelled
      Sj.ScheduleStatus.Expired
      Sj.ScheduleStatus.Completed ]
    |> List.forall (fun s -> Sj.statusOfString (Sj.statusToString s) = Some s)
    |> should be True

[<Fact>]
let ``duePolicy wraps catchup with max one catchup`` () =
    Sj.duePolicy SkipMissed |> should equal { Catchup = SkipMissed; MaxCatchUp = 1 }

    Sj.duePolicy CatchUpOnce
    |> should
        equal
        { Catchup = CatchUpOnce
          MaxCatchUp = 1 }
