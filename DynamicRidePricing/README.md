# Dynamic Ride Pricing

This Parkitect mod reprices every open, tested ride using:

- excitement rating as the base price;
- current queue length as the demand signal;
- rain, storms, temperature, and ride weather protection;
- a session popularity score that decays when recent activity is low.

Shops and facilities are not included because Parkitect stores them separately
from its attraction list.

## Behaviour

- First pricing pass: 15 seconds after loading a park.
- Later pricing passes: every 60 real-time seconds.
- The mod runs quietly without notifications or routine log spam.
- A ride may change by at most $0.50 in one pass.
- All open, tested attractions are changed, including flat rides and tracked rides.
- Excitement uses Parkitect's native 0-1 scale and becomes the base currency
  price at a factor of 10 (for example, 0.72 becomes 7.20 before multipliers).
- Version 1.0 is single-player only.

Popularity history is session-based and resets to neutral when a park is loaded.

## Installation

1. Download `DynamicRidePricing-1.0.1.zip` from the latest GitHub release.
2. Extract the included `DynamicRidePricing` folder into `Documents\Parkitect\Mods`.
3. Enable **Dynamic Ride Pricing** from Parkitect's Content Manager.

The installed DLL should be located at:

```text
Documents\Parkitect\Mods\DynamicRidePricing\DynamicRidePricing.dll
```

## Building

The project references the assemblies from a local Parkitect installation. Clone
the repository beneath Parkitect's `ModDevelopment` directory or update the
assembly hint paths in `DynamicCoasterPricing.csproj` before building.

```powershell
dotnet build DynamicCoasterPricing.csproj -c Release
```
