dotnet run --project "$PSScriptRoot\.." `
    -- split-day `
    --tlog "C:\ticks\XAUUSDm.tlog" `
    --out "C:\out\daily" `
    --timezone "Asia/Tokyo" `
    --dry-run
