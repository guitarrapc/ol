---
name: sandbox-code-guidelines
description: Guidelines for quick C# experiments in `sandbox/DotnetFiles/` across Ol's full domain: dependency graphs, OSS license evidence, SPDX expressions, reconciliation, package/source metadata, caches, reports, policy ideas, performance probes, and CLI-adjacent behavior.
---

# Sandbox Code Guidelines

**IMPORTANT:** Never use `dotnet script` or `dotnet-script` command. This project does NOT use dotnet-script.

If you need to create a .cs file to verify something, you can create it in the `sandbox/DotnetFiles/` folder and run it.

Ol is a transitive OSS license resolver and future policy-enforcement tool. The sandbox is not limited to SBOM parsing. Use it for isolated experiments involving:

- component identity and root/direct/transitive dependency relationships
- SPDX identifiers, exceptions, expressions, and normalization
- license candidates, evidence reconciliation, and status precedence
- package registry or source repository response normalization
- cache keys, hashing, serialization, concurrency, and retry behavior
- report shaping, grouping, sorting, and policy-evaluation prototypes
- allocation, generated-code, Native AOT, and BCL API feasibility checks

Use the smallest boundary needed for the question. Reference `Ol.Core` when validating reusable domain behavior; a standalone BCL-only file is preferable when no Ol type is needed. CLI orchestration and user-visible behavior belong in repository tests once the experiment establishes the approach.

See `dotnet run` details here: https://github.com/dotnet/sdk/blob/main/documentation/general/dotnet-run-file.md

- For a standalone C# file (without .csproj):

```csharp
#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:project ../../src/Ol.Core/Ol.Core.csproj
using System.Text;
using Ol.Core;

```

The project reference makes all public `Ol.Core` domains available; it does not imply that the experiment must scan an SBOM.
Use `Microsoft.NET.Sdk.Web` only when the experiment specifically requires Web SDK features.

```shell
# Create a single .cs file and run it directly
dotnet run sandbox/DotnetFiles/YourCsFile.cs
```

- For a project folder with .csproj:

```shell
cd sandbox/YourProjectFolder
dotnet run -c Release
# Or specify the project file:
dotnet run -c Release --project YourProjectName.csproj
```

use `sandbox/DotnetFiles/Sample.cs` for template.

## Guardrails

- Treat sandbox code as disposable verification, not production implementation.
- Do not duplicate a production test in the sandbox. Move confirmed behavior into the appropriate TUnit project before changing `src/`.
- Do not call live package registries or source hosts when a deterministic in-memory payload or fake `HttpMessageHandler` can answer the question.
- Never place tokens, private repository identities, or user cache contents in sandbox files.
- For performance questions, run Release mode and use a proper benchmark project for conclusions; stopwatch results from a single-file experiment are only exploratory.
- Delete task-specific files when they no longer document a useful reusable experiment. Keep `Sample.cs` as the template.
