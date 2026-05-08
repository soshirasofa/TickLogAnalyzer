dotnet run --project "$PSScriptRoot\.." `
    -- slice `
    --tlog "C:\ticks\XAUUSDm.tlog" `
    --from "2026-04-28T00:00:00Z" `
    --to "2026-04-28T06:00:00Z" `
    --out "C:\out\slices"
