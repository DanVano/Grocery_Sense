using GrocerySense.Core;

namespace GrocerySense.Tests;

public sealed class ConfigStoreMemberTests : TempDirTestBase
{

    private ConfigStore New() => new(_dir);

    [Fact]
    public void Add_member_appends_a_secondary_with_the_next_id()
    {
        var cfg = New();
        var master = cfg.GetMasterMember();

        var kid = cfg.AddMember("Kid");
        Assert.NotEqual(master.Id, kid.Id);
        Assert.Equal("Kid", kid.Name);
        Assert.True(cfg.IsSecondary(kid.Id));
        Assert.True(cfg.IsMaster(master.Id));
        Assert.Contains(cfg.ListMembers(), m => m.Id == kid.Id);
    }

    [Fact]
    public void Rename_and_active_member_persist()
    {
        var cfg = New();
        var kid = cfg.AddMember("Kd");
        cfg.RenameMember(kid.Id, "Kiddo");
        Assert.Equal("Kiddo", cfg.GetMember(kid.Id)!.Name);

        cfg.SetActiveMemberId(kid.Id);
        Assert.Equal(kid.Id, cfg.GetActiveMember().Id);
    }

    [Fact]
    public void Delete_secondary_removes_it_and_resets_active_to_primary()
    {
        var cfg = New();
        var kid = cfg.AddMember("Kid");
        cfg.SetActiveMemberId(kid.Id);

        cfg.DeleteMember(kid.Id);
        Assert.DoesNotContain(cfg.ListMembers(), m => m.Id == kid.Id);
        Assert.NotEqual(kid.Id, cfg.GetActiveMember().Id); // active fell back off the deleted member
    }

    [Fact]
    public void Master_cannot_be_deleted_even_as_the_last_member()
    {
        var cfg = New();
        var master = cfg.GetMasterMember();

        var ex = Assert.Throws<InvalidOperationException>(() => cfg.DeleteMember(master.Id));
        Assert.Contains("master", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Reduce back down to just the master, then confirm the guard still holds (the count-<=1 guard is
        // defensive: a household always keeps a master, so the master check shadows it in practice).
        var kid = cfg.AddMember("Kid");
        cfg.DeleteMember(kid.Id);
        Assert.Single(cfg.ListMembers());
        Assert.Throws<InvalidOperationException>(() => cfg.DeleteMember(master.Id));
    }

    // member_requests rows attribute picks by memberId with no DB FK — a reused id would silently hand a
    // deleted member's pick history to the newcomer. NextMemberId must stay monotonic across deletes.
    [Fact]
    public void Add_after_delete_does_not_reuse_the_deleted_id()
    {
        var cfg = New();
        var kid = cfg.AddMember("Kid");
        cfg.DeleteMember(kid.Id);

        var next = cfg.AddMember("New Kid");

        Assert.Equal(kid.Id + 1, next.Id); // id 3, never 2 again
        Assert.DoesNotContain(cfg.ListMembers(), m => m.Id == kid.Id);
    }

    [Fact]
    public void Rename_rejects_a_blank_name()
    {
        var cfg = New();
        var kid = cfg.AddMember("Kid");
        Assert.Throws<ArgumentException>(() => cfg.RenameMember(kid.Id, "   "));
        Assert.Equal("Kid", cfg.GetMember(kid.Id)!.Name); // unchanged
    }
}
