using Tommy.Core.Configuration;
using Tommy.Core.Engine;

namespace Tommy.Persistence.Setup;

/// <summary>Outcome of validating first-run setup input.</summary>
public readonly record struct ValidationResult(bool Ok, string? Error)
{
    public static ValidationResult Success { get; } = new(true, null);
    public static ValidationResult Fail(string error) => new(false, error);
}

/// <summary>
/// Validates engine-profile input before saving, so setup fails loudly and
/// early: a remote profile needs a valid http(s) URL; a managed profile needs an
/// install directory and run script that actually exist on disk.
/// </summary>
public static class FirstRunValidator
{
    public static ValidationResult Validate(EngineProfile profile) => profile.Mode switch
    {
        EngineMode.Remote => ValidateRemote(profile.BaseUrl),
        EngineMode.Managed => ValidateManaged(profile.InstallDir, profile.RunScript, profile.BaseUrl),
        _ => ValidationResult.Fail("Unknown engine mode.")
    };

    public static ValidationResult ValidateRemote(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return ValidationResult.Fail("Base URL is required.");

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return ValidationResult.Fail("Base URL must be an absolute http(s) URL.");

        return ValidationResult.Success;
    }

    public static ValidationResult ValidateManaged(string? installDir, string? runScript, string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(installDir))
            return ValidationResult.Fail("Install directory is required.");
        if (!Directory.Exists(installDir))
            return ValidationResult.Fail($"Install directory not found: {installDir}");
        if (string.IsNullOrWhiteSpace(runScript))
            return ValidationResult.Fail("Run-script name is required.");
        if (!File.Exists(Path.Combine(installDir, runScript)))
            return ValidationResult.Fail($"Run script not found: {Path.Combine(installDir, runScript)}");

        // A managed engine still needs an endpoint to talk to once launched.
        return ValidateRemote(baseUrl);
    }
}
