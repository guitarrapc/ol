# exact versioned PURL のライセンスポリシー例外

## 背景

組織全体のルールとして resolver 上の development scope に追加ライセンスを許可する場合は、[development scope の追加 allow-list](plan_development_license_policy.md) のような CLI policy が適している。

一方、次の判断は全体ルールではなく、個別にレビューされた例外である。

- 特定 package/version だけ LGPL を許可する。
- upstream の移行期間中だけ、本来の allow-list にないライセンスを認める。
- owner と理由を明示し、期限後に通常ポリシーへ戻す。

これを `--allow-package-license purl=license` のような CLI token へ詰め込むと、PURL の quoting、複数ライセンス、理由、owner、期限を表現できない。CI command も長くなり、pull request で例外そのものをレビューしにくい。

既存 baseline を流用してもならない。baseline は unresolved evidence の reviewed snapshot であり、解決済みで通常 allow-list が拒否するライセンスを許可しないことが安全境界になっている。

## 結論

一般的な Ol config や暗黙 discovery は追加せず、個別例外だけを記録する versioned JSON artifact を導入する。`check` では path を明示する。

```text
ol scan --input . --format json > ol-report.json
ol check --report ol-report.json \
  --allow-licenses MIT,Apache-2.0,BSD-3-Clause \
  --policy-exceptions ol-policy-exceptions.json
```

初期 schema は exact versioned PURL と、その package に追加で許可する SPDX License Identifier、機械的な usage 条件、理由、owner、期限を持つ。

```json
{
  "schemaVersion": 1,
  "exceptions": [
    {
      "purl": "pkg:npm/example@1.2.3",
      "allowLicenses": [
        "LGPL-2.1-only"
      ],
      "usage": "development",
      "reason": "Approved for resolver-declared development scope; the production artifact is checked separately",
      "owner": "frontend-platform",
      "expiresOn": "2027-03-31"
    }
  ]
}
```

ファイル名は規約化せず、`--policy-exceptions` を指定した場合だけ読む。例外を一般設定、license evidence の訂正、baseline acknowledgement と混ぜない。

## 例外の意味

### exact PURL identity

初期実装は report が出力する versioned purl の UTF-8 bytes と exact match する component だけに適用する。利用者は report の purl をそのまま policy file へコピーする。PURL の意味的 canonicalization、type casing、percent-encoding、qualifier order の同値化は初期 scope に含めない。

- version を省略した package-wide exception は許可しない。
- wildcard、glob、正規表現、version range は初期 schema に含めない。
- ecosystem/name だけの fallback match は行わない。
- purl を持たない private/path/git component は初期 schema の対象外とする。
- 同じ purl を持つ複数 occurrence または複数 report component がある場合、一つの entry をその全 component へ適用する。package/version 単位の例外であり、installed path 単位の例外ではない。
- 各 component では license、usage、期限を独立に評価する。一つの component に適用できたことを理由に、同じ purl の別 component の条件を省略しない。
- 特定 source ID または installed path だけを選ぶ selector は初期 scope に含めない。

version update で例外が自動的に外れ、再レビューされることを優先する。利用者の typo や stale entry は通常ポリシーを弱めないため、match しない例外として可視化する。

### license constraint

`allowLicenses` は、例外対象 component なら任意のライセンスを無条件に許可するためのものではない。現在観測した SPDX expression を、通常 allow-list とその entry の追加 allow-list の和集合で評価する。

例:

- entry が `LGPL-2.1-only` だけを許可し、component が `LGPL-2.1-only` なら通過する。
- component が将来 `GPL-3.0-only` へ変われば、同じ purl に一致しても失敗する。
- `MIT AND LGPL-2.1-only` は通常 allow-list に MIT、entry に LGPL があれば通過する。
- `allowLicenses` の未知 identifier、空配列、expression、exception identifier は configuration error とする。

例外は `LicenseStatus.Matched` にだけ適用する。`unknown`、`ambiguous`、`conflict`、`invalid`、`error` を個別例外で隠さない。初期実装では package exception を `LicenseAllowPolicy.CanAcknowledge` に持ち込まず、baseline の候補判定も変更しない。

### usage constraint

`usage` は必須で、次の二値だけを受け付ける。

| 値 | 意味 |
|---|---|
| `development` | [development scope policy](plan_development_license_policy.md) と同じ resolver usage が `DevelopmentOnly` の component にだけ適用する |
| `any` | runtime、development を問わない無条件の package/version 例外として適用する |

`development` entry は、同じ purl/version/license の package が runtime へ移った場合に自動的に失効する。mixed または usage unknown の component へは適用しない。package-manager scope は artifact 非包含を証明しないため、`reason` に「製品へ含まれない」と書くだけでは条件を追加できない。

`any` は通常 allow-list を package/version 単位で意図的に広げる強い承認である。既定値を設けず、利用者が明示した場合だけ受け付ける。`reason`、owner、CODEOWNERS 等の repository review はこの承認の governance であり、Ol が owner の権限を認証するものではない。

### owner、reason、期限

`owner`、`reason`、`expiresOn` は必須とする。

- `owner` は例外の再レビュー先を示す論理名であり、メールアドレスである必要はない。
- `reason` はポリシー判断の理由であり、license evidence の訂正文ではない。
- `expiresOn` は ISO 8601 の calendar date `YYYY-MM-DD` とし、その日を含めて有効とする。
- 日付比較は UTC の calendar date で行い、時刻と timezone を schema に含めない。
- 期限を過ぎた entry は削除せず読み取るが、追加 allow-list として適用しない。

現在時刻は CLI/I/O boundary で一度だけ取得し、policy evaluator へ明示的な evaluation date として渡す。core evaluator が直接 system clock を読まない。これにより単体テストと policy logic を deterministic に保つ。

期限によって同じ report の verdict が日をまたいで変わることは、例外 expiration の意図した挙動である。再現可能性のため、例外ファイルを使用した `check` は evaluation date を出力する。

```text
Policy exceptions evaluated as of 2026-07-29 UTC.
```

過去の判定を再現できるよう、初期 CLI から任意の `--policy-date YYYY-MM-DD` を追加する。`--policy-exceptions` 指定時だけ使用でき、省略時は UTC の現在日を一度だけ取得する。指定時は system clock を読まない。

```text
ol check --report ol-report.json \
  --allow-licenses MIT,Apache-2.0 \
  --policy-exceptions ol-policy-exceptions.json \
  --policy-date 2026-07-29
```

同じ report、SPDX data、policy file、evaluation date は同じ verdict と stdout を返す。

## policy の合成順序

複数の許可手段を次の順序で評価する。

1. 通常の `--allow-licenses`
2. component が resolver 上の `DevelopmentOnly` である場合の `--allow-dev-licenses`
3. exact purl、license、usage、期限が一致する package exception
4. unresolved component に対する baseline acknowledgement

1〜3は `LicenseStatus.Matched` の SPDX expression を評価する。4は unresolved evidence にだけ作用する。この順序は「強い順」ではなく、どの理由で通過したかを一意に記録するための precedence である。

通常 allow-list で通過した component は package exception の適用 component 数に数えない。development policy で通過した component も package exception に数えない。package exception は前段で失敗した場合だけ verdict を変えるが、entry 自体は後述の `redundant` として追跡する。

同じ purl に複数 entry を置くことは拒否する。複数 entry の allow-list、owner、期限を暗黙に merge すると、どの承認が verdict を変えたか一意に説明できないためである。

## schema と入力検証

policy exception file は独立した schema version を持つ。canonical scan report や baseline schema と共有しない。

読み取り時に少なくとも次を検証する。

- top-level object と `schemaVersion`
- 必須の `exceptions` array
- entry 数、文字列長、file byte length の上限
- 必須 field、未知 field の扱い
- report と exact match する versioned purl
- 重複 purl
- SPDX identifier の正規化と重複
- `usage` の必須性と `development` / `any` 以外の拒否
- 空または whitespace-only の `owner` / `reason`
- `expiresOn` の厳密な日付形式と実在日

未知 field は初期 schema では拒否する。typo した policy field を無視して例外が適用されない、または将来別の意味で解釈されることを避ける。error message の表示長は入力妥当性とは別に上限化し、pathological input 全体を文字列化しない。

missing、unreadable、malformed、unsupported schema、重複 entry は exit 1 の command/configuration failure とする。途中まで読めた例外だけを適用して partial policy result を出してはならない。

## entry 状態と適用結果

例外ファイルを指定した場合、全 entry を次の排他的な状態の一つへ分類する。状態数の合計は常に exception entry 数と一致する。

1. `expired`: purl が一致したが evaluation date を過ぎている。
2. `applied`: 一つ以上の component が通常/development policy では失敗し、この entry によって通過した。
3. `license-mismatch`: purl と usage は一致したが、対象 component の expression を entry の追加 allow-list が許可しない。
4. `usage-mismatch`: purl は一致したが、`usage: development` に対して runtime、mixed、unknown しかない。
5. `redundant`: purl と条件は一致するが、全対象 component が通常または development policy で既に通過する。
6. `unmatched`: purl に一致する report component がない。

同じ purl の複数 component が異なる結果を持つ場合は、上記の先勝ち順序で entry state を一意に決める一方、component ごとの decision は全て保持する。たとえば一つでも実際に verdict を変えれば entry は `applied` とし、別 component の usage mismatch を消さず詳細出力に残す。

```text
Policy exceptions: 2 entries applied to 3 components, 1 expired, 1 license-mismatch, 1 usage-mismatch, 1 redundant, 3 unmatched.
```

expired、license-mismatch、usage-mismatch、redundant、unmatched は初期実装では exit code を単独で変えない。元の component が通常 policy を満たさなければ、その violation が exit 2 を決める。異なる project、target、platform の report に同じ policy file を適用する運用があり得るため、stale entry 自体は configuration error にしない。

expired entry が一致した component は通常ポリシーの violation のままとする。text と SARIF は、同じ `NotAllowed` rule ID を維持しつつ、matching exception が期限切れだったことと期限を診断情報として示す。expired entry 自体を別の license violation として二重計上しない。

## データモデルと評価境界

policy exception は plain typed data と immutable lookup として表す。読み取り、日付取得、error rendering は CLI boundary に置き、core policy evaluation は次の入力だけを受け取る deterministic transform にする。

- normalized base allow-list
- 任意の normalized development allow-list と component usage
- normalized package exception set
- evaluation date
- completed scan components と dependency inventory

policy file は run ごとに一度だけ parse、SPDX normalize、index 構築する。component loop では次を禁止する。

- purl の `ToString()` と再 encoding
- entry array の線形走査
- LINQ、closure、regex
- component ごとの `HashSet` / `FrozenSet` 構築
- exception metadata の出力用文字列生成

lookup は `Utf8Slice` / `ReadOnlySpan<byte>` の report purl bytes から exact 照合できる indexed structure とする。例外数から capacity を一度だけ決め、同一 purl を O(1) で検索する。SPDX identifier は entry ごとに事前正規化し、expression evaluator が component loop 内で policy structure を作り直さない形にする。

evaluation result は violation だけでなく、次の index/count を explicit data として返す。

- package exception を適用した component
- 各 exception entry の排他的な最終状態
- 同じ entry に一致した各 component の license/usage decision
- violation に関連する expired exception

renderer が policy を再評価してこれらを推測してはならない。pooled working storage を result に露出せず、owned result へ使用範囲だけをコピーする。

## CLI と既存機能

`--policy-exceptions` は report 専用の `check` で使用する。policy evaluation は report、exception file、SPDX data 以外の dependency input、cache、registry、repository にアクセスしない。

`usage: development` entry は typed usage と stable inventory mapping を持つ canonical report version 2 を要求する。version 1 report と組み合わせた場合は exit 1 にする。`usage: any` だけの file は version 1 reportにも適用できる。

`--verbose` では、entry state にかかわらず全 entry を bounded かつ決定的な順序で列挙する。`applied` では対象 component identity、license、usage、owner、reason、expiry を結び付ける。件数だけで、どの component が例外によって通過したかを隠さない。

初期実装では次へ option を広げない。

- `scan`: factual report に policy exception を適用しない。
- `diff`: 現在の `--allow-licenses` policy transition だけを維持する。exception-aware diff は、利用要求と出力契約を別途定義してから追加する。
- `--update-baseline`: package exception file を生成、編集、または上書きしない。

SARIF violation 集合は text と一致させる。package exception で通過した component を SARIF result にしない代わりに、`run.properties` の policy allowance として component identity、exception purl、license、usage、owner、reason、expiry、evaluation date を記録する。期限切れにより残った violation には、秘密情報や絶対 file path を含めず、owner と expiry date を bounded property として付ける。

## 実施順序

### Phase 1: schema と precedence をテストで固定する

`test-first-development` に従い、parser と policy integration の失敗テストを先に追加する。

1. exact purl と allowed license が一致する component は通過する。
2. version が変わると exception は unmatched になり、component は失敗する。
3. license が entry の allow-list 外へ変わると失敗する。
4. usage、owner、reason、expiresOn の欠落と malformed value は exit 1 になる。
5. 同じ purl の重複 entry は merge されず exit 1 になる。
6. 期限当日は有効、翌日は期限切れになる。
7. unresolved status は package exception で通過しない。
8. base、development、package exception、baseline の precedence が一意になる。
9. option 省略時は既存 stdout、SARIF、exit code が変わらない。
10. `usage: development` の package を同じ purl/version/license のまま runtime へ移すと例外が失効する。
11. `usage: any` は runtime component に適用できる。
12. 同じ purl の異なる source ID/component 全てへ entry を照合し、各 usage/license 条件を独立に評価する。
13. applied、expired、license-mismatch、usage-mismatch、redundant、unmatched の合計が常に entry 数と一致する。
14. 明示 `--policy-date` により、日付をまたいでも同じ verdict と stdout を再現できる。
15. `--verbose` と SARIF `run.properties` から、適用 component と exception owner/reason/expiry を逆引きできる。
16. version 1 report は `usage: any` を評価できるが、`usage: development` との組み合わせを exit 1 にする。

### Phase 2: versioned reader と immutable policy data を実装する

file I/O と JSON parsing を分離し、core parser は UTF-8 span を受け取る。source byte buffer から借用する値と、policy object が所有する値の lifetime を明示する。pooled storage を public/owned result へ逃がさない。

invalid input の全 equivalence class、上限、duplicate、未知 field を test する。JSON serialization は初期 scope に含めず、利用者が手で管理する入力 artifact とする。

### Phase 3: `LicenseAllowPolicy` と CLI へ接続する

exception file を指定しない既存の base evaluation を fast path として維持する。exception file を指定した場合は全 component の purl を lookup し、base/development policy で既に通る entry も `redundant` として分類する。package exception の SPDX evaluation は前段 policy が拒否した matched component にだけ行う。

evaluation date は CLI から一度だけ渡し、境界値 test では fake clock または明示日付を使用する。entry state、component decision、renderer を接続する。

### Phase 4: persisted report、SARIF、文書を同期する

同じ report、policy exception file、evaluation date に対して live input と persisted report が同一 verdict と同一 stdout を返すことを固定する。SARIF と text の violation 集合、および verbose/SARIF の policy allowance 集合を照合する。

実装後に次を更新する。

- `specs/cli.md`: exception schema、precedence、exit code、expiration、出力
- `README.md`: 明示 file path を使う CI 例
- `backlog.md`: per-package policy exception 項目の完了内容と残した非目標

仕様文書には確定した WHAT/WHY、期限による意図した時間依存、実装で判明した lessons learned を記録する。UTF-8 lookup や pooled buffer の詳細 HOW は plan と code comment に留める。

## 性能検証

policy exception は通常 policy より低頻度でも、component ごとの評価経路に入る。次を同一 code revision の変更前後で測定する。

- `LicensePolicyBenchmark`
  - option 省略
  - 例外 file 指定、match なし
  - 少数 entry が少数 component に一致
  - entry 数と component 数が大きい場合
  - expired entry
  - 同じ purl の複数 component
  - redundant、license-mismatch、usage-mismatch の全状態
- `E2EBenchmark`
  - persisted policy input の I/O と parse costを含む run

受け入れ条件は次とする。

- option 省略時の component loop に lookup、clock read、追加 allocation を入れない。
- exception 指定時の評価を O(component + exception) とし、O(component × exception) にしない。
- component loop で purl string を materialize しない。
- policy file parse と index 構築の allocation を run 固定費として説明できる。
- mean time または allocated bytes の説明できない regression を残さない。

## スコープ外

- policy file の暗黙 discovery と repository-wide 一般 config
- CLI token による per-package exception の完全表現
- package 名だけ、version なし purl、wildcard、glob、regex、version range
- dependency path の途中にある package を条件とする `allow-via`
- license evidence の訂正または concluded license
- unresolved/conflict/invalid/error の抑制
- exception file の自動生成、自動更新、期限延長
- expired または unmatched entry の自動削除
- deny-list、license category、copyleft の自動分類
- `diff` の exception-aware policy transition

## 実装前に確定する判断事項

1. 初期 schema で未知 field を拒否する方針が、将来 schema version を上げる運用と整合することを確認する。
2. owner と reason の最大 byte length、exception file 全体と entry 数の上限を fixture と実利用例から決める。
3. 同じ purl の複数 component に複数の非適用理由がある場合、entry の排他的状態に使う先勝ち順序と component-level detail が実例を十分説明できることを fixture で確認する。

## 成功条件

1. 通常 allow-list を広げず、exact package/version に対してだけ追加 SPDX license を許可できる。
2. version、license、または `usage: development` の development-to-runtime 変化で例外が自動的に外れ、元の violation が再発する。
3. owner、reason、期限が repository でレビュー可能な一つの artifact に残る。
4. 全 entry が排他的な状態へ分類され、状態数の合計が entry 数と一致する。
5. baseline の unresolved-only 境界と factual scan result を変更しない。
6. `--policy-date` により過去の判定を再現でき、`--input` と `--report` が同じ evaluation date で同一 verdict、stdout、SARIF violation 集合を返す。
7. verbose と SARIF の policy allowance から、例外を適用した component と owner/reason/expiry を監査できる。
8. option 省略時の既存 CLI 契約と policy hot-path 性能を維持する。
