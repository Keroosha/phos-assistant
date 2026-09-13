module Phos.Core.PromptBudget

/// Ordered prompt layers, in the order they appear in the assembled prompt.
type Layer =
    | BasePolicy
    | Persona
    | Skills
    | CoreMemory
    | Recall
    | State
    | CurrentJob

/// A layer together with its text content.
type LayerContent = { Layer: Layer; Text: string }

/// Token budget: total limit, per-layer limits and the truncation order.
///
/// Layers listed earlier in `TruncationOrder` are truncated first when the total
/// budget is exceeded.
type Budget =
    { TotalTokens: int
      PerLayer: Map<Layer, int>
      TruncationOrder: Layer list }

/// Deterministically truncates `text` to at most `maxTokens` tokens.
///
/// Uses `tokenCount` to measure prefixes and binary-searches the largest prefix
/// (in UTF-16 code units) whose token count does not exceed `maxTokens`.
let truncate (tokenCount: string -> int) (maxTokens: int) (text: string) : string =
    if maxTokens <= 0 then
        ""
    elif tokenCount text <= maxTokens then
        text
    else
        let mutable lo = 0
        let mutable hi = text.Length
        let mutable best = 0

        while lo <= hi do
            let mid = (lo + hi) / 2

            if tokenCount (text.Substring(0, mid)) <= maxTokens then
                best <- mid
                lo <- mid + 1
            else
                hi <- mid - 1

        text.Substring(0, best)

/// Builds the prompt from the given layers.
///
/// - Each layer is first truncated to its `PerLayer` cap.
/// - If the total then exceeds `TotalTokens`, layers are re-truncated in
///   `TruncationOrder` (earliest first) until the total fits.
/// - Layers absent from `contents` are skipped; the result keeps the order of `contents`.
let build (tokenCount: string -> int) (budget: Budget) (contents: LayerContent list) : (Layer * string) list =
    let capOf layer =
        Map.tryFind layer budget.PerLayer |> Option.defaultValue System.Int32.MaxValue

    let initial =
        contents
        |> List.map (fun c -> c.Layer, truncate tokenCount (capOf c.Layer) c.Text)

    let mutable total = initial |> List.sumBy (fun (_, t) -> tokenCount t)

    if total <= budget.TotalTokens then
        initial
    else
        let mutable table = Map.ofList initial
        let mutable over = total - budget.TotalTokens

        // Reduce layers in TruncationOrder first, then any remaining layer in content order.
        let reductionOrder =
            budget.TruncationOrder
            @ (contents
               |> List.map (fun c -> c.Layer)
               |> List.filter (fun l -> not (List.contains l budget.TruncationOrder)))

        for layer in reductionOrder do
            if over > 0 then
                match Map.tryFind layer table with
                | Some text ->
                    let current = tokenCount text

                    if current > 0 then
                        let target = max 0 (current - over)
                        let newText = truncate tokenCount target text
                        let newCount = tokenCount newText
                        over <- over - (current - newCount)
                        table <- Map.add layer newText table
                | None -> ()

        contents
        |> List.choose (fun c -> Map.tryFind c.Layer table |> Option.map (fun t -> c.Layer, t))
