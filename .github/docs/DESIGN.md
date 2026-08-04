# Library Design

Ol aims to make the license state of every OSS dependency explainable, reproducible, and enforceable.
We pursue complete transitive dependency visibility, evidence-based conclusions without guessing, deterministic reports, policy evaluation independent of collection, and a small high-performance CLI that runs well anywhere.

This document defines the design principles that make Ol what it is. The system structure and boundaries are documented in [Architecture.md](Architecture.md); user-facing behavior for individual feature specs are documented in [specs/](specs/).

## Principles

### Complete Dependency Visibility

We resolve the complete dependency inventory before filtering or policy evaluation.
Root, direct, transitive, and unknown relationships remain explicit so an incomplete graph or a convenient view cannot silently hide OSS use.

### Evidence, Never guesswork

We preserve raw claims, normalized SPDX expressions, provenance, disagreements, and collection failures from every evidence source.
We normalize strictly against versioned SPDX data and report uncertainty rather than turning vague text or source precedence into false certainty.

### Facts Before Policy

Evidence collection answers what the available sources say; policy answers whether that result is acceptable to an organization.
The same factual report can be evaluated repeatedly under different policies without rescanning dependencies or depending on current network state.

### Deterministic and Explainable Results

The same inputs and selected data produce the same ordered result.
Canonical JSON retains the complete facts needed by automation, while text and Markdown remain projections that a person can review and trace back to their evidence.

### Best Effort Without Hidden Failure

A failure for one component or evidence source must not erase usable results for the rest of the inventory.
Uncertainty remains visible in the report, while failures that prevent a trustworthy complete result are explicit command failures.

### Privacy at Every Persistence Boundary

Reports, caches, baselines, and diagnostics retain enough logical provenance for audit without exposing credentials, absolute private paths, or hidden cache locations.
Credentials are explicit inputs and remain confined to their intended authority.

### Performance Is Measured

We optimize complete usage paths based on representative measurements, not assumptions.
The core favors explicit data, bounded work, minimal allocation, and reusable results while preserving evidence and policy semantics.

### API-Driven Development

We design an API that feels natural and pleasant to use, then determine how to implement it without compromising that experience or performance. The API should let users start with a single line of code and progressively move down to lower-level control when needed.

### Native and Portable by Design

Ol remains suitable for a small Native AOT CLI and ordinary offline use.
Runtime behavior does not depend on development-time generators, implicit network access, reflection-heavy design, or dynamic code generation.

---

# ライブラリデザイン

Olは、すべてのOSS依存関係について、そのライセンス状態を説明可能、再現可能、かつポリシーとして強制可能にすることを目指します。
推測に頼らない証拠ベースの結論、推移的依存関係を含む完全な可視性、決定論的なレポート、収集から独立したポリシー評価、そしてどこでも快適に動作する小さく高性能なCLIを追求します。

この文書は、OlがOlであり続けるためのデザイン原則を定義します。システムの構造と境界は[Architecture.md](Architecture.md)に、個々の機能仕様における利用者向けの振る舞いは[specs/](specs/)に記録します。

## 原則

### 完全な依存関係の可視化

フィルタリングやポリシー評価を行う前に、依存関係の完全なインベントリを解決します。
不完全なグラフや表示上の都合によってOSSの利用が暗黙に隠れないよう、ルート、直接、推移的、不明の関係を明示的に保持します。

### 証拠に基づき、決して推測しない

すべての証拠ソースについて、生の主張、正規化されたSPDX式、来歴、不一致、収集失敗を保持します。
バージョン管理されたSPDXデータに対して厳密に正規化し、曖昧な文言やソースの優先順位を誤った確実性へ変換せず、不確実性として報告します。

### ポリシーより先に事実

証拠収集は利用可能なソースが何を示しているかを答え、ポリシーはその結果を組織として許容できるかを答えます。
同じ事実レポートを、依存関係の再スキャンや現在のネットワーク状態に依存せず、異なるポリシーで繰り返し評価できます。

### 決定論的で説明可能な結果

同じ入力と選択されたデータから、同じ順序の結果を生成します。
Canonical JSONは自動化に必要な完全な事実を保持し、textとMarkdownは、人が確認し証拠まで遡れる同一結果の投影とします。

### 隠れた失敗のないベストエフォート

1つのコンポーネントや証拠ソースの失敗によって、残りのインベントリから利用可能な結果を失ってはなりません。
不確実性はレポートに残し、信頼できる完全な結果を妨げる失敗は明示的なコマンド失敗とします。

### すべての永続化境界でプライバシーを守る

レポート、キャッシュ、ベースライン、診断には、認証情報、絶対プライベートパス、隠されたキャッシュ位置を公開することなく、監査に十分な論理的来歴を保持します。
認証情報は明示的な入力とし、意図した権限範囲の外へ持ち出しません。

### 性能は計測する

推測ではなく、代表的な利用経路の計測結果に基づいて最適化します。
コアは証拠とポリシーの意味を維持しながら、明示的なデータ、境界のある処理、最小限のアロケーション、再利用可能な結果を優先します。

### API駆動開発

自然で気持ちよく使えるAPIを先に設計し、その体験や性能を損なわずに実装する方法を考えます。
1行で使い始められ、必要に応じて低レベルな制御へ段階的に降りられるAPIを提供します。

### ネイティブかつポータブルな設計

Olは、小さなNative AOT CLIとして、また通常のオフライン環境でも利用できる状態を保ちます。
実行時の振る舞いを、開発時のジェネレーター、暗黙のネットワークアクセス、リフレクションに大きく依存する設計、動的コード生成に依存させません。
