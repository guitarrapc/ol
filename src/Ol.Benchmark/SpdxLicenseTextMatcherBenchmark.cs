using BenchmarkDotNet.Attributes;
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
    private readonly SpdxLicenseTextMatcher bundledMatcher;

    public SpdxLicenseTextMatcherBenchmark()
    {
        using var stream = typeof(SpdxLicenseTextCorpus).Assembly.GetManifestResourceStream(SpdxLicenseTextCorpus.EmbeddedResourceName)!;
        using var output = new MemoryStream();
        stream.CopyTo(output);
        bundledCorpus = output.ToArray();
        bundledMatcher = SpdxData.Load(null).Matcher;
    }

    [Benchmark]
    public bool Match() => matcher.TryMatch(licenseText, out _);

    [Benchmark]
    public bool MatchBundledCorpus() => bundledMatcher.TryMatch(licenseText, out _);

    [Benchmark]
    public int ConstructBundledCorpus()
    {
        var corpus = SpdxLicenseTextCorpus.Load(bundledCorpus);
        return new SpdxLicenseTextMatcher(corpus.CorpusVersion, corpus.Templates).CorpusVersion.Length;
    }
}
