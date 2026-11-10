using PcTidy.Features.AiChats.Local;
using Xunit;

namespace PcTidy.Features.AiChats.Tests;

/// <summary>v3 UI review fix UI3-39: agent chat projects are named after their folder, never with the Windows user name.</summary>
[Collection("aichats-sandbox")]
public class UiReviewFixTests
{
    [Fact]
    public void Encoded_Claude_Code_folders_decode_to_the_real_folder_and_drop_the_user_name()
    {
        using var s = new AiSandbox("decode");
        var profile = s.K.UserProfile;
        Directory.CreateDirectory(Path.Combine(profile, "source", "repos", "my-app"));
        Directory.CreateDirectory(Path.Combine(profile, "source", "repos", "WeatherApp"));

        // Encoded like Claude Code does it (every non letter/digit becomes '-'), also the usual C--Users-<name> form.
        var encoded = AgentProjects.Encode(Path.Combine(profile, "source", "repos", "my-app"));
        Assert.Equal(Path.Combine(profile, "source", "repos", "my-app"), AgentProjects.Decode(encoded, profile));
        var user = Path.GetFileName(profile);
        Assert.Equal(Path.Combine(profile, "source", "repos", "WeatherApp"), AgentProjects.Decode($"C--Users-{user}-source-repos-WeatherApp", profile));

        // Can't be matched (moved away): no user name in what is shown.
        Assert.Null(AgentProjects.Decode($"C--Users-{user}-gone-project", profile));
        Assert.Equal("gone-project", AgentProjects.StripProfile($"C--Users-{user}-gone-project", profile));

        Assert.Equal(Path.Combine("source", "repos"), AgentProjects.Hint(Path.Combine(profile, "source", "repos", "my-app"), profile));
        Assert.Equal("your user folder", AgentProjects.Hint(Path.Combine(profile, "notes"), profile));
        Assert.Null(AgentProjects.Hint(null, profile));
    }

    [Fact]
    public async Task Inventory_names_a_session_without_cwd_after_its_decoded_folder()
    {
        using var s = new AiSandbox("decode-inventory");
        var profile = s.K.UserProfile;
        Directory.CreateDirectory(Path.Combine(profile, "source", "repos", "WeatherApp"));
        var user = Path.GetFileName(profile);
        var dir = Path.Combine(s.K.ClaudeDir, "projects", $"C--Users-{user}-source-repos-WeatherApp");
        AiSandbox.Put(Path.Combine(dir, "11111111-2222-3333-4444-555555555555.jsonl"), "{\"type\":\"summary\",\"summary\":\"no cwd here\"}\n");

        var session = Assert.Single(await AgentChatInventory.ClaudeAsync(s.K, CancellationToken.None));
        Assert.Null(session.Cwd);
        Assert.Equal("WeatherApp", session.ProjectName);
        Assert.DoesNotContain(user, session.ProjectName);
        Assert.Equal(Path.Combine("source", "repos"), AgentProjects.Hint(session.ProjectPath, profile));
    }
}
