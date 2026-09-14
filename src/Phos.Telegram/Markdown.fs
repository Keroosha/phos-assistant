namespace Phos.Telegram

open Phos.Core.Chunker

/// Pure markdown-to-entity parser for outgoing Telegram messages.
///
/// Parses in a single, non-nested, left-to-right pass with first-match-wins
/// semantics, producing non-overlapping entities. Offsets and lengths are
/// measured in UTF-16 code units (string indexing is used directly).
///
/// Supported markup:
///   - `**bold**`  -> Bold
///   - `*italic*`  -> Italic (a single `*`; two chars are inspected to
///     disambiguate bold from italic)
///   - `` `code` `` -> Code (single backticks)
///   - ``` fences (``` or ```lang ... ```) -> Pre spanning the WHOLE fenced
///     block, including the opening fence, language line, content and closing
///     fence (renders as one Telegram code block)
///   - `[text](url)` -> TextUrl carrying the destination URL
///
/// Links `[text](url)` are parsed into a `TextUrl` entity whose `Url` is the
/// destination. Links are not parsed inside code spans or fenced blocks (those
/// are consumed first). Unmatched markers are emitted as literal text with no
/// entity.
module Markdown =

    /// True when `p` begins a fence delimiter: exactly three backticks that are
    /// not part of a longer run (mirrors the Chunker's fence detection).
    let private isFenceDelim (text: string) (p: int) : bool =
        let n = text.Length

        p + 2 < n
        && text.[p] = '`'
        && text.[p + 1] = '`'
        && text.[p + 2] = '`'
        && (p = 0 || text.[p - 1] <> '`')
        && (p + 3 >= n || text.[p + 3] <> '`')

    /// Finds the next fence delimiter at or after `start`.
    [<TailCall>]
    let rec private findFenceDelim (text: string) (start: int) : int option =
        if start >= text.Length then None
        elif isFenceDelim text start then Some start
        else findFenceDelim text (start + 1)

    /// Finds the next single backtick at or after `start` that is not part of a
    /// fence delimiter.
    [<TailCall>]
    let rec private findBacktick (text: string) (start: int) : int option =
        if start >= text.Length then
            None
        elif text.[start] = '`' && not (isFenceDelim text start) then
            Some start
        else
            findBacktick text (start + 1)

    /// Finds the next single `*` (neither neighbor is a `*`) at or after `start`.
    [<TailCall>]
    let rec private findSingleStar (text: string) (start: int) : int option =
        if start >= text.Length then
            None
        elif text.[start] = '*' then
            let prevIsStar = start > 0 && text.[start - 1] = '*'
            let nextIsStar = start + 1 < text.Length && text.[start + 1] = '*'

            if prevIsStar || nextIsStar then
                findSingleStar text (start + 1)
            else
                Some start
        else
            findSingleStar text (start + 1)

    /// Parses `text` into a list of non-overlapping Telegram entities.
    let parse (text: string) : TelegramEntity list =
        let n = text.Length
        let entities = ResizeArray<TelegramEntity>()
        let mutable i = 0

        while i < n do
            let c = text.[i]

            if c = '`' && isFenceDelim text i then
                // Fenced code block: the Pre entity spans the whole block,
                // including the opening fence, language line, content and the
                // closing fence (renders as one Telegram code block).
                match findFenceDelim text (i + 3) with
                | Some close ->
                    entities.Add
                        { Offset = i
                          Length = close + 3 - i
                          Kind = Pre
                          Url = None }

                    i <- close + 3
                | None ->
                    // Unmatched fence opener: all three backticks are literal,
                    // no entity (avoid reinterpreting them as inline code).
                    i <- i + 3
            elif c = '`' then
                // Inline code span delimited by single backticks.
                match findBacktick text (i + 1) with
                | Some close ->
                    entities.Add
                        { Offset = i
                          Length = close + 1 - i
                          Kind = Code
                          Url = None }

                    i <- close + 1
                | None -> i <- i + 1
            elif c = '*' && i + 1 < n && text.[i + 1] = '*' then
                // Bold opener `**` — disambiguated from italic by checking the
                // second character before deciding bold vs italic.
                let close = text.IndexOf("**", i + 2)

                if close >= 0 then
                    entities.Add
                        { Offset = i
                          Length = close + 2 - i
                          Kind = Bold
                          Url = None }

                    i <- close + 2
                else
                    i <- i + 1
            elif c = '*' then
                // Italic opener `*` (a single star, not part of `**`).
                match findSingleStar text (i + 1) with
                | Some close ->
                    entities.Add
                        { Offset = i
                          Length = close + 1 - i
                          Kind = Italic
                          Url = None }

                    i <- close + 1
                | None -> i <- i + 1
            elif c = '[' then
                // Markdown link `[text](url)`. The `](` must be a single unit
                // (the `]` immediately preceding the `(`), so unrelated `]`
                // later in the text never swallows a preceding `[`.
                let labelClose = text.IndexOf(']', i + 1)

                if labelClose >= 0 && labelClose + 1 < n && text.[labelClose + 1] = '(' then
                    let urlStart = labelClose + 2
                    let close = text.IndexOf(')', urlStart)

                    if close >= 0 then
                        let url = text.Substring(urlStart, close - urlStart)

                        entities.Add
                            { Offset = i
                              Length = close + 1 - i
                              Kind = TextUrl
                              Url = Some url }

                        i <- close + 1
                    else
                        i <- i + 1
                else
                    i <- i + 1
            else
                i <- i + 1

        List.ofSeq entities
