---
id: JF-824
title: >-
  After JF-823 the AudiobookTitle type is catalog-backed, but the session
  Dialog.UpdateDynamicEntities push still replaces its vocabulary with the
  budget-truncated library list at turn 2+
status: To Do
labels: [audiobooks, catalog-sync, dynamic-entities]
---

## Description

Filed by the JF-823 worker (2026-10-09) from the /code-review high gate, finding F1.

JF-823 made `AudiobookTitle` catalog-backed (valueSupplier in the saved model:
library titles + the 22-value it-IT seed). `DynamicEntityBuilder`'s per-session
push (the arm around line 197/221, untouched by JF-823) still writes a STATIC
values block onto the same type in the turn-2+ response. Two competing
vocabularies, never reconciled:

- Post-first-sync, turn-1 book selection resolves against the catalog (library +
  seed, catalog-version backed).
- A turn-2+ response carrying the audiobook dynamic block REPLACES that
  vocabulary for the session with the `baseBudget`-truncated library list, so a
  seed-only title that routed at turn 1 can silently stop resolving later in the
  same session.

Series has the same shape (proven-tolerated precedent, weakened severity), and
the same question now applies to it. Decide per type: drop the dynamic push for
catalog-backed types (the catalog already carries the same library names plus
the seed), keep it, or make the push seed-aware. Needs the JF-684
selection-gating lens (a catalog type with a vocabulary the NLU misses selects
NO intent; a dynamic block that REMOVES vocabulary has the same risk
in-session).

References: Jellyfin.Plugin.AlexaSkill/Alexa/DynamicEntities/DynamicEntityBuilder.cs
(BuildSlotValues audiobook/series arms), Alexa/Catalog/CatalogSlotTypes.cs
(CatalogSlotTypeNames).
