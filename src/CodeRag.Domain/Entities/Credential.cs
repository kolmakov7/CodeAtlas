using CodeRag.Domain.Enums;
using CodeRag.Domain.Exceptions;

namespace CodeRag.Domain.Entities;

/// <summary>Учётные данные для Git (таблица credentials).</summary>
public sealed class Credential
{
    public Guid Id { get; private set; }
    public CredentialKind Kind { get; private set; }
    public string? Username { get; private set; }
    public string? SecretEncrypted { get; private set; }
    public string? SshKeyPath { get; private set; }

    private Credential(Guid id, CredentialKind kind)
    {
        Id = id;
        Kind = kind;
    }

    public static Credential Create(
        Guid id, CredentialKind kind,
        string? username = null, string? secretEncrypted = null, string? sshKeyPath = null)
    {
        if (id == Guid.Empty) throw new DomainValidationException("Credential Id must not be empty.");

        var cred = new Credential(id, kind)
        {
            Username = string.IsNullOrWhiteSpace(username) ? null : username.Trim(),
            SecretEncrypted = string.IsNullOrWhiteSpace(secretEncrypted) ? null : secretEncrypted,
            SshKeyPath = string.IsNullOrWhiteSpace(sshKeyPath) ? null : sshKeyPath.Trim()
        };

        // Минимальные инварианты по виду
        if (kind == CredentialKind.SshKey && cred.SshKeyPath is null && cred.SecretEncrypted is null)
            throw new DomainValidationException("SshKey credential requires ssh_key_path or secret_encrypted.");

        return cred;
    }
}
