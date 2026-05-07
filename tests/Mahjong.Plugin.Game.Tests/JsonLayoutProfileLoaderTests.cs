using System.IO;

namespace Mahjong.Plugin.Game.Tests;

public class JsonLayoutProfileLoaderTests
{
    private const string EmjJson = """
{
  "name": "Emj",
  "addonName": "Emj",
  "tileTextureBase": 76041,
  "offsets": {
    "selfScore": "0x0500",
    "shimochaScore": "0x07E0",
    "toimenScore": "0x0AC0",
    "kamichaScore": "0x0DA0",
    "selfDiscardCountByte": "0x04FE",
    "shimochaDiscardCountByte": "0x07DE",
    "toimenDiscardCountByte": "0x0ABE",
    "kamichaDiscardCountByte": "0x0D9E",
    "handArrayStart": "0x0DB8",
    "doraIndicator": "0x0FD8"
  },
  "nodeIds": {
    "callModalHost": 104,
    "callModalShell": 3
  },
  "atkValues": {
    "stateCode": 0,
    "wallCount": 1,
    "chiClaimedTile": 19
  },
  "stateCodes": {
    "ourTurnDiscard": 30,
    "callPrompt": 15,
    "callPromptList": 28,
    "selfDeclareList": 6,
    "postDrawIdle": 5
  },
  "limits": {
    "handSize": 14,
    "wallInitial": 70,
    "scoreSanityMax": 200000,
    "discardCountSanityMax": 40,
    "maxAkadoraSlots": 3
  }
}
""";

    [Fact]
    public void Parse_loads_emj_profile_with_correct_texture_base()
    {
        var profile = JsonLayoutProfileLoader.Parse(EmjJson);
        Assert.Equal("Emj", profile.Name);
        Assert.Equal("Emj", profile.AddonName);
        Assert.Equal(76041, profile.TileTextureBase);
    }

    [Fact]
    public void Parse_decodes_hex_strings_to_decimal_offsets()
    {
        var profile = JsonLayoutProfileLoader.Parse(EmjJson);
        Assert.Equal(0x0500, profile.Offsets.SelfScore);
        Assert.Equal(0x0DB8, profile.Offsets.HandArrayStart);
        Assert.Equal(0x0FD8, profile.Offsets.DoraIndicator);
    }

    [Fact]
    public void Parse_accepts_decimal_numbers_for_node_ids()
    {
        var profile = JsonLayoutProfileLoader.Parse(EmjJson);
        Assert.Equal(104u, profile.NodeIds.CallModalHost);
        Assert.Equal(3u, profile.NodeIds.CallModalShell);
    }

    [Fact]
    public void Parse_rejects_invalid_json_with_clear_error()
    {
        Assert.ThrowsAny<Exception>(() => JsonLayoutProfileLoader.Parse("{ not json"));
    }

    [Fact]
    public void Load_throws_FileNotFoundException_for_missing_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid()}.json");
        Assert.Throws<FileNotFoundException>(() => JsonLayoutProfileLoader.Load(path));
    }

    [Fact]
    public void Load_round_trips_a_written_profile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"profile-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(path, EmjJson);
            var profile = JsonLayoutProfileLoader.Load(path);
            Assert.Equal(76041, profile.TileTextureBase);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void LoadAll_discovers_every_json_in_a_directory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"layouts-{Guid.NewGuid()}");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "emj.json"), EmjJson);
            File.WriteAllText(Path.Combine(dir, "emj_l.json"), EmjJson.Replace("76041", "76003").Replace("\"Emj\"", "\"EmjL\""));

            var profiles = JsonLayoutProfileLoader.LoadAll(dir);
            Assert.Equal(2, profiles.Count);
            Assert.Contains(profiles, p => p.TileTextureBase == 76041);
            Assert.Contains(profiles, p => p.TileTextureBase == 76003);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadAll_returns_empty_list_for_missing_directory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid()}");
        var profiles = JsonLayoutProfileLoader.LoadAll(dir);
        Assert.Empty(profiles);
    }

    [Fact]
    public void Real_emj_layout_loads_from_the_repo_data_dir()
    {
        // Sanity check: the actual data/layouts/emj.json that ships in the repo
        // parses cleanly into the same expected texture base. If this fails,
        // either the JSON or the loader has drifted.
        var path = ResolveRepoFile("data", "layouts", "emj.json");
        if (path is null)
            return;   // running outside the repo (CI artifact extraction etc.) — skip.

        var profile = JsonLayoutProfileLoader.Load(path);
        Assert.Equal("Emj", profile.Name);
        Assert.Equal(76041, profile.TileTextureBase);
    }

    private static string? ResolveRepoFile(params string[] relativeSegments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Mahjong.Plugin.Dalamud.sln")))
            {
                var segments = new string[relativeSegments.Length + 1];
                segments[0] = dir.FullName;
                Array.Copy(relativeSegments, 0, segments, 1, relativeSegments.Length);
                var path = Path.Combine(segments);
                return File.Exists(path) ? path : null;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
