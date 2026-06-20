namespace Winche.Storage.Constants;

internal static class ServiceKeys
{
    public const string DATA_SOURCE_KEY = "WincheStorage";

    /// <summary>
    /// The keyed-service key under which this package's isolated <c>RuleEngine</c> is registered.
    /// Distinct from any other package's engine so rulesets never merge.
    /// </summary>
    public const string RULE_ENGINE_KEY = "WincheStorage.RuleEngine";
}
