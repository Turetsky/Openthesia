using Veldrid.Sdl2;
using Veldrid;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Veldrid.StartupUtilities;
using System.Numerics;
using ImGuiNET;
using Vanara.PInvoke;
using Openthesia.Core;
using Openthesia.Core.Plugins;
using Openthesia.Settings;

namespace Openthesia;

class Program
{
    public static bool IsRunning = true;
    public static Sdl2Window _window;
    private static GraphicsDevice _gd;
    private static CommandList _cl;
    private static ImGuiController _controller;
    private static Vector3 _clearColor = new(0.45f, 0.55f, 0.6f);

    // Disable Windows press-and-hold gesture detection so touch taps register
    // instantly instead of being held back ~500ms while Windows decides whether
    // the tap is a long-press / right-click.
    private const uint WM_TABLET_QUERYSYSTEMGESTURESTATUS = 0x02CC;
    private const int TouchDisableFlags =
        0x00000001  // TABLET_DISABLE_PRESSANDHOLD
      | 0x00000008  // TABLET_DISABLE_PENTAPFEEDBACK
      | 0x00000010  // TABLET_DISABLE_PENBARRELFEEDBACK
      | 0x00010000  // TABLET_DISABLE_FLICKS
      | 0x00080000; // TABLET_DISABLE_SMOOTHSCROLLING

    private const int GWLP_WNDPROC = -4;

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private static WndProcDelegate? _touchWndProc;
    private static IntPtr _originalWndProc;

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW", CharSet = CharSet.Unicode)]
    private static extern IntPtr CallWindowProcW(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private static IntPtr TouchAwareWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_TABLET_QUERYSYSTEMGESTURESTATUS)
            return (IntPtr)TouchDisableFlags;
        return CallWindowProcW(_originalWndProc, hWnd, msg, wParam, lParam);
    }

    private static void InstallTouchFix(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        _touchWndProc = TouchAwareWndProc;
        IntPtr fnPtr = Marshal.GetFunctionPointerForDelegate(_touchWndProc);
        _originalWndProc = SetWindowLongPtrW(hwnd, GWLP_WNDPROC, fnPtr);
    }

    [STAThread]
    static void Main(string[] args)
    {
        User32.SetProcessDPIAware();

        VeldridStartup.CreateWindowAndGraphicsDevice(
            new WindowCreateInfo(50, 50, 1280, 720, WindowState.Maximized, $"Openthesia {ProgramData.ProgramVersion}"),
            new GraphicsDeviceOptions(false, null, true, ResourceBindingModel.Improved, true, true),
            out _window,
            out _gd);

        InstallTouchFix(_window.Handle);

        _cl = _gd.ResourceFactory.CreateCommandList();
        _controller = new ImGuiController(_gd, _gd.MainSwapchain.Framebuffer.OutputDescription, _window.Width, _window.Height);

        _window.Resized += () =>
        {
            int minWidth = (int)(1280 * FontController.DSF);
            int minHeigth = (int)(720 * FontController.DSF);

            if (_window.Width < minWidth)
                _window.Width = minWidth;

            if (_window.Height < minHeigth)
                _window.Height = minHeigth;

            _gd.MainSwapchain.Resize((uint)_window.Width, (uint)_window.Height);
            _controller.WindowResized(_window.Width, _window.Height);
        };

        var stopwatch = Stopwatch.StartNew();
        float deltaTime = 0f;

        ImGuiController.LoadImages(_gd, _controller);
        ProgramData.Initialize();

        Application app = new();

        while (_window.Exists)
        {
            deltaTime = stopwatch.ElapsedTicks / (float)Stopwatch.Frequency;
            stopwatch.Restart();
            InputSnapshot snapshot = _window.PumpEvents();
            if (!_window.Exists) { break; }
            _controller.Update(deltaTime, snapshot);

            if (ImGui.IsKeyPressed(ImGuiKey.F11, false))
            {
                var windowsState = _window.WindowState == WindowState.BorderlessFullScreen 
                    ? WindowState.Normal 
                    : WindowState.BorderlessFullScreen;
                _window.WindowState = windowsState;
            }

            if (CoreSettings.SoundEngine == Enums.SoundEngine.Plugins)
            {
                if (VstPlayer.PluginsChain?.PluginInstrument is VstPlugin instrument)
                {
                    instrument.PluginWindow.PumpEvents();
                }
                foreach (var plugin in VstPlayer.PluginsChain.FxPlugins)
                {
                    if (plugin is VstPlugin plug)
                    {
                        plug.PluginWindow.PumpEvents();
                    }
                }
            }

            app.OnUpdate();
            if (!app.IsRunning())
            {
                break;
            }

            _cl.Begin();
            _cl.SetFramebuffer(_gd.MainSwapchain.Framebuffer);
            _cl.ClearColorTarget(0, new RgbaFloat(_clearColor.X, _clearColor.Y, _clearColor.Z, 1f));
            _controller.Render(_gd, _cl);
            _cl.End();
            _gd.SubmitCommands(_cl);
            _gd.SwapBuffers(_gd.MainSwapchain);
        }

        ProgramData.SaveSettings();

        _gd.WaitForIdle();
        _controller.Dispose();
        _cl.Dispose();
        _gd.Dispose();
        Process.GetCurrentProcess().Kill(); // temporary solution since process doesn't close when using ASIO4ALL
    }
}