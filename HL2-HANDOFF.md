# SDR-VST3-HL2 — Project Handoff

Last updated: 2026-09-14

## Objective

Create **`nubbyless/SDR-VST3-HL2`** — an independent, side-by-side variant of our
SDR-VST3 console built for the **HL2** radio (vs. the ANAN). It must be fully
independent from SDR-VST3 at runtime: separate install folder, appdata, registry,
upgrade codes, firewall rules, update check URL — so both products can be installed
and used side by side without clobbering each other's config/database.

Three staged commits:
- **A** — Seed the new repo with a clean import of our SDR-VST3 master. ✅ done
- **B** — Identity/independence pass (rename everything SDR-VST3 → SDR-VST3-HL2
  that has a real runtime/integration consequence). ✅ done, committed, pushed
- **C** — Port mi0bot's HL2 code so the console actually supports HL2 hardware.
  ✅ done, committed, pushed (2026-09-14)

## Repositories / Layout

- **Remote:** https://github.com/nubbyless/SDR-VST3-HL2 (public, default branch `master`)
- **Local working folder:** `C:\Users\W4YNY\Documents\SDR-VST3-HL2`
  (do all HL2 work here; `C:\Users\W4YNY\Documents\thetisvst` is the untouched ANAN codebase)
- Local clone of the ANAN repo used only as reference/for shared-resource copies.

## Commits

| Commit | SHA        | What                                                                 |
|--------|------------|----------------------------------------------------------------------|
| A      | `34a7b07`  | Clean import of our `master 60940b7` via `git archive` — 12,193 files / 672 MB, no history, no build junk, no nested `wsjtx/`/`fldigi/` sources |
| B      | `79e8537`  | "SDR-VST3-HL2 identity pass: appdata, registry, install folder, upgrade codes, firewall, mutex, update URL" — 23 files, 84/84 |
| C      | _see `git log -1`_ | "Commit C: HL2 support — port mi0bot HL2 code" — 30 files, +4352/−251 (see §Commit C below) |

## Identity Pattern (what Commit B did)

- Executable stays **`Thetis.exe`** (that is what makes it a real Thetis fork).
- Separation is achieved via:
  - install folder `ProgramFiles\OpenHPSDR\SDR-VST3-HL2`
  - appdata `%APPDATA%\OpenHPSDR\SDR-VST3-HL2-x64\` (32-bit: `...\SDR-VST3-HL2\`)
  - registry `SOFTWARE\OpenHPSDR\SDR-VST3-HL2-x64`
  - per-version DBs come **for free**: `clsDBMan.cs` derives
    `_db_data_path = _app_data_path + "DB\"`, so different appdata = separate DBs.
- New UpgradeCodes (so v5.5 of SDR-VST3-HL2 replaces its own earlier installs only):
  - Product x64 `f4157dcb-19e2-4fb6-b90b-1e33a714cb31`, x86 `35e7322e-f2f7-4b4a-b86e-d56ea01d0876`
  - Bundle  x64 `0c9aeb89-8557-4d72-9953-137833287fd2`, x86 `72c0272f-45c0-484f-b58f-a910d10d0d91`
- Update check URL → `https://api.github.com/repos/nubbyless/SDR-VST3-HL2/releases/latest`

### Files changed in Commit B
`console.cs` (4 appdata paths, loading log, crash dialog, update URL, UA
`SDR-VST3-HL2-Console`), `common.cs` (crash dir `OpenHPSDR\SDR-VST3-HL2-x64`,
crash filename/header), `clsCMASIOConfig.cs`, `clsProgressLog.cs`
(registry + "SDR-VST3-HL2 Startup Log"), `clsSingleInstance.cs` (mutex
`Global\SDR-VST3-HL2_e5a2cba2-ce31-467f-ab24-892c5aa20fad` + dialogs),
`Firewall.cs` (rules `SDR-VST3-HL2 Allow IN/OUT TCP/UDP`),
`clsAudioRecordPlayback.cs` + `MemoryForm.cs` (`MyMusic\SDR-VST3-HL2`),
`setup.cs` (dialog title + recording folder), `clsDBMan.cs`
(export filenames `SDR-VST3-HL2_database_export_*`), `FreeDVReporterManager.cs`,
`frmAbout.Designer.cs`, `FldigiManager.cs` + `WsjtManager.cs` (appdata dirs),
`splash.cs` (`APPLICATION_NAME`), `titlebar.cs` (window title),
`cmASIO\hostsample.cpp` (5 ASIO registry subkeys),
`Product.wxs`, `Bundle.wxs`, `Thetis_en-us.wxl`,
`Thetis-Installer.wixproj` + `Thetis-Bundle.wixproj` (OutputName SDR-VST3-HL2,
`SDR-VST3-HL2-v*.msi/exe` copy/move, MsiPath),
`.github/workflows/release.yml` (globs `SDR-VST3-HL2-v*.exe/.msi` + release body).

## DB Loading Shortcut (decided 2026-09-14)

- The single install shortcut (desktop + Start Menu, `SDR-VST3-HL2[-x64]`)
  launches `Thetis.exe -dbid:HL2`, so the HL2 console always uses its own
  active-DB selection (`HL2_dbman_settings.json` in `DB\`).
- No second `-HL2` shortcut (mi0bot's dual-shortcut approach) — this product
  is HL2; ANAN capability stays available by running `Thetis.exe` directly
  (no args → shared `dbman_settings.json`).
- Note: `-dbid:` splits the *active-DB pointer* only; DB folders are shared
  GUID dirs. ANAN/HL2 isolation on top of the separate appdata is thus
  belt-and-suspenders, not required.

## Intentional Keeps (do NOT "fix")

- Shared skin dir `AppData\OpenHPSDR\Skins\SDRVST3` — content is identical;
  per-version DB stores the skin preference.
- Sidecar named-pipe `PIPE_BASE` ("SDRVST3.FLDIGI" / "SDRVST3.WSJTX") and
  `-r SDR-VST3 -c SDR-VST3` rig args — fork sidecars register those names.
- Cosmetic residual strings (no runtime consequence): display.cs dialogs,
  Midi2CatSetupForm, clsDBMan messages, console.resx "SDR-VST3 Only",
  frmSeqLog, MeterManager, Fldigi/Wsjt bridge header comments, ReadMe.md/docs.

## Commit C — HL2 port (done & pushed 2026-09-14)

Ports mi0bot's HL2 support into this tree (against our baseline — NOT mi0's;
our codebase has diverged from stock Thetis). Reference fork kept at
`C:\Users\W4YNY\AppData\Local\Temp\opencode\omi0bot`.

Scope — everything needed for HL2 hardware to work:

- **`console.cs`** — all 94 `HERMESLITE`/`HermesLite` sites, cross-checked
  content-identical to mi0: DDS freq switches, `CATtoVFOB`, PWR clamp 90,
  TX-atten data (`31 - txatt`, both call sites), auto-attenuator ("A-ATT"/"S-ATT")
  with `_band_change`, meter labels/`SWR_POWER`/" W" output, PA volts/amps/temp
  readouts, the big HL2 block (`computeHermesLiteTemp`, `computeHermesLitePAAmps`,
  `SetIOBoardAerialPorts`, `SetI2CPollingPause`, `AutoTuneState`/`ProtocolEvent`,
  `AutoTuningHL2`, `UpdateIOBoard`), IOBoard_update_thread + join,
  Ext10MHz/Cl2 + VAC scale, TX→RX `AutoTuningHL2(Idle)`, chkTUN auto-tune power
  handling, XVTR antenna, `ModelIsHPSDRorHermes`, `UpdateDriveLabel` HL2 only-15
  steps, `UpdateDriveLB`, `SetPowerUsingTargetDBM` HL2 drive/RadioVolume,
  VFO/mode-based audio (chkVFOATX/chkVFOBTX, ptbMic_Scroll, VAC gain), RX1/RX2
  mode-panel HL2 blocks, btnVFOSwap, CollapsedHeight, `chkEnableMultiRX_MouseDown`
  (+ designer wiring), LFrange/attenuator clamps, preamp combo case, lblPreamp
  auto-att toggle, FW version string.
- **`setup.cs` + `setup.designer.cs`** — HL2 option tab (`tpHL2Options`), I/O
  board controls (`chkHL2IOBoardPresent`, `ucIOPinsLedStripHF`, `grpIOPinState`,
  `ucOutPinsLedStripHF`), full I2C-bus access UI, external-10MHz (`chkExt10MHz` +
  `EnableCl1_10MHz`) and Cl2 clock output (`chkCl2Enable` + `ControlCl2` +
  VersaClock programming), `UpdateIOLedStrip`/`EnableIOLedStrip`, `ATTOnTX` −28
  clamp, TX tune-power HL2 conversion + range, 15 PA-wattmeter `HERMESLITE`
  cases, `handleOldPAGainSettings` standalone `HERMESLITE` case, radio-model
  option gating (`hermes_lite` entry + `removeHL2Options`), tx-buffer/PTT-hang/
  CAT-to-VFO-B/disconnect-reset options, HL2 band-volts + PS-sync, `chkSwapAudioChannels`.
- **`HPSDR/IoBoardHl2.cs`** (new) + Thetis.csproj entry; `NetI2CeX`/`I2C*` NI
  imports already present. Sidecar `chkEnableMultiRX.MouseDown` wiring added.

Everything was verified by a full **Release|x64 build: exit 0**.

### Files added/changed in Commit C
Changed: `ChannelMaster/*` (netInterface.c, network.c/.h, networkproto1.c),
`Console/Andromeda/Andromeda.cs`, `Console/CAT/CATCommands.cs`+`CATParser.cs`,
`Console/HPSDR/Alex.cs`, `NetworkIOImports.cs`, `Penny.cs`,
`clsRadioDiscovery.cs`, `Midi2CatCommands.cs`, `PSForm.cs`, `Thetis.csproj`,
`clsDiscoveredRadioPicker.cs`, `clsHardwareSpecific.cs`, `cmaster.cs`,
`console.Designer.cs`, `console.cs`, `console.resx`, `cwx.cs`, `database.cs`,
`setup.cs`, `setup.designer.cs`, `splash.cs`, `ucOCLedStrip.cs`,
`ucRadioList.cs`, `xvtr.cs`, `Midi2Cat/Midi2Cat.Data/CatCmdDb.cs`,
`Thetis-Installer/Product.wxs`.
Added: `Console/HPSDR/IoBoardHl2.cs`, `HL2-HANDOFF.md`.

Preserved (no-regression checks): SDR-VST3-HL2 rename (appdata/registry/install),
version 5.5.0.0 (`AssemblyInfo.cs:71`), `chkMeshDiagLog` left disabled in setup.cs.

## Build Toolchain (this machine)

- `msbuild` is **NOT on PATH** — use:
  `C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe`
  (pass explicitly as `-p:WixTargetsPath=` too, see below)
- dotnet SDK `10.0.302`
- WiX v3.14 at `C:\Program Files (x86)\WiX Toolset v3.14\bin\dark.exe`
- WiX targets: `C:\Program Files (x86)\MSBuild\Microsoft\WiX\v3.x\Wix.targets`
- Runtime redist `Project Files\Source\Thetis-Installer\redist\windowsdesktop-runtime-10.0.11-win-x64.exe`
  (57.2 MB) is **gitignored** — copied from thetisvst working tree for local bundle
  builds; CI downloads its own copy, so never commit it. If you pull fresh / build
  on a new machine, re-copy it from the thetisvst tree.

## Build Result (verified 2026-09-13)

- Solution restore + build exit 0 → `Project Files\bin\x64\Release\Thetis.exe`
  (0.43 MB), `Thetis.dll` (10.68 MB), `cmASIO.dll`
- Installers → `Project Files\bin\Installers\`
  `SDR-VST3-HL2-v5.5.0.0.x64.exe` (222 MB bundle), `.x64.msi` (165.9 MB),
  `SDR-VST3-HL2.msi`, `SDR-VST3-HL2.wixpdb`
- Verified via `dark.exe` decompile (`...\Temp\opencode\hl2msi\Product.wxs`):
  ProductName `SDR-VST3-HL2 (64-bit)`, UpgradeCode `{F4157DCB-...}`,
  Package Comments `Installs SDR-VST3-HL2 5.5.0.0`, INSTALLFOLDER `SDR-VST3-HL2`,
  registry `Software\OpenHPSDR\SDR-VST3-HL2-x64`, shortcut
  `SDR-VST3-HL2-x64`, firewall rules `SDR-VST3-HL2-x64 (TCP In)/(UDP In)`
- Binary spot-check: `Thetis.dll` contains `SDR-VST3-HL2-x64`,
  `SDR-VST3-HL2 Startup Log`, mutex `Global\SDR-VST3-HL2_e5a2cba2...`;
  old `OpenHPSDR\SDR-VST3-x64` absent. `cmASIO.dll` has the new ASIO keys.
  (Use raw-byte/base64 search on .NET dlls — whole-file Unicode decode can
  misalign and give false negatives.)

## NEXT UP — Post-Commit C

- Real-HL2 smoke test (run on actual HL2 hardware) — the code compiles and
  marker-parity checks pass, but nothing over-the-wire has been exercised.
- Optional: decide version naming/branding for the HL2 variant (still 5.5.0.0 —
  intentional, deferred).
- Optional: CI release workflow (`release.yml`) assumed unchanged; confirm the
  HL2 installers still build in CI (WiX paths differ from thetisvst).

## Pending User-Side Items (not blocking HL2 work)

- Manual upload of the ANAN `SDR-VST3` installers to the v5.5 GitHub release —
  user-side task carried from the earlier thread (do NOT do it from this repo).
- Decide version naming for the HL2 variant (currently still reports 5.5.0.0 —
  intentional: version/branding is a later step, not Commit B).

## How to resume

```
cd C:\Users\W4YNY\Documents\SDR-VST3-HL2
git status            # expect clean, branch master tracking origin/master
```
All three staged commits (A, B, C) are done and pushed. Next work is
hardware smoke-testing or the post-commit items above.