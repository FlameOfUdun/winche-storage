using System.Collections.Immutable;
using Winche.Storage.Models;
using WincheSentinel.Interfaces;

namespace Winche.Storage.Services;

public sealed class CallerContextAccessor : ICallerContextAccessor<FileRecord>
{
    private readonly AsyncLocal<IReadOnlyDictionary<string, object?>> _asyncLocal = new();

    public IReadOnlyDictionary<string, object?> GetClaims()
    {
        return _asyncLocal.Value ??= ImmutableDictionary<string, object?>.Empty;
    }

    public void SetClaims(Dictionary<string, object?> claims)
    {
        _asyncLocal.Value = claims;
    }

    public void SetClaims(IReadOnlyDictionary<string, object?> claims)
    {
        _asyncLocal.Value = claims;
    }
}
