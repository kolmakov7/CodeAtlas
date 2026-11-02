import { Plugin } from "@opencode/plugin"

type Options = {
  threshold?: number
  cooldownMinutes?: number
  delivery?: "steer" | "queue"
  onlyDirectories?: string[]
}

type NudgeState = {
  at: number
  ratio: number
}

export default Plugin.define({
  id: "codeatlas.context-nudge",
  async setup(ctx) {
    const options = (ctx.options ?? {}) as Options
    const threshold = Math.min(0.95, Math.max(0.5, Number(options.threshold ?? 0.78)))
    const cooldownMs = Math.max(1, Number(options.cooldownMinutes ?? 20)) * 60_000
    const delivery = options.delivery === "queue" ? "queue" : "steer"
    const onlyDirectories = (options.onlyDirectories ?? []).filter(Boolean)

    const limitCache = new Map<string, { value: number | undefined; at: number }>()

    const inScope = (directory: string | undefined) => {
      if (!directory) return onlyDirectories.length === 0
      if (onlyDirectories.length === 0) return true
      return onlyDirectories.some((root) => directory === root || directory.startsWith(root + "/"))
    }

    const resolveLimit = async (providerID: string, modelID: string) => {
      const key = providerID + "/" + modelID
      const cached = limitCache.get(key)
      if (cached && Date.now() - cached.at < 10 * 60_000) return cached.value
      let value: number | undefined
      try {
        const listed = (await ctx.model.list()) as any
        const models: any[] = Array.isArray(listed)
          ? listed
          : Array.isArray(listed?.models)
            ? listed.models
            : Object.values(listed?.models ?? {})
        const model = models.find((m) => m.providerID === providerID && m.id === modelID)
        const context = Number(model?.limit?.context ?? 0)
        if (context > 0) value = context
      } catch {
        value = undefined
      }
      limitCache.set(key, { value, at: Date.now() })
      return value
    }

    const promptText = (ratio: number) =>
      [
        `[CodeAtlas checkpoint] Context usage is about ${Math.round(ratio * 100)}% of the model limit.`,
        "Before continuing and before any compaction:",
        "1. Update the current task with update_task (status/next step/blockers) using its expectedVersion.",
        "2. If this work produced durable decisions, constraints, or research results, save them with save_knowledge in the current project scope.",
        "3. If there is nothing durable to save, do nothing — do not create empty knowledge or placeholder tasks.",
        "4. Continue the current work. Do not create TODO_*.md files."
      ].join("\n")

    const controller = new AbortController()
    void (async () => {
      try {
        for await (const event of ctx.event.subscribe({
          types: ["session.next.step.ended", "session.next.compaction.ended"],
          signal: controller.signal
        }) as any) {
          try {
            const sessionID = String(event?.properties?.sessionID ?? "")
            if (!sessionID) continue

            if (event.type === "session.next.compaction.ended") {
              await ctx.storage.remove("nudge:" + sessionID)
              continue
            }

            const session = await ctx.session.get({ sessionID })
            if (session.parentID) continue
            if (!inScope(session.directory)) continue
            if (!session.model?.providerID || !session.model?.id) continue

            const limit = await resolveLimit(session.model.providerID, session.model.id)
            if (!limit) continue

            const tokens = event.properties?.tokens ?? {}
            const used =
              Number(tokens.input ?? 0) +
              Number(tokens.output ?? 0) +
              Number(tokens.reasoning ?? 0) +
              Number(tokens.cache?.read ?? 0) +
              Number(tokens.cache?.write ?? 0)
            if (used <= 0) continue

            const ratio = used / limit
            if (ratio < threshold) continue

            const key = "nudge:" + sessionID
            const previous = (await ctx.storage.get(key)) as NudgeState | undefined
            if (previous && Date.now() - previous.at < cooldownMs) continue

            await ctx.session.prompt({ sessionID, text: promptText(ratio), delivery })
            await ctx.storage.set(key, { at: Date.now(), ratio })
          } catch (error) {
            console.error("[codeatlas.context-nudge] event handling failed:", error)
          }
        }
      } catch (error) {
        if (!controller.signal.aborted) console.error("[codeatlas.context-nudge] subscription failed:", error)
      }
    })()

    return () => controller.abort()
  }
})
