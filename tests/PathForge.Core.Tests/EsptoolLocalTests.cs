using PathForge.Core.Grbl;

namespace PathForge.Core.Tests;

public sealed class EsptoolLocalTests
{
    [Fact]
    public void Local_executable_is_checked_in_the_application_folder_only()
    {
        var folder = Path.Combine(Path.GetTempPath(), "PathForge with spaces");
        var expected = Path.Combine(folder, "esptool.exe");
        var checkedPaths = new List<string>();

        var found = EsptoolLocator.LocalExecutable(folder, path =>
        {
            checkedPaths.Add(path);
            return path == expected;
        });

        Assert.Equal(expected, found);
        Assert.Equal(new[] { expected }, checkedPaths);
        Assert.Null(EsptoolLocator.LocalExecutable(folder, _ => false));
        Assert.Null(EsptoolLocator.LocalExecutable("", _ => true));
    }

    [Fact]
    public void Local_candidates_ignore_saved_path_subfolders_and_python()
    {
        var folder = Path.Combine(Path.GetTempPath(), "PathForge");
        var candidate = Assert.Single(EsptoolLocator.Candidates(
            "old-custom-esptool.exe", folder, _ => true, windows: true, localOnly: true));

        Assert.Equal(Path.Combine(folder, "esptool.exe"), candidate.FileName);
        Assert.Empty(candidate.PrefixArguments);
        Assert.Empty(EsptoolLocator.Candidates("old-custom-esptool.exe", folder,
            path => path != Path.Combine(folder, "esptool.exe"), windows: true, localOnly: true));
    }

    [Theory]
    [InlineData("4.8.1")]
    [InlineData("5.5.0")]
    public async Task Local_session_probes_the_Arduino_executable_directly(string version)
    {
        var folder = Path.Combine(Path.GetTempPath(), "PathForge with spaces");
        var local = Path.Combine(folder, "esptool.exe");
        var process = new FakeEsptool { Version = version };
        var session = new Esp32FlashSession(process, folder, _ => { }, (_, _) => { }, path => path == local, windows: true)
        {
            LocalToolOnly = true,
            ExplicitToolPath = "old-custom-esptool.exe",
        };

        var found = await session.FindToolAsync(CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(local, found!.FileName);
        Assert.Empty(found.PrefixArguments);
        Assert.Equal(int.Parse(version.Split('.')[0]), found.Version.Major);
        Assert.Single(process.Calls);
        Assert.Equal(new[] { "version" }, process.Calls[0]);
    }

    [Fact]
    public async Task Missing_local_executable_never_starts_a_process()
    {
        var process = new FakeEsptool();
        var session = new Esp32FlashSession(process, "PathForge", _ => { }, (_, _) => { }, _ => false, windows: true)
        {
            LocalToolOnly = true,
        };

        Assert.Null(await session.FindToolAsync(CancellationToken.None));
        Assert.Empty(process.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Broken_local_executable_never_falls_back_to_another_installation(bool cannotStart)
    {
        var process = new FakeEsptool
        {
            OnProbe = (tool, _, _) => cannotStart
                ? throw new EsptoolNotStartedException(tool.FileName)
                : Task.FromResult(1),
        };
        var session = new Esp32FlashSession(process, "PathForge", _ => { }, (_, _) => { }, _ => true, windows: true)
        {
            LocalToolOnly = true,
        };

        Assert.Null(await session.FindToolAsync(CancellationToken.None));
        Assert.Equal(Path.Combine("PathForge", "esptool.exe"), Assert.Single(process.Tools).FileName);
    }
}
