# NuGet `licenseFile` warning とライセンス本文解決

## この文書の位置付け

NuGet package が SPDX `licenseExpression` ではなく `.nupkg` 内の `licenseFile` を宣言した場合に、Ol が現在 `unknown` だけを返して理由を説明できない問題と、埋め込み本文から再現可能な SPDX candidate を得るための実装順序を定める。

これは実装済み仕様ではない。warning の追加は現行の識別子データで独立して実施できるが、本文同定は [SPDX data contract](../specs/spdx.md) と [package metadata evidence](../specs/packagemanager.md) の変更を先に確定してから着手する。

## 背景

`Microsoft.DotNet.PlatformAbstractions@3.1.6` は再現例である。

- NuGet registration leaf は versioned package と `packageContent` を識別する。
- catalog entry は `licenseFile: LICENSE.TXT` を持つが、`licenseExpression` を持たない。
- NuGet Gallery は archive 内の `LICENSE.TXT` を表示し、その本文は MIT License である。
- Ol の NuGet provider は `licenseExpression` だけを `RawLicense` へ投影するため、registry candidate は空になる。
- package metadata cache はこの空の観測を正当な cache hit として永続化する。TTL がないため、実装を更新しても既存 cache は自動では再収集されない。

現在の結果は「利用可能な SPDX declaration がない」という意味では正しい。しかし `licenseFile` が存在することを捨てるため、「metadata 自体がない」のか「Ol が file evidence をまだ処理できない」のかを利用者が区別できない。

## 目標

1. `licenseExpression` が空で `licenseFile` が存在するとき、unknown の理由を stable warning で説明する。
2. versioned NuGet artifact 内の宣言された file だけを bounded に取得し、版固定の SPDX matcher で同定する。
3. matched、no-match、multiple-match、取得不能、archive 不正を別の観測として保持し、unknown を false certainty に変えない。
4. 同じ package/version、SPDX data version、artifact bytes から同じ candidate と provenance を得る。
5. component 数ではなく deduplicated package target 数に比例して cache・network・archive work を行う。
6. 既存の空 NuGet cache が新実装を永久に隠さない migration path を持つ。

## 非目標

- NuGet Gallery の HTML や `/License` page を scrape しない。
- `licenseUrl`、file 名、本文冒頭の自然言語を SPDX ID として推測しない。
- GitHub repository の現在の default branch を package version の証拠として代用しない。
- NuGet global-packages folder や restore 済み host state を既定の証拠源にしない。
- fuzzy similarity だけで `matched` にしない。
- package 内の全 legal file や source tree を探索しない。対象は catalog が宣言した exact `licenseFile` だけとする。
- license 本文を既定 report に埋め込まない。
- NuGet 以外の artifact scan を同時に一般化しない。共通化は第二の実装例が現れてから判断する。

## 採用する境界

### Declaration の優先順位

NuGet catalog の `licenseExpression` が存在する場合は、現在どおりその declaration を使い、artifact を取得しない。`licenseFile` 解決は `licenseExpression` が空の場合だけ計画する。

両方が存在する不正または将来形式の応答を黙って統合しない。expression を declaration として保持し、file の存在は warning にできるが、不要な archive request は行わない。

### Artifact の取得元

registration leaf が返す HTTPS `packageContent` を使用する。これにより package identity と取得対象が versioned registry record に結び付き、ローカル restore 状態へ依存しない。

許可する authority は NuGet provider が現在許可する NuGet API boundary に限定する。redirect も最終 authority、scheme、port、userinfo、query、fragment の規則を明示的に検証し、任意 URL fetch の入口にしない。

### Evidence の意味

`licenseFile` は package author が指定した legal artifact だが、そこから得る SPDX ID は registry declaration そのものではなく、版固定 matcher による検出結果である。

そのため candidate は少なくとも次を区別できなければならない。

- `licenseExpression` 由来の declared value
- `licenseFile` 本文から一意に検出した value
- 本文を取得したが一致しなかった状態
- 複数 template に一致して一意に決められなかった状態
- file または archive を取得・検証できなかった状態

package-registry evidence は opaque cache identity と収集時刻だけでは不足する。file path、content SHA-256、byte length、artifact content hash、matcher identity/version、match class を typed provenance として保持する。本文自体は既定 report と baseline に含めない。

SBOM、`licenseExpression`、`licenseFile` 検出結果が異なる場合は、既存 reconciler で conflict を保持する。source ごとの特別な勝者を作らない。

## Warning の先行実装

本文 matcher を待たず、次を先に実装する。

### 状態

| Catalog state | Raw license | Warning | Result |
|---|---:|---|---|
| `licenseExpression` あり | expression | なし | 現行どおり正規化 |
| expression なし、`licenseFile` あり | 空 | `nuget_license_file_unresolved` | `unknown`、file evidence が未処理と説明 |
| expression/file なし、legacy `licenseUrl` あり | 空 | `nuget_license_url_unsupported` | `unknown`、registry metadata はあるが安全に正規化できないと説明 |
| expression/file/licenseUrl なし | 空 | `nuget_license_metadata_missing` | `unknown`、registry declaration 自体がないと説明 |

warning 名は candidate の stable identifier として `LicenseCandidateWarnings`、cache JSON、text/Markdown/JSON report で同じ値を使う。file path を warning string へ連結しない。path は将来の typed provenance に置く。

### 旧 cache の扱い

既存 cache には `Source = nuget-registry`、空の `RawLicense`、空の warnings という entry がある。この形だけを旧 capability の観測として cache miss 扱いにし、一度再収集する。

再収集後は `nuget_license_file_unresolved`、`nuget_license_url_unsupported`、`nuget_license_metadata_missing` のいずれかを必ず持つため、永続 unknown でも通常の cache hit に戻る。他 ecosystem と licenseExpression を持つ NuGet entry は無効化しない。全 package metadata cache の schema bump と全 ecosystem の一斉 refetch は避ける。

`--refresh` は従来どおり全対象を再取得する。migration と refresh の summary count を混同しない。

## 本文解決の前提: SPDX data contract

現在の bundled SPDX data は identifier と exception の lookup 用で、license text/template corpus を持たない。`licenseFile` 対応を実装する前に、次を [spdx.md](../specs/spdx.md) で決める。

1. matcher corpus の取得元と License List version。
2. bundled、user-managed、`--spdx-data` の各 source が template data を必須とするか。
3. identifier data と template data の version 不一致を拒否するか。
4. generated native data、外部 versioned data、実行時 JSON のどれを採用するか。
5. native AOT binary size と startup/allocation budget。
6. corpus が利用できない場合を command failure、component warning、または明示的な enrichment-disabled state のどれにするか。

identifier list と異なる版の template で判定してはならない。report metadata は matcher data reference と digest を記録し、同じ report を後で説明できるようにする。

## Bounded artifact contract

実装前に次の上限を spec へ固定する。数値は representative NuGet corpus を測定して決め、コード内の偶然の定数にしない。

- HTTP response の最大 compressed bytes
- ZIP central directory の最大 entry 数
- 宣言された license entry の最大 uncompressed bytes
- compression ratio の上限
- redirect 回数
- request timeout
- artifact request concurrency

処理は次を満たす。

- HTTP body を無制限に `ReadAsByteArrayAsync` しない。
- registration が返す package hash を利用できる場合は、download bytes を同じ algorithm で検証する。
- archive path は absolute path、drive path、`..` segment、NUL、backslash ambiguity、directory entry を拒否する。
- catalog の exact `licenseFile` と一意に対応する entry だけを読む。大文字小文字を変えた推測探索をしない。
- declared entry がない、重複する、暗号化されている、unsupported compression、CRC/hash 不一致、上限超過の場合は bounded failure evidence を返す。
- temporary extraction directoryを作らず、対象 entry を bounded buffer/stream から読む。
- cancellation 後に response、archive、rental を保持しない。

## Matcher contract

1. SPDX License List の版固定 template rules に従う。
2. copyright year/name など template が許可する可変部だけを正規化する。
3. encoding と改行の許容規則を spec と test fixture で固定する。
4. 一意に一つの SPDX ID が成立した場合だけ `matched` candidate を作る。
5. 0 match は `unknown`、複数 match は `ambiguous` とする。
6. fuzzy score や先頭行だけの alias 推測は結果を `matched` に昇格させない。
7. matcher identity、SPDX version、input SHA-256 で結果を再利用できるようにする。

## Data flow と cache

```text
versioned NuGet purl
  -> registration leaf
  -> catalog entry
  -> expression があれば既存 candidate
  -> file の場合だけ packageContent target
  -> deduplicated bounded artifact fetch
  -> exact entry read + hash
  -> versioned SPDX match
  -> normalized candidate + typed provenance
  -> package metadata cache
  -> existing reconciliation
```

同一 canonical purl を持つ複数 component は registration、catalog、artifact、match を一度だけ行い、結果を元の component order へ投影する。completion order を report order にしない。

cache は normalized conclusion だけでなく、少なくとも artifact hash、license file path/hash/length、matcher version、match outcome を検証可能な形で持つ。cache key は package identity に加え、matcher data version/capability を freshness 判定へ含める。SPDX template data が変わった場合、network download を繰り返さず同じ artifact bytes に対して再 match できる設計が望ましいが、archive bytes を永続化する場合は cache size・privacy・clear contract を先に定義する。初期実装では archive 本体を永続化せず、normalized evidence のみを package metadata cache に保存する。

## 実施順序

### Phase 1: 理由を説明する warning

1. NuGet provider response が expression/file/missing を区別する failing tests を追加する。
2. stable warning flags と report identifiers を追加する。
3. empty legacy NuGet cache の targeted recollection test を追加する。
4. `Microsoft.DotNet.PlatformAbstractions@3.1.6` 相当 fixture が `nuget_license_file_unresolved` を持つことを確認する。
5. [packagemanager.md](../specs/packagemanager.md) と [cache_format.md](../specs/cache_format.md) を実装結果に合わせる。

Phase 1 は license を MIT と確定しない。unknown の理由だけを改善する。

### Phase 2: SPDX template data

1. corpus contract と data resolution を spec で確定する。
2. `Ol.Update`、bundled data、user-managed data の red/green tests を追加する。
3. exact matcher の positive、near miss、multiple match、pathological input tests を追加する。
4. generated data size、startup、lookup allocation を測定する。

### Phase 3: NuGet artifact retrieval

1. registration leaf と catalog の情報を lossless に次段へ渡す typed data を追加する。
2. trusted `packageContent` endpoint と bounded response contract を実装する。
3. exact ZIP entry reader と malicious/oversized fixtures を追加する。
4. identical purl の request/archive/match count が 1 になる scheduler test を追加する。

### Phase 4: Evidence、cache、reconciliation

1. file provenance と match outcome を cache/report schema に追加する。
2. matched/no-match/multiple-match/fetch-failure を candidate へ投影する。
3. SBOM/registry expression/file detection の agreement と conflict tests を追加する。
4. old empty cache、new warning-only cache、matched cache、matcher-version change の migration tests を追加する。
5. golden self-scan と ecosystem smoke の report change を review する。

### Phase 5: 実例検証

`Microsoft.DotNet.PlatformAbstractions@3.1.6` を固定 fixture とし、次を確認する。

- Phase 1: unknown のままだが `nuget_license_file_unresolved` が出る。
- Phase 4: exact `LICENSE.TXT` が versioned MIT template に一意 match し、MIT candidate になる。
- 同じ artifact を変更した near-miss fixture は MIT にならない。
- package metadata cache clear 後と cache hit 後で conclusion/provenance が一致する。
- `--refresh` は再取得し、`--skip-enrichment` は artifact request を一切行わない。

## Test matrix

| Expression | File metadata | Legacy URL | Artifact/file | Match | Expected |
|---|---|---|---|---|---|
| valid | none | any | not requested | n/a | expression candidate |
| valid | present | any | not requested | n/a | expression candidate、不要な fetch なし |
| absent | absent | absent | not requested | n/a | unknown + metadata-missing warning |
| absent | absent | present | not requested | n/a | unknown + URL-unsupported warning |
| absent | present | any | available | one | matched detected candidate |
| absent | present | any | available | zero | unknown + unresolved warning |
| absent | present | any | available | multiple | ambiguous + unresolved warning |
| absent | present | any | missing entry | invalid | error/unavailable evidence、scan 継続 |
| absent | present | any | hash mismatch | invalid | error evidence、cache へ成功結果を書かない |
| absent | present | any | oversized/malicious | rejected | bounded error evidence、scan 継続 |
| absent | present | any | transient HTTP failure | unavailable | retry contract 後に component error |
| absent | present | any | cached | cached result | network/archive request なし |
| absent | present | any | duplicate components | one target | request と match は一度だけ |
| absent | present | any | available | SBOM と不一致 | conflict を保持 |

file unavailable を `unknown` と `error` のどちらへ写像するかは、既存 package metadata fetch failure と source unavailable の意味論を照合して [packagemanager.md](../specs/packagemanager.md) で Phase 3 前に確定する。表は transport/integrity failure を error 寄り、正常取得した no-match を unknown とする初期案である。

## Performance と安全性の合格条件

- enrichment planning は component count から capacity を決め、canonical purl を network 前に deduplicate する。
- external request は bounded worker で実行し、component ごとの pending task を作らない。
- archive response、entry content、matcher scratch は上限付きで、pooled buffer の owner を async boundary の外へ漏らさない。
- hot loop に LINQ、closure、regex、per-component growable collection を追加しない。
- `EnrichmentFixedCostBenchmark` で expression-only package の mean/allocated が +10% を超えない。
- file target の scale/dedup case を benchmark に追加し、target 数ではなく duplicate component 数に比例する network/archive work がないことを確認する。
- `E2EBenchmark` で cost を別 stage へ移しただけでないことを確認する。
- native matcher data を追加する場合は binary size と startup allocation を変更前後で記録する。

## Documentation の完了条件

実装した phase ごとに次を更新する。

- [packagemanager.md](../specs/packagemanager.md): NuGet expression/file precedence、best-effort outcome、request/caching contract
- [spdx.md](../specs/spdx.md): template corpus、version、resolution、matcher semantics
- [cache_format.md](../specs/cache_format.md): file provenance、capability/migration、compatibility
- [cli.md](../specs/cli.md): report warning/provenance と status の利用者向け意味
- [verification.md](../specs/verification.md): real NuGet fixture と deterministic smoke assertions

## 完了条件

- unknown の理由が warning と typed evidence から説明できる。
- `Microsoft.DotNet.PlatformAbstractions@3.1.6` が package に埋め込まれた exact bytes と版固定 matcher によって MIT へ解決される。
- file 名、license URL、現在の repository state から MIT を推測していない。
- no-match、multiple-match、missing、malicious、oversized のいずれも false `matched` を作らない。
- expression-only NuGet package は追加 artifact request を行わない。
- duplicate purl、cache hit、refresh、skip-enrichment の request count が契約どおりである。
- report と cache が artifact/matcher provenance を保持し、本文と秘密情報を既定出力へ含めない。
- targeted migration により旧 empty NuGet cache が新しい解決を隠さない。
- full test suite、ecosystem smoke、relevant benchmarks が合格する。
