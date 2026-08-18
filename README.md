# MarkUpViewMini

A lightweight, fast Markdown viewer for Windows, built with WPF and WebView2. It renders Markdown and Mermaid diagrams side by side with your source, and includes a visual editor for tweaking Mermaid diagrams without hand-editing syntax.

## Features

- **Markdown rendering** in a WebView2 document surface, kept in sync with the source as you edit or as the file changes on disk
- **Mermaid diagram support**, including a dedicated visual editor for building and adjusting diagrams
- **Multi-tab / multi-window** editing with per-window session recovery after a crash or restart
- **Sidebar & outline navigation**, full-text search across open documents, and jump-to-line/anchor navigation
- **Encoding-aware file handling** (open/save with explicit encoding and line-ending control)
- **Windows integration** — optional file association so Markdown files can open directly in MarkUpViewMini
- **Localization** — English and Korean UI out of the box
- **Portable** — runs from a folder with no installer required; an MSIX package is also available

## Getting started

### Download

Grab the latest release from the [Releases](../../releases) page:

- `MarkUpViewMini-win-x64.zip` — self-contained, no .NET runtime required
- `MarkUpViewMini-win-x64-fxdependent.zip` — smaller download, requires the [.NET 10 desktop runtime](https://dotnet.microsoft.com/download/dotnet/10.0)

Unzip and run `MarkUpViewMini.exe` — no installation needed.

### Build from source

**Prerequisites**

- [.NET SDK 10.0.400+](https://dotnet.microsoft.com/download/dotnet/10.0) (pinned in [`global.json`](global.json))
- [Node.js 20+](https://nodejs.org/) (used to build the bundled web document surface and Mermaid editor)
- Windows, for WPF/WebView2

**Build & run**

```bash
dotnet build MarkUpViewMini.slnx
dotnet run --project src/MarkUpViewMini.App
```

The web assets (`web/document-surface`, `web/mermaid-editor`) are built automatically as part of the .NET build via MSBuild targets — no separate `npm run build` step is required.

### Run the tests

```bash
dotnet test MarkUpViewMini.slnx
```

### Build a portable release locally

```powershell
./scripts/publish-portable.ps1                    # self-contained
./scripts/publish-portable.ps1 -FrameworkDependent # framework-dependent
```

This mirrors the CI release pipeline: it runs the full test suite, publishes a `win-x64` build, and produces a verified zip under `artifacts/`.

## Project layout

```
src/
  MarkUpViewMini.App/            WPF application shell, viewmodels, WebView2 document surface
  MarkUpViewMini.App.Package/    MSIX packaging project
  MarkUpViewMini.Core/           Document model, formats, search, workspace, navigation
  MarkUpViewMini.Infrastructure/ File system, recovery, Windows shell integration
web/
  document-surface/              Markdown/Mermaid rendering surface (TypeScript, bundled into the app)
  mermaid-editor/                Visual Mermaid diagram editor
  shared/                        Shared web code and i18n resources
tests/                           Unit, integration, and performance test projects
scripts/                         Release and packaging scripts
```

## Releasing

Pushing a `v*` tag triggers [`.github/workflows/release.yml`](.github/workflows/release.yml), which builds and publishes both portable zips as GitHub Release assets.

## License

[MIT](LICENSE)
