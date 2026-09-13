module Phos.Core.Whitelist

open Phos.Core.DomainTypes

/// Whitelist of users and explicitly allowed group/channel chats.
type Whitelist =
    { Users: Map<UserId, UserRole>
      AllowedChats: Set<ChatId> }

/// Why a request was denied.
type DenialReason =
    | NotWhitelisted
    | ChatNotAllowed

/// Authorization decision for a user in a chat.
type Decision =
    | Allow of UserRole
    | Deny of DenialReason

/// Creates a whitelist from the user roles and explicitly allowed chats.
let create (users: Map<UserId, UserRole>) (allowedChats: Set<ChatId>) : Whitelist =
    { Users = users
      AllowedChats = allowedChats }

/// Authorizes a user to act in a chat.
///
/// - Private chats: only whitelisted users (their role); otherwise `Deny NotWhitelisted`.
/// - Group/Channel: only if the chat id is in `AllowedChats`; the role is taken from
///   `Users` if present, otherwise the fallback role `User`. Otherwise `Deny ChatNotAllowed`.
let authorize (whitelist: Whitelist) (user: User) (chat: Chat) : Decision =
    match chat.Kind with
    | Private ->
        match Map.tryFind user.Id whitelist.Users with
        | Some role -> Allow role
        | None -> Deny NotWhitelisted
    | Group
    | Channel ->
        if Set.contains chat.Id whitelist.AllowedChats then
            let role = Map.tryFind user.Id whitelist.Users |> Option.defaultValue User
            Allow role
        else
            Deny ChatNotAllowed
