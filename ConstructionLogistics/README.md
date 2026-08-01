# Construction Logistics

Turns newly completed Parkitect attractions into temporary, visually staged construction projects.

## Current behaviour

- Existing rides in loaded saves are ignored.
- New attractions are detected after the player finishes editing them.
- Construction time scales with attraction price and visual footprint, from one to six gameplay minutes.
- The attraction is forced closed during construction.
- Ride-shaped striped safety barriers, shorter posts, spaced perimeter cones, warning lamps, staged materials, scaffolding, and soft alpha-blended dust mark the site. Sparse entrance-only tile reports fall back to a convex visual footprint instead of an oversized rectangle.
- Entrance and exit approaches receive automatic barrier gaps, with construction materials staged beside the entrance rather than across it.
- Site dressing changes as construction advances, while a clean circular progress ring shows percentage and time remaining.
- The progress indicator centres over the complete site and hides where Parkitect UI is occupying its screen position.
- The attraction itself fades smoothly from 0% to 100% opacity as construction advances, using isolated copies on a reliable transparent shader; Parkitect's original ride materials and shaders are restored on completion.
- Opening or editing an attraction does not reset or pause its active construction timer.
- A completion notification announces when the attraction is ready to test or open.
- Pausing the game pauses construction progress.

Version 0.3.1 does not yet delay terraforming, block path graph connections, add construction workers, or persist unfinished projects through save/load. Multiplayer is disabled.
