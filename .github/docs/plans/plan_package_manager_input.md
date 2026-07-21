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
- Phase 5以降に列挙していないpackage manager入力の具体実装

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
ol scan --input bom.json
ol scan --input bom.spdx.json
ol scan --input obj/project.assets.json
```

`--input-format`は`auto`を既定とし、登録済みadapterが所有するcontent signatureで判定する。異なる形式が同じJSON、YAML、lockfileを使用する可能性があるため、拡張子だけに依存した判定を公開契約にしない。明示formatは検出結果に対するassertionとして維持する。

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
- Phase 1時点では`--input`に`--input-format`を必須とした。Phase 5で省略時を`auto`へ変更した。`--sbom`との同時指定は引き続き拒否する。
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
- project rootはcontextが所有するedge endpoint sentinelとし、`project.frameworks`で証明できるroot dependencyからtarget graphを探索する。license対象ではないproject componentは生成しない。
- `type: package`かつ`libraries`でもpackageと確認できるentryだけをNuGet package occurrenceにする。project reference、未解決entry、非package entryはpurlを持つpackageとして扱わない。
- project reference nodeは到達性とdirect/transitive分類には使うが、package occurrenceやpackage edgeへ偽装しない。
- package-to-package edgeとroot-to-direct-package edgeだけをcontextごとに保持する。証明できないdependency typeはunknownとする。
- 同じpackage/versionが複数contextに現れた場合はcomponentとoccurrenceをcontextごとに保持し、生成した同一versioned purlを既存enrichmentのdeduplicate keyとして再利用する。
- adapterは入力由来文字列をsource-backed `Utf8Slice`で保持し、graph作業配列はpoolし、生成purlとowned resultだけを永続allocationとする。

### Phase 4: 出力と性能検証（完了）

- text、JSON、Markdownが入力種別を正しく表示することを確認する。
- JSONがdeterministicで、occurrenceとresolution contextを失わないことを確認する。
- inventory ingestionとend-to-end scanの時間・allocationをBenchmarkDotNetで既存baselineと比較する。
- network requestが重複package occurrence数ではなく、deduplicate済みtarget数に基づくことを確認する。

Phase 4では次の契約と性能境界に確定した。

- textとMarkdownのprimary reportは`{input kind}/{input format}` headerを持ち、`--quiet`でも入力種別を失わない。
- schema v1 JSONへinput-orderの`inventory` objectを追加し、contexts、component identity、occurrences、edgesを表示用component viewと分離する。
- occurrenceのcomponent indexは常に`inventory.components`を参照し、sort、filter、group後の表示配列を参照しない。
- context所有のproject rootは`fromOccurrenceIndex = -1`で表し、license対象ではないsynthetic `ScanComponent`とoccurrenceを割り当てない。
- `metadata.packageMetadata.targetCount`はdeduplicate後のversioned package target数を示す。fixtureでは6 occurrenceを4 targetへ集約する。
- NuGet parserの一時HashSetとDictionaryを、pooled package identity配列とpooled open-addressing node indexへ置換した。
- 2 package fixtureのNuGet ingestionはN=3で5.490 µs / 856 Bとなった。allocationは1,464 Bから約42%削減し、同等のowned result allocation floorは792 B、parser固有の上乗せは64 Bである。
- 同時刻のHEAD E2E比較では、textは1.946 ms / 22.77 KBから2.013 ms / 23.21 KB、JSONは2.042 ms / 39.63 KBから1.934 ms / 41.68 KBとなり、時間とallocationはいずれも10%基準内に収まった。JSONのowned output増加は完全inventory追加に対応する。
- NuGet専用E2E baselineとして、2 packageとcache済みenrichmentを含むtext 3.484 ms / 42.14 KB、JSON 3.201 ms / 78.88 KBを記録した。

### Phase 5: input format自動判定とverbose診断（完了）

- `--input-format`省略時と`--input-format auto`を同義とし、登録済みformatをcontentから判定する。
- 各`DependencyInputHandler`が型付きのtop-level JSON signatureを所有し、拡張子や登録順へ依存しない。
- CycloneDXは`bomFormat == CycloneDX`、SPDXはstring型`spdxVersion`、NuGet assetsはnumeric型`version`およびobject型`targets`、`libraries`、`project`を必須markerとする。NuGet parserはschema version 3/4だけを受理する。
- signature一致が0件ならunsupported、複数ならambiguousとしてscan全体を失敗させ、推測しない。
- 明示した非auto formatは従来どおり検出結果と照合し、不一致を拒否する。
- 既存`--verbose`を詳細列と実行診断の両方に用い、検出した`{kind}/{format}`をstderrへ表示する。
- 通常パスではverbose文字列を構築せず、format判定はJSONを1回走査する。16 handlerまで192 bytesの固定stack状態を使い、それを超えるregistryだけ`ArrayPool`を使う。
- N=10のfocused benchmarkでは、旧single marker相当の検出が1.367 µs / 0 B、NuGet複合signatureが1.389 µs / 0 Bで、追加CPUは1.6%、allocation増加は0 Bだった。full ingestionのallocationもCycloneDX 280 B、NuGet 856 Bから増加していない。

### Phase 6: npm `package-lock.json` input adapter（完了）

- npm lockfile version 2および3の`packages`とdependency linkを、registry問い合わせによる再解決なしで取り込む。
- root packageとworkspace originをcontextとして保持し、workspace、link、optional、peer、dev entryをpackage occurrenceと混同しない。
- package pathが異なる同一name/version occurrenceを保持し、`pkg:npm` purl単位のenrichment targetだけをdeduplicateする。
- `os`、`cpu`、optional install条件は入力が提供した値だけをvariantとして保持し、実行hostで評価または推測しない。
- malformed、workspace、nested duplicate、optional/platform固有fixtureとallocation benchmarkを追加する。

Phase 6では次の境界と性能特性に確定した。

- `lockfileVersion`のnumeric markerと`packages` objectを所有する`npm-package-lock` handlerを登録し、正確な`package-lock.json`名をdirectory discoveryへ追加した。parserはversion 2/3だけを受理する。
- 空package pathをroot context、非`node_modules`かつ非linkのpackage pathをworkspace contextとして保持する。link/workspace/rootはgraph traversalに使うがnpm registry componentへ偽装しない。
- `dependencies`、`optionalDependencies`、`devDependencies`、`peerDependencies`をinstalled pathに対するancestor lookupで結び、欠落したoptional/peer entryからphantom occurrenceを生成しない。
- installed pathごとにcomponentとoccurrenceを保持し、handlerのcollection identityを`purl + sourceId`とするため、同一name/versionのnested duplicateも単一・複数入力のどちらでもsource idとedgeを失わない。同じcanonical `pkg:npm` purlは既存enrichment plannerのtarget deduplicate keyになる。
- package固有の`dev`、`optional`、`devOptional`、`peer`、`os`、`cpu`は条件を持つoccurrenceだけのsparse variant配列に保持する。実行hostで条件を評価または推測しない。
- `packages[].license`は`dependency-input` provenanceとしてSPDX分類し、SBOM evidenceとして表示しない。
- parser token loopはsource-backed `Utf8Slice`、pooled node/dependency/index/graph buffer、span-based open addressingを使用し、LINQ、transient string、per-edge collection allocationを持たない。
- 2 package focused benchmarkではnpm ingestionが5.271 µs / 856 B、同じowned result floorが254.2 ns / 856 Bで、parser固有のmanaged allocationは0 Bだった。同時測定した既存CycloneDXは1.616 µs / 264 B、NuGetは5.237 µs / 824 Bで、記録済みallocation baselineから増加していない。

### Phase 7: pnpmおよびYarn lock input adapters（完了）

- pnpmは`pnpm-lock.yaml`のlockfile versionを明示formatとして扱い、importerごとのrootとsnapshot graphを保持する。
- YarnはBerryのinstall-stateを再現しようとせず、lockfileが証明できるdescriptor/resolution graphとworkspace originだけを取り込む。ClassicとBerryを一つの曖昧parserへ混在させない。
- peer dependency variant、virtual package、workspace/link/protocol、optional dependencyを通常のregistry packageと区別する。
- YAML parser導入のbinary size、Native AOT、allocation影響を先にbenchmarkし、許容できない場合は狭い専用parserを選ぶ。

Phase 7では次の境界に確定した。

- `pnpm-lock`はversion 9.0の`importers`、`packages`、`snapshots`を取り込み、importerをcontext、snapshotをnpm componentとして保持する。workspace/linkはtraversal nodeに限定する。
- `yarn-classic-lock`はversion 1 header、`yarn-berry-lock`は`__metadata.version == 8`で別々に検出・解析し、同じ`yarn.lock`名を登録順に依存せずcontentで判別する。
- Berry workspace resolutionはcontext、npm resolutionはcomponentとし、virtual hashをsparse variantへ保持する。Classicはroot manifestを持たないためdependency typeをunknownのまま保持する。
- peer、virtual、optional、dev、`os`、`cpu`は入力が証明する値だけをvariantへ投影し、実行hostで評価しない。
- 汎用YAML object modelは導入せず、source-backed `Utf8Slice`を返す狭いUTF-8 indentation readerと、`ArrayPool`によるnode/dependency/traversal bufferを使用する。これにより追加reflection metadataやNative AOT dependencyを持たない。
- 1 component focused benchmarkではpnpm 3.207 µs、Yarn Classic 1.571 µs、Yarn Berry 3.414 µsで、いずれも472 Bだった。同じowned result floorも472 Bであり、3 parser固有のmanaged allocationは0 Bだった。

### Phase 8: Cargo resolved metadata input adapter（完了）

- `cargo metadata --format-version 1 --locked` JSONを入力とし、`Cargo.toml`や`Cargo.lock`だけからfeature解決を再実装しない。
- resolve nodes、package IDs、workspace members、features、target-specific dependencyを保持する。
- target tripleやfeature setは生成条件としてcontext/variantへ保持し、複数metadata結果を自動mergeしない。
- registry、git、path packageを区別し、registry packageだけに`pkg:cargo` lookup identityを付与する。

Phase 8では次の境界に確定した。

- `cargo-metadata`はtop-levelのformat version 1、`packages`、`workspace_members`、resolved `resolve`、`target_directory`、`workspace_root`でcontent detectionし、`cargo-metadata.json`をdirectory discoveryへ登録する。`resolve: null`となる`--no-deps`出力は受理しない。
- workspace memberをpackage nameとresolved feature variantを持つcontextにし、workspace nodeはgraph traversal専用とする。非workspaceのregistry、git、path packageはCargo package idをsource identityにしたcomponentとして保持する。
- crates.ioのgit indexおよびsparse indexだけにcanonical `pkg:cargo/{name}@{version}`を付与する。alternate registry、git、pathをcrates.io enrichment対象に偽装しない。
- resolve node feature、incoming dependency kind、target expressionをsparse occurrence variantへ保持し、実行hostで評価しない。metadataには`--filter-platform`引数そのものがないためtarget tripleをhostから推測しない。
- workspace crossingは到達性とdirect/transitive判定へ使うが、省略したworkspace traversal nodeを架空のpackage edgeへ置き換えない。`packages[].license`は`cargo-metadata`の`dependency-input` provenanceとして分類する。
- parserは`Utf8JsonReader`を用いてDOMを構築せず、通常の文字列fieldをsource-backed `Utf8Slice`として保持する。package-id hash index、node/dependency/feature/graph buffer、incoming-edge indexは`ArrayPool`を使用し、token/edge loopにLINQ、transient string、per-edge collection allocationを持たない。
- 1 component focused benchmarkではCargo metadata ingestionが13.772 µs / 600 B、同じowned result floorが186.3 ns / 600 Bで、parser固有のmanaged allocationは0 Bだった。

### Phase 9: Go module graph input adapter（完了）

- `go mod graph`の解決済みmodule edgeを入力とし、`go.mod`からMinimal Version Selectionを再実装しない。
- main moduleをcontext root、versioned moduleをoccurrenceとして保持し、replace/local moduleをregistry moduleと偽装しない。
- GOOS、GOARCH、build tagsは`go mod graph`単体では証明できないため未指定とし、別context入力が導入されるまでhostから推測しない。
- `pkg:golang` identity、pseudo-version、retractionやreplace表現のfixtureを追加する。

Phase 9では、raw `go mod graph`だけではMVSで未選択のversionを除外できず、適用済みreplace/localの識別情報も失われるため、次の2-file境界に確定した。

- `go-list-modules.json`は`go list -m -json all`のJSON object sequence、`go-mod-graph.txt`は同じmodule/workspaceで実行した`go mod graph`出力とする。handlerが2つのexact filenameとbundle parserを所有し、明示されたpairまたは同一directoryの完全なpairを1 inventoryとして読む。
- selected module listだけをcomponent集合の根拠にし、graph edgeは両端がselected listに存在する場合だけ保持する。これにより未選択version、`go@...`、`toolchain@...`をcomponent化せず、Ol内でMVSを再実装しない。
- main moduleをcontext root、versioned moduleをoccurrenceとする。unreplaced moduleはoriginal `path@version`と`pkg:golang`を持ち、local replaceはpurlを持たず`replace=local`だけをvariantへ保持してprivate pathをreportへ出さない。
- versioned module replaceはoriginal requirementをsource identityとして保持し、実際のreplacement path/versionをenrichment purlとvariantへ使う。`Indirect`、入力に存在する`Retracted`、pseudo-versionを損失なく保持する。
- GOOS、GOARCH、build tagsは両出力から証明できないためcontextで未指定とし、実行hostから推測しない。
- JSON sequenceは`Utf8JsonReader`、graphはUTF-8 line parserでDOMやstring化なしに読み、module identity hash indexとnode/edge/traversal bufferを`ArrayPool`で管理する。token/edge loopにLINQ、regex、per-edge collection allocationを持たない。
- 1 component focused benchmarkではpaired Go ingestionが2.596 µs / 544 B、同じowned result floorが166.9 ns / 544 Bで、parser固有のmanaged allocationは0 Bだった。

### Phase 10: JVMおよびPython resolved input調査

- Maven、Gradle、Pythonはmanifestやregistryからの独自解決を行わず、標準的かつ機械可読なresolved graph出力をfixtureで比較する。
- Maven configuration/scope、Gradle configuration/variant、Python environment marker/platform wheelをresolution contextで表現できることを採用条件にする。
- 安定した標準出力がないecosystemでは、Ol固有portable inventoryを新設せず、既存SBOM生成経路を推奨する選択肢を残す。
- adapter採用前にdeterminism、Native AOT依存、pathological input、allocation floorを評価する。

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

1. `scan` がSBOMとNuGet `project.assets.json`を既定で自動判定し、明示formatもassertionとして受け付ける。
2. 既存の`scan --sbom`利用方法とlicense判定が維持される。
3. 入力解析、dependency inventory、license enrichment、scan result、renderingの責務が分離される。
4. package manager入力をSBOMと偽ってmetadataやevidenceを出力しない。
5. target framework、RID、platformなどが異なるgraphを早期にmergeしない。
6. occurrenceとedgeを保持しながら、registry/source lookupは正規化済みtarget単位でdeduplicateされる。
7. JSON出力がdeterministicで、将来または外部のbase/HEAD比較を妨げない情報を保持する。
8. 新しい入力形式の追加がenrichment、reconciliation、view、output formatの中央分岐へ波及しない。
9. 関連テストとallocation benchmarkで、正しさと説明不能な性能退行がないことを確認する。

## 実装前に確定する判断事項

- 将来の非JSON adapterで型付きcontent signatureをどう表現するか
- 既存JSON metadata fieldの互換期間
- resolution contextの必須fieldとecosystem固有fieldの保持方法
- SBOMにtarget/platform情報がない場合のcontext表現
- NuGetのproject reference、local package、compile/runtime/native assetの表示範囲
- package occurrenceをrendererで既定集約するか、contextごとに表示するか

これらは入力fixtureと既存CLI互換性テストを根拠に決定し、未確定の値をparser側で推測しない。
