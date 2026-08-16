using System.Text;
using MarkUpViewMini.App.ViewModels;
using MarkUpViewMini.App.Web;
using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Core.Navigation;
using MarkUpViewMini.Core.Workspace;
using MarkUpViewMini.Infrastructure.Files;

namespace MarkUpViewMini.App.Tests.ViewModels;

public sealed class ShellViewModelTests
{
    [Fact]
    public async Task First_open_creates_a_read_mode_tab()
    {
        var shell = CreateShell();

        await shell.OpenAsync(Target("first.md"), OpenGesture.Normal, CancellationToken.None);

        var tab = Assert.Single(shell.Tabs);
        Assert.Same(tab, shell.ActiveTab);
        Assert.Equal(DocumentMode.Read, tab.Mode);
        Assert.False(tab.IsDirty);
        Assert.Equal("loaded:first.md", tab.Text);
    }

    [Fact]
    public async Task Successful_load_projects_the_authoritative_buffer_metadata()
    {
        var version = new DiskFileVersion(
            12,
            DateTime.UnixEpoch.AddDays(1),
            new string('b', 64));
        var document = new LoadedDocument(
            "first\r\nsecond\nthird",
            new EncodingDescriptor("utf-8", true),
            NewLineKind.Mixed,
            "\r\n",
            version);
        var shell = CreateShell(load: (_, _, _) => Task.FromResult(document));

        await shell.OpenAsync(Target("buffer.md"), OpenGesture.Normal, CancellationToken.None);

        var tab = Assert.Single(shell.Tabs);
        Assert.NotNull(tab.Buffer);
        Assert.Equal(tab.Id, tab.Buffer.TabId);
        Assert.Equal(tab.Path, tab.Buffer.Path);
        Assert.Equal(document.Text, tab.Text);
        Assert.Equal(1, tab.Revision);
        Assert.False(tab.IsDirty);
        Assert.Equal(document.Encoding, tab.Encoding);
        Assert.Equal(document.NewLine, tab.NewLine);
        Assert.Equal(document.PreferredNewLine, tab.PreferredNewLine);
        Assert.Equal(version, tab.DiskVersion);
    }

    [Fact]
    public async Task Successful_load_preserves_projection_notification_order()
    {
        var pendingLoad = new TaskCompletionSource<LoadedDocument>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var shell = CreateShell(load: (_, _, _) => pendingLoad.Task);
        var opening = shell.OpenAsync(
            Target("notifications.md"),
            OpenGesture.Normal,
            CancellationToken.None);
        var tab = shell.ActiveTab!;
        var observed = new List<string?>();
        tab.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(DocumentTabViewModel.Text)
                or nameof(DocumentTabViewModel.Encoding)
                or nameof(DocumentTabViewModel.NewLine)
                or nameof(DocumentTabViewModel.PreferredNewLine)
                or nameof(DocumentTabViewModel.DiskVersion)
                or nameof(DocumentTabViewModel.Revision))
            {
                observed.Add(args.PropertyName);
            }
        };
        pendingLoad.SetResult(new LoadedDocument(
            "first\r\nsecond\nthird",
            new EncodingDescriptor("utf-8", true),
            NewLineKind.Mixed,
            "\r\n",
            new DiskFileVersion(20, DateTime.UnixEpoch, new string('c', 64))));

        await opening;

        Assert.Equal(
            [
                nameof(DocumentTabViewModel.Text),
                nameof(DocumentTabViewModel.Encoding),
                nameof(DocumentTabViewModel.NewLine),
                nameof(DocumentTabViewModel.PreferredNewLine),
                nameof(DocumentTabViewModel.DiskVersion),
                nameof(DocumentTabViewModel.Revision),
            ],
            observed);
    }

    [Fact]
    public void PrepareForLoad_preserves_projection_clear_notification_order()
    {
        var tab = new DocumentTabViewModel(Target("first.md"));
        tab.ApplyLoaded(Loaded("first.md"));
        tab.Buffer!.Apply(new DocumentEdit(
            tab.Revision,
            [new TextChange(tab.Text.Length, tab.Text.Length, "!")]));
        var observed = new List<string?>();
        tab.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(DocumentTabViewModel.Path)
                or nameof(DocumentTabViewModel.Buffer)
                or nameof(DocumentTabViewModel.Text)
                or nameof(DocumentTabViewModel.IsDirty)
                or nameof(DocumentTabViewModel.DisplayTitle)
                or nameof(DocumentTabViewModel.Encoding)
                or nameof(DocumentTabViewModel.DiskVersion))
            {
                observed.Add(args.PropertyName);
            }
        };

        tab.PrepareForLoad(Target("replacement.md"));

        Assert.Equal(
            [
                nameof(DocumentTabViewModel.Path),
                nameof(DocumentTabViewModel.DisplayTitle),
                nameof(DocumentTabViewModel.Buffer),
                nameof(DocumentTabViewModel.Text),
                nameof(DocumentTabViewModel.IsDirty),
                nameof(DocumentTabViewModel.DisplayTitle),
                nameof(DocumentTabViewModel.Encoding),
                nameof(DocumentTabViewModel.DiskVersion),
            ],
            observed);
    }

    [Fact]
    public async Task Normal_open_replaces_the_clean_active_tab()
    {
        var shell = CreateShell();
        await shell.OpenAsync(Target("first.md"), OpenGesture.Normal, CancellationToken.None);
        var originalId = shell.ActiveTab!.Id;

        await shell.OpenAsync(Target("second.markdown"), OpenGesture.Normal, CancellationToken.None);

        var tab = Assert.Single(shell.Tabs);
        Assert.Equal(originalId, tab.Id);
        Assert.Equal(Path.GetFullPath("second.markdown"), tab.Path);
        Assert.Equal(2, tab.Revision);
    }

    [Fact]
    public async Task Normal_open_creates_a_new_tab_when_the_active_tab_is_dirty()
    {
        var shell = CreateShell();
        await shell.OpenAsync(Target("first.md"), OpenGesture.Normal, CancellationToken.None);
        var first = shell.ActiveTab!;
        first.Buffer!.Apply(new DocumentEdit(
            first.Revision,
            [new TextChange(first.Text.Length, first.Text.Length, "!")]));

        await shell.OpenAsync(Target("second.md"), OpenGesture.Normal, CancellationToken.None);

        Assert.Equal(2, shell.Tabs.Count);
        Assert.Same(first, shell.Tabs[0]);
        Assert.NotSame(first, shell.ActiveTab);
        Assert.Equal(Path.GetFullPath("second.md"), shell.ActiveTab!.Path);
    }

    [Fact]
    public async Task Unsupported_format_is_rejected_before_loading_or_activation()
    {
        var loadCount = 0;
        var activationCount = 0;
        var shell = CreateShell(
            (path, encoding, cancellationToken) =>
            {
                loadCount++;
                return Task.FromResult(Loaded(path));
            },
            (tab, cancellationToken) =>
            {
                activationCount++;
                return Task.CompletedTask;
            });

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            shell.OpenAsync(Target("page.html"), OpenGesture.Normal, CancellationToken.None));

        Assert.Equal(0, loadCount);
        Assert.Equal(0, activationCount);
        Assert.Empty(shell.Tabs);
    }

    [Fact]
    public async Task Decoder_failure_requires_an_explicit_encoding_retry()
    {
        Encoding? attemptedEncoding = null;
        var loadCount = 0;
        var activationCount = 0;
        var shell = CreateShell(
            (path, encoding, cancellationToken) =>
            {
                loadCount++;
                attemptedEncoding = encoding;
                return loadCount == 1
                    ? Task.FromException<LoadedDocument>(new DecoderFallbackException("secret document bytes"))
                    : Task.FromResult(Loaded(path));
            },
            (tab, cancellationToken) =>
            {
                activationCount++;
                return Task.CompletedTask;
            });

        await shell.OpenAsync(Target("legacy.md"), OpenGesture.Normal, CancellationToken.None);

        Assert.True(shell.ActiveTab!.Error!.CanChooseEncoding);
        Assert.False(shell.ActiveTab.Error.CanRetry);
        Assert.Equal(0, activationCount);
        Assert.Null(attemptedEncoding);

        await shell.RetryWithEncodingAsync(Encoding.Unicode, CancellationToken.None);

        Assert.Same(Encoding.Unicode, attemptedEncoding);
        Assert.Null(shell.ActiveTab.Error);
        Assert.Equal(1, activationCount);
    }

    [Fact]
    public async Task Windows949_choice_reaches_the_strict_loader_and_activates_Korean_text()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            nameof(ShellViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "legacy.md");
        try
        {
            App.RegisterEncodingProviders();
            const string koreanText = "# 한글 문서\r\n본문입니다.";
            var service = new DocumentFileService();
            var windows949 = Encoding.GetEncoding(949);
            await File.WriteAllBytesAsync(path, windows949.GetBytes(koreanText));
            string? activatedText = null;
            var shell = CreateShell(
                (documentPath, encoding, cancellationToken) => encoding is null
                    ? service.LoadAsync(documentPath, cancellationToken)
                    : service.LoadAsync(documentPath, encoding, cancellationToken),
                (tab, cancellationToken) =>
                {
                    activatedText = tab.Text;
                    return Task.CompletedTask;
                });

            await shell.OpenAsync(
                new DocumentTarget(path, null, null),
                OpenGesture.Normal,
                CancellationToken.None);
            Assert.True(shell.ActiveTab!.Error?.CanChooseEncoding);

            var choice = Assert.Single(
                shell.EncodingSelection.Options,
                option => option.Encoding.CodePage == 949);
            shell.EncodingSelection.Selected = choice;
            await shell.RetryWithEncodingAsync(choice.Encoding, CancellationToken.None);

            Assert.Equal(koreanText, shell.ActiveTab.Text);
            Assert.Equal(koreanText, activatedText);
            Assert.DoesNotContain(
                shell.EncodingSelection.Options,
                option => option.Encoding.CodePage == Encoding.Latin1.CodePage);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(RetryableLoadFailures))]
    public async Task Missing_or_unauthorized_file_keeps_retryable_error_and_successful_retry_clears_it(
        Func<Exception> createFailure)
    {
        var loadCount = 0;
        var shell = CreateShell((path, encoding, cancellationToken) =>
        {
            loadCount++;
            return loadCount == 1
                ? Task.FromException<LoadedDocument>(createFailure())
                : Task.FromResult(Loaded(path));
        });

        await shell.OpenAsync(Target("retry.md"), OpenGesture.Normal, CancellationToken.None);

        var failedTab = Assert.Single(shell.Tabs);
        Assert.Same(failedTab, shell.ActiveTab);
        Assert.True(failedTab.Error!.CanRetry);
        Assert.Equal(0, failedTab.Revision);

        await shell.RetryAsync(CancellationToken.None);

        Assert.Same(failedTab, shell.ActiveTab);
        Assert.Null(failedTab.Error);
        Assert.Equal(1, failedTab.Revision);
        Assert.Equal("loaded:retry.md", failedTab.Text);
    }

    [Fact]
    public async Task Successful_load_activates_the_exact_tab_revision_and_navigation_context()
    {
        ActivationSnapshot? activation = null;
        var shell = CreateShell(
            activate: (tab, cancellationToken) =>
            {
                activation = new(
                    tab.Id,
                    tab.Path,
                    tab.Revision,
                    tab.TargetLine,
                    tab.TargetAnchor,
                    tab.Mode,
                    tab.Text);
                return Task.CompletedTask;
            });
        var target = new DocumentTarget(Path.GetFullPath("context.md"), 17, "details");

        await shell.OpenAsync(target, OpenGesture.ExplicitNewTab, CancellationToken.None);

        Assert.NotNull(activation);
        Assert.Equal(shell.ActiveTab!.Id, activation.TabId);
        Assert.Equal(target.Path, activation.Path);
        Assert.Equal(1, activation.Revision);
        Assert.Equal(17, activation.Line);
        Assert.Equal("details", activation.Anchor);
        Assert.Equal(DocumentMode.Read, activation.Mode);
        Assert.Equal("loaded:context.md", activation.Text);
    }

    [Fact]
    public async Task Slower_replaced_load_cannot_overwrite_the_newer_tab_target()
    {
        var firstLoad = new TaskCompletionSource<LoadedDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondLoad = new TaskCompletionSource<LoadedDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadCount = 0;
        var activations = new List<string>();
        var shell = CreateShell(
            (path, encoding, cancellationToken) =>
            {
                loadCount++;
                return loadCount == 1 ? firstLoad.Task : secondLoad.Task;
            },
            (tab, cancellationToken) =>
            {
                activations.Add(tab.Text);
                return Task.CompletedTask;
            });

        var earlierOpen = shell.OpenAsync(Target("earlier.md"), OpenGesture.Normal, CancellationToken.None);
        var laterOpen = shell.OpenAsync(Target("later.md"), OpenGesture.Normal, CancellationToken.None);
        secondLoad.SetResult(Loaded("later.md"));
        await laterOpen;
        firstLoad.SetResult(Loaded("earlier.md"));
        await earlierOpen;

        var tab = Assert.Single(shell.Tabs);
        Assert.Equal(Path.GetFullPath("later.md"), tab.Path);
        Assert.Equal("loaded:later.md", tab.Text);
        Assert.Equal(1, tab.Revision);
        Assert.Equal(["loaded:later.md"], activations);
    }

    [Fact]
    public async Task Background_load_applies_to_its_tab_without_replacing_the_selected_surface()
    {
        var backgroundLoad = new TaskCompletionSource<LoadedDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activations = new List<string>();
        var shell = CreateShell(
            (path, encoding, cancellationToken) =>
                Path.GetFileName(path) == "background.md"
                    ? backgroundLoad.Task
                    : Task.FromResult(Loaded(path)),
            (tab, cancellationToken) =>
            {
                activations.Add(Path.GetFileName(tab.Path));
                return Task.CompletedTask;
            });

        var backgroundOpen = shell.OpenAsync(Target("background.md"), OpenGesture.Normal, CancellationToken.None);
        var backgroundTab = shell.ActiveTab!;
        await shell.OpenAsync(Target("selected.md"), OpenGesture.ExplicitNewTab, CancellationToken.None);

        backgroundLoad.SetResult(Loaded("background.md"));
        await backgroundOpen;

        Assert.Equal("loaded:background.md", backgroundTab.Text);
        Assert.Equal("selected.md", Path.GetFileName(shell.ActiveTab!.Path));
        Assert.Equal(["selected.md"], activations);

        await shell.ActivateAsync(backgroundTab, CancellationToken.None);

        Assert.Equal(["selected.md", "background.md"], activations);
    }

    [Theory]
    [MemberData(nameof(ActiveSurfaceLoadFailures))]
    public async Task Failed_replacement_clears_the_previously_active_surface(
        Func<Exception> createFailure)
    {
        string? activeSurface = null;
        var loadCount = 0;
        var shell = CreateShell(
            (path, encoding, cancellationToken) =>
            {
                loadCount++;
                return loadCount == 1
                    ? Task.FromResult(Loaded(path))
                    : Task.FromException<LoadedDocument>(createFailure());
            },
            (tab, cancellationToken) =>
            {
                activeSurface = Path.GetFileName(tab.Path);
                return Task.CompletedTask;
            },
            () => activeSurface = null);
        await shell.OpenAsync(Target("shown.md"), OpenGesture.Normal, CancellationToken.None);
        Assert.Equal("shown.md", activeSurface);

        await shell.OpenAsync(Target("failed.md"), OpenGesture.Normal, CancellationToken.None);

        Assert.Null(activeSurface);
        Assert.NotNull(shell.ActiveTab!.Error);
    }

    [Fact]
    public async Task Failed_explicit_new_tab_clears_the_previously_active_surface()
    {
        string? activeSurface = null;
        var shell = CreateShell(
            (path, encoding, cancellationToken) =>
                Path.GetFileName(path) == "missing.md"
                    ? Task.FromException<LoadedDocument>(new FileNotFoundException("missing"))
                    : Task.FromResult(Loaded(path)),
            (tab, cancellationToken) =>
            {
                activeSurface = Path.GetFileName(tab.Path);
                return Task.CompletedTask;
            },
            () => activeSurface = null);
        await shell.OpenAsync(Target("shown.md"), OpenGesture.Normal, CancellationToken.None);

        await shell.OpenAsync(
            Target("missing.md"),
            OpenGesture.ExplicitNewTab,
            CancellationToken.None);

        Assert.Equal(2, shell.Tabs.Count);
        Assert.Equal("missing.md", Path.GetFileName(shell.ActiveTab!.Path));
        Assert.Null(activeSurface);
    }

    [Fact]
    public async Task Background_tab_failure_does_not_clear_the_selected_valid_surface()
    {
        var backgroundFailure = new TaskCompletionSource<LoadedDocument>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        string? activeSurface = null;
        var shell = CreateShell(
            (path, encoding, cancellationToken) =>
                Path.GetFileName(path) == "background.md"
                    ? backgroundFailure.Task
                    : Task.FromResult(Loaded(path)),
            (tab, cancellationToken) =>
            {
                activeSurface = Path.GetFileName(tab.Path);
                return Task.CompletedTask;
            },
            () => activeSurface = null);
        await shell.OpenAsync(Target("selected.md"), OpenGesture.Normal, CancellationToken.None);
        var selected = shell.ActiveTab!;
        var backgroundOpen = shell.OpenAsync(
            Target("background.md"),
            OpenGesture.ExplicitNewTab,
            CancellationToken.None);
        var background = shell.ActiveTab!;
        await shell.ActivateAsync(selected, CancellationToken.None);
        Assert.Equal("selected.md", activeSurface);

        backgroundFailure.SetException(new UnauthorizedAccessException("denied"));
        await backgroundOpen;

        Assert.Same(selected, shell.ActiveTab);
        Assert.NotNull(background.Error);
        Assert.Equal("selected.md", activeSurface);
    }

    [Fact]
    public async Task Selecting_an_errored_tab_clears_the_selected_valid_surface()
    {
        string? activeSurface = null;
        var shell = CreateShell(
            (path, encoding, cancellationToken) =>
                Path.GetFileName(path) == "missing.md"
                    ? Task.FromException<LoadedDocument>(new FileNotFoundException("missing"))
                    : Task.FromResult(Loaded(path)),
            (tab, cancellationToken) =>
            {
                activeSurface = Path.GetFileName(tab.Path);
                return Task.CompletedTask;
            },
            () => activeSurface = null);
        await shell.OpenAsync(Target("selected.md"), OpenGesture.Normal, CancellationToken.None);
        var selected = shell.ActiveTab!;
        await shell.OpenAsync(
            Target("missing.md"),
            OpenGesture.ExplicitNewTab,
            CancellationToken.None);
        var failed = shell.ActiveTab!;
        await shell.ActivateAsync(selected, CancellationToken.None);
        Assert.Equal("selected.md", activeSurface);

        await shell.ActivateAsync(failed, CancellationToken.None);

        Assert.Same(failed, shell.ActiveTab);
        Assert.Null(activeSurface);
    }

    [Fact]
    public async Task Errored_selection_clears_the_surface_when_two_way_binding_updates_active_tab_first()
    {
        string? activeSurface = null;
        var shell = CreateShell(
            (path, encoding, cancellationToken) =>
                Path.GetFileName(path) == "missing.md"
                    ? Task.FromException<LoadedDocument>(new FileNotFoundException("missing"))
                    : Task.FromResult(Loaded(path)),
            (tab, cancellationToken) =>
            {
                activeSurface = Path.GetFileName(tab.Path);
                return Task.CompletedTask;
            },
            () => activeSurface = null);
        await shell.OpenAsync(Target("selected.md"), OpenGesture.Normal, CancellationToken.None);
        var selected = shell.ActiveTab!;
        await shell.OpenAsync(
            Target("missing.md"),
            OpenGesture.ExplicitNewTab,
            CancellationToken.None);
        var failed = shell.ActiveTab!;
        await shell.ActivateAsync(selected, CancellationToken.None);
        Assert.Equal("selected.md", activeSurface);

        shell.ActiveTab = failed;
        await shell.ActivateAsync(failed, CancellationToken.None);

        Assert.Null(activeSurface);
    }

    [Fact]
    public async Task Closing_a_valid_tab_onto_an_errored_successor_clears_the_closed_surface()
    {
        string? activeSurface = null;
        var shell = CreateShell(
            (path, encoding, cancellationToken) =>
                Path.GetFileName(path) == "missing.md"
                    ? Task.FromException<LoadedDocument>(new FileNotFoundException("missing"))
                    : Task.FromResult(Loaded(path)),
            (tab, cancellationToken) =>
            {
                activeSurface = Path.GetFileName(tab.Path);
                return Task.CompletedTask;
            },
            () => activeSurface = null);
        await shell.OpenAsync(Target("shown.md"), OpenGesture.Normal, CancellationToken.None);
        var shown = shell.ActiveTab!;
        await shell.OpenAsync(
            Target("missing.md"),
            OpenGesture.ExplicitNewTab,
            CancellationToken.None);
        var failed = shell.ActiveTab!;
        await shell.ActivateAsync(shown, CancellationToken.None);
        Assert.Equal("shown.md", activeSurface);

        shell.CloseTab(shown);

        Assert.Same(failed, shell.ActiveTab);
        Assert.NotNull(failed.Error);
        Assert.Null(activeSurface);
    }

    [Fact]
    public async Task Closing_an_active_tab_does_not_blank_a_successor_activated_reentrantly()
    {
        string? activeSurface = null;
        var shell = CreateShell(
            activate: (tab, cancellationToken) =>
            {
                activeSurface = Path.GetFileName(tab.Path);
                return Task.CompletedTask;
            },
            deactivate: () => activeSurface = null);
        await shell.OpenAsync(Target("first.md"), OpenGesture.Normal, CancellationToken.None);
        var first = shell.ActiveTab!;
        await shell.OpenAsync(
            Target("successor.md"),
            OpenGesture.ExplicitNewTab,
            CancellationToken.None);
        var successor = shell.ActiveTab!;
        await shell.ActivateAsync(first, CancellationToken.None);
        Assert.Equal("first.md", activeSurface);
        shell.Tabs.CollectionChanged += (_, _) =>
            shell.ActivateAsync(successor, CancellationToken.None).GetAwaiter().GetResult();

        shell.CloseTab(first);

        Assert.Same(successor, shell.ActiveTab);
        Assert.Equal("successor.md", activeSurface);
    }

    [Fact]
    public async Task Replacing_a_tab_cancels_its_previous_load()
    {
        var firstLoad = new TaskCompletionSource<LoadedDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken firstToken = default;
        var loadCount = 0;
        var shell = CreateShell((path, encoding, cancellationToken) =>
        {
            loadCount++;
            if (loadCount == 1)
            {
                firstToken = cancellationToken;
                return firstLoad.Task;
            }

            return Task.FromResult(Loaded(path));
        });

        var firstOpen = shell.OpenAsync(Target("first.md"), OpenGesture.Normal, CancellationToken.None);
        await shell.OpenAsync(Target("replacement.md"), OpenGesture.Normal, CancellationToken.None);

        Assert.True(firstToken.IsCancellationRequested);
        firstLoad.SetResult(Loaded("first.md"));
        await firstOpen;
    }

    [Fact]
    public async Task Closing_a_loading_tab_cancels_it_and_clears_the_last_surface()
    {
        var pendingLoad = new TaskCompletionSource<LoadedDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken loadToken = default;
        var clearCount = 0;
        var shell = CreateShell(
            (path, encoding, cancellationToken) =>
            {
                loadToken = cancellationToken;
                return pendingLoad.Task;
            },
            deactivate: () => clearCount++);

        var open = shell.OpenAsync(Target("closing.md"), OpenGesture.Normal, CancellationToken.None);
        var closedTab = shell.ActiveTab!;
        shell.CloseActiveTab();

        Assert.True(loadToken.IsCancellationRequested);
        Assert.Null(shell.ActiveTab);
        Assert.Equal(1, clearCount);

        pendingLoad.SetResult(Loaded("closing.md"));
        await open;
        Assert.Equal(0, closedTab.Revision);
        Assert.Null(closedTab.Error);
        await Assert.ThrowsAsync<InvalidOperationException>(() => shell.RetryAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Closing_a_tab_cancels_its_pending_surface_activation()
    {
        var activationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activationCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shell = CreateShell(
            activate: async (tab, cancellationToken) =>
            {
                activationStarted.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    activationCancelled.SetResult();
                    throw;
                }
            });

        var open = shell.OpenAsync(Target("closing.md"), OpenGesture.Normal, CancellationToken.None);
        await activationStarted.Task;

        shell.CloseActiveTab();

        await activationCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await open;
        Assert.Null(shell.ActiveTab);
    }

    [Fact]
    public async Task Closing_a_tab_cancels_an_explicit_selection_activation()
    {
        var holdActivation = false;
        var activationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activationCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shell = CreateShell(
            activate: async (tab, cancellationToken) =>
            {
                if (!holdActivation)
                {
                    return;
                }

                activationStarted.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    activationCancelled.SetResult();
                    throw;
                }
            });
        await shell.OpenAsync(Target("selected.md"), OpenGesture.Normal, CancellationToken.None);
        var tab = shell.ActiveTab!;
        holdActivation = true;

        var selection = shell.ActivateAsync(tab, CancellationToken.None);
        await activationStarted.Task;
        shell.CloseActiveTab();

        await activationCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await selection;
        Assert.Null(shell.ActiveTab);
    }

    [Fact]
    public async Task Replacement_cancels_selection_without_surface_retry_when_the_new_load_fails()
    {
        var coordinator = new WebSurfaceActivationCoordinator();
        var holdActivation = false;
        var activationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activationCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadCount = 0;
        var shell = CreateShell(
            (path, encoding, cancellationToken) =>
            {
                loadCount++;
                return loadCount == 1
                    ? Task.FromResult(Loaded(path))
                    : Task.FromException<LoadedDocument>(new FileNotFoundException("missing"));
            },
            async (tab, cancellationToken) =>
            {
                var activation = coordinator.BeginActivation(tab.Id);
                if (!holdActivation)
                {
                    coordinator.MarkAwaitingReady(activation, tab.Id);
                    Assert.True(coordinator.TryMarkReady(tab.Id));
                    Assert.True(coordinator.TryRecordPosted(activation, Guid.NewGuid(), tab.Revision));
                    return;
                }

                activationStarted.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    coordinator.CancelActivation(activation);
                    activationCancelled.SetResult();
                    throw;
                }
            });
        await shell.OpenAsync(Target("shown.md"), OpenGesture.Normal, CancellationToken.None);
        var tab = shell.ActiveTab!;
        holdActivation = true;
        var selection = shell.ActivateAsync(tab, CancellationToken.None);
        await activationStarted.Task;

        await shell.OpenAsync(Target("missing.md"), OpenGesture.Normal, CancellationToken.None);
        await selection;

        await activationCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(tab.Error?.CanRetry);
        Assert.Equal(WebSurfaceFailure.None, coordinator.Failure);
        Assert.False(coordinator.CanRetry);
        Assert.Null(coordinator.CurrentResponse);
    }

    [Fact]
    public async Task Selecting_a_tab_during_replacement_load_joins_without_cancelling_or_activating_old_revision()
    {
        var replacementLoad = new TaskCompletionSource<LoadedDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadCount = 0;
        CancellationToken replacementToken = default;
        var activatedRevisions = new List<long>();
        var shell = CreateShell(
            (path, encoding, cancellationToken) =>
            {
                loadCount++;
                if (loadCount == 1)
                {
                    return Task.FromResult(Loaded(path));
                }

                replacementToken = cancellationToken;
                return replacementLoad.Task;
            },
            (tab, cancellationToken) =>
            {
                activatedRevisions.Add(tab.Revision);
                return Task.CompletedTask;
            });
        await shell.OpenAsync(Target("shown.md"), OpenGesture.Normal, CancellationToken.None);
        var tab = shell.ActiveTab!;
        var replacement = shell.OpenAsync(Target("replacement.md"), OpenGesture.Normal, CancellationToken.None);

        await shell.ActivateAsync(tab, CancellationToken.None);

        Assert.False(replacementToken.IsCancellationRequested);
        Assert.Equal([1L], activatedRevisions);
        replacementLoad.SetResult(Loaded("replacement.md"));
        await replacement;
        Assert.Equal([1L, 2L], activatedRevisions);
    }

    [Fact]
    public async Task Closing_a_loading_tab_suppresses_its_late_error()
    {
        var pendingLoad = new TaskCompletionSource<LoadedDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shell = CreateShell((path, encoding, cancellationToken) => pendingLoad.Task);
        var open = shell.OpenAsync(Target("closing.md"), OpenGesture.Normal, CancellationToken.None);
        var closedTab = shell.ActiveTab!;

        shell.CloseActiveTab();
        pendingLoad.SetException(new UnauthorizedAccessException("secret"));
        await open;

        Assert.Null(closedTab.Error);
        Assert.Equal(0, closedTab.Revision);
    }

    [Fact]
    public void Dispose_cancels_every_inflight_tab_load_and_clears_the_surface()
    {
        var tokens = new List<CancellationToken>();
        var clearCount = 0;
        var shell = CreateShell(
            (path, encoding, cancellationToken) =>
            {
                tokens.Add(cancellationToken);
                return new TaskCompletionSource<LoadedDocument>().Task;
            },
            deactivate: () => clearCount++);

        _ = shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        _ = shell.OpenAsync(Target("two.md"), OpenGesture.ExplicitNewTab, CancellationToken.None);

        shell.Dispose();

        Assert.All(tokens, token => Assert.True(token.IsCancellationRequested));
        Assert.Equal(1, clearCount);
    }

    [Fact]
    public async Task Active_error_properties_cover_startup_failure_retry_and_last_tab_close()
    {
        var loadCount = 0;
        var shell = CreateShell((path, encoding, cancellationToken) =>
        {
            loadCount++;
            return loadCount == 1
                ? Task.FromException<LoadedDocument>(new FileNotFoundException("secret"))
                : Task.FromResult(Loaded(path));
        });

        Assert.False(shell.HasActiveError);
        Assert.False(shell.CanRetryActiveError);
        Assert.False(shell.CanChooseEncodingForActiveError);
        Assert.False(shell.CanCloseActiveError);
        Assert.Null(shell.ActiveErrorMessage);

        await shell.OpenAsync(Target("retry.md"), OpenGesture.Normal, CancellationToken.None);

        Assert.True(shell.HasActiveError);
        Assert.True(shell.CanRetryActiveError);
        Assert.False(shell.CanChooseEncodingForActiveError);
        Assert.True(shell.CanCloseActiveError);
        Assert.NotNull(shell.ActiveErrorMessage);

        await shell.RetryAsync(CancellationToken.None);

        Assert.False(shell.HasActiveError);
        Assert.False(shell.CanRetryActiveError);
        Assert.False(shell.CanCloseActiveError);
        Assert.Null(shell.ActiveErrorMessage);

        shell.CloseActiveTab();

        Assert.False(shell.HasActiveError);
        Assert.False(shell.CanCloseActiveError);
    }

    public static TheoryData<Func<Exception>> RetryableLoadFailures => new()
    {
        () => new FileNotFoundException("secret path"),
        () => new UnauthorizedAccessException("secret path"),
    };

    public static TheoryData<Func<Exception>> ActiveSurfaceLoadFailures => new()
    {
        () => new FileNotFoundException("missing"),
        () => new UnauthorizedAccessException("denied"),
        () => new DecoderFallbackException("legacy bytes"),
    };

    private static ShellViewModel CreateShell(
        Func<string, Encoding?, CancellationToken, Task<LoadedDocument>>? load = null,
        Func<DocumentTabViewModel, CancellationToken, Task>? activate = null,
        Action? deactivate = null)
    {
        App.RegisterEncodingProviders();
        var registry = new DocumentFormatRegistry([new MarkdownDocumentProvider()]);
        return new ShellViewModel(
            registry,
            load ?? ((path, encoding, cancellationToken) => Task.FromResult(Loaded(path))),
            activate ?? ((tab, cancellationToken) => Task.CompletedTask),
            deactivate ?? (() => { }));
    }

    private static DocumentTarget Target(string path) => new(Path.GetFullPath(path), null, null);

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

    private sealed record ActivationSnapshot(
        Guid TabId,
        string Path,
        long Revision,
        int? Line,
        string? Anchor,
        DocumentMode Mode,
        string Text);
}
