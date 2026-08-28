namespace CodeRag.Git.Auth;

/// <summary>
/// Абстракция шифрования секретов (DataProtection placeholder).
/// MVP: не шифрует, но интерфейс позволяет подменить на IDataProtector в будущем.
/// Хранение в /data/keys/ volume (см. docs/git-proxy-auth.md §3).
/// </summary>
public interface ICredentialProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedData);
}
