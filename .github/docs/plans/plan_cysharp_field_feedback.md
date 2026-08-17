# Cysharp リポジトリ群に対する `ol` 実地検証フィードバック

## この文書の位置付け

`D:\github\cysharp` にある 8 リポジトリへ `ol` を実際に実行し、SBOM 経路と非 SBOM 経路の差、ライセンスを確定できるべきなのに確定できない事例、そして原理的に確定しようがない事例を洗い出した記録である。実行日は 2026-08-18。検証に使った `ol` は `0.9.2` 相当 (`fa11171` 時点のソースを `-c Release` でビルドしたもの) である。

この文書は仕様ではない。仕様として確定した内容は [`specs/spdx.md`](../specs/spdx.md) 側に書いてある。ここに残すのは、どのコマンドを実行して何が出たか、そこから何を直したか、そして直さずに残した判断とその理由である。

対象は AIApiTracer / DFrame / LogicLooper / MagicOnion / NativeCompressions / UniTask / ZLinq / csbindgen の 8 リポジトリ、非 SBOM 経路で延べ 2,833 component。

## 結論

`ol` は実用に足りた。8 リポジトリ全てで引数なしに近い `ol scan --input .` が通り、入力形式の自動判別、対象外入力の明示、未解決 component の理由提示まで一貫していた。特に「解決できなかった理由」が warning として型で残る設計は、実際に原因を追う作業を成立させた。この検証で挙げた不具合を特定できたのも、`licenseCandidates` に evidence が残っていたからである。

一方で **1 件の誤検知 (false positive) を実データで発見した**。xunit 2.4.x 系 14 package が Apache-2.0 であるにもかかわらず `MIT` と報告されていた。これは「未解決になる」より深刻で、`ol check` が誤って許可してしまう種類の誤りである。原因と修正は [Round 2](#round-2-ライセンス文書内の宣言 url-を読む) に書いた。

修正後の集計は次のとおり。matched の総数は 2,575 → 2,565 と減っているが、これは誤って matched になっていた 20 件 (14 package 種) を未解決へ落とし、正しく確定できる 10 件 (8 package 種) を新たに確定した結果である。

| Repository | 修正前 matched/unknown | 修正後 matched/unknown | component 数 |
|---|---|---|---|
| AIApiTracer | 100 / 0 | 100 / 0 | 100 |
| DFrame | 111 / 78 | 106 / 83 | 189 |
| LogicLooper | 45 / 5 | 39 / 11 | 50 |
| MagicOnion | 1510 / 80 | 1517 / 73 | 1590 |
| NativeCompressions | 639 / 3 | 639 / 3 | 642 |
| UniTask | 53 / 88 | 47 / 94 | 141 |
| ZLinq | 105 / 3 | 105 / 3 | 108 |
| csbindgen | 12 / 1 | 12 / 1 | 13 |
| **合計** | **2575 / 258** | **2565 / 268** | **2833** |

## 検証環境

```bash
# ol のビルド
dotnet build src/Ol/Ol.csproj -c Release
# => src/Ol/bin/Release/net10.0/ol.exe

export OL=/d/github/guitarrapc/ol/src/Ol/bin/Release/net10.0/ol.exe
export OL_GITHUB_TOKEN=$(gh auth token)   # GitHub License API のレート制限回避
```

`OL_GITHUB_TOKEN` を明示的に渡さないと `GITHUB_TOKEN` は読まれない。README のとおりで、意図した挙動である。summary の `GitHub auth ol_github_token` で有効か確認できた。

## Round 1: 非 SBOM 経路の全リポジトリ走査

### 実行

```bash
cd /d/github/cysharp/ZLinq && $OL scan --input . --format text
```

```text
Input: package-manager/nuget-assets

NAME VERSION LICENSE ECOSYSTEM DEPENDENCY STATUS SUPPLIED
AndanteSoft.SpanLinq 1.0.1 MIT nuget direct matched package-manager
BenchmarkDotNet 0.15.2 MIT nuget direct matched package-manager
...
Microsoft.NETCore.Platforms 1.1.0 - nuget transitive unknown package-manager
Microsoft.VisualStudio.DiagnosticsHub.BenchmarkDotNetDiagnosers 18.0.36421.1 - nuget direct unknown package-manager
Microsoft.VisualStudio.DiagnosticsHub.UserMarks 18.0.36421.1 - nuget direct unknown package-manager
```

stderr の summary:

```text
Scan summary
  License results: 108 displayed components; 105 matched; 0 conflict; 3 unknown; 0 ambiguous; 0 invalid; 0 error
  Findings: 5 warnings on unresolved components; 60 on resolved components; 0 deprecated SPDX identifiers
  Package artifacts (full scan): 139 targets; 50 documents; 47 matched
  Declared GitHub files (full scan): 1 targets; 0 GitHub requests; 1 cache hits; 0 cache misses; 1 documents; 1 matched; 0 fetch errors
  Package metadata (full scan): 108 supported; 108 cache hits; 0 cache misses; 0 refreshed; 0 fetch errors; 0 unsupported ecosystems; 0 unversioned purls
  Source repositories (full scan): 27 targets; 0 GitHub requests; 27 cache hits; 0 cache misses; 0 fetch errors; 62 components without source license
  Run: concurrency 8; retries 1; GitHub auth ol_github_token
  Input discovery: 11 detected files; 0 ignored candidates; 0 incomplete input sets; ecosystems nuget
  Input: ZLinq; input format NuGet assets; SPDX e4c1f27 (bundled)
```

所要 2.5 秒。11 個の `project.assets.json` を自動発見しており、リポジトリ直下を指すだけで済むのは実運用上とても楽だった。

残り 7 リポジトリも同じ形で回した。

```bash
for r in AIApiTracer DFrame LogicLooper MagicOnion NativeCompressions UniTask csbindgen; do
  cd /d/github/cysharp/$r && $OL scan --input . --format json > $r.json
done
```

csbindgen と NativeCompressions では stderr に次が出た。

```text
Warning: Rust dependencies were not scanned: Cargo.lock is not a supported input. Run 'cargo metadata --format-version 1 --locked > cargo-metadata.json', then scan cargo-metadata.json.
```

**評価: 良い。** 「対応していない」で終わらせず、次に打つコマンドまで書いてある。`Input discovery` 行にも `1 ignored candidate (Cargo.lock)` として残る。多言語リポジトリで沈黙して取りこぼすのが一番怖いので、この設計は正しい。

### 未解決 component の分類

```bash
jq -r '.components[]|select(.status!="matched")|[.name+"@"+.version,(.warnings|join("+"))]|@tsv' *.json | sort -u
```

重複を除いた未解決は 120 種。うち 102 種は同一原因だった。

| 分類 | 件数 | 代表 | 判定 |
|---|---|---|---|
| 旧 Microsoft package の `licenseUrl` が fwlink | 102 | `Microsoft.CSharp@4.0.1` | 確定不能・`ol` は正しい |
| ライセンス文書が非 SPDX (proprietary) | 5 | `Microsoft.VisualStudio.DiagnosticsHub.*` | 確定不能・`ol` は正しい |
| ライセンス文書が SPDX テンプレートに一致しない | 8 | `Microsoft.Extensions.*@2.1.1` | **確定できるべき → Round 2 で修正** |
| 宣言 URL のリンク切れ | 2 | `Google.Protobuf@3.18.0` | 確定不能だが可視性に課題 → [F3](#f3-宣言された-github-ファイルが読めなかった事実が-component-に残らない) |
| SPDX 外の外部ライセンスページ / リダイレクタ | 2 | `Portable.BouncyCastle@1.9.0` | 確定不能・`ol` は正しい |
| npm monorepo subdirectory | 1 | `@pandacss/is-valid-prop@0.54.0` | 確定不能・`ol` は正しい |

この 120 種のうち、修正対象になったのは 3 行目の 8 種だけである。残る 112 種は証跡そのものが SPDX 識別子へ落ちない。

最大勢力の 102 件は次の形をしている。

```bash
jq '.components[]|select(.name=="Microsoft.CSharp" and .version=="4.0.1")' UniTask.json
```

```json
{
  "name": "Microsoft.CSharp", "version": "4.0.1", "license": "-", "status": "unknown",
  "licenseCandidates": [
    { "source": "nuget-registry", "status": "unknown",
      "evidence": { "declaredLicenseReferenceKind": "location",
                    "declaredLicenseReference": "http://go.microsoft.com/fwlink/?LinkId=329770" } },
    { "source": "source-repository", "kind": "unsupported", "raw": "https://dot.net/",
      "warnings": ["unsupported_source_repository"] }
  ],
  "warnings": ["unsupported_source_repository"]
}
```

`LinkId=329770` は MICROSOFT .NET LIBRARY の EULA へのリダイレクタで、SPDX 識別子ではない。`projectUrl` は `https://dot.net/` で GitHub ですらない。**これは機械可読な証跡が本当に存在しない事例であり、`unknown` が正解である。** 人間が「dotnet/corefx だから MIT だろう」と補うのは推測であって、`ol` がそれをやらないのは設計どおり。ただし UniTask では 141 中 88 件がこれになるため、実運用では baseline での受容が前提になる ([運用手順](#確定しようがないケースの運用))。

## Round 2: ライセンス文書内の宣言 URL を読む

### 発見した誤検知

分類作業中に、ライセンス文書のうち SPDX テンプレートに一致しなかったものを集めて `SpdxLicenseTextMatcher` へ直接かけた。

```text
corpus=e4c1f27 templates=733
github-file_aspnet_home_2.0.0_LICENSE.txt.txt:            match=False id=-           bytes=593
github-file_jamesnk_newtonsoft.json_master_LICENSE.md.txt: match=True  id=MIT         bytes=1084
github-file_kevin-montrose_linqaf_master_LICENSE.txt.txt:  match=True  id=Apache-2.0  bytes=11344
github-file_miloszkrajewski_lz4net_master_LICENSE.md.txt:  match=True  id=BSD-2-Clause bytes=1320
github-file_xunit_xunit.analyzers_master_LICENSE.txt:      match=False id=-           bytes=591
github-file_xunit_xunit_master_license.txt.txt:            match=True  id=MIT         bytes=2357
protobuf-LICENSE.txt:                                      match=False id=-           bytes=1732
```

最後から 2 行目が問題である。`xunit/xunit@master/license.txt` の中身はこうなっている。

```text
Unless otherwise noted, the source code here is covered by the following license:

    Copyright (c) .NET Foundation and Contributors
    All Rights Reserved

    Licensed under the Apache License, Version 2.0 (the "License");
    ...
        http://www.apache.org/licenses/LICENSE-2.0
    ...

-----------------------

The code in src/common/AssemblyResolution/Microsoft.DotNet.PlatformAbstractions was imported from:
    https://github.com/dotnet/core-setup/tree/v2.0.1/src/managed/Microsoft.DotNet.PlatformAbstractions
...
Both sets of code are covered by the following license:

    The MIT License (MIT)
    ... (MIT 全文) ...
```

**プロジェクト本体は Apache-2.0、1 サブディレクトリに取り込んだコードだけが MIT** という文書である。`ol` は MIT 全文だけをテンプレートとして認識し、Apache-2.0 は「通知形式 + 正典 URL」でしか書かれていないため見えなかった。結果、識別子が 1 つしか一致せず、単一解として MIT を採用していた。

レポート上の実害は次のとおり。同一レポート内で新旧 xunit の答えが食い違っていた。

```bash
jq -r '.components[]|select(.name|startswith("xunit"))|"\(.name)@\(.version) -> \(.license)"' DFrame.json
```

```text
xunit@2.4.1 -> MIT                 <- 誤り (実際は Apache-2.0)
xunit.core@2.4.1 -> MIT            <- 誤り
xunit.runner.visualstudio@2.4.1 -> MIT  <- 誤り
xunit.v3@1.0.0 -> Apache-2.0       <- 正しい (nuspec が SPDX 式を宣言)
```

GitHub 自身の License API はこのファイルに `NOASSERTION` / `licenseKey: "other"` を返しており、判断を保留していた。`ol` はそれを上書きして誤答していた。

### 原因

`SpdxLicenseTextMatcher` は「文書のどこかに license テンプレートが部分一致するか」を全テンプレートについて調べ、**相異なる識別子が 2 つ以上一致したときだけ** 未解決にする。曖昧性ガード自体は正しいが、`ol` が認識できるライセンス表明が「全文の再現」だけだったため、通知形式で書かれた Apache-2.0 がガードの入力に入らなかった。

つまり根本原因は単一で、**表明の一形態が見えていないこと**である。そしてこの盲点は同時に取りこぼしも生んでいた。593 バイトの `aspnet/Home@2.0.0/LICENSE.txt` は全文が Apache-2.0 の通知段落そのもので、これも一致しなかった。

### 修正

盲点を塞ぐ。`SpdxLicenseTextMatcher` に SPDX license index を渡せるようにし、文書中に含まれる URL のうち **SPDX license list 自身が `seeAlso` として 1 ライセンスにだけ公開している URL** を、テンプレート一致と同じ単一解ルールに参加させた。

新しい知識源は導入していない。`ol` は既に「宣言された `licenseUrl` が SPDX の `seeAlso` なら識別子として解決する」を行っている ([`spdx.md#contract-spdx-license-see-also`](../specs/spdx.md))。同じ規則を、読むと決めた文書の中身にも適用しただけである。

真理値表 (T = テンプレート一致, U = URL 解決):

| T | U | 期待 | 実例 |
|---|---|---|---|
| なし | なし | 未解決 | proprietary EULA |
| 単一 X | なし | X | 通常の MIT `LICENSE` |
| 衝突 | — | 未解決 | 従来どおり |
| なし | 単一 X | **X (新規)** | `aspnet/Home@2.0.0/LICENSE.txt` |
| なし | 複数 | 未解決 (新規) | THIRD-PARTY-NOTICES |
| 単一 X | 単一 X | X | Apache-2.0 全文 (付録に正典 URL) |
| 単一 X | 単一 Y | **未解決 (新規)** | `xunit/xunit@master/license.txt` |
| 単一 X | 複数 | 未解決 (新規) | — |

複数ライセンスで共有されている `seeAlso` URL は index 構築時に既に除外されているため、`https://opensource.org/licenses/LGPL-2.1` のような URL は何も解決しない。この規則も既存のものをそのまま使っている。

変更ファイル:

- `src/Ol.Core/Spdx/SpdxLicenseIndex.cs` — `TryResolveLicenseUrl` の `ReadOnlySpan<char>` 版を追加 (文書は既に decode 済みなので再 encode を避ける)
- `src/Ol.Core/Spdx/SpdxLicenseTextMatcher.cs` — 宣言 URL 走査を追加、テンプレート走査と統合
- `src/Ol/SpdxData.cs` — matcher 構築時に index を渡す

テストは先に書いて赤を確認した。

```bash
dotnet test --project tests/Ol.Tests/Ol.Tests.csproj --treenode-filter '/*/*/SpdxLicenseTextMatcherTests/*'
# 赤: error CS1739: The best overload for 'SpdxLicenseTextMatcher' does not have a parameter named 'licenseIndex'
# 緑: total: 23, failed: 0
```

URL の綴り (`HTTP://WWW.` / 末尾スラッシュ / 括弧や句点に囲まれた場合) は `[Arguments]` で 4 パターン網羅した。

### 実データでの検証

8 リポジトリ 2,833 component を再走査し、修正前後を突き合わせた。

```bash
diff <(cut -f2,3,4 before.tsv|sort -u) <(cut -f2,3,4 after.tsv|sort -u)
```

```text
< Microsoft.Extensions.Configuration.Abstractions@2.1.1   -   unknown
> Microsoft.Extensions.Configuration.Abstractions@2.1.1   Apache-2.0  matched
< Microsoft.Extensions.Configuration.Binder@2.1.1         -   unknown
> Microsoft.Extensions.Configuration.Binder@2.1.1         Apache-2.0  matched
  ... (Microsoft.Extensions.* 2.1.1 が計 7 件)
< xunit.analyzers@0.10.0                                  -   unknown
> xunit.analyzers@0.10.0                                  Apache-2.0  matched
> xunit@2.4.1                                             -   unknown
< xunit@2.4.1                                             MIT matched
  ... (xunit 2.4.x 系が計 14 種)
```

**変化したのは 2,833 行中 30 行 (package 種としては 22 種) のみで、残る 2,803 行は完全に一致した。** 巻き添えゼロ。内訳は

- 新たに確定 10 行 / 8 種: `Microsoft.Extensions.*@2.1.1` × 7 (ASP.NET Core 2.x = Apache-2.0)、`xunit.analyzers@0.10.0` (Apache-2.0)
- 誤答を撤回 20 行 / 14 種: xunit 2.4.x 系が `MIT (matched)` → `unknown`

後者が `unknown` に落ちるのは正しい。あの `license.txt` は機械可読な範囲では 2 つのライセンスを名指しており、どちらが package を支配するかを書いていない。**推測して当てるのではなく、確定しようがないと申告するのが `ol` の正しい振る舞いである。**

`ol diff` でも変化を追える。

```bash
$OL diff --previous before/DFrame.json --current after/DFrame.json
```

```text
License-relevant changes: 15 changes in 8 components.

~ nuget:xunit@2.4.1
    status: matched -> unknown
    license: MIT -> -

~ nuget:xunit.analyzers@0.10.0
    status: unknown -> matched
    license: - -> Apache-2.0
...
```

## Round 3: 証跡の正確さ

Round 2 の修正直後、evidence はこうなっていた。

```json
{ "source": "package-artifact", "raw": "Apache-2.0", "status": "matched",
  "evidence": { "path": "LICENSE.txt", "matcher": "spdx-template", "corpusVersion": "e4c1f27" } }
```

`matcher: "spdx-template"` は嘘である。この Apache-2.0 は文書中の宣言 URL から解決しており、`LICENSE.txt` に Apache-2.0 の全文は入っていない。レビュアがこの evidence を頼りに文書を開くと、書いてあるはずのものが見つからない。`ol` の価値は証跡の正確さにあるので、自分の変更でそこを濁したままにはできない。

`SpdxLicenseTextMatchKind` を追加し、`TryMatch` の 3 引数版でどちらの読み方が答えを出したかを返すようにした。4 つの収集経路 (NuGet restore artifact / package artifact registry / declared GitHub file / GitHub file cache) がこれを evidence の `matcher` に流す。既存の 2 引数版は残してあるので呼び出し側の互換は保たれる。

```bash
jq -c '.components[]|select(.name=="Microsoft.Extensions.Configuration" and .version=="2.1.1")' MagicOnion.json
```

```json
{ "license": "Apache-2.0", "status": "matched",
  "cands": [ { "source": "package-artifact", "raw": "Apache-2.0",
               "path": "LICENSE.txt", "matcher": "spdx-license-url" } ] }
```

全リポジトリでの内訳:

```text
     16 spdx-license-url  matched
    775 spdx-template     matched
     39 spdx-template     unknown
```

解決に失敗した文書は `spdx-template` のままにしてある。試みたのはテンプレート照合だからで、失敗を新しい名前で呼ぶ理由がない。

### テストとベンチマーク

```bash
dotnet test
# total: 1035, failed: 0, succeeded: 1035
```

SPDX 照合はホットパスなので、index 有無の A/B をベンチマークに追加して測った。

```bash
dotnet run --project src/Ol.Benchmark/Ol.Benchmark.csproj -c Release -- --filter '*SpdxLicenseTextMatcherBenchmark*'
```

| Method | Mean | Allocated |
|---|---:|---:|
| Match | 20.97 us | - |
| MatchBundledCorpus (URL 走査あり) | 631.75 us | - |
| MatchBundledCorpusTemplateOnly (従来相当) | 632.55 us | - |
| ConstructBundledCorpus | 12,623.49 us | 11,431,563 B |

差は測定誤差の範囲、allocation は両方 0 B。733 テンプレートの regex 評価に対して 1 KB 程度の文書を 1 回線形走査するだけなので、構造的にも無視できる。

## SBOM 経路と非 SBOM 経路の比較

ZLinq を題材に 3 経路を比較した。同じリポジトリ、同じ復元済み依存に対して入力形式だけを変えている。

### 入力の用意

```bash
# CycloneDX
cd /d/github/cysharp/ZLinq
dotnet CycloneDX ZLinq.slnx -o ./zlinq-cdx -fn bom.cdx.json -F Json -dpr
# => Found 111 packages

# SPDX 2.2
sbom-tool generate -b ./zlinq-spdx -bc /d/github/cysharp/ZLinq \
  -pn ZLinq -pv 0.0.0 -ps Cysharp -nsb https://cysharp.example/zlinq
# => _manifest/spdx_2.2/manifest.spdx.json
```

### 結果

```bash
$OL scan --input . --format json                                    # 非 SBOM
$OL scan --input zlinq-cdx/bom.cdx.json --format json               # CycloneDX
$OL scan --input zlinq-spdx/_manifest/spdx_2.2/manifest.spdx.json --format json  # SPDX
$OL scan --input zlinq-cdx/bom.cdx.json --input . --format json     # 併用
```

| | 非 SBOM (nuget-assets) | CycloneDX | SPDX 2.2 | CycloneDX + 非 SBOM |
|---|---|---|---|---|
| component 数 | 108 | 109 | 124 | 109 |
| matched | 105 | 104 | 116 | 105 |
| unknown | 3 | 4 | 8 | 3 |
| ambiguous | 0 | 1 | 0 | 1 |
| dependency: direct | 31 | 17 | 41 | 31 |
| dependency: transitive | 77 | 44 | 82 | 77 |
| **dependency: unknown** | **0** | **47** | **0** | **0** |
| Package artifacts targets | 139 | **0** | **0** | 139 |

### SBOM-1: SBOM 経路では package artifact 証跡が完全に失われる

これが最大の差である。非 SBOM 経路では `project.assets.json` から復元済み package のパスが分かるため、`ol` は **ビルドが実際に消費した nupkg の中の LICENSE ファイル** を読める。summary で `Package artifacts (full scan): 139 targets` と出ていたものが、SBOM 経路では `0 targets` になる。

実害の例。

```bash
jq -c '.components[]|select(.name=="Microsoft.DotNet.PlatformAbstractions")' <各レポート>
```

```text
# 非 SBOM
{"license":"MIT","status":"matched",
 "cands":[{"source":"package-artifact","raw":"MIT","path":"LICENSE.TXT"}, ...]}

# CycloneDX / SPDX
{"license":"-","status":"unknown","warnings":["license_not_detected"],
 "cands":[{"source":"nuget-registry","raw":""},{"source":"github-license-api","raw":"NOASSERTION"}]}
```

registry も GitHub も答えを持たない package が、手元の nupkg には答えを持っている。SBOM 経路はそこへ到達できない。

**評価:** `ol` の落ち度ではなく入力の情報量の差だが、summary に `Package artifacts (full scan): 0 targets` と正直に出るので気付ける。設計として正しい。

### SBOM-2: CycloneDX 経路で dependency 分類が 47/109 失われる

`dotnet CycloneDX` が出力した BOM には `dependencies` が 109 件あり、root (`ZLinq@0.0.0`) の `dependsOn` も存在する。しかし solution 全体を 1 BOM にまとめる過程で root の直接依存リストが不完全になっており (例: `Microsoft.NET.Test.Sdk@17.13.0` は載るが `17.14.1` は載らない)、root から到達できない component が 47 件出る。`ol` はそれらを推測せず `dependency: unknown` にした。

**評価:** `ol` の判断は正しい。ただし利用者から見ると `--dependency direct` の結果が 31 → 17 に減り、`--allow-dev-licenses` も効かなくなる。にもかかわらず summary には手掛かりがない (`0 incomplete input sets` と出る)。

**改善案 (未実装):** summary の `Input discovery` 行に「root から到達できない component 数」を出す。`ol` 側の欠陥ではないと分かる形で書けば、利用者は生成ツール側を疑える。実装していないのは、これが `ol` の正しさの問題ではなく表示の問題で、Round 2/3 の変更と独立に判断すべきだと考えたため。

なお SPDX 経路 (`sbom-tool`) では relationship が完全で、dependency 分類は失われなかった (41 direct / 82 transitive / 0 unknown)。生成ツール依存の問題である。

### SBOM-3: SBOM が持ち込む固有の入力

CycloneDX 経路だけ `ambiguous` が 1 件出た。

```json
{"name":"Microsoft.NETCore.Platforms","version":"1.1.0",
 "licenses":[{"license":{"name":"Unknown - See URL","url":"http://go.microsoft.com/fwlink/?LinkId=329770"}}]}
```

生成ツールが `Unknown - See URL` という文字列を license name として書き込んでいる。`ol` はこれを SPDX 名として解決できず `ambiguous` にした。非 SBOM 経路では同じ package が `unknown` になる。**同じ事実に対して SBOM が余計な主張を足すと status が変わる**という、SBOM 経路固有の挙動である。`ol` は正しく拒否しているので問題はないが、経路によって status が変わることは知っておく必要がある。

SPDX 経路では逆に、Unity UPM package が component として現れた。

```text
com.cysharp.zlinq@1.5.6            unknown  [source_repository_unavailable, package_metadata_not_found]
com.cysharp.zlinq.internal@1.0.0   unknown  [source_repository_unavailable, package_metadata_not_found]
```

public registry に存在しない package を `package_metadata_not_found` として正しく分類している。README の言う「private feed の package はこう見える」がそのまま出た。

また両 SBOM 経路とも、成果物自身 (`ZLinq@0.0.0`) が component として現れ未解決になる。`ol check` はこれを violation として数えるので、実運用では `--exclude-packages` か baseline が要る。

### SBOM 経路の推奨

**CycloneDX SBOM と復元済みディレクトリを併用するのが最良だった。**

```bash
$OL scan --input bom.cdx.json --input .
```

この 1 コマンドで、SBOM 側の宣言 (`Microsoft.NETCore.Platforms` の `ambiguous` 判定) を保ちつつ、package artifact 証跡と dependency 分類を完全に回復した (matched 105 / unknown 3 / direct 31 / transitive 77 = 非 SBOM 経路と同値)。README にある「1 つの SBOM と package-manager 入力は 1 レポートに合成できる」の実用価値が、この組み合わせで最もはっきり出る。README の該当箇所は現状 TIP の一文なので、CI 例として昇格させる価値がある。

## 確定しようがないケースと `ol` の検出

「原理的に確定できない」ものを洗い出し、`ol` が正しくそう報告するかを確認した。修正後に残った未解決 (fwlink 系 102 件を除く) は以下で、**いずれも確定不能であり、`ol` の分類は妥当だった**。

| Package | 事実 | `ol` の warning | 妥当性 |
|---|---|---|---|
| `Microsoft.CSharp@4.0.1` ほか 101 件 | `licenseUrl` が MS EULA リダイレクタ、`projectUrl` が `https://dot.net/` | `unsupported_source_repository` | ○ SPDX 識別子に落とせる証跡が無い |
| `Microsoft.VisualStudio.DiagnosticsHub.*` | nupkg 内 `LICENSE.md` が Microsoft VS の supplement EULA | `license_not_detected` | ○ 文書は読んだが SPDX ライセンスではない |
| `Microsoft.Windows.SDK.Win32Metadata@52.0.65-preview` ほか | nupkg 内 `sdk_license.txt` が Windows SDK EULA | `license_not_detected` + `license_not_recognized` | ○ 同上 |
| `Portable.BouncyCastle@1.9.0` | `licenseUrl` が `bouncycastle.org`、リポジトリの LICENSE は Bouncy Castle Licence | `license_not_recognized` | ○ MIT に酷似するが同一ではない |
| `Google.Protobuf@3.18.0` | LICENSE は BSD-3-Clause 相当だが Google の追加条項付き。GitHub API も `NOASSERTION` | `license_not_recognized` + `source_repository_ref_not_found` | ○ 単一 SPDX 識別子に落ちない |
| `xunit@2.4.1` ほか 13 種 | 1 文書が Apache-2.0 (本体) と MIT (取り込みコード) を名指す | `license_not_detected` + `license_not_recognized` | ○ **Round 2 で誤答から訂正** |
| `@pandacss/is-valid-prop@0.54.0` | npm registry の `license` が `null`、repository は monorepo subdirectory | `source_repository_subdirectory` | ○ package 自身が何も宣言していない |
| `com.cysharp.zlinq@1.5.6` ほか | public registry に存在しない Unity package | `package_metadata_not_found` | ○ private feed の正しい表現 |

`ol` が推測で埋めなかった点はすべて妥当だった。`@pandacss/is-valid-prop` は実際には MIT だが、npm registry を直接引くと `"license": null` であり、monorepo root の LICENSE を subdirectory package に適用しないという判断も正しい。

### 確定しようがないケースの運用

`check` が fail-closed であることと baseline の往復を確認した。

```bash
$OL check --report ZLinq.json --allow-licenses MIT,Apache-2.0,BSD-2-Clause,BSD-3-Clause
```

```text
License check failed: 3 violations.

Package                      Version       Ecosystem  License/Status  Reason                 Path
Microsoft.NETCore.Platforms  1.1.0         nuget      unknown         license is unresolved  pkg:nuget/NETStandard.Library@2.0.3 > pkg:nuget/Microsoft.NETCore.Platforms@1.1.0
Microsoft.VisualStudio...    18.0.36421.1  nuget      unknown         license is unresolved  -
Microsoft.VisualStudio...    18.0.36421.1  nuget      unknown         license is unresolved  -
```

exit code 2。`Path` 列に依存経路が出るので、なぜその package が入っているかを追える。

```bash
$OL check --report ZLinq.json --allow-licenses MIT,Apache-2.0,BSD-2-Clause,BSD-3-Clause \
  --baseline zlinq-baseline.json --update-baseline
```

```text
Acknowledged by baseline: 3 components.
License check passed: 108 components satisfy the allow-list.
```

exit code 0。再実行しても同じ結果で安定した。UniTask のように 88 件が確定不能なリポジトリでも、この手順なら 1 度レビューして commit すれば以後は差分だけを見られる。

## 残した課題

以下は原因まで特定したが、この検証では実装しなかった。理由も併記する。

### F3: 宣言された GitHub ファイルが読めなかった事実が component に残らない

`Google.Protobuf@3.18.0` の `licenseUrl` は `https://github.com/protocolbuffers/protobuf/blob/master/LICENSE` だが、protobuf は既定ブランチを `main` に改名しており `master` は存在しない。

```bash
curl -s -o /dev/null -w "%{http_code}\n" -H "Authorization: Bearer $GH" \
  "https://api.github.com/repos/protocolbuffers/protobuf/contents/LICENSE?ref=master"
# 404   {"message":"No commit found for the ref master"}
```

`CommandLineParser@2.4.3` も同型で、`gsscoder/commandline` はリポジトリ自体が移動しており宣言 URL が 404 になる。

現状 `ol` は 404 のとき candidate を作らず、component の warning にも何も残さない。これは [`source.md`](../specs/source.md) が「404 は artifact evidence を捏造せず宣言参照をそのまま残す」と明示している意図的な設計で、その判断自体は正しい。しかし結果として **「publisher が示したライセンスへのリンクが切れている」という調査可能な事実が消える**。レビュアはリポジトリが改名されたことに気付けば人力で解決できる種類の未解決である。

**改善案:** artifact evidence は作らないまま、component に `declared_license_file_unavailable` 相当の warning を残す。

**実装しなかった理由:** `LicenseCandidateWarnings` は `ushort` で `1 << 14` まで使用済み、残り 1 bit。この enum は「レポートが型で持っている事実は入れない」方針で 3 つの warning を削って bit を確保した経緯がコメントに残っている。最後の 1 bit を独断で使うのは適切でないと判断した。run 単位では `Declared GitHub files: 4 targets; ... 3 documents` の差分から不可読数を導出できるため、component 単位の可視性をこの bit に見合う価値と見るかは維持者の判断に委ねる。

### F4: `--format json` では stderr summary が出ない

```bash
$OL scan --input . --format json 1>/dev/null 2>err.txt   # err.txt: 0 bytes
$OL scan --input . --format markdown 1>/dev/null 2>err.txt # err.txt: 984 bytes
```

`src/Ol/ScanCommands.cs:147` の JSON 分岐が `return 0;` で早期復帰し、`if (!quiet)` の summary ブロックへ到達しない。text / markdown では出る。

影響は 2 点。`--quiet` の説明 (`Suppress stderr summary`) が JSON では意味を持たない。そして README の CI 例 `ol scan --input . --format json > ol-report.json` は、実行ログに何も残らない。

JSON レポート自体が `summary` と `metadata` を持つので情報は失われていない、という読み方も成り立つ。挙動の変更は利用者の期待に触れるため、報告に留めた。

### F5: 同一ライセンス文書の candidate が重複する

```bash
jq '.components[]|select(.name=="Microsoft.VisualStudio.DiagnosticsHub.UserMarks")' ZLinq.json
```

`path: "LICENSE.md"`、`contentSha256: 0cc93faa...` が完全に同一の `package-artifact` candidate が 2 つ並ぶ。同じ nupkg が複数の `project.assets.json` から参照されたときに重複しているとみられる。同一 sha なので結論は変わらないが、レポートのノイズになる。

### F6: summary の `Declared GitHub files` 行は単位が混在する

```text
Declared GitHub files (full scan): 4 targets; 1 GitHub requests; 3 cache hits; 1 cache misses; 3 documents; 7 matched; 0 fetch errors
```

`matched` (7) が `documents` (3) を超える。前者は component 数、後者は取得した文書数で、1 文書が複数 component に効くため。数値としては正しいが、同一行の他の項目がすべて target 単位なので初見では計数バグに見える。

## 実行したコマンドの一覧

再現用にまとめる。`$OL` と `OL_GITHUB_TOKEN` は [検証環境](#検証環境) のとおり。

```bash
# Round 1: 非 SBOM 経路
for r in AIApiTracer DFrame LogicLooper MagicOnion NativeCompressions UniTask ZLinq csbindgen; do
  cd /d/github/cysharp/$r && $OL scan --input . --format json > out/$r.json 2> out/$r.err
done
cd /d/github/cysharp/ZLinq && $OL scan --input . --format text          # summary 確認
cd /d/github/cysharp/DFrame && $OL scan --input . --format text --verbose

# 未解決の分類
jq -r '.components[]|select(.status!="matched")|[.name+"@"+.version,(.warnings|join("+"))]|@tsv' out/*.json | sort -u

# Round 2/3 後の再走査と突き合わせ
for r in ...; do cd /d/github/cysharp/$r && $OL scan --input . --format json > out3/$r.json; done
diff <(cut -f2,3,4 before.tsv|sort -u) <(cut -f2,3,4 after.tsv|sort -u)
$OL diff --previous out/DFrame.json --current out3/DFrame.json

# SBOM 経路
dotnet CycloneDX ZLinq.slnx -o zlinq-cdx -fn bom.cdx.json -F Json -dpr
sbom-tool generate -b zlinq-spdx -bc /d/github/cysharp/ZLinq -pn ZLinq -pv 0.0.0 -ps Cysharp -nsb https://cysharp.example/zlinq
$OL scan --input zlinq-cdx/bom.cdx.json --format json
$OL scan --input zlinq-spdx/_manifest/spdx_2.2/manifest.spdx.json --format json
$OL scan --input zlinq-cdx/bom.cdx.json --input . --format json         # 併用 (推奨)

# 運用確認
$OL check --report out3/ZLinq.json --allow-licenses MIT,Apache-2.0,BSD-2-Clause,BSD-3-Clause
$OL check --report out3/ZLinq.json --allow-licenses MIT,Apache-2.0,BSD-2-Clause,BSD-3-Clause \
  --baseline zlinq-baseline.json --update-baseline
$OL scan --input . --no-external-evidence --format text
$OL scan --input . --group-by license --dependency direct --quiet

# 検証
dotnet test                                                              # 1035 passed
dotnet run --project src/Ol.Benchmark/Ol.Benchmark.csproj -c Release -- --filter '*SpdxLicenseTextMatcherBenchmark*'
```

## 学んだこと

**単一解ルールは、比較対象が全部見えているときにしか安全でない。** `SpdxLicenseTextMatcher` の「識別子が 2 つ一致したら未解決」は正しい保守的ルールだったが、片方のライセンスが `ol` の認識できない形式で書かれていると、ガードは発動せずに残った 1 つが単一解として通る。曖昧性ガードを足すときは、ガードが見落とす表明形式が何かを同時に問う必要がある。今回は「全文の再現」しか見ていないことが盲点で、それを塞いだら取りこぼしと誤検知が同時に消えた。

**外部の権威が保留したときは、それを上書きする前に理由を疑う。** GitHub License API はこの `license.txt` に `NOASSERTION` を返していた。`ol` はより強い証跡 (テンプレート完全一致) を持っていたので上書きしたが、実際には GitHub のほうが正しかった。既存の candidate が保留しているのに自分だけが確定するときは、その差が本当に証跡の強さから来ているのかを確認する価値がある。

**証跡のラベルは実装を変えたら必ず追随させる。** Round 2 の直後、URL から解決した結果に `matcher: "spdx-template"` が付いたままだった。テストは全部緑で、レポートの license 値も正しく、実害が出るのはレビュアが証跡を頼りに文書を開いた瞬間だけである。`ol` のように証跡が製品価値そのものである道具では、この種の不整合はバグと同じ重みで扱う。

## 追試: baseline 運用で CI に載るか

「Microsoft の .NET 基盤ライブラリは baseline に入れる」という実務判断を置いたうえで、CI と同じ形 (コールドキャッシュ、baseline 往復、再現性) で回した記録。

### 違反の内訳は 2 種類に分かれる

まず allow-list だけで検査すると 8 リポジトリ合計 353 violation が出た。理由別の内訳が重要である。

```bash
ALLOW="MIT,Apache-2.0,BSD-2-Clause,BSD-3-Clause,ISC,MS-PL,Unlicense,CC0-1.0"
for r in ...; do $OL check --report out3/$r.json --allow-licenses "$ALLOW"; done
```

```text
    268 license is unresolved     <- baseline の対象
     85 license is not allowed    <- baseline では消えない。ポリシー判断が要る
```

**baseline が受容できるのは「未解決」だけで、「解決できたが許可リストにない」は消えない。** これは正しい設計である。実際に出たのは `MPL-2.0` (lightningcss)、`CC-BY-4.0` (caniuse-lite)、`BlueOak-1.0.0`、`0BSD` で、いずれも `ol` は正しく確定している。許可するかどうかは組織の判断であり、`ol` が黙って通してはいけない。

matched 全体のライセンス分布は次のとおりで、複合式も含まれる。

```text
2375 MIT / 252 Apache-2.0 / 96 ISC / 62 MIT-0 / 37 BSD-3-Clause / 35 BSD-2-Clause
  11 MPL-2.0 / 4 BlueOak-1.0.0 / 4 0BSD / 3 CC0-1.0 / 2 Python-2.0 / 2 CC-BY-4.0
   2 (WTFPL OR MIT) / 2 (MIT OR CC0-1.0) / 1 (MPL-2.0 OR Apache-2.0) / 1 (BSD-2-Clause OR MIT OR Apache-2.0)
```

`WTFPL` を許可リストへ入れると `(WTFPL OR MIT)` が違反から消え、`Apache-2.0` を許可した状態で `(MPL-2.0 OR Apache-2.0)` も消えた。OR 式は「どれか 1 つが許可されていれば可」として正しく評価されている。

### `--allow-dev-licenses` は証明があるときだけ効く

```bash
$OL check --report out3/AIApiTracer.json --allow-licenses "$ALLOW" --allow-dev-licenses "MPL-2.0,CC-BY-4.0"
```

```text
Allowed by development policy: 11 components.
License check passed: 100 components satisfy the allow-list.
```

AIApiTracer の `lightningcss` は `usage: "development"` を持つ (`package-lock.json` の `dev: true` 由来) ため緩和された。一方 MagicOnion の `caniuse-lite` は Docusaurus の通常依存で `usage` が付かず、緩和されない。**同じライセンスでも入力が dev-only を証明できるかで結果が変わる。** 期待どおりだが、リポジトリごとに挙動が違う点は運用前に把握しておく必要がある。

### baseline 適用後の結果

```bash
ALLOW="MIT,MIT-0,Apache-2.0,BSD-2-Clause,BSD-3-Clause,0BSD,ISC,CC0-1.0,Unlicense,BlueOak-1.0.0,Python-2.0,WTFPL"
$OL check --report out3/$r.json --allow-licenses "$ALLOW" --allow-dev-licenses "MPL-2.0,CC-BY-4.0" \
  --baseline baseline/$r.json --update-baseline
```

| Repository | baseline 件数 | exit | 備考 |
|---|---:|---:|---|
| AIApiTracer | 0 | 0 | baseline 不要。dev 緩和 11 件のみ |
| DFrame | 83 | 0 | |
| LogicLooper | 11 | 0 | |
| MagicOnion | 73 | 2 → 0 | `caniuse-lite` CC-BY-4.0 を許可リストへ入れれば 0 |
| NativeCompressions | 3 | 2 → 0 | 同上 |
| UniTask | 94 | 0 | 141 中 94 が baseline。旧 .NET 依存が主 |
| ZLinq | 3 | 0 | |
| csbindgen | 1 | 0 | |

MagicOnion / NativeCompressions の 1 件は `caniuse-lite` (CC-BY-4.0、browserslist のデータファイル) で、許可リストへ加えれば全 8 リポジトリが exit 0 になる。**Microsoft の旧 .NET 基盤ライブラリを baseline に入れる方針で、8 リポジトリすべてが CI で green にできる。**

### CI で効く性質を 3 つ確認した

**1. 再スキャンしても baseline が揺れない。** 新しくスキャンし直したレポートを既存 baseline で検査した。

```bash
$OL scan --input . --format json > rescan.json
$OL check --report rescan.json --allow-licenses "$ALLOW" --baseline baseline/$r.json
```

```text
DFrame       exit=0 | Acknowledged by baseline: 83 components.
UniTask      exit=0 | Acknowledged by baseline: 94 components.
ZLinq        exit=0 | Acknowledged by baseline: 3 components.
csbindgen    exit=0 | Acknowledged by baseline: 1 component.
LogicLooper  exit=0 | Acknowledged by baseline: 11 components.
```

fingerprint は `collectedAt` のようなスキャン時刻では変わらない。これがないと毎回 CI が赤くなるので、最も重要な性質である。

**2. baseline は新しい未解決を吸収しない。** 修正前レポート (未解決 5 件) から作った baseline で、修正後レポート (未解決 11 件) を検査した。

```text
Acknowledged by baseline: 4 components.
License check failed: 7 violations.
xunit  2.4.0  nuget  unknown  license is unresolved  -
...
exit=2
```

新規の 7 件は素通りせず violation になった。さらに acknowledged が 5 → **4** に減っている。`xunit.analyzers@0.10.0` が未解決から Apache-2.0 に変わり、baseline が持つ evidence の fingerprint と一致しなくなったためである。**baseline は package 名ではなく evidence を覚えている。**

**3. 一過性の収集失敗は baseline に焼き付かない。** `LicenseAllowPolicy.CanAcknowledge` (`src/Ol.Core/Licensing/LicenseAllowPolicy.cs:191`) は `Unknown / Ambiguous / Conflict / Invalid` だけを受容対象にしており、`LicenseStatus.Error` を明示的に除外している。timeout・429・5xx は `error` になり、`check` は violation が全て `Error` のとき exit 3 (inconclusive) を返す (`src/Ol/CheckCommands.cs:175`)。

つまり **GitHub がレート制限を返した回で `--update-baseline` を走らせても、誰もレビューしていない package が「レビュー済み」として記録されることはない。** CI に載せるうえでここが一番怖い部分だったが、設計で塞がれている。

### コールドキャッシュのコスト

CI の実行環境はキャッシュが空である。`--cache-dir` を新しいディレクトリへ向けて実測した。

```bash
time $OL scan --input . --cache-dir ./coldcache --format json > report.json
```

| Repository | component 数 | 所要 | registry 要求 | GitHub 要求 | 結果 |
|---|---:|---:|---:|---:|---|
| ZLinq | 108 | 13 秒 | 108 | 28 | ウォームと完全一致 |
| MagicOnion | 1590 | 2 分 08 秒 | 1590 | 822 | ウォームと完全一致 (1517/73) |
| DFrame | 189 | — | 189 | 19 | 未認証でもウォームと一致 (106/83) |

**GitHub API の消費量は npm 依存の量に比例する。** MagicOnion の 820 requests は Docusaurus の npm ツリー由来で、NuGet だけの DFrame は 19 requests しか使わない。DFrame は `OL_GITHUB_TOKEN` なし (未認証 60 req/h) でも認証時と同一の結果になった。.NET リポジトリでは package registry と nupkg 内 LICENSE が主戦力で、GitHub License API は補助である。

注意が必要なのは npm を多く含むリポジトリである。GitHub Actions の `GITHUB_TOKEN` は 1 リポジトリあたり毎時 1,000 requests なので、MagicOnion の 822 は 1 回で 8 割を使う。matrix build や複数 job で回すならキャッシュ (`--cache-dir` を `actions/cache` に載せる) か PAT を検討する。レート制限に当たっても結果は `error` → exit 3 になるので、誤った green にはならない。

### SARIF

```bash
$OL check --report report.json --allow-licenses "$ALLOW" --sarif ol.sarif
```

```json
{"version":"2.1.0","rules":["OL0001","OL0002","OL0003","OL0004","OL0005","OL0006"],
 "results":[{"ruleId":"OL0001","level":"error","msg":"pkg:nuget/Shouldly@4.3.0: license is not allowed (BSD-3-Clause)"},
            {"ruleId":"OL0003","level":"error","msg":"pkg:nuget/Microsoft.NETCore.Platforms@1.1.0: license is unresolved. Introduced through pkg:nuget/NETStandard.Library@2.0.3 > pkg:nuget/Microsoft.NETCore.Platforms@1.1.0"}]}
```

理由が rule id で分かれており (`OL0001` = 許可外、`OL0003` = 未解決)、未解決側には依存経路が入る。code scanning に載せてトリアージできる形になっている。

### 結論: 載る

`ol scan` → `ol diff` → `ol check --baseline` の 3 段で CI に載せられる。CI 化にあたって決めるべきは `ol` の側ではなく、次の 3 点である。

1. **許可リスト** — MPL-2.0 / CC-BY-4.0 / BlueOak-1.0.0 / 0BSD を許可するか。baseline では消えない。
2. **baseline のコミット** — Microsoft 旧 .NET 基盤ライブラリが主。UniTask で 94 件、8 リポジトリ計 268 件。1 度レビューして commit すれば以後は差分だけ見る。
3. **キャッシュと token** — npm 依存が多いリポジトリのみ `--cache-dir` の永続化を検討。NuGet だけなら不要。

SBOM 単体を入力にすると package artifact 証跡を失うので ([SBOM-1](#sbom-1-sbom-経路では-package-artifact-証跡が完全に失われる))、CI では復元済みディレクトリを直接渡すか、SBOM と併用する。
