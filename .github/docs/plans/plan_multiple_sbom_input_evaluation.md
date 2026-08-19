# 複数 SBOM 入力の評価と導入判断

## 目的

`ol scan` が複数の SBOM を一つの scan で受けるべきかを判断する。対象は CycloneDX/SPDX 文書の単純なファイル結合ではなく、複数文書から得た package inventory、dependency graph、license evidence を `ol` の正規化結果として統合する機能である。

調査日は 2026-08-19。現行 `ol` は次の三つを成立させている。

- SBOM のみ
- package-manager の解決済み入力のみ
- 一つの SBOM と複数の package-manager 入力

三つ目では package-manager の occurrence を細かい観測、SBOM を同じ解決結果の PURL-keyed projection として扱う。両者が同じ component を示す場合は package-manager row を残し、SBOM の license evidence、repository URL、より強い dependency type を吸収する。一方、SBOM は一つに制限されている。

## 結論

**一つの scan が複数 SBOM を扱えることには価値がある。ただし、現在の one-SBOM 制約を外して暗黙に union する変更は採用しない。**

導入するなら、複数文書が一つの audit subject を補完することを利用者が明示する **SBOM fragment set mode** として設計する。これは merged SBOM の生成機能ではなく、各入力文書の境界を残した `ol` report の生成機能とする。既定動作は当面 one-SBOM のままにする。

判断理由は次の通りである。

1. polyglot repository では、言語別 generator の精度を保ちながら一つの policy evaluation に集約したい実需がある。
2. CycloneDX for .NET 6.2.0 は .NET/NuGet 専用であり、同じ repository にある Cargo、npm、Go、Maven の解決資材は収集しなかった。言語別 generator を選ぶなら複数文書が自然に生じる。
3. 外部の flat merge は `bom-ref` 衝突、入力順依存の metadata、provenance 消失を起こし得る。hierarchical merge は衝突を避けられるが、現行 `ol` は nested component を読まないため package inventory を失う。
4. 現行の combiner と report schema は複数 SBOM の由来を保持できず、同一 format・同一 PURL の SBOM component が重なると license candidate を reconcile せず先着 row を残す。単に guard を削除する実装は fail-open になり得る。
5. 同じ product の補完 fragment、同じ product に対する別 generator の競合観測、別 product、同じ serial number の改訂版は意味が異なる。入力ファイル数だけでは merge semantics を決められない。

したがって推奨順序は次である。

1. 現在の one-SBOM 制約を維持し、外部 flat/hierarchical merge を回避策として推奨しない。
2. per-document provenance と overlap diagnostics を先に設計する。
3. 明示的な fragment set mode を追加し、補完 fragment の union と競合 evidence の reconciliation を行う。
4. 代替観測の比較、複数 product の aggregate、BOM-Link/SPDX external document reference の追跡は別機能として扱う。

## 用語と判断境界

この文書では、次を区別する。

| 操作 | 意味 | `ol` の扱い |
|---|---|---|
| SBOM file merge | 複数の CycloneDX/SPDX 文書を一つの標準文書へ書き換える | 非目標 |
| Fragment set | 同じ audit subject の相補的な部分を表す複数文書 | 将来の明示 mode の対象 |
| Alternate observations | 同じ範囲を別 generator、別設定、別時点で観測した文書 | 自動 union せず比較・競合診断 |
| Product aggregate | 独立して ship する複数 product/service の集合 | 原則別 report。必要なら link/manifest |
| Revision | 同じ CycloneDX `serialNumber` の異なる `version` | merge せず最新 revision を選択 |
| SBOM + resolver | 一つの SBOM と aligned package-manager inputs | 現行機能を維持 |

「repository が一つ」は同じ audit subject の十分条件ではない。release artifact、application、workspace、service、container など、何を policy evaluation するかを先に定義する。

## 対立する立場

### 肯定派: 複数 SBOM は標準化の約束を実用にする

**主張 1: SBOM は generator boundary を越えるための共通形式である。**

言語別 generator が最も正確な resolver graph と ecosystem metadata を出せるなら、それぞれを CycloneDX/SPDX に正規化して consumer で集約するのは自然である。一つの言語中立 generator へ固定すると、その generator が未対応の resolver output や設定を最小公倍数に落とす。

**反対派の反駁:** 共通 schema は同じ意味を保証しない。source scan と installed artifact scan、runtime と development、target framework、feature、platform、root subject が違っても形式は同じである。形式互換性を semantic alignment と誤認すると過剰・過少計上になる。

**肯定派の再反駁:** だから複数入力を拒絶するのではなく、文書単位の provenance と scope metadata を consumer が保持すべきである。標準文書の情報不足を検出可能にすることも consumer の価値である。

### 反対派: merge は不確実性を一つの見栄えの良い一覧へ隠す

**主張 1: union は coverage を増やすが、正しさを増やすとは限らない。**

同じ package の異なる build representation、stale SBOM、異なる target、internal project の package 誤認が union されると、component count は増えても audit subject から遠ざかる。今回の Syft scan でも一つの polyglot directory から複数 ecosystem を得られた一方、binary-derived component と duplicate-looking identity が入った。

**肯定派の反駁:** これは複数 SBOM 固有ではなく単一 repository-wide SBOM でも起きる。むしろ ecosystem ごとに生成範囲を限定し、文書別 coverage を比較できる fragment set の方が診断しやすい。

**反対派の再反駁:** その利点は `ol` が文書境界を保持する場合だけ成立する。現在の report は SBOM evidence に document id がなく、複数 path は `2 inputs` のような aggregate source reference になる。境界を消した union は診断性を悪化させる。

### 肯定派: polyglot CI の interface を単純化できる

各 ecosystem team が SBOM を生成し、最終 job が複数 SBOM を `ol` に渡せれば、`ol` は各 resolver command を知る必要がない。組織内外の upstream artifact SBOM も同じ入口へ渡せる。

**反対派の反駁:** package-manager input が持つ install occurrence、development/runtime usage、target context を SBOM generator が落とす場合、すべてを SBOM に変換してから渡すことは情報量を減らす。既に `ol` が直接読める resolved input まで SBOM 化する理由はない。

**判定:** 両方正しい。`ol` が直接読む resolved input は維持する。複数 SBOM は、upstream から SBOM しか得られない場合、または言語別 generator の出力が直接入力より必要な情報を持つ場合の追加経路にする。

### 反対派: root と dependency graph を安全に一つへできない

二つの SBOM は二つの metadata subject/root を持ち得る。flat merge はどちらを aggregate root にするか決められず、hierarchical merge は新しい assembly root と namespaced child graph を作る。これは単なる deduplication ではなく product model の決定である。

**肯定派の反駁:** `ol` report は merged SBOM ではないため、唯一の root を捏造する必要はない。文書ごとの graph context を並存させ、component view だけを PURL で reconcile できる。

**判定:** この形を採る。graph occurrence は文書ごとに保持し、component summary と graph identity を分離する。cross-document edge は入力に明示されない限り作らない。

### 最終討論結果

肯定派は「複数 SBOM を受ける価値」を立証した。反対派は「暗黙 merge が安全」という主張を退けた。よって **capability は肯定、unconditional behavior は否定** とする。

## 検証環境

| 対象 | Version / revision | 用途 |
|---|---|---|
| `ol` | 2026-08-19 working tree の既存 Debug build | 現行入力制約と merged output の読取確認 |
| CycloneDX CLI | `0.33.1+b3cfa4b` | flat/hierarchical merge と validation |
| CycloneDX for .NET | `6.2.0+55877e2` | 言語別 generator の範囲確認 |
| Syft | `1.50.0`, schema `16.1.10` | 言語中立な polyglot directory scan |
| CycloneDX fixture | `tests/Ol.Tests/Fixtures/mixed-npm.cdx.json`、`mixed-nuget.cdx.json` | 二つの root と graph を持つ小さい入力 |
| Polyglot fixture | `sandbox/ecosystems/` | Cargo、CocoaPods、Composer、Go、Maven、npm、NuGet、Python、Ruby、Swift/Yarn 資材 |

一時 tool と生成物は `.references/sbom-merge-evaluation/` に置いた。この directory は `.gitignore` 対象であり、判断文書だけを commit 対象とする。

外部 evidence を必要としない `ol` 比較では `--no-external-evidence` を使った。これは入力 inventory/graph の比較であり、最終的な license coverage 評価ではない。

## 検証シナリオ

### S1: 現行の複数 SBOM 入力

目的は現在の failure boundary を確認することである。

```powershell
dotnet src/Ol/bin/Debug/net10.0/ol.dll scan `
  --input tests/Ol.Tests/Fixtures/mixed-npm.cdx.json `
  --input tests/Ol.Tests/Fixtures/mixed-nuget.cdx.json `
  --no-external-evidence --format json
```

結果は exit code 1 で、標準エラーは次だった。

```text
Unable to scan input: A collection accepts at most one SBOM document.
```

実装上も `ScanInputIngestion.Ingest` が二つ目の `ScanInputKind.Sbom` を明示的に拒否する。既存 test `Scan_WithTwoSbomDocuments_RejectsTheInput` と一致した。

### S2: CycloneDX CLI flat merge

```powershell
cyclonedx-win-x64.exe merge `
  --input-files mixed-npm.cdx.json mixed-nuget.cdx.json `
  --output-file flat.json --output-format json --output-version v1_5

cyclonedx-win-x64.exe validate `
  --input-file flat.json --input-format json --input-version v1_5 --fail-on-errors
```

入力 component は 3 + 2、出力 top-level component は 7 になった。各入力の metadata subject も component として追加されたためである。validation は成功した。

しかし両入力の metadata subject はどちらも `bom-ref: root-app` であり、出力には同じ `bom-ref` が二つ残った。dependency entries も二つの `ref: root-app` を持つ。CycloneDX reference は全 `bom-ref` が一文書内で unique であることを要求するが、CLI validation はこの semantic collision を検出しなかった。

さらに入力順を逆転すると metadata subject が変わった。

| Input order | merged `metadata.component.name` |
|---|---|
| npm, NuGet | `mixed-app` |
| NuGet, npm | `mixed-nuget-app` |

flat merge は「この二つを含む新しい subject」を定義せず、先頭文書の metadata を結果の metadata とする。この結果を現行 `ol` で読むと 8 components、root 3、direct 4、transitive 1 となった。元入力には root が二つしかないが、先頭 metadata root と component 化された二つの subject が並び、aggregate subject の意味は成立していない。

### S3: CycloneDX CLI hierarchical merge

```powershell
cyclonedx-win-x64.exe merge `
  --input-files mixed-npm.cdx.json mixed-nuget.cdx.json `
  --output-file hierarchical.json --output-format json --output-version v1_5 `
  --hierarchical --group example --name polyglot-app --version 1.0.0
```

出力は新しい `polyglot-app` を metadata subject とし、二つの入力 subject を top-level components、その依存 package 3 件と 2 件を各 subject の nested `components` にした。`bom-ref` は入力 subject の name/version で namespace 化され、validation も成功した。標準文書を一つ作る方法としては flat merge より意味が明確である。

現行 `ol` の CycloneDX parser は root-level `components` array だけを読み、各 component 内の nested `components` を再帰走査しない。この出力を scan した結果は次だった。

| Metric | Result |
|---|---:|
| Components | 3 |
| Root | 1 |
| Direct | 2 |
| Transitive | 0 |
| Package children retained | 0 / 5 |

つまり external hierarchical merge は現行 `ol` の回避策にならない。

### S4: 言語別 generator は本当に一言語だけか

CycloneDX for .NET 6.2.0 の CLI は `.sln`、`.slnf`、`.slnx`、`.csproj`、`.fsproj`、`.vbproj`、`.xsproj`、`packages.config` を受け、directory 指定は `packages.config` を再帰探索する。Cargo/npm/Go/Maven manifest を入力として受けない。

`Ol.slnx` を入力に、同じ repository 内に次が存在する状態で実行した。

- `sandbox/ecosystems/cargo/Cargo.lock`
- `sandbox/ecosystems/npm/package-lock.json`
- `sandbox/ecosystems/golang/go.mod`
- `sandbox/ecosystems/maven/pom.xml`

```powershell
dotnet-CycloneDX Ol.slnx `
  --output .references/sbom-merge-evaluation/dotnet-output `
  --filename ol-dotnet.cdx.json --output-format Json `
  --disable-package-restore --disable-hash-computation --no-serial-number
```

tool は solution の 6 projects と 45 packages を解析し、生成物は 42 components、42 件すべて `pkg:nuget/` だった。他 ecosystem の component は 0 だった。

したがって、少なくとも CycloneDX for .NET 6.2.0 について「言語 chain しか検知しない傾向」は正しい。solution 内の複数 .NET projects は aggregate するが、polyglot repository generator ではない。

### S5: 言語中立 generator の polyglot coverage

```powershell
syft.exe scan dir:sandbox/ecosystems `
  --output cyclonedx-json=.references/sbom-merge-evaluation/syft-polyglot.cdx.json
```

Syft 1.50.0 は一回の directory scan で次を生成した。

| PURL type | Count |
|---|---:|
| cargo | 1 |
| cocoapods | 2 |
| composer | 2 |
| gem | 4 |
| golang | 1 |
| maven | 2 |
| npm | 7 |
| nuget | 3 |
| pypi | 3 |
| PURL なし | 12 |
| Total | 37 |

これは言語中立 generator が repository-wide discovery を簡単にする肯定材料である。一方、PURL なしの `Simple Launcher`/Python binary observations、同一 package の duplicate-looking entries、Yarn v2 metadata parse error も観測した。dependency entries は 7 で、component 37 に対して graph は疎だった。

同じ NuGet fixture では、CycloneDX for .NET が external package `Humanizer.Core` 一件と root-to-package edge を生成したのに対し、polyglot Syft scan の NuGet observations は `Humanizer.Core` に加えて build output 由来と見られる root `Ol.Ci.NuGet` を二件含んだ。generator の対象範囲と artifact state により、単純な component count の多さは品質を意味しない。

### S6: guard だけを外した場合の現行 combiner

source inspection で次を確認した。

1. `DependencyInventoryCombiner` は SBOM と package-manager が両方ある場合だけ PURL projection fold を使う。
2. SBOM だけが複数ある場合は `AppendSbomComponents` / `AssignComponents` を通る。
3. 同じ format と同じ PURL の component は一 row に map するが、duplicate branch が merge するのは `DependencyType` だけである。
4. 後続 SBOM の license candidates、repository URL、`SuppliedBy` は `Absorb` されない。
5. CycloneDX と SPDX は format が違うため、同じ PURL でもこの経路では別 row になる。
6. SBOM `LicenseEvidence` は `type`、field、declared/concluded acknowledgement を持つが、document path/hash/id を持たない。
7. 複数 input の `ScanInputDescriptor.SourceReference` は `2 inputs` のような件数表現になり、component/evidence から元文書へ戻れない。

よって `sbomCount` guard の削除だけでは、後続文書の異なる license assertion が黙って消える場合がある。これは複数 SBOM support の最小実装として許容できない。

## 検証結果のまとめ

| Question | Result |
|---|---|
| 言語別 CycloneDX generator は対象言語だけか | CycloneDX for .NET 6.2.0 は .NET/NuGet のみ。検証範囲では Yes |
| 言語中立 generator は polyglot を一括検出できるか | Syft 1.50.0 は 9 PURL types を一括検出。Yes |
| 言語中立 generator の方が常に高品質か | No。noise、duplicate-looking identity、parse error、疎な graph を観測 |
| 外部 flat merge を一つの SBOM として渡せば安全か | No。`bom-ref` collision、metadata の入力順依存、root semantics の欠如 |
| 外部 hierarchical merge は安全か | 標準上の構造は改善するが、現行 `ol` は nested package 5/5 を落とすため No |
| 現行 guard を外すだけで複数 SBOM を扱えるか | No。license evidence と provenance が失われる |
| 複数 SBOM の use case は存在するか | Yes。同一 release の相補的な言語別 fragment |

## 設計案

### 採用候補: explicit fragment set

CLI の詳細名は実装時に確定するが、意味として次を要求する。

```text
ol scan \
  --input backend.cdx.json \
  --input frontend.cdx.json \
  --sbom-set fragments \
  --format json
```

`--sbom-set fragments` は「入力が同じ audit subject の相補的 fragment である」という user assertion である。指定がなければ二つ目の SBOM を現在どおり拒否する。directory discovery で偶然複数 SBOM を見つけても自動的に fragment set にしない。

### 必須データモデル

各 SBOM document に stable input index を割り当て、少なくとも次を canonical report に残す。

- logical path/source reference
- source SHA-256
- format と specification version
- CycloneDX `serialNumber`/`version` または SPDX `documentNamespace`
- generator/tool identity（入力にあれば）
- metadata subject の `bom-ref`、PURL、name、version
- scan 全体で宣言された audit subject

component occurrence、dependency edge、SBOM license evidence は document index を参照する。`bom-ref` は document-local identifier として `(document index, bom-ref)` で扱い、文書間で直接比較しない。

### Component reconciliation

- graph occurrence は文書ごとに全件保持する。
- cross-document edge を推測しない。
- package summary は normalized PURL identity で group 化できるが、format、qualifier、subpath、version の差を diagnostic に残す。
- 同じ identity の license candidates はすべて `LicenseReconciler` に渡す。互いに両立しなければ `conflict` にする。
- 一方の文書にだけある component は union するが `suppliedBy` だけでなく supplying document indexes を残す。
- PURL のない component は name/version 推測で merge しない。
- 同じ PURL が別 install occurrence、target、product を表す可能性があるため、summary の deduplication と occurrence の同一視を分ける。

### Preflight diagnostics

scan 前または report metadata で次を出す。

- document ごとの component、PURL、edge、root、license assertion count
- document 間の exact PURL overlap、left-only、right-only
- 同一 PURL の license disagreement
- 同じ CycloneDX serial の異なる version（revision selection を促す）
- 同一 subject に見えるが generator/scan scope が違う alternate observation
- 複数 metadata subjects と aggregate subject の欠如
- duplicate `bom-ref` は文書内 error、文書間は正常な local-id collision
- component 数に対して graph がない、または極端に疎い文書
- format conversion/merge 済みと推測される nested components や composition completeness

### Package-manager inputs との関係

複数 SBOM + package-manager inputs を一度に解禁すると、どの SBOM がどの resolver graph の projection かが曖昧になる。最初の release は次のどちらかに限定する方が安全である。

1. 複数 SBOM fragments のみ
2. 一つの SBOM + 複数 package-manager inputs（現行）

将来両方を許す場合は SBOM document と resolver input の alignment mapping が必要である。全 SBOM assertion を同じ PURL の全 package-manager occurrence に無条件で投影しない。

## シナリオ別の推奨

| Scenario | 推奨 |
|---|---|
| 一つの repository-wide Syft SBOM で十分 | 一つの SBOM + aligned resolver inputs を維持 |
| .NET と npm が一つの application を構成し、各 native generator を使う | explicit fragment set の候補 |
| 同じ .NET solution を Syft と CycloneDX for .NET で生成 | alternate observations。merge せず coverage comparison |
| 同じ CycloneDX serial の version 1 と 2 | version 2 を選択。union しない |
| independently shipped services | service ごとに scan/report/check。必要なら BOM-Link で aggregate |
| upstream dependency の SBOM を recursive に受領 | flatten せず document link と trust boundary を維持 |
| source repository SBOM と container/image SBOM | audit subject が違うため別 report |
| CycloneDX と SPDX の相補 fragment | normalized input は可能。ただし document provenance 実装後 |

## 実装フェーズ

### Phase 0: 現状維持と文書化

- [ ] one-SBOM-per-collection を維持する。
- [ ] external flat merge は `bom-ref`/root/provenance を検証する必要があると明記する。
- [ ] hierarchical CycloneDX の nested components は現在未対応と明記する。
- [ ] 複数 independently shipped product は別 scan にする。

### Phase 1: document provenance

- [ ] `DependencyInventory`/report に input document table を追加する。
- [ ] occurrence、edge、SBOM evidence から document index を参照できるようにする。
- [ ] single-SBOM report の互換性と allocation/performance を確認する。
- [ ] source path を非機密な logical reference とし、hash を保存する。

### Phase 2: overlap-only dry run

- [ ] policy evaluation へ混ぜず、複数 SBOM の overlap/only/conflict summary を生成する比較 mode を追加する。
- [ ] same serial revision、same subject alternate observation、distinct fragment の diagnostics を試す。
- [ ] CycloneDX/CycloneDX、SPDX/SPDX、CycloneDX/SPDX の組み合わせを test する。

### Phase 3: explicit fragment set

- [ ] opt-in flag/manifest でだけ複数 SBOM inventory を union する。
- [ ] document-local `bom-ref` namespace を保持する。
- [ ] candidate reconciliation を全 SBOM observations に適用する。
- [ ] graph occurrence を保持し、cross-document edge を作らない。
- [ ] directory auto-discovery は opt-in なしに複数 SBOM を採用しない。

### Phase 4: nested/link support の別評価

- [ ] CycloneDX nested components を再帰的に読む価値と component hierarchy の保持方法を評価する。
- [ ] BOM-Link と SPDX external document references を resolve するか、link metadata だけを保存するか決める。
- [ ] remote document fetch は trust、authentication、hash verification、offline reproducibility を別設計にする。

## 受け入れ条件

複数 SBOM support は、少なくとも次を満たすまで release しない。

1. 二つの文書が同じ `bom-ref` を使っても graph edge が混線しない。
2. 同一 PURL に MIT と Apache-2.0 が来た場合、片方を捨てず `conflict` になる。
3. report の各 SBOM license candidate から元 document path/hash/field を特定できる。
4. input order を逆転しても component status、graph count、policy result が変わらない。
5. 同じ serial の old/new revision を union せず diagnostic で拒否または選択できる。
6. PURL のない component を document 間で推測 merge しない。
7. component-only fragment と graph-rich fragment の coverage 差を metadata に表示する。
8. single SBOM、package-manager only、一つの SBOM + package-manager の既存結果を変えない。
9. independently shipped products を誤って一つの pass/fail にまとめない guidance がある。
10. large input で document provenance が component/license enrichment の hot path に不要な allocation を増やさない。

## 非目標

- CycloneDX/SPDX の新しい merged document を `ol` が出力すること。
- repository 内の全 SBOM を無条件で自動発見・union すること。
- name/version だけで PURL のない component を同一視すること。
- stale input や異なる build target を多数決で修正すること。
- generator の誤った identity を `ol` が暗黙に補正すること。
- BOM-Link/external document reference を無条件に network fetch すること。

## 参照資料

- [CycloneDX CLI: merge command](https://github.com/CycloneDX/cyclonedx-cli)
- [CycloneDX for .NET](https://github.com/CycloneDX/cyclonedx-dotnet)
- [CycloneDX v1.7 JSON reference: `bom-ref` uniqueness and BOM revision](https://cyclonedx.org/docs/1.7/json/)
- [CycloneDX BOM-Link](https://cyclonedx.org/capabilities/bomlink/)
- [CycloneDX component compositions](https://cyclonedx.org/use-cases/compositions-components/)
- [SPDX 2.3 external document references](https://spdx.github.io/spdx-spec/v2.3/document-creation-information/)
- [SPDX 2.3 relationships between elements](https://spdx.github.io/spdx-spec/v2.3/relationships-between-SPDX-elements/)
- [Syft package catalogers](https://oss.anchore.com/docs/guides/sbom/catalogers/)
- [Syft supported package ecosystems](https://oss.anchore.com/docs/capabilities/all-packages/)
- [CycloneDX CLI issue #179: merged polyglot SBOM loses dependency graph information](https://github.com/CycloneDX/cyclonedx-cli/issues/179)

