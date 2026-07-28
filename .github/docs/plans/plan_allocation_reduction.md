# 中間アロケーション削減と cache の 0 alloc デシリアライズ

## 背景

ol は CLI であり、最終出力（report、evidence、rendered text）のアロケーションは避けられない。しかし現状は **出力より前段のアロケーションが出力そのものを大きく上回っている**。

E2E benchmark（1 component の CycloneDX を cache hit で scan）は 10.94 KB を確保するが、そのうち **7.5 KB は package metadata cache を 1 件読むためだけに消えている**。renderer は既に 0 alloc であり、SBOM parser も 240 B/scan に収まっている。つまり残る主因は「入力を読む」でも「出力を書く」でもなく、**cache の読み取りと enrichment の橋渡し**にある。

本文書は、推測ではなく実測でアロケーションの出所を特定し、優先度付きの対応順序を決める。

## 計測方法

`GC.GetAllocatedBytesForCurrentThread()` を各操作の前後で取得し、200 回の平均を B/op として求めた（warmup 20 回、`GC.Collect()` 後に測定、Release ビルド、単一スレッド）。BenchmarkDotNet の `[MemoryDiagnoser]` と異なり、**1 回の呼び出しの内訳を分解できる**ため原因特定に用いた。確定後の検証は既存 benchmark で行う（[検証](#検証)参照）。

対象データは benchmark と同じ形状。package cache file 328 B、source cache file 437 B。

## 実測結果

### package metadata cache（`cache.TryRead` = 7489 B/op）

| 操作 | B/op | 説明 |
|---|---:|---|
| `GetCacheKeySha256(key)` | 408 | `UTF8.GetBytes` + hash byte[] + `ToHexString` + `ToLowerInvariant` |
| `GetPath(key)` | 920 | 上記 + `string.Concat` + `Path.Combine` |
| `File.Exists(path)` | 0 | — |
| `File.OpenRead` + close | 241 | FileStream 本体のみ |
| **`FileStream.Read` を伴う JSON 読み取り** | **4433** | **初回 Read で 4096 B の内部バッファを確保** |
| `JsonDocument.Parse(byte[])` | 72 | 比較用 |
| `JsonDocument.Parse(MemoryStream)` | 136 | 比較用 |
| `RandomAccess.Read`（再利用バッファ） | 73 | 比較用 |
| `Element.Deserialize(ctx)` | 1112 | string ×5 + string[] ×2 + record |
| `Utf8JsonReader` で全項目を手走査 | **0** | 到達可能な下限 |
| **`cache.TryRead(key)` 合計** | **7489** | |

`FileStream.Read->pooled buffer + Parse(span)` も 4433 B/op だった。つまり 4.2 KB は JSON API ではなく **FileStream が初回 Read で確保する 4096 B バッファ**であり、`File.OpenRead` 単体（241 B）では発生しない。`File.ReadAllBytes`（425 B）と `RandomAccess.Read`（73 B）は FileStream を経由しないためこの費用を払わない。

### source repository cache

| 操作 | B/op |
|---|---:|
| `sourceCache.Read(key)`（hit） | 3665 |
| `record.CacheKeySha256` プロパティ | 416 |
| `target.CacheKey` プロパティ | 144 |
| `SourceRepositoryTarget.TryCreate` | 488 |
| **存在しない cache file の Read（miss）** | **1305** | ← 例外 throw の費用 |

### candidate 生成・reconcile

| 操作 | B/op |
|---|---:|
| `record.CacheKeySha256`（evidence 用に再計算） | 408 |
| `new PackageRegistryEvidence(...)` | 448 |
| `Utf8Slice.FromString(record.RawLicense)` | 32 |
| `LicenseCandidateIdentifiers.ParseWarnings(record.Warnings)` | 0 |
| `LicenseReconciler.AddCandidate` 1 回目 | 104 |
| `LicenseReconciler.AddCandidate` 2 回目（配列再確保） | 184 |

### プロセス固定費（benchmark に現れない）

| 操作 | B/op |
|---|---:|
| `new SpdxLicenseIndex(生成データ)` | **293,624** |
| `ComputeGeneratedDataHash(LicenseIds)` | 27,432 |

`SpdxData.Bundled` は `static readonly` のため benchmark では 1 回しか計上されないが、**CLI は 1 回の起動につき必ず 1 回払う**。text 出力しか使わない場合でも hash 2 本を計算する。

### 到達可能な下限（prototype 実測）

`File.OpenHandle` + `RandomAccess.Read`（pooled buffer）+ 単一 `Utf8JsonReader` パス（validate と field 取得を統合）+ `string.Create` による path 構築で prototype を書いて測定した結果は **425 B/op**（7489 B からの削減率 94%）。内訳はほぼ path string（.NET の file API は `string` path しか受け付けない）と `SafeFileHandle` であり、**JSON デシリアライズ自体は 0 alloc に到達している**。

## 原因の分類

| # | 原因 | 影響 | 実測根拠 |
|---|---|---|---|
| A | cache 読み取りに FileStream を使い、初回 Read で 4 KB バッファを確保している | 4.2 KB × cache 読み取り回数 | 上表 |
| B | JSON を 2〜3 回走査している（`IsValidVersion1` で GetString → `Deserialize` で再パース） | 1.0〜1.1 KB × 回数 | `Element.Deserialize` 1112 B、手走査 0 B |
| C | cache key の SHA-256 を 1 lookup あたり 3 回計算している（`GetPath` / `IsValidVersion1` / `CacheKeySha256`） | 408 B × 3 | 上表 |
| D | 直後に捨てる string / string[] を materialize している（`Source`→enum、`Warnings`/`Errors`→flags、`RawLicense`→`Utf8Slice` 往復、`CacheKey`→比較のみ） | B に含まれる | `ParseWarnings` が 0 B である事実が、string[] が純粋な無駄であることを示す |
| E | source cache の miss を例外で判定している | 1.3 KB × miss 数（初回実行では全 component） | `Read` on 不在 file = 1305 B |
| F | 計算プロパティが呼ぶたびに文字列を作る（`SourceRepositoryTarget.CacheKey`/`Repository`、`*Record.CacheKeySha256`） | 144〜416 B × アクセス回数 | 上表 |
| G | candidate 配列を追加のたびに再確保している | 104 + 184 B × component | 上表 |
| H | purl を `Utf8Slice` → `string` → substring 群に往復させている | 64 + 136 B × component | `Utf8Slice.ToString` 64 B、`TryCreatePackageMetadataRequest` 136 B |
| I | 起動時に SPDX index を構築し、使わない hash も計算している | 294 KB + 30 KB / process | 上表 |

A〜D は同じ 1 箇所（cache 読み取り）に集中しており、**cache 読み取りだけで 1 component あたり 7.5 KB（package）+ 3.7 KB（source）** を占める。500 component の scan なら約 5.6 MB が中間アロケーションとして消える計算になる。

なお、component 重複の coalescing は [PackageMetadataService](../../../src/Ol/Internals/PackageMetadataService.cs) と [SourceRepositoryService](../../../src/Ol/Internals/SourceRepositoryService.cs) で既に実装済みであり（purl / cache key / target による dedup と pooled workspace）、本計画の対象外とする。

## 対応プラン

### P0-1: cache 読み取りから FileStream を排除する

**対象**: `PackageMetadataCache.TryRead` / `TryReadAsync`、`SourceRepositoryCache.Read` / `ReadAsync`

`File.OpenRead` + `JsonDocument.Parse(stream)` / `JsonSerializer.DeserializeAsync(stream)` を、`File.OpenHandle` + `RandomAccess.Read`（`ArrayPool<byte>.Shared` の借用バッファ）+ span からのパースに置き換える。async 版は `RandomAccess.ReadAsync` を使う。

- 期待効果: **-4.2 KB / cache 読み取り**
- リスク: 低。ファイル読み取り方法のみの変更で、外部契約は変わらない
- 単独で実施可能。P0-2 の前提でもある

### P0-2: 検証とフィールド取得を単一 `Utf8JsonReader` パスに統合し、`Utf8Slice` で返す

**対象**: 上記 4 メソッドと、その戻り値型

現在は「`IsValidVersion1` が GetString しながら検証 → 破棄 → `Deserialize` が同じ JSON を再度パースして string ×5 と string[] ×2 を作る」という二重構造になっている。これを 1 パスに統合し、`string` ではなく借用バッファへの `Utf8Slice` を返す。

消費側の実測上の要求は次のとおりで、**string を要求しているものは 1 つもない**。

| フィールド | 現在の消費のされ方 | 必要な形 |
|---|---|---|
| `CacheKey` | 要求キーとの ordinal 比較のみ | UTF-8 比較 |
| `CacheKeySha256` | file 名の検証 + evidence 表示 | path 構築時の hash を再利用（P0-3） |
| `Source` | `GetCandidateSource` で enum へ | UTF-8 switch |
| `RawLicense` | `Utf8Slice.FromString` で UTF-8 へ戻される | そのまま `Utf8Slice` |
| `Warnings` / `Errors` | `ParseWarnings` で flags へ | UTF-8 のまま flags へ |
| `RepositoryUrl` / `RepositoryRef` | `SourceRepositoryTarget.TryCreate` へ渡る | `Utf8Slice`（P1-3 と対） |
| `FetchedAt` | `DateTimeOffset` | 変更なし |

**所有権の設計**: 借用バッファは source enrichment 段階まで生存する必要がある（`RepositoryUrl` / `RepositoryRef` を使うため）。既存の [PackageMetadataWorkspace](../../../src/Ol/Internals/PackageMetadataWorkspace.cs) が両 enrichment 段階を跨いで生存し `Dispose` で pooled storage を返す設計になっているので、**cache buffer の所有も workspace に移す**。report へ残るのは raw license のみであり、これは現在も 32 B の所有バッファとして複製されている。

- 期待効果: **-1.0〜1.1 KB / cache 読み取り**（および string[] の GC 圧）
- リスク: 中。`PackageMetadataRecord` / `SourceRepositoryRecord` は public 型で write path とテストが依存する。**読み取り専用の entry 型を新設し、write path の record 型は変更しない**方針で分離する
- 前提: P0-1

### P0-3: cache path 構築と SHA-256 計算を 1 回にする

`GetCacheKeySha256` は `UTF8.GetBytes` → `SHA256.HashData` → `ToHexString` → `ToLowerInvariant` と 4 つの中間物を作り、それを 1 lookup で 3 回繰り返している。

- hash は `stackalloc` バッファ上で計算し、hex は小文字で直接書き出す（`ToHexString` + `ToLowerInvariant` の 2 段を廃止）
- path は `string.Create` で 1 回の確保にまとめる（`string.Concat` + `Path.Combine` を廃止）
- **1 度作った hex 文字列を evidence（`PackageRegistryEvidence.CacheKeySha256` / `SourceRepositoryEvidence.CacheKeySha256`）へそのまま渡す**。`Record.CacheKeySha256` 計算プロパティは再計算をやめる

- 期待効果: **-1.2 KB / lookup**（920 + 408 ×2 → path string 1 本 ≈ 250 B）
- リスク: 低。hash 値は不変で、cache file 名の互換性も保たれる

**P0 完了時点の到達目標**: `PackageOneCached` 7792 B → **600 B 以下**、`SourceOneCached` 4728 B → **700 B 以下**。prototype 実測 425 B が下限の目安。

### P1-1: source cache の miss 判定から例外を外す（P0 で実施済み）

`SourceRepositoryCache.Read` は不在 file を `FileNotFoundException` / `DirectoryNotFoundException` で判定しており 1305 B/miss を払っていた。P0-1 で共有 helper に `File.Exists` の事前判定を入れ、両 cache の miss が 504 B になった。

### P1-2: 計算プロパティを確定値に変え、正規化を dedup の後ろへ移す（実施済み）

着手前の計測で、当初の想定より大きな問題が見つかった。`SourceRepositoryService.PlanTargets` は **dedup する前に component ごとに `SourceRepositoryTarget.TryCreate` を呼んでいた**。64 component が同じ 1 repository を指していても 64 回正規化する。

| 操作 | B/op（実測） |
|---|---:|
| `SourceRepositoryTarget.TryCreate` | 488 |
| `target.CacheKey`（計算プロパティ） | 144 |
| **PlanTargets が component ごとに払っていた合計** | **632** |

632 B × 64 component = 40.4 KB で、`EnrichDuplicateCachedTarget` 63.71 KB の 63% を占めていた。

対応は 2 つ。

1. `Repository` / `CacheKey` を構築時に 1 度だけ確定させる（計算プロパティを廃止）
2. **dedup を 2 段にする。** 先に「供給された repository URL + ref」で重複を排除し、既に計画済みなら正規化そのものを飛ばす。正規化後の cache key による dedup は残す — 綴りの異なる URL（`git+https://...`、`git@github.com:...`、`.git` 付き）が同じ target に収束する必要があり、ここを落とすと GitHub への要求回数が増えるため

**結果: `EnrichDuplicateCachedTarget` 63.71 KB → 27.18 KB（-57%）**。差分 36.5 KB は、40.4 KB から origin 索引 dictionary の約 3.3 KB と初回 1 回分の正規化を引いた値で説明できる。`EnrichmentFixedCost` と `E2E` は不変（単一 component 経路は `PlanTargets` を通らない）。

dedup の品質が落ちていないことは、実装前に characterization test で固定した（綴り違い 3 種 → 1 target / 1 要求、同一 URL で ref 違い → 2 target、12 component で 2 repository → 2 target、12 component で同一の非対応 URL → 全件 unsupported）。

### P1-3: purl と repository URL の string 往復を排除する

`components[i].Purl.ToString()`（64 B）→ `TryCreatePackageMetadataRequest`（136 B、内部で substring と `Uri.UnescapeDataString`）という往復がある。`PackageMetadataRequest` の生成を `ReadOnlySpan<byte>` 起点にし、cache key を UTF-8 で保持する。同様に `SourceRepositoryTarget.TryCreate` に UTF-8 版を追加する（P0-2 の `RepositoryUrl` が `Utf8Slice` になるため対で必要）。

- 期待効果: **-200 B / component**
- リスク: 中。provider registry（7 ecosystem）の `TryCreate` 境界に波及する。**ecosystem ごとの provider が purl 検証と endpoint 構築を所有する構造は維持する**こと。1 ecosystem の追加が複数ファイルに散らないという既存の設計制約を壊さない

### P1-4: source evidence の cache key digest を読み取りから引き継ぐ（実施済み）

`SourceRepositoryRecord.CacheKeySha256` は計算プロパティで、`CreateResult` が target ごとに SHA-256 を計算し直していた。読み取り側は entry file を特定するために**同じ digest を既に計算している**。

`SourceRepositoryCacheReadResult` に digest を載せ、`CreateResult` は引数で受け取る形にした。fetch 経路は cache hit ではないので、これまでどおり 1 回計算する。

- **`SourceOneCached` 1,872 B → 1,720 B（-152 B / cached target）**
- `EnrichDuplicateCachedTarget` 27.18 KB → 27.04 KB（target 1 つ分）

削減幅は当初 416 B と見込んでいたが、実測は 152 B だった。**見込みは P0-3 より前の計測値だった**。P0-3 で `GetCacheKeySha256` が「4 つの中間物」から「stackalloc + `Convert.ToHexStringLower` で string 1 本」に変わり、1 回あたりの費用が 408 B から 152 B に下がっていたため。削減は target ごとの SHA-256 計算そのもの（CPU）にも効く。

`SourceRepositoryRecord.CacheKeySha256` は永続化のために残す（[cache_format.md](../specs/cache_format.md) が要求する）。読み手が呼ばないよう、プロパティに理由を書いた。

### P2-1: candidate 配列の再確保をやめる

`LicenseReconciler.AddCandidate` は追加のたびに `new LicenseCandidate[n+1]` を作る（enrichment 2 段で 104 + 184 B/component）。候補数の上限は evidence source 数で決まるため、component 構築時に容量を確保して 1 回の確保に収める。`AdditionalCandidates` は report が出力するため確保自体は必要だが、**再確保は不要**。

- 期待効果: **-100〜180 B / component**
- リスク: 低〜中。`ScanComponent` の候補数の表現（`CandidateCount` と配列長の分離）が必要

### P2-2: 起動時の SPDX 固定費を削る

1 回の CLI 起動につき 324 KB を払っている。2 つの独立した項目からなる。

#### hash の遅延化（実施済み）

`SpdxData` は毎回 2 本の SHA-256 を先に計算していたが、その値は **JSON report の `metadata.spdx` でしか読まれない**（`WriteSpdxMetadata` の呼び出し元は `WriteJson` の 2 箇所のみ）。

| 計算 | B/op（実測） |
|---|---:|
| 生成 `LicenseIds` の hash | 27,432 |
| 生成 `ExceptionIds` の hash | 6,016 |
| **起動ごとの合計** | **33,448** |

`SpdxData` から 2 本の string フィールドを外し、「どう導出するか」だけを持つ `SpdxDataDigest` に置き換えた。text / Markdown 出力では **1 バイトも計算しない**。

- 計算プロパティではなく `GetLicensesSha256()` という**メソッド名**にした。ただの読み取りに見える property が 33 KB を確保するのは罠であり、[P1-2](#p1-2-計算プロパティを確定値に変え正規化を-dedup-の後ろへ移す実施済み) で消したのと同じ形だから
- `SpdxDataDigest` は struct ではなく class にして結果を保持する。`--format json --out-file` は `WriteJson` を出力先と標準出力の 2 回呼ぶため、毎回計算する形にすると 66 KB に増えてしまう
- user-installed SPDX data（`--spdx-data` / `ol spdx use`）では、`HashFile` が licenses.json と exceptions.json を**もう一度全部読み直していた**（`ReadSpdxData` が既に読んだ後で）。text 出力ではこの再読み込みも起きなくなった。実 SPDX データはこの 2 ファイルが大きいため、生成データより削減幅は大きい

digest の値が変わっていないことは、実装前に characterization test で固定した（bundled は生成識別子から、user directory は実ファイルから、テスト側で独立に再計算して照合）。`E2E`、`JsonReportRenderer`（0 B 維持）、`TextReportRenderer`（0 B 維持）に変化なし。

#### UTF-8 テーブル（未着手）

`SpdxLicenseIndex` の `licenseUtf8` は識別子ごとに個別の byte[] を持つ（構築 293,624 B/回）。**生成時に 1 本の連結 UTF-8 バッファ + offset table** に置き換えれば 700 個超の小さな byte[] が消える。

- 期待効果: **-100 KB 以上（要測定）**
- リスク: 中。SPDX code generator（`Ol.Update`）の出力形式変更を伴う

### P3: cache の物理形式（P0 の実測により優先度が上がった）

P0 完了後の `PackageCacheHit` 880 B のうち **504 B が path 構築**である（`CacheRead.PackageCachePath` で単独計測）。hash の 64 文字 string と path string で、.NET の file API が `string` path しか受け付けない以上、1 key = 1 file の形式では下限に達している。単一 index file 化すれば N 回の open と N 本の path string が 1 回になる。

parser 側の削り込みはもう効かないため、**cache 読み取りの次の削減はここにしかない**。ただし cache 形式は [specs/cache_format.md](../specs/cache_format.md) の契約であり、並行書き込み・部分破損の扱いが変わる。着手前に仕様の WHAT / WHY を更新すること。

## P0 実装結果

P0-1 / P0-2 / P0-3 を実装した。`dotnet test` は 278 件すべて成功（実装前 264 件 + 追加 14 件）。

| 指標 | 実装前 | 実装後 | 削減 |
|---|---:|---:|---:|
| `CacheRead.PackageCacheHit` | 7,400 B | **880 B** | -88% |
| `CacheRead.PackageCacheMiss` | 920 B | **504 B** | -45% |
| `CacheRead.SourceCacheHit` | 3,448 B | **856 B** | -75% |
| `CacheRead.SourceCacheMiss` | 1,824 B | **504 B** | -72% |
| `EnrichmentFixedCost.PackageOneCached` | 7,792 B | **960 B** | -88% |
| `EnrichmentFixedCost.SourceOneCached` | 4,728 B | **1,872 B** | -60% |
| `E2E.ScanTextWithCachedMetadata` | 10.94 KB | **4.27 KB** | -61% |
| `E2E.ScanJsonWithCachedMetadata` | 11.67 KB | **5.00 KB** | -57% |
| `E2E.ScanNuGetTextWithCachedMetadata` | 23.32 KB | **9.17 KB** | -61% |
| `E2E.ScanNuGetJsonWithCachedMetadata` | 24.07 KB | **9.92 KB** | -59% |
| `SourceRepositoryEnrichment.EnrichDuplicateCachedTarget` | 67.53 KB | **63.71 KB** | -6% |

P1-2 実施後は `EnrichDuplicateCachedTarget` がさらに **27.18 KB**（実装前比 -60%）になった。詳細は [P1-2](#p1-2-計算プロパティを確定値に変え正規化を-dedup-の後ろへ移す実施済み) を参照。

Mean は cache 読み取りが file I/O 律速であり、benchmark 設定が `IterationCount=1` のため実行ごとに 30〜55 μs の幅で揺れる。判断は Allocated で行った。

### 計画からの変更

1. **pooled buffer を workspace が保持する案は採用しなかった。** lookup ごとの借用 buffer を scan 終了まで保持すると、避けられる複製（raw license・repository URL・ref の 3 つ、合計 130 B 程度）よりも peak memory と pool 枯渇の方が高くつく。cache entry は読み取り区間だけ生存する短命な値とし、読み取りより長生きする値だけを複製する。`RepositoryUrl` / `RepositoryRef` は entry 検証（`Uri.TryCreate` は `string` を要求する）と source 段階の両方で必要なので、1 度だけ `string` として materialize して使い回す。

2. **P1-1（例外による miss 判定の排除）を P0 に前倒しした。** FileStream を外した際に `File.Exists` の事前判定も失われ、`PackageCacheMiss` が 920 B → 1,208 B に悪化した。共有 helper 側で `File.Exists` を先に確認する形に直したところ、package と source の両方の miss が 504 B になった（source は元々例外経由で 1,824 B だった）。競合で消える可能性は残るため例外 catch は保持している。

3. **source cache の検証が仕様を満たしていなかったのを、書き換えと同じ場所で直した。** [cache_format.md](../specs/cache_format.md) は `SchemaVersion` と `CacheKeySha256` の永続値を検証するよう要求しているが、旧実装は deserialize 済み record の**計算プロパティ**と比較していた。この 2 つは serialize 専用で、file から読み戻されることがないため、どちらの検証も実質的に無効だった。`HttpStatus` と `License` の必須性も未検証だった。単一パス parser はこれらを file の値として検証する。ol が書いた既存 entry はすべてこれらを含むため、有効な cache が無効化されることはない。あわせて、`Repository` / `Ref` が欠けた entry で `NullReferenceException` が呼び出し元へ漏れていた不具合も解消した。

### 未達の目標と残りの内訳

`PackageOneCached` は目標 600 B に対して 960 B、`SourceOneCached` は目標 700 B に対して 1,872 B で、いずれも未達である。残りの内訳は計測で特定できている。

- **path 構築が 504 B**（`CacheRead.PackageCachePath`）。hash の 64 文字 string と path string で、`PackageCacheHit` 880 B の 57% を占める。.NET の file API は `string` path しか受け付けないため、**1 key = 1 file という物理形式を保つ限りこれ以上は下がらない**。次の大きな削減は parser ではなく P3（単一 index file 化）にある。
- **source は evidence が支配的**。`SourceOneCached` 1,872 B のうち cache 読み取りは 856 B で、残り約 1,000 B は `SourceRepositoryEvidence` の 9 本の所有 string と candidate 配列の再確保である。前者は text 出力では一切使われないため、**evidence の遅延生成**が次の対象になる（P2 相当、本計画では未着手）。
- `EnrichDuplicateCachedTarget`（64 component が 1 target を共有）が -6% に留まったのは、この benchmark の費用が cache 読み取り 1 回ではなく component ごとの reconcile（P2-1）だからである。1 component あたり約 1 KB が残っている。

## 目標値

当初の目標と P0 実測の対比。未達分は[残りの内訳](#未達の目標と残りの内訳)に費用の所在を記録した。

| 指標 | 実装前 | P0 目標 | **P0 実測** | P1/P2/P3 後（目標） |
|---|---:|---:|---:|---:|
| `EnrichmentFixedCost.PackageOneCached` | 7,792 B | ≤ 600 B | **960 B** | ≤ 450 B |
| `EnrichmentFixedCost.SourceOneCached` | 4,728 B | ≤ 700 B | **1,872 B** | ≤ 900 B |
| `E2E.ScanTextWithCachedMetadata` | 10.94 KB | ≤ 3.5 KB | **4.27 KB** | ≤ 2.5 KB |
| `E2E.ScanNuGetTextWithCachedMetadata`（3 component） | 23.32 KB | ≤ 6 KB | **9.17 KB** | ≤ 4 KB |
| `SourceRepositoryEnrichment.EnrichDuplicateCachedTarget` | 67.53 KB | ≤ 20 KB | **63.71 KB** | ≤ 12 KB |
| CLI 起動固定費 | 約 324 KB | 変化なし | 変化なし | ≤ 200 KB（P2-2） |

P2-2 の hash 遅延化により、起動固定費は約 324 KB → **約 291 KB**（JSON 出力時のみ 33 KB を後から支払う）。残りはほぼ `SpdxLicenseIndex` の構築で、UTF-8 テーブル化が未着手。

P0 目標の見積もりは、cache 読み取り単体の下限（prototype 425 B）から外挿しており、**component ごとに必ず残る所有 string（evidence・candidate）を数えていなかった**。cache 読み取り自体は目標に近い水準（package 880 B / source 856 B）に達している。次の削減対象は parser ではなく、path 構築（P3）と evidence の生成時期（P2）である。

## 検証

1. 各段階で `dotnet test` を先に通す（test-first。cache の互換性・検証ロジックの等価性は既存テストで固定されている）
2. 変更ごとに対応する benchmark を再実行し、直前の baseline と比較する
   - P0-1 / P0-2 / P0-3 → `EnrichmentFixedCostBenchmark`、`E2EBenchmark`
   - P1-1 / P1-2 → `SourceRepositoryEnrichmentBenchmark`
   - P1-3 / P2-1 → `E2EBenchmark`、`DependencyInputScannerBenchmark`
   - P2-2 → E2E に現れないため、**プロセス起動を含む測定を別途用意する**
3. cache 読み取り単体の benchmark として `CacheReadBenchmark`（hit / miss / path 構築）を追加した。**P0 の計測のための一時的なもの**であり、目標が `EnrichmentFixedCostBenchmark` と `E2EBenchmark` で維持できると確認できた時点で削除してよい（ファイル冒頭に同じ注記がある）
4. 説明できない mean / allocated の悪化は却下する

## やらないこと

- 出力（report、rendered text、evidence の所有 string）のアロケーション削減。CLI の成果物であり削減対象ではない
- cache 形式・検証規則の変更（P3 として保留）。**検証の厳密さを性能のために緩めない**
- enrichment の重複排除の作り直し（実装済み）
- LINQ / regex の導入、hot path への抽象層追加
