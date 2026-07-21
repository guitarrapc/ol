using BenchmarkDotNet.Attributes;
using Ol.Core;
using Ol.Core.Licensing;
using Ol.Core.Spdx;

[MemoryDiagnoser]
public class LicensePolicyBenchmark
{
    private const int ComponentCount = 1_024;
    private readonly LicenseAllowPolicy policy;
    private readonly ScanComponent[] allowed;
    private readonly ScanComponent[] mixedExpressions;
    private readonly ScanComponent[] violations;
    private readonly ScanComponent[] unresolved;

    public LicensePolicyBenchmark()
    {
        var spdx = new SpdxLicenseIndex(["MIT", "Apache-2.0", "GPL-3.0-only"], []);
        LicenseAllowPolicy.TryCreate(["MIT", "Apache-2.0"], spdx, out policy!, out _);
        allowed = new ScanComponent[ComponentCount];
        mixedExpressions = new ScanComponent[ComponentCount];
        violations = new ScanComponent[ComponentCount];
        unresolved = new ScanComponent[ComponentCount];
        for (var i = 0; i < ComponentCount; i++)
        {
            allowed[i] = CreateComponent("MIT", LicenseStatus.Matched);
            mixedExpressions[i] = CreateComponent(i % 2 == 0 ? "MIT AND Apache-2.0" : "GPL-3.0-only OR MIT", LicenseStatus.Matched);
            violations[i] = CreateComponent("GPL-3.0-only", LicenseStatus.Matched);
            unresolved[i] = CreateComponent(default, LicenseStatus.Unknown);
        }
    }

    [Benchmark(Baseline = true)]
    public int AllAllowed() => policy.Evaluate(allowed).Length;

    [Benchmark]
    public int MixedExpressions() => policy.Evaluate(mixedExpressions).Length;

    [Benchmark]
    public int AllViolations() => policy.Evaluate(violations).Length;

    [Benchmark]
    public int AllUnresolved() => policy.Evaluate(unresolved).Length;

    private static ScanComponent CreateComponent(Utf8Slice license, LicenseStatus status)
        => new("example", "1.0.0", license, "npm", DependencyType.Transitive, status, "pkg:npm/example@1.0.0", default, default, [], []);
}
