namespace MovieDiscovery.Core.Similarity;

/// <summary>
/// Внесок одного сигналу у схожість. Weight — вага сигналу, Value — нормалізоване значення 0..1,
/// Items — спільні значення (для причин; для credits із префіксами "actor:" / "director:"),
/// Support — частка еталонів, що підтримують сигнал (1.0 для одного seed-а, k/n для multi-seed).
/// </summary>
public sealed record SignalContribution(
    string Signal, double Weight, double Value, IReadOnlyList<string> Items, double Support = 1.0);

/// <summary>Результат скорингу пари «еталон-профіль ↔ кандидат».</summary>
public sealed record SimilarityScore(double Value, IReadOnlyList<SignalContribution> Contributions);
