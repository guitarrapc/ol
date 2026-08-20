# TODO

## mixed input における `DependencyType` の代表値

### Status

- 状態: P0 / P1 完了、P2 は判断待ち
- 対象: cross-input relationship merge、`DependencyInventoryCombiner`
- 発見契機: [Microsoft Component Detection の分析](../references/component_detection.md)、およびその分析に対する敵対的レビュー

### この文書の前提

初版はこの節を「native resolved graph > SBOM projection」という入力種別のヒエラルキーとして立てていた。その定式化は採用しない。理由は二つある。

一つ目。プロジェクトはすでに同じ問題に対する原則を持っており、それは入力種別の優劣ではない。[開発用途の判定](../specs/cli.md#contract-development-usage-and-sbom)で確立された規則は **「fact を determine した input がそれを所有し、何も determine していない input はそれを取り消さない」** である。軸は native 対 SBOM ではなく determined 対 undetermined であり、この軸のほうが扱える case が広い。package-manager input が `unknown`、SBOM が `direct` の場合、入力種別のヒエラルキーは SBOM の determination を捨てるが、determined 対 undetermined は捨てない。

二つ目。初版は `direct` 対 `transitive` の精度を論じることに大半を費やしていたが、その二値は表示列と `--dependency` filter にしか影響しない。exit code に影響するのは `root` だけであり、初版はその rung を「検討する問い」の 7 番目に分類学の質問として置いただけだった。

### P0 として解消した defect（完了）

`MergeDependencyType` は input 境界を跨いで `root > direct > transitive > unknown` を適用していた。ここで次の二つが重なっていた。

- `DependencyType.Root` を生成するのは SBOM parser だけである。package-manager adapter は 14 個どれも生成しない。
- `Root` は [policy 評価から除外される](../specs/cli.md#contract-policy-checks)。他の三値に policy 影響はない。

したがって、SBOM が resolved dependency と同じ purl を自身の root として述べると、その component が gate から消えた。再現は単純で、`pkg:npm/alpha@1.0.0` を `metadata.component` に持つ artifact SBOM を、alpha に依存する lockfile と一緒に scan すると、`check` が exit 2 から exit 0 に変わる。scan の表には該当行が残るため、人間の view では気づけるが CI は黙る。

Ol は他の未確定 case をすべて fail-closed に倒しているので、姿勢としても例外だった。`suppliedBy` は正常な fold と区別できないため、usage risk に対する緩和策として仕様が挙げる supply tally もここでは効かない。

**採用した規則**: package-manager input が供給した row に対して、SBOM の `root` は適用しない。package-manager input が component を列挙したこと自体が「これは scan 対象 resolution の依存である」という determination であり、SBOM の root はその SBOM 自身の graph の root にすぎない。受け手の row には沈黙以上のことを述べていないので、merge せず捨てる。package-manager input が答えない SBOM root は fold が発生しないため影響を受けず、従来どおり自身の row を保って `root` のままになる。

仕様は [folded relationship](../specs/cli.md#contract-folded-relationship) に記載した。regression は `MixedInputScanTests` の relationship 系 6 件と `CliCheckTests.Check_WithSbomRootNamingAResolvedDependency_StillEvaluatesThatDependency` が固定している。cross-input fold の等価クラスは次のとおり。

| package-manager | SBOM | 修正前 | 修正後 |
|---|---|---|---|
| unknown | root | `root` | `unknown` |
| direct | root | `root` | `direct` |
| transitive | root | `root` | `transitive` |
| （対応 row なし） | root | `root` | `root`（fold 不発） |
| unknown | direct / transitive | 強い方 | 変更なし |
| direct / transitive | 相互に不一致 | 強い方 | 変更なし（P2） |

`CombineSbomWithPackageManagerInputs` benchmark は 1024 components で 542.4 → 547.7 μs、4096 components で 1897.3 → 1883.0 μs、allocation は実質同値だった（`IterationCount=1` の単発測定）。

### P1 として明文化した内容（完了）

- [Architecture の input combination decision](../Architecture.md#decision-input-combination) に、combine が入力種別の ranking ではないこと、determine した input が所有し、何も determine していない input は取り消さないことを追記した。
- [CLI input combination contract](../specs/cli.md#contract-input-combination) に [folded relationship](../specs/cli.md#contract-folded-relationship) を追加した。
- [contract-development-usage-and-sbom](../specs/cli.md#contract-development-usage-and-sbom) に、同じ規則が relationship にも及ぶこと、license evidence はそのどちらとも別で入力種別による選別を行わないことを追記した。combined scan は同じ component について三つの別の規則を述べており、そのいずれも SBOM 対 package-manager の勝敗ではない。
- [Package-manager の lessons learned](../specs/packagemanager.md#lessons-learned) の「Neither input path is generally better」が license evidence の観測に限定された結論であることを明記した。この bullet を入力種別全体の parity と読んだことが P0 の defect の遠因である。
- `DependencyType` の XML doc が全 4 値を「SBOM root component 相対」と定義していたため、graph 相対の記述に直した。`Root` を述べるのが SBOM だけである理由もそこに書いた。

### P2 として残る問い（判断待ち）

残る唯一の実質的な設計問題は次である。

**package-manager input と SBOM が両方 relationship を determine していて食い違うとき、component-level の代表値をどうするか。**

現在は strongest-wins のまま、`direct` が `transitive` に勝つ。この値は表示列、`--dependency` filter、sort、group にのみ影響し、exit code には影響しない。したがって急ぐ理由はない。

判断する前に答える問い。

1. component-level の `DependencyType` は「どれか一つの input が観測した最も root に近い relationship」か、「代表 resolved graph が証明した relationship」か。
2. artifact-specific SBOM が repository-wide package-manager graph とは別の配布 artifact について `direct` を述べている場合、その値を同じ component-level 分類に畳んでよいか。
3. relationship の不一致を warning、structured provenance、通常状態のどれとして公開するか。

選択肢は初版から変わらない。

- **A. package-manager classification を代表値にする**。native graph が inventory structure を所有する原則と単純に一致するが、artifact-specific SBOM の異なる relationship が default view から見えなくなる。
- **B. strongest-wins を維持し、出典と不一致を明示する**。human output と policy behavior の変更が最小。provenance field と disagreement state が増える。
- **C. component-level の単一分類を graph-derived view にする**。canonical JSON、filter、group、summary、`check` の dependency filtering まで波及する大きな contract change。graph を持たない input と legacy report の扱いも必要になる。

現時点で C を正当化する根拠はない。動機が概念的な正しさのみで、exit code に効かない値のために report contract 全体を変えることになる。判断のために実測が必要なのは B と A の差だけであり、その実測は「ecosystem 別に native resolved input と SBOM を対にして `direct` / `transitive` の不一致を数え、missing edge・異なる root scope・workspace 差・artifact 差・generator defect に分類する」ことに限定できる。初版の Phase 2 が計画していた全 rung の実測は不要になった。

### 付随して残る整理（優先度低）

`DependencyTypes.Merge` と `DependencyInventoryCombiner.MergeDependencyType` は同一実装が二箇所にある。前者は一つの graph の複数観測を集約する intra-input 用、後者は input 境界を跨ぐ cross-input 用で、意味が異なる。P0 で cross-input 側の入口を `FoldDependencyType` として名前で分けたが、実装の重複自体は残っている。P2 の判断が A か C になった場合、この二つは別々の規則になるため統合してはならない。B になった場合のみ統合を検討できる。

### 非目標

- SBOM 入力 support を廃止すること。
- SBOM-only component を inventory から削除すること。
- SBOM graph や occurrence を package-manager graph へ推測で接続すること。
- package-manager license claim を常に SBOM claim より優先すること。
- SBOM generator の品質差を Ol が暗黙に補正・推測すること。
- dependency resolution を Ol 内部で実行すること。

### 関連実装と仕様

- [Architecture: input combination](../Architecture.md#decision-input-combination)
- [CLI: component supply](../specs/cli.md#contract-component-supply)
- [CLI: input combination](../specs/cli.md#contract-input-combination)
- [CLI: folded relationship](../specs/cli.md#contract-folded-relationship)
- [CLI: development usage and SBOM](../specs/cli.md#contract-development-usage-and-sbom)
- [Package-manager evidence lessons](../specs/packagemanager.md#lessons-learned)
- [`DependencyInventoryCombiner`](../../../src/Ol.Core/DependencyInventoryCombiner.cs)
- [`DependencyType`](../../../src/Ol.Core/DependencyType.cs)
- [`MixedInputScanTests`](../../../tests/Ol.Tests/MixedInputScanTests.cs)
- [Component Detection comparison](../references/component_detection.md)
