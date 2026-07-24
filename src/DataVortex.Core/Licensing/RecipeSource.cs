using DataVortex.Licensing;

namespace DataVortex.Core.Licensing;

/// <summary>Where the Passculture checker reads its operational recipe from at runtime (Palier C). In a Release
/// build this is fed ONLY by the live session (the server delivers the recipe encrypted; the SessionManager
/// decrypts it into here). No session ⇒ <see cref="Current"/> is null ⇒ the checker builds nothing. The recipe
/// never lives in the client binary nor on disk.</summary>
public interface IRecipeSource
{
    OperationalRecipe? Current { get; }
}

/// <summary>Thread-safe in-memory holder for the current recipe. Set by the SessionManager (Release) or the dev
/// bypass (Debug). Cleared (set to null) the moment the session drops, so the checker fails closed.</summary>
public sealed class RecipeHolder : IRecipeSource
{
    private volatile OperationalRecipe? _current;
    public OperationalRecipe? Current => _current;
    public void Set(OperationalRecipe? recipe) => _current = recipe;
}
