# Song Scout

Song Scout is a lightweight Windows tray app for identifying music playing through the default output device.

The source is available under the [MIT license](LICENSE).

## Install and use

1. Run `SongScout-Setup-1.8.1.exe` and finish the short installer.
2. Play a song through speakers or headphones.
3. Left-click the Song Scout icon in the Windows notification area.
4. The quiet listening panel changes into the result after about eight seconds.

Right-click the tray icon to identify a song, reopen the last result, change notification behavior, or exit. Routine Windows notifications are off by default; the in-app panel still shows every result.

## Updates

Song Scout checks for a newer signed release about once a day and stays quiet when there is none. If an update is available, you can choose whether to install it. The tray menu also has **Check for updates**. Updates are downloaded from the public GitHub Releases page and installed over the existing version, keeping the same app location and preferences. The old app closes before installation, then the new app starts. Existing installations made before version 1.8.0 need one manual upgrade to gain this feature.

The update feed and installer are signed with a separate release key to prevent tampering. Windows may still warn about the unsigned publisher; the update signature does not replace Windows code signing.

The interface uses a fixed dark graphite backdrop and a silver-and-blue signal-aperture mark. A matched song displays its cover artwork above the title when Shazam supplies a usable image. Otherwise it says "Artwork unavailable" instead of showing the app logo. It keeps centered song information, custom-drawn action icons, a listening waveform, and the full-width reactive equalizer. The panel enters with a brief slide and fade, and buttons respond to hover and press. Windows reduced-motion settings simplify the motion while keeping progress visible.

## Calls and microphones

Song Scout reads the Windows output stream. It does not change, mix into, or transmit through your microphone, so Discord and other calls keep using the microphone you selected. Call audio playing through the same output device may be present in the recognition sample and can make matching less accurate.

## Resource use

The small native tray host remains idle. The recognition worker starts only after a click and exits when the result is returned. There is no continuous listening or background fingerprinting. The updater checks for a new version at most once a day and downloads an installer only after you choose to update.

## Privacy and service limits

Audio is held in memory only long enough to create a fingerprint and is not saved to disk. The fingerprint is sent to Shazam for matching. The endpoint is unofficial and has no published monthly quota, but excessive requests can be temporarily rate-limited and the endpoint may change.

## Requirements

- Windows 10 or Windows 11, 64-bit
- Internet access while identifying a song

The installer includes its runtime, so no separate framework installation is required. The application is unsigned, so Windows may show a reputation warning the first time it runs.

## Build from source

On Windows, install .NET 10 SDK, Python 3.14, and Inno Setup 6. From this folder, create a Python virtual environment and install `requirements-build.txt`. Run `python -m PyInstaller SongScoutRecognizer.spec` to build the recognition worker. Publish `TrayHost/SongScout.csproj` as a self-contained `win-x64` single file with native libraries included. Put the resulting `SongScout.exe`, the worker `dist/SongScoutRecognizer.exe`, `README.md`, and `THIRD_PARTY_NOTICES.md` in `release-package`, then compile `installer.iss` with Inno Setup. The `NuGet.Config` file supplies the WinSparkle package source.

For a public update, sign the completed installer with `prepare-appcast.ps1`. Upload the installer and generated `installer-dist/appcast.xml` to the same stable GitHub Release. The signing key belongs outside the source tree and must be backed up. Older releases do not contain the update checker, so their users need the first upgrade manually.
