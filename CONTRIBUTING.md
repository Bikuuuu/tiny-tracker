# Contributing

Thanks for helping. Tiny Tracker is small on purpose: it updates only the apps someone picks, through winget, and leaves nothing behind.

## Before you start

- [docs/design.md](docs/design.md) is the source of truth: how it behaves, its security rules, and what's out of scope.
- For anything bigger than a small fix, please open an issue first, so we can agree on the approach.

## Build and test

You need Windows 11 x64 and the .NET 10 SDK.

```powershell
dotnet build TinyTracker.slnx -c Release
dotnet test TinyTracker.slnx -c Release
```

Some tests run only on GitHub's runners, because they install and uninstall software: real upgrades, the elevated helper, the speed limit and the install test. Elsewhere they skip themselves. CI runs them on every push, and the install test on every pull request to main.

## Try it without touching your apps

Debug builds know `--demo`: made-up apps and a fake winget, so nothing installs. Its helper is the real one, with the real UAC prompt.

```powershell
dotnet build src/TinyTracker.App
src\TinyTracker.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64\TinyTracker.exe --demo
```

The demo runs next to an installed Tiny Tracker, keeps its data in a temporary folder, and deletes it when it quits or when you sign out.

## Build the installer

With Inno Setup 7.1 installed:

```powershell
scripts\build-installer.ps1
```

It publishes the app into `dist\app` and writes `dist\TinyTracker-Setup-<version>-x64.exe`. The install test on the runner installs, upgrades and uninstalls it for every pull request to main.

## Pull requests

- Tests first: add a test that fails without your change.
- Keep CI green. Warnings count as errors.
- One topic per pull request, with a short title.
- Nothing personal anywhere: no names, email addresses, user paths or machine details in code, tests, logs or screenshots.
- Text that users see goes in `src/TinyTracker.Presentation/Strings.resx`.
- Your contribution is licensed under the [MIT license](LICENSE), like the rest of the project.

## Bugs and security

Use the bug report form for bugs. For a security problem, see [SECURITY.md](SECURITY.md).
