# CI ジョブサマリーへの出力を `ol` 側で持つ案

## この文書の位置付け

`ol` を CI で実行したとき、artifact をダウンロードせずに Workflow の画面だけでライセンス状況を確認できるようにしたい、という要求から出た設計案を記録する。実装判断は別途行う。

測定は 2026-08-20、`ol 0.9.3`（本 branch のビルド）、Cysharp の 8 リポジトリで行った。

## 何が問題か

CI がジョブサマリーを作るには、いま呼び出し側が `ol` の出力形式を知っている必要がある。[Cysharp/Actions の PR Harness](plan_ol_cysharp_harness_measurement.md) では次を workflow が担っている。

- `--format json` は **stderr 要約を抑制する**（[cli.md](../specs/cli.md) の契約: 文書が要約を持つので stderr に重複を出さない）。したがって `check` に食わせる JSON を得つつ人間向け要約も得るには、`jq` でレポートを解析するか、`scan` を 2 回走らせるしかない。
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

## 提案: `--summary <file>`

`scan` と `check` の双方に、stdout を変えずに GitHub Flavored Markdown を指定ファイルへ追記するオプションを設ける。

```bash
ol scan  --input . --format json --summary "$GITHUB_STEP_SUMMARY" > report.json
ol check --report report.json --allow-licenses ... --summary "$GITHUB_STEP_SUMMARY"
```

### 既存契約との整合

**`check --sarif <file>` が既に同じ形である。** 「stdout を変えずに、名前を指定されたファイルへ CI 向けの投影を書く」。したがって「第 3 の出力先は [process contract](../specs/cli.md) に反する」という懸念は成立しない。契約が禁じているのは暗黙の出力先であって、明示的に名前を与えられたファイルではない。

`scan` にとっては初のファイル出力になる点だけが非対称だが、`--sarif` が `check` 専用であるのと同じ理由（投影の対象が違う）で許容できる。

### `--format` の値にしない理由

**format は排他だが、この用途は加算的である。** `check` に渡す JSON を stdout に出しながら、同時に人間向けの投影も要る。format 値にすると 2 回スキャンすることになり、上で測った 1.5 秒を毎回払う。file オプションなら 1 回で両方得られる。

`--format github-actions` 案も同じ理由で採らない。加えて、GFM と `<details>` はどの CI でも描画されるため、GitHub 固有の形式を作る必要が実測できていない。

### 環境変数による自動検出にしない理由

`GITHUB_ACTIONS` や `GITHUB_STEP_SUMMARY` の存在で暗黙に有効化する案は採らない。

- 環境が挙動を変える。残留した環境変数を持つ端末で `ol scan` を叩くと、利用者の知らないファイルに追記される。`ol` は観測できない事実を推測しない設計であり、環境からの推測で副作用を増やすのは一貫しない。
- テスト時に必ず隔離が要る。現在の CLI テストは stdout / stderr / exit code の 3 つで完結している。
- CI ベンダーの知識が core に入る。1 つ入れると次を断る理由がなくなる。

## `ol` にしかできないこと

呼び出し側では原理的に到達できない品質が 3 つある。これが「workflow でやればよい」に対する反論になる。

1. **違反を本物の markdown 表にできる。** `ol` は列を知っているので `| Package | Version | ... |` を出せる。workflow は TSV をフェンスで囲むことしかできない。
2. **フェンスと `<details>` の使い分けを 1 回で正しくできる。** 上記の細かい規則を利用者が再発見せずに済む。
3. **`ol` の出力が進化したときに CI へ自動で反映される。** `jq` のパス埋め込みは schema 変更で黙って壊れる。

## 設計要件

- **サイズ上限。** 1 MiB を超えるとジョブサマリーは丸ごと描画されない。`ol` 側で上限を持ち、超えたら inventory を省略したうえで「N components omitted」と**明記する**。黙って切ることは `ol` の流儀に反する。
- **[report-privacy 契約](../specs/cli.md#contract-report-privacy)が適用される。** token 値、絶対ローカルパス、隠しキャッシュパスを書かない。
- **決定性。** 同じレポートから同じ summary が出ること。生成時刻を書かない。
- **追記であること。** `scan` と `check` は別プロセスなので、それぞれが自分のセクションを追記する。相互の調整を要しない。
- **`--sarif` と併用できること。** consumer が違う（code scanning と人間）。

## 構成案

| セクション | 表示 | 内容 |
|---|---|---|
| scan 要約 | 見える | counters、`Supplied by`、`Input discovery` |
| check 結果 | 見える | pass/fail、`Unresolved mechanisms` 集計 |
| Violations | `<details>` | markdown 表 |
| Full inventory | `<details>` | 既存の markdown 表 |

実際に組んだプレビューでは、この 4 節で **3.4 KB**（inventory を省略した状態）だった。開いた瞬間に状況が分かり、必要なら畳んだ部分を開く形になる。

## 未決の論点

1. **オプション名。** `--summary <file>` か、`--markdown-summary <file>` か。前者は簡潔だが「何の summary か」が曖昧で、`scan` の stderr 要約と紛らわしい。
2. **`scan --summary` に inventory を含めるか。** 含めれば `--format markdown` を別途走らせる必要がなくなり、workflow は 1 回のスキャンで済む。含めなければ counters だけの小さな出力になるが、利用者は結局 2 回走らせることになる。**含める案を推す。**
3. **unresolved 節を inventory の `<details>` に同梱するか、独立させるか。**
4. **上限を超えたときに何を残すか。** 要約と check 結果を残して inventory を落とすのが自然だが、違反が数千件ある場合は違反表も落とす必要がある。優先順位を決める必要がある。

## 非目標

- 環境変数による自動検出。
- GitHub 固有の出力形式（`--format github-actions`）。
- PR コメント投稿やアノテーション出力。`::error::` は exit code に応じて呼び出し側が出すほうが素直であり、現行の harness がそうしている。
- `check` の stdout 形式の変更。summary は追加の投影であって、既存の TSV を置き換えるものではない。

## 実装しない場合の代替

`ol` 側に持たない場合、呼び出し側は次を行う。実際に動作を確認済みである。

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

コストは 2 回目のスキャン 1.5 秒と、フェンス規則を呼び出し側が持ち続けることである。`check` の出力は TSV のままなので、違反は表にならない。
