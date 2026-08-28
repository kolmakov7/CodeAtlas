namespace CodeRag.Domain.Enums;

/// <summary>Тип учётных данных (credentials.kind).</summary>
public enum CredentialKind
{
    HttpsToken = 0,
    HttpsUserPass = 1,
    SshKey = 2
}
