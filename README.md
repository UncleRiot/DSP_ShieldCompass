# DSP Shield Compass · RIOTdock

A compact, in-game guide for placing **8 planetary shield generators** in *Dyson Sphere Program*.

## Installation

Requires **BepInEx 5**. Copy `DSPShieldCompass.dll` into `Dyson Sphere Program/BepInEx/plugins/` and start the game.

## Controls & Features

| Action | How / What it does |
|---|---|
| Open RIOTdock | Hover over the icons on the right screen edge |
| Toggle compass | Click the shield/compass icon |
| Move UI | Left-drag the dock header, compass panel, or arrow |
| Settings | Click ⚙ to adjust dock and compass text/UI scale separately |
| Mark positions | Click a checklist row; progress is saved per planet |
| Check extra shields | Toggle **Find extra shields** to list and navigate to off-layout or duplicate generators |
| Hide dock | Select **Hide dock**; restore it using the small edge tab |
| Hotkeys | None required; dock and compass are mouse-controlled |

## Quick Start

**1. Open RIOTdock.** Hover over the right-edge icons and select Shield Compass.

<img width="260" height="112" alt="grafik" src="https://github.com/user-attachments/assets/fbe5e4db-29aa-4403-801f-dc5bb37701d7" />


or


<img width="1879" height="1018" alt="grafik" src="https://github.com/user-attachments/assets/9c785a1c-2f34-4aeb-8131-a741b12201e1" />



**2. Follow the checklist.** Place shields at both poles, then at six equatorial points spaced **60°** apart. Built positions are detected automatically; rows can also be marked manually.

<img width="298" height="510" alt="grafik" src="https://github.com/user-attachments/assets/1325c819-e267-4329-9e88-68e65f03e634" />


**3. Adjust the interface.** Open ⚙ to set separate text sizes and UI scales.

<img width="260" height="349" alt="grafik" src="https://github.com/user-attachments/assets/a1d25fe5-230f-4503-8dab-767af16e6c2e" />


### Arrow Colors

| Icon | Status | Meaning |
|---|---|---|
| <img width="45" height="51" alt="grafik" src="https://github.com/user-attachments/assets/481e6683-ee78-4f03-b98b-9779a2fa684f" />
 | **OFF TARGET** | Not at a planned shield position; follow the arrow |
| <img width="45" height="51" alt="grafik" src="https://github.com/user-attachments/assets/748daca7-fb1a-4868-97a4-5777a7cdeeac" />
 | **BUILD SPOT OK** | At the next free shield position |
| <img width="45" height="51" alt="grafik" src="https://github.com/user-attachments/assets/c8f0f600-8cbb-442b-a687-a2384b1f66c5" />
 | **SHIELD POSITION DONE** | At an already completed shield position |

*Off-layout detection compares generators to the selected 8-point layout; it does not measure actual shield coverage.*
