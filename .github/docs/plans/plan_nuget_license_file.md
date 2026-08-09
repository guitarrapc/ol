# NuGet `licenseFile` のライセンス本文解決

## この文書の位置付け

NuGet package が SPDX `licenseExpression` ではなく `.nupkg` 内の `licenseFile` を宣言した場合に、埋め込み本文から再現可能な SPDX candidate を得るための実装順序を定める。

**未実装である。** 「なぜ unknown なのか」を説明する部分 (当初の目標 1) は別経路で達成済みで、後述の節に経緯を残した。残るのは本文照合そのもので、[SPDX data contract](../specs/spdx.md) と [package metadata evidence](../specs/packagemanager.md) の変更を先に確定してから着手する。

## 背景

`Microsoft.DotNet.PlatformAbstractions@3.1.6` は再現例である。

- NuGet registration leaf は versioned package と `packageContent` を識別する。
- catalog entry は `licenseFile: LICENSE.TXT` を持つが、`licenseExpression` を持たない。
- NuGet Gallery は archive 内の `LICENSE.TXT` を表示し、その本文は MIT License である。
- Ol の NuGet provider は `licenseExpression` だけを `RawLicense` へ投影するため、registry candidate は空になる。
- package metadata cache はこの空の観測を正当な cache hit として永続化する。TTL がないため、実装を更新しても既存 cache は自動では再収集されない。

現在の結果は「利用可能な SPDX declaration がない」という意味では正しい。しかし `licenseFile` が存在することを捨てるため、「metadata 自体がない」のか「Ol が file evidence をまだ処理できない」のかを利用者が区別できない。

## 目標

1. ~~`licenseExpression` が空で `licenseFile` が存在するとき、unknown の理由を stable warning で説明する。~~ 達成済み (後述)。
2. versioned NuGet artifact 内の宣言された file だけを bounded に取得し、版固定の SPDX matcher で同定する。
3. matched、no-match、multiple-match、取得不能、archive 不正を別の観測として保持し、unknown を false certainty に変えない。
4. 同じ package/version、SPDX data version、artifact bytes から同じ candidate と provenance を得る。
5. component 数ではなく deduplicated package target 数に比例して cache・network・archive work を行う。
6. ~~既存の空 NuGet cache が新実装を永久に隠さない migration path を持つ。~~ resolver capability version で達成済み。matcher data version による再 match は Phase 4 の対象として残る。

## 非目標

- NuGet Gallery の HTML や `/License` page を scrape しない。
- `licenseUrl`、file 名、本文冒頭の自然言語を SPDX ID として推測しない。安全な GitHub license-file URL から repository/ref だけを抽出して既存の source evidence に渡す互換処理は、この禁止に含めない。
- GitHub repository の現在の default branch を package version の証拠として代用しない。legacy `licenseUrl` が `blob/{ref}/` などで ref を明示している場合、その ref は Ol が補った既定値ではないためこの禁止に含めない。ただし `master` や `main` を指す URL から得られるのはその branch の現在の内容であり、package version 時点に固定された証拠ではない。
- NuGet global-packages folder や restore 済み host state を既定の証拠源にしない。
- fuzzy similarity だけで `matched` にしない。
- package 内の全 legal file や source tree を探索しない。対象は catalog が宣言した exact `licenseFile` だけとする。
- license 本文を既定 report に埋め込まない。
- NuGet 以外の artifact scan を同時に一般化しない。共通化は第二の実装例が現れてから判断する。

## 採用する境界

### Declaration の優先順位

NuGet catalog の `licenseExpression` が存在する場合は、現在どおりその declaration を使い、artifact を取得しない。`licenseFile` 解決は `licenseExpression` が空の場合だけ計画する。

両方が存在する不正または将来形式の応答を黙って統合しない。expression を declaration として保持し、file の存在は declared reference として残せるが、不要な archive request は行わない。

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

## 目標 1 (unknown の理由の説明) は別経路で達成済み

当初はこの文書で NuGet 固有の warning を 3 つ (`nuget_license_file_unresolved`、`nuget_license_url_unsupported`、`nuget_license_metadata_missing`) 定義し、本文 matcher を待たずに先行実装する計画だった。実装したが、その後 [multi-source evidence](plan_multi_source_evidence.md) の Phase 3 が同じ事実を横断的な typed evidence として表現したため、3 つとも**削除**した。

現在は publisher が示したライセンスの所在を [declared license reference](../specs/spdx.md#contract-declared-license-reference) が保持し、未解決コンポーネントの reason はその `DeclaredLicenseReferenceKind` から導出される ([unresolved section](../specs/cli.md#contract-unresolved-section))。`licenseFile` は `artifact-path`、legacy `licenseUrl` は `location` として現れる。安全な GitHub license-file URL からの repository/ref 抽出も実装済みで、source evidence 解決へ渡る。旧 cache の一度きり再収集も resolver capability version 4 で全エコシステムに対して実施済みである。

したがって残るのはこの文書の目標 2 以降、すなわち**宣言された `artifact-path` を実際に読んで SPDX ID を同定すること**だけである。参照は結論を作らないという境界は維持し、本文照合だけがそれを解決に変える。

なぜ warning を横断語彙へ改名せず削除したかは [backlog.md](../backlog.md#warning-vocabulary-budget) に残した。

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

### Phase 1: 理由を説明する (完了、ただし別の形で)

`licenseFile` と legacy `licenseUrl` の存在は declared license reference として保持され、未解決コンポーネントの reason はその種別から導出される。NuGet 固有 warning は不要と判明して削除した。旧 cache の再収集も実施済みである。

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
4. reference-only cache、matched cache、matcher-version change の migration tests を追加する。
5. golden self-scan と ecosystem smoke の report change を review する。

### Phase 5: 実例検証

`Microsoft.DotNet.PlatformAbstractions@3.1.6` を固定 fixture とし、次を確認する。

- 現状: unknown のままだが `artifact-path` の declared reference として `LICENSE.TXT` が出る。
- Phase 4: その exact `LICENSE.TXT` が versioned MIT template に一意 match し、MIT candidate になる。
- 同じ artifact を変更した near-miss fixture は MIT にならない。
- package metadata cache clear 後と cache hit 後で conclusion/provenance が一致する。
- `--refresh` は再取得し、`--skip-enrichment` は artifact request を一切行わない。

## Test matrix

| Expression | File metadata | Legacy URL | Artifact/file | Match | Expected |
|---|---|---|---|---|---|
| valid | none | any | not requested | n/a | expression candidate |
| valid | present | any | not requested | n/a | expression candidate、不要な fetch なし |
| valid | any | safe GitHub URL | not requested | n/a | expression candidate、legacy URL を repository に射影しない |
| absent | absent | absent | not requested | n/a | unknown、declared reference なし (実装済み) |
| absent | absent | safe GitHub root URL | repository API | one SPDX result | source candidate (実装済み) |
| absent | absent | その他の URL | not requested | n/a | unknown + `location` reference (実装済み) |
| absent | present | any | available | one | matched detected candidate |
| absent | present | any | available | zero | unknown、`artifact-path` reference を保持 |
| absent | present | any | available | multiple | ambiguous、`artifact-path` reference を保持 |
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
- [spdx.md](../specs/spdx.md): template corpus、version、resolution、matcher semantics。宣言された `artifact-path` が「参照は結論を作らない」から抜ける唯一の経路であること
- [cache_format.md](../specs/cache_format.md): file provenance、capability/migration、compatibility
- [cli.md](../specs/cli.md): matcher provenance と status の利用者向け意味
- [verification.md](../specs/verification.md): real NuGet fixture と deterministic smoke assertions

## 完了条件

- `Microsoft.DotNet.PlatformAbstractions@3.1.6` が package に埋め込まれた exact bytes と版固定 matcher によって MIT へ解決される。
- file 名、license URL、現在の repository state から MIT を推測していない。
- no-match、multiple-match、missing、malicious、oversized のいずれも false `matched` を作らない。
- expression-only NuGet package は追加 artifact request を行わない。
- duplicate purl、cache hit、refresh、skip-enrichment の request count が契約どおりである。
- report と cache が artifact/matcher provenance を保持し、本文と秘密情報を既定出力へ含めない。
- full test suite、ecosystem smoke、relevant benchmarks が合格する。
