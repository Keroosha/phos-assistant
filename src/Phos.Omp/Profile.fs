namespace Phos.Omp

open System
open System.IO

/// Provisions the single shared OMP profile (`phos`) that every user session
/// runs under. The profile's `agent/` directory holds the settings, provider
/// credentials and model definitions; per-user isolation is achieved through
/// workspaces, not profiles.
type ProfileManager(ompRoot: string) =

    let agentDir (name: string) : string =
        Path.Combine(ompRoot, "profiles", name, "agent")

    /// Minimal, stable profile settings template. `modelRoles` is intentionally
    /// absent — model resolution falls back to global defaults / `--model`, so
    /// the integration wave can pass `--model` without touching this file.
    static member configTemplate: string =
        """
symbolPreset: unicode
memory:
  backend: mnemopi
autolearn:
  enabled: false
astGrep:
  enabled: false
security:
  enabled: false
"""

    /// Ensures the named profile exists, copying `models.yml` and `.env` from a
    /// source profile the first time. Idempotent: an existing profile is left
    /// untouched. Returns an `Error` (without creating a half-configured
    /// profile) when the source profile is missing the required files.
    member _.EnsureProfile(name: string, sourceProfile: string) : Result<unit, string> =
        let dir = agentDir name

        try
            if Directory.Exists dir then
                Ok()
            else
                Directory.CreateDirectory(dir) |> ignore

                let sourceDir = agentDir sourceProfile
                let modelsSrc = Path.Combine(sourceDir, "models.yml")
                let envSrc = Path.Combine(sourceDir, ".env")

                if File.Exists modelsSrc && File.Exists envSrc then
                    File.WriteAllText(Path.Combine(dir, "config.yml"), ProfileManager.configTemplate)
                    File.Copy(modelsSrc, Path.Combine(dir, "models.yml"), false)
                    File.Copy(envSrc, Path.Combine(dir, ".env"), false)
                    Ok()
                else
                    Error(
                        sprintf
                            "source profile '%s' not found; set Omp:SourceProfile or create models.yml/.env manually"
                            sourceProfile
                    )
        with ex ->
            Error ex.Message

    /// Test helper that builds an isolated profile from explicit content,
    /// letting integration tests create fake profiles without a real source.
    member _.EnsureProfileWith(name: string, modelsYml: string, configYml: string) : Result<unit, string> =
        let dir = agentDir name

        try
            Directory.CreateDirectory(dir) |> ignore
            File.WriteAllText(Path.Combine(dir, "config.yml"), configYml)
            File.WriteAllText(Path.Combine(dir, "models.yml"), modelsYml)
            Ok()
        with ex ->
            Error ex.Message
