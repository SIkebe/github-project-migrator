using Ghpmv.Core.Fixtures;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Tests;

public class FixtureProjectBuilderTests
{
    [Fact]
    public void Demo_fixture_exercises_every_snapshot_field_pattern()
    {
        var snapshot = FixtureProjectBuilder.CreateSnapshot(
            "Fixture",
            "example/fixture",
            "octocat",
            pullRequestNumber: 2);
        var values = snapshot.Items.SelectMany(item => item.FieldValues).ToList();

        foreach (var property in typeof(FieldValueSnapshot).GetProperties()
                     .Where(property => property.Name != nameof(FieldValueSnapshot.FieldName)))
        {
            Assert.Contains(values, value => property.GetValue(value) is not null);
        }

        foreach (var property in typeof(FieldSnapshot).GetProperties()
                     .Where(property => property.Name is not nameof(FieldSnapshot.Name) and not nameof(FieldSnapshot.DataType)))
        {
            Assert.Contains(snapshot.Fields, field => property.GetValue(field) is not null);
        }
    }

    [Fact]
    public void Demo_fixture_puts_multi_select_values_on_a_real_issue()
    {
        var snapshot = FixtureProjectBuilder.CreateSnapshot(
            "Fixture",
            "example/fixture",
            "octocat",
            pullRequestNumber: 2);

        var field = Assert.Single(snapshot.Fields, field => field.Name == "Fixture Teams");
        Assert.Equal("MULTI_SELECT", field.DataType);
        Assert.NotNull(field.IssueField);
        Assert.Equal("ALL", field.IssueField.Visibility);
        Assert.Equal(["Platform", "SDK", "Docs"], field.Options!.Select(option => option.Name));

        var issue = Assert.Single(snapshot.Items, item => item.Type == "ISSUE");
        var value = Assert.Single(issue.FieldValues, value => value.FieldName == field.Name);
        Assert.Equal(["Platform", "SDK"], value.MultiSelectOptionNames);
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, false, false, false)]
    public void Item_stage_runs_only_for_new_or_resumable_fixture(
        bool projectAlreadyExists,
        bool hasItemLog,
        bool projectImportWasPending,
        bool expected)
    {
        Assert.Equal(
            expected,
            FixtureProjectBuilder.ShouldImportItems(
                projectAlreadyExists,
                hasItemLog,
                projectImportWasPending));
    }
}
