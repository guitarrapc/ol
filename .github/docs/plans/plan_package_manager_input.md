# 複数の解決済み依存入力を受け付ける `scan` の整理

## 背景

`ol` は現在、CycloneDX JSON または SPDX JSON の SBOM を入力として、依存コンポーネントが持つライセンス情報を読み取り、package registry と GitHub License API の情報で補強し、ライセンスレポートを生成する。

利用者から、SBOM を用意しなくても package manager の解決結果からライセンスを調査したいという要望がある。想定する代表的な用途は、PR によって直接または推移的に追加されたパッケージと、そのライセンスを確認することである。

この用途では、package registry や License API だけから依存関係を再解決してはならない。実際に選択されたバージョン、optional dependency、target 固有依存などを正確に再現できないためである。`ol` は package manager や既存ツールが生成した**解決済み依存入力**を読み取る。

## 結論

`scan` を SBOM 専用コマンドではなく、解決済みの依存 inventory を入力としてライセンスを調査するコマンドとして整理する。

SBOM は引き続き主要な入力形式だが、製品境界ではなく複数ある入力形式の一つとする。package manager 入力のために、`scan` と同じ処理を行う別の `inventory` コマンドは追加しない。

```text
Resolved dependency input
  ├─ CycloneDX JSON
  ├─ SPDX JSON
  ├─ NuGet project.assets.json
  └─ 将来の lockfile / package manager output
                  │
                  v
         Dependency inventory
       (occurrences + edges + contexts)
                  │
                  v
      License evidence enrichment
       (registry / GitHub License API)
                  │
                  v
             Scan result
                  │
                  v
       text / JSON / Markdown
```

## 今回のスコープ

この plan では、次を優先する。

1. `scan` が受け付ける入力の指定方法を整理する。
2. 入力解析、依存 inventory、ライセンス補強、scan result の責務を分離する。
3. SBOM 固有の入力 metadata と、入力形式に依存しない scan pipeline を分離する。
4. platform、target、project などによって異なる依存グラフを保持できる共通モデルを定義する。
5. 最初の非 SBOM 入力として NuGet `project.assets.json` を追加できる境界を定義する。

## スコープ外

次はこの plan では扱わない。

- base と HEAD の差分判定および `diff` コマンド
- PR comment の生成や投稿
- Git checkout、worktree、restore、install の実行
- package registry を使った依存バージョンの独自解決
- portable inventory 専用コマンドまたは専用ファイル形式
- npm、pnpm、Cargo、Go、Maven、Gradle、Python 入力の具体実装

差分機能は、入力と scan result のモデルが安定した後に別課題として検討する。JSON 出力を決定的にし、base と HEAD の `ol` 出力を外部ツールで比較できる余地は残すが、この plan では比較単位や差分分類を仕様化しない。

## 用語と責務

### 解決済み依存入力

SBOM、lockfile、package manager の出力など、実際に解決されたpackage occurrenceと依存関係を提供する入力である。

入力アダプターは、その形式の構文と意味を解釈して、共通の dependency inventory に変換する。manifest だけを読み、registry API を再帰的に呼び出して依存解決を再実装してはならない。

### Dependency inventory

ライセンス補強前の、解決済み依存グラフを表す内部ドメインモデルである。`inventory` はこの内部概念を指し、今回新設するCLIコマンド名ではない。

最低限、次を保持できるようにする。

- package occurrence
- normalized package name
- resolved version
- ecosystem
- versioned purl（表現可能な場合）
- root、direct、transitive、unknown の依存種別
- dependency edge
- project または workspace origin
- resolution context
- resolver-native identifier（入力が提供する場合）
- 入力が提供するlicenseまたはrepository evidence
- 入力解析時のwarning

package occurrenceとnetwork lookup targetは分ける。同じpackage/versionが複数のproject、target、依存位置に出現した場合、occurrenceとedgeは保持する。一方、package registryやsource repositoryへの問い合わせは、意味的に同じversioned purlまたはsource target単位でdeduplicateする。

### License evidence enrichment

package registry、GitHub License API、cacheなどからライセンス証拠を追加する処理である。これらはdependency inputではなく、inventoryに含まれるpackage identityを補強するproviderである。

外部I/Oはこの境界に隔離し、bounded concurrency、cancellation、cache、retryの既存方針を維持する。問い合わせ完了順にかかわらず、結果順序はdeterministicにする。

### Scan result

dependency inventoryに、収集・照合済みのlicense candidate、status、warningと実行summaryを加えた結果である。text、JSON、Markdownの表示処理はscan resultを入力とし、元の入力形式を再解釈しない。

## `scan` のコマンドモデル

長期的なコマンド形は、入力パスと入力形式を分ける。

```text
ol scan --input bom.json --input-format cyclonedx
ol scan --input bom.spdx.json --input-format spdx
ol scan --input obj/project.assets.json --input-format nuget-assets
```

`--input-format` を明示できることを基本とする。異なる形式が同じJSON、YAML、lockfileを使用する可能性があるため、拡張子だけに依存した判定を公開契約にしない。

既存の次の呼び出しは、互換性のため維持する。

```text
ol scan --sbom bom.json
```

`--sbom` は SBOM 入力であることを明示する既存のショートカットとして扱う。`--input` と `--sbom` の同時指定はエラーにする。既存形式の自動判定を `--sbom` で継続するか、format optionを追加するかは実装前にCLI互換性テストで確定する。

入力形式の追加は、形式固有のmarker、parser、input metadata projectionを一つの登録単位に閉じ込める。新しい形式の追加によって、enrichment、reconciliation、view、output formatにecosystem固有switchを追加しない。

## Resolution context

解決済み依存グラフは、同じprojectやmanifestから常に一つに決まるとは限らない。platform、architecture、target framework、runtime、feature、variantなどによって、直接依存および推移的依存が変わり得る。

これはNuGet固有の問題ではない。

- NuGetでは、target frameworkとRIDによってcompile/runtime/native assetおよび依存関係が変わり得る。
- npm系では、OS、CPU、optional dependency、install条件によって実際のinstall graphが変わり得る。
- Cargoでは、target-specific dependencyやfeature selectionによってgraphが変わり得る。
- Goでは、GOOS、GOARCH、build constraintsなど、graphを生成した条件が結果に影響し得る。
- MavenやGradleでも、configuration、variant、classifier、platform条件などにより選択結果が変わり得る。
- Pythonでも、environment markerやplatform固有distributionによって結果が変わり得る。

この差をecosystem共通の`resolution context`として扱う。contextは一つの固定文字列に潰さず、少なくとも次の意味を表せる明示的なデータとする。

- projectまたはworkspace origin
- target frameworkまたはlanguage target
- runtime/platform/OS
- architecture
- resolver固有のtarget、configuration、variant

入力が値を提供しない項目はunknownまたは未指定として保持し、推測しない。

異なるresolution contextのgraphを入力解析時にmergeしてはならない。同じpackage/versionが複数contextに存在してもoccurrenceを保持し、direct/transitive判定は各contextのrootとedgeに対して行う。表示時の集約は可能だが、集約前の情報をscan resultから失わない。

NuGetの初期対応では、最低限target frameworkごとのgraphを分離する。RID別targetが存在する場合も別contextとして保持する。compile/runtime/native assetの詳細をどこまでreportに露出するかは、`project.assets.json` fixtureを確認して別途決定する。

## 入力metadata

現在の`ScanReport`とJSON出力は、`SbomFormat`、SBOM specification version、`sbomRef`、`sbomSha256`などを直接持つ。これを入力形式共通のdescriptorへ分離する。

入力descriptorは、少なくとも次を表せるようにする。

- input kind: `sbom`、`package-manager`など
- input format: `cyclonedx`、`spdx`、`nuget-assets`など
- source reference
- source hash
- parser identityまたはparser version
- format specification version（存在する場合）
- 入力から判明したresolution context

SBOM入力では既存metadataを正確に投影する。package manager入力をSBOMとして表示したり、存在しないSBOM versionを合成したりしない。

JSON schemaの変更では、既存consumerへの影響を明示する。旧SBOM metadata fieldを互換目的で残す場合でも、新しい入力形式に対して偽の値を出力しない。

## 既存pipelineの再利用と変更境界

現在の実装では、`SbomScanner`が`ScanReport`と`ScanComponent[]`を生成した後、次の処理は比較的入力形式から分離されている。

1. package metadata enrichment
2. source repository enrichment
3. license reconciliation
4. dependency filter、sort、group
5. text、JSON、Markdown rendering

この後半を再利用する。一方、現在の`ScanReport`と`ScanComponent`はSBOM固有の命名とmetadataを持ち、edge、origin、resolution contextを表現できないため、そのままpackage manager inputを詰め込まない。

最小の共通データ構造を先に定義し、次の境界を作る。

```text
Input adapter
    -> Dependency inventory
    -> License enrichment / reconciliation
    -> Scan result
    -> View / renderer
```

実装はdata-orientedな配列と明示的なvalue typeを優先する。componentとedgeの蓄積では入力件数からcapacityを見積もり、必要に応じてpooled bufferを使用する。owned resultにpoolの配列を露出しない。入力由来UTF-8値は、所有元bufferが生存する範囲では`Utf8Slice`を維持する。

## 最初の非SBOM入力: NuGet

最初の対象は`obj/project.assets.json`とする。これはrestore済みの結果であり、projectのtargetごとの解決グラフを取得できるためである。

初期対応では次を満たす。

- package libraryからNuGet package occurrenceを作る。
- `pkg:nuget/{id}@{version}` purlを正規化して生成する。
- project originを保持する。
- target frameworkおよびRIDをresolution contextとして保持する。
- contextごとにrootとdependency edgeを解釈する。
- 証明できる場合だけdirectまたはtransitiveとし、それ以外はunknownにする。
- project reference、local/path dependency、unresolved entryを黙ってpackageとして扱わない。
- package registry、source repository、cache、reconciliationを既存pipelineで再利用する。

複数targetに同じpackage/versionが現れても、inventory occurrenceはmergeしない。registry lookupはversioned purl単位でdeduplicateし、結果を各occurrenceへ投影する。

## 実施順序

### Phase 1: コマンドとデータ境界の仕様化（完了）

- `scan --input`、`--input-format`、既存`--sbom`の互換規則を決める。
- input descriptor、dependency inventory、resolution context、scan resultの最小データを定義する。
- occurrence、edge、network lookup targetを別の概念として定義する。
- JSON metadataの互換方針を決める。

Phase 1では次の契約に確定した。

- `--input-format`はcase-insensitiveな登録名とする。Phase 1時点では`cyclonedx`と`spdx`だけを受理し、Phase 3で`nuget-assets`を有効化した。
- 既存`--sbom`はcontentによるCycloneDX/SPDX自動判定を維持する。
- `--input`には`--input-format`を必須とし、`--sbom`との同時指定を拒否する。
- schema v1 JSONへ汎用input metadataを追加し、既存SBOM fieldは互換aliasとして維持する。非SBOM入力ではSBOM aliasを出力しない。
- resolution context、occurrence、edgeは別のvalue型として保持し、network lookup targetはinventoryに含めない。
- 非SBOM入力が将来license claimを提供する場合は`dependency-input` provenanceを使用し、SBOM evidenceと偽装しない。

### Phase 2: 既存SBOM scanの分離（完了）

- CycloneDX/SPDX parserをinput adapterとして共通inventory境界へ接続する。
- enrichment、reconciliation、view、rendererがSBOM parserを直接前提にしないようにする。
- 既存CLI出力とlicense判定を維持する回帰テストを追加する。
- format registration testを追加し、形式追加が中央switchへ波及しないことを確認する。

Phase 2では次の境界に確定した。

- `DependencyInputRegistry`のhandlerがinput kind、format、marker、parserを一つの登録単位として所有する。
- `DependencyInputScanner`がcontent detection、明示format照合、parser実行、input descriptor投影を担当する。
- CycloneDX/SPDX parserは`DependencyInventory`を返し、component、occurrence、解決可能なedgeをowned resultとして保持する。
- SBOMが共通のplatform/target contextを提供しない場合、context配列は空、occurrenceは`UnspecifiedContext`として保持し、実行hostから推測しない。
- occurrenceはcomponent dataを複製せず、context indexとcomponent indexだけを保持する。
- `ScanResult`は完全なinventoryとenrichment後componentを分離し、viewのsort/filterは別のprojectionへ適用する。
- CLI orchestrationは`SbomScanner`、`ScanReport`、`SbomFormat`へ直接依存しない。
- 既存`SbomScanner.Scan`と`ScanReport`は互換APIとして同じregistered adapterを経由する。

### Phase 3: NuGet input adapter（完了）

- `project.assets.json` fixtureからtarget/RID別graphを読み取る。
- package occurrence、edge、dependency type、purlを検証する。
- 複数target、RID固有native dependency、重複package、project reference、malformed inputをfixtureで検証する。
- enrichmentなし、およびcacheされたenrichmentを使ったscanを検証する。

Phase 3では次の境界に確定した。

- `targets`をformat markerとする`nuget-assets` handlerを既存のinput registryへ一件登録する。
- `project.assets.json` version 3の各targetを独立したresolution contextとし、`framework/RID`のRIDはruntimeへそのまま保持する。platformとarchitectureは推測しない。
- project自身はcontextごとのsynthetic rootとし、`project.frameworks`で証明できるroot dependencyからtarget graphを探索する。
- `type: package`かつ`libraries`でもpackageと確認できるentryだけをNuGet package occurrenceにする。project reference、未解決entry、非package entryはpurlを持つpackageとして扱わない。
- project reference nodeは到達性とdirect/transitive分類には使うが、package occurrenceやpackage edgeへ偽装しない。
- package-to-package edgeとroot-to-direct-package edgeだけをcontextごとに保持する。証明できないdependency typeはunknownとする。
- 同じpackage/versionが複数contextに現れた場合はcomponentとoccurrenceをcontextごとに保持し、生成した同一versioned purlを既存enrichmentのdeduplicate keyとして再利用する。
- adapterは入力由来文字列をsource-backed `Utf8Slice`で保持し、graph作業配列はpoolし、生成purlとowned resultだけを永続allocationとする。

### Phase 4: 出力と性能検証

- text、JSON、Markdownが入力種別を正しく表示することを確認する。
- JSONがdeterministicで、occurrenceとresolution contextを失わないことを確認する。
- inventory ingestionとend-to-end scanの時間・allocationをBenchmarkDotNetで既存baselineと比較する。
- network requestが重複package occurrence数ではなく、deduplicate済みtarget数に基づくことを確認する。

## テスト方針

変更はtest-firstで進め、少なくとも次を検証する。

- 既存`scan --sbom`の互換性
- `--input`、`--input-format`、`--sbom`の排他とvalidation
- 入力形式のregistration
- SBOM入力から共通inventoryへの変換
- NuGet target framework/RIDごとのpackage occurrenceとedge
- context間で異なるdirect/transitive判定
- 入力が証明できない関係のunknown判定
- occurrenceを保持したままnetwork targetをdeduplicateすること
- package manager由来evidenceをSBOM evidenceと偽装しないこと
- deterministicなreport順序とJSON出力
- malformed inputとpathological inputに対するboundedなエラー処理

## 成功条件

この計画が完了したと判断する条件は次のとおり。

1. `scan` がSBOMとNuGet `project.assets.json`を、明示された入力形式として受け付ける。
2. 既存の`scan --sbom`利用方法とlicense判定が維持される。
3. 入力解析、dependency inventory、license enrichment、scan result、renderingの責務が分離される。
4. package manager入力をSBOMと偽ってmetadataやevidenceを出力しない。
5. target framework、RID、platformなどが異なるgraphを早期にmergeしない。
6. occurrenceとedgeを保持しながら、registry/source lookupは正規化済みtarget単位でdeduplicateされる。
7. JSON出力がdeterministicで、将来または外部のbase/HEAD比較を妨げない情報を保持する。
8. 新しい入力形式の追加がenrichment、reconciliation、view、output formatの中央分岐へ波及しない。
9. 関連テストとallocation benchmarkで、正しさと説明不能な性能退行がないことを確認する。

## 実装前に確定する判断事項

- `--input-format`の値と命名規則
- `--sbom`互換時のformat自動判定をどこまで維持するか
- 既存JSON metadata fieldの互換期間
- resolution contextの必須fieldとecosystem固有fieldの保持方法
- SBOMにtarget/platform情報がない場合のcontext表現
- NuGetのproject reference、local package、compile/runtime/native assetの表示範囲
- package occurrenceをrendererで既定集約するか、contextごとに表示するか

これらは入力fixtureと既存CLI互換性テストを根拠に決定し、未確定の値をparser側で推測しない。
