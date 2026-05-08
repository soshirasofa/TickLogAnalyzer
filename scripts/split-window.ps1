dotnet run --project "$PSScriptRoot\.." `
    -- split-window `
    --tlog "C:\ticks\XAUUSDm.tlog" `
    --window "6h" `
    --out "C:\out\6h" `
    --align "day" `
    --timezone "Asia/Tokyo" `
    --dry-run
