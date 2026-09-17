namespace Phos.Backup

open System
open System.Diagnostics
open System.Formats.Tar
open System.IO
open System.IO.Compression

/// Packs the staging tree into a gzipped tar archive and encrypts it with the
/// `age` CLI.
module Archive =

    /// Runs a process with an argument list, returning `(stdout, stderr)` on
    /// success. On a non-zero exit or timeout returns an `Error` with a short
    /// stderr tail. No shell is involved: `ArgumentList` quotes arguments
    /// directly, so paths are never interpolated.
    let private runProcess (exe: string) (args: string list) (timeoutSeconds: int) : Result<string * string, string> =
        let psi = ProcessStartInfo(exe)
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true

        for a in args do
            psi.ArgumentList.Add a

        try
            use proc = new Process()
            proc.StartInfo <- psi

            if not (proc.Start()) then
                Error(sprintf "failed to start %s" exe)
            else
                let stdoutTask = proc.StandardOutput.ReadToEndAsync()
                let stderrTask = proc.StandardError.ReadToEndAsync()

                if not (proc.WaitForExit(timeoutSeconds * 1000)) then
                    try
                        proc.Kill(entireProcessTree = true)
                    with _ ->
                        ()

                    Error(sprintf "%s timed out after %d s" exe timeoutSeconds)
                elif proc.ExitCode <> 0 then
                    let err = stderrTask.Result

                    let tail =
                        if String.IsNullOrWhiteSpace err then
                            ""
                        else
                            err.Trim().Substring(0, min 400 (err.Trim().Length))

                    Error(sprintf "%s exited %d: %s" exe proc.ExitCode tail)
                else
                    Ok(stdoutTask.Result, stderrTask.Result)
        with ex ->
            Error ex.Message

    /// Packs every file under `stagingDir` into `tarPath` as a gzipped tar
    /// archive (PAX). One entry per file, named by its path relative to
    /// `stagingDir` with forward slashes; no directory entries are emitted.
    let packTarGz (stagingDir: string) (tarPath: string) : Result<unit, string> =
        try
            match Path.GetDirectoryName tarPath with
            | null
            | "" -> ()
            | dir -> Directory.CreateDirectory dir |> ignore

            let files =
                Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories)
                |> Seq.sort

            use fs = File.Create tarPath
            use gz = new GZipStream(fs, CompressionLevel.Fastest)
            use tar = new TarWriter(gz)

            for file in files do
                let rel =
                    Path.GetRelativePath(stagingDir, file).Replace(Path.DirectorySeparatorChar, '/')

                tar.WriteEntry(file, rel)

            Ok()
        with ex ->
            Error ex.Message

    /// Encrypts `tarPath` with `age -R <recipientFile> -o <agePath> <tarPath>`.
    /// A missing recipient file is an `Error`; a non-zero exit is an `Error`
    /// with a short stderr tail; the output file is removed on failure.
    let encryptAge (tarPath: string) (agePath: string) (recipientFile: string) : Result<unit, string> =
        if not (File.Exists recipientFile) then
            Error(sprintf "age recipient file does not exist: %s" recipientFile)
        else
            if File.Exists agePath then
                File.Delete agePath

            match runProcess "age" [ "-R"; recipientFile; "-o"; agePath; tarPath ] 60 with
            | Ok _ -> Ok()
            | Error e ->
                if File.Exists agePath then
                    try
                        File.Delete agePath
                    with _ ->
                        ()

                Error e
