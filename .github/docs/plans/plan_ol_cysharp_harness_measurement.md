# Cysharp 8 リポジトリでの ol PR Harness 実測と改善計画

## この文書の位置付け

[plan_syft_sbom_pr_harness_feedback.md](plan_syft_sbom_pr_harness_feedback.md) が AIApiTracer 1 件の試行から導いた構成を、Cysharp の 8 リポジトリで実測して検証した記録である。前計画の結論のうち何が支持され、何が反証されたかを示し、Cysharp/Actions の `pr-harness.yaml`（branch `ol`, commit bb407a4）の不具合と、`ol` 側の改善候補を、測定値付きで整理する。

調査日は 2026-08-19。使用した tool は `ol 0.9.3`、Syft `1.50.0`（CI の pin と同一）、`dotnet 10.0.301`、`cargo 1.97.1`。対象は各リポジトリの `HEAD` を `git worktree` で clean checkout したもので、CI と同じく `dotnet restore <solution>`、`cargo metadata --format-version 1 --locked`、`OL_GITHUB_TOKEN` 設定済み、Syft は CI と同じ exclude / cataloger 選択を用いた。同一入力に対する `ol scan` の canonical JSON は再実行しても byte 単位で一致することを確認している。

対象は AIApiTracer、csbindgen、DFrame、LogicLooper、MagicOnion、NativeCompressions、UniTask、ZLinq の 8 件。

## 結論

1. **現状の harness は 8 リポジトリすべてで失敗する**（`ol check` exit 2、合計 451 violations）。onboarding 手順が欠けているのであって、`ol` が壊れているのではない。
2. **Syft SBOM は 129 個の phantom component と 108 件の violation を追加し、`ol` 単独が解決できなかった component を 1 つも解決しなかった。** 原因は Syft の binary cataloger であり、cataloger 選択の修正で phantom は 129 → 4 に減る。
3. **残る 343 violations の大半は legacy .NET NuGet corpus**（`System.*` / `runtime.*` / 旧 xunit）で、`licenseUrl` が非ライセンス文書を指すため原理的に解決しない。prefix 除外では分離できず、baseline が唯一の正しい機構である。
4. **129 entries / 76 KB の共有 baseline 1 個 + allow-list への 8 identifier 追加で、8 リポジトリすべてが exit 0 に到達する。** baseline はリポジトリ間で再利用でき、組織で 1 ファイルを共有できる。
5. **`ol` は実用に足る。** 実用足る使い方は「Syft を第一入力にする」ではなく「`ol scan --input .` を主入力にし、Syft は `ol` が adapter を持たない ecosystem の補完として、binary cataloger を切って使う」である。

## 前計画のどれが支持され、どれが反証されたか

| 前計画の主張 | 実測 |
|---|---|
| `dotnet build` 後の Syft scan は noise が増える | **支持**。ただし build しなくても Unity `Assets/Plugins` などの commit 済み DLL から同じ noise が出るため、`bin`/`obj` 除外では防げない |
| npm dev dependency は inventory に含める | **支持**。ただし SBOM を併記すると `--allow-dev-licenses` が機能しなくなる |
| GitHub Actions cataloger を除外する | **部分的に支持**。真の noise 源は github ではなく binary cataloger だった |
| allow-list と baseline の役割を分ける | **支持**。8 リポジトリで例外なく成立した |
| Syft を第一候補にする | **反証**。Cysharp の 8 リポジトリで Syft の限界効用は負だった |
| `.` に既存 SBOM があると one-SBOM 制約に当たる | **誤り**。directory discovery は SBOM を発見しない（[DependencyInputRegistry.cs:112](../../../src/Ol.Core/DependencyInputRegistry.cs:112) の SBOM handler は `DirectoryFileNames` を登録していない） |
| baseline を CI で必須にする | **反証**。安全性に寄与せず、`--baseline` 未指定でも unresolved は fail する |
| Phase 3 の `suppliedBy` 内訳表示は「検討する」 | **最優先で必要**。この内訳を出していれば上記の反証は試行時点で見えていた |

## 測定 1: 現状の harness を 8 リポジトリに当てる

Cysharp の既定 allow-list（`Apache-2.0, BSD-2-Clause, BSD-3-Clause, ISC, MIT, MPL-2.0`）、baseline なし。

| Repository | inventory | Syft 既定 | Syft `declared` | Syft `declared,-binary` | SBOM なし |
|---|---:|---:|---:|---:|---:|
| AIApiTracer | 100 | 4 | 4 | 4 | 4 |
| csbindgen | 13 | 2 | 2 | 1 | 1 |
| DFrame | 189 | 113 | 84 | 83 | 83 |
| LogicLooper | 50 | 11 | 11 | 11 | 11 |
| MagicOnion | 1590 | 208 | 143 | 143 | 143 |
| NativeCompressions | 166 | 16 | 15 | 7 | 4 |
| UniTask | 141 | 94 | 94 | 94 | 94 |
| ZLinq | 108 | 3 | 3 | 3 | 3 |
| **合計** | **2357** | **451** | **356** | **346** | **343** |

`inventory` は `ol scan --input .`（+ Cargo metadata）単独の component 数。violations 列は `ol check` の件数。

## 測定 2: Syft の限界効用

`suppliedBy` を 3 分割して数えた。SBOM のみが供給した非 root component は、Syft 既定設定で 8 リポジトリ合計 **129 個**。そのうち `matched` は 20 個で、いずれも `ol` が別経路で既に持っていた component の重複である。

**SBOM を加えて violation が減ったリポジトリは 1 つもない。** 増分は 0 / +1 / +30 / 0 / +65 / +12 / 0 / 0 = **+108**。

129 個の内訳は次の 5 種類で、いずれも依存パッケージではない。

| 出所 | 例 | 件数 |
|---|---|---:|
| `pe-binary-package-cataloger`（commit 済み .NET DLL の assembly version） | `pkg:nuget/Grpc.Core.Api@2.65.0.0`（実体は `Grpc.Core.Api@2.65.0`） | DFrame 34, MagicOnion 79 |
| `binary-classifier-cataloger`（commit 済み実行ファイル） | `Apache HTTP Server@2.4.46`（実体は DFrame の `tools/ab.exe`） | 1 |
| binary cataloger が path を名前にした artifact | `\unity-sandbox\Assets\csbindgen_tests@UNKNOWN`、`\src\...\native\libzstd@UNKNOWN` | csbindgen 1, NativeCompressions 10 |
| `rust-cargo-lock-cataloger` の workspace member | `liblz4@0.1.0`, `libopenzl@0.1.0`, `libzstd@0.1.0`（リポジトリ自身の crate） | 3 |
| npm lockfile root の重複 | `pkg:npm/aiapitracer@1.0.0` | 1 |

これは [packagemanager.md:355](../specs/packagemanager.md:355) が 18 リポジトリ・672 件で既に記録している現象と同一である。新しいのは、`dotnet build` しなくても Unity の `Assets/Plugins` や `tools/` に commit された binary から同じものが出るため、`bin`/`obj` の exclusion では防げないという点だけである。

### 修正: cataloger を絞れば phantom は 129 → 4 になる

```text
--override-default-catalogers 'declared' --select-catalogers '-github-actions,-binary'
```

`--override-default-catalogers 'declared'` だけでは足りない。Syft 1.50.0 では `declared` セットに `binary-classifier-cataloger` / `pe-binary-package-cataloger` / `elf-binary-package-cataloger` が含まれるため、`-binary` の明示除外が要る（cataloger 数 55 → 32）。この設定で violations は 451 → 346、SBOM 固有 component は 129 → 4 になる。残る 4 は NativeCompressions の workspace crate 3 個と AIApiTracer の npm root 1 個である。

### purl を持たない component は `--exclude-packages` で除去できない

上記 phantom のうち `Apache HTTP Server`、path 名の artifact、workspace crate は purl を持たない。[cli.md](../specs/cli.md) の `contract-purl-prefix` が定めるとおり purl のない component は prefix に一致しないため、実測でも除去できなかった。

```text
$ ol check --report combined.json --allow-licenses MIT,Apache-2.0 --exclude-packages "pkg:nuget/,pkg:npm/" --verbose
Exclusion prefix pkg:nuget/ matched 13 components.
Exclusion prefix pkg:npm/ matched 0 components.
License check failed: 1 violation.
\unity-sandbox\Assets\csbindgen_tests  UNKNOWN  -  -  unknown  license is unresolved  -
```

policy 側の逃げ道が存在しないので、generator 側で出さないことが唯一の対処になる。

### Cargo だけは Syft に固有の価値がある。ただし evidence は浅い

NativeCompressions で Syft は `Cargo.lock` から 36 crates を出し、`cargo metadata` なしで 36/36 が `matched` になった。CI から Rust toolchain を外せる点は本物の利点である。

ただし `cargo metadata` を渡した場合は 35/36 で、残る 1 件が `minimal-lexical@0.2.1` の **conflict** だった。Cargo.toml と crates.io は `MIT/Apache-2.0` と宣言しているのに、restore した artifact の license file は `BSD-3-Clause` である。これは人間が見るべき本物の食い違いで、Syft 単独では見えない。

差は `metadata.packageArtifacts` に現れる。`cargo metadata` あり 244 targets / 149 documents、Syft のみ 208 / 140。差の 36 はちょうど cargo crates で、[packagemanager.md:80](../specs/packagemanager.md:80) が定めるとおり **artifact restore は「resolver が記録した物理的な install 位置」を要求するため、SBOM 供給の component には効かない**。

つまり `36/36 matched` は「より良い答え」ではなく「より少ない証拠から出した答え」である。SBOM は resolved input の代替にならない。

## 測定 3: SBOM を併記すると `--allow-dev-licenses` が無効化される

AIApiTracer で `usage=development` と判定された component 数:

| 入力 | development |
|---|---:|
| `ol scan --input .`（lockfile のみ） | **78** |
| `ol scan --input <syft-sbom> --input .` | **1** |

cataloger を `declared,-binary` に絞っても 1 のままだった。npm cataloger は `declared` なので SBOM は同じ lockfile を列挙し続け、[packagemanager.md:178](../specs/packagemanager.md:178) の「SBOM inputs leave usage unknown」と「any runtime or unknown occurrence wins」により、fold のたびに dev 判定が消える。仕様どおりの fail-closed であって bug ではないが、結果として **推奨構成が `--allow-dev-licenses` を機能しなくする**。

前計画が AIApiTracer で `0BSD` と `BlueOak-1.0.0` を dev policy ではなく主 allow-list に入れたのは、この相互作用に押し出された結果と考えられる。Cysharp/Actions が `license-allow-dev-licenses` を input として公開している以上、この組み合わせは文書化されるか、設計として解消される必要がある。

## 測定 4: 残る 343 violations の正体

SBOM を外した状態の violation を名前で分類すると、.NET リポジトリでは 8〜9 割が同じ母集団に落ちる。

| Repository | `System.*` | `runtime.*` | `Microsoft.*` | `xunit*` | その他 |
|---|---:|---:|---:|---:|---:|
| DFrame | 55 | 16 | 4 | 6 | 2 |
| MagicOnion | 51 | 16 | 4 | 0 | 72 |
| UniTask | 64 | 16 | 5 | 7 | 2 |
| LogicLooper | 0 | 0 | 4 | 7 | 0 |
| ZLinq | 0 | 0 | 3 | 0 | 0 |

これらは netstandard2.0 世代の NuGet package で、registry の `license` が空、`licenseUrl` が `http://go.microsoft.com/fwlink/?LinkId=329770`、repository が `https://dot.net/` である。[packagemanager.md:350](../specs/packagemanager.md:350) が既に測定しているとおり、この URL の先は「本文書は参考情報であり、それ自体はライセンスではない」と始まるページで、パッケージ自身の宣言からは SPDX identifier に到達できない。`ol` の `declared_license_location_not_collected` は正しく、かつ完全な答えである。

### prefix 除外はこの母集団を分離できない

`--exclude-packages` が巻き添えにする `matched` component を数えた。

| Repository | prefix | 未解決を除去 | 巻き添えの matched |
|---|---|---:|---:|
| MagicOnion | `pkg:nuget/Microsoft.` | 4 | **119** |
| DFrame | `pkg:nuget/Microsoft.` | 4 | **56** |
| ZLinq | `pkg:nuget/Microsoft.` | 3 | **39** |
| MagicOnion | `pkg:nuget/System.` | 51 | **39** |
| UniTask | `pkg:nuget/System.` | 64 | 22 |
| DFrame | `pkg:nuget/runtime.` | 16 | 0 |

`runtime.` 以外は分離できない。したがってこの母集団に対する正しい機構は baseline であり、それは `ol` の設計どおりである。

## 測定 5: 到達可能な steady state

8 リポジトリの baseline を生成して統合し、1 ファイルにまとめた。**129 entries、76 KB。**

このファイル 1 個を全リポジトリに適用すると、既定 allow-list のままで 5/8 が exit 0 になり、残る 3 件は解決済みだが allow-list にない license だけになった。

| License | 件数 | 主な出所 |
|---|---:|---|
| `MIT-0` | 62 | MagicOnion docs (Docusaurus) |
| `BlueOak-1.0.0` | 4 | `tar`, `chownr`, `yallist` |
| `0BSD` | 3 | `tslib` |
| `CC0-1.0` | 2 | |
| `Unlicense` / `Python-2.0` / `CC-BY-4.0` | 各 1 | |
| `(MIT OR Apache-2.0) AND Unicode-3.0` | 1 | NativeCompressions |

この 8 identifier を allow-list に追加すると、**8 リポジトリすべてが exit 0**（Syft あり構成・Syft なし構成の両方で確認）。

### baseline はリポジトリ間で共有できる

- baseline の `fingerprint` は status と evidence のハッシュで、identity（ecosystem/name/version/purl）とは別フィールドである。したがって同じ component は別リポジトリでも同じ entry になる。
- report に存在しない entry を含む baseline はエラーにならない。LogicLooper の 11 entry baseline を csbindgen に適用して `Acknowledged by baseline: 1 component.` / exit 0 を確認した。

つまり **Cysharp/Actions に共有 baseline を 1 個置き、リポジトリ側は原則ファイルを持たない**運用が今日の `ol` で成立する。「空なら空にしたい、作るのを求めたくない」という要件はこの形で満たせる。

ただし統合 129 entries には `minimal-lexical` の conflict が 1 件含まれている。`--update-baseline` は conflict も acknowledge するため、**onboarding の一括生成は本物の食い違いを埋めうる**。共有 baseline を作る作業は、生成コマンドを流すことではなく 129 件を読むことである。

## 測定 6: CI での実行コスト

MagicOnion（1670 components）を cache 完全空で 1 回スキャンした。

| 指標 | 値 |
|---|---|
| 実時間 | **99 秒** |
| package metadata 取得 | 1669 miss / 0 error |
| package artifact | 1330 targets / 456 documents |
| **GitHub License API** | **821 requests**（うち 766 が `unknown` 回答） |
| declared GitHub file | 2 requests |

timeout 10 分には収まる。問題は GitHub API で、Actions の `GITHUB_TOKEN` は **1 リポジトリあたり毎時 1,000 requests**（Enterprise Cloud は 15,000）である。cold cache の MagicOnion 1 回で時間枠の 82% を使い、**同一時間内の 2 本目の PR で rate limit に到達する**。しかも 821 のうち 766 は何も答えていない。

これは 2 つの帰結を生む。evidence cache の CI 復元は最適化ではなく前提条件であること。そして rate limit で collection が落ちた run は `error` status になり `ol check` は exit **3** を返すため、exit 3 の扱いを決めていない harness は「registry 障害」と「ライセンス違反」を区別できないこと。

## Cysharp/Actions `pr-harness.yaml` の不具合

重大な順に挙げる。

### 1. license-check が必須チェックに繋がっていない

```yaml
pr-harness-check:
  needs: [prevent-githubactions-changes, dependency-review]
```

`license-check` が `needs` にない。branch protection が `pr-harness-check` を要求している場合、**license 違反があっても PR は merge できる**。gate として機能していない。

`license-check` は `if: inputs.license-check` で skip されうるので、`needs` に加えるだけでは skip 時に harness 全体が落ちる。`pr-harness-check` 側で `if: ${{ !cancelled() }}` と `needs.license-check.result` の明示判定（`skipped` と `success` を通し、それ以外を落とす）が要る。

### 2. csbindgen で `cargo metadata --locked` が失敗する

```text
error: cannot create the lock file ...\Cargo.lock because --locked was passed to prevent this
```

csbindgen は library なので `Cargo.lock` を commit していない。`run:` は `bash -e` なので step ごと失敗する。`--locked` は lockfile を commit している repository（NativeCompressions）でのみ正しい。lockfile の有無で分岐するか、`--locked` を外して「解決結果が commit された lock と一致する保証はない」ことを受け入れるかの選択が要る。

### 3. Rust ecosystem が無言で監査されない

csbindgen を `ol scan --input .` すると次のようになる。

```text
Input discovery: 2 detected files; 0 ignored candidates; 0 incomplete input sets; ecosystems nuget
```

warnings は空。Rust の依存が 1 つも監査されていないのに、baseline を置けば `License check passed` になる。`ol` の candidate 検出は `Cargo.lock` と `*.csproj` を見るが `Cargo.toml` を見ないため、lockfile を commit しない library repository では hint が発火しない。[cli.md](../specs/cli.md) が「a silently unscanned ecosystem is the failure the hint exists to prevent」と書いている失敗そのものである。

### 4. `ol` の version が固定されていない

`guitarrapc/setup-ol` の `ol-version` 既定は `latest` で、workflow は指定していない。SPDX データは CLI に bundle され version と共に動くため、`ol` の自動更新は分類と baseline fingerprint を静かに変えうる。Syft と CycloneDX を pin して `ol` を pin しないのは一貫していない。

### 5. exit code 3 の扱いがない

`ol check` は「pipeline を直す / 依存を直す / 再試行する」を区別するために `2` と `3` を分けている。測定 6 のとおり rate limit は現実的な確率で起きる。非ゼロを一律 failure にすると、この設計は捨てられる。

### 6. Syft の binary cataloger が除外されていない

測定 2 のとおり、これだけで 108 件の phantom violation が生まれる。

### 7. baseline の存在必須は安全性に寄与しない

```yaml
if [[ ! -f "$BASELINE_PATH" ]]; then
  echo "::error::ol baseline was not found: $BASELINE_PATH"
```

`--baseline` を渡さなくても unresolved component は violation として fail する。空 baseline は「渡さない」と機能的に同一で、fingerprint を持たないため失効もせず、review されないファイルとして各リポジトリに残るだけである。ファイルがあるときだけ `--baseline` を付ける形にすれば、要件（空なら置かない）と安全性が両立する。

### 8. `license-dotnet-path` が 1 つしか取れない

MagicOnion は `MagicOnion.slnx` と `samples/ChatApp/ChatApp.Server.sln`、ZLinq は `ZLinq.slnx` と `tests/System.Linq.Tests/System.Linq.Tests.slnx` を持つ。1 つしか restore しないと残りの `project.assets.json` は生成されず、その ecosystem は無言で監査対象外になる（不具合 3 と同じ false negative の形）。複数指定を許すか、restore した project 数を job summary に出す必要がある。

### 9. 監査 subject の scope が宣言されていない

`ol scan --input .` は MagicOnion で 1590 components を返すが、**そのうち 1292 は docs サイト（Docusaurus）の npm 依存**で、NuGet は 298 である。`MIT-0` 62 件もここから来る。ドキュメントサイトの依存を出荷物の license compliance と同じ report に入れるかは組織の判断であり、既定で混ぜるなら明示すべきである。

### 10. cache 復元、`--sarif`、`ol diff` がない

- `--cache-dir` を `actions/cache` に載せないと、測定 6 の 821 requests が毎 PR 発生する。
- `--sarif` を出せば dependency path 付きで PR の code scanning に出る。
- base ↔ head の `ol diff` がないため、既存 violation が全 PR を止める。段階導入の障壁はここである。

## 適用済みの修正（Cysharp/Actions, branch `ol`, 未 commit）

`D:\github\cysharp\Actions` の作業ツリーに以下を適用した。`seiton` は 0 error で通っている。commit と push はしていない。

| # | 不具合 | 適用した修正 |
|---|---|---|
| 1 | gate に繋がっていない | `pr-harness-check` に `license-check` を `needs` 追加。`if: !cancelled()` と、`success`/`skipped` 以外を落とす明示判定を入れた |
| 2 | `cargo metadata --locked` 失敗 | `Cargo.lock` が manifest の隣にあるときだけ `--locked` を付け、無いときは warning を出す |
| 4 | `ol` version 未固定 | `license-ol-version` input（既定 `0.9.3`）を追加し `setup-ol` に渡す。`license-syft-version` も同様に input 化 |
| 5 | exit 3 未処理 | exit code ごとに annotation を分け、3 は「収集失敗であり policy 違反ではない、再実行せよ」と明示 |
| 6 | binary cataloger | `--override-default-catalogers 'declared' --select-catalogers '-github-actions,-binary'` に変更 |
| 7 | baseline 必須 | ファイルがあるときだけ `--baseline` を付ける。無いときは「未解決はすべて allow-list を満たす必要がある」と表示 |
| 8 | solution 1 つのみ | `license-dotnet-path` を改行区切りにして全部 restore |
| 10 | cache / SARIF / summary | `--cache-dir` を `actions/cache` に載せ、`--sarif` を出力し、artifact 保存を check 後（`!cancelled()`）に移して SARIF も含めた。job summary に status / ecosystem / **suppliedBy 3 分割** / GitHub API 使用量を出す |

job summary の実出力例（MagicOnion の実 report に対して検証済み）。

```text
### ol license scan

- components: 1591
- status: matched 1517, conflict 0, unknown 74, ambiguous 0, invalid 0, error 0
- ecosystems: npm 1292, nuget 298, - 1
- supplied by: sbom-only 0, package-manager-only 298, both 1292
- github license api: 0 requests, 820 cache hits, 0 errors
```

`sbom-only 0` の 1 行が、この調査で最も時間のかかった問いに即答している。優先度 2 の改善が `ol` 本体に入れば、この行は workflow の jq ではなく `ol` が出すべきものになる。

### 適用していない、人間が決めるべきもの

- **不具合 3（Rust が無言で未監査）**: workflow 側では検出できない。`ol` の優先度 5 で対処するのが筋。当面は csbindgen を onboarding する前に `cargo-metadata.json` が生成されていることを目視確認する。
- **不具合 9（docs 依存の scope）**: MagicOnion の 1292 件を監査対象に含めるかは組織判断。
- **allow-list の内容**: 測定 5 の 8 identifier を Cysharp の既定に加えるかは policy owner の判断。
- **共有 baseline の作成**: 129 entries を人が読む作業。生成コマンドは通るが、`minimal-lexical` の conflict のような本物の食い違いが混ざる。

## `ol` への改善候補

### 優先度 1: `ol check` が「なぜ未解決か」を出さない

CI の operator が見るのはこれである。

```text
Package  Version  Ecosystem  Purl  License/Status  Reason  Path
Microsoft.CSharp  4.0.1  nuget  pkg:nuget/Microsoft.CSharp@4.0.1  unknown  license is unresolved  ...
```

同じ component について `ol scan --format text` はこう出す。

```text
Microsoft.CSharp 4.0.1 declared_license_location_not_collected http://go.microsoft.com/fwlink/?LinkId=329770 via ...
```

harness は `--format json` でしか scan しないので、この行は誰の目にも触れない。94 行の `license is unresolved` を見た人間に次の行動は決められない。

これは [cli.md:301](../specs/cli.md:301) が自ら書いた原則に反する。

> A fact that only the machine-readable projection carries is a fact the human never gets. ... Whenever one output answers "what do I change" and another does not, the gap is a defect in the one that does not.

dependency path は同じ理由で `check` に持ち込まれた。`REASON` と `REFERENCE` も同じ扱いを受けるべきである。report にはすでに両方あり、`check` は再収集しない。

- [ ] `check` の violation 行に、unresolved 系 status の mechanism と reference を出す。
- [ ] mechanism ごとの件数を末尾に集計する。`declared_license_location_not_collected: 86` の 1 行があれば、94 件が 1 つの母集団だと即座に分かる。

### 優先度 2: 入力ごとの寄与を summary に出す

前計画 Phase 3 の項目だが、実測すると**これが最も費用対効果の高い診断**である。`suppliedBy` の 3 分割を数えるだけで、測定 2 の結論（Syft が 129 個の phantom を足して 0 個を解決した）は試行時点で出ていた。

- [ ] scan summary（stderr / JSON `summary`）に `sbom-only` / `package-manager-only` / `both` の件数を出す。
- [ ] `--verbose` では ecosystem 別に出す。
- [ ] 片方の入力にしか現れない component が支配的な場合、scope mismatch の可能性を 1 行で述べる。

`metadata.input` は collection のとき `"sourceRef": "2 inputs"` と 1 つのハッシュしか持たないため、入力ごとの内訳は現状 component を全走査しないと得られない。

### 優先度 3: purl を持たない component の扱い

generator は package でないもの（実行ファイル、path 依存、workspace member）を purl なしの component として出す。現状これは policy 対象になり、`--exclude-packages` では除去できず、baseline に入れるしかない。名前が `\unity-sandbox\Assets\csbindgen_tests` のように OS 依存の path 断片になることもあり、Windows で生成した baseline が Linux CI で一致しない懸念もある。

`ol` が名前から推測して落とすのは fail-open なので採るべきでない。可視性で解く。

- [ ] purl を持たない component の violation に、`component_has_no_package_identity` 相当の独立した reason を与える。「registry に問えない」ことは status ではなく identity の性質であり、reviewer の行動（generator 設定を直す）が他と異なる。
- [ ] その件数を常に集計行に出す。
- [ ] 除外手段を与えるかは別途判断する。与えるなら prefix ではなく「identity のない component」という機構名で指定させる。

### 優先度 4: `--baseline` の重複指定が無言で last-wins

```text
$ ol check --report DFrame.json --baseline org.json --baseline zlinq.json
Acknowledged by baseline: 1 component.   → 82 violations

$ ol check --report DFrame.json --baseline zlinq.json --baseline org.json
Acknowledged by baseline: 83 components. → passed
```

警告もエラーも出ない。組織共有 baseline とリポジトリ固有 baseline を合成しようとした人が、順序次第で通ったり落ちたりする。[cli.md](../specs/cli.md) の「an unusable invocation ... is an explicit command failure」に従えば、単数オプションの重複は invocation error であるべきである。

- [ ] 重複指定を exit 1 にする。
- [ ] そのうえで、共有 baseline の需要（測定 5）に応えるなら `--baseline` を明示的に repeatable（union）にするかを設計判断する。fingerprint が identity と分離されている以上、union は今日でも意味論的に破綻しない。

### 優先度 5: `Cargo.toml` を candidate として検出する

`*.csproj` が「それ自体は入力ではないが restore すれば入力になる」ものとして検出されているのと同じ理由で、`Cargo.toml` も検出対象にする。lockfile を commit しない library repository では `Cargo.lock` が存在せず、現状は hint が一切出ない（不具合 3）。

- [ ] `Cargo.toml` を検出し、`cargo metadata --format-version 1 > cargo-metadata.json` を案内する。
- [ ] `--locked` を案内文に含めるかは lockfile の有無で分ける。

### 優先度 6: SBOM fold による development usage の消失を文書化する

仕様としては fail-closed で正しいが、「SBOM と lockfile を併記する」推奨構成が「`--allow-dev-licenses` を使う」推奨構成を無効化することは、どの文書にも書かれていない。

- [ ] `--allow-dev-licenses` の説明に、同じ ecosystem を SBOM も列挙している場合は usage が unknown に落ちて相殺されることを明記する。
- [ ] 併記時に「dev 判定を持っていた component が fold で unknown になった件数」を warning として出せないか検討する。これは可視性の問題であり、挙動を変える提案ではない。

### 優先度 7: skill と README の polyglot 例

`.claude/skills/license-scan/SKILL.md` は generator 非依存に書かれており、その判断は測定に照らして妥当だった。Syft を第一候補として名指ししていたら誤りになっていた。追記すべきは generator 名ではなく、generator が出す noise の種類と対処である。

- [ ] source repository を scan するときは binary cataloger を切ること、切らないと commit 済み binary の assembly version が package として現れることを、generator 中立な言い方で記す。
- [ ] resolved input が取れる ecosystem では、SBOM は evidence を増やさず、artifact restore が効かない分むしろ浅くなることを記す。
- [ ] SBOM の価値は「`ol` が adapter を持たない ecosystem」と「CI に resolver toolchain を入れたくない場合」に限られることを記す。

## 推奨する運用

### Cysharp 向けの構成

```text
1. dotnet restore <各 solution>                              # project.assets.json を作る
2. cargo metadata (lockfile があれば --locked)                # 任意
3. syft dir:. --override-default-catalogers declared \
        --select-catalogers '-github-actions,-binary' \
        -o cyclonedx-json=$RUNNER_TEMP/sbom.cdx.json         # 任意
4. ol scan --input $RUNNER_TEMP/sbom.cdx.json --input . [--input cargo-metadata.json] \
        --cache-dir $OL_CACHE --format json > $RUNNER_TEMP/ol-report.json
5. ol check --report $RUNNER_TEMP/ol-report.json \
        --allow-licenses <base + MIT-0,0BSD,BlueOak-1.0.0,CC0-1.0,Unlicense,Unicode-3.0,CC-BY-4.0,Python-2.0> \
        [--baseline <shared-baseline>] --sarif $RUNNER_TEMP/ol.sarif
```

step 3 は Cysharp の現行 8 リポジトリでは省略しても結果が変わらない。将来 `ol` が adapter を持たない ecosystem が入ったときに効くので残す価値はあるが、**第一入力は `--input .`** である。

`--input .` は 8 リポジトリすべてで問題なく機能した。fixture を意図的に多言語で置いている `ol` 自身のようなリポジトリでは成立しないが、それは product repository の形ではない。該当するリポジトリは `license-check: false` にするか `license-scan-path` を絞ればよい。

### baseline の運用

- Cysharp/Actions に共有 baseline を 1 個置く（129 entries / 76 KB で現行 8 リポジトリを充足）。
- リポジトリ側は原則ファイルを持たない。必要になったリポジトリだけ追加ファイルを持つ。
- workflow は「ファイルがあれば `--baseline` を付ける」。存在必須にしない。
- 共有 baseline の更新は人間が entry を読む作業とする。`--update-baseline` は CI で実行しない。
- 生成時に conflict が混じることを前提に、conflict entry は個別に判断する（`minimal-lexical` が実例）。

### 段階導入

1. `ol scan` + artifact 保存 + job summary のみ。`ol check` は `continue-on-error`。
2. 共有 baseline と allow-list を確定させる。
3. `ol check` を必須にし、`pr-harness-check` の `needs` に入れる。
4. `ol diff` で base ↔ head の regression 判定を足す。

## 非目標・未検証

- `ol` に Syft を組み込むことは引き続き非目標とする。測定はその判断を支持した。
- source repository scan と release artifact scan を同じ report に混ぜない。
- 本測定は Linux runner ではなく Windows 上の clean worktree で行った。path 区切りに依存する component 名（purl のない binary/UPM 由来のもの）は OS で表記が変わるため、baseline の可搬性は Linux CI で再確認が要る。cataloger を絞ればこの母集団自体がほぼ消えるので優先度は低い。
- MagicOnion の docs 依存 1292 件を監査 subject に含めるかは組織判断であり、本測定では含めたままにしている。
- `ol diff` を PR harness に組み込んだ場合の挙動は未測定。
- Syft 1.22.0 と 1.50.0 の差は未測定。本測定はすべて 1.50.0 で行った。

## 再現手順

```bash
git worktree add /tmp/wt HEAD --detach
cd /tmp/wt && dotnet restore <solution>
syft dir:/tmp/wt --exclude './.vs/**' --exclude './**/bin/**' --exclude './**/obj/**' \
  --override-default-catalogers 'declared' --select-catalogers '-github-actions,-binary' \
  -o cyclonedx-json=/tmp/sbom.cdx.json
ol scan --input /tmp/sbom.cdx.json --input /tmp/wt --format json > /tmp/report.json
ol check --report /tmp/report.json --allow-licenses "Apache-2.0,BSD-2-Clause,BSD-3-Clause,ISC,MIT,MPL-2.0"
```

Syft の限界効用は次で数える。

```bash
jq '[.components[]|select(.suppliedBy==["sbom"] and .dependency!="root")]|length' /tmp/report.json
```

SBOM なしとの比較は次で行う。

```bash
ol scan --input /tmp/wt --format json > /tmp/nosbom.json
```
