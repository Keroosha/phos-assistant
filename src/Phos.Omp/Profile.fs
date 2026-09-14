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

    /// True when the YAML text defines a `modelRoles` mapping — the owner's
    /// model selection (e.g. `default: vanbukin/DeepSeek-V4-Flash-Vision-Exp`).
    /// Without it OMP falls back to its own default provider/model (observed:
    /// `huggingface/DeepSeek-R1` → HTTP 400, no reply), so the source profile's
    /// `modelRoles` must always be adopted.
    let hasModelRoles (yml: string) : bool = yml.Contains("modelRoles:")

    /// Writes the target `config.yml`: the source profile's file when it
    /// carries `modelRoles`, otherwise the minimal template.
    let writeConfig (targetDir: string) (sourceDir: string) : unit =
        let src = Path.Combine(sourceDir, "config.yml")
        let target = Path.Combine(targetDir, "config.yml")

        if File.Exists src && hasModelRoles (File.ReadAllText src) then
            File.WriteAllText(target, File.ReadAllText src)
        else
            File.WriteAllText(target, ProfileManager.configTemplate)

    /// Minimal, stable profile settings template, used only when the source
    /// profile carries no `config.yml` (or one without `modelRoles`).
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

    /// Ensures the named profile exists, copying `models.yml`, `.env` and the
    /// source `config.yml` (when it carries `modelRoles`) the first time.
    /// Idempotent: an existing profile is repaired only when its `config.yml`
    /// lacks `modelRoles` while the source has them (profiles created before
    /// the modelRoles provisioning silently resolved the wrong model).
    member _.EnsureProfile(name: string, sourceProfile: string) : Result<unit, string> =
        let dir = agentDir name
        let sourceDir = agentDir sourceProfile
        let modelsSrc = Path.Combine(sourceDir, "models.yml")
        let envSrc = Path.Combine(sourceDir, ".env")

        try
            if Directory.Exists dir then
                // Existing profile: no source required (already provisioned).
                // Only repair a `config.yml` that lacks `modelRoles` (profiles
                // created before the modelRoles provisioning silently resolved
                // the wrong model) when the source has them.
                let targetConfig = Path.Combine(dir, "config.yml")

                if File.Exists targetConfig && not (hasModelRoles (File.ReadAllText targetConfig)) then
                    let src = Path.Combine(sourceDir, "config.yml")

                    if File.Exists src && hasModelRoles (File.ReadAllText src) then
                        File.WriteAllText(targetConfig, File.ReadAllText src)

                Ok()
            elif not (File.Exists modelsSrc && File.Exists envSrc) then
                Error(
                    sprintf
                        "source profile '%s' not found; set Omp:SourceProfile or create models.yml/.env manually"
                        sourceProfile
                )
            else
                Directory.CreateDirectory(dir) |> ignore
                writeConfig dir sourceDir
                File.Copy(modelsSrc, Path.Combine(dir, "models.yml"), false)
                File.Copy(envSrc, Path.Combine(dir, ".env"), false)
                Ok()
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
