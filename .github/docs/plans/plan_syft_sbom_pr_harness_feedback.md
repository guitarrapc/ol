# Syft SBOM と package-manager 入力を組み合わせる PR Harness 計画

## この文書の位置付け

Cysharp/AIApiTracer を対象に、Cysharp/Actions の reusable PR Harness へ `ol` を組み込んだ試行から得たフィードバックを記録する。言語非依存の repository SBOM、package-manager ごとの解決済み入力、allow-list、baseline をどの境界で扱うと安定するかを整理し、`ol` の文書、GitHub Actions、将来の SBOM generation support の判断材料とする。

調査日は 2026-08-18。使用した主な tool は `ol 0.9.3`、Syft `1.50.0`、CycloneDX for .NET `6.2.0` である。外部 tool の対応形式と default cataloger は変わり得るため、実装時には version を固定し、更新時に同じ coverage 比較を行う。

## 結論

PR の repository-wide license check では、**Syft などの言語非依存 SBOM generator を一つ使い、同じ revision から生成した package-manager 入力を `ol` へ併記する**構成を第一候補とする。

```text
package-manager resolve
  -> syft dir:. -o cyclonedx-json=<temp>/sbom.cdx.json
  -> ol scan --input <temp>/sbom.cdx.json --input . [--input <temp>/cargo-metadata.json] --format json
  -> ol check --report <temp>/ol-report.json --allow-licenses ... --baseline ol-baseline.json
```

`SBOM + .` は簡潔な default になるが、`.` は無条件に安全な監査範囲ではない。clean checkout で実行し、SBOM と report は repository 外の runner temp へ出力する。monorepo、複数の independently shipped product、既存 SBOM、古い `obj`、sample、tool が混在する repository では、scan path と resolved input を明示する。

言語別 SBOM generator を Harness が個別に組み合わせる方式は default にしない。言語が増えるたびに workflow interface と生成物の合成方法が増え、repository 全体を一つの監査 subject として扱いにくくなる。言語別 generator は release artifact 固有の SBOM や、Syft が十分な graph を作れない ecosystem の補助手段として残す。

## 試行対象

AIApiTracer は一つの shipped application に .NET と npm が含まれる repository である。

- `.NET`: `AIApiTracer.slnx`、application project、test project
- `npm`: Tailwind CSS build 用の `src/AIApiTracer/package-lock.json`
- PR policy: Cysharp の既存 allow-list に、実際の transitive dependency が必要とする `0BSD` と `BlueOak-1.0.0` を reviewed addition として追加
- baseline: unresolved evidence だけを対象とし、resolved but disallowed license は含めない

最終の combined report は 102 components を持ち、そのうち root 以外の 101 components が policy evaluation 対象になった。内訳は NuGet 22、npm 79 で、101 components はすべて `matched` だった。package metadata、source repository、declared GitHub file collector の fetch error は 0 だった。

```text
Acknowledged by baseline: 0 components.
License check passed: 101 components satisfy the allow-list.
```

baseline は次の空 snapshot になった。空であっても、CI が baseline path の存在を必須にすることで、各 repository が policy onboarding を完了したことと、将来の unresolved component を自動承認しないことを明示できる。

```json
{
  "schemaVersion": 1,
  "tool": {
    "version": "0.9.3.0"
  },
  "spdx": {
    "licenseListVersion": "e4c1f27"
  },
  "acknowledged": []
}
```

## 比較で分かったこと

### 言語非依存 SBOM には Syft が適する

Syft の directory scan は一つの command で repository を再帰走査し、npm、Cargo、NuGet など複数 ecosystem の cataloger を同じ SBOM generation boundary で実行できる。CycloneDX JSON または SPDX JSON を出力できるため、`ol` の既存 SBOM parser と分離したまま利用できる。

この構成では Harness が「repository の言語一覧」を完全に知る必要がない。npm lockfile や Cargo lockfile は Syft が発見し、Syft が直接扱わない resolver output は `ol` の package-manager parser が補う。新しい ecosystem の追加も、まず Syft と `ol` の既存 capability の組み合わせで評価できる。

Syft 自体を `ol` core へ埋め込む必要はない。generator version、cataloger selection、scan root は CI boundary に残し、`ol` は標準 SBOM と resolved input の解析に集中する。

### Syft SBOM 単独では .NET の resolved graph を十分に表せない

試行時の Syft は `project.assets.json` を読まず、.NET について主に `packages.lock.json` または build 後の `*.deps.json` を使った。一方、`ol` は `project.assets.json` を直接読める。

したがって .NET repository では次を default とする。

1. `dotnet restore <solution-or-project>` で current `project.assets.json` を生成する。
2. Syft は source repository を走査する。
3. `ol scan` に Syft SBOM と restore 済み repository path を同時指定する。

これにより Syft の言語横断 inventory と、NuGet resolver が実際に選択した graph を union できる。Syft が将来 `project.assets.json` に対応しても、両入力の component count と `suppliedBy` を比較してから companion input を省略する。

### `dotnet build` 後の repository 全体を Syft で走査すると noise が増える

`dotnet build` 後に exclusions なしで Syft を走査した試行では、342 components、52 unique PURL になった。NuGet components が 316 entries に膨らみ、`*.deps.json`、DLL metadata、Debug/Release や local IDE output 由来の重複・別 version identity が混在した。GitHub Actions も 13 components 入り、npm は dev dependency を有効化していなかったため root しか出なかった。

これは license inventory の coverage 増加ではなく、同じ package の複数 build representation と assembly version を package version のように扱う noise を含む。source repository の PR check では次を避ける。

- SBOM generation のためだけに `dotnet build` しない。
- `.vs/**`、`**/bin/**`、`**/obj/**` を Syft source scan から除外する。
- .NET graph は `ol` が current `project.assets.json` から取得する。
- binary/release artifact を監査する場合は repository scan と別 subject、別 report にする。

### 一時生成した `packages.lock.json` は ProjectReference を誤認し得る

`dotnet restore --use-lock-file` を solution 全体へ実行して Syft に読ませる試行では、test project の `ProjectReference` が version `UNKNOWN` の `pkg:nuget/aiapitracer` として SBOM に入った。`ol check` から見ると通常の unversioned NuGet component であり、正しく `unknown` violation になった。

これは review 済み third-party unresolved dependency ではないため、baseline に固定してはいけない。Syft の `dotnet.exclude-project-references` は試行 version では `packages.lock.json` cataloger のこの結果を除かなかった。

PR Harness の default では、SBOM generation のためだけに solution-wide `packages.lock.json` を一時生成しない。repository が lockfile を正規の reproducibility artifact として commit している場合は、ProjectReference の表現と component identity を個別に確認する。

### npm development dependencies は明示的に含める

AIApiTracer の npm dependencies は Tailwind build 用の `devDependencies` だった。Syft source scan では `SYFT_JAVASCRIPT_INCLUDE_DEV_DEPENDENCIES=true` を指定しないと、lockfile root だけになり、実際の transitive graph を取りこぼした。

development tool も PR や release build で実行される third-party code であるため、repository-wide compliance scan では inventory へ含める。license policy を緩める必要がある場合だけ、resolver data が development-only と証明した component に `--allow-dev-licenses` を適用する。inventory から dev dependencies 自体を消すことと、dev-only policy を分ける。

### GitHub Actions components は監査 scope を明示する

Syft は workflow と local action references から `pkg:github/...` を生成する。AIApiTracer の試行では 13 components が検出された。application/library dependency の license check と CI supply-chain review は subject が異なるため、今回の PR Harness では次の Syft cataloger を除外した。

```text
github-action-workflow-usage-cataloger
github-actions-usage-cataloger
```

GitHub Actions は既存の dependency-review と protected-workflow check が別に扱う。組織として GitHub Actions の license も同じ policy で評価すると決めた場合は cataloger を戻し、`ol` の GitHub component resolution または明示的な policy scope を先に定義する。単に unresolved が多いという理由で baseline に入れない。

### allow-list と baseline の役割を混ぜない

baseline 前の AIApiTracer check では、次の resolved license が現行 allow-list にないため 4 violations になった。

| License | Packages |
|---|---|
| `BlueOak-1.0.0` | `tar`, `chownr`, `yallist` |
| `0BSD` | `tslib` |

これらは license が確定しているため baseline eligible ではない。dependency path を確認し、policy owner が AIApiTracer の allow-list へ追加した。その後に baseline を生成すると `acknowledged` は空になり、steady-state check が成功した。

CI は `--update-baseline` を実行しない。新しい unresolved evidence は PR を失敗させ、review 後に repository owner が完全 snapshot を更新する。

## 推奨する PR Harness interface

reusable workflow は任意 shell command を受け取るより、監査 boundary を表す typed input を受け取る。

| Input | Purpose |
|---|---|
| `license-check` | repository ごとの opt-in |
| `license-allow-licenses` | repository owner が承認した SPDX allow-list |
| `license-allow-dev-licenses` | resolver が dev-only と証明した場合だけ使う追加 allow-list |
| `license-baseline-path` | commit 済み baseline。enabled 時は存在必須 |
| `license-scan-path` | Syft と package-manager directory scan の subject |
| `license-dotnet-path` | `dotnet restore` する solution/project。空なら無効 |
| `license-cargo-manifest-path` | locked Cargo metadata を生成する manifest。空なら無効 |

生の `dotnet-command` や `cargo-command` を input にしない。path から Harness が固定 command を組み立てることで、workflow expression injection と repository ごとの command drift を減らせる。特殊な build configuration が compliance meaning を変える repository は、generic Harness の command string を拡張する前に dedicated prepare workflow または明示的 resolved-input artifact を検討する。

job の順序は次に固定する。

1. External PR が protected workflow/action を変更していないことを確認する。
2. checkout する。
3. enabled ecosystem の resolved input を生成する。
4. Syft を version pin して source SBOM を runner temp に生成する。
5. `OL_GITHUB_TOKEN` を scan process の environment だけに設定する。
6. canonical JSON report を runner temp に保存する。
7. SBOM と report を artifact として check 前に保存する。
8. commit 済み baseline で `ol check` を実行する。

## `ol` へのフィードバックと実装候補

### Phase 1: documentation と skill を Syft-first にする

- [ ] Repository-wide / polyglot SBOM の最初の例を Syft にする。
- [ ] `syft dir:. -o cyclonedx-json=...` と `ol scan --input <sbom> --input .` の combined example を追加する。
- [ ] .NET では Syft が `project.assets.json` を読まない version があり、`dotnet restore` + `ol --input <assets-containing-path>` が必要なことを記載する。
- [ ] npm dev dependency、GitHub Actions cataloger、`bin/obj/.vs` exclusions の推奨を記載する。
- [ ] Source repository と built artifact は別 audit subject であり、同じ directory scan に混在させないことを記載する。
- [ ] `.` に既存 SBOMや unrelated resolved inputs がある場合、`ol` の one-SBOM-per-collection 制約や scope mismatch が起きることを記載する。

### Phase 2: reusable GitHub Action / workflow example を用意する

- [ ] Syft download、generator version pin、cataloger selection、temp output を含む reference workflow を追加する。
- [ ] .NET path と Cargo manifest path から固定 resolver command を生成する。
- [ ] allow-list と baseline path を caller-owned input にし、baseline の自動更新を禁止する。
- [ ] canonical JSON report と upstream SBOM を artifact 保存する。
- [ ] collector health と component/status/ecosystem count を job summary に出す。
- [ ] GitHub Actions dependency を application dependency と同じ scan に含めるか、明示的な scope option/example を用意する。

### Phase 3: coverage mismatch の診断を短くする

- [ ] Combined scan summary で input ごとの inventory count と、`suppliedBy` の SBOM-only / resolver-only / both count を表示できるか検討する。
- [ ] 同じ name/ecosystem に複数の version-like identity が大量にある場合、binary/source scope mixing の診断 hint を出せるか検討する。
- [ ] Unversioned PURL が SBOM のどの generator/cataloger/property から来たか、canonical report だけで追跡しやすい provenance 表示を検討する。
- [ ] Directory input に複数 SBOM、古い resolver output、unrelated subtree が含まれた場合の diagnostics を改善する。

これらは parser が upstream fact を勝手に修正する提案ではない。Syft が ProjectReference を NuGet package として出した場合、`ol` が名前だけから internal project と推測して除外すると fail-open になる。`ol` は upstream identity を保持し、generator configuration または scan scope の問題として説明しやすくする。

### Phase 4: optional SBOM generation wrapper を評価する

- [ ] Core scan semantics とは別 package/action として Syft wrapper を prototype する。
- [ ] Syft version、source name/version、cataloger selection、exclusions を生成 metadata と report artifact に残す。
- [ ] Wrapper なしの standard SBOM input を常に支援し、Syft を `ol scan` の必須 runtime dependency にしない。
- [ ] Syft update 時に AIApiTracer 相当の polyglot fixture で component count、PURL identity、dependency relationship の regression を比較する。

## 完了条件

1. Repository-wide CI の primary example が、一つの言語非依存 SBOMと aligned package-manager inputs を同じ `ol scan` に渡す。
2. .NET、npm、Cargo の各 input preparation と inclusion scope が caller から明示できる。
3. Source scan は local build/IDE output と CI workflow dependenciesを意図せず application inventory に混ぜない。
4. Resolved but disallowed license は allow-list reviewへ進み、baseline は reviewed unresolved evidence だけを保持する。
5. Canonical JSON report と upstream SBOM が CI artifact として残り、coverage と collector failure を後から確認できる。
6. SBOM generator の変更で component identity または coverage が変わったとき、baseline 更新前に差分を説明できる。

## 非目標

- Syft を `ol` core の必須 dependency にしない。
- 全 ecosystem の build/restore command を一つの汎用 shell input で表現しない。
- Source repository scan と release binary/container scan を一つの report に混ぜない。
- GitHub Actions dependency を議論なしに application dependency と同一視しない。
- SBOM generator の誤った component identity を、名前による推測で `ol` が黙って修正しない。
- Empty baseline を unresolved dependency の包括的な許可として扱わない。

## 参照資料

- [Syft: Package Catalogers](https://oss.anchore.com/docs/guides/sbom/catalogers/)
- [Syft: Supported Scan Targets](https://oss.anchore.com/docs/guides/sbom/scan-targets/)
- [Syft: .NET capabilities](https://oss.anchore.com/docs/capabilities/dotnet/)
- [Syft: JavaScript capabilities](https://oss.anchore.com/docs/capabilities/javascript/)
- [Syft: Rust capabilities](https://oss.anchore.com/docs/capabilities/rust/)
- [Anchore SBOM Action](https://github.com/anchore/sbom-action)
