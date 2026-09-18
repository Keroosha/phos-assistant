namespace Phos.Omp

open System
open System.IO
open Phos.Core.DomainTypes

/// Manages the per-user workspace directories. Each user gets an isolated
/// `cwd` (workspace) so the single shared OMP profile still yields per-user
/// sessions, memory banks (`mnemopi` per-project) and persona (`APPEND_SYSTEM.md`).
type WorkspaceManager(root: string, ?personaFile: string) =

    let defaultPersona =
        "You are phos, a helpful assistant embedded in Telegram.\nBe concise, direct and friendly.\n\nWhen the user asks for a reminder, a recurring task or a loop (e.g. \"напоминай мне каждый день в 09:00\", \"проверяй деплой каждые 5 минут\"):\n- Use schedule_add with cron_expr or interval_seconds for recurring schedules, plus timezone and prompt — it creates a PENDING job.\n- For a one-time calendar date use run_at (ISO-8601 date and exact time), never cron because cron repeats.\n- If the user gives only a date, ask for the exact time; never guess it.\n- If timezone is omitted, it means the host process timezone. Always show the resolved occurrence (UTC and local time) and ask for explicit confirmation.\n- ALWAYS tell the user the next 5 occurrences for recurring schedules from the tool result and ask for explicit confirmation.\n- Call schedule_confirm ONLY after the user explicitly confirms; schedule_cancel if they decline.\n- Stop or disable a loop with schedule_pause / schedule_remove when the user asks.\n- Schedules survive restarts — a loop keeps running until the user disables it.\n- You cannot activate a schedule from a scheduled run: only a human message can confirm it.\n- One-shot delayed tasks (\"write to me in 5 minutes\") use schedule_add with after_seconds: fires once and completes itself.\n"

    let schedulingBlockStart = "<!-- PHOS_HOST_SCHEDULING_START -->"
    let schedulingBlockEnd = "<!-- PHOS_HOST_SCHEDULING_END -->"

    let schedulingBlock =
        String.concat
            "\n"
            [ schedulingBlockStart
              "## Host-managed scheduling instructions"
              "These instructions supersede any earlier scheduling instructions in this file."
              "- Use `schedule_add` with `cron_expr` or `interval_seconds` for recurring schedules."
              "- Use `run_at` for a one-time calendar occurrence; never use cron for a one-time date because cron repeats."
              "- `run_at` must contain an exact ISO-8601 date and time. If the user gives only a date, ask for the exact time and never guess."
              "- If `timezone` is omitted, use the host process timezone."
              "- Always show the resolved occurrence (UTC and local time); for recurring schedules show the next 5 occurrences."
              "- Keep the job pending and ask for explicit confirmation. Call `schedule_confirm` only after the user explicitly confirms; otherwise use `schedule_cancel`."
              schedulingBlockEnd ]

    let upsertSchedulingBlock (content: string) =
        let start = content.IndexOf(schedulingBlockStart, StringComparison.Ordinal)

        let withoutExisting =
            if start < 0 then
                content
            else
                let endIndex =
                    content.IndexOf(schedulingBlockEnd, start + schedulingBlockStart.Length, StringComparison.Ordinal)

                if endIndex < 0 then
                    content
                else
                    let before = content.Substring(0, start).TrimEnd([| '\r'; '\n' |])
                    let after =
                        content.Substring(endIndex + schedulingBlockEnd.Length).TrimStart([| '\r'; '\n' |])

                    if String.IsNullOrEmpty before then
                        after
                    elif String.IsNullOrEmpty after then
                        before
                    else
                        before + "\n\n" + after

        let baseContent = withoutExisting.TrimEnd([| '\r'; '\n' |])

        if String.IsNullOrEmpty baseContent then
            schedulingBlock
        else
            baseContent + "\n\n" + schedulingBlock

    let pathFor (UserId id) : string = Path.Combine(root, string id)

    /// Absolute workspace path for a user.
    member _.PathFor(userId: UserId) : string = pathFor userId

    /// Ensures the workspace directory and its `.omp/APPEND_SYSTEM.md` persona
    /// file exist. The persona source is copied when configured, then the
    /// host-managed scheduling block is upserted on every call without
    /// overwriting custom/user persona content.
    member _.Ensure(userId: UserId) : Result<string, string> =
        try
            let dir = pathFor userId
            Directory.CreateDirectory(dir) |> ignore
            let ompDir = Path.Combine(dir, ".omp")
            Directory.CreateDirectory(ompDir) |> ignore
            let appendPath = Path.Combine(ompDir, "APPEND_SYSTEM.md")
            let existed = File.Exists appendPath

            let original =
                if existed then
                    File.ReadAllText appendPath
                else
                    match personaFile with
                    | Some pf when File.Exists pf -> File.ReadAllText pf
                    | _ -> defaultPersona

            let updated = upsertSchedulingBlock original

            if not existed || not (String.Equals(original, updated, StringComparison.Ordinal)) then
                File.WriteAllText(appendPath, updated)

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
