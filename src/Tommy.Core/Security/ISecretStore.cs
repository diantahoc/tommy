namespace Tommy.Core.Security;

/// <summary>
/// Abstraction over secret storage (engine/DB credentials, keyed by instance).
/// v1 ships a plaintext-file implementation; swapping in the OS store (DPAPI on
/// Windows, libsecret on Linux) later is a one-class change behind this
/// interface, not a data migration.
/// </summary>
public interface ISecretStore
{
    /// <summary>Return the stored value for a key, or null if absent.</summary>
    string? Get(string key);

    /// <summary>Store (or replace) a value for a key.</summary>
    void Set(string key, string value);

    /// <summary>Remove a key if present.</summary>
    void Delete(string key);
}
