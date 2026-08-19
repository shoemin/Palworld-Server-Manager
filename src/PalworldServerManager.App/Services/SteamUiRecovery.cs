using System.Diagnostics;
using System.Windows;
using PalworldServerManager.Core.Services;

namespace PalworldServerManager.App.Services;

internal static class SteamUiRecovery
{
    public static bool ConfirmPreflight(Window owner, AppServices services)
    {
        if (services.SteamLocator.IsSteamClientRunning())
        {
            services.Logger.Info("Steam preflight: Steam desktop client is running. Login/account ownership is not automatically verified; continuing with anonymous SteamCMD.");
            return true;
        }

        services.Logger.Warning("Steam preflight: Steam desktop client is not running.");
        var result = MessageBox.Show(owner,
            "Steam is not currently running.\n\n" +
            "Palworld's dedicated server is normally installed with anonymous SteamCMD, so Steam does not strictly need to be open. However, during field testing on this PC, SteamCMD exit code 7 was resolved after Steam was opened and signed in.\n\n" +
            "For the most reliable test, choose Yes to open Steam now. Sign in and confirm Palworld is available in your library, then return here.\n\n" +
            "Yes = Open Steam\nNo = Continue with anonymous SteamCMD\nCancel = Cancel this operation",
            "Steam Preflight", MessageBoxButton.YesNoCancel, MessageBoxImage.Information);

        if (result == MessageBoxResult.Cancel)
        {
            services.Logger.Info("Steam preflight canceled by user.");
            return false;
        }
        if (result == MessageBoxResult.No)
        {
            services.Logger.Info("Steam preflight: user chose to continue while Steam desktop client is not running.");
            return true;
        }

        var steamExe = services.SteamLocator.FindSteamClientExecutable();
        if (steamExe is null)
        {
            MessageBox.Show(owner,
                "The manager could not locate steam.exe in the expected Steam installation locations. Open Steam manually, sign in, then retry this operation.\n\nNo files have been downloaded or changed by this operation yet.",
                "Steam Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
            services.Logger.Warning("Steam preflight: user requested Steam launch, but steam.exe was not found in expected locations.");
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(steamExe) { UseShellExecute = true });
            services.Logger.Info($"Steam preflight: launched Steam desktop client from '{steamExe}'.");
        }
        catch (Exception ex)
        {
            services.Logger.Error("Steam preflight: failed to launch Steam desktop client.", ex);
            MessageBox.Show(owner,
                "Steam could not be started automatically. Open Steam manually, sign in, then retry this operation.\n\n" + ex.Message,
                "Could Not Start Steam", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        MessageBox.Show(owner,
            "Steam has been started. Sign in to the account you normally use for Palworld and wait for Steam to finish connecting.\n\nThe manager cannot reliably verify Steam account ownership or sign-in state without separate account/API integration. Click OK when Steam is ready; the server download will still use anonymous SteamCMD as documented by Palworld.",
            "Finish Steam Sign-In", MessageBoxButton.OK, MessageBoxImage.Information);

        var running = services.SteamLocator.IsSteamClientRunning();
        services.Logger.Info($"Steam preflight after launch prompt: Steam desktop client running={running}.");
        if (!running)
        {
            MessageBox.Show(owner,
                "Steam still does not appear to be running. The operation has been canceled so you can finish starting/signing in to Steam and try again.",
                "Steam Not Ready", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return true;
    }

    public static bool PromptRetryAfterFailure(Window owner, AppServices services, SteamCmdException exception)
    {
        if (!exception.SuggestSteamClientRecovery) return false;

        services.Logger.Warning($"SteamCMD exit code {exception.ExitCode} reached UI recovery path. SteamRunning={services.SteamLocator.IsSteamClientRunning()}.");
        var retry = MessageBox.Show(owner,
            "SteamCMD exited with code 7.\n\n" +
            "On this test PC, that failure was resolved by opening Steam and signing in before retrying. Palworld's dedicated-server download itself uses anonymous SteamCMD, so this is being treated as a recovery step rather than a mandatory ownership check.\n\n" +
            "Would you like to run the Steam preflight now and retry the entire operation once?",
            "SteamCMD Code 7", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        return retry == MessageBoxResult.Yes && ConfirmPreflight(owner, services);
    }
}
