using LustreRadio.Core;
using Xunit;

namespace LustreRadio.Core.Tests;

public class BuildProbeTests
{
    [Fact]
    public void Add_ReturnsSum() => Assert.Equal(5, BuildProbe.Add(2, 3));

    [Fact]
    public void RunsOnWindows() => Assert.True(OperatingSystem.IsWindows());
}
