using Muesli.Windows.Services.Persistence;

namespace Muesli.Windows.Tests;

/// <summary>Opens a SQLite library in a temporary data directory, the way the app does at launch.</summary>
internal static class TestLibrary
{
    public static SqliteLibraryHistoryAdapter Open(string dataDirectory)
    {
        var startup = LibraryDatabaseStartup.Prepare(dataDirectory);
        Assert.True(startup.IsReady, startup.Message);
        return SqliteLibraryHistoryAdapter.Open(dataDirectory);
    }
}
