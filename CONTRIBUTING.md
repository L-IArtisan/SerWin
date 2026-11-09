# Contributing to SerWin

🇫🇷 [Version française](CONTRIBUTING.fr.md)

Thanks for your interest! The most valuable contribution is **the knowledge base**: reporting a wrong explanation, improving an entry, or writing one for a service that has none.

## Reporting a problem

Use the [issue templates](https://github.com/L-IArtisan/SerWin/issues/new/choose):

- **Wrong entry** — an explanation that is incorrect, incomplete or outdated;
- **New entry** — a service without an entry (`SerWin.Verify coverage` lists them);
- **Bug** — something the app does wrong.

Always include your Windows edition and build (shown at the bottom of the SerWin window).

## Build and run

Requirements: Windows 11, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
dotnet build
dotnet test
dotnet run --project src/SerWin.App
```

Portable executables (one file each, .NET included):

```powershell
dotnet publish src/SerWin.App -c Release -r win-x64 -o publish
dotnet publish src/SerWin.Verify -c Release -r win-x64 -o publish   # verification tool, usable in Windows Sandbox
```

Tests that really modify a service (the Fax service, restored to its exact state) only run from an elevated terminal; otherwise they are reported as skipped.

### Layout

```
src/SerWin.App/        WPF interface (WPF-UI, Fluent style)
src/SerWin.Core/       SCM access (advapi32), registry, snapshot, actions, safety lists
src/SerWin.Knowledge/  Knowledge base loading and validation
src/SerWin.Verify/     Command-line tool to verify entries
knowledge/             Knowledge base (one JSON file per service or process) — CC BY-SA 4.0
tests/                 xUnit tests
tools/                 Scripts (.resx generation, winget manifest)
packaging/winget/      winget manifest templates
```

## Writing or fixing an entry

Each service lives in `knowledge/services/<Name>.json` (schema: [`knowledge/schema.json`](knowledge/schema.json)). An entry contains:

- three write-ups — **Simple** (for everyone), **Advanced** (mechanisms), **Expert** (DLL, svchost group, account, registry) — in French (source language) and English;
- what stops working if it is disabled (`ifDisabled`);
- known revival mechanisms (`revivers`);
- context rules (`usefulIf`, e.g. `{ "bluetooth": "present" }`) and the verdict when the rule is not met.

Writing rules:

- short sentences, no jargon at the Simple level;
- never state what has not been checked: if in doubt, say so;
- a `critique` (critical) service must be on the hard-coded red list (`SafetyLists.cs`); a test enforces it.

```powershell
dotnet run --project src/SerWin.Verify -- validate knowledge   # validate all entries
dotnet run --project src/SerWin.Verify -- dump wuauserv         # real configuration of a service
dotnet run --project src/SerWin.Verify -- coverage              # services on your machine without an entry
dotnet run --project src/SerWin.Verify -- who wuauserv          # who can restart it, who actually did
dotnet run --project src/SerWin.Verify -- compare               # differences from known factory settings
```

### Verification levels

- 📝 **Draft** — written from public documentation;
- 🔍 **Configuration verified** — triggers, dependencies and account confirmed on a given build with `SerWin.Verify`;
- ✅ **Behavior observed** — disabling tested, revivals observed.

To promote entries to "🔍 Configuration verified":

1. In **Windows Sandbox** (clean machine, detected automatically): `SerWin.Verify.exe capture` → produces `serwin-capture-<Edition>-<build>.json`.
2. In the repository: `dotnet run --project src/SerWin.Verify -- apply serwin-capture-….json` (`--dry-run` to simulate).

`apply` records the factory configuration (`defaults["Edition:Build"]`) and only marks an entry as verified if everything it claims really exists. A capture made on an already-modified machine does not record startup types.

### Network destinations (v1.1)

Each online service is described in `knowledge/destinations/<name>.json` (schema: [`knowledge/destination.schema.json`](knowledge/destination.schema.json)): host patterns (`login.live.com`, `*.events.data.microsoft.com`), owner, category, need (`essentiel`, `utile`, `optionnel`, `indesirable`), the usual senders, what breaks if blocked and the official setting that reduces it. Entries must stay **general** (true for any Windows 11 PC), not describe one machine. The most precise pattern wins between entries.

```powershell
dotnet run --project src/SerWin.Verify -- net              # what this PC exchanges (DNS log + live TCP), with matching entries
dotnet run --project src/SerWin.Verify -- net --unknown    # host names seen here that have no entry yet
```

## Translating the interface

UI strings live in `src/SerWin.App/Resources/Strings.tsv` (key, French, English). After editing:

```powershell
powershell -ExecutionPolicy Bypass -File tools\Build-Resx.ps1
```

A test checks that every key used exists in both languages.

## Principles

- **Zero network:** no feature may connect to the Internet.
- **Understand before acting:** no bulk action without explanation, nothing pre-checked.
- **Everything can be undone:** any new action goes through `ActionEngine` (backup → verification → history).

## Releasing

1. Update `<Version>` in `Directory.Build.props`, then push a tag: `git tag v1.2.3 && git push origin v1.2.3`.
2. `.github/workflows/release.yml` builds, validates the knowledge base, runs the tests, publishes `SerWin.exe` and `SerWin.Verify.exe`, signs them if SignPath is configured (`SIGNPATH_API_TOKEN`, `SIGNPATH_ORGANIZATION_ID` secrets), and creates the release with `SHA256SUMS.txt`.
3. winget: see [`packaging/winget/README.md`](packaging/winget/README.md).

## Licenses

By contributing, you agree that your code is published under [GPL-3.0-or-later](LICENSE) and your entries under [CC BY-SA 4.0](knowledge/LICENSE).
