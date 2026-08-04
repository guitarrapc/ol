# 既存 OSS ライセンスチェッカーの実装分析

## 目的と調査範囲

OSSツールの CLI 起点から依存関係の列挙、ライセンス証拠の収集、ライセンス同定、ポリシー判定、結果出力までをソースコードで追跡した。

この文書では、曖昧になりやすい「ライセンスの判断」を次の二段階に分ける。

1. **ライセンス同定**: パッケージの宣言値、URL、ライセンスファイル本文などを SPDX expression やツール固有の分類へ変換する。
2. **ポリシー判定**: 同定したライセンスを allow / deny / review / exception などのプロジェクト方針に照らして合否判定する。

対象言語は各ツール自身の実装言語ではなく、解析対象となる言語または package ecosystem を指す。記述は次のローカル snapshot に基づき、各リポジトリの現在の最新版を保証するものではない。

| Tool | Commit | Origin |
|---|---|---|
| dotnet-delice | `7e011d656add` | <https://github.com/aaronpowell/dotnet-delice> |
| go-licenses | `3e084b0caf71` | <https://github.com/google/go-licenses> |
| license-checker-php | `3a14885b05f1` | <https://github.com/madewithlove/license-checker-php> |
| license-checker-rseidelsohn | `3d28518039b2` | <https://github.com/RSeidelsohn/license-checker-rseidelsohn> |
| licensed | `2db4c2a2743e` | <https://github.com/licensee/licensed> |
| LicenseFinder | `00b04cb91e8e` | <https://github.com/pivotal/LicenseFinder> |
| nuget-license | `3cdd273a4fa0` | <https://github.com/sensslen/nuget-license> |
| ORT | `6c317bd857a4` | <https://github.com/oss-review-toolkit/ort> |
| pip-licenses | `2a8cfbc292a7` | <https://github.com/raimon49/pip-licenses> |

## 全体比較

| Tool | 対象言語・ecosystem | 依存関係の基準 | 主なライセンス取得元 | 本文からの同定 | ポリシー判定 | 特筆すべき点 |
|---|---|---|---|---|---|---|
| dotnet-delice | .NET: C#、F# | MSBuild restore graph と `project.assets.json` | `.nuspec` の expression / file / URL、GitHub API | 類似度によるテンプレート照合 | なし | NuGet metadata から URL、GitHub、本文照合へ段階的に fallback する |
| go-licenses | Go | 実際に import される Go package graph | module 内の LICENSE / README / NOTICE 等 | `licenseclassifier` | license 名またはカテゴリ | 実際に利用する package を基準にし、再配布用 source / license bundle まで生成する |
| license-checker-php | PHP / Composer | Composer の installed dependency tree | `composer license` の宣言値 | なし | 生文字列の allow / deny | transitive violation を導入した direct dependency と `composer.json` の位置まで示す |
| license-checker-rseidelsohn | JavaScript / TypeScript、npm | `node_modules` の Arborist actual tree | `package.json`、README、license files、clarification | SPDX parse と regex heuristic | allow / fail と package filter | semver と file checksum で固定できる clarification により metadata の誤りを監査可能に補正する |
| licensed | JavaScript / TypeScript、Ruby、Rust、Haskell、PHP、Go、JVM、.NET、Python、Swift / Objective-C、Elixir、C / C++ 等 | package manager ごとの installed dependency | package metadata と package 内 legal files | Licensee | allow、review、ignore | 検出結果を Git 管理し、review 後の license text 変更を検知して再 review を要求する |
| LicenseFinder | Go、Ruby、JavaScript / TypeScript、Python、JVM、Swift / Objective-C、Erlang / Elixir、.NET、C / C++、Rust、PHP、Dart 等 | package manager ごとの installed dependency | package spec、installed files、手動 decision | template regex | permit / restrict / approve / ignore | who / why / timestamp / version を持つ decision history と inherited policy を中心にする |
| nuget-license | .NET: C#、F#、Visual Basic 等、NuGet を使う C++ | MSBuild / assets / packages.config | `.nuspec` expression / embedded file / URL / override | SPDX template matcher | SPDX expression の allow | package author が指定した embedded license file を SPDX template で照合し、graph-aware に対象を選ぶ |
| ORT | JVM、JavaScript / TypeScript、Python、Ruby、PHP、Go、Rust、.NET、C / C++、Swift、Dart、Elixir、Erlang、Haskell 等 | package manager analyzer の dependency graph | declared metadata、source/artifact scan、curation | scanner plugin 群 | Kotlin rule、classification、resolution | declared / detected / concluded / effective license を分離した多段 compliance pipeline を持つ |
| pip-licenses | Python | Python environment の installed distributions | PEP 639、classifier、Core Metadata、legal files | なし | 生文字列の allow / fail | PEP 639 `License-Expression` を優先し、installed environment を単純な inventory として扱う |

重要な差は、入力を lockfile / resolved graph とするか、既にインストールされた package directory とするかである。後者は実ファイルを調べやすい反面、環境の再現性と「なぜその package が入ったか」のグラフ精度を package manager に依存する。

## dotnet-delice

### 概要

.NET SDK-style project の NuGet 依存を列挙し、`.nuspec` の license metadata を中心に、URL、GitHub、ライセンスファイル本文へ段階的に fallback してライセンスを報告する F# 製 CLI である。allow / deny を評価する enforcement tool ではなく、ライセンスの発見・同定・一覧化が中心である。

### 対象言語

- `.csproj` の C#
- `.fsproj` の F#
- 上記を含む `.sln` / `.slnx`

実装上 `.vbproj` は入力候補に含まれない。

### 特筆すべき特徴

- `dotnet msbuild /t:GenerateRestoreGraphFile` と `project.assets.json` を併用し、restore 済み NuGet graph を読む。
- 現行の NuGet `license` metadata と、旧式 `licenseUrl` の両方に対応する。
- SPDX license list の `seeAlso` URL、既知 URL、GitHub License API、本文テンプレート照合を fallback chain として持つ。
- SPDX license list を download / cache し、OSI approved、FSF libre、deprecated の付加情報を出す。
- 類似度閾値を指定して、完全一致でない license text も既知テンプレートへ寄せられる。

### ライセンスの取得元

優先順位は概ね次のとおりである。

1. NuGet package の `.nuspec` `license` metadata
   - `type="expression"`: expression をそのまま候補にする。
   - `type="file"`: `.nupkg` 内の指定ファイルを読む。
2. 旧式 `.nuspec` `licenseUrl`
   - SPDX license list の `seeAlso` URL。
   - ツール内の既知 URL mapping。
3. 任意設定の GitHub License API
   - license URL、次に project URL を repository 候補として使う。
4. license URL から download した本文。

package 自体は NuGet global packages / fallback folders から取得する。

### ライセンスの判断基準

**ライセンス同定**

- `type="expression"` は独自の SPDX expression 構文評価をせず、宣言値を採用する。
- URL は SPDX license list の `seeAlso` または既知 URL mapping との一致で SPDX ID にする。
- 本文は組み込みテンプレートとの Sørensen–Dice 類似度が閾値より大きい場合に一致とする。既定閾値は `0.9`。
- 組み込みテンプレートは [`CommonLicenses`](../../../.references/dotnet-delice/src/DotNetDelice.Licensing/CommonLicenses) の 7 件（MIT、Apache、CPL、GPL v2、BSD-3、.NET Foundation、Microsoft license）だけで、SPDX license list 全体ではない。fallback chain の最終段があることと、その段が広く効くことは別である。
- GitHub License API が返した SPDX key も候補にする。
- 同定できない legacy metadata は unknown 相当として残る。

`type="file"` の本文が一致しない場合に metadata の file 名を license 値として返す経路があり、file 名を license identity と誤認し得る点には注意が必要である。

**ポリシー判定**

allow / deny の合否判定は行わない。

### 起動からライセンス判定結果出力までのフロー

1. CLI が path、出力形式、GitHub 利用、SPDX refresh、類似度閾値を読む。
2. path が solution / project / directory のどれかを判定する。
3. MSBuild restore graph を一時ファイルへ生成する。
4. 各 project の `project.assets.json` と NuGet package folders から依存 package と `.nuspec` を読む。
5. `license` metadata、`licenseUrl`、GitHub、download 本文の順に license を解決する。
6. SPDX data から付加属性を結合する。
7. console では license ごとに group 化し、JSON では package ごとの結果を出す。

### 主な実装箇所

- [CLI と入力判定](../../../.references/dotnet-delice/src/DotNetDelice/App.fs)
- [restore graph と NuGet package 読み取り](../../../.references/dotnet-delice/src/DotNetDelice.Licensing/DependencyGraph.fs)
- [証拠の fallback chain](../../../.references/dotnet-delice/src/DotNetDelice.Licensing/LicenseBuilder.fs)
- [GitHub / URL / 本文照合](../../../.references/dotnet-delice/src/DotNetDelice.Licensing/LicenseCache.fs)
- [SPDX data](../../../.references/dotnet-delice/src/DotNetDelice.Licensing/Spdx.fs)
- [console output](../../../.references/dotnet-delice/src/DotNetDelice/ConsoleOutput.fs) / [JSON output](../../../.references/dotnet-delice/src/DotNetDelice/JsonOutput.fs)

## go-licenses

### 概要

Go binary / package が実際に利用する package graph を `go/packages` で解析し、module 内の legal files を内容分類するツールである。report と policy check に加え、再配布に必要な source と license text を集める `save` を持つ。

### 対象言語

- Go modules の Go
- cgo 等で取り込まれる非 Go code は完全には検査せず、警告対象となる。

### 特筆すべき特徴

- module 一覧ではなく、対象 package から実際に import される package graph を基準にする。
- package directory から module root まで親 directory をたどって legal files を探索する。
- ライセンス名だけでなく、再配布上の性質を `Restricted`、`Reciprocal`、`Notice`、`Unencumbered`、`Forbidden`、`Unknown` 等へ分類する。
- `save` は最も厳しい license condition に応じて source、license、notice を配置し、配布可能な compliance bundle を作る。
- license file ごとに複数 package を group 化するため、同じ file の重複処理を減らしている。

### ライセンスの取得元

- package directory から module root までに存在する次の file 名:
  - `LICENSE` / `LICENCE`
  - `COPYING`
  - `README`
  - `NOTICE`
- file 名は case-insensitive で suffix を許容する。
- module metadata は version と source URL の構築にも使う。

### ライセンスの判断基準

**ライセンス同定**

- Google `licenseclassifier` で file content を分類する。
- classifier の `MatchType == "License"` の match を採用し、重複した license 名を除く。
- classifier corpus は `licenseclassifier/v2` module 同梱の `assets.DefaultClassifier()` であり、go-licenses 自身は本文データを持たない。同定精度と再現性はこの module version に従属する。
- 複数候補 file のうち、最初に classifier が認識したものを package の license file とする。
- license 名をツール内の静的 table で配布カテゴリへ対応付ける。未知名は `Unknown` になる。

**ポリシー判定**

- `--allowed_licenses`: 検出 license 名が exact match で allow list に含まれることを要求する。
- `--disallowed_types`: category が指定した禁止集合に含まれないことを要求する。
- 両方未指定時は `Forbidden` と `Unknown` を禁止する。
- license file が見つからない package も違反である。
- 違反を収集して stderr に出し、exit 1 とする。

### 起動からライセンス判定結果出力までのフロー

1. `check` / `report` / `save` subcommand と build tags、test inclusion、ignore prefix を読む。
2. `go/packages.Load` で対象 package と import graph、module 情報、source files を load する。
3. standard library、test binary、ignore 対象を除外し、module ごとの package を作る。
4. package directory から module root まで legal file 候補を探索する。
5. file content を classifier にかけ、認識できた最初の候補と license category を採用する。
6. subcommand ごとに処理する。
   - `check`: allow 名または禁止 category を評価する。
   - `report`: CSV または Go template で一覧を出す。
   - `save`: license condition を集約し、source / license / notice を保存する。
7. 違反または配布不可能な condition があれば非 0 で終了する。

### 主な実装箇所

- [package graph と license file の割り当て](../../../.references/go-licenses/licenses/library.go)
- [legal file 探索](../../../.references/go-licenses/licenses/find.go)
- [本文 classifier](../../../.references/go-licenses/licenses/classifier.go)
- [license category](../../../.references/go-licenses/licenses/types.go)
- [policy check](../../../.references/go-licenses/check.go)
- [report](../../../.references/go-licenses/report.go)
- [compliance bundle](../../../.references/go-licenses/save.go)

## license-checker-php

### 概要

Composer 自身の CLI 出力を使い、installed PHP dependency の宣言 license を allow または deny list と比較する薄い policy checker である。transitive violation を、それを導入した top-level dependency まで説明することに重点がある。

### 対象言語

- PHP / Composer

### 特筆すべき特徴

- Composer の公開 CLI 出力を input contract とし、自前で `composer.lock` や installed metadata を解釈しない。
- 違反 package だけでなく、そこへ至る direct dependency path を提示する。
- SARIF では `composer.json` の direct dependency 宣言行へ violation を関連付ける。
- allow mode と deny mode を明示的に分けた小さな policy model である。

### ライセンスの取得元

- `composer license --format=json --verbose`
- `--no-dev` 指定時は Composer 呼び出しにも反映する。
- dependency path は `composer show --tree --format=json --verbose` から作る。

license file、package source、registry API は調べない。

### ライセンスの判断基準

**ライセンス同定**

- Composer が返す `license` 配列の先頭要素だけを使う。
- SPDX parse、case normalization、alias 補正、本文照合は行わない。
- 複数 license、SPDX `AND` / `OR` の意味論を保持しない。

**ポリシー判定**

- allow mode: 使用 license 集合から設定 license 集合を引いたものが violation。
- deny mode: 使用 license 集合と設定 license 集合の積が violation。
- 比較は生文字列の exact match である。
- violation license を使用する package を dependency tree へ逆引きし、影響する direct dependency ごとに結果を作る。

### 起動からライセンス判定結果出力までのフロー

1. `check` command が `.license-checker.yml`、出力形式、`--no-dev` を読む。
2. `composer license` から installed package と license 配列を JSON で取得する。
3. 各 package の先頭 license だけを `package -> license` map にする。
4. allow / deny 設定と生文字列集合を比較して violating license を得る。
5. `composer show --tree` を flatten し、各 violation package へ至る direct dependency path を求める。
6. text、JSON、SARIF のいずれかで全 violation を出す。
7. violation があれば failure exit code を返す。

### 主な実装箇所

- [Composer license 取得](../../../.references/license-checker-php/src/Composer/UsedLicensesRetriever.php)
- [先頭 license の抽出](../../../.references/license-checker-php/src/Composer/UsedLicensesParser.php)
- [dependency tree 取得](../../../.references/license-checker-php/src/Composer/DependencyTreeRetriever.php) / [flatten](../../../.references/license-checker-php/src/Composer/DependencyTree.php)
- [allow / deny 集合演算](../../../.references/license-checker-php/src/Configuration/LicenseConfiguration.php)
- [check orchestration](../../../.references/license-checker-php/src/Commands/CheckLicenses.php)
- [SARIF](../../../.references/license-checker-php/src/Output/SarifOutputFormatter.php)

## license-checker-rseidelsohn

### 概要

Node.js の installed `node_modules` tree を Arborist で読み、`package.json` の宣言と package 内の README / license files を組み合わせて license を推定・filter・出力する CLI である。この clone の README では package が deprecated とされているが、clarification、checksum、豊富な出力という設計上の比較価値がある。

### 対象言語

- JavaScript / TypeScript を主とする npm package
- npm が管理する `node_modules`

### 特筆すべき特徴

- npm Arborist の actual tree を使い、production / development / direct / depth / peer / optional を filter できる。
- package と semver range を指定した **clarification** で、誤った metadata の license、file、text range を手動補正できる。
- clarification 対象 file に SHA-256 checksum を要求でき、upstream の内容変化を検知できる。
- 使用されなかった clarification を error にでき、古い例外設定を発見できる。
- license file path / text、NOTICE、copyright を保持し、多数の出力形式へ展開する。

### ライセンスの取得元

概ね次の優先順位である。

1. version range に一致した clarification。
2. `package.json` の `license` または旧 `licenses`。
3. metadata がない場合の README。
4. unknown の場合に package root の license files。

license file 探索は `LICENSE`、`LICENCE`、`MIT-LICENSE`、`COPYING`、README 等の pattern を定義し、pattern ごとに最初の file を採用する。

### ライセンスの判断基準

**ライセンス同定**

- まず `spdx-expression-parse` が受理する expression を採用する。
- parse できない本文や文字列は regex heuristic で MIT、BSD、Apache、GPL、LGPL、ISC、CC0、Public Domain 等を推定する。
- heuristic による値には guessed marker `*` を付ける。
- clarification は package + semver で metadata より優先し、file checksum が設定値と異なれば失敗する。
- `spdx-correct` と `spdx-satisfies` を使う normalization / include-exclude 経路もある。

**ポリシー判定**

- `failOn`: 現在の license 文字列全体との exact match。
- `onlyAllow`: 現在の license 文字列に allowed string が含まれるかで判定する。
- package / license include-exclude filter を追加適用できる。

`onlyAllow` の substring 判定は SPDX expression の Boolean 意味論ではなく、短い ID が別の文字列に含まれる場合も通し得る。検出の豊富さと policy の厳密さは別に評価すべきである。

### 起動からライセンス判定結果出力までのフロー

1. CLI preflight が option の矛盾、出力 path、clarification 設定を検証する。
2. Arborist `loadActual()` で installed tree を読み、package metadata と disk 上の `package.json` を結合する。
3. dependency tree を走査し、development / production / direct / depth 等の package filter を適用する。
4. 各 package で clarification、metadata、README、license files の順に証拠を集める。
5. SPDX parser または heuristic で license title を同定し、legal file text と copyright を保持する。
6. clarification checksum と unused clarification を検証する。
7. license policy と include-exclude filter を評価する。
8. tree、summary、JSON、CSV、Markdown、plain 等へ整形し、stdout または file に出す。
9. policy 違反や clarification error があれば非 0 で終了する。

### 主な実装箇所

- [CLI](../../../.references/license-checker-rseidelsohn/lib/cli.js) / [全体 orchestration](../../../.references/license-checker-rseidelsohn/lib/index.js)
- [Arborist tree 読み取り](../../../.references/license-checker-rseidelsohn/lib/dependencies/read-installed-packages.js)
- [証拠の優先順位](../../../.references/license-checker-rseidelsohn/lib/licenses/collect-license-results.js)
- [license file 探索](../../../.references/license-checker-rseidelsohn/lib/licenses/find-license-files.js)
- [SPDX / heuristic 同定](../../../.references/license-checker-rseidelsohn/lib/licenses/detect-license-title.js)
- [clarification と checksum](../../../.references/license-checker-rseidelsohn/lib/licenses/clarifications.js)
- [policy](../../../.references/license-checker-rseidelsohn/lib/policies/license-policy.js)
- [renderer](../../../.references/license-checker-rseidelsohn/lib/output/renderers.js)

## licensed

### 概要

GitHub が開発する Ruby 製 dependency license checker で、package manager ごとの source enumerator と Licensee の license detection を組み合わせる。検出結果を repository 内の YAML file に cache し、review とともに Git 管理する workflow が中心である。

### 対象言語

登録 source から見ると、主に次を対象とする。

- JavaScript / TypeScript: npm、Yarn、pnpm、Bower
- Ruby: Bundler
- Rust: Cargo
- Haskell: Cabal、Stack
- PHP: Composer
- Go: Go modules、dep
- JVM: Gradle
- .NET: NuGet
- Python: pip、Pipenv
- Apple ecosystem: CocoaPods、SwiftPM
- Elixir: Mix
- C / C++ 等: manifest、Git submodule

source ごとに dependency の列挙精度や必要な installed artifact は異なる。

### 特筆すべき特徴

- `.licenses/<application>/<source>/<dependency>.dep.yml` を review 可能な repository artifact として保存する。
- status 実行時に毎回 license detection を繰り返さず、cache と現在の dependency identity を比較する。
- 人手で修正された license は、正規化済み license text が変わらない限り再 cache 時にも保持する。
- review 済み dependency の license text が変わると `review_changed_license` として再 review を要求する。
- version-aware review、ignore、stale cache policy、複数 application、NOTICE 生成を持つ。

### ライセンスの取得元

- 各 package manager source が返す package name、version、installed path、metadata。
- Licensee が package directory から見つける license / README 等の files。
- AUTHORS、NOTICE、LEGAL 等の legal content。
- 一部 source は package metadata の license と repository 情報を追加する。
- NuGet source など一部では、local package で未認識の場合に legacy license URL を取得する。

### ライセンスの判断基準

**ライセンス同定**

- dependency を `Licensee::Projects::FSProject` として扱い、Licensee の matcher と confidence threshold で license を同定する。
- cache record に license key、全 license text、notice 等を保存する。
- 複数本文が単一 license にまとまらない場合は `other`、検出不能なら `none` になり得る。
- user が cache の `license` を review / 修正できるが、元の text は監査材料として残る。

**ポリシー判定**

`status` は次を failure とする。

- 現在の dependency に対応する cache record がない。
- version が一致しない。
- license text がない。
- review 後に license text が変化した。
- license が allowed でも reviewed でもない。

`license: other` でも、複数 license text の各検出結果がすべて allowed なら通る。`reviewed` は個別例外、`ignored` は検査対象外を表し、version 条件を付けられる。

### 起動からライセンス判定結果出力までのフロー

1. configuration から application と source enumerator を作る。
2. source ごとに package manager command または lock / installed metadata から dependency を列挙する。
3. `cache` command が各 dependency directory を Licensee で調べ、license / legal contents と metadata を record 化する。
4. 既存 record があれば text fingerprint 相当を比較し、安全に維持できる human correction と review 状態を引き継ぐ。
5. YAML cache を書き、消えた dependency の stale record を除去する。
6. `status` command が現在の dependency name / version と cache を比較する。
7. missing、version mismatch、changed review、unreviewed / disallowed を reporter に集める。
8. YAML / JSON / status report を出し、problem があれば非 0 で終了する。
9. `notices` command は cache の legal contents から attribution artifact を生成する。

### 主な実装箇所

- [source registry](../../../.references/licensed/lib/licensed/sources.rb) / [source boundary](../../../.references/licensed/lib/licensed/sources/source.rb)
- [dependency と Licensee 接続](../../../.references/licensed/lib/licensed/dependency.rb)
- [cache command](../../../.references/licensed/lib/licensed/commands/cache.rb)
- [status command](../../../.references/licensed/lib/licensed/commands/status.rb)
- [NOTICE command](../../../.references/licensed/lib/licensed/commands/notices.rb)
- [configuration](../../../.references/licensed/docs/configuration.md)
- [review workflow](../../../.references/licensed/docs/configuration/reviewing_dependencies.md)

## LicenseFinder

### 概要

多くの package manager から installed dependency を列挙し、package metadata、package 内 legal files、永続化された人手 decision を統合して policy action item を出す Ruby 製ツールである。自動同定だけで完結させず、「誰が、なぜ、いつ、どの package / version を承認または補正したか」を workflow の中心に置く。

### 対象言語

登録 package manager から見ると、主に次を対象とする。

- Go、Ruby / Bundler
- JavaScript / TypeScript: npm、pnpm、Yarn、Bower
- Python: pip、Pipenv、Conda
- JVM: Maven、Gradle、sbt
- Apple ecosystem: CocoaPods、Carthage、SwiftPM
- Erlang / Elixir: Rebar、Erlang.mk、Mix
- .NET: NuGet、dotnet
- C / C++: Conan
- Rust: Cargo
- PHP: Composer
- Dart / Flutter: Pub

README 上、一部 integration には experimental または license discovery の制約があり、全 ecosystem が同じ情報量を提供するわけではない。

### 特筆すべき特徴

- package manager adapter の breadth が広い。
- automatic evidence より manual decision を優先する明確な reconciliation order を持つ。
- decision file に approver、reason、timestamp、version 条件を記録する。
- permit / restrict、package approval、manual license、ignore、group、homepage 修正を別 action として扱う。
- repository 固有 decision を inherited decision と重ねられる。
- current result と過去 report の diff を出せる。

### ライセンスの取得元

- package manager が返す package spec / metadata の license。
- installed package directory 内の `LICENSE`、`LICENCE`、`COPYING`、README 等。
- archive / jar 内の legal files。
- user の decision file にある manual license assignment。
- custom license definition と user-defined license text。

### ライセンスの判断基準

**ライセンス同定**

`Licensing.activations` の優先順位は次のとおりである。

1. manual decision。
2. package spec の license name。
3. package directory の matched license files。
4. unknown。

license file matcher は template を正規化した regex に変換し、optional / variable 部分を含む定義と照合する。名称は SPDX expression を表せる model に変換される。

**ポリシー判定**

- `OR`: いずれかの sub-license が permitted なら permitted。
- `AND`: すべての sub-license が permitted な場合だけ permitted。
- package に複数 activation がある場合、すべて restricted のとき restricted になる。
- manual package approval があれば、その package / version を承認する。
- restrict decision、未承認 license、unknown を action item として全件収集する。
- 違反があれば exit 1。

### 起動からライセンス判定結果出力までのフロー

1. CLI が project path、enabled package managers、prepare、decision file、report format を読む。
2. scanner が project 内で active な package manager を検出する。
3. 必要に応じて prepare command を実行し、installed dependency と dependency relation を列挙する。
4. 各 package から spec license と installed legal files を収集し、template matcher で同定する。
5. local / inherited decision を時系列に適用し、manual license、approval、permit / restrict、ignore 等を反映する。
6. license activation と package approval を評価し、action item を作る。
7. text、CSV、HTML、Markdown、JSON、JUnit、diff 等の report を出す。
8. unresolved action item があれば非 0 で終了する。

### 主な実装箇所

- [package manager registry と scan](../../../.references/LicenseFinder/lib/license_finder/scanner.rb)
- [package data](../../../.references/LicenseFinder/lib/license_finder/package.rb)
- [evidence 優先順位](../../../.references/LicenseFinder/lib/license_finder/package_utils/licensing.rb)
- [license file 探索](../../../.references/LicenseFinder/lib/license_finder/package_utils/license_files.rb)
- [file content matching](../../../.references/LicenseFinder/lib/license_finder/package_utils/possible_license_file.rb)
- [license / expression model](../../../.references/LicenseFinder/lib/license_finder/license.rb)
- [decision storage](../../../.references/LicenseFinder/lib/license_finder/decisions.rb) / [decision 適用](../../../.references/LicenseFinder/lib/license_finder/decision_applier.rb)
- [reporters](../../../.references/LicenseFinder/lib/license_finder/reports)

## nuget-license

### 概要

.NET / C++ project が参照する NuGet package を MSBuild と NuGet API で列挙し、`.nuspec` の expression、embedded license file、legacy URL、user override を allow list と比較する .NET CLI である。

### 対象言語

- .NET Framework / .NET Core / .NET Standard project
- C#、F#、Visual Basic 等、MSBuild / NuGet を使う .NET project
- NuGet を使う native C++ project
- solution、project、`project.assets.json`、Windows の `packages.config`

### 特筆すべき特徴

- direct dependency のみと transitive dependency を選べる。
- target framework を選択できる。
- `Publish=false` の root からだけ到達する package を除外する graph-aware filtering を持つ。
- package content hash を key にした metadata cache を使う。
- `.nupkg` 内の embedded license file を読み、SPDX template matching guideline を意識した matcher で同定する。
- package ignore pattern、URL mapping、file mapping、package metadata override を持つ。
- license files の download / 保存を report と別に支援する。

### ライセンスの取得元

1. user supplied package metadata override。
2. NuGet global packages / fallback folders 内の `.nuspec` と `.nupkg`。
3. configured NuGet repository の package metadata。
4. `.nuspec` の `license`:
   - SPDX expression。
   - embedded file path とその content。
5. 旧式 `licenseUrl`。
6. user supplied URL-to-license / file-to-license mapping。

### ライセンスの判断基準

**ライセンス同定**

- expression は Tethys SPDX parser で AST にする。
- embedded file は SPDX 標準 template から生成した `FastLicenseMatcher` で正規化・token 比較し、一致した全 license を `OR` で結ぶ。
- template 本体は自前で持たず、`Sensslen.SPDX.Licenses.Net` 3.28.0 という**版を固定した外部 SPDX データ package** から `StandardLicenseTemplate`、無い場合は `LicenseText` を読む。identifier list ではなく本文つきの SPDX データが matcher の前提である。
- user file mapping を追加 matcher として利用できる。
- legacy URL は user mapping がある場合だけ license ID へ変換する。download した URL 本文は保存対象であり、自動分類には使わない。

**ポリシー判定**

- SPDX atom は allowed license との exact string equality。
- `AND` は両辺、`OR` はいずれか、`WITH` は expression node の評価規則に従う。
- ignored package は validation 対象外になる。
- allow list が空の場合、parse 可能な expression は許容される。
- embedded file が未知でも allow list が空なら validation error にしない。allow list がある場合は unknown / not allowed になる。
- validation error を持つ package 数を process exit code として返す。

parser callback が任意 identifier / reference を受理する構成なので、ol の strict SPDX identifier validation より寛容である。

### 起動からライセンス判定結果出力までのフロー

1. CLI が solution / project / assets input、TFM、transitive、allow、ignore、mapping、override、出力形式を読む。
2. project collector が MSBuild project を集める。
3. referenced package reader が assets / packages.config と graph を読み、対象 package を決める。
4. package information reader が local package folders、cache、NuGet repository の順に metadata を取得する。
5. `.nuspec` expression / file / URL / override を normalized validation input にする。
6. embedded file は template matcher、expression は SPDX parser、URL は mapping で license を得る。
7. allow list と expression を比較し、package ごとの validation errors を作る。
8. deterministic に sort し、table、Markdown、JSON、CSV を出す。
9. 必要なら license files を保存し、error package 数を exit code にする。

### 主な実装箇所

- [CLI](../../../.references/nuget-license/src/NuGetLicense/Program.cs)
- [orchestration](../../../.references/nuget-license/src/NuGetLicense/LicenseValidationOrchestrator.cs)
- [dependency graph](../../../.references/nuget-license/src/NuGetUtility/ReferencedPackagesReader/ReferencedPackageReader.cs)
- [package metadata](../../../.references/nuget-license/src/NuGetUtility/PackageInformationReader/PackageInformationReader.cs)
- [global package / embedded file](../../../.references/nuget-license/src/NuGetUtility/Wrapper/NuGetWrapper/Protocol/GlobalPackagesFolderUtility.cs)
- [validation](../../../.references/nuget-license/src/NuGetLicense/LicenseValidator/LicenseValidator.cs)
- [SPDX template matcher](../../../.references/nuget-license/src/FileLicenseMatcher/SPDX/FastLicenseMatcher.cs)
- [output](../../../.references/nuget-license/src/NuGetLicense/Output)

## ORT

### 概要

OSS Review Toolkit は単一の license checker ではなく、dependency analysis、source / artifact scanning、curation、policy evaluation、report generation を分離した compliance platform である。package metadata の declared license と source file の detected license を別の fact として保持し、concluded / effective license を明示的に導出する。

### 対象言語

plugin 群から見ると、次を含む広い ecosystem を対象とする。

- JVM: Maven、Gradle、sbt
- JavaScript / TypeScript: npm、Yarn、pnpm、Bower
- Python、Ruby / Bundler、PHP / Composer
- Go、Rust / Cargo
- .NET / NuGet
- C / C++: Conan、Carthage、CocoaPods、Bazel、unmanaged
- Swift、Dart / Flutter、Elixir、Erlang、Haskell、Gleam
- generic SPDX document、GitHub Actions 等

### 特筆すべき特徴

- Analyzer、Scanner、Evaluator、Reporter を明確に分離し、中間 result を再利用する。
- source provenance / artifact provenance と scanner result の cache / storage を持つ。
- detected finding に file path、line、copyright、snippet 等の位置情報を保持する。
- declared、detected、main、concluded、effective license を区別する。
- path exclusion、license finding curation、package curation、copyright garbage、resolution を evidence を消さずに適用する。
- SPDX `OR` choice を project / package 単位で明示し、effective license に反映する。
- evaluator rule、license classification、reporter、scanner、package manager を plugin として拡張できる。

### ライセンスの取得元

- Analyzer:
  - package manager の manifest / lock / installed resolution。
  - package metadata の declared license。
  - source / artifact provenance。
- Scanner:
  - downloaded source tree。
  - binary / source artifact。
  - scanner plugin の license、copyright、snippet findings。
- Curations / resolutions:
  - package curation。
  - concluded license。
  - license finding curation。
  - path exclusion、issue resolution。
- Archive / license text provider:
  - actual license files と license text。

### ライセンスの判断基準

**ライセンス同定**

- `DeclaredLicenseProcessor` は custom mapping、built-in mapping、known URL / prefix / suffix 除去、quote / tag 除去を段階適用して SPDX expression を parse する。
- 複数 declared license は既定で `AND` としてまとめる。
- Scanner は configured backend により file content を license finding にする。Askalono、Licensee、ScanCode、SCANOSS 等の plugin がある。
- `DefaultLicenseInfoProvider` と `LicenseInfoResolver` が concluded、declared、detected を provenance と location 付きで統合する。
- license view と explicit license choice を適用して effective license を導出する。

**ポリシー判定**

- user の Kotlin rule script が package、scope、license、vulnerability、issue 等を評価する。
- license classification と license choice を rule input に使える。
- rule violation は severity、message、how-to-fix を持つ。
- configured failure severity 以上があれば evaluator command は failure status を返す。
- resolution は violation や finding を削除せず、解決済みという別情報として扱う。

### 起動からライセンス判定結果出力までのフロー

ORT は単一 command で全工程を隠さず、概ね次の pipeline を中間 result とともに実行する。

1. `analyzer` が repository を走査し、active package manager plugin を選ぶ。
2. manifest / lock / package manager command から project、package、scope、dependency graph、declared license、provenance を得る。
3. package curation と declared license mapping を適用し、analyzer result を保存する。
4. `scanner` が source / artifact provenance を resolve / download し、scan result storage を確認する。
5. cache miss の provenance を scanner plugin で走査し、license / copyright / snippet findings を保存する。
6. license info resolver が concluded / declared / detected、path exclusion、finding curation、resolution、license choice を統合する。
7. `evaluator` が rule script と classification を使い、全 rule violations を作る。
8. `reporter` が同じ resolved facts と policy result から SPDX、CycloneDX、NOTICE、HTML、Web app、custom template 等を生成する。
9. command error と policy violation を区別した status で終了する。

### 主な実装箇所

- [CLI entry](../../../.references/ort/cli/src/main/kotlin/OrtMain.kt)
- [declared license normalization](../../../.references/ort/utils/ort/src/main/kotlin/DeclaredLicenseProcessor.kt)
- [license source selection](../../../.references/ort/model/src/main/kotlin/licenses/DefaultLicenseInfoProvider.kt)
- [finding curation / exclusion / resolution](../../../.references/ort/model/src/main/kotlin/licenses/LicenseInfoResolver.kt)
- [effective license](../../../.references/ort/model/src/main/kotlin/licenses/ResolvedLicenseInfo.kt)
- [evaluator](../../../.references/ort/evaluator/src/main/kotlin/Evaluator.kt)
- [reporter boundary](../../../.references/ort/reporter/src/main/kotlin/Reporter.kt)
- [package manager plugins](../../../.references/ort/plugins/package-managers)
- [scanner plugins](../../../.references/ort/plugins/scanners)
- [reporter plugins](../../../.references/ort/plugins/reporters)

## pip-licenses

### 概要

現在の Python environment に installed された distribution の Core Metadata を列挙し、license、author、URL 等を多形式で報告する CLI である。PEP 639 `License-Expression` を優先でき、簡易 allow / fail policy と package 内 legal file text の表示を持つ。

### 対象言語

- Python / installed distributions
- current interpreter の environment、または指定 Python path

lockfile の dependency graph や direct / transitive path は解析しない。

### 特筆すべき特徴

- PEP 639 `License-Expression` を従来 metadata より優先する。
- Trove classifier と Core Metadata `License` の選択 mode を持つ。
- installed distribution の file list から license、NOTICE、AUTHORS を表示できる。
- plain、Markdown、reStructuredText、Confluence、HTML、JSON、CSV、LicenseFinder JSON 等の出力がある。
- `--from=mixed` 等は license metadata の選択 mode であり、inventory 自体は常に実行 environment である。この境界が単純で扱いやすい。

### ライセンスの取得元

1. `License-Expression` metadata。
2. `Classifier: License :: ...` の Trove classifier。
3. Core Metadata の `License`。
4. 表示用の package file:
   - `LICENSE` / `LICENCE` variants。
   - `COPYING`。
   - `NOTICE`。
   - `AUTHORS`。

file content は表示対象であり、policy 用 license identity の同定には使わない。

### ライセンスの判断基準

**ライセンス同定**

- `License-Expression` があれば最優先する。
- classifier mode では一般的な `OSI Approved` 自体を除き、末尾の具体的 license 名を集める。
- `mixed` は classifier を優先し、なければ metadata `License` を使う。
- `meta`、`classifier`、`all` で取得元を切り替える。
- SPDX parse、alias 補正、本文照合は行わない。

**ポリシー判定**

- `--fail-on`: semicolon 区切りの禁止値のいずれかと一致したら failure。
- `--allow-only`: package の license 値のいずれかが許可値に一致しなければ failure。
- 既定は case-insensitive exact comparison。
- option で partial / substring match にできる。
- 最初の violation で message を出し exit 1 になる。

### 起動からライセンス判定結果出力までのフロー

1. CLI が Python path、include / ignore package、license source mode、fields、output format、policy を読む。
2. `importlib.metadata.distributions()` で installed distributions を列挙する。
3. self / system package と user filter を適用する。
4. 各 distribution から PEP 639、classifier、Core Metadata の優先規則で license 表示値を作る。
5. 必要に応じて distribution file list から legal files を探して text / path を追加する。
6. fail-on または allow-only を生文字列で評価する。
7. requested fields を sort し、plain / Markdown / JSON / CSV 等へ出す。
8. policy 違反があれば exit 1。

### 主な実装箇所

- [CLI、metadata 取得、policy、全 renderer](../../../.references/pip-licenses/piplicenses.py)
- [利用方法と license source mode](../../../.references/pip-licenses/README.md)

## ol の設計目標から見た横断評価

ここまでは各ツールを実装として記述した。以下は同じ観察を、[ol のアーキテクチャ](../Architecture.md)が利用者へ約束している体験の側から読み直す。ol にとっての問いは「参照ツールが何を持っているか」ではなく、**ol が既に約束している体験のうち、どれがまだ果たされておらず、それを果たすと何を支払うことになるか**である。

評価軸は DESIGN の Design Goals を利用者の言葉へ置き換えたものとする。

| 軸 | 利用者にとっての意味 | 対応する設計目標 |
|---|---|---|
| A. 数え落とさない | 推移的依存まで含めて、実際に配布物へ入る OSS が漏れない | 1 |
| B. 判定の理由が残る | なぜその結論なのかを後から辿り、再 review できる | 2、3 |
| C. 同じ入力なら同じ結果 | 時刻・実行環境・機械が変わっても判定が揺れない | 4 |
| D. 止まったときに前へ進める | fail-closed で止まった後、監査可能な形で解決できる | 5 |
| E. 検査の次へ届く | 合否の先にある再配布義務の履行を短縮できる | なし（[非目標](../Architecture.md#non-goals)） |
| F. 小さく速いままでいる | 単一 native バイナリとして配布・実行できる | 9 |

### A. 数え落とさない

inventory の基準は二分される。go-licenses は実際に import される package graph、nuget-license は restore graph と `Publish=false` 到達性、ORT は analyzer result を先に確定する。対して installed directory を列挙する pip-licenses、LicenseFinder、licensed、license-checker-rseidelsohn は実ファイルへ到達しやすい代わりに、root / direct / transitive、target framework、scope の説明力を package manager の状態へ委ねる。

**依存 inventory と license evidence は別問題である。** 前者を後者の都合で決めると、「なぜこの package が入っているか」を答えられなくなる。license-checker-php が violating package から `composer.json` の direct dependency 行まで逆引きするのは、この説明力を出力側で回復する試みである。

### B. 判定の理由が残る

同定を一つの値へ畳む実装と、証拠を並べて保持する実装に分かれる。

- 畳む側: license-checker-php は Composer が返す `license` 配列の**先頭要素だけ**を使う。dotnet-delice は `type="file"` の本文が一致しないとき metadata の file 名を license 値として返す経路を持つ。いずれも「値はあるが根拠がない」結果を作る。
- 並べる側: ORT は declared / detected / concluded / effective を別 fact として保ち、finding に file path、line、copyright、provenance を残す。licensed は license text 全文を cache record へ保存し、人手修正後も元の text を監査材料として残す。license-checker-rseidelsohn は license file path、text range、SHA-256 を保持する。

**宣言値だけでは不足し、本文だけでも不足する。** 宣言値は高速で再現性が高いが、欠落、誤記、legacy URL、複数 license の崩壊がある。本文検出は欠落を埋めるが、file 選択、template 差分、heuristic、subdirectory license による誤検出がある。強い実装は declared と detected を上書き関係にせず、provenance 付きの別 evidence として保存する。

### C. 同じ入力なら同じ結果

この軸が参照ツール群で最も差が出る。**本文同定を導入した瞬間、判定は「入力」だけでなく「同定データの版」と「ローカルに実体化された package」に依存し始める。** 参照実装はこの二つの依存を必ず支払っている。

#### 同定データの版

| ツール | 本文同定の corpus | 規模 | 版の従属先 |
|---|---|---|---|
| license-checker-php、pip-licenses | なし（宣言値のみ） | — | なし |
| dotnet-delice | [`CommonLicenses`](../../../.references/dotnet-delice/src/DotNetDelice.Licensing/CommonLicenses) | **7 ライセンス / 約 69KB** | tool 自身 |
| LicenseFinder | [`license/templates`](../../../.references/LicenseFinder/lib/license_finder/license/templates) | 29 template / 約 336KB | tool 自身 |
| licensed | Licensee gem `>= 9.18` | gem 同梱 | gem version |
| go-licenses | `licenseclassifier/v2` の `assets.DefaultClassifier()` | module 同梱 | module version |
| nuget-license | `Sensslen.SPDX.Licenses.Net` 3.28.0 | **SPDX license list 全量（本文 + template）** | SPDX list version |
| ORT | 別プロセスの [scanner plugin](../../../.references/ort/plugins/scanners)（askalono、ScanCode、SCANOSS、FossID、Licensee） | 外部 | scanner + dataset version |

読み取るべきことは三つある。

1. **「本文照合の fallback がある」という記述だけでは同定能力を評価できない。** dotnet-delice の fallback chain は最終段まで到達しても 7 ライセンスしか判別しない。カバレッジは chain の段数ではなく corpus の規模で決まる。
2. **SPDX template matching を選ぶと、必要な SPDX データが identifier list から本文つきデータへ変わる。** nuget-license はこれを版を固定した外部データ package として取り込み、matcher の再現性を SPDX license list version に結び付けている。identifier だけを持つ実装から移行する場合、これはデータ契約の変更であって matcher の追加ではない。
3. **corpus を外部化するほど、結果の再現性は自分の版管理から離れる。** ORT がこの極であり、だからこそ scan result に scanner 名と version を記録し、provenance ごと storage に保存する。corpus を外に出すなら、その版を結果へ刻む義務が同時に発生する。

#### package のローカル実体化

本文を読む実装は、例外なく package が実行機上に展開済みであることを要求する。

- go-licenses: package directory から module root まで親を遡る（[`FindCandidates(dir, rootDir)`](../../../.references/go-licenses/licenses/find.go)）。module cache が前提。
- nuget-license: NuGet global packages folder と fallback folders から `.nupkg` を取る（[`GlobalPackagesFolderUtility`](../../../.references/nuget-license/src/NuGetUtility/Wrapper/NuGetWrapper/Protocol/GlobalPackagesFolderUtility.cs)）。restore 済みが前提。
- licensed、LicenseFinder、license-checker-rseidelsohn、pip-licenses: installed directory が前提。
- ORT のみ例外で、provenance を固定した source / artifact を**自分で download** し、その provenance を scan result の key にする。

したがって lockfile / resolved graph を入力とするツールが本文同定を足すと、**同じ入力ファイルから機械ごとに異なる evidence が出る**状態へ移る。CI で restore していない、SBOM だけを別機械から受け取った、といった経路で本文が取れないのは異常ではなく通常の分岐である。ORT の解法が示すのは、この分岐を消す方法は「provenance を固定して自分で取得する」か、「取得できないことを結果の一級の状態として表現する」かのどちらかしかない、ということである。

#### policy 側の再現性

**SPDX expression の意味論と文字列検索は代替関係にない。** license-checker-php と pip-licenses の exact raw string、license-checker-rseidelsohn の substring allow は実装が簡単だが、alias、casing、`AND` / `OR` / `WITH` を正しく扱えない。policy は同定済みの構文木か、同等の厳密な evaluator に対して行う必要がある。

### D. 止まったときに前へ進める

判定が厳しいほど、利用者は「正しいが通せない」状態に置かれる。参照実装はここに三種類の異なる出口を用意しており、混同してはならない。

| 出口 | 意味 | 参照実装 |
|---|---|---|
| 事実の訂正 | upstream metadata が誤り。正しい license はこれである | license-checker-rseidelsohn の clarification、LicenseFinder の manual license、ORT の package curation |
| 結論の確定 | 証拠は割れているが、人間が解釈を確定した | licensed の review、ORT の concluded license |
| 方針の例外 | 事実は正しい。この package のこの版だけ方針上許容する | LicenseFinder の package approval、licensed の `reviewed`、ORT の resolution |

**人手判断には変更検知が必要である。** licensed の `review_changed_license`、license-checker-rseidelsohn の file SHA-256 checksum、LicenseFinder の versioned decision は、例外を単なる package name allow にしない。対象 version、reason、reviewer、元 evidence の fingerprint を固定し、upstream が変われば再 review させる。license-checker-rseidelsohn が unused clarification を error にできるのも同じ動機で、古い例外が黙って残ることを防いでいる。

一方、この安全性には**製品契約上の代償**がある。三つの出口と変更検知はいずれも新しい観測可能な状態を増やす。licensed の `review_changed_license`、rseidelsohn の unused clarification error、ORT の resolution はすべて、利用者が理解し CI が分岐する対象になる。状態を増やす判断は matcher やファイル探索の実装より先に決めるべき事項である。

またこの軸では、**証拠の欠落と方針違反を同じ失敗として扱わない**ことが重要になる。go-licenses は license file が見つからない package を違反として扱う。nuget-license は逆に、allow list が空なら embedded file が未知でも validation error にしない。前者は収集失敗を policy 違反へ、後者は policy 不在を同定成功へ寄せており、どちらも exit code から原因を読めなくする。

### E. 検査の次へ届く

この軸だけは ol の約束ではない。再配布成果物の生成は [Architecture の非目標](../Architecture.md#non-goals)であり、以下は参照実装の観察と、その観察が示す境界の記録である。

**最終成果物は合否だけではない。** go-licenses の `save` は最も厳しい license condition に応じて source / license / notice を配置し、licensed の `notices` は reviewed cache の legal contents から attribution を作り、nuget-license は license file を保存する。report に license ID があるだけでは、製品へ同梱すべき原文や attribution を作れない。

ここで ORT が示す設計上の分かれ目が一つある。**`OR` のライセンス選択は、policy 評価から導出される結果ではなく、利用者が与える入力である。** ORT は license choice を project / package 単位の明示的な設定として受け取り、それを effective license へ反映する。allow-list を満たした branch を「選ばれた license」として成果物に書けば、それはツールが利用者に代わって選択を宣言したことになる。NOTICE 生成を持つ実装がすべて legal text の provenance を保持しているのも同じ理由で、生成物は観測した事実の再構成であって、ツールの推論結果ではない。

### F. 小さく速いままでいる

参照ツールの多くは、この制約を持たない。LicenseFinder と licensed は package manager の CLI を子プロセスとして呼び、ORT は外部 scanner を別プロセスで動かし、dotnet-delice と nuget-license は MSBuild を必要とする。そのため「参照実装にあるから採用できる」という推論は、配布形態の違いを飛ばしている。

小さな単一バイナリを保つ側から見ると、費用は次の順で重い。

1. 外部プロセス依存（scanner、package manager CLI、MSBuild）— 配布と再現性の両方を壊す。
2. 同定データの同梱 — 本文つき SPDX データはバイナリサイズの桁を変える。
3. 新しい I/O 境界（archive 展開、local package folder 探索）— 上限、path traversal、symlink escape の防御が必須になる。
4. 出力の多重化（original / curated / effective）— report サイズと hot path の allocation に効く。

**大規模 platform の境界は学べるが、規模は模倣しない。** ORT の analyzer / scanner / evaluator / reporter 分離、declared / detected / concluded / effective の語彙、curation を evidence と別に持つ設計は有用である。一方、plugin framework、Kotlin rule DSL、複数 backend storage をそのまま小さな CLI に導入する価値は低い。既存の typed data と明示的 side-effect boundary を維持し、必要な seam だけを採るべきである。

### 逆算に使うときの注意

この文書は参照実装の観察であり、ol に何が足りないかの結論ではない。ol 側の不足を判断するときは、次を必ず現物で確認する。

- 対象能力が ol に本当に無いか（[`DependencyInputRegistry`](../../../src/Ol.Core/DependencyInputRegistry.cs) と [`OlDefaults`](../../../src/Ol.Core/OlDefaults.cs) が登録済みの input / provider の正である）。
- その体験が既存機能で代替できないか（例: 永続 cache は再実行時の network 依存を既に外している）。
- 参照実装が支払っている代償を ol も支払えるか（上表の corpus 版、ローカル実体化、状態モデルの増加、配布サイズ）。
