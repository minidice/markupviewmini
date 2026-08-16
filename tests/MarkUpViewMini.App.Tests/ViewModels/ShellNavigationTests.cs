using System.Text;
using MarkUpViewMini.App.Services;
using MarkUpViewMini.App.ViewModels;
using MarkUpViewMini.App.Web;
using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Core.Navigation;
using MarkUpViewMini.Core.Search;
using MarkUpViewMini.Core.Workspace;
using MarkUpViewMini.Infrastructure.Folders;

namespace MarkUpViewMini.App.Tests.ViewModels;

public sealed class ShellNavigationTests
{
    [Fact]
    public async Task Search_match_uses_current_tab_policy_and_requested_line()
    {
        // Break caught: a default search result can open a new tab or lose its source line.
        var activations = new List<ActivationSnapshot>();
        using var shell = CreateShell(activations: activations);
        await shell.OpenAsync(Target(@"C:\Docs\first.md"), OpenGesture.Normal, default);

        await shell.GoToSearchMatchAsync(
            new SearchMatch(Guid.NewGuid(), @"C:\Docs\chapter.md", 27, "match", 0, 5),
            LinkOpenDisposition.Default,
            default);

        var tab = Assert.Single(shell.Tabs);
        Assert.Equal(@"C:\Docs\chapter.md", tab.Path);
        Assert.Equal(27, tab.TargetLine);
        Assert.Equal(27, activations[^1].Line);
    }

    [Fact]
    public async Task New_tab_search_disposition_creates_a_tab_at_the_requested_line()
    {
        // Break caught: Ctrl-activated search results can replace the active tab or drop their line.
        using var shell = CreateShell();
        await shell.OpenAsync(Target(@"C:\Docs\first.md"), OpenGesture.Normal, default);

        await shell.GoToSearchMatchAsync(
            new SearchMatch(Guid.NewGuid(), @"C:\Docs\chapter.md", 31, "match", 0, 5),
            LinkOpenDisposition.NewTab,
            default);

        Assert.Equal(2, shell.Tabs.Count);
        Assert.Equal(@"C:\Docs\chapter.md", shell.ActiveTab!.Path);
        Assert.Equal(31, shell.ActiveTab.TargetLine);
    }

    [Fact]
    public async Task Back_and_forward_restore_without_recording_duplicate_history()
    {
        // Break caught: restoring history can push itself and destroy the expected cursor position.
        using var shell = CreateShell();
        await shell.OpenAsync(Target(@"C:\Docs\first.md"), OpenGesture.Normal, default);
        await shell.OpenAsync(Target(@"C:\Docs\second.md"), OpenGesture.Normal, default);

        await shell.GoBackAsync(default);

        Assert.Equal(@"C:\Docs\first.md", shell.ActiveTab!.Path);
        Assert.False(shell.ActiveTab.NavigationHistory.CanMoveBack);
        Assert.True(shell.ActiveTab.NavigationHistory.CanMoveForward);

        await shell.GoForwardAsync(default);

        Assert.Equal(@"C:\Docs\second.md", shell.ActiveTab.Path);
        Assert.True(shell.ActiveTab.NavigationHistory.CanMoveBack);
        Assert.False(shell.ActiveTab.NavigationHistory.CanMoveForward);
    }

    [Fact]
    public async Task Opening_after_back_drops_the_forward_history_branch()
    {
        // Break caught: a new navigation after Back can retain an abandoned forward destination.
        using var shell = CreateShell();
        await shell.OpenAsync(Target(@"C:\Docs\first.md"), OpenGesture.Normal, default);
        await shell.OpenAsync(Target(@"C:\Docs\second.md"), OpenGesture.Normal, default);
        await shell.GoBackAsync(default);

        await shell.OpenAsync(Target(@"C:\Docs\replacement.md"), OpenGesture.Normal, default);

        Assert.False(shell.CanGoForward);
        await shell.GoBackAsync(default);
        Assert.Equal(@"C:\Docs\first.md", shell.ActiveTab!.Path);
    }

    [Fact]
    public async Task Failed_back_restoration_rolls_the_history_cursor_forward()
    {
        // Break caught: a failed Back load can leave the cursor claiming a document that never activated.
        var failFirstOnRestore = false;
        using var shell = CreateShell(load: (path, _, _) =>
            failFirstOnRestore && path.EndsWith("first.md", StringComparison.OrdinalIgnoreCase)
                ? Task.FromException<LoadedDocument>(new FileNotFoundException("missing"))
                : Task.FromResult(Loaded(path)));
        await shell.OpenAsync(Target(@"C:\Docs\first.md"), OpenGesture.Normal, default);
        await shell.OpenAsync(Target(@"C:\Docs\second.md"), OpenGesture.Normal, default);
        failFirstOnRestore = true;

        await shell.GoBackAsync(default);

        Assert.True(shell.ActiveTab!.NavigationHistory.CanMoveBack);
        Assert.False(shell.ActiveTab.NavigationHistory.CanMoveForward);
        Assert.NotNull(shell.ActiveTab.Error);
    }

    [Fact]
    public async Task Root_follows_only_a_successful_out_of_root_document_in_follow_mode()
    {
        // Break caught: KeepRoot can move, or FollowCurrentDocument can move before an out-of-root load succeeds.
        using var keepSidebar = CreateSidebar(@"C:\Docs", RootFollowMode.KeepRoot);
        using var keepShell = CreateShell(sidebar: keepSidebar);
        await keepShell.OpenAsync(Target(@"D:\Elsewhere\guide.md"), OpenGesture.Normal, default);
        Assert.Equal(@"C:\Docs", keepSidebar.RootPath);

        using var followSidebar = CreateSidebar(@"C:\Docs", RootFollowMode.FollowCurrentDocument);
        using var followShell = CreateShell(sidebar: followSidebar);
        await followShell.OpenAsync(Target(@"D:\Elsewhere\guide.md"), OpenGesture.Normal, default);
        Assert.Equal(@"D:\Elsewhere", followSidebar.RootPath);

        using var failedSidebar = CreateSidebar(@"C:\Docs", RootFollowMode.FollowCurrentDocument);
        using var failedShell = CreateShell(
            load: (_, _, _) => Task.FromException<LoadedDocument>(new FileNotFoundException("missing")),
            sidebar: failedSidebar);
        await failedShell.OpenAsync(Target(@"D:\Failed\guide.md"), OpenGesture.Normal, default);
        Assert.Equal(@"C:\Docs", failedSidebar.RootPath);
    }

    [Fact]
    public async Task First_successful_document_initializes_the_keep_root_sidebar_folder()
    {
        // Break caught: KeepRoot can retain a null root forever, leaving the composed tree and search UI empty.
        using var sidebar = CreateSidebar(root: null, RootFollowMode.KeepRoot);
        using var shell = CreateShell(sidebar: sidebar);

        await shell.OpenAsync(Target(@"C:\Docs\guide.md"), OpenGesture.Normal, CancellationToken.None);

        Assert.Equal(@"C:\Docs", sidebar.RootPath);
        Assert.Equal(RootFollowMode.KeepRoot, sidebar.RootMode);
    }

    [Fact]
    public async Task Same_document_outline_jump_uses_typed_line_message_and_records_history()
    {
        // Break caught: an outline click can reload the document, bypass the typed WebView command, or omit history.
        var loaded = 0;
        var lines = new List<int>();
        using var shell = CreateShell(
            load: (path, _, _) =>
            {
                loaded++;
                return Task.FromResult(Loaded(path));
            },
            goToLine: (line, _) =>
            {
                lines.Add(line);
                return Task.CompletedTask;
            });
        await shell.OpenAsync(Target(@"C:\Docs\guide.md"), OpenGesture.Normal, default);

        await shell.GoToOutlineAsync(new OutlineItemViewModel(2, "Install", "install", 18), default);

        Assert.Equal(1, loaded);
        Assert.Equal([18], lines);
        Assert.True(shell.ActiveTab!.NavigationHistory.CanMoveBack);
    }

    [Fact]
    public async Task Routed_links_use_only_internal_navigation_or_external_boundary()
    {
        // Break caught: Shell can bypass LinkRoutingService, load an external target, or externally launch Markdown.
        var external = new List<LinkRoute>();
        using var shell = CreateShell(externalOpen: route =>
        {
            external.Add(route);
            return new ExternalOpenResult(true, null);
        });
        await shell.OpenAsync(Target(@"C:\Docs\guide.md"), OpenGesture.Normal, default);

        await shell.OpenLinkAsync("chapter.md#install", LinkOpenDisposition.Default, default);
        await shell.OpenLinkAsync("https://example.com/docs", LinkOpenDisposition.Default, default);

        Assert.Equal(@"C:\Docs\chapter.md", shell.ActiveTab!.Path);
        Assert.Equal("install", shell.ActiveTab.TargetAnchor);
        var route = Assert.Single(external);
        Assert.Equal(LinkRouteKind.DefaultBrowser, route.Kind);
        Assert.Equal("https://example.com/docs", route.Target);
    }

    [Fact]
    public async Task Failed_external_launch_is_exposed_as_a_nonblocking_navigation_error()
    {
        // Break caught: discarding ExternalOpenResult turns a browser/associated-app failure into an unexplained no-op.
        using var shell = CreateShell(externalOpen: _ => new ExternalOpenResult(false, "No associated application."));
        await shell.OpenAsync(Target(@"C:\Docs\guide.md"), OpenGesture.Normal, CancellationToken.None);

        await shell.OpenLinkAsync("https://example.com", LinkOpenDisposition.Default, CancellationToken.None);

        Assert.True(shell.HasNavigationError);
        Assert.Equal("No associated application.", shell.NavigationErrorMessage);
        shell.ClearNavigationError();
        Assert.False(shell.HasNavigationError);
    }

    [Theory]
    [InlineData("chapter.md", true, true)]
    [InlineData("attachment.pdf", false, false)]
    [InlineData("https://example.com", false, false)]
    public async Task Link_context_menu_enables_internal_choices_only_for_registered_documents(
        string target,
        bool canOpenInternal,
        bool canOpenNewTab)
    {
        // Break caught: context-menu internal commands can bypass format registration or disable supported documents.
        using var shell = CreateShell();
        await shell.OpenAsync(Target(@"C:\Docs\guide.md"), OpenGesture.Normal, CancellationToken.None);

        var state = shell.GetLinkContextMenuState(target);

        Assert.True(state.CanOpenDefault);
        Assert.Equal(canOpenInternal, state.CanOpenInternal);
        Assert.True(state.CanOpenWithWindows);
        Assert.Equal(canOpenNewTab, state.CanOpenNewTab);
    }

    [Fact]
    public async Task Link_context_menu_state_and_selection_reject_a_stale_document_owner()
    {
        // Break caught: leaving a context menu open across activation can route its old relative target from the new document.
        var external = new List<LinkRoute>();
        using var shell = CreateShell(externalOpen: route =>
        {
            external.Add(route);
            return new ExternalOpenResult(true, null);
        });
        await shell.OpenAsync(Target(@"C:\Docs\first.md"), OpenGesture.Normal, CancellationToken.None);
        var stale = new LinkContextMenuMessage(Owner(shell), "https://example.com/stale");
        await shell.OpenAsync(Target(@"C:\Docs\second.md"), OpenGesture.Normal, CancellationToken.None);

        Assert.False(shell.TryGetLinkContextMenuState(stale, out _));
        await shell.HandleLinkContextMenuSelectionAsync(
            stale,
            LinkOpenDisposition.Default,
            CancellationToken.None);

        Assert.Empty(external);
        Assert.Equal(@"C:\Docs\second.md", shell.ActiveTab!.Path);
    }

    [Fact]
    public async Task Typed_outline_and_link_handlers_reject_stale_or_reentrant_owners()
    {
        // Break caught: a delayed outline/link event from a stale revision can mutate or navigate the new active document.
        using var sidebar = CreateSidebar(@"C:\Docs", RootFollowMode.KeepRoot);
        var external = new List<LinkRoute>();
        using var shell = CreateShell(
            sidebar: sidebar,
            externalOpen: route =>
            {
                external.Add(route);
                return new ExternalOpenResult(true, null);
            });
        await shell.OpenAsync(Target(@"C:\Docs\first.md"), OpenGesture.Normal, default);
        var staleOwner = Owner(shell);
        await shell.OpenAsync(Target(@"C:\Docs\second.md"), OpenGesture.Normal, default);

        shell.HandleOutline(new DocumentOutlineMessage(
            staleOwner,
            [new WebOutlineItem(1, "Stale", "stale", 1)]));
        await shell.HandleLinkOpenAsync(
            new LinkOpenMessage(staleOwner, "https://example.com/stale", LinkOpenDisposition.Default),
            default);

        Assert.Empty(sidebar.Outline);
        Assert.Empty(external);
        Assert.Equal(@"C:\Docs\second.md", shell.ActiveTab!.Path);
    }

    [Fact]
    public async Task Background_failure_does_not_clear_the_selected_document_outline()
    {
        // Break caught: a background tab's late load failure can clear the selected tab's current outline.
        var background = new TaskCompletionSource<LoadedDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sidebar = CreateSidebar(@"C:\Docs", RootFollowMode.KeepRoot);
        using var shell = CreateShell(
            load: (path, _, _) => path.EndsWith("background.md", StringComparison.OrdinalIgnoreCase)
                ? background.Task
                : Task.FromResult(Loaded(path)),
            sidebar: sidebar);
        await shell.OpenAsync(Target(@"C:\Docs\selected.md"), OpenGesture.Normal, default);
        var selected = shell.ActiveTab!;
        shell.HandleOutline(new DocumentOutlineMessage(
            Owner(shell),
            [new WebOutlineItem(1, "Selected", "selected", 1)]));
        var backgroundOpen = shell.OpenAsync(
            Target(@"C:\Docs\background.md"),
            OpenGesture.ExplicitNewTab,
            default);
        await shell.ActivateAsync(selected, default);
        shell.HandleOutline(new DocumentOutlineMessage(
            Owner(shell),
            [new WebOutlineItem(1, "Selected", "selected", 1)]));

        background.SetException(new FileNotFoundException("missing"));
        await backgroundOpen;

        Assert.Equal("Selected", Assert.Single(sidebar.Outline).Text);
    }

    [Fact]
    public async Task Reentrant_navigation_from_history_notification_supersedes_the_stale_back()
    {
        // Break caught: Back can resume after its notification synchronously starts a newer same-tab open.
        using var shell = CreateShell();
        await shell.OpenAsync(Target(@"C:\Docs\first.md"), OpenGesture.Normal, default);
        await shell.OpenAsync(Target(@"C:\Docs\second.md"), OpenGesture.Normal, default);
        var reentered = false;
        shell.PropertyChanged += (_, args) =>
        {
            if (!reentered && args.PropertyName == nameof(ShellViewModel.CanGoBack))
            {
                reentered = true;
                shell.OpenAsync(Target(@"C:\Docs\newest.md"), OpenGesture.Normal, default)
                    .GetAwaiter()
                    .GetResult();
            }
        };

        await shell.GoBackAsync(default);

        Assert.Equal(@"C:\Docs\newest.md", shell.ActiveTab!.Path);
        Assert.False(shell.CanGoForward);
    }

    [Fact]
    public async Task Later_outline_jump_wins_when_an_earlier_command_completes_last()
    {
        // Break caught: an older delayed line command can overwrite a newer jump in the same tab revision.
        var firstCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        using var shell = CreateShell(goToLine: (_, _) =>
        {
            callCount++;
            return callCount == 1 ? firstCompletion.Task : Task.CompletedTask;
        });
        await shell.OpenAsync(Target(@"C:\Docs\guide.md"), OpenGesture.Normal, default);

        var earlier = shell.GoToOutlineAsync(new OutlineItemViewModel(1, "Earlier", "earlier", 10), default);
        await shell.GoToOutlineAsync(new OutlineItemViewModel(1, "Latest", "latest", 20), default);
        firstCompletion.SetResult();
        await earlier;

        Assert.Equal(20, shell.ActiveTab!.TargetLine);
        Assert.Equal("latest", shell.ActiveTab.TargetAnchor);
    }

    [Fact]
    public async Task Cancelled_back_restoration_rolls_the_history_cursor_forward()
    {
        // Break caught: caller cancellation after moving Back can leave the tab emptied/mutated while history rolls forward.
        var restore = false;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shell = CreateShell(load: async (path, _, cancellationToken) =>
        {
            if (restore && path.EndsWith("first.md", StringComparison.OrdinalIgnoreCase))
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return Loaded(path);
        });
        await shell.OpenAsync(Target(@"C:\Docs\first.md"), OpenGesture.Normal, default);
        await shell.OpenAsync(
            new DocumentTarget(@"C:\Docs\second.md", 44, "current"),
            OpenGesture.Normal,
            default);
        var original = shell.ActiveTab!;
        var originalText = original.Text;
        var originalRevision = original.Revision;
        restore = true;
        using var cancellation = new CancellationTokenSource();

        var back = shell.GoBackAsync(cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => back);

        Assert.True(shell.CanGoBack);
        Assert.False(shell.CanGoForward);
        Assert.Same(original, shell.ActiveTab);
        Assert.Equal(@"C:\Docs\second.md", original.Path);
        Assert.Equal(originalText, original.Text);
        Assert.Equal(originalRevision, original.Revision);
        Assert.Equal(44, original.TargetLine);
        Assert.Equal("current", original.TargetAnchor);
        Assert.Null(original.Error);
    }

    [Fact]
    public async Task Cancelled_back_after_tab_switch_restores_the_inactive_tab_snapshot()
    {
        // Break caught: cancellation after switching tabs can roll history forward while leaving the owner emptied/mutated.
        var restore = false;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shell = CreateShell(load: async (path, _, cancellationToken) =>
        {
            if (restore && path.EndsWith("first.md", StringComparison.OrdinalIgnoreCase))
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return path.EndsWith("second.md", StringComparison.OrdinalIgnoreCase)
                ? new LoadedDocument(
                    "second snapshot",
                    new EncodingDescriptor("utf-16", true),
                    NewLineKind.CrLf,
                    "\r\n",
                    new DiskFileVersion(42, DateTime.UnixEpoch.AddDays(2), new string('b', 64)))
                : Loaded(path);
        });
        await shell.OpenAsync(Target(@"C:\Docs\first.md"), OpenGesture.Normal, default);
        await shell.OpenAsync(
            new DocumentTarget(@"C:\Docs\second.md", 44, "current"),
            OpenGesture.Normal,
            default);
        var owner = shell.ActiveTab!;
        restore = true;
        using var cancellation = new CancellationTokenSource();

        var back = shell.GoBackAsync(cancellation.Token);
        await started.Task;
        await shell.OpenAsync(Target(@"E:\Selected\active.md"), OpenGesture.ExplicitNewTab, default);
        var selected = shell.ActiveTab!;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => back);

        Assert.Same(selected, shell.ActiveTab);
        Assert.Equal(@"E:\Selected\active.md", selected.Path);
        Assert.Equal("loaded:active.md", selected.Text);
        Assert.Equal(1, selected.Revision);
        Assert.Null(selected.TargetLine);
        Assert.Null(selected.TargetAnchor);
        Assert.Null(selected.Error);
        Assert.Equal(@"C:\Docs\second.md", owner.Path);
        Assert.Equal("second snapshot", owner.Text);
        Assert.Equal(2, owner.Revision);
        Assert.Equal(44, owner.TargetLine);
        Assert.Equal("current", owner.TargetAnchor);
        Assert.Equal(new EncodingDescriptor("utf-16", true), owner.Encoding);
        Assert.Equal(NewLineKind.CrLf, owner.NewLine);
        Assert.Equal("\r\n", owner.PreferredNewLine);
        Assert.Equal(
            new DiskFileVersion(42, DateTime.UnixEpoch.AddDays(2), new string('b', 64)),
            owner.DiskVersion);
        Assert.Null(owner.Error);
        Assert.True(owner.NavigationHistory.CanMoveBack);
        Assert.False(owner.NavigationHistory.CanMoveForward);
    }

    [Fact]
    public async Task Failed_back_after_tab_switch_restores_the_inactive_tab_snapshot()
    {
        // Break caught: a failed background history load can leave its owner mutated even after cursor rollback.
        var restore = false;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failedLoad = new TaskCompletionSource<LoadedDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shell = CreateShell(load: async (path, _, _) =>
        {
            if (restore && path.EndsWith("first.md", StringComparison.OrdinalIgnoreCase))
            {
                started.SetResult();
                return await failedLoad.Task;
            }

            return path.EndsWith("second.md", StringComparison.OrdinalIgnoreCase)
                ? new LoadedDocument(
                    "second snapshot",
                    new EncodingDescriptor("utf-16", true),
                    NewLineKind.CrLf,
                    "\r\n",
                    new DiskFileVersion(42, DateTime.UnixEpoch.AddDays(2), new string('b', 64)))
                : Loaded(path);
        });
        await shell.OpenAsync(Target(@"C:\Docs\first.md"), OpenGesture.Normal, default);
        await shell.OpenAsync(
            new DocumentTarget(@"C:\Docs\second.md", 44, "current"),
            OpenGesture.Normal,
            default);
        var owner = shell.ActiveTab!;
        restore = true;

        var back = shell.GoBackAsync(default);
        await started.Task;
        await shell.OpenAsync(Target(@"E:\Selected\active.md"), OpenGesture.ExplicitNewTab, default);
        var selected = shell.ActiveTab!;
        failedLoad.SetException(new IOException("missing"));
        await back;

        Assert.Same(selected, shell.ActiveTab);
        Assert.Equal(@"E:\Selected\active.md", selected.Path);
        Assert.Equal("loaded:active.md", selected.Text);
        Assert.Equal(1, selected.Revision);
        Assert.Null(selected.TargetLine);
        Assert.Null(selected.TargetAnchor);
        Assert.Null(selected.Error);
        Assert.Equal(@"C:\Docs\second.md", owner.Path);
        Assert.Equal("second snapshot", owner.Text);
        Assert.Equal(2, owner.Revision);
        Assert.Equal(44, owner.TargetLine);
        Assert.Equal("current", owner.TargetAnchor);
        Assert.Equal(new EncodingDescriptor("utf-16", true), owner.Encoding);
        Assert.Equal(NewLineKind.CrLf, owner.NewLine);
        Assert.Equal("\r\n", owner.PreferredNewLine);
        Assert.Equal(
            new DiskFileVersion(42, DateTime.UnixEpoch.AddDays(2), new string('b', 64)),
            owner.DiskVersion);
        Assert.Null(owner.Error);
        Assert.True(owner.NavigationHistory.CanMoveBack);
        Assert.False(owner.NavigationHistory.CanMoveForward);
    }

    [Fact]
    public async Task Back_to_a_positionless_document_entry_reloads_the_top()
    {
        // Break caught: restoring an entry with no line or anchor can emit no command and leave the old scroll target visible.
        var loadCount = 0;
        using var shell = CreateShell(
            load: (path, _, _) =>
            {
                loadCount++;
                return Task.FromResult(Loaded(path));
            },
            goToLine: (_, _) => Task.CompletedTask);
        await shell.OpenAsync(Target(@"C:\Docs\guide.md"), OpenGesture.Normal, default);
        await shell.GoToOutlineAsync(new OutlineItemViewModel(1, "Details", "details", 20), default);

        await shell.GoBackAsync(default);

        Assert.Equal(2, loadCount);
        Assert.Null(shell.ActiveTab!.TargetLine);
        Assert.Null(shell.ActiveTab.TargetAnchor);
    }

    [Fact]
    public async Task Superseding_outline_jump_survives_stale_history_cancellation()
    {
        // Break caught: a cancelled stale same-document Back can roll its target over a newer winning jump.
        var sendCount = 0;
        var staleSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shell = CreateShell(goToLine: (_, _) =>
        {
            sendCount++;
            return sendCount == 3 ? staleSend.Task : Task.CompletedTask;
        });
        await shell.OpenAsync(Target(@"C:\Docs\guide.md"), OpenGesture.Normal, default);
        await shell.GoToOutlineAsync(new OutlineItemViewModel(1, "Ten", "ten", 10), default);
        await shell.GoToOutlineAsync(new OutlineItemViewModel(1, "Twenty", "twenty", 20), default);

        var staleBack = shell.GoBackAsync(default);
        await shell.GoToOutlineAsync(new OutlineItemViewModel(1, "Newest", "newest", 30), default);
        staleSend.SetException(new OperationCanceledException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => staleBack);

        Assert.Equal(30, shell.ActiveTab!.TargetLine);
        Assert.Equal("newest", shell.ActiveTab.TargetAnchor);
    }

    [Fact]
    public async Task Back_and_forward_follow_successfully_restored_documents_across_roots()
    {
        // Break caught: history restoration can activate an out-of-root document without updating FollowCurrentDocument.
        using var sidebar = CreateSidebar(@"C:\RootA", RootFollowMode.FollowCurrentDocument);
        using var shell = CreateShell(sidebar: sidebar);
        await shell.OpenAsync(Target(@"C:\RootA\first.md"), OpenGesture.Normal, default);
        await shell.OpenAsync(Target(@"D:\RootB\second.md"), OpenGesture.Normal, default);
        Assert.Equal(@"D:\RootB", sidebar.RootPath);

        await shell.GoBackAsync(default);
        Assert.Equal(@"C:\RootA\first.md", shell.ActiveTab!.Path);
        Assert.Equal(@"C:\RootA", sidebar.RootPath);

        await shell.GoForwardAsync(default);
        Assert.Equal(@"D:\RootB\second.md", shell.ActiveTab.Path);
        Assert.Equal(@"D:\RootB", sidebar.RootPath);
    }

    [Fact]
    public async Task Background_history_completion_does_not_follow_the_sidebar_root()
    {
        // Break caught: a history load completing after tab switch can move the root away from the selected document.
        var restore = false;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restoredLoad = new TaskCompletionSource<LoadedDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sidebar = CreateSidebar(@"C:\RootA", RootFollowMode.FollowCurrentDocument);
        using var shell = CreateShell(
            load: async (path, _, _) =>
            {
                if (restore && path.EndsWith("first.md", StringComparison.OrdinalIgnoreCase))
                {
                    started.SetResult();
                    return await restoredLoad.Task;
                }

                return Loaded(path);
            },
            sidebar: sidebar);
        await shell.OpenAsync(Target(@"C:\RootA\first.md"), OpenGesture.Normal, default);
        await shell.OpenAsync(Target(@"D:\RootB\second.md"), OpenGesture.Normal, default);
        var owner = shell.ActiveTab!;
        restore = true;

        var back = shell.GoBackAsync(default);
        await started.Task;
        await shell.OpenAsync(Target(@"E:\RootC\selected.md"), OpenGesture.ExplicitNewTab, default);
        var selected = shell.ActiveTab!;
        Assert.Equal(@"E:\RootC", sidebar.RootPath);
        restoredLoad.SetResult(Loaded(@"C:\RootA\first.md"));
        await back;

        Assert.Same(selected, shell.ActiveTab);
        Assert.Equal(@"E:\RootC\selected.md", selected.Path);
        Assert.Equal(@"C:\RootA\first.md", owner.Path);
        Assert.Equal(@"E:\RootC", sidebar.RootPath);
    }

    [Fact]
    public async Task No_op_back_does_not_supersede_an_inflight_outline_jump()
    {
        // Break caught: disabled Back can advance navigation ownership and suppress a real in-flight jump commit.
        var send = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shell = CreateShell(goToLine: (_, _) => send.Task);
        await shell.OpenAsync(Target(@"C:\Docs\guide.md"), OpenGesture.Normal, default);

        var jump = shell.GoToOutlineAsync(new OutlineItemViewModel(1, "Details", "details", 20), default);
        await shell.GoBackAsync(default);
        send.SetResult();
        await jump;

        Assert.Equal(20, shell.ActiveTab!.TargetLine);
        Assert.Equal("details", shell.ActiveTab.TargetAnchor);
        Assert.True(shell.CanGoBack);
    }

    [Fact]
    public async Task Positionless_same_document_link_reloads_the_document_top()
    {
        // Break caught: a same-document link without line/anchor can record top while WebView remains at the old target.
        var loadCount = 0;
        using var shell = CreateShell(
            load: (path, _, _) =>
            {
                loadCount++;
                return Task.FromResult(Loaded(path));
            },
            goToLine: (_, _) => Task.CompletedTask);
        await shell.OpenAsync(Target(@"C:\Docs\guide.md"), OpenGesture.Normal, default);
        await shell.GoToOutlineAsync(new OutlineItemViewModel(1, "Details", "details", 20), default);

        await shell.OpenLinkAsync("guide.md", LinkOpenDisposition.Default, default);

        Assert.Equal(2, loadCount);
        Assert.Null(shell.ActiveTab!.TargetLine);
        Assert.Null(shell.ActiveTab.TargetAnchor);
    }

    [Fact]
    public async Task Cancellation_snapshot_restore_stops_after_reentrant_navigation()
    {
        // Break caught: snapshot PropertyChanged reentrancy can let stale restoration corrupt a newer same-tab load.
        var restore = false;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shell = CreateShell(load: async (path, _, cancellationToken) =>
        {
            if (restore && path.EndsWith("first.md", StringComparison.OrdinalIgnoreCase))
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return Loaded(path);
        });
        await shell.OpenAsync(Target(@"C:\Docs\first.md"), OpenGesture.Normal, default);
        await shell.OpenAsync(Target(@"C:\Docs\second.md"), OpenGesture.Normal, default);
        var tab = shell.ActiveTab!;
        var reentered = false;
        tab.PropertyChanged += (_, args) =>
        {
            if (!reentered &&
                args.PropertyName == nameof(DocumentTabViewModel.Path) &&
                tab.Path.EndsWith("second.md", StringComparison.OrdinalIgnoreCase))
            {
                reentered = true;
                shell.OpenAsync(Target(@"C:\Docs\newest.md"), OpenGesture.Normal, default)
                    .GetAwaiter()
                    .GetResult();
            }
        };
        restore = true;
        using var cancellation = new CancellationTokenSource();

        var back = shell.GoBackAsync(cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => back);

        Assert.Equal(@"C:\Docs\newest.md", tab.Path);
        Assert.Equal("loaded:newest.md", tab.Text);
        Assert.Equal(3, tab.Revision);
        Assert.Null(tab.Error);
    }

    private static ShellViewModel CreateShell(
        Func<string, Encoding?, CancellationToken, Task<LoadedDocument>>? load = null,
        List<ActivationSnapshot>? activations = null,
        SidebarViewModel? sidebar = null,
        Func<LinkRoute, ExternalOpenResult>? externalOpen = null,
        Func<int, CancellationToken, Task>? goToLine = null,
        Func<string, CancellationToken, Task>? goToAnchor = null)
    {
        App.RegisterEncodingProviders();
        var registry = Registry();
        return new ShellViewModel(
            registry,
            load ?? ((path, _, _) => Task.FromResult(Loaded(path))),
            (tab, _) =>
            {
                activations?.Add(new ActivationSnapshot(tab.Path, tab.Revision, tab.TargetLine, tab.TargetAnchor));
                return Task.CompletedTask;
            },
            deactivateDocument: null,
            sidebar,
            new LinkRoutingService(registry),
            externalOpen,
            goToLine,
            goToAnchor);
    }

    private static SidebarViewModel CreateSidebar(string? root, RootFollowMode mode)
    {
        var extensions = new HashSet<string>([".md", ".markdown"], StringComparer.OrdinalIgnoreCase);
        var sidebar = new SidebarViewModel(
            new FolderTreeService(),
            new EmptySearchService(),
            extensions,
            extensions,
            extensions,
            action => action());
        sidebar.RootPath = root;
        sidebar.RootMode = mode;
        return sidebar;
    }

    private static DocumentFormatRegistry Registry() =>
        new([new MarkdownDocumentProvider()]);

    private static DocumentTarget Target(string path) => new(Path.GetFullPath(path), null, null);

    private static WebMessageOwner Owner(ShellViewModel shell) =>
        new(Guid.NewGuid(), shell.WindowId, shell.ActiveTab!.Id, shell.ActiveTab.Revision);

    private static LoadedDocument Loaded(string path)
    {
        var text = $"loaded:{Path.GetFileName(path)}";
        return new LoadedDocument(
            text,
            new EncodingDescriptor("utf-8", false),
            NewLineKind.Lf,
            "\n",
            new DiskFileVersion(text.Length, DateTime.UnixEpoch, new string('a', 64)));
    }

    private sealed record ActivationSnapshot(string Path, long Revision, int? Line, string? Anchor);

    private sealed class EmptySearchService : IDocumentSearchService
    {
        public async IAsyncEnumerable<SearchEvent> SearchAsync(
            SearchQuery query,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
