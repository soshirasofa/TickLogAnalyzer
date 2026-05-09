dotnet run --project "$PSScriptRoot\.." `
    -- summary `
    --tlog "C:\ticks\XAUUSDm.tlog" `
    --cadence-ms "1,10,33,100,250,500" `
    --json-out ".\summary.json"
