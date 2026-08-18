using BenchmarkDotNet.Attributes;
using Ol.Core.Generated;
using Ol.Core.Spdx;
using System.Text;

[MemoryDiagnoser]
public class SpdxLicenseTextMatcherBenchmark
{
    private const string MitTemplate = """
        <<beginOptional>>MIT License

        <<endOptional>>Copyright (c) <<var;name="copyright";original="<year> <copyright holders>";match=".{0,5000}">>

        Permission is hereby granted, free of charge, to any person obtaining a copy
        of this software and associated documentation files (the "Software"), to deal
        in the Software without restriction, including without limitation the rights
        to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
        copies of the Software, and to permit persons to whom the Software is
        furnished to do so, subject to the following conditions:

        The above copyright notice and this permission notice shall be included in all
        copies or substantial portions of the Software.

        THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
        IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
        FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
        AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
        LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
        OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
        SOFTWARE.
        """;

    private const string MitText = """
        MIT License

        Copyright (c) .NET Foundation and Contributors

        Permission is hereby granted, free of charge, to any person obtaining a copy
        of this software and associated documentation files (the "Software"), to deal
        in the Software without restriction, including without limitation the rights
        to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
        copies of the Software, and to permit persons to whom the Software is
        furnished to do so, subject to the following conditions:

        The above copyright notice and this permission notice shall be included in all
        copies or substantial portions of the Software.

        THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
        IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
        FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
        AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
        LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
        OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
        SOFTWARE.
        """;

    private readonly SpdxLicenseTextMatcher matcher = new(
        "benchmark",
        [new SpdxLicenseTextTemplate("MIT", MitTemplate)]);
    private readonly byte[] licenseText = Encoding.UTF8.GetBytes(MitText);
    private readonly byte[] bundledCorpus;
    private readonly SpdxLicenseTextCorpusData bundledCorpusData;
    private readonly SpdxLicenseIndex bundledIndex;
    private readonly SpdxLicenseTextMatcher bundledMatcher;
    private readonly SpdxLicenseTextMatcher bundledTemplateOnlyMatcher;

    public SpdxLicenseTextMatcherBenchmark()
    {
        using var stream = typeof(SpdxLicenseTextCorpus).Assembly.GetManifestResourceStream(SpdxLicenseTextCorpus.EmbeddedResourceName)!;
        using var output = new MemoryStream();
        stream.CopyTo(output);
        bundledCorpus = output.ToArray();
        var spdx = SpdxData.Load(null);
        bundledIndex = spdx.Index;
        bundledMatcher = spdx.Matcher;
        bundledCorpusData = SpdxLicenseTextCorpus.Load(bundledCorpus);
        bundledTemplateOnlyMatcher = new SpdxLicenseTextMatcher(bundledCorpusData.CorpusVersion, bundledCorpusData.Templates);
    }

    [Benchmark]
    public bool Match() => matcher.TryMatch(licenseText, out _);

    [Benchmark]
    public bool MatchBundledCorpus() => bundledMatcher.TryMatch(licenseText, out _);

    [Benchmark]
    public bool MatchBundledCorpusTemplateOnly() => bundledTemplateOnlyMatcher.TryMatch(licenseText, out _);

    [Benchmark]
    public SpdxLicenseTextCorpusData LoadBundledCorpus()
        => SpdxLicenseTextCorpus.Load(bundledCorpus);

    [Benchmark]
    public SpdxLicenseTextMatcher ConstructBundledMatcher()
        => new(bundledCorpusData.CorpusVersion, bundledCorpusData.Templates);

    [Benchmark]
    public SpdxLicenseIndex ConstructBundledIndex()
        => new(
            SpdxGeneratedLicenseData.LicenseIds,
            SpdxGeneratedLicenseData.ExceptionIds,
            SpdxGeneratedLicenseData.DeprecatedLicenseIds,
            SpdxGeneratedLicenseData.LicenseNames,
            SpdxGeneratedLicenseData.SeeAlsoUrls,
            SpdxGeneratedLicenseData.SeeAlsoLicenseIds,
            SpdxGeneratedLicenseData.LicenseIdsUtf8);

    [Benchmark]
    public SpdxLicenseTextMatcher ConstructBundledCorpus()
    {
        using var stream = new MemoryStream(bundledCorpus, writable: false);
        return SpdxLicenseTextCorpus.LoadMatcher(stream, bundledIndex);
    }
}
