# Emby 4.10 compatibility — smoke test checklist

Fill this in after deploying the built DLL to a real Emby 4.10.x
instance. Every unchecked item is a blocker for merging P0-03.

## Build
- [x] dotnet build -c Release produces 0 warnings, 0 errors
- [x] Binaries show 1.3.0.0 in Version/FileVersion

## Load
- [ ] DLL copied to the plugin directory and Emby restarted
- [ ] Server log shows "Debryfin has been loaded successfully"
- [ ] Plugin appears in Dashboard → Plugins

## Configuration
- [ ] Settings page opens without 500
- [ ] API keys can be set and saved
- [ ] No NRE in logs on first configuration load

## System.Text.Json
- [ ] No FileNotFoundException / TypeLoadException in logs on start
- [ ] No assembly binding warnings related to System.Text.Json

## Real-Debrid (production, must not break)
- [ ] Play a .strm from the library successfully
- [ ] .strm.original is preserved after playback
- [ ] .strm contains the resolved URL

## AllDebrid (production, must not break)
- [ ] Play a .strm from the library successfully
- [ ] .strm.original is preserved after playback
- [ ] .strm contains the resolved URL

## Rollback
- [ ] If any check above fails: note the exact error and revert
      Debryfin.csproj to netstandard2.1 / Core 4.9.1.90 / STJ 8.0.5

## Sign-off
- Tester:
- Emby version:
- Date:
- Result: PASS / FAIL (circle one)
