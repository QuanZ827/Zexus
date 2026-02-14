# Tool Reference

Zexus has **20 registered tools** (active) plus **11 reserve tools** (code preserved, available for re-activation). All tools implement the `IAgentTool` interface.

## Registered Tools (20)

### Query Tools (5)

#### GetModelOverview
Model statistics — element counts by category, levels with elevations, view counts by type, linked models.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `include_views` | boolean | No | Include view statistics (default: true) |
| `top_categories` | integer | No | Number of top categories to show (default: 20) |

**Example prompt:** *"What's in this model?"* / *"How many elements are there?"*

---

#### SearchElements
Find elements by category, family, type, level, or parameter value. Returns element IDs for use with other tools.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `category` | string | No | Category name (e.g. "Walls", "Doors") |
| `family_name` | string | No | Family name (partial match) |
| `type_name` | string | No | Type name (partial match) |
| `level_name` | string | No | Level name filter |
| `parameter_filter` | object | No | `{"name": "Mark", "value": "A-001", "operator": "equals"}` |
| `match_selected` | boolean | No | Find elements matching selected element's type |
| `max_results` | integer | No | Maximum results (default: 100) |

**Example prompt:** *"Find all doors on Level 1"* / *"How many cable trays have Mark starting with 'CT'?"*

---

#### GetParameterValues
Show the distribution of values for a parameter across a category (unique values + counts).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `category` | string | Yes | Category name |
| `parameter_name` | string | Yes | Parameter name to analyze |
| `show_element_ids` | boolean | No | Include element IDs per value (default: false) |

**Example prompt:** *"What Mark values do walls have?"* / *"Show department distribution for rooms"*

---

#### GetSelection
Read the user's current Revit selection. No parameters required.

**Example prompt:** *"What did I select?"* / *"Tell me about these elements"*

---

#### GetWarnings
Model warnings grouped by type, with affected element IDs.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `include_element_ids` | boolean | No | Include element IDs (default: true) |
| `max_warnings` | integer | No | Maximum warnings to return (default: 100) |

**Example prompt:** *"Are there any warnings?"* / *"Show model issues"*

---

### Action Tools (4)

#### SelectElements
Select and highlight elements in Revit, with optional zoom-to-fit.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `element_ids` | array | Yes | Element IDs to select |
| `zoom_to_fit` | boolean | No | Zoom to show selected elements (default: true) |
| `clear_previous` | boolean | No | Clear previous selection (default: true) |

**Typical chain:** SearchElements → SelectElements

---

#### IsolateElements
Temporarily isolate or hide elements in the active view. Does not modify the model.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `element_ids` | array | No | Elements to isolate/hide (not needed for reset) |
| `mode` | string | No | `"isolate"`, `"hide"`, or `"reset"` (default: isolate) |

**Example prompt:** *"Show only the selected walls"* / *"Reset visibility"*

---

#### SetElementParameter
Modify one parameter on one element. **Write operation — requires confirmation.**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `element_id` | integer | Yes | Element ID to modify |
| `parameter_name` | string | Yes | Parameter name |
| `value` | string | Yes | New value |

**Example prompt:** *"Set the Mark of element 12345 to 'A-001'"*

---

#### ActivateView
Open/switch to any view by name, ID, or sheet number. Works for floor plans, schedules, sheets, 3D views, sections, legends, and drafting views.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `view_name` | string | No | View name (exact or fuzzy match). For sheets, also matches sheet number. |
| `view_id` | number | No | Element ID (takes priority over name) |
| `view_type` | string | No | Filter: `"FloorPlan"`, `"Schedule"`, `"Sheet"`, `"ThreeD"`, etc. |

**Example prompt:** *"Open the door schedule"* / *"Switch to sheet A101"* / *"Show Level 1 floor plan"*

---

### Schedule Tools (6)

#### CreateSchedule
Create a new schedule for a Revit category. **Write operation.**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `category` | string | Yes | Category name (e.g. "Walls", "Doors", "Rooms") |
| `name` | string | No | Custom schedule name |
| `fields` | array | No | Field names to add immediately after creation |

**Example prompt:** *"Create a room schedule with Name, Number, and Area"*

---

#### AddScheduleField
Add, remove, reorder, or list columns in a schedule. **Write operation for add/remove/reorder.**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `schedule_name` | string | Yes | Schedule name |
| `mode` | string | Yes | `"list"`, `"add"`, `"remove"`, `"reorder"` |
| `field_name` | string | Depends | Required for add/remove/reorder |
| `column_header` | string | No | Custom column header (for add) |
| `hidden` | string | No | `"true"` to add as hidden field |
| `position` | number | No | Target position for reorder (0-based) |

**Example prompt:** *"Add a Level column to the door schedule"* / *"Move Mark to the first position"*

---

#### FormatScheduleField
Set column width, alignment, header text, bold/italic. **Write operation for set mode.**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `schedule_name` | string | Yes | Schedule name |
| `mode` | string | Yes | `"list"` or `"set"` |
| `field_name` | string | For set | Column to format |
| `column_heading` | string | No | New header text |
| `alignment` | string | No | `"Left"`, `"Center"`, `"Right"` |
| `width_mm` | number | No | Column width in millimeters |
| `hidden` | string | No | `"true"` / `"false"` |
| `bold` | string | No | `"true"` / `"false"` |
| `italic` | string | No | `"true"` / `"false"` |

**Example prompt:** *"Make the Name column 50mm wide and bold"*

---

#### CreateProjectParameter
Create a new project parameter and bind to categories. **Write operation.**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `parameter_name` | string | Yes | Parameter name |
| `parameter_type` | string | Yes | `"Text"`, `"Integer"`, `"Number"`, `"Length"`, `"Area"`, `"Volume"`, `"YesNo"`, etc. |
| `categories` | string | Yes | Comma-separated names or `"ALL"` |
| `is_instance` | string | No | `"instance"` or `"type"` (default: instance) |
| `group` | string | No | UI group for organization |

**Example prompt:** *"Create a text parameter called 'QC Status' for Walls and Doors"*

---

#### ModifyScheduleFilter
Add, remove, clear, or list filters on a schedule. 14 filter operators supported.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `schedule_name` | string | Yes | Schedule name |
| `mode` | string | Yes | `"list"`, `"add"`, `"remove"`, `"clear"` |
| `field_name` | string | For add | Field to filter by |
| `operator` | string | For add | `"Equal"`, `"NotEqual"`, `"GreaterThan"`, `"Contains"`, `"BeginsWith"`, `"HasValue"`, etc. |
| `value` | string | For add | Filter value |
| `filter_index` | integer | For remove | Index of filter to remove |

**Example prompt:** *"Filter the schedule to show only Level 1 elements"*

---

#### ModifyScheduleSort
Add, remove, clear, or list sort/group definitions with headers and footers.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `schedule_name` | string | Yes | Schedule name |
| `mode` | string | Yes | `"list"`, `"add"`, `"remove"`, `"clear"` |
| `field_name` | string | For add | Field to sort by |
| `sort_order` | string | No | `"ascending"` or `"descending"` |
| `show_header` | string | No | `"true"` / `"false"` (group header) |
| `show_footer` | string | No | `"true"` / `"false"` (group footer with totals) |
| `blank_line` | string | No | `"true"` / `"false"` (blank line between groups) |
| `itemize_every_instance` | string | No | `"true"` / `"false"` |
| `sort_index` | integer | For remove | Index of sort to remove |

**Example prompt:** *"Group the schedule by Level with headers"*

---

### Output Tools (4)

#### ListSheets
List all sheets with number, name, title block, placed view count, and current revision.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name_filter` | string | No | Filter by name (partial match) |
| `number_filter` | string | No | Filter by sheet number prefix |

---

#### ListViews
List views (not sheets) with name, type, level, and printable status.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name_filter` | string | No | Filter by name |
| `view_type` | string | No | `"FloorPlan"`, `"Section"`, `"ThreeD"`, `"Schedule"`, etc. |
| `printable_only` | boolean | No | Only printable views (default: false) |

---

#### PrintSheets
Print sheets to PDF. **Execution tool — requires confirmation.**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `sheet_numbers` | array | Yes | Sheet numbers to print |
| `output_path` | string | Yes | File or folder path |
| `combined` | boolean | No | One combined PDF vs. separate PDFs (default: true) |
| `paper_size` | string | No | `"auto"`, `"A0"`–`"A4"`, `"Letter"`, `"Tabloid"` |
| `orientation` | string | No | `"auto"`, `"landscape"`, `"portrait"` |
| `color_mode` | string | No | `"color"`, `"grayscale"`, `"monochrome"` |

**Example prompt:** *"Print all E-series sheets to PDF in A3 landscape"*

---

#### ExportDocument
Export to DWG, DXF, IFC, NWC, images (PNG/JPG/TIFF), or CSV. **Execution tool — requires confirmation.**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `format` | string | Yes | `"dwg"`, `"dxf"`, `"ifc"`, `"nwc"`, `"png"`, `"jpg"`, `"tiff"`, `"csv"` |
| `output_folder` | string | Yes | Destination folder |
| `sheet_numbers` | array | No | Sheets to export (DWG/DXF/NWC) |
| `view_ids` | array | No | Views to export (images) |
| `schedule_name` | string | No | Schedule name (CSV) |
| `dwg_version` | string | No | `"2013"` or `"2018"` |
| `ifc_version` | string | No | `"IFC2x3"` or `"IFC4"` |
| `image_resolution` | integer | No | DPI (default: 300) |

---

### Universal Tool (1)

#### ExecuteCode
Compile and run arbitrary C# code inside Revit via Roslyn. The AI writes the method body of:

```csharp
public static object Execute(Document doc, UIDocument uiDoc, StringBuilder output) { ... }
```

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `code` | string | Yes | C# method body |
| `description` | string | No | Brief description |

**When to use:** Anything not covered by the 19 predefined tools — spatial queries, batch operations, complex filtering, view creation, etc.

---

## Reserve Tools (11 — not registered)

These tools exist in the codebase but are **not registered** in `ToolRegistry`. They can be re-enabled by adding them to `ToolRegistry.CreateDefault()`. They were deactivated because ExecuteCode can handle their use cases with acceptable reliability, and keeping the tool count low reduces LLM decision complexity and token cost.

| Tool | Purpose | Reason not registered |
|------|---------|----------------------|
| `GetElementDetails` | Single-element detailed info | ExecuteCode handles well |
| `GetCategoryParameters` | List parameters for a category | ExecuteCode handles well |
| `GetElementParameters` | All parameters of one element | ExecuteCode handles well |
| `GetAllSheets` | Detailed sheet listing | Replaced by lighter `ListSheets` |
| `GetViewsOnSheet` | Views placed on a specific sheet | Low frequency, ExecuteCode handles well |
| `GetNearbyElements` | Spatial proximity search | Low frequency, ExecuteCode handles well |
| `CheckMissingParameters` | Find empty parameter values | ExecuteCode handles well |
| `AnalyzeNamingPatterns` | Value distribution + anomalies | ExecuteCode handles well |
| `FindSimilarValues` | Typo detection via edit distance | Low frequency, ExecuteCode handles well |
| `ColorElements` | Visual override (ColorSplash) | Complex API, candidate for re-activation |
| `BatchSetParameter` | Batch parameter writes | Candidate for re-activation |

### Re-activation Criteria

A reserve tool should be promoted to registered status when:
1. **ExecuteCode success rate drops below 70%** for that operation pattern
2. **Usage frequency exceeds 10%** of sessions
3. **User feedback** indicates the operation is too slow or unreliable via ExecuteCode

## Tool Design Principles

### What Makes a Good Atomic Tool

1. **Single responsibility** — One tool, one action. If it needs modes, each mode operates on the same resource (e.g., AddScheduleField's list/add/remove/reorder all target schedule fields).

2. **Minimal parameters** — 2–4 required parameters, no more than 8 total. LLMs get confused by too many options.

3. **Helpful error messages** — When a tool fails, tell the LLM what to do next. Example: *"Schedule 'X' not found. Use ListViews to find available schedules."*

4. **Write safety built-in** — Description includes `"WRITE OPERATION"` so the LLM knows to confirm with the user.

5. **Zero token cost** — Using a predefined tool costs zero tokens for code generation (vs. 500–2000 tokens for ExecuteCode).

### Adding a New Tool

See [CONTRIBUTING.md](../CONTRIBUTING.md) for the step-by-step guide:

1. Implement `IAgentTool` in `Tools/YourTool.cs`
2. Register in `ToolRegistry.CreateDefault()`
3. Add to system prompt table in `AgentService.BuildSystemPrompt()`
4. Add thinking chain mapping in `ChatWindow.xaml.cs`
5. Build both targets (`net48` + `net8.0-windows`)
