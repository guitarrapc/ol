# Microsoft Component Detection の分析と ol との比較

## 調査範囲

この文書は、ローカルクローン [`component-detection`](../../../.references/component-detection) の実装を一次資料として、Microsoft Component Detection（以下 CD）が何を解決するツールか、どのように依存コンポーネントを検出するか、ol とどこが重なりどこが異なるかを整理したものである。

- CD: commit `d5f04d9d2c10494381d0b773f4d37635d3725ab2`（2026-08-10）
- ol: commit `074ba462baa56abfe6fa0869a6501fa84c24451f`（2026-08-19）
- 調査日: 2026-08-19

ユーザー指定の `C:\github\guitarrapc\ol.references\component-detection` は存在しなかったため、実在する `C:\github\guitarrapc\ol\.references\component-detection` を調査対象とした。

## 結論

CD と ol は、どちらも直接・推移的 OSS dependency を列挙し、package URL と依存関係を扱う点では似ている。しかし、製品としての中心は異なる。

- **CD は build-time component inventory generator である。** ソースツリー、lockfile、build artifact、package manager CLI、container image を横断してコンポーネントと依存グラフを見つける。
- **ol は resolved inventory に対する license evidence reconciler と policy evaluator である。** 解決済み入力を正規化し、SBOM・registry・source repository のライセンス証拠を SPDX に対して検証し、矛盾や不明を残した report を policy で評価する。
- CD は ol が非目標としている dependency resolution と build-context discovery の一部を担う一方、ol の中心である license evidence provenance、SPDX normalization、reconciliation、cache、diff、policy check は提供しない。
- したがって CD は全面的な代替ではない。概念的には **CD が上流 inventory、ol が下流 license compliance** という補完関係が近い。ただし、CD manifest は CycloneDX / SPDX SBOM ではなく、ol が現在直接読める形式でもない。

最も参考になるのは検出器の数ではなく、次の設計判断である。

1. 同じ ecosystem でも静的 parser、resolved output、build log、package manager CLI を段階的に使い分ける。
2. package identity と、manifest ごとの occurrence / graph / scope / target framework を分ける。
3. 新 detector を Experimental として本番出力から隔離し、既存 detector と比較してから昇格する。
4. build artifact にしかない情報（実際の target、self-contained、container layer、workspace owner）を inventory に残す。

一方、CD の detector framework、外部 process 実行、可変な fallback、mutable graph、出力モデル全体を ol に移植するべきではない。ol の deterministic input、typed provenance、policy separation、Native AOT という設計目標と衝突する。

## CD は何をするツールか

README は CD を「build time に利用し、複数 package ecosystem から graph-based output を作る package scanning tool」と定義している。また、CLI だけでなく NuGet library として detector / orchestrator を組み込める構成になっている。

CLI は二つの command を持つ。

- `scan`: `--SourceDirectory` 以下を走査し、既定では `ScanManifest_{timestamp}.json` を出力する。
- `list-detectors`: 利用可能な detector を列挙する。

主な入力は「一つの SBOM」ではなく、repository / build workspace 全体である。検出器により入力の信頼度と副作用は大きく異なる。

- lockfile / resolved artifact の静的解析: `package-lock.json`、`project.assets.json`、`Gemfile.lock`、`uv.lock` など。
- manifest の静的解析: `package.json`、`pom.xml` fallback、`requirements.txt` など。
- package manager CLI: `go list` / `go mod graph`、`cargo metadata`、`mvn dependency:tree`、`pip install --report`、Ant/Ivy。
- build artifact: Cargo SBOM、MSBuild binlog、vcpkg SPDX、.NET output assembly。
- container / deployment descriptor: Docker image、Dockerfile、Compose、Helm values。
- SBOM: SPDX 2.2 document。ただし一般 SPDX detector は package を展開せず、SPDX document 自体を一 component として登録する。

この幅広さが CD の強みである。同時に、同じ `scan` の中に pure parsing、local process execution、network resolution、container pull が混在することを意味する。

## アーキテクチャ

### project 構成

CD は .NET 8 の五つの主要 project に分かれる。

| project | 責務 |
|---|---|
| `Microsoft.ComponentDetection` | CLI entry point、DI、logging、command registration |
| `Microsoft.ComponentDetection.Contracts` | detector interface、typed component、recorder、scan/output contract |
| `Microsoft.ComponentDetection.Common` | directory walker、stream、dependency graph、recorder、process / Docker / telemetry utility |
| `Microsoft.ComponentDetection.Detectors` | ecosystem detector と parser |
| `Microsoft.ComponentDetection.Orchestrator` | detector selection、parallel execution、experiment、graph translation、manifest output |

実装規模は調査時点で `src` の C# が 396 files / 約 34,940 lines、`test` が 137 files / 約 39,822 lines だった。detector 固有の parser と fixture に厚い test を持つ一方、runtime は DI、Reactive Extensions、Dataflow、Serilog、複数 JSON stack、Docker / NuGet / MSBuild / YAML / TOML library を利用する比較的大きな managed application である。

project 自体は MIT license で、Windows / Linux / macOS の x64 / arm64 runtime identifiers を公開する。README によれば telemetry は既定で output path へ JSON として書かれるだけで Microsoft へ送信されない。

### detector extension point

中心 contract は [`IComponentDetector`](../../../.references/component-detection/src/Microsoft.ComponentDetection.Contracts/IComponentDetector.cs) である。detector は次を宣言する。

- stable detector ID と version。
- category と supported component types。
- root dependency を detector が明示するか、orchestrator が graph から自動計算するか。
- `ExecuteDetectorAsync(ScanRequest)`。

file-based detector は [`FileComponentDetector`](../../../.references/component-detection/src/Microsoft.ComponentDetection.Contracts/FileComponentDetector.cs) を継承し、file glob、prepare、per-file processing、finish の lifecycle を実装する。これは新 ecosystem を足しやすい一方、継承、mutable detector state、DI service、observable stream に依存する behavior-heavy extension model である。

detector は [`ServiceCollectionExtensions`](../../../.references/component-detection/src/Microsoft.ComponentDetection.Orchestrator/Extensions/ServiceCollectionExtensions.cs) に静的登録される。調査 commit では `IComponentDetector` 登録が 32 個ある。plugin directory からの runtime discovery ではなく、build-time composition である。

### 一回の directory traversal を detector 間で共有する

[`FastDirectoryWalkerFactory`](../../../.references/component-detection/src/Microsoft.ComponentDetection.Common/FastDirectoryWalkerFactory.cs) は root directory ごとの observable を `Replay().AutoConnect(1)` で cache する。最初の detector subscription が enumeration を開始し、後から購読した detector へ既走査結果を replay する。各 detector が repository 全体を別々に列挙する構造ではない。

directory traversal は次の性質を持つ。

- file name pattern は detector ごとに filter する。
- inaccessible entry は無視する。
- directory exclusion glob を適用する。
- reparse point は physical path を解決し、同じ実 directory の再走査を抑止する。
- directory enumeration 自体は processor count まで並列化する。
- file stream は match 後に lazy open される。

これは broad repository scan で有効な最適化である。ol の directory input も exact file name discovery を行うが、CD のような detector subscriber bus は持たず、登録された resolved input file を収集して format-owned parser へ渡す。

### detector 実行と並列性

[`DetectorProcessingService`](../../../.references/component-detection/src/Microsoft.ComponentDetection.Orchestrator/Services/DetectorProcessingService.cs) は enabled detector をすべて task 化し、`Task.WhenAll` で実行する。各 file detector の中は既定で逐次、`EnableParallelism` を選んだ detector は `min(Environment.ProcessorCount, MaxDetectionThreads)` まで per-file processing を並列化する。既定 `MaxDetectionThreads` は 5 である。

外部 tool 固有の制約は detector 側で処理する。たとえば Maven は local repository lock と JVM memory pressure を避けるため root POM ごとの CLI を逐次実行する。全 detector の一律な execution policy ではなく、framework-level concurrency と detector-owned concurrency が二層になっている。

### recorder と graph

detector は component を return value に積むのではなく、[`IComponentRecorder`](../../../.references/component-detection/src/Microsoft.ComponentDetection.Contracts/IComponentRecorder.cs) に登録する。manifest / lockfile location ごとに `SingleFileComponentRecorder` と dependency graph が作られ、`RegisterUsage` が次を同時に記録する。

- typed component identity。
- explicit root かどうか。
- parent component ID。
- development dependency の三値状態。
- Maven dependency scope。
- target framework。
- component に関連する source file location。

内部 [`DependencyGraph`](../../../.references/component-detection/src/Microsoft.ComponentDetection.Common/DependencyGraph/DependencyGraph.cs) は component ID を node key とし、dependency / depended-on-by の両方向 set を持つ。明示 root が detector から得られない場合は、incoming edge のない node を root とみなす。component ごとに top-level referrer と全 ancestor を逆向き traversal で計算する。

orchestrator は detector + component ID 単位で component を merge し、location、root、ancestor、dev status、scope、container layer、target framework を統合する。その後 [`DefaultGraphTranslationService`](../../../.references/component-detection/src/Microsoft.ComponentDetection.Orchestrator/Services/GraphTranslation/DefaultGraphTranslationService.cs) が output graph の ID を merged component ID と reconciliation する。

この model は「どの file で見つかったか」と「どの top-level dependency が連れてきたか」の説明力が高い。一方、同一 package の複数 target / runtime / workspace occurrence を独立した一級 object として保持せず、component に集合を merge する部分がある。この点は ol の `component` / `occurrence` / `resolution context` 分離より情報を畳みやすい。

## detector の成熟度 model

CD は detector を三種類に分ける。

| 種類 | 既定動作 | output |
|---|---|---|
| Stable | 実行する | 通常 manifest に含める |
| Experimental | 実行し、4 分 guard と telemetry / experiment comparison を適用 | 明示 enable しない限り component result を捨てる |
| DefaultOff | 実行しない | `DetectorArgs DetectorId=EnableIfDefaultOff` 等で opt-in |

Experimental は「利用者に beta result を見せる」状態ではなく、**shadow execution して既存 detector と比較する migration mechanism** として使われている。MSBuild binlog detector はその典型で、既存 NuGetProjectCentric / DotNet detector の置換候補を本番 scan 内で比較する。

この方式は detector migration に有用だが、コストは無料ではない。明示 enable していない Experimental detector も実行されるため、scan time と一時的副作用は発生し得る。さらに調査 commit では [`docs/detectors/README.md`](../../../.references/component-detection/docs/detectors/README.md) に 31 detector が載る一方、registration には experimental `LinuxApplicationLayerDetector` を含む 32 detector があり、catalog と実 composition に drift がある。

## ecosystem と検出方式

以下は CD の主要 detector と、ol の直接入力 support を対応づけたものである。「ol support」はその ecosystem の resolved input parser を意味し、SBOM 経由で任意 purl を受け取れることとは分けている。

| ecosystem / 対象 | CD の主入力・方式 | graph | ol の直接入力 |
|---|---|---:|---|
| CocoaPods | `Podfile.lock` を parse、subspec / Git dependency 対応 | あり | `Podfile.lock` |
| Conan | `conan.lock` | 限定的 | なし |
| Conda | `conda-lock.yml` | あり | なし |
| Docker Compose | Compose YAML の `services.*.image` | なし | なし |
| Dockerfile | `FROM` / `COPY --from` の image reference | なし | なし |
| .NET SDK | `project.assets.json`、`global.json`、`dotnet --version`、output PE | package graph ではない | SDK component なし |
| Go | `go.mod` + `go list -m` + `go mod graph`、失敗時 `go.sum` | CLI 時あり | `go list -m -json all` + `go mod graph` pair |
| Gradle | Gradle 7 以前の single-file `*.lockfile` | なし | なし |
| Helm | values YAML の image reference | なし | なし |
| Ivy | Ant / Ivy を temporary build で実行 | あり | なし |
| Linux image | Docker / OCI image を Syft で scan | layer mapping、package graph なし | なし |
| Maven | `mvn dependency:tree`、失敗時 static POM parse | CLI 時あり | Maven Dependency Plugin JSON |
| npm | `package.json`、package-lock v1-v3 | あり | `package-lock.json` |
| pnpm | lock v5 / v6 / v9 | あり | `pnpm-lock.yaml` v9 |
| Yarn | `yarn.lock` + peer/workspace `package.json` | あり | Classic / Berry `yarn.lock` |
| NuGet | `project.assets.json`、legacy nuspec/nupkg/packages.config/Paket、experimental binlog | assets 時あり | `project.assets.json` |
| Pip | `pip install --report`、失敗時 manifest parse、legacy PyPI resolver | report 時あり | `pip inspect` JSON |
| Poetry | `poetry.lock` | なし | なし |
| Ruby / Bundler | `Gemfile.lock` | あり | `Gemfile.lock` |
| Rust / Cargo | Cargo SBOM、fallback `cargo metadata`、さらに `Cargo.lock` | SBOM / metadata 時あり | `cargo metadata` JSON |
| SPDX | SPDX 2.2 document を一 component として登録 | packages は読まない | SPDX package inventory を読む |
| SwiftPM | `Package.resolved` | なし | `Package.resolved` v2 / v3 |
| uv | `uv.lock`、dependency group reachability | あり | なし |
| vcpkg | generated `vcpkg.spdx.json` + `manifest-info.json` | なし | なし |
| CycloneDX | detector なし | — | CycloneDX component / graph を読む |
| Composer | detector なし | — | `composer.json` + `composer.lock` pair |

CD の ecosystem 数は広いが、すべて同じ精度ではない。「supported」は次のいずれかを意味し得る。

- resolved graph をそのまま読む。
- package manager を起動して graph を生成する。
- manifest / lockfile の flat list を読む。
- image reference や SDK version だけを component 化する。
- fallback で過剰検出を許容する。

したがって単純な support count より、**各 detector の primary source、fallback、graph completeness、side effect** を比較すべきである。

## 検出精度に関する重要な設計

### resolved output を優先し、fallback を明示する

Go、Maven、Pip、Rust は高精度経路と低精度 fallback を持つ。

- Go: CLI が使えれば selected modules と graph を取得し、使えなければ `go.sum` を読む。後者は historical dependency を含むため over-report し得る。
- Maven: `mvn dependency:tree` が full graph と resolved version / scope の正であり、失敗時の POM parser は variable inheritance を三 pass で補うが、range / missing version は完全には解決できない。
- Pip: stable path は `pip install --report`。legacy detector は `setup.py` を Python で実行し PyPI へ問い合わせる。失敗時 source scan は graph を作らない。
- Rust: Cargo-generated per-artifact SBOM を最優先し、なければ `cargo metadata`、さらに `Cargo.lock` へ fallback する。

これは「何らかの結果を返す」実用性を高めるが、同じ source tree でも host tool availability、network、prebuilt artifacts によって output semantics が変わる。CD は telemetry と log に fallback reason を残すが、component ごとの input provenance と confidence を統一 data model として保持してはいない。

### build context を inventory に取り込む

CD が単なる lockfile parser より強いのは build context である。

- NuGet は target framework ごとの assets、compile-only / no-assets、framework conflict を使って development classification を補う。
- experimental MSBuild binlog detector は `IsTestProject`、`IsShipping`、`SelfContained`、`PublishAot`、`PackageReference.IsDevelopmentDependency` を読む。
- DotNet detector は SDK version、target framework、application / library、self-contained を component 化する。
- Cargo SBOM は「解決可能な graph」ではなく「artifact ごとに実際に build された graph」を優先する。
- vcpkg は installed tree の generated SPDX を読み、optional platform package の過剰検出を避ける。
- container scan は package と image layer を結び、base image だけに存在する component を filter できる。

ol の resolved input 原則と方向は一致している。ただし ol は caller が native resolver output を先に生成する contract であり、CD はその生成・発見まで tool 内で行う。

### location を actionability に使う

CD は component が見つかった generated artifact だけでなく、可能なら developer が変更すべき manifest へ location を写す。Cargo SBOM package を owning `Cargo.toml` に、vcpkg SPDX package を source `vcpkg.json` に関連づける例がある。

これは単なる provenance ではなく remediation UX のための mapping である。ol は report privacy のため absolute local path を永続化せず logical source reference を使うが、relative actionable location を occurrence provenance としてどこまで保持するかは CD から学べる。

## component identity と output contract

### typed component

[`TypedComponent`](../../../.references/component-detection/src/Microsoft.ComponentDetection.Contracts/TypedComponent/TypedComponent.cs) は ecosystem ごとの subclass を持ち、required identity field から `BaseId` を、download / source URL 等の optional provenance を加えて `Id` を作る。多くの component type は package URL も生成する。

この設計には二つの identity がある。

- `BaseId`: name / version / ecosystem 固有 field による logical package identity。
- `Id`: `BaseId` に optional URL metadata を加えた detection identity。

同じ package が rich ID と bare ID の両方で graph に現れた場合、graph translation が rich counterpart へ edge と metadata を展開する reconciliation を行う。これは optional metadata が identity に入ることで必要になった複雑さである。

ol は canonical purl と resolver-native `SourceId` の比較規則を input handler ごとに定義し、package identity と occurrence を分離する。URL evidence が後から増えても原則として package identity を変えないため、CD の bare / rich ID reconciliation はそのまま導入すべき pattern ではない。

### JSON manifest

[`ScanResult`](../../../.references/component-detection/src/Microsoft.ComponentDetection.Contracts/BcdeModels/ScanResult.cs) と [`ScannedComponent`](../../../.references/component-detection/src/Microsoft.ComponentDetection.Contracts/BcdeModels/ScannedComponent.cs) は次を出力する。

- `componentsFound`: typed component、detector ID、locations、dev flag、scope、top-level / ancestral referrer、target frameworks、container metadata。
- `dependencyGraphs`: source location ごとの adjacency map と root / dev metadata。
- `detectorsInScan` / `detectorsNotInScan`: detector ID、version、experimental status、component types。
- `resultCode`: `Success`、`PartialSuccess`、`Error`、`InputError`、`TimeoutError`。
- `sourceDirectory`: scan root。

公開 schema は [`manifest.schema.json`](../../../.references/component-detection/docs/schema/manifest.schema.json) にある。

注意点は次の通りである。

1. `sourceDirectory` は absolute path になり得る。CD manifest は ol の report privacy contract のままでは取り込めない。
2. graph key は location と component ID の string contract であり、ol の context / occurrence index model とは直接対応しない。
3. component の `licenses`、`licensesConcluded`、authors、supplier、download / source URL field は contract に追加されているが、調査 commit の built-in detector 実装には `Licenses` / `LicensesConcluded` を設定する code がない。serialization test は field contract を検証するが、CD 自体が license evidence collector になったことを意味しない。
4. component merge は concurrent dictionary / hash set 由来の collection を含み、canonical sort と hash を明示する ol の JSON contract ほど強い deterministic ordering は設計として宣言されていない。
5. schema generation test は基底 `ScanResult` から schema を作るため、実際に CLI が返す派生 `DefaultGraphScanResult.dependencyGraphs` を schema が記述していない。JSON Schema が追加 property を禁止していないので manifest validation は通るが、graph は公開 schema から型を復元できない。

### failure と exit behavior

内部 `resultCode` は detector failure の最大 severity を manifest に記録する。Experimental detector の failure は通常 result code を悪化させず、その components も捨てる。parse / connectivity failure component は recorder の skipped list と log warning に残る。

ただし [`ScanCommand.ExecuteAsync`](../../../.references/component-detection/src/Microsoft.ComponentDetection.Orchestrator/Commands/ScanCommand.cs) は scan result の `resultCode` にかかわらず manifest を書いた後 `0` を return する。したがって CI caller は process exit code だけで partial / timeout / input error を判定できず、manifest の `resultCode` または log を読む必要がある。

ol は command failure、incomplete evidence、policy violation を別 exit behavior として扱う。CD manifest adapter を将来検討する場合、CD process success と manifest completeness を混同してはならない。

## 外部依存、副作用、再現性

CD scan は detector 構成次第で read-only / offline operation ではない。

| detector | 外部依存・副作用 |
|---|---|
| Go | Go CLI を実行し、module 未取得なら fetch し得る |
| Maven | Maven CLI / dependency plugin を実行し、plugin / dependency download、一時 `bcde.mvndeps` 作成 |
| Ivy | Ant / JDK を使い temporary build と dependency resolution |
| Pip | pip / Python と package index、report file 作成。legacy path は `setup.py` を実行 |
| Rust | Cargo CLI。metadata 実行時に resolver / local cache 状態の影響 |
| Linux container | Docker daemon、remote image pull、Syft container / binary execution |
| DotNet | `dotnet --version` と build output inspection |

directory exclusion、timeout、cleanup、CLI disable environment variable はあるが、権限・network・credential scope を一つの scan plan として事前表示する仕組みはない。untrusted repository を走査する場合、特に legacy Pip の `setup.py` 実行と package manager plugin execution は sandbox boundary を必要とする。

ol の通常 scan も package registry / source repository enrichment で network I/O を行うが、inventory parsing 自体は caller が明示的に生成した resolved input に限定される。また credential authority、bounded concurrency、cache、provenance を source-specific I/O boundary に閉じ込める。両者の再現性 model はここで大きく異なる。

## ol との機能比較

| 観点 | Component Detection | ol |
|---|---|---|
| 第一目的 | build workspace から component inventory / graph を発見 | resolved inventory の license state を説明・強制 |
| resolution | detector が CLI / network を使って行う場合がある | 行わない。native resolved output を入力にする |
| discovery | source directory を broad scan | file / directory から登録済み exact resolved input を検出 |
| ecosystem breadth | 23 前後の package / image / SDK 対象 | 15 input formats、9 registry metadata providers、SBOM は ecosystem-neutral |
| graph model | location ごとの component-ID adjacency graph | component、occurrence、resolution context、edge を分離 |
| build context | target framework、binlog、artifact、container layer が強い | resolved input が供給する context / variant を保持 |
| identity | ecosystem-specific typed subclass + BaseId / rich Id + purl | canonical purl + source ID、format-owned comparison |
| license claim | field はあるが built-in detector は実質未供給 | SBOM / package / source claims を candidate として保持 |
| SPDX | SPDX 2.2 document detectorは document component 化 | versioned SPDX data で ID / expression / text を検証・正規化 |
| reconciliation | detector result と graph ID / metadata の merge | source 間の matched / conflict / unknown / ambiguous / invalid / error |
| provenance | detector、location、referrer、target、container layer | typed license evidence、input hash、logical source、SPDX dataset hash |
| policy | なし | persisted JSON に対する `check`、baseline、SARIF |
| report lifecycle | scan manifest 一種類 | canonical JSON + text / Markdown view、`diff`、offline re-check |
| cache | detector 固有の in-memory cache が中心 | versioned persistent evidence cache、明示 refresh |
| failure | manifest `resultCode` と log、CLI scan は通常 exit 0 | collection failure、command failure、policy violation を分離 |
| determinism | host tool / network / artifact / fallback に依存 | same inputs + selected SPDX data で deterministic を目標 |
| deployment | .NET 8 managed app、外部 CLI / Docker を利用し得る | .NET 10 Native AOT single CLI、外部 resolver を内包しない |
| library design | interface / inheritance / DI-based detector framework | typed data、registry、delegate、explicit side-effect boundary |

## 似ている部分を実装レベルで比較する

### input registry と detector registry

CD の detector registry と ol の [`DependencyInputRegistry`](../../../src/Ol.Core/DependencyInputRegistry.cs) は、入力 format を追加する extension point という意味で対応する。しかし責務が違う。

- CD detector は file discovery、process execution、parsing、graph construction、telemetry を持つ behavior object である。
- ol handler は content signature、parser delegate、directory file names、identity comparison を持つ immutable data registration である。

CD model は多様な lifecycle に強く、ol model は determinism、AOT、allocation、testability に強い。ol に build invocation が必要になっても `DependencyInputHandler` 自体へ side effect を入れるべきではない。resolved artifact generator を別 command / adapter boundary に置き、生成された bytes を既存 parser に渡す方が設計に合う。

### graph completeness

CD は detector ごとに graph availability が異なり、graph がない flat component も同じ manifest に載せる。ol も input が graph を供給しない場合を `Unknown` relationship として表現できるが、component / occurrence / edge の配列を分離し、unknown を推測で埋めない。

CD から学ぶべきは「全 detector に graph を要求する」ことではなく、graph completeness を detector / input capability として明示すること、fallback で graph が失われたことを result に残すことである。ol では input descriptor に parser identity と input hash はあるが、primary / fallback generator の差は caller-generated file name にしか現れない場合がある。

### development dependency

両者とも boolean 一つでは足りないことを認識している。

- CD は per-node nullable bool を merge し、production path が一つでもあれば false 側へ寄せる。Maven scope は別 enum で残す。
- ol は occurrence ごとの `Runtime` / `Development` / `Unknown` と、usage determined range を持つ。同一 component が context ごとに異なる usage を持てる。

CD の binlog / uv reachability の情報源は参考になるが、ol へ取り込むなら component flag へ潰さず occurrence classification として扱うべきである。

### package metadata と license evidence

CD の typed component に license / supplier / author field が追加されたことで見た目は ol に近づいている。しかし data flow は異なる。

- CD の recorder は detector が与えた metadata を union するだけで、source authority、raw claim、normalization result、conflict を model 化しない。
- ol の `LicenseCandidate` は source、kind、raw / normalized claim、typed evidence、warning を持ち、全 source を共通 reconciliation に通す。

CD manifest の `licenses` を将来 ol の evidence に使う場合も、それを concluded fact とせず **input-declared candidate** として扱い、detector ID / version / location を provenance にする必要がある。

## ol が採用を検討できる知見

### 1. build-artifact-first の入力 guidance を強化する

CD の Cargo SBOM、MSBuild binlog、vcpkg SPDX の設計は、generic lockfile より build artifact の方が実際に使われた dependency を正確に表す場合があることを示す。ol はすでに resolved input を要求しているため方針変更は不要だが、ecosystem ごとに次の優先 input を document / skill で案内できる。

1. artifact-specific SBOM / resolver output。
2. complete resolved graph。
3. lockfile。
4. manifest は受け取らない。

### 2. input capability / completeness を report に明示する

CD detector docs は graph creation、dev labeling、requirements を detector ごとに公開している。ol でも input format ごとに次を machine-readable にできる余地がある。

- graph completeness。
- root/direct classification availability。
- development usage availability。
- resolution context availability。
- source license claims availability。

これは parser の成否とは別に「入力が何を証明できないか」を説明する。

### 3. detector experiment の考え方だけを取り入れる

MSBuild binlog detector の shadow comparison は、新 parser migration の rollout pattern として有用である。ol では runtime に experimental parser を二重実行する必要はないが、verification harness で旧 / 新 parser の inventory、occurrence、edge、usage、allocation を同一 corpus に対して diff する形へ翻訳できる。

### 4. generated artifact から editable manifest への mapping

Cargo / vcpkg の location mapping は remediation に効く。ol では privacy contract を維持しながら、input 内に明示された relative project origin や manifest reference を occurrence に残せる。host filesystem を推測して absolute path を書くべきではない。

### 5. container inventory は別 upstream として扱う

CD + Syft の layer-aware inventory は ol にない能力である。しかし container extraction、archive safety、Docker credential、platform selection、Syft version pinningまで ol に内包すると scope と distribution cost が大きい。ol 自身が container scanner になるより、Syft 等が生成した CycloneDX / SPDX を ol が license analysis する既存境界の方が適切である。

## 採用しない方がよい部分

### detector framework 全体

DI、interface、inheritance、observable、mutable recorder は多様な detector lifecycle には合うが、ol の hot path と Native AOT の制約には過剰である。ol の format-owned parser + explicit registry を維持し、必要な lifecycle は CLI boundary の小さな data-driven plan として追加すべきである。

### tool availability による implicit fallback

同じ command と source tree が、Go / Maven / Cargo / Python の有無で異なる inventory を返すのは build integration では便利だが compliance report の reproducibility を弱める。ol が input generator を提供する場合は primary / fallback を自動選択せず、command / format 名を分け、生成方法と tool version を input descriptor に刻むべきである。

### optional metadata を package identity に含めること

download URL / source URL の追加で component ID が変わると、bare / rich reconciliation が必要になる。ol は package identity と evidence provenance を分離し続けるべきである。

### process exit 0 と manifest status の二重契約

CI-oriented compliance tool では process status と persisted result の関係を曖昧にしない方がよい。ol の command / collection / policy failure 分離を維持する。

## CD manifest を ol input にする案の評価

技術的には adapter を作れる。component の package URL、location、dev flag、graph を ol inventory に写し、`licenses` があれば SBOM-like candidate として受け取れる。しかし現時点では優先度は高くない。

### 利点

- ol が直接 support しない Conda、Gradle、uv 等を一つの upstream format から受け取れる。
- CD が集めた target framework、referrer、container layer metadata を使える。
- organization が既に CD / Azure DevOps component governance を標準化している場合、再 resolution を避けられる。

### 問題

- CD manifest は標準 SBOM ではなく CD 固有 contract である。
- `sourceDirectory` と locations の privacy normalization が必要である。
- package URL が object 表現で、全 component type に存在するとは限らない。
- graph は location-keyed string IDs で、ol の occurrence / context に一意に写せない場合がある。
- detector fallback / external tool version / skipped component の provenance が component 単位で十分でない。
- built-in detector は license field を実質埋めないため、adapter を追加しても license enrichment work は減らない。
- CD 自体が CycloneDX を出力しないため、標準 format を介した単純な接続ではない。

したがって、具体的な利用者需要と fixture が現れるまでは専用 adapter を追加せず、まず native resolver output または standard CycloneDX / SPDX を ol に渡すべきである。需要が出た場合も、CD manifest 全体を信頼済み SBOM と扱わず、`ScanInputFormat.ComponentDetection` の独立 parser と明示 capability を持たせる必要がある。

## 推奨する位置づけ

CD を ol の roadmap へ反映するときは、次の境界が妥当である。

```text
source / build workspace
        |
        | native resolver, build SBOM, CD, Syft, package manager command
        v
resolved inventory / standard SBOM
        |
        | ol input adapters
        v
license evidence collection -> SPDX normalization -> reconciliation
        |
        v
canonical report -> check / diff / SARIF
```

CD からは上段の inventory completeness、build context、migration verification を学ぶ。ol は下段の evidence、reproducibility、privacy、policy contract を守る。この分担なら、CD の ecosystem breadth を参考にしつつ ol を multi-purpose build scanner に変質させない。

## 主な実装参照

### Component Detection

- [README と feature overview](../../../.references/component-detection/README.md)
- [detector status catalog](../../../.references/component-detection/docs/detectors/README.md)
- [detector authoring guide](../../../.references/component-detection/docs/creating-a-new-detector.md)
- [CLI entry point](../../../.references/component-detection/src/Microsoft.ComponentDetection/Program.cs)
- [scan settings](../../../.references/component-detection/src/Microsoft.ComponentDetection.Orchestrator/Commands/ScanSettings.cs)
- [scan orchestration](../../../.references/component-detection/src/Microsoft.ComponentDetection.Orchestrator/Services/ScanExecutionService.cs)
- [detector execution](../../../.references/component-detection/src/Microsoft.ComponentDetection.Orchestrator/Services/DetectorProcessingService.cs)
- [detector restrictions](../../../.references/component-detection/src/Microsoft.ComponentDetection.Orchestrator/Services/DetectorRestrictionService.cs)
- [file detector lifecycle](../../../.references/component-detection/src/Microsoft.ComponentDetection.Contracts/FileComponentDetector.cs)
- [shared directory walker](../../../.references/component-detection/src/Microsoft.ComponentDetection.Common/FastDirectoryWalkerFactory.cs)
- [component recorder](../../../.references/component-detection/src/Microsoft.ComponentDetection.Common/DependencyGraph/ComponentRecorder.cs)
- [dependency graph](../../../.references/component-detection/src/Microsoft.ComponentDetection.Common/DependencyGraph/DependencyGraph.cs)
- [graph translation](../../../.references/component-detection/src/Microsoft.ComponentDetection.Orchestrator/Services/GraphTranslation/DefaultGraphTranslationService.cs)
- [typed component](../../../.references/component-detection/src/Microsoft.ComponentDetection.Contracts/TypedComponent/TypedComponent.cs)
- [output schema](../../../.references/component-detection/docs/schema/manifest.schema.json)

### ol

- [design principles](../DESIGN.md)
- [architecture](../Architecture.md)
- [input registry and scanner](../../../src/Ol.Core/DependencyInputRegistry.cs)
- [normalized inventory model](../../../src/Ol.Core/DependencyInventory.cs)
- [built-in metadata providers](../../../src/Ol.Core/OlDefaults.cs)
- [SBOM parser and license evidence](../../../src/Ol.Core/Sbom/SbomInputParser.cs)
- [scan command pipeline](../../../src/Ol/ScanCommands.cs)
- [CLI and report contract](../specs/cli.md)
