module Phos.Core.ToolPolicy

open Phos.Core.DomainTypes

/// How a tool is treated by the policy.
type ToolPolicy =
    | Allow
    | Prompt
    | Deny

/// Tool policy: default behaviour, per-tool overrides and the set of admin-only tools.
type Policy =
    { Default: ToolPolicy
      Tools: Map<string, ToolPolicy>
      AdminTools: Set<string> }

/// Resolution result for a single tool invocation.
type Decision =
    { Allowed: bool
      NeedsConfirmation: bool }

/// Resolves whether a user with `role` may invoke `toolName`.
///
/// - A tool in `AdminTools` requires Owner/Admin; otherwise `Allowed=false`.
/// - `Deny` -> `Allowed=false`; `Prompt` -> `Allowed=true, NeedsConfirmation=true`;
///   `Allow` -> `Allowed=true`.
/// - Unknown tool -> the policy `Default`.
let resolve (policy: Policy) (role: UserRole) (toolName: string) : Decision =
    if Set.contains toolName policy.AdminTools && role <> Owner && role <> Admin then
        { Allowed = false
          NeedsConfirmation = false }
    else
        match Map.tryFind toolName policy.Tools with
        | Some Deny ->
            { Allowed = false
              NeedsConfirmation = false }
        | Some Prompt ->
            { Allowed = true
              NeedsConfirmation = true }
        | Some Allow ->
            { Allowed = true
              NeedsConfirmation = false }
        | None ->
            match policy.Default with
            | Deny ->
                { Allowed = false
                  NeedsConfirmation = false }
            | Prompt ->
                { Allowed = true
                  NeedsConfirmation = true }
            | Allow ->
                { Allowed = true
                  NeedsConfirmation = false }
