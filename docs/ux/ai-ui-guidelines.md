# opportuniTY UI Design and Relativity Compatibility Guidelines

## Purpose

opportuniTY is an independent, free, open-source eDiscovery review platform.

The product should feel familiar to experienced eDiscovery reviewers, including users who have worked with Relativity, without copying Relativity's proprietary visual design, branding, or distinctive UI implementation.

The core principle is:

> Preserve familiar eDiscovery workflows and user expectations, but create an independent visual language and product identity.

A useful shorthand is:

> Copy the workflow, not the pixels.

---

## 1. General Design Rule

AI agents and contributors MAY use common eDiscovery interaction patterns and workflows.

AI agents and contributors MUST NOT intentionally reproduce Relativity screens pixel-for-pixel or closely imitate Relativity's distinctive visual appearance.

When choosing between familiarity and visual similarity:

- Prefer workflow familiarity.
- Prefer standard industry conventions.
- Prefer opportuniTY's own design system.
- Avoid unnecessary resemblance to Relativity's specific implementation.

---

## 2. What May Be Similar

The following concepts are considered normal eDiscovery workflows and may be implemented in opportuniTY.

### Document Review Layout

A review screen may contain:

- Navigation
- Document list
- Document viewer
- Metadata panel
- Coding panel
- Tags
- Previous/Next document navigation
- Save and Save & Next actions

Example:

```text
+----------------------------------------------------------+
| opportuniTY                         Workspace / Matter    |
+------------+-----------------------------+---------------+
| Navigation |                             | Coding Panel  |
|            |                             |               |
| Documents  |       Document Viewer       | Relevance     |
| Search     |                             | Privilege     |
| Tags       |                             | Issues        |
| Exports    |                             | Notes         |
|            |                             |               |
+------------+-----------------------------+---------------+
| Previous              Save & Next                 Next   |
+----------------------------------------------------------+
```

This general layout is acceptable because it follows common document-review workflow requirements.

---

## 3. Familiar Workflows Are Encouraged

opportuniTY SHOULD minimize training requirements for experienced eDiscovery users.

Common workflows may include:

```text
Documents
   ↓
Search / Filter
   ↓
Open Document
   ↓
Review
   ↓
Code / Tag
   ↓
Save
   ↓
Next Document
```

Other familiar concepts may include:

- Saved searches
- Views
- Fields
- Tags
- Coding layouts
- Document families
- Batches
- Review queues
- Mass operations
- Bulk coding
- Search conditions
- Highlighting
- Native viewer
- Text viewer
- Image viewer
- Production viewer
- Document history
- Audit history
- Export
- Production sets
- Privilege coding
- Issue coding

These concepts should use opportuniTY's own implementation and design.

---

## 4. Do Not Copy Relativity Screens

DO NOT use screenshots of Relativity as implementation specifications.

Screenshots may be used to understand workflow requirements, but must not be used as pixel-level design references.

Do not reproduce combinations of:

- exact panel dimensions
- exact toolbar placement
- exact spacing
- exact button positions
- exact typography
- exact colors
- exact menu hierarchy
- exact icons
- exact border styles
- exact tab designs
- exact modal layouts
- exact table styling

A screen should not reasonably look like:

> "Relativity with a different logo and different colors."

If it does, redesign it.

---

## 5. Branding Must Be Independent

The opportuniTY interface must use opportuniTY branding.

Use:

- opportuniTY
- oppor+unity
- opportuniTY logo
- opportuniTY color system
- opportuniTY typography
- opportuniTY icon system

Do not use:

- Relativity logos
- Relativity product logos
- Relativity orange as the primary identity
- Relativity-specific artwork
- Relativity screenshots
- Relativity brand assets

The opportuniTY application should remain visually identifiable as opportuniTY even to someone familiar with both products.

---

## 6. Color System

Use the opportuniTY design-system colors defined elsewhere in this repository.

Do not derive the application's palette from Relativity's palette.

Avoid using Relativity's characteristic orange as the dominant product color.

Neutral colors such as:

- white
- gray
- black
- standard accessibility colors
- conventional warning/error colors

may naturally overlap with other software products.

That alone is not a problem.

---

## 7. Icons

Use an independent icon library or custom opportuniTY icons.

Preferred options include common open-source libraries such as:

- Lucide
- Material Symbols
- Font Awesome
- Heroicons

Do not copy or recreate proprietary Relativity icon assets.

Do not trace Relativity icons.

When an icon represents a common operation, use a conventional industry symbol.

Examples:

```text
Search      → magnifying glass
Filter      → funnel
Save        → disk/check
Delete      → trash
Export      → arrow-out / download
Document    → file
Tags        → tag
Settings    → gear
Previous    → left arrow
Next        → right arrow
```

---

## 8. Terminology

Prefer generic eDiscovery terminology.

Good examples:

```text
Documents
Review
Search
Saved Search
Fields
Coding
Tags
Issues
Privilege
Batch
Export
Production
Workspace
Matter
Viewer
Document Family
Custodian
```

Do not invent unnecessarily different terminology merely to avoid similarity.

Common functional terminology is desirable because users already understand it.

However, avoid copying terminology that is clearly a unique Relativity product or feature brand unless interoperability or documentation explicitly requires mentioning it.

---

## 9. Relativity Product Names

Do not name opportuniTY features after Relativity-branded features.

For example, avoid naming a new opportuniTY feature:

```text
Aero Viewer
Relativity Review
Relativity Workspace
Relativity Search
```

Instead use independent generic names such as:

```text
Document Viewer
Review Workspace
Search
Review Queue
Coding Panel
```

---

## 10. Comparative References

Documentation may factually explain interoperability or product comparisons when necessary.

Acceptable examples:

```text
Users familiar with modern eDiscovery review platforms should find the workflow familiar.
```

```text
opportuniTY provides document review, search, coding, tagging, and export capabilities.
```

When necessary for technical comparison:

```text
This feature provides functionality comparable to document review workflows available in platforms such as Relativity.
```

Avoid marketing language that could imply affiliation.

Do not write:

```text
Official Relativity Alternative
Relativity Open Source Edition
Free Relativity
Relativity Clone
Relativity Community Edition
Relativity-compatible UI
```

unless there is a specific legal and technical reason to use such wording.

---

## 11. Navigation

The application may use conventional enterprise navigation patterns.

Examples:

```text
Workspace
  Documents
  Search
  Review
  Tags
  Batches
  Exports
  Administration
```

The specific ordering, visual hierarchy, interaction states, icons, spacing, and grouping should be designed for opportuniTY.

Do not mechanically reproduce the navigation structure of another product.

---

## 12. Tables and Document Lists

Document grids may contain standard functionality such as:

- sortable columns
- resizable columns
- filtering
- column selection
- pagination
- infinite scrolling
- row selection
- bulk operations
- saved views

These are common application patterns.

However, opportuniTY should define its own:

- row height
- typography
- selected state
- hover state
- header style
- pagination controls
- filter UI
- column menu
- bulk-operation toolbar

---

## 13. Document Viewer

The document viewer may provide familiar controls such as:

```text
Native
Text
Image
Production
Zoom
Rotate
Find
Highlight
Download
Previous
Next
```

The viewer toolbar and controls should follow opportuniTY's design system.

Do not recreate another product's viewer toolbar exactly.

---

## 14. Coding Panel

The coding panel may support:

- Yes/No fields
- single-choice fields
- multi-choice fields
- tags
- text fields
- date fields
- numeric fields
- notes
- validation
- conditional fields

AI agents should optimize the coding experience for reviewer speed.

Keyboard-driven review is encouraged.

Examples:

```text
Ctrl/Cmd + S       Save
Alt + →            Next document
Alt + ←            Previous document
```

Actual shortcuts should be documented and configurable where practical.

---

## 15. User Muscle Memory

Preserving user muscle memory is an explicit product goal.

An experienced reviewer should be able to infer:

- where documents are located
- how to open a document
- where coding decisions are made
- how to navigate to the next document
- how to search
- how to filter
- how to tag documents
- how to perform bulk operations

without requiring substantial training.

This familiarity should come from workflow conventions, not visual imitation.

---

## 16. Accessibility and Usability Take Priority

If matching a familiar workflow conflicts with accessibility or usability, prefer the better user experience.

opportuniTY should target:

- WCAG accessibility principles
- keyboard navigation
- visible focus indicators
- sufficient contrast
- screen-reader compatibility
- scalable typography
- responsive layouts
- clear loading states
- clear error states

---

## 17. AI Agent Instructions

When an AI agent is asked to implement or redesign opportuniTY UI, it MUST follow these rules.

### The AI MUST:

1. Use opportuniTY's existing design tokens.
2. Use opportuniTY branding.
3. Prefer standard eDiscovery workflows.
4. Prefer standard enterprise UX patterns.
5. Reuse existing opportuniTY components.
6. Maintain consistency across the application.
7. Optimize for reviewer productivity.
8. Preserve keyboard accessibility.
9. Create independent visual implementations.
10. Treat screenshots from competing products as workflow references only.

### The AI MUST NOT:

1. Pixel-copy Relativity screens.
2. Trace Relativity UI.
3. Copy Relativity CSS.
4. Copy Relativity HTML.
5. Copy proprietary icons.
6. Copy proprietary images or assets.
7. Reproduce distinctive Relativity layouts solely for visual similarity.
8. Use Relativity branding.
9. Make opportuniTY appear officially associated with Relativity.
10. Describe opportuniTY in the UI as "Relativity Clone."

---

## 18. Screenshot Rule

If a contributor provides a screenshot of Relativity or another commercial platform and asks:

> "Make this."

The AI should interpret the request as:

> "Identify the workflow and functional requirements shown here, then implement an independent opportuniTY design that accomplishes the same task."

The AI should extract:

- required controls
- workflow
- information hierarchy
- user actions
- data requirements

but should create a new visual implementation.

---

## 19. Similarity Check

Before completing a major UI feature, ask:

### Workflow similarity

Is this familiar to experienced eDiscovery users?

If yes, that is generally desirable.

### Visual similarity

Would someone looking at screenshots confuse opportuniTY with Relativity?

If yes, redesign the screen.

### Branding similarity

Could a reasonable user believe opportuniTY is produced, sponsored, or endorsed by Relativity?

If yes, redesign or change the wording.

---

## 20. Preferred Design Philosophy

The opportuniTY UI should be:

- familiar
- fast
- dense where useful
- clean
- modern
- keyboard-friendly
- accessible
- customizable
- optimized for professional document review

The product should not attempt to look novel merely for novelty's sake.

Common UI conventions are good.

Distinct product identity is also required.

---

## 21. Architecture Principle

User workflows and UI implementation should remain separate concepts.

For example:

```text
Review Workflow
       │
       ▼
Functional Requirements
       │
       ▼
opportuniTY Components
       │
       ▼
opportuniTY Design System
       │
       ▼
Final UI
```

Do not use:

```text
Relativity Screenshot
       │
       ▼
Pixel Recreation
```

---

## 22. Implementation Decision Rule

When unsure whether a design is acceptable, use this hierarchy:

1. eDiscovery industry convention
2. general enterprise software convention
3. opportuniTY design system
4. usability and reviewer productivity
5. accessibility
6. product-specific inspiration

Never make direct imitation of a competitor the primary design rationale.

---

## 23. Code Review Checklist

For every major UI pull request, reviewers should verify:

- [ ] Uses opportuniTY branding
- [ ] Uses approved design tokens
- [ ] Uses approved icons
- [ ] Does not contain copied third-party assets
- [ ] Does not contain copied third-party CSS
- [ ] Does not reproduce a competitor screen pixel-for-pixel
- [ ] Uses familiar eDiscovery terminology where appropriate
- [ ] Maintains keyboard accessibility
- [ ] Supports efficient review workflow
- [ ] Maintains adequate visual distinction from Relativity
- [ ] Does not imply partnership or endorsement by Relativity

---

## 24. One-Sentence Rule

When implementing opportuniTY UI, remember:

> Make the workflow immediately familiar to experienced eDiscovery reviewers while ensuring the visual design, branding, components, and implementation are unmistakably opportuniTY.

---

## Legal Note

These guidelines are engineering and product-design rules intended to reduce unnecessary intellectual-property and branding risk.

They are not legal advice.

Questions involving trademarks, copyright, trade dress, licensing, interoperability, or commercial distribution should be reviewed by qualified counsel when appropriate.
