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
    public async Task CorpusCreate_DuplicateLicenseIdentifier_RejectsAmbiguousCorpus()
    {
        await Assert.That(() => SpdxLicenseTextCorpus.Create(
            "test",
            [new("MIT", "first"), new("MIT", "second")]))
            .Throws<InvalidDataException>();
    }

    [Test]
    public async Task CorpusLoad_UnicodeTemplate_PreservesMatcherBehavior()
    {
        var bytes = SpdxLicenseTextCorpus.Create("test", [new("Unicode", "許可条件 <<var;name=\"owner\";original=\"著作権者\";match=\".+\">>")]);
        var corpus = SpdxLicenseTextCorpus.Load(bytes);
        var matcher = new SpdxLicenseTextMatcher(corpus.CorpusVersion, corpus.Templates);

        await Assert.That(matcher.TryMatch("許可条件 開発者"u8, out var licenseId)).IsTrue();
        await Assert.That(licenseId).IsEqualTo("Unicode");
    }

    [Test]
    public async Task Constructor_DuplicateLicenseIdentifier_RejectsBroadenedMatcher()
    {
        await Assert.That(() => new SpdxLicenseTextMatcher(
            "test",
            [new("MIT", "MIT License"), new("MIT", "unrelated terms")]))
            .Throws<ArgumentException>();
    }

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

    [Test]
    public async Task Match_LiteralLessThanBeforeOptionalEnd_ParsesNextRule()
    {
        var matcher = new SpdxLicenseTextMatcher("test", [new("Example", "a<<beginOptional>>b<<<endOptional>>c")]);

        await Assert.That(matcher.TryMatch("ab<c"u8, out var id)).IsTrue();
        await Assert.That(id).IsEqualTo("Example");
    }

    [Test]
    public async Task Match_LiteralWordAdjacentToVariable_DoesNotUseUnsafeWordAnchor()
    {
        var matcher = new SpdxLicenseTextMatcher("test", [new("Example", "license<<var;name=\"holder\";original=\"holder\";match=\"holder\">>")]);

        await Assert.That(matcher.TryMatch("licenseholder"u8, out var id)).IsTrue();
        await Assert.That(id).IsEqualTo("Example");
    }

    [Test]
    [Arguments("<<beginOptionalExtra>>terms<<endOptionalExtra>>")]
    [Arguments("<<var;name=\"holder\";original=\"holder\">>")]
    public async Task Constructor_MalformedRuleThatOnlySharesAValidPrefix_RejectsTemplate(string template)
    {
        await Assert.That(() => new SpdxLicenseTextMatcher("test", [new("Example", template)]))
            .Throws<ArgumentException>();
    }

    private const string ApacheNotice = """
        Copyright (c) .NET Foundation and Contributors

        All rights reserved.

        Licensed under the Apache License, Version 2.0 (the "License"); you may not use
        this file except in compliance with the License. You may obtain a copy of the
        License at

            http://www.apache.org/licenses/LICENSE-2.0

        Unless required by applicable law or agreed to in writing, software distributed
        under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR
        CONDITIONS OF ANY KIND, either express or implied. See the License for the
        specific language governing permissions and limitations under the License.
        """;

    private static SpdxLicenseIndex CreateUrlIndex() => new(
        ["MIT", "Apache-2.0", "LGPL-2.1-only", "LGPL-2.1-or-later"],
        [],
        seeAlsoUrls:
        [
            "https://opensource.org/license/mit/",
            "https://www.apache.org/licenses/LICENSE-2.0",
            "https://opensource.org/licenses/LGPL-2.1",
            "https://opensource.org/licenses/LGPL-2.1",
        ],
        seeAlsoLicenseIds: ["MIT", "Apache-2.0", "LGPL-2.1-only", "LGPL-2.1-or-later"]);

    private static SpdxLicenseTextMatcher CreateUrlAwareMatcher()
        => new("test", [new("MIT", MitTemplate)], licenseIndex: CreateUrlIndex());

    [Test]
    public async Task Match_DeclaredLicenseUrlWithoutTemplateText_ResolvesLicenseFromUrl()
    {
        var matcher = CreateUrlAwareMatcher();

        var matched = matcher.TryMatch(Encoding.UTF8.GetBytes(ApacheNotice), out var licenseId);

        await Assert.That(matched).IsTrue();
        await Assert.That(licenseId).IsEqualTo("Apache-2.0");
    }

    [Test]
    public async Task Match_TemplateTextAndDeclaredUrlNamingAnotherLicense_ResolvesNothing()
    {
        var matcher = CreateUrlAwareMatcher();
        var mixed = $"""
            Unless otherwise noted, the source code here is covered by the following license:

            {ApacheNotice}

            -----------------------

            The imported code is covered by the following license:

            {CoreFxMit}
            """;

        await Assert.That(matcher.TryMatch(Encoding.UTF8.GetBytes(mixed), out _)).IsFalse();
    }

    [Test]
    public async Task Match_TemplateTextAndDeclaredUrlNamingTheSameLicense_ResolvesLicense()
    {
        var matcher = CreateUrlAwareMatcher();
        var corroborated = $"{CoreFxMit}\n\nSee https://opensource.org/license/mit/ for the full text.";

        await Assert.That(matcher.TryMatch(Encoding.UTF8.GetBytes(corroborated), out var licenseId)).IsTrue();
        await Assert.That(licenseId).IsEqualTo("MIT");
    }

    [Test]
    public async Task Match_TwoDeclaredUrlsNamingDifferentLicenses_ResolvesNothing()
    {
        var matcher = CreateUrlAwareMatcher();
        var listing = "This product is licensed under https://opensource.org/license/mit/ and https://www.apache.org/licenses/LICENSE-2.0 terms.";

        await Assert.That(matcher.TryMatch(Encoding.UTF8.GetBytes(listing), out _)).IsFalse();
    }

    [Test]
    public async Task Match_DeclaredUrlSpdxPublishesForSeveralLicenses_ResolvesNothing()
    {
        var matcher = CreateUrlAwareMatcher();
        var shared = "Licensed under the terms published at https://opensource.org/licenses/LGPL-2.1 only.";

        await Assert.That(matcher.TryMatch(Encoding.UTF8.GetBytes(shared), out _)).IsFalse();
    }

    [Test]
    [Arguments("HTTP://WWW.APACHE.ORG/licenses/LICENSE-2.0")]
    [Arguments("https://apache.org/licenses/LICENSE-2.0/")]
    [Arguments("(https://www.apache.org/licenses/LICENSE-2.0).")]
    [Arguments("<https://www.apache.org/licenses/LICENSE-2.0>,")]
    public async Task Match_DeclaredUrlSpelling_ResolvesTheSameLicense(string spelling)
    {
        var matcher = CreateUrlAwareMatcher();
        var text = $"This component is licensed under the license published at {spelling}";

        await Assert.That(matcher.TryMatch(Encoding.UTF8.GetBytes(text), out var licenseId)).IsTrue();
        await Assert.That(licenseId).IsEqualTo("Apache-2.0");
    }

    [Test]
    public async Task Match_UrlSpdxDoesNotPublish_ResolvesNothing()
    {
        var matcher = CreateUrlAwareMatcher();
        var text = "See https://example.com/legal/terms for the license that governs this product.";

        await Assert.That(matcher.TryMatch(Encoding.UTF8.GetBytes(text), out _)).IsFalse();
    }

    [Test]
    public async Task Match_WithoutLicenseIndex_IgnoresDeclaredUrl()
    {
        var matcher = new SpdxLicenseTextMatcher("test", [new("MIT", MitTemplate)]);

        await Assert.That(matcher.TryMatch(Encoding.UTF8.GetBytes(ApacheNotice), out _)).IsFalse();
    }

    [Test]
    public async Task Match_TemplateText_ReportsTemplateAsTheMatcher()
    {
        var matcher = CreateUrlAwareMatcher();

        await Assert.That(matcher.TryMatch(Encoding.UTF8.GetBytes(CoreFxMit), out _, out var kind)).IsTrue();
        await Assert.That(kind).IsEqualTo(SpdxLicenseTextMatchKind.Template);
        await Assert.That(kind.ToMatcherId()).IsEqualTo("spdx-template");
    }

    [Test]
    public async Task Match_DeclaredLicenseUrlOnly_ReportsTheUrlAsTheMatcher()
    {
        var matcher = CreateUrlAwareMatcher();

        await Assert.That(matcher.TryMatch(Encoding.UTF8.GetBytes(ApacheNotice), out _, out var kind)).IsTrue();
        await Assert.That(kind).IsEqualTo(SpdxLicenseTextMatchKind.DeclaredUrl);
        await Assert.That(kind.ToMatcherId()).IsEqualTo("spdx-license-url");
    }

    [Test]
    public async Task Match_ResolvingNothing_ReportsNoMatcher()
    {
        var matcher = CreateUrlAwareMatcher();

        await Assert.That(matcher.TryMatch("nothing here"u8, out _, out var kind)).IsFalse();
        await Assert.That(kind).IsEqualTo(SpdxLicenseTextMatchKind.None);
    }
}
