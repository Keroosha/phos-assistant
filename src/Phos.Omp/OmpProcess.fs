namespace Phos.Omp

open System
open System.Diagnostics
open System.Text.Json.Nodes
open System.Threading.Tasks
open Microsoft.Extensions.Logging

/// Arguments used to spawn an `omp --mode rpc` child proc.
type OmpProcessOptions =
    {
        OmpPath: string
        Profile: string
        WorkspaceDir: string
        SessionResume: string option
        Tools: string
        ApprovalMode: string
        /// OMP-side operational wall-clock ceiling passed as `--max-time`. This
        /// is an explicit operator ceiling only — never a Phos turn-failure
        /// mechanism. Empty/whitespace omits the flag so a healthy long turn
        /// always runs to its terminal `agent_end`.
        MaxTime: string
        ExtraFlags: string list
        ReadyTimeoutSeconds: int
    }

module private OmpProcessUtil =
    let killQuiet (p: Process) : unit =
        try
            if not p.HasExited then
                p.Kill(entireProcessTree = true)
        with _ ->
            ()

    /// Kills the proc (if needed) and drains whatever stderr is buffered, so
    /// a failed startup reports a useful tail.
    let readStderrTail (p: Process) : string =
        try
            killQuiet p

            try
                p.WaitForExit(2000) |> ignore
            with _ ->
                ()

            try
                p.StandardError.ReadToEnd()
            with _ ->
                ""
        with _ ->
            ""

    /// Formats a stderr tail for embedding into an error message.
    let stderrSuffix (tail: string) : string =
        if String.IsNullOrWhiteSpace tail then
            ""
        else
            ": " + tail.Trim()

/// Process abstraction so `SessionManager` can be tested with a fake.
type IOmpProcess =
    abstract Process: Process
    abstract ReadyFrame: JsonObject
    abstract Exited: IEvent<unit>
    abstract IsDead: bool
    abstract Kill: unit -> unit

/// A spawned `omp --mode rpc` proc, including its stdout ready frame, a
/// stderr logger and an exit signal. `SessionManager` owns the lifecycle.
type OmpProcess private (proc: Process, readyFrame: JsonObject, logger: ILogger) =
    let exited = Event<unit>()
    let mutable isDead = false

    do
        let stderrLoop () : Task =
            task {
                let mutable running = true

                while running && not proc.HasExited do
                    let! line = proc.StandardError.ReadLineAsync()

                    if isNull line then
                        running <- false
                    else
                        logger.LogWarning("omp stderr: {Line}", line)
            }

        let exitLoop () : Task =
            task {
                do! proc.WaitForExitAsync(System.Threading.CancellationToken.None)
                isDead <- true
                exited.Trigger()
            }

        Task.Run(Func<Task>(fun () -> stderrLoop ())) |> ignore
        Task.Run(Func<Task>(fun () -> exitLoop ())) |> ignore

    /// The underlying `System.Diagnostics.Process`.
    member _.Process = proc

    /// The protocol `ready` frame captured at startup (used for v2 negotiation).
    member _.ReadyFrame = readyFrame

    /// True once the proc has exited.
    member _.IsDead = isDead

    /// Fired when the proc exits.
    member _.Exited = exited.Publish

    /// Graceful best-effort kill of the whole proc tree.
    member _.Kill() = OmpProcessUtil.killQuiet proc

    interface IOmpProcess with
        member _.Process = proc
        member _.ReadyFrame = readyFrame
        member _.Exited = exited.Publish
        member _.IsDead = isDead
        member _.Kill() = OmpProcessUtil.killQuiet proc

    /// Spawns `omp --mode rpc`, waits for the `ready` frame (bounded by
    /// `ReadyTimeoutSeconds`), and returns a running `OmpProcess`. On timeout,
    /// premature exit or a malformed ready frame it returns an `Error` carrying
    /// the stderr tail.
    static member Start(options: OmpProcessOptions, logger: ILogger) : Result<OmpProcess, string> =
        try
            let psi = ProcessStartInfo()
            psi.FileName <- options.OmpPath
            psi.WorkingDirectory <- options.WorkspaceDir
            psi.RedirectStandardInput <- true
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.UseShellExecute <- false

            // `--max-time` is an OMP-side operational wall-clock ceiling only —
            // it is NOT part of Phos's failure/retry logic, which relies on the
            // terminal `agent_end`. An empty `MaxTime` omits the flag entirely
            // so a healthy, genuinely long turn is never killed prematurely.
            let timedArgs =
                if String.IsNullOrWhiteSpace options.MaxTime then
                    []
                else
                    [ "--max-time"; options.MaxTime ]

            let args =
                [ "--mode"
                  "rpc"
                  "--profile"
                  options.Profile
                  "--cwd"
                  options.WorkspaceDir
                  "--tools"
                  options.Tools
                  "--approval-mode"
                  options.ApprovalMode ]
                @ timedArgs
                @ [ "--no-lsp"; "--no-pty" ]

            for a in args do
                psi.ArgumentList.Add(a) |> ignore

            for f in options.ExtraFlags do
                psi.ArgumentList.Add(f) |> ignore

            options.SessionResume
            |> Option.iter (fun s ->
                psi.ArgumentList.Add("--resume") |> ignore
                psi.ArgumentList.Add(s) |> ignore)

            let p = new Process()
            p.StartInfo <- psi

            if not (p.Start()) then
                Error "failed to start omp proc"
            else
                let readyTask = p.StandardOutput.ReadLineAsync()
                let readyInTime = readyTask.Wait(options.ReadyTimeoutSeconds * 1000)

                if not readyInTime then
                    let tail = OmpProcessUtil.readStderrTail p

                    Error(
                        sprintf
                            "omp did not become ready within %ds%s"
                            options.ReadyTimeoutSeconds
                            (OmpProcessUtil.stderrSuffix tail)
                    )
                else

                    match readyTask.Result with
                    | null ->
                        let tail = OmpProcessUtil.readStderrTail p
                        Error(sprintf "omp exited before ready%s" (OmpProcessUtil.stderrSuffix tail))
                    | line ->
                        match RpcProtocol.tryParseFrame line with
                        | Ok frame when RpcProtocol.classify frame = FrameKind.Ready -> Ok(OmpProcess(p, frame, logger))
                        | Ok frame ->
                            let tail = OmpProcessUtil.readStderrTail p

                            Error(
                                sprintf
                                    "expected ready frame, got: %s%s"
                                    (frame.ToJsonString())
                                    (OmpProcessUtil.stderrSuffix tail)
                            )
                        | Error e ->
                            let tail = OmpProcessUtil.readStderrTail p
                            Error(sprintf "%s%s" e (OmpProcessUtil.stderrSuffix tail))
        with ex ->
            Error ex.Message
