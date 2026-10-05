# Public source snapshot — 2026-10-05

Repository: TungMEP.

This snapshot contains the current source, UI resources, build/installer scripts and relevant documentation. It intentionally starts a new Git history rather than publishing local development history or backups.

Not published: local SDKs, build/cache/output directories, private local configuration, credentials, model/project files, workbooks, PDFs, manufacturer family libraries and third-party library DLLs. The original working folders and existing installation are unchanged.

Building requires Autodesk Revit 2024 and the appropriate .NET SDK/framework tooling. Restore NuGet dependencies where the projects use PackageReference; projects with local lib/ references need those libraries from their official distributions. Revit API DLLs are not redistributed. Existing dependency licenses still apply; public visibility is not a new license grant.

The optional AutoSprinkler family content is not distributed in this source snapshot. Supply the required family locally when packaging; see the original README and build scripts.

