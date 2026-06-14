# ALTs Tools — macOS

macOS port of the Windows WPF app (`../RefreshToAccess2`), rebuilt on
**Avalonia UI** with a **Material You 3** design (purple, light default,
runtime light/dark + accent switching). Feature-parity goal: token converter,
alt manager, account injection, player profile / skin, settings, EN/简体中文.

## Requirements

- macOS on **Apple Silicon (arm64)**
- **.NET SDK** to build (the machine here has .NET 10 at `/usr/local/share/dotnet`)
- **Account injection** additionally needs: **SIP disabled** + admin auth at
  inject time (uses `task_for_pid`). See `../mac-inject-poc`.

## Build & run (dev)

```bash
export DOTNET_ROOT=/usr/local/share/dotnet
export PATH="$DOTNET_ROOT:$PATH"
cd mac
dotnet run
```

## Package a double-clickable .app

```bash
cd ../mac-inject-poc && make            # build the native injector + payload (once)
cd ../mac
dotnet publish -c Release -r osx-arm64 --self-contained true -o ./publish
./package.sh                            # -> dist/AltsTools.app
open dist/AltsTools.app
```

`package.sh` assembles a self-contained bundle (bundles the .NET runtime),
generates the icon + `Info.plist`, copies the native `injector`/`payload.dylib`,
and signs the injector with the debugger entitlement.

## Layout

| Path | What |
| --- | --- |
| `App.axaml` | Material You 3 design system: tonal palette, button/card styles, motion |
| `MainWindow.axaml` | NavigationRail (tonal pill selection) + page host; starts the `:38964` injection listener |
| `Views/` | The 5 pages (Converter / AltManager / Injector / SkinChanger / Settings) |
| `ViewModels/` | Ported from the WPF project (logic unchanged) |
| `Services/` | Auth + Minecraft APIs (portable) + macOS rewrites: `RegistryService` (→ `~/Library`), `TokenInjectionService` (→ native injector), `WallpaperColorService` / `HeadSkinCacheService` (→ SkiaSharp) |
| `Crypto/` `Models/` `Localization/` | Reused verbatim from the WPF project |
| `Compat/` | WPF→Avalonia shims: MessageBox, Open/Save file dialogs |
| `Theming/ThemeManager.cs` | Runtime light/dark + accent via Material.Avalonia |

## Injection (how it works)

The macOS equivalent of the Windows `CreateRemoteThread + LoadLibraryA +
TokenSwapper.dll` chain lives in `../mac-inject-poc` (validated end-to-end):
`task_for_pid` → `mach_vm_*` → **thread hijack** + `thread_abort_safely` →
`dlopen(payload.dylib)` → JVMTI/JNI `defineClass` of `cn.zhyujun.tokenswap.TokenSwapper`
→ local HTTP server speaking the original protocol (`/client/online`,
`/handshake/init`, `/token/swap`).

## Status

- ✅ All pages, theming, localization, injection backend — builds & runs.
- ⏳ Phase 3: the 3D skin preview is currently an animated placeholder;
  the real-time 3D (Windows used Direct3D 11) is to be reimplemented with
  Silk.NET + Metal.
