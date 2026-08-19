# TODO

## Native resolved graph と SBOM の inventory 構造上の関係を明文化する

### Status

- 状態: 検討待ち
- 対象: inventory combination contract、`DependencyType` reconciliation
- 発見契機: [Microsoft Component Detection の分析](../references/component_detection.md)

### 背景

Ol は resolved package-manager input と SBOM を同じ dependency inventory input として受け取り、一つの scan で結合できる。現在の architecture は [inputs combine rather than compete](../Architecture.md#decision-input-combination) を原則とし、片方を捨てずに component population、graph、license evidence の差を観測可能にする。

ただし「入力を競合させない」ことと「すべての情報について両入力を同格に扱う」ことは同じではない。resolved package-manager output は通常、SBOM への変換後には失われ得る次の構造を持つ。

- resolver-native package identity と source identifier。
- 同一 package/version の複数 installation / occurrence。
- project、workspace、target、runtime、platform ごとの resolution context。
- root、direct、transitive を証明する dependency edge。
- development / runtime usage、optional、peer、feature 等の resolver condition。
- package manager が実際に選択した version と graph。

SBOM は標準的な交換形式として有用であり、artifact scanner が package-manager graph にない OS package、vendored component、embedded binary、別 subproject を観測することもある。一方、generator によって component identity、occurrence、edge、scope、context が省略・平坦化・誤変換される。したがって inventory 構造については、両者を単純に同格と表現すると Ol の実装意図を誤解させる。

この原則は **license evidence の優先順位を意味しない**。license fact がどこにあるかは ecosystem と generator によって異なる。SBOM が package contents から得た license claim を持ち、resolved input が何も持たない場合もある。現在どおり SBOM、dependency input、registry、package artifact、source repository の claim は provenance 付きの独立 candidate として保存し、共通 reconciliation に通す必要がある。

明文化したい関係は次である。

```text
Inventory structure and resolver facts
    native resolved package-manager graph > SBOM projection

Artifact observation and additional population
    SBOM supplements the native graph; SBOM-only components remain visible

License evidence
    no blanket precedence; preserve and reconcile every supported claim
```

ここで `>` は「常に正しい」「SBOM の component を捨てる」という意味ではない。両方が同じ resolved population を述べる場合に、package-manager input がより細粒度な row、occurrence、context、edge、resolver classification を所有するという構造上の原則である。

### 現在の実装

[`DependencyInventoryCombiner`](../../../src/Ol.Core/DependencyInventoryCombiner.cs) はすでにこの原則の大部分を実装している。

- package-manager component を先に割り当て、SBOM component を後から purl identity で fold する。
- matching row の identity、purl spelling、qualifier、source ID は package-manager input が所有する。
- package manager が区別する複数 installation を SBOM の一 row へ collapse せず、SBOM evidence を各 matching row へ fan-out する。
- SBOM にしかない component と purl のない component は `sbom` supply の独立 row として残す。
- package-manager input にしかない component も残す。
- 各 input の context、occurrence、edge を保持し、input 間の edge は発明しない。
- matching row には SBOM の license candidate を追加するが、package-manager candidate を上書きしない。
- package-manager側に repository URL がある場合は維持し、ない場合だけ SBOM の URL で補う。
- `suppliedBy` と summary の `sbomOnly` / `packageManagerOnly` / `both` で population の差を公開する。

これらは [CLI input combination contract](../specs/cli.md#contract-input-combination) と [`MixedInputScanTests`](../../../tests/Ol.Tests/MixedInputScanTests.cs) で固定されている。特に仕様は「package-manager inputs own the resulting rows and the SBOM folds into them」と述べている。

一方、[package-manager evidence の lessons learned](../specs/packagemanager.md#lessons-learned) にある「Neither input path is generally better」は license resolution の実測結果を述べたものだが、inventory fidelity まで同格であるようにも読める。仕様上、次の二点を分けて明示する必要がある。

1. inventory identity / occurrence / context / graph の骨格は native resolved graph が所有する。
2. license evidence の有用性には入力種別全体での優先順位を付けない。

### 未解決点: `DependencyType` の strongest-wins merge

現在の [`MergeDependencyType`](../../../src/Ol.Core/DependencyInventoryCombiner.cs) は、同じ component について複数 input が異なる分類を供給したとき、出典に関係なく次の強い値を combined component に採用する。

```text
root > direct > transitive > unknown
```

そのため package-manager graph が `transitive`、SBOM が `direct` と述べる component は `direct` と表示される。この動作は [`Scan_WithSbomDirectAndPackageManagerTransitive_KeepsTheStrongerRelationship`](../../../tests/Ol.Tests/MixedInputScanTests.cs) で明示的に固定されている。

この merge は「少なくとも一つの graph から root/direct と観測された」という集約値としては合理的である。しかし native graph を inventory 構造の基準とする原則とは緊張関係がある。

- `direct` は常に `transitive` より正確な値ではない。異なる root、workspace、artifact scope を述べている可能性がある。
- SBOM generator が edge や root を平坦化した結果、transitive package を direct と出力している可能性がある。
- package-manager classification を SBOM classification で強めると、combined component の代表値から resolver-native fact が見えなくなる。
- 一方で artifact-specific SBOM が、root package-manager graph とは別の実際の配布 artifact に対する direct relationship を述べている可能性もあり、SBOM の値を一律に無視するのも情報損失になる。
- graph、context、occurrence は input ごとに保持されるため、component-level の単一 `DependencyType` に異なる graph-relative relationship を畳むこと自体が問題かもしれない。

### 検討する選択肢

#### A. package-manager classification を代表値にする

matching component が package-manager input に存在するときは、その `DependencyType` を combined component の代表値とする。SBOM-only component だけは SBOM の分類を使う。

利点:

- native graph が inventory structure を所有する原則と最も単純に一致する。
- lossy generator が package-manager classification を上書きしない。

欠点:

- artifact-specific SBOM の異なる relationship が default view から見えにくくなる。
- 複数 package-manager input の classification をどう代表させるかは別途必要になる。ただし現在は異なる package-manager input の同一 purl を別 row として維持するため、SBOM boundary より問題は限定される。

#### B. strongest-wins を維持し、出典と不一致を明示する

combined display は現在どおり strongest-wins とするが、各 input / occurrence の classification と disagreement を canonical JSON や warning に残す。

利点:

- root/direct dependency を default view で見落としにくい。
- 現在の human output と policy behavior の変更が小さい。

欠点:

- `stronger` が `more authoritative` と誤解される。
- provenance field と disagreement state が増え、report contract が複雑になる。

#### C. component-level の単一分類を graph-derived view にする

canonical inventory では relationship を occurrence / edge / context の fact として扱い、component-level `DependencyType` は selected view の graph set から導出する。input 別または context 別 view ならそれぞれ異なる値を表示できる。

利点:

- graph-relative な概念を package identity に固定しない。
- package-manager graph と artifact SBOM graph の不一致を失わない。

欠点:

- canonical JSON、filter、group、summary、`check` の dependency filtering まで影響する大きな contract change になる。
- graph を持たない input と legacy report の扱いが必要になる。
- performance、report size、migration cost の評価が必要になる。

### 判断時に答える問い

1. `DependencyType` は「どれか一つの input が観測した最も近い root relationship」か、「代表 resolved graph が証明した relationship」か。
2. mixed input scan で代表 graph を選ぶのか、それとも代表値という概念を廃止するのか。
3. artifact-specific SBOM と repository-wide package-manager graph が異なる population / root を述べる場合、両者を同じ component-level分類へ畳んでよいか。
4. dependency filter と `--allow-dev-licenses` は combined component、occurrence、どの input の classification を評価すべきか。
5. relationship disagreement は warning、structured provenance、通常状態のどれとして公開するか。
6. package-manager input が `unknown`、SBOM が `direct` の場合も package-manager値を優先するのか。`unknown` は否定ではなく未証明なので、`transitive`対`direct`とは分ける必要があるか。
7. root componentを含むSBOMと、root packageをinventory componentに含めないpackage-manager inputをどう比較するか。

### 優先度付き対応フェーズ

#### Phase 1 — P1: 仕様上の原則を明確化する

目的: behaviorを変えず、現在すでに実装されている構造上の主従とevidenceの非優先を明文化する。

- [ ] [Architecture](../Architecture.md) の input combination decision に、native resolved graph が matching inventory row / occurrence granularity / context / edge を所有することを追記する。
- [ ] [CLI input combination contract](../specs/cli.md#contract-input-combination) に、優先対象が inventory structure であり、SBOM-only population と SBOM graph を捨てないことを追記する。
- [ ] [Package-manager specification](../specs/packagemanager.md) の「Neither input path is generally better」が license evidence の観測に限定された結論であることを明確化する。
- [ ] license candidateには blanket precedenceを導入しないことを明記する。

完了条件:

- inventory structure、artifact observation、license evidence の三つが別の規則として読める。
- 「Native graph > SBOM」がSBOM-only componentの削除やSBOM evidenceの無視を意味しない。
- 現行behaviorを変更しないため、このphaseではreport schema versionを変更しない。

#### Phase 2 — P1: `DependencyType` semanticsを実測・監査する

目的: 実装変更の前に strongest-wins が実際の mixed input で何を隠すかを確認し、A/B/C のいずれを採るか決める。

- [ ] ecosystem別に、同一projectのnative resolved inputとnative-generator / general scanner SBOMを対にしたfixtureまたはharnessを用意する。
- [ ] `root/direct/transitive/unknown` の一致・不一致をcomponent supply別に集計する。
- [ ] 不一致について、missing edge、異なるroot scope、workspace差、artifact差、generator defectを分類する。
- [ ] dependency filter、development-only policy、summaryへの影響を確認する。
- [ ] A/B/C をcorrectness、explainability、compatibility、performanceで比較し、decision recordを残す。

最低限のcase:

- package-manager `transitive` / SBOM `direct`。
- package-manager `direct` / SBOM `transitive`。
- package-manager `unknown` / SBOM `direct`。
- package-manager `direct` / SBOM `unknown`。
- package-managerに複数installation、SBOMに一component。
- SBOM-only component。
- SBOMにroot componentがあるがpackage-manager側には対応package rowがない場合。
- 同じpurlだが異なるworkspace / target / artifact graphに属する場合。

完了条件:

- strongest-winsを維持または変更する根拠がreal inputの観測と明示的semanticsで説明できる。
- `DependencyType`を読む利用者が何を証明された値として扱えるか定義されている。
- implementation phaseのcompatibility要件とschema影響が確定している。

#### Phase 3 — P2: 決定したbehaviorをtest-firstで実装する

目的: Phase 2のdecisionを最小のmodel変更で実装する。

- [ ] 先に mixed-input regression test を新しい期待値で追加または更新する。
- [ ] combiner、report projection、filter / grouping / policyへの影響を必要な範囲だけ変更する。
- [ ] package-manager-only、SBOM-only、複数package-manager inputの既存contractを維持する。
- [ ] purl identity、installation fan-out、candidate reconciliation、`suppliedBy` tallyが変わらないことを確認する。
- [ ] canonical JSONのobservable changeがある場合はschema versionとmigration policyを更新する。

完了条件:

- Phase 2で選んだsemanticsをunit / CLI testが再現する。
- inventoryのoccurrence / edge indexが全inputで正しくremapされる。
- license conflict、SBOM-only population、package-manager installation granularityを失わない。
- representative mixed-input benchmarkに有意なregressionがない。

#### Phase 4 — P2: 利用者向けguidanceと検証を更新する

目的: 入力の選び方を単純なSBOM対package-managerの勝敗ではなく、目的別に説明する。

- [ ] README / skill / CLI guidanceで、inventory骨格にはnative resolved outputを推奨する。
- [ ] artifact inclusionやpackage-manager外componentの観測にはartifact-derived SBOMを併用することを案内する。
- [ ] license completenessはecosystemとSBOM generatorに依存するため、`suppliedBy`とcandidate provenanceを確認するよう案内する。
- [ ] mixed-input verification corpusとbenchmarkを継続実行できる形にする。

完了条件:

- 利用者が「SBOMだけ」「package-managerだけ」「両方」の選択を、population、graph fidelity、license evidenceの目的から判断できる。
- 文書が一律に「SBOMが上」「package-managerが上」と推奨しない。

### 非目標

- SBOM入力supportを廃止すること。
- SBOM-only componentをinventoryから削除すること。
- SBOM graphやoccurrenceをpackage-manager graphへ推測で接続すること。
- package-manager license claimを常にSBOM claimより優先すること。
- SBOM generatorの品質差をOlが暗黙に補正・推測すること。
- dependency resolutionをOl内部で実行すること。

### 関連実装と仕様

- [Architecture: input combination](../Architecture.md#decision-input-combination)
- [CLI: component supply](../specs/cli.md#contract-component-supply)
- [CLI: input combination](../specs/cli.md#contract-input-combination)
- [Package-manager evidence lessons](../specs/packagemanager.md#lessons-learned)
- [`DependencyInventoryCombiner`](../../../src/Ol.Core/DependencyInventoryCombiner.cs)
- [`DependencyInventory`](../../../src/Ol.Core/DependencyInventory.cs)
- [`MixedInputScanTests`](../../../tests/Ol.Tests/MixedInputScanTests.cs)
- [Component Detection comparison](../references/component_detection.md)
