using RevitCli.Profile;

namespace RevitCli.Fix;

internal static class FixRecipeIdentity
{
    public static string Create(FixRecipe? recipe)
    {
        if (recipe is null)
        {
            return "";
        }

        return string.Join(
            "\u001f",
            recipe.Rule ?? "",
            recipe.Category ?? "",
            recipe.Parameter ?? "",
            recipe.Strategy ?? "",
            recipe.Value ?? "",
            recipe.Match ?? "",
            recipe.Replace ?? "");
    }
}
