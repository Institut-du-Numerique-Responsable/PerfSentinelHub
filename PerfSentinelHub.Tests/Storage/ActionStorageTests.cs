using System.Reflection;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Storage;

public sealed class ActionStorageTests(HubApplicationFactory factory) : IClassFixture<HubApplicationFactory>
{
    private HubDatabase Database => factory.Database;

    [Fact]
    public async Task Events_are_appended_never_rewritten()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string signature = "appended";

        await Database.CreateActionAsync(signature, ActionStatuses.Todo, null, null, "alice", 100, cancellationToken);
        await Database.UpdateActionAsync(signature, ActionStatuses.InProgress, "bob", true, "alice", 200,
            cancellationToken);
        await Database.UpdateActionAsync(signature, ActionStatuses.Done, null, false, "bob", 300, cancellationToken);

        var action = Assert.Single(await Database.ReadActionsAsync([signature], cancellationToken));
        Assert.Equal(
            [
                new StoredActionEvent(null, ActionStatuses.Todo, null, null, "alice", 100),
                new StoredActionEvent(ActionStatuses.Todo, ActionStatuses.InProgress, "bob", null, "alice", 200),
                new StoredActionEvent(ActionStatuses.InProgress, ActionStatuses.Done, "bob", null, "bob", 300)
            ],
            action.Events);
        Assert.Equal(ActionStatuses.Done, action.Status);
        Assert.Equal("bob", action.Owner);
        Assert.Equal("alice", action.CreatedBy);
        Assert.Equal(100, action.CreatedAtMs);
        Assert.Equal(300, action.UpdatedAtMs);
        Assert.Equal(300, action.DoneAtMs);
    }

    [Fact]
    public async Task A_take_stores_the_display_name()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string signature = "display-take";

        await Database.CreateActionAsync(signature, ActionStatuses.InProgress, "alice@acme.com", "Alice Martin",
            "alice@acme.com", 100, cancellationToken);

        var action = Assert.Single(await Database.ReadActionsAsync([signature], cancellationToken));
        Assert.Equal("alice@acme.com", action.Owner);
        Assert.Equal("Alice Martin", action.OwnerDisplay);
        Assert.Equal(
            new StoredActionEvent(null, ActionStatuses.InProgress, "alice@acme.com", "Alice Martin", "alice@acme.com",
                100),
            Assert.Single(action.Events));
    }

    [Fact]
    public async Task A_typed_owner_clears_the_display_name()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string signature = "display-typed";
        await Database.CreateActionAsync(signature, ActionStatuses.InProgress, "alice@acme.com", "Alice Martin",
            "alice@acme.com", 100, cancellationToken);

        await Database.UpdateActionAsync(signature, null, "Payments team", true, "alice@acme.com", 200,
            cancellationToken);

        var action = Assert.Single(await Database.ReadActionsAsync([signature], cancellationToken));
        Assert.Equal("Payments team", action.Owner);
        Assert.Null(action.OwnerDisplay);
        Assert.Null(action.Events[1].OwnerDisplay);
    }

    [Fact]
    public async Task A_status_change_or_the_same_owner_keeps_the_display_name()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string signature = "display-kept";
        await Database.CreateActionAsync(signature, ActionStatuses.InProgress, "alice@acme.com", "Alice Martin",
            "alice@acme.com", 100, cancellationToken);

        await Database.UpdateActionAsync(signature, null, "alice@acme.com", true, "bob", 150, cancellationToken);
        await Database.UpdateActionAsync(signature, ActionStatuses.Done, null, false, "bob", 200, cancellationToken);

        var action = Assert.Single(await Database.ReadActionsAsync([signature], cancellationToken));
        Assert.Equal("Alice Martin", action.OwnerDisplay);
        Assert.Equal(2, action.Events.Count);
        Assert.Equal("Alice Martin", action.Events[1].OwnerDisplay);
    }

    [Fact]
    public async Task An_unchanged_patch_adds_no_event()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string signature = "unchanged";
        await Database.CreateActionAsync(signature, ActionStatuses.InProgress, "alice", null, "alice", 100,
            cancellationToken);

        var outcome = await Database.UpdateActionAsync(signature, ActionStatuses.InProgress, "alice", true, "bob",
            200, cancellationToken);

        Assert.Equal(ActionWriteOutcome.Written, outcome);
        var action = Assert.Single(await Database.ReadActionsAsync([signature], cancellationToken));
        Assert.Single(action.Events);
        Assert.Equal(100, action.UpdatedAtMs);
    }

    [Fact]
    public async Task A_second_create_is_refused_and_a_missing_update_says_so()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string signature = "twice";
        await Database.CreateActionAsync(signature, ActionStatuses.Todo, null, null, "alice", 100, cancellationToken);

        Assert.Equal(ActionWriteOutcome.Exists,
            await Database.CreateActionAsync(signature, ActionStatuses.Done, null, null, "bob", 200,
                cancellationToken));
        Assert.Equal(ActionWriteOutcome.Missing,
            await Database.UpdateActionAsync("never", ActionStatuses.Done, null, false, "bob", 200,
                cancellationToken));
        Assert.Single(Assert.Single(await Database.ReadActionsAsync([signature], cancellationToken)).Events);
    }

    [Fact]
    public async Task Leaving_done_clears_the_stamp_and_a_clearing_patch_drops_the_owner()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string signature = "leaving";
        await Database.CreateActionAsync(signature, ActionStatuses.Done, "alice", null, "alice", 100,
            cancellationToken);

        await Database.UpdateActionAsync(signature, ActionStatuses.InProgress, null, true, "bob", 200,
            cancellationToken);

        var action = Assert.Single(await Database.ReadActionsAsync([signature], cancellationToken));
        Assert.Null(action.DoneAtMs);
        Assert.Null(action.Owner);
        Assert.Equal(2, action.Events.Count);
    }

    [Fact]
    public async Task Reading_several_signatures_returns_only_those_with_an_action()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await Database.CreateActionAsync("read-a", ActionStatuses.Todo, null, null, "alice", 100, cancellationToken);
        await Database.CreateActionAsync("read-b", ActionStatuses.Todo, null, null, "alice", 100, cancellationToken);

        var actions = await Database.ReadActionsAsync(["read-a", "read-b", "read-none"], cancellationToken);

        Assert.Equal(["read-a", "read-b"], actions.Select(action => action.Signature).Order(StringComparer.Ordinal));
        Assert.Empty(await Database.ReadActionsAsync([], cancellationToken));
    }

    [Fact]
    public async Task A_held_write_lock_answers_busy()
    {
        // No public seam holds the write lock, and a write only gives up after WriteGateWait (5 s).
        var cancellationToken = TestContext.Current.CancellationToken;
        var writeGate = (SemaphoreSlim)typeof(HubDatabase)
            .GetField("_writeGate", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(Database)!;
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            Assert.Equal(ActionWriteOutcome.Busy,
                await Database.CreateActionAsync("busy", ActionStatuses.Todo, null, null, "alice", 100,
                    cancellationToken));
            Assert.Equal(ActionWriteOutcome.Busy,
                await Database.UpdateActionAsync("busy", ActionStatuses.Done, null, false, "alice", 100,
                    cancellationToken));
        }
        finally
        {
            writeGate.Release();
        }

        Assert.Empty(await Database.ReadActionsAsync(["busy"], cancellationToken));
    }

    [Fact]
    public async Task Finding_copies_carry_each_copy_s_own_service_and_attribute_rows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string signature = "copies:cart:1";
        await Database.UpsertBatchAsync(new SourceSnapshot("copy-a", "copy-a", "prod", "0.26.2"), FindingJson.Batch(
                FindingJson.Envelope(signature, service: "cart", grouping: """[{"key":"team","value":"shop"}]""")),
            1, cancellationToken);
        await Database.UpsertBatchAsync(new SourceSnapshot("copy-b", "copy-b", "lab", "0.26.2"), FindingJson.Batch(
            FindingJson.Envelope(signature, service: "basket")), 1, cancellationToken);

        var copies = (await Database.ReadFindingCopiesAsync(signature, cancellationToken))
            .OrderBy(copy => copy.Source, StringComparer.Ordinal).ToList();

        Assert.Equal(["copy-a", "copy-b"], copies.Select(copy => copy.Source));
        Assert.Equal(["cart", "basket"], copies.Select(copy => copy.Service));
        Assert.Equal("shop", Assert.Single(copies[0].Rows).Value);
        // A copy with no grouping carries the empty marker row, which matches no key.
        Assert.Equal("", Assert.Single(copies[1].Rows).Key);
        Assert.Empty(await Database.ReadFindingCopiesAsync("copies:none", cancellationToken));
    }
}
