using Winche.Rules;
using Winche.Rules.Evaluation;
using Winche.Storage.Interfaces;

namespace Winche.Storage.Authorization;

/// <summary>
/// Backs <c>get()</c>/<c>exists()</c> in rule expressions, resolving paths to file resources.
/// </summary>
internal sealed class FileStoreResourceProvider(IFileStorage inner) : IRuleResourceProvider
{
    public async Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var file = await inner.GetUnprotectedAsync(path, ct);
        return file is not null;
    }

    public async Task<RuleValue> GetAsync(string path, CancellationToken ct = default)
    {
        var file = await inner.GetUnprotectedAsync(path, ct);
        return file is not null ? FileToResource.Convert(file) : RuleValue.Null;
    }
}
