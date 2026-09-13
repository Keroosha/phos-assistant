module Phos.Core.DomainTypes

/// Stable identifier of a Telegram user (immutable numeric id).
type UserId = UserId of int64

/// Stable identifier of a chat (private chat, group or channel).
type ChatId = ChatId of int64

/// Kind of chat the bot is talking to.
type ChatKind =
    | Private
    | Group
    | Channel

/// Role of a user in the whitelist.
type UserRole =
    | Owner
    | Admin
    | User

/// Origin of a command (how it entered the system).
type Origin =
    | Telegram
    | Schedule
    | System

/// A chat the bot operates in.
type Chat = { Id: ChatId; Kind: ChatKind }

/// A Telegram user known to the bot.
type User =
    { Id: UserId
      Username: string option
      Role: UserRole }
