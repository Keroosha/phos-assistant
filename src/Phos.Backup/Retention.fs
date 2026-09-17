namespace Phos.Backup

open System.IO

/// Local retention policy: keeps the newest `keep` `phos-backup-*.age` archives
/// in a directory and deletes the rest.
module Retention =

    /// Deletes `phos-backup-*.age` archives in `directory` beyond the newest
    /// `keep` (sorted by name descending; archive names embed the timestamp),
    /// returning the number of deleted archives. Other files are untouched. A
    /// missing directory is a no-op returning 0.
    let prune (directory: string) (keep: int) : Result<int, string> =
        try
            if not (Directory.Exists directory) then
                Ok 0
            else
                let archives =
                    Directory.EnumerateFiles(directory, "phos-backup-*.age")
                    |> Seq.sortDescending
                    |> Seq.toList

                let toDelete =
                    if archives.Length <= keep then
                        []
                    else
                        archives |> List.skip keep

                for f in toDelete do
                    File.Delete f

                Ok toDelete.Length
        with ex ->
            Error ex.Message
