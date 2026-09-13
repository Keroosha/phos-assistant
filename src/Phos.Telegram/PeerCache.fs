namespace Phos.Telegram

open System.Collections.Concurrent
open Phos.Core.DomainTypes

/// Thread-safe cache of Telegram `InputPeer` values (with access hashes),
/// keyed by `ChatId`. Access hashes are populated from updates; for a brand-new
/// peer not seen in an update the transport must refetch it. No secrets are
/// stored here — access hashes are public identifiers, not credentials.
type PeerCache() =
    let peers = ConcurrentDictionary<int64, TL.InputPeer>()

    /// Returns the cached `InputPeer` for a chat, if known.
    member _.Get(ChatId id) =
        match peers.TryGetValue id with
        | true, peer -> Some peer
        | _ -> None

    /// Caches an `InputPeer` for a chat id.
    member _.Cache(ChatId id, peer: TL.InputPeer) = peers.[id] <- peer

    /// Caches a private chat peer (a user) from its access hash.
    member _.CacheUser(userId: int64, accessHash: int64) =
        peers.[userId] <- TL.InputPeerUser(userId, accessHash) :> TL.InputPeer

    /// Caches a channel peer from its access hash.
    member _.CacheChannel(channelId: int64, accessHash: int64) =
        peers.[channelId] <- TL.InputPeerChannel(channelId, accessHash) :> TL.InputPeer

    /// Caches a basic group peer (no access hash needed).
    member _.CacheChat(chatId: int64) =
        peers.[chatId] <- TL.InputPeerChat(chatId) :> TL.InputPeer
