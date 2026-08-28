namespace CodeRag.Domain.Exceptions;

/// <summary>Базовое исключение домена.</summary>
public class DomainException : Exception
{
    public DomainException(string message) : base(message) { }
}

/// <summary>Ошибка валидации инварианта.</summary>
public sealed class DomainValidationException : DomainException
{
    public DomainValidationException(string message) : base(message) { }
}
