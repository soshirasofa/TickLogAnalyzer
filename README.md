更新日時: 2026-05-07 18:23 JST

# TickLogAnalyzer

`.tlog` の tick 密度、期間、価格・spread、timestamp の偏りを確認し、必要に応じて検証用 `.tlog` を分割・切り出しする CLI ツールである。Simulator 専用ではなく、live 運用で取得した `.tlog` の確認にも使用する独立 tool である。

主な用途は以下である。

- SimRunner / LiveRunner の処理 cadence 検討用に tick 分布を確認する。
- `.tlog` 全体の統計を console または JSON で出力する。
- 任意 bucket 幅で histogram CSV を作る。
- 日付単位、任意期間、固定時間幅で `.tlog` を分割する。

## 実行方法

`TickLogAnalyzer` ディレクトリで実行する。

```powershell
dotnet run --project . -- <command> [options]
```

使用可能なコマンドは以下である。

```powershell
dotnet run --project . -- --help
dotnet run --project . -- summary --help
dotnet run --project . -- histogram --help
dotnet run --project . -- split-day --help
dotnet run --project . -- slice --help
dotnet run --project . -- split-window --help
```

## summary

`.tlog` 全体の統計を表示し、同じ内容を JSON に保存する。`--json-out` 未指定時は現在ディレクトリに `{tlog basename}.summary.json` を出力する。

```powershell
dotnet run --project . -- summary --tlog C:\ticks\XAUUSDm.tlog
dotnet run --project . -- summary --tlog C:\ticks\XAUUSDm.tlog --json-out C:\out\summary.json
dotnet run --project . -- summary --tlog C:\ticks\XAUUSDm.tlog --cadence-ms 1,10,33,100
```

```pwsh
dotnet run --project ~\Documents\cAlgo-dev\MT5\TickLogAnalyzer\TickLogAnalyzer.csproj -- summary --tlog ~\Documents\cAlgo-dev\MT5\Mt5.Simulator\capture\20260426_152546\XAUUSDm\20260426_220203_XAUUSDm_Exness-MT5Trial5.tlog --cadence-ms 1,10,33,100,250,500 --json-out .\tickobsv.json
```

出力内容は、symbol、broker、source kind、digits、tick size、price scale、record count、first / last tick、duration、平均 tick/sec、bid / ask min max、spread percentile、timestamp 単調性、重複 timestamp 数、大きな gap、高密度 window、cadence 別の概算観測率である。

`--large-gap-ms` で large gap として列挙する最小 gap を変更できる。既定値は `1000` である。

summary には JST 時間帯別の tick 密度も含める。1分 bucket を JST の hour-of-day でまとめ、total ticks、avg ticks/min、p95 ticks/min、max ticks/min、p95 ticks/sec、max ticks/sec を表示する。

## histogram

指定 bucket 幅で tick 件数と価格統計を集計し、CSV に保存する。`--csv-out` 未指定時は現在ディレクトリに `{tlog basename}.histogram.{bucket}.csv` を出力する。

```powershell
dotnet run --project . -- histogram --tlog C:\ticks\XAUUSDm.tlog --bucket 1m
dotnet run --project . -- histogram --tlog C:\ticks\XAUUSDm.tlog --bucket 5m --csv-out C:\out\histogram.csv
```

対応 bucket は以下である。

- `1s`
- `10s`
- `1m`
- `5m`
- `15m`
- `1h`

CSV には bucket start / end UTC、tick count、tick/sec、first / last bid ask、bid / ask high low、spread avg / p95 / max、max timestamp gap ms を出力する。

## split-day

指定 timezone の日付境界で `.tlog` を分割する。元ファイルは変更しない。出力先には symbol subdirectory を作る。

```powershell
dotnet run --project . -- split-day --tlog C:\ticks\XAUUSDm.tlog --out C:\out\daily --timezone UTC
dotnet run --project . -- split-day --tlog C:\ticks\XAUUSDm.tlog --out C:\out\daily --timezone Asia/Tokyo
```

出力ファイル名は以下である。

```text
{yyyyMMdd}_{symbol}_{broker}.tlog
```

既存ファイルがある場合は失敗する。上書きする場合は `--overwrite` を指定する。書き込み予定だけ確認する場合は `--dry-run` を使う。

```powershell
dotnet run --project . -- split-day --tlog C:\ticks\XAUUSDm.tlog --out C:\out\daily --timezone Asia/Tokyo --dry-run
```

## slice

`from <= tick < to` の範囲で `.tlog` を切り出す。`--from` は必須で、`--to` または `--hours` のどちらか一方を指定する。

```powershell
dotnet run --project . -- slice --tlog C:\ticks\XAUUSDm.tlog --from 2026-04-28T00:00:00Z --to 2026-04-28T06:00:00Z --out C:\out\slices
dotnet run --project . -- slice --tlog C:\ticks\XAUUSDm.tlog --from 2026-04-28T09:00:00 --hours 3 --timezone Asia/Tokyo --out C:\out\slices
```

offset 付き日時はそのまま UTC に変換する。offset なし日時は `--timezone` のローカル時刻として解釈する。該当 tick が 0 件の場合、終了コードは `2` で、ファイルは作らない。

出力ファイル名は以下である。

```text
{fromUtc:yyyyMMdd_HHmmss}_{toUtc:yyyyMMdd_HHmmss}_{symbol}_{broker}.tlog
```

## split-window

固定時間幅で連続分割する。既定では first tick を起点にする。`--align day` を指定すると、指定 timezone の日付境界を起点にする。

```powershell
dotnet run --project . -- split-window --tlog C:\ticks\XAUUSDm.tlog --window 6h --out C:\out\6h
dotnet run --project . -- split-window --tlog C:\ticks\XAUUSDm.tlog --window 6h --align day --timezone Asia/Tokyo --out C:\out\6h
```

対応 window は以下である。

- `1h`
- `2h`
- `3h`
- `4h`
- `6h`
- `12h`

## 書き込み時の仕様

split / slice 系のコマンドは入力 `.tlog` を上書きしない。出力 `.tlog` は入力 header の symbol、broker、source kind、tick size、digits を維持し、`sessionStartMs` は出力ファイル内の first tick にする。

入力 `.meta.json` が存在する場合は、同名 sidecar として出力先へコピーする。価格系列は `.tlog` を正とする。

既存ファイルは既定で上書きしない。上書きする場合は `--overwrite` を指定する。
