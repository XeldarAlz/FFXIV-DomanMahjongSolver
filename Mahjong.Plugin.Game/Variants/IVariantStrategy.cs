namespace Mahjong.Plugin.Game.Variants;

/// <summary>
/// One client variant's reading strategy. Owns the variant's
/// <see cref="LayoutProfile"/> and knows how to dereference the live addon
/// using its offsets.
///
/// Phase 6.A: a single concrete implementation in <c>Mahjong.Plugin.Dalamud</c>
/// reads <see cref="Profile"/> at runtime — no per-variant subclass.
/// Phase 6.B: <see cref="IVariantSelector"/> picks among registered strategies
/// and caches the choice per session.
/// </summary>
public interface IVariantStrategy
{
    LayoutProfile Profile { get; }
}

/// <summary>
/// Selects the variant strategy to use for the live addon. The Dalamud-coupled
/// implementation invokes each strategy's probe; the JSON-driven fallback
/// matches by addon name.
/// </summary>
public interface IVariantSelector
{
    IReadOnlyList<IVariantStrategy> Strategies { get; }

    /// <summary>Return the strategy whose <see cref="LayoutProfile.AddonName"/> matches, or null.</summary>
    IVariantStrategy? ResolveByAddonName(string addonName);
}
