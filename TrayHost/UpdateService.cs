using System.Runtime.InteropServices;

namespace SongScout;

internal sealed class UpdateService : IDisposable
{
    private const string FeedUrl = "https://github.com/MIR4ZZZ/song-scout/releases/latest/download/appcast.xml";
    private const string PublicKey = "USjcdYbIuj+WirXK6p5/60AnTpuy3DHkv+LFbBxJiFg=";
    private readonly SynchronizationContext uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
    private readonly CanShutdownCallback canShutdown;
    private readonly ShutdownRequestCallback shutdownRequested;
    private bool started;

    public UpdateService(Func<bool> mayClose, Action close)
    {
        canShutdown = () => mayClose() ? 1 : 0;
        shutdownRequested = () => uiContext.Post(_ => close(), null);
    }

    public bool Start()
    {
        try
        {
            Native.SetAppcastUrl(FeedUrl);
            if (Native.SetPublicKey(PublicKey) != 1)
                return false;
            Native.SetCanShutdownCallback(canShutdown);
            Native.SetShutdownRequestCallback(shutdownRequested);
            Native.SetAutomaticCheckForUpdates(1);
            Native.SetUpdateCheckInterval(24 * 60 * 60);
            Native.Init();
            started = true;
            return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        catch (BadImageFormatException) { return false; }
    }

    public void CheckNow()
    {
        if (started)
            Native.CheckUpdateWithUi();
    }

    public void Dispose()
    {
        if (!started)
            return;
        started = false;
        Native.Cleanup();
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CanShutdownCallback();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ShutdownRequestCallback();

    private static class Native
    {
        [DllImport("WinSparkle.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "win_sparkle_set_appcast_url", CharSet = CharSet.Ansi)]
        internal static extern void SetAppcastUrl(string url);

        [DllImport("WinSparkle.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "win_sparkle_set_eddsa_public_key", CharSet = CharSet.Ansi)]
        internal static extern int SetPublicKey(string key);

        [DllImport("WinSparkle.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "win_sparkle_set_can_shutdown_callback")]
        internal static extern void SetCanShutdownCallback(CanShutdownCallback callback);

        [DllImport("WinSparkle.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "win_sparkle_set_shutdown_request_callback")]
        internal static extern void SetShutdownRequestCallback(ShutdownRequestCallback callback);

        [DllImport("WinSparkle.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "win_sparkle_set_automatic_check_for_updates")]
        internal static extern void SetAutomaticCheckForUpdates(int enabled);

        [DllImport("WinSparkle.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "win_sparkle_set_update_check_interval")]
        internal static extern void SetUpdateCheckInterval(int seconds);

        [DllImport("WinSparkle.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "win_sparkle_init")]
        internal static extern void Init();

        [DllImport("WinSparkle.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "win_sparkle_check_update_with_ui")]
        internal static extern void CheckUpdateWithUi();

        [DllImport("WinSparkle.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "win_sparkle_cleanup")]
        internal static extern void Cleanup();
    }
}
