# `check --allow-licenses` によるライセンス許可判定

## 背景

`ol scan` は解決済み依存入力から完全な dependency inventory を構築し、package registry と source repository の evidence を加え、SPDX expression を照合した scan result を生成する。現在の `matched` は、有効な evidence が一つの SPDX expression に収束したことを表すだけであり、そのライセンスが組織の方針で許可されていることは表さない。

CI では、許可したライセンス以外を含む依存、またはライセンスを確定できない依存を検出し、違反内容を表示して非ゼロ終了する機能が必要である。

## 結論

policy enforcement は `scan` のoptionではなく、別コマンド `check` として追加する。

```text
ol check --input . --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

`scan` は事実の収集と表示、`check` は完成した事実に対するpolicy評価を担当する。両コマンドは同じscan pipelineを利用するが、1回の`check`内で入力解析やenrichmentを二重実行しない。

```text
Resolved dependency input
          │
          v
Inventory / enrichment / reconciliation
          │
          v
    Completed ScanResult
          │
          ├─ scan  -> view / report -> exit 0
          │
          └─ check -> allow policy -> exit 0 / 1 / 2
```

## 成功条件

1. `check --allow-licenses` が既存の全対応入力を `scan` と同じ規則で一度だけ処理する。
2. 全componentがpolicyを満たす場合はexit 0、1件以上の違反があれば全違反を出力してexit 1となる。
3. option、input、SPDX data、whole-command evidence pipeline、outputの失敗はpolicy違反と区別してexit 2となる。
4. `scan` の出力、終了コード、best-effort契約に回帰がない。
5. SPDX `AND`、`OR`、`WITH`、括弧と、全license statusの判定が仕様どおりである。
6. policy評価は完全なcomponent配列に対して行われ、表示filterによって違反を隠せない。
7. policy loopにcomponentごとの一時string、LINQ、regex、growable collectionを導入せず、focused benchmarkで説明不能な時間・allocation退行がない。

## 今回のスコープ

- `ol check` コマンド
- 必須option `--allow-licenses`
- `scan` と共通の入力、SPDX data、cache、enrichment設定
- 完成済み `ScanResult` に対するallow-list評価
- deterministicなtext形式のpass結果と全違反一覧
- exit code 0、1、2の契約

## スコープ外

- policy file
- deny-list
- package、ecosystem、dependency typeごとの例外
- 期限付き承認やwaiver
- scan済みJSON reportの再入力
- JSONまたはMarkdownのcheck結果
- `--out`、`--quiet`、view filter、sort、group
- 自動修正、PR comment、外部systemへの通知

これらを先回りするinterface、service layer、設定schemaは追加しない。初期実装で必要なdataとpure transformだけを定義する。

## CLI契約

### Command

```text
ol check --input <path> --allow-licenses <id,id,...>
```

`--input`はrepeatableであり、file、directory、format detection、collectionの規則は`scan`と同一とする。

`check`が受け付けるscan共通optionは次に限定する。

- `--input`
- `--input-format`
- `--spdx-data`
- `--cache-dir`
- `--refresh`
- `--skip-enrichment`
- `--concurrency`
- `--retry`
- `--verbose`

`--dependency`などのview optionは受け付けない。policy対象は常にenrichmentとreconciliationが完了した全componentである。

### `--allow-licenses`

値はSPDX License Identifierのカンマ区切りとする。

```text
--allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

解析規則は次のとおり。

1. カンマで分割する。
2. 各項目の前後にあるASCII whitespaceを除く。
3. active SPDX dataでcase-insensitiveに照合し、official casingへ正規化する。
4. 正規化後の重複を1件としてimmutable membership lookupを構築する。

次はoption errorとしてexit 2にする。

- optionの省略
- 空文字列または空白だけの値
- `MIT,,Apache-2.0`のような空項目
- active SPDX dataにないidentifier
- `MIT OR Apache-2.0`のようなSPDX expression
- exception identifier
- `Apache License`のような自然言語名

deprecated SPDX License Identifierはactive SPDX dataがidentifierとして認識する限り受理する。deprecated warningはscan evidence側の既存契約を維持し、初期policyに別の拒否規則を追加しない。

## Policy意味論

### Matched expression

`LicenseStatus.Matched` のcomponentだけSPDX expressionを評価する。allow-listに含まれるlicense leafをtrue、それ以外をfalseとする。

| Expression | Allowed IDs | Result | 理由 |
| --- | --- | --- | --- |
| `MIT` | `MIT` | pass | leafが許可済み |
| `GPL-3.0-only` | `MIT` | violation | leafが未許可 |
| `MIT AND Apache-2.0` | `MIT,Apache-2.0` | pass | ANDの両辺が許可済み |
| `MIT AND GPL-3.0-only` | `MIT` | violation | ANDに未許可leafがある |
| `MIT OR GPL-3.0-only` | `MIT` | pass | 許可済みの選択肢がある |
| `GPL-2.0-only WITH Classpath-exception-2.0` | `GPL-2.0-only` | pass | base licenseが許可済み |
| `GPL-2.0-only WITH Classpath-exception-2.0` | `MIT` | violation | exceptionは未許可baseを許可に変えない |

通常のSPDX precedenceを使い、括弧を保持する。componentのnormalized expressionはscan時点でSPDX validityを確認済みだが、policy evaluatorはmalformedな内部入力をpassとして扱わずfalseを返す。

### Unresolved statuses

次はすべてfail-closedとする。

| Status | Violation reason |
| --- | --- |
| `unknown` | usableなlicense informationがない |
| `conflict` | evidenceが一つのexpressionに収束していない |
| `ambiguous` | guessingなしにSPDX expressionへ正規化できない |
| `invalid` | expressionまたはidentifierがSPDXとして無効 |
| `error` | 必要なevidence collectionまたは処理を完了できない |

`conflict`内にallow-list対象candidateが含まれていてもpassにしない。policyは不確実性を解消せず、scan resultの状態をそのまま評価する。

## 出力契約

初期版はdeterministicなtext出力だけを持つ。評価はfail-fastせず、全componentを調べて全違反を収集する。

pass時はstdoutへ少なくとも評価component数を含むsummaryを出す。

```text
License check passed: 42 components satisfy the allow-list.
```

violation時はstdoutへsummaryと一覧を出す。各rowは少なくとも次を識別可能にする。

- component name
- version
- ecosystem
- purl（存在する場合）
- normalized SPDX expression、またはunresolved status
- violation reason

```text
License check failed: 2 violations.

Package      Version  Ecosystem  License/Status  Reason
example-lib  1.2.3    npm        GPL-3.0-only    license is not allowed
unknown-lib  2.0.0    nuget      unknown         license is unresolved
```

row順序はcompleted resultのdeterministicなcomponent順序を維持する。absolute input path、cache path、tokenを出力しない。

option validation、input、SPDX data、whole-command evidence pipeline、stdoutの失敗は、partialなpolicy resultをstdoutへ出さず、stderrへ簡潔な原因を出す。component単位のregistry/source fetch失敗は既存どおりcompleted resultのevidenceとstatusに残し、そのcomponentがunresolvedならpolicy violationとしてexit 1に含める。`--verbose`のinput detection診断も既存`scan`と同様にstderrへ出す。

## 終了コード

| Code | Meaning |
| --- | --- |
| `0` | 全componentがallow-listを満たす |
| `1` | policy violationが1件以上ある |
| `2` | checkの設定または実行に失敗し、policy結果を確定できない |

`scan`の既存終了コードは変更しない。

## Domainと処理境界

### 共通scan execution

現在 `ScanCommands.Scan` にある次の処理を、CLI表示から分離した一つの内部execution boundaryへ切り出す。

1. input selectionとvalidation
2. active SPDX dataのload
3. dependency input scanとinventory構築
4. package metadata enrichment
5. source repository enrichment
6. completed `ScanResult` と実行summaryの返却

boundaryは明示的なoption dataを入力とし、completed result、SPDX data、package metadata summary、source repository summaryをdataとして返す。`scan` rendererと`check` evaluatorはこの結果を消費する。入力形式やecosystemごとの分岐をpolicy側へ追加しない。

I/O errorをpolicy violationへ変換しない。共通boundaryはfailure dataまたは既存の限定されたexceptionを返し、`scan`は従来どおりexit 1、`check`はexit 2へ写像する。

### Allow policy

CLI文字列の解析と正規化はcomponent loopの前に一度だけ行う。正規化済みidentifierは `FrozenSet<string>` などのimmutable membership lookupにする。component expression中のidentifierは既存 `SpdxLicenseIndex` から共有canonical stringを取得してlookupし、per-componentまたはper-tokenのowned stringを作らない。

policy evaluatorはcompleted `ScanComponent[]` を受け取るdeterministicなpure transformとする。per-item hot pathにLINQ、regex、closure、virtual dispatch、growable collectionを使わない。結果として返すviolation arrayはowned result allocationであり、poolしたarrayを外へ露出しない。

SPDX expression評価はAST objectをcomponentごとに構築せず、既存grammarと同じprecedenceのspan-based recursive descentでBoolean値を計算する。`AND`、`OR`、`WITH`、括弧のgrammarを既存normalizerと別々に進化させないよう、共有可能なtoken read/grammar helperを最小単位で抽出する。ただしbehavior-heavy parser abstractionやinterfaceは追加しない。

### Violation data

最低限、次を明示的なvalue dataとして保持する。

- completed componentのindex
- violation kind: `not-allowed` または各unresolved status
- 表示対象となるnormalized expressionまたはstatus

name、version、ecosystem、purlはcomponent indexから出力時に参照し、violationごとに複製しない。

## 実装順序

すべての `src/` 変更はtest-firstで進める。各Phaseで対象testが現在の実装に対して失敗することを確認してからproduction codeを変更する。

### Phase 1: CLI contract tests（Red）

`CliCheckTests` を追加し、built CLI DLLを実行して次を先に失敗させる。

- helpに`check`と`--allow-licenses`が表示される
- allowedな単一licenseでexit 0
- forbidden licenseで全findingを出してexit 1
- `unknown`、`conflict`、`ambiguous`、`invalid`、`error`がexit 1
- allow-list省略、空項目、未知identifier、expression指定がexit 2
- malformed inputまたは不完全SPDX dataがexit 2でpartial stdoutを出さない
- 複数inputとdirectory inputが`scan`と同じ規則で処理される
- `--skip-enrichment`およびcache済みenrichmentが同じcompleted result境界を使う
- scan view optionがcheckではunknown optionになる
- `scan`の既存statusとexit codeが変わらない

対象testをTUnitの`--treenode-filter`で実行し、未実装によるfailureを確認する。

### Phase 2: Policy equivalence classes（Red）

stableなinternal domain seamに対して、次のtruth tableをtestにする。

- leaf: allowed / forbidden
- AND: true-true / true-false / false-true / false-false
- OR: true-true / true-false / false-true / false-false
- WITH: allowed base / forbidden base
- precedenceとnested parentheses
- mixed-case allow inputとofficial casing
- duplicate allow entries
- empty、unknown、expression、exception allow entry
- `matched`と6種類のunresolved status
- zero component result

private methodをreflectionでtestしない。policy evaluationを名前の付いたinternal data transformとして直接検証する。

### Phase 3: 共通scan executionの抽出（Green）

既存`ScanCommands.Scan`から、completed `ScanResult` を作るまでの処理を最小限切り出す。まず既存 `CliScanTests` が同じ出力と終了コードを維持することを確認する。

このPhaseではpolicy behaviorをscanへ入れず、renderer、view、JSON schemaを変更しない。入力collection、enrichment、cache、retryを複製しない。

### Phase 4: Allow policy evaluator（Green）

- allow-listをactive SPDX dataで一度だけ正規化する
- immutable lookupを構築する
- normalized SPDX expressionをspan上でBoolean評価する
- unresolved statusをfail-closedへ写像する
- component indexを持つowned violation arrayを返す

最小実装でPhase 2のtestを通す。expression normalizerのgrammarを変更する場合は、既存SPDX normalization testもすべて実行する。

### Phase 5: `CheckCommands` とtext renderer（Green）

- `Program.cs`へ`check`を登録する
- 共通scan executionを一度だけ呼ぶ
- completed result全体をpolicy evaluatorへ渡す
- passまたは全violationをstdoutへdeterministicにrenderする
- operational/configuration failureをstderrとexit 2へ写像する
- policy violationをexit 1へ写像する

CLI binderのparse errorも契約どおりexit 2になることをintegration testで確認する。framework既定動作で満たせない場合は、check固有option validationの最小境界で処理し、global CLI behaviorを広く変更しない。

### Phase 6: 回帰・性能・文書確認

1. focused policy testを実行する。
2. `dotnet test` でfull suiteを実行する。
3. policy evaluatorのrepresentative benchmarkをactive benchmark runnerへ追加する。
4. allow-list hit、AND/OR混在、全violation、unresolved statusを含むcomponent setでmeanとallocationを測る。
5. clean baselineと比較し、meanとallocated bytesの+10%超または説明不能な退行を受け入れない。
6. `README.md` のcommand一覧、help例、CI利用例を実装済み状態へ更新する。
7. `.github/docs/specs/cli.md` のplanned表記を実装済みcontractへ更新する。

## テストコマンド

```shell
dotnet test --project tests/Ol.Tests/Ol.Tests.csproj --treenode-filter /*/*/CliCheckTests/*
dotnet test --project tests/Ol.Tests/Ol.Tests.csproj --treenode-filter /*/*/LicensePolicyTests/*
dotnet test
dotnet run --project src/Ol.Benchmark/Ol.Benchmark.csproj -c Release
```

## 実装時に確定する表示詳細

Console幅や既存rendererのstyleを確認し、text tableの列幅と長いpurlの表示方法をfixtureで固定する。これはpolicy意味論を変えない表示上の判断であり、少なくともcomponent identity、expression/status、reasonを失ってはならない。
