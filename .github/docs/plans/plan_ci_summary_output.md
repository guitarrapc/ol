# CI の投影を `ol` 側で持つ案 — `ol render`

## この文書の位置付け

`ol` を CI で実行したとき、artifact をダウンロードせずに Workflow の画面だけでライセンス状況を確認できるようにしたい、という要求から出た設計案を記録する。実装判断は別途行う。

満たすべきことは 2 つある。

1. **CI ログから実行結果が追えること。** 生の出力がログに残り、`::group::` で畳んでも読める形であること。
2. **`GITHUB_STEP_SUMMARY` から実行結果が確認できること。** `<details>` で適切にまとまっていること。

測定は 2026-08-20、`ol 0.9.3`（本 branch のビルド）、Cysharp の 8 リポジトリと本リポジトリの self-scan で行った。

## 何が問題か

### `--format` が直交する 2 軸を 1 個のオプションに畳んでいる

| | 何を見せるか | どう符号化するか | どこへ出すか |
|---|---|---|---|
| `--format json` | inventory + summary | JSON | stdout |
| `--format text` | inventory | プレーン | stdout |
| `--format markdown` | inventory | GFM | stdout |
| （常に） | summary | **プレーンのみ** | stderr |

帰結が 2 つある。

**「要約を markdown で」という組み合わせが、どのオプション値にも存在しない。** stderr 要約は常にプレーンテキストである。だから呼び出し側が手で組み立てるしかない。

**人間向けの出力が、連結できない 2 本のストリームに分断されている。** `--format markdown` の stdout は題名も要約もない表（`Input: ...` で始まり、unresolved があれば `## Unresolved components` が続く）で、要約は stderr のプレーンテキストにある。`2>&1` で混ぜても GFM の後にプレーン行が続いて潰れる。

### 今日の推奨経路は、ログに何も残さない

`check` に食わせる JSON を得る経路、すなわち `ol scan --format json` は、**stderr が 0 バイトになる**（self-scan で確認）。CI ログにはスキャンした痕跡すら残らない。要件 1 は現状まったく満たされていない。

`--format json` が stderr 要約を抑制するのは [cli.md](../specs/cli.md) の契約であり、それ自体は正しい（文書が要約を持つので重複を出さない）。問題は、その文書を**人間向けに投影する手段が `ol` に無い**ことである。

### だから呼び出し側が `ol` の出力形式を知る羽目になる

Cysharp/Actions の PR Harness では次を workflow が担っていた。

- 人間向け要約も得るには `jq` でレポートを解析するか、`scan` を 2 回走らせるしかない。
- `jq` を使う場合、`.metadata.sourceRepository.githubLicenseRequestCount` のようなパスが workflow に埋め込まれる。schema が変われば黙って壊れる。
- `check` の違反表は TSV なので**コードフェンスが必須**、`scan --format markdown` は表として描画させるため**フェンス禁止**、`<summary>` の後は**空行必須**。この規則を利用者が毎回再発見している。

これは優先度 1 で扱った「片方の投影しか持たない事実は、もう片方の読者に永久に届かない」と同じ構図である。フェンスや `<details>` の扱いは **`ol` 自身の出力形式についての知識**であり、呼び出し側が持つべきものではない。

## 測定

### ジョブサマリーに全部載せてもサイズは問題にならない

`GITHUB_STEP_SUMMARY` は 1 step あたり 1 MiB を超えると描画されない。`scan --format markdown` の実測は次のとおり。

| Repository | components | markdown | 1 MiB 比 |
|---|---:|---:|---:|
| csbindgen | 61 | 6 KB | 0% |
| LogicLooper | 50 | 7 KB | 0% |
| AIApiTracer | 100 | 8 KB | 0% |
| ZLinq | 122 | 11 KB | 1% |
| NativeCompressions | 166 | 16 KB | 1% |
| UniTask | 141 | 42 KB | 4% |
| DFrame | 189 | 43 KB | 4% |
| MagicOnion（docs 除外） | 298 | 55 KB | 5% |
| MagicOnion（docs 込み） | 1590 | **164 KB** | **16%** |

Cysharp の範囲では余裕がある。ただし `ol` は汎用ツールであり、数万 component の利用者は上限に届きうる。

### `--format markdown` は要約ではない

| 出力先 | 内容 | 規模（MagicOnion, docs 除外） |
|---|---|---|
| stdout | component 表 + unresolved 節 | 380 行 / 55 KB |
| stderr | 要約（counters、`Supplied by`、`Input discovery`） | 12 行 |

「markdown をもう 1 回実行して貼る」だけでは、**55 KB の表が載って要約は得られない**。要約は stderr にある。

### 2 回目のスキャンのコスト

cache 温、MagicOnion 298 components で **約 1.5 秒**（json 1450 ms / text 1428 ms）。許容範囲だが、1 回で済むなら不要な支出である。

### canonical JSON は stderr 要約の上位集合になっていない

self-scan の [ol.json](../../../sandbox/self/ol.json) を stderr 要約と突き合わせた結果、**`Input discovery:` 行の 3 値が JSON に無い**。

| stderr 要約の項目 | JSON の対応 |
|---|---|
| License results | `summary.*` ✓ |
| Findings | 各 component の `warnings` から導出可 ✓ |
| Supplied by | `summary.supply` ✓ |
| External evidence | `metadata.collection.externalEvidence` ✓ |
| Package artifacts / Declared GitHub files / Package metadata / Source repositories | `metadata.*` ✓ |
| Run（concurrency / retries / auth） | `metadata.packageMetadata.*`, `metadata.network.githubAuth` ✓ |
| Input discovery — detected file count | **無し** |
| Input discovery — ignored candidates（件数と名前） | **無し** |
| Input discovery — incomplete input sets | **無し** |
| Input discovery — excluded input paths | `metadata.inputScope` ✓ |
| Input discovery — ecosystems | component から導出可 ✓ |
| Input | `metadata.input.*`, `metadata.spdx.*` ✓ |

[cli.md](../specs/cli.md#contract-output-formats) は JSON の stderr 要約免除について次を約束している。

> That exemption holds only while the document states everything the stderr summary states

守られていない。しかも欠けている 3 値は、spec 自身が「a silently unscanned ecosystem is the failure the hint exists to prevent」と呼んでいる事実そのものである。**`--format json` で走らせた CI は、ecosystem が丸ごとスキャンされなかったことをレポートから知りようがない。**

これは本案と独立に存在する契約違反だが、本案の前提でもある（後述）。

### 表・unresolved 節は JSON から完全に導出できる

`license` / `status` / `dependency` / `suppliedBy` は component に、`declaredLicenseReferenceKind` / `declaredLicenseReference` は `licenseCandidates[].evidence` にある。`check` が「re-derives nothing — the mechanism follows from the evidence the persisted report already carries」として mechanism を再導出しているのが既存の前例である。

## 提案: `ol render`

```text
ol render --report <scan.json> [--format text|markdown]
```

`check` と `diff` は既に「永続化された JSON を読んで投影する」コマンドである。**この族に三人目が欠けている**から、`scan` に出力先を生やす話になっていた。

`render` は収集も SPDX ロードもキャッシュもネットワークも行わない。`check` と同じ純粋さを持ち、同じ理由で決定的である（生成時刻を書かない、という要件が自動的に満たされる）。

### `scan` は変わらない

`--format text|markdown|json` も、stdout / stderr の分け方も、`--quiet` も現状維持。`ol scan --input .` の一行体験には手を入れない。

`scan --format markdown` と `render --format markdown` が別の形になるのは、意図した非対称である。

**`scan` の stdout は「結果」、stderr は「その走行についての注釈」である。走行しているから 2 本ある。`render` は走行していないので注釈の行き先がなく、両方を 1 本の文書に合成する。**

### 分岐を防ぐ不変条件

> `render --format X` の中に現れる表は、`scan --format X` の stdout と**バイト単位で同一**である。`render` が足すのは、題名と、断片をつなぐ見出し・`<details>` だけ。要約ブロックは `scan` の stderr と**同じ文**を、その形式に再符号化したものである。

レンダラーは 1 本のままで、`render` は合成器になる。`render` の出力が `scan --format markdown` の stdout を verbatim に含むことを assert できるので、分岐はテストで止まる。

### `check --format text|markdown`

`check` の結果は policy を含むので scan report からは導出できず、`render` の入力にできない。format 軸だけの追加なので排他で問題なく、SARIF は既にファイルオプションなので stdout の TSV と競合しない。TSV は既定のまま残す。

これで「違反を本物の markdown 表にできる」が満たせる。

## 出力例

self-scan（`sandbox/self/ol.cdx.json`、42 components、38 matched、4 unknown）の実測をもとにする。

### 今日 — `ol scan --format text`

stdout（45 行）:

```text
Input: sbom/cyclonedx

NAME VERSION LICENSE ECOSYSTEM DEPENDENCY STATUS SUPPLIED
Ol 0.0.0 - - root unknown sbom
BenchmarkDotNet 0.15.8 MIT nuget direct matched sbom
...
```

stderr（7 行）:

```text
Scan summary
  License results: 42 displayed components; 38 matched; 0 conflict; 4 unknown; 0 ambiguous; 0 invalid; 0 error
  Findings: 0 warnings on unresolved components; 0 on resolved components; 0 deprecated SPDX identifiers
  Supplied by: 42 sbom only; 0 package-manager only; 0 both
  External evidence: not collected; package registries, source repositories, and their caches were not read (--no-external-evidence)
  Input discovery: 1 detected file; 0 ignored candidates; 0 incomplete input sets; 0 excluded input paths; ecosystems nuget
  Input: ol.cdx.json; input format CycloneDX; SPDX e4c1f27 (bundled)
```

どちらも単体では `GITHUB_STEP_SUMMARY` に流せない。

### `ol render --format text`（stdout 1 本）

```text
License scan

  License results: 42 displayed components; 38 matched; 0 conflict; 4 unknown; 0 ambiguous; 0 invalid; 0 error
  Findings: 0 warnings on unresolved components; 0 on resolved components; 0 deprecated SPDX identifiers
  Supplied by: 42 sbom only; 0 package-manager only; 0 both
  External evidence: not collected; package registries, source repositories, and their caches were not read (--no-external-evidence)
  Input discovery: 1 detected file; 0 ignored candidates; 0 incomplete input sets; 0 excluded input paths; ecosystems nuget
  Input: ol.cdx.json; input format CycloneDX; SPDX e4c1f27 (bundled)

Components

NAME VERSION LICENSE ECOSYSTEM DEPENDENCY STATUS SUPPLIED
Ol 0.0.0 - - root unknown sbom
BenchmarkDotNet 0.15.8 MIT nuget direct matched sbom
...
```

要約 6 行は stderr と一字一句同じ。これが `::group::` で畳める単位になる。

要約が先頭に来るのは意図的である。stderr の要約が最後に出るのは走行の副産物（終わったときに書くから）であり、文書としては結論が先に来る。

### `ol render --format markdown`（生ソース）

```markdown
## License scan

- **License results:** 42 displayed components; 38 matched; 0 conflict; 4 unknown; 0 ambiguous; 0 invalid; 0 error
- **Findings:** 0 warnings on unresolved components; 0 on resolved components; 0 deprecated SPDX identifiers
- **Supplied by:** 42 sbom only; 0 package-manager only; 0 both
- **External evidence:** not collected; package registries, source repositories, and their caches were not read (`--no-external-evidence`)
- **Input discovery:** 1 detected file; 0 ignored candidates; 0 incomplete input sets; 0 excluded input paths; ecosystems nuget
- **Input:** `ol.cdx.json`; input format CycloneDX; SPDX e4c1f27 (bundled)

<details><summary>Full inventory (42 components)</summary>

| NAME | VERSION | LICENSE | ECOSYSTEM | DEPENDENCY | STATUS | SUPPLIED |
|---|---|---|---|---|---|---|
| Ol | 0.0.0 | - | - | root | unknown | sbom |
| BenchmarkDotNet | 0.15.8 | MIT | nuget | direct | matched | sbom |
| ... |

</details>
```

`<details>` の中身は [ol.md](../../../sandbox/self/ol.md) と同一バイト列（3,641 B）。`render` が足しているのは題名 1 行、箇条書き 6 行、`<details>` 2 行のみ。

unresolved が出る場合は、既存の `## Unresolved components` セクション（`| NAME | VERSION | REASON | REFERENCE | PATH |`）が**折らずに**要約と `<details>` の間に入る。レビュアーが実際に動く対象なので、畳むのは inventory だけである。

### `ol check --format markdown`

今日の stdout（`--allow-licenses MIT`、exit 2）:

```text
License check failed: 3 violations.

Package	Version	Ecosystem	Purl	License/Status	Reason	Mechanism	Reference	Path
CommandLineParser	2.9.1	nuget	pkg:nuget/CommandLineParser@2.9.1	unknown	license is unresolved	-	-	pkg:nuget/BenchmarkDotNet@0.15.8 > pkg:nuget/CommandLineParser@2.9.1
...

Unresolved mechanisms
  no mechanism reported: 3
```

`--format markdown`:

```markdown
## License check — failed

**3 violations.**

| Package | Version | Ecosystem | License/Status | Reason | Mechanism | Reference | Path |
|---|---|---|---|---|---|---|---|
| CommandLineParser | 2.9.1 | nuget | unknown | license is unresolved | - | - | `pkg:nuget/BenchmarkDotNet@0.15.8` > `pkg:nuget/CommandLineParser@2.9.1` |
| Microsoft.DotNet.PlatformAbstractions | 3.1.6 | nuget | unknown | license is unresolved | - | - | `pkg:nuget/BenchmarkDotNet@0.15.8` > `pkg:nuget/Microsoft.DotNet.PlatformAbstractions@3.1.6` |
| Microsoft.Testing.Extensions.CodeCoverage | 18.3.2 | nuget | unknown | license is unresolved | - | - | `pkg:nuget/TUnit@1.12.111` > `pkg:nuget/Microsoft.Testing.Extensions.CodeCoverage@18.3.2` |

**Unresolved mechanisms**

- no mechanism reported: 3
```

`Purl` 列は Package / Version / Ecosystem と重複して幅を食うので markdown では落とす。TSV は機械可読なので現状維持。

### 合わせたワークフロー

```yaml
- name: License
  run: |
    ol scan --input . --format json > report.json

    echo "::group::License inventory"
    ol render --report report.json --format text
    echo "::endgroup::"

    ol render --report report.json --format markdown >> "$GITHUB_STEP_SUMMARY"
    ol check --report report.json --allow-licenses MIT,Apache-2.0 --format markdown --sarif ol.sarif >> "$GITHUB_STEP_SUMMARY"
```

スキャンは 1 回。`jq` も、フェンス規則も、`ol` の出力形式についての知識も呼び出し側に無い。ランナー固有なのは `::group::` の 2 行だけである。

## 引く線: `ol` は文書形式を出す。ランナーのプロトコルは出さない

`<details>` は GFM であり、GitHub / GitLab / Gitea / 任意の markdown ビューアで描画される。`::group::` は Actions ランナーのプロトコルであり、他所ではログを壊す。

この線を引くと、`::group::` も `::error::` も同じ理由で `ol` の外に出る。環境変数による自動検出を却下した論理と一貫する。

`render` があれば呼び出し側の `::group::` は 2 行で済む。今それが面倒なのは、要約を見せたまま表だけ畳もうとするとストリームを分ける必要があるからで、`render` はその問題自体を消す。

## 前提として塞ぐ穴: `metadata.inputDiscovery`

`render` が stderr 要約を再現するには、JSON に次が要る。

```json
"inputDiscovery": {
  "detectedFileCount": 1,
  "ignoredCandidateCount": 0,
  "ignoredCandidates": [],
  "incompleteInputSetCount": 0
}
```

- additive なので `schemaVersion` は据え置く。`diff` の boundary 追加と同じ理由付けが使える（consumer が読まないキーの追加は既存キーの意味を変えない）。
- **古いレポートを `render` するとき、欠落を 0 と読んではならない。** `check` の `metadata.view` 読み取り規則（「a count Ol supplied is not the same claim as a count Ol defaulted」）がそのまま前例になる。フィールドが無いレポートは、その値を `-` として表示する。
- [report-privacy 契約](../specs/cli.md#contract-report-privacy)が適用される。`ignoredCandidates` は logical path で書く。

これは本案の追加コストというより、本案が露出させた既存の穴である。

## 設計要件

- **決定性。** 同じレポートから同じ出力が出ること。生成時刻を書かない。`render` が永続化文書だけを読むので自動的に満たされる。
- **[report-privacy 契約](../specs/cli.md#contract-report-privacy)。** token 値、絶対ローカルパス、隠しキャッシュパスを書かない。
- **`--sarif` と併用できること。** consumer が違う（code scanning と人間）。
- **サイズ上限は既定値を持たない。** 1 MiB は GitHub の制約であり、既定で切ると `render` の markdown を他用途でファイルに書いたときに壊れる。`--max-bytes <n>` を用意し、**`ol` は劣化のしかた（inventory を落として `N components omitted` と明記）を持ち、限界値は呼び出し側が言う**。`--sarif <file>` と同じ分担である。黙って切ることは `ol` の流儀に反する。
- **exit code は変わらない。** `render` は成功 `0`、レポートが読めない・出力できない場合 `1`。`diff` と同じ。

## 却下した案

### `scan --summary <file>` / `check --summary <file>`

stdout を変えずに GFM を指定ファイルへ追記するオプション。`check --sarif <file>` と同じ形なので process contract には反しない。当初はこれを推していた。

却下する理由は 2 つ。

**要約のレンダラーが 2 本になる。** stderr 要約と summary ファイルが並び、同じ事実を投影するレンダラーが `scan` だけで 5 本（text / markdown / json / stderr 要約 / summary ファイル）になる。「`ol` の出力が進化したときに CI へ自動で反映される」という利点は、その 2 本が同期し続ける限りでしか成り立たない。

**ログの要件を何も解決しない。** `--summary` はジョブサマリーだけの話で、`--format json` の stderr が 0 バイトである問題はそのまま残る。

`render` を選ぶと、`--summary` が解く問題はすべて解け、加えてログの問題も解ける。第 3 の出力先という概念を `scan` に導入する必要もなくなる（`render` は stdout に書き、リダイレクトは呼び出し側）。テストも既存の stdout / stderr / exit code の 3 つ組で完結する。

### `--format github-actions`

format は排他であり、GitHub 固有の形式を作る必要が実測できていない。GFM と `<details>` はどの CI でも描画される。

### 環境変数による自動検出

`GITHUB_ACTIONS` や `GITHUB_STEP_SUMMARY` の存在で暗黙に有効化する案は採らない。

- 環境が挙動を変える。残留した環境変数を持つ端末で `ol scan` を叩くと、利用者の知らないファイルに追記される。`ol` は観測できない事実を推測しない設計であり、環境からの推測で副作用を増やすのは一貫しない。
- テスト時に必ず隔離が要る。
- CI ベンダーの知識が core に入る。1 つ入れると次を断る理由がなくなる。

### `ol` が `::group::` を出す

上記「引く線」のとおり。ランナーのプロトコルであって文書形式ではない。

### `scan` を JSON 専用にして人間向けを `render` に一本化する

`ol scan --input .` が人間向けの表を出す挙動を壊す。CLI 一行の体験は `ol` の制約であり、これを崩す変更は採らない。`scan --format text` は「scan して render する」の同義であり続ける。

## 未決の論点

1. **コマンド名。** `render` / `report` / `show` / `view`。`render` を推す（`check` `diff` と同じく動詞で、投影であることを言っている）。
2. **`--sort` / `--group-by` を `render` に移すか。** `render` 側にあるのが自然（永続化レポートから再集計できる）。ただし `scan` からも消すと一行体験が痩せるので、両方に置くのが妥当か。`--dependency` は**レポートの母集団を狭めて `check` の評価範囲を変える**ので `scan` に残す。
3. **`render --format json` を持つか。** `--report` をそのまま吐くだけなので無意味に見えるが、`--sort` / `--group-by` と組めば「再集計した canonical JSON」になる。当面は持たない案を推す。
4. **`render` の text で要約を先頭に置く決定を、`scan --format text` にも波及させるか。** させない案を推す（stderr の要約は走行の注釈であり、順序は走行に従う）。
5. **`check --format markdown` で `Purl` 列を落とす判断。** 幅の問題であって情報の問題ではないので、`--verbose` で戻す余地はある。

## 非目標

- 環境変数による自動検出。
- GitHub 固有の出力形式、およびランナープロトコル（`::group::`、`::error::`）の出力。
- PR コメント投稿。
- `check` の stdout TSV の変更。`--format markdown` は追加の投影であって、既存の TSV を置き換えるものではない。
- `scan` の stdout / stderr 分割の変更。

## 実装しない場合の代替

`ol` 側に持たない場合、呼び出し側は次を行う。動作は確認済みである。

````yaml
- name: "Summarize"
  if: ${{ !cancelled() }}
  run: |
    {
      echo '## License scan'; echo; echo '```text'
      ol scan --input . --format markdown 2>&1 >"$RUNNER_TEMP/inventory.md"
      echo '```'; echo
      echo '## License check'; echo; echo '```text'
      cat "$RUNNER_TEMP/check.txt"
      echo '```'; echo
      echo '<details><summary>Full inventory</summary>'; echo
      cat "$RUNNER_TEMP/inventory.md"
      echo; echo '</details>'
    } >> "$GITHUB_STEP_SUMMARY"
````

コストは 2 回目のスキャン 1.5 秒と、フェンス規則を呼び出し側が持ち続けることである。`check` の出力は TSV のままなので違反は表にならず、`--format json` のログが空である問題も残る。
