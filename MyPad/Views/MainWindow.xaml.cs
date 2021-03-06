﻿using Dragablz;
using MahApps.Metro.Controls;
using MyBase.Logging;
using MyPad.Models;
using MyPad.ViewModels;
using MyPad.Views.Controls;
using MyPad.Views.Regions;
using Prism.Commands;
using Prism.Ioc;
using Prism.Regions;
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using ToastNotifications;
using ToastNotifications.Lifetime;
using ToastNotifications.Position;
using Unity;
using Vanara.InteropServices;
using Vanara.PInvoke;

namespace MyPad.Views
{
    public partial class MainWindow : MetroWindow
    {
        #region インジェクション

        // Constructor Injection
        public IContainerExtension ContainerExtension { get; set; }

        // Dependency Injection
        [Dependency]
        public ILoggerFacade Logger { get; set; }
        [Dependency]
        public IRegionManager RegionManager { get; set; }
        [Dependency]
        public Settings Settings { get; set; }

        #endregion

        #region プロパティ

        public static readonly ICommand ActivateTextEditorCommand
            = new RoutedCommand(
                nameof(ActivateTextEditorCommand),
                typeof(MainWindow),
                new InputGestureCollection { new KeyGesture(Key.F6, ModifierKeys.Control) });
        public static readonly ICommand ActivateTerminalCommand
            = new RoutedCommand(
                nameof(ActivateTerminalCommand),
                typeof(MainWindow),
                new InputGestureCollection { new KeyGesture(Key.OemTilde, ModifierKeys.Control, "Ctrl+@") });
        public static readonly ICommand ActivateScriptRunnerCommand
            = new RoutedCommand(
                nameof(ActivateScriptRunnerCommand),
                typeof(MainWindow),
                new InputGestureCollection { new KeyGesture(Key.OemTilde, ModifierKeys.Control | ModifierKeys.Shift, "Ctrl+Shift+@") });
        public static readonly ICommand ActivateFileExplorerCommand
            = new RoutedCommand(
                nameof(ActivateFileExplorerCommand),
                typeof(MainWindow),
                new InputGestureCollection { new KeyGesture(Key.E, ModifierKeys.Control | ModifierKeys.Shift) });
                public static readonly ICommand ActivatePropertyCommand
                    = new RoutedCommand(
                        nameof(ActivatePropertyCommand),
                        typeof(MainWindow),
                        new InputGestureCollection { new KeyGesture(Key.Enter, ModifierKeys.Alt) });

        private static readonly DependencyProperty IsVisibleBottomContentProperty
            = DependencyPropertyExtensions.Register(
                new PropertyMetadata((obj, e) =>
                {
                    var self = (MainWindow)obj;
                    if (e.NewValue is bool value && value)
                    {
                        self.BottomContentRow.Height = new(150);
                        self.FocusBottomContent();
                    }
                    else
                    {
                        self.BottomContentRow.Height = new(0);
                        self.FocusTextEditor();
                    }
                }));

        private HwndSource _handleSource;
        private (uint fByPosition, User32.MENUITEMINFO lpmii) _miiShowMenuBar;
        private (uint fByPosition, User32.MENUITEMINFO lpmii) _miiShowToolBar;
        private (uint fByPosition, User32.MENUITEMINFO lpmii) _miiShowSideBar;
        private (uint fByPosition, User32.MENUITEMINFO lpmii) _miiShowStatusBar;
        private (uint fByPosition, User32.MENUITEMINFO lpmii) _miiSeparator;
        private (double sideBarWidth, double contentAreaWidth) _columnWidthCache = (2, 5);
        private bool _fullScreenMode;

        public ICSharpCode.AvalonEdit.Search.Localization Localization { get; }
        public IInterTabClient InterTabClient { get; }
        public Notifier Notifier { get; }
        public bool IsNewTabHost { get; set; }
        public MainWindowViewModel ViewModel => (MainWindowViewModel)this.DataContext;

        public TextEditor ActiveTextEditor
        {
            get
            {
                try
                {
                    // NOTE: 選択されたタブ内のコントロールを取得
                    // ItemsSource に ViewModel をバインドした場合、その参照が Item プロパティに設定されるため、
                    // 子要素のビジュアルオブジェクトを直接取得する方法が無い。(仕様)
                    // VisualTree をたどり ContentPreseneter を取得し、内包する要素の名前からコントロールを特定する。
                    // ただし、描画前のオブジェクトはツリーに登録されていないため取得できない。
                    var presenter = this.MainContent.Descendants().OfType<ContentPresenter>()
                        .Where(x => x.DataContext == this.MainContent.SelectedItem)
                        .Where(x => x.TemplatedParent == null)
                        .FirstOrDefault();
                    return presenter?.ContentTemplate?.FindName("TextEditor", presenter) as TextEditor;
                }
                catch
                {
                    return null;
                }
            }
        }

        public bool IsVisibleBottomContent
        {
            get => (bool)this.GetValue(IsVisibleBottomContentProperty);
            set => this.SetValue(IsVisibleBottomContentProperty, value);
        }

        public ICommand SwitchFullScreenModeCommand
            => new DelegateCommand(() => {
                if (this._fullScreenMode)
                {
                    this._fullScreenMode = false;
                    this.ShowTitleBar = true;
                    this.IgnoreTaskbarOnMaximize = false;
                    this.WindowState = WindowState.Normal;
                }
                else
                {
                    this._fullScreenMode = true;
                    this.ShowTitleBar = false;
                    this.IgnoreTaskbarOnMaximize = true;
                    this.WindowState = WindowState.Maximized;
                    this.ViewModel.DialogService.ToastNotify(Properties.Resources.Message_NotifyFullScreenMode);
                }
            });

        #endregion

        #region メソッド

        [InjectionConstructor]
        [LogInterceptor]
        public MainWindow(IContainerExtension containerExtension)
        {
            this.InitializeComponent();
            this.ContainerExtension = containerExtension;
            this.Localization = new LocalizationWrapper();
            this.InterTabClient = this.ContainerExtension.Resolve<InterTabClientWrapper>();
            this.Notifier = new(config =>
            {
                config.LifetimeSupervisor = new TimeAndCountBasedLifetimeSupervisor(TimeSpan.FromSeconds(AppSettingsReader.ToastLifetime), MaximumNotificationCount.FromCount(AppSettingsReader.ToastCountLimit));
                config.PositionProvider = new WindowPositionProvider(this, Corner.BottomRight, 5, 0);
                config.Dispatcher = Application.Current.Dispatcher;
                config.DisplayOptions.Width = 280;
                config.DisplayOptions.TopMost = false;
            });

            this.CommandBindings.AddRange(new[] {
                // ApplicationCommands.Close の実装
                new CommandBinding(
                    ApplicationCommands.Close,
                    (sender, e) =>
                    {
                        if (sender is Window window)
                        {
                            window.Close();
                            e.Handled = true;
                        }
                    },
                    (sender, e) =>
                    {
                        e.CanExecute = true;
                        e.Handled = true;
                    }
                ),
                new CommandBinding(
                    ActivateTextEditorCommand,
                    (sender, e) => this.FocusTextEditor()
                ),
                new CommandBinding(
                    ActivateTerminalCommand,
                    (sender, e) =>
                    {
                        if (this.IsVisibleBottomContent && this.BottomContent.SelectedIndex == 0)
                        {
                            this.IsVisibleBottomContent = false;
                        }
                        else
                        {
                            this.BottomContent.SelectedIndex = 0;
                            this.IsVisibleBottomContent = true;
                            this.FocusBottomContent();
                        }
                    }
                ),
                new CommandBinding(
                    ActivateScriptRunnerCommand,
                    (sender, e) =>
                     {
                        if (this.IsVisibleBottomContent && this.BottomContent.SelectedIndex == 1)
                        {
                            this.IsVisibleBottomContent = false;
                        }
                        else
                        {
                            this.BottomContent.SelectedIndex = 1;
                            this.IsVisibleBottomContent = true;
                            this.FocusBottomContent();
                         }
                    }
                ),
                new CommandBinding(
                    ActivateFileExplorerCommand,
                    (sender, e) => this.PerformInvokeSideContent(this.FileExplorerItem)
                ),
                new CommandBinding(
                    ActivatePropertyCommand,
                    (sender, e) => this.PerformInvokeSideContent(this.PropertyItem)
                ),
            });

            var sequence = 0u;
            User32.MENUITEMINFO createMenuItem(bool isSeparator = false)
                => new()
                {
                    cbSize = (uint)Marshal.SizeOf(typeof(User32.MENUITEMINFO)),
                    fMask = isSeparator ? User32.MenuItemInfoMask.MIIM_FTYPE : User32.MenuItemInfoMask.MIIM_STATE | User32.MenuItemInfoMask.MIIM_ID | User32.MenuItemInfoMask.MIIM_STRING,
                    fType = isSeparator ? User32.MenuItemType.MFT_SEPARATOR : User32.MenuItemType.MFT_MENUBARBREAK,
                    fState = User32.MenuItemState.MFS_ENABLED,
                    wID = ++sequence,
                    hSubMenu = IntPtr.Zero,
                    hbmpChecked = IntPtr.Zero,
                    hbmpUnchecked = IntPtr.Zero,
                    dwItemData = IntPtr.Zero,
                    dwTypeData = new(string.Empty), // 必ず文字列で初期化するように
                    cch = 0,
                    hbmpItem = IntPtr.Zero
                };
            this._miiShowMenuBar = (6, createMenuItem());
            this._miiShowToolBar = (7, createMenuItem());
            this._miiShowSideBar = (8, createMenuItem());
            this._miiShowStatusBar = (9, createMenuItem());
            this._miiSeparator = (10, createMenuItem(true));
        }

        [LogInterceptor]
        private void FocusTextEditor()
        {
            this.Dispatcher.InvokeAsync(() => this.ActiveTextEditor?.Focus());
        }

        [LogInterceptor]
        private void FocusBottomContent()
        {
            if ((((this.BottomContent.SelectedItem as TabItem)?.Content as ContentControl)?.Content as UserControl)?.FindName("ScriptInputField") is not FrameworkElement element)
                return;

            if (element.IsLoaded)
            {
                this.Dispatcher.InvokeAsync(() => element.Focus());
                return;
            }

            void elementLoaded(object sender, EventArgs e)
            {
                element.Focus();
                element.Loaded -= elementLoaded;
            }
            element.Loaded += elementLoaded;
        }

        [LogInterceptor]
        private void FocusSideContent()
        {
            if ((this.SideContent.Content as HamburgerMenuItem)?.Tag is not FrameworkElement element)
                return;

            void elementLoaded(object sender, EventArgs e)
            {
                element.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                element.Loaded -= elementLoaded;
            }
            element.Loaded += elementLoaded;
        }

        [LogInterceptor]
        private void OpenSideContent(object targetItem)
        {
            // 指定されたコンテンツを選択し、ハンバーガーメニューを開く
            this.SideContent.Content = targetItem;
            this.SideContent.Width = double.NaN;

            // グリッドの列構成を調整する
            this.SideContentColumn.Width = new(this._columnWidthCache.sideBarWidth, GridUnitType.Star);
            this.MainContentColumn.Width = new(this._columnWidthCache.contentAreaWidth, GridUnitType.Star);
        }

        [LogInterceptor]
        private void CloseSideContent()
        {
            // コンテンツの選択状態を解除し、ハンバーガーメニューを閉じる
            this.SideContent.Content = null;
            this.SideContent.Width = this.SideContent.HamburgerWidth;

            // グリッドの列構成を調整する
            this._columnWidthCache = (this.SideContentColumn.Width.Value, this.MainContentColumn.Width.Value);
            this.SideContentColumn.Width = GridLength.Auto;
            this.MainContentColumn.Width = new(1, GridUnitType.Star);
        }

        [LogInterceptor]
        private bool IsClosedSideContent()
        {
            return double.IsNaN(this.SideContent.Width) == false;
        }

        [LogInterceptor]
        private void PerformInvokeSideContent(object targetItem)
        {
            this.Settings.System.ShowSideBar = true;

            if (this.IsClosedSideContent())
            {
                // 閉じた状態の場合
                this.OpenSideContent(targetItem);
                this.FocusSideContent();
            }
            else if (targetItem?.Equals(this.SideContent.Content) != true)
            {
                // 開いた状態 かつ 非アクティブな項目が選択された場合
                // 選択された項目をアクティブにする
                this.SideContent.Content = targetItem;
                this.FocusSideContent();
            }
            else
            {
                // 開いた状態 かつ アクティブな項目が選択された 場合
                // 選択状態を解除し、ハンバーガーメニューを閉じる
                this.CloseSideContent();
                this.FocusTextEditor();
            }
        }

        [LogInterceptor]
        public void ScrollToCaret()
        {
            this.ActiveTextEditor?.ScrollToCaret();
        }

        #endregion

        #region イベントハンドラ

        [LogInterceptor]
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // フックメソッドを登録する
            this._handleSource = (HwndSource)PresentationSource.FromVisual(this);
            this._handleSource.AddHook(this.WndProc);

            // システムメニューを構築する
            var hMenu = User32.GetSystemMenu(this._handleSource.Handle, false);
            User32.InsertMenuItem(hMenu, this._miiShowMenuBar.fByPosition, true, ref this._miiShowMenuBar.lpmii);
            User32.InsertMenuItem(hMenu, this._miiShowToolBar.fByPosition, true, ref this._miiShowToolBar.lpmii);
            User32.InsertMenuItem(hMenu, this._miiShowSideBar.fByPosition, true, ref this._miiShowSideBar.lpmii);
            User32.InsertMenuItem(hMenu, this._miiShowStatusBar.fByPosition, true, ref this._miiShowStatusBar.lpmii);
            User32.InsertMenuItem(hMenu, this._miiSeparator.fByPosition, true, ref this._miiSeparator.lpmii);

            // 初期ウィンドウ向けの処理を行う
            if (this.IsNewTabHost == false)
            {
                // 表示位置を復元する
                if (this.Settings.System.SaveWindowPlacement && this.Settings.System.WindowPlacement.HasValue)
                {
                    var lpwndpl = this.Settings.System.WindowPlacement.Value;
                    if (lpwndpl.showCmd == ShowWindowCommand.SW_SHOWMINIMIZED)
                        lpwndpl.showCmd = ShowWindowCommand.SW_SHOWNORMAL;
                    User32.SetWindowPlacement(this._handleSource.Handle, ref lpwndpl);
                }

                // 既定のタブを生成する
                if (this.MainContent.Items.Count == 0)
                {
                    TabablzControl.AddItemCommand.Execute(null, this.MainContent);
                }
            }

            // リージョンにビューを設定する
            void injectRegionContent<T>(string suffix = null)
            {
                var regionName = $"{PrismConvertHelper.ConvertToRegionName<T>()}{suffix}";
                var content = this.ContainerExtension.Resolve<T>();

                try
                {
                    this.RegionManager.AddToRegion(regionName, content);
                    return;
                }
                catch (Exception e)
                {
                    this.Logger.Log($"IRegionManager.AddToRegion() が失敗しました。別メソッドで再試行します。: RegionName={regionName}", Category.Warn, e);
                }

                try
                {
                    this.RegionManager.RegisterViewWithRegion(regionName, () => content);
                    return;
                }
                catch (Exception e)
                {
                    this.Logger.Log($"IRegionManager.RegisterViewWithRegion() が失敗しました。: RegionName={regionName}", Category.Error, e);
                }
            }
            injectRegionContent<MenuBarView>("1");
            injectRegionContent<MenuBarView>("2");
            injectRegionContent<ToolBarView>();
            injectRegionContent<StatusBarView>();
            injectRegionContent<DiffContentView>();
            injectRegionContent<PrintPreviewContentView>();
            injectRegionContent<OptionContentView>();
            injectRegionContent<MaintenanceContentView>();
            injectRegionContent<AboutContentView>();
            injectRegionContent<TerminalView>();
            injectRegionContent<ScriptRunnerView>();
        }

        [LogInterceptor]
        private void Window_Closing(object sender, CancelEventArgs e)
        {
            // ダイアログの表示に備えてフォアグラウンドへ移動
            this.SetForegroundWindow();
        }

        [LogInterceptor]
        private void Window_Closed(object sender, EventArgs e)
        {
            this.Logger.Log($"ウィンドウを破棄しました。win#{this.ViewModel.Sequense}", Category.Info);

            // 表示位置を退避する
            if (this.Settings.System.SaveWindowPlacement && this._handleSource.IsDisposed == false)
            {
                var lpwndpl = new User32.WINDOWPLACEMENT();
                User32.GetWindowPlacement(this._handleSource.Handle, ref lpwndpl);
                this.Settings.System.WindowPlacement = lpwndpl;
            }

            // フックメソッドを解除する
            this._handleSource.RemoveHook(this.WndProc);

            // リソースを開放する
            this.Notifier.Dispose();

            // 他のウィンドウが存在せず、タスクトレイに存在しない場合はアプリケーションを終了する
            if (Application.Current.Windows.OfType<MainWindow>().Any() == false &&
                (this.Settings.System.EnableNotificationIcon == false ||
                 this.Settings.System.EnableResident == false))
            {
                Application.Current.Windows.OfType<Workspace>().ForEach(w => w.Close());
            }
        }

        [LogInterceptor]
        private void Window_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            void viewModel_Disposed(object sender, EventArgs e)
            {
                // NOTE: Closing イベント内で非同期処理後にイベントをキャンセルできなくなる問題 (View)
                // ViewModel の Dispose をトリガーに、View の Close メソッドを実行する。
                ((ViewModelBase)sender).Disposed -= viewModel_Disposed;
                this.Dispatcher.InvokeAsync(() => this.Close());
            }

            if (e.OldValue is ViewModelBase oldViewModel)
                oldViewModel.Disposed -= viewModel_Disposed;
            if (e.NewValue is ViewModelBase newViewModel)
                newViewModel.Disposed += viewModel_Disposed;
        }

        [LogInterceptor]
        private void Flyout_IsOpenChanged(object sender, RoutedEventArgs e)
        {
            if ((sender as Flyout)?.IsOpen != false)
                return;

            this.FocusTextEditor();
            e.Handled = true;
        }

        [LogInterceptor]
        private void SideContent_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is not bool visible || visible)
                return;
            if (this.IsClosedSideContent())
                return;

            this.CloseSideContent();
        }

        [LogInterceptor]
        private void SideContent_ItemInvoked(object sender, HamburgerMenuItemInvokedEventArgs e)
        {
            if (System.Windows.Input.MouseButtonState.Pressed == Mouse.LeftButton && e.IsItemOptions == false)
                this.PerformInvokeSideContent(e.InvokedItem);
            e.Handled = true;
        }

        [LogInterceptor]
        private void FileTreeNode_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.Handled)
                return;

            if ((e.OriginalSource as DependencyObject)?.Ancestor().FirstOrDefault(d => d is TreeViewItem) is not TreeViewItem item)
                return;

            item.IsSelected = true;
            e.Handled = true;
        }

        [LogInterceptor]
        private void FileTreeNode_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.Handled)
                return;

            var node = (FileExplorerViewModel.FileTreeNode)((TreeViewItem)sender).DataContext;
            if (node.IsEmpty)
            {
                e.Handled = true;
                return;
            }
            if (File.Exists(node.FileName))
            {
                this.ViewModel.LoadCommand.Execute(new[] { node.FileName });
                e.Handled = true;
                return;
            }
        }

        [LogInterceptor]
        private void FileTreeNode_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Handled)
                return;

            switch (e.Key)
            {
                case Key.Enter:
                {
                    var node = (FileExplorerViewModel.FileTreeNode)((TreeViewItem)sender).DataContext;
                    if (node.IsEmpty)
                    {
                        e.Handled = true;
                        return;
                    }
                    if (File.Exists(node.FileName))
                    {
                        this.ViewModel.LoadCommand.Execute(new[] { node.FileName });
                        e.Handled = true;
                        return;
                    }
                    if (Directory.Exists(node.FileName))
                    {
                        node.IsExpanded = !node.IsExpanded;
                        e.Handled = true;
                        return;
                    }
                    break;
                }
            }
        }

        [LogInterceptor]
        private void DraggableTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Handled || e.Source != e.OriginalSource)
                return;

            this.FocusTextEditor();
            e.Handled = true;
        }

        [LogInterceptor]
        private void DragablzItem_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.Handled)
                return;

            var item = (ContentControl)sender;
            item.Ancestor().OfType<DraggableTabControl>().First().SelectedItem = item.Content;
            e.Handled = true;
        }

        [LogInterceptor]
        private void TextEditor_Loaded(object sender, RoutedEventArgs e)
        {
            var textEditor = (TextEditor)sender;
            textEditor.Focus();
            textEditor.Redraw();
        }

        [LogInterceptor]
        private void ColumnSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (this.SideContentColumn.Width.Value != this.SideContentColumn.MinWidth)
                return;

            this.SideContentColumn.Width = new(this._columnWidthCache.sideBarWidth, GridUnitType.Star);
            this.MainContentColumn.Width = new(this._columnWidthCache.contentAreaWidth, GridUnitType.Star);
            this.CloseSideContent();
        }

        [LogInterceptor]
        private void RowSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (this.BottomContentRow.Height.Value != 0)
                return;

            this.IsVisibleBottomContent = false;
        }

        // NOTE: このメソッドは頻発するためトレースしない
        private IntPtr WndProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch ((User32.WindowMessage)msg)
            {
                case User32.WindowMessage.WM_INITMENUPOPUP:
                {
                    var hMenu = User32.GetSystemMenu(this._handleSource.Handle, false);
                    this._miiShowMenuBar.lpmii.dwTypeData.Assign(Properties.Resources.Command_ShowMenuBar);
                    this._miiShowToolBar.lpmii.dwTypeData.Assign(Properties.Resources.Command_ShowToolBar);
                    this._miiShowSideBar.lpmii.dwTypeData.Assign(Properties.Resources.Command_ShowSideBar);
                    this._miiShowStatusBar.lpmii.dwTypeData.Assign(Properties.Resources.Command_ShowStatusBar);
                    this._miiShowMenuBar.lpmii.fState = this.Settings.System.ShowMenuBar ? User32.MenuItemState.MFS_CHECKED : User32.MenuItemState.MFS_ENABLED;
                    this._miiShowToolBar.lpmii.fState = this.Settings.System.ShowToolBar ? User32.MenuItemState.MFS_CHECKED : User32.MenuItemState.MFS_ENABLED;
                    this._miiShowSideBar.lpmii.fState = this.Settings.System.ShowSideBar ? User32.MenuItemState.MFS_CHECKED : User32.MenuItemState.MFS_ENABLED;
                    this._miiShowStatusBar.lpmii.fState = this.Settings.System.ShowStatusBar ? User32.MenuItemState.MFS_CHECKED : User32.MenuItemState.MFS_ENABLED;
                    User32.SetMenuItemInfo(hMenu, this._miiShowMenuBar.fByPosition, true, in this._miiShowMenuBar.lpmii);
                    User32.SetMenuItemInfo(hMenu, this._miiShowToolBar.fByPosition, true, in this._miiShowToolBar.lpmii);
                    User32.SetMenuItemInfo(hMenu, this._miiShowSideBar.fByPosition, true, in this._miiShowSideBar.lpmii);
                    User32.SetMenuItemInfo(hMenu, this._miiShowStatusBar.fByPosition, true, in this._miiShowStatusBar.lpmii);
                    break;
                }

                case User32.WindowMessage.WM_SYSCOMMAND:
                {
                    var settings = this.Settings.System;
                    var wID = wParam.ToInt32();
                    if (this._miiShowMenuBar.lpmii.wID == wID)
                        settings.ShowMenuBar = !settings.ShowMenuBar;
                    if (this._miiShowToolBar.lpmii.wID == wID)
                        settings.ShowToolBar = !settings.ShowToolBar;
                    if (this._miiShowSideBar.lpmii.wID == wID)
                        settings.ShowSideBar = !settings.ShowSideBar;
                    if (this._miiShowStatusBar.lpmii.wID == wID)
                        settings.ShowStatusBar = !settings.ShowStatusBar;
                    break;
                }
            }
            return IntPtr.Zero;
        }

        #endregion

        #region 内部クラス

        public class InterTabClientWrapper : IInterTabClient
        {
            [Dependency]
            public IContainerExtension ContainerExtension { get; set; }
            [Dependency]
            public IRegionManager RegionManager { get; set; }
            [Dependency]
            public ILoggerFacade Logger { get; set; }

            [LogInterceptor]
            INewTabHost<Window> IInterTabClient.GetNewHost(IInterTabClient interTabClient, object partition, TabablzControl source)
            {
                var view = this.ContainerExtension.Resolve<MainWindow>((typeof(IRegionManager), this.RegionManager.CreateRegionManager()));
                this.Logger.Log($"タブのアンドックによりウィンドウが生成されました。win#{((MainWindowViewModel)view.DataContext).Sequense}", Category.Info);
                view.IsNewTabHost = true;

                // NOTE: IsHeaderPanelVisible = false の状態でフローティングを行うと例外が発生する現象への対策
                // ドラッグ移動中(マウスの左ボタンが押下されている間)はタブを表示する。
                // また、既存のタブコントロールにドッキングされウィンドウが消滅する場合に備え Closed イベントも監視する。
                //
                // Dragablz/Dragablz/TabablzControl.cs | 6311e72 on 16 Aug 2017 | Line 1330:
                //   _dragablzItemsControl.InstigateDrag(interTabTransfer.Item, newContainer =>
                if (view.Settings.System.ShowSingleTab == false)
                {
                    void windowMoveEnd(object sender, EventArgs e)
                    {
                        view.Settings.System.ShowSingleTab = false;
                        ((Window)sender).PreviewMouseLeftButtonUp -= windowMoveEnd;
                        ((Window)sender).Closed -= windowMoveEnd;
                    }
                    view.Settings.System.ShowSingleTab = true;
                    view.PreviewMouseLeftButtonUp += windowMoveEnd;
                    view.Closed += windowMoveEnd;
                }

                return new NewTabHost<Window>(view, view.MainContent);
            }

            [LogInterceptor]
            TabEmptiedResponse IInterTabClient.TabEmptiedHandler(TabablzControl tabControl, Window window)
                => TabEmptiedResponse.CloseWindowOrLayoutBranch;
        }

        public class LocalizationWrapper : ICSharpCode.AvalonEdit.Search.Localization
        {
            public override string MatchCaseText => Properties.Resources.Command_CaseSensitive;
            public override string MatchWholeWordsText => string.Empty;
            public override string UseRegexText => Properties.Resources.Command_UseRegex;
            public override string FindNextText => Properties.Resources.Command_FindNext;
            public override string FindPreviousText => Properties.Resources.Command_FindPrev;
            public override string ErrorText => $"{Properties.Resources.Message_NotifyErrorText}: ";
            public override string NoMatchesFoundText => Properties.Resources.Message_NotifyNoMatchesText;

            public string SwitchFindModeText => Properties.Resources.Command_SwitchFindMode;
            public string FindText => Properties.Resources.Command_Find;
            public string ReplaceText => Properties.Resources.Command_Replace;
            public string ReplaceNextText => Properties.Resources.Command_ReplaceNext;
            public string ReplaceAllText => Properties.Resources.Command_ReplaceAll;
            public string CloseText => Properties.Resources.Command_Close;
        }

        #endregion
    }
}
