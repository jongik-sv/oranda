using Oranda.Core.Connection;

namespace Oranda.Core.Tests.Connection;

// 접속 폴더 규칙: 경로 정규화, 하위 폴더, 이름 바꾸기·옮기기·삭제(접속은 부모로 올림).
public sealed class ConnectionFoldersTests
{
    private static ConnectionProfile P(string name, string? group, Guid? id = null) =>
        new(name, "scott", "db:1521/s", Group: group, Id: id ?? Guid.NewGuid());

    private static ConnectionsDocument Doc(IEnumerable<string> folders, params ConnectionProfile[] profiles) =>
        new() { Folders = [.. folders], Profiles = profiles };

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData("//", null)]
    [InlineData(" a / b ", "a/b")]
    [InlineData("a//b/", "a/b")]
    public void Normalize_trims_each_level_and_drops_empty_levels(string? input, string? expected) =>
        Assert.Equal(expected, ConnectionFolders.Normalize(input));

    [Fact]
    public void All_includes_empty_folders_profile_groups_and_every_parent()
    {
        var doc = Doc(["빈 폴더", "x/y"], P("a", "dev/team"), P("b", null));
        Assert.Equal(["dev", "dev/team", "x", "x/y", "빈 폴더"], ConnectionFolders.All(doc).OrderBy(f => f, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void UniqueName_numbers_the_name_under_the_same_parent()
    {
        Assert.Equal("새 폴더", ConnectionFolders.UniqueName([], null));
        Assert.Equal("새 폴더 (2)", ConnectionFolders.UniqueName(["새 폴더"], null));
        Assert.Equal("새 폴더 (3)", ConnectionFolders.UniqueName(["새 폴더", "새 폴더 (2)", "a/새 폴더"], null));
        Assert.Equal("새 폴더", ConnectionFolders.UniqueName(["새 폴더"], "a"));
    }

    [Fact]
    public void Move_renames_the_folder_and_rewrites_profiles_and_subfolders()
    {
        var doc = Doc(["a", "a/b", "a/b/c", "other"], P("p1", "a"), P("p2", "a/b"), P("p3", "other"));
        var moved = ConnectionFolders.Move(doc, "a", "z");

        Assert.Equal(["z", "z/b", "z/b/c", "other"], moved.Folders.ToArray());
        Assert.Equal(["z", "z/b", "other"], moved.Profiles.Select(p => p.Group).ToArray());
    }

    [Fact]
    public void Move_into_itself_or_a_descendant_or_an_existing_folder_is_rejected()
    {
        var doc = Doc(["a", "a/b", "c"]);
        Assert.Throws<ConnectionFolderException>(() => ConnectionFolders.Move(doc, "a", "a/b/a"));
        Assert.Throws<ConnectionFolderException>(() => ConnectionFolders.Move(doc, "a", "c"));
        Assert.Throws<ConnectionFolderException>(() => ConnectionFolders.Move(doc, "nope", "x"));
    }

    [Fact]
    public void Delete_lifts_profiles_and_subfolders_to_the_parent_without_removing_profiles()
    {
        var doc = Doc(["a", "a/b", "a/b/c"], P("p1", "a/b"), P("p2", "a/b/c"), P("p3", "a"));
        var after = ConnectionFolders.Delete(doc, "a/b");

        Assert.Equal(3, after.Profiles.Count);
        Assert.Equal(["a", "a/c", "a"], after.Profiles.Select(p => p.Group).ToArray());
        Assert.DoesNotContain("a/b", after.Folders);
        Assert.Contains("a/c", after.Folders);
    }

    [Fact]
    public void Delete_of_a_top_folder_moves_its_profiles_to_the_top()
    {
        var after = ConnectionFolders.Delete(Doc(["a"], P("p1", "a")), "a");
        Assert.Null(after.Profiles[0].Group);
        Assert.Empty(after.Folders);
    }

    [Fact]
    public void MoveProfile_changes_only_that_profile_group()
    {
        var id = Guid.NewGuid();
        var doc = Doc(["a"], P("p1", null, id), P("p2", "a"));
        var after = ConnectionFolders.MoveProfile(doc, id, " a / b ");
        Assert.Equal(["a/b", "a"], after.Profiles.Select(p => p.Group).ToArray());
        Assert.Null(ConnectionFolders.MoveProfile(after, id, null).Profiles[0].Group);
    }
}
