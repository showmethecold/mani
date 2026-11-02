# Mani

**English** | [简体中文](README.zh-CN.md)

![Mani icon](src/Mani.App/Assets/Square44x44Logo.targetsize-256.png)

Mani is a Windows media player inspired by [IINA](https://github.com/iina/iina),
built with C#, WinUI 3, .NET 10, and libmpv. It brings IINA-style interactions
and Liquid Glass-inspired visuals to Windows.

## Features

- Multiple files, folders, HTTP(S) media URLs, and drag-and-drop opening.
- Playlists with M3U8 export, chapters with optional timeline markers, and media editions.
- Searchable, grouped history, playback resume, and configurable thumbnails for local files.
- Floating, top, and bottom playback controls with adjustable sidebars.
- Playback speed, frame stepping, A–B looping, and screenshots.
- External audio, output-device selection, gain limits, and a ten-band equalizer with presets.
- Subtitle matching, encoding reload, dual ASS controls, and shared text styling.
- Fullscreen, picture in picture, music mode, and multiple player windows.
- Keyboard, mouse, and native gesture controls.
- English by default, with a saved Simplified Chinese interface option.

## Building

Requirements:

- Windows 11 x64, build 26100 or later.
- .NET SDK 10.0.402 or a compatible patch selected by [global.json](global.json).
- Windows SDK 10.0.26100.0 and the WinUI development prerequisites.
- Python 3.11 or later to restore the native dependency.
- Windows Developer Mode enabled for the local development package.

Run from the repository root:

```powershell
python build/restore_libmpv.py
dotnet restore Mani.sln --locked-mode -p:Platform=x64
dotnet build Mani.sln -c Debug -p:Platform=x64 --no-restore
dotnet msbuild src/Mani.App/Mani.App.csproj -target:RunPackagedApp -property:Configuration=Debug -property:Platform=x64
```

The restore script verifies the complete native bundle pinned in
[config/libmpv.lock.json](config/libmpv.lock.json), including its dependencies
and licenses. The archive and corresponding native sources are attached to the
[native dependency release](https://github.com/showmethecold/mani/releases/tag/native-sup-20261101-r2).
The runtime download is public. To restore from a previously downloaded archive, use
`python build/restore_libmpv.py --archive C:\path\to\mani-sup-msys2-ucrt64-20261101-r2.zip`.
Native binaries are not included in Git. NuGet dependencies are pinned in
[Directory.Packages.props](Directory.Packages.props) and the project lock files.

## Testing

```powershell
dotnet restore Mani.Tests.slnf --locked-mode -p:Platform=x64
dotnet test --solution Mani.Tests.slnf --no-restore --configuration Release -p:Platform=x64
```

The unit suite runs on Windows x64 without libmpv or an interactive desktop.
The [CI workflow](.github/workflows/unit-tests.yml) is configured to run the suite
and compile the WinUI app in Debug and Release on pushes to `main` and pull
requests. Application jobs use their read-only GitHub token to retrieve the locked
runtime. Native playback and window interaction checks run separately.

## Beta test packages

Maintainers can run the [Beta MSIX workflow](.github/workflows/beta-package.yml)
on `main` to build signed, self-contained Windows x64 test packages with diagnostics
disabled. It requires the repository's existing internal signing secrets.
Download its artifact, verify `SHA256SUMS.txt`, and follow `INSTALL.txt` to trust
the accompanying public test certificate and install the MSIX. No development
tools or separately installed .NET/Windows App Runtime are required.

The [Beta setup workflow](.github/workflows/beta-setup.yml) wraps the pinned
Beta 0.1 MSIX and public certificate in a signed `Setup.exe`, with English and
Chinese installation screens. Certificate consent and Windows installation happen
in one flow; administrator approval and Windows security prompts may still apply.
The existing MSIX, package identity and application contents remain unchanged.

Artifacts are retained for 14 days and follow repository access permissions.
They are preview test packages, not a public stable release. The
[Internal MSIX workflow](.github/workflows/internal-package.yml) retains its
explicit diagnostic build for development checks.

For a local self-contained Beta test package with diagnostics disabled, use
`build/Build-InternalPackage.ps1 -Beta -Version 0.5.6.0 -UpgradeVersion 0.5.6.1`.
The separate `Mani.Beta.Test` identity preserves other Mani installations.
The second package exists to test upgrades with the same application payload.

## License

Mani's original code is licensed under **GPL-3.0-only**. See [LICENSE](LICENSE)
and [Mani's notice](licenses/Mani-NOTICE.txt).

IINA-derived code and assets retain their upstream notices; see
[IINA acknowledgments](licenses/IINA-NOTICE.txt). Third-party components retain
their respective licenses. Bundled notices are also available in **More > About Mani**.
