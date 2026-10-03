using PathForge.Core.Grbl;
using PathForge.Core.Localization;
using PathForge.Core.Machining;
using PathForge.Core.Projects;

namespace PathForge.Core.Tests;

/// <summary>Tests that switch the global language run alone: other tests expect the default Russian texts.</summary>
[CollectionDefinition(nameof(LanguageCollection), DisableParallelization = true)]
public sealed class LanguageCollection
{
}

[Collection(nameof(LanguageCollection))]
public class LocalizationTests
{
    private static void InEnglish(Action action)
    {
        Loc.Language = AppLanguage.English;
        try
        {
            action();
        }
        finally
        {
            Loc.Language = AppLanguage.Russian;
        }
    }

    [Fact]
    public void Texts_follow_the_current_language()
    {
        Assert.Equal("Глубина", Loc.T("Глубина", "Depth"));
        InEnglish(() =>
        {
            Assert.True(Loc.IsEnglish);
            Assert.Equal("Depth", Loc.T("Глубина", "Depth"));
            Assert.Equal("CNC 3018 Pro (10 W laser)", MachineProfiles.Cnc3018Laser10W.Name);
            Assert.StartsWith("Error 22:", GrblMessages.Error(22), StringComparison.Ordinal);
            Assert.Equal("Idle", GrblMessages.StateName(GrblState.Idle));
        });
        Assert.Equal("CNC 3018 Pro (лазер 10 Вт)", MachineProfiles.Cnc3018Laser10W.Name);
    }

    [Fact]
    public void Language_change_is_announced_once()
    {
        var calls = 0;
        void Handler() => calls++;
        Loc.LanguageChanged += Handler;
        try
        {
            InEnglish(() => Loc.Language = AppLanguage.English);
        }
        finally
        {
            Loc.LanguageChanged -= Handler;
        }

        // Russian → English and back; setting the same language again is not a change.
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Stored_profile_names_are_found_in_both_languages()
    {
        foreach (var profile in MachineProfiles.All)
        {
            Assert.Same(profile, MachineProfiles.Find(profile.NameRu));
            Assert.Same(profile, MachineProfiles.Find(profile.NameEn));
        }

        Assert.Null(MachineProfiles.Find("unknown"));
    }

    [Fact]
    public void New_tools_and_warnings_use_the_current_language()
    {
        InEnglish(() =>
        {
            var project = CamProject.CreateDefault();
            Assert.Equal("New project", project.Name);
            Assert.Equal("1-flute end mill Ø3.175", project.Tools[0].Name);
            Assert.Equal("CNC 3018 Pro (775 spindle)", project.Machine.ProfileName);

            project.Operations.Add(new ProfileOperation { Name = "Cut", ToolId = project.Tools[0].Id });
            var warning = Assert.Single(ToolpathGenerator.Generate(project).Warnings);
            Assert.Equal("Cut: no contours selected.", warning);
        });

        Assert.Equal("Фреза 1-заходная Ø3,175", CamProject.CreateDefault().Tools[0].Name);
    }
}
