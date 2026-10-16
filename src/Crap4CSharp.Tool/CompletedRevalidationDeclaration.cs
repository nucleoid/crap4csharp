using Crap4CSharp.Core;

// Declarations are compared to independently evaluated compiler paths, never
// resolved or executed as captured instructions. This alone enables no reuse.
internal static class CompletedRevalidationDeclaration
{
    internal const string Provider = "finite-sdk-completed-tests-v1";

    internal static void Validate(ManifestContext context, string evaluatedProject,
        string evaluatedAssembly, string evaluatedPdb)
    {
        var recipe = context.CurrentRevalidation;
        if (!context.ReuseRecipeComplete || recipe is null || recipe.Provider != Provider ||
            recipe.References is null || recipe.References.Count != 0 ||
            !Exact(context.Project, evaluatedProject) || !Exact(recipe.Project, evaluatedProject) ||
            !Exact(recipe.AssemblyPath, evaluatedAssembly) || !Exact(recipe.PdbPath, evaluatedPdb))
            throw new PolicyException("provenance.completedDeclarationUnproven",
                "Completed revalidation declarations differ from independently evaluated compiler paths.");
        // Actual metadata references remain fully bound by SavedCompilerContexts;
        // unsupported saved reference path directives are not accepted or executed.
    }

    private static bool Exact(string declared, string evaluated)
    {
        if (string.IsNullOrEmpty(declared) || Path.IsPathRooted(declared) || declared.Contains('\\') ||
            declared.Contains(':') || declared.Split('/').Any(part => part is "" or "." or "..")) return false;
        try { return declared == CanonicalIdentity.NormalizeLogicalPath(declared) && declared == evaluated; }
        catch (ArgumentException) { return false; }
    }
}
