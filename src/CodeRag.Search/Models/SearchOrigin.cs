namespace CodeRag.Search.Models;

/// <summary>
/// Origin of a search candidate. The wire value is the lower-invariant name
/// (exact / fts / vector / structural) emitted in <see cref="SearchResultItem.Origin"/>.
/// </summary>
public enum SearchOrigin
{
    Exact = 0,
    Fts = 1,
    Vector = 2,
    Structural = 3
}
