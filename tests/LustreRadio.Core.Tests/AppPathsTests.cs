using LustreRadio.Core;
using Xunit;

namespace LustreRadio.Core.Tests;

public class AppPathsTests
{
    [Fact]
    public void SubFolders_AreDistinct()
    {
        Assert.Equal(AppPaths.SubFolders.Count, AppPaths.SubFolders.Distinct().Count());
    }

    [Fact]
    public void SubFolder_IsInsideDataRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "lustre-test-root");
        foreach (var name in AppPaths.SubFolders)
        {
            Assert.Equal(Path.Combine(root, name), AppPaths.SubFolder(root, name));
        }
    }

    [Fact]
    public void SubFolder_RejectsUnknownName()
    {
        Assert.Throws<ArgumentException>(() => AppPaths.SubFolder("x", "nepostojeci"));
    }

    [Fact]
    public void DefaultDataRoot_EndsWithAppFolder()
    {
        Assert.EndsWith(AppPaths.AppFolderName, AppPaths.DefaultDataRoot);
    }

    [Fact]
    public void DatabasePath_IsInsideDataRoot()
    {
        Assert.Equal(Path.Combine("root", "lustre.db"), AppPaths.DatabasePath("root"));
    }
}
