// Phase 0 spike: multilingual-e5-small .NET (ONNX + Microsoft.ML.Tokenizers)
// vs Python/HuggingFace reference — cosine parity check.
//
// Reads vectors.json produced by reference.py; loads ONNX model + SentencePiece
// tokenizer; computes embeddings and reports per-string cosine and token parity.
// Throwaway: not part of Phos.sln, not run in CI.

open System
open System.Collections.Generic
open System.Collections.ObjectModel
open System.IO
open System.Text.Json
open Microsoft.ML.OnnxRuntime
open Microsoft.ML.OnnxRuntime.Tensors
open Microsoft.ML.Tokenizers

let dataDir = "data"
let onnxDir = "onnx_model"

let specialTokens: IReadOnlyDictionary<string, int> =
    ReadOnlyDictionary<string, int>(
        Dictionary<string, int>(dict [ "<s>", 0; "<pad>", 1; "</s>", 2; "<unk>", 3; "<mask>", 250001 ])
    )

let tokenizer =
    use stream = File.OpenRead(Path.Combine(dataDir, "sentencepiece.bpe.model"))
    SentencePieceTokenizer.Create(stream, true, true, specialTokens)

let session =
    let opts = new SessionOptions()
    opts.IntraOpNumThreads <- 4
    new InferenceSession(Path.Combine(onnxDir, "model.onnx"), opts)

// SentencePieceTokenizer emits raw SentencePiece ids; XLM-R/HuggingFace vocab
// remaps them: <s> raw 1 -> 0, <unk> raw 0 -> 3, <pad> is 1 (absent in raw model),
// </s> raw 2 -> 2, all pieces (raw >= 3) shift +1.
let remapToHf (rawId: int) =
    match rawId with
    | 1 -> 0
    | 0 -> 3
    | 2 -> 2
    | id -> id + 1

let encode (text: string) (prefix: string) =
    let ids =
        tokenizer.EncodeToIds(prefix + ": " + text, true, true)
        |> Seq.map remapToHf
        |> Seq.toArray
    // Pad to 512 with <pad> id 1; mask 0 for padding.
    let maxLen = 512
    let inputIds = Array.zeroCreate<int64> maxLen
    let mask = Array.zeroCreate<int64> maxLen

    for i in 0 .. ids.Length - 1 do
        inputIds[i] <- int64 ids[i]
        mask[i] <- 1L

    inputIds, mask, ids

let runModel (inputIds: int64[]) (mask: int64[]) : float32[] =
    let dims = ReadOnlySpan<int>([| 1; 512 |])
    let idsTensor = new DenseTensor<int64>(Memory<int64>(inputIds), dims, false)
    let maskTensor = new DenseTensor<int64>(Memory<int64>(mask), dims, false)

    let typeIds =
        new DenseTensor<int64>(Memory<int64>(Array.zeroCreate<int64> 512), dims, false)

    use input =
        session.Run(
            [ NamedOnnxValue.CreateFromTensor("input_ids", idsTensor)
              NamedOnnxValue.CreateFromTensor("attention_mask", maskTensor)
              NamedOnnxValue.CreateFromTensor("token_type_ids", typeIds) ]
        )

    let lastHidden =
        (input |> Seq.find (fun v -> v.Name = "last_hidden_state")).AsTensor<float32>()
    // Mean pooling over attention mask, then L2 normalize.
    let dim = 384
    let pooled = Array.zeroCreate<float32> dim
    let mutable count = 0.0f

    for t in 0..511 do
        if mask[t] = 1L then
            count <- count + 1.0f

            for d in 0 .. dim - 1 do
                pooled[d] <- pooled[d] + lastHidden[0, t, d]

    for d in 0 .. dim - 1 do
        pooled[d] <- pooled[d] / count

    let norm = sqrt (pooled |> Array.sumBy (fun x -> x * x))

    for d in 0 .. dim - 1 do
        pooled[d] <- pooled[d] / norm

    pooled

let cosine (a: float32[]) (b: float32[]) =
    let mutable dot = 0.0

    for i in 0 .. a.Length - 1 do
        dot <- dot + float a[i] * float b[i]

    dot

[<EntryPoint>]
let main _ =
    let vectors =
        use f = File.OpenRead("vectors.json")
        use doc = JsonDocument.Parse(f)

        doc.RootElement.EnumerateObject()
        |> Seq.map (fun prop ->
            let e = prop.Value

            let prefix =
                Option.ofObj (e.GetProperty("prefix").GetString()) |> Option.defaultValue ""

            let text =
                Option.ofObj (e.GetProperty("text").GetString()) |> Option.defaultValue ""

            let pyVec =
                e.GetProperty("vec").EnumerateArray()
                |> Seq.map (fun x -> float32 (x.GetDouble()))
                |> Seq.toArray

            let pyIds =
                e.GetProperty("ids_unpadded").EnumerateArray()
                |> Seq.map (fun x -> int (x.GetInt32()))
                |> Seq.toArray

            let onnxPyVec =
                match e.TryGetProperty("onnx_python_vec") with
                | true, v -> v.EnumerateArray() |> Seq.map (fun x -> float32 (x.GetDouble())) |> Seq.toArray
                | _ -> [||]

            (prefix, text, pyVec, pyIds, onnxPyVec))
        |> Seq.toList

    let mutable worst = 1.0
    let mutable worstOnnx = 1.0
    let mutable tokenParityOk = 0
    let mutable total = 0

    for (prefix, text, pyVec, pyIds, onnxPyVec) in vectors do
        let inputIds, mask, ids = encode text prefix
        let dotnetVec = runModel inputIds mask
        let cos = cosine dotnetVec pyVec

        let cosOnnx =
            if onnxPyVec.Length = 384 then
                cosine dotnetVec onnxPyVec
            else
                -1.0

        let idsMatch = (ids |> Array.map int) = pyIds

        if idsMatch then
            tokenParityOk <- tokenParityOk + 1

        total <- total + 1
        worst <- min worst cos

        if cosOnnx >= 0.0 then
            worstOnnx <- min worstOnnx cosOnnx

        printfn
            "%-8s %-45s cos=%f (onnx=%f) tokens_match=%b ids=%d"
            prefix
            (if text.Length > 45 then
                 text.Substring(0, 42) + "..."
             else
                 text)
            cos
            cosOnnx
            idsMatch
            (ids.Length)

    printfn ""
    printfn "total: %d, token parity: %d/%d" total tokenParityOk total
    printfn "min cosine (dotnet vs python torch): %.6f" worst
    printfn "min cosine (dotnet vs python-onnx): %.6f" worstOnnx
    0
