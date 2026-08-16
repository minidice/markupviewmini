using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Automation;
using System.Windows.Documents;
using System.Runtime.ExceptionServices;
using System.Reflection;
using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Core.Navigation;
using MarkUpViewMini.Core.Search;
using MarkUpViewMini.Core.Workspace;
using MarkUpViewMini.Infrastructure.Paths;
using MarkUpViewMini.Infrastructure.Folders;
using MarkUpViewMini.Infrastructure.State;
using MarkUpViewMini.App.ViewModels;
using MarkUpViewMini.App.Web;
using MarkUpViewMini.App.Composition;
using System.Text;
using System.Windows.Threading;
using System.Diagnostics;
using MarkUpViewMini.App.About;
using Microsoft.Web.WebView2.Wpf;

namespace MarkUpViewMini.App.Tests.Composition;

public sealed class MainWindowConstructionTests
{
    [Fact]
    public void Information_menu_routes_each_item_to_its_owner_bound_dialog_without_session_side_effects_on_sta()
    {
        RunOnSta(() =>
        {
            App.RegisterEncodingProviders();
            var dialogs = new RecordingAboutDialogService();
            var testRoot = Path.Combine(Path.GetTempPath(), $"markup-view-mini-information-{Guid.NewGuid():N}");
            var paths = PortableAppDataPaths.Create(testRoot);
            var window = new MainWindow(
                new DocumentFormatRegistry([new MarkdownDocumentProvider()]),
                paths,
                aboutDialogService: dialogs);
            try
            {
                var shell = Assert.IsType<ShellViewModel>(window.DataContext);
                var initialTabCount = shell.Tabs.Count;
                var informationMenu = Assert.IsType<MenuItem>(window.FindName("InformationMenu"));
                var rootMenu = Assert.IsType<Menu>(ItemsControl.ItemsControlFromItemContainer(informationMenu));
                var settingsMenu = Assert.IsType<MenuItem>(window.FindName("WindowsIntegrationMenu"));
                var settingsIndex = rootMenu.Items.IndexOf(settingsMenu);
                var browser = GetDocumentBrowser(window);

                Assert.Equal("정보", informationMenu.Header);
                Assert.Equal(settingsIndex + 1, rootMenu.Items.IndexOf(informationMenu));
                Assert.Equal(
                    ["버전 정보", "타사 라이선스", "앱 라이선스"],
                    informationMenu.Items.Cast<MenuItem>().Select(static item => item.Header));
                Assert.Null(browser.CoreWebView2);
                Assert.False(File.Exists(paths.SettingsFile));
                Assert.False(File.Exists(paths.SessionFile));
                ((MenuItem)window.FindName("VersionInformationItem")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                ((MenuItem)window.FindName("ThirdPartyLicensesItem")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                ((MenuItem)window.FindName("ApplicationLicenseItem")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

                Assert.Equal(
                    [AboutDialogKind.Version, AboutDialogKind.ThirdPartyLicenses, AboutDialogKind.ApplicationLicense],
                    dialogs.Kinds);
                Assert.All(dialogs.Owners, owner => Assert.Same(window, owner));
                Assert.Equal(initialTabCount, shell.Tabs.Count);
                Assert.Null(browser.CoreWebView2);
                Assert.False(File.Exists(paths.SettingsFile));
                Assert.False(File.Exists(paths.SessionFile));
            }
            finally
            {
                window.Close();
                if (Directory.Exists(testRoot))
                {
                    Directory.Delete(testRoot, recursive: true);
                }
            }
        });
    }

    private static WebView2 GetDocumentBrowser(MainWindow window)
    {
        var surface = Assert.IsType<WebDocumentSurface>(window.FindName("DocumentSurface"));
        var property = typeof(WebDocumentSurface).GetProperty(
            "Browser",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(property);
        return Assert.IsType<WebView2>(property.GetValue(surface));
    }

    [Fact]
    public void Clean_window_contributes_a_complete_shutdown_request_and_lifetime_identity_on_sta()
    {
        RunOnSta(() =>
        {
            App.RegisterEncodingProviders();
            var window = new MainWindow(
                new DocumentFormatRegistry([new MarkdownDocumentProvider()]),
                PortableAppDataPaths.Create(AppContext.BaseDirectory));
            var ownership = window.CaptureShutdownOwnership();
            try
            {
                Assert.True(window.TryCreateApplicationShutdownRequest(out var request));
                Assert.NotNull(request);
                Assert.Empty(request.Tabs);
                Assert.True(window.IsCurrentShutdownOwnership(ownership));
            }
            finally
            {
                window.Close();
            }

            Assert.False(window.IsCurrentShutdownOwnership(ownership));
        });
    }

    [Fact]
    public void Editing_commands_and_live_regions_construct_without_binding_trace_errors_on_sta()
    {
        RunOnSta(() =>
        {
            App.RegisterEncodingProviders();
            var output = new StringBuilder();
            using var writer = new StringWriter(output);
            using var listener = new TextWriterTraceListener(writer);
            PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
            var previousLevel = PresentationTraceSources.DataBindingSource.Switch.Level;
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            try
            {
                var window = new MainWindow(
                    new DocumentFormatRegistry([new MarkdownDocumentProvider()]),
                    PortableAppDataPaths.Create(AppContext.BaseDirectory));
                window.Show();
                window.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                listener.Flush();

                Assert.Empty(output.ToString());
                window.Close();
            }
            finally
            {
                PresentationTraceSources.DataBindingSource.Switch.Level = previousLevel;
                PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            }
        });
    }

    [Fact]
    public void Hidden_startup_candidates_do_not_own_application_main_window_until_commit()
    {
        // Break caught: WPF auto-assigns a failed hidden candidate and a later successful window never becomes MainWindow.
        RunOnSta(() =>
        {
            App.RegisterEncodingProviders();
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var registry = new DocumentFormatRegistry([new MarkdownDocumentProvider()]);
            var paths = PortableAppDataPaths.Create(AppContext.BaseDirectory);
            var first = new MainWindow(registry, paths);
            Assert.Same(first, application.MainWindow);
            StartupMainWindowOwnership.PreservePrevious(application, first, previous: null);
            Assert.Null(application.MainWindow);
            StartupMainWindowOwnership.Abandon(application, first, previous: null);
            Assert.Null(application.MainWindow);

            var second = new MainWindow(registry, paths);
            Assert.Same(second, application.MainWindow);
            StartupMainWindowOwnership.PreservePrevious(application, second, previous: null);
            Assert.Null(application.MainWindow);
            StartupMainWindowOwnership.Commit(application, second);
            Assert.Same(second, application.MainWindow);
            second.Close();
            application.Shutdown();
        });
    }

    [Fact]
    public void Recovery_comparison_has_persistent_localized_accessible_headers_and_safe_title()
    {
        // Break caught: side-by-side bodies are unlabeled or a full private path/body leaks into the window title.
        RunOnSta(() =>
        {
            var comparison = new RecoveryComparisonViewModel(
                new RecoveryReadOnlySnapshot(@"C:\private\secret\guide.md", "RECOVERY-BODY-SECRET"),
                new RecoveryReadOnlySnapshot(@"C:\private\secret\guide.md", "ORIGINAL-BODY-SECRET"));
            var window = NativeRecoveryDecisionDialog.CreateComparisonWindow(comparison);
            try
            {
                var grid = Assert.IsType<Grid>(window.Content);
                var headers = grid.Children.OfType<TextBlock>().ToArray();
                Assert.Contains(headers, header =>
                    header.Text == "복구본" && AutomationProperties.GetName(header) == "복구본");
                Assert.Contains(headers, header =>
                    header.Text == "현재 원본" && AutomationProperties.GetName(header) == "현재 원본");
                var bodies = grid.Children.OfType<TextBox>().ToArray();
                Assert.All(bodies, body => Assert.True(body.IsReadOnly));
                Assert.Contains(bodies, body => AutomationProperties.GetName(body) == "복구본 내용 (읽기 전용)");
                Assert.Contains(bodies, body => AutomationProperties.GetName(body) == "현재 원본 내용 (읽기 전용)");
                Assert.Contains("guide.md", window.Title, StringComparison.Ordinal);
                Assert.DoesNotContain(@"C:\private\secret", window.Title, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("RECOVERY-BODY-SECRET", window.Title, StringComparison.Ordinal);
                Assert.DoesNotContain("ORIGINAL-BODY-SECRET", window.Title, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(ModifierKeys.None, LinkOpenDisposition.Default)]
    [InlineData(ModifierKeys.Control, LinkOpenDisposition.NewTab)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Shift, LinkOpenDisposition.NewTab)]
    public void Pointer_modifiers_map_normal_and_control_activation_to_the_approved_disposition(
        ModifierKeys modifiers,
        LinkOpenDisposition expected)
    {
        // Break caught: WPF click handlers can invert normal/Ctrl-click tab ownership.
        Assert.Equal(expected, WindowInputPolicy.GetLinkDisposition(modifiers));
    }

    [Theory]
    [InlineData(Key.Enter, true)]
    [InlineData(Key.Space, true)]
    [InlineData(Key.Tab, false)]
    public void Tree_and_outline_keyboard_activation_accept_only_enter_and_space(Key key, bool expected)
    {
        // Break caught: pointer-only item handlers leave keyboard users unable to activate tree files or outline headings.
        Assert.Equal(expected, WindowInputPolicy.IsItemActivationKey(key));
    }

    [Fact]
    public void Nested_tree_source_resolves_the_nearest_two_level_item()
    {
        // Break caught: resolving from only the root ItemsControl returns the owner-level directory instead of its nested file.
        RunOnSta(() =>
        {
            var directory = new FolderNode("nested", @"C:\Docs\nested", true, [], null);
            var file = new FolderNode("deep.md", @"C:\Docs\nested\deep.md", false, [], null);
            var source = new Run("deep.md");
            var child = new TreeViewItem
            {
                DataContext = file,
                Header = new TextBlock { Inlines = { source } },
            };
            var parent = new TreeViewItem { DataContext = directory, Header = "nested", IsExpanded = true };
            parent.Items.Add(child);
            var tree = new TreeView();
            tree.Items.Add(parent);
            var window = new Window { Content = tree };
            try
            {
                window.Show();
                window.UpdateLayout();

                Assert.Same(file, MainWindow.FindNearestItem<FolderNode>(source));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Find_requires_the_surface_response_to_match_the_loaded_active_tab_revision()
    {
        // Break caught: a loaded tab alone enables find while WebView is replacing, deactivated, failed, or owns another revision.
        var tab = new DocumentTabViewModel(new DocumentTarget(@"C:\Docs\guide.md", null, null));
        tab.ApplyLoaded(new LoadedDocument(
            "# Guide",
            new EncodingDescriptor("utf-8", false),
            NewLineKind.Lf,
            "\n",
            new DiskFileVersion(7, DateTime.UnixEpoch, "hash")));
        var matching = new WebResponseContext(Guid.NewGuid(), tab.Id, tab.Revision);

        Assert.True(WindowInputPolicy.CanExecuteFind(tab, matching));
        Assert.False(WindowInputPolicy.CanExecuteFind(tab, null));
        Assert.False(WindowInputPolicy.CanExecuteFind(tab, matching with { TabId = Guid.NewGuid() }));
        Assert.False(WindowInputPolicy.CanExecuteFind(tab, matching with { Revision = tab.Revision + 1 }));
        tab.PrepareForLoad(new DocumentTarget(@"C:\Docs\replacement.md", null, null));
        Assert.False(WindowInputPolicy.CanExecuteFind(tab, matching));
        tab.Error = DocumentOpenErrorViewModel.From(new IOException("failed"));
        Assert.False(WindowInputPolicy.CanExecuteFind(tab, matching));
    }

    [Fact]
    public void Window_constructs_the_sidebar_defaults_root_options_and_find_shortcuts_on_sta()
    {
        // Break caught: XAML can compile while omitting planned controls, selecting option 2, or losing a keyboard route.
        RunOnSta(() =>
        {
            App.RegisterEncodingProviders();
            var registry = new DocumentFormatRegistry([new MarkdownDocumentProvider()]);
            var paths = PortableAppDataPaths.Create(AppContext.BaseDirectory);
            var window = new MainWindow(registry, paths);
            try
            {
                window.Show();
                window.UpdateLayout();
                var sidebarTabs = Assert.IsType<TabControl>(window.FindName("SidebarTabs"));
                var fileName = Assert.IsType<RadioButton>(window.FindName("FileNameSearchRadio"));
                var body = Assert.IsType<RadioButton>(window.FindName("BodySearchRadio"));
                var keepRoot = Assert.IsType<RadioButton>(window.FindName("KeepRootRadio"));
                var followRoot = Assert.IsType<RadioButton>(window.FindName("FollowRootRadio"));
                Assert.IsType<TreeView>(window.FindName("FolderTree"));
                Assert.IsType<ListBox>(window.FindName("OutlineList"));
                Assert.IsType<TextBox>(window.FindName("SearchBox"));
                Assert.IsType<TextBlock>(window.FindName("RootRefreshingText"));
                Assert.IsType<TextBlock>(window.FindName("RootTreeErrorText"));
                var back = Assert.IsType<Button>(window.FindName("BackButton"));
                var forward = Assert.IsType<Button>(window.FindName("ForwardButton"));
                var conflictStatus = Assert.IsType<Border>(window.FindName("ConflictStatusRegion"));
                var editingStatus = Assert.IsType<Border>(window.FindName("EditingStatusRegion"));
                var modeToggle = Assert.IsType<Button>(window.FindName("ModeToggleButton"));

                Assert.Equal(0, sidebarTabs.SelectedIndex);
                Assert.True(fileName.IsChecked == true);
                Assert.True(body.IsChecked == false);
                Assert.True(keepRoot.IsChecked == true);
                Assert.True(followRoot.IsChecked == false);
                Assert.True(followRoot.IsEnabled);
                Assert.False(MainWindowCommands.OpenFind.CanExecute(null, window));
                Assert.Equal("뒤로", AutomationProperties.GetName(back));
                Assert.Equal("앞으로", AutomationProperties.GetName(forward));
                Assert.Equal("외부 파일 변경", AutomationProperties.GetName(conflictStatus));
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(conflictStatus));
                Assert.Equal("편집 및 저장 상태", AutomationProperties.GetName(editingStatus));
                Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(editingStatus));
                Assert.Equal("읽기/편집 모드 전환", AutomationProperties.GetName(modeToggle));
                Assert.Contains(window.InputBindings.OfType<KeyBinding>(), binding =>
                    binding.Key == Key.F && binding.Modifiers == ModifierKeys.Control);
                Assert.Contains(window.InputBindings.OfType<KeyBinding>(), binding =>
                    binding.Key == Key.F3 && binding.Modifiers == ModifierKeys.None);
                Assert.Contains(window.InputBindings.OfType<KeyBinding>(), binding =>
                    binding.Key == Key.F3 && binding.Modifiers == ModifierKeys.Shift);
                Assert.Contains(window.InputBindings.OfType<KeyBinding>(), binding =>
                    binding.Key == Key.Escape && binding.Modifiers == ModifierKeys.None);
                Assert.Contains(window.InputBindings.OfType<KeyBinding>(), binding =>
                    binding.Key == Key.S && binding.Modifiers == ModifierKeys.Control &&
                    binding.Command == MainWindowCommands.Save);
                Assert.Contains(window.InputBindings.OfType<KeyBinding>(), binding =>
                    binding.Key == Key.S && binding.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) &&
                    binding.Command == MainWindowCommands.SaveAs);
                Assert.Contains(window.InputBindings.OfType<KeyBinding>(), binding =>
                    binding.Key == Key.Z && binding.Modifiers == ModifierKeys.Control &&
                    binding.Command == MainWindowCommands.Undo);
                Assert.Contains(window.InputBindings.OfType<KeyBinding>(), binding =>
                    binding.Key == Key.Y && binding.Modifiers == ModifierKeys.Control &&
                    binding.Command == MainWindowCommands.Redo);

                body.IsChecked = true;
                followRoot.IsChecked = true;
                Assert.Equal(SearchMode.Body, window.Sidebar.SearchMode);
                Assert.Equal(RootFollowMode.FollowCurrentDocument, window.Sidebar.RootMode);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Window_exposes_user_controlled_file_registration_settings_and_disposes_their_commands()
    {
        // Break caught: registration exists only as a service, or window closure leaves settings commands active.
        RunOnSta(() =>
        {
            App.RegisterEncodingProviders();
            var registry = new DocumentFormatRegistry([new MarkdownDocumentProvider()]);
            var paths = PortableAppDataPaths.Create(AppContext.BaseDirectory);
            var window = new MainWindow(registry, paths);
            var viewModel = window.WindowsIntegration;
            try
            {
                window.Show();
                window.UpdateLayout();
                var menu = Assert.IsType<MenuItem>(window.FindName("WindowsIntegrationMenu"));
                var status = Assert.IsType<MenuItem>(window.FindName("RegistrationStatusItem"));
                var guidance = Assert.IsType<MenuItem>(window.FindName("RegistrationGuidanceItem"));
                var register = Assert.IsType<MenuItem>(window.FindName("RegisterFileTypesItem"));
                var unregister = Assert.IsType<MenuItem>(window.FindName("UnregisterFileTypesItem"));
                var openSettings = Assert.IsType<MenuItem>(window.FindName("OpenDefaultAppsSettingsItem"));
                var shortcutStatus = Assert.IsType<MenuItem>(window.FindName("ShortcutStatusItem"));
                var createStartMenu = Assert.IsType<MenuItem>(window.FindName("CreateStartMenuShortcutItem"));
                var createDesktop = Assert.IsType<MenuItem>(window.FindName("CreateDesktopShortcutItem"));
                var removeShortcuts = Assert.IsType<MenuItem>(window.FindName("RemoveShortcutsItem"));
                var error = Assert.IsType<MenuItem>(window.FindName("WindowsIntegrationErrorItem"));

                Assert.Same(viewModel, menu.DataContext);
                Assert.Equal(viewModel.StatusText, status.Header);
                Assert.Equal(viewModel.GuidanceText, guidance.Header);
                Assert.Equal("파일 형식 등록", register.Header);
                Assert.Equal("등록 해제", unregister.Header);
                Assert.Equal("Windows 기본 앱 설정 열기", openSettings.Header);
                Assert.Same(viewModel.RegisterCommand, register.Command);
                Assert.Same(viewModel.UnregisterCommand, unregister.Command);
                Assert.Same(viewModel.OpenDefaultAppsSettingsCommand, openSettings.Command);
                Assert.Equal(viewModel.ShortcutStatusText, shortcutStatus.Header);
                Assert.Same(viewModel.CreateStartMenuShortcutCommand, createStartMenu.Command);
                Assert.Same(viewModel.CreateDesktopShortcutCommand, createDesktop.Command);
                Assert.Same(viewModel.RemoveShortcutsCommand, removeShortcuts.Command);
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(error));
            }
            finally
            {
                window.Close();
            }

            Assert.False(viewModel.RegisterCommand.CanExecute(null));
            Assert.False(viewModel.UnregisterCommand.CanExecute(null));
            Assert.False(viewModel.OpenDefaultAppsSettingsCommand.CanExecute(null));
            Assert.False(viewModel.CreateStartMenuShortcutCommand.CanExecute(null));
            Assert.False(viewModel.CreateDesktopShortcutCommand.CanExecute(null));
            Assert.False(viewModel.RemoveShortcutsCommand.CanExecute(null));
        });
    }

    [Fact]
    public async Task Window_applies_loaded_layout_and_exposes_recent_documents_in_the_file_menu()
    {
        // Break caught: persisted settings exist but the WPF window starts with hard-coded layout or an unreachable MRU.
        App.RegisterEncodingProviders();
        var root = Path.Combine(
            Path.GetTempPath(),
            nameof(MainWindowConstructionTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var recentPath = Path.Combine(root, "recent.md");
        File.WriteAllText(recentPath, "# Recent");
        var paths = PortableAppDataPaths.Create(root);
        var settings = new SettingsService(paths);
        settings.ScheduleSave(SettingsV1.CreateDefault() with
        {
            RootMode = RootFollowMode.FollowCurrentDocument,
            SidebarWidth = 333,
            SidebarSearchMode = SearchMode.Body,
            RecentDocuments = [new(recentPath)],
        });
        try
        {
            RunOnSta(() =>
            {
                var registry = new DocumentFormatRegistry([new MarkdownDocumentProvider()]);
                var window = new MainWindow(registry, paths, settings);
                try
                {
                    window.Show();
                    window.UpdateLayout();
                    var sidebarColumn = Assert.IsType<ColumnDefinition>(window.FindName("SidebarColumn"));
                    var recentMenu = Assert.IsType<MenuItem>(window.FindName("RecentDocumentsMenu"));

                    Assert.Equal(333, sidebarColumn.ActualWidth, precision: 0);
                    Assert.Equal(RootFollowMode.FollowCurrentDocument, window.Sidebar.RootMode);
                    Assert.Equal(SearchMode.Body, window.Sidebar.SearchMode);
                    var recent = Assert.Single(recentMenu.Items);
                    Assert.Equal(recentPath, Assert.IsType<RecentDocumentEntry>(recent).Path);
                }
                finally
                {
                    window.Close();
                }
            });
        }
        finally
        {
            await settings.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Shared_settings_update_both_windows_and_closing_stale_window_cannot_erase_latest_fields()
    {
        // Break caught: window B closes with its old full snapshot and erases window A's MRU/preferences.
        App.RegisterEncodingProviders();
        var root = Path.Combine(Path.GetTempPath(), nameof(MainWindowConstructionTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "a.md");
        File.WriteAllText(path, "# A");
        var paths = PortableAppDataPaths.Create(root);
        var settings = new SettingsService(paths);
        try
        {
            RunOnSta(() =>
            {
                var registry = new DocumentFormatRegistry([new MarkdownDocumentProvider()]);
                var first = new MainWindow(registry, paths, settings);
                var second = new MainWindow(registry, paths, settings);
                try
                {
                    first.Show();
                    second.Show();
                    settings.RecordSuccessfulOpen(path);
                    settings.UpdateSidebarPreferences(
                        RootFollowMode.FollowCurrentDocument,
                        SearchMode.Body,
                        new SearchOptionsV1(true, true, false));
                    settings.UpdateSidebarWidth(411);
                    second.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

                    var recentMenu = Assert.IsType<MenuItem>(second.FindName("RecentDocumentsMenu"));
                    Assert.Equal(path, Assert.IsType<RecentDocumentEntry>(Assert.Single(recentMenu.Items)).Path);

                    second.Close();

                    Assert.Equal(path, Assert.Single(settings.Current.RecentDocuments).Path);
                    Assert.Equal(RootFollowMode.FollowCurrentDocument, settings.Current.RootMode);
                    Assert.Equal(SearchMode.Body, settings.Current.SidebarSearchMode);
                    Assert.Equal(411, settings.Current.SidebarWidth);
                }
                finally
                {
                    if (second.IsLoaded) second.Close();
                    first.Close();
                }
            });
        }
        finally
        {
            await settings.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Window_rejects_a_stale_settings_notification_delivered_after_a_newer_generation()
    {
        // Break caught: T1's delayed notification arrives after T2 and rolls the rendered window back to A.
        var root = Path.Combine(Path.GetTempPath(), nameof(MainWindowConstructionTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = PortableAppDataPaths.Create(root);
        var settings = new SettingsService(paths);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<SettingsChangedEventArgs> blocker = (_, change) =>
        {
            if (change.Snapshot.SidebarWidth == 301)
            {
                firstEntered.TrySetResult();
                releaseFirst.Task.GetAwaiter().GetResult();
            }
        };
        settings.Changed += blocker;
        try
        {
            RunOnSta(() =>
            {
                App.RegisterEncodingProviders();
                var window = new MainWindow(
                    new DocumentFormatRegistry([new MarkdownDocumentProvider()]),
                    paths,
                    settings);
                try
                {
                    window.Show();
                    var first = Task.Run(() => settings.UpdateSidebarWidth(301));
                    Assert.True(firstEntered.Task.Wait(TimeSpan.FromSeconds(2)));
                    Task.Run(() => settings.UpdateSidebarWidth(402)).GetAwaiter().GetResult();
                    releaseFirst.TrySetResult();
                    first.GetAwaiter().GetResult();
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

                    var sidebarColumn = Assert.IsType<ColumnDefinition>(window.FindName("SidebarColumn"));
                    Assert.Equal(402, settings.Current.SidebarWidth);
                    Assert.Equal(402, sidebarColumn.Width.Value);
                }
                finally
                {
                    window.Close();
                }
            });
        }
        finally
        {
            settings.Changed -= blocker;
            releaseFirst.TrySetResult();
            await settings.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Tree_and_outline_enter_space_handlers_mark_supported_keyboard_activation_handled()
    {
        // Break caught: declaring activation-key policy without wiring the real TreeView/ListBox leaves keyboard input unhandled.
        var root = Path.Combine(Path.GetTempPath(), nameof(MainWindowConstructionTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "keyboard.md");
        File.WriteAllText(path, "# Keyboard");
        try
        {
            RunOnSta(() =>
            {
                App.RegisterEncodingProviders();
                var registry = new DocumentFormatRegistry([new MarkdownDocumentProvider()]);
                var paths = PortableAppDataPaths.Create(AppContext.BaseDirectory);
                var window = new MainWindow(registry, paths);
                try
                {
                    window.Show();
                    window.Sidebar.RootPath = root;
                    WaitForTask(window.Sidebar.RefreshTreeAsync(CancellationToken.None));
                    window.UpdateLayout();
                    var tree = Assert.IsType<TreeView>(window.FindName("FolderTree"));
                    var file = Assert.Single(Assert.IsType<FolderNode>(window.Sidebar.Tree).Children);
                    var treeItem = Assert.IsType<TreeViewItem>(tree.ItemContainerGenerator.ContainerFromItem(file));
                    treeItem.IsSelected = true;
                    File.Delete(path);
                    var treeKey = CreateKeyEvent(window, Key.Space);

                    tree.RaiseEvent(treeKey);

                    Assert.True(treeKey.Handled);
                    Assert.Contains(
                        Assert.IsType<ShellViewModel>(window.DataContext).Tabs,
                        tab => tab.Path == path);

                    var heading = new OutlineItemViewModel(2, "Heading", "heading", 8);
                    window.Sidebar.SetOutline([heading]);
                    window.UpdateLayout();
                    var outline = Assert.IsType<ListBox>(window.FindName("OutlineList"));
                    outline.SelectedItem = heading;
                    var outlineKey = CreateKeyEvent(window, Key.Enter);

                    outline.RaiseEvent(outlineKey);

                    Assert.True(outlineKey.Handled);
                }
                finally
                {
                    window.Close();
                }
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Deleted_root_error_is_visible_above_the_child_tree_on_sta()
    {
        // Break caught: binding only Tree.Children silently hides an error stored on the deleted root FolderNode itself.
        var deletedRoot = Path.Combine(
            Path.GetTempPath(),
            nameof(MainWindowConstructionTests),
            Guid.NewGuid().ToString("N"));
        RunOnSta(() =>
        {
            App.RegisterEncodingProviders();
            var registry = new DocumentFormatRegistry([new MarkdownDocumentProvider()]);
            var paths = PortableAppDataPaths.Create(AppContext.BaseDirectory);
            var window = new MainWindow(registry, paths);
            try
            {
                window.Show();
                window.Sidebar.RootPath = deletedRoot;
                WaitForTask(window.Sidebar.RefreshTreeAsync(CancellationToken.None));
                window.UpdateLayout();

                var rootError = Assert.IsType<TextBlock>(window.FindName("RootTreeErrorText"));
                Assert.True(window.Sidebar.HasRootError);
                Assert.False(string.IsNullOrWhiteSpace(window.Sidebar.RootError));
                Assert.Equal(Visibility.Visible, rootError.Visibility);
                Assert.Equal(window.Sidebar.RootError, rootError.Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static KeyEventArgs CreateKeyEvent(Window window, Key key) =>
        new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };

    private static void WaitForTask(Task task)
    {
        while (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }

        task.GetAwaiter().GetResult();
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "The WPF construction test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private sealed class RecordingAboutDialogService : IAboutDialogService
    {
        internal List<AboutDialogKind> Kinds { get; } = [];

        internal List<Window> Owners { get; } = [];

        public void Show(AboutDialogKind kind, Window owner)
        {
            Kinds.Add(kind);
            Owners.Add(owner);
        }
    }
}
