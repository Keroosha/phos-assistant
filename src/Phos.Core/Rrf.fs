module Phos.Core.Rrf

/// Reciprocal Rank Fusion score.
///
/// `score(t) = sum over lists of 1 / (k + rank)` where `rank` is the 1-based
/// position of `t` in a list. Adding a list never decreases any score, and an
/// earlier rank contributes at least as much as a later one.
let score (k: float) (ranks: seq<seq<'T>>) : Map<'T, float> =
    ranks
    |> Seq.fold
        (fun acc list ->
            list
            |> Seq.mapi (fun i item -> item, 1.0 / (k + float (i + 1)))
            |> Seq.fold
                (fun acc' (item, s) -> Map.change item (fun cur -> Some(Option.defaultValue 0.0 cur + s)) acc')
                acc)
        Map.empty
