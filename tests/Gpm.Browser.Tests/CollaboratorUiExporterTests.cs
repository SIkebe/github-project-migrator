using Gpm.Core.Browser;

namespace Gpm.Browser.Tests;

public class CollaboratorUiExporterTests
{
    [Fact]
    public void ParseAccessSnapshot_reads_explicit_user_collaborators_and_roles()
    {
        const string snapshot = """
        - heading "Manage access" [level=3]
        - checkbox "Select all collaborators. 1 member"
        - text: Select all collaborators. 1 member
        - checkbox "Select ravel-maurice-uo_sde"
        - img "ravel-maurice-uo_sde"
        - link "Ravel Maurice":
          - /url: /ravel-maurice-uo_sde
        - text: ravel-maurice-uo_sde
        - 'button "Role: Write"'
        - button "Remove"
        """;

        var collaborator = Assert.Single(CollaboratorUiExporter.ParseAccessSnapshot(snapshot, "gpm-source"));

        Assert.Equal("USER", collaborator.Type);
        Assert.Equal("ravel-maurice-uo_sde", collaborator.Login);
        Assert.Equal("WRITER", collaborator.Role);
    }

    [Fact]
    public void ParseAccessSnapshot_reads_team_slug_when_team_url_is_present()
    {
        const string snapshot = """
        - heading "Manage access" [level=3]
        - checkbox "Select Roadmap Team"
        - link "Roadmap Team":
          - /url: /orgs/gpm-source/teams/roadmap-team
        - text: Roadmap Team
        - 'button "Role: Admin"'
        """;

        var collaborator = Assert.Single(CollaboratorUiExporter.ParseAccessSnapshot(snapshot, "gpm-source"));

        Assert.Equal("TEAM", collaborator.Type);
        Assert.Equal("roadmap-team", collaborator.Login);
        Assert.Equal("ADMIN", collaborator.Role);
    }

    [Fact]
    public void ParseAccessSnapshot_ignores_select_all_checkbox()
    {
        const string snapshot = """
        - checkbox "Select all collaborators. 0 members"
        - text: Select all collaborators. 0 members
        - heading "You don't have any collaborators yet." [level=3]
        """;

        Assert.Empty(CollaboratorUiExporter.ParseAccessSnapshot(snapshot, "gpm-source"));
    }
}
