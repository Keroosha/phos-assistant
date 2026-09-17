namespace Phos.Backup

open System

/// Runtime knobs controlling where and how a backup is produced. Bound by the
/// App from the `Backup` config section.
type BackupOptions =
    { Directory: string
      Interval: TimeSpan
      AgeRecipient: string
      RetainCount: int
      DatabasePath: string
      ProfileDir: string
      WorkspaceRoot: string
      OmpPath: string }
