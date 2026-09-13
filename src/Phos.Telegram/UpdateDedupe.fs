namespace Phos.Telegram

open System.Collections.Generic

/// Bounded LRU deduplicator keyed by Telegram update id.
///
/// After a restart the in-memory cache is empty; Telegram's `pts`/update
/// offset mechanism re-delivers only genuinely-new updates, so the bounded LRU
/// only needs to collapse duplicates within a single process lifetime (e.g. a
/// handler race or a reconnect re-delivering the last batch). The default
/// capacity (1000) far exceeds the number of updates a bot sees in a short
/// window, so eviction only removes very old, already-processed ids.
type UpdateDedupe(capacity: int) =
    let capacity = if capacity < 1 then 1000 else capacity
    let cache = Dictionary<int64, LinkedListNode<int64>>()
    let order = LinkedList<int64>()
    let gate = obj ()

    /// Records `updateId` as seen. Returns `true` the first time it is seen and
    /// `false` when it is a duplicate.
    member _.TryAdd(updateId: int64) : bool =
        lock gate (fun () ->
            // Unchecked.defaultof: F# out-param placeholder for Dictionary.TryGetValue;
            // the value is only read when TryGetValue returns true (non-null).
            let mutable node = Unchecked.defaultof<LinkedListNode<int64>>

            if cache.TryGetValue(updateId, &node) then
                // Re-insert at the front (most recently used).
                order.Remove node |> ignore
                order.AddFirst node |> ignore
                false
            else
                if cache.Count >= capacity then
                    // Evict the least recently used (tail). `capacity >= 1` and
                    // `cache.Count >= capacity` guarantee the list is non-empty,
                    // but the BCL annotation makes the node nullable.
                    match order.Last with
                    | null -> ()
                    | last ->
                        order.RemoveLast() |> ignore
                        cache.Remove last.Value |> ignore

                let newNode = order.AddFirst updateId
                cache.[updateId] <- newNode
                true)

    /// Number of update ids currently tracked.
    member _.Count = cache.Count
