module Phos.Core.Chunker

/// Kind of a message entity.
type EntityKind =
    | Bold
    | Italic
    | Code
    | Pre
    | TextUrl
    | Mention
    | Hashtag
    | Unknown

/// A message entity, with offsets measured in UTF-16 code units.
/// `Url` carries the destination URL for `TextUrl` entities (e.g. a markdown
/// link); it is `None` for all other kinds.
type Entity =
    { Offset: int
      Length: int
      Kind: EntityKind
      Url: string option }

/// A chunk of the original text plus its rebased entities and fence state.
type Chunk =
    { Text: string
      Entities: Entity list
      FenceOpened: bool
      FenceClosed: bool }

/// Splits `text` into chunks of at most `maxUnits` UTF-16 code units.
///
/// Invariants:
/// - each `Chunk.Text` is at most `maxUnits` code units;
/// - entity offsets/lengths are rebased into chunk-local coordinates (entities that
///   cross a chunk boundary are split, keeping the same `Kind` and `Url`);
/// - ``` fences are never cut: a split inside a fence closes the fence in the current
///   chunk (`FenceClosed`) and reopens it in the next (`FenceOpened`);
/// - for fence-free text the concatenation of `Chunk.Text` equals the original `text`.
///
/// Degenerate case: for `maxUnits < 7` a ``` fence split cannot hold any content
/// while satisfying the size bound, so fence markers are treated as literal text
/// (the production value 4096 is far above this threshold).
let chunk (maxUnits: int) (text: string) (entities: Entity list) : Chunk list =
    let n = text.Length
    let maxUnits = max 1 maxUnits
    let handleFences = maxUnits >= 7

    // A fence delimiter is a maximal run of exactly three backticks.
    let isFenceDelim (p: int) : bool =
        handleFences
        && p + 2 < n
        && text.[p] = '`'
        && text.[p + 1] = '`'
        && text.[p + 2] = '`'
        && (p = 0 || text.[p - 1] <> '`')
        && (p + 3 >= n || text.[p + 3] <> '`')

    let sortedEntities = entities |> List.sortBy (fun e -> e.Offset)

    let result = ResizeArray<Chunk>()
    let mutable pos = 0
    let mutable resumeFence = false

    while pos < n do
        let chunkStart = pos
        let sb = System.Text.StringBuilder()
        let mutable len = 0
        let mutable inFence = false
        let mutable fenceOpened = false
        let mutable fenceClosed = false

        if resumeFence then
            sb.Append("```") |> ignore
            len <- 3
            inFence <- true
            fenceOpened <- true
            resumeFence <- false

        let mutable stop = false

        while pos < n && not stop do
            if isFenceDelim pos then
                if inFence then
                    // Closing delimiter; it always fits because of the in-fence reserve.
                    sb.Append("```") |> ignore
                    len <- len + 3
                    pos <- pos + 3
                    inFence <- false
                elif len + 6 <= maxUnits then
                    // Opening delimiter: reserve room for a potential close.
                    sb.Append("```") |> ignore
                    len <- len + 3
                    pos <- pos + 3
                    inFence <- true
                else
                    stop <- true
            else
                let limit = if inFence then maxUnits - 3 else maxUnits

                if len + 1 <= limit then
                    sb.Append(text.[pos]) |> ignore
                    len <- len + 1
                    pos <- pos + 1
                else
                    stop <- true

        if inFence then
            sb.Append("```") |> ignore
            len <- len + 3
            fenceClosed <- true

        // Rebase entities overlapping [chunkStart, pos) into chunk coordinates.
        let prefixLen = if fenceOpened then 3 else 0

        let chunkEntities =
            sortedEntities
            |> List.choose (fun e ->
                let eStart = max e.Offset chunkStart
                let eEnd = min (e.Offset + e.Length) pos

                if eStart < eEnd then
                    Some
                        { Offset = (eStart - chunkStart) + prefixLen
                          Length = eEnd - eStart
                          Kind = e.Kind
                          Url = e.Url }
                else
                    None)

        result.Add(
            { Text = sb.ToString()
              Entities = chunkEntities
              FenceOpened = fenceOpened
              FenceClosed = fenceClosed }
        )

        // Safety net: if no original characters were consumed (e.g. a resumed fence
        // that cannot hold content at a tiny maxUnits), force progress so we never
        // loop forever. With maxUnits >= 7 this branch is unreachable in practice.
        if pos = chunkStart && chunkStart < n then
            if len < maxUnits then
                sb.Append(text.[chunkStart]) |> ignore

            pos <- chunkStart + 1

        if fenceClosed && pos < n then
            resumeFence <- true

    List.ofSeq result
