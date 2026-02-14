# Atomic Tool Design Principles

This document defines what an "atomic tool" is in Zexus, the criteria for creating one, and the framework for deciding what should become the next tool. It serves as a binding reference for all future development — whether by a human contributor, an AI coding agent, or a new conversation session.

---

## Definition: What Is an Atomic Tool?

An atomic tool is a **single-responsibility, composable, safe, and performant** unit of Revit API functionality that the AI agent can invoke without generating any code.

A valid atomic tool satisfies **all six** of the following properties:

### 1. Single Responsibility

Each tool does **one thing**. The AI agent composes multiple tools to solve complex tasks.

**Rule:** If your tool description contains "and" connecting two unrelated actions, split it into two tools.

**Acceptable "and":** AddScheduleField supports `list`, `add`, `remove`, `reorder` — these are different operations on the **same resource** (schedule fields). The `mode` parameter selects the operation.

**Not acceptable:** A tool that "searches elements **and** sets their parameters" — these are two different resources (element collection vs. element data). Split into SearchElements + SetElementParameter.

### 2. Composability

Tool outputs must be directly consumable by other tools. This means:

- **Return structured data**, not just human-readable text. Every tool returns `ToolResult` with a `Data` dictionary.
- **IDs flow downstream.** SearchElements returns `element_ids` → SelectElements consumes `element_ids`. ListSheets returns `sheet_numbers` → PrintSheets consumes `sheet_numbers`. CreateSchedule returns `schedule_name` → ActivateView consumes `view_name`.
- **Counts and summaries enable decisions.** GetParameterValues returns value distribution → AI decides which values to filter on.

**Composability chain examples:**
```
SearchElements → SelectElements → IsolateElements       (find → show → focus)
CreateSchedule → AddScheduleField → ModifyScheduleSort → ActivateView  (build schedule end-to-end)
ListSheets → PrintSheets                                (discover → execute)
GetParameterValues → ModifyScheduleFilter               (understand data → filter view)
```

**Test:** If your tool's output cannot be used by any other tool, it is either a terminal action (like ActivateView) or it lacks structured output (fix it).

### 3. Write Safety (Preview → Confirm → Execute)

Every tool that modifies the Revit model must support a **controlled execution flow:**

| Level | Mechanism | Status |
|-------|-----------|--------|
| **Behavioral** | Tool description contains `"WRITE OPERATION"`, prompting the LLM to ask the user for confirmation before calling | ✅ Implemented |
| **Output verification** | Write tools return `old_value`/`new_value` or element counts so the user can verify after execution | ✅ Implemented |
| **Transaction wrapping** | All writes use `Transaction` with `Start()`/`Commit()`, enabling Revit's native `Ctrl+Z` undo | ✅ Implemented |
| **Dry-run / preview** | Optional `preview` parameter that shows what will change without committing | ⬜ Not yet implemented |

**Dry-run target design (future):**
```csharp
if (preview)
{
    trans.Start();
    // perform changes
    var result = DescribeChanges();  // capture what changed
    trans.RollBack();                // undo everything
    return ToolResult.Ok("Preview: " + result);
}
```

**Rule for new write tools:** Always include a `preview` parameter in the schema, even if the initial implementation only validates inputs without a full dry-run. This keeps the interface forward-compatible.

### 4. Performance (10K Element Scale)

Tools must operate efficiently at the scale of a real BIM project (10,000–100,000+ elements).

**Rules:**

| Rule | Do | Don't |
|------|----|-------|
| **Filter at the collector** | `.OfCategoryId(id).WhereElementIsNotElementType()` | `.ToList().Where(e => e.Category.Name == "Walls")` |
| **Materialize late** | Chain all filters, then `.ToList()` once at the end | `.ToList()` then `.Where()` then `.ToList()` again |
| **Exit early** | Use `.FirstOrDefault()` or `.Take(max)` when you only need a few results | Iterate the entire collection when you need one match |
| **Avoid N+1** | Batch-read parameters in a single loop | Call `doc.GetElement()` inside a nested loop |
| **Scope by category** | Always apply a category filter when the user provides one | Fall back to "all elements" without warning |

**Known issues in current codebase (to be fixed):**

| Tool | Issue | Fix |
|------|-------|-----|
| SearchElementsTool | 5 sequential `.ToList()` calls with LINQ `.Where()` in between | Chain all predicates before single `.ToList()` |
| GetModelOverviewTool | `.ToList()` all elements before `GroupBy` category | Use per-category `GetElementCount()` or `OfCategoryId` loop |
| ActivateViewTool | `.Cast<View>()` before `.Where(!IsTemplate)` | Move IsTemplate check into FEC filter or use `.WhereElementIsNotElementType()` equivalent |
| ListViewsTool | Same pattern as ActivateViewTool | Same fix |

**Benchmark target:** Any query tool must return results within **2 seconds** on a model with 50,000 elements. If it takes longer, the collector chain needs optimization.

### 5. High Frequency + High Failure Cost

A tool should only be "atomic" (predefined) if it meets the frequency/reliability threshold:

| Factor | Threshold for Atomic Tool | Leave to ExecuteCode |
|--------|---------------------------|----------------------|
| Session frequency | >10% of sessions use this operation | <10% |
| ExecuteCode success rate | <70% for this pattern | >90% |
| API complexity | Hidden constraints, multi-step orchestration, UIDocument required | Simple 1-2 API calls |
| Token cost per use | Atomic = 0 tokens; ExecuteCode = 500-2000+ tokens | Acceptable overhead |

**Rule:** Adding a tool has a cost — it increases the system prompt size, making every API call more expensive and increasing LLM decision space. **Keep the registered tool count under 25.**

### 6. Cross-Version Compatibility

Every tool must work across Revit 2023–2026 (net48 + net8.0-windows).

| API | Revit 2023/2024 | Revit 2025/2026 |
|-----|-----------------|-----------------|
| ElementId value | `id.IntegerValue` (int) | `id.Value` (long) |
| ElementId constructor | `new ElementId(int)` | `new ElementId(long)` |
| CompoundStructure layers | `GetLayers()` | `GetLayers()` |

**Rule:** Never use `ElementId.IntegerValue` or `ElementId.Value` directly. Always use `RevitCompat.GetIdValue(id)` and `RevitCompat.CreateId(long)`.

---

## Decision Framework: Should This Be a New Tool?

Use this checklist when evaluating a candidate operation:

### Step 1: Identify the Signal

| Signal Source | What to Look For |
|---------------|------------------|
| **Usage Report** | ExecuteCode success rate <70% for a repeated pattern |
| **User feedback** | "Why can't it do X?" / "It keeps failing at Y" |
| **Repeated ExecuteCode** | Same C# code pattern generated across multiple sessions |
| **LLM confusion** | Agent selects the wrong tool, or tries ExecuteCode for something a tool could handle |

### Step 2: Score (0–2 per dimension, need ≥6 to proceed)

| Dimension | 0 | 1 | 2 |
|-----------|---|---|---|
| **Frequency** | <5% sessions | 10-30% | >30% |
| **EC reliability** | >90% success | 60-90% | <60% |
| **API complexity** | 1-2 simple calls | Hidden constraints | Multi-step + UIDocument |
| **Write safety need** | Read-only | Reversible write | Destructive write |
| **Token savings** | <500 tokens/call | 500-1500 | >1500 or frequent retries |

**≥6 → Build it. 4–5 → Wait for more data. ≤3 → Leave to ExecuteCode.**

### Step 3: Design Check

Before writing code:

- [ ] Does it do exactly **one thing**?
- [ ] Can its output feed into at least one other tool?
- [ ] Does it overlap with an existing tool? (If yes: add a `mode` to the existing tool instead)
- [ ] Are required parameters ≤4? Total parameters ≤8?
- [ ] Does the error message tell the LLM what to do next?
- [ ] Is the Description tagged with `"WRITE OPERATION"` if it modifies the model?
- [ ] Does it use `RevitCompat` for ElementId operations?
- [ ] Does the collector chain filter before materializing?

### Step 4: Implement

1. Create `Tools/YourTool.cs` implementing `IAgentTool`
2. Register in `ToolRegistry.CreateDefault()`
3. Add to system prompt table in `AgentService.BuildSystemPrompt()`
4. Add thinking chain mapping in `ChatWindow.xaml.cs`
5. Build both targets: `dotnet build -f net48 -c Release && dotnet build -f net8.0-windows -c Release`

---

## Current Inventory

### 20 Registered Tools

| # | Tool | Category | R/W | Composability Output |
|---|------|----------|-----|---------------------|
| 1 | GetModelOverview | Query | R | category counts, level list |
| 2 | SearchElements | Query | R | **element_ids** → SelectElements, SetElementParameter |
| 3 | GetParameterValues | Query | R | value distribution, element_ids per value |
| 4 | GetSelection | Query | R | **element_ids** → SelectElements, any write tool |
| 5 | GetWarnings | Query | R | warning types, **element_ids** → SelectElements |
| 6 | SelectElements | Action | R (UI) | selected_ids (echo) |
| 7 | IsolateElements | Action | R (temp) | — (terminal) |
| 8 | SetElementParameter | Action | **W** | old/new value |
| 9 | ActivateView | Action | R (UI) | — (terminal) |
| 10 | CreateProjectParameter | Param | **W** | category count |
| 11 | CreateSchedule | Schedule | **W** | **schedule_id, schedule_name** → AddScheduleField, ActivateView |
| 12 | AddScheduleField | Schedule | **W** | field position |
| 13 | FormatScheduleField | Schedule | **W** | formatting details |
| 14 | ModifyScheduleFilter | Schedule | **W** | filter count |
| 15 | ModifyScheduleSort | Schedule | **W** | sort count |
| 16 | ListSheets | Output | R | **sheet_numbers** → PrintSheets, ExportDocument |
| 17 | ListViews | Output | R | **view ids/names** → ActivateView, ExportDocument |
| 18 | PrintSheets | Output | **Exec** | file paths |
| 19 | ExportDocument | Output | **Exec** | file paths |
| 20 | ExecuteCode | Universal | **Any** | arbitrary (LLM-dependent) |

### 11 Reserve Tools (code preserved, not registered)

| Tool | Candidate for Re-activation? | Trigger |
|------|------------------------------|---------|
| BatchSetParameter | **Yes** — high frequency, transaction safety | EC success <70% for batch writes |
| ColorElements | **Yes** — complex OverrideGraphicSettings API | EC success <70% for visual QA |
| GetElementDetails | Maybe — EC handles well currently | User feedback |
| CheckMissingParameters | Maybe | QAQC workflow demand |
| GetNearbyElements | Low priority | Spatial query demand |
| Others (6) | Low priority | Usage data |

---

## Anti-Patterns (Do NOT Do This)

### 1. "Kitchen Sink" Tool
```
❌ Tool: ManageSchedule
   Modes: create, delete, addField, removeField, addFilter, removeFilter, addSort,
          removeSort, format, rename, duplicate, export
```
Too many modes on different sub-resources. Split by resource: CreateSchedule, AddScheduleField, ModifyScheduleFilter, etc.

### 2. Non-Composable Output
```
❌ return ToolResult.Ok("Found 42 walls on Level 1");  // String only, no data
✅ return ToolResult.Ok("Found 42 walls on Level 1",
       new Dictionary<string, object> {
           ["element_ids"] = wallIds,
           ["count"] = 42
       });
```

### 3. Materialize-Then-Filter
```
❌ collector.ToList().Where(e => e.Category.Name == "Walls").ToList()
✅ collector.OfCategoryId(wallCatId).WhereElementIsNotElementType().ToList()
```

### 4. Skipping RevitCompat
```
❌ long id = element.Id.Value;           // Fails on Revit 2024
❌ int id = element.Id.IntegerValue;     // Fails on Revit 2025+
✅ long id = RevitCompat.GetIdValue(element.Id);  // Works everywhere
```
