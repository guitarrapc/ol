# 任意パッケージのライセンスポリシー例外

## 背景

組織全体のルールとして development-only dependency に追加ライセンスを許可する場合は、[開発専用依存の追加 allow-list](plan_development_license_policy.md) のような CLI policy が適している。

一方、次の判断は全体ルールではなく、個別にレビューされた例外である。

- 特定 package/version だけ LGPL を許可する。
- upstream の移行期間中だけ、本来の allow-list にないライセンスを認める。
- owner と理由を明示し、期限後に通常ポリシーへ戻す。

これを `--allow-package-license purl=license` のような CLI token へ詰め込むと、PURL の quoting、複数ライセンス、理由、owner、期限を表現できない。CI command も長くなり、pull request で例外そのものをレビューしにくい。

既存 baseline を流用してもならない。baseline は unresolved evidence の reviewed snapshot であり、解決済みで通常 allow-list が拒否するライセンスを許可しないことが安全境界になっている。

## 結論

一般的な Ol config や暗黙 discovery は追加せず、個別例外だけを記録する versioned JSON artifact を導入する。`check` では path を明示する。

```text
ol check --input . \
  --allow-licenses MIT,Apache-2.0,BSD-3-Clause \
  --policy-exceptions ol-policy-exceptions.json
```

初期 schema は exact package/version と、その package に追加で許可する SPDX License Identifier、理由、owner、期限だけを持つ。

```json
{
  "schemaVersion": 1,
  "exceptions": [
    {
      "purl": "pkg:npm/example@1.2.3",
      "allowLicenses": [
        "LGPL-2.1-only"
      ],
      "reason": "Build-time tool only; not included in distributed artifacts",
      "owner": "frontend-platform",
      "expiresOn": "2027-03-31"
    }
  ]
}
```

ファイル名は規約化せず、`--policy-exceptions` を指定した場合だけ読む。例外を一般設定、license evidence の訂正、baseline acknowledgement と混ぜない。

## 例外の意味

### exact identity

例外は canonical versioned purl と exact match する component だけに適用する。

- version を省略した package-wide exception は許可しない。
- wildcard、glob、正規表現、version range は初期 schema に含めない。
- ecosystem/name だけの fallback match は行わない。
- purl が同じ複数 occurrence に適用されることは許すが、異なる report component identity に曖昧に一致してはならない。

version update で例外が自動的に外れ、再レビューされることを優先する。利用者の typo や stale entry は通常ポリシーを弱めないため、match しない例外として可視化する。

### license constraint

`allowLicenses` は、例外対象 component なら任意のライセンスを無条件に許可するためのものではない。現在観測した SPDX expression を、通常 allow-list とその entry の追加 allow-list の和集合で評価する。

例:

- entry が `LGPL-2.1-only` だけを許可し、component が `LGPL-2.1-only` なら通過する。
- component が将来 `GPL-3.0-only` へ変われば、同じ purl に一致しても失敗する。
- `MIT AND LGPL-2.1-only` は通常 allow-list に MIT、entry に LGPL があれば通過する。
- `allowLicenses` の未知 identifier、空配列、expression、exception identifier は configuration error とする。

例外は `LicenseStatus.Matched` にだけ適用する。`unknown`、`ambiguous`、`conflict`、`invalid`、`error` を個別例外で隠さない。初期実装では package exception を `LicenseAllowPolicy.CanAcknowledge` に持ち込まず、baseline の候補判定も変更しない。

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

将来、過去日での監査再実行が必要になった場合は、evaluation date の明示入力を別 plan で検討する。初期 CLI に `--policy-date` は追加しない。

## policy の合成順序

複数の許可手段を次の順序で評価する。

1. 通常の `--allow-licenses`
2. component が development-only の場合の `--allow-dev-licenses`
3. exact purl に一致する有効な package exception
4. unresolved component に対する baseline acknowledgement

1〜3は `LicenseStatus.Matched` の SPDX expression を評価する。4は unresolved evidence にだけ作用する。この順序は「強い順」ではなく、どの理由で通過したかを一意に記録するための precedence である。

通常 allow-list で通過した component は package exception の適用件数に数えない。development policy で通過した component も package exception に数えない。package exception は前段で失敗した場合だけ使用する。

同じ purl に複数 entry を置くことは拒否する。複数 entry の allow-list、owner、期限を暗黙に merge すると、どの承認が verdict を変えたか一意に説明できないためである。

## schema と入力検証

policy exception file は独立した schema version を持つ。canonical scan report や baseline schema と共有しない。

読み取り時に少なくとも次を検証する。

- top-level object と `schemaVersion`
- 必須の `exceptions` array
- entry 数、文字列長、file byte length の上限
- 必須 field、未知 field の扱い
- canonical versioned purl
- 重複 purl
- SPDX identifier の正規化と重複
- 空または whitespace-only の `owner` / `reason`
- `expiresOn` の厳密な日付形式と実在日

未知 field は初期 schema では拒否する。typo した policy field を無視して例外が適用されない、または将来別の意味で解釈されることを避ける。error message の表示長は入力妥当性とは別に上限化し、pathological input 全体を文字列化しない。

missing、unreadable、malformed、unsupported schema、重複 entry は exit 2 の command/configuration failure とする。途中まで読めた例外だけを適用して partial policy result を出してはならない。

## 未使用・期限切れの扱い

例外ファイルを指定した場合、次の件数を pass/fail にかかわらず表示する。

```text
Policy exceptions: 2 applied, 1 expired, 3 unused.
```

- `applied`: 通常および development policy では失敗し、entry によって通過した component 数
- `expired`: report component に purl が一致したが期限切れで適用されなかった entry 数
- `unused`: purl に一致する report component がなかった entry 数

unused entry は初期実装では exit code を変えない。異なる project、target、platform の report に同じ policy file を適用する運用があり得るためである。ただし件数を隠さず、verbose では bounded な purl、owner、期限を列挙する。

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

lookup は `Utf8Slice` / `ReadOnlySpan<byte>` の canonical purl から照合できる indexed structure とする。例外数から capacity を一度だけ決め、同一 purl を O(1) で検索する。SPDX identifier は entry ごとに事前正規化し、expression evaluator が component loop 内で policy structure を作り直さない形にする。

evaluation result は violation だけでなく、次の index/count を explicit data として返す。

- package exception を適用した component
- 一致したが期限切れだった exception
- 未使用 exception
- violation に関連する expired exception

renderer が policy を再評価してこれらを推測してはならない。pooled working storage を result に露出せず、owned result へ使用範囲だけをコピーする。

## CLI と既存機能

`--policy-exceptions` は `check --input` と `check --report` の両方で使用できる。report evaluation は exception file と SPDX data 以外の dependency input、cache、registry、repository にアクセスしない。

初期実装では次へ option を広げない。

- `scan`: factual report に policy exception を適用しない。
- `diff`: 現在の `--allow-licenses` policy transition だけを維持する。exception-aware diff は、利用要求と出力契約を別途定義してから追加する。
- `--update-baseline`: package exception file を生成、編集、または上書きしない。

SARIF violation 集合は text と一致させる。package exception で通過した component を SARIF result にしない。期限切れにより残った violation には、秘密情報や絶対 file path を含めず、owner と expiry date を bounded property として付けられる。

## 実施順序

### Phase 1: schema と precedence をテストで固定する

`test-first-development` に従い、parser と policy integration の失敗テストを先に追加する。

1. exact purl と allowed license が一致する component は通過する。
2. version が変わると exception は unused になり、component は失敗する。
3. license が entry の allow-list 外へ変わると失敗する。
4. owner、reason、expiresOn の欠落と malformed date は exit 2 になる。
5. 同じ purl の重複 entry は merge されず exit 2 になる。
6. 期限当日は有効、翌日は期限切れになる。
7. unresolved status は package exception で通過しない。
8. base、development、package exception、baseline の precedence が一意になる。
9. option 省略時は既存 stdout、SARIF、exit code が変わらない。

### Phase 2: versioned reader と immutable policy data を実装する

file I/O と JSON parsing を分離し、core parser は UTF-8 span を受け取る。source byte buffer から借用する値と、policy object が所有する値の lifetime を明示する。pooled storage を public/owned result へ逃がさない。

invalid input の全 equivalence class、上限、duplicate、未知 field を test する。JSON serialization は初期 scope に含めず、利用者が手で管理する入力 artifact とする。

### Phase 3: `LicenseAllowPolicy` と CLI へ接続する

既存の base evaluation を fast path として維持する。package exception lookup は base policy が拒否した matched component にだけ行う。

evaluation date は CLI から一度だけ渡し、境界値 test では fake clock または明示日付を使用する。expired/unused/application count と renderer を接続する。

### Phase 4: persisted report、SARIF、文書を同期する

同じ report、policy exception file、evaluation date に対して live input と persisted report が同一 verdict と同一 stdout を返すことを固定する。SARIF と text の violation 集合を照合する。

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
- expired または unused entry の自動削除
- deny-list、license category、copyleft の自動分類
- `diff` の exception-aware policy transition

## 実装前に確定する判断事項

1. canonical versioned purl の検証を、Ol 内部の既存 identity contract だけで行うか、bounded な共通 purl parser を導入するか。外部 package を追加する場合は native AOT、size、allocation を測定する。
2. 初期 schema で未知 field を拒否する方針が、将来 schema version を上げる運用と整合することを確認する。
3. owner と reason の最大 byte length、exception file 全体と entry 数の上限を fixture と実利用例から決める。
4. 過去日の監査再実行に evaluation date option が必要かを確認する。初期実装では追加しない。

## 成功条件

1. 通常 allow-list を広げず、exact package/version に対してだけ追加 SPDX license を許可できる。
2. version または license の変化で例外が自動的に外れ、元の violation が再発する。
3. owner、reason、期限が repository でレビュー可能な一つの artifact に残る。
4. 期限切れと未使用 entry が pass 時にも可視化される。
5. baseline の unresolved-only 境界と factual scan result を変更しない。
6. `--input` と `--report` が同じ evaluation date で同一 verdict、stdout、SARIF violation 集合を返す。
7. option 省略時の既存 CLI 契約と policy hot-path 性能を維持する。
