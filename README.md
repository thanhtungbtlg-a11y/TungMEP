# Tung MEP

Standalone Autodesk Revit 2024 add-in collection targeting .NET Framework 4.8.

## Current modules

- Auto Sprinkler: sprinkler grid placement, obstacle-aware branch routing, and physical pipe generation.
- Boundary line style: `TungMEP_SprinklerBoundary`.
- Bundled default family: `TungMEP Test Sprinkler`, a level-based Sprinklers family that does not require walls, rooms, ceilings, or hosts.
- Grid Mode: switch selected or visible grids between 2D and 3D extents.
- Grid Bubble Arrange: detect overlapping visible Grid bubbles and stagger them with view-specific leaders while preserving the Grid model line and its native references.
- Draft to Legend: duplicate a Legend seed and copy the active Drafting View contents with chosen name and scale.
- More Tools: payload-driven launcher for productivity commands that can grow without adding a new ribbon button each time.
- TungMEP Control: dedicated ribbon panel containing Repeat Last TungMEP and Reload TungMEP. Repeat Last reruns the most recently completed command and can be assigned a native Revit keyboard shortcut.
- Align Objects by BOP: move selected straight MEP curves and supported MEP family instances vertically so their model bottom elevations share one project BOP.
- Equal Spacing: distribute three or more selected model objects by center point along view X/Y, world Z, or the detected row direction while keeping the first/last extents or using an entered spacing.
- VG Filter from Selection: create a native multi-category Revit Parameter Filter from a common selected-object parameter/value and optionally apply its visibility/halftone override to the active view.
- Schedule from Selection: create and open a native Revit schedule for one selected category, add useful fields, and optionally filter it by a common same-value parameter chosen from the selected objects.
- MEP Project Health Dashboard (pyRevit): read-only project/view/selection audit for open MEP connectors, missing Pipe/Duct systems, invalid sizes, unusual offsets, missing active-view tags, and related Revit warnings; produces an HTML-style report with clickable Select/Zoom links and breakdowns by category, Level, and System.
- pyRevit Companion (optional): searchable TungMEP launcher, Repeat Last, payload reload, and installation diagnostics using the installed pyRevit runtime; the C# add-in remains fully independent.
- Auto Dimension: creates native Revit dimension chains by selection/scan line, dimensions sprinkler rows, or dimensions one sprinkler to the nearest Structural Column/Wall boundaries in view X and Y. It remembers the last settings and never deletes existing dimensions.
- MEP SLIPT BY LEVEL: split selected straight Pipe, Duct, Conduit, and Cable Tray elements where they cross selected project Level planes. Only intersecting Levels are offered; split locations touching Pipe/Duct/Conduit/Cable Tray fittings or Pipe/Duct accessories are skipped using a configurable safety clearance. Optional union fittings keep successful breaks physically connected where the loaded routing preferences support them.
- Batch View Copy: duplicate multiple views using Duplicate, With Detailing, or As Dependent plus batch naming rules.
- Sheet Renumber: prechecks Sheets selected before the command, presents Sheets in a searchable number-prefix folder tree, and safely previews/renumbers with prefix, suffix, start, increment, and zero padding.
- Smart Auto Tag MEP: creates native Pipe tags, scores tag/model overlap and leader crossings, optionally applies the Documentation Rail optimizer, updates an optional BOP-FFL pipe parameter, and never deletes existing tags.
- Universal Auto Tag MEP: tags multiple host-model family categories in one run (Plumbing Fixtures, Pipe/Duct Accessories, Mechanical Equipment, Air Terminals, Sprinklers, electrical categories, Specialty Equipment, and Generic Models), with a native Tag Family Type per category, duplicate protection, preview rollback, and optional documentation-grade leader optimization.
- Smart Annotation Arrange: aligns, distributes, compacts, smart-rails, or reflows existing Independent Tags and Text Notes. Documentation Rail searches multiple rails, stack shifts, and source orders to minimize label/model overlap and leader crossings.
- Smart MEP Annotation Pack: creates native Pipe, Duct, and Mechanical Equipment tags, optionally groups matching parallel runs into a multi-leader tag, optimizes the result on model-aware rails, and can add native grid-reference and elevation-to-level dimensions.
- View & Sheet Automation: creates Plan, RCP, and Scope-Box-based Section X/Y views for multiple Levels, with a live preview of final conflict-resolved View names, Sheet numbers/names, and viewport batches before creation.

## Build

Run `build.ps1`. It produces the stable `bin\net48\TungMEP.Loader.dll` and a versioned payload in `bin\net48\payload`.

If pyRevit is installed, run `install-pyrevit-companion.ps1` to copy the optional companion into `%APPDATA%\pyRevit\Extensions\TungMEP.extension`, then reload pyRevit once. The normal installer performs this step automatically only when pyRevit is detected.

Run `Tung MEP QA > Project QA > MEP Health Dashboard` to audit the full project, the active view, or the current selection. The pyRevit tools deliberately use their own `Tung MEP QA` tab so a pyRevit Reload can never replace the native C# panels on the `Tung MEP` tab. Missing-tag checks are intentionally limited to the active view because native Revit tags are view-specific. The dashboard never changes the model; use each `Select/Zoom` result link to review an issue before fixing it.

After the loader has been installed and Revit restarted once, run `deploy-payload.ps1` to build and copy a new payload while Revit stays open. Then click `Reload TungMEP` on the Ribbon (or run a TungMEP command directly) to use the latest payload. If the loader/ribbon itself changed, the deploy script stages a hash-versioned loader and updates the manifest automatically; restart Revit once to display the new ribbon.

The downloaded Reliable manufacturer library remains under `content\Reliable_Quick_Response` for future production use. The unhosted test family is copied to `bin\net48\content\AutoSprinkler` during the build.

## Auto Sprinkler workflow

1. Open a floor plan or ceiling plan.
2. Run `Boundary Style` once to create the dedicated line style.
3. Use Revit `DL` to draw one or more closed loops and assign `TungMEP_SprinklerBoundary` in Properties.
4. Optionally select the loops to process only those boundaries.
5. Run `Auto Sprinkler`.

The default branch elevation is `2340 mm` above the active Level. The user can change it in the command settings. Existing generated systems are never deleted automatically; remove or undo an old test layout explicitly before rerunning the same area if duplicates are not wanted.

Aligned branch mode uses a closed `TungMEP_SprinklerBoundary` and one straight `TungMEP_SprinklerMain` Detail Line. The main guide controls the grid direction and must span the region where branch takeoffs are required. Sprinklers are placed only on a common orthogonal grid; the free-point repair behavior is no longer used. The tool generates blue perpendicular branch guides and automatically creates red 90-degree detours when a straight branch would cross a concave excluded part of the boundary. Coverage is still sampled and reported for checking.

Only user-drawn Detail Lines using the exact `TungMEP_SprinklerObstacle` line style are treated as routing obstacles. Model and linked-model columns/beams are deliberately ignored. Closed obstacle loops and recoverable open outline chains are excluded from sprinkler placement and branch routing with the configured obstacle-line clearance; blue/red branch guides remain generated output and are never user inputs.

If an obstacle blocks the initial run from the main guide to the first head, the branch takeoff is shifted along the main guide to the nearest clean side and returned to the aligned sprinkler row with a red orthogonal detour. If the green main guide itself crosses an obstacle, the result dialog reports a warning because the user-defined main route must also be moved or rerouted.

The command uses a two-step dialog flow. The first dialog contains layout parameters only. After confirmation, a separate Family/Type picker lists compatible loaded level-based Sprinkler types, starts with no selection, and requires an explicit user choice before placement can begin.

## Auto Dimension workflow

Open `Tung MEP > Utilities > More Tools > Auto Dimension` in a plan, section, elevation, or detail view.

- `By Line` now has two input methods. `Select objects by rectangle` is the default: preselect 2+ supported objects or drag a rectangle after `OK`, then review a live ghost of the final chain/overall dimensions—including formatted segment values, dimension lines, ticks, and witness lines—before clicking to place it. The final native dimensions use the clicked position directly unless overlap avoidance must move them to a free lane. `Draw scan line` keeps the previous workflow for special cases and uses the same live result preview.
- `Sprinkler`: preselect or rectangle-select sprinkler heads. The command dimensions head spacing along branch rows, spacing between rows, and overall extents. It detects the dominant branch direction from visible branch pipes; when that direction is ambiguous or no pipe is available, pick two points along a branch.
- `Single sprinkler to nearest column/wall (X + Y)`: preselect or pick one sprinkler, then click once in the desired placement quadrant. Each view axis is resolved independently on both sides of the head: a nearby outer Structural/Architectural Column face is preferred when its spatial distance is shorter than the nearest Wall-face distance; otherwise the nearest Interior/Exterior Wall face is used. A 500 mm lateral capture zone lets a nearby column face win even when the finite column outline is slightly offset from the exact X/Y ray. The command uses real surfaces—not element location lines—and searches both the current model and visible loaded Revit links.
- Offset and lane step are paper-space millimetres and are converted using the active view scale. Existing dimensions are never deleted or modified, exact duplicate reference sets are skipped, and separate sprinkler regions are processed independently.
- `Outer planar faces` uses native planar reference faces. When a round Pipe/Duct has no usable planar side faces, the command automatically falls back to its native centerline and reports the fallback count instead of silently skipping the element.

## Smart Auto Tag MEP workflow

Open `Tung MEP > Utilities > More Tools > Smart Auto Tag MEP` in a Floor Plan, Engineering Plan, or Ceiling Plan.

- Tag the current selection, drag a rectangle, or process all visible host-model Pipes in the active view.
- Select any loaded native Revit `Pipe Tag Type`. The Tag Family controls which fields are shown, such as System Abbreviation, Diameter, Slope, and a shared BOP-FFL value.
- Tag heads are tested in multiple paper-space lanes on the least congested side. Existing tags, text, dimensions, and visible pipe geometry are treated as occupied space. Existing manual tags are never deleted or moved.
- Select a Revit Leader Arrowhead Type in the dialog. TungMEP safely duplicates the selected Tag Type when needed and assigns that arrowhead, so existing tags using the original type are not changed.
- The optional `TungMEP_BOP_FFL` pipe parameter is updated before tagging. Double parameters receive the true Revit length value; text parameters receive `BOP-FFL+/-<mm>`.
- The last scope, Tag Type, spacing, leader, side, and BOP settings are remembered per Windows user.

## Universal Auto Tag MEP workflow

Open `Tung MEP > Utilities > More Tools > Universal Auto Tag MEP` in a Floor Plan, Engineering Plan, or Ceiling Plan. Enable one or more MEP categories, choose a loaded native Tag Family Type for each category, select the scope and placement settings, then review the preview before applying. Pipe and Duct runs remain handled by Smart MEP Annotation Pack.

- Use the current selection, drag a rectangle, or process all visible enabled-category family instances in the active view.
- Enable any combination of Plumbing Fixtures, Pipe Accessories, Mechanical Equipment, Air Terminals, Duct Accessories, Sprinklers, Electrical Equipment/Fixtures, Lighting Fixtures, Fire Alarm Devices, Specialty Equipment, and Generic Models. Each category remembers its own loaded native Tag Family Type.
- The tool measures the real tag size, tries left/right/top/bottom paper-space lanes, avoids visible primary building/MEP geometry and annotation bounds, and penalizes crossing leaders.
- Select a Leader Arrowhead Type in the dialog. Each generated tag uses a protected TungMEP duplicate of its selected Tag Type when needed, so the arrowhead does not alter existing manual tags.
- Already tagged elements can be skipped. Existing manual tags are never deleted or moved. Preview can keep the complete result or roll it back as one operation.

## Smart Annotation Arrange workflow

Open `Tung MEP > Utilities > More Tools > Smart Annotation Arrange` in a plan, RCP, section, elevation, detail, or drafting view.

- Modes: Align only, Distribute evenly, Compact stack, Smart rail, and Reflow after edit.
- Rail options: automatic, left, right, top, bottom, split into left/right rails by source clusters, preserve the current left/right grouping, or an orthogonal rail picked with two points.
- Customize near/center/far alignment, preserved/straight/orthogonal leaders, source/current/ID sorting, or Nested leader fan sorting for clean section/elevation equipment callouts; plus reverse order, paper-space item gap, rail offset, obstacle clearance, column gap, and maximum items per column.
- Independent Tags and Text Notes are supported. Smart obstacle avoidance keeps text clear of visible walls, floors, structural framing/columns, primary MEP elements, unselected annotations, and dimensions. Leader/arrow endpoints are preserved by default; pinned annotations can be locked or temporarily moved and repinned.
- Optional preview applies the whole layout temporarily and lets the user keep it or restore every original position. A kept result is one Revit Undo operation.

## Smart MEP Annotation Pack workflow

Open `Tung MEP > Utilities > More Tools > Smart MEP Annotation Pack`.

- Select Pipes, Ducts, and/or Mechanical Equipment and choose one native Tag Family Type for each enabled category.
- Matching parallel runs with the same System, size, slope, and direction can be represented by one native multi-leader tag. Equipment remains individually tagged.
- The selected Tag Family controls displayed fields. Use a family containing the required System, DN/size, BOP/TOP, and Slope labels.
- New tags can be automatically split between two nested rails, packed with paper-space spacing, kept clear of visible model elements, and previewed before commit.
- Orthographic documentation options can optimize white space/leader crossings, dimension representative MEP elements to the nearest compatible grid, and create elevation-to-level dimensions in section/elevation views. Dimensions are created only from stable native Revit references.

## View & Sheet Automation workflow

Open `Tung MEP > Utilities > More Tools > View & Sheet Automation`.

- Select one or more Levels and create Floor/Engineering Plans, RCPs, Section X, and/or Section Y views. Sections use the selected Scope Box as their zone and vertical Level band.
- Choose separate View Family Types and View Templates for Plans, RCPs, and Sections; apply a naming pattern with `{Level}`, `{Type}`, `{Zone}`, and `{Index}` placeholders.
- Review the live table before creation. It shows the final conflict-resolved View names, proposed Sheet numbers/names, and the View batch assigned to each Sheet.
- Optionally create sheets with a selected Title Block, safe prefix/start/padding numbering, and 1-4 viewports per sheet. Viewports are centered and evenly distributed on a 1x1, 1x2, or 2x2 grid.

## Sheet and Grid cleanup workflow

- Before opening `Sheet Renumber`, select Sheets in Project Browser when possible. Those Sheets are checked automatically. The left tree follows the current Revit Browser Organization for Sheets; search or choose a folder, then use the right table to review the complete numbering preview.
- Run `Grid Bubble Arrange` from More Tools after preselecting Grids, or with no Grid selected to use all visible Grids. Set a minimum paper-space center spacing; only colliding visible bubbles are staggered using 2D Grid leaders. The model Grid lines and their references are not translated.

## Installer

Build the per-user Revit 2024 installer with `installer\build-installer.ps1`. It installs the stable loader plus the current payload under `%LOCALAPPDATA%\TungMEP\Revit2024`, creates the current user's Revit manifest under `%APPDATA%\Autodesk\Revit\Addins\2024`, and registers an uninstaller in Windows Apps & Features. Close Revit before installation, upgrade, or removal.

For portable installation on another machine, copy the whole project folder, close Revit, then run `powershell -ExecutionPolicy Bypass -File .\install-portable.ps1` from that folder. The target machine must have Revit 2024 installed.

## Add another tool

1. Create a folder under `src\Modules`.
2. Implement the Revit `IExternalCommand`.
3. Add its command type to the payload-driven `ProductivityToolsCommand` launcher when a dedicated ribbon button is unnecessary.
4. Add a proxy/button in `src\Loader\LoaderApplication.cs` only when the command needs a dedicated ribbon button.
5. `build.ps1` automatically compiles every `.cs` file under `src\Modules`; loader/proxy changes under `src\Loader` require one Revit restart after deployment.
