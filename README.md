# Wattif GA

A local-first C# / Avalonia desktop application for exploring electricity usage, Residential Service energy charges, and what-if scenarios. The GUI uses MVVM, with CSV/storage, rate calculation, and presentation concerns in separate files and projects.

**Implemented scope:** Residential Service and Nights & Weekends are calculated using the rate illustrations supplied with this project. All seven Georgia Power plan names appear in the application; the five unimplemented plans never receive fabricated prices or compete for a recommendation. Recommendations compare only the implemented plans.

## Run

Install the .NET 10 SDK, then run from this directory:

```powershell
dotnet restore WhatIfGa.sln
dotnet run --project WhatIfGa
```

The application targets Windows through Avalonia Desktop. The source is cross-platform; macOS and Linux packaging and behavior require validation on those systems. Initial NuGet restore requires internet access. Normal application operation performs no network requests.

Use **Load sample year** to explore a clearly labeled, synthetic year of 8,760 hourly readings, or **Import usage file** to select a local `.csv` or `.xlsx` file. Import errors and data-quality warnings are shown in the app. Imported data is saved locally and restored when the application starts again. Microsoft Excel does not need to be installed.

Run the console regression suite (no additional test-framework packages) with:

```powershell
dotnet run --project WhatIfGa.Tests
```

The suite contains 37 grouped checks and returns a nonzero exit code on failure. It covers tariff tiers, billing boundaries, rounding, plan selection, scenario comparisons, CSV/XLSX usage and bill history imports, local persistence, cancellation, and the five-second annual comparison target for the implemented Residential Service plan.

The same suite also exercises view-model/controller workflows without opening a window: demo initialization, valid and rejected imports, independent scenario history, sparse chart overlays, upper-date forecast handling, and restored coverage warnings.

## Import formats

Both `.csv` files and ordinary `.xlsx` workbooks are supported. Each row represents **one hour's consumption in kWh**, using a local, whole-hour timestamp. Use a decimal point and no thousands separator. Temperature is optional metadata and does not affect pricing.

The Georgia Power download layout is accepted, including its leading disclaimer and account-number rows:

```csv
"Disclaimer: Usage may be estimated; refer to your bill.",,
Account Number: 0001112223,,
Hour,kWh,Temp
2025-01-01 00:00,1.25,42
2025-01-01 01:00,0.75,41
```

The account number above is fictional. Recognized disclaimer/account metadata before the header is skipped, and account identifiers are not stored in the local usage snapshot or included in the validation report. Unknown preamble content or malformed rows are rejected instead of being silently discarded. `Hour` means a full timestamp in this layout.

These existing layouts remain accepted:

```csv
Timestamp,kWh,Temp
2025-06-01 00:00,1.25,73
2025-06-01 01:00,0.80,72
```

```csv
Year,Month,Day,Hour,kWh,Temp
2025,10,1,0,1.25,63
2025,10,1,1,0.80,62
```

Small importable examples are in `samples/usage-timestamp.csv` and `samples/usage-components.csv`. These examples cover only six hours and will correctly trigger incomplete-history warnings. The demo provides a complete year.

For `.xlsx`, the importer finds the visible worksheet containing a supported usage header; a cover sheet can precede it. More than one matching usage worksheet is rejected to avoid silently selecting the wrong history. Text timestamps, shared/inline strings, sparse optional temperature cells, and formatted Excel dates in either the 1900 or 1904 date system are supported. Formula cells, Excel error cells, external worksheet relationships, malformed archives and unsupported workbook types (`.xls` and `.xlsm`) are rejected. Workbook formulas and links are never executed. The reader uses bounded ZIP/XML processing built into .NET, with no Excel automation or cloud conversion.

To verify two downloaded exports contain exactly the same normalized readings and yield the same Residential Service estimate, run the optional local check:

```powershell
dotnet run --project WhatIfGa.Tests -- --verify-georgia-power 'C:\path\usage.csv' 'C:\path\usage.xlsx'
```

This check reads both source files without copying their contents into the project and prints aggregate counts/usage rather than account metadata.

Validation rejects malformed quoting, missing required columns/values, invalid dates or hours, negative/non-numeric consumption, duplicate timestamps, oversized files, and data exceeding the supported one-year range. Hourly consumption is bounded at 100,000 kWh, files at 10 MiB, and record count at 8,784. Rows are sorted by timestamp. Missing hours and partial calendar months produce warnings; the app does not invent readings or extrapolate missing consumption. The local-time schema cannot distinguish the repeated hour when daylight saving time ends; exports containing duplicate local hours must be normalized before import. Sub-hourly data must likewise be aggregated to hourly readings.

## Rate assumptions

These are the **supplied illustration's rates**, not a live tariff feed:

| Season | Billing months | Energy tier per period | Price per kWh |
| --- | --- | --- | --- |
| Winter | October–May | All usage | $0.082 |
| Summer | June–September | First 650 kWh | $0.088 |
| Summer | June–September | Next 350 kWh | $0.146 |
| Summer | June–September | Above 1,000 kWh | $0.151 |

For example, 1,200 kWh in July costs `650 × .088 + 350 × .146 + 200 × .151 = $138.50` in estimated energy charges. The same consumption in January costs `$98.40`.

In **Bill details → Billing periods**, use calendar months, an estimated monthly closing day, or actual periods from your bills. For actual periods, enter one row per period in `YYYY-MM-DD,YYYY-MM-DD,YYYY-MM` format, such as `2025-06-15,2025-07-15,2025-07`. The start date is included and the end date is excluded at local midnight. Estimated closing days use the last day of shorter months. Apply the schedule to save it locally and recalculate estimates. Overview and Bill details share the selected plan.

**Import bill history** fills the period editor from a CSV or XLSX export with `Date` and `Description` columns. Consecutive `Bill` dates provide the boundaries, and the closing date supplies the billing month. Payment rows are skipped. The oldest bill date anchors the first period because the preceding date is unavailable. Review the inferred dates and select **Apply billing periods** to use them.

Summer tiers reset for each billing period. The billing month determines which season applies. Partial periods use recorded consumption and the full tier thresholds. The app shows coverage and reports readings outside the applied periods. Coverage accounts for the Eastern spring clock change. The original and modified usage in each scenario use the same boundaries. Each period subtotal is rounded to cents using `MidpointRounding.AwayFromZero`.

Estimates **exclude** basic service/customer charges, fuel recovery, taxes, franchise fees, riders, and other adjustments. Bill details explicitly show these limitations. Historical estimated costs and what-if estimates replay the supplied readings under this rate baseline. The next-year same-period forecast replays each recorded month's usage in the corresponding month one year later, at the same rates, with no weather, growth, or behavior adjustment. Missing history is not extrapolated. These projections are not predictions of future tariffs; year 9999 falls back to the recorded period because a later date cannot be represented.

The [Georgia Power residential rate-plan page](https://www.georgiapower.com/residential/rate-plans.html) identifies the seven plan names used in the registry:

| Plan | Implementation |
| --- | --- |
| Residential Service | Seasonal monthly energy calculation implemented |
| Smart Usage | Placeholder; time-of-use and demand pricing pending |
| Overnight Advantage | Placeholder; time-of-use pricing pending |
| Nights & Weekends | Implemented from the supplied off-peak/on-peak illustration |
| FlatBill | Placeholder; requires a personalized utility quote |
| PrePay | Placeholder; applicable pricing/program rules pending |
| Pay by Day | Placeholder; requires a personalized fixed daily quote |

“Available estimate” or a recommendation refers only to implemented rates. Nights & Weekends uses the supplied illustration: $0.078/kWh off-peak and $0.303/kWh on-peak, Monday–Friday 2–7 p.m. from June through September, excluding observed U.S. federal holidays. The illustration does not enumerate holidays; the observed federal holiday calendar is the explicit assumption used here. All other hours, weekends, and holidays use the off-peak rate. New `RatePlan` subclasses can join the registry when their complete pricing rules and fixtures are available.

## What-if behavior

Scenarios apply a global percentage reduction and optionally move a percentage of usage from a selected hour window by a chosen number of hours. The source window includes its start and excludes its end; a window whose end is earlier than its start crosses midnight. Equal start/end selects the full day. Shifted energy wraps **within the same calendar day**, merges with readings already at the target hour, and preserves total kWh before any reduction. This convention prevents shifts from changing the month, season, or historical coverage.

Original readings remain intact. Run independent settings against the original dataset to evaluate alternative strategies. The app displays the modified estimate and its difference from the original. Residential Service has no time-of-day component, so moving usage alone does not reduce its energy charge; reducing consumption can reduce it. Time shifting will become financially relevant when time-of-use plans are implemented.

## Architecture and privacy

| Component | Responsibility |
| --- | --- |
| `WhatIfGa.Core/Models/UsageData.cs` | Typed hourly readings and calendar fields |
| `WhatIfGa.Core/Models/RatePlan.cs` and subclasses | Extensible tariff strategies |
| `WhatIfGa.Core/Models/BillEstimate.cs`, `MonthlyBill.cs`, `BillLineItem.cs` | Transparent monthly estimates and tier details |
| `WhatIfGa.Core/Models/UsageScenario.cs` | Immutable-output usage transformations and cost differences |
| `WhatIfGa.Core/Services/DataManager.cs` | CSV/XLSX import dispatch and local JSON persistence |
| `WhatIfGa.Core/Services/XlsxUsageReader.cs` | Bounded native OOXML worksheet extraction without executing formulas |
| `WhatIfGa.Core/Services/RateCalculator.cs` | Plan comparison, available-plan recommendation, and scenario estimates |
| `WhatIfGa/Services/AppController.cs` | Application orchestration and cancellable background work |
| `WhatIfGa/ViewModels/` | Bindable state and commands |
| `WhatIfGa/Views/` | Avalonia layouts and native file picker |
| `WhatIfGa/Controls/` | Interactive chart rendering |
| `WhatIfGa.Tests/` | Repeatable regression and performance checks |

The MVC class diagram's `AppController` role is retained as an application service; `MainViewModel` provides the MVVM bridge between that service and `MainWindow`. Storage and calculation code have no Avalonia dependency. Expensive work runs away from the UI thread and accepts cancellation.

Imported data is stored in `%LOCALAPPDATA%/WhatIfGa/usage-data.json` on Windows, using the corresponding .NET local application-data directory on other systems. Writes use a temporary file and atomic replacement. The saved document is versioned and validated when loaded; corrupt data is reported instead of crashing. The app makes no uploads, telemetry calls, or external account connections. Local storage is ordinary JSON protected by the operating system's user-account permissions, not application-level encryption. Removing the file while the app is closed clears saved readings.

Set the optional `WHATIFGA_DATA_DIRECTORY` environment variable to choose a different **local** storage directory, for example when running a separate test/demo profile:

```powershell
$env:WHATIFGA_DATA_DIRECTORY = 'C:\Users\YourName\AppData\Local\WhatIfGa-Test'
dotnet run --project WhatIfGa
```

The app writes `usage-data.json` in that directory. This setting changes the storage location; it does not enable an upload or cloud integration. Leave it unset to use the normal per-user directory.

## Requirements coverage and remaining work

| Requirements | Current coverage |
| --- | --- |
| FR1–FR3 | CSV/XLSX import/validation and local persistence |
| FR4–FR6 | Two implemented tariffs and five visible placeholders; recommendations compare the implemented plans only |
| FR7 | Monthly/tier energy-charge details with exclusions shown |
| FR8 | Interactive historical usage and estimated-cost charts; historical replay rather than an independent forecast model |
| FR9–FR10 | Usage reduction/time shifting with original-versus-modified estimates |
| NFR1 | Annual benchmark in regression suite; future tariff implementations must retain the target |
| NFR2–NFR3 | GUI workflow, readable validation errors/warnings, guarded asynchronous operations |
| NFR4–NFR6 | Local-only data, separated C# layers, Avalonia desktop target |
| NFR7 | Deterministic calculation and validation regression classes |
| NFR8 | Background operations, busy state, and cancellation |

Matching full utility bills requires the applicable tariff version, service charges, riders, and taxes. Select any listed plan to see its availability. Residential Service provides the energy estimates in this release.
