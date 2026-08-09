# `ol` の配布名と npm / Homebrew packaging 計画

## この文書の位置付け

`ol` の製品名と実行コマンドを維持しながら、NuGet、Homebrew、npm の名前空間衝突を避ける方針と実装順序を定める。

NuGet と Homebrew は対応済みである (`Formula/ol.rb`、`.github/workflows/homebrew-formula.yaml`、fully qualified install への README 更新)。**npm 対応は未着手で、`npm/` directory も存在しない。** この文書は package 名、native binary の格納境界、公開認証、release verification を決めるが、npm package が公開済みであることを示すものではない。

調査日は 2026-08-06 とする。registry の公開状態と外部仕様は実装着手時に再確認する。npm の名前取得状況は時間とともに変わるため、Phase 2 で必ず再確認する。

## 結論

製品名と利用者が実行するコマンドは、全配布経路で `ol` のままにする。registry ごとの package identifier は同一である必要がない。

| Distribution | Package / formula identifier | Installed command | Status |
|---|---|---|---|
| GitHub Releases | `ol-<os>-<arch>` assets | `ol` / `ol.exe` | 対応済み |
| NuGet | `ol` | `ol` | 対応済み、名前取得済み |
| Homebrew | `guitarrapc/ol/ol` | `ol` | 対応済み |
| npm | `@guitarrapc/ol` | `ol` | **未着手** |

npm の unscoped `ol` を別名へ変更させようとせず、管理下の scope で衝突を解消する。Homebrew は tap を含む fully qualified formula 名で core の同名 formula と区別する。

## 調査結果

### npm

2026-08-06 に public npm registry を `npm view` で確認した結果は次のとおりだった。

| Name | Result |
|---|---|
| `ol` | 取得済み、確認時 version `10.10.0` |
| `ol-cli` | 取得済み、確認時 version `1.0.5` |
| `@guitarrapc/ol` | public package は見つからない |
| `olscan` | public package は見つからない |
| `oss-license-lens` | public package は見つからない |
| `license-evidence` | public package は見つからない |
| `deplicense` | public package は見つからない |
| `licensift` | public package は見つからない |
| `oss-license-audit` | public package は見つからない |
| `sbom-license-checker` | public package は見つからない |
| `resolved-license` | public package は見つからない |

`npm view` の not found は将来の publish 成功を保証しない。名前は後から取得される可能性があり、npm 側の命名・類似性ルールで拒否される可能性もある。これらの unscoped 候補は調査記録であり、採用候補ではない。

npm scope は user または organization が所有する名前空間であり、他の所有者が同じ scope 内へ package を追加できない。[npm の scope 仕様](https://docs.npmjs.com/about-scopes/)に従い、`guitarrapc` scope の管理権限を確保した上で `@guitarrapc/ol` を採用する。public scoped package は初回 publish 時に public access の明示が必要である。[Creating and publishing scoped public packages](https://docs.npmjs.com/creating-and-publishing-scoped-public-packages/)を実装時の基準とする。

package 名と executable 名は分離できる。main package の `package.json` は少なくとも次の意味を持つ。

```json
{
  "name": "@guitarrapc/ol",
  "bin": {
    "ol": "./bin/ol.js"
  },
  "publishConfig": {
    "access": "public"
  }
}
```

これにより利用者向けの形は次になる。

```sh
npm install --global @guitarrapc/ol
ol --version

npx @guitarrapc/ol --version
```

### Homebrew

`homebrew/core` には `ol` という Lisp implementation の formula が既に存在する。[Homebrew Formulae の `ol`](https://formulae.brew.sh/formula/ol) と project の `ol` は別製品である。

Homebrew は tap 内の formula が core と同名でも、`user/repository/formula` の fully qualified name で選択できる。[Homebrew Taps の duplicate names](https://docs.brew.sh/Taps#duplicate-names)に従い、現在の same-repository tap では次を canonical installation とする。

```sh
brew tap guitarrapc/ol https://github.com/guitarrapc/ol
brew install guitarrapc/ol/ol
```

`brew install ol` は canonical installation としない。core の別製品を選ぶ可能性があり、tap trust の対象も曖昧になる。fully qualified formula の install は個別 formula を明示的に trust する形にも一致する。[Homebrew Tap Trust](https://docs.brew.sh/Tap-Trust)を参照する。

将来、Formula を専用の `guitarrapc/homebrew-tap` repository へ移す場合は、次の一コマンド形式へ変更できる。

```sh
brew install guitarrapc/tap/ol
```

専用 tap への分離は npm 対応の前提ではなく、現在の same-repository formula を破棄する理由にもならない。

### NuGet

`src/Ol/Ol.csproj` は既に次を分離している。

```xml
<PackAsTool>true</PackAsTool>
<ToolCommandName>ol</ToolCommandName>
<PackageId>ol</PackageId>
```

NuGet の `ol` は取得済みであり、既存利用者の install command と package identity を変更しない。

```sh
dotnet tool install --global ol
ol --version
```

## npm native binary の配布境界

[`guitarrapc/scenetake` の npm packaging](https://github.com/guitarrapc/scenetake/tree/main/npm)を参照実装とし、esbuild-style の meta package + platform-specific optional packages を採用する。全 platform binary を一つの package へ同梱する案と、install script で GitHub Releases から取得する案は採用しない。

scenetake は unscoped meta package と製品専用の `@scenetake` scope を使うが、`ol` は unscoped 名が取得済みであり、`@guitarrapc` は共有 scope である。この差だけを反映し、meta package を `@guitarrapc/ol`、platform packages を `@guitarrapc/ol-<platform>-<arch>` とする。

現行 release は Linux x64 / arm64、Windows x64 / arm64、macOS x64 / arm64 の Native AOT artifact を生成する。npm 対応はこれらと別の binary を build せず、同じ tag から生成された release artifact を七つの npm packages へ組み立てる。

| Package | Role | Contents |
|---|---|---|
| `@guitarrapc/ol` | 利用者が install / `npx` する meta package | `bin/ol.js`、README、platform packages の exact-version `optionalDependencies` |
| `@guitarrapc/ol-linux-x64` | Linux x64 | `ol` |
| `@guitarrapc/ol-linux-arm64` | Linux arm64 | `ol` |
| `@guitarrapc/ol-darwin-x64` | macOS x64 | `ol` |
| `@guitarrapc/ol-darwin-arm64` | macOS arm64 | `ol` |
| `@guitarrapc/ol-win32-x64` | Windows x64 | `ol.exe` |
| `@guitarrapc/ol-win32-arm64` | Windows arm64 | `ol.exe` |

各 platform package は Node の値と一致する `os` / `cpu` を一つずつ宣言し、`files` には executable と package metadata だけを含める。Native AOT binary が archive 内で追加 runtime file を必要とすることが判明した場合だけ、その target package の `files` へ明示的に追加する。package manager が matching package を通常の filesystem へ展開できるよう、scenetake と同様に `preferUnplugged: true` を宣言する。

meta package は六 packages を同じ exact version で `optionalDependencies` に列挙し、対応する host package だけを npm の `os` / `cpu` selection に選ばせる。version range は使わない。install-time selection と runtime launcher は次の同じ写像を共有する。

| `process.platform` | `process.arch` | Optional package |
|---|---|---|
| `linux` | `x64` | `@guitarrapc/ol-linux-x64` |
| `linux` | `arm64` | `@guitarrapc/ol-linux-arm64` |
| `darwin` | `x64` | `@guitarrapc/ol-darwin-x64` |
| `darwin` | `arm64` | `@guitarrapc/ol-darwin-arm64` |
| `win32` | `x64` | `@guitarrapc/ol-win32-x64` |
| `win32` | `arm64` | `@guitarrapc/ol-win32-arm64` |

### install script を使わない理由

`preinstall`、`install`、`postinstall` で binary を download または配置しない。npm v12 は dependency install scripts を default-deny とし、global install や `npx` でも利用者による明示許可が必要になる。[npm config の `allow-scripts`](https://docs.npmjs.com/cli/using-npm/config/#allow-scripts)と [npm v12 changelog](https://github.com/npm/cli/blob/latest/CHANGELOG.md)を前提に、install scripts が無効でも完全に動く package とする。

この構成には次の性質がある。

- install 時の追加 host、redirect、checksum、archive extraction を package script に持ち込まない。
- npm registry から取得した packages だけで install が完了する。
- lifecycle scripts を許可しない環境でも global install と `npx` が動く。
- 利用しない五 platform 分の binary を取得しない。
- publish 前の tarball inspection で実際に配置される executable を監査できる。

## npm launcher の契約

`@guitarrapc/ol` の `bin.ol` は、scenetake と同じ CommonJS の小さな launcher とする。launcher は次だけを担当する。

1. `process.platform` と `process.arch` を既知の六 target へ写像する。
2. `createRequire` による Node module resolution で platform package と固定 executable path を解決する。
3. 全引数と標準入出力を native `ol` へ渡す。
4. native process の終了コードまたは signal termination を呼び出し元へ反映する。
5. unsupported platform、missing optional package、壊れた package を区別できる短い error と非 0 exit code を返す。

launcher は dependency resolution、license logic、update check、binary download を行わない。domain behavior は Native AOT executable に一元化する。

## 公開と supply-chain boundary

npm の staged publishing と GitHub Actions の OIDC trusted publishing を最終形とする。CI は七 packages を `npm stage publish --access public --provenance` で非公開の stage へ送り、maintainer が 2FA で内容を確認して approve する。長期 token から直接 public publish しない。npm の [Trusted publishing](https://docs.npmjs.com/trusted-publishers/)は GitHub-hosted runner、`id-token: write`、対応する npm / Node version を要求する。

scenetake と同様に、stage publish は既存 package を前提とする。初回だけ maintainer が 2FA を使い、platform packages 六つを先、meta package を最後に `npm publish --access public` する。その直後に七 packages それぞれへ同じ GitHub Actions workflow の trusted publisher を設定し、許可 action は `npm stage publish` に限定する。CI 用の長期 write token を恒久運用しない。

release job は Git tag の version を唯一の version source とし、次を守る。

- GitHub Release job が生成した同一 artifact を download し、npm 用に binary を再 build しない。
- `checksums-sha256.txt` と照合してから package へ格納する。
- platform packages 六つを先、meta package を最後に stage する。approve も platform packages を先に完了し、同じ exact version が public registry に存在することを確認してから meta package を approve する。
- npm version は immutable である前提で、retry は既存の同一 version を検証して skip できるようにする。異なる bytes を同じ version として再 publish しようとしない。
- `package.json` の `repository.url` は trusted publishing の対象 GitHub repository と一致させる。
- publish 後に registry から clean install して verification を行う。

## 実装順序

### Phase 1: Homebrew 対応 (実装済み、実機検証だけ残る)

formula 生成、artifact 選択、`ol` の install、fixture による render regression test、release 後の formula 更新 workflow、README の fully qualified install はすべて実装済みである。残るのは自動化できない実機検証だけで、これは Phase 2 以降の前提ではない。

- [ ] 公開済み release を使い、macOS arm64 / x64 と Linux arm64 / x64 で `brew install guitarrapc/ol/ol`、`ol --version`、`brew test guitarrapc/ol/ol` を検証する。
- [ ] core の `ol` が未 install / install 済みの両状態で挙動を確認し、同時 link を支援しない場合はその制約を README に明記する。

### Phase 2: npm identity を確保する

- [ ] npm の `guitarrapc` user または organization scope の所有権を確認する。
- [ ] maintainer account の 2FA と recovery path を確認する。
- [ ] `@guitarrapc/ol` が未公開であることを再確認する。
- [ ] package metadata の `name`、`license`、`repository`、`homepage`、`bugs`、`engines`、`files`、`bin`、`publishConfig.access` を固定する。
- [ ] 七 packages の初回 manual publish と trusted publisher bootstrap の手順を repository 外の maintainer operation として記録する。

### Phase 3: scenetake 方式の npm package layout を実装する

- [ ] `npm/packages/ol` と六つの platform package directory を追加する。binary は commit しない。
- [ ] meta package の六 `optionalDependencies` と全七 package version を exact に同期する version check / update を追加する。
- [ ] 各 platform manifest の `os`、`cpu`、`files`、`preferUnplugged` を固定する。
- [ ] launcher の platform mapping、argument forwarding、exit code、signal、unsupported platform、missing binary を test-first で固定する。
- [ ] GitHub Release archives を一時 directory へ展開し、各 manifest と binary を配置する `npm/scripts/assemble-packages.sh` 相当を追加する。
- [ ] `npm pack --dry-run` で package contents を固定し、source、cache、CI credential、不要な release files が入らないことを検証する。
- [ ] 七 packages の manifest に `preinstall`、`install`、`postinstall` が存在しないことを検証する。

### Phase 4: release workflow へ npm staged publishing を追加する

- [ ] GitHub-hosted runner と対応 Node / npm version を pin する。
- [ ] job permission を `contents: read` と `id-token: write` に限定する。
- [ ] tag version と全 package version の一致を検証する。
- [ ] release artifact の SHA-256 を検証してから package を組み立てる。
- [ ] 七 packages を `npm pack` し、target host の meta + platform tarballs から local install して `ol --version` と `ol --help` を実行する。
- [ ] trusted publishing で platform packages 六つ、meta package の順に `npm stage publish --access public --provenance` する。
- [ ] workflow summary に stage IDs と、platform packages を先に approve する手順を表示する。
- [ ] maintainer が 2FA で platform packages を approve し、registry 上の version を確認してから meta package を approve する。
- [ ] registry から `@guitarrapc/ol@<version>` を clean install し、実際の native command を実行する。
- [ ] 七つの npm package pages に provenance と repository link が表示されることを確認する。

### Phase 5: platform verification と文書化

- [ ] Linux x64 / arm64、Windows x64 / arm64、macOS x64 / arm64 の各 native runner で global install と `npx` を検証する。
- [ ] `--version`、`--help`、代表的な `scan`、policy violation exit、invalid input exit の forwarding を検証する。
- [ ] README / README-ja に npm global install と `npx` の例を追加する。
- [ ] release summary に NuGet、npm、GitHub Release、Homebrew formula update の各結果を分けて表示する。
- [ ] npm publish failure が NuGet や GitHub Release の状態を偽って rollback しないことを確認し、partial release の再実行手順を文書化する。

## 完了条件

1. 製品名と installed command は全経路で `ol` のままである。
2. Homebrew は core の同名 formula を誤選択せず、fully qualified formula から既存 release binary を install できる。
3. npm は `@guitarrapc/ol` から対応 platform の既存 Native AOT binary を取得し、install-time download なしで `ol` を実行できる。
4. npm package version、Git tag、NuGet version、GitHub Release artifact version が一致する。
5. npm package に含まれる binary は release checksum で検証済みであり、CI は OIDC trusted publishing を使う。
6. 六 target の install と command / exit forwarding が自動検証される。

## 非目標

- `ol` 自体を Node.js で再実装しない。
- npm 対応のために CLI command、report schema、license evaluation semantics を変更しない。
- unscoped npm package 名の取得交渉や既存 package の移譲を前提にしない。
- Homebrew core の既存 `ol` formula の rename や移譲を求めない。
- 初期 npm 対応と同時に専用 Homebrew tap repository へ移行しない。
