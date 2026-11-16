# Wattif GA

Compare Georgia Power rate plans using hourly electricity readings and explore what-if scenarios.

## Run the downloaded app

After extracting the project ZIP, open the `publish/` folder. Find the archive for your operating system and processor, then extract it before opening the app. Each build includes the .NET runtime.

| Platform | Archive | How to start |
| --- | --- | --- |
| Windows x64 | `WhatIfGa-win-x64.zip` | Double-click `WhatIfGa.exe` in the extracted folder. |
| Mac with Apple silicon | `WhatIfGa-osx-arm64.tar.gz` | Double-click `WhatIfGa.app` in Finder. |
| Mac with Intel processor | `WhatIfGa-osx-x64.tar.gz` | Double-click `WhatIfGa.app` in Finder. |
| Linux x64 | `WhatIfGa-linux-x64.tar.gz` | Open a terminal in the extracted folder and run `./WhatIfGa`. |

If Linux reports a permissions error, run `chmod +x WhatIfGa` and try again. The app uses an X11 or XWayland desktop. [Avalonia Linux dependencies](https://docs.avaloniaui.net/docs/deployment/linux)

## Build and run from source

With the .NET 10 SDK installed, open a terminal in the repository root and run these commands.

```sh
dotnet build WhatIfGa.sln
dotnet run --project WhatIfGa --no-build
```

## Import the Excel workbook

1. Open Wattif GA and select **Import usage**.
2. Select the supplied `.xlsx` workbook wherever you saved it.
3. Use **Overview** to compare estimates. Open **Import report** to check warnings or errors.

The workbook should contain one row per local hour with `Hour` or `Timestamp` and `kWh` columns. `Temp` is optional. You can also import a CSV in the same format. Excel is optional. Imported usage is saved locally. Select **Load sample year** to try the app before importing data.

For a separate bill-history workbook, go to **Bill details → Billing periods → Import bill history**. Review the inferred dates, then select **Apply billing periods**.

## Completed features

- **Usage charts and validation.** View monthly consumption and average usage by hour. The app checks imported CSV and Excel files and flags gaps in the readings.
- **Rate plan comparison.** Compare Residential Service, Smart Usage, Overnight Advantage, and Nights & Weekends using the supplied rates. Smart Usage includes a demand charge based on the highest recorded hour in each billing period.
- **Billing estimates.** Use calendar months, an estimated closing day, or actual bill dates. Bill details show how each charge was calculated and how much of the period is covered by the readings.
- **What-if scenarios.** Model a reduction in electricity use or shift usage to different hours. Compare the resulting costs with the original estimate for the selected plan.

Estimates exclude service charges, fuel adjustments, riders, and taxes. The plan list marks FlatBill, PrePay, and Pay by Day as unavailable.

## AI Use Disclaimer

AI was used to generate the user interface, the import functionality and the algorithms that calculate the Georgia power rate plans. Manual testing was done to verify the accuracy of the calculated output and the integrity of the GUI.
