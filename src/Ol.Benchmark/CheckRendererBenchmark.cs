using System.Buffers;
using BenchmarkDotNet.Attributes;
using Ol.Core;
using Ol.Core.Licensing;

/// <summary>Measures the CI-facing projection of a large unresolved policy result.</summary>
[MemoryDiagnoser]
public class CheckRendererBenchmark
{
    private const int ComponentCount = 1_024;
    private readonly ArrayBufferWriter<byte> buffer = new(128 * 1024);
    private readonly ScanComponent[] components = new ScanComponent[ComponentCount];
    private readonly LicensePolicyViolation[] violations = new LicensePolicyViolation[ComponentCount];
    private readonly DependencyInventory inventory;

    public CheckRendererBenchmark()
    {
        var candidate = new LicenseCandidate(
            LicenseCandidateSource.PackageRegistry,
            LicenseCandidateKind.Fetch,
            default,
            default,
            LicenseStatus.Unknown,
            false,
            LicenseCandidateWarnings.PackageMetadataNotFound);

        for (var i = 0; i < ComponentCount; i++)
        {
            components[i] = new ScanComponent(
                "example",
                "1.0.0",
                default,
                "npm",
                DependencyType.Transitive,
                LicenseStatus.Unknown,
                "pkg:npm/example@1.0.0",
                default,
                candidate,
                []);
            violations[i] = new LicensePolicyViolation(i, LicensePolicyViolationKind.Unknown);
        }

        inventory = new DependencyInventory(default, [], components, [], []);
    }

    [Benchmark]
    public int WriteUnresolvedViolations()
    {
        buffer.Clear();
        CheckRenderer.Write(buffer, inventory, components, violations, ComponentCount);
        return buffer.WrittenCount;
    }
}
