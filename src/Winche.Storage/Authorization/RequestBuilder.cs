using Winche.Rules;

namespace Winche.Storage.Authorization;

/// <summary>
/// Builds the Firestore-style <c>request</c> map passed to the rules engine.
/// Produced map: <c>{ auth, resource, method, time }</c>.
/// </summary>
internal static class RequestBuilder
{
    public static RuleValue Build(
        IReadOnlyDictionary<string, object?>? claims,
        string method,
        RuleValue requestResource) =>
        Build(claims, method, requestResource, DateTimeOffset.UtcNow);

    public static RuleValue Build(
        IReadOnlyDictionary<string, object?>? claims,
        string method,
        RuleValue requestResource,
        DateTimeOffset now) =>
        RuleValue.Map(new Dictionary<string, RuleValue>
        {
            ["auth"] = BuildAuth(claims),
            ["resource"] = requestResource,
            ["method"] = RuleValue.String(method),
            ["time"] = RuleValue.Timestamp(now),
        });

    private static RuleValue BuildAuth(IReadOnlyDictionary<string, object?>? claims)
    {
        if (claims is null || claims.Count == 0)
            return RuleValue.Null;

        var uid = claims.TryGetValue("uid", out var rawUid) && rawUid is not null
            ? RuleValue.String(rawUid.ToString()!)
            : RuleValue.Null;

        return RuleValue.Map(new Dictionary<string, RuleValue>
        {
            ["uid"] = uid,
            ["token"] = RuleValue.Map(claims.ToDictionary(kv => kv.Key, kv => ConvertClaimValue(kv.Value))),
        });
    }

    private static RuleValue ConvertClaimValue(object? value) => value switch
    {
        null => RuleValue.Null,
        bool b => RuleValue.Bool(b),
        long l => RuleValue.Int(l),
        int i => RuleValue.Int(i),
        double d => RuleValue.Double(d),
        float f => RuleValue.Double(f),
        string s => RuleValue.String(s),
        byte[] bytes => RuleValue.Bytes(bytes),
        DateTimeOffset dto => RuleValue.Timestamp(dto),
        IReadOnlyDictionary<string, object?> nested => RuleValue.Map(nested.ToDictionary(kv => kv.Key, kv => ConvertClaimValue(kv.Value))),
        IDictionary<string, object?> nested => RuleValue.Map(nested.ToDictionary(kv => kv.Key, kv => ConvertClaimValue(kv.Value))),
        IEnumerable<object?> list => RuleValue.List(list.Select(ConvertClaimValue).ToList()),
        _ => RuleValue.String(value.ToString() ?? string.Empty),
    };
}
