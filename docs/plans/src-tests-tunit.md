# Plan: src/ 配下への実装移動 + TUnit テスト追加

## Context

現在 `Program.cs` 1ファイル（864行）にすべてのロジックが集約されている。  
テスト可能な構造にするため、純粋な計算・パース・フォーマット処理を `internal static` クラスとして `src/` に分離し、`tests/` 配下に TUnit テストプロジェクトを追加する。

---

## 最終ディレクトリ構造

```
TickLogAnalyzer/              ← ソリューションルート
├── Program.cs                ← エントリポイント（3行のまま残す）
├── TickLogAnalyzer.csproj    ← InternalsVisibleTo を追加
├── TickLogAnalyzer.slnx      ← tests プロジェクト参照を追加
├── src/
│   ├── Models.cs             ← 全 record 型（TickLogHeader 等）
│   ├── TickParsing.cs        ← internal static class TickParsing
│   ├── TickStats.cs          ← internal static class TickStats
│   ├── TickFormatting.cs     ← internal static class TickFormatting
│   └── Commands.cs           ← TickLogAnalyzerCommands（委譲のみ）
└── tests/
    └── TickLogAnalyzer.Tests/
        ├── TickLogAnalyzer.Tests.csproj
        ├── TickParsingTests.cs
        └── TickStatsTests.cs
```

---

## 分離する内容

### `src/Models.cs`
- `TickLogHeader`, `TickLogData`, `TickData`, `TickGroup`
- `SummaryDocument`, `TimestampGap`, `DensityWindow`, `HourlyDensityRow`
- `TickDensitySummary`, `CadenceEstimate`, `HistogramRow`

### `src/TickParsing.cs` — `internal static class TickParsing`
- `ParseDurationMs` / `ParseBucket` / `ParseCadences`
- `ParseInstant` / `HasExplicitOffset`
- `ResolveTimeZone` / `ResolveWindowOriginMs`

### `src/TickStats.cs` — `internal static class TickStats`
- `PercentileSorted`
- `BuildTimestampGaps` / `BuildDensityBuckets`
- `BuildCadenceEstimates` / `BuildHourlyDensity`
- `BuildHistogram` / `BuildSummary`

### `src/TickFormatting.cs` — `internal static class TickFormatting`
- `Csv` / `SanitizeFileNamePart`
- `FormatNumber` / `FormatPercent`
- `ResolveAnalysisOutputPath` / `HasDirectorySeparatorSuffix`

### `src/Commands.cs`
- `TickLogAnalyzerCommands` を残し、上記クラスへ委譲するよう更新

---

## テストプロジェクト設定

```xml
<!-- tests/TickLogAnalyzer.Tests/TickLogAnalyzer.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="TUnit" Version="*" />
    <ProjectReference Include="..\..\TickLogAnalyzer.csproj" />
  </ItemGroup>
</Project>
```

メインプロジェクトに追加:
```xml
<ItemGroup>
  <InternalsVisibleTo Include="TickLogAnalyzer.Tests" />
</ItemGroup>
```

実行方法: `dotnet run --project tests/TickLogAnalyzer.Tests`

---

## テスト内容（優先度順）

### 優先度 1: `TickParsing` — `ParseDurationMs`
| ケース | 期待値 |
|--------|--------|
| `"1s"` | 1_000 |
| `"10s"` | 10_000 |
| `"1m"` | 60_000 |
| `"5m"` | 300_000 |
| `"1h"` | 3_600_000 |
| `"100ms"` | 100 |
| `"0s"` | ArgumentException |
| `"abc"` | ArgumentException |
| `"1x"` | ArgumentException |

### 優先度 2: `TickStats` — `PercentileSorted`
| ケース | 期待値 |
|--------|--------|
| 空リスト, p50 | 0 |
| 単一要素 [5.0], p50 | 5.0 |
| [1,2,3,4,5], p50 | 3.0 |
| [1,2,3,4,5], p00 | 1.0 |
| [1,2,3,4,5], p100 | 5.0 |
| [1..100], p90 | 90.1 (線形補間) |

### 優先度 3: `TickStats` — `BuildTimestampGaps`
| ケース | 期待値 |
|--------|--------|
| 単一 tick | gaps.Count == 0 |
| 2 ticks (差 500ms) | gaps.Count == 1, GapMs == 500 |
| 3 ticks (等間隔 1000ms) | gaps.Count == 2, 各 GapMs == 1000 |

### 優先度 4: `TickParsing` — `ParseInstant`
| ケース | 期待値 |
|--------|--------|
| `"2024-01-01T00:00:00Z"`, UTC | UTC 2024-01-01T00:00:00Z |
| `"2024-01-01T09:00:00+09:00"`, UTC | UTC 2024-01-01T00:00:00Z |
| `"2024-01-01 09:00:00"`, JST zone | UTC 2024-01-01T00:00:00Z |

---

## 実装手順

1. `src/Models.cs` 作成（record 型を移動）
2. `src/TickParsing.cs` 作成（パース系メソッドを移動）
3. `src/TickStats.cs` 作成（統計系メソッドを移動）
4. `src/TickFormatting.cs` 作成（フォーマット系メソッドを移動）
5. `src/Commands.cs` 作成（`TickLogAnalyzerCommands` を更新して各クラスに委譲）
6. `Program.cs` を元の 3 行エントリポイントのみに整理
7. `TickLogAnalyzer.csproj` に `InternalsVisibleTo` 追加
8. `tests/TickLogAnalyzer.Tests/` プロジェクト作成（TUnit 導入）
9. `TickLogAnalyzer.slnx` に tests プロジェクト追加
10. `TickParsingTests.cs` 作成（ParseDurationMs, ParseInstant テスト）
11. `TickStatsTests.cs` 作成（PercentileSorted, BuildTimestampGaps テスト）
12. `dotnet run --project tests/TickLogAnalyzer.Tests` で全テスト通過確認
13. コミット

---

## 破綻リスク

- `TickLog` パッケージが内部型を使っており `TickData` 等と名前衝突する場合 → 名前空間で解決
- TUnit の最新バージョンで `dotnet run` の起動方法が変わった場合 → バージョンを固定して対応
