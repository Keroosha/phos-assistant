module Phos.Tests.HarnessSmoke

open Xunit
open FsUnit.Xunit
open FsCheck
open FsCheck.Xunit

// Phase 0: smoke tests that validate the test toolchain (xunit + FsUnit + FsCheck)
// and the coverage pipeline. Replaced by product tests in Phase 1.

[<Fact>]
let ``FsUnit assertion works`` () = [ 1; 2; 3 ] |> should equal [ 1; 2; 3 ]

[<Property>]
let ``FsCheck reverse distributes over append`` (xs: int list) (ys: int list) =
    List.rev (xs @ ys) = List.rev ys @ List.rev xs
