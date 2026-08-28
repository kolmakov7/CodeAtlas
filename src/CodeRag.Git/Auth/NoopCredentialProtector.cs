namespace CodeRag.Git.Auth;

/// <summary>Заглушка — не шифрует, как указано: "пока не шифровать, но интерфейс ICredentialProtector".</summary>
public sealed class NoopCredentialProtector : ICredentialProtector
{
    public string Protect(string plaintext) => plaintext ?? string.Empty;
    public string Unprotect(string protectedData) => protectedData ?? string.Empty;
}
