using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Project_Phantom
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;
        private Microsoft.Windows.AppLifecycle.AppInstance? _instance;
        private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;
        private bool _openWhenReady;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
#if DEBUG
            // Temporary crash diagnostics; do not mark exceptions as handled.
            UnhandledException += (_, args) => System.Diagnostics.Debug.WriteLine(
                $"[WindowDebug] XAML unhandled exception; HRESULT=0x{args.Exception.HResult:X8}: {args.Message}\n{args.Exception}");
            AppDomain.CurrentDomain.UnhandledException += (_, args) => System.Diagnostics.Debug.WriteLine(
                $"[WindowDebug] Process unhandled exception; terminating={args.IsTerminating}: {args.ExceptionObject}");
#endif
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            var activation = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
            var startup = IsStartupActivation(activation) || Environment.GetCommandLineArgs().Contains("--startup");
            if (_instance is not null)
            {
                if (!startup) OpenExistingWindow();
                return;
            }
            _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            try
            {
                _instance = Microsoft.Windows.AppLifecycle.AppInstance.FindOrRegisterForKey("ProjectPhantom.Main");
                if (!_instance.IsCurrent)
                {
                    // Await without blocking the STA. Never construct a second window,
                    // tray icon, controller, or shortcut service in this process.
                    await _instance.RedirectActivationToAsync(activation);
                    Exit();
                    return;
                }
                _instance.Activated += (_, forwarded) =>
                {
                    if (!IsStartupActivation(forwarded))
                        _dispatcher.TryEnqueue(OpenExistingWindow);
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Single-instance activation failed: " + ex);
                MessageBox(IntPtr.Zero, "Phantom could not connect to its existing instance. " + ex.Message,
                    "Project Phantom", 0x10);
                Exit();
                return;
            }
            var window = new MainWindow();
            _window = window;
            await window.InitializeStartupAsync();
            if (!startup || !window.StartMinimizedToTray || _openWhenReady)
                window.OpenFromLaunch();
        }

        private void OpenExistingWindow()
        {
            if (_window is MainWindow window) window.OpenFromLaunch();
            else _openWhenReady = true;
        }

        private static bool IsStartupActivation(Microsoft.Windows.AppLifecycle.AppActivationArguments activation) =>
            activation.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.StartupTask ||
            (activation.Data is ILaunchActivatedEventArgs launch &&
                launch.Arguments.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                    .Any(argument => argument.Trim('"') == "--startup"));

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int MessageBox(IntPtr window, string text, string caption, uint type);
    }
}
