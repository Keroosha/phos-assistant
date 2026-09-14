module Phos.Tests.MarkdownTests

open Xunit
open FsUnit.Xunit
open Phos.Telegram
open Phos.Core.Chunker

let private entity (offset: int) (length: int) (kind: EntityKind) : TelegramEntity =
    { Offset = offset
      Length = length
      Kind = kind }

[<Fact>]
let ``bold parses to a single Bold entity`` () =
    Markdown.parse "**bold**" |> should equal [ entity 0 8 Bold ]

[<Fact>]
let ``italic parses to a single Italic entity`` () =
    Markdown.parse "*italic*" |> should equal [ entity 0 8 Italic ]

[<Fact>]
let ``code parses to a single Code entity`` () =
    Markdown.parse "`code`" |> should equal [ entity 0 6 Code ]

[<Fact>]
let ``fenced pre parses to a single Pre entity covering the whole block`` () =
    Markdown.parse "```\ncode\n```" |> should equal [ entity 0 12 Pre ]

[<Fact>]
let ``fenced pre with language covers markers and language line`` () =
    Markdown.parse "```fsharp\nlet x = 1\n```" |> should equal [ entity 0 23 Pre ]

[<Fact>]
let ``mixed text emits entities at exact UTF-16 offsets`` () =
    Markdown.parse "Hello **bold** and *italic*"
    |> should equal [ entity 6 8 Bold; entity 19 8 Italic ]

[<Fact>]
let ``unmatched markers are literal with no entity`` () =
    Markdown.parse "**unclosed" |> should be Empty

[<Fact>]
let ``bold disambiguated from italic by checking two chars`` () =
    Markdown.parse "**bold** vs *italic*"
    |> should equal [ entity 0 8 Bold; entity 12 8 Italic ]
