namespace Phos.Backup

open System
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes
open FsToolkit.ErrorHandling

/// One file captured in a backup, with its content hash and size so a restore
/// can verify the archive was not corrupted or tampered with.
type ManifestFile =
    { Path: string
      Sha256: string
      Size: int64 }

/// The backup manifest, serialized to `manifest.json` at the staging root.
/// `Format` is the manifest schema version; `SchemaVersion` is the host
/// `VersionInfo` max at snapshot time.
type Manifest =
    { Format: int
      CreatedAt: DateTimeOffset
      AppVersion: string
      SchemaVersion: int64
      OmpVersion: string
      Files: ManifestFile list
      PayloadBytes: int64 }

/// Builds, serializes and verifies a backup manifest.
module Manifest =

    /// Computes the lowercase hex SHA-256 of a file.
    let sha256File (path: string) : Result<string, string> =
        try
            use stream = File.OpenRead path
            use sha = SHA256.Create()
            let hash = sha.ComputeHash stream
            Ok(Convert.ToHexString(hash).ToLowerInvariant())
        with ex ->
            Error ex.Message

    /// Serializes a manifest to JSON. Uses `System.Text.Json.Nodes` manually
    /// (repo convention: no `JsonSerializer` on F# records). `CreatedAt` is
    /// written as an ISO-8601 "O" round-trip string.
    let toJson (manifest: Manifest) : string =
        let files = JsonArray()

        for f in manifest.Files do
            let obj = JsonObject()
            obj["path"] <- f.Path
            obj["sha256"] <- f.Sha256
            obj["size"] <- f.Size
            files.Add(obj)

        let root = JsonObject()
        root["format"] <- manifest.Format
        root["createdAt"] <- manifest.CreatedAt.ToString("O")
        root["appVersion"] <- manifest.AppVersion
        root["schemaVersion"] <- manifest.SchemaVersion
        root["ompVersion"] <- manifest.OmpVersion
        root["files"] <- files
        root["payloadBytes"] <- manifest.PayloadBytes
        root.ToJsonString()

    /// Reads a required string property; a null value is a corrupt manifest.
    let private str (e: JsonElement) (name: string) : string =
        match e.GetProperty(name).GetString() with
        | null -> failwithf "manifest field %s is null" name
        | s -> s

    /// Parses a manifest from its JSON text.
    let ofJson (json: string) : Result<Manifest, string> =
        try
            use doc = JsonDocument.Parse json
            let root = doc.RootElement

            let files =
                [ for f in root.GetProperty("files").EnumerateArray() do
                      { Path = str f "path"
                        Sha256 = str f "sha256"
                        Size = f.GetProperty("size").GetInt64() } ]

            let createdAt =
                DateTimeOffset.Parse(str root "createdAt", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)

            Ok
                { Format = root.GetProperty("format").GetInt32()
                  CreatedAt = createdAt
                  AppVersion = str root "appVersion"
                  SchemaVersion = root.GetProperty("schemaVersion").GetInt64()
                  OmpVersion = str root "ompVersion"
                  Files = files
                  PayloadBytes = root.GetProperty("payloadBytes").GetInt64() }
        with ex ->
            Error ex.Message

    /// Verifies every file listed in the manifest against `rootDir`: each file
    /// must exist with the recorded size and SHA-256. Returns an `Error` naming
    /// the first mismatch (missing file, size mismatch, or hash mismatch).
    let verify (rootDir: string) (manifest: Manifest) : Result<unit, string> =
        let filePath (f: ManifestFile) =
            Path.Combine(rootDir, f.Path.Replace('/', Path.DirectorySeparatorChar))

        let rec check =
            function
            | [] -> Ok()
            | f :: rest ->
                let full = filePath f

                if not (File.Exists full) then
                    Error(sprintf "missing file: %s" f.Path)
                else
                    let info = FileInfo full

                    if info.Length <> f.Size then
                        Error(sprintf "size mismatch: %s (expected %d, got %d)" f.Path f.Size info.Length)
                    else
                        match sha256File full with
                        | Ok sha when sha = f.Sha256 -> check rest
                        | Ok _ -> Error(sprintf "sha256 mismatch: %s" f.Path)
                        | Error e -> Error e

        check manifest.Files
