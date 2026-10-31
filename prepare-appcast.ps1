param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.8.4',
    [string]$KeyFile = (Join-Path $env:LOCALAPPDATA 'SongScoutRelease/update-private.key')
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$installer = Join-Path $root "installer-dist/SongScout-Setup-$Version.exe"
$packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
$signer = Join-Path $packages 'winsparkle/0.9.4/tools/winsparkle-tool.exe'
if (-not (Test-Path -LiteralPath $installer)) { throw "Installer missing: $installer" }
if (-not (Test-Path -LiteralPath $KeyFile)) { throw "Release key missing: $KeyFile" }
if (-not (Test-Path -LiteralPath $signer)) { throw "WinSparkle signing tool missing: $signer" }

$signed = (& $signer sign -v -f $KeyFile $installer | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $signed -notmatch 'sparkle:edSignature="([^"]+)" length="(\d+)"') {
    throw "Could not sign installer: $signed"
}
$signature = $Matches[1]
$length = [long]$Matches[2]
if ($length -ne (Get-Item -LiteralPath $installer).Length) { throw 'Signed length does not match installer.' }
$publicKey = 'USjcdYbIuj+WirXK6p5/60AnTpuy3DHkv+LFbBxJiFg='
$verification = (& $signer verify -p $publicKey -s $signature $installer | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw "App's update key cannot verify installer: $verification" }

$url = "https://github.com/MIR4ZZZ/song-scout/releases/download/v$Version/SongScout-Setup-$Version.exe"
$notes = "https://github.com/MIR4ZZZ/song-scout/releases/tag/v$Version"
$published = [DateTimeOffset]::UtcNow.ToString('r', [Globalization.CultureInfo]::InvariantCulture)
$feed = @"
<?xml version="1.0" encoding="UTF-8"?>
<rss version="2.0" xmlns:sparkle="http://www.andymatuschak.org/xml-namespaces/sparkle">
  <channel>
    <title>Song Scout updates</title>
    <description>Signed Song Scout releases for Windows</description>
    <language>en</language>
    <item>
      <title>Song Scout $Version</title>
      <sparkle:version>$Version.0</sparkle:version>
      <sparkle:releaseNotesLink>$notes</sparkle:releaseNotesLink>
      <pubDate>$published</pubDate>
      <enclosure url="$url" length="$length" type="application/octet-stream" sparkle:edSignature="$signature" sparkle:installerArguments="/SILENT /SP-" />
    </item>
  </channel>
</rss>
"@
[xml]$parsed = $feed
if ($parsed.rss.channel.item.enclosure.length -ne [string]$length) { throw 'Appcast validation failed.' }
$path = Join-Path $root 'installer-dist/appcast.xml'
[IO.File]::WriteAllText($path, $feed, [Text.UTF8Encoding]::new($false))
Write-Output "Signed update feed: $path"
