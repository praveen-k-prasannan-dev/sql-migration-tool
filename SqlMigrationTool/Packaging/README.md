# Packaging for the Microsoft Store

Everything needed to build the MSIX package lives in this folder.

| File | Purpose |
|---|---|
| `Package.appxmanifest` | Manifest template. `$...$` tokens are filled by the build. |
| `StoreIdentity.json` | Package identity from Partner Center. Holds `-Dev` placeholders until the reserved identity is entered. |
| `Build-Msix.ps1` | Builds `out/SqlMigrationTool_<version>_x64.msix`. |
| `Generate-Assets.ps1` | Regenerates `Assets/` (Store and tile logos) from the app logo. |
| `Assets/` | Generated logos (24 PNGs). |

Requirements: .NET 8 SDK and the Windows 10/11 SDK (provides `makeappx.exe` and `makepri.exe`).

## Build

```powershell
.\Build-Msix.ps1
```

The version comes from `<Version>` in `SqlMigrationTool.csproj`. The MSIX version is that version with a
fourth part of `0`, as the Store requires.

## Test the packaged app locally

With Windows **Developer Mode** turned on (Settings → System → For developers), no certificate is needed:

```powershell
.\Build-Msix.ps1 -Register      # builds, then installs the unpacked package for your user
```

Start **SQL Migration Tool** from the Start menu and check:

- [ ] The splash screen shows **Version 2.0.0**, then the Connect window opens
- [ ] Connect to a server, tick *Save profile*, close the app and reopen it: the profile is listed with its password
- [ ] Back up a database to a folder of your choice (e.g. the Desktop) with *Also write a cleanup script* ticked
- [ ] Install that backup into an empty database, and *Preview only* into another
- [ ] `error.log` and `preview_install.sql` appear in the backup folder

Saved profiles of the packaged app live in the package's private storage
(`%LocalAppData%\Packages\<package family>\LocalCache\Roaming\SqlMigrationTool`), separate from the
portable version's `%AppData%\SqlMigrationTool`. This is normal for MSIX apps.

Remove the test install with:

```powershell
Get-AppxPackage *SQLMigrationTool* | Remove-AppxPackage
```

## Submit to the Store

1. In Partner Center, open the app → **Product management → Product identity** and copy
   `Package/Identity/Name`, `Package/Identity/Publisher` and `Package/Properties/PublisherDisplayName`
   into `StoreIdentity.json`.
2. Run `.\Build-Msix.ps1`. The placeholder warning must be gone.
3. Optional: run the Windows App Certification Kit on the package (needs an elevated prompt):
   `appcert.exe test -appxpackagepath out\SqlMigrationTool_2.0.0.0_x64.msix -reportoutputpath out\wack.xml`
4. In the submission's **Packages** page, upload the `.msix`. Leave it unsigned: the Store signs it.
5. Listing text and the privacy policy are in `docs/` at the repository root.
