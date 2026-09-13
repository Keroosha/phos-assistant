namespace Phos.Storage

open System
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Phos.Core.DomainTypes

/// A user record persisted in the `users` table.
type UserRecord =
    { Id: UserId
      Username: string option
      Role: UserRole
      WorkspacePath: string
      Timezone: string option }

/// Repository for the `users` table.
type IUserRepository =
    abstract Upsert: UserRecord -> Task<unit>
    abstract GetByTelegramId: UserId -> Task<UserRecord option>
    abstract List: unit -> Task<UserRecord list>

type UserRepository(exec: StorageExecutor) =
    let roleToString (role: UserRole) : string =
        match role with
        | Owner -> "owner"
        | Admin -> "admin"
        | User -> "user"

    let roleOfString (s: string) : UserRole =
        match s.ToLowerInvariant() with
        | "owner" -> Owner
        | "admin" -> Admin
        | _ -> User

    let userId (UserId id) = id

    let addOpt (cmd: SqliteCommand) (name: string) (v: string option) =
        cmd.Parameters.AddWithValue(
            name,
            match v with
            | Some s -> box s
            | None -> box DBNull.Value
        )
        |> ignore

    let readUser (reader: SqliteDataReader) : UserRecord =
        { Id = UserId(reader.GetInt64 0)
          Username = if reader.IsDBNull 1 then None else Some(reader.GetString 1)
          Role = roleOfString (reader.GetString 2)
          WorkspacePath = reader.GetString 3
          Timezone = if reader.IsDBNull 4 then None else Some(reader.GetString 4) }

    interface IUserRepository with
        member _.Upsert(user: UserRecord) =
            exec.WriteAsync(fun conn ->
                use cmd = conn.CreateCommand()

                cmd.CommandText <-
                    """
                    INSERT INTO users(user_id, username, role, workspace_path, timezone, created_at, updated_at)
                    VALUES ($userId, $username, $role, $workspacePath, $timezone, $now, $now)
                    ON CONFLICT(user_id) DO UPDATE SET
                        username = excluded.username,
                        role = excluded.role,
                        workspace_path = excluded.workspace_path,
                        timezone = excluded.timezone,
                        updated_at = excluded.updated_at;
                """

                cmd.Parameters.AddWithValue("$userId", userId user.Id) |> ignore
                addOpt cmd "$username" user.Username
                cmd.Parameters.AddWithValue("$role", roleToString user.Role) |> ignore
                cmd.Parameters.AddWithValue("$workspacePath", user.WorkspacePath) |> ignore
                addOpt cmd "$timezone" user.Timezone

                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                |> ignore

                cmd.ExecuteNonQuery() |> ignore
                ())

        member _.GetByTelegramId(id: UserId) =
            exec.ReadAsync(fun conn ->
                use cmd = conn.CreateCommand()

                cmd.CommandText <-
                    "SELECT user_id, username, role, workspace_path, timezone FROM users WHERE user_id = $userId;"

                cmd.Parameters.AddWithValue("$userId", userId id) |> ignore

                use reader = cmd.ExecuteReader()
                if reader.Read() then Some(readUser reader) else None)

        member _.List() =
            exec.ReadAsync(fun conn ->
                use cmd = conn.CreateCommand()

                cmd.CommandText <-
                    "SELECT user_id, username, role, workspace_path, timezone FROM users ORDER BY user_id;"

                use reader = cmd.ExecuteReader()
                let results = ResizeArray<UserRecord>()

                while reader.Read() do
                    results.Add(readUser reader)

                List.ofSeq results)
