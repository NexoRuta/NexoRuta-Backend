namespace NexoRuta.IntegrationTests.Fixtures;

internal static class CompanionPortals
{
#if DEBUG
    internal const string Configuration = "Debug";
#else
    internal const string Configuration = "Release";
#endif

    internal static string WorkspaceRoot
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "frontend", "src", "NexoRuta.Commerce", "NexoRuta.Commerce.csproj")))
                    return directory.FullName;
            throw new DirectoryNotFoundException("Build the pinned frontend companion beside backend before browser integration tests.");
        }
    }
}
