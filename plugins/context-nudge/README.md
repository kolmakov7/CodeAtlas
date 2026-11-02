# CodeAtlas context nudge

Nudges the agent shortly **before** context compaction, without touching the compaction
mechanism. It watches `session.next.step.ended` token usage, and when usage crosses the
threshold it sends a normal prompt asking the agent to:

1. update the current CodeAtlas task (`update_task`, with `expectedVersion`);
2. save durable decisions/constraints/research with `save_knowledge` **only if there is
   something worth saving**;
3. skip knowledge creation when there is nothing durable;
4. continue the work.

It never edits the compaction summary, never blocks compaction, and has a cooldown per session.
After `session.next.compaction.ended` the nudge state is cleared.

## Config

```jsonc
{
  "package": "/mnt/recyclebin/projects/CodeAtlas/plugins/context-nudge",
  "options": {
    "threshold": 0.78,
    "cooldownMinutes": 20,
    "delivery": "steer",
    "onlyDirectories": []
  }
}
```

- `threshold` — fraction of the model context limit (0.5–0.95).
- `cooldownMinutes` — minimum gap between nudges in one session.
- `delivery` — `steer` (default) or `queue`.
- `onlyDirectories` — optional allow-list of project directories; empty means all.
