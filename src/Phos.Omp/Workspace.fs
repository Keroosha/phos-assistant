namespace Phos.Omp

open System
open System.IO
open Phos.Core.DomainTypes

/// Manages the per-user workspace directories. Each user gets an isolated
/// `cwd` (workspace) so the single shared OMP profile still yields per-user
/// sessions, memory banks (`mnemopi` per-project) and persona (`APPEND_SYSTEM.md`).
type WorkspaceManager(root: string, ?personaFile: string) =

    let defaultPersona =
        "You are phos, a helpful assistant embedded in Telegram.\nBe concise, direct and friendly.\n\nWhen the user asks for a reminder, a recurring task or a loop (e.g. \"напоминай мне каждый день в 09:00\", \"проверяй деплой каждые 5 минут\"):\n- Use schedule_add (cron_expr or interval_seconds, timezone, prompt) — it creates a PENDING job.\n- ALWAYS tell the user the next 5 occurrences from the tool result and ask for explicit confirmation.\n- Call schedule_confirm ONLY after the user explicitly confirms; schedule_cancel if they decline.\n- Stop or disable a loop with schedule_pause / schedule_remove when the user asks.\n- Schedules survive restarts — a loop keeps running until the user disables it.\n- You cannot activate a schedule from a scheduled run: only a human message can confirm it.\n- One-shot delayed tasks (\"write to me in 5 minutes\") use schedule_add with after_seconds: fires once and completes itself.\n"

    let pathFor (UserId id) : string = Path.Combine(root, string id)

    /// Absolute workspace path for a user.
    member _.PathFor(userId: UserId) : string = pathFor userId

    /// Ensures the workspace directory and its `.omp/APPEND_SYSTEM.md` persona
    /// file exist. The persona is copied from `personaFile` when configured and
    /// present, otherwise the built-in default is written. An existing persona
    /// file is never overwritten.
    member _.Ensure(userId: UserId) : Result<string, string> =
        try
            let dir = pathFor userId
            Directory.CreateDirectory(dir) |> ignore
            let ompDir = Path.Combine(dir, ".omp")
            Directory.CreateDirectory(ompDir) |> ignore
            let appendPath = Path.Combine(ompDir, "APPEND_SYSTEM.md")

            if not (File.Exists appendPath) then
                match personaFile with
                | Some pf when File.Exists pf -> File.Copy(pf, appendPath, false)
                | _ -> File.WriteAllText(appendPath, defaultPersona)

            Ok dir
        with ex ->
            Error ex.Message

    /// Deletes a user's workspace (for cleanup / user removal).
    member _.Remove(userId: UserId) : unit =
        try
            let dir = pathFor userId

            if Directory.Exists dir then
                Directory.Delete(dir, true)
        with _ ->
            ()
