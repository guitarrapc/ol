# Ol

OpenSource License checker.

## Generate an SBOM

Install the CycloneDX .NET tool and generate a CycloneDX JSON SBOM from the solution:

```powershell
dotnet tool install --global CycloneDX
dotnet CycloneDX Ol.slnx --json --output sandbox/sbom --filename cyclonedx-sample.json
```

Scan the generated SBOM with `ol`:

```powershell
dotnet run --project src/Ol -- scan --sbom sandbox/sbom/cyclonedx-sample.json
```

PS C:\github\guitarrapc\ol> dotnet run --project src/Ol -- scan --sbom sandbox/sbom/cyclonedx-sample.json --format markdown
| NAME | VERSION | LICENSE | ECOSYSTEM | DEPENDENCY | STATUS |
|---|---|---|---|---|---|
| BenchmarkDotNet | 0.15.8 | MIT | - | unknown | matched |
| BenchmarkDotNet.Annotations | 0.15.8 | MIT | - | unknown | matched |
| CommandLineParser | 2.9.1 | - | - | unknown | unknown |
| ConsoleAppFramework | 5.7.13 | MIT | - | unknown | matched |
| CycloneDX module for .NET | 6.2.0.0 | - | - | unknown | unknown |
| EnumerableAsyncProcessor | 3.8.4 | MIT | - | unknown | matched |
| Gee.External.Capstone | 2.3.0 | MIT | - | unknown | matched |
| Iced | 1.21.0 | MIT | - | unknown | matched |
| Microsoft.ApplicationInsights | 2.23.0 | MIT | - | unknown | matched |
| Microsoft.CodeAnalysis.Analyzers | 3.11.0 | MIT | - | unknown | matched |
| Microsoft.CodeAnalysis.CSharp | 4.14.0 | MIT | - | unknown | matched |
| Microsoft.CodeAnalysis.Common | 4.14.0 | MIT | - | unknown | matched |
| Microsoft.DiaSymReader | 2.0.0 | MIT | - | unknown | matched |
| Microsoft.Diagnostics.NETCore.Client | 0.2.510501 | MIT | - | unknown | matched |
| Microsoft.Diagnostics.Runtime | 3.1.512801 | MIT | - | unknown | matched |
| Microsoft.Diagnostics.Tracing.TraceEvent | 3.1.21 | MIT | - | unknown | matched |
| Microsoft.DotNet.ILCompiler | 10.0.9 | MIT | - | unknown | matched |
| Microsoft.DotNet.PlatformAbstractions | 3.1.6 | - | - | unknown | unknown |
| Microsoft.Extensions.DependencyInjection | 6.0.0 | MIT | - | unknown | matched |
| Microsoft.Extensions.DependencyInjection.Abstractions | 6.0.0 | MIT | - | unknown | matched |
| Microsoft.Extensions.DependencyModel | 6.0.2 | MIT | - | unknown | matched |
| Microsoft.Extensions.Logging | 6.0.0 | MIT | - | unknown | matched |
| Microsoft.Extensions.Logging.Abstractions | 6.0.0 | MIT | - | unknown | matched |
| Microsoft.Extensions.Options | 6.0.0 | MIT | - | unknown | matched |
| Microsoft.Extensions.Primitives | 6.0.0 | MIT | - | unknown | matched |
| Microsoft.NET.ILLink.Tasks | 10.0.9 | MIT | - | unknown | matched |
| Microsoft.Testing.Extensions.CodeCoverage | 18.3.2 | - | - | unknown | unknown |
| Microsoft.Testing.Extensions.Telemetry | 2.0.2 | MIT | - | unknown | matched |
| Microsoft.Testing.Extensions.TrxReport | 2.0.2 | MIT | - | unknown | matched |
| Microsoft.Testing.Extensions.TrxReport.Abstractions | 2.0.2 | MIT | - | unknown | matched |
| Microsoft.Testing.Platform | 2.0.2 | MIT | - | unknown | matched |
| Microsoft.Testing.Platform.MSBuild | 2.0.2 | MIT | - | unknown | matched |
| Perfolizer | 0.6.1 | MIT | - | unknown | matched |
| Pragmastat | 3.2.4 | MIT | - | unknown | matched |
| System.CodeDom | 9.0.5 | MIT | - | unknown | matched |
| System.Management | 9.0.5 | MIT | - | unknown | matched |
| System.Reflection.TypeExtensions | 4.7.0 | MIT | - | unknown | matched |
| TUnit | 1.12.111 | MIT | - | unknown | matched |
| TUnit.Assertions | 1.12.111 | MIT | - | unknown | matched |
| TUnit.Core | 1.12.111 | MIT | - | unknown | matched |
| TUnit.Engine | 1.12.111 | MIT | - | unknown | matched |
| runtime.win-x64.Microsoft.DotNet.ILCompiler | 10.0.9 | MIT | - | unknown | matched |
components: 42; matched: 38; conflict: 0; unknown: 4; ambiguous: 0; invalid: 0; format: CycloneDxJson; spdx: bundled
