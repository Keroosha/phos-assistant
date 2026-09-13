module Phos.Core.ToolPolicy

open Phos.Core.DomainTypes

/// Host-tool access policy.
///
/// `Tools` maps a host tool name to the minimum role that may invoke it
/// (Owner > Admin > User). A known tool is available to a role iff
/// `role >= minRole`. An unknown tool falls back to `DefaultMinRole`
/// (`None` = always denied).
type Policy =
    { DefaultMinRole: UserRole option
      Tools: Map<string, UserRole> }

/// Resolution result for a single tool invocation.
///
/// - `Allowed = true` + `MinRole = Some min` when the role is sufficient.
/// - `Allowed = false` + `MinRole = None` when denied.
type Decision =
    { Allowed: bool
      MinRole: UserRole option }

/// Numeric rank of a role in the Owner > Admin > User ordering.
let private rank (role: UserRole) : int =
    match role with
    | Owner -> 2
    | Admin -> 1
    | User -> 0

/// Whether `a` is at least as privileged as `b` (Owner > Admin > User).
let roleAtLeast (a: UserRole) (b: UserRole) : bool = rank a >= rank b

/// Resolves whether a user with `role` may invoke host tool `toolName`.
///
/// - Known tool: allowed iff `role >= minRole`.
/// - Unknown tool: falls back to `DefaultMinRole` (`None` -> always denied).
let resolve (policy: Policy) (role: UserRole) (toolName: string) : Decision =
    let minRole =
        match Map.tryFind toolName policy.Tools with
        | Some m -> Some m
        | None -> policy.DefaultMinRole

    match minRole with
    | Some m ->
        if roleAtLeast role m then
            { Allowed = true; MinRole = Some m }
        else
            { Allowed = false; MinRole = None }
    | None -> { Allowed = false; MinRole = None }
