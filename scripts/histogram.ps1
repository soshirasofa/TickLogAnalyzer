dotnet run --project "$PSScriptRoot\.." `
    -- histogram `
    --tlog "C:\ticks\XAUUSDm.tlog" `
    --bucket "1m" `
    --csv-out ".\histogram.csv"
