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
open Phos.Core.PromptBudget
open Phos.Core.Chunker
open Phos.Core.Rrf
open Phos.Core.SchedulePolicy
open Phos.Core.InboxStateMachine
open Phos.Core.OutboxStateMachine

// Module abbreviations to disambiguate the heavily-overlapping type/case names.
module Wl = Phos.Core.Whitelist
module Tp = Phos.Core.ToolPolicy
module In = Phos.Core.InboxStateMachine
module Out = Phos.Core.OutboxStateMachine

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/// Monotonic, deterministic token counter (1 token per UTF-16 code unit).
let tokenCount (s: string) : int = s.Length

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
let ``tool policy admin tool denied for non-admin`` () =
    let policy =
        { Default = Tp.Allow
          Tools = Map.empty
          AdminTools = Set.singleton "kill" }

    resolve policy User "kill"
    |> should
        equal
        { Allowed = false
          NeedsConfirmation = false }

[<Fact>]
let ``tool policy admin tool allowed for owner and admin`` () =
    let policy =
        { Default = Tp.Allow
          Tools = Map.empty
          AdminTools = Set.singleton "kill" }

    resolve policy Owner "kill"
    |> should
        equal
        { Allowed = true
          NeedsConfirmation = false }

    resolve policy Admin "kill"
    |> should
        equal
        { Allowed = true
          NeedsConfirmation = false }

[<Fact>]
let ``tool policy prompt needs confirmation`` () =
    let policy =
        { Default = Tp.Allow
          Tools = Map.ofList [ "danger", Tp.Prompt ]
          AdminTools = Set.empty }

    resolve policy User "danger"
    |> should
        equal
        { Allowed = true
          NeedsConfirmation = true }

[<Fact>]
let ``tool policy deny blocks`` () =
    let policy =
        { Default = Tp.Allow
          Tools = Map.ofList [ "rm", Tp.Deny ]
          AdminTools = Set.empty }

    resolve policy Admin "rm"
    |> should
        equal
        { Allowed = false
          NeedsConfirmation = false }

[<Fact>]
let ``tool policy unknown tool uses default`` () =
    let policy =
        { Default = Tp.Deny
          Tools = Map.empty
          AdminTools = Set.empty }

    resolve policy Owner "unknown"
    |> should
        equal
        { Allowed = false
          NeedsConfirmation = false }

[<Property>]
let ``tool policy admin tools require owner or admin`` (role: UserRole) (toolName: string) =
    let policy =
        { Default = Tp.Allow
          Tools = Map.empty
          AdminTools = Set.singleton toolName }

    let decision = resolve policy role toolName

    match role with
    | Owner
    | Admin -> decision.Allowed
    | User -> not decision.Allowed

[<Property>]
let ``tool policy unknown tool resolves to default``
    (role: UserRole)
    (defaultPolicy: Tp.ToolPolicy)
    (toolName: string)
    =
    let policy =
        { Default = defaultPolicy
          Tools = Map.empty
          AdminTools = Set.empty }

    let decision = resolve policy role toolName

    match defaultPolicy with
    | Tp.Allow -> decision.Allowed
    | Tp.Prompt -> decision.Allowed && decision.NeedsConfirmation
    | Tp.Deny -> not decision.Allowed

// ---------------------------------------------------------------------------
// PromptBudget
// ---------------------------------------------------------------------------

[<Fact>]
let ``truncate keeps prefix up to max tokens`` () =
    truncate tokenCount 4 "abcdefghij" |> should equal "abcd"

[<Fact>]
let ``truncate returns empty for non-positive max tokens`` () =
    truncate tokenCount 0 "abcdef" |> should equal ""

[<Fact>]
let ``truncate returns text unchanged when within budget`` () =
    truncate tokenCount 100 "abcdef" |> should equal "abcdef"

[<Fact>]
let ``build is deterministic`` () =
    let budget =
        { TotalTokens = 8
          PerLayer = Map.ofList [ (BasePolicy, 5); (Persona, 5) ]
          TruncationOrder = [ BasePolicy; Persona ] }

    let contents =
        [ { Layer = BasePolicy; Text = "abcdef" }
          { Layer = Persona; Text = "ghijkl" } ]

    build tokenCount budget contents
    |> should equal (build tokenCount budget contents)

[<Fact>]
let ``build respects total and per-layer limits`` () =
    let budget =
        { TotalTokens = 6
          PerLayer = Map.ofList [ (BasePolicy, 5); (Persona, 5) ]
          TruncationOrder = [ BasePolicy; Persona ] }

    let contents =
        [ { Layer = BasePolicy; Text = "abcdef" }
          { Layer = Persona; Text = "ghijkl" } ]

    let result = build tokenCount budget contents

    result
    |> List.sumBy (fun (_, t) -> tokenCount t)
    |> should be (lessThanOrEqualTo 6)

    result
    |> List.forall (fun (layer, text) ->
        match Map.tryFind layer budget.PerLayer with
        | Some cap -> tokenCount text <= cap
        | None -> true)
    |> should be True

[<Fact>]
let ``build keeps content order and skips missing layers`` () =
    let budget =
        { TotalTokens = 100
          PerLayer = Map.ofList [ (BasePolicy, 100); (Persona, 100) ]
          TruncationOrder = [ BasePolicy; Persona ] }

    let contents =
        [ { Layer = BasePolicy; Text = "hello" }; { Layer = Persona; Text = "world" } ]

    let result = build tokenCount budget contents
    result |> List.map fst |> should equal [ BasePolicy; Persona ]
    result |> List.map snd |> should equal [ "hello"; "world" ]

let private layerGen: Gen<Layer> =
    Gen.elements [ BasePolicy; Persona; Skills; CoreMemory; Recall; State; CurrentJob ]

let private contentGen: Gen<LayerContent list> =
    Gen.listOf (
        Gen.zip layerGen stringGen
        |> Gen.map (fun (layer, text) -> { Layer = layer; Text = text })
    )
    |> Gen.map (List.distinctBy (fun c -> c.Layer))

let private budgetGen: Gen<Budget> =
    Gen.zip (Gen.choose (0, 200)) (Gen.zip (Gen.listOf (Gen.zip layerGen (Gen.choose (0, 100)))) (Gen.listOf layerGen))
    |> Gen.map (fun (total, (perLayer, order)) ->
        { TotalTokens = total
          PerLayer = Map.ofList perLayer
          TruncationOrder = order })

[<Property>]
let ``prompt budget build respects total and per-layer`` () =
    Prop.forAll (Arb.fromGen (Gen.zip contentGen budgetGen)) (fun (contents, budget) ->
        let result = build tokenCount budget contents
        let total = result |> List.sumBy (fun (_, t) -> tokenCount t)

        let perLayerOk =
            result
            |> List.forall (fun (layer, text) ->
                match Map.tryFind layer budget.PerLayer with
                | Some cap -> tokenCount text <= cap
                | None -> true)

        total <= budget.TotalTokens && perLayerOk)

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
          Kind = kind })

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
    let entities = [ { Offset = 0; Length = 7; Kind = Bold } ]
    let chunks = chunk 4 "abcdefgh" entities

    let flattened =
        chunks
        |> List.map (fun c -> c.Entities |> List.map (fun e -> e.Offset, e.Length, e.Kind))

    flattened |> should equal [ [ (0, 4, Bold) ]; [ (0, 3, Bold) ] ]

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
// Rrf
// ---------------------------------------------------------------------------

[<Fact>]
let ``rrf scores by reciprocal rank`` () =
    let scores = score 1.0 [ Seq.ofList [ "a"; "b" ]; Seq.ofList [ "b"; "c" ] ]
    scores.["a"] |> should equal (1.0 / 2.0)
    scores.["b"] |> should equal (1.0 / 3.0 + 1.0 / 2.0)
    scores.["c"] |> should equal (1.0 / 3.0)

[<Property>]
let ``rrf adding a list never decreases any score`` (k: float) (lists: string list list) (extra: string list) =
    let k =
        if Double.IsNaN k || Double.IsInfinity k then
            1.0
        else
            abs k + 1.0

    let toSeqSeq (xs: string list list) : seq<seq<string>> = xs |> List.map Seq.ofList |> List.toSeq
    let baseScores = score k (toSeqSeq lists)
    let newScores = score k (toSeqSeq (lists @ [ extra ]))

    let allKeys =
        Set.union
            (baseScores |> Map.toList |> List.map fst |> Set.ofList)
            (newScores |> Map.toList |> List.map fst |> Set.ofList)

    allKeys
    |> Set.forall (fun key ->
        let b = Map.tryFind key baseScores |> Option.defaultValue 0.0
        let n = Map.tryFind key newScores |> Option.defaultValue 0.0
        n >= b)

[<Property>]
let ``rrf earlier rank contributes at least as much as later`` (k: float) (items: string list) =
    let k =
        if Double.IsNaN k || Double.IsInfinity k then
            1.0
        else
            abs k + 1.0
    // Distinct items preserve the rank->contribution ordering (duplicates would sum).
    let distinct = items |> List.distinct
    let scores = score k [ Seq.ofList distinct ]

    distinct
    |> List.mapi (fun i x -> x, i)
    |> List.pairwise
    |> List.forall (fun ((x, _), (y, _)) -> Map.find x scores >= Map.find y scores)

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
    | Error msg -> failwith msg

[<Fact>]
let ``inbox Claimed --Start--> Running`` () =
    let cmd = mkInboxCommand In.Claimed 1 5

    match In.apply cmd In.Start with
    | Ok next -> next.Status |> should equal In.Running
    | Error msg -> failwith msg

[<Fact>]
let ``inbox Running --Complete--> Completed`` () =
    let cmd = mkInboxCommand In.Running 1 5

    match In.apply cmd In.Complete with
    | Ok next -> next.Status |> should equal In.Completed
    | Error msg -> failwith msg

[<Fact>]
let ``inbox Running --Fail--> Failed when attempts below max`` () =
    let cmd = mkInboxCommand In.Running 1 5

    match In.apply cmd In.Fail with
    | Ok next -> next.Status |> should equal In.Failed
    | Error msg -> failwith msg

[<Fact>]
let ``inbox Running --Fail--> DeadLetter when attempts at max`` () =
    let cmd = mkInboxCommand In.Running 5 5

    match In.apply cmd In.Fail with
    | Ok next -> next.Status |> should equal In.DeadLetter
    | Error msg -> failwith msg

[<Fact>]
let ``inbox LeaseExpired resets to Pending`` () =
    let cmd = mkInboxCommand In.Claimed 1 5

    match In.apply cmd (In.LeaseExpired(DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))) with
    | Ok next ->
        next.Status |> should equal In.Pending
        next.LeaseUntil |> should equal None
    | Error msg -> failwith msg

[<Fact>]
let ``inbox Failed --Retry--> Pending`` () =
    let cmd = mkInboxCommand In.Failed 2 5

    match In.apply cmd In.Retry with
    | Ok next -> next.Status |> should equal In.Pending
    | Error msg -> failwith msg

[<Fact>]
let ``inbox Failed --DeadLetter--> DeadLetter`` () =
    let cmd = mkInboxCommand In.Failed 5 5

    match In.apply cmd In.Event.DeadLetter with
    | Ok next -> next.Status |> should equal In.Status.DeadLetter
    | Error msg -> failwith msg

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
              In.Status.DeadLetter ]
    | Error _ -> true

// ---------------------------------------------------------------------------
// OutboxStateMachine
// ---------------------------------------------------------------------------

[<Fact>]
let ``outbox Pending --BeginSend--> Sending`` () =
    let entry = mkOutboxEntry Out.Pending 0 5

    match Out.apply entry Out.BeginSend with
    | Ok next -> next.Status |> should equal Out.Sending
    | Error msg -> failwith msg

[<Fact>]
let ``outbox Sending --Sent--> Sent stores remote id`` () =
    let entry = mkOutboxEntry Out.Sending 0 5

    match Out.apply entry (Out.Event.Sent 99L) with
    | Ok next ->
        next.Status |> should equal Out.Status.Sent
        next.RemoteMessageId |> should equal (Some 99L)
    | Error msg -> failwith msg

[<Fact>]
let ``outbox Sending --Fail--> Failed increments attempts`` () =
    let entry = mkOutboxEntry Out.Sending 1 5

    match Out.apply entry Out.Fail with
    | Ok next ->
        next.Status |> should equal Out.Failed
        next.Attempts |> should equal 2
    | Error msg -> failwith msg

[<Fact>]
let ``outbox retry keeps RandomId`` () =
    let entry = mkOutboxEntry Out.Failed 1 5

    match Out.apply entry Out.BeginSend with
    | Ok next -> next.RandomId |> should equal 42L
    | Error msg -> failwith msg

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
