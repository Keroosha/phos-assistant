namespace Phos.Storage

/// Factory helpers that wire concrete repository implementations to a
/// `StorageExecutor`.
module Repositories =

    let userRepository (exec: StorageExecutor) : IUserRepository = UserRepository(exec) :> IUserRepository

    let commandInbox (exec: StorageExecutor) : ICommandInbox = CommandInbox(exec) :> ICommandInbox

    let messageOutbox (exec: StorageExecutor) : IMessageOutbox = MessageOutbox(exec) :> IMessageOutbox

    let scheduleJobRepository (exec: StorageExecutor) : IScheduleJobRepository =
        ScheduleJobRepository(exec) :> IScheduleJobRepository
