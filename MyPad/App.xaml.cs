﻿using MyBase;
using MyBase.Logging;
using MyBase.Wpf.CommonDialogs;
using Prism.Ioc;
using Prism.Mvvm;
using Prism.Services.Dialogs;
using Prism.Unity;
using QuickConverter;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using Vanara.PInvoke;
using WPFLocalizeExtension.Providers;

namespace MyPad
{
    /// <summary>
    /// アプリケーションのエントリーポイントとなるクラスを表します。
    /// </summary>
    public partial class App : PrismApplication
    {
        /// <summary>
        /// ロガーを取得します。
        /// </summary>
        public ILoggerFacade Logger { get; }

        /// <summary>
        /// プロダクト情報を取得します。
        /// </summary>
        public IProductInfo ProductInfo { get; }

        /// <summary>
        /// アプリケーションの共有情報を取得します。
        /// </summary>
        public SharedDataStore SharedDataStore { get; }

        /// <summary>
        /// このクラスの新しいインスタンスを生成します。
        /// </summary>
        public App()
        {
            this.Logger = new CompositeLogger(
                new DebugLogger(),
                new NLogger() {
                    PublisherType = typeof(ILoggerFacadeExtension),
                    ConfigurationFactory = () =>
                    {
                        var headerText = new StringBuilder();
                        headerText.AppendLine($"# {this.ProductInfo.Product} ${{var:CTG}} Log");
                        headerText.AppendLine($"# {Environment.OSVersion} - CLR {Environment.Version}");
                        headerText.AppendLine("# ${environment:PROCESSOR_ARCHITECTURE} - ${environment:PROCESSOR_IDENTIFIER}");
                        headerText.AppendLine("# ${environment:COMPUTERNAME}");
                        headerText.Append("##");

                        var header = new NLog.Layouts.CsvLayout();
                        header.Delimiter = NLog.Layouts.CsvColumnDelimiterMode.Tab;
                        header.Quoting = NLog.Layouts.CsvQuotingMode.Nothing;
                        header.Columns.Add(new NLog.Layouts.CsvColumn(string.Empty, headerText.ToString()));

                        var layout = new NLog.Layouts.CsvLayout();
                        layout.Delimiter = NLog.Layouts.CsvColumnDelimiterMode.Tab;
                        layout.Quoting = NLog.Layouts.CsvQuotingMode.Nothing;
                        layout.Header = header;
                        layout.Columns.Add(new NLog.Layouts.CsvColumn(string.Empty, "${longdate}"));
                        layout.Columns.Add(new NLog.Layouts.CsvColumn(string.Empty, "${environment-user}"));
                        layout.Columns.Add(new NLog.Layouts.CsvColumn(string.Empty, $"{this.ProductInfo.Version}"));
                        layout.Columns.Add(new NLog.Layouts.CsvColumn(string.Empty, "${processid}"));
                        layout.Columns.Add(new NLog.Layouts.CsvColumn(string.Empty, "${threadid}"));
                        layout.Columns.Add(new NLog.Layouts.CsvColumn(string.Empty, "${message}"));

                        var file = new NLog.Targets.FileTarget();
                        file.Encoding = Encoding.UTF8;
                        file.Footer = "${newline}";
                        file.FileName = "${var:DIR}/${var:CTG}.log";
                        file.ArchiveFileName = "${var:DIR}/archive/{#}.${var:CTG}.log";
                        file.ArchiveEvery = NLog.Targets.FileArchivePeriod.Day;
                        file.ArchiveNumbering = NLog.Targets.ArchiveNumberingMode.Date;
                        file.MaxArchiveFiles = 10;
                        file.Layout = layout;

                        var memory = new NLog.Targets.MemoryTarget();
                        memory.Layout = layout;

                        var config = new NLog.Config.LoggingConfiguration();
                        config.AddTarget(nameof(file), file);
                        config.AddTarget(nameof(memory), memory);
                        config.LoggingRules.Add(new NLog.Config.LoggingRule("*", NLog.LogLevel.Trace, file));
                        config.LoggingRules.Add(new NLog.Config.LoggingRule("*", NLog.LogLevel.Trace, memory));
                        config.Variables.Add("DIR", this.SharedDataStore.LogDirectoryPath);
                        return config;
                    },
                    CreateLoggerHook = (logger, category) =>
                    {
                        // ログの種類ごとにファイルを切り替える
                        logger.Factory.Configuration.Variables.Add("CTG", category.ToString());
                        logger.Factory.ReconfigExistingLoggers();
                    },
                });
            this.ProductInfo = new ProductInfo();
            this.SharedDataStore = new(this.Logger, this.ProductInfo, Process.GetCurrentProcess());
            UnhandledExceptionObserver.Observe(this, this.Logger, this.ProductInfo);
        }

        /// <summary>
        /// アプリケーションの開始直後に行う処理を定義します。
        /// </summary>
        /// <param name="e">イベントの情報</param>
        [LogInterceptor]
        protected override void OnStartup(StartupEventArgs e)
        {
            this.Logger.Log($"アプリケーションを開始しました。", Category.Info);
            this.Logger.Debug($" アプリケーションを開始しました。: Args=[{string.Join(", ", e.Args)}]");

            Initializer.InitEncoding();
            Initializer.InitQuickConverter();

            this.SharedDataStore.CommandLineArgs = e.Args;

            var handle = this.GetOtherProcessHandle(this.SharedDataStore.Process);
            if (handle.IsNull == false)
            {
                this.SendData(this.SharedDataStore.Process, handle, this.SharedDataStore.CommandLineArgs);
                this.Shutdown(0);
                return;
            }

            this.SharedDataStore.CreateTempDirectory();

            var cachedDirectories = new DirectoryInfo(this.ProductInfo.Temporary)
                .EnumerateDirectories()
                .Where(i => i.FullName != this.SharedDataStore.TempDirectoryPath)
                .Select(i =>
                {
                    var result = DateTime.TryParseExact(Path.GetFileName(i.FullName), "yyyyMMddHHmmssfff", CultureInfo.CurrentCulture, DateTimeStyles.None, out var value);
                    return (result, value, info: i);
                });

            if (cachedDirectories.Any())
            {
                const int LOOP_DELAY = 500;

                try
                {
                    // 残存する一時フォルダの隠し属性を外す
                    foreach (var info in cachedDirectories.Select(t => t.info))
                        info.Attributes &= ~FileAttributes.Hidden;

                    // 残存する一時フォルダのうち、指定の期間を超えたものを削除する
                    var basis = this.SharedDataStore.Process.StartTime.AddDays(-1 * AppSettingsReader.CacheLifetime);
                    foreach (var info in cachedDirectories
                        .Where(t => t.result == false || t.value < basis || t.info.EnumerateFileSystemInfos().Any() == false)
                        .Select(t => t.info))
                    {
                        Directory.Delete(info.FullName, true);
                        while (Directory.Exists(info.FullName))
                            Thread.Sleep(LOOP_DELAY);
                    }
                    this.Logger.Debug($"保存期限を過ぎた不要な一時フォルダを削除しました。");
                }
                catch (Exception ex)
                {
                    this.Logger.Log("保存期限を過ぎた不要な一時フォルダの削除に失敗しました。", Category.Warn, ex);
                }

                this.SharedDataStore.CachedDirectories = cachedDirectories.Select(t => t.info.FullName).ToList();
            }

            base.OnStartup(e);
        }

        /// <summary>
        /// View から ViewModel を生成するための規則を定義します。
        /// </summary>
        [LogInterceptor]
        protected override void ConfigureViewModelLocator()
        {
            ViewModelLocationProvider.SetDefaultViewModelFactory((view, viewModelType) => {
                // 多言語化の初期設定を行う
                if (view is DependencyObject obj)
                    Initializer.InitWPFLocalizeExtension(obj);

                // ViewModel のインスタンスを生成する
                var viewModel = this.Container.Resolve(viewModelType);
                return viewModel;
            });
        }

        /// <summary>
        /// DI コンテナに登録される型とインスタンスを定義します。
        /// </summary>
        /// <param name="containerRegistry">DI コンテナ</param>
        [LogInterceptor]
        protected override void RegisterTypes(IContainerRegistry containerRegistry)
        {
            // シングルトン
            containerRegistry.RegisterInstance(this.Logger);
            containerRegistry.RegisterInstance(this.ProductInfo);
            containerRegistry.RegisterInstance(this.SharedDataStore);
            containerRegistry.RegisterSingleton<ICommonDialogService, CommonDialogService>();
            containerRegistry.RegisterSingleton<Models.Settings>();
            containerRegistry.RegisterSingleton<Models.SyntaxService>();

            // ファクトリー
            containerRegistry.Register<ViewModels.TextEditorViewModel>();
            containerRegistry.Register<ViewModels.FileExplorerViewModel>();
            containerRegistry.Register<ViewModels.FileExplorerViewModel.FileTreeNode>();
            containerRegistry.Register<Views.MainWindow.InterTabClientWrapper>();

            // ダイアログ
            containerRegistry.RegisterDialogWindow<PrismDialogWindowWrapper>();
            containerRegistry.RegisterDialog<Views.Dialogs.NotifyDialog, ViewModels.Dialogs.NotifyDialogViewModel>();
            containerRegistry.RegisterDialog<Views.Dialogs.WarnDialog, ViewModels.Dialogs.WarnDialogViewModel>();
            containerRegistry.RegisterDialog<Views.Dialogs.ConfirmDialog, ViewModels.Dialogs.ConfirmDialogViewModel>();
            containerRegistry.RegisterDialog<Views.Dialogs.CancelableConfirmDialog, ViewModels.Dialogs.CancelableConfirmDialogViewModel>();
            containerRegistry.RegisterDialog<Views.Dialogs.ChangeLineDialog, ViewModels.Dialogs.ChangeLineDialogViewModel>();
            containerRegistry.RegisterDialog<Views.Dialogs.ChangeEncodingDialog, ViewModels.Dialogs.ChangeEncodingDialogViewModel>();
            containerRegistry.RegisterDialog<Views.Dialogs.ChangeSyntaxDialog, ViewModels.Dialogs.ChangeSyntaxDialogViewModel>();
            containerRegistry.RegisterDialog<Views.Dialogs.SelectDiffFilesDialog, ViewModels.Dialogs.SelectDiffFilesDialogViewModel>();
        }

        /// <summary>
        /// アプリケーションのメインウィンドウを生成します。
        /// </summary>
        /// <returns>ウィンドウのインスタンス</returns>
        [LogInterceptor]
        protected override Window CreateShell()
        {
            var settings = this.Container.Resolve<Models.Settings>();
            settings.Load();
            this.Container.Resolve<Models.SyntaxService>().Initialize(settings.IsDifferentVersion());

            if (settings.IsDifferentVersion())
                this.Logger.Debug($"アプリケーションのバージョンが更新されました。: Old={settings.Version}, New={this.ProductInfo.Version}");

            var shell = this.Container.Resolve<Views.Workspace>();
            shell.Title = this.SharedDataStore.Identifier;
            shell.Closed += (sender, e) => this.Container?.Resolve<Models.Settings>()?.Save();
            return shell;
        }

        /// <summary>
        /// アプリケーションの終了時に行う処理を定義します。
        /// </summary>
        /// <param name="e">イベントの情報</param>
        [LogInterceptor]
        protected override void OnExit(ExitEventArgs e)
        {
            const int LOOP_DELAY = 500;

            if (Directory.Exists(this.SharedDataStore.TempDirectoryPath))
            {
                try
                {
                    Directory.Delete(this.SharedDataStore.TempDirectoryPath, true);
                    while (Directory.Exists(this.SharedDataStore.TempDirectoryPath))
                        Thread.Sleep(LOOP_DELAY);
                    this.Logger.Debug($"一時フォルダを削除しました。: Path={this.SharedDataStore.TempDirectoryPath}");
                }
                catch (Exception ex)
                {
                    this.Logger.Log($"一時フォルダの削除に失敗しました。: Path={this.SharedDataStore.TempDirectoryPath}", Category.Warn, ex);
                }
            }

            base.OnExit(e);
            this.Logger.Log($"アプリケーションを終了しました。", Category.Info);
            this.Logger.Debug($"アプリケーションを終了しました。: ExitCode={e.ApplicationExitCode}");
        }

        /// <summary>
        /// 別プロセスで起動中の同一アプリケーションのウィンドウハンドルを取得します。
        /// </summary>
        /// <param name="sourceProcess">比較元のプロセス</param>
        /// <returns>ウィンドウハンドル</returns>
        [LogInterceptor]
        private HWND GetOtherProcessHandle(Process sourceProcess)
        {
            // 本アプリはタスクバー上の通知アイコンを内包した仮ウィンドウが MainWindow に指定されている。
            // このウィンドウは描画されないため Process.MainWindowHandle からハンドルを取得できない。
            // すべてのハンドルを列挙し、ウィンドウテキストが一致するハンドルから特定する。

            var (hWnd, lpdwProcessId) = (HWND.NULL, 0u);
            var result = User32.EnumWindows(new User32.EnumWindowsProc((_hWnd, _) =>
            {
                try
                {
                    // プロセスの情報を比較する
                    User32.GetWindowThreadProcessId(_hWnd, out var _lpdwProcessId);
                    var process = Process.GetProcessById((int)_lpdwProcessId);
                    if (sourceProcess.Id == _lpdwProcessId)
                        return true;
                    if (sourceProcess.ProcessName != process.ProcessName)
                        return true;

                    // ウィンドウテキストを比較する
                    // 本アプリケーションでは実質的に識別子として使用される
                    var lpString = new StringBuilder(256);
                    User32.GetWindowText(_hWnd, lpString, lpString.Capacity);
                    if (lpString.ToString().Contains(this.SharedDataStore.Identifier) == false)
                        return true;

                    // ハンドルを保持する
                    (hWnd, lpdwProcessId) = (_hWnd, _lpdwProcessId);
                    return false;
                }
                catch
                {
                    return true;
                }
            }), IntPtr.Zero);

            if (result)
                this.Logger.Debug($"起動中の同一アプリケーションは存在しませんでした。: ProcessName={sourceProcess?.ProcessName}");
            else
                this.Logger.Debug($"起動中の同一アプリケーションのウィンドウハンドルを取得しました。: ProcessName={sourceProcess?.ProcessName}, lpdwProcessID={lpdwProcessId}, hWnd=0x{hWnd.DangerousGetHandle():X)}");
            return hWnd;
        }

        /// <summary>
        /// 指定されたウィンドウハンドルに対してデータを送信します。
        /// </summary>
        /// <param name="sourceProcess">送信元のプロセス</param>
        /// <param name="destinationHandle">送信先のウィンドウハンドル</param>
        /// <param name="data">送信されるデータ</param>
        /// <param name="separator">データの区切りを示す文字</param>
        /// <returns>ウィンドウプロシージャの戻り値</returns>
        [LogInterceptor]
        private IntPtr SendData(Process sourceProcess, HWND destinationHandle, IEnumerable<string> data, char separator = '\t')
        {
            var lpData = data.Any() ? string.Join(separator, data) : string.Empty;
            var structure = new COPYDATASTRUCT
            {
                dwData = IntPtr.Zero,
                cbData = Encoding.UTF8.GetByteCount(lpData) + 1,
                lpData = lpData,
            };

            var lParam = Marshal.AllocHGlobal(Marshal.SizeOf(structure));
            Marshal.StructureToPtr(structure, lParam, false);
            var msg = User32.WindowMessage.WM_COPYDATA;
            this.Logger.Debug($"ウィンドウメッセージを送信します。: hWnd=0x{destinationHandle.DangerousGetHandle():X}, msg={msg}, data=[{string.Join(", ", data)}]");

            return User32.SendMessage(destinationHandle, (uint)msg, sourceProcess.Handle, lParam);
        }

        /// <summary>
        /// 初期化処理を行うためのメソッドを提供します。
        /// </summary>
        private static class Initializer
        {
            /// <summary>
            /// 文字コードの初期設定を行います。
            /// </summary>
            [LogInterceptor]
            public static void InitEncoding()
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            }

            /// <summary>
            /// <see cref="QuickConverter"/> の初期設定を行います。
            /// </summary>
            [LogInterceptor]
            public static void InitQuickConverter()
            {
#pragma warning disable IDE0049
                EquationTokenizer.AddNamespace(typeof(System.Object));                           // System                  : System.Runtime.dll
                EquationTokenizer.AddNamespace(typeof(System.IO.Path));                          // System.IO               : System.Runtime.dll
                EquationTokenizer.AddNamespace(typeof(System.Text.Encoding));                    // System.Text             : System.Runtime.dll
                EquationTokenizer.AddNamespace(typeof(System.Reflection.Assembly));              // System.Reflection       : System.Runtime.dll
                EquationTokenizer.AddNamespace(typeof(System.Windows.Point));                    // System.Windows          : WindowsBase.dll
                EquationTokenizer.AddNamespace(typeof(System.Windows.UIElement));                // System.Windows          : PresentationCore.dll
                EquationTokenizer.AddNamespace(typeof(System.Windows.Window));                   // System.Windows          : PresentationFramework.dll
                EquationTokenizer.AddNamespace(typeof(System.Windows.Input.Key));                // System.Windows.Input    : WindowsBase.dll
                EquationTokenizer.AddNamespace(typeof(System.Windows.Input.Cursor));             // System.Windows.Input    : PresentationCore.dll
                EquationTokenizer.AddNamespace(typeof(System.Windows.Input.KeyboardNavigation)); // System.Windows.Input    : PresentationFramework.dll
                EquationTokenizer.AddNamespace(typeof(System.Windows.Controls.Control));         // System.Windows.Controls : PresentationFramework.dll
                EquationTokenizer.AddNamespace(typeof(System.Windows.Media.Brush));              // System.Windows.Media    : PresentationFramework.dll
                EquationTokenizer.AddNamespace(typeof(System.Linq.Enumerable));                  // System.Linq             : System.Linq.dll
                EquationTokenizer.AddExtensionMethods(typeof(System.Linq.Enumerable));           // System.Linq             : System.Linq.dll
#pragma warning restore IDE0049
            }

            /// <summary>
            /// 指定された View のインスタンスに対する <see cref="WPFLocalizeExtension"/> の初期設定を行います。
            /// </summary>
            /// <param name="view">View のインスタンス</param>
            [LogInterceptor]
            public static void InitWPFLocalizeExtension(DependencyObject view)
            {
                ResxLocalizationProvider.SetDefaultAssembly(view, nameof(MyPad));
                ResxLocalizationProvider.SetDefaultDictionary(view, nameof(MyPad.Properties.Resources));
            }
        }

        /// <summary>
        /// Prism によって表示されるダイアログウィンドウの基底クラスを置き換えます。
        /// </summary>
        private class PrismDialogWindowWrapper : MahApps.Metro.Controls.MetroWindow, IDialogWindow
        {
            IDialogResult IDialogWindow.Result { get; set; }

            /// <summary>
            /// このクラスの新しいインスタンスを生成します。
            /// </summary>
            [LogInterceptor]
            public PrismDialogWindowWrapper()
            {
                this.Loaded += this.Window_Loaded;
            }

            /// <summary>
            /// ウィンドウがロードされたときに行う処理を定義します。
            /// </summary>
            /// <param name="sender">イベントの発生源</param>
            /// <param name="e">イベントの情報</param>
            [LogInterceptor]
            private void Window_Loaded(object sender, RoutedEventArgs e)
            {
                if (this.DataContext is IDialogAware dialogAware)
                    this.Title = dialogAware.Title;
                this.Loaded -= this.Window_Loaded;
            }
        }
    }
}
