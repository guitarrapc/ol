# TODO

## mixed input における `DependencyType` の代表値

### Status

- 状態: 完了（P0 / P1 / P2 すべて解消）
- 対象: cross-input relationship merge、`check` の view 開示
- 発見契機: [Microsoft Component Detection の分析](../references/component_detection.md)、およびその分析に対する敵対的レビュー

open な作業項目は残っていない。決定と根拠は仕様側へ移してある。この文書は経緯の記録であり、[repo の慣行](../../../AGENTS.md)に従って削除してよい。

### 初版の定式化を採用しなかった理由

初版はこの節を「native resolved graph > SBOM projection」という入力種別のヒエラルキーとして立てていた。採用しなかった理由は二つ。

一つ目。プロジェクトはすでに同じ問題への原則を持っており、それは入力種別の優劣ではない。[開発用途の判定](../specs/cli.md#contract-development-usage-and-sbom)で確立された規則は **「fact を determine した input がそれを所有し、何も determine していない input はそれを取り消さない」** である。入力種別のヒエラルキーは package-manager が `unknown` で SBOM が `direct` の場合に SBOM の determination を捨てるが、determined 対 undetermined は捨てない。

二つ目。初版は `direct` 対 `transitive` の精度に大半を費やしていたが、その二値は exit code に効かない。効くのは `root` だけで、初版はその rung を「検討する問い」の 7 番目に分類学の質問として置いただけだった。

### 解消した内容

#### 1. SBOM root による policy 素通し（P0）

`DependencyType.Root` を生成するのは SBOM parser だけであり、`Root` は [policy 評価から除外される](../specs/cli.md#contract-policy-checks)。cross-input の strongest-wins はこの二つを掛け合わせ、SBOM が resolved dependency と同じ purl を自身の root として述べると、その component が gate から消えていた。`pkg:npm/alpha@1.0.0` を `metadata.component` に持つ artifact SBOM を alpha に依存する lockfile と一緒に scan すると `check` が exit 2 から exit 0 に変わり、scan の表には該当行が残るため CI だけが黙った。

#### 2. resolver relationship の所有（P2）

両 input が relationship を determine して食い違う場合、closest-to-a-root ではなく resolver の値を代表値とする。relationship は graph 相対なので、値が違うのは通常「食い違い」ではなく「別の graph を述べている」であり、row は scan 対象の graph に属する。SBOM 側の relationship は occurrence / edge に保持されるため導出可能で、変わるのは要約値だけである。

決め手になった非対称性: [`--dependency` で絞った report](../specs/cli.md#contract-dependency-filtering) は `check` の母集団を狭める。strongest-wins では resolver が `transitive` と述べた component が `direct` に書き換わり、`--dependency transitive` の gate から抜ける。逆方向（resolver `direct` / SBOM `transitive`）はどちらの規則でも `direct` なので差が出ない。resolver 優先には対称のコストがない。

#### 3. `check` が filtered report を黙って gate していた

`scan` は `--dependency` を `metadata.view` に記録するよう既に直されていたが、`check` はそれを読まず、狭められた母集団を評価して pass を出していた。`--verbose` でも出なかった。producer 側で回収した fact を、次の consumer が再び落としていた。

filtered report は refuse せず開示する。grouped report を refuse するのは行が aggregate で評価不能だからで、filtered report は評価可能で小さいだけであり、どこまでを gate に含めるかは利用者の判断だからである。relationship が `unknown` のため除外された件数は別に数える。それらは policy が fail-closed に保つ population であり、落ちたときに証明できることが変わるのはその部分だからである。

### 最終的な cross-input fold の等価クラス

| package-manager | SBOM | 初版 | 現在 |
|---|---|---|---|
| unknown | unknown | `unknown` | `unknown` |
| unknown | root | `root` | `unknown` |
| unknown | direct | `direct` | `direct` |
| unknown | transitive | `transitive` | `transitive` |
| direct | root | `root` | `direct` |
| direct | transitive | `direct` | `direct` |
| transitive | root | `root` | `transitive` |
| transitive | direct | `direct` | `transitive` |
| （対応 row なし） | root | `root` | `root`（fold 不発） |

### 検討したが採らなかった選択肢

- **strongest-wins を維持し、出典と不一致を明示する**。default view で root/direct を見落とさないことが利点だが、resolver が `transitive` と述べているならこの resolution についての真実は `transitive` であり、見落としは起きない。advisory な値のために provenance field と disagreement state を report contract に足すことになる。
- **component-level の分類を graph-derived view にする**。canonical JSON、filter、group、summary、`check` の dependency filtering まで波及する。per-graph view を求める消費者が現れた場合、必要なデータは既に `inventory` の occurrence / edge / context にある。動機が概念的な正しさのみである間は採らない。

### 実装上の注意

`DependencyTypes.Merge` と `DependencyInventoryCombiner.FoldDependencyType` は**統合してはならない**。前者は一つの graph の複数観測を集約する intra-input 用、後者は input 境界を跨ぐ cross-input 用で、規則が異なる。現在は名前と comment で分けてある。

`FoldDependencyType` は row の現在値ではなく **fold 前に控えた resolver の値** から判定する。row を読み返すと、同じ purl を二箇所に置く SBOM の二回目の fold で「resolver が determine した」と「一回目の fill-in が書いた」を区別できず、結果が SBOM の component 列挙順に依存する。実装時にこの defect を一度作り込んで検出した。`resolvedRelationships` の pooled snapshot はそのためにある。

### filtered report の開示範囲

`metadata.view` を読む consumer は三つあり、すべてが開示する。

- `check` — 結果の前に filter と除外件数を出す。relationship が `unknown` のため除外された件数は別に数える。
- `check --sarif` — run-level の `properties.evaluatedView`。development allowance と同じ property bag を共有する（bag を二つ書くと duplicate key になり、reader は片方だけを残す）。
- `ol diff` — audit boundary の隣に `Evaluated view` block、JSON は `view` object。両者とも `--dependency` を**集合として**比較する。`inputScope` と同じく、綴りの違いを boundary の変更として報告しない。

`view` が読めない report は command failure。`dependencyFilter` キーの presence を必須とし、「filter は無かった」と述べる文書と「何も述べていない」文書を区別する。

### 非目標（当時のまま）

- SBOM 入力 support を廃止すること。
- SBOM-only component を inventory から削除すること。
- SBOM graph や occurrence を package-manager graph へ推測で接続すること。
- package-manager license claim を常に SBOM claim より優先すること。
- SBOM generator の品質差を Ol が暗黙に補正・推測すること。
- dependency resolution を Ol 内部で実行すること。

### 関連実装と仕様

- [Architecture: input combination](../Architecture.md#decision-input-combination)
- [CLI: input combination](../specs/cli.md#contract-input-combination)
- [CLI: folded relationship](../specs/cli.md#contract-folded-relationship)
- [CLI: development usage and SBOM](../specs/cli.md#contract-development-usage-and-sbom)
- [CLI: filtered report as policy input](../specs/cli.md#contract-policy-filtered-report)
- [Package-manager evidence lessons](../specs/packagemanager.md#lessons-learned)
- [`DependencyInventoryCombiner`](../../../src/Ol.Core/DependencyInventoryCombiner.cs)
- [`DependencyType`](../../../src/Ol.Core/DependencyType.cs)
- [`ScanReportReader`](../../../src/Ol.Core/Reporting/ScanReportReader.cs)
- [`MixedInputScanTests`](../../../tests/Ol.Tests/MixedInputScanTests.cs)
- [Component Detection comparison](../references/component_detection.md)
