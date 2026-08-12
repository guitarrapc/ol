using System.Text;
using Ol.Core.Spdx;

namespace Ol.Tests;

public sealed class SpdxLicenseTextMatcherTests
{
    private const string MitTemplate = """
        <<beginOptional>>MIT License

        <<endOptional>><<var;name="copyright";original="Copyright (c) <year> <copyright holders>";match=".{0,5000}">>
        Permission is hereby granted, free of charge, to any person obtaining a copy of <<var;name="files";original="this software and associated documentation files";match="this\s+software\s+and\s+associated\s+documentation\s+files|this\s+source\s+file">> (the "<<var;name="Software1";original="Software";match="Software|Materials">>"), to deal in the <<var;name="Software2";original="Software";match="Software|Materials">> without restriction, including without limitation<<beginOptional>> on<<endOptional>> the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the <<var;name="Software3";original="Software";match="Software|Materials">>, and to permit persons to whom the <<var;name="Software4";original="Software";match="Software|Materials">> is furnished to do so, subject to the following conditions:

        The above copyright notice and this permission notice<<beginOptional>> (including the next paragraph)<<endOptional>> shall be included in all copies or substantial portions of the <<var;name="Software5";original="Software";match="Software|Materials">>.

        THE <<var;name="Software-verb";original="SOFTWARE IS";match="SOFTWARE IS|MATERIALS ARE">> PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL <<var;name="copyrightHolder";original="THE AUTHORS OR COPYRIGHT HOLDERS";match=".+">> BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE <<var;name="Software7";original="SOFTWARE";match="SOFTWARE|MATERIALS">> OR THE USE OR OTHER DEALINGS IN THE <<var;name="Software8";original="SOFTWARE";match="SOFTWARE|MATERIALS">>.
        """;

    private const string CoreFxMit = """
        The MIT License (MIT)

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

    [Test]
    public async Task Match_CoreFxMitText_ResolvesMitFromSpdxTemplate()
    {
        var matcher = new SpdxLicenseTextMatcher("3.28.0", [new("MIT", MitTemplate)]);

        var matched = matcher.TryMatch(Encoding.UTF8.GetBytes(CoreFxMit), out var licenseId);

        await Assert.That(matched).IsTrue();
        await Assert.That(licenseId).IsEqualTo("MIT");
        await Assert.That(matcher.CorpusVersion).IsEqualTo("3.28.0");
    }

    [Test]
    public async Task Match_TextWithChangedGrant_ResolvesNothing()
    {
        var matcher = new SpdxLicenseTextMatcher("3.28.0", [new("MIT", MitTemplate)]);
        var changed = CoreFxMit.Replace("Permission is hereby granted", "Permission is not granted", StringComparison.Ordinal);

        await Assert.That(matcher.TryMatch(Encoding.UTF8.GetBytes(changed), out _)).IsFalse();
    }

    [Test]
    public async Task Match_TextOverConfiguredLimit_ResolvesNothingWithoutParsing()
    {
        var matcher = new SpdxLicenseTextMatcher("3.28.0", [new("MIT", MitTemplate)], maximumTextBytes: 32);

        await Assert.That(matcher.TryMatch(Encoding.UTF8.GetBytes(CoreFxMit), out _)).IsFalse();
    }

    [Test]
    public async Task Match_TwoTemplatesResolvingSameText_ReportsAmbiguous()
    {
        var matcher = new SpdxLicenseTextMatcher("test", [new("First", "same text"), new("Second", "same text")]);

        await Assert.That(matcher.TryMatch("same text"u8, out _)).IsFalse();
    }

    [Test]
    public async Task Match_InvalidUtf8_ResolvesNothing()
    {
        var matcher = new SpdxLicenseTextMatcher("test", [new("MIT", MitTemplate)]);

        await Assert.That(matcher.TryMatch([0xff, 0xfe], out _)).IsFalse();
    }

    [Test]
    public async Task Match_MissingWordBoundary_ResolvesNothing()
    {
        var matcher = new SpdxLicenseTextMatcher("test", [new("Example", "same text")]);

        await Assert.That(matcher.TryMatch("sametext"u8, out _)).IsFalse();
    }
}
